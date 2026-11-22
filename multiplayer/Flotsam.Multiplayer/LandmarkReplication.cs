using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Synchronizes island &amp; POI landmark exploration, scavenging (<see cref="LandmarkActionSalvage"/>),
/// live remaining salvage composition counts, independent-ship scavenging contention &amp; ruins discoveries,
/// and stranded Drifter / Seagull rescue (<see cref="LandmarkActionRescue"/> / <see cref="LandmarkRescueable"/>).
/// </summary>
internal static class LandmarkReplication
{
    private static bool _installed;
    private static readonly FieldInfo? UseBoatField = AccessTools.Field(typeof(LandmarkAction), "_useBoat");
    private static readonly MethodInfo? SetLimitSetter = AccessTools.PropertySetter(typeof(LandmarkAction), nameof(LandmarkAction.AssignmentLimit));
    private static readonly MethodInfo? SetStateMethod = AccessTools.DeclaredMethod(
        typeof(LandmarkAction),
        "SetState",
        new[] { typeof(ILandmarkActionStates), typeof(bool) });
    private static readonly MethodInfo? SetCatToggleSetter = AccessTools.PropertySetter(
        typeof(LandmarkActionSalvage.Category),
        nameof(LandmarkActionSalvage.Category.IsToggled));
    private static readonly MethodInfo? SetRescueToggleSetter = AccessTools.PropertySetter(
        typeof(LandmarkActionRescue.Rescueable),
        nameof(LandmarkActionRescue.Rescueable.IsToggled));

    private static readonly Dictionary<string, string> ActiveActionOwners =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static Dictionary<string, ItemProperties>? _itemCatalog;

    internal static Community? CurrentRescueOwner { get; private set; }

    private static GameSession? Network => Plugin.Instance?.Network;
    private static bool Client => Network?.IsClient == true && Network.ReplicaReady && !Network.Applying;

    private static string ActionKey(float mapX, float mapZ, int idx)
    {
        var rx = Mathf.RoundToInt(mapX);
        var rz = Mathf.RoundToInt(mapZ);
        return string.Concat(rx.ToString(), ":", rz.ToString(), ":", idx.ToString());
    }

    internal static void Install(Harmony harmony)
    {
        if (_installed) return;

        harmony.Patch(AccessTools.Method(typeof(LandmarkAction), nameof(LandmarkAction.Activate)),
            prefix: new HarmonyMethod(typeof(LandmarkReplication), nameof(OnActivate)));
        harmony.Patch(AccessTools.Method(typeof(LandmarkAction), nameof(LandmarkAction.Deactivate)),
            prefix: new HarmonyMethod(typeof(LandmarkReplication), nameof(OnDeactivate)));
        harmony.Patch(AccessTools.Method(typeof(LandmarkAction), nameof(LandmarkAction.SetAssignmentLimit)),
            prefix: new HarmonyMethod(typeof(LandmarkReplication), nameof(OnSetLimit)));
        var useBoatSetter = AccessTools.PropertySetter(typeof(LandmarkAction), nameof(LandmarkAction.UseBoat));
        if (useBoatSetter != null)
            harmony.Patch(useBoatSetter, prefix: new HarmonyMethod(typeof(LandmarkReplication), nameof(OnSetUseBoat)));

        var salvageFilter = AccessTools.Method(typeof(LandmarkActionSalvage), nameof(LandmarkActionSalvage.ToggleItemFilter));
        if (salvageFilter != null)
            harmony.Patch(salvageFilter, prefix: new HarmonyMethod(typeof(LandmarkReplication), nameof(OnToggleItemFilter)));

        var catToggle = AccessTools.Method(typeof(LandmarkActionSalvage.Category), nameof(LandmarkActionSalvage.Category.Toggle));
        if (catToggle != null)
            harmony.Patch(catToggle, prefix: new HarmonyMethod(typeof(LandmarkReplication), nameof(OnCategoryToggle)));

        var rescueToggle = AccessTools.Method(typeof(LandmarkActionRescue.Rescueable), nameof(LandmarkActionRescue.Rescueable.Toggle));
        if (rescueToggle != null)
            harmony.Patch(rescueToggle, prefix: new HarmonyMethod(typeof(LandmarkReplication), nameof(OnRescueableToggle)));

        var directRescue = AccessTools.Method(typeof(LandmarkRescueable), nameof(LandmarkRescueable.Rescue));
        if (directRescue != null)
            harmony.Patch(directRescue, prefix: new HarmonyMethod(typeof(LandmarkReplication), nameof(OnDirectRescue)));

        // Route LandmarkActionSalvage.ReturnProjectBlockers to the owning ship's CommunityInventory
        var salvageBlockers = AccessTools.DeclaredMethod(typeof(LandmarkActionSalvage), "ReturnProjectBlockers");
        if (salvageBlockers != null)
        {
            harmony.Patch(
                salvageBlockers,
                prefix: new HarmonyMethod(typeof(LandmarkReplication), nameof(BeforeSalvageBlockers)),
                finalizer: new HarmonyMethod(typeof(LandmarkReplication), nameof(AfterSalvageBlockers)));
        }

        _installed = true;
    }

    private static void BeforeSalvageBlockers(LandmarkActionSalvage __instance, out FleetRuntime.PlayerCommunityScope __state)
    {
        var owner = __instance?.Project != null ? FleetRuntime.ResolveProjectOwner(__instance.Project) : null;
        __state = FleetRuntime.WithPlayerCommunity(owner);
    }

    private static Exception? AfterSalvageBlockers(Exception? __exception, FleetRuntime.PlayerCommunityScope __state)
    {
        __state.Dispose();
        return __exception;
    }

    internal static void Reset()
    {
        CurrentRescueOwner = null;
        ActiveActionOwners.Clear();
        _itemCatalog = null;
    }

    internal static bool Handles(string kind) =>
        kind != null && kind.StartsWith("landmark", StringComparison.Ordinal);

    private static IEnumerable<(LandmarkSpawner Spawner, ActionsBehaviour Behaviour)> EnumerateActiveLandmarks()
    {
        var tiles = GameManager.WorldManager?.World?.Tiles;
        if (tiles == null) yield break;
        foreach (var tile in tiles)
        {
            if (tile?.Landmarks == null) continue;
            foreach (var spawner in tile.Landmarks)
            {
                if (spawner?.LandmarkBehaviour is ActionsBehaviour actions && actions.Actions != null && actions.Actions.Count > 0)
                    yield return (spawner, actions);
            }
        }
    }

    private static bool TryLocateAction(LandmarkAction target, out Vector2 mapPos, out int actionIndex)
    {
        foreach (var (spawner, behaviour) in EnumerateActiveLandmarks())
        {
            var idx = behaviour.Actions.IndexOf(target);
            if (idx >= 0)
            {
                mapPos = spawner.WorldPosition2D;
                actionIndex = idx;
                return true;
            }
        }
        mapPos = Vector2.zero;
        actionIndex = -1;
        return false;
    }

    private static (LandmarkSpawner? Spawner, ActionsBehaviour? Behaviour) FindBehaviourAt(float mapX, float mapZ)
    {
        LandmarkSpawner? bestSpawner = null;
        ActionsBehaviour? best = null;
        var bestDistSq = 16f; // Within 4m in canonical world coordinates
        foreach (var (spawner, behaviour) in EnumerateActiveLandmarks())
        {
            var dx = spawner.WorldPosition2D.x - mapX;
            var dz = spawner.WorldPosition2D.y - mapZ;
            var dSq = dx * dx + dz * dz;
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                bestSpawner = spawner;
                best = behaviour;
            }
        }
        return (bestSpawner, best);
    }

    private static LandmarkAction? FindActionAt(float mapX, float mapZ, int actionIndex)
    {
        if (actionIndex < 0) return null;
        var (_, best) = FindBehaviourAt(mapX, mapZ);
        return best != null && actionIndex < best.Actions.Count ? best.Actions[actionIndex] : null;
    }

    private static bool OnActivate(LandmarkAction __instance)
    {
        if (!Client) return true;
        if (TryLocateAction(__instance, out var pos, out var idx))
            Network!.Submit(new GameCommand { kind = "landmarkActivate", x = pos.x, z = pos.y, value = idx });
        return false;
    }

    private static bool OnDeactivate(LandmarkAction __instance)
    {
        if (!Client) return true;
        if (TryLocateAction(__instance, out var pos, out var idx))
            Network!.Submit(new GameCommand { kind = "landmarkDeactivate", x = pos.x, z = pos.y, value = idx });
        return false;
    }

    private static bool OnSetLimit(LandmarkAction __instance, int limit)
    {
        if (!Client) return true;
        if (TryLocateAction(__instance, out var pos, out var idx))
            Network!.Submit(new GameCommand { kind = "landmarkLimit", x = pos.x, z = pos.y, value = idx, amount = limit });
        return false;
    }

    private static bool OnSetUseBoat(LandmarkAction __instance, bool value)
    {
        if (!Client) return true;
        if (TryLocateAction(__instance, out var pos, out var idx))
            Network!.Submit(new GameCommand { kind = "landmarkBoat", x = pos.x, z = pos.y, value = idx, visual = value ? 1 : 0 });
        return false;
    }

    private static bool OnToggleItemFilter(LandmarkActionSalvage __instance, ItemProperties itemProperties)
    {
        if (!Client) return true;
        if (itemProperties != null && TryLocateAction(__instance, out var pos, out var idx))
            Network!.Submit(new GameCommand { kind = "landmarkItemFilter", x = pos.x, z = pos.y, value = idx, properties = itemProperties.name });
        return false;
    }

    private static bool OnCategoryToggle(LandmarkActionSalvage.Category __instance)
    {
        if (!Client) return true;
        foreach (var (spawner, behaviour) in EnumerateActiveLandmarks())
        {
            for (var i = 0; i < behaviour.Actions.Count; i++)
            {
                if (behaviour.Actions[i] is LandmarkActionSalvage salvage && salvage.Categories != null)
                {
                    var catIdx = salvage.Categories.IndexOf(__instance);
                    if (catIdx >= 0)
                    {
                        var pos = spawner.WorldPosition2D;
                        Network!.Submit(new GameCommand { kind = "landmarkCatToggle", x = pos.x, z = pos.y, value = i, amount = catIdx });
                        return false;
                    }
                }
            }
        }
        return false;
    }

    private static bool OnRescueableToggle(LandmarkActionRescue.Rescueable __instance)
    {
        if (!Client) return true;
        foreach (var (spawner, behaviour) in EnumerateActiveLandmarks())
        {
            for (var i = 0; i < behaviour.Actions.Count; i++)
            {
                if (behaviour.Actions[i] is LandmarkActionRescue rescue && rescue.Rescueables != null)
                {
                    var rIdx = rescue.Rescueables.IndexOf(__instance);
                    if (rIdx >= 0)
                    {
                        var pos = spawner.WorldPosition2D;
                        Network!.Submit(new GameCommand { kind = "landmarkRescueToggle", x = pos.x, z = pos.y, value = i, amount = rIdx });
                        return false;
                    }
                }
            }
        }
        return false;
    }

    private static bool OnDirectRescue(LandmarkRescueable __instance)
    {
        if (!Client) return true;
        if (__instance != null)
        {
            var mapPos = FleetOwnedNavigation.SceneToMap(__instance.transform.position);
            Network!.Submit(new GameCommand { kind = "landmarkDirectRescue", x = mapPos.x, z = mapPos.z });
        }
        return false;
    }

    private static ItemCount[] CaptureLandmarkRemainingItems(ActionsBehaviour behaviour)
    {
        if (behaviour.Landmark == null) return Array.Empty<ItemCount>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var buffer = new List<Item>();
        foreach (var inv in behaviour.Landmark.GetComponentsInChildren<Inventory>(true))
        {
            if (inv == null || !inv.SubInventoryTypes.Contains(SubInventoryType.Composition)) continue;
            buffer.Clear();
            foreach (var it in inv.ReturnAllItems(SubInventoryType.Composition, buffer))
            {
                var pName = it?.Properties?.name;
                if (string.IsNullOrEmpty(pName)) continue;
                counts.TryGetValue(pName!, out var cur);
                counts[pName!] = cur + 1;
            }
        }
        return counts
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ItemCount { properties = kv.Key, section = (int)SubInventoryType.Composition, count = kv.Value })
            .ToArray();
    }

    private static void ReconcileLandmarkRemainingItemsClient(ActionsBehaviour behaviour, ItemCount[]? desired)
    {
        if (desired == null || behaviour.Landmark == null) return;
        if (_itemCatalog == null)
        {
            _itemCatalog = new Dictionary<string, ItemProperties>(StringComparer.Ordinal);
            foreach (var p in Resources.FindObjectsOfTypeAll<ItemProperties>())
                if (p != null && !string.IsNullOrEmpty(p.name) && !_itemCatalog.ContainsKey(p.name))
                    _itemCatalog[p.name] = p;
        }

        var wantedByProp = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ic in desired)
        {
            if (ic != null && !string.IsNullOrEmpty(ic.properties) && ic.count > 0)
                wantedByProp[ic.properties] = ic.count;
        }

        var inventories = behaviour.Landmark.GetComponentsInChildren<Inventory>(true)
            .Where(inv => inv != null && inv.SubInventoryTypes.Contains(SubInventoryType.Composition))
            .ToArray();
        if (inventories.Length == 0) return;

        var currentByProp = new Dictionary<string, int>(StringComparer.Ordinal);
        var buf = new List<Item>();
        foreach (var inv in inventories)
        {
            buf.Clear();
            foreach (var it in inv.ReturnAllItems(SubInventoryType.Composition, buf))
            {
                var n = it?.Properties?.name;
                if (string.IsNullOrEmpty(n)) continue;
                currentByProp.TryGetValue(n!, out var c);
                currentByProp[n!] = c + 1;
            }
        }

        // Remove items from client landmark Composition inventories as Host scavengers haul them away
        foreach (var kv in currentByProp)
        {
            wantedByProp.TryGetValue(kv.Key, out var targetCount);
            var toRemove = kv.Value - targetCount;
            if (toRemove <= 0 || !_itemCatalog.TryGetValue(kv.Key, out var prop)) continue;

            for (var i = 0; i < inventories.Length && toRemove > 0; i++)
            {
                var inv = inventories[i];
                buf.Clear();
                var haveHere = inv.ReturnAllItems(SubInventoryType.Composition, buf)
                    .Count(it => it?.Properties == prop);
                var removeHere = Mathf.Min(toRemove, haveHere);
                for (var r = 0; r < removeHere; r++)
                {
                    if (inv.RemoveItem(prop, SubInventoryType.Composition) != null)
                        toRemove--;
                }
            }
        }
    }

    internal static void Capture(WorldFrame frame)
    {
        var list = new List<LandmarkState>();
        foreach (var (spawner, behaviour) in EnumerateActiveLandmarks())
        {
            var actionStates = new List<LandmarkActionState>();
            var hasInterestingState = false;
            var pos2D = spawner.WorldPosition2D;

            for (var i = 0; i < behaviour.Actions.Count; i++)
            {
                var act = behaviour.Actions[i];
                if (act == null) continue;
                var aKey = ActionKey(pos2D.x, pos2D.y, i);
                var ownerKey = act.Project != null ? Replication.ShipKey(FleetRuntime.ResolveProjectOwner(act.Project)) : "";
                if (!string.IsNullOrEmpty(ownerKey))
                    ActiveActionOwners[aKey] = ownerKey;
                else if (ActiveActionOwners.TryGetValue(aKey, out var remembered))
                    ownerKey = remembered;

                if (act.State == ILandmarkActionStates.Completed)
                {
                    var rewardComm = !string.IsNullOrEmpty(ownerKey)
                        ? Replication.ResolveShipCommunity(ownerKey)
                        : Community.PlayerCommunity;
                    SeaLootAndLandmarks.CheckLandmarkDiscoveryOnComplete(act, pos2D, i, rewardComm);
                }

                var disabledCats = Array.Empty<int>();
                var disabledItems = Array.Empty<string>();
                var toggledRescue = Array.Empty<int>();

                if (act is LandmarkActionSalvage salvage)
                {
                    if (salvage.Categories != null)
                    {
                        var dc = new List<int>();
                        for (var c = 0; c < salvage.Categories.Count; c++)
                            if (salvage.Categories[c] != null && !salvage.Categories[c].IsToggled) dc.Add(c);
                        if (dc.Count > 0) disabledCats = dc.ToArray();
                    }
                    if (salvage.ItemFilter != null)
                    {
                        disabledItems = salvage.ItemFilter
                            .Where(kv => kv.Key != null && !kv.Value)
                            .Select(kv => kv.Key.name)
                            .OrderBy(s => s, StringComparer.Ordinal)
                            .ToArray();
                    }
                }
                else if (act is LandmarkActionRescue rescue && rescue.Rescueables != null)
                {
                    var tr = new List<int>();
                    for (var r = 0; r < rescue.Rescueables.Count; r++)
                        if (rescue.Rescueables[r] != null && rescue.Rescueables[r].IsToggled) tr.Add(r);
                    if (tr.Count > 0) toggledRescue = tr.ToArray();
                }

                if (act.State == ILandmarkActionStates.Active ||
                    act.State == ILandmarkActionStates.Completed ||
                    disabledCats.Length > 0 ||
                    disabledItems.Length > 0 ||
                    toggledRescue.Length > 0)
                {
                    hasInterestingState = true;
                }

                actionStates.Add(new LandmarkActionState
                {
                    index = i,
                    state = (int)act.State,
                    limit = act.AssignmentLimit,
                    useBoat = act.UseBoat,
                    owner = ownerKey,
                    disabledCategories = disabledCats,
                    disabledItems = disabledItems,
                    toggledRescueables = toggledRescue
                });
            }

            if (hasInterestingState)
            {
                list.Add(new LandmarkState
                {
                    mapX = pos2D.x,
                    mapZ = pos2D.y,
                    actions = actionStates.ToArray(),
                    remainingItems = CaptureLandmarkRemainingItems(behaviour)
                });
            }
        }
        frame.landmarks = list.ToArray();
    }

    internal static void Apply(WorldFrame frame)
    {
        if (frame.landmarks == null || frame.landmarks.Length == 0) return;
        foreach (var lm in frame.landmarks)
        {
            if (lm == null || lm.actions == null || !Replication.Finite(lm.mapX) || !Replication.Finite(lm.mapZ)) continue;
            var (_, behaviour) = FindBehaviourAt(lm.mapX, lm.mapZ);
            if (behaviour == null) continue;

            ReconcileLandmarkRemainingItemsClient(behaviour, lm.remainingItems);

            foreach (var aState in lm.actions)
            {
                if (aState == null || !Enum.IsDefined(typeof(ILandmarkActionStates), aState.state)) continue;
                if (aState.index < 0 || aState.index >= behaviour.Actions.Count) continue;
                var action = behaviour.Actions[aState.index];
                if (action == null) continue;

                UseBoatField?.SetValue(action, aState.useBoat);
                if (aState.limit >= 1 && aState.limit <= 20)
                    SetLimitSetter?.Invoke(action, new object[] { aState.limit });

                if (action is LandmarkActionSalvage salvage)
                {
                    var disCatSet = new HashSet<int>(aState.disabledCategories ?? Array.Empty<int>());
                    if (salvage.Categories != null)
                    {
                        for (var c = 0; c < salvage.Categories.Count; c++)
                        {
                            var cat = salvage.Categories[c];
                            if (cat != null)
                                SetCatToggleSetter?.Invoke(cat, new object[] { !disCatSet.Contains(c) });
                        }
                    }
                    if (salvage.ItemFilter != null)
                    {
                        var disItemSet = new HashSet<string>(aState.disabledItems ?? Array.Empty<string>(), StringComparer.Ordinal);
                        foreach (var itemKey in salvage.ItemFilter.Keys.ToArray())
                            if (itemKey != null) salvage.ItemFilter[itemKey] = !disItemSet.Contains(itemKey.name);
                    }
                }
                else if (action is LandmarkActionRescue rescue && rescue.Rescueables != null)
                {
                    var togSet = new HashSet<int>(aState.toggledRescueables ?? Array.Empty<int>());
                    for (var r = 0; r < rescue.Rescueables.Count; r++)
                    {
                        var entry = rescue.Rescueables[r];
                        if (entry != null)
                            SetRescueToggleSetter?.Invoke(entry, new object[] { togSet.Contains(r) });
                    }
                }

                var desired = (ILandmarkActionStates)aState.state;
                if (action.State != desired)
                    SetStateMethod?.Invoke(action, new object[] { desired, true });
            }
        }
    }

    internal static string? Execute(GameCommand command, Community targetCommunity, IReadOnlyDictionary<string, ItemProperties> items)
    {
        using var _scope = FleetRuntime.WithPlayerCommunity(targetCommunity);
        var previousOwner = CurrentRescueOwner;
        CurrentRescueOwner = targetCommunity;
        try
        {
            if (command.kind == "landmarkDirectRescue")
            {
                var targetScene = FleetOwnedNavigation.MapToScene(command.x, command.z);
                LandmarkRescueable? closest = null;
                var bestSq = 400f; // 20m tolerance
                foreach (var r in UnityEngine.Object.FindObjectsByType<LandmarkRescueable>(FindObjectsSortMode.None))
                {
                    if (r == null || r.IsRescued) continue;
                    var dx = r.transform.position.x - targetScene.x;
                    var dz = r.transform.position.z - targetScene.z;
                    var dSq = dx * dx + dz * dz;
                    if (dSq < bestSq)
                    {
                        bestSq = dSq;
                        closest = r;
                    }
                }
                if (closest == null) return "未找到可救援的幸存者";
                closest.Rescue();
                return null;
            }

            var action = FindActionAt(command.x, command.z, command.value);
            if (action == null) return "探索岛屿尚未在视野内生成";

            var aKey = ActionKey(command.x, command.z, command.value);
            var actorKey = Replication.ShipKey(targetCommunity);

            if (Plugin.Options.mode == "independent" && action.State == ILandmarkActionStates.Active)
            {
                var curOwner = action.Project != null
                    ? Replication.ShipKey(FleetRuntime.ResolveProjectOwner(action.Project))
                    : (ActiveActionOwners.TryGetValue(aKey, out var saved) ? saved : "");

                if (!string.IsNullOrEmpty(curOwner) &&
                    curOwner != actorKey &&
                    !FleetVisibility.AreAlliedHost(curOwner, actorKey))
                {
                    return "另一艘船正在搜刮该岛屿废墟，请等待其撤离或争夺制海权";
                }
            }

            switch (command.kind)
            {
                case "landmarkActivate":
                    ActiveActionOwners[aKey] = actorKey;
                    action.Activate();
                    return null;
                case "landmarkDeactivate":
                    ActiveActionOwners.Remove(aKey);
                    action.Deactivate();
                    return null;
                case "landmarkLimit":
                    if (command.amount < 1 || command.amount > 20) return "派遣人数无效";
                    action.SetAssignmentLimit(command.amount);
                    return null;
                case "landmarkBoat":
                    action.UseBoat = command.visual != 0;
                    return null;
                case "landmarkItemFilter":
                    if (action is not LandmarkActionSalvage salFilter || !items.TryGetValue(command.properties, out var prop))
                        return "搜刮过滤物品无效";
                    salFilter.ToggleItemFilter(prop);
                    return null;
                case "landmarkCatToggle":
                    if (action is not LandmarkActionSalvage salCat || salCat.Categories == null ||
                        command.amount < 0 || command.amount >= salCat.Categories.Count)
                        return "搜刮物品分类无效";
                    salCat.Categories[command.amount]?.Toggle();
                    return null;
                case "landmarkRescueToggle":
                    if (action is not LandmarkActionRescue resAct || resAct.Rescueables == null ||
                        command.amount < 0 || command.amount >= resAct.Rescueables.Count)
                        return "救援目标无效";
                    resAct.Rescueables[command.amount]?.Toggle();
                    return null;
                default:
                    return "未知探索地标指令";
            }
        }
        finally
        {
            CurrentRescueOwner = previousOwner;
        }
    }
}
