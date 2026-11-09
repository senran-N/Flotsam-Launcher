using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Widens world spawn and retention radii to cover all active secondary ships.
/// </summary>
public static class FleetUnionTiles
{
    private const float SpawnRetention = 250f;
    private const float DestructionRetention = 60f;

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(WorldManager), nameof(WorldManager.IsInSpawnRadius)),
            postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(SpawnRadius)));
        harmony.Patch(AccessTools.Method(typeof(WorldManager), nameof(WorldManager.IsOutsideDestructionRadius)),
            postfix: new HarmonyMethod(typeof(FleetUnionTiles), nameof(DestructionRadius)));
    }

    private static void SpawnRadius(Vector3 position, ref bool __result)
    {
        if (__result || !FleetRuntime.Active) return;
        foreach (var owner in FleetEngineBundle.SecondaryCommunities)
        {
            var heart = FleetEngineBundle.HeartBuildable(owner);
            if (heart != null && Vector3.Distance(position, heart.transform.position) <= SpawnRetention)
            {
                __result = true;
                return;
            }
        }
    }

    private static void DestructionRadius(Vector3 position, ref bool __result)
    {
        if (!__result || !FleetRuntime.Active) return;
        foreach (var owner in FleetEngineBundle.SecondaryCommunities)
        {
            var heart = FleetEngineBundle.HeartBuildable(owner);
            if (heart != null && Vector3.Distance(position, heart.transform.position) <= DestructionRetention)
            {
                __result = false;
                return;
            }
        }
    }
}
