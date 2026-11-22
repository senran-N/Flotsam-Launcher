using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Real multi-disc ship hull collision, tangential island/hull sliding, and high-speed ship-to-ship
/// ramming physics powered by <see cref="FleetMechanicsMath.SolveHullSlideAndRam"/>.
/// </summary>
public static class FleetHullCollision
{
    private const float DefaultDiscRadius = 2.4f;
    private static readonly Dictionary<string, float> RamCooldownUntil = new Dictionary<string, float>(StringComparer.Ordinal);

    internal static void Reset() => RamCooldownUntil.Clear();

    public static bool TryMove(
        Community owner,
        Vector3 current,
        Vector3 target,
        out Vector3 accepted,
        out string reason,
        float dt = 0.05f)
    {
        accepted = target;
        reason = "";
        if (owner == null)
        {
            reason = "owner missing";
            return false;
        }

        var stepX = target.x - current.x;
        var stepZ = target.z - current.z;
        var stepLen = Mathf.Sqrt(stepX * stepX + stepZ * stepZ);
        if (stepLen <= 0.0001f)
            return true;

        var discs = BuildMovingHullDiscs(owner, current);
        var maxExtent = ComputeEnvelopeRadius(discs, current);
        var searchCenter = new Vector3((current.x + target.x) * 0.5f, current.y + 0.5f, (current.z + target.z) * 0.5f);
        var searchRadius = maxExtent + stepLen + 14f;

        CollectObstacles(owner, searchCenter, searchRadius, out var obstacles, out var obstacleBuildings);
        if (obstacles.Length == 0)
            return true;

        var closingSpeed = dt > 0.001f ? stepLen / dt : stepLen * 20f;
        var collided = FleetMechanicsMath.SolveHullSlideAndRam(
            discs,
            stepX,
            stepZ,
            obstacles,
            closingSpeed,
            out var resolvedDx,
            out var resolvedDz,
            out var ramDamage,
            out var hitIdx);

        if (!collided)
            return true;

        if (hitIdx >= 0 && hitIdx < obstacleBuildings.Count && ramDamage > 0.5f)
        {
            var struck = obstacleBuildings[hitIdx];
            if (struck != null && struck.Community != null && !ReferenceEquals(struck.Community, owner))
                HandleShipRamImpact(owner, struck, ramDamage);
        }

        var resolvedSq = resolvedDx * resolvedDx + resolvedDz * resolvedDz;
        if (resolvedSq <= 0.000025f)
        {
            accepted = current;
            reason = "head-on hull block";
            return false;
        }

        accepted = new Vector3(current.x + resolvedDx, current.y, current.z + resolvedDz);
        return true;
    }

    private static void HandleShipRamImpact(Community attacker, Buildable struckBuilding, float ramDamage)
    {
        var aKey = Replication.ShipKey(attacker);
        var bKey = Replication.ShipKey(struckBuilding.Community);
        var pairKey = string.CompareOrdinal(aKey, bKey) <= 0 ? aKey + "|" + bKey : bKey + "|" + aKey;
        var now = Time.realtimeSinceStartup;
        if (RamCooldownUntil.TryGetValue(pairKey, out var until) && now < until)
            return;
        RamCooldownUntil[pairKey] = now + 2.5f;

        var struckPos = struckBuilding.transform.position;
        var ownOuter = attacker.Buildables?
            .Where(b => b != null && !MoveConstructionCommands.IsStored(b) && (!b.TryGetComponent<Boat>(out var bt) || bt.CurrentMooringPoint != null))
            .OrderBy(b => (b.transform.position - struckPos).sqrMagnitude)
            .FirstOrDefault();

        var midPos = ownOuter != null ? (ownOuter.transform.position + struckPos) * 0.5f : struckPos;
        FleetPhysicalActors.SpawnWaterShockwaveRing(midPos, new Color(1.0f, 0.52f, 0.16f, 0.95f), 6.2f);
        FleetPhysicalActors.PlayFleetSound("shell_impact", midPos, 0.95f);
        FleetPhysicalActors.TriggerCameraShake(midPos, 0.48f, 0.35f);

        if (Plugin.Options.rules.friendlyFire)
        {
            FleetVisibility.CheckAndBreakAllianceOnHostileAction(attacker, struckBuilding.Community);
            BuildingDurability.ApplyDamage(struckBuilding, ramDamage, applyEmp: false, struckPos);
            if (ownOuter != null)
                BuildingDurability.ApplyDamage(ownOuter, ramDamage * 0.65f, applyEmp: false, ownOuter.transform.position);
        }
        else
        {
            FleetPhysicalActors.SpawnNativePopup(midPos + Vector3.up * 2.8f, "⚓ 船体擦碰缓冲 (未开启友伤火炮)");
        }
    }

    private static HullDisc[] BuildMovingHullDiscs(Community owner, Vector3 fallbackCenter)
    {
        var list = new List<HullDisc>();
        if (owner.Buildables != null)
        {
            foreach (var b in owner.Buildables)
            {
                if (b == null || MoveConstructionCommands.IsStored(b)) continue;
                if (b.TryGetComponent<Boat>(out var boat) && boat.CurrentMooringPoint == null) continue;
                var pos = b.transform.position;
                var r = BuildingDiscRadius(b);
                list.Add(new HullDisc(pos.x, pos.z, r));
            }
        }
        if (list.Count == 0)
            list.Add(new HullDisc(fallbackCenter.x, fallbackCenter.z, DefaultDiscRadius));
        return list.ToArray();
    }

    private static float ComputeEnvelopeRadius(HullDisc[] discs, Vector3 center)
    {
        var maxR = DefaultDiscRadius;
        for (var i = 0; i < discs.Length; i++)
        {
            var dx = discs[i].X - center.x;
            var dz = discs[i].Z - center.z;
            var extent = Mathf.Sqrt(dx * dx + dz * dz) + discs[i].Radius;
            if (extent > maxR) maxR = extent;
        }
        return Mathf.Min(maxR, 60f);
    }

    private static void CollectObstacles(
        Community movingOwner,
        Vector3 searchCenter,
        float searchRadius,
        out HullObstacle[] obstacles,
        out List<Buildable?> obstacleBuildings)
    {
        var obsList = new List<HullObstacle>();
        obstacleBuildings = new List<Buildable?>();
        var seenBuildings = new HashSet<int>();
        var seenColliders = new HashSet<int>();
        var rSq = searchRadius * searchRadius;

        // 1. Other player ships' deck buildings (exact multi-disc ship-vs-ship envelope)
        foreach (var comm in Community.Communities)
        {
            if (comm == null || ReferenceEquals(comm, movingOwner) || comm.Buildables == null)
                continue;
            var isPlayerFleetShip = comm.IsPlayerCommunity() || FleetEngineBundle.IsSecondary(comm) ||
                                    (!string.IsNullOrEmpty(comm.Name) && comm.Name.StartsWith("Ship:", StringComparison.Ordinal));
            if (!isPlayerFleetShip) continue;

            foreach (var b in comm.Buildables)
            {
                if (b == null || MoveConstructionCommands.IsStored(b)) continue;
                if (b.TryGetComponent<Boat>(out var boat) && boat.CurrentMooringPoint == null) continue;
                var pos = b.transform.position;
                var dx = pos.x - searchCenter.x;
                var dz = pos.z - searchCenter.z;
                if (dx * dx + dz * dz > rSq) continue;
                if (!seenBuildings.Add(b.GetInstanceID())) continue;

                obsList.Add(new HullObstacle(pos.x, pos.z, BuildingDiscRadius(b), isShip: true, tag: b.GetInstanceID()));
                obstacleBuildings.Add(b);
            }
        }

        // 2. Nearby 3D Island / Landmark colliders
        var hits = Physics.OverlapSphere(searchCenter, searchRadius, ~0, QueryTriggerInteraction.Ignore);
        foreach (var hit in hits)
        {
            if (hit == null || !seenColliders.Add(hit.GetInstanceID())) continue;
            if (hit.GetComponentInParent<Agent>() != null) continue;

            var buildable = hit.GetComponentInParent<Buildable>();
            if (buildable != null)
            {
                if (ReferenceEquals(buildable.Community, movingOwner)) continue;
                if (!seenBuildings.Add(buildable.GetInstanceID())) continue;
                var isOtherShip = buildable.Community != null &&
                                  (buildable.Community.IsPlayerCommunity() || FleetEngineBundle.IsSecondary(buildable.Community));
                var bp = buildable.transform.position;
                obsList.Add(new HullObstacle(bp.x, bp.z, BuildingDiscRadius(buildable), isShip: isOtherShip, tag: buildable.GetInstanceID()));
                obstacleBuildings.Add(isOtherShip ? buildable : null);
                continue;
            }

            var boat = hit.GetComponentInParent<Boat>();
            if (boat != null)
            {
                if (ReferenceEquals(boat.Buildable?.Community, movingOwner) ||
                    ReferenceEquals(boat.TownMooringPoint?.Buildable?.Community, movingOwner))
                    continue;
            }

            if (hit.GetComponentInParent<Landmark>() != null)
            {
                var bounds = hit.bounds;
                var lx = bounds.center.x;
                var lz = bounds.center.z;
                var lr = Mathf.Clamp(Mathf.Max(bounds.extents.x, bounds.extents.z), 1.6f, 28f);
                obsList.Add(new HullObstacle(lx, lz, lr, isShip: false, tag: hit.GetInstanceID()));
                obstacleBuildings.Add(null);
            }
        }

        obstacles = obsList.ToArray();
    }

    private static float BuildingDiscRadius(Buildable building)
    {
        var polygon = building.OutlinePolygon ?? building.BlockingPolygon;
        if (polygon != null)
        {
            var r = new Vector2(polygon.Bounds.width, polygon.Bounds.height).magnitude * 0.42f;
            if (r > 0.4f)
                return Mathf.Clamp(r, 1.2f, 6.5f);
        }
        return DefaultDiscRadius;
    }

    public static float HullRadius(Community owner)
    {
        if (owner == null) return DefaultDiscRadius;
        var heart = FleetEngineBundle.HeartBuildable(owner);
        var center = heart != null ? heart.transform.position : Vector3.zero;
        return ComputeEnvelopeRadius(BuildMovingHullDiscs(owner, center), center);
    }
}
