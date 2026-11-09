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
    private float _navigationUntil;
    private float _nextSnapshot,_nextHeartbeat;
    public string Status="尚未连接";
    public int PeerCount=>_peers.Count;
    public bool ReplicaReady {get;private set;}
    public bool IsClient=>_options.role=="client";
    public bool IsHost=>_options.role=="host";
    public bool Connected=>IsHost ? _listener!=null : _server?.Open==true;
    public bool Applying {get;private set;}
    public WorldFrame? AppliedFrame {get;private set;}
    public FleetShipState[] Ships {get;private set;}=Array.Empty<FleetShipState>();
    public CommandResult? LastResult;
    internal CommandResult? LastRejected;
    public GameSession(NetConfig options)
    {
        _options=options;
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
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(12));
            using var close=deadline.Token.Register(()=>{if(connection==null)socket.Close();});
            var stream=socket.GetStream();var nonce=Frames.RandomToken();await Frames.Write(stream,new Packet{type="challenge",nonce=nonce},deadline.Token).ConfigureAwait(false);
            var hello=await Frames.Read(stream,deadline.Token).ConfigureAwait(false);
            if(hello.type!="hello"||hello.protocol!=4||hello.peer.Length!=32||!Guid.TryParseExact(hello.peer,"N",out _)||hello.nonce.Length>128||!Frames.Verify(_options.secret,"client",nonce,hello.peer,hello.fingerprint,hello.proof))throw new InvalidDataException("身份校验失败");
            if(hello.fingerprint!=_fingerprint){await Frames.Write(stream,new Packet{type="error",error="游戏或插件文件不一致"},deadline.Token).ConfigureAwait(false);return;}
            var identity=Guid.NewGuid().ToString("N");
            await Frames.Write(stream,new Packet{type="welcome",peer=identity,proof=Frames.Sign(_options.secret,"host",hello.nonce,hello.peer,_fingerprint+":"+identity)},deadline.Token).ConfigureAwait(false);
            connection=new Connection(socket,identity);
            if(!_peers.TryAdd(identity,connection))throw new InvalidDataException("此玩家已在房间内");
            var active=connection;
            _main.Enqueue(()=>{_crew.Join(active.Peer,CleanName(hello.name,"同行者"));SendBootstrap(active);});
            await connection.Run(ReceiveHost).ConfigureAwait(false);
        }
        catch(Exception e){_main.Enqueue(()=>Plugin.Log("Peer disconnected: "+e.GetType().Name));}
        finally{Interlocked.Decrement(ref _handshakes);if(connection!=null){if(_peers.TryGetValue(connection.Peer,out var current)&&ReferenceEquals(current,connection))_peers.TryRemove(connection.Peer,out _);connection.Dispose();}else socket.Close();}
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
            var challenge=await Frames.Read(stream,deadline.Token).ConfigureAwait(false);if(challenge.type!="challenge"||challenge.protocol!=4)throw new InvalidDataException("房主协议无效");
            var nonce=Frames.RandomToken();
            await Frames.Write(stream,new Packet{type="hello",peer=_peer,name=CleanName(_options.nickname,"同行者"),nonce=nonce,fingerprint=_fingerprint,proof=Frames.Sign(_options.secret,"client",challenge.nonce,_peer,_fingerprint)},deadline.Token).ConfigureAwait(false);
            var welcome=await Frames.Read(stream,deadline.Token).ConfigureAwait(false);
            if(welcome.type=="error")throw new InvalidDataException(welcome.error);
            if(welcome.type!="welcome"||welcome.protocol!=4||!Guid.TryParseExact(welcome.peer,"N",out _)||!Frames.Verify(_options.secret,"host",nonce,_peer,_fingerprint+":"+welcome.peer,welcome.proof))throw new InvalidDataException("房主身份校验失败");
            _server=new Connection(socket,welcome.peer);_main.Enqueue(()=>{_assignedPeer=welcome.peer;_captain="";_members=Array.Empty<CrewMember>();Status="等待房主世界";});await _server.Run(ReceiveClient).ConfigureAwait(false);
            if(!_stop.IsCancellationRequested)_main.Enqueue(()=>{_canSubmit=false;Status="房主连接已断开，副本已冻结";});
        }
        catch(Exception e){if(!_stop.IsCancellationRequested)_main.Enqueue(()=>Status=e is InvalidDataException?"连接失败："+e.Message:"连接中断，正在重试");}
        finally{_server?.Dispose();_server=null;socket?.Dispose();}
    }
    private void ReceiveHost(Connection connection,Packet packet)
    {
        if(packet.type=="ping")return;
        if(_main.Count>128){connection.Dispose();return;}
        if(packet.type=="resync"){_main.Enqueue(()=>{if(_resyncAfter.TryGetValue(connection.Peer,out var after)&&Time.realtimeSinceStartup<after)return;_resyncAfter[connection.Peer]=Time.realtimeSinceStartup+10;_bootstrapped.Remove(connection.Peer);SendBootstrap(connection);});return;}
        if(packet.type!="command"||packet.command==null){connection.Dispose();return;}
        var command=packet.command;
        if(_main.Count>128){connection.Dispose();return;}
        _main.Enqueue(()=>{
            if(!connection.Open)return;
            if(Ready())RefreshWorld();
            var result=command.epoch!=_epoch?new CommandResult{sequence=command.sequence,error="世界已切换，请等待重新同步"}:_ledger.Execute(connection.Peer,command,c=>ExecuteAuthorized(connection.Peer,c));
            if(result.accepted&&(command.kind=="navigate"||command.kind=="navigateKeys")&&(_options.mode=="shared"||connection.Peer==_peer))_navigationUntil=Time.realtimeSinceStartup+.75f;
            if(result.accepted&&command.kind=="navigateStop"&&(_options.mode=="shared"||connection.Peer==_peer))_navigationUntil=0;
            connection.Send(new Packet{type="result",result=result});
        });
    }
    private void ReceiveClient(Connection connection,Packet packet)
    {
        if(_main.Count>128){connection.Dispose();return;}
        switch(packet.type)
        {
            case "ping":return;
            case "world":if(packet.world!=null)Interlocked.Exchange(ref _incomingWorld,packet.world);break;
            case "bootstrap":Interlocked.Exchange(ref _incomingWorld,null);Plugin.Log($"bootstrap received from {connection.Peer}");_main.Enqueue(()=>{_canSubmit=false;_pendingBootstrap=packet;});break;
            case "result":_main.Enqueue(()=>{LastResult=packet.result;if(packet.result?.accepted==false)LastRejected=packet.result;});break;
            case "error":_main.Enqueue(()=>Status=packet.error);break;
            default:connection.Dispose();break;
        }
    }
    private void SendBootstrap(Connection connection)
    {
        // Ready() already requires an initialised, non-loading world with a player
        // community; the leftover NewGamePanel in the scene after an auto-start
        // must not block the first bootstrap.
        if(!Ready()||!connection.Open)return;
        RefreshWorld();
        Community? ship=null;
        if(_options.mode=="independent")
        {
            var member=_crew.Members().FirstOrDefault(m=>m.id==connection.Peer);
            ship=FleetRuntime.EnsurePlayerShip(connection.Peer,member?.name??"同行者");
        }
        if(!PersistenceManager.TryGetSaveData(out var bytes)){Plugin.Log("bootstrap failed: save snapshot unavailable");connection.Send(new Packet{type="error",error="暂时无法取得世界快照"});return;}
        SnapshotGuard.Validate(bytes);
        Plugin.Log($"bootstrap {connection.Peer}: {bytes.Length} bytes, ship={ship?.Name ?? "-"}");
        connection.Send(new Packet{type="bootstrap",bootstrap=Convert.ToBase64String(bytes),peer=Community.PlayerCommunity.Name,ship=ship?.Name??"",world=CaptureFrame()});
        _bootstrapped.Add(connection.Peer);
    }
    internal int RestoreStarted {get;private set;}
    internal int RestoreApplied {get;private set;}
    private void Restore(Packet packet)
    {
        try
        {
            if(Session.Profile==null){Status="游戏会话尚未就绪，请重新连接";return;}
            var bytes=Convert.FromBase64String(packet.bootstrap);SnapshotGuard.Validate(bytes);
            if(packet.world==null||!Guid.TryParseExact(packet.world.epoch,"N",out _))throw new InvalidDataException("Invalid world generation");
            RestoreStarted++;ReplicaReady=false;_canSubmit=false;AppliedFrame=null;_epoch=packet.world.epoch;Applying=true;
            _localShip=packet.ship??"";_adopted=false;
            try
            {
                _replication.Reset();_latest=null;
                AccessTools.Field(typeof(PersistenceManager),"_snapShot").SetValue(null,bytes);
                if(!PersistenceManager.SetRestoreSnapShot())throw new InvalidDataException("无法载入快照");
                var run=new PlayerRun(Session.Profile,"联机世界",null,Path.Combine(_options.profile,"Saves")+Path.DirectorySeparatorChar);
                Session.Profile.LoadRun(run);_latest=packet.world;Status="正在载入房主世界";
            }
            finally{Applying=false;}
        }
        catch(Exception e){Status="世界载入失败："+e.Message;Plugin.Log(Status);}
    }
    internal static bool Ready()=>GameManager.Initialized&&!LoadingScreen.IsLoading&&Community.PlayerCommunity!=null;
    public void Update()
    {
        if(_stop.IsCancellationRequested)return;
        var count=0;while(count++<32&&_main.TryDequeue(out var action)){try{action();}catch(Exception e){Plugin.Log(e.ToString());Status="同步处理失败，需重新同步";}}
        if(_pendingBootstrap!=null&&Session.IsReady&&Session.Profile!=null&&!LoadingScreen.IsLoading){var packet=_pendingBootstrap;_pendingBootstrap=null;Plugin.Log("applying host bootstrap");Restore(packet);return;}
        if(_pendingBootstrap!=null&&Time.realtimeSinceStartup>=_pendingLogAt){_pendingLogAt=Time.realtimeSinceStartup+3;Plugin.Log($"bootstrap waiting: session={Session.IsReady} profile={Session.Profile!=null} loading={LoadingScreen.IsLoading}");}
        var now=Time.realtimeSinceStartup;
        if(IsHost&&_navigationUntil>0&&now>_navigationUntil&&Ready())StopNavigation();
        if(now>=_nextHeartbeat){_nextHeartbeat=now+3;var ping=new Packet{type="ping"};_server?.Send(ping);foreach(var peer in _peers.Values)peer.Send(ping);}
        if(!Ready())return;
        LiveNavigation.Update();
        var incoming=Interlocked.Exchange(ref _incomingWorld,null);
        if(incoming!=null&&incoming.epoch==_epoch&&(_latest==null||incoming.sequence>_latest.sequence))_latest=incoming;
        if(IsHost&&now>=_nextSnapshot)
        {
            _nextSnapshot=now+.25f;
            RefreshWorld();
            foreach(var member in _crew.Members())if(!member.host&&!_peers.ContainsKey(member.id)){if(_crew.Leave(member.id))StopNavigation();if(_options.mode=="independent"){var ship=FleetEngineBundle.FindShipById(member.id);if(ship!=null)FleetOwnedNavigation.Stop(ship);}_ledger.Forget(member.id);}
            _bootstrapped.RemoveWhere(peer=>!_peers.ContainsKey(peer));
            foreach(var peer in _resyncAfter.Keys.ToArray())if(!_peers.ContainsKey(peer))_resyncAfter.Remove(peer);
            foreach(var peer in _peers.Values)if(!_bootstrapped.Contains(peer.Peer))SendBootstrap(peer);
            if(_peers.Count>0){var broadcast=new Packet{type="world",world=CaptureFrame()};foreach(var peer in _peers.Values)if(_bootstrapped.Contains(peer.Peer))peer.Send(broadcast);}
            Status=($"{(_options.mode=="independent"?"独立船队":"共建模式")} · {_peers.Count+1} 位玩家");
        }
        if(IsClient&&_latest!=null)
        {
            Applying=true;try{_replication.Apply(_latest);_captain=_latest.captain;_members=_latest.crew;Ships=_latest.ships??Array.Empty<FleetShipState>();AppliedFrame=_latest;RestoreApplied=RestoreStarted;ReplicaReady=true;_canSubmit=_server?.Open==true;if(_canSubmit)Status=_options.mode=="independent"?$"独立船队 · {Math.Max(1,Ships.Length)} 艘船在线":"已连接 · 世界同步中";_latest=null;}finally{Applying=false;}
            // A guest drives its own ship, not the host's. Promote the assigned
            // community to the local player community once the replica is live.
            if(!_adopted&&_options.mode=="independent"&&!_localShip.Equals(""))_adopted=FleetRuntime.AdoptLocalShip(_localShip);
        }
        if(IsClient&&ReplicaReady)Time.timeScale=0;
    }
    private void RefreshWorld(){if(ReferenceEquals(_hostWorld,GameManager.WorldManager.World))return;_hostWorld=GameManager.WorldManager.World;_epoch=Guid.NewGuid().ToString("N");_replication.Reset();_bootstrapped.Clear();}
    private void AddCrew(WorldFrame frame){frame.epoch=_epoch;frame.captain=Captain;frame.crew=Crew;}
    private WorldFrame CaptureFrame(){var frame=_replication.Capture(++_sequence);AddCrew(frame);Ships=frame.ships;return frame;}
    private string? ExecuteAuthorized(string peer,GameCommand command)
    {
        if(_options.mode=="independent"&&peer!=_peer)
        {
            var member=_crew.Members().FirstOrDefault(m=>m.id==peer);
            if(member==null)return "不在当前房间内";
            var ship=FleetRuntime.EnsurePlayerShip(peer,member.name);
            if(command.kind=="navigate"){FleetOwnedNavigation.StartMap(ship,command.x,command.z,4f+(command.value*2f));return null;}
            if(command.kind=="navigateKeys"){FleetOwnedNavigation.Steer(ship,command.x,command.z,4.5f+(command.value*1.5f));return null;}
            if(command.kind=="navigateStop"){FleetOwnedNavigation.Stop(ship);return null;}
            if(command.kind=="mapOpen"||command.kind=="mapClose")return null;
            if(command.kind=="speed"||command.kind=="speedZero")return "独立模式下由房主统一控制全局时间倍速";
            Applying=true;try{return _replication.Execute(command,ship);}finally{Applying=false;}
        }
        var error=_crew.Authorize(peer,command.kind);if(error!=null)return error;
        if(command.kind=="captainTransfer"){error=_crew.Transfer(peer,command.text);if(error==null)StopNavigation();return error;}
        Applying=true;try{return _replication.Execute(command);}finally{Applying=false;}
    }
    public void ExecuteOrSubmit(GameCommand command)
    {
        if(IsHost)
        {
            var error=ExecuteAuthorized(_peer,command);
            LastResult=new CommandResult{accepted=error==null,error=error??""};
        }
        else Submit(command);
    }
    private void StopNavigation(){LiveNavigation.Stop();if(!Ready())return;var previous=Applying;Applying=true;try{_navigationUntil=0;GameManager.WorldMapManager.WorldMap.ClearMouseMovementTarget();GameManager.WorldMapManager.WorldMap.StopAllMovement();}finally{Applying=previous;}}
    public void TransferCaptain(string peer){if(IsHost){var error=ExecuteAuthorized(_peer,new GameCommand{kind="captainTransfer",text=peer});LastResult=new CommandResult{accepted=error==null,error=error??""};}else Submit(new GameCommand{kind="captainTransfer",text=peer});}
    public long QueueId(Producer producer,int index)=>_replication.Economy.QueueId(producer,index);
    public void QueueReleased(QueuedRecipe item)=>_replication.Economy.Released(item);
    public void Submit(GameCommand command){if(!ReplicaReady||!_canSubmit||_server?.Open!=true)return;command.epoch=_epoch;command.sequence=Interlocked.Increment(ref _commands);_server.Send(new Packet{type="command",command=command});}
    public long Id(Buildable building)=>_replication.Id(building);
    public long Id(Agent agent)=>_replication.Id(agent);
    public long Id(Marker marker)=>_replication.Id(marker);
    public void Resync()=>_server?.Send(new Packet{type="resync"});
    public void Dispose(){if(_stop.IsCancellationRequested)return;if(IsHost)StopNavigation();_stop.Cancel();_listener?.Stop();_server?.Dispose();foreach(var peer in _peers.Values)peer.Dispose();Status=IsClient?"连接已关闭，副本已冻结":"联机已关闭";}
}
