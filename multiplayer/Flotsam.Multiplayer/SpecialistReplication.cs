using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PajamaLlama.Fltsm;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Flotsam.Multiplayer;

/// <summary>
/// Synchronizes mid/late-game specialized building panels (<see cref="StorageFilterPanel"/>,
/// <see cref="AquaFarm"/>, <see cref="ItemDistributer"/>, <see cref="OzoneShield"/>),
/// boat dock-to-dock re-mooring (<see cref="BoatMovementCursorProperties"/>),
/// and drifter specialist progression (<see cref="Diet"/>, <see cref="DrifterAttributes"/>, <see cref="Disease"/>).
/// </summary>
internal static class SpecialistReplication
{
    private static bool _installed;
    private static readonly Dictionary<string, FishProperties> FishCatalog =
        new Dictionary<string, FishProperties>(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> LastSliderSubmitAt =
        new Dictionary<string, float>(StringComparer.Ordinal);

    private static readonly FieldInfo? StoragePanelStorageField =
        AccessTools.Field(typeof(StorageFilterPanel), "_storage");
    private static readonly FieldInfo? FilterUiFilterField =
        AccessTools.Field(typeof(FilterUIInteractable), "_filter");
    private static readonly FieldInfo? BoatCursorPreviewField =
        AccessTools.Field(typeof(BoatMovementCursorProperties), "_boatPreview");
    private static readonly FieldInfo? BoatCursorBoatField =
        AccessTools.Field(typeof(BoatMovementCursorProperties), "_boat");
    private static readonly MethodInfo? BoatCursorUpdateTransform =
        AccessTools.DeclaredMethod(typeof(BoatMovementCursorProperties), "UpdateTransform", new[] { typeof(BoatPreview), typeof(Vector3) });

    private static readonly PropertyInfo? FishConsumedProp =
        AccessTools.Property(typeof(AquaFarm.Fish), "Consumed");
    private static readonly PropertyInfo? FishProgressProp =
        AccessTools.Property(typeof(AquaFarm.Fish), "Progress");
    private static readonly PropertyInfo? FishHungryProp =
        AccessTools.Property(typeof(AquaFarm.Fish), "Hungry");
    private static readonly MethodInfo? AquaAddBroodInstanceMethod =
        AccessTools.DeclaredMethod(typeof(AquaFarm), "AddBroodFish", new[] { typeof(AquaFarm.Fish) });

    private static readonly PropertyInfo? DistAvailableProp =
        AccessTools.Property(typeof(ItemToDistribute), nameof(ItemToDistribute.Available));
    private static readonly PropertyInfo? AttrLevelProp =
        AccessTools.Property(typeof(DrifterAttributes), nameof(DrifterAttributes.Level));
    private static readonly PropertyInfo? AttrExpProp =
        AccessTools.Property(typeof(DrifterAttributes), nameof(DrifterAttributes.Experience));
    private static readonly PropertyInfo? AttrPointsProp =
        AccessTools.Property(typeof(DrifterAttributes), nameof(DrifterAttributes.SpendablePoints));
    private static readonly PropertyInfo? PollutionDiseaseProp =
        AccessTools.Property(typeof(Pollution), nameof(Pollution.CurrentDisease));

    private static GameSession? Network => Plugin.Instance?.Network;
    private static bool Client => Network?.IsClient == true && Network.ReplicaReady && !Network.Applying;

    internal static void Reset()
    {
        FishCatalog.Clear();
        LastSliderSubmitAt.Clear();
    }

    private static void EnsureFishCatalog()
    {
        if (FishCatalog.Count != 0) return;
        foreach (var fp in Resources.FindObjectsOfTypeAll<FishProperties>())
        {
            if (fp != null && !string.IsNullOrEmpty(fp.name) && !FishCatalog.ContainsKey(fp.name))
                FishCatalog.Add(fp.name, fp);
        }
    }

    internal static bool HandlesBuilding(string kind) =>
        kind == "moveBoat" ||
        kind == "storageTag" ||
        kind == "storageItem" ||
        kind == "storageBatch" ||
        kind == "storagePaste" ||
        kind == "aquaActive" ||
        kind == "aquaAddBrood" ||
        kind == "aquaRemoveBrood" ||
        kind == "distThreshold" ||
        kind == "distAmount" ||
        kind == "ozoneTrigger";

    internal static bool HandlesAgent(string kind) =>
        kind == "agentDiet" || kind == "agentLevelAttr";

    internal static void Install(Harmony harmony)
    {
        if (_installed) return;

        var boatCursorUpdate = AccessTools.DeclaredMethod(typeof(BoatMovementCursorProperties), nameof(BoatMovementCursorProperties.UpdateCursor), new[] { typeof(CursorManager) });
        if (boatCursorUpdate != null)
            harmony.Patch(boatCursorUpdate, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnBoatMovementUpdate)));

        var toggleTags = AccessTools.DeclaredMethod(typeof(StorageFilterPanel), nameof(StorageFilterPanel.ToggleTags), new[] { typeof(Item.Tags), typeof(bool) });
        if (toggleTags != null)
            harmony.Patch(toggleTags, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnStorageToggleTags)));

        var filterSwitch = AccessTools.DeclaredMethod(typeof(StorageFilterPanel), nameof(StorageFilterPanel.FilterSwitch), new[] { typeof(bool) });
        if (filterSwitch != null)
            harmony.Patch(filterSwitch, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnStorageFilterSwitch)));

        var filterPaste = AccessTools.DeclaredMethod(typeof(StorageFilterPanel), nameof(StorageFilterPanel.Paste), Type.EmptyTypes);
        if (filterPaste != null)
            harmony.Patch(filterPaste, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnStoragePaste)));

        var activateItem = AccessTools.DeclaredMethod(typeof(FilterUIInteractable), nameof(FilterUIInteractable.ActivateFilter), new[] { typeof(bool) });
        if (activateItem != null)
            harmony.Patch(activateItem, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnStorageActivateItem)));

        var aquaSetActive = AccessTools.DeclaredMethod(typeof(AquaFarm), nameof(AquaFarm.SetActiveFishProperties), new[] { typeof(FishProperties) });
        if (aquaSetActive != null)
            harmony.Patch(aquaSetActive, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnAquaSetActive)));

        var aquaAddBrood = AccessTools.DeclaredMethod(typeof(AquaFarm), nameof(AquaFarm.AddBroodFish), Type.EmptyTypes);
        if (aquaAddBrood != null)
            harmony.Patch(aquaAddBrood, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnAquaAddBrood)));

        var aquaRemoveBrood = AccessTools.DeclaredMethod(typeof(AquaFarm), nameof(AquaFarm.RemoveBroodFish), Type.EmptyTypes);
        if (aquaRemoveBrood != null)
            harmony.Patch(aquaRemoveBrood, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnAquaRemoveBrood)));

        var setThreshold = AccessTools.DeclaredMethod(typeof(ItemToDistribute), nameof(ItemToDistribute.SetRefillThrehold), new[] { typeof(float) });
        if (setThreshold != null)
            harmony.Patch(setThreshold, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnSetRefillThreshold)));

        var setAmount = AccessTools.DeclaredMethod(typeof(ItemToDistribute), nameof(ItemToDistribute.SetRefillAmount), new[] { typeof(float) });
        if (setAmount != null)
            harmony.Patch(setAmount, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnSetRefillAmount)));

        var ozoneTrigger = AccessTools.DeclaredMethod(typeof(OzoneShield), nameof(OzoneShield.Trigger), Type.EmptyTypes);
        if (ozoneTrigger != null)
            harmony.Patch(ozoneTrigger, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnOzoneTrigger)));

        var dietPriority = AccessTools.DeclaredMethod(typeof(Diet), nameof(Diet.SetPriority), new[] { typeof(ItemProperties), typeof(AssignmentPriority) });
        if (dietPriority != null)
            harmony.Patch(dietPriority, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnDietSetPriority)));

        var levelAttr = AccessTools.DeclaredMethod(typeof(DrifterAttributes), nameof(DrifterAttributes.TryLevelAttribute), new[] { typeof(DrifterAttributes.AttributeType) });
        if (levelAttr != null)
            harmony.Patch(levelAttr, prefix: new HarmonyMethod(typeof(SpecialistReplication), nameof(OnTryLevelAttribute)));

        _installed = true;
    }

    // =========================================================================
    // CLIENT UI INTERCEPTIONS
    // =========================================================================

    private static bool OnBoatMovementUpdate(BoatMovementCursorProperties __instance, CursorManager cursor)
    {
        if (!Client) return true;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return true;
        if (!__instance.GetInteract()) return true;

        var preview = BoatCursorPreviewField?.GetValue(__instance) as BoatPreview;
        var boat = BoatCursorBoatField?.GetValue(__instance) as Boat;
        if (preview == null || boat?.Buildable == null) return false;

        BoatCursorUpdateTransform?.Invoke(__instance, new object[] { preview, CursorManager.BuildingPosition });
        var dock = preview.ClosestMooringPoint;
        if (dock?.Buildable == null) return false;

        var boatId = Network!.Id(boat.Buildable);
        var dockId = Network.Id(dock.Buildable);
        if (boatId > 0 && dockId > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "moveBoat",
                target = boatId,
                reference = dockId
            });
        }
        cursor.Deactivate();
        return false;
    }

    private static bool OnStorageToggleTags(StorageFilterPanel __instance, Item.Tags tags, bool enabled)
    {
        if (!Client) return true;
        var storage = StoragePanelStorageField?.GetValue(__instance) as Storage;
        if (storage?.Buildable == null) return false;
        var id = Network!.Id(storage.Buildable);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "storageTag",
                target = id,
                value = (int)tags,
                amount = enabled ? 1 : 0
            });
        }
        return false;
    }

    private static bool OnStorageFilterSwitch(StorageFilterPanel __instance, bool activate)
    {
        if (!Client) return true;
        var storage = StoragePanelStorageField?.GetValue(__instance) as Storage;
        if (storage?.Buildable == null) return false;
        var id = Network!.Id(storage.Buildable);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "storageBatch",
                target = id,
                amount = activate ? 1 : 0
            });
        }
        return false;
    }

    private static bool OnStoragePaste(StorageFilterPanel __instance)
    {
        if (!Client) return true;
        var storage = StoragePanelStorageField?.GetValue(__instance) as Storage;
        if (storage?.Buildable == null || storage.Filter == null) return false;
        if (!ItemFilter.ItemFiltersToCopy.TryGetValue(storage.Filter.Tags, out var copied) || copied == null)
            return false;

        var id = Network!.Id(storage.Buildable);
        if (id > 0)
        {
            var disabled = copied.AllItems
                .Where(i => i != null && !copied.AcceptedItems.Contains(i))
                .Select(i => i.name)
                .ToArray();
            var joined = string.Join(",", disabled);
            if (joined.Length > 500) joined = joined.Substring(0, 500);
            Network.Submit(new GameCommand
            {
                kind = "storagePaste",
                target = id,
                value = (int)copied.AcceptedTags,
                text = joined
            });
        }
        return false;
    }

    private static bool OnStorageActivateItem(FilterUIInteractable __instance, bool activate)
    {
        if (!Client) return true;
        if (__instance?.ItemProperties == null) return false;
        var filter = FilterUiFilterField?.GetValue(__instance) as ItemFilter;
        var storage = ResolveStorageForFilter(filter);
        if (storage?.Buildable == null) return false;

        var id = Network!.Id(storage.Buildable);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "storageItem",
                target = id,
                properties = __instance.ItemProperties.name,
                amount = activate ? 1 : 0
            });
        }
        return false;
    }

    private static Storage? ResolveStorageForFilter(ItemFilter? filter)
    {
        if (filter == null) return null;
        foreach (var panel in Resources.FindObjectsOfTypeAll<StorageFilterPanel>())
        {
            if (panel == null || !panel.gameObject.scene.IsValid()) continue;
            if (StoragePanelStorageField?.GetValue(panel) is Storage s && ReferenceEquals(s.Filter, filter))
                return s;
        }
        foreach (var c in Community.Communities)
        {
            if (c == null) continue;
            foreach (var b in c.Buildables)
            {
                if (b != null && b.TryGetComponent<Storage>(out var s) && ReferenceEquals(s.Filter, filter))
                    return s;
            }
        }
        return null;
    }

    private static bool OnAquaSetActive(AquaFarm __instance, FishProperties fishProperties)
    {
        if (!Client) return true;
        if (__instance?.Buildable != null && fishProperties != null)
        {
            var id = Network!.Id(__instance.Buildable);
            if (id > 0)
            {
                Network.Submit(new GameCommand
                {
                    kind = "aquaActive",
                    target = id,
                    properties = fishProperties.name
                });
            }
        }
        return true; // Allow local UI page switch immediately while Host applies authoritative state
    }

    private static bool OnAquaAddBrood(AquaFarm __instance, ref bool __result)
    {
        if (!Client) return true;
        __result = false;
        if (__instance?.Buildable == null) return false;
        var id = Network!.Id(__instance.Buildable);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "aquaAddBrood",
                target = id,
                properties = __instance.ActiveFishProperties != null ? __instance.ActiveFishProperties.name : ""
            });
        }
        return false;
    }

    private static bool OnAquaRemoveBrood(AquaFarm __instance)
    {
        if (!Client) return true;
        if (__instance?.Buildable == null) return false;
        var id = Network!.Id(__instance.Buildable);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "aquaRemoveBrood",
                target = id,
                properties = __instance.ActiveFishProperties != null ? __instance.ActiveFishProperties.name : ""
            });
        }
        return false;
    }

    private static bool OnSetRefillThreshold(ItemToDistribute __instance, float value)
    {
        if (!Client) return true;
        SubmitDistributerValue(__instance, "distThreshold", value);
        return false;
    }

    private static bool OnSetRefillAmount(ItemToDistribute __instance, float value)
    {
        if (!Client) return true;
        SubmitDistributerValue(__instance, "distAmount", value);
        return false;
    }

    private static void SubmitDistributerValue(ItemToDistribute? item, string kind, float value)
    {
        if (item?.ItemDistributer?.Buildable == null || item.ItemProperties == null || !Replication.Finite(value)) return;
        var id = Network!.Id(item.ItemDistributer.Buildable);
        if (id <= 0) return;

        var key = $"{kind}:{id}:{item.ItemProperties.name}";
        var now = Time.realtimeSinceStartup;
        if (LastSliderSubmitAt.TryGetValue(key, out var prev) && now - prev < 0.08f) return;
        LastSliderSubmitAt[key] = now;

        Network.Submit(new GameCommand
        {
            kind = kind,
            target = id,
            properties = item.ItemProperties.name,
            x = value
        });
    }

    private static bool OnOzoneTrigger(OzoneShield __instance)
    {
        if (!Client) return true;
        if (__instance?.Buildable == null) return false;
        var id = Network!.Id(__instance.Buildable);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "ozoneTrigger",
                target = id
            });
        }
        return false;
    }

    private static bool OnDietSetPriority(Diet __instance, ItemProperties itemProperties, AssignmentPriority priority)
    {
        if (!Client) return true;
        var agent = __instance?.Vital?.Agent;
        if (agent == null || itemProperties == null) return false;
        var id = Network!.Id(agent);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "agentDiet",
                target = id,
                value = (int)__instance!.Vital.VitalType,
                properties = itemProperties.name,
                amount = (int)priority
            });
        }
        return false;
    }

    private static bool OnTryLevelAttribute(DrifterAttributes __instance, DrifterAttributes.AttributeType type, ref bool __result)
    {
        if (!Client) return true;
        __result = false;
        Agent? ownerAgent = null;
        foreach (var comm in Community.Communities)
        {
            if (comm == null) continue;
            ownerAgent = comm.Agents.FirstOrDefault(a => a != null && ReferenceEquals(a.Attributes, __instance));
            if (ownerAgent != null) break;
        }
        if (ownerAgent == null) return false;
        var id = Network!.Id(ownerAgent);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "agentLevelAttr",
                target = id,
                value = (int)type
            });
        }
        return false;
    }

    // =========================================================================
    // CAPTURE & APPLY (BUILDINGS)
    // =========================================================================

    internal static void CaptureBuilding(Buildable building, BuildingState state)
    {
        if (building.TryGetComponent<Storage>(out var storage) && storage.Filter != null && storage.ShowFilterPanel)
        {
            var disabled = storage.Filter.AllItems
                .Where(i => i != null && !storage.Filter.AcceptedItems.Contains(i))
                .Select(i => i.name)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            if ((storage.Filter.AcceptedTags != storage.Filter.Tags) || disabled.Length > 0)
            {
                state.storageFilter = new StorageFilterState
                {
                    acceptedTags = (int)storage.Filter.AcceptedTags,
                    disabledItems = disabled
                };
            }
        }

        if (building.TryGetComponent<AquaFarm>(out var farm))
        {
            state.aquaFarm = new AquaFarmState
            {
                activeFish = farm.ActiveFishProperties != null ? farm.ActiveFishProperties.name : "",
                broodstock = (farm.Broodstock ?? new List<AquaFarm.Fish>())
                    .Where(f => f?.FishProperties != null)
                    .Select(f => new FishEntryState
                    {
                        fish = f.FishProperties.name,
                        consumed = f.Consumed,
                        progress = f.Progress,
                        hungry = f.Hungry
                    }).ToArray(),
                fishes = (farm.Fishes ?? new List<AquaFarm.Fish>())
                    .Where(f => f?.FishProperties != null)
                    .Select(f => new FishEntryState
                    {
                        fish = f.FishProperties.name,
                        consumed = f.Consumed,
                        progress = f.Progress,
                        hungry = f.Hungry
                    }).ToArray()
            };
        }

        if (building.TryGetComponent<ItemDistributer>(out var dist) && dist.ItemsToDistribute != null && dist.ItemsToDistribute.Count > 0)
        {
            state.distributers = dist.ItemsToDistribute
                .Where(d => d?.ItemProperties != null)
                .Select(d => new DistributerItemState
                {
                    item = d.ItemProperties.name,
                    available = d.Available,
                    threshold = d.RefillThreshold,
                    amount = d.RefillAmount
                }).ToArray();
        }
    }

    internal static void ApplyBuilding(
        Buildable building,
        BuildingState state,
        IReadOnlyDictionary<string, ItemProperties> items)
    {
        if (building.TryGetComponent<Storage>(out var storage) && storage.Filter != null && storage.ShowFilterPanel)
        {
            var desiredTags = state.storageFilter != null ? (Item.Tags)state.storageFilter.acceptedTags : storage.Filter.Tags;
            var disabledSet = new HashSet<string>(state.storageFilter?.disabledItems ?? Array.Empty<string>(), StringComparer.Ordinal);
            var changed = false;

            if (storage.Filter.AcceptedTags != desiredTags)
            {
                storage.Filter.RemoveAcceptedTags(storage.Filter.Tags);
                storage.Filter.AddAcceptedTags(desiredTags);
                changed = true;
            }

            foreach (var ip in storage.Filter.AllItems)
            {
                if (ip == null) continue;
                var wantAccepted = !disabledSet.Contains(ip.name) && (ip.Tags & desiredTags) != Item.Tags.None;
                var currentlyAccepted = storage.Filter.AcceptedItems.Contains(ip);
                if (wantAccepted && !currentlyAccepted)
                {
                    storage.Filter.AcceptedItems.Add(ip);
                    changed = true;
                }
                else if (!wantAccepted && currentlyAccepted)
                {
                    storage.Filter.AcceptedItems.Remove(ip);
                    changed = true;
                }
            }

            if (changed)
                storage.Filter.OnUpdated?.Invoke();
        }

        if (state.aquaFarm != null && building.TryGetComponent<AquaFarm>(out var farm))
        {
            EnsureFishCatalog();
            var af = state.aquaFarm;
            if (!string.IsNullOrEmpty(af.activeFish) &&
                FishCatalog.TryGetValue(af.activeFish, out var activeFp) &&
                farm.ActiveFishProperties != activeFp)
            {
                farm.SetActiveFishProperties(activeFp);
            }

            var structuralChange = SyncFishList(farm, farm.Broodstock, af.broodstock, isBrood: true);
            if (SyncFishList(farm, farm.Fishes, af.fishes, isBrood: false))
                structuralChange = true;

            if (structuralChange)
                farm.UpdatedEvent?.Invoke();
        }

        if (state.distributers != null && state.distributers.Length > 0 && building.TryGetComponent<ItemDistributer>(out var dist))
        {
            foreach (var entry in state.distributers)
            {
                if (entry == null || string.IsNullOrEmpty(entry.item) || !items.TryGetValue(entry.item, out var ip))
                    continue;

                var target = dist.ItemsToDistribute.FirstOrDefault(x => x != null && x.ItemProperties == ip);
                if (target == null)
                {
                    target = new ItemToDistribute(dist, ip);
                    dist.ItemsToDistribute.Add(target);
                }

                if (Replication.Finite(entry.available))
                    DistAvailableProp?.SetValue(target, Mathf.Clamp(entry.available, 0f, target.Capacity));
                if (Replication.Finite(entry.threshold) && !Mathf.Approximately(target.RefillThreshold, entry.threshold))
                    target.SetRefillThrehold(entry.threshold);
                if (Replication.Finite(entry.amount) && !Mathf.Approximately(target.RefillAmount, entry.amount))
                    target.SetRefillAmount(entry.amount);
            }
        }
    }

    private static bool SyncFishList(AquaFarm farm, List<AquaFarm.Fish>? current, FishEntryState[]? incoming, bool isBrood)
    {
        if (current == null) return false;
        var wanted = incoming ?? Array.Empty<FishEntryState>();
        if (wanted.Length > 128) return false;

        var shapeMismatch = current.Count != wanted.Length;
        if (!shapeMismatch)
        {
            for (var i = 0; i < wanted.Length; i++)
            {
                if (current[i]?.FishProperties == null || current[i].FishProperties.name != wanted[i].fish)
                {
                    shapeMismatch = true;
                    break;
                }
            }
        }

        if (shapeMismatch)
        {
            current.Clear();
            foreach (var w in wanted)
            {
                if (w == null || !FishCatalog.TryGetValue(w.fish, out var fp)) continue;
                var req = isBrood ? fp.FeedRequirementBrooding : fp.FeedRequirementGrowing;
                var fish = new AquaFarm.Fish(farm, fp, req);
                ApplyFishMetrics(fish, w);
                if (isBrood && AquaAddBroodInstanceMethod != null)
                {
                    try { AquaAddBroodInstanceMethod.Invoke(farm, new object[] { fish }); continue; }
                    catch { /* fallback to direct list add below */ }
                }
                current.Add(fish);
            }
            return true;
        }

        for (var i = 0; i < wanted.Length; i++)
            ApplyFishMetrics(current[i], wanted[i]);
        return false;
    }

    private static void ApplyFishMetrics(AquaFarm.Fish fish, FishEntryState state)
    {
        if (Replication.Finite(state.consumed))
            FishConsumedProp?.SetValue(fish, Mathf.Max(0f, state.consumed));
        if (Replication.Finite(state.progress))
            FishProgressProp?.SetValue(fish, Mathf.Clamp01(state.progress));
        FishHungryProp?.SetValue(fish, state.hungry);
    }

    // =========================================================================
    // CAPTURE & APPLY (AGENTS)
    // =========================================================================

    internal static void CaptureAgent(Agent agent, AgentState state)
    {
        if (agent.Attributes != null)
        {
            state.level = agent.Attributes.Level;
            state.spendablePoints = agent.Attributes.SpendablePoints;
            state.experience = agent.Attributes.Experience;

            var attrs = new List<AttributeState>();
            foreach (DrifterAttributes.AttributeType t in Enum.GetValues(typeof(DrifterAttributes.AttributeType)))
            {
                if (t == DrifterAttributes.AttributeType.None) continue;
                var attr = agent.Attributes.ReturnAttribute(t);
                if (attr != null && attr.Expertise > 0)
                    attrs.Add(new AttributeState { type = (int)t, level = attr.Expertise });
            }
            state.attributes = attrs.ToArray();
        }

        var diets = new List<DietEntryState>();
        CaptureDietEntries(agent.Vitals?.Hunger?.Diet, (int)VitalType.Hunger, diets);
        CaptureDietEntries(agent.Vitals?.Thirst?.Diet, (int)VitalType.Thirst, diets);
        state.diets = diets.ToArray();

        var curDisease = agent.Vitals?.Pollution?.CurrentDisease;
        state.disease = curDisease != null ? StripCloneSuffix(curDisease.name) : "";
    }

    private static void CaptureDietEntries(Diet? diet, int vitalType, List<DietEntryState> output)
    {
        if (diet?.Entries == null) return;
        foreach (var e in diet.Entries)
        {
            if (e?.ItemProperties == null || e.Priority == AssignmentPriority.Lowest) continue;
            output.Add(new DietEntryState
            {
                vital = vitalType,
                item = e.ItemProperties.name,
                priority = (int)e.Priority
            });
        }
    }

    internal static void ApplyAgent(Agent agent, AgentState state)
    {
        if (agent.Attributes != null)
        {
            if (state.level >= 0 && agent.Attributes.Level != state.level)
                AttrLevelProp?.SetValue(agent.Attributes, state.level);
            if (state.spendablePoints >= 0 && agent.Attributes.SpendablePoints != state.spendablePoints)
                AttrPointsProp?.SetValue(agent.Attributes, state.spendablePoints);
            if (Replication.Finite(state.experience) && state.experience >= 0f && !Mathf.Approximately(agent.Attributes.Experience, state.experience))
                AttrExpProp?.SetValue(agent.Attributes, state.experience);

            var wantedAttrs = new Dictionary<int, int>();
            if (state.attributes != null)
            {
                foreach (var a in state.attributes)
                    if (a != null && a.level >= 0 && a.level <= 50)
                        wantedAttrs[a.type] = a.level;
            }
            foreach (DrifterAttributes.AttributeType t in Enum.GetValues(typeof(DrifterAttributes.AttributeType)))
            {
                if (t == DrifterAttributes.AttributeType.None) continue;
                var attr = agent.Attributes.ReturnAttribute(t);
                if (attr == null) continue;
                wantedAttrs.TryGetValue((int)t, out var targetExp);
                if (attr.Expertise != targetExp)
                    attr.Expertise = targetExp;
            }
        }

        var dietChanged = ApplyDietEntries(agent.Vitals?.Hunger?.Diet, (int)VitalType.Hunger, state.diets);
        if (ApplyDietEntries(agent.Vitals?.Thirst?.Diet, (int)VitalType.Thirst, state.diets))
            dietChanged = true;
        if (dietChanged)
            GameEventDispatcher.Dispatch(GameEventType.AgentDietUpdated);

        var pollution = agent.Vitals?.Pollution;
        if (pollution != null)
        {
            var curName = pollution.CurrentDisease != null ? StripCloneSuffix(pollution.CurrentDisease.name) : "";
            var wantName = state.disease ?? "";
            if (curName != wantName)
            {
                if (string.IsNullOrEmpty(wantName))
                {
                    PollutionDiseaseProp?.SetValue(pollution, null);
                    pollution.OnCurrentDiseaseUpdatedEvent?.Invoke(null);
                }
                else
                {
                    var template = agent.Vitals?.Properties?.Diseases?.FirstOrDefault(d => d != null && StripCloneSuffix(d.name) == wantName);
                    if (template != null)
                    {
                        var inst = template.CreateInstance();
                        PollutionDiseaseProp?.SetValue(pollution, inst);
                        pollution.OnCurrentDiseaseUpdatedEvent?.Invoke(inst);
                    }
                }
            }
        }
    }

    private static bool ApplyDietEntries(Diet? diet, int vitalType, DietEntryState[]? incoming)
    {
        if (diet?.Entries == null) return false;
        var map = new Dictionary<string, AssignmentPriority>(StringComparer.Ordinal);
        if (incoming != null)
        {
            foreach (var d in incoming)
            {
                if (d != null && d.vital == vitalType && !string.IsNullOrEmpty(d.item) && Enum.IsDefined(typeof(AssignmentPriority), d.priority))
                    map[d.item] = (AssignmentPriority)d.priority;
            }
        }

        var changed = false;
        foreach (var entry in diet.Entries)
        {
            if (entry?.ItemProperties == null) continue;
            var targetPrio = map.TryGetValue(entry.ItemProperties.name, out var found) ? found : AssignmentPriority.Lowest;
            if (entry.SetPriority(targetPrio))
                changed = true;
        }
        return changed;
    }

    private static string StripCloneSuffix(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        const string suffix = "(Clone)";
        return name!.EndsWith(suffix, StringComparison.Ordinal) ? name.Substring(0, name.Length - suffix.Length) : name;
    }

    // =========================================================================
    // HOST AUTHORITATIVE EXECUTION
    // =========================================================================

    internal static string? ExecuteBuilding(
        GameCommand command,
        IReadOnlyDictionary<long, Buildable> buildings,
        IReadOnlyDictionary<string, ItemProperties> items,
        Community targetCommunity)
    {
        if (!buildings.TryGetValue(command.target, out var building) || building == null)
            return "目标建筑已不存在";

        if (command.kind == "moveBoat")
        {
            if (building.Community != targetCommunity || !building.TryGetComponent<Boat>(out var boat))
                return "只能重新系泊己方小船";
            if(FleetInteractions.IsBoatBusy(building))return "请等待出航小艇返航后重新系泊";
            if (!buildings.TryGetValue(command.reference, out var dockBuilding) || dockBuilding == null ||
                dockBuilding.Community != targetCommunity || !dockBuilding.TryGetComponent<MooringPoint>(out var dock))
                return "请选择我方船上的有效码头";
            if (!dock.IsAvailableForMooring || dock.LinkedBoat != null)
                return "目标码头已有小船停靠";

            using var _scope = FleetRuntime.WithPlayerCommunity(targetCommunity);
            if (boat.TownMooringPoint != null)
                boat.TownMooringPoint.UnlinkBoat(boat);
            dock.LinkBoat(boat);
            targetCommunity.QueueProject(new Project(GameManager.Settings.ProjectSettings.MooringProperties, boat.gameObject));
            return null;
        }

        if (building.Community != targetCommunity)
        {
            if (building.Community != targetCommunity)
                return "不能操作其他船只的建筑面板";
            targetCommunity = building.Community;
        }

        using var _commScope = FleetRuntime.WithPlayerCommunity(targetCommunity);

        switch (command.kind)
        {
            case "storageTag":
            {
                if (!building.TryGetComponent<Storage>(out var storage) || storage.Filter == null)
                    return "建筑没有储物筛选器";
                var tags = (Item.Tags)command.value;
                if (command.amount != 0) storage.Filter.AddAcceptedTags(tags);
                else storage.Filter.RemoveAcceptedTags(tags);
                return null;
            }
            case "storageItem":
            {
                if (!building.TryGetComponent<Storage>(out var storage) || storage.Filter == null)
                    return "建筑没有储物筛选器";
                if (!items.TryGetValue(command.properties, out var ip))
                    return "筛选项物品不存在";
                if (command.amount != 0) storage.Filter.AddAcceptedItem(ip);
                else storage.Filter.RemoveAcceptedItem(ip);
                return null;
            }
            case "storageBatch":
            {
                if (!building.TryGetComponent<Storage>(out var storage) || storage.Filter == null)
                    return "建筑没有储物筛选器";
                var enable = command.amount != 0;
                foreach (var ip in storage.Filter.AllItems.ToArray())
                {
                    if (ip == null || !storage.AcceptsTags(ip.Tags)) continue;
                    if (enable) storage.Filter.AddAcceptedItem(ip);
                    else storage.Filter.RemoveAcceptedItem(ip);
                }
                return null;
            }
            case "storagePaste":
            {
                if (!building.TryGetComponent<Storage>(out var storage) || storage.Filter == null)
                    return "建筑没有储物筛选器";
                var desiredTags = (Item.Tags)command.value & storage.Filter.Tags;
                var disabled = new HashSet<string>(
                    string.IsNullOrEmpty(command.text) ? Array.Empty<string>() : command.text.Split(','),
                    StringComparer.Ordinal);
                storage.Filter.RemoveAcceptedTags(storage.Filter.Tags);
                storage.Filter.AddAcceptedTags(desiredTags);
                foreach (var ip in storage.Filter.AllItems.ToArray())
                {
                    if (ip == null) continue;
                    if (!disabled.Contains(ip.name) && (ip.Tags & desiredTags) != Item.Tags.None)
                        storage.Filter.AddAcceptedItem(ip);
                    else
                        storage.Filter.RemoveAcceptedItem(ip);
                }
                return null;
            }
            case "aquaActive":
            {
                EnsureFishCatalog();
                if (!building.TryGetComponent<AquaFarm>(out var farm))
                    return "目标建筑不是养鱼场";
                if (!FishCatalog.TryGetValue(command.properties, out var fp))
                    return "鱼类品种不存在";
                farm.SetActiveFishProperties(fp);
                return null;
            }
            case "aquaAddBrood":
            {
                EnsureFishCatalog();
                if (!building.TryGetComponent<AquaFarm>(out var farm))
                    return "目标建筑不是养鱼场";
                if (!string.IsNullOrEmpty(command.properties) && FishCatalog.TryGetValue(command.properties, out var fp))
                    farm.SetActiveFishProperties(fp);
                if (!farm.AddBroodFish())
                    return "种鱼库存不足或鱼池已满";
                return null;
            }
            case "aquaRemoveBrood":
            {
                EnsureFishCatalog();
                if (!building.TryGetComponent<AquaFarm>(out var farm))
                    return "目标建筑不是养鱼场";
                if (!string.IsNullOrEmpty(command.properties) && FishCatalog.TryGetValue(command.properties, out var fp))
                    farm.SetActiveFishProperties(fp);
                farm.RemoveBroodFish();
                return null;
            }
            case "distThreshold":
            case "distAmount":
            {
                if (!building.TryGetComponent<ItemDistributer>(out var dist))
                    return "建筑不支持物资分配设置";
                if (!items.TryGetValue(command.properties, out var ip))
                    return "分配物资不存在";
                var entry = dist.ItemsToDistribute.FirstOrDefault(x => x != null && x.ItemProperties == ip);
                if (entry == null) return "该分配条目尚未初始化";
                if (command.kind == "distThreshold") entry.SetRefillThrehold(command.x);
                else entry.SetRefillAmount(command.x);
                return null;
            }
            case "ozoneTrigger":
            {
                if (!building.TryGetComponent<OzoneShield>(out var shield))
                    return "目标建筑不是臭氧护盾";
                if (!shield.IsInteractable())
                    return "臭氧护盾尚未接入运行中的聚变反应堆或条件未满足";
                shield.Trigger();
                return null;
            }
            default:
                return "未知专项建筑指令";
        }
    }

    internal static string? ExecuteAgent(
        GameCommand command,
        IReadOnlyDictionary<long, Agent> agents,
        IReadOnlyDictionary<string, ItemProperties> items,
        Community targetCommunity)
    {
        if (!agents.TryGetValue(command.target, out var agent) || agent == null || agent.Community != targetCommunity)
            return "居民已不存在或不属于你的船只";

        using var _scope = FleetRuntime.WithPlayerCommunity(targetCommunity);

        if (command.kind == "agentDiet")
        {
            if (!items.TryGetValue(command.properties, out var ip))
                return "饮食条目不存在";
            if (!Enum.IsDefined(typeof(AssignmentPriority), command.amount))
                return "饮食优先级无效";
            var diet = command.value == (int)VitalType.Thirst
                ? agent.Vitals?.Thirst?.Diet
                : agent.Vitals?.Hunger?.Diet;
            if (diet == null) return "居民饮食组件未就绪";
            diet.SetPriority(ip, (AssignmentPriority)command.amount);
            return null;
        }

        if (command.kind == "agentLevelAttr")
        {
            if (!Enum.IsDefined(typeof(DrifterAttributes.AttributeType), command.value))
                return "专精属性类型无效";
            if (agent.Attributes == null || !agent.Attributes.TryLevelAttribute((DrifterAttributes.AttributeType)command.value))
                return "可用专精点数不足或该专精已达上限";
            return null;
        }

        return "未知居民指令";
    }
}
