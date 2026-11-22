using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using BepInEx;
using M4.Session;
using UnityEngine;

namespace Flotsam.Multiplayer;

internal sealed class GameSession:IDisposable
{
    private readonly NetConfig _options;
    private readonly PlayerIdentity _identity;
    private readonly ConcurrentDictionary<string,string> _owners=new ConcurrentDictionary<string,string>();
    private readonly ConcurrentDictionary<string,string> _tickets=new ConcurrentDictionary<string,string>();
    private readonly ConcurrentDictionary<string,string> _ownerPeers=new ConcurrentDictionary<string,string>();
    private readonly ConcurrentDictionary<string,string> _ticketPeers=new ConcurrentDictionary<string,string>();
    private float _nextAdmission;
    private readonly List<ShipOwnershipRequest> _ownershipRequests=new List<ShipOwnershipRequest>();
    private readonly HashSet<string> _declinedOwnership=new HashSet<string>();
    private long _nextOwnershipRequest;
    internal ShipOwnershipRequest[] OwnershipRequests=>_ownershipRequests.Where(r=>_peers.ContainsKey(r.Peer)&&r.Ship!=null).ToArray();
    private readonly string _fingerprint,_peer;
    private readonly CrewPolicy _crew;
    private string _captain="";
    private string _assignedPeer="";
    private CrewMember[] _members=Array.Empty<CrewMember>();
    public bool IsCaptain=>Captain==LocalPeer;
    public CrewMember[] Crew=>IsHost?_crew.Members():_members;
    public string LocalPeer=>IsHost?_peer:_assignedPeer;
    public string Captain=>IsHost?_crew.Captain:_captain;
    private readonly ConcurrentQueue<Action> _main=new ConcurrentQueue<Action>();
    private readonly ConcurrentDictionary<string,Connection> _peers=new ConcurrentDictionary<string,Connection>();
    private readonly Dictionary<long,string> _pendingNotices=new Dictionary<long,string>();
    private readonly Dictionary<long,(Action<bool> callback,float until)> _pendingReplies=new Dictionary<long,(Action<bool>,float)>();
    private readonly CommandLedger _ledger=new CommandLedger();
    private readonly CancellationTokenSource _stop=new CancellationTokenSource();
    private readonly Replication _replication=new Replication();
    private Connection? _server;
    private TcpListener? _listener;
    private Task? _network;
    private WorldFrame? _latest;
    private WorldFrame? _incomingWorld;
    private string _localShip="";
    private bool _adopted;
    private float _pendingLogAt;
    private Packet? _pendingBootstrap;
    private World? _hostWorld;
    private readonly HashSet<string> _bootstrapped=new HashSet<string>();
    private readonly Dictionary<string,float> _resyncAfter=new Dictionary<string,float>();
    private int _handshakes;
    private long _sequence,_commands;
    private string _epoch=Guid.NewGuid().ToString("N");
    private bool _canSubmit;
    private bool _resyncing;
    private float _nextResync,_nextErrorLog;
    private float _navigationUntil;
    private float _nextSnapshot,_nextHeartbeat;
    public string Status="尚未连接";
    public int PeerCount=>_peers.Count;
    public bool ReplicaReady {get;private set;}
    public bool IsClient=>_options.role=="client";
    public bool IsHost=>_options.role=="host";
    public bool Connected=>IsHost ? _listener!=null : _server?.Open==true;
    public int Latency=>IsClient?_server?.Latency??0:_peers.Values.Select(p=>p.Latency).DefaultIfEmpty(0).Max();
    public int QueueDepth=>IsClient?_server?.PendingPackets??0:_peers.Values.Select(p=>p.PendingPackets).DefaultIfEmpty(0).Max();
    public float CaptureMilliseconds {get;private set;}
    public string Phase=>!Connected?"connecting":!Ready()?"loading":IsHost?(_peers.Count==0?"waiting":"playing"):ReplicaReady?"playing":_resyncing?"resyncing":"syncing";
    public bool Applying {get;private set;}
    public WorldFrame? AppliedFrame {get;private set;}
    public FleetShipState[] Ships {get;private set;}=Array.Empty<FleetShipState>();
    public string NoticeText {get;private set;}="";
    public bool NoticeError {get;private set;}
    public float NoticeUntil {get;private set;}
    public void ShowNotice(string text,bool isError=false){if(string.IsNullOrWhiteSpace(text))return;NoticeText=text;NoticeError=isError;NoticeUntil=Time.realtimeSinceStartup+3.6f;}
    public GameSession(NetConfig options)
    {
        _options=options;
        _identity=new PlayerIdentity(Path.Combine(options.profile,"multiplayer-identity.key"));
        if(options.port<1024||options.port>65535||options.secret.Length<32)throw new InvalidDataException("联机配置中的端口或密钥无效");
        if(options.mode!="shared"&&options.mode!="independent")throw new InvalidDataException("联机模式无效: "+options.mode);
        _peer=Guid.NewGuid().ToString("N");
        _crew=new CrewPolicy(_peer,CleanName(options.nickname,IsHost?"房主":"同行者"));
        _fingerprint=Fingerprint();
        _network=Task.Run(IsHost?Host:Client);
    }
    private static string Fingerprint()
    {
        var root=Paths.GameRootPath;var files=new List<string>{Path.Combine(root,"Flotsam_Data","Managed","Assembly-CSharp.dll")};
        foreach(var directory in new[]{"plugins","patchers","core"}){var path=Path.Combine(root,"BepInEx",directory);if(Directory.Exists(path))files.AddRange(Directory.GetFiles(path,"*.dll",SearchOption.AllDirectories));}
        var entries=new List<string>();
        using var sha=SHA256.Create();foreach(var file in files){using var stream=File.OpenRead(file);entries.Add(Path.GetFileName(file).ToLowerInvariant()+":"+Convert.ToBase64String(sha.ComputeHash(stream)));}
        entries.Sort(StringComparer.Ordinal);return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",entries))));
    }
    private static string CleanName(string name,string fallback)=>string.IsNullOrWhiteSpace(name)||name.Length>24||name.Any(char.IsControl)?fallback:name.Trim();
    private async Task Host()
    {
        try
        {
            _listener=new TcpListener(IPAddress.Any,_options.port);_listener.Start(8);Status="等待客机加入";
            using var registration=_stop.Token.Register(()=>_listener.Stop());
            while(!_stop.IsCancellationRequested){var socket=await _listener.AcceptTcpClientAsync().ConfigureAwait(false);var accepted=Interlocked.Increment(ref _handshakes);if(_peers.Count>=7||accepted>8){Interlocked.Decrement(ref _handshakes);socket.Close();continue;}_=Accept(socket);}
        }
        catch(Exception)when(_stop.IsCancellationRequested){}
        catch(Exception e){_main.Enqueue(()=>Status="房主网络错误："+e.Message);}
        finally{_listener=null;}
    }
    private async Task Accept(TcpClient socket)
    {
        Connection? connection=null;
        var acceptedOwner="";var acceptedTicket="";var identity=Guid.NewGuid().ToString("N");
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(12));
            using var close=deadline.Token.Register(()=>{if(connection==null)socket.Close();});
            var stream=socket.GetStream();var nonce=Frames.RandomToken();await Frames.Write(stream,new Packet{type="challenge",nonce=nonce},deadline.Token).ConfigureAwait(false);
            var hello=await Frames.Read(stream,deadline.Token).ConfigureAwait(false);
            if(hello.protocol!=Frames.ProtocolVersion){await Frames.Write(stream,new Packet{type="error",error="游戏联机协议不同，请双方更新启动器及联机组件"},deadline.Token).ConfigureAwait(false);return;}
            if(hello.type!="hello"||hello.peer.Length!=32||!Guid.TryParseExact(hello.peer,"N",out _)||hello.nonce.Length>128||!Frames.Verify(_options.secret,"client",nonce,hello.peer,hello.fingerprint,hello.proof))throw new InvalidDataException("身份校验失败");
            if(hello.fingerprint!=_fingerprint){await Frames.Write(stream,new Packet{type="error",error="游戏或插件文件不一致"},deadline.Token).ConfigureAwait(false);return;}
            var owner=PlayerIdentity.Verify(hello.publicKey,hello.identityProof,nonce,hello.peer,hello.fingerprint,hello.ticket);
            if(!TicketValid(hello.ticket))throw new InvalidDataException("请先在启动器加入房间");
            if(!_ownerPeers.TryAdd(owner,identity))throw new InvalidDataException("该玩家已连接游戏");
            acceptedOwner=owner;
            if(hello.ticket!=""){if(!_ticketPeers.TryAdd(hello.ticket,identity))throw new InvalidDataException("此房间席位已进入游戏");acceptedTicket=hello.ticket;}
            _owners[identity]=owner;_tickets[identity]=hello.ticket;
            await Frames.Write(stream,new Packet{type="welcome",peer=identity,proof=Frames.Sign(_options.secret,"host",hello.nonce,hello.peer,_fingerprint+":"+identity)},deadline.Token).ConfigureAwait(false);
            connection=new Connection(socket,identity);
            if(!_peers.TryAdd(identity,connection))throw new InvalidDataException("此玩家已在房间内");
            var active=connection;
            _main.Enqueue(()=>{_crew.Join(active.Peer,CleanName(hello.name,"同行者"));SendBootstrap(active);});
            await connection.Run(ReceiveHost).ConfigureAwait(false);
        }
        catch(Exception e)
        {
            if(connection==null&&e is InvalidDataException)
            {
                try{using var reply=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);reply.CancelAfter(1000);await Frames.Write(socket.GetStream(),new Packet{type="error",error=e.Message},reply.Token).ConfigureAwait(false);}catch(Exception){}
            }
            _main.Enqueue(()=>Plugin.Log("Peer disconnected: "+e.GetType().Name));
        }
        finally{Interlocked.Decrement(ref _handshakes);if(acceptedOwner!="")_ownerPeers.TryRemove(acceptedOwner,out _);if(acceptedTicket!="")_ticketPeers.TryRemove(acceptedTicket,out _);if(connection!=null){if(_peers.TryGetValue(connection.Peer,out var current)&&ReferenceEquals(current,connection))_peers.TryRemove(connection.Peer,out _);connection.Dispose();_tickets.TryRemove(connection.Peer,out _);}else{socket.Close();_owners.TryRemove(identity,out _);_tickets.TryRemove(identity,out _);}}
    }
    private async Task Client()
    {
        while(!_stop.IsCancellationRequested)
        {
            await ConnectClient().ConfigureAwait(false);
            try{await Task.Delay(3000,_stop.Token).ConfigureAwait(false);}catch(OperationCanceledException){break;}
        }
    }
    private async Task ConnectClient()
    {
        TcpClient? socket=null;
        try
        {
            _main.Enqueue(()=>{_canSubmit=false;Status="连接游戏房主";});socket=new TcpClient();
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(12));
            using var close=deadline.Token.Register(()=>{if(_server==null)socket.Close();});
            await socket.ConnectAsync(_options.address,_options.port).ConfigureAwait(false);var stream=socket.GetStream();
            var challenge=await Frames.Read(stream,deadline.Token).ConfigureAwait(false);if(challenge.type!="challenge"||challenge.protocol!=Frames.ProtocolVersion)throw new InvalidDataException("房主协议无效");
            var nonce=Frames.RandomToken();
            await Frames.Write(stream,new Packet{type="hello",peer=_peer,publicKey=_identity.PublicKey,identityProof=_identity.Sign(challenge.nonce,_peer,_fingerprint,_options.ticket),ticket=_options.ticket,name=CleanName(_options.nickname,"同行者"),nonce=nonce,fingerprint=_fingerprint,proof=Frames.Sign(_options.secret,"client",challenge.nonce,_peer,_fingerprint)},deadline.Token).ConfigureAwait(false);
            var welcome=await Frames.Read(stream,deadline.Token).ConfigureAwait(false);
            if(welcome.type=="error")throw new InvalidDataException(welcome.error);
            if(welcome.type!="welcome"||welcome.protocol!=Frames.ProtocolVersion||!Guid.TryParseExact(welcome.peer,"N",out _)||!Frames.Verify(_options.secret,"host",nonce,_peer,_fingerprint+":"+welcome.peer,welcome.proof))throw new InvalidDataException("房主身份校验失败");
            _server=new Connection(socket,welcome.peer);_main.Enqueue(()=>{_assignedPeer=welcome.peer;_captain="";_members=Array.Empty<CrewMember>();_resyncing=false;_nextResync=0f;Status="等待房主世界";});await _server.Run(ReceiveClient).ConfigureAwait(false);
            if(!_stop.IsCancellationRequested)_main.Enqueue(()=>{_canSubmit=false;Status="房主连接已断开，副本已冻结";});
        }
        catch(Exception e){if(!_stop.IsCancellationRequested)_main.Enqueue(()=>Status=e is InvalidDataException?"连接失败："+e.Message:"连接中断，正在重试");}
        finally{_server?.Dispose();_server=null;socket?.Dispose();}
    }
    private void ReceiveHost(Connection connection,Packet packet)
    {
        if(packet.type=="ping")return;
        if(_main.Count>128){connection.Dispose();return;}
        if(packet.type=="resync")
        {
            _main.Enqueue(()=>
            {
                if(!connection.Open)return;
                _bootstrapped.Remove(connection.Peer);
                if(_options.mode=="independent")
                {
                    var ship=FleetEngineBundle.FindShipById(OwnerOf(connection.Peer));
                    if(ship!=null)FleetOwnedNavigation.Stop(ship);
                }
                else if(Captain==connection.Peer)StopNavigation();
                if(!_resyncAfter.TryGetValue(connection.Peer,out var after)||Time.realtimeSinceStartup>=after)
                    SendBootstrap(connection);
            });
            return;
        }
        if(packet.type!="command"||packet.command==null){connection.Dispose();return;}
        var command=packet.command;
        if(_main.Count>128){connection.Dispose();return;}
        _main.Enqueue(()=>{
            if(!connection.Open)return;
            if(Ready())RefreshWorld();
            var result=command.epoch!=_epoch?new CommandResult{sequence=command.sequence,error="世界已切换，请等待重新同步"}:_ledger.Execute(connection.Peer,command,c=>ExecuteAuthorized(connection.Peer,c));
            if(result.accepted&&(command.kind=="navigate"||command.kind=="navigateKeys")&&(_options.mode=="shared"||connection.Peer==_peer))_navigationUntil=Time.realtimeSinceStartup+.75f;
            if(result.accepted&&(command.kind=="navigateStop"||command.kind=="navigatePlan")&&(_options.mode=="shared"||connection.Peer==_peer))_navigationUntil=0;
            connection.Send(new Packet{type="result",result=result});
        });
    }
    private void ReceiveClient(Connection connection,Packet packet)
    {
        if(_main.Count>128){connection.Dispose();return;}
        switch(packet.type)
        {
            case "ping":return;
            case "world":if(!_resyncing&&packet.world!=null)Interlocked.Exchange(ref _incomingWorld,packet.world);break;
            case "bootstrap":Interlocked.Exchange(ref _incomingWorld,null);Plugin.Log($"bootstrap received from {connection.Peer}");_main.Enqueue(()=>{_canSubmit=false;_latest=null;_pendingBootstrap=packet;});break;
            case "result":_main.Enqueue(()=>{if(packet.result!=null){if(_pendingNotices.TryGetValue(packet.result.sequence,out var success)){_pendingNotices.Remove(packet.result.sequence);if(packet.result.accepted)ShowNotice(success);}if(_pendingReplies.TryGetValue(packet.result.sequence,out var reply)){_pendingReplies.Remove(packet.result.sequence);reply.callback(packet.result.accepted);}}if(packet.result?.accepted==false){ShowNotice(packet.result.error,true);if(packet.result.error=="世界已切换，请等待重新同步")RequestResync(packet.result.error);}});break;
            case "error":_main.Enqueue(()=>{Status=packet.error;ShowNotice(packet.error,true);});break;
            case "saveResult":_main.Enqueue(()=>{ShowNotice(packet.error,packet.result?.accepted!=true);FleetSaving.ReceiveCompleted(packet.result?.accepted==true,packet.peer);});break;
            default:connection.Dispose();break;
        }
    }
    private void SendBootstrap(Connection connection)
    {
        // Ready() already requires an initialised, non-loading world with a player
        // community; the leftover NewGamePanel in the scene after an auto-start
        // must not block the first bootstrap.
        if(!Ready()||!connection.Open||FleetSaving.Busy)return;
        _resyncAfter[connection.Peer]=Time.realtimeSinceStartup+3f;
        try
        {
            RefreshWorld();
            Community? ship=null;
            if(_options.mode=="independent")
            {
                var member=_crew.Members().FirstOrDefault(m=>m.id==connection.Peer);
                if(FleetEngineBundle.FindShipById(OwnerOf(connection.Peer))==null&&!_declinedOwnership.Contains(OwnerOf(connection.Peer)))
                {
                    var oldShips=FleetEngineBundle.SecondaryCommunities.Where(c=>!FleetOwnership.Verified(c)&&FleetRuntime.ExtractShipOwnerName(c.Name)==member?.name).ToArray();
                    if(oldShips.Length==1)
                    {
                        if(!_ownershipRequests.Any(r=>r.Peer==connection.Peer))_ownershipRequests.Add(new ShipOwnershipRequest{Id=++_nextOwnershipRequest,Peer=connection.Peer,Owner=OwnerOf(connection.Peer),Player=member!.name,Ship=oldShips[0]});
                        connection.Send(new Packet{type="error",error="发现旧版船只，正在等待房主确认归属"});return;
                    }
                }
                ship=FleetRuntime.EnsurePlayerShip(OwnerOf(connection.Peer),member?.name??"同行者");
            }
            if(!PersistenceManager.TryGetSaveData(out var bytes)){Plugin.Log("bootstrap failed: save snapshot unavailable");connection.Send(new Packet{type="error",error="暂时无法取得世界快照"});return;}
            SnapshotGuard.Validate(bytes);
            Plugin.Log($"bootstrap {connection.Peer}: {bytes.Length} bytes, ship={ship?.Name ?? "-"}");
            _replication.InvalidateStaticCaches();
            connection.Send(new Packet{type="bootstrap",bootstrap=Convert.ToBase64String(bytes),peer=Community.PlayerCommunity.Name,ship=ship?.Name??"",world=CaptureFrame()});
            _bootstrapped.Add(connection.Peer);
        }
        catch(Exception e)
        {
            Plugin.Log($"bootstrap failed for {connection.Peer}: {e}");
            connection.Send(new Packet{type="error",error="暂时无法取得世界快照"});
        }
    }
    internal int RestoreStarted {get;private set;}
    internal int RestoreApplied {get;private set;}
    private void Restore(Packet packet)
    {
        try
        {
            if(Session.Profile==null){RequestResync("游戏会话尚未就绪，正在重试同步");return;}
            var bytes=Convert.FromBase64String(packet.bootstrap);SnapshotGuard.Validate(bytes);
            if(packet.world==null||!Guid.TryParseExact(packet.world.epoch,"N",out _))throw new InvalidDataException("Invalid world generation");
            RestoreStarted++;ReplicaReady=false;_canSubmit=false;AppliedFrame=null;_epoch=packet.world.epoch;Applying=true;
            _localShip=packet.ship??"";_adopted=false;
            try
            {
                _replication.Reset();_latest=null;Interlocked.Exchange(ref _incomingWorld,null);
                AccessTools.Field(typeof(PersistenceManager),"_snapShot").SetValue(null,bytes);
                if(!PersistenceManager.SetRestoreSnapShot())throw new InvalidDataException("无法载入快照");
                var run=new PlayerRun(Session.Profile,"联机世界",null,Path.Combine(_options.profile,"Saves")+Path.DirectorySeparatorChar);
                Session.Profile.LoadRun(run);_latest=packet.world;_resyncing=false;Status="正在载入房主世界";
            }
            finally{Applying=false;}
        }
        catch(Exception e){RequestResync("世界载入失败，正在重新同步："+e.Message,e);}
    }
    internal static bool Ready()=>GameManager.Initialized&&!LoadingScreen.IsLoading&&Community.PlayerCommunity!=null;
    public void Update()
    {
        if(_stop.IsCancellationRequested)return;
        var count=0;while(count++<32&&_main.TryDequeue(out var action)){try{action();}catch(Exception e){if(IsClient)RequestResync("同步处理失败，正在重新同步",e);else{Plugin.Log(e.ToString());Status="同步处理失败";}}}
        if(_pendingBootstrap!=null&&Session.IsReady&&Session.Profile!=null&&!LoadingScreen.IsLoading){var packet=_pendingBootstrap;_pendingBootstrap=null;Plugin.Log("applying host bootstrap");Restore(packet);return;}
        if(_pendingBootstrap!=null&&Time.realtimeSinceStartup>=_pendingLogAt){_pendingLogAt=Time.realtimeSinceStartup+3;Plugin.Log($"bootstrap waiting: session={Session.IsReady} profile={Session.Profile!=null} loading={LoadingScreen.IsLoading}");}
        var now=Time.realtimeSinceStartup;
        foreach(var pending in _pendingReplies.ToArray())if(now>pending.Value.until){_pendingReplies.Remove(pending.Key);_pendingNotices.Remove(pending.Key);pending.Value.callback(false);}
        if(IsHost&&now>=_nextAdmission)
        {
            _nextAdmission=now+.5f;
            foreach(var entry in _tickets)if(!TicketValid(entry.Value)&&_peers.TryGetValue(entry.Key,out var revoked))revoked.Dispose();
        }
        if(IsClient&&_resyncing&&_pendingBootstrap==null&&!LoadingScreen.IsLoading&&_server?.Open==true&&now>=_nextResync)Resync();
        if(IsHost&&_navigationUntil>0&&now>_navigationUntil&&Ready())StopNavigation();
        if(now>=_nextHeartbeat){_nextHeartbeat=now+3;var ping=new Packet{type="ping"};_server?.Send(ping);foreach(var peer in _peers.Values)peer.Send(ping);}
        if(!Ready())return;
        LiveNavigation.Update();
        if(!_resyncing&&(_latest==null||ReplicaReady))
        {
            var incoming=Interlocked.Exchange(ref _incomingWorld,null);
            if(incoming!=null&&incoming.epoch==_epoch&&(_latest==null||incoming.sequence>_latest.sequence))_latest=incoming;
        }
        if(IsHost&&now>=_nextSnapshot)
        {
            _ownershipRequests.RemoveAll(r=>!_peers.ContainsKey(r.Peer)||r.Ship==null);
            _nextSnapshot=now+(_peers.Count>0?.05f:.10f);
            RefreshWorld();
            foreach(var member in _crew.Members())if(!member.host&&!_peers.ContainsKey(member.id)){if(_crew.Leave(member.id))StopNavigation();if(_options.mode=="independent"){var ship=FleetEngineBundle.FindShipById(OwnerOf(member.id));if(ship!=null)FleetOwnedNavigation.Stop(ship);}_ledger.Forget(member.id);_owners.TryRemove(member.id,out _);}
            _bootstrapped.RemoveWhere(peer=>!_peers.ContainsKey(peer));
            foreach(var peer in _resyncAfter.Keys.ToArray())if(!_peers.ContainsKey(peer))_resyncAfter.Remove(peer);
            foreach(var peer in _peers.Values)if(!_bootstrapped.Contains(peer.Peer)&&(!_resyncAfter.TryGetValue(peer.Peer,out var after)||now>=after))SendBootstrap(peer);
            if(_peers.Count>0){var broadcast=new Packet{type="world",world=CaptureFrame()};foreach(var peer in _peers.Values)if(_bootstrapped.Contains(peer.Peer))peer.Send(broadcast);}
            else Ships=CaptureFleetSummary();
            Status=($"{(_options.mode=="independent"?"独立船队":"共建模式")} · {_peers.Count+1} 位玩家");
        }
        if(IsClient&&!_resyncing&&_latest!=null)
        {
            var frame=_latest;_latest=null;
            Applying=true;
            try
            {
                _replication.Apply(frame);
                _captain=frame.captain;_members=frame.crew;Ships=frame.ships??Array.Empty<FleetShipState>();
                AppliedFrame=frame;RestoreApplied=RestoreStarted;ReplicaReady=true;_canSubmit=_server?.Open==true;
                if(_canSubmit)Status=_options.mode=="independent"?$"独立船队 · {Math.Max(1,Ships.Length)} 艘船在线":"已连接 · 世界同步中";
            }
            catch(Exception e)
            {
                RequestResync("世界同步异常，正在重新同步",e);
            }
            finally{Applying=false;}
            // A guest drives its own ship, not the host's. Promote the assigned
            // community to the local player community once the replica is live.
            if(ReplicaReady&&!_adopted&&_options.mode=="independent"&&!_localShip.Equals(""))
            {
                _adopted=FleetRuntime.AdoptLocalShip(_localShip);
                if(_adopted){FleetVisibility.OnLocalShipAdoptedOnClient();FleetHud.FocusCameraOnOwnShip();}
            }
        }
        if(IsClient&&ReplicaReady)
        {
            if(Time.timeScale==0f&&GameSpeedManager.GameSpeed!=GameSpeed.Zero)Time.timeScale=1f;
            _replication.Interpolate(Time.unscaledDeltaTime);
        }
    }
    private void RefreshWorld(){if(ReferenceEquals(_hostWorld,GameManager.WorldManager.World))return;_hostWorld=GameManager.WorldManager.World;_epoch=Guid.NewGuid().ToString("N");_replication.Reset();_bootstrapped.Clear();_resyncAfter.Clear();}
    internal void NotifyWorldChanged()
    {
        if(!IsHost)return;
        _hostWorld=GameManager.WorldManager?.World;
        _epoch=Guid.NewGuid().ToString("N");
        _replication.Reset();
        _bootstrapped.Clear();
        _resyncAfter.Clear();
        _nextSnapshot=0f;
        ShowNotice("已抵达新海域，正在同步全体船员...",false);
    }
    internal void BroadcastRulesChanged(string notice)
    {
        if(!IsHost)return;
        _nextSnapshot=0f;
        ShowNotice(notice,false);
    }
    private void AddCrew(WorldFrame frame){frame.epoch=_epoch;frame.captain=Captain;frame.crew=Crew;}
    private WorldFrame CaptureFrame(){var timer=System.Diagnostics.Stopwatch.StartNew();var frame=_replication.Capture(++_sequence);AddCrew(frame);Ships=frame.ships;CaptureMilliseconds=(float)timer.Elapsed.TotalMilliseconds;return frame;}
    private FleetShipState[] CaptureFleetSummary()
    {
        var primary=Community.PlayerCommunity;
        if(primary==null)return Array.Empty<FleetShipState>();
        var list=new List<Community>{primary};
        foreach(var sec in FleetEngineBundle.SecondaryCommunities)if(sec!=null)list.Add(sec);
        var world=GameManager.WorldManager?.World;
        var nowPlay=GameManager.TimeManager!=null?GameManager.TimeManager.CurrentPlayTime:0f;
        return list.Select(owner=>{
            var isPrimary=ReferenceEquals(owner,primary);
            var root=FleetEngineBundle.RootFor(owner)??(isPrimary?Construction.Townheart:null);
            var pos=root!=null?root.transform.position:Vector3.zero;
            var rot=root!=null?root.transform.eulerAngles.y:0f;
            var mapPos=FleetOwnedNavigation.SceneToMap(pos);
            var mapRot=world!=null?(world.TownheartRotation*Quaternion.Euler(0f,rot,0f)).eulerAngles.y:rot;
            var key=Replication.ShipKey(owner);
            return new FleetShipState{
                name=key,
                peerId=isPrimary?"host":FleetEngineBundle.OwnerIdOf(owner),
                ownerName=isPrimary?(string.IsNullOrWhiteSpace(_options.nickname)?"房主":_options.nickname):FleetRuntime.ExtractShipOwnerName(owner.Name),
                x=pos.x,z=pos.z,rotation=rot,
                mapX=mapPos.x,mapZ=mapPos.z,mapRotation=mapRot,
                energy=FleetEngineBundle.EnergyOf(owner),
                buildings=owner.Buildables.Count(b=>b!=null),
                agents=owner.Agents.Count(a=>a!=null),
                birds=owner.Birds.Count(b=>b!=null),
                researchPoints=owner.Research?.ResearchPoints??0,
                flareUntil=FleetVisibility.GetFlareUntil(key,nowPlay),
                hasWatchTower=FleetVisibility.HasActiveWatchTower(owner),
                crippledHeart=BuildingDurability.IsShipHeartCrippled(owner),
                allies=FleetVisibility.GetAlliesFor(key,list)
            };
        }).ToArray();
    }
    private string OwnerOf(string peer)=>_owners.TryGetValue(peer,out var owner)?owner:peer;
    private bool TicketValid(string ticket)
    {
        if(string.IsNullOrEmpty(_options.admissions))return string.IsNullOrEmpty(_options.ticket); // Local sandbox has no launcher lobby.
        try{return ticket.Length>=32&&File.ReadAllLines(_options.admissions).Contains(ticket);}catch(IOException){return false;}
    }
    private bool IsCaptainPeer(string peer)=>Captain==peer;
    private string? ExecuteAuthorized(string peer,GameCommand command)
    {
        if(command.kind=="saveWorld")return !_crew.Members().Any(m=>m.id==peer)?"不在当前房间内":FleetSaving.Request(command.text,command.value,peer);
        if(command.kind=="shipOwnerAccept"||command.kind=="shipOwnerReject")
        {
            if(!IsHost||peer!=_peer)return "只有房主能确认旧船归属";
            var request=_ownershipRequests.FirstOrDefault(r=>r.Id==command.reference&&_peers.ContainsKey(r.Peer));
            if(request==null||request.Ship==null)return "该玩家已断开或旧船已不存在";
            _ownershipRequests.Remove(request);
            if(command.kind=="shipOwnerAccept")FleetOwnership.Bind(request.Ship,request.Owner);else _declinedOwnership.Add(request.Owner);
            if(_peers.TryGetValue(request.Peer,out var waiting))SendBootstrap(waiting);return null;
        }
        var actor = _options.mode=="independent" && peer!=_peer ? FleetEngineBundle.FindShipById(OwnerOf(peer)) : Community.PlayerCommunity;
        if(FleetCombat.Handles(command.kind))
        {
            if(actor==null)return "船只尚未就绪";
            return FleetCombat.Execute(command,actor,peer);
        }
        if(command.kind=="voyageRecall")return actor==null?"船只尚未就绪":FleetVoyages.Recall(command.reference,actor);
        if(command.kind=="seaPing")
        {
            if(!Replication.Finite(command.x)||!Replication.Finite(command.z))return "信标坐标无效";
            var sender=_crew.Members().FirstOrDefault(m=>m.id==peer)?.name??"船员";
            var label=string.IsNullOrWhiteSpace(command.text)?"海域集结":command.text.Trim();
            var pingPos=new Vector3(command.x,0f,command.z);
            FleetPhysicalActors.RecordAndSpawn("seaPing",$"{sender} · {label}",pingPos,pingPos,sourceShip:peer);
            return null;
        }
        if(command.kind=="captainRequest"&&_options.mode=="shared")
        {
            var sender=_crew.Members().FirstOrDefault(m=>m.id==peer)?.name??"船员";
            if(string.IsNullOrEmpty(sender))return "不在当前房间内";
            var pos=Construction.TownheartPosition;
            FleetPhysicalActors.RecordAndSpawn("captainRequest",sender,pos,pos,sourceShip:peer);
            return null;
        }
        if(_options.mode=="independent"&&peer!=_peer)
        {
            var member=_crew.Members().FirstOrDefault(m=>m.id==peer);
            if(member==null)return "不在当前房间内";
            var ship=FleetRuntime.EnsurePlayerShip(OwnerOf(peer),member.name);
            if(command.kind=="navigate")
            {
                var gear=Enum.IsDefined(typeof(MouseMovementCursorProperties.Gear),command.value)
                    ?(MouseMovementCursorProperties.Gear)command.value
                    :MouseMovementCursorProperties.Gear.Forward;
                FleetOwnedNavigation.StartMap(ship,command.x,command.z,gear);
                return null;
            }
            if(command.kind=="navigatePlan"){var wps=NavigationReplication.ParseWaypoints(command.text,command.x,command.z);FleetOwnedNavigation.StartMapPath(ship,wps);return null;}
            if(command.kind=="navigateKeys"){FleetOwnedNavigation.Steer(ship,command.x,command.z);return null;}
            if(command.kind=="navigateStop"){FleetOwnedNavigation.Stop(ship);return null;}
            if(command.kind=="mapOpen"||command.kind=="mapClose")return null;
            if(command.kind=="speed"||command.kind=="speedZero")return "独立模式下由房主统一控制全局时间倍速";
            Applying=true;try{return _replication.Execute(command,ship);}finally{Applying=false;}
        }
        var error=_crew.Authorize(peer,command.kind);if(error!=null)return error;
        if(command.kind=="captainTransfer"){error=_crew.Transfer(peer,command.text);if(error==null)StopNavigation();return error;}
        Applying=true;try{return _replication.Execute(command);}finally{Applying=false;}
    }
    public void ExecuteOrSubmit(GameCommand command,string? successNotice=null,Action<bool>? completed=null)
    {
        if(!command.kind.StartsWith("cannonAim",StringComparison.Ordinal)&&!command.kind.StartsWith("navigate",StringComparison.Ordinal)&&command.kind!="mapOpen"&&command.kind!="mapClose")
            FleetPhysicalActors.PlayUiClick();
        if(IsHost)
        {
            var error=ExecuteAuthorized(_peer,command);
            if(error==null)
            {
                if(command.kind=="navigate"||command.kind=="navigateKeys")_navigationUntil=Time.realtimeSinceStartup+1.2f;
                else if(command.kind=="navigateStop")_navigationUntil=0f;
                if(!string.IsNullOrEmpty(successNotice))ShowNotice(successNotice!,false);
            }
            else ShowNotice(error,true);
            completed?.Invoke(error==null);
        }
        else if(Submit(command))
        {
            if(!string.IsNullOrEmpty(successNotice))_pendingNotices[command.sequence]=successNotice!;
            if(completed!=null)_pendingReplies[command.sequence]=(completed,Time.realtimeSinceStartup+10);
        }
        else completed?.Invoke(false);
    }
    private void StopNavigation(){LiveNavigation.Stop();if(!Ready())return;var previous=Applying;Applying=true;try{_navigationUntil=0;GameManager.WorldMapManager.WorldMap.ClearMouseMovementTarget();GameManager.WorldMapManager.WorldMap.StopAllMovement();}finally{Applying=previous;}}
    public void TransferCaptain(string peer,string? successNotice=null)=>ExecuteOrSubmit(new GameCommand{kind="captainTransfer",text=peer},successNotice);
    internal void SaveCompleted(bool success,string name,string error,string peer)
    {
        var message=success?"世界已保存到房主："+name:"保存失败："+(error.Length>512?error.Substring(0,512):error);
        ShowNotice(message,!success);
        var packet=new Packet{type="saveResult",peer=peer,error=message,result=new CommandResult{accepted=success}};
        foreach(var connection in _peers.Values)connection.Send(packet);
    }
    public long QueueId(Producer producer,int index)=>_replication.Economy.QueueId(producer,index);
    public void QueueReleased(QueuedRecipe item)=>_replication.Economy.Released(item);
    public bool Submit(GameCommand command)
    {
        if(!ReplicaReady||!_canSubmit||_server?.Open!=true)
        {
            if(command.kind!="navigateStop")
                ShowNotice(_resyncing?"世界正在重新同步，请稍候":"未连接房主，操作未发送",true);
            return false;
        }
        command.epoch=_epoch;command.sequence=Interlocked.Increment(ref _commands);_server.Send(new Packet{type="command",command=command});
        return true;
    }
    public long Id(Buildable building)=>_replication.Id(building);
    public long Id(Agent agent)=>_replication.Id(agent);
    public long Id(Marker marker)=>_replication.Id(marker);
    public long Id(Bird bird)=>_replication.Id(bird);
    public Buildable? ResolveBuilding(long id)=>_replication.ResolveBuilding(id);
    public Agent? ResolveAgent(long id)=>_replication.ResolveAgent(id);
    private void RequestResync(string reason,Exception? error=null)
    {
        _latest=null;
        Interlocked.Exchange(ref _incomingWorld,null);
        ReplicaReady=false;
        _canSubmit=false;
        _resyncing=true;
        Status=reason;
        var now=Time.realtimeSinceStartup;
        if(error!=null&&now>=_nextErrorLog)
        {
            _nextErrorLog=now+3f;
            Plugin.Log($"{reason}: {error}");
            ShowNotice(reason,true);
        }
        if(now>=_nextResync)Resync();
    }
    public void Resync()
    {
        _latest=null;
        Interlocked.Exchange(ref _incomingWorld,null);
        ReplicaReady=false;
        _canSubmit=false;
        _resyncing=true;
        _nextResync=Time.realtimeSinceStartup+3f;
        _server?.Send(new Packet{type="resync"});
    }
    public void Dispose(){if(_stop.IsCancellationRequested)return;if(IsHost)StopNavigation();_stop.Cancel();_listener?.Stop();_server?.Dispose();foreach(var peer in _peers.Values)peer.Dispose();Status=IsClient?"连接已关闭，副本已冻结":"联机已关闭";}
}
