extern alias Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Debris = Game::Flotsam;

namespace Flotsam.Multiplayer;

/// <summary>
/// Spawns genuine pooled ocean <see cref="Debris"/> (<see cref="FlotsamPool"/> + <see cref="CompositionInventory"/>):
/// - Starter flotsam rings around newly spawned independent player ships
/// - Landmark ruin scavenging completion discoveries (Salvaged Skiff &amp; Mooring Kit, Scrap Ammo Crate, or 1200m Old Sea Chart)
/// </summary>
internal static class SeaLootAndLandmarks
{
    private static readonly HashSet<string> RewardedLandmarkActions = new HashSet<string>(StringComparer.Ordinal);
    private static readonly FieldInfo? StoredBuildablesField = AccessTools.Field(typeof(Community), "_storedBuildables");
    private static FlotsamProperties? _cachedFlotsamProps;

    internal static void Reset()
    {
        RewardedLandmarkActions.Clear();
        _cachedFlotsamProps = null;
    }

    private static FlotsamProperties? ResolveCrateFlotsamProperties()
    {
        if (_cachedFlotsamProps != null && _cachedFlotsamProps.FlotsamPrefab != null)
            return _cachedFlotsamProps;

        var all = Resources.FindObjectsOfTypeAll<FlotsamProperties>();
        _cachedFlotsamProps = all.FirstOrDefault(p =>
                                  p != null && !p.Static && p.FlotsamPrefab != null &&
                                  (p.name.IndexOf("Wood", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   p.name.IndexOf("Crate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   p.name.IndexOf("Plastic", StringComparison.OrdinalIgnoreCase) >= 0))
                              ?? all.FirstOrDefault(p => p != null && !p.Static && p.FlotsamPrefab != null);
        return _cachedFlotsamProps;
    }

    internal static Debris? SpawnCustomFlotsam(Vector3 scenePos, IReadOnlyList<(ItemProperties Prop, int Amount)> items)
    {
        if (GameManager.WorldManager == null || items == null || items.Count == 0)
            return null;

        var flotsamProps = ResolveCrateFlotsamProperties();
        if (flotsamProps == null) return null;

        var counted = new List<CountedItemProperty>();
        foreach (var (prop, amt) in items)
        {
            if (prop != null && amt > 0)
                counted.Add(new CountedItemProperty(prop, amt));
        }
        if (counted.Count == 0) return null;

        var worldPos = new Vector3(scenePos.x, 0f, scenePos.z);
        var visIdx = FlotsamPool.Instance.Aquire(out var behaviour, flotsamProps, worldPos, interactable: true);
        if (behaviour is not Debris flotsam || flotsam == null)
            return null;

        var comp = new CompositionInventory(counted);
        comp.Fill(flotsam.Inventory);
        flotsam.InitializeComposition(comp);
        var rot = visIdx >= 0 ? flotsamProps.ReturnVisualPrefabRotation(visIdx) : Quaternion.identity;
        flotsam.UpdatePositionAndRotation(worldPos, rot);
        return flotsam;
    }

    internal static void SpawnStarterDebrisRing(Vector3 shipCenterScenePos)
    {
        var allItems = Resources.FindObjectsOfTypeAll<ItemProperties>();
        var wetWood = allItems.FirstOrDefault(p => p != null && p.name == "WetWood");
        var plastic = allItems.FirstOrDefault(p => p != null && p.name == "Plastic");
        var scrap = allItems.FirstOrDefault(p => p != null && (p.name == "ScrapMetal" || p.name == "Metal"));
        if (wetWood == null && plastic == null) return;

        for (var i = 0; i < 6; i++)
        {
            var angle = i * (Mathf.PI * 2f / 6f) + 0.35f;
            var dist = 26f + (i % 3) * 8f;
            var pos = shipCenterScenePos + new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);
            var contents = new List<(ItemProperties, int)>();
            if (wetWood != null) contents.Add((wetWood, 5));
            if (plastic != null) contents.Add((plastic, 5));
            if (i % 2 == 0 && scrap != null) contents.Add((scrap, 2));
            SpawnCustomFlotsam(pos, contents);
        }
    }

    internal static void CheckLandmarkDiscoveryOnComplete(
        LandmarkAction action,
        Vector2 mapPos,
        int actionIndex,
        Community? owner)
    {
        if (Plugin.Options.role != "host" || action == null || action.State != ILandmarkActionStates.Completed)
            return;

        var key = $"{Mathf.RoundToInt(mapPos.x)}:{Mathf.RoundToInt(mapPos.y)}:{actionIndex}";
        if (!RewardedLandmarkActions.Add(key))
            return;

        var recipient = owner ?? Community.PlayerCommunity;
        if (recipient == null) return;

        var hasBoat = FleetInteractions.ShipHasOperationalBoat(recipient);
        var roll = UnityEngine.Random.value;

        // Priority 1: If this ship doesn't own a Boat yet, grant a salvaged Skiff & Mooring Point kit!
        if (!hasBoat || roll < 0.34f)
        {
            if (TryGrantSalvagedBoatKit(recipient))
                return;
        }

        // Priority 2: Scrap Cannon Shell & Tech Crate
        if (roll < 0.68f)
        {
            var allItems = Resources.FindObjectsOfTypeAll<ItemProperties>();
            var scrap = allItems.FirstOrDefault(p => p != null && (p.name == "ScrapMetal" || p.name == "Metal"));
            var elec = allItems.FirstOrDefault(p => p != null && (p.name == "Electronics" || p.name.IndexOf("Circuit", StringComparison.OrdinalIgnoreCase) >= 0));
            for (var i = 0; i < 6; i++)
                if (scrap != null) recipient.SpawnItemToAvailableStorage(scrap);
            for (var i = 0; i < 2; i++)
                if (elec != null) recipient.SpawnItemToAvailableStorage(elec);

            var heartPos = FleetEngineBundle.HeartBuildable(recipient)?.transform.position ?? Construction.TownheartPosition;
            FleetPhysicalActors.RecordAndSpawn(
                "lootNotice",
                $"{FleetVisibility.ResolveOwnerDisplayName(recipient)} 在废墟搜出【废铁炮弹与元件箱】(废铁×6、元件×2)！",
                heartPos,
                heartPos,
                sourceShip: Replication.ShipKey(recipient),
                targetShip: Replication.ShipKey(recipient));
            return;
        }

        // Priority 3: Old Sea Chart revealing 1200m surrounding Fog of War!
        FleetVisibility.RevealChartAreaForShip(recipient, mapPos, 1200f);
    }

    private static bool TryGrantSalvagedBoatKit(Community recipient)
    {
        var allProps = Resources.FindObjectsOfTypeAll<BuildableProperties>();
        var mooringProp = allProps.FirstOrDefault(p => p != null && p.Prefab != null && p.Prefab.GetComponent<MooringPoint>() != null);
        var boatProp = allProps.FirstOrDefault(p => p != null && p.Prefab != null && p.Prefab.GetComponent<Boat>() != null && p.name.IndexOf("Salvage", StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? allProps.FirstOrDefault(p => p != null && p.Prefab != null && p.Prefab.GetComponent<Boat>() != null);

        if (mooringProp == null || boatProp == null)
            return false;

        if (recipient.Research != null)
        {
            if (!recipient.Research.ResearchedBuildableProperties.Contains(mooringProp))
                recipient.Research.UnlockBuildable(mooringProp);
            if (!recipient.Research.ResearchedBuildableProperties.Contains(boatProp))
                recipient.Research.UnlockBuildable(boatProp);
        }

        // Deposit a pre-built MooringPoint directly into recipient's StoredBuildables + full SalvageBoat materials!
        var storedMooring = false;
        if (StoredBuildablesField?.GetValue(recipient) is Dictionary<BuildableProperties, List<Buildable>> storedMap &&
            mooringProp.Prefab != null)
        {
            var heartPos = FleetEngineBundle.HeartBuildable(recipient)?.transform.position ?? Construction.TownheartPosition;
            var inst = UnityEngine.Object.Instantiate(mooringProp.Prefab, heartPos + Vector3.back * 45f, Quaternion.identity);
            inst.Initialize(recipient, 0, null, false);
            inst.FinishBuilding(restored: true);
            inst.Deactivate();
            if (inst.TryGetComponent<Construction>(out var c) && recipient.Constructions.Contains(c))
                c.Remove();
            if (!storedMap.TryGetValue(mooringProp, out var list))
            {
                list = new List<Buildable>();
                storedMap[mooringProp] = list;
            }
            list.Add(inst);
            inst.gameObject.SetActive(false);
            recipient.OnStoredBuildableAdded.Invoke(mooringProp, false);
            storedMooring = true;
        }

        if (boatProp.RequiredResources != null)
        {
            foreach (var r in boatProp.RequiredResources)
            {
                if (r?.ItemProperties == null) continue;
                for (var i = 0; i < r.Amount; i++)
                    recipient.SpawnItemToAvailableStorage(r.ItemProperties);
            }
        }
        if (!storedMooring && mooringProp.RequiredResources != null)
        {
            foreach (var r in mooringProp.RequiredResources)
            {
                if (r?.ItemProperties == null) continue;
                for (var i = 0; i < r.Amount; i++)
                    recipient.SpawnItemToAvailableStorage(r.ItemProperties);
            }
        }

        var shipPos = FleetEngineBundle.HeartBuildable(recipient)?.transform.position ?? Construction.TownheartPosition;
        FleetPhysicalActors.RecordAndSpawn(
            "lootNotice",
            $"{FleetVisibility.ResolveOwnerDisplayName(recipient)} 从岛屿废墟修复了【二手打捞艇与系泊桩套件】(已存入建筑库存)！",
            shipPos,
            shipPos,
            sourceShip: Replication.ShipKey(recipient),
            targetShip: Replication.ShipKey(recipient));
        return true;
    }
}
