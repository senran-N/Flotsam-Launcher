using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

// Replica-only geometry-cache maintenance. Call after BOTH building application passes
// (including walkway endpoints/scale) and Reset on replica/world reset. Never registers
// nodes, changes hook eligibility, initializes a component or rebuilds pathfinding graphs.
internal static class ReplicaGeometry
{
    private sealed class Entry
    {
        internal readonly Buildable Building;
        internal readonly Vector3 Position;
        internal readonly Quaternion Rotation;
        internal readonly Vector3[] Corners;
        internal readonly Obstacle[] Obstacles;
        internal readonly Vector3[] ObstaclePositions;
        internal readonly Quaternion[] ObstacleRotations;
        internal readonly Hookable? Hook;
        internal readonly int Refreshes;
        internal Entry(Buildable building,Obstacle[] obstacles,int refreshes)
        {
            Building=building;Position=building.transform.position;Rotation=building.transform.rotation;
            Corners=building.OutlineCorners.Select(c=>c.position).ToArray();
            Obstacles=obstacles;ObstaclePositions=obstacles.Select(o=>o.transform.position).ToArray();
            ObstacleRotations=obstacles.Select(o=>o.transform.rotation).ToArray();
            Hook=building.GetComponent<Hookable>();Refreshes=refreshes;
        }
        internal bool Matches(Buildable building,Obstacle[] obstacles)
        {
            if(Building!=building||Position!=building.transform.position||Rotation!=building.transform.rotation||Corners.Length!=building.OutlineCorners.Count||Hook!=building.GetComponent<Hookable>()||!Obstacles.SequenceEqual(obstacles))return false;
            for(var i=0;i<Corners.Length;i++)if(Corners[i]!=building.OutlineCorners[i].position)return false;
            for(var i=0;i<obstacles.Length;i++)if(ObstaclePositions[i]!=obstacles[i].transform.position||ObstacleRotations[i]!=obstacles[i].transform.rotation)return false;
            return true;
        }
    }
    private static readonly Dictionary<int,Entry> Cache=new Dictionary<int,Entry>();
    private static readonly MethodInfo InitializeEdges=AccessTools.DeclaredMethod(typeof(Hookable),"InitializeEdges",new[]{typeof(Buildable)});
    private static readonly FieldInfo Edges=AccessTools.Field(typeof(Hookable),"_edges");
    private static readonly MethodInfo UpdateTargetPosition=AccessTools.DeclaredMethod(typeof(Target),"UpdatePosition",Type.EmptyTypes);
    internal static void Reset()=>Cache.Clear();

    // Returns true when refreshed, false for unchanged/unsupported/unready objects. Does not
    // require Network.Applying so the existing replica Apply/isolated smoke can call it; a
    // configured host can never enter this path accidentally.
    internal static bool RefreshReplicaPose(Buildable building)
    {
        if(Plugin.Instance?.Network?.IsClient!=true||building==null||building.OutlineCorners==null||building.OutlineCorners.Count==0)return false;
        var id=building.GetInstanceID();
        var obstacles=building.GetComponentsInChildren<Obstacle>(true);
        Cache.TryGetValue(id,out var previous);
        if(previous!=null&&previous.Matches(building,obstacles))return false;
        // Update(false) refreshes world vertices AND bounds/radius/Position2D. FastUpdate
        // refreshes only vertices/projections, and Update(true) skips an in-place rotation.
        building.OutlinePolygon?.Update(false);
        building.BlockingPolygon?.Update(false);
        if(building.TryGetComponent<Hookable>(out var hook))
        {
            var edges=(Hookable.Edge[]?)Edges.GetValue(hook);
            // Native InitializeEdges reallocates only a null/empty array. Outline topology
            // changes require discarding just this derived cache before the native rebuild.
            if(edges!=null&&edges.Length!=building.OutlineCorners.Count)Edges.SetValue(hook,null);
            InitializeEdges.Invoke(hook,new object[]{building});
        }
        foreach(var obstacle in obstacles)
        {
            if(obstacle.Polygon!=null)
            {
                obstacle.Polygon.Update(false);
                obstacle.SetRadius();
            }
            // This native method only assigns Target.Position (and updates an override).
            UpdateTargetPosition.Invoke(obstacle,Array.Empty<object>());
        }
        Cache[id]=new Entry(building,obstacles,(previous?.Refreshes??0)+1);
        // Object references in this small cache must not retain removed replica hierarchies.
        foreach(var stale in Cache.Where(pair=>pair.Value.Building==null).Select(pair=>pair.Key).ToArray())Cache.Remove(stale);
        return true;
    }
}
