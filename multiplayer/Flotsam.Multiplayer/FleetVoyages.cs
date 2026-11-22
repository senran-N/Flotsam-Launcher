using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

[DataContract]
public sealed class FleetCargoDrop
{
    [DataMember] public float x,z;
    [DataMember] public ItemCount[] cargo=Array.Empty<ItemCount>();
}

internal static class FleetVoyages
{
    internal sealed class Journey
    {
        public FleetVoyageState State=new FleetVoyageState();
        public Agent Worker=null!;
        public Buildable? Boat;
        public int Phase;
        public float Work,Elapsed,Blocked;
        public bool NavigatorEnabled;
        public bool BoatNavigatorEnabled,BoatCurrent;
        public Rigidbody? WorkerBody,BoatBody;
        public bool WorkerKinematic,WorkerGravity,BoatKinematic,BoatGravity;
        public long TargetBuilding;
        public int Capacity=4;
        public bool Boarded;
        public GameObject? CargoVisual;
        public Vector3 Position;
        public readonly List<ItemProperties> Cargo=new List<ItemProperties>();
    }
    internal static readonly List<Journey> Active=new List<Journey>();
    private static FleetVoyageState[] _remote=Array.Empty<FleetVoyageState>();
    private static readonly Dictionary<long,GameObject> RemoteCargo=new Dictionary<long,GameObject>();
    private static long _next;
    private static readonly List<FleetCargoDrop> PendingDrops=new List<FleetCargoDrop>();
    private static float _dropAt;
    internal static FleetCargoDrop[] SaveDrops()=>PendingDrops.Select(d=>new FleetCargoDrop{x=d.x,z=d.z,cargo=d.cargo}).ToArray();
    internal static void RestoreDrops(FleetCargoDrop[] drops){if(drops!=null)PendingDrops.AddRange(drops);}
    internal static bool IsBusy(Agent a)=>Active.Any(j=>j.Worker==a);
    internal static bool IsBusy(Buildable b)=>Active.Any(j=>j.Boat==b);
    internal static void Install(Harmony h)
    {
        h.Patch(AccessTools.DeclaredMethod(typeof(Agent),"LateUpdate"),prefix:new HarmonyMethod(typeof(FleetVoyages),nameof(WorkerUpdate)));
        h.Patch(AccessTools.DeclaredMethod(typeof(Navigator),"Update"),prefix:new HarmonyMethod(typeof(FleetVoyages),nameof(NavigatorUpdate)));
    }
    private static bool WorkerUpdate(Agent __instance)=>!IsBusy(__instance)&&!FleetCombat.IsBusy(__instance)&&!BuildingDurability.IsRepairWorker(__instance);
    private static bool NavigatorUpdate(Navigator __instance){var a=__instance.GetComponent<Agent>();return a==null||!IsBusy(a);}
    internal static string? CanLaunch(Community owner,Community target,int amount,out Agent? worker,out Buildable? boat)
    {
        worker=owner.Agents.FirstOrDefault(a=>a!=null&&a.IsAlive&&a.CurrentActivity!=Activity.Sleeping&&!FleetInteractions.IsAgentBusy(a)&&(a.Assignment==null||a.Assignment.IsIdleProject));
        boat=owner.Buildables.FirstOrDefault(b=>b!=null&&b.TryGetComponent<Boat>(out var v)&&v.Captain==null&&v.CurrentMooringPoint!=null&&v.CurrentMooringPoint.IsInTown&&!v.CurrentMooringPoint.IsReserved&&v.Buildable.Inventory.ReturnCount()==0&&b.BuildPhase==BuildPhase.Finished&&!MoveConstructionCommands.IsStored(b)&&!FleetInteractions.IsBoatBusy(b));
        if(worker==null)return "需要一名空闲、清醒的本船居民";
        var distance=Vector3.Distance(Heart(owner),Heart(target));
        if(distance>(boat==null?85:250))return boat==null?"泅渡最远85米，请靠近或准备小艇":"小艇航程最远250米";
        if(amount>(boat==null?4:25))return boat==null?"泅渡最多携带4件货物":"小艇最多携带25件货物，请分批交易";
        return null;
    }
    internal static string? Launch(Community owner,Community target,string item,int amount,bool raid,long targetBuilding)
    {
        if(amount>25)return "单次运输最多25件货物";
        amount=amount<=0?4:amount;
        var error=CanLaunch(owner,target,amount,out var worker,out var boat);if(error!=null)return error;
        var cargo=raid?new List<ItemProperties>():FleetInteractions.ExtractItemsFromShip(owner,item,amount);
        if(!raid&&cargo.Count!=amount){ReturnCargo(owner,cargo);return "可用物资不足";}
        if(raid)FleetVisibility.CheckAndBreakAllianceOnHostileAction(owner,target);
        var journey=Start(owner,target,worker!,boat,cargo,raid,targetBuilding);journey.Capacity=amount;
        return null;
    }
    internal static Journey Start(Community owner,Community target,Agent worker,Buildable? boat,IEnumerable<ItemProperties> cargo,bool raid,long targetBuilding)
    {
        if(worker.Assignment!=null)worker.Assignment.Stop(ProjectFlags.Cancelled);
        var nav=worker.ReturnNavigator(true);var enabled=nav!=null&&nav.enabled;nav?.StopNavigation(ProjectFlags.Cancelled);
        var j=new Journey{Worker=worker,Boat=boat,NavigatorEnabled=enabled,TargetBuilding=targetBuilding,Capacity=boat!=null?25:4,Position=worker.transform.position,State=new FleetVoyageState{id=++_next,fromShip=Replication.ShipKey(owner),toShip=Replication.ShipKey(target),kind=raid?"raid":"trade",status="出航",workerName=worker.Name,maxHp=boat!=null?180:100,hp=boat!=null?180:100}};
        if(boat!=null&&boat.TryGetComponent<Boat>(out var vessel))
        {
            j.BoatNavigatorEnabled=vessel.Navigator.enabled;j.BoatCurrent=boat.PhysicsController.ShouldApplyCurrent;
            if(vessel.Captain==null)vessel.BoardCaptain(worker);
            vessel.CurrentMooringPoint?.UnmoorBoat(true);if(vessel.TownMooringPoint!=null&&!vessel.TownMooringPoint.IsReserved)vessel.TownMooringPoint.Reserve(worker);
            vessel.Navigator.enabled=false;boat.PhysicsController.ShouldApplyCurrent=false;j.Position=boat.transform.position;
            j.BoatBody=boat.GetComponent<Rigidbody>();if(j.BoatBody!=null){j.BoatKinematic=j.BoatBody.isKinematic;j.BoatGravity=j.BoatBody.useGravity;j.BoatBody.isKinematic=true;j.BoatBody.useGravity=false;}
        }
        else if(nav!=null){nav.UpdateTerrain(Navigator.TerrainType.WaterSurface);worker.transform.SetParent(GameManager.WorldManager.WorldParent);j.Position.y=0;}
        if(nav!=null)nav.enabled=false;
        j.WorkerBody=worker.GetComponent<Rigidbody>();if(j.WorkerBody!=null){j.WorkerKinematic=j.WorkerBody.isKinematic;j.WorkerGravity=j.WorkerBody.useGravity;j.WorkerBody.isKinematic=true;j.WorkerBody.useGravity=false;}
        j.State.worker=Plugin.Instance!.Network!.Id(worker);j.State.boat=boat!=null?Plugin.Instance.Network.Id(boat):0;
        j.Cargo.AddRange(cargo);RefreshCargo(j);j.State.x=j.Position.x;j.State.z=j.Position.z;Active.Add(j);
        j.CargoVisual=FleetPhysicalActors.AttachCargoCrates(boat!=null?boat.transform:worker.transform,j.Cargo.Count>0);
        FleetPhysicalActors.SpawnNativePopup(j.Position+Vector3.up*3,worker.Name+(raid?" 带队出发掠夺":" 出发运货"));
        return j;
    }
    internal static string? Recall(long id,Community owner)
    {
        var j=Active.FirstOrDefault(v=>v.State.id==id&&v.State.fromShip==Replication.ShipKey(owner));
        if(j==null)return "只能召回自己的出航小队";
        j.Phase=2;j.State.status="撤退返航";return null;
    }
    private static Vector3 Heart(Community c)=>FleetEngineBundle.HeartBuildable(c)?.transform.position??Construction.TownheartPosition;
    internal static void RefreshCargo(Journey j)=>j.State.cargo=j.Cargo.GroupBy(p=>p.name).Select(g=>new ItemCount{properties=g.Key,count=g.Count()}).ToArray();
    internal static void Tick(float dt)
    {
        if(!GameSession.Ready()||Plugin.Instance?.Network?.IsHost!=true||dt<=0)return;
        if(PendingDrops.Count>0&&Time.realtimeSinceStartup>=_dropAt)
        {
            _dropAt=Time.realtimeSinceStartup+1;
            var catalog=Resources.FindObjectsOfTypeAll<ItemProperties>().Where(p=>p!=null).GroupBy(p=>p.name).ToDictionary(g=>g.Key,g=>g.First());
            PendingDrops.RemoveAll(drop=>drop.cargo.All(c=>catalog.ContainsKey(c.properties))&&TryDrop(new Vector3(drop.x,0,drop.z),drop.cargo.Select(c=>(catalog[c.properties],c.count)).ToArray()));
        }
        foreach(var j in Active.ToArray())
        {
            var owner=Replication.ResolveShipCommunity(j.State.fromShip);var target=Replication.ResolveShipCommunity(j.State.toShip);
            if(owner==null||j.Worker==null||!j.Worker.IsAlive){Drop(j.Position,j.Cargo);Finish(j);continue;}
            j.Elapsed+=dt;
            if(j.Elapsed>180&&j.Phase!=2){j.Phase=2;j.State.status="航程超时，返航";}
            if(target==null)j.Phase=2;
            var storage=target!=null?RaidStorage(target,j):null;
            var destination=j.Phase==2?Heart(owner):storage!=null?storage.transform.position:target!=null?Heart(target):Heart(owner);
            if(j.Phase!=2)
            {
                var outward=Heart(owner)-destination;outward.y=0;
                if(outward.sqrMagnitude>.01f)destination+=outward.normalized*(j.Boat!=null?10:3);
            }
            destination.y=0;
            if(j.Phase!=1)
            {
                Move(j,owner,destination,dt);
                if(Vector3.Distance(j.Position,destination)<5)
                {
                    if(j.Phase==2)
                    {
                        // Retain unaccepted cargo aboard until space exists; no silent loss.
                        j.Cargo.RemoveAll(p=>owner.SpawnItemToAvailableStorage(p));
                        if(j.Cargo.Count==0){Finish(j);continue;}
                        j.State.status="等待本船仓库腾出空间";
                    }
                    else {j.Phase=1;j.State.status=j.State.kind=="raid"?"登船交战":"卸货";}
                }
            }
            else if(target!=null)
            {
                Move(j,owner,destination,dt);
                if(Vector3.Distance(j.Position,destination)>18){j.Phase=0;j.State.status="目标驶离，追赶";}
                else if(j.State.kind=="raid")
                {
                    var defenders=target.Agents.Count(a=>a!=null&&a.IsAlive&&a.CurrentActivity!=Activity.Sleeping&&!IsBusy(a)&&Vector3.Distance(a.transform.position,j.Position)<28);
                    j.State.hp-=defenders*7f*dt;
                    j.Work+=dt;
                    if(j.State.hp<=0){Drop(j.Position,j.Cargo);j.Cargo.Clear();j.Worker.Vitals?.Pollution?.Increase(25);j.Phase=2;j.State.hp=1;j.State.status="突袭被击退，负伤撤退";}
                    else if(j.Work>=2)
                    {
                        j.Work=0;
                        var loot=storage!=null?FleetInteractions.ExtractItemsFromShip(target,"",1,storage,true):new List<ItemProperties>();
                        j.Cargo.AddRange(loot);
                        if(storage!=null&&Plugin.Options.rules.sabotage)BuildingDurability.ApplyDamage(storage,4,false,storage.transform.position);
                        if(j.Cargo.Count>=j.Capacity||loot.Count==0){j.Phase=2;j.State.status="载货返航";}
                    }
                }
                else
                {
                    j.Cargo.RemoveAll(p=>target.SpawnItemToAvailableStorage(p));
                    if(j.Cargo.Count==0){j.Phase=2;j.State.status="交付完成，返航";}
                    else j.State.status="等待收货方仓库空间";
                }
            }
            var direction=destination-j.Position;direction.y=0;
            var aboard=j.Worker.Boat!=null;
            if(j.Phase==1&&j.State.kind=="raid"&&storage!=null)
            {
                if(aboard){j.Worker.Boat!.ClearCaptain();j.Worker.transform.SetParent(GameManager.WorldManager.WorldParent);}
                if(!j.Boarded){j.Worker.ReturnNavigator(true)?.UpdateTerrain(Navigator.TerrainType.Construction);j.Boarded=true;}
                j.Worker.transform.position=storage.transform.position+Vector3.right*1.3f;
                j.Worker.UpdateActivity(Activity.Building);
            }
            else if(j.Boat!=null)
            {
                if(!aboard)j.Boat.GetComponent<Boat>().BoardCaptain(j.Worker);j.Boarded=false;
            }
            else{if(j.Boarded){j.Worker.ReturnNavigator(true)?.UpdateTerrain(Navigator.TerrainType.WaterSurface);j.Worker.transform.SetParent(GameManager.WorldManager.WorldParent);j.Boarded=false;}j.Worker.transform.position=j.Position;if(direction.sqrMagnitude>.01f)j.Worker.transform.rotation=Quaternion.LookRotation(direction);}
            if(j.Boat!=null){j.Boat.transform.position=j.Position;if(direction.sqrMagnitude>.01f)j.Boat.transform.rotation=Quaternion.LookRotation(direction);}
            j.State.x=j.Position.x;j.State.y=j.Position.y;j.State.z=j.Position.z;
            RefreshCargo(j);
            if(j.CargoVisual!=null)j.CargoVisual.SetActive(j.Cargo.Count>0);
        }
    }
    private static void Move(Journey j,Community owner,Vector3 destination,float dt)
    {
        var delta=destination-j.Position;delta.y=0;if(delta.sqrMagnitude<.01f)return;
        var step=Mathf.Min(delta.magnitude,(j.Boat!=null?10f:3.5f)*dt);
        var direction=delta.normalized;
        bool Clear(Vector3 d)
        {
            var hits=Physics.SphereCastAll(j.Position+Vector3.up*.8f,j.Boat!=null?1.2f:.35f,d,step+1,-1,QueryTriggerInteraction.Ignore);
            return !hits.Any(h=>h.collider!=null&&(h.collider.GetComponentInParent<Landmark>()!=null||h.collider is TerrainCollider||(h.collider.GetComponentInParent<Buildable>() is Buildable b&&b!=j.Boat&&b.Community!=owner)));
        }
        if(!Clear(direction))
        {
            var sign=j.State.id%2==0?1:-1;var found=false;
            foreach(var angle in new[]{45f*sign,90f*sign,-45f*sign,-90f*sign})
            {
                var alternative=Quaternion.AngleAxis(angle,Vector3.up)*direction;
                if(!Clear(alternative))continue;direction=alternative;found=true;break;
            }
            if(!found){j.Blocked+=dt;j.State.status="航路受阻，等待通行";if(j.Blocked>15&&j.Phase!=2){j.Phase=2;j.State.status="航路受阻，返航";}return;}
        }
        j.Blocked=0;j.Position+=direction*step;
    }
    private static Buildable? RaidStorage(Community target,Journey j)
    {
        if(j.State.kind!="raid")return null;
        var chosen=Plugin.Instance?.Network?.ResolveBuilding(j.TargetBuilding);
        if(chosen!=null&&chosen.Community==target&&chosen.Inventory!=null&&Vector3.Distance(chosen.transform.position,j.Position)<30)return chosen;
        return target.Buildables.Where(b=>b!=null&&!MoveConstructionCommands.IsStored(b)&&b.Inventory!=null&&b.Inventory.SubInventoryTypes.Any(Replication.IsTradableSection)&&b.Inventory.ReturnCount()>0).OrderBy(b=>Vector3.SqrMagnitude(b.transform.position-j.Position)).FirstOrDefault();
    }
    internal static void Damage(Agent? worker,Buildable? boat,Vector3 point,float damage)
    {
        var j=Active.FirstOrDefault(v=>(worker!=null&&v.Worker==worker)||(boat!=null&&v.Boat==boat));
        if(j==null)return;
        j.State.hp=Mathf.Max(0,j.State.hp-damage);
        if(j.State.hp<=0)
        {
            Drop(point,j.Cargo);j.Cargo.Clear();j.Phase=2;j.State.status="载具受创，紧急撤退";
            j.Worker.Vitals?.Pollution?.Increase(25);j.State.hp=1;
        }
    }
    private static void Finish(Journey j)
    {
        if(j.CargoVisual!=null)UnityEngine.Object.Destroy(j.CargoVisual);
        if(j.Boat!=null&&j.Boat.TryGetComponent<Boat>(out var vessel))
        {
            if(vessel.TownMooringPoint!=null&&vessel.TownMooringPoint.ReturnIsReservedByAgent(j.Worker))vessel.TownMooringPoint.Unreserve(j.Worker);
            if(vessel.CurrentMooringPoint==null&&vessel.TownMooringPoint!=null&&vessel.TownMooringPoint.IsEmpty)vessel.TownMooringPoint.MoorBoat(vessel,true);
            if(j.Worker!=null&&vessel.Captain==j.Worker){if(vessel.CurrentMooringPoint!=null)vessel.Disembark(j.Worker);else vessel.ClearCaptain();}
            vessel.Navigator.enabled=j.BoatNavigatorEnabled;
            j.Boat.PhysicsController.ShouldApplyCurrent=j.BoatCurrent;
        }
        if(j.BoatBody!=null){j.BoatBody.isKinematic=j.BoatKinematic;j.BoatBody.useGravity=j.BoatGravity;}
        if(j.WorkerBody!=null){j.WorkerBody.isKinematic=j.WorkerKinematic;j.WorkerBody.useGravity=j.WorkerGravity;}
        if(j.Worker!=null){var n=j.Worker.ReturnNavigator();if(n!=null){n.enabled=j.NavigatorEnabled;var owner=Replication.ResolveShipCommunity(j.State.fromShip);var heart=owner!=null?FleetEngineBundle.RootFor(owner):null;if(heart!=null)n.AttachToTarget(heart.Target);}}
        Active.Remove(j);
    }
    internal static void ReturnCargo(Community owner,IEnumerable<ItemProperties> items)
    {
        var left=items.Where(p=>p!=null&&!owner.SpawnItemToAvailableStorage(p)).ToList();
        if(left.Count>0)Drop(Heart(owner)+Vector3.right*7,left);
    }
    internal static void Drop(Vector3 pos,IEnumerable<ItemProperties> cargo)
    {
        var items=cargo.Where(p=>p!=null).GroupBy(p=>p).Select(g=>(g.Key,g.Count())).ToArray();
        if(items.Length>0&&!TryDrop(pos,items))
            PendingDrops.Add(new FleetCargoDrop{x=pos.x,z=pos.z,cargo=items.Select(i=>new ItemCount{properties=i.Key.name,count=i.Item2}).ToArray()});
    }
    private static bool TryDrop(Vector3 pos,IReadOnlyList<(ItemProperties,int)> items)
    {
        try{return SeaLootAndLandmarks.SpawnCustomFlotsam(pos,items)!=null;}
        catch(Exception e){Plugin.Log("Cargo remains at sea awaiting a free native pool: "+e.GetType().Name);return false;}
    }
    internal static FleetVoyageState Snapshot(Journey j)=>new FleetVoyageState{id=j.State.id,worker=j.State.worker,boat=j.State.boat,fromShip=j.State.fromShip,toShip=j.State.toShip,kind=j.State.kind,status=j.State.status,workerName=j.State.workerName,x=j.State.x,y=j.State.y,z=j.State.z,hp=j.State.hp,maxHp=j.State.maxHp,cargo=j.State.cargo};
    internal static FleetVoyageState[] Capture()=>Active.Select(Snapshot).ToArray();
    internal static void Rebase(Vector3 oldPosition,Quaternion oldRotation,Vector3 newPosition,Quaternion newRotation)
    {
        var inverse=Quaternion.Inverse(newRotation);
        foreach(var j in Active)j.Position=inverse*(oldPosition+oldRotation*j.Position-newPosition);
        foreach(var drop in PendingDrops){var p=inverse*(oldPosition+oldRotation*new Vector3(drop.x,0,drop.z)-newPosition);drop.x=p.x;drop.z=p.z;}
    }
    internal static FleetVoyageState[] Visible=>Plugin.Instance?.Network?.IsHost==true?Capture():_remote;
    internal static void Apply(FleetVoyageState[] states)
    {
        _remote=states??Array.Empty<FleetVoyageState>();var ids=new HashSet<long>();var net=Plugin.Instance?.Network;
        foreach(var state in _remote)
        {
            ids.Add(state.id);
            if(!RemoteCargo.TryGetValue(state.id,out var visual)||visual==null)
            {
                var parent=state.boat>0?net?.ResolveBuilding(state.boat)?.transform:net?.ResolveAgent(state.worker)?.transform;if(parent==null)continue;
                RemoteCargo[state.id]=visual=FleetPhysicalActors.AttachCargoCrates(parent,state.cargo.Length>0);
            }
            visual.SetActive(state.cargo.Length>0);
        }
        foreach(var pair in RemoteCargo.ToArray())if(!ids.Contains(pair.Key)){if(pair.Value!=null)UnityEngine.Object.Destroy(pair.Value);RemoteCargo.Remove(pair.Key);}
    }
    internal static void Reset(){foreach(var j in Active.ToArray())Finish(j);PendingDrops.Clear();_remote=Array.Empty<FleetVoyageState>();foreach(var visual in RemoteCargo.Values)if(visual!=null)UnityEngine.Object.Destroy(visual);RemoteCargo.Clear();}
}
