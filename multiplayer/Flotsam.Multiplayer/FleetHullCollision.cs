using System;
using System.Linq;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Coarse hull/environment collision gate for the continuous navigation layer. It is intentionally
/// conservative: it samples the target position against native 3D colliders, ignores the moving
/// owner's own objects/agents and rejects the step instead of sliding. Real per-owner rigid-body
/// physics and full swept-continuum collision remain a later batch.
/// </summary>
public static class FleetHullCollision
{
    private const float DefaultHullRadius = 2.6f;

    public static bool TryMove(Community owner, Vector3 current, Vector3 target, out Vector3 accepted, out string reason)
    {
        accepted = target;
        reason = "";
        if (owner == null) { reason = "owner missing"; return false; }
        var radius = HullRadius(owner);
        var y = current.y + 0.5f;
        var hits = Physics.OverlapSphere(new Vector3(target.x, y, target.z), radius, ~0, QueryTriggerInteraction.Ignore);
        foreach (var hit in hits)
        {
            if (hit == null) continue;
            var buildable = hit.GetComponentInParent<Buildable>();
            if (buildable != null)
            {
                if (ReferenceEquals(buildable.Community, owner)) continue;
                // Landmarks, primary buildings and other owners block; report the first concrete hit.
                reason = "hull collision with " + (buildable.Properties == null ? hit.name : buildable.Properties.name);
                return false;
            }
            if (hit.GetComponentInParent<Agent>() != null) continue;
            if (hit.GetComponentInParent<Boat>() != null || hit.GetComponentInParent<Landmark>() != null)
            {
                reason = "hull collision with " + hit.name;
                return false;
            }
        }
        return true;
    }

    public static float HullRadius(Community owner)
    {
        var radius = 0f;
        foreach (var building in owner.Buildables.Where(b => b != null))
        {
            var polygon = building.OutlinePolygon ?? building.BlockingPolygon;
            if (polygon == null) continue;
            radius = Mathf.Max(radius, new Vector2(polygon.Bounds.width, polygon.Bounds.height).magnitude * 0.5f);
        }
        return Mathf.Clamp(radius <= 0f ? DefaultHullRadius : radius, 1.5f, 8f);
    }
}
