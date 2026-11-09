using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

// A request contains input, never a trusted polygon, length, prefab or resource balance.
// Existing GameCommand fields keep this slice independent of the shared wire schema:
// target/reference = expected start/end buildable ids (reference 0 means a new ponton),
// text = start hook input x;z, x/z = end cursor input, value = grid snapping.
internal static class ConstructionCommands
{
    internal const string PlaceRoad="roadPlace";
    private static readonly Type LineType=typeof(LineConstructionCursorProperties);
    private static FieldInfo Field(Type type,string name)=>AccessTools.Field(type,name);
    private static T Get<T>(object cursor,string name)=>(T)Field(LineType,name).GetValue(cursor);
    private static void Set(object cursor,string name,object? value)=>Field(LineType,name).SetValue(cursor,value);
    private static MethodInfo Method(string name,params Type[] args)=>AccessTools.Method(LineType,name,args);
    private static readonly MethodInfo UpdateStart=AccessTools.DeclaredMethod(typeof(ConstructionCursorProperties),"UpdateTransform",new[]{typeof(ConstructionPreview),typeof(Vector3),typeof(Vector2).MakeByRefType(),typeof(bool),typeof(Hookable)});
    private static readonly MethodInfo UpdateLine=Method("UpdateLinePreview",typeof(bool),typeof(bool).MakeByRefType());
    private static readonly MethodInfo NativePlace=Method("PlaceLine",typeof(WalkwaySegment),typeof(bool),typeof(bool));
    private static readonly MethodInfo SetInput=AccessTools.PropertySetter(typeof(CursorManager),nameof(CursorManager.BuildingPosition));
    private static bool _replaying;
    // Native rotate-around-hook preview updates the global grid. The isolated command preview
    // must not move the host's own cursor grid, but all native geometry checks still execute.
    internal static bool PreserveHostGrid()=>!_replaying;

    internal static bool Submit(GameSession network,LineConstructionCursorProperties cursor)
    {
        if(cursor.GetType()!=LineType)return Reject(network,"移动/库存道路尚未适配，请由房主操作");
        var start=Get<ConstructionPreview>(cursor,"_startWaypointPreview");
        var end=Get<ConstructionPreview>(cursor,"_endWaypointPreview");
        var segment=Get<ConstructionPreview>(cursor,"_segmentPreview");
        var prefab=(Buildable)Field(typeof(BuildableCursorProperties),"_buildable").GetValue(cursor);
        if(start==null||end==null||segment==null||prefab==null||!start.IsHooked||start.Hookable==null||!segment.CanPlace||(!end.IsHooked&&!end.CanPlace))return Reject(network,"道路预览无效，请重新选择起点");
        var target=network.Id(start.Hookable.Buildable);
        var reference=end.IsHooked&&end.Hookable!=null?network.Id(end.Hookable.Buildable):0;
        if(target<=0||(end.IsHooked&&reference<=0))return Reject(network,"道路端点尚未同步，请稍后重试");
        var seed=Get<Vector2>(cursor,"_startPosition");
        var input=CursorManager.BuildingPosition;
        if(!Replication.Finite(seed.x)||!Replication.Finite(seed.y)||!Replication.Finite(input.x)||!Replication.Finite(input.z))return Reject(network,"道路坐标无效");
        network.Submit(new GameCommand{kind=PlaceRoad,properties=prefab.Properties.name,target=target,reference=reference,text=seed.x.ToString("R",CultureInfo.InvariantCulture)+";"+seed.y.ToString("R",CultureInfo.InvariantCulture),x=input.x,z=input.z,visual=end.VisualIndex,value=Settings.Instance.GameplayPlayerData.SnapBuilding?1:0});
        // No local PlaceLine, ponton, inventory or predicted neighbour graph. Reset to the first
        // native preview; continuous extension must wait for the authoritative endpoint frame.
        Method("CancelIteration",Type.EmptyTypes).Invoke(cursor,Array.Empty<object>());
        return true;
    }

    private static bool Reject(GameSession network,string error){network.LastResult=new CommandResult{accepted=false,error=error};return false;}

    internal static string? Execute(GameCommand command,IReadOnlyDictionary<string,BuildableProperties> properties,IReadOnlyDictionary<long,Buildable> buildings,Community? owner=null)
    {
        var community=owner??Community.PlayerCommunity;
        using var _scope=FleetRuntime.WithPlayerCommunity(community);
        var heartPos=FleetEngineBundle.RootFor(community)?.transform.position??Construction.TownheartPosition;
        if(!properties.TryGetValue(command.properties,out var p)||p.Prefab==null||!GameSettings.Instance.BuildableSettings.Buildables.Contains(p))return "道路类型不存在";
        if(p.PlacementCursorProperties==null||p.PlacementCursorProperties.GetType()!=LineType||!p.Prefab.TryGetComponent<Hookable>(out _))return "此类型不是已适配的原生道路";
        if(!p.IsUnlocked())return "道路尚未解锁";
        if(command.value!=0&&command.value!=1)return "道路吸附参数无效";
        if(command.visual<0||p.Visuals==null||command.visual>=p.Visuals.Length)return "道路外观无效";
        if(BuildingDevTools.InstantBuild||BuildingDevTools.AutoSpawnResources||BuildingDevTools.InstantUnlock)return "联机道路不允许开发者资源或瞬建模式";
        var coordinates=command.text.Split(';');
        if(coordinates.Length!=2||!float.TryParse(coordinates[0],NumberStyles.Float,CultureInfo.InvariantCulture,out var sx)||!float.TryParse(coordinates[1],NumberStyles.Float,CultureInfo.InvariantCulture,out var sz)||!Replication.Finite(sx)||!Replication.Finite(sz)||!Replication.Finite(command.x)||!Replication.Finite(command.z))return "道路坐标无效";
        if(!TryEndpoint(buildings,command.target,community,out var expectedStart)||command.reference<0)return "道路起点已失效";
        Hookable? expectedEnd=null;
        if(command.reference!=0&&!TryEndpoint(buildings,command.reference,community,out expectedEnd))return "道路终点已失效";
        if(expectedStart==expectedEnd)return "道路不能连接同一建筑";
        var radius=GameSettings.Instance.GameplaySettings.ConstructionRadius;
        var seed=new Vector3(sx,0,sz);var input=new Vector3(command.x,0,command.z);
        if((seed-heartPos).sqrMagnitude>(radius+1000)*(radius+1000)||(input-heartPos).sqrMagnitude>(radius+1000)*(radius+1000))return "道路输入超出支持范围";
        if(p.RequiredResources.Any(r=>r.Amount>0))return "有材料消耗的道路端点尚未适配";
        if(!p.ReturnCanBePlaced(community,true))return "道路资源或建造条件不满足";

        var source=(LineConstructionCursorProperties)p.PlacementCursorProperties;
        if(source.Segments==null||source.Segments.Length==0||source.Segments.Any(s=>s==null||s.Length<1))return "道路原生段配置无效";
        var cursor=UnityEngine.Object.Instantiate(source);
        var previousInput=CursorManager.BuildingPosition;
        var previousSnap=Settings.Instance.GameplayPlayerData.SnapBuilding;
        var boundaryVisible=(bool)Field(typeof(VisualBoundary),"_visible").GetValue(null);
        var previousReplay=_replaying;
        try
        {
            // Do not Activate/Deactivate this clone: those methods operate host UI, the shared
            // grid and construction border. Only the clone's private previews are owned here.
            Set(cursor,"_startWaypointPreview",null);Set(cursor,"_endWaypointPreview",null);Set(cursor,"_segmentPreview",null);Set(cursor,"_currentSegment",null);
            cursor.Initialize(p.Prefab,command.visual);
            Method("InitializeSegments",Type.EmptyTypes).Invoke(cursor,Array.Empty<object>());
            var previewSettings=(VisualPrefabPreviewSettings)Field(typeof(BuildableCursorProperties),"_previewSettings").GetValue(cursor);
            var start=new ConstructionPreview(p.Prefab,previewSettings,command.visual,false);
            Set(cursor,"_startWaypointPreview",start);
            var end=new ConstructionPreview(p.Prefab,previewSettings,command.visual,false);
            Set(cursor,"_endWaypointPreview",end);
            Settings.Instance.GameplayPlayerData.SnapBuilding=command.value==1;
            _replaying=true;
            // This is the same native start transform/hook/sidesnap routine used by the cursor.
            var args=new object?[]{start,seed,Vector2.zero,command.value==1,null};
            UpdateStart.Invoke(cursor,args);
            start.UpdateCanBePlaced(true,true);
            if(!start.CanPlace||!start.IsHooked||start.Hookable!=expectedStart)return "道路起点或原生碰撞校验失败，请重新选择";
            Set(cursor,"_startPosition",(Vector2)args[2]!);
            Set(cursor,"_endPosition",new Vector2(input.x,input.z));
            SetInput.Invoke(null,new object[]{input});
            // UpdateLinePreview runs native end snapping, minimum/maximum length, angle limits,
            // outline construction, blocking-line tests and resource checks. Two passes settle
            // rotate-around-hook's dependence on the preceding frame's endpoint.
            var lineArgs=new object[]{command.value==1,false};
            UpdateLine.Invoke(cursor,lineArgs);
            UpdateLine.Invoke(cursor,lineArgs);
            if(!(bool)lineArgs[1])return "道路位置、连接、碰撞或资源条件不满足";
            if(end.IsHooked?end.Hookable!=expectedEnd:expectedEnd!=null)return "道路终点已改变，请重新选择";
            if(end.IsHooked&&(end.Hookable==null||end.Hookable.Buildable.Community!=community))return "无权连接道路终点";
            var current=Get<WalkwaySegment>(cursor,"_currentSegment");
            var segmentPreview=Get<ConstructionPreview>(cursor,"_segmentPreview");
            if(current==null||current.Buildable==null||segmentPreview==null||!segmentPreview.CanPlace||!source.Segments.Contains(current))return "道路段预览无效";
            var length=Get<float>(cursor,"_wantedLength");
            if(!Replication.Finite(length)||length<1||length>1000)return "道路长度无效";
            var required=Get<CountedItemProperty[]>(cursor,"_requiredResources");
            if(!community.Inventory.ReturnContainsItems(required)||!community.Inventory.ReturnContainsItems(current.Buildable.Properties.RequiredResources)||!current.Buildable.Properties.ReturnCanBePlaced(community,true))return "道路实际材料不足或建造条件不满足";
            return (bool)NativePlace.Invoke(cursor,new object[]{current,false,false})?null:"原生道路放置被拒绝";
        }
        finally
        {
            SetInput.Invoke(null,new object[]{previousInput});
            Settings.Instance.GameplayPlayerData.SnapBuilding=previousSnap;
            _replaying=previousReplay;
            foreach(var field in new[]{"_segmentPreview","_endWaypointPreview","_startWaypointPreview"})
            {
                var preview=Get<ConstructionPreview>(cursor,field);
                if(preview!=null){preview.Destroy();Set(cursor,field,null);}
            }
            VisualBoundary.Display(boundaryVisible);
            UnityEngine.Object.Destroy(cursor);
        }
    }

    private static bool TryEndpoint(IReadOnlyDictionary<long,Buildable> buildings,long id,Community community,out Hookable? hook)
    {
        hook=null;
        return id>0&&buildings.TryGetValue(id,out var building)&&building!=null&&building.Community==community&&building.TryGetComponent<Hookable>(out hook)&&hook!=null&&Hookable.Hookables.Contains(hook);
    }
}
