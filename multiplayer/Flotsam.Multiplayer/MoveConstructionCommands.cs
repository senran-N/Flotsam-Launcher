using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

// Integration is deliberately explicit: replace the old move-denial prefixes with the
// Client* prefixes below; do not keep both prefixes on the same method. Host commands use
// the existing GameCommand schema, never a recreated/FinishBuilding replacement object.
internal static class MoveConstructionCommands
{
    internal const string Move="buildingMove", Store="buildingStore";
    private static readonly Dictionary<MoveBuildableCursorProperties,Selection> Selections=new Dictionary<MoveBuildableCursorProperties,Selection>();
    private static readonly Dictionary<Buildable,string> Quarantined=new Dictionary<Buildable,string>();
    private static Obstacle? _movingObstacle;
    private static bool _obstaclePatchInstalled;
    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Obstacle),nameof(Obstacle.Initialize),new[]{typeof(List<Transform>),typeof(Vector2Polygon[])}),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(ReinitializeMovedObstacle)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Obstacle),nameof(Obstacle.Initialize),Type.EmptyTypes),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(ReinitializeMovedObstacleWithoutVertices)));
        _obstaclePatchInstalled=true;
    }
    private static bool ReinitializeMovedObstacle(Obstacle __instance,List<Transform> vertices)
    {
        if(__instance!=_movingObstacle)return true;
        __instance.Polygon.SetVertices(vertices);__instance.Polygon.Update();__instance.SetRadius();
        F(typeof(Obstacle),"_updateGraphs").SetValue(__instance,true);
        return false;
    }
    private static bool ReinitializeMovedObstacleWithoutVertices(Obstacle __instance)
    {
        if(__instance!=_movingObstacle)return true;
        __instance.Polygon.Update();__instance.SetRadius();
        F(typeof(Obstacle),"_updateGraphs").SetValue(__instance,true);
        return false;
    }
    private static FieldInfo F(Type type,string name)=>AccessTools.Field(type,name);
    private static readonly MethodInfo UpdateTransform=AccessTools.DeclaredMethod(typeof(ConstructionCursorProperties),"UpdateTransform",new[]{typeof(ConstructionPreview),typeof(Vector3),typeof(Vector2).MakeByRefType(),typeof(bool),typeof(Hookable)});
    private static bool Client=>Plugin.Instance?.Network is GameSession n&&n.IsClient&&n.ReplicaReady&&!n.Applying;
    // Set true ONLY after stored membership + gameObject active state are carried in the
    // authoritative frame and ApplyStoredState is connected. Default cannot silently desync.
    internal static bool StorageReplicationReady {get;set;}
    internal static bool Handles(string kind)=>kind==Move||kind==Store;
    internal static bool IsStored(Buildable building)=>building!=null&&building.Community!=null&&building.Community.StoredBuildables.Values.Any(list=>list.Contains(building));
    internal static string? QuarantineReason(Buildable building)=>Quarantined.TryGetValue(building,out var reason)?reason:null;
    // Integration: reject ALL target-based operations on this object, not only move/store.
    internal static bool IsQuarantined(Buildable building)=>Quarantined.ContainsKey(building);
    internal static void ResetClientPreviews(){foreach(var cursor in Selections.Keys.ToArray())Clear(cursor);}

    private sealed class Selection
    {
        internal readonly Buildable Building;
        internal readonly ConstructionPreview Preview;
        internal readonly SingleConstructionPreviewStrategy Strategy;
        internal readonly bool FromStorage;
        internal Selection(Buildable building,ConstructionPreview preview,SingleConstructionPreviewStrategy strategy,bool fromStorage=false){Building=building;Preview=preview;Strategy=strategy;FromStorage=fromStorage;}
    }

    private static string? Eligible(Buildable b,bool stored=false,Community? owner=null)
    {
        var community=owner??b?.Community??Community.PlayerCommunity;
        if(b==null||b.Community!=community)return "建筑已不存在或不属于本城镇";
        if(Quarantined.TryGetValue(b,out var reason))return "建筑已隔离，须由房主重新载入权威存档："+reason;
        if(b.Properties.PlacementCursorProperties==null||b.Properties.PlacementCursorProperties.GetType()!=typeof(SingleConstructionCursorProperties)||b.Properties.UseCustomSize||b.Properties.RequiresMooringPoint)return "此建筑不是已适配的普通单体建筑";
        if(b.BuildPhase!=BuildPhase.Finished||b.AssignedProject!=null)return "建筑尚未完工或仍有建造项目";
        var root=FleetEngineBundle.RootFor(community)??Construction.Townheart;
        if(!b.TryGetComponent<Construction>(out var construction)||construction==root||construction.Target==null||construction.Target.PrimaryMarker==null)return "不能移动城镇核心或缺少原生施工图节点的建筑";
        if(b.GetComponent<Boat>()!=null||b.GetComponent<WalkwaySegment>()!=null||b.GetComponent<WalkwayPonton>()!=null||b.GetComponent<EnergyGridBuildableComponent>()!=null||b.GetComponent<EnergyGridPole>()!=null||b.GetComponent<DecorationSlots>()!=null)return "道路、船只、能源和附属装饰建筑暂不支持移动/收纳";
        if(b.GetComponentInChildren<Buoyancy>(true)==null)return "建筑缺少原生浮力组件";
        if(b.ReturnAgentsOnBuildable().Length>0||b.GetComponentsInChildren<Agent>(true).Length>0||b.GetWorkers().Count>0)return "请先等待建筑上的居民和工作人员离开";
        var obstacle=b.GetComponent<Obstacle>();
        if(obstacle==null||((System.Collections.ICollection)F(typeof(Obstacle),"_pathfindingPolygons").GetValue(obstacle)).Count==0)return "建筑原生障碍图尚未初始化";
        if(b.TryGetComponent<Producer>(out var producer)&&producer.QueuedRecipes.Count>0)return "请先完成或取消建筑生产队列";
        foreach(var section in b.Inventory.SubInventoryTypes)
            if(b.Inventory.ReturnAllItems(section,new List<Item>()).Any(item=>item.IsReserved))return "建筑库存仍有预留物品";
        if(construction.NeighbourConstructions.Any(n=>n==null||n.Buildable==null||n.Buildable.GetComponent<WalkwaySegment>()!=null||n.Buildable.GetComponent<WalkwayPonton>()!=null))return "建筑仍连接道路或失效邻居，暂不可移动";
        if(!stored&&(!b.gameObject.activeSelf||!b.IsDraggable()||!community.Constructions.Contains(construction)||IsStored(b)))return "建筑当前不能被原生移动";
        if(!stored&&construction.NeighbourConstructions.Any(n=>!n.IsConnectedToTownheart(construction)))return "移动会切断其他建筑与城镇核心的连接";
        return null;
    }

    private static string? StoredEligible(Buildable building,Community? owner=null)
    {
        var community=owner??building.Community??Community.PlayerCommunity;
        if(!StorageReplicationReady)return "建筑收纳同步尚未接入";
        var error=Eligible(building,true,community);if(error!=null)return error;
        var construction=building.GetComponent<Construction>();
        if(!IsStored(building)||building.gameObject.activeSelf||building.IsActive||community.Constructions.Contains(construction)||construction.NeighbourConstructions.Count!=0||construction.Target.PrimaryMarker.Node!=null)return "库存建筑状态不完整，须等待权威同步";
        if(!building.IsDraggable())return "库存建筑不满足原生可移动条件";
        if(building.Inventory.SubInventoryTypes.Any(section=>section!=SubInventoryType.Composition&&building.Inventory.ReturnAllItems(section,new List<Item>()).Count>0))return "库存建筑仍有非结构物品，暂不可取出";
        return null;
    }

    // Prefix SetSelectedBuildableProperties(properties,continuous). Select the same last
    // native inventory object GetStoredBuildable would pop, but keep list/object untouched.
    internal static bool ClientPickStored(MoveBuildableCursorProperties __instance,BuildableProperties properties)
    {
        if(!Client)return true;
        Clear(__instance);
        if(!Community.PlayerCommunity.StoredBuildables.TryGetValue(properties,out var list)||list.Count==0){Reject("库存中没有此建筑");return false;}
        var building=list[list.Count-1];var error=StoredEligible(building);
        if(error!=null){Reject(error);return false;}
        CreateSelection(__instance,building,true);return false;
    }

    // Prefix MoveBuildableCursorProperties.SetSelectedBuildable(Buildable selected).
    // Creates ONLY a visual preview: calling native strategy.Activate(selected) here would
    // deactivate/disconnect the authoritative replica before the final placement click.
    internal static bool ClientPickUp(MoveBuildableCursorProperties __instance,Buildable selected)
    {
        if(!Client)return true;
        Clear(__instance);
        var error=Eligible(selected);
        if(error!=null){Reject(error);return false;}
        CreateSelection(__instance,selected,false);return false;
    }
    private static void CreateSelection(MoveBuildableCursorProperties __instance,Buildable selected,bool fromStorage)
    {
        var strategySource=(SingleConstructionPreviewStrategy)F(typeof(MoveBuildableCursorProperties),"_singleConstructionPreviewStrategy").GetValue(__instance);
        var strategy=UnityEngine.Object.Instantiate(strategySource);
        ConstructionPreview? preview=null;
        try
        {
            strategy.Initialize(selected.Properties.Prefab,selected.VisualIndex);
            var settings=(VisualPrefabPreviewSettings)F(typeof(BuildableCursorProperties),"_previewSettings").GetValue(__instance);
            preview=new ConstructionPreview(selected.Properties.Prefab,settings,selected.VisualIndex,true);
            Selections.Add(__instance,new Selection(selected,preview,strategy,fromStorage));
            F(typeof(MoveBuildableCursorProperties),"_constructionPreview").SetValue(__instance,preview);
            // Keep _buildable null: native removal/deactivation must not store or mutate it.
            F(typeof(BuildableCursorProperties),"_buildable").SetValue(__instance,null);
        }
        catch{preview?.Destroy();UnityEngine.Object.Destroy(strategy);throw;}
    }

    // Prefix MoveBuildableCursorProperties.UpdateConstuctionPreview() [native misspelling].
    internal static bool ClientUpdate(MoveBuildableCursorProperties __instance)
    {
        if(!Client)return true;
        if(!Selections.TryGetValue(__instance,out var selection))return false;
        if(selection.Building==null){Clear(__instance);return false;}
        Preview(selection.Strategy,selection.Preview,selection.Building,CursorManager.BuildingPosition,Settings.Instance.GameplayPlayerData.SnapBuilding);
        return false;
    }

    // Prefix MoveBuildableCursorProperties.MoveSelectedBuildable().
    // reference identifies the expected hook, x/z is raw cursor input; host recomputes all.
    internal static bool ClientPlace(MoveBuildableCursorProperties __instance)
    {
        if(!Client)return true;
        if(!Selections.TryGetValue(__instance,out var selection))return false;
        if(!selection.Preview.CanPlace||selection.Preview.Hookable==null){Reject("建筑位置或原生碰撞校验未通过");return false;}
        var network=Plugin.Instance!.Network!;
        var target=network.Id(selection.Building);var reference=network.Id(selection.Preview.Hookable.Buildable);
        if(target<=0||reference<=0){Reject("移动目标或挂钩尚未同步");return false;}
        var input=CursorManager.BuildingPosition;
        network.Submit(new GameCommand{kind=Move,target=target,reference=reference,x=input.x,z=input.z,value=Settings.Instance.GameplayPlayerData.SnapBuilding?1:0,amount=selection.FromStorage?1:0});
        Clear(__instance);return false;
    }

    // Prefix RemoveSelectedBuildable(bool store=false,bool toggleCategory=true). The native
    // right-click/store interaction reaches this immediately after ClientPickUp. Cursor
    // deactivation should call ClientCancel first, so cancellation never submits a store.
    internal static bool ClientRemove(MoveBuildableCursorProperties __instance,bool store)
    {
        if(!Client)return true;
        if(store&&Selections.TryGetValue(__instance,out var selection)&&!selection.FromStorage)
        {
            if(!StorageReplicationReady)Reject("建筑收纳同步尚未接入，未修改任何建筑");
            else
            {
                var network=Plugin.Instance!.Network!;var target=network.Id(selection.Building);
                if(target<=0)Reject("收纳建筑尚未同步");
                else network.Submit(new GameCommand{kind=Store,target=target});
            }
        }
        Clear(__instance);return false;
    }

    // Prefix DeactivateImmediately; native function can continue, its _buildable and preview
    // fields are already null. Also call on world reset/disconnect if this cursor persists.
    internal static void ClientCancel(MoveBuildableCursorProperties __instance){if(Selections.ContainsKey(__instance))Clear(__instance);}
    // Keep the existing deny prefix on SetSelectedBuildableProperties: stored retrieval,
    // roads and decorations are explicitly outside this slice.
    private static void Reject(string error){var network=Plugin.Instance?.Network;if(network!=null)network.LastResult=new CommandResult{accepted=false,error=error};}
    private static void Clear(MoveBuildableCursorProperties cursor)
    {
        if(!Selections.TryGetValue(cursor,out var selection))return;
        Selections.Remove(cursor);
        if(ReferenceEquals(F(typeof(MoveBuildableCursorProperties),"_constructionPreview").GetValue(cursor),selection.Preview))F(typeof(MoveBuildableCursorProperties),"_constructionPreview").SetValue(cursor,null);
        selection.Preview.Destroy();UnityEngine.Object.Destroy(selection.Strategy);
    }

    // Non-destructive preview: native UpdateTransform and UpdateCanBePlaced, temporarily omit
    // only the moving object's own collision/hook entry. All OTHER native blockers remain.
    private static void Preview(SingleConstructionPreviewStrategy strategy,ConstructionPreview preview,Buildable moving,Vector3 input,bool snap)
    {
        var blocking=Buildable.BlockingPolygons.Remove(moving.BlockingPolygon);
        var freeform=Buildable.FreeformBlockerPolygons.Remove(moving.OutlinePolygon);
        var hook=moving.GetComponent<Hookable>();var hookIndex=hook==null?-1:Hookable.Hookables.IndexOf(hook);
        if(hookIndex>=0)Hookable.Hookables.RemoveAt(hookIndex);
        var construction=moving.GetComponent<Construction>();var community=moving.Community;
        var constructionIndex=community.Constructions.IndexOf(construction);
        if(constructionIndex>=0)community.Constructions.RemoveAt(constructionIndex);
        try
        {
            var args=new object?[]{preview,input,Vector2.zero,snap,hook};
            UpdateTransform.Invoke(strategy,args);
            preview.UpdateCanBePlaced(true,true);
        }
        finally
        {
            if(blocking)Buildable.BlockingPolygons.Add(moving.BlockingPolygon);
            if(freeform)Buildable.FreeformBlockerPolygons.Add(moving.OutlinePolygon);
            if(hookIndex>=0)Hookable.Hookables.Insert(Math.Min(hookIndex,Hookable.Hookables.Count),hook!);
            if(constructionIndex>=0)community.Constructions.Insert(Math.Min(constructionIndex,community.Constructions.Count),construction);
        }
    }

    internal static string? Execute(GameCommand command,IReadOnlyDictionary<long,Buildable> buildings,Community? owner=null)
    {
        var community=owner??Community.PlayerCommunity;
        var heartPos=FleetEngineBundle.RootFor(community)?.transform.position??Construction.TownheartPosition;
        if(!Handles(command.kind))return "未知移动指令";
        if(!_obstaclePatchInstalled)return "原生移动障碍图重入补丁尚未安装";
        if(!GameSession.Ready())return "房主游戏尚未就绪";
        if(!buildings.TryGetValue(command.target,out var building)||building==null)return "目标建筑已不存在";
        if(command.kind==Move&&(command.amount<0||command.amount>1))return "库存取出标志无效";
        var fromStorage=command.kind==Move&&command.amount==1;
        var error=fromStorage?StoredEligible(building,community):Eligible(building,false,community);if(error!=null)return error;
        if(command.kind==Store&&!StorageReplicationReady)return "建筑收纳同步尚未接入";
        if(command.kind==Store&&building.Inventory.SubInventoryTypes.Any(section=>section!=SubInventoryType.Composition&&building.Inventory.ReturnAllItems(section,new List<Item>()).Count>0))return "请先清空建筑存货再收纳，结构材料会保留原对象";
        if(BuildingDevTools.InstantBuild||BuildingDevTools.AutoSpawnResources)return "联机移动不允许开发者资源模式";
        var source=building.Properties.PlacementCursorProperties;
        var settings=(VisualPrefabPreviewSettings)F(typeof(BuildableCursorProperties),"_previewSettings").GetValue(source);
        var strategy=ScriptableObject.CreateInstance<SingleConstructionPreviewStrategy>();
        ConstructionPreview? preview=null;
        var visible=(bool)F(typeof(VisualBoundary),"_visible").GetValue(null);
        try
        {
            strategy.Initialize(building.Properties.Prefab,building.VisualIndex);
            F(typeof(BuildableCursorProperties),"_previewSettings").SetValue(strategy,settings);
            preview=new ConstructionPreview(building.Properties.Prefab,settings,building.VisualIndex,true);
            if(command.kind==Move)
            {
                if((command.value!=0&&command.value!=1)||!Replication.Finite(command.x)||!Replication.Finite(command.z))return "移动输入无效";
                if(!buildings.TryGetValue(command.reference,out var expected)||expected==null||expected==building||expected.Community!=community||IsStored(expected)||!expected.TryGetComponent<Hookable>(out var expectedHook))return "移动挂钩已失效";
                var input=new Vector3(command.x,0,command.z);
                var radius=GameSettings.Instance.GameplaySettings.ConstructionRadius+100;
                if((input-heartPos).sqrMagnitude>radius*radius)return "移动超出支持范围";
                Preview(strategy,preview,building,input,command.value==1);
                if(!preview.CanPlace||!preview.IsHooked||preview.Hookable!=expectedHook)return "移动位置、碰撞或连接校验失败";
            }
            // Snapshot BEFORE any native mutation. A refused preview never touches graph, phase,
            // inventory or the stored dictionary. Native mutation is synchronous on the main thread.
            var journal=new Journal(building);
            var obstacle=building.GetComponent<Obstacle>();
            var obstaclePolygons=(List<PathfindingPolygon>)F(typeof(Obstacle),"_pathfindingPolygons").GetValue(obstacle);
            // Native Construction.Initialize would append duplicate pathfinding polygons and
            // LogException on this already-initialized obstacle. The installed exact-instance
            // prefix reuses its original live polygons; no shared prefab/properties are edited.
            var previousObstacle=_movingObstacle;
            try
            {
                _movingObstacle=obstacle;
                var storedPosition=Vector3.back*(GameSettings.Instance.GameplaySettings.DestructionRadius-50);
                F(typeof(SingleConstructionPreviewStrategy),"_storedPosition").SetValue(strategy,storedPosition);
                if(fromStorage)
                {
                    // Remove this exact object, not an arbitrary same-type inventory item.
                    // Native pickup already ran when it was stored: do not deactivate/remove
                    // its absent construction twice or recreate it from a prefab.
                    var map=(Dictionary<BuildableProperties,List<Buildable>>)F(typeof(Community),"_storedBuildables").GetValue(building.Community);
                    if(!map.TryGetValue(building.Properties,out var list)||!list.Remove(building))throw new InvalidOperationException("Stored object vanished before retrieval");
                    building.Community.OnStoredBuildableRemoved.Invoke(building.Properties);
                }
                else strategy.SetBuildable(building);
                if(command.kind==Store)strategy.StoreBuildable(building,false);
                else
                {
                    var same=building;
                    if(!strategy.MoveBuildable(ref same,preview)||same!=building)throw new InvalidOperationException("Native move did not retain the same object");
                    if(!journal.Active&&!fromStorage)building.Deactivate();
                }
                foreach(var polygon in obstaclePolygons)if(command.kind==Store)polygon.Clear();else polygon.UpdateGraphs();
                building.OutlinePolygon.FastUpdate();building.BlockingPolygon.FastUpdate();
                journal.AssertUnchangedItems();
                if(command.kind==Store)
                {
                    if(!IsStored(building)||building.gameObject.activeSelf||building.Community.Constructions.Contains(building.GetComponent<Construction>()))throw new InvalidOperationException("Native store postcondition failed");
                }
                else if(IsStored(building)||!building.gameObject.activeSelf||!building.Community.Constructions.Contains(building.GetComponent<Construction>())||(building.transform.position-preview.Transform.position).sqrMagnitude>.01f)throw new InvalidOperationException("Native move postcondition failed");
                return null;
            }
            catch(Exception failure)
            {
                try
                {
                    journal.Rollback();
                    return "原生移动/收纳失败，已回滚："+failure.GetType().Name;
                }
                catch(Exception rollbackFailure)
                {
                    var reason=failure.GetType().Name+" / rollback "+rollbackFailure.GetType().Name;
                    Quarantined[building]=reason;
                    try{if(GameSpeedManager.GameSpeed!=GameSpeed.Paused)GameSpeedManager.Pause();}catch(Exception pauseFailure){Plugin.Log("Unable to pause quarantined world: "+pauseFailure);}
                    Plugin.Log("Move quarantine "+building.Properties.name+": "+failure+"; rollback: "+rollbackFailure);
                    return "原生操作及回滚失败，建筑已隔离；房主须暂停并重载存档："+reason;
                }
            }
            finally
            {
                _movingObstacle=previousObstacle;
            }
        }
        finally{preview?.Destroy();VisualBoundary.Display(visible);UnityEngine.Object.Destroy(strategy);}
    }

    private sealed class Journal
    {
        private readonly Buildable _building;
        private readonly Vector3 _position;
        private readonly Quaternion _rotation;
        private readonly Construction _construction;
        private readonly Construction[] _neighbours;
        private readonly Dictionary<SubInventoryType,Item[]> _items=new Dictionary<SubInventoryType,Item[]>();
        private readonly Dictionary<BuildableProperties,Buildable[]> _stored;
        internal readonly bool Active;
        private readonly bool _objectActive;
        private readonly BuildPhase _phase;
        private readonly float _health;
        private readonly bool _constructionMember,_markerMember;
        private readonly int _graphNodeCount;
        private readonly PathfindingPolygon[] _polygons;
        private readonly Hookable? _hook;
        private readonly int _hookIndex;
        private readonly Hookable.Edge[]? _hookEdges;
        private readonly int _communityBuildingCount;
        internal string VerificationSummary=>"pose+rotation+phase+active+stored+neighbours+marker+graphNodes="+_graphNodeCount+"+polygons="+_polygons.Length+"+sameItems="+_items.Sum(pair=>pair.Value.Length);
        internal Journal(Buildable building)
        {
            _building=building;_position=building.transform.position;_rotation=building.transform.rotation;Active=building.IsActive;_objectActive=building.gameObject.activeSelf;
            _construction=building.GetComponent<Construction>();_neighbours=_construction.NeighbourConstructions.ToArray();
            foreach(var section in building.Inventory.SubInventoryTypes)_items.Add(section,building.Inventory.ReturnAllItems(section,new List<Item>()).ToArray());
            _stored=building.Community.StoredBuildables.ToDictionary(pair=>pair.Key,pair=>pair.Value.ToArray());
            _phase=building.BuildPhase;_health=building.Health;
            _constructionMember=building.Community.Constructions.Contains(_construction);
            _markerMember=_construction.Target.PrimaryMarker.Node!=null;
            _graphNodeCount=GameManager.GraphManager.ConstructionGraph.ReturnAllNodes().Count;
            _polygons=((List<PathfindingPolygon>)F(typeof(Obstacle),"_pathfindingPolygons").GetValue(building.GetComponent<Obstacle>())).ToArray();
            _hook=building.GetComponent<Hookable>();_hookIndex=_hook==null?-1:Hookable.Hookables.IndexOf(_hook);
            _hookEdges=_hook==null?null:((Hookable.Edge[])F(typeof(Hookable),"_edges").GetValue(_hook)).ToArray();
            _communityBuildingCount=building.Community.Buildables.Count;
        }
        internal void AssertUnchangedItems()
        {
            if(_building.BuildPhase!=_phase||_building.Inventory.SubInventoryTypes.Count()!=_items.Count||Mathf.Abs(_building.Health-_health)>.0001f)throw new InvalidOperationException("Phase, health or inventory sections changed");
            foreach(var pair in _items)
            {
                var now=_building.Inventory.ReturnAllItems(pair.Key,new List<Item>());
                if(now.Count!=pair.Value.Length||pair.Value.Any(item=>!now.Contains(item)))throw new InvalidOperationException("Inventory item identity changed");
            }
        }
        internal void Rollback()
        {
            // Restore membership/order without replacing surviving native List instances.
            // Never use GetStoredBuildable here: that pops an arbitrary same-type object.
            var community=_building.Community;
            var stored=(Dictionary<BuildableProperties,List<Buildable>>)F(typeof(Community),"_storedBuildables").GetValue(community);
            var hadMembership=IsStored(_building);
            foreach(var key in stored.Keys.ToArray())if(!_stored.ContainsKey(key))stored.Remove(key);
            foreach(var pair in _stored)
            {
                if(!stored.TryGetValue(pair.Key,out var list)){list=new List<Buildable>();stored.Add(pair.Key,list);}
                list.Clear();list.AddRange(pair.Value);
            }
            foreach(var neighbour in _construction.NeighbourConstructions.ToArray()){neighbour.RemoveNeighbour(_construction);_construction.RemoveNeighbour(neighbour);}
            // Native MoveBuildable may already have registered a node at its failed destination.
            // Remove it explicitly before native Initialize, otherwise it warns and retains it.
            var marker=_construction.Target.PrimaryMarker;
            marker.RemoveFromConstructionGraph();
            if(marker.Node!=null)throw new InvalidOperationException("Unable to remove failed destination marker");
            if(community.Constructions.Contains(_construction))community.RemoveConstruction(_construction);
            _building.gameObject.SetActive(true);_building.transform.SetPositionAndRotation(_position,_rotation);
            _construction.Initialize(_building); // exact-instance obstacle guard is still active
            if(!_markerMember)marker.RemoveFromConstructionGraph();
            if(!_constructionMember)community.RemoveConstruction(_construction);
            foreach(var polygon in _polygons)if(_constructionMember)polygon.UpdateGraphs();else polygon.Clear();
            if(Active)_building.Activate();else _building.Deactivate();
            // Hookable.Activate regenerates edge caches at the current transform, but a formerly
            // inactive object only ran Deactivate above. Restore its original valid cache too.
            if(_hook!=null)
            {
                F(typeof(Hookable),"_edges").SetValue(_hook,_hookEdges?.ToArray());
                Hookable.Hookables.RemoveAll(h=>h==_hook);
                if(_hookIndex>=0)Hookable.Hookables.Insert(Math.Min(_hookIndex,Hookable.Hookables.Count),_hook);
            }
            foreach(var neighbour in _neighbours){_construction.AddNeighbourConstruction(neighbour);neighbour.AddNeighbourConstruction(_construction);}
            _building.gameObject.SetActive(_objectActive);
            _building.GetComponentInChildren<Buoyancy>(true).ForceWaterLevel(_position.y);
            _building.OutlinePolygon.Update();_building.BlockingPolygon.Update();
            var restoredMembership=IsStored(_building);
            if(hadMembership!=restoredMembership)
            {
                if(restoredMembership)community.OnStoredBuildableAdded.Invoke(_building.Properties,false);
                else community.OnStoredBuildableRemoved.Invoke(_building.Properties);
            }
            AssertRollback();
        }
        private void AssertRollback()
        {
            AssertUnchangedItems();
            var community=_building.Community;
            var marker=_construction.Target.PrimaryMarker;
            var polygons=(List<PathfindingPolygon>)F(typeof(Obstacle),"_pathfindingPolygons").GetValue(_building.GetComponent<Obstacle>());
            var stored=community.StoredBuildables;
            if(_building.IsActive!=Active||_building.gameObject.activeSelf!=_objectActive||(_building.transform.position-_position).sqrMagnitude>.0001f||Quaternion.Angle(_building.transform.rotation,_rotation)>.01f||community.Buildables.Count!=_communityBuildingCount)throw new InvalidOperationException("Rollback pose/rotation/active/building count mismatch");
            if(stored.Count!=_stored.Count||_stored.Any(pair=>!stored.TryGetValue(pair.Key,out var list)||!pair.Value.SequenceEqual(list)))throw new InvalidOperationException("Rollback storage membership/order mismatch");
            if(community.Constructions.Contains(_construction)!=_constructionMember||(marker.Node!=null)!=_markerMember||GameManager.GraphManager.ConstructionGraph.ReturnAllNodes().Count!=_graphNodeCount)throw new InvalidOperationException("Rollback construction marker/node count mismatch");
            if(polygons.Count!=_polygons.Length||!_polygons.SequenceEqual(polygons))throw new InvalidOperationException("Rollback obstacle polygon identity/count mismatch");
            if(_construction.NeighbourConstructions.Count!=_neighbours.Length||_neighbours.Any(n=>!_construction.NeighbourConstructions.Contains(n)||!n.NeighbourConstructions.Contains(_construction)))throw new InvalidOperationException("Rollback neighbour graph mismatch");
            if(_hook!=null)
            {
                var edges=(Hookable.Edge[])F(typeof(Hookable),"_edges").GetValue(_hook);
                if(Hookable.Hookables.IndexOf(_hook)!=_hookIndex||Hookable.Hookables.Count(h=>h==_hook)!=(_hookIndex<0?0:1)||(_hookEdges!=null&&!_hookEdges.SequenceEqual(edges)))throw new InvalidOperationException("Rollback hook membership/edge cache mismatch");
            }
        }
    }

    // Replica integration, called with Network.Applying=true AFTER ordinary phase/active/item
    // application and BEFORE neighbour reconciliation. Wire must add BuildingState.stored.
    // Call for every frame, including bootstrap; do not infer storage from far-away coordinates.
    internal static void ApplyStoredState(Buildable building,bool stored)
    {
        var community=building.Community;var construction=building.GetComponent<Construction>();
        var map=(Dictionary<BuildableProperties,List<Buildable>>)F(typeof(Community),"_storedBuildables").GetValue(community);
        var wasStored=IsStored(building);
        var wasHidden=!building.gameObject.activeSelf;
        if(stored&&wasStored&&wasHidden)return;
        if(!stored&&!wasStored&&!wasHidden)return;
        if(stored)
        {
            if(building.IsActive)building.Deactivate();
            if(construction!=null&&community.Constructions.Contains(construction))construction.Remove();
            if(!map.TryGetValue(building.Properties,out var list)){list=new List<Buildable>();map.Add(building.Properties,list);}
            if(!list.Contains(building))list.Add(building);
            building.gameObject.SetActive(false);
            if(!wasStored)community.OnStoredBuildableAdded.Invoke(building.Properties,false);
        }
        else
        {
            ForgetStored(building);
            building.gameObject.SetActive(true);
            if(construction!=null&&!community.Constructions.Contains(construction))
            {
                // Re-register the existing graph marker, not Construction.Initialize (which
                // appends obstacle polygons). Ordinary replication never runs client pathfinding.
                construction.Target.PrimaryMarker.AddToConstructionGraph();
                community.AddConstruction(construction);
            }
        }
    }
    internal static void ForgetStored(Buildable building)
    {
        var map=(Dictionary<BuildableProperties,List<Buildable>>)F(typeof(Community),"_storedBuildables").GetValue(building.Community);
        var wasStored=IsStored(building);
        foreach(var key in map.Keys.ToArray())
        {
            map[key].RemoveAll(item=>item==null||ReferenceEquals(item,building));
            if(map[key].Count==0)map.Remove(key);
        }
        if(wasStored)building.Community.OnStoredBuildableRemoved.Invoke(building.Properties);
    }
}
