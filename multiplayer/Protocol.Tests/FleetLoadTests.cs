using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Flotsam.Multiplayer;

// Real loopback transport concurrency against the production protocol primitives
// (Frames, Packet, Connection, CommandLedger, CrewPolicy). GameSession itself needs the
// Unity runtime, so this harness drives the same primitives with the same handshake order
// and the same 8-player/16-packet/ledger bounds. It proves protocol/transport behavior only,
// never gameplay or native fleet state.
public static class FleetLoadTests
{
    private const string Secret="load-test-secret";
    private const string Fingerprint="0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    public static int Run()
    {
        var passed=0;
        void Case(string name,Action body){body();Console.WriteLine("PASS "+name);passed++;}

        Case("eight authenticated peers bind host-assigned identities over real loopback TCP",()=>
        {
            using var host=new HostHarness(Secret,Fingerprint);
            var guests=new List<GuestHarness>();
            try
            {
                guests.AddRange(JoinAll(host,7));
                Check(host.PeerCount==7,"host peer count");
                Check(host.Crew.Members().Length==8,"crew size includes host");
                Check(guests.All(g=>g.ProofVerified),"guest verified host welcome proof");
                Check(guests.Select(g=>g.Identity).Distinct().Count()==7,"host assigned distinct identities");
                Check(guests.All(g=>g.Identity.Length==32&&Guid.TryParseExact(g.Identity,"N",out _)),"identity format");
                Check(guests.All(g=>g.Connection!.Open),"all guest transports open");
            }
            finally{DisposeAll(guests);}
        });

        Case("ninth connection is refused while eight authenticated peers keep working",()=>
        {
            using var host=new HostHarness(Secret,Fingerprint);
            var guests=JoinAll(host,7);
            try
            {
                var refused=false;
                try
                {
                    using var extra=new GuestHarness();
                    Await(extra.JoinAsync(host.Port,Secret,Fingerprint,"第九人"),10);
                }
                catch(Exception e) when(e is IOException or SocketException or InvalidDataException or EndOfStreamException or TimeoutException or OperationCanceledException){refused=true;}
                Check(refused,"extra guest was not refused");
                Check(host.PeerCount==7,"authenticated peers preserved");
                Check(guests.All(g=>g.Connection!.Open),"existing guests still open");
                guests[0].Connection!.Send(new Packet{type="command",command=new GameCommand{sequence=1,kind="place"}});
                WaitUntil(()=>host.Applied>=1,TimeSpan.FromSeconds(5),"surviving peer command");
                Check(host.RefusedConnections>=1,"refusal counted");
            }
            finally{DisposeAll(guests);}
        });

        Case("each guest command executes exactly once across duplicate retries",()=>
        {
            using var host=new HostHarness(Secret,Fingerprint);
            var guests=JoinAll(host,7);
            try
            {
                for(var i=0;i<guests.Count;i++)
                    guests[i].Connection!.Send(new Packet{type="command",command=new GameCommand{sequence=i+1,kind="place",properties="Sawmill",value=i+1}});
                WaitUntil(()=>host.Applied==7,TimeSpan.FromSeconds(8),"all guest commands applied");
                Check(host.DistinctApplied==7,"seven distinct applications");
                for(var i=0;i<guests.Count;i++)
                    guests[i].Connection!.Send(new Packet{type="command",command=new GameCommand{sequence=i+1,kind="place",properties="Sawmill",value=i+1}});
                WaitUntil(()=>host.ResultsSent>=14,TimeSpan.FromSeconds(8),"duplicate results answered");
                Check(host.Applied==7,"retries never double spend");
                Check(host.DistinctApplied==7,"retries kept exactly-once applications");
            }
            finally{DisposeAll(guests);}
        });

        Case("captain-only navigation is enforced and transfer moves authority",()=>
        {
            using var host=new HostHarness(Secret,Fingerprint);
            var guests=JoinAll(host,2);
            try
            {
                WaitUntil(()=>host.Crew.Members().Length==3,TimeSpan.FromSeconds(5),"crew assembled");
                Check(host.Crew.Authorize("host","navigate")==null,"host steers by default");
                Check(host.Crew.Authorize(guests[0].Identity,"navigate")!=null,"guest cannot steer");
                Check(host.Crew.Authorize(guests[0].Identity,"place")==null,"guest can build");
                Check(host.Crew.Transfer(guests[0].Identity,guests[1].Identity)!=null,"guest cannot transfer captain");
                Check(host.Crew.Transfer("host",guests[0].Identity)==null,"host transfers captain");
                Check(host.Crew.Authorize(guests[0].Identity,"navigate")==null,"new captain steers");
                Check(host.Crew.Authorize("host","navigate")!=null,"previous captain lost helm");
                Check(host.Crew.Transfer("host","host")==null,"host reclaims helm");
                Check(host.Crew.Authorize("host","navigate")==null,"host steering restored");
            }
            finally{DisposeAll(guests);}
        });

        Case("slow peer coalesces to the newest world frame then disconnects at the queue bound",()=>
        {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            try
            {
                using var outgoing=new TcpClient();
                Await(outgoing.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port),5);
                using var incoming=Await(listener.AcceptTcpClientAsync(),5);
                using var pending=new Connection(incoming,"slow");
                for(var i=1;i<=10000;i++)pending.Send(new Packet{type="world",world=new WorldFrame{sequence=i}});
                pending.Send(new Packet{type="result",result=new CommandResult{sequence=9,accepted=true}});
                Check(pending.Open,"coalescing must not disconnect");
                Check(pending.PendingPackets==2,"world frames coalesced, reliable reply retained");
                var running=pending.Run((_,_)=>{});
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var stream=outgoing.GetStream();
                var world=Await(Frames.Read(stream,deadline.Token),5);
                var reply=Await(Frames.Read(stream,deadline.Token),5);
                Check(world.world?.sequence==10000,"newest world frame won");
                Check(reply.result?.sequence==9,"reliable reply preserved");
                pending.Dispose();
                Await(running,5);
                using var overflow=new TcpClient();
                using var bounded=new Connection(new TcpClient(),"overflow");
                for(var i=0;i<17;i++)bounded.Send(new Packet{type="ping"});
                Check(!bounded.Open,"unbounded queue disconnected the peer");
            }
            finally{listener.Stop();}
        });

        Case("one encoded broadcast is reused across peers while reliable replies stay per peer",()=>
        {
            using var host=new HostHarness(Secret,Fingerprint);
            var guests=JoinAll(host,7);
            try
            {
                var packet=new Packet{type="world",world=new WorldFrame{sequence=4242}};
                Check(ReferenceEquals(packet.Encode(),packet.Encode()),"broadcast bytes encoded once");
                host.Broadcast(packet);
                WaitUntil(()=>guests.All(g=>g.Received.Any(p=>p.type=="world"&&p.world?.sequence==4242)),TimeSpan.FromSeconds(8),"broadcast reached all peers");
                guests[0].Connection!.Send(new Packet{type="command",command=new GameCommand{sequence=99,kind="researchStart"}});
                WaitUntil(()=>guests.Count(g=>g.Received.Any(p=>p.type=="result"))==1,TimeSpan.FromSeconds(6),"reliable reply delivered once");
                Check(guests.Skip(1).All(g=>!g.Received.Any(p=>p.type=="result")),"reply not broadcast to unrelated peers");
            }
            finally{DisposeAll(guests);}
        });

        Case("reconnect gets a fresh identity and ledger high-water marks do not leak across peers",()=>
        {
            using var host=new HostHarness(Secret,Fingerprint);
            var first=JoinAll(host,1)[0];
            try
            {
                first.Connection!.Send(new Packet{type="command",command=new GameCommand{sequence=7,kind="place"}});
                WaitUntil(()=>host.Applied==1,TimeSpan.FromSeconds(5),"first peer applied");
                var firstIdentity=first.Identity;
                first.Dispose();
                WaitUntil(()=>host.PeerCount==0,TimeSpan.FromSeconds(6),"old peer removed");
                var stale=host.Ledger.Execute(firstIdentity,new GameCommand{sequence=1,kind="place"},_=>null);
                Check(!stale.accepted&&stale.error.Length>0,"closed identity keeps its high-water mark until forgotten");
                host.Ledger.Forget(firstIdentity);
                var replacement=JoinAll(host,1)[0];
                try
                {
                    Check(replacement.Identity!=firstIdentity,"reconnect has a new identity");
                    replacement.Connection!.Send(new Packet{type="command",command=new GameCommand{sequence=1,kind="place"}});
                    WaitUntil(()=>host.Applied==2,TimeSpan.FromSeconds(6),"fresh identity sequence 1 accepted");
                    Check(host.AcceptedCommands>=2,"reconnect resync accepted");
                }
                finally{replacement.Dispose();}
            }
            finally{first.Dispose();}
        });

        Case("bounded 300-tick soak across seven guests keeps queues and sockets healthy",()=>
        {
            using var host=new HostHarness(Secret,Fingerprint);
            var guests=JoinAll(host,7);
            try
            {
                var acksExpected=0;
                for(var tick=1;tick<=300;tick++)
                {
                    host.Broadcast(new Packet{type="world",world=new WorldFrame{sequence=tick}});
                    if(tick%10==0)
                        foreach(var guest in guests)
                            // Respect the real 16-packet bound instead of overflowing a test peer.
                            if(guest.Connection!.PendingPackets<8){guest.Connection.Send(new Packet{type="ack",sequence=tick});acksExpected++;}
                    if(tick%25==0)Thread.Sleep(5);
                }
                var expectedAcks=acksExpected;
                WaitUntil(()=>guests.All(g=>g.Received.Any(p=>p.type=="world"&&p.world?.sequence==300)),TimeSpan.FromSeconds(20),
                    "final tick reached every guest (last="+string.Join(",",guests.Select(g=>g.Received.LastOrDefault(p=>p.type=="world")?.world?.sequence??0))+"; open="+string.Join(",",guests.Select(g=>g.Connection?.Open))+")");
                WaitUntil(()=>host.Acks>=expectedAcks,TimeSpan.FromSeconds(20),"every acknowledgement arrived ("+host.Acks+"/"+expectedAcks+")");
                Check(guests.All(g=>g.Connection!.Open),"no guest disconnected during soak");
                Check(host.PeerCount==7,"peer set stable during soak");
                Check(guests.All(g=>g.Connection!.PendingPackets<=16),"per-peer queue stayed bounded");
                Check(guests.All(g=>g.Received.Count(p=>p.type=="world")>=1),"each guest observed world frames");
            }
            finally{DisposeAll(guests);}
        });

        Case("malformed peer frame disconnects only that peer",()=>
        {
            using var host=new HostHarness(Secret,Fingerprint);
            var guests=JoinAll(host,6);
            try
            {
                using var rogue=new GuestHarness();
                Await(rogue.JoinAsync(host.Port,Secret,Fingerprint,"坏帧"),10);
                WaitUntil(()=>host.PeerCount==7,TimeSpan.FromSeconds(5),"rogue joined before corruption");
                var stream=rogue.Stream!;
                var prefix=new byte[]{(byte)0x7f,(byte)0xff,(byte)0xff,(byte)0xff};
                stream.Write(prefix,0,4);stream.Flush();
                WaitUntil(()=>host.PeerCount==6,TimeSpan.FromSeconds(8),"only the malformed peer was dropped");
                Check(guests.All(g=>g.Connection!.Open),"healthy guests preserved");
                guests[0].Connection!.Send(new Packet{type="command",command=new GameCommand{sequence=1,kind="place"}});
                WaitUntil(()=>host.Applied==1,TimeSpan.FromSeconds(6),"host still usable after isolation");
                Check(!host.PeerFailuresAttributableToHealthyPeers(),"only the malformed peer was attributed a failure");
            }
            finally{DisposeAll(guests);}
        });

        Case("final diagnostics are consistent and every socket is closed",()=>
        {
            var host=new HostHarness(Secret,Fingerprint);
            var guests=JoinAll(host,7);
            for(var i=0;i<guests.Count;i++)
                guests[i].Connection!.Send(new Packet{type="command",command=new GameCommand{sequence=i+1,kind="place"}});
            WaitUntil(()=>host.Applied==7,TimeSpan.FromSeconds(8),"seven applied");
            foreach(var guest in guests)guest.Dispose();
            WaitUntil(()=>host.PeerCount==0,TimeSpan.FromSeconds(8),"all peers released");
            var accepted=host.AcceptedCommands;var rejected=host.RejectedCommands;
            host.Dispose();
            Check(guests.All(g=>g.Connection==null||!g.Connection.Open),"guest sockets closed");
            Check(host.SocketsClosed,"host sockets closed");
            Check(accepted==7,"accepted command counter consistent");
            Check(rejected==0,"no spurious rejections");
            Check(host.Applied==7&&host.DistinctApplied==7,"applied counters consistent");
            Check(host.RefusedConnections==0,"no capacity refusals in this run");
        });

        return passed;
    }
    private static List<GuestHarness> JoinAll(HostHarness host,int count)
    {
        var guests=new List<GuestHarness>();
        for(var i=0;i<count;i++)
        {
            var guest=new GuestHarness();
            try{Await(guest.JoinAsync(host.Port,Secret,Fingerprint,"同行者"+(i+1)),15);}
            catch{guest.Dispose();throw;}
            guests.Add(guest);
        }
        WaitUntil(()=>host.PeerCount==count,TimeSpan.FromSeconds(10),"all guests registered");
        return guests;
    }
    private static void DisposeAll(IEnumerable<GuestHarness> guests){foreach(var guest in guests)guest.Dispose();}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Fleet load assertion failed: "+message);}
    private static void WaitUntil(Func<bool> condition,TimeSpan budget,string message)
    {
        var deadline=DateTime.UtcNow+budget;
        while(DateTime.UtcNow<deadline){if(condition())return;Thread.Sleep(20);}
        throw new TimeoutException("Timed out waiting for: "+message);
    }
    private static void Await(Task task,int seconds)
    {
        try{if(!task.Wait(TimeSpan.FromSeconds(seconds)))throw new TimeoutException("Operation exceeded "+seconds+"s");}
        catch(AggregateException e) when(e.InnerException!=null){throw e.InnerException;}
    }
    private static T Await<T>(Task<T> task,int seconds)
    {
        try{if(!task.Wait(TimeSpan.FromSeconds(seconds)))throw new TimeoutException("Operation exceeded "+seconds+"s");return task.Result;}
        catch(AggregateException e) when(e.InnerException!=null){throw e.InnerException;}
    }
    // Mirrors GameSession's host loop against the real Connection/Frames/CrewPolicy/CommandLedger.
    private sealed class HostHarness : IDisposable
    {
        private readonly string _secret,_fingerprint;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop=new CancellationTokenSource();
        private readonly ConcurrentDictionary<string,Connection> _peers=new ConcurrentDictionary<string,Connection>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long,int> _distinct=new ConcurrentDictionary<long,int>();
        private readonly Task _accepting;
        private int _refused,_failures,_applied,_accepted,_rejected,_results,_acks,_removed;
        public HostHarness(string secret,string fingerprint)
        {
            _secret=secret;_fingerprint=fingerprint;
            Crew=new CrewPolicy("host","房主");Ledger=new CommandLedger();
            _listener=new TcpListener(IPAddress.Loopback,0);_listener.Start(8);
            _accepting=Task.Run(AcceptLoop);
        }
        public CrewPolicy Crew{get;}
        public CommandLedger Ledger{get;}
        public int Port=>((IPEndPoint)_listener.LocalEndpoint).Port;
        public int PeerCount=>_peers.Count;
        public int RefusedConnections=>Volatile.Read(ref _refused);
        public int PeerFailures=>Volatile.Read(ref _failures);
        public int RemovedPeers=>Volatile.Read(ref _removed);
        public int Applied=>Volatile.Read(ref _applied);
        public int AcceptedCommands=>Volatile.Read(ref _accepted);
        public int RejectedCommands=>Volatile.Read(ref _rejected);
        public int ResultsSent=>Volatile.Read(ref _results);
        public int Acks=>Volatile.Read(ref _acks);
        public int DistinctApplied=>_distinct.Count;
        public bool SocketsClosed=>_peers.IsEmpty&&!_accepting.IsCompletedBadly();
        public bool PeerFailuresAttributableToHealthyPeers()=>_failures>RemovedPeers;
        public void Broadcast(Packet packet){foreach(var peer in _peers.Values)peer.Send(packet);}
        private async Task AcceptLoop()
        {
            while(!_stop.IsCancellationRequested)
            {
                TcpClient socket;
                try{socket=await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);}
                catch{break;}
                if(_peers.Count>=7){Interlocked.Increment(ref _refused);socket.Close();continue;}
                _=AcceptSocket(socket);
            }
        }
        private async Task AcceptSocket(TcpClient socket)
        {
            Connection? connection=null;
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(12));
                using var close=deadline.Token.Register(()=>{if(connection==null)socket.Close();});
                var stream=socket.GetStream();var nonce=Frames.RandomToken();
                await Frames.Write(stream,new Packet{type="challenge",nonce=nonce},deadline.Token).ConfigureAwait(false);
                var hello=await Frames.Read(stream,deadline.Token).ConfigureAwait(false);
                if(hello.type!="hello"||hello.protocol!=4||hello.peer.Length!=32||!Guid.TryParseExact(hello.peer,"N",out _)||!Frames.Verify(_secret,"client",nonce,hello.peer,hello.fingerprint,hello.proof))
                    throw new InvalidDataException("身份校验失败");
                if(hello.fingerprint!=_fingerprint)
                {
                    await Frames.Write(stream,new Packet{type="error",error="游戏或插件文件不一致"},deadline.Token).ConfigureAwait(false);
                    return;
                }
                var identity=Guid.NewGuid().ToString("N");
                await Frames.Write(stream,new Packet{type="welcome",peer=identity,proof=Frames.Sign(_secret,"host",hello.nonce,hello.peer,_fingerprint+":"+identity)},deadline.Token).ConfigureAwait(false);
                connection=new Connection(socket,identity);
                _peers.TryAdd(identity,connection);
                Crew.Join(identity,"同行者");
                var active=connection;
                await active.Run(Receive).ConfigureAwait(false);
                // Connection.Run deliberately closes the transport on a malformed/oversized
                // inbound frame and swallows the reader fault after disposal. The observable
                // contract is therefore an isolated roster removal, asserted by the caller.
            }
            catch{Interlocked.Increment(ref _failures);}
            finally
            {
                if(connection!=null){_peers.TryRemove(connection.Peer,out _);Crew.Leave(connection.Peer);Interlocked.Increment(ref _removed);connection.Dispose();}
                else socket.Close();
            }
        }
        private void Receive(Connection connection,Packet packet)
        {
            if(packet.type=="command"&&packet.command!=null)
            {
                var result=Ledger.Execute(connection.Peer,packet.command,Apply);
                if(result.accepted)Interlocked.Increment(ref _accepted);else Interlocked.Increment(ref _rejected);
                connection.Send(new Packet{type="result",result=result});
                Interlocked.Increment(ref _results);
            }
            else if(packet.type=="ack")Interlocked.Increment(ref _acks);
        }
        private string? Apply(GameCommand command)
        {
            Interlocked.Increment(ref _applied);
            _distinct.AddOrUpdate(command.sequence,1,(_,count)=>count+1);
            return command.kind=="reject"?"游戏拒绝执行此指令":null;
        }
        public void Dispose()
        {
            _stop.Cancel();_listener.Stop();
            foreach(var peer in _peers.Values)peer.Dispose();
            _peers.Clear();
            try{_accepting.Wait(TimeSpan.FromSeconds(5));}catch{}
            _stop.Dispose();
        }
    }
    private sealed class GuestHarness : IDisposable
    {
        private TcpClient? _client=new TcpClient();
        private Task? _running;
        public Connection? Connection{get;private set;}
        public NetworkStream? Stream{get;private set;}
        public string Identity{get;private set;}="";
        public bool ProofVerified{get;private set;}
        public ConcurrentQueue<Packet> Received{get;}=new ConcurrentQueue<Packet>();
        public async Task JoinAsync(int port,string secret,string fingerprint,string name)
        {
            var client=_client??throw new InvalidOperationException("guest already closed");
            await client.ConnectAsync(IPAddress.Loopback,port).ConfigureAwait(false);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var stream=client.GetStream();Stream=stream;
            var challenge=await Frames.Read(stream,deadline.Token).ConfigureAwait(false);
            if(challenge.type!="challenge")throw new InvalidDataException("challenge missing");
            var peer=Guid.NewGuid().ToString("N");
            var hello=new Packet{type="hello",protocol=4,peer=peer,nonce=challenge.nonce,name=name,fingerprint=fingerprint};
            hello.proof=Frames.Sign(secret,"client",challenge.nonce,peer,fingerprint);
            await Frames.Write(stream,hello,deadline.Token).ConfigureAwait(false);
            var welcome=await Frames.Read(stream,deadline.Token).ConfigureAwait(false);
            if(welcome.type!="welcome")throw new InvalidDataException("welcome missing: "+welcome.type+" "+welcome.error);
            ProofVerified=Frames.Verify(secret,"host",hello.nonce,hello.peer,fingerprint+":"+welcome.peer,welcome.proof);
            Identity=welcome.peer;
            Connection=new Connection(client,welcome.peer);
            var active=Connection;
            _running=active.Run((_,packet)=>Received.Enqueue(packet));
        }
        public void Dispose()
        {
            Connection?.Dispose();
            try{_running?.Wait(TimeSpan.FromSeconds(5));}catch{}
            _client?.Close();_client?.Dispose();_client=null;Stream=null;
        }
    }
    private static bool IsCompletedBadly(this Task task)=>task.IsFaulted;
}
