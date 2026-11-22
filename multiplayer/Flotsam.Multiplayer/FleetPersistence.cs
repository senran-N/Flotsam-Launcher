using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

// This is native save data, also included in the joining player's bootstrap snapshot.
[DataContract]
public sealed class FleetBuildingData : IBuildableExtendablePersistentData
{
    [DataMember] public float[] durability=Array.Empty<float>();
    [DataMember] public CannonState? cannon;
    [DataMember] public FleetJourneyData[] journeys=Array.Empty<FleetJourneyData>();
    [DataMember] public string[] alliances=Array.Empty<string>();
    [DataMember] public string owner="";
    [DataMember] public CannonShellState[] shells=Array.Empty<CannonShellState>();
    [DataMember] public FleetCargoDrop[] cargoDrops=Array.Empty<FleetCargoDrop>();
    public void PopulateReferences() { }
    public void Restore() { }
    public void Validate() { }
    public void RestoreData(Buildable buildable)
    {
        if(!string.IsNullOrEmpty(owner))FleetOwnership.Bind(buildable.Community,owner);
        BuildingDurability.Restore(buildable,durability);
        FleetCombat.Restore(buildable,cannon);
        FleetCombat.RestoreShells(shells);
        FleetVoyages.RestoreDrops(cargoDrops);
        FleetPersistence.Queue(buildable,journeys,alliances);
    }
    public void RestoreReferences() { }
}
[DataContract]
public sealed class FleetJourneyData
{
    [DataMember] public FleetVoyageState state=new FleetVoyageState();
    [DataMember] public int worker,boatIndex=-1,phase;
    [DataMember] public int capacity=4;
    [DataMember] public long targetBuilding;
    [DataMember] public int targetBuildingIndex=-1;
    [DataMember] public bool workerNavigator=true,boatNavigator=true,boatCurrent=true,workerKinematic,workerGravity,boatKinematic,boatGravity;
    [DataMember] public float work,elapsed;
}
internal static class FleetPersistence
{
    private static readonly Queue<(Buildable heart,FleetJourneyData[] journeys,string[] alliances)> Pending=new Queue<(Buildable,FleetJourneyData[],string[])>();
    internal static void Install(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(WorldPersistentData),nameof(WorldPersistentData.Restore)),prefix:new HarmonyMethod(typeof(FleetPersistence),nameof(BeginRestore)));
        h.Patch(AccessTools.DeclaredMethod(typeof(BuildablePersistentData),"GetKnownTypes"),postfix:new HarmonyMethod(typeof(FleetPersistence),nameof(KnownTypes)));
        h.Patch(typeof(BuildablePersistentData).GetConstructor(new[]{typeof(Buildable)}),postfix:new HarmonyMethod(typeof(FleetPersistence),nameof(SaveBuilding)));
    }
    private static void BeginRestore()
    {
        Pending.Clear();FleetVoyages.Reset();FleetCombat.Reset();BuildingDurability.Reset();FleetVisibility.ClearDiplomacy();FleetOwnership.Reset();
    }
    private static void KnownTypes(ref Type[] __result)=>__result=__result.Concat(new[]{typeof(FleetBuildingData)}).ToArray();
    private static void SaveBuilding(BuildablePersistentData __instance,Buildable buildable)
    {
        if(Plugin.Instance?.Network==null)return;
        var data=new FleetBuildingData{durability=BuildingDurability.Save(buildable),cannon=FleetCombat.Save(buildable)};
        if(buildable.TryGetComponent<TownheartStorage>(out _))
        {
            data.owner=FleetOwnership.SavedOwner(buildable.Community);
            if(buildable.Community.IsPlayerCommunity()){data.shells=FleetCombat.CaptureShells();data.cargoDrops=FleetVoyages.SaveDrops();}
            data.alliances=FleetVisibility.SaveAlliances();
            var owner=buildable.Community;var key=Replication.ShipKey(owner);
            data.journeys=FleetVoyages.Active.Where(j=>j.State.fromShip==key&&j.Worker!=null).Select(j=>new FleetJourneyData
            {
                state=FleetVoyages.Snapshot(j),worker=j.Worker.Descriptor.UniqueID,boatIndex=j.Boat!=null?owner.Buildables.IndexOf(j.Boat):-1,
                phase=j.Phase,work=j.Work,elapsed=j.Elapsed,capacity=j.Capacity,targetBuilding=j.TargetBuilding,
                targetBuildingIndex=TargetIndex(j),workerNavigator=j.NavigatorEnabled,boatNavigator=j.BoatNavigatorEnabled,boatCurrent=j.BoatCurrent,
                workerKinematic=j.WorkerKinematic,workerGravity=j.WorkerGravity,boatKinematic=j.BoatKinematic,boatGravity=j.BoatGravity
            }).ToArray();
        }
        __instance.Extendables=(__instance.Extendables??Array.Empty<IBuildableExtendablePersistentData>()).Concat(new IBuildableExtendablePersistentData[]{data}).ToArray();
    }
    private static int TargetIndex(FleetVoyages.Journey j)
    {
        var b=Plugin.Instance?.Network?.ResolveBuilding(j.TargetBuilding);return b!=null?b.Community.Buildables.IndexOf(b):-1;
    }
    internal static void Queue(Buildable b,FleetJourneyData[] data,string[] alliances)
    {
        if((data?.Length??0)>0||(alliances?.Length??0)>0)Pending.Enqueue((b,data??Array.Empty<FleetJourneyData>(),alliances??Array.Empty<string>()));
    }
    internal static void Tick()
    {
        if(!GameSession.Ready())return;
        while(Pending.Count>0)
        {
            var saved=Pending.Dequeue();if(saved.heart==null)continue;
            FleetVisibility.RestoreAlliances(saved.alliances);
            if(Plugin.Instance?.Network?.IsHost!=true)continue;
            var owner=saved.heart.Community;
            var catalog=Resources.FindObjectsOfTypeAll<ItemProperties>().Where(p=>p!=null).GroupBy(p=>p.name).ToDictionary(g=>g.Key,g=>g.First());
            foreach(var item in saved.journeys)
            {
                var cargo=(item.state.cargo??Array.Empty<ItemCount>()).Where(c=>catalog.ContainsKey(c.properties)).SelectMany(c=>Enumerable.Repeat(catalog[c.properties],Mathf.Clamp(c.count,0,999))).ToArray();
                var worker=owner.Agents.FirstOrDefault(a=>a!=null&&a.Descriptor.UniqueID==item.worker);
                var target=Replication.ResolveShipCommunity(item.state.toShip);
                if(worker==null||target==null){FleetVoyages.ReturnCargo(owner,cargo);continue;}
                var boat=item.boatIndex>=0&&item.boatIndex<owner.Buildables.Count?owner.Buildables[item.boatIndex]:null;
                var j=FleetVoyages.Start(owner,target,worker,boat,cargo,item.state.kind=="raid",0);
                j.State.hp=item.state.hp;j.State.maxHp=item.state.maxHp;j.Phase=item.phase;j.Work=item.work;j.Elapsed=item.elapsed;
                j.Capacity=Mathf.Clamp(item.capacity,1,25);j.TargetBuilding=item.targetBuildingIndex>=0&&item.targetBuildingIndex<target.Buildables.Count?Plugin.Instance!.Network!.Id(target.Buildables[item.targetBuildingIndex]):0;
                j.NavigatorEnabled=item.workerNavigator;j.BoatNavigatorEnabled=item.boatNavigator;j.BoatCurrent=item.boatCurrent;
                j.WorkerKinematic=item.workerKinematic;j.WorkerGravity=item.workerGravity;j.BoatKinematic=item.boatKinematic;j.BoatGravity=item.boatGravity;
                j.Position=new Vector3(item.state.x,item.state.y,item.state.z);
            }
        }
    }
}
