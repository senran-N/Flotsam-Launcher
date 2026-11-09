extern alias Game;
using System;
using Debris=Game::Flotsam;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

internal sealed class SalvageReplication
{
    private readonly Dictionary<long,Debris> _flotsam=new Dictionary<long,Debris>();
    private readonly Dictionary<long,(Vector3 pos,Quaternion rot)> _flotsamTargets=new Dictionary<long,(Vector3,Quaternion)>();
    private readonly Dictionary<long,Marker> _markers=new Dictionary<long,Marker>();
    private readonly Dictionary<string,MarkerCursorProperties> _cursors=new Dictionary<string,MarkerCursorProperties>(StringComparer.Ordinal);
    private readonly Dictionary<string,FlotsamProperties> _properties=new Dictionary<string,FlotsamProperties>(StringComparer.Ordinal);
    private readonly Dictionary<string,ItemProperties> _items=new Dictionary<string,ItemProperties>(StringComparer.Ordinal);
    public void Reset(){_flotsam.Clear();_flotsamTargets.Clear();_markers.Clear();_cursors.Clear();_properties.Clear();_items.Clear();}
    private void Catalog()
    {
        if(_cursors.Count!=0)return;
        foreach(var p in Resources.FindObjectsOfTypeAll<MarkerCursorProperties>())if(!_cursors.ContainsKey(p.name))_cursors.Add(p.name,p);
        foreach(var p in Resources.FindObjectsOfTypeAll<FlotsamProperties>())if(!_properties.ContainsKey(p.name))_properties.Add(p.name,p);
        foreach(var p in Resources.FindObjectsOfTypeAll<ItemProperties>())if(!_items.ContainsKey(p.name))_items.Add(p.name,p);
    }
    private static IEnumerable<(Community Community,bool Primary)> Owners()
    {
        if(Community.PlayerCommunity!=null)yield return (Community.PlayerCommunity,true);
        foreach(var secondary in FleetEngineBundle.SecondaryCommunities)if(secondary!=null)yield return (secondary,false);
    }
    public void Capture(WorldFrame frame,Func<UnityEngine.Object,long> id)
    {
        Catalog();
        frame.flotsam=GameManager.WorldManager.FlotsamInWorld.Where(f=>f!=null&&f.Initialized&&!f.Pooled).Select(f=>
        {
            var key=id(f);_flotsam[key]=f;var p=f.transform.position;
            return new FlotsamState{id=key,properties=f.Properties.name,x=p.x,y=p.y,z=p.z,rotation=f.transform.eulerAngles.y,visual=f.VisualPrefabIndex,progress=f.Inventory.ReturnCompositionProgress(),items=f.Inventory.ReturnAllItems().GroupBy(i=>i.Properties.name).Select(g=>new ItemCount{properties=g.Key,count=g.Count()}).ToArray()};
        }).ToArray();
        frame.markers=Owners().SelectMany(owner=>owner.Community.Markers.Where(m=>m!=null).Select(m=>
        {
            var key=id(m);_markers[key]=m;var p=m.transform.position;
            return new MarkerState{id=key,owner=owner.Community.Name??"",properties=m.MarkerCursorProperties.name,x=p.x,y=p.y,z=p.z,radius=m.CurrentRadiusIntervalIndex,limit=m.Project?.AssignmentLimit??1,disabled=m.ItemFilter.Where(e=>!e.Value).Select(e=>e.Key.name).OrderBy(s=>s,StringComparer.Ordinal).ToArray()};
        })).ToArray();
        var alive=new HashSet<long>(frame.flotsam.Select(f=>f.id));foreach(var key in _flotsam.Keys.ToArray())if(!alive.Contains(key))_flotsam.Remove(key);
        alive=new HashSet<long>(frame.markers.Select(m=>m.id));foreach(var key in _markers.Keys.ToArray())if(!alive.Contains(key))_markers.Remove(key);
    }
    public void Apply(WorldFrame frame,Action<UnityEngine.Object,long> bind)
    {
        Catalog();if(frame.flotsam.Length>12000||frame.markers.Length>256)throw new InvalidOperationException("Salvage world exceeds limit");
        var used=new HashSet<Debris>(_flotsam.Values);var alive=new HashSet<long>();
        foreach(var state in frame.flotsam)
        {
            if(state.id<=0||!alive.Add(state.id)||!Replication.Finite(state.x)||!Replication.Finite(state.y)||!Replication.Finite(state.z)||!Replication.Finite(state.rotation)||state.items.Length>128)throw new InvalidOperationException("Invalid flotsam state");
            if(!_properties.TryGetValue(state.properties,out var properties)||properties.FlotsamPrefab==null)throw new InvalidOperationException("Missing flotsam prefab");
            if(_flotsam.TryGetValue(state.id,out var previous)&&previous!=null&&!previous.Pooled&&previous.Properties!=properties){previous.SafelyDestroy();_flotsam.Remove(state.id);}
            if(!_flotsam.TryGetValue(state.id,out var f)||f==null||f.Pooled)
            {
                f=GameManager.WorldManager.FlotsamInWorld.FirstOrDefault(local=>local!=null&&!used.Contains(local)&&local.Properties==properties&&(local.transform.position-new Vector3(state.x,state.y,state.z)).sqrMagnitude<4);
                if(f==null){f=UnityEngine.Object.Instantiate(properties.FlotsamPrefab);f.Initialize(properties,state.visual);var initial=new List<Item>();foreach(var e in state.items){if(e.count<1||e.count>1000||!_items.TryGetValue(e.properties,out var item))throw new InvalidOperationException("Invalid flotsam inventory");for(var i=0;i<e.count;i++)initial.Add(new Item(item));}f.InitializeComposition(new CompositionInventory(initial));}
                _flotsam[state.id]=f;used.Add(f);bind(f,state.id);
            }
            var current=f.Inventory.ReturnAllItems();
            if(current.Count!=state.items.Sum(i=>i.count)||state.items.Any(e=>current.Count(i=>i.Properties.name==e.properties)!=e.count))
            {
                var items=new List<Item>();
                foreach(var entry in state.items){if(entry.count<1||entry.count>1000||!_items.TryGetValue(entry.properties,out var item))throw new InvalidOperationException("Invalid flotsam inventory");for(var i=0;i<entry.count;i++)items.Add(new Item(item));}
                (AccessTools.Field(typeof(FlotsamInventory),"_composition").GetValue(f.Inventory) as CompositionInventory)?.Clear();
                f.InitializeComposition(new CompositionInventory(items));
            }
            var targetPos=new Vector3(state.x,state.y,state.z);var targetRot=Quaternion.Euler(0,state.rotation,0);
            if(!_flotsamTargets.ContainsKey(state.id)||(f.transform.position-targetPos).sqrMagnitude>400f)f.UpdatePositionAndRotation(targetPos,targetRot);
            _flotsamTargets[state.id]=(targetPos,targetRot);
            f.StopAllCoroutines();
            foreach(var body in f.GetComponentsInChildren<Rigidbody>())body.isKinematic=true;
            if(f.VisualPrefab!=null)f.VisualPrefab.SetProgress(state.progress);
        }
        foreach(var pair in _flotsam.ToArray())if(!alive.Contains(pair.Key)){if(pair.Value!=null&&!pair.Value.Pooled)pair.Value.SafelyDestroy();_flotsam.Remove(pair.Key);_flotsamTargets.Remove(pair.Key);}
        var authoritative=new HashSet<Debris>(_flotsam.Values);
        foreach(var local in GameManager.WorldManager.FlotsamInWorld.ToArray())if(local!=null&&!authoritative.Contains(local))local.SafelyDestroy();
        var markerUsed=new HashSet<Marker>(_markers.Values);alive.Clear();
        foreach(var state in frame.markers)
        {
            if(state.id<=0||!alive.Add(state.id)||!_cursors.TryGetValue(state.properties,out var cursor)||state.radius<0||state.radius>=cursor.RadiusIntervalAmount||state.limit<1||state.limit>20||!Replication.Finite(state.x)||!Replication.Finite(state.z))throw new InvalidOperationException("Invalid salvage marker");
            if(!_markers.TryGetValue(state.id,out var marker)||marker==null)
            {
                var community=Replication.ResolveCommunity(state.owner)??Community.PlayerCommunity;
                marker=community.Markers.FirstOrDefault(m=>m!=null&&!markerUsed.Contains(m)&&m.MarkerCursorProperties==cursor&&(m.transform.position-new Vector3(state.x,state.y,state.z)).sqrMagnitude<1);
                if(marker==null){marker=UnityEngine.Object.Instantiate(cursor.MarkerPrefab,new Vector3(state.x,state.y,state.z),Quaternion.identity);marker.Restore(cursor,state.radius,null);marker.Community=community;community.Markers.Add(marker);}
                _markers[state.id]=marker;markerUsed.Add(marker);bind(marker,state.id);
                if(marker.Project==null)marker.LinkProject(new Project(Project(cursor),marker.gameObject,marker));
            }
            marker.transform.position=new Vector3(state.x,state.y,state.z);if(marker.CurrentRadiusIntervalIndex!=state.radius)marker.SetSizeIndex(state.radius);
            marker.Project!.AssignmentLimit=state.limit;
            foreach(var key in marker.ItemFilter.Keys.ToArray())marker.ItemFilter[key]=!state.disabled.Contains(key.name);
            marker.UpdatedAllowedItemPropertiesEvent.Invoke();AccessTools.Method(typeof(Marker),"UpdateFlotsamInRadius").Invoke(marker,null);
        }
        foreach(var pair in _markers.ToArray())if(!alive.Contains(pair.Key)){if(pair.Value!=null){if(pair.Value.Project!=null)pair.Value.Project.Stop(ProjectFlags.Cancelled);else UnityEngine.Object.Destroy(pair.Value.gameObject);}_markers.Remove(pair.Key);}
    }
    private static ProjectProperties Project(MarkerCursorProperties cursor)=>(ProjectProperties)AccessTools.Field(typeof(MarkerCursorProperties),"_projectProperties").GetValue(cursor);
    public void Interpolate(float dt)
    {
        var t=Mathf.Clamp01(dt*12f);
        foreach(var pair in _flotsamTargets)
        {
            if(!_flotsam.TryGetValue(pair.Key,out var f)||f==null||f.Pooled)continue;
            var cur=f.transform.position;
            if((cur-pair.Value.pos).sqrMagnitude>0.0001f||Quaternion.Angle(f.transform.rotation,pair.Value.rot)>0.1f)
                f.UpdatePositionAndRotation(Vector3.Lerp(cur,pair.Value.pos,t),Quaternion.Slerp(f.transform.rotation,pair.Value.rot,t));
        }
    }
    public string? Execute(GameCommand command,Community? actorCommunity=null)
    {
        Catalog();
        var owner=actorCommunity??Community.PlayerCommunity;
        var heartPos=FleetEngineBundle.RootFor(owner)?.transform.position??Construction.TownheartPosition;
        if(command.kind=="markerPlace")
        {
            if(!_cursors.TryGetValue(command.properties,out var cursor)||cursor.MarkerPrefab==null||owner.Markers.Count>=128)return "打捞标记类型无效";
            if(command.value<0||command.value>=cursor.RadiusIntervalAmount||command.amount<1||command.amount>20)return "打捞标记参数无效";
            var position=new Vector3(command.x,0,command.z);var max=cursor is SwimmingMarkerCursorProperties?GameSettings.Instance.GameplaySettings.SwimmingRadius:GameSettings.Instance.GameplaySettings.InteractionRadius;
            if((position-heartPos).sqrMagnitude>max*max)return "超出你船只的打捞范围";
            var node=GameManager.GraphManager.WaterSurfaceGraph.ReturnNode(new Vector2(command.x,command.z));var clearance=(int)AccessTools.Field(typeof(MarkerCursorProperties),"_requiredClearance").GetValue(cursor);
            if(node==null||node.Clearance<clearance)return "该位置不能打捞";
            var marker=UnityEngine.Object.Instantiate(cursor.MarkerPrefab,GameManager.WorldManager.WorldParent);
            var project=new Project(Project(cursor),marker.gameObject,marker){AssignmentLimit=command.amount};
            marker.Initialize(position,command.value,project,cursor);marker.Community=owner;owner.QueueProject(project);owner.Markers.Add(marker);return null;
        }
        if(!_markers.TryGetValue(command.target,out var target)||target==null)return "打捞标记已不存在";
        if(target.Community!=null&&target.Community!=owner&&!Plugin.Options.rules.Allows(command.kind))return "房主未允许修改其他船只的打捞标记";
        switch(command.kind)
        {
            case "markerRemove":target.Remove();return null;
            case "markerLimit":if(command.value<1||command.value>20)return "人数无效";target.SetAgentAmount(command.value);return null;
            case "markerSize":if(command.value<0||command.value>=target.MarkerCursorProperties.RadiusIntervalAmount)return "打捞范围无效";target.SetSizeIndex(command.value);return null;
            case "markerFilter":if(!_items.TryGetValue(command.properties,out var item)||!target.ItemFilter.ContainsKey(item))return "打捞物品无效";target.ToggleItemFilter(item);return null;
            default:return "未知打捞指令";
        }
    }
}
