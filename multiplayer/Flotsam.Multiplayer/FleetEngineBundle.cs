using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Multi-community Engine & EnergyGrid isolation. Allows arbitrary secondary player ships
/// without overwriting primary singletons or merging cross-ship energy grids.
/// </summary>
public static class FleetEngineBundle
{
    private static readonly Dictionary<Community, Owner> Owners = new Dictionary<Community, Owner>();
    private static bool _installed;
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo HeartEngine = Field(typeof(Engine), "_townheartEngine");
    private static readonly FieldInfo Engines = Field(typeof(Engine), "_engines");
    private static readonly FieldInfo IsHeart = Field(typeof(Engine), "_isTownheartEngine");
    private static readonly FieldInfo Tug = Field(typeof(Engine), "_tugCapacity");
    private static readonly FieldInfo Threshold = Field(typeof(Engine), "_cooldownThreshold");
    private static readonly FieldInfo NativeCooling = Field(typeof(Engine), "<IsCoolingDown>k__BackingField");
    private static readonly FieldInfo NativeCooldownStart = Field(typeof(Engine), "<CooldownStartTime>k__BackingField");

    internal sealed class Owner
    {
        internal readonly Community Community;
        internal readonly string Id;
        internal readonly bool Primary;
        internal Engine Heart;
        internal Construction Root;
        internal float Weight, Capacity, Used, Available, Start;
        internal bool Cooling;
        internal WeightTier? Tier;

        internal Owner(Community community, string id, Engine heart, Construction root)
        {
            Community = community;
            Id = id;
            Heart = heart;
            Root = root;
            Primary = ReferenceEquals(community, Community.PlayerCommunity);
        }
    }

    private static FieldInfo Field(Type type, string name) => type.GetField(name, Flags) ?? throw new MissingFieldException(type.FullName, name);
    private static MethodInfo Method(Type type, string name, params Type[] args) => type.GetMethod(name, Flags, null, args, null) ?? throw new MissingMethodException(type.FullName, name);
    private static MethodInfo Own(string name) => typeof(FleetEngineBundle).GetMethods(Flags).Single(m => m.Name == name);

    public static void Install(Harmony harmony)
    {
        if (_installed) return;
        harmony.Patch(Method(typeof(Engine), "Initialize", typeof(Buildable), typeof(bool)), transpiler: new HarmonyMethod(Own(nameof(EngineInitializeIL))));
        harmony.Patch(Method(typeof(Engine), "Finish", typeof(bool)), postfix: new HarmonyMethod(Own(nameof(Finished))));
        harmony.Patch(Method(typeof(Engine), "LateUpdate"), prefix: new HarmonyMethod(Own(nameof(LateEngine))), postfix: new HarmonyMethod(Own(nameof(AfterLateEngine))));
        harmony.Patch(Method(typeof(Engine), "ReturnTownTugCapacity"), prefix: new HarmonyMethod(Own(nameof(TugCapacity))));
        harmony.Patch(Method(typeof(Engine), "ReturnEelsPerUnit"), prefix: new HarmonyMethod(Own(nameof(EelsPerUnit))));
        harmony.Patch(Method(typeof(Engine), "ReturnEnergyCost", typeof(float)), prefix: new HarmonyMethod(Own(nameof(EnergyCost))));
        harmony.Patch(Method(typeof(Engine), "ConsumeEnergy", typeof(float)), prefix: new HarmonyMethod(Own(nameof(Consume))));
        harmony.Patch(Method(typeof(Engine), "ReturnMoveableDistance", typeof(float)), prefix: new HarmonyMethod(Own(nameof(Moveable))));
        harmony.Patch(Method(typeof(Engine), "IsEnabled"), prefix: new HarmonyMethod(Own(nameof(EnabledEngine))));
        harmony.Patch(Method(typeof(TownheartStorage), "Initialize", typeof(Buildable), typeof(bool)), transpiler: new HarmonyMethod(Own(nameof(HeartInitializeIL))));
        harmony.Patch(Method(typeof(EnergyGrid), "Connect", typeof(EnergyGridConnector), typeof(EnergyGridConnector)), prefix: new HarmonyMethod(Own(nameof(ConnectGrid))));
        harmony.Patch(Method(typeof(EnergyGrid), "ConnectWithIndex", typeof(EnergyGridConnector), typeof(uint), typeof(EnergyGridConnector)), prefix: new HarmonyMethod(Own(nameof(ConnectGridIndexed))));
        harmony.Patch(Method(typeof(EnergyGrid), "Merge", typeof(EnergyGrid)), prefix: new HarmonyMethod(Own(nameof(MergeGrid))));
        harmony.Patch(Method(typeof(EnergyGridConnector), "Connect", typeof(EnergyGridConnector)), prefix: new HarmonyMethod(Own(nameof(ConnectConnector))));
        harmony.Patch(Method(typeof(EnergyGridConnector), "Connect", typeof(EnergyGridConnector), typeof(uint)), prefix: new HarmonyMethod(Own(nameof(ConnectConnectorIndexed))));
        harmony.Patch(Method(typeof(EnergyGridConnector), "SetEnergyGrid", typeof(EnergyGrid)), prefix: new HarmonyMethod(Own(nameof(SetGrid))));
        harmony.Patch(Method(typeof(Community), "ReturnRegularCommunities"), postfix: new HarmonyMethod(Own(nameof(FilterNpcCommunities))));
        _installed = true;
    }

    public static void Reset() => Owners.Clear();

    public static void EnsurePrimary()
    {
        var primary = Community.PlayerCommunity;
        if (primary == null || Owners.ContainsKey(primary)) return;
        var heart = primary.Engine;
        var root = Construction.Townheart;
        if (heart != null && root != null) Owners[primary] = new Owner(primary, "host", heart, root);
    }

    public static void RegisterShip(Community community, string id, Engine engine, Construction root)
    {
        EnsurePrimary();
        Owners[community] = new Owner(community, id, engine, root);
    }

    /// <summary>
    /// Re-points the owner table after a guest promotes its assigned ship to the
    /// local player community: the local ship becomes primary and the former
    /// primary (the host's ship) becomes a secondary fleet member.
    /// </summary>
    public static void AdoptLocal(Community? previousPrimary, Community local, string previousId)
    {
        if (local == null) return;
        Owner? old = null;
        if (previousPrimary != null && Owners.TryGetValue(previousPrimary, out var previous))
        {
            old = previous;
            Owners.Remove(previousPrimary);
        }
        Owners.Remove(local);
        var heart = local.Buildables.FirstOrDefault(b => b != null && b.GetComponent<Engine>() != null && b.GetComponent<Construction>() != null);
        if (heart == null) return;
        Owners[local] = new Owner(local, "local", heart.GetComponent<Engine>()!, heart.GetComponent<Construction>()!);
        if (old != null && previousPrimary != null)
            Owners[previousPrimary] = new Owner(previousPrimary, previousId, old.Heart, old.Root);
    }

    public static bool IsSecondary(Community? community) => community != null && Owners.TryGetValue(community, out var owner) && !owner.Primary;
    public static Construction? RootFor(Community? community) => community != null && Owners.TryGetValue(community, out var owner) ? owner.Root : null;
    public static Buildable? HeartBuildable(Community? community) => RootFor(community)?.Buildable;
    public static IEnumerable<Community> SecondaryCommunities => Owners.Values.Where(o => !o.Primary && o.Community != null && o.Root != null).Select(o => o.Community);
    public static Community? FindShipById(string id) => Owners.Values.FirstOrDefault(o => o.Id == id)?.Community;
    public static string OwnerIdOf(Community? community) => community != null && Owners.TryGetValue(community, out var owner) ? owner.Id : "";

    public static void RebindShipId(Community community, string newId)
    {
        if (community == null || !Owners.TryGetValue(community, out var old)) return;
        Owners[community] = new Owner(community, newId, old.Heart, old.Root);
    }

    public static float EnergyOf(Community community)
    {
        if (!Owners.TryGetValue(community, out var owner) || owner.Heart?.EnergyGrid == null) return 0f;
        return owner.Heart.EnergyGrid.ReturnStorageEnergy();
    }

    public static void ConsumeEnergy(Community community, float distance)
    {
        if (distance <= 0f || !Owners.TryGetValue(community, out var owner) || owner.Heart == null) return;
        owner.Heart.ConsumeEnergy(distance);
    }

    private static List<Engine> NativeEngines() => Engines.GetValue(null) as List<Engine> ?? new List<Engine>();

    private static bool TryOwner(Engine engine, out Owner owner)
    {
        owner = null!;
        if (!_installed || engine == null || engine.Buildable == null || engine.Buildable.Community == null) return false;
        EnsurePrimary();
        return Owners.TryGetValue(engine.Buildable.Community, out owner!);
    }

    private static Community? CommunityOf(EnergyGridConnector? connector)
    {
        if (connector == null) return null;
        if (connector is EnergyGridBuildableComponent b && b.Buildable != null) return b.Buildable.Community;
        if (connector is EnergyGridDecorationComponent d && d.Decoration != null) return d.Decoration.Community;
        return connector.GetComponentInParent<Buildable>()?.Community;
    }

    private static bool Tracked(Community? community) => community != null && Owners.ContainsKey(community);
    private static bool Compatible(Community? a, Community? b) => (!Tracked(a) && !Tracked(b)) || (a != null && ReferenceEquals(a, b));

    private static bool GridCompatible(EnergyGrid a, EnergyGrid b)
    {
        if (ReferenceEquals(a, b)) return true;
        var all = a.Links.Concat(b.Links).Select(CommunityOf).ToArray();
        var owned = all.Where(Tracked).Distinct().ToArray();
        return owned.Length == 0 || (owned.Length == 1 && all.All(c => ReferenceEquals(c, owned[0])));
    }

    private static bool OwnerGrid(EnergyGrid? grid, Owner owner) =>
        grid != null && grid.IsTownheartGrid && owner.Heart != null && ReferenceEquals(grid, owner.Heart.EnergyGrid);

    private static float Capacity(Owner owner)
    {
        float total = 0f;
        foreach (var engine in NativeEngines())
        {
            if (engine == null || engine.Buildable == null || !ReferenceEquals(engine.Buildable.Community, owner.Community)) continue;
            if ((bool)IsHeart.GetValue(engine)! || (engine.Active && engine.Buildable.BuildPhase == BuildPhase.Finished && OwnerGrid(engine.EnergyGrid, owner)))
                total += (float)Tug.GetValue(engine)!;
        }
        return Mathf.Max(total, 1f);
    }

    private static void Refresh(Owner owner)
    {
        if (owner.Heart == null || owner.Root == null) return;
        owner.Weight = Mathf.Max(0f, owner.Community.ReturnWeight());
        owner.Capacity = Capacity(owner);
        owner.Used = Mathf.Clamp(owner.Weight / owner.Capacity, 0f, 1f);
        owner.Available = Mathf.Max(owner.Capacity - owner.Weight, 0f);
        owner.Tier = owner.Heart.WeightTiers.FirstOrDefault(t => t != null && t.IsInRange(owner.Used));
        if (owner.Primary) { owner.Cooling = Engine.IsCoolingDown; owner.Start = Engine.CooldownStartTime; }
    }

    private static float Eels(Owner owner, Engine engine) =>
        GameplaySettings.ReturnEelsPerUnit(owner.Weight) * engine.Buildable.ReturnModifier(ModifierType.MovementEnergyCost);

    private static float Cost(Owner owner, Engine engine, float distance) =>
        owner.Capacity <= 0f ? 0f : distance * Eels(owner, engine);

    private static void SetCooling(Owner owner, bool cooling, float start)
    {
        owner.Cooling = cooling;
        owner.Start = start;
        if (owner.Primary) { NativeCooling.SetValue(null, cooling); NativeCooldownStart.SetValue(null, start); }
    }

    private static bool TugCapacity(Engine __instance, ref float __result)
    {
        if (!TryOwner(__instance, out var owner)) return true;
        __result = Capacity(owner);
        return false;
    }

    private static bool EelsPerUnit(Engine __instance, ref float __result)
    {
        if (!TryOwner(__instance, out var owner)) return true;
        Refresh(owner);
        __result = Eels(owner, __instance);
        return false;
    }

    private static bool EnergyCost(Engine __instance, float distance, ref float __result)
    {
        if (!TryOwner(__instance, out var owner)) return true;
        Refresh(owner);
        __result = Cost(owner, __instance, distance);
        return false;
    }

    private static bool Consume(Engine __instance, float distance, ref bool __result)
    {
        if (!TryOwner(__instance, out var owner)) return true;
        if (EnergyDevTools.FreeMovement || distance <= 0f) { __result = true; return false; }
        Refresh(owner);
        if (__instance.EnergyGrid != null)
        {
            var cost = Mathf.Max(0f, Cost(owner, __instance, distance));
            __instance.EnergyGrid.RequestStorageEnergy(cost);
            if (!owner.Cooling && __instance.ReturnEnergyRange() == 0f) SetCooling(owner, true, Time.realtimeSinceStartup);
        }
        __result = true;
        return false;
    }

    private static bool Moveable(Engine __instance, float desiredDistance, ref float __result)
    {
        if (!TryOwner(__instance, out var owner)) return true;
        Refresh(owner);
        __result = owner.Cooling ? 0f : desiredDistance;
        return false;
    }

    private static bool EnabledEngine(Engine __instance, ref bool __result)
    {
        if (!TryOwner(__instance, out var owner)) return true;
        __result = __instance.Active && __instance.Buildable.BuildPhase == BuildPhase.Finished &&
            (ReferenceEquals(__instance, owner.Heart) || OwnerGrid(__instance.EnergyGrid, owner));
        return false;
    }

    private static bool LateEngine(Engine __instance)
    {
        if (!TryOwner(__instance, out var owner) || owner.Primary || !(bool)IsHeart.GetValue(__instance)!) return true;
        Refresh(owner);
        if (owner.Cooling && __instance.EnergyGrid != null)
            owner.Cooling = __instance.EnergyGrid.ReturnStorageEnergy() < (float)Threshold.GetValue(__instance)!;
        return false;
    }

    private static void AfterLateEngine(Engine __instance)
    {
        if (TryOwner(__instance, out var owner) && owner.Primary) Refresh(owner);
    }

    private static void Finished(Engine __instance)
    {
        if (TryOwner(__instance, out var owner)) Refresh(owner);
    }

    private static void BindEngine(Engine engine)
    {
        if (!_installed || engine.Buildable == null || !Owners.TryGetValue(engine.Buildable.Community, out var owner) || owner.Primary)
            HeartEngine.SetValue(null, engine);
    }

    private static void BindHeart(Construction root, TownheartStorage storage)
    {
        if (!_installed || storage.Buildable == null || !Owners.TryGetValue(storage.Buildable.Community, out var owner) || owner.Primary)
            Construction.Townheart = root;
    }

    private static void MoveLabels(CodeInstruction source, CodeInstruction dest)
    {
        foreach (var name in new[] { "labels", "blocks" })
        {
            var f = Field(typeof(CodeInstruction), name);
            var from = (System.Collections.IList)f.GetValue(source)!;
            var to = (System.Collections.IList)f.GetValue(dest)!;
            foreach (var item in from) to.Add(item);
            from.Clear();
        }
    }

    private static IEnumerable<CodeInstruction> EngineInitializeIL(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var i in instructions)
        {
            if (i.opcode == OpCodes.Stsfld && Equals(i.operand, HeartEngine))
            {
                i.opcode = OpCodes.Call;
                i.operand = Own(nameof(BindEngine));
            }
            yield return i;
        }
    }

    private static IEnumerable<CodeInstruction> HeartInitializeIL(IEnumerable<CodeInstruction> instructions)
    {
        var setter = AccessTools.PropertySetter(typeof(Construction), "Townheart");
        foreach (var i in instructions)
        {
            if (!i.Calls(setter)) { yield return i; continue; }
            var load = new CodeInstruction(OpCodes.Ldarg_0);
            MoveLabels(i, load);
            yield return load;
            i.opcode = OpCodes.Call;
            i.operand = Own(nameof(BindHeart));
            yield return i;
        }
    }

    private static bool ConnectGrid(EnergyGridConnector from, EnergyGridConnector to) => !_installed || Compatible(CommunityOf(from), CommunityOf(to));
    private static bool ConnectGridIndexed(EnergyGridConnector from, EnergyGridConnector to) => ConnectGrid(from, to);
    private static bool ConnectConnector(EnergyGridConnector __instance, EnergyGridConnector component, ref bool __result)
    {
        if (!_installed || Compatible(CommunityOf(__instance), CommunityOf(component))) return true;
        __result = false;
        return false;
    }
    private static bool ConnectConnectorIndexed(EnergyGridConnector __instance, EnergyGridConnector component, ref bool __result) =>
        ConnectConnector(__instance, component, ref __result);
    private static bool MergeGrid(EnergyGrid __instance, EnergyGrid other) => !_installed || GridCompatible(__instance, other);
    private static bool SetGrid(EnergyGridConnector __instance, EnergyGrid grid)
    {
        if (!_installed) return true;
        var owner = CommunityOf(__instance);
        var links = grid.Links.Select(CommunityOf).ToArray();
        if (!Tracked(owner) && !links.Any(Tracked)) return true;
        return owner != null && links.All(c => ReferenceEquals(c, owner));
    }

    private static void FilterNpcCommunities(ref List<Community> __result)
    {
        if (_installed && Owners.Count > 0) __result = __result.Where(c => !Owners.ContainsKey(c)).ToList();
    }
}
