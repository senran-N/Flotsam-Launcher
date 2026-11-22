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

        foreach (var transform in TopLevelTransforms(owner, includeAtSea: false))
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

    /// <summary>
    /// Enumerates top-level transforms belonging to <paramref name="owner"/>.
    /// When <paramref name="includeAtSea"/> is false (ship hull propulsion), open-sea boats,
    /// open-water swimmers, and off-board stored buildables are left at their ocean positions.
    /// When <paramref name="includeAtSea"/> is true (world-origin anchor rebase), all owned scene
    /// objects are rebased together so they remain stationary in canonical ocean coordinates.
    /// </summary>
    public static IEnumerable<Transform> TopLevelTransforms(Community owner, bool includeAtSea = false)
    {
        var movingBuildings = new HashSet<Buildable>();
        foreach (var building in owner.Buildables)
        {
            if (building == null) continue;
            if (!includeAtSea)
            {
                if (MoveConstructionCommands.IsStored(building)) continue;
                if (building.TryGetComponent<Boat>(out var boat) && boat.CurrentMooringPoint == null) continue;
            }
            movingBuildings.Add(building);
        }

        foreach (var building in movingBuildings)
        {
            var parent = building.transform.parent;
            var nested = false;
            while (parent != null)
            {
                if (parent.GetComponent<Buildable>() is Buildable b && movingBuildings.Contains(b)) { nested = true; break; }
                parent = parent.parent;
            }
            if (!nested) yield return building.transform;
        }

        foreach (var agent in owner.Agents)
        {
            if (agent == null) continue;
            if (!includeAtSea)
            {
                if (agent.Boat != null && agent.Boat.CurrentMooringPoint == null) continue;
                var nav = agent.ReturnNavigator(true);
                if (nav != null && (nav.Terrain == Navigator.TerrainType.WaterSurface || nav.Terrain == Navigator.TerrainType.Underwater))
                    continue;
            }
            var parent = agent.transform.parent;
            var nested = false;
            while (parent != null)
            {
                if (parent.GetComponent<Buildable>() is Buildable b && movingBuildings.Contains(b)) { nested = true; break; }
                parent = parent.parent;
            }
            if (!nested) yield return agent.transform;
        }

        if (owner.Birds != null)
        {
            foreach (var bird in owner.Birds)
            {
                if (bird == null) continue;
                if (!includeAtSea && (bird.State == Bird.BirdState.GettingItem || bird.State == Bird.BirdState.LeavingWorld))
                    continue;
                var parent = bird.transform.parent;
                var nested = false;
                while (parent != null)
                {
                    if (parent.GetComponent<Buildable>() is Buildable b && movingBuildings.Contains(b)) { nested = true; break; }
                    parent = parent.parent;
                }
                if (!nested) yield return bird.transform;
            }
        }
    }

    public static void RefreshCaches(Community owner)
    {
        foreach (var building in owner.Buildables)
        {
            if (building == null || MoveConstructionCommands.IsStored(building)) continue;
            building.OutlinePolygon?.Update(false);
            building.BlockingPolygon?.Update(false);
            foreach (var obstacle in building.GetComponentsInChildren<Obstacle>(true))
            {
                if (obstacle == null) continue;
                if (obstacle.Polygon != null)
                {
                    obstacle.Polygon.Update(false);
                    obstacle.SetRadius();
                }
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
