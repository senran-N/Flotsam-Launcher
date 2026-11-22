using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Anchors the Client's 3D scene origin to a stationary ocean coordinate instead of the Host's
/// floating-origin Townheart treadmill.
/// As a result, sea resources (<c>Flotsam</c>), salvage markers (<c>Marker</c>), and islands
/// (<c>Landmark</c>) remain 100% motionless on the ocean water while ships physically sail across
/// the waves toward them at full frame-rate interpolation.
/// </summary>
internal static class ClientOceanAnchor
{
    internal static readonly OceanAnchorMath Math = new OceanAnchorMath();

    /// <summary>
    /// When false (default), the 3D camera stays anchored to the ocean water so players visibly
    /// watch their ship sail toward resources. When true (toggled via <c>V</c> or <c>F8</c> HUD),
    /// the camera rides along with the local ship.
    /// </summary>
    internal static bool FollowOwnShip { get; set; }

    private static readonly MethodInfo? SetTownheartMapPos =
        AccessTools.PropertySetter(typeof(World), nameof(World.TownheartMapPosition));

    private static Vector2 _lastSpawnCheckOceanPos;
    private static bool _hasSpawnCheck;
    private static Vector3 _lastLocalShipScenePos;
    private static bool _hasLocalShipScenePos;

    private static bool ActiveClient => Plugin.Instance?.Network?.IsClient == true && Math.Initialized;

    internal static Vector3 AnchorPos => new Vector3(Math.AnchorX, 0f, Math.AnchorZ);
    internal static Quaternion AnchorRot => Quaternion.Euler(0f, Math.AnchorYaw, 0f);

    internal static void Reset()
    {
        Math.Reset();
        _hasSpawnCheck = false;
        _hasLocalShipScenePos = false;
        _lastSpawnCheckOceanPos = Vector2.zero;
        _lastLocalShipScenePos = Vector3.zero;
    }

    internal static Vector3 HostToClientPos(Vector3 hostPos)
    {
        if (!ActiveClient) return hostPos;
        Math.HostToClient(hostPos.x, hostPos.z, 0f, out var cx, out var cz, out _);
        return new Vector3(cx, hostPos.y, cz);
    }

    internal static float HostToClientYaw(float hostYaw)
    {
        if (!ActiveClient) return hostYaw;
        Math.HostToClient(0f, 0f, hostYaw, out _, out _, out var cyaw);
        return cyaw;
    }

    internal static void HostToClientPose(float hx, float hz, float hyaw, out float cx, out float cz, out float cyaw)
    {
        if (!ActiveClient)
        {
            cx = hx;
            cz = hz;
            cyaw = hyaw;
            return;
        }
        Math.HostToClient(hx, hz, hyaw, out cx, out cz, out cyaw);
    }

    internal static Vector3 ClientToHostPos(Vector3 clientPos)
    {
        if (!ActiveClient) return clientPos;
        Math.ClientToHost(clientPos.x, clientPos.z, 0f, out var hx, out var hz, out _);
        return new Vector3(hx, clientPos.y, hz);
    }

    internal static float ClientToHostYaw(float clientYaw)
    {
        if (!ActiveClient) return clientYaw;
        Math.ClientToHost(0f, 0f, clientYaw, out _, out _, out var hyaw);
        return hyaw;
    }

    internal static Vector3 OceanToHostPos(float mapX, float mapZ)
    {
        if (!ActiveClient) return FleetOwnedNavigation.MapToScene(mapX, mapZ);
        Math.OceanToHostScene(mapX, mapZ, 0f, out var hx, out var hz, out _);
        return new Vector3(hx, 0f, hz);
    }

    internal static void SyncFrame(
        WorldFrame frame,
        World world,
        PoseTrackMap buildingTracks,
        PoseTrackMap agentTracks,
        SalvageReplication salvage,
        BirdReplication birds,
        IEnumerable<Buildable> buildings,
        IEnumerable<Agent> agents)
    {
        var firstFrame = !Math.Initialized;
        Math.UpdateHostFrame(frame.worldX, frame.worldZ, frame.worldRotation);

        if (!firstFrame && Construction.Townheart != null)
        {
            var localHeart = Construction.TownheartPosition;
            if (Math.ShouldRebase(localHeart.x, localHeart.z))
            {
                Math.RebaseToClientScenePoint(localHeart.x, localHeart.z, out var oldAx, out var oldAz, out var oldAyaw);
                var newAx = Math.AnchorX;
                var newAz = Math.AnchorZ;
                var newAyaw = Math.AnchorYaw;

                PoseSample Shift(PoseSample s) =>
                    OceanAnchorMath.RebasePose(oldAx, oldAz, oldAyaw, newAx, newAz, newAyaw, s);

                world.RepositionTownheart(AnchorPos, AnchorRot);

                buildingTracks.ShiftAll(Shift);
                agentTracks.ShiftAll(Shift);
                salvage.ShiftRebased(Shift);
                birds.ShiftRebased(Shift);

                foreach (var b in buildings)
                {
                    if (b != null)
                        ReplicaFreeze.ApplyPose(b.transform, Shift(ReplicaFreeze.CurrentPose(b.transform)));
                }

                foreach (var a in agents)
                {
                    if (a != null)
                        ReplicaFreeze.ApplyPose(a.transform, Shift(ReplicaFreeze.CurrentPose(a.transform)));
                }

                if (_hasLocalShipScenePos)
                {
                    var shiftedLocal = Shift(new PoseSample(_lastLocalShipScenePos.x, _lastLocalShipScenePos.y, _lastLocalShipScenePos.z, 0f));
                    _lastLocalShipScenePos = new Vector3(shiftedLocal.X, shiftedLocal.Y, shiftedLocal.Z);
                }

                if (CameraController.Instance != null)
                {
                    var camTr = CameraController.Instance.transform;
                    var shiftedCam = Shift(new PoseSample(camTr.position.x, camTr.position.y, camTr.position.z, camTr.eulerAngles.y));
                    camTr.position = new Vector3(shiftedCam.X, shiftedCam.Y, shiftedCam.Z);
                }
            }
        }

        var anchor2D = new Vector2(Math.AnchorX, Math.AnchorZ);
        if (frame.ships != null)
        {
            foreach (var s in frame.ships)
            {
                if (s != null && Replication.Finite(s.mapX) && Replication.Finite(s.mapZ))
                    world.UpdateTiles(new Vector2(s.mapX, s.mapZ));
            }
        }
        world.UpdateTiles(new Vector2(frame.worldX, frame.worldZ));
        world.UpdateTiles(anchor2D);
        SetTownheartMapPos?.Invoke(world, new object[] { anchor2D });

        var checkOcean = new Vector2(frame.worldX, frame.worldZ);
        if (Plugin.Options.mode == "independent" && Community.PlayerCommunity != null && frame.ships != null)
        {
            var myKey = Replication.ShipKey(Community.PlayerCommunity);
            foreach (var s in frame.ships)
            {
                if (s != null && s.name == myKey && Replication.Finite(s.mapX) && Replication.Finite(s.mapZ))
                {
                    checkOcean = new Vector2(s.mapX, s.mapZ);
                    break;
                }
            }
        }

        var anchorMismatch = (world.TownheartWorldPosition - AnchorPos).sqrMagnitude > 0.001f
                             || Quaternion.Angle(world.TownheartRotation, AnchorRot) > 0.05f;
        var sailedFar = !_hasSpawnCheck || (checkOcean - _lastSpawnCheckOceanPos).sqrMagnitude >= 625f;
        if (firstFrame || anchorMismatch || sailedFar)
        {
            _lastSpawnCheckOceanPos = checkOcean;
            _hasSpawnCheck = true;
            world.RepositionTownheart(AnchorPos, AnchorRot);
        }
    }

    internal static void LateTickCameraAndHighlighters()
    {
        var net = Plugin.Instance?.Network;
        if (net == null || !GameSession.Ready()) return;

        FleetUnionTiles.RefreshActiveHighlighters();

        if (!net.IsClient || !net.ReplicaReady)
        {
            _hasLocalShipScenePos = false;
            return;
        }

        var heart = FleetEngineBundle.HeartBuildable(Community.PlayerCommunity) ?? Construction.Townheart?.Buildable;
        var currentPos = heart != null ? heart.transform.position : Construction.TownheartPosition;

        if (_hasLocalShipScenePos)
        {
            var delta = currentPos - _lastLocalShipScenePos;
            delta.y = 0f;
            if (FollowOwnShip && !LiveNavigation.ViewOpen && CameraController.Instance != null &&
                delta.sqrMagnitude > 0.0000001f && delta.sqrMagnitude < 400f)
            {
                CameraController.Instance.transform.position += delta;
            }
        }

        _lastLocalShipScenePos = currentPos;
        _hasLocalShipScenePos = true;
    }
}
