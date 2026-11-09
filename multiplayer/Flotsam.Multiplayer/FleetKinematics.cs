using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Fast, permit-free rigid body step & derived geometry/graph cache refresh for secondary ships.
/// </summary>
public static class FleetKinematics
{
    private static readonly MethodInfo? UpdateTargetPosition = AccessTools.DeclaredMethod(typeof(Target), "UpdatePosition", Type.EmptyTypes);
    private static readonly MethodInfo? InitializeEdges = AccessTools.DeclaredMethod(typeof(Hookable), "InitializeEdges", new[] { typeof(Buildable) });
    private static readonly FieldInfo? TargetOverride = AccessTools.Field(typeof(Target), "_override");
    private static readonly FieldInfo? ObstaclePolygons = AccessTools.Field(typeof(Obstacle), "_pathfindingPolygons");
    private static readonly FieldInfo? HookEdges = AccessTools.Field(typeof(Hookable), "_edges");

    public static void ApplyContinuousPose(Community owner, Buildable heart, Vector3 newPose, Quaternion newRotation, float distance)
    {
        if (owner == null || heart == null) return;
        var oldPose = heart.transform.position;
        var oldRotation = heart.transform.rotation;
        var deltaRotation = newRotation * Quaternion.Inverse(oldRotation);

        foreach (var transform in TopLevelTransforms(owner))
        {
            var offset = transform.position - oldPose;
            transform.position = newPose + deltaRotation * offset;
            transform.rotation = ReferenceEquals(transform, heart.transform) ? newRotation : deltaRotation * transform.rotation;
        }

        RefreshCaches(owner);
        RefreshOwnedNodePositions(owner);
        FleetPathRefresh.Refresh(owner);
        if (distance > 0f) FleetEngineBundle.ConsumeEnergy(owner, distance);
    }

    public static IEnumerable<Transform> TopLevelTransforms(Community owner)
    {
        var owned = new HashSet<Buildable>(owner.Buildables.Where(b => b != null));
        foreach (var building in owned)
        {
            var parent = building.transform.parent;
            var nested = false;
            while (parent != null)
            {
                if (parent.GetComponent<Buildable>() is Buildable b && owned.Contains(b)) { nested = true; break; }
                parent = parent.parent;
            }
            if (!nested) yield return building.transform;
        }
        foreach (var agent in owner.Agents)
        {
            if (agent == null) continue;
            var parent = agent.transform.parent;
            var nested = false;
            while (parent != null)
            {
                if (parent.GetComponent<Buildable>() is Buildable b && owned.Contains(b)) { nested = true; break; }
                parent = parent.parent;
            }
            if (!nested) yield return agent.transform;
        }
    }

    public static void RefreshCaches(Community owner)
    {
        foreach (var building in owner.Buildables)
        {
            if (building == null) continue;
            building.OutlinePolygon?.Update(false);
            building.BlockingPolygon?.Update(false);
            foreach (var obstacle in building.GetComponentsInChildren<Obstacle>(true))
            {
                if (obstacle == null) continue;
                obstacle.Polygon?.Update(false);
                try { obstacle.SetRadius(); } catch { }
                if (ObstaclePolygons?.GetValue(obstacle) is List<PathfindingPolygon> waters)
                    foreach (var water in waters) water?.Update(false);
            }
            var hook = building.GetComponent<Hookable>();
            if (hook != null)
            {
                var edges = HookEdges?.GetValue(hook) as Hookable.Edge[];
                if (edges != null && edges.Length != building.OutlineCorners.Count) HookEdges?.SetValue(hook, null);
                InitializeEdges?.Invoke(hook, new object[] { building });
            }
            foreach (var target in building.GetComponentsInChildren<Target>(true))
            {
                if (target != null && TargetOverride?.GetValue(target) == null)
                    UpdateTargetPosition?.Invoke(target, Array.Empty<object>());
            }
        }
        foreach (var agent in owner.Agents)
        {
            if (agent == null) continue;
            foreach (var target in agent.GetComponentsInChildren<Target>(true))
            {
                if (target != null && TargetOverride?.GetValue(target) == null)
                    UpdateTargetPosition?.Invoke(target, Array.Empty<object>());
            }
        }
    }

    public static void RefreshOwnedNodePositions(Community owner)
    {
        var graph = GameManager.GraphManager?.ConstructionGraph;
        if (graph == null) return;
        foreach (var node in graph.ReturnAllNodes())
        {
            if (node == null) continue;
            var community = node.Marker?.Construction?.Buildable?.Community ?? node.Marker?.GetComponentInParent<Buildable>()?.Community;
            if (ReferenceEquals(community, owner)) node.UpdateRootPosition();
        }
    }
}
