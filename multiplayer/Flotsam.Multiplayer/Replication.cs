using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

internal sealed class Replication
{
    private readonly Dictionary<int,long> _ids=new Dictionary<int,long>();
    private readonly Dictionary<long,Buildable> _buildings=new Dictionary<long,Buildable>();
    private readonly PoseTrackMap _buildingTracks=new PoseTrackMap();
    private readonly Dictionary<long,Agent> _agents=new Dictionary<long,Agent>();
    private readonly PoseTrackMap _agentTracks=new PoseTrackMap();
    private readonly SnapshotSpanClock _spanClock=new SnapshotSpanClock();
    private readonly Dictionary<string,BuildableProperties> _properties=new Dictionary<string,BuildableProperties>(StringComparer.Ordinal);
    private readonly Dictionary<string,ItemProperties> _items=new Dictionary<string,ItemProperties>(StringComparer.Ordinal);
    private long _nextId=1,_applied;
    private bool _catalog;
    private readonly Dictionary<long,int> _knownBuildingPhases=new Dictionary<long,int>();
    private readonly Dictionary<long,float> _agentAlertCooldowns=new Dictionary<long,float>();
    internal readonly EconomyReplication Economy=new EconomyReplication();
    internal readonly SalvageReplication Salvage=new SalvageReplication();
    internal readonly BirdReplication Birds=new BirdReplication();
    public void Reset(){ClientOceanAnchor.Reset();ReplicaFreeze.Reset();AgentReplication.Reset();FleetVisibility.Reset();FleetInteractions.Reset();SeaLootAndLandmarks.Reset();FleetHullCollision.Reset();LandmarkReplication.Reset();DecorationReplication.Reset();SpecialistReplication.Reset();ProgressionReplication.Reset();ReplicaGeometry.Reset();MoveConstructionCommands.ResetClientPreviews();NavigationReplication.Reset();FleetPhysicalActors.Reset();_ids.Clear();_buildings.Clear();_buildingTracks.Clear();_agents.Clear();_agentTracks.Clear();_knownBuildingPhases.Clear();_agentAlertCooldowns.Clear();_spanClock.Reset();_properties.Clear();_items.Clear();_applied=0;_catalog=false;Economy.Reset();Salvage.Reset();Birds.Reset();}
    internal void InvalidateStaticCaches(){AgentReplication.InvalidateClientBudgets();Birds.InvalidateClientBudgets();}
    public long Id(Buildable value)
    {
        if(value==null)return 0;
        if(Plugin.Instance?.Network?.IsHost==true){var id=HostId(value);_buildings[id]=value;return id;}
        return _ids.TryGetValue(value.GetInstanceID(),out var known)?known:0;
    }
    public long Id(Agent value)
    {
        if(value==null)return 0;
        if(Plugin.Instance?.Network?.IsHost==true){var id=HostId(value);_agents[id]=value;return id;}
        return _ids.TryGetValue(value.GetInstanceID(),out var known)?known:0;
    }
    public long Id(Marker value)=>value!=null&&_ids.TryGetValue(value.GetInstanceID(),out var id)?id:0;
    public long Id(Bird value)=>value!=null&&_ids.TryGetValue(value.GetInstanceID(),out var id)?id:0;
    public Buildable? ResolveBuilding(long id)=>id>0&&_buildings.TryGetValue(id,out var b)?b:null;
    public Agent? ResolveAgent(long id)=>id>0&&_agents.TryGetValue(id,out var a)?a:null;
    private long HostId(UnityEngine.Object value){var instance=value.GetInstanceID();if(!_ids.TryGetValue(instance,out var id)){id=_nextId++;_ids.Add(instance,id);}return id;}
    private void Catalog()
    {
        if(_catalog)return;
        foreach(var p in Resources.FindObjectsOfTypeAll<BuildableProperties>())if(!_properties.ContainsKey(p.name))_properties.Add(p.name,p);
        foreach(var p in Resources.FindObjectsOfTypeAll<ItemProperties>())if(!_items.ContainsKey(p.name))_items.Add(p.name,p);
        _catalog=true;
    }
    private static IEnumerable<Community> AllPlayerCommunities()
    {
        yield return Community.PlayerCommunity;
        foreach(var secondary in FleetEngineBundle.SecondaryCommunities)if(secondary!=null)yield return secondary;
    }
    public WorldFrame Capture(long sequence)
    {
        Catalog();var community=Community.PlayerCommunity;
        var nowPlayTime=GameManager.TimeManager!=null?GameManager.TimeManager.CurrentPlayTime:0f;
        var allCommunities=AllPlayerCommunities().ToArray();
        foreach(var pair in _buildings.ToArray())if(pair.Value==null)_buildings.Remove(pair.Key);
        var buildings=new List<BuildingState>();var agents=new List<AgentState>();
        foreach(var owner in allCommunities)
        foreach(var b in owner.Buildables)
        {
            if(b==null)continue;var inventory=new List<ItemCount>();
            foreach(var section in b.Inventory.SubInventoryTypes.ToArray())
            {
                var grouped=new Dictionary<string,int>(StringComparer.Ordinal);
                foreach(var item in b.Inventory.ReturnAllItems(section,new List<Item>())){var key=item.Properties.name;grouped.TryGetValue(key,out var count);grouped[key]=count+1;}
                foreach(var pair in grouped)inventory.Add(new ItemCount{properties=pair.Key,section=(int)section,count=pair.Value});
            }
            var p=b.transform.position;
            var id=HostId(b);_buildings[id]=b;
            var mooredId=b.TryGetComponent<Boat>(out var boatComp)&&boatComp.CurrentMooringPoint is MooringPoint mp&&mp.Buildable!=null?HostId(mp.Buildable):0;
            var eLinks=new List<long>();
            if(b.TryGetComponent<EnergyGridBuildableComponent>(out var egc)&&egc.Connections!=null)
                foreach(var conn in egc.Connections)
                    if(conn is EnergyGridBuildableComponent otherEgc&&otherEgc.Buildable!=null&&otherEgc.Buildable!=b)
                        eLinks.Add(HostId(otherEgc.Buildable));
            var state=new BuildingState{id=id,owner=owner.Name??"",properties=b.Properties.name,name=b.CustomName??"",x=p.x,y=p.y,z=p.z,rotation=b.transform.eulerAngles.y,visual=b.VisualIndex,phase=(int)b.BuildPhase,assignmentLimit=b.AssignmentLimit,active=b.IsActive,stored=MoveConstructionCommands.IsStored(b),health=b.Health,inventory=inventory.ToArray(),producer=Economy.Capture(b),neighbours=b.TryGetComponent<Construction>(out var construction)?construction.NeighbourConstructions.Where(n=>n!=null&&n.Buildable.Community==owner).Select(n=>HostId(n.Buildable)).ToArray():Array.Empty<long>(),energyLinks=eLinks.Distinct().ToArray(),mooredTo=mooredId,outline=b.OutlinePositions?.Select(v=>new PointState{x=v.x,z=v.y}).ToArray()??Array.Empty<PointState>()};
            DecorationReplication.Capture(b,HostId,CaptureInventory,out state.decorations,out state.poleLinks);
            SpecialistReplication.CaptureBuilding(b,state);
            BuildingDurability.CaptureBuilding(b,state,nowPlayTime);
            state.birdHouse=BirdReplication.CaptureBirdHouse(b);
            if(b.TryGetComponent<WalkwaySegment>(out var segment))
            {
                var start=(Hookable)AccessTools.Field(typeof(WalkwaySegment),"_startHookable").GetValue(segment);var end=(Hookable)AccessTools.Field(typeof(WalkwaySegment),"_endHookable").GetValue(segment);
                if(start!=null&&end!=null)state.walkway=new WalkwayState{start=HostId(start.Buildable),end=HostId(end.Buildable),x1=segment.StartPosition.x,z1=segment.StartPosition.z,x2=segment.EndPosition.x,z2=segment.EndPosition.z,length=(float)AccessTools.Field(typeof(WalkwaySegment),"_scaledLength").GetValue(segment)};
            }
            buildings.Add(state);
        }
        foreach(var owner in allCommunities)
        foreach(var a in owner.Agents){if(a==null)continue;var p=a.transform.position;var id=HostId(a);_agents[id]=a;var nav=a.ReturnNavigator(true);var boatId=a.Boat?.Buildable!=null?HostId(a.Boat.Buildable):0;var state=new AgentState{id=id,owner=owner.Name??"",name=a.Name,x=p.x,y=p.y,z=p.z,rotation=a.transform.eulerAngles.y,activity=(int)a.CurrentActivity,terrain=nav!=null?(int)nav.Terrain:1,boatId=boatId};AgentReplication.Capture(a,state);state.inventory=CaptureInventory(a.Inventory);agents.Add(state);}
        var world=GameManager.WorldManager.World;
        Economy.Prune(allCommunities.SelectMany(c=>c.Buildables));
        var fleetShips=allCommunities.Select(owner=>{
            var root=FleetEngineBundle.RootFor(owner)??(ReferenceEquals(owner,community)?Construction.Townheart:null);
            var scenePos=root!=null?root.transform.position:Vector3.zero;
            var sceneRot=root!=null?root.transform.eulerAngles.y:0f;
            var mapPos=FleetOwnedNavigation.SceneToMap(scenePos);
            var mapRot=(world.TownheartRotation*Quaternion.Euler(0f,sceneRot,0f)).eulerAngles.y;
            var isPrimary=ReferenceEquals(owner,community);
            var shipKey=ShipKey(owner);
            return new FleetShipState{
                name=shipKey,
                peerId=isPrimary?"host":FleetEngineBundle.OwnerIdOf(owner),
                ownerName=isPrimary?(string.IsNullOrWhiteSpace(Plugin.Options.nickname)?"房主":Plugin.Options.nickname):FleetRuntime.ExtractShipOwnerName(owner.Name),
                x=scenePos.x,z=scenePos.z,rotation=sceneRot,
                mapX=mapPos.x,mapZ=mapPos.z,mapRotation=mapRot,
                energy=FleetEngineBundle.EnergyOf(owner),
                buildings=owner.Buildables.Count(b=>b!=null),
                agents=owner.Agents.Count(a=>a!=null),
                birds=owner.Birds.Count(b=>b!=null),
                researchPoints=owner.Research?.ResearchPoints??0,
                researched=owner.Research?.ResearchedBuildableProperties.Select(p=>p.name).OrderBy(s=>s,StringComparer.Ordinal).ToArray()??Array.Empty<string>(),
                unlockedTech=Economy.UnlockedTech(owner),
                research=Economy.CaptureResearch(owner),
                flareUntil=FleetVisibility.GetFlareUntil(shipKey,nowPlayTime),
                hasWatchTower=FleetVisibility.HasActiveWatchTower(owner),
                crippledHeart=BuildingDurability.IsShipHeartCrippled(owner),
                allies=FleetVisibility.GetAlliesFor(shipKey,allCommunities)
            };
        }).ToArray();
        var frame=new WorldFrame{sequence=sequence,worldOwner=community.Name??"",buildings=buildings.ToArray(),agents=agents.ToArray(),researchPoints=community.Research.ResearchPoints,researched=community.Research.ResearchedBuildableProperties.Select(p=>p.name).OrderBy(s=>s,StringComparer.Ordinal).ToArray(),worldX=world.TownheartWorldPosition.x,worldZ=world.TownheartWorldPosition.z,worldRotation=world.TownheartRotation.eulerAngles.y,speed=(int)GameSpeedManager.GameSpeed,research=Economy.CaptureResearch(community),unlockedTech=Economy.UnlockedTech(),rules=Plugin.Options.rules,ships=fleetShips,actions=FleetPhysicalActors.CaptureRecentActions(),tradeOffers=FleetInteractions.CaptureOffers(),allianceRequests=FleetVisibility.CaptureRequests(),cannons=FleetCombat.Capture(),shells=FleetCombat.CaptureShells(),voyages=FleetVoyages.Capture()};
        Salvage.Capture(frame,HostId);NavigationReplication.Capture(frame);LandmarkReplication.Capture(frame);Birds.Capture(frame,allCommunities,HostId);ProgressionReplication.Capture(frame);frame.assets=AssetTable.Capture();return frame;
    }
    public void Apply(WorldFrame frame)
    {
        if(frame.sequence<=_applied)return;
        if(frame.buildings.Length>10000||frame.agents.Length>500)throw new InvalidOperationException("World exceeds supported replica size");
        AssetTable.Apply(frame.assets);
        Catalog();
        // The frame names the host's authoritative community. On an independent-mode
        // client the local player community is its own ship, while vanilla Flotsam leaves
        // the host's primary Community.Name empty (""). Resolve the host ship explicitly.
        var community=ResolveShipCommunity(frame.worldOwner,"host")??Community.PlayerCommunity;
        var byName=new Dictionary<string,Community>(StringComparer.Ordinal);
        foreach(var candidate in Community.Communities)if(candidate!=null&&candidate.Name!=null)byName[candidate.Name]=candidate;
        Community Owned(string? name)=>!string.IsNullOrEmpty(name)&&byName.TryGetValue(name,out var found)?found:community;
        // Rebase scenery first: native movement events also move swimmers and
        // markers. Applying their authoritative poses afterwards avoids moving
        // those replicas a second time in the same frame.
        var world=GameManager.WorldManager.World;
        if(!Finite(frame.worldX)||!Finite(frame.worldZ)||!Finite(frame.worldRotation))throw new InvalidOperationException("Invalid world position");
        _spanClock.BeginFrame(Time.unscaledTime);
        ReplicaFreeze.FreezePhysics(GameManager.WorldManager.WorldParent);
        ClientOceanAnchor.SyncFrame(frame,world,_buildingTracks,_agentTracks,Salvage,Birds,_buildings.Values,_agents.Values);
        if(frame.ships!=null)
        {
            foreach(var s in frame.ships)
            {
                if(s==null||!Finite(s.x)||!Finite(s.z)||!Finite(s.rotation))continue;
                ClientOceanAnchor.HostToClientPose(s.x,s.z,s.rotation,out s.x,out s.z,out s.rotation);
            }
        }
        var used=new HashSet<int>(_buildings.Values.Where(b=>b!=null).Select(b=>b.GetInstanceID()));
        var alive=new HashSet<long>();
        foreach(var state in frame.buildings)
        {
            if(!Finite(state.x)||!Finite(state.y)||!Finite(state.z)||!Finite(state.rotation)||state.id<=0)throw new InvalidOperationException("Invalid building state");
            if(!alive.Add(state.id)||(state.owner?.Length??0)>64||!Enum.IsDefined(typeof(BuildPhase),state.phase)||!Finite(state.health)||state.health<0||state.health>1)throw new InvalidOperationException("Invalid building identity or phase");
            ClientOceanAnchor.HostToClientPose(state.x,state.z,state.rotation,out var bx,out var bz,out var byaw);
            if(!_buildings.TryGetValue(state.id,out var building)||building==null)
            {
                var owner=Owned(state.owner);
                building=owner.Buildables.FirstOrDefault(b=>b!=null&&!used.Contains(b.GetInstanceID())&&b.Properties.name==state.properties&&(b.transform.position-new Vector3(bx,state.y,bz)).sqrMagnitude<1);
                if(building==null)
                {
                    if(!_properties.TryGetValue(state.properties,out var properties)||properties.Prefab==null)throw new InvalidOperationException("Missing buildable: "+state.properties);
                    building=UnityEngine.Object.Instantiate(properties.Prefab,new Vector3(bx,state.y,bz),Quaternion.Euler(0,byaw,0));
                    if(state.outline.Length>256||state.outline.Any(v=>!Finite(v.x)||!Finite(v.z)))throw new InvalidOperationException("Invalid building outline");
                    building.Initialize(owner,state.visual,state.outline.Length>0?state.outline.Select(v=>new Vector2(v.x,v.z)).ToArray():null,true);
                    building.RestorePhase((BuildPhase)state.phase);
                }
                _buildings[state.id]=building;_ids[building.GetInstanceID()]=state.id;used.Add(building.GetInstanceID());
            }
            ReplicaFreeze.FreezeBuilding(building);
            var targetBPose=new PoseSample(bx,state.y,bz,byaw);
            if(_buildingTracks.UpdateTarget(state.id,ReplicaFreeze.CurrentPose(building.transform),targetBPose,625f))
                ReplicaFreeze.ApplyPose(building.transform,targetBPose);
            if(building.CustomName!=state.name)building.Name=state.name;
            if((int)building.BuildPhase!=state.phase)building.RestorePhase((BuildPhase)state.phase);
            if(building.IsActive!=state.active){if(state.active)building.Activate();else building.Deactivate();}
            building.AssignmentLimit=state.assignmentLimit;
            ApplyInventory(building.Inventory,state.inventory);
            Economy.Apply(building,state.producer);
            ReplicaFreeze.SyncProducerVisualActivity(building,state.producer);
            DecorationReplication.ApplyDecorations(building,state.decorations,ApplyInventory);
            SpecialistReplication.ApplyBuilding(building,state,_items);
            BirdReplication.ApplyBirdHouse(building,state.birdHouse);
            AccessTools.PropertySetter(typeof(Buildable),nameof(Buildable.Health)).Invoke(building,new object[]{state.health});
            if(building.SpawnedVisual!=null)building.SpawnedVisual.SetProgress(state.health);
            BuildingDurability.ApplyBuildingClient(building,state,frame.playTime);
            MoveConstructionCommands.ApplyStoredState(building,state.stored);
            if(_knownBuildingPhases.TryGetValue(state.id,out var prevPhase)&&prevPhase!=(int)BuildPhase.Finished&&state.phase==(int)BuildPhase.Finished&&_applied>0&&ReferenceEquals(building.Community,Community.PlayerCommunity))
            {
                FleetPhysicalActors.PlayFleetSound("cargo_deliver",building.transform.position,0.78f);
                FleetPhysicalActors.SpawnNativePopup(building.transform.position+Vector3.up*2.4f,$"🔨 {building.Name} 建造完工");
            }
            _knownBuildingPhases[state.id]=state.phase;
        }
        foreach(var pair in _buildings.ToArray())if(!alive.Contains(pair.Key)){if(pair.Value!=null){_ids.Remove(pair.Value.GetInstanceID());MoveConstructionCommands.ForgetStored(pair.Value);pair.Value.Remove();}_buildings.Remove(pair.Key);_buildingTracks.Remove(pair.Key);_knownBuildingPhases.Remove(pair.Key);}
        foreach(var state in frame.buildings)
        {
            if(state.stored)continue;
            if(state.walkway!=null&&_buildings[state.id].TryGetComponent<WalkwaySegment>(out var segment))
            {
                // Tradeoff: a replica segment is created through RestorePhase(Finished), so the
                // extendable finishes with restored:true and AddHierarchicalMarkersToConstructionGraph
                // is skipped. The client never runs simulation/pathfinding, so the road is visual and
                // structural only; enabling client simulation would require adding that call here.
                var s=state.walkway;
                // A walkway whose geometry or endpoints are not resolvable yet is left for the next
                // authoritative frame instead of failing the whole replica: the host re-sends the
                // same state, and one bad segment must not force a full resync.
                var valid=Finite(s.length)&&s.length>=1&&s.length<=1000&&Finite(s.x1)&&Finite(s.z1)&&Finite(s.x2)&&Finite(s.z2);
                var startVector=valid?ClientOceanAnchor.HostToClientPos(new Vector3(s.x1,0,s.z1)):Vector3.zero;
                var endVector=valid?ClientOceanAnchor.HostToClientPos(new Vector3(s.x2,0,s.z2)):Vector3.zero;
                if(!valid)Plugin.Log("Skipped walkway with invalid geometry");
                else if(!_buildings.TryGetValue(s.start,out var start)||start==null||!_buildings.TryGetValue(s.end,out var end)||end==null||!start.TryGetComponent<Hookable>(out var startHook)||!end.TryGetComponent<Hookable>(out var endHook))Plugin.Log("Skipped walkway with unresolved endpoints");
                else if(AccessTools.Field(typeof(WalkwaySegment),"_startTransform").GetValue(segment)==null){segment.InitializeSegment(startVector,endVector,startHook,endHook,s.length);segment.UpdateScale();}
                else
                {
                    // Re-align on every authoritative frame instead of only on first creation.
                    // The host replaces a ponton's Buildable when it is upgraded, so a segment's
                    // endpoint identity and scaled length both change during a normal session.
                    var currentStart=(Hookable)AccessTools.Field(typeof(WalkwaySegment),"_startHookable").GetValue(segment);
                    var currentEnd=(Hookable)AccessTools.Field(typeof(WalkwaySegment),"_endHookable").GetValue(segment);
                    var changed=false;
                    if(currentStart!=startHook){segment.SetStartHookable(startHook,startVector-segment.StartPosition);AccessTools.PropertySetter(typeof(WalkwaySegment),nameof(WalkwaySegment.StartPosition)).Invoke(segment,new object[]{startVector});changed=true;}
                    if(currentEnd!=endHook){segment.SetEndHookable(endHook,endVector-segment.EndPosition);AccessTools.PropertySetter(typeof(WalkwaySegment),nameof(WalkwaySegment.EndPosition)).Invoke(segment,new object[]{endVector});changed=true;}
                    if(!Mathf.Approximately((float)AccessTools.Field(typeof(WalkwaySegment),"_scaledLength").GetValue(segment),s.length)){AccessTools.Field(typeof(WalkwaySegment),"_scaledLength").SetValue(segment,s.length);changed=true;}
                    if(changed)segment.UpdateScale();
                }
            }
            if(state.mooredTo>0&&_buildings.TryGetValue(state.id,out var boatBuilding)&&boatBuilding!=null&&boatBuilding.TryGetComponent<Boat>(out var repBoat)&&_buildings.TryGetValue(state.mooredTo,out var dockBuilding)&&dockBuilding!=null&&dockBuilding.TryGetComponent<MooringPoint>(out var repDock))
            {
                if(repDock.LinkedBoat!=repBoat)
                {
                    try{repDock.LinkBoat(repBoat);repDock.MoorBoat(repBoat,true);}
                    catch(Exception e){Plugin.Log("Replica moor sync skipped: "+e.GetType().Name);}
                }
            }
            if(_buildings.TryGetValue(state.id,out var eSrc)&&eSrc!=null&&eSrc.TryGetComponent<EnergyGridBuildableComponent>(out var srcComp))
            {
                var wantedLinks=new HashSet<long>(state.energyLinks??Array.Empty<long>());
                if(srcComp.Connections!=null)
                {
                    foreach(var conn in srcComp.Connections.ToArray())
                    {
                        if(conn is EnergyGridBuildableComponent existingDst&&existingDst.Buildable!=null&&_ids.TryGetValue(existingDst.Buildable.GetInstanceID(),out var existingId)&&!wantedLinks.Contains(existingId))
                        {
                            try{EnergyGrid.Disconnect(srcComp,existingDst);}
                            catch(Exception e){Plugin.Log("Replica energy disconnect skipped: "+e.GetType().Name);}
                        }
                    }
                }
                foreach(var otherId in wantedLinks)
                {
                    if(_buildings.TryGetValue(otherId,out var eDst)&&eDst!=null&&eDst.TryGetComponent<EnergyGridBuildableComponent>(out var dstComp)&&!srcComp.IsConnected(dstComp))
                    {
                        try{EnergyGrid.Connect(srcComp,dstComp);}
                        catch(Exception e){Plugin.Log("Replica energy connect skipped: "+e.GetType().Name);}
                    }
                }
            }
            if(!_buildings[state.id].TryGetComponent<Construction>(out var construction))continue;
            if(state.neighbours.Length>128)throw new InvalidOperationException("Invalid construction connections");
            var desired=new HashSet<Construction>();
            foreach(var id in state.neighbours)if(_buildings.TryGetValue(id,out var neighbour)&&neighbour.TryGetComponent<Construction>(out var next))desired.Add(next);
            foreach(var old in construction.NeighbourConstructions.ToArray())if(!desired.Contains(old))construction.RemoveNeighbour(old);
            foreach(var next in desired)if(!construction.NeighbourConstructions.Contains(next))construction.AddNeighbourConstruction(next);
        }
        DecorationReplication.ApplyPoleLinks(_buildings,_ids,frame.buildings);
        foreach(var building in _buildings.Values)ReplicaGeometry.RefreshReplicaPose(building);
        var assigned=new HashSet<int>(_agents.Values.Where(a=>a!=null).Select(a=>a.GetInstanceID()));
        var aliveAgents=new HashSet<long>();
        foreach(var state in frame.agents)
        {
            if(state.id<=0||(state.owner?.Length??0)>64||!aliveAgents.Add(state.id)||!Finite(state.x)||!Finite(state.y)||!Finite(state.z)||!Finite(state.rotation)||!Enum.IsDefined(typeof(Activity),state.activity))throw new InvalidOperationException("Invalid agent state");
            ClientOceanAnchor.HostToClientPose(state.x,state.z,state.rotation,out var ax,out var az,out var ayaw);
            var agentOwner=Owned(state.owner);
            if(!_agents.TryGetValue(state.id,out var agent)||agent==null)
            {
                agent=agentOwner.Agents.FirstOrDefault(a=>a!=null&&!assigned.Contains(a.GetInstanceID())&&a.Descriptor.UniqueID==state.descriptorId);
                if(agent==null)agent=AgentReplication.Spawn(state,agentOwner);
                _agents[state.id]=agent;_ids[agent.GetInstanceID()]=state.id;assigned.Add(agent.GetInstanceID());agent.SetName(state.name);
            }
            ReplicaFreeze.FreezeAgent(agent);
            var targetAPose=new PoseSample(ax,state.y,az,ayaw);
            if(_agentTracks.UpdateTarget(state.id,ReplicaFreeze.CurrentPose(agent.transform),targetAPose,400f))
                ReplicaFreeze.ApplyPose(agent.transform,targetAPose);
            var boardedBoat=state.boatId>0&&_buildings.TryGetValue(state.boatId,out var boatBuildable)&&boatBuildable!=null&&boatBuildable.TryGetComponent<Boat>(out var boatComp)?boatComp:null;
            ReplicaFreeze.SyncAgentVisualState(agent,(Activity)state.activity,state.terrain,boardedBoat);
            AgentReplication.Apply(agent,state);ApplyInventory(agent.Inventory,state.inventory);
            if(_applied>0&&ReferenceEquals(agentOwner,Community.PlayerCommunity)&&(state.hunger>0.88f||state.thirst>0.88f||!string.IsNullOrEmpty(state.disease))&&(!_agentAlertCooldowns.TryGetValue(state.id,out var alertCd)||Time.realtimeSinceStartup>=alertCd))
            {
                _agentAlertCooldowns[state.id]=Time.realtimeSinceStartup+25f;
                Plugin.Instance?.Network?.ShowNotice(!string.IsNullOrEmpty(state.disease)?$"⚠ 船员 {state.name} 感染疾病，请安排诊所治疗！":$"⚠ 船员 {state.name} 极度饥渴，请补充食物/淡水！",true);
            }
        }
        foreach(var pair in _agents.ToArray())if(!aliveAgents.Contains(pair.Key)){if(pair.Value!=null){var owner=pair.Value.Community??community;owner.RemoveAgent(pair.Value);UnityEngine.Object.Destroy(pair.Value.gameObject);}_agents.Remove(pair.Key);_agentTracks.Remove(pair.Key);_agentAlertCooldowns.Remove(pair.Key);}
        if(frame.rules!=null)
        {
            var wasFriendlyFire=Plugin.Options.rules.friendlyFire;
            var firstSync=_applied<=0;
            Plugin.Options.rules=frame.rules;
            if(!firstSync&&wasFriendlyFire!=frame.rules.friendlyFire&&Plugin.Options.mode=="independent")
                Plugin.Instance?.Network?.ShowNotice(frame.rules.friendlyFire?"房主已开启炮战，请进入本船炮位手动瞄准开火":"房主已关闭海战火炮规则",false);
        }
        FleetPhysicalActors.ApplyRemoteActions(frame.actions);
        FleetCombat.Apply(frame);
        FleetVoyages.Apply(frame.voyages);
        if(frame.ships!=null&&frame.ships.Length>0)
        {
            foreach(var shipState in frame.ships)
            {
                if(shipState==null)continue;
                var shipCommunity=ResolveShipCommunity(shipState.name,shipState.peerId);
                if(shipCommunity==null)continue;
                FleetEngineBundle.SyncEnergy(shipCommunity,shipState.energy);
                if(shipCommunity.Research==null)continue;
                shipCommunity.Research.ResearchPoints=shipState.researchPoints;
                foreach(var key in shipState.researched)
                    if(_properties.TryGetValue(key,out var prop)&&!shipCommunity.Research.ResearchedBuildableProperties.Contains(prop))
                        shipCommunity.Research.UnlockBuildable(prop);
                var unlocked=shipState.unlockedTech!=null&&shipState.unlockedTech.Length>0?shipState.unlockedTech:frame.unlockedTech;
                Economy.ApplyResearch(shipState.research,unlocked,shipCommunity);
            }
        }
        else
        {
            community.Research.ResearchPoints=frame.researchPoints;
            var local=Community.PlayerCommunity;
            if(!ReferenceEquals(local,community))local.Research.ResearchPoints=frame.researchPoints;
            foreach(var key in frame.researched)if(_properties.TryGetValue(key,out var p))
            {
                if(!community.Research.ResearchedBuildableProperties.Contains(p))community.Research.UnlockBuildable(p);
                if(!ReferenceEquals(local,community)&&!local.Research.ResearchedBuildableProperties.Contains(p))local.Research.UnlockBuildable(p);
            }
            Economy.ApplyResearch(frame.research,frame.unlockedTech,community);
        }
        Salvage.Apply(frame,(obj,id)=>_ids[obj.GetInstanceID()]=id);
        Birds.Apply(frame,_buildings,Owned,(obj,id)=>_ids[obj.GetInstanceID()]=id,obj=>_ids.Remove(obj.GetInstanceID()),_items);
        NavigationReplication.Apply(frame);
        LandmarkReplication.Apply(frame);
        ProgressionReplication.Apply(frame);
        Community.PlayerCommunity?.Inventory?.LateUpdate();
        _applied=frame.sequence;
    }
    public void Interpolate(float dt)
    {
        if(dt<=0f||dt>0.25f)return;
        var alpha=_spanClock.Advance(dt);
        var waveTime=Time.unscaledTime;
        var movedBuildings=false;
        for(var boatPass=0;boatPass<2;boatPass++)
        {
            foreach(var pair in _buildings)
            {
                var b=pair.Value;
                if(b==null)continue;
                if(boatPass==1)ReplicaFreeze.ApplyBuildingWaveBob(b,waveTime);
                if((b.TryGetComponent<Boat>(out _)==(boatPass==0))||!_buildingTracks.Sample(pair.Key,alpha,out var pose))continue;
                var cur=ReplicaFreeze.CurrentPose(b.transform);
                if(cur.DistanceSq(pose)>0.000001f||cur.AngleDeltaAbs(pose)>0.05f)
                {
                    ReplicaFreeze.ApplyPose(b.transform,pose);
                    movedBuildings=true;
                }
            }
        }
        if(movedBuildings)foreach(var b in _buildings.Values)if(b!=null)ReplicaGeometry.RefreshReplicaPose(b);
        foreach(var pair in _agents)
        {
            var a=pair.Value;
            if(a==null||!_agentTracks.Sample(pair.Key,alpha,out var pose))continue;
            var cur=ReplicaFreeze.CurrentPose(a.transform);
            var distSq=cur.DistanceSq(pose);
            if(distSq>0.000001f||cur.AngleDeltaAbs(pose)>0.05f)
                ReplicaFreeze.ApplyPose(a.transform,pose);
            ReplicaFreeze.DriveClientAgentMotion(a,Mathf.Sqrt(distSq),dt);
        }
        Salvage.Interpolate(alpha);
        Birds.Interpolate(alpha);
        NavigationReplication.Interpolate(alpha);
    }
    private void ApplyInventory(Inventory inventory,ItemCount[] incoming)
    {
        if(incoming.Length>2048)throw new InvalidOperationException("Inventory state too large");
        var desired=new Dictionary<SubInventoryType,Dictionary<string,int>>();
        foreach(var state in incoming)
        {
            if(state.count<0||state.count>10000||!Enum.IsDefined(typeof(SubInventoryType),state.section)||!_items.ContainsKey(state.properties))throw new InvalidOperationException("Invalid inventory state");
            var section=(SubInventoryType)state.section;if(!desired.TryGetValue(section,out var counts)){counts=new Dictionary<string,int>();desired.Add(section,counts);}counts.Add(state.properties,state.count);
        }
        foreach(var section in inventory.SubInventoryTypes.ToArray())
        {
            desired.TryGetValue(section,out var wanted);
            var existingItems=inventory.ReturnAllItems(section,new List<Item>());
            if((wanted==null||wanted.Count==0)&&existingItems.Count==0)continue;
            var current=existingItems.GroupBy(i=>i.Properties.name).ToDictionary(g=>g.Key,g=>g.Count());
            if(wanted!=null&&wanted.Count==current.Count&&wanted.All(kv=>current.TryGetValue(kv.Key,out var cn)&&cn==kv.Value))continue;
            foreach(var item in existingItems)if(item.IsReserved)item.CancelReservation();
            foreach(var pair in current){var target=wanted!=null&&wanted.TryGetValue(pair.Key,out var n)?n:0;for(var i=target;i<pair.Value;i++)inventory.RemoveItem(_items[pair.Key],section);}
            if(wanted!=null)foreach(var pair in wanted){current.TryGetValue(pair.Key,out var n);for(var i=n;i<pair.Value;i++)if(!inventory.AddItem(new Item(_items[pair.Key]),section))throw new InvalidOperationException("Replica inventory capacity mismatch");}
        }
    }
    private static ItemCount[] CaptureInventory(Inventory inventory)
    {
        var result=new List<ItemCount>();
        foreach(var section in inventory.SubInventoryTypes.ToArray())foreach(var group in inventory.ReturnAllItems(section,new List<Item>()).GroupBy(i=>i.Properties.name))result.Add(new ItemCount{properties=group.Key,section=(int)section,count=group.Count()});
        return result.ToArray();
    }
    internal static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
    internal static string ShipKey(Community? community)=>
        community==null?"":(!string.IsNullOrEmpty(community.Name)?community.Name:"host");
    internal static Community? ResolveCommunity(string? name)
    {
        if(string.IsNullOrEmpty(name))return null;
        if(name=="host")return ResolveShipCommunity("host","host");
        foreach(var community in Community.Communities)
            if(community!=null&&community.Name==name)return community;
        return null;
    }
    internal static Community? ResolveShipCommunity(string? nameOrKey,string? peerId=null)
    {
        if(!string.IsNullOrEmpty(nameOrKey)&&nameOrKey!="host")
        {
            foreach(var c in Community.Communities)
                if(c!=null&&c.Name==nameOrKey)return c;
            return null;
        }
        var byHostId=FleetEngineBundle.FindShipById("host");
        if(byHostId!=null)return byHostId;
        foreach(var c in Community.Communities)
        {
            if(c==null||(!string.IsNullOrEmpty(c.Name)&&c.Name.StartsWith("Ship:",StringComparison.Ordinal)))continue;
            if(c.Buildables.Any(b=>b!=null&&(b.TryGetComponent<TownheartStorage>(out _)||b.TryGetComponent<Engine>(out _))))
                return c;
        }
        return peerId=="host"||string.IsNullOrEmpty(nameOrKey)?Community.PlayerCommunity:null;
    }
    public string? Execute(GameCommand command,Community? actorCommunity=null)
    {
        if(!GameSession.Ready())return "房主尚未载入游戏";
        if(!Finite(command.x)||!Finite(command.z)||!Finite(command.rotation))return "坐标无效";
        Catalog();
        var targetCommunity=actorCommunity??Community.PlayerCommunity;
        if(command.target>0 && _buildings.TryGetValue(command.target,out var ownedTarget) && ownedTarget!=null && ownedTarget.Community!=targetCommunity
            && !FleetInteractions.Handles(command.kind)) return "不能操作其他船主的建筑";

        var heartPos=FleetEngineBundle.RootFor(targetCommunity)?.transform.position??Construction.TownheartPosition;
        var targetRoot=FleetEngineBundle.RootFor(targetCommunity)??Construction.Townheart;
        if(command.kind==ConstructionCommands.PlaceRoad)return ConstructionCommands.Execute(command,_properties,_buildings,targetCommunity);
        if(MoveConstructionCommands.Handles(command.kind))return MoveConstructionCommands.Execute(command,_buildings,targetCommunity);
        if(DecorationReplication.Handles(command.kind))return DecorationReplication.Execute(command,_buildings,targetCommunity);
        if(SpecialistReplication.HandlesBuilding(command.kind))return SpecialistReplication.ExecuteBuilding(command,_buildings,_items,targetCommunity);
        if(SpecialistReplication.HandlesAgent(command.kind))return SpecialistReplication.ExecuteAgent(command,_agents,_items,targetCommunity);
        if(BirdReplication.Handles(command.kind))return Birds.Execute(command,_buildings,targetCommunity);
        if(ProgressionReplication.Handles(command.kind))return ProgressionReplication.Execute(command,targetCommunity);
        if(BuildingDurability.Handles(command.kind))return BuildingDurability.ExecuteRepair(command,_buildings,targetCommunity);
        if(FleetVisibility.Handles(command.kind))return FleetVisibility.Execute(command,targetCommunity);
        if(command.kind=="researchStart"||command.kind=="researchCancel")return Economy.ExecuteGlobal(command,targetCommunity);
        if(command.kind.StartsWith("marker",StringComparison.Ordinal))return Salvage.Execute(command,targetCommunity);
        if(LandmarkReplication.Handles(command.kind))return LandmarkReplication.Execute(command,targetCommunity,_items);
        if(FleetInteractions.Handles(command.kind))return FleetInteractions.Execute(command,targetCommunity,heartPos,_buildings,HostId);
        if(command.kind=="speed"||command.kind=="speedZero"||command.kind=="mapOpen"||command.kind=="mapClose"||command.kind=="navigate"||command.kind=="navigatePlan"||command.kind=="navigateKeys"||command.kind=="navigateStop")return NavigationReplication.Execute(command);
        if(command.kind=="agentRename"||command.kind=="agentAssignment")
        {
            if(!_agents.TryGetValue(command.target,out var agent)||agent==null||agent.Community!=targetCommunity)return "居民已不存在或不属于你的船只";
            if(command.kind=="agentRename"){if(string.IsNullOrWhiteSpace(command.text)||command.text.Length>48||command.text.Any(char.IsControl))return "名称无效";agent.SetName(command.text);return null;}
            if(!Enum.IsDefined(typeof(AssignmentType),command.value)||!Enum.IsDefined(typeof(AssignmentPriority),command.amount)||!agent.Assignments.Any(a=>(int)a.Type==command.value))return "工作分配无效";
            agent.SetAssignmentEnabled((AssignmentType)command.value,command.visual!=0,(AssignmentPriority)command.amount);return null;
        }
        if(command.kind=="placeBoat")
        {
            using var _scope=FleetRuntime.WithPlayerCommunity(targetCommunity);
            if(!_properties.TryGetValue(command.properties,out var bp)||bp.Prefab==null||!bp.Prefab.TryGetComponent<Boat>(out _))return "船只类型不存在";
            if(!bp.IsUnlocked())return "该小船尚未研究解锁";
            if(!_buildings.TryGetValue(command.target,out var dockBuilding)||dockBuilding==null||dockBuilding.Community!=targetCommunity||!dockBuilding.TryGetComponent<MooringPoint>(out var dock))return "请选择我方船上的可用系泊点";
            if(!dock.IsAvailableForMooring||dock.LinkedBoat!=null)return "该系泊点已有船只停靠";
            if(!bp.ReturnCanBePlaced(targetCommunity,true))return "建造小船所需材料不足";
            var boatPos=dock.ReturnBoatPosition();
            var spawnedBoat=UnityEngine.Object.Instantiate(bp.Prefab,boatPos,dockBuilding.transform.rotation);
            spawnedBoat.Initialize(targetCommunity,Mathf.Clamp(command.visual,0,Math.Max(0,(bp.Visuals?.Length??1)-1)));
            var boatComp=spawnedBoat.GetComponent<Boat>();
            dock.LinkBoat(boatComp);
            dock.MoorBoat(boatComp,false);
            spawnedBoat.StartBuilding();
            return null;
        }
        if(command.kind=="energyConnect"||command.kind=="energyDisconnect")
            return DecorationReplication.ExecuteEnergyCommand(command,_buildings,targetCommunity);
        if(command.kind=="place")
        {
            using var _scope=FleetRuntime.WithPlayerCommunity(targetCommunity);
            if(!_properties.TryGetValue(command.properties,out var p)||p.Prefab==null)return "建筑类型不存在";
            if(!GameSettings.Instance.BuildableSettings.Buildables.Contains(p)||p.Prefab.GetComponent<TownheartStorage>()!=null)return "此建筑不能直接建造";
            if(!p.IsUnlocked())return "建筑尚未解锁";
            if(p.PlacementCursorProperties is LineConstructionCursorProperties)return "道路必须使用双端点原生道路建造指令";
            if(p.RequiresMooringPoint||p.Outline==null||p.Outline.Length<3||!(p.PlacementCursorProperties is SingleConstructionCursorProperties))return "此建筑的联机放置适配尚未完成";
            if(command.visual<0||p.Visuals==null||command.visual>=p.Visuals.Length)return "建筑外观无效";
            if(!p.ReturnCanBePlaced(targetCommunity,true))return "资源或建筑条件不满足";
            var position=new Vector3(command.x,0,command.z);
            if((position-heartPos).sqrMagnitude>GameSettings.Instance.GameplaySettings.ConstructionRadius*GameSettings.Instance.GameplaySettings.ConstructionRadius)return "超出建造范围";
            var rotation=Quaternion.Euler(0,command.rotation,0);var vertices=new List<Vector2>();
            foreach(var point in p.Outline){var v=position+rotation*new Vector3(point.x,0,point.y);vertices.Add(new Vector2(v.x,v.z));}
            var polygon=new Polygon(vertices);
            foreach(var blocker in Buildable.BlockingPolygons){blocker.FastUpdate();if(polygon.ReturnArePolygonsOverlapping(blocker,true))return "该位置与现有建筑重叠";}
            foreach(var blocker in Buildable.FreeformBlockerPolygons){blocker.FastUpdate();if(polygon.ReturnArePolygonsOverlapping(blocker,true))return "该位置不能建造";}
            var hookDistance=Vector2.Distance(Vector2.zero,(p.Outline[0]+p.Outline[p.Outline.Length-1])*.5f);
            var hookPosition=position+rotation*Vector3.forward*hookDistance;
            if(!Hookable.TryHook(out var hook,out var hookable,.15f,hookPosition,null)||hookable==null||hookable.Buildable.Community!=targetCommunity||Vector3.Angle(hook.Forward,rotation*Vector3.forward)>1)return "建筑必须连接到你自己的船只";
            var previewSettings=(VisualPrefabPreviewSettings)AccessTools.Field(typeof(BuildableCursorProperties),"_previewSettings").GetValue(p.PlacementCursorProperties);
            var preview=new ConstructionPreview(p.Prefab,previewSettings,command.visual,true,true);
            try{preview.Transform.SetPositionAndRotation(position,rotation);preview.Hook=hook;preview.Hookable=hookable;preview.UpdateCanBePlaced(true,true);if(!preview.CanPlace)return "建筑位置或通行连接无效";}finally{preview.Destroy();}
            var placed=UnityEngine.Object.Instantiate(p.Prefab,position,rotation);placed.Initialize(targetCommunity,command.visual);
            if(placed.TryGetComponent<Construction>(out var construction)&&hookable.TryGetComponent<Construction>(out var neighbour)&&construction.AddNeighbourConstruction(neighbour))neighbour.AddNeighbourConstruction(construction);
            placed.StartBuilding();
            return null;
        }
        if(!_buildings.TryGetValue(command.target,out var building)||building==null)return "目标已不存在";
        if(building.Community!=targetCommunity)
        {
            return "他船经营权限属于船主，请通过运输、登船或炮击进行跨船互动";
        }
        if(MoveConstructionCommands.IsStored(building))return "建筑已收纳，请先从库存放置";
        if(command.kind.StartsWith("recipe",StringComparison.Ordinal)||command.kind=="queueCancel"||command.kind=="producerPriority")return Economy.Execute(building,command);
        switch(command.kind)
        {
            case "activate":building.Activate();return null;
            case "deactivate":building.Deactivate();return null;
            case "upgrade":if(!building.CanUpgrade())return "升级条件不满足";building.Upgrade();return null;
            case "cancelupgrade":building.CancelUpgrade();return null;
            case "salvage":if(targetRoot!=null&&targetRoot.Buildable==building)return "不能拆除城镇核心";building.Salvage();return null;
            case "rename":if(command.text.Length>48||command.text.Any(char.IsControl))return "名称无效";building.Name=command.text;return null;
            case "assignmentLimit":if(command.value<1||command.value>20)return "人数无效";building.AssignmentLimit=command.value;return null;
            default:return "尚未适配的游戏指令";
        }
    }
    internal static bool IsTradableSection(SubInventoryType section)=>
        section==SubInventoryType.Storage||section==SubInventoryType.Liquid||section==SubInventoryType.Export;
    internal static string CleanItemName(ItemProperties? prop)
    {
        if(prop==null)return "物资";
        var loc=prop.LocalizedName;
        if(!string.IsNullOrWhiteSpace(loc))
        {
            var line=loc.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if(!string.IsNullOrEmpty(line))return line!;
        }
        return string.IsNullOrWhiteSpace(prop.name)?"物资":prop.name;
    }
    internal static string FormatCategoryLabel(string? selector)
    {
        if(string.IsNullOrEmpty(selector))return "物资";
        var resolved=Resources.FindObjectsOfTypeAll<ItemProperties>().FirstOrDefault(x=>x!=null&&x.name==selector);
        if(resolved!=null)return CleanItemName(resolved);
        return selector switch
        {
            "Wood"=>"木材",
            "Plastic"=>"塑料",
            "Metal"=>"金属",
            "Food"=>"食物",
            "Drink"=>"淡水",
            "Tech"=>"电子/科技",
            _=>selector!
        };
    }
    internal static List<(ItemProperties Prop,int Count)> CollectShipStock(Community? from,Buildable? prioritizeBuilding=null)
    {
        var counts=new Dictionary<string,(ItemProperties Prop,int Count)>(StringComparer.Ordinal);
        if(from==null)return new List<(ItemProperties,int)>();
        var ordered=prioritizeBuilding!=null&&prioritizeBuilding.Community==from
            ?new[]{prioritizeBuilding}.Concat(from.Buildables.Where(b=>b!=prioritizeBuilding))
            :from.Buildables;
        foreach(var building in ordered)
        {
            if(building==null||MoveConstructionCommands.IsStored(building)||building.Inventory==null)continue;
            foreach(var section in building.Inventory.SubInventoryTypes)
            {
                if(!IsTradableSection(section))continue;
                foreach(var item in building.Inventory.ReturnAllItems(section,new List<Item>()))
                {
                    var p=item?.Properties;
                    if(item==null||p==null||item.IsReserved||p.IsQuestItem)continue;
                    if(counts.TryGetValue(p.name,out var cur))counts[p.name]=(p,cur.Count+1);
                    else counts[p.name]=(p,1);
                }
            }
        }
        return counts.Values
            .OrderByDescending(x=>x.Count)
            .ThenBy(x=>x.Prop.name,StringComparer.Ordinal)
            .ToList();
    }
}
