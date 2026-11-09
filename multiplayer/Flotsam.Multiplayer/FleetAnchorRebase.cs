using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Keeps all secondary ships stationary in canonical world space when the primary ship moves.
/// </summary>
public static class FleetAnchorRebase
{
    private static Vector3 _oldPosition;
    private static Quaternion _oldRotation;
    private static bool _captured;

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(World), nameof(World.RepositionTownheart)),
            prefix: new HarmonyMethod(typeof(FleetAnchorRebase), nameof(BeforeAnchorMove)),
            postfix: new HarmonyMethod(typeof(FleetAnchorRebase), nameof(AfterAnchorMove)));
    }

    private static void BeforeAnchorMove()
    {
        if (!FleetRuntime.Active) return;
        var world = GameManager.WorldManager?.World;
        if (world == null) return;
        _oldPosition = world.TownheartWorldPosition;
        _oldRotation = world.TownheartRotation;
        _captured = true;
    }

    private static void AfterAnchorMove()
    {
        if (!_captured) return;
        _captured = false;
        if (!FleetRuntime.Active) return;
        var world = GameManager.WorldManager?.World;
        if (world == null) return;
        var newPosition = world.TownheartWorldPosition;
        var newRotation = world.TownheartRotation;
        if (Vector3.Distance(_oldPosition, newPosition) < 0.0001f && Quaternion.Angle(_oldRotation, newRotation) < 0.01f) return;

        var inverseNew = Quaternion.Inverse(newRotation);
        foreach (var owner in FleetEngineBundle.SecondaryCommunities.ToArray())
        {
            foreach (var transform in FleetKinematics.TopLevelTransforms(owner))
            {
                var canonical = _oldPosition + _oldRotation * transform.position;
                transform.position = inverseNew * (canonical - newPosition);
                transform.rotation = inverseNew * _oldRotation * transform.rotation;
            }
            FleetKinematics.RefreshCaches(owner);
            FleetKinematics.RefreshOwnedNodePositions(owner);
            FleetPathRefresh.Refresh(owner);
        }
        FleetOwnedNavigation.RebaseAnchor(_oldPosition, _oldRotation, newPosition, newRotation);
    }
}
