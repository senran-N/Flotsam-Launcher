using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Replicates community seagulls (<see cref="Bird"/>) with 60fps flight/perch pose interpolation,
/// carried salvage item visuals, <see cref="BirdPanel"/> rename/release actions, and
/// <see cref="BirdHousePanel"/> food/export/group filter controls.
/// </summary>
internal sealed class BirdReplication
{
    private static bool _installed;

    private static readonly FieldInfo? BirdReservedItemField =
        AccessTools.Field(typeof(Bird), "_reservedItem");
    private static readonly FieldInfo? BirdCarriedParentField =
        AccessTools.Field(typeof(Bird), "_carriedItemParent");
    private static readonly FieldInfo? BirdCarriedTransformField =
        AccessTools.Field(typeof(Bird), "_carriedItemTransform");
    private static readonly PropertyInfo? BirdStateProp =
        AccessTools.Property(typeof(Bird), nameof(Bird.State));
    private static readonly PropertyInfo? BirdHappinessProp =
        AccessTools.Property(typeof(Bird), nameof(Bird.Happiness));
    private static readonly PropertyInfo? BirdIsFedProp =
        AccessTools.Property(typeof(Bird), nameof(Bird.IsFed));
    private static readonly MethodInfo? BirdSetFreeMethod =
        AccessTools.DeclaredMethod(typeof(Bird), "SetFree", new[] { typeof(bool) });
    private static readonly FieldInfo? SalvageToggleGroupField =
        AccessTools.Field(typeof(BirdHousePanelSalvageToggle), "_itemPropertiesGroup");

    private static GameSession? Network => Plugin.Instance?.Network;
    private static bool Client => Network?.IsClient == true && Network.ReplicaReady && !Network.Applying;

    private readonly Dictionary<long, Bird> _birds = new Dictionary<long, Bird>();
    private readonly PoseTrackMap _birdTracks = new PoseTrackMap();
    private readonly Dictionary<long, string> _carriedVisualNames = new Dictionary<long, string>();
    private readonly Dictionary<int, string> _descriptorCache = new Dictionary<int, string>();
    private readonly Dictionary<int, int> _descriptorBudget = new Dictionary<int, int>();

    internal void Reset()
    {
        _birds.Clear();
        _birdTracks.Clear();
        _carriedVisualNames.Clear();
        _descriptorCache.Clear();
        _descriptorBudget.Clear();
    }

    internal void InvalidateClientBudgets() => _descriptorBudget.Clear();

    internal static bool Handles(string kind) =>
        kind == "birdRename" ||
        kind == "birdFree" ||
        kind == "birdHouseFood" ||
        kind == "birdHouseExport" ||
        kind == "birdHouseGroup";

    internal static void Install(Harmony harmony)
    {
        if (_installed) return;

        var setName = AccessTools.DeclaredMethod(typeof(ActorDescriptor), nameof(ActorDescriptor.SetName), new[] { typeof(string) });
        if (setName != null)
            harmony.Patch(setName, prefix: new HarmonyMethod(typeof(BirdReplication), nameof(OnDescriptorSetName)));

        if (BirdSetFreeMethod != null)
            harmony.Patch(BirdSetFreeMethod, prefix: new HarmonyMethod(typeof(BirdReplication), nameof(OnBirdSetFree)));

        var toggleFood = AccessTools.DeclaredMethod(typeof(BirdHouse), nameof(BirdHouse.ToggleFoodRefilling), new[] { typeof(bool) });
        if (toggleFood != null)
            harmony.Patch(toggleFood, prefix: new HarmonyMethod(typeof(BirdReplication), nameof(OnBirdHouseToggleFood)));

        var toggleExport = AccessTools.DeclaredMethod(typeof(BirdHouse), nameof(BirdHouse.EnableItemExport), new[] { typeof(bool) });
        if (toggleExport != null)
            harmony.Patch(toggleExport, prefix: new HarmonyMethod(typeof(BirdReplication), nameof(OnBirdHouseToggleExport)));

        var toggleGroup = AccessTools.DeclaredMethod(typeof(BirdHousePanelSalvageToggle), "OnValueChanged", new[] { typeof(bool) });
        if (toggleGroup != null)
            harmony.Patch(toggleGroup, prefix: new HarmonyMethod(typeof(BirdReplication), nameof(OnBirdHouseToggleGroup)));

        _installed = true;
    }

    // =========================================================================
    // CLIENT UI INTERCEPTIONS
    // =========================================================================

    private static bool OnDescriptorSetName(ActorDescriptor __instance, string name)
    {
        if (!Client) return true;
        if (__instance is not BirdDescriptor bd || bd.Bird == null) return true;
        var id = Network!.Id(bd.Bird);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "birdRename",
                target = id,
                text = name ?? ""
            });
        }
        return false;
    }

    private static bool OnBirdSetFree(Bird __instance, bool setFree)
    {
        if (!Client || !setFree) return true;
        if (__instance == null) return false;
        var id = Network!.Id(__instance);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "birdFree",
                target = id
            });
        }
        return false;
    }

    private static bool OnBirdHouseToggleFood(BirdHouse __instance, bool isOn)
    {
        if (!Client) return true;
        if (__instance?.Buildable == null) return false;
        var id = Network!.Id(__instance.Buildable);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "birdHouseFood",
                target = id,
                value = isOn ? 1 : 0
            });
        }
        return false;
    }

    private static bool OnBirdHouseToggleExport(BirdHouse __instance, bool exportItems)
    {
        if (!Client) return true;
        if (__instance?.Buildable == null) return false;
        var id = Network!.Id(__instance.Buildable);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "birdHouseExport",
                target = id,
                value = exportItems ? 1 : 0
            });
        }
        return false;
    }

    private static bool OnBirdHouseToggleGroup(BirdHousePanelSalvageToggle __instance, bool value)
    {
        if (!Client) return true;
        if (SalvageToggleGroupField?.GetValue(__instance) is not ItemPropertiesGroup group) return false;

        foreach (var comm in Community.Communities)
        {
            if (comm?.BirdHouses == null) continue;
            foreach (var house in comm.BirdHouses)
            {
                if (house?.Buildable == null || house.ItemGroups == null) continue;
                var idx = Array.IndexOf(house.ItemGroups, group);
                if (idx >= 0)
                {
                    var id = Network!.Id(house.Buildable);
                    if (id > 0)
                    {
                        Network.Submit(new GameCommand
                        {
                            kind = "birdHouseGroup",
                            target = id,
                            value = idx,
                            amount = value ? 1 : 0
                        });
                    }
                    return false;
                }
            }
        }
        return false;
    }

    // =========================================================================
    // CAPTURE & APPLY (BIRDHOUSE + BIRDS)
    // =========================================================================

    internal static BirdHouseState? CaptureBirdHouse(Buildable building)
    {
        if (!building.TryGetComponent<BirdHouse>(out var house)) return null;
        var disabled = new List<int>();
        if (house.ItemGroups != null)
        {
            for (var i = 0; i < house.ItemGroups.Length; i++)
            {
                if (house.ItemGroups[i] != null && !house.ItemGroups[i].Enabled)
                    disabled.Add(i);
            }
        }
        return new BirdHouseState
        {
            refillFood = house.RefillFood,
            exportItems = house.ExportItems,
            disabledGroups = disabled.ToArray()
        };
    }

    internal static void ApplyBirdHouse(Buildable building, BirdHouseState? state)
    {
        if (state == null || !building.TryGetComponent<BirdHouse>(out var house)) return;
        if (house.RefillFood != state.refillFood)
            house.RefillFood = state.refillFood;
        if (house.ExportItems != state.exportItems)
            house.ExportItems = state.exportItems;

        if (house.ItemGroups != null)
        {
            var disabled = new HashSet<int>(state.disabledGroups ?? Array.Empty<int>());
            for (var i = 0; i < house.ItemGroups.Length; i++)
            {
                if (house.ItemGroups[i] != null)
                    house.ItemGroups[i].Enabled = !disabled.Contains(i);
            }
        }
    }

    internal void Capture(WorldFrame frame, IEnumerable<Community> communities, Func<UnityEngine.Object, long> hostId)
    {
        foreach (var pair in _birds.ToArray())
            if (pair.Value == null) _birds.Remove(pair.Key);

        var list = new List<BirdReplicaState>();
        foreach (var owner in communities)
        {
            if (owner?.Birds == null) continue;
            foreach (var bird in owner.Birds)
            {
                if (bird == null || bird.Descriptor == null) continue;
                var id = hostId(bird);
                _birds[id] = bird;
                var p = bird.transform.position;
                var houseId = bird.BirdHouse?.Buildable != null ? hostId(bird.BirdHouse.Buildable) : 0L;

                var carried = "";
                if (bird.ItemPickedUp || bird.State == Bird.BirdState.ReturningItem)
                {
                    if (BirdReservedItemField?.GetValue(bird) is Item resItem && resItem.Properties != null)
                        carried = resItem.Properties.name;
                    else if (bird.Inventory != null)
                    {
                        var stored = bird.Inventory.ReturnAllItems(SubInventoryType.Storage, new List<Item>());
                        if (stored.Count > 0 && stored[0]?.Properties != null)
                            carried = stored[0].Properties.name;
                    }
                }

                var uid = bird.Descriptor.UniqueID;
                if (!_descriptorCache.TryGetValue(uid, out var encodedDesc))
                {
                    encodedDesc = Convert.ToBase64String(JsonCodec.EncodeRuntime(bird.Descriptor.GetPersistentData()));
                    _descriptorCache[uid] = encodedDesc;
                }
                _descriptorBudget.TryGetValue(uid, out var sent);
                var sendDesc = sent < 12 ? encodedDesc : "";
                if (sent < 12) _descriptorBudget[uid] = sent + 1;

                list.Add(new BirdReplicaState
                {
                    id = id,
                    owner = owner.Name ?? "",
                    name = bird.Name ?? "",
                    descriptorId = uid,
                    descriptor = sendDesc,
                    birdHouseId = houseId,
                    state = (int)bird.State,
                    happiness = bird.Happiness,
                    isFed = bird.IsFed,
                    carriedItem = carried,
                    x = p.x,
                    y = p.y,
                    z = p.z,
                    rotation = bird.transform.eulerAngles.y
                });
            }
        }
        frame.birds = list.ToArray();
    }

    internal void Apply(
        WorldFrame frame,
        IReadOnlyDictionary<long, Buildable> buildings,
        Func<string?, Community> resolveOwner,
        Action<UnityEngine.Object, long> bindId,
        Action<UnityEngine.Object> unbindId,
        IReadOnlyDictionary<string, ItemProperties> items)
    {
        var incoming = frame.birds ?? Array.Empty<BirdReplicaState>();
        if (incoming.Length > 256) throw new InvalidOperationException("Bird replica count exceeds limit");

        var assigned = new HashSet<int>(_birds.Values.Where(b => b != null).Select(b => b.GetInstanceID()));
        var alive = new HashSet<long>();

        foreach (var state in incoming)
        {
            if (state == null || state.id <= 0 || !alive.Add(state.id) ||
                !Replication.Finite(state.x) || !Replication.Finite(state.y) || !Replication.Finite(state.z) || !Replication.Finite(state.rotation))
                continue;

            ClientOceanAnchor.HostToClientPose(state.x, state.z, state.rotation, out var bx, out var bz, out var byaw);
            var owner = resolveOwner(state.owner);

            if (!_birds.TryGetValue(state.id, out var bird) || bird == null)
            {
                bird = owner.Birds.FirstOrDefault(b => b != null && !assigned.Contains(b.GetInstanceID()) && b.Descriptor?.UniqueID == state.descriptorId);
                if (bird == null)
                    bird = SpawnReplicaBird(state, owner, new Vector3(bx, state.y, bz));
                if (bird == null) continue;

                _birds[state.id] = bird;
                bindId(bird, state.id);
                assigned.Add(bird.GetInstanceID());
            }

            ReplicaFreeze.FreezeBird(bird);

            var targetPose = new PoseSample(bx, state.y, bz, byaw);
            if (_birdTracks.UpdateTarget(state.id, ReplicaFreeze.CurrentPose(bird.transform), targetPose, 625f))
                ReplicaFreeze.ApplyPose(bird.transform, targetPose);

            if (bird.Descriptor != null && bird.Name != state.name && !string.IsNullOrEmpty(state.name))
                bird.Descriptor.SetName(state.name);

            BirdHouse? wantedHouse = null;
            if (state.birdHouseId > 0 && buildings.TryGetValue(state.birdHouseId, out var hb) && hb != null)
                hb.TryGetComponent(out wantedHouse);

            if (bird.BirdHouse != wantedHouse)
            {
                if (bird.BirdHouse != null)
                    bird.BirdHouse.Birds.Remove(bird);
                if (wantedHouse != null && !wantedHouse.Birds.Contains(bird))
                    wantedHouse.Birds.Add(bird);
                bird.SetBirdHouse(wantedHouse!);
            }

            if (Enum.IsDefined(typeof(Bird.BirdState), state.state) && (int)bird.State != state.state)
                BirdStateProp?.SetValue(bird, (Bird.BirdState)state.state);

            if (bird.Happiness != state.happiness)
                BirdHappinessProp?.SetValue(bird, Mathf.Clamp(state.happiness, 0, Mathf.Max(1, bird.HappinessMaxValue)));
            if (bird.IsFed != state.isFed)
                BirdIsFedProp?.SetValue(bird, state.isFed);

            SyncBirdAnimation(bird, (Bird.BirdState)state.state);
            SyncCarriedVisual(state.id, bird, state.carriedItem ?? "", items);
        }

        foreach (var pair in _birds.ToArray())
        {
            if (alive.Contains(pair.Key)) continue;
            if (pair.Value != null)
            {
                unbindId(pair.Value);
                if (pair.Value.BirdHouse != null)
                    pair.Value.BirdHouse.Birds.Remove(pair.Value);
                pair.Value.Community?.RemoveBird(pair.Value);
                UnityEngine.Object.Destroy(pair.Value.gameObject);
            }
            _birds.Remove(pair.Key);
            _birdTracks.Remove(pair.Key);
            _carriedVisualNames.Remove(pair.Key);
        }
    }

    private static Bird? SpawnReplicaBird(BirdReplicaState state, Community owner, Vector3 pos)
    {
        if (state.descriptorId < 0 || state.descriptorId > ushort.MaxValue || state.descriptor.Length > 32768)
            return null;
        if (!ActorDescriptor.TryGet<BirdDescriptor>(out var descriptor, (ushort)state.descriptorId))
        {
            var innerType = AccessTools.Inner(typeof(BirdDescriptor), "PersistentData");
            if (innerType == null || string.IsNullOrEmpty(state.descriptor)) return null;
            var data = (ActorDescriptor.PersistentDataBase)JsonCodec.DecodeRuntime(innerType, Convert.FromBase64String(state.descriptor));
            descriptor = (BirdDescriptor)data.Restore();
        }
        return descriptor?.Spawn<Bird>(owner, pos);
    }

    private static void SyncBirdAnimation(Bird bird, Bird.BirdState state)
    {
        var anim = bird.Animator;
        if (anim == null) return;
        var activity = state switch
        {
            Bird.BirdState.Sleeping => Activity.Sleeping,
            Bird.BirdState.Eating => Activity.Eating,
            Bird.BirdState.ReturningItem => Activity.MovingWithItem,
            Bird.BirdState.GoingToBirdhouse => Activity.Moving,
            Bird.BirdState.GettingItem => Activity.Moving,
            Bird.BirdState.CirclingTown => Activity.Moving,
            Bird.BirdState.LeavingWorld => Activity.Moving,
            _ => Activity.Sitting
        };
        if (anim.GetInteger("Activity") != (int)activity)
            anim.SetInteger("Activity", (int)activity);
    }

    private void SyncCarriedVisual(long id, Bird bird, string wantedItem, IReadOnlyDictionary<string, ItemProperties> items)
    {
        _carriedVisualNames.TryGetValue(id, out var currentItem);
        if ((currentItem ?? "") == wantedItem) return;
        _carriedVisualNames[id] = wantedItem;

        if (BirdCarriedTransformField?.GetValue(bird) is Transform oldVisual && oldVisual != null)
        {
            UnityEngine.Object.Destroy(oldVisual.gameObject);
            BirdCarriedTransformField.SetValue(bird, null);
        }

        if (!string.IsNullOrEmpty(wantedItem) &&
            items.TryGetValue(wantedItem, out var ip) &&
            ip.StorageVisualPrefab != null &&
            BirdCarriedParentField?.GetValue(bird) is Transform parent &&
            parent != null)
        {
            var spawned = UnityEngine.Object.Instantiate(ip.StorageVisualPrefab, parent).transform;
            BirdCarriedTransformField?.SetValue(bird, spawned);
        }
    }

    internal void Interpolate(float alpha)
    {
        foreach (var pair in _birds)
        {
            var bird = pair.Value;
            if (bird == null || !_birdTracks.Sample(pair.Key, alpha, out var pose)) continue;
            var cur = ReplicaFreeze.CurrentPose(bird.transform);
            if (cur.DistanceSq(pose) > 0.000001f || cur.AngleDeltaAbs(pose) > 0.05f)
                ReplicaFreeze.ApplyPose(bird.transform, pose);
        }
    }

    internal void ShiftRebased(Func<PoseSample, PoseSample> shift)
    {
        _birdTracks.ShiftAll(shift);
        foreach (var bird in _birds.Values)
        {
            if (bird != null)
                ReplicaFreeze.ApplyPose(bird.transform, shift(ReplicaFreeze.CurrentPose(bird.transform)));
        }
    }

    // =========================================================================
    // HOST AUTHORITATIVE EXECUTION
    // =========================================================================

    internal string? Execute(
        GameCommand command,
        IReadOnlyDictionary<long, Buildable> buildings,
        Community targetCommunity)
    {
        if (command.kind == "birdRename" || command.kind == "birdFree")
        {
            if (!_birds.TryGetValue(command.target, out var bird) || bird == null || bird.Community != targetCommunity)
                return "该海鸥已不存在或不属于你的船只";

            if (command.kind == "birdRename")
            {
                if (string.IsNullOrWhiteSpace(command.text) || command.text.Length > 48 || command.text.Any(char.IsControl))
                    return "海鸥名称无效";
                bird.Descriptor?.SetName(command.text);
                return null;
            }

            using var _scope = FleetRuntime.WithPlayerCommunity(targetCommunity);
            BirdSetFreeMethod?.Invoke(bird, new object[] { true });
            return null;
        }

        if (!buildings.TryGetValue(command.target, out var building) || building == null)
            return "目标鸟舍已不存在";

        if (building.Community != targetCommunity)
        {
            if (building.Community != targetCommunity)
                return "不能修改其他船只的鸟舍设置";
            targetCommunity = building.Community;
        }

        if (!building.TryGetComponent<BirdHouse>(out var house))
            return "目标建筑不是鸟舍";

        using var _commScope = FleetRuntime.WithPlayerCommunity(targetCommunity);
        switch (command.kind)
        {
            case "birdHouseFood":
                house.ToggleFoodRefilling(command.value != 0);
                return null;
            case "birdHouseExport":
                house.EnableItemExport(command.value != 0);
                return null;
            case "birdHouseGroup":
                if (house.ItemGroups == null || command.value < 0 || command.value >= house.ItemGroups.Length)
                    return "打捞分类索引无效";
                house.ItemGroups[command.value].Enabled = command.amount != 0;
                return null;
            default:
                return "未知海鸥指令";
        }
    }
}
