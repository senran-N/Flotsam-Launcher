extern alias Game;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Debris = Game::Flotsam;

namespace Flotsam.Multiplayer;

/// <summary>
/// Keeps all secondary ships, loose ocean flotsam, navigation goals, and in-flight sea actors
/// stationary in canonical ocean space when the primary world-origin ship moves.
/// </summary>
public static class FleetAnchorRebase
{
    private static Vector3 _oldPosition;
    private static Quaternion _oldRotation;
    private static bool _captured;
    private static bool _installed;
    private static readonly Dictionary<int, (Debris Flotsam, Vector3 Pos, Quaternion Rot)> PreMoveFlotsam =
        new Dictionary<int, (Debris, Vector3, Quaternion)>();

    public static void Install(Harmony harmony)
    {
        if (_installed) return;
        harmony.Patch(AccessTools.Method(typeof(World), nameof(World.RepositionTownheart)),
            prefix: new HarmonyMethod(typeof(FleetAnchorRebase), nameof(BeforeAnchorMove)),
            postfix: new HarmonyMethod(typeof(FleetAnchorRebase), nameof(AfterAnchorMove)));
        _installed = true;
    }

    private static void BeforeAnchorMove()
    {
        var world = GameManager.WorldManager?.World;
        if (world == null) return;
        _oldPosition = world.TownheartWorldPosition;
        _oldRotation = world.TownheartRotation;
        _captured = true;

        PreMoveFlotsam.Clear();
        if (Plugin.Instance?.Network?.IsClient == true) return;
        var flotsamList = GameManager.WorldManager?.FlotsamInWorld;
        if (flotsamList == null) return;
        foreach (var f in flotsamList)
        {
            if (f == null || !f.Initialized || f.Pooled) continue;
            PreMoveFlotsam[f.GetInstanceID()] = (f, f.transform.position, f.transform.rotation);
        }
    }

    private static void AfterAnchorMove()
    {
        if (!_captured) return;
        _captured = false;
        if (ProgressionReplication.TransitioningBiome)
        {
            PreMoveFlotsam.Clear();
            return;
        }
        var world = GameManager.WorldManager?.World;
        if (world == null)
        {
            PreMoveFlotsam.Clear();
            return;
        }
        var newPosition = world.TownheartWorldPosition;
        var newRotation = world.TownheartRotation;
        if (Vector3.Distance(_oldPosition, newPosition) < 0.0001f && Quaternion.Angle(_oldRotation, newRotation) < 0.01f)
        {
            PreMoveFlotsam.Clear();
            return;
        }

        var inverseNew = Quaternion.Inverse(newRotation);
        var rotDelta = inverseNew * _oldRotation;

        if (FleetRuntime.Active && Plugin.Instance?.Network?.IsClient != true)
        {
            foreach (var owner in FleetEngineBundle.SecondaryCommunities.ToArray())
            {
                foreach (var transform in FleetKinematics.TopLevelTransforms(owner, includeAtSea: true))
                {
                    var canonical = _oldPosition + _oldRotation * transform.position;
                    transform.position = inverseNew * (canonical - newPosition);
                    transform.rotation = rotDelta * transform.rotation;
                }
                FleetKinematics.RefreshCaches(owner);
                FleetKinematics.RefreshOwnedNodePositions(owner);
                FleetPathRefresh.Refresh(owner);
            }
            FleetOwnedNavigation.RebaseAnchor(_oldPosition, _oldRotation, newPosition, newRotation);
        }

        // Rebase any loose ocean flotsam not already moved by a PointOfInterestSpawner
        if (PreMoveFlotsam.Count > 0 && Plugin.Instance?.Network?.IsClient != true)
        {
            foreach (var entry in PreMoveFlotsam.Values)
            {
                var f = entry.Flotsam;
                if (f == null || !f.Initialized || f.Pooled) continue;
                if ((f.transform.position - entry.Pos).sqrMagnitude < 0.0001f)
                {
                    var canonical = _oldPosition + _oldRotation * entry.Pos;
                    var rebasedPos = inverseNew * (canonical - newPosition);
                    rebasedPos.y = entry.Pos.y;
                    f.UpdatePositionAndRotation(rebasedPos, rotDelta * entry.Rot);
                }
            }
            PreMoveFlotsam.Clear();
        }

        // Keep Host 3D camera anchored to the ocean water (when FollowOwnShip is off) so Host
        // players also watch their ship physically sail across the screen toward stationary resources!
        if (Plugin.Instance?.Network?.IsClient != true && !LiveNavigation.ViewOpen && !ClientOceanAnchor.FollowOwnShip)
        {
            var camCtrl = CameraController.Instance;
            if (camCtrl != null)
            {
                var locked = AccessTools.Field(typeof(CameraController), "_lockedTransform")?.GetValue(camCtrl) as Transform;
                if (locked == null)
                {
                    var camCanonical = _oldPosition + _oldRotation * camCtrl.transform.position;
                    var nextCamPos = inverseNew * (camCanonical - newPosition);
                    nextCamPos.y = camCtrl.transform.position.y;
                    camCtrl.transform.position = nextCamPos;
                    camCtrl.transform.rotation = rotDelta * camCtrl.transform.rotation;
                    camCtrl.CameraPosition = nextCamPos;
                }
            }
        }

        FleetPhysicalActors.RebaseAnchor(_oldPosition, _oldRotation, newPosition, newRotation);
        if(Plugin.Instance?.Network?.IsHost==true){FleetVoyages.Rebase(_oldPosition,_oldRotation,newPosition,newRotation);FleetCombat.Rebase(_oldPosition,_oldRotation,newPosition,newRotation);}
    }
}
