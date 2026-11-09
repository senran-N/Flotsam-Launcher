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
    /// <summary>Community name of the ship assigned to the receiving peer.</summary>
    public string ship="";
    public int protocol=4;
    public long sequence;
    public WorldFrame? world;
    public GameCommand? command;
    public CommandResult? result;
}
public sealed class WorldFrame
{
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
}
public sealed class FleetActionState
{
    public long id;
    public string kind="",label="";
    public float startX,startZ,endX,endZ;
}
public sealed class FleetShipState
{
    public string name="",ownerName="",peerId="";
    public float x,z,rotation,mapX,mapZ,mapRotation,energy;
    public int buildings,agents,researchPoints;
    public string[] researched=Array.Empty<string>();
    public string[] unlockedTech=Array.Empty<string>();
    public ResearchState research=new ResearchState();
}
public sealed class BuildingState
{
    public long id;
    public string owner="";
    public string properties="",name="";
    public float x,y,z,rotation,health;
    public int visual,phase,assignmentLimit;
    public bool active,stored;
    public ItemCount[] inventory=Array.Empty<ItemCount>();
    public ProducerState? producer;
    public long[] neighbours=Array.Empty<long>();
    public long[] energyLinks=Array.Empty<long>();
    public long mooredTo;
    public PointState[] outline=Array.Empty<PointState>();
    public WalkwayState? walkway;
}
public sealed class AgentState
{
    public long id;
    public string owner="";
    public string name="";
    public int descriptorId,hunger,thirst;
    public string descriptor="";
    public float pollution;
    public AssignmentState[] assignments=Array.Empty<AssignmentState>();
    public ItemCount[] inventory=Array.Empty<ItemCount>();
    public float x,y,z,rotation;
    public int activity;
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
public sealed class MarkerState { public long id;public string owner="";public string properties="";public float x,y,z;public int radius,limit;public string[] disabled=Array.Empty<string>(); }
public sealed class CommandResult { public long sequence;public bool accepted;public string error=""; }

public static class Frames
{
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
        if(packet.command!=null&&(packet.command.epoch==null||packet.command.epoch.Length>64||packet.command.kind==null||packet.command.text==null||packet.command.properties==null||packet.command.kind.Length>32||packet.command.text.Length>128||packet.command.properties.Length>128))throw new InvalidDataException("Invalid command fields");
        return packet;
    }
    private static async Task Exact(Stream stream,byte[] bytes,CancellationToken cancellation)
    {
        var read=0;while(read<bytes.Length){var next=await stream.ReadAsync(bytes,read,bytes.Length-read,cancellation).ConfigureAwait(false);if(next==0)throw new EndOfStreamException();read+=next;}
    }
    public static string RandomToken(){var bytes=new byte[32];using var random=RandomNumberGenerator.Create();random.GetBytes(bytes);return Convert.ToBase64String(bytes);}
    public static string Sign(string secret,string side,string nonce,string peer,string fingerprint)
    {
        using var hmac=new HMACSHA256(Encoding.UTF8.GetBytes(secret));return Convert.ToBase64String(hmac.ComputeHash(JsonCodec.Encode(new[]{"4",side,nonce,peer,fingerprint})));
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
    public string Peer {get;}
    public bool Open=>!_stop.IsCancellationRequested;
    internal int PendingPackets {get{lock(_queue)return _queue.Count;}}
    public Connection(TcpClient client,string peer){_client=client;Peer=peer;client.NoDelay=true;}
    public void Send(Packet packet)
    {
        lock(_queue){if(!Open)return;if(packet.type=="world"&&_worldNode!=null){_worldNode.Value=packet;return;}if(_queue.Count>=16){Dispose();return;}var node=_queue.AddLast(packet);if(packet.type=="world")_worldNode=node;_available.Release();}
    }
    public async Task Run(Action<Connection,Packet> receive)
    {
        try
        {
            var stream=_client.GetStream();
            var reader=Task.Run(async()=>{while(Open){using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(15));var packet=await Frames.Read(stream,deadline.Token).ConfigureAwait(false);receive(this,packet);}},_stop.Token);
            var writer=Task.Run(async()=>{while(Open){Packet packet;if(await _available.WaitAsync(TimeSpan.FromSeconds(3),_stop.Token).ConfigureAwait(false)){lock(_queue){var node=_queue.First!;packet=node.Value;if(ReferenceEquals(node,_worldNode))_worldNode=null;_queue.RemoveFirst();}}else packet=new Packet{type="ping"};await Frames.Write(stream,packet,_stop.Token).ConfigureAwait(false);}},_stop.Token);
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
