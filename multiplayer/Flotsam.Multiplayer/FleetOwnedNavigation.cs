using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Per-ship continuous kinematic navigation, multi-waypoint route following,
/// and canonical WorldMap coordinate mapping for player ships.
/// Matches native <see cref="WorldMap"/> movement speed (~50 m/s × Engine modifier)
/// and rotation speed (~50 deg/s keys, up to 2.5x when turning toward mouse/route targets).
/// </summary>
public static class FleetOwnedNavigation
{
    private enum GoalMode
    {
        Waypoints,
        MouseTarget,
        Keys
    }

    private sealed class NavGoal
    {
        public GoalMode Mode;
        public Vector3 Target;
        public readonly Queue<Vector3> Waypoints = new Queue<Vector3>();
        public float Direction = 1f;
        public float TurnInput;
        public float ThrottleInput;
        public float SpeedOverride;
        /// <summary>0 when the goal is a clicked route; a deadline for held mouse/key inputs.</summary>
        public float Expires;
    }

    private static readonly Dictionary<Community, NavGoal> Goals = new Dictionary<Community, NavGoal>();
    private static readonly FieldInfo? MapMoveSpeedField = AccessTools.Field(typeof(WorldMap), "_movementSpeed");
    private static readonly FieldInfo? MapRotSpeedField = AccessTools.Field(typeof(WorldMap), "_rotationSpeed");
    private static readonly FieldInfo? MouseRotMultField = AccessTools.Field(typeof(WorldMapTownheartPhycis2D), "_mouseRotationSpeedMultiplier");
    private static readonly FieldInfo? MapPhysicsField = AccessTools.Field(typeof(WorldMap), "_townheartPhysics");

    /// <summary>
    /// Resolves the exact native movement speed (m/s) and rotation speed (deg/s) for <paramref name="owner"/>,
    /// matching <see cref="WorldMap.MovementSpeed"/> and <see cref="WorldMap.RotationSpeed"/>.
    /// </summary>
    public static void ResolveNativeSpeeds(Community? owner, out float moveSpeed, out float rotSpeed, out float mouseTurnMult)
    {
        var baseMove = 50f;
        var baseRot = 50f;
        mouseTurnMult = 2.5f;

        var map = GameManager.WorldMapManager?.WorldMap;
        if (map != null)
        {
            if (MapMoveSpeedField?.GetValue(map) is float m && float.IsFinite(m) && m > 0.1f)
                baseMove = m;
            if (MapRotSpeedField?.GetValue(map) is float r && float.IsFinite(r) && r > 0.1f)
                baseRot = r;
            if (MapPhysicsField?.GetValue(map) is WorldMapTownheartPhycis2D phys &&
                MouseRotMultField?.GetValue(phys) is float mult && float.IsFinite(mult) && mult >= 1f)
                mouseTurnMult = mult;
        }

        var engineMult = owner?.Engine != null ? owner.Engine.ReturnMovementSpeed() : 1f;
        if (!float.IsFinite(engineMult) || engineMult <= 0.01f) engineMult = 1f;

        moveSpeed = Mathf.Clamp(EnergyDevTools.ApplyMovementSpeed(baseMove) * engineMult, 5f, 250f);
        rotSpeed = Mathf.Clamp(EnergyDevTools.ApplyMovementSpeed(baseRot) * engineMult, 15f, 360f);

        if (BuildingDurability.IsShipHeartCrippled(owner))
        {
            moveSpeed = Mathf.Max(3.5f, moveSpeed * 0.20f);
            rotSpeed = Mathf.Max(10f, rotSpeed * 0.45f);
        }
    }

    /// <summary>
    /// Converts Host 3D scene coordinates to canonical WorldMap XZ coordinates.
    /// Note: <see cref="World.TownheartWorldPosition"/> is always anchored to Host scene origin (0,0,0).
    /// </summary>
    public static Vector3 SceneToMap(Vector3 scenePosition)
    {
        var world = GameManager.WorldManager?.World;
        if (world == null) return scenePosition;
        return world.TownheartWorldPosition + world.TownheartRotation * scenePosition;
    }

    /// <summary>
    /// Converts canonical WorldMap XZ coordinates to Host 3D scene coordinates.
    /// </summary>
    public static Vector3 MapToScene(float mapX, float mapZ)
    {
        var world = GameManager.WorldManager?.World;
        if (world == null) return new Vector3(mapX, 0f, mapZ);
        return Quaternion.Inverse(world.TownheartRotation) * (new Vector3(mapX, 0f, mapZ) - world.TownheartWorldPosition);
    }

    public static bool Start(Community owner, float sceneX, float sceneZ, float speedOverride = 0f)
    {
        if (owner == null || !float.IsFinite(sceneX) || !float.IsFinite(sceneZ) || !float.IsFinite(speedOverride)) return false;
        var heart = FleetEngineBundle.HeartBuildable(owner);
        if (heart == null) return false;
        Goals[owner] = new NavGoal
        {
            Mode = GoalMode.Waypoints,
            Target = new Vector3(sceneX, heart.transform.position.y, sceneZ),
            SpeedOverride = speedOverride > 0f ? Mathf.Clamp(speedOverride, 0.5f, 250f) : 0f
        };
        return true;
    }

    public static bool StartMap(
        Community owner,
        float mapX,
        float mapZ,
        MouseMovementCursorProperties.Gear gear = MouseMovementCursorProperties.Gear.Forward,
        float speedOverride = 0f)
    {
        if (owner == null || !float.IsFinite(mapX) || !float.IsFinite(mapZ) || !float.IsFinite(speedOverride)) return false;
        var heart = FleetEngineBundle.HeartBuildable(owner);
        if (heart == null) return false;
        var scene = MapToScene(mapX, mapZ);
        var direction = (gear == MouseMovementCursorProperties.Gear.Reverse || gear == MouseMovementCursorProperties.Gear.ForwardReverse)
            ? -1f
            : 1f;
        Goals[owner] = new NavGoal
        {
            Mode = GoalMode.MouseTarget,
            Target = new Vector3(scene.x, heart.transform.position.y, scene.z),
            Direction = direction,
            SpeedOverride = speedOverride > 0f ? Mathf.Clamp(speedOverride, 0.5f, 250f) : 0f,
            Expires = Time.realtimeSinceStartup + 1.2f
        };
        return true;
    }

    public static bool StartMapPath(Community owner, IReadOnlyList<Vector2> mapWaypoints, float speedOverride = 0f)
    {
        if (owner == null || mapWaypoints == null || mapWaypoints.Count == 0 || !float.IsFinite(speedOverride)) return false;
        var heart = FleetEngineBundle.HeartBuildable(owner);
        if (heart == null) return false;
        var y = heart.transform.position.y;
        var goal = new NavGoal
        {
            Mode = GoalMode.Waypoints,
            SpeedOverride = speedOverride > 0f ? Mathf.Clamp(speedOverride, 0.5f, 250f) : 0f
        };
        var added = 0;
        foreach (var wp in mapWaypoints)
        {
            if (!float.IsFinite(wp.x) || !float.IsFinite(wp.y)) continue;
            var scene = MapToScene(wp.x, wp.y);
            var pt = new Vector3(scene.x, y, scene.z);
            if (added == 0) goal.Target = pt;
            else goal.Waypoints.Enqueue(pt);
            added++;
        }
        if (added == 0) return false;
        Goals[owner] = goal;
        return true;
    }

    public static void Stop(Community owner) => Goals.Remove(owner);
    public static void Clear() => Goals.Clear();
    public static bool IsMoving(Community owner) => owner != null && Goals.ContainsKey(owner);

    /// <summary>
    /// Held steering keys drive a ship with exact native <see cref="WorldMapTownheartPhycis2D.MoveWithKeys"/>
    /// semantics: <paramref name="turn"/> (-1 left .. +1 right) rotates in place or while moving,
    /// and <paramref name="throttle"/> (-1 astern .. +1 ahead) moves along the hull heading without flipping 180°.
    /// </summary>
    public static bool Steer(Community owner, float turn, float throttle, float speedOverride = 0f)
    {
        if (owner == null || !float.IsFinite(turn) || !float.IsFinite(throttle) || !float.IsFinite(speedOverride)) return false;
        if (Mathf.Abs(turn) <= 0.01f && Mathf.Abs(throttle) <= 0.01f) { Stop(owner); return true; }
        var heart = FleetEngineBundle.HeartBuildable(owner);
        if (heart == null) return false;

        Goals[owner] = new NavGoal
        {
            Mode = GoalMode.Keys,
            TurnInput = Mathf.Clamp(turn, -1f, 1f),
            ThrottleInput = Mathf.Clamp(throttle, -1f, 1f),
            SpeedOverride = speedOverride > 0f ? Mathf.Clamp(speedOverride, 0.5f, 250f) : 0f,
            Expires = Time.realtimeSinceStartup + 1.2f
        };
        return true;
    }

    internal static void RebaseAnchor(Vector3 oldPos, Quaternion oldRot, Vector3 newPos, Quaternion newRot)
    {
        var inv = Quaternion.Inverse(newRot);
        foreach (var goal in Goals.Values)
        {
            var canonical = oldPos + oldRot * goal.Target;
            goal.Target = inv * (canonical - newPos);
            if (goal.Waypoints.Count > 0)
            {
                var count = goal.Waypoints.Count;
                for (var i = 0; i < count; i++)
                {
                    var wp = goal.Waypoints.Dequeue();
                    var wpCanonical = oldPos + oldRot * wp;
                    goal.Waypoints.Enqueue(inv * (wpCanonical - newPos));
                }
            }
        }
    }

    public static void Tick(float deltaTime)
    {
        if (Goals.Count == 0 || deltaTime <= 0f) return;
        var dt = Mathf.Min(deltaTime, 0.1f);
        var interactionRadius = GameSettings.Instance?.GameplaySettings?.InteractionRadius ?? 200f;
        var maxRangeSq = (interactionRadius + 15f) * (interactionRadius + 15f);

        foreach (var pair in Goals.ToArray())
        {
            var owner = pair.Key;
            var goal = pair.Value;
            var heart = FleetEngineBundle.HeartBuildable(owner);
            if (owner == null || heart == null) { Goals.Remove(owner!); continue; }
            if (goal.Expires > 0f && Time.realtimeSinceStartup > goal.Expires) { Goals.Remove(owner); continue; }
            if (owner.Engine != null && owner.Engine.ReturnMoveableDistance(1f) <= 0f) { Goals.Remove(owner); continue; }

            ResolveNativeSpeeds(owner, out var moveSpeed, out var rotSpeed, out var mouseTurnMult);
            if (goal.SpeedOverride > 0f) moveSpeed = goal.SpeedOverride;

            var current = heart.transform.position;
            var curRot = heart.transform.rotation;
            Vector3 desiredPos;
            Quaternion desiredRot;
            float movedDistance;

            if (goal.Mode == GoalMode.Keys)
            {
                var yawDelta = goal.TurnInput * rotSpeed * dt;
                desiredRot = curRot * Quaternion.Euler(0f, yawDelta, 0f);
                var fwd = desiredRot * Vector3.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
                else fwd.Normalize();

                var rawDist = Mathf.Min(Mathf.Abs(goal.ThrottleInput) * moveSpeed * dt, 12f);
                if (rawDist <= 0.0001f)
                {
                    if (Mathf.Abs(yawDelta) > 0.001f)
                        FleetKinematics.ApplyContinuousPose(owner, heart, current, desiredRot, 0f);
                    continue;
                }

                movedDistance = ClampDistanceToAvailableEnergy(owner, rawDist);
                if (movedDistance <= 0.0001f) { Goals.Remove(owner); continue; }
                desiredPos = current + fwd * (Mathf.Sign(goal.ThrottleInput) * movedDistance);
            }
            else if (goal.Mode == GoalMode.MouseTarget)
            {
                var toTarget = goal.Target - current;
                toTarget.y = 0f;
                var distToTarget = toTarget.magnitude;
                if (distToTarget <= 0.4f) { Goals.Remove(owner); continue; }

                var reverse = goal.Direction < 0f;
                var lookVec = reverse ? -toTarget / distToTarget : toTarget / distToTarget;
                var targetRot = Quaternion.LookRotation(lookVec, Vector3.up);
                var angle = Quaternion.Angle(curRot, targetRot);
                var turnBoost = Mathf.Lerp(1f, mouseTurnMult, angle / 180f);
                desiredRot = Quaternion.RotateTowards(curRot, targetRot, rotSpeed * turnBoost * dt);

                var rawStep = Mathf.Min(Mathf.Min(moveSpeed * dt, 12f), distToTarget);
                movedDistance = ClampDistanceToAvailableEnergy(owner, rawStep);
                if (movedDistance <= 0.0001f) { Goals.Remove(owner); continue; }

                var fwd = desiredRot * Vector3.forward;
                fwd.y = 0f;
                fwd.Normalize();
                desiredPos = current + fwd * (goal.Direction * movedDistance);
            }
            else
            {
                // Waypoints mode (planned WorldMap route or Shift+RightClick 3D course)
                var budget = ClampDistanceToAvailableEnergy(owner, Mathf.Min(moveSpeed * dt, 12f));
                if (budget <= 0.0001f) { Goals.Remove(owner); continue; }

                var cursor = current;
                var lastDir = Vector3.zero;
                var remaining = budget;
                while (remaining > 0.0001f)
                {
                    var seg = goal.Target - cursor;
                    seg.y = 0f;
                    var segLen = seg.magnitude;
                    if (segLen <= 0.25f)
                    {
                        if (goal.Waypoints.Count > 0)
                        {
                            goal.Target = goal.Waypoints.Dequeue();
                            continue;
                        }
                        break;
                    }
                    lastDir = seg / segLen;
                    if (segLen <= remaining)
                    {
                        cursor = new Vector3(goal.Target.x, current.y, goal.Target.z);
                        remaining -= segLen;
                        if (goal.Waypoints.Count > 0)
                        {
                            goal.Target = goal.Waypoints.Dequeue();
                            continue;
                        }
                        break;
                    }
                    cursor += lastDir * remaining;
                    remaining = 0f;
                }

                movedDistance = budget - remaining;
                if (movedDistance <= 0.0001f) { Goals.Remove(owner); continue; }

                desiredPos = cursor;
                desiredRot = lastDir.sqrMagnitude > 0.0001f
                    ? Quaternion.RotateTowards(curRot, Quaternion.LookRotation(lastDir, Vector3.up), rotSpeed * mouseTurnMult * dt)
                    : curRot;

                var endDelta = goal.Target - desiredPos;
                endDelta.y = 0f;
                if (endDelta.sqrMagnitude <= 0.0625f && goal.Waypoints.Count == 0)
                    Goals.Remove(owner);
            }

            if (!FleetHullCollision.TryMove(owner, current, desiredPos, out var accepted, out _, dt))
            {
                Goals.Remove(owner);
                continue;
            }

            var actualMoved = Vector3.Distance(new Vector3(current.x, 0f, current.z), new Vector3(accepted.x, 0f, accepted.z));
            FleetKinematics.ApplyContinuousPose(owner, heart, accepted, desiredRot, actualMoved);

            // Cancel salvage markers left behind beyond this ship's interaction radius
            if (owner.Markers != null && owner.Markers.Count > 0)
            {
                foreach (var marker in owner.Markers.ToArray())
                {
                    if (marker == null) continue;
                    var dx = marker.transform.position.x - accepted.x;
                    var dz = marker.transform.position.z - accepted.z;
                    if (dx * dx + dz * dz > maxRangeSq)
                        marker.Remove();
                }
            }
        }
    }

    private static float ClampDistanceToAvailableEnergy(Community owner, float desiredDistance)
    {
        if (desiredDistance <= 0f || EnergyDevTools.FreeMovement || owner.Engine == null)
            return desiredDistance;
        var avail = FleetEngineBundle.EnergyOf(owner);
        var cost = owner.Engine.ReturnEnergyCost(desiredDistance);
        if (cost <= avail) return desiredDistance;
        if (avail <= 0.01f || cost <= 0.0001f) return 0f;
        return desiredDistance * Mathf.Clamp01(avail / cost);
    }
}
