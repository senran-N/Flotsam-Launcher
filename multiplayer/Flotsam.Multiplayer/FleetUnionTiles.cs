using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Widens world spawn, POI interactability, salvaging, camera roaming, and tile retention radii
/// across all active ships in the fleet, and keeps range highlighters centered on the local ship.
/// </summary>
public static class FleetUnionTiles
{
    private static bool _installed;
    private static readonly FieldInfo? ConstructionBorderField = AccessTools.Field(typeof(WorldManager), "_constructionRadiusBorder");
    private static readonly FieldInfo? SwimmingHighlighterField = AccessTools.Field(typeof(WorldManager), "_swimmingRangeHighlighter");
    private static readonly FieldInfo? BoatHighlighterField = AccessTools.Field(typeof(WorldManager), "_boatRangeHighlighter");
    private static readonly FieldInfo? HighlighterMaterialField = AccessTools.Field(typeof(CircleWaveHighlighter), "_material");

    public static void Install(Harmony harmony)
    {
        if (_installed) return;
        harmony.Patch(AccessTools.Method(typeof(WorldManager), nameof(WorldManager.IsInSpawnRadius)),
            postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(SpawnRadius)));
        harmony.Patch(AccessTools.Method(typeof(WorldManager), nameof(WorldManager.IsOutsideDestructionRadius)),
            postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(DestructionRadius)));
        harmony.Patch(AccessTools.Method(typeof(WorldManager), nameof(WorldManager.IsInteractable)),
            postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(InteractableRadius)));
        harmony.Patch(AccessTools.Method(typeof(WorldManager), nameof(WorldManager.IsInInteractionRadius)),
            postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(CameraAndInteractionRadius)));
        harmony.Patch(AccessTools.Method(typeof(WorldManager), nameof(WorldManager.IsInSwimmingRadius)),
            postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(SwimmingRadius)));
        harmony.Patch(AccessTools.Method(typeof(WorldManager), nameof(WorldManager.IsInBoatRadius)),
            postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(BoatRadius)));

        var canPrune = AccessTools.Method(typeof(WorldManager), nameof(WorldManager.CanPruneWorldTile));
        if (canPrune != null)
            harmony.Patch(canPrune, postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(CanPruneTile)));

        var markerRadius = AccessTools.DeclaredMethod(typeof(Marker), "ReturnIsInWorldManagerRadius", new[] { typeof(Vector3) });
        if (markerRadius != null)
            harmony.Patch(markerRadius, postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(MarkerWorldRadius)));

        var swimMarkerRadius = AccessTools.DeclaredMethod(typeof(SwimmingMarker), "ReturnIsInWorldManagerRadius", new[] { typeof(Vector3) });
        if (swimMarkerRadius != null)
            harmony.Patch(swimMarkerRadius, postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(SwimmingMarkerWorldRadius)));

        var showBuild = AccessTools.Method(typeof(WorldManager), nameof(WorldManager.ShowConstructionBorder));
        if (showBuild != null)
            harmony.Patch(showBuild, postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(AlignConstructionBorder)));

        var showSwim = AccessTools.Method(typeof(WorldManager), nameof(WorldManager.ShowSwimmingRange));
        if (showSwim != null)
            harmony.Patch(showSwim, postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(AlignSwimmingHighlighter)));

        var showBoat = AccessTools.Method(typeof(WorldManager), nameof(WorldManager.ShowBoatRange));
        if (showBoat != null)
            harmony.Patch(showBoat, postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(AlignBoatHighlighter)));

        _installed = true;
    }

    private static bool WithinXZ(Vector3 a, Vector3 b, float radius)
    {
        var dx = a.x - b.x;
        var dz = a.z - b.z;
        return dx * dx + dz * dz <= radius * radius;
    }

    private static void SpawnRadius(Vector3 position, ref bool __result)
    {
        if (__result) return;
        var mapRadius = GameSettings.Instance?.GameplaySettings != null ? GameSettings.Instance.GameplaySettings.MapRadius : 250f;
        var r = Mathf.Max(450f, mapRadius + 250f);
        if (WithinXZ(position, Construction.TownheartPosition, r)) { __result = true; return; }
        var net = Plugin.Instance?.Network;
        if (net != null)
        {
            foreach (var ship in net.Ships)
            {
                if (ship != null && WithinXZ(position, new Vector3(ship.x, 0f, ship.z), r))
                {
                    __result = true;
                    return;
                }
            }
        }
        if (!FleetRuntime.Active) return;
        foreach (var owner in FleetEngineBundle.SecondaryCommunities)
        {
            var heart = FleetEngineBundle.HeartBuildable(owner);
            if (heart != null && WithinXZ(position, heart.transform.position, r))
            {
                __result = true;
                return;
            }
        }
    }

    private static void DestructionRadius(Vector3 position, ref bool __result)
    {
        if (!__result) return;
        var r = Mathf.Max(260f, GameSettings.Instance?.GameplaySettings?.DestructionRadius ?? 300f);
        if (WithinXZ(position, Construction.TownheartPosition, r)) { __result = false; return; }
        var net = Plugin.Instance?.Network;
        if (net != null)
        {
            foreach (var ship in net.Ships)
            {
                if (ship != null && WithinXZ(position, new Vector3(ship.x, 0f, ship.z), r))
                {
                    __result = false;
                    return;
                }
            }
        }
        if (!FleetRuntime.Active) return;
        foreach (var owner in FleetEngineBundle.SecondaryCommunities)
        {
            var heart = FleetEngineBundle.HeartBuildable(owner);
            if (heart != null && WithinXZ(position, heart.transform.position, r))
            {
                __result = false;
                return;
            }
        }
    }

    private static void InteractableRadius(Vector3 position, ref bool __result)
    {
        if (__result) return;
        var r = Mathf.Max(180f, GameManager.WorldManager != null ? GameManager.WorldManager.InteractableRadius : 200f);
        if (WithinXZ(position, Construction.TownheartPosition, r)) { __result = true; return; }
        var net = Plugin.Instance?.Network;
        if (net != null)
        {
            foreach (var ship in net.Ships)
            {
                if (ship != null && WithinXZ(position, new Vector3(ship.x, 0f, ship.z), r))
                {
                    __result = true;
                    return;
                }
            }
        }
        if (!FleetRuntime.Active) return;
        foreach (var owner in FleetEngineBundle.SecondaryCommunities)
        {
            var heart = FleetEngineBundle.HeartBuildable(owner);
            if (heart != null && WithinXZ(position, heart.transform.position, r))
            {
                __result = true;
                return;
            }
        }
    }

    private static void CameraAndInteractionRadius(Vector3 position, ref bool __result)
    {
        if (__result) return;
        var r = Mathf.Max(180f, GameSettings.Instance?.GameplaySettings?.InteractionRadius ?? 200f);
        if (WithinXZ(position, Construction.TownheartPosition, r)) { __result = true; return; }

        var net = Plugin.Instance?.Network;
        var independent = Plugin.Options.mode == "independent";

        // Allow camera inspection around secondary ships only when spotted (or in shared mode)
        if (FleetRuntime.Active)
        {
            foreach (var owner in FleetEngineBundle.SecondaryCommunities)
            {
                if (owner == null) continue;
                if (independent && net != null)
                {
                    var key = Replication.ShipKey(owner);
                    var shipState = System.Array.Find(net.Ships, s => s != null && s.name == key);
                    if (shipState != null && !FleetVisibility.IsShipVisibleToLocal(shipState, net.Ships))
                        continue;
                }
                var heart = FleetEngineBundle.HeartBuildable(owner);
                if (heart != null && WithinXZ(position, heart.transform.position, r))
                {
                    __result = true;
                    return;
                }
            }
        }

        // Allow camera inspection around any spotted replicated fleet ship or active co-op sea beacon
        if (net != null)
        {
            foreach (var ship in net.Ships)
            {
                if (ship == null) continue;
                if (independent && !FleetVisibility.IsShipVisibleToLocal(ship, net.Ships))
                    continue;
                if (WithinXZ(position, new Vector3(ship.x, 0f, ship.z), r))
                {
                    __result = true;
                    return;
                }
            }
        }

        if (FleetPhysicalActors.IsNearActiveBeacon(position, r * 0.75f))
            __result = true;
    }

    private static void SwimmingRadius(Vector3 position, ref bool __result)
    {
        var r = GameSettings.Instance?.GameplaySettings?.SwimmingRadius ?? 85f;
        var net = Plugin.Instance?.Network;
        // On a client, native WorldManager checks (0,0,0) (the stationary ocean anchor) instead of the sailing Townheart
        if (net?.IsClient == true)
        {
            if (WithinXZ(position, Construction.TownheartPosition, r))
            {
                __result = true;
                return;
            }
            var localKey = Replication.ShipKey(Community.PlayerCommunity);
            var localShip = System.Array.Find(net.Ships, s => s != null && s.name == localKey);
            foreach (var ship in net.Ships)
            {
                if (ship != null && FleetVisibility.AreAlliedState(localShip, ship) && WithinXZ(position, new Vector3(ship.x, 0f, ship.z), r))
                {
                    __result = true;
                    return;
                }
            }
            __result = false;
            return;
        }
        if (__result || !FleetRuntime.Active) return;
        foreach (var owner in FleetEngineBundle.SecondaryCommunities)
        {
            var heart = FleetEngineBundle.HeartBuildable(owner);
            if (heart != null && WithinXZ(position, heart.transform.position, r))
            {
                __result = true;
                return;
            }
        }
    }

    private static void BoatRadius(Vector3 position, ref bool __result)
    {
        var r = GameSettings.Instance?.GameplaySettings?.InteractionRadius ?? 200f;
        var net = Plugin.Instance?.Network;
        if (net?.IsClient == true)
        {
            if (WithinXZ(position, Construction.TownheartPosition, r))
            {
                __result = true;
                return;
            }
            var localKey = Replication.ShipKey(Community.PlayerCommunity);
            var localShip = System.Array.Find(net.Ships, s => s != null && s.name == localKey);
            foreach (var ship in net.Ships)
            {
                if (ship != null && FleetVisibility.AreAlliedState(localShip, ship) && WithinXZ(position, new Vector3(ship.x, 0f, ship.z), r))
                {
                    __result = true;
                    return;
                }
            }
            __result = false;
            return;
        }
        if (__result || !FleetRuntime.Active) return;
        foreach (var owner in FleetEngineBundle.SecondaryCommunities)
        {
            var heart = FleetEngineBundle.HeartBuildable(owner);
            if (heart != null && WithinXZ(position, heart.transform.position, r))
            {
                __result = true;
                return;
            }
        }
    }

    private static void MarkerWorldRadius(Marker __instance, Vector3 position, ref bool __result)
    {
        if (__instance == null || !FleetRuntime.Active) return;
        var owner = __instance.Community;
        if (owner == null) return;
        var heartPos = FleetEngineBundle.HeartBuildable(owner)?.transform.position ?? Construction.TownheartPosition;
        var r = __instance is SwimmingMarker
            ? (GameSettings.Instance?.GameplaySettings?.SwimmingRadius ?? 85f)
            : (GameSettings.Instance?.GameplaySettings?.InteractionRadius ?? 200f);
        __result = WithinXZ(position, heartPos, r);
    }

    private static void SwimmingMarkerWorldRadius(SwimmingMarker __instance, Vector3 position, ref bool __result)
    {
        if (__instance == null || !FleetRuntime.Active) return;
        var owner = __instance.Community;
        if (owner == null) return;
        var heartPos = FleetEngineBundle.HeartBuildable(owner)?.transform.position ?? Construction.TownheartPosition;
        var r = GameSettings.Instance?.GameplaySettings?.SwimmingRadius ?? 85f;
        __result = WithinXZ(position, heartPos, r);
    }

    private static void CanPruneTile(WorldTile worldTile, ref bool __result)
    {
        if (!__result || worldTile == null) return;
        var bounds = worldTile.WorldBounds;
        const float margin = 1500f;
        var localMap = FleetOwnedNavigation.SceneToMap(Construction.TownheartPosition);
        if (localMap.x >= bounds.xMin - margin && localMap.x <= bounds.xMax + margin &&
            localMap.z >= bounds.yMin - margin && localMap.z <= bounds.yMax + margin)
        {
            __result = false;
            return;
        }
        var net = Plugin.Instance?.Network;
        if (net != null)
        {
            foreach (var s in net.Ships)
            {
                if (s == null) continue;
                if (s.mapX >= bounds.xMin - margin && s.mapX <= bounds.xMax + margin &&
                    s.mapZ >= bounds.yMin - margin && s.mapZ <= bounds.yMax + margin)
                {
                    __result = false;
                    return;
                }
            }
        }
        if (!FleetRuntime.Active) return;
        foreach (var owner in FleetEngineBundle.SecondaryCommunities)
        {
            var heart = FleetEngineBundle.HeartBuildable(owner);
            if (heart == null) continue;
            var mapPos = FleetOwnedNavigation.SceneToMap(heart.transform.position);
            if (mapPos.x >= bounds.xMin - margin && mapPos.x <= bounds.xMax + margin &&
                mapPos.z >= bounds.yMin - margin && mapPos.z <= bounds.yMax + margin)
            {
                __result = false;
                return;
            }
        }
    }

    internal static void RefreshActiveHighlighters()
    {
        var wm = GameManager.WorldManager;
        if (wm == null) return;
        if (ConstructionBorderField?.GetValue(wm) is Component comp && comp != null && comp.gameObject.activeSelf)
            AlignConstructionBorder(wm);
        else if (ConstructionBorderField?.GetValue(wm) is GameObject go && go != null && go.activeSelf)
            AlignConstructionBorder(wm);
        if (SwimmingHighlighterField?.GetValue(wm) is CircleWaveHighlighter swim && swim != null && swim.gameObject.activeSelf)
            AlignWaveHighlighter(wm, SwimmingHighlighterField);
        if (BoatHighlighterField?.GetValue(wm) is CircleWaveHighlighter boat && boat != null && boat.gameObject.activeSelf)
            AlignWaveHighlighter(wm, BoatHighlighterField);
    }

    private static void AlignConstructionBorder(WorldManager __instance)
    {
        if (__instance == null) return;
        var center = Construction.TownheartPosition;
        if (ConstructionBorderField?.GetValue(__instance) is Component comp && comp != null)
            comp.transform.position = new Vector3(center.x, comp.transform.position.y, center.z);
        else if (ConstructionBorderField?.GetValue(__instance) is GameObject go && go != null)
            go.transform.position = new Vector3(center.x, go.transform.position.y, center.z);
    }

    private static void AlignSwimmingHighlighter(WorldManager __instance) => AlignWaveHighlighter(__instance, SwimmingHighlighterField);
    private static void AlignBoatHighlighter(WorldManager __instance) => AlignWaveHighlighter(__instance, BoatHighlighterField);

    private static void AlignWaveHighlighter(WorldManager? manager, FieldInfo? field)
    {
        if (manager == null || field == null) return;
        if (field.GetValue(manager) is not CircleWaveHighlighter highlighter || highlighter == null) return;
        var center = Construction.TownheartPosition;
        highlighter.transform.position = new Vector3(center.x, highlighter.transform.position.y, center.z);
        if (HighlighterMaterialField?.GetValue(highlighter) is Material mat && mat != null)
            mat.SetVector("_SwimmingCenter", center);
    }
}
