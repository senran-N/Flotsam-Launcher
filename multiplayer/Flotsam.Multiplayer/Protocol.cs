using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flotsam.Multiplayer;

public sealed class Packet
{
    // A packet is immutable once queued. All broadcast writers reuse its bytes.
    private byte[]? _encoded;
    internal byte[] Encode(){lock(this){return _encoded??=JsonCodec.Encode(this);}}
    public string type="",peer="",nonce="",proof="",fingerprint="",error="",bootstrap="";
    public string name="";
    public string publicKey="",identityProof="",ticket="";
    /// <summary>Community name of the ship assigned to the receiving peer.</summary>
    public string ship="";
    public int protocol=Frames.ProtocolVersion;
    public long sequence;
    public int chunkCount,totalBytes;
    public WorldFrame? world;
    public GameCommand? command;
    public CommandResult? result;
}
public sealed class WorldFrame
{
    public bool delta;
    public long basis;
    public long[] removedBuildings=Array.Empty<long>(),removedAgents=Array.Empty<long>(),removedFlotsam=Array.Empty<long>(),removedMarkers=Array.Empty<long>(),removedBirds=Array.Empty<long>();
    public string epoch="";
    public string worldOwner="";
    public long sequence;
    public BuildingState[] buildings=Array.Empty<BuildingState>();
    public AgentState[] agents=Array.Empty<AgentState>();
    public int researchPoints;
    public string[] researched=Array.Empty<string>();
    public float worldX,worldZ,worldRotation;
    public int speed;
    public ResearchState research=new ResearchState();
    public string[] unlockedTech=Array.Empty<string>();
    public FlotsamState[] flotsam=Array.Empty<FlotsamState>();
    public MarkerState[] markers=Array.Empty<MarkerState>();
    public int days;
    public float dayTime,playTime,mapX,mapZ,mapRotation;
    public bool mapActive,navigationActive;
    public string[] assets=Array.Empty<string>();
    public string captain="";
    public CrewMember[] crew=Array.Empty<CrewMember>();
    public Rules rules=new Rules();
    public FleetShipState[] ships=Array.Empty<FleetShipState>();
    public FleetActionState[] actions=Array.Empty<FleetActionState>();
    public LandmarkState[] landmarks=Array.Empty<LandmarkState>();
    public BirdReplicaState[] birds=Array.Empty<BirdReplicaState>();
    public TradeOfferState[] tradeOffers=Array.Empty<TradeOfferState>();
    public AllianceRequestState[] allianceRequests=Array.Empty<AllianceRequestState>();
    public CannonState[] cannons=Array.Empty<CannonState>();
    public CannonShellState[] shells=Array.Empty<CannonShellState>();
    public FleetVoyageState[] voyages=Array.Empty<FleetVoyageState>();
    public string[] biomeReadyPeers=Array.Empty<string>();
    public float biomeCountdown=-1f;
    public string activeDialogue="";
}
public sealed class AllianceRequestState
{
    public long id;
    public string fromShip="",toShip="",fromOwner="";
    public float expiresAt;
}
public sealed class CannonState
{
    public long building;
    public float yaw,pitch=15,reload,buildRemaining;
    public string operatorId="";
}
public sealed class CannonShellState
{
    public long id;
    public float x,y,z,vx,vy,vz,age;
    public int tier;
    public string owner="";
}
public sealed class FleetVoyageState
{
    public long id;
    public long worker,boat;
    public string fromShip="",toShip="",kind="",status="",workerName="";
    public float x,y,z,hp=100,maxHp=100;
    public ItemCount[] cargo=Array.Empty<ItemCount>();
}
public sealed class TradeOfferState
{
    public long id;
    public string fromShip="",fromOwner="",toShip="",giveItem="",wantItem="";
    public int giveAmount,wantAmount;
}
public sealed class BirdReplicaState
{
    public long id,birdHouseId;
    public string owner="",name="",descriptor="",carriedItem="";
    public int descriptorId,state,happiness;
    public bool isFed;
    public float x,y,z,rotation;
}
public sealed class BirdHouseState
{
    public bool refillFood=true,exportItems=true;
    public int[] disabledGroups=Array.Empty<int>();
}
public sealed class DecorationState
{
    public string properties="",name="";
    public int turn,visual,phase,limit;
    public float progress,cropProgress;
    public int[] slots=Array.Empty<int>();
    public ItemCount[] inventory=Array.Empty<ItemCount>();
}
public sealed class EnergyWireState
{
    public int srcSlot=-1;
    public long dstBuilding;
    public int dstSlot=-1;
}
public sealed class StorageFilterState
{
    public int acceptedTags;
    public string[] disabledItems=Array.Empty<string>();
}
public sealed class AquaFarmState
{
    public string activeFish="";
    public FishEntryState[] broodstock=Array.Empty<FishEntryState>();
    public FishEntryState[] fishes=Array.Empty<FishEntryState>();
}
public sealed class FishEntryState
{
    public string fish="";
    public float consumed,progress;
    public bool hungry;
}
public sealed class DistributerItemState
{
    public string item="";
    public float available,threshold,amount;
}
public sealed class AttributeState
{
    public int type,level;
}
public sealed class DietEntryState
{
    public int vital,priority;
    public string item="";
}
public sealed class LandmarkState
{
    public float mapX,mapZ;
    public LandmarkActionState[] actions=Array.Empty<LandmarkActionState>();
    public ItemCount[] remainingItems=Array.Empty<ItemCount>();
}
public sealed class LandmarkActionState
{
    public int index,state,limit;
    public bool useBoat;
    public string owner="";
    public int[] disabledCategories=Array.Empty<int>();
    public string[] disabledItems=Array.Empty<string>();
    public int[] toggledRescueables=Array.Empty<int>();
}
public sealed class FleetActionState
{
    public long id,targetBuilding;
    public int subKind;
    public string kind="",label="",sourceShip="",targetShip="";
    public float startX,startZ,endX,endZ;
}
public sealed class FleetShipState
{
    public string name="",ownerName="",peerId="";
    public float x,z,rotation,mapX,mapZ,mapRotation,energy,flareUntil;
    public bool hasWatchTower,crippledHeart;
    public int buildings,agents,birds,researchPoints;
    public string[] researched=Array.Empty<string>();
    public string[] unlockedTech=Array.Empty<string>();
    public string[] allies=Array.Empty<string>();
    public ResearchState research=new ResearchState();
}
public sealed class BuildingState
{
    public long id;
    public string owner="";
    public string properties="",name="";
    public float x,y,z,rotation,health,structuralHp,maxStructuralHp,empUntil;
    public int visual,phase,assignmentLimit;
    public bool active,stored,underRepair;
    public ItemCount[] inventory=Array.Empty<ItemCount>();
    public ProducerState? producer;
    public long[] neighbours=Array.Empty<long>();
    public long[] energyLinks=Array.Empty<long>();
    public long mooredTo;
    public PointState[] outline=Array.Empty<PointState>();
    public WalkwayState? walkway;
    public DecorationState[] decorations=Array.Empty<DecorationState>();
    public EnergyWireState[] poleLinks=Array.Empty<EnergyWireState>();
    public StorageFilterState? storageFilter;
    public AquaFarmState? aquaFarm;
    public DistributerItemState[] distributers=Array.Empty<DistributerItemState>();
    public BirdHouseState? birdHouse;
}
public sealed class AgentState
{
    public long id;
    public string owner="";
    public string name="";
    public int descriptorId,hunger,thirst,level,spendablePoints;
    public string descriptor="",disease="";
    public float pollution,experience;
    public AssignmentState[] assignments=Array.Empty<AssignmentState>();
    public AttributeState[] attributes=Array.Empty<AttributeState>();
    public DietEntryState[] diets=Array.Empty<DietEntryState>();
    public ItemCount[] inventory=Array.Empty<ItemCount>();
    public float x,y,z,rotation;
    public int activity;
    public int terrain=1;
    public long boatId;
}
public sealed class ItemCount { public string properties="";public int section,count; }
public sealed class AssignmentState { public int type,priority;public bool enabled; }
public sealed class PointState { public float x,z; }
public sealed class WalkwayState { public long start,end;public float x1,z1,x2,z2,length; }
public sealed class CrewMember { public string id="",name="";public bool host; }
public sealed class GameCommand
{
    public string epoch="";
    public long sequence,target,reference;
    public string kind="",properties="",text="";
    public float x,z,rotation;
    public int visual,value,amount;
}
public sealed class ProducerState
{
    public int selected,priority,threshold;
    public RecipeState[] recipes=Array.Empty<RecipeState>();
    public QueueState[] queue=Array.Empty<QueueState>();
}
public sealed class RecipeState { public int index,amount;public bool prioritized;public string properties=""; }
public sealed class QueueState { public long id;public int recipe,stage;public float progress; }
public sealed class ResearchState { public string current="";public ResearchProgress[] progresses=Array.Empty<ResearchProgress>(); }
public sealed class ResearchProgress { public string guid="";public int progress; }
public sealed class FlotsamState { public long id;public string properties="";public float x,y,z,rotation,progress;public int visual;public ItemCount[] items=Array.Empty<ItemCount>(); }
public sealed class MarkerState { public long id;public string owner="";public string properties="";public float x,y,z,rotation;public int radius,limit;public string[] disabled=Array.Empty<string>(); }
public sealed class CommandResult { public long sequence;public bool accepted;public string error=""; }

public static class Frames
{
    public const int ProtocolVersion=5;
    public const int MaxBytes=8*1024*1024;
    public static async Task Write(Stream stream,Packet packet,CancellationToken cancellation)
    {
        var bytes=packet.Encode();if(bytes.Length>MaxBytes)throw new InvalidDataException("Frame exceeds limit");
        var prefix=new[]{(byte)(bytes.Length>>24),(byte)(bytes.Length>>16),(byte)(bytes.Length>>8),(byte)bytes.Length};
        await stream.WriteAsync(prefix,0,4,cancellation).ConfigureAwait(false);await stream.WriteAsync(bytes,0,bytes.Length,cancellation).ConfigureAwait(false);
    }
    public static async Task<Packet> Read(Stream stream,CancellationToken cancellation)
    {
        var prefix=new byte[4];await Exact(stream,prefix,cancellation).ConfigureAwait(false);
        var length=(prefix[0]<<24)|(prefix[1]<<16)|(prefix[2]<<8)|prefix[3];
        if(length<1||length>MaxBytes)throw new InvalidDataException("Invalid frame size");
        var bytes=new byte[length];await Exact(stream,bytes,cancellation).ConfigureAwait(false);var packet=JsonCodec.Decode<Packet>(bytes);
        if(packet==null||packet.name==null||packet.name.Length>24||packet.type==null||packet.peer==null||packet.nonce==null||packet.proof==null||packet.fingerprint==null||packet.error==null||packet.bootstrap==null||packet.ship==null||packet.type.Length>24||packet.peer.Length>128||packet.nonce.Length>128||packet.proof.Length>128||packet.fingerprint.Length>128||packet.error.Length>1024||packet.ship.Length>128)throw new InvalidDataException("Invalid packet fields");
        if(packet.publicKey==null||packet.publicKey.Length>2048||packet.identityProof==null||packet.identityProof.Length>1024||packet.ticket==null||packet.ticket.Length>128)throw new InvalidDataException("Invalid identity fields");
        if(packet.command!=null&&(packet.command.epoch==null||packet.command.epoch.Length>64||packet.command.kind==null||packet.command.text==null||packet.command.properties==null||packet.command.kind.Length>32||packet.command.text.Length>512||packet.command.properties.Length>128))throw new InvalidDataException("Invalid command fields");
        return packet;
    }
    private static async Task Exact(Stream stream,byte[] bytes,CancellationToken cancellation)
    {
        var read=0;while(read<bytes.Length){var next=await stream.ReadAsync(bytes,read,bytes.Length-read,cancellation).ConfigureAwait(false);if(next==0)throw new EndOfStreamException();read+=next;}
    }
    public static string RandomToken(){var bytes=new byte[32];using var random=RandomNumberGenerator.Create();random.GetBytes(bytes);return Convert.ToBase64String(bytes);}
    public static string Sign(string secret,string side,string nonce,string peer,string fingerprint)
    {
        using var hmac=new HMACSHA256(Encoding.UTF8.GetBytes(secret));return Convert.ToBase64String(hmac.ComputeHash(JsonCodec.Encode(new[]{"5",side,nonce,peer,fingerprint})));
    }
    public static bool Verify(string secret,string side,string nonce,string peer,string fingerprint,string signed)
    {
        try{var a=Convert.FromBase64String(Sign(secret,side,nonce,peer,fingerprint));var b=Convert.FromBase64String(signed);if(a.Length!=b.Length)return false;var difference=0;for(var i=0;i<a.Length;i++)difference|=a[i]^b[i];return difference==0;}catch(FormatException){return false;}
    }
}

// One reader and one writer per socket; a bounded queue prevents a slow peer retaining frames.
public sealed class Connection:IDisposable
{
    private readonly TcpClient _client;
    private readonly CancellationTokenSource _stop=new CancellationTokenSource();
    private readonly LinkedList<Packet> _queue=new LinkedList<Packet>();
    private LinkedListNode<Packet>? _worldNode;
    private readonly SemaphoreSlim _available=new SemaphoreSlim(0);
    private readonly WorldDeltaEncoder _encoder=new WorldDeltaEncoder();
    private readonly WorldDeltaDecoder _decoder=new WorldDeltaDecoder();
    private readonly BootstrapTransfer _bootstrap=new BootstrapTransfer();
    public string Peer {get;}
    public bool Open=>!_stop.IsCancellationRequested;
    public int Latency {get;private set;}
    internal int PendingPackets {get{lock(_queue)return _queue.Count;}}
    public Connection(TcpClient client,string peer){_client=client;Peer=peer;client.NoDelay=true;}
    public void Send(Packet packet)
    {
        if(packet.type=="ping"&&packet.sequence==0)packet=new Packet{type="ping",sequence=System.Diagnostics.Stopwatch.GetTimestamp()};
        lock(_queue){if(!Open)return;if(packet.type=="world"&&_worldNode!=null){_worldNode.Value=packet;return;}if(packet.type=="bootstrap"&&_worldNode!=null){_worldNode.Value=packet;_worldNode=null;return;}if(_queue.Count>=16){Dispose();return;}var node=_queue.AddLast(packet);if(packet.type=="world")_worldNode=node;_available.Release();}
    }
    public async Task Run(Action<Connection,Packet> receive)
    {
        try
        {
            var stream=_client.GetStream();
            var reader=Task.Run(async()=>{while(Open){using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(15));var packet=await Frames.Read(stream,deadline.Token).ConfigureAwait(false);if(packet.type=="ping"){Send(new Packet{type="pong",sequence=packet.sequence});continue;}if(packet.type=="pong"){var ms=(System.Diagnostics.Stopwatch.GetTimestamp()-packet.sequence)*1000d/System.Diagnostics.Stopwatch.Frequency;if(packet.sequence>0&&ms>=0&&ms<15000)Latency=(int)Math.Round(ms);continue;}if(packet.type=="bootstrapBegin"||packet.type=="bootstrapChunk"){var assembled=_bootstrap.Receive(packet);if(assembled==null)continue;packet=assembled;_decoder.Reset();}if(packet.type=="world"&&packet.world!=null)packet.world=_decoder.Apply(packet.world);receive(this,packet);}},_stop.Token);
            var writer=Task.Run(async()=>{while(Open){Packet packet;if(await _available.WaitAsync(TimeSpan.FromSeconds(3),_stop.Token).ConfigureAwait(false)){lock(_queue){var node=_queue.First!;packet=node.Value;if(ReferenceEquals(node,_worldNode))_worldNode=null;_queue.RemoveFirst();}}else packet=new Packet{type="ping",sequence=System.Diagnostics.Stopwatch.GetTimestamp()};if(packet.type=="bootstrap"){_encoder.Reset();foreach(var chunk in BootstrapTransfer.Split(packet))await Frames.Write(stream,chunk,_stop.Token).ConfigureAwait(false);}else{if(packet.type=="world"&&packet.world!=null)packet=new Packet{type="world",world=_encoder.Encode(packet.world)};await Frames.Write(stream,packet,_stop.Token).ConfigureAwait(false);}}},_stop.Token);
            await Task.WhenAny(reader,writer).ConfigureAwait(false);Dispose();
            try{await Task.WhenAll(reader,writer).ConfigureAwait(false);}catch(Exception)when(!Open){}
        }
        finally{Dispose();}
    }
    public void Dispose(){if(!_stop.IsCancellationRequested){_stop.Cancel();_client.Close();lock(_queue)_queue.Clear();}}
}

public sealed class CommandLedger
{
    private readonly Dictionary<string,long> _last=new Dictionary<string,long>();
    private readonly Dictionary<string,CommandResult> _results=new Dictionary<string,CommandResult>();
    private readonly Queue<string> _order=new Queue<string>();
    // Peers are assigned a new authenticated identity on reconnect. Closed peers can
    // never execute queued commands, so retaining their high-water mark indefinitely
    // only grows memory during repeated reconnects; bounded result history stays intact.
    public void Forget(string peer)=>_last.Remove(peer);
    public CommandResult Execute(string peer,GameCommand command,Func<GameCommand,string?> apply)
    {
        var key=peer+":"+command.sequence;
        if(_results.TryGetValue(key,out var previous))return previous;
        var result=new CommandResult{sequence=command.sequence};
        if(command.sequence<=0||(_last.TryGetValue(peer,out var last)&&command.sequence<=last)){result.error="过期指令";return result;}
        _last[peer]=command.sequence;
        try{result.error=apply(command)??"";result.accepted=result.error.Length==0;}catch(Exception){result.error="游戏拒绝执行此指令";}
        _results.Add(key,result);_order.Enqueue(key);if(_order.Count>4096)_results.Remove(_order.Dequeue());return result;
    }
}
