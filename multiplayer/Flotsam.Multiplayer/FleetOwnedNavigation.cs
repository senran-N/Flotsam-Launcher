using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Per-ship continuous kinematic navigation and WorldMap coordinate mapping for player ships.
/// </summary>
public static class FleetOwnedNavigation
{
    private sealed class NavGoal
    {
        public Vector3 Target;
        public float Speed;
        /// <summary>0 when the goal is a clicked destination; a deadline for held keys.</summary>
        public float Expires;
    }

    private static readonly Dictionary<Community, NavGoal> Goals = new Dictionary<Community, NavGoal>();

    public static Vector3 SceneToMap(Vector3 scenePosition)
    {
        var world = GameManager.WorldManager?.World;
        if (world == null) return scenePosition;
        var primaryScene = Construction.TownheartPosition;
        return world.TownheartWorldPosition + world.TownheartRotation * (scenePosition - primaryScene);
    }

    public static Vector3 MapToScene(float mapX, float mapZ)
    {
        var world = GameManager.WorldManager?.World;
        if (world == null) return new Vector3(mapX, 0f, mapZ);
        var primaryScene = Construction.TownheartPosition;
        return primaryScene + Quaternion.Inverse(world.TownheartRotation) * (new Vector3(mapX, 0f, mapZ) - world.TownheartWorldPosition);
    }

    public static bool Start(Community owner, float sceneX, float sceneZ, float speed = 5f)
    {
        if (owner == null || !float.IsFinite(sceneX) || !float.IsFinite(sceneZ) || !float.IsFinite(speed) || speed <= 0f) return false;
        var heart = FleetEngineBundle.HeartBuildable(owner);
        if (heart == null) return false;
        Goals[owner] = new NavGoal { Target = new Vector3(sceneX, heart.transform.position.y, sceneZ), Speed = Mathf.Clamp(speed, 0.5f, 20f) };
        return true;
    }

    public static bool StartMap(Community owner, float mapX, float mapZ, float speed = 5f)
    {
        if (!float.IsFinite(mapX) || !float.IsFinite(mapZ)) return false;
        var scene = MapToScene(mapX, mapZ);
        return Start(owner, scene.x, scene.z, speed);
    }

    public static void Stop(Community owner) => Goals.Remove(owner);
    public static void Clear() => Goals.Clear();
    public static bool IsMoving(Community owner) => owner != null && Goals.ContainsKey(owner);

    /// <summary>
    /// Held steering keys or Town-view helm pad drive a ship along its heading.
    /// x = turn (-1 left .. +1 right), z = throttle (-1 astern .. +1 ahead).
    /// </summary>
    public static bool Steer(Community owner, float turn, float throttle, float speed = 5.5f)
    {
        if (owner == null || !float.IsFinite(turn) || !float.IsFinite(throttle)) return false;
        if (Mathf.Abs(turn) <= 0.01f && Mathf.Abs(throttle) <= 0.01f) { Stop(owner); return true; }
        var heart = FleetEngineBundle.HeartBuildable(owner);
        if (heart == null) return false;

        var forward = heart.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        forward.Normalize();
        var right = new Vector3(forward.z, 0f, -forward.x);

        var effectiveThrottle = Mathf.Abs(throttle) > 0.05f ? throttle : 0.45f;
        var direction = (forward * effectiveThrottle + right * (turn * 0.85f)).normalized;
        var target = heart.transform.position + direction * 45f;
        Goals[owner] = new NavGoal
        {
            Target = new Vector3(target.x, heart.transform.position.y, target.z),
            Speed = Mathf.Clamp(speed, 0.5f, 20f),
            Expires = Time.realtimeSinceStartup + 1.4f
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
        }
    }

    public static void Tick(float deltaTime)
    {
        if (Goals.Count == 0 || deltaTime <= 0f || deltaTime > 0.25f) return;
        var primaryPos = Community.PlayerCommunity?.Buildables.FirstOrDefault()?.transform.position;

        foreach (var pair in Goals.ToArray())
        {
            var owner = pair.Key;
            var goal = pair.Value;
            var heart = FleetEngineBundle.HeartBuildable(owner);
            if (owner == null || heart == null) { Goals.Remove(owner!); continue; }
            if (goal.Expires > 0f && Time.realtimeSinceStartup > goal.Expires) { Goals.Remove(owner); continue; }

            var current = heart.transform.position;
            var delta = goal.Target - current;
            delta.y = 0f;
            if (delta.sqrMagnitude <= 0.02f) { Goals.Remove(owner); continue; }

            var step = Mathf.Min(Mathf.Min(goal.Speed * deltaTime, 2.0f), delta.magnitude);
            var cost = owner.Engine != null ? owner.Engine.ReturnEnergyCost(step) : 0f;
            if (!EnergyDevTools.FreeMovement && FleetEngineBundle.EnergyOf(owner) < cost) { Goals.Remove(owner); continue; }

            var newPose = current + delta.normalized * step;
            if (primaryPos.HasValue && !ReferenceEquals(owner, Community.PlayerCommunity) && (newPose - primaryPos.Value).sqrMagnitude < 16f)
            {
                Goals.Remove(owner);
                continue;
            }
            if (!FleetHullCollision.TryMove(owner, current, newPose, out var accepted, out _)) { Goals.Remove(owner); continue; }

            var newRotation = Quaternion.Slerp(heart.transform.rotation, Quaternion.LookRotation(delta.normalized, Vector3.up), Mathf.Clamp01(deltaTime * 4f));
            FleetKinematics.ApplyContinuousPose(owner, heart, accepted, newRotation, step);
        }
    }
}
