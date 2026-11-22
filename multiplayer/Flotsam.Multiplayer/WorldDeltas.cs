using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Flotsam.Multiplayer;

// Baselines follow packets actually written to the socket, so dropping queued world
// frames cannot leave a guest dependent on a frame it never received.
public sealed class WorldDeltaEncoder
{
    private WorldFrame? _previous;
    private readonly Dictionary<string,Dictionary<long,byte[]>> _cache=new Dictionary<string,Dictionary<long,byte[]>>();
    private int _frames;
    public void Reset(){_previous=null;_cache.Clear();_frames=0;}
    public WorldFrame Encode(WorldFrame frame)
    {
        var full=_previous==null||_previous.epoch!=frame.epoch||++_frames%40==0;
        var delta=WorldDeltas.Copy(frame);delta.delta=!full;delta.basis=full?0:_previous!.sequence;
        delta.buildings=Changed(frame,"buildings",frame.buildings,b=>b.id,full,out delta.removedBuildings);
        delta.agents=Changed(frame,"agents",frame.agents,b=>b.id,full,out delta.removedAgents);
        delta.flotsam=Changed(frame,"flotsam",frame.flotsam,b=>b.id,full,out delta.removedFlotsam);
        delta.markers=Changed(frame,"markers",frame.markers,b=>b.id,full,out delta.removedMarkers);
        delta.birds=Changed(frame,"birds",frame.birds,b=>b.id,full,out delta.removedBirds);
        _previous=frame;return delta;
    }
    private T[] Changed<T>(WorldFrame frame,string key,T[] values,Func<T,long> id,bool full,out long[] removed)
    {
        if(!_cache.TryGetValue(key,out var previous))_cache[key]=previous=new Dictionary<long,byte[]>();
        var next=WorldDeltas.Bytes(frame,key,values,id);var changed=new List<T>();
        foreach(var value in values){var identity=id(value);var bytes=next[identity];if(full||!previous.TryGetValue(identity,out var old)||!bytes.SequenceEqual(old))changed.Add(value);}
        removed=full?Array.Empty<long>():previous.Keys.Where(k=>!next.ContainsKey(k)).ToArray();_cache[key]=next;return changed.ToArray();
    }
}
public sealed class WorldDeltaDecoder
{
    private WorldFrame? _previous;
    public void Reset()=>_previous=null;
    public WorldFrame Apply(WorldFrame frame)
    {
        if(!frame.delta){_previous=frame;return frame;}
        if(_previous==null||_previous.epoch!=frame.epoch||_previous.sequence!=frame.basis)throw new InvalidDataException("世界增量缺少基准，请重新同步");
        var full=WorldDeltas.Copy(frame);full.delta=false;full.basis=0;
        full.buildings=Merge(_previous.buildings,frame.buildings,frame.removedBuildings,b=>b.id);
        full.agents=Merge(_previous.agents,frame.agents,frame.removedAgents,b=>b.id);
        full.flotsam=Merge(_previous.flotsam,frame.flotsam,frame.removedFlotsam,b=>b.id);
        full.markers=Merge(_previous.markers,frame.markers,frame.removedMarkers,b=>b.id);
        full.birds=Merge(_previous.birds,frame.birds,frame.removedBirds,b=>b.id);
        _previous=full;return full;
    }
    private static T[] Merge<T>(T[] old,T[] changes,long[] removed,Func<T,long> id)
    {
        var values=old.ToDictionary(id);foreach(var key in removed)values.Remove(key);foreach(var item in changes)values[id(item)]=item;return values.Values.ToArray();
    }
}
internal static class WorldDeltas
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldFrame,Dictionary<string,Dictionary<long,byte[]>>> Cache=new System.Runtime.CompilerServices.ConditionalWeakTable<WorldFrame,Dictionary<string,Dictionary<long,byte[]>>>();
    internal static Dictionary<long,byte[]> Bytes<T>(WorldFrame frame,string key,T[] values,Func<T,long> id)
    {
        var entries=Cache.GetOrCreateValue(frame);lock(entries){if(!entries.TryGetValue(key,out var encoded)){encoded=values.ToDictionary(id,v=>JsonCodec.Encode(v));entries.Add(key,encoded);}return encoded;}
    }
    private static readonly FieldInfo[] Fields=typeof(WorldFrame).GetFields();
    internal static WorldFrame Copy(WorldFrame source){var frame=new WorldFrame();foreach(var field in Fields)field.SetValue(frame,field.GetValue(source));return frame;}
}
