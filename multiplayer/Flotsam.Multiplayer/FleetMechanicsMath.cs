using System;

namespace Flotsam.Multiplayer;

public enum BoardingOutcome
{
    CleanSuccess = 0,
    BrawlPartial = 1,
    Repelled = 2
}

public readonly struct HullDisc
{
    public readonly float X;
    public readonly float Z;
    public readonly float Radius;

    public HullDisc(float x, float z, float radius)
    {
        X = x;
        Z = z;
        Radius = radius;
    }
}

public readonly struct HullObstacle
{
    public readonly float X;
    public readonly float Z;
    public readonly float Radius;
    public readonly bool IsShip;
    public readonly int Tag;

    public HullObstacle(float x, float z, float radius, bool isShip, int tag = 0)
    {
        X = x;
        Z = z;
        Radius = radius;
        IsShip = isShip;
        Tag = tag;
    }
}

/// <summary>
/// Pure deterministic math for structural durability, 3-tier ballistics, boarding defense,
/// asymmetric WatchTower fog-of-war vision, and multi-disc tangential hull sliding &amp; ramming.
/// Shared with Protocol.Tests (zero Unity dependency).
/// </summary>
public static class FleetMechanicsMath
{
    public const float BaseVisionMeters = 400f;
    public const float WatchTowerVisionMeters = 750f;
    public const float FrogmanMaxSwimMeters = 85f;

    public static int ClassifyMaterialTier(string? itemName)
    {
        if (string.IsNullOrEmpty(itemName))
            return 1;

        var n = itemName!;
        if (ContainsIgnoreCase(n, "ScrapMetal") || ContainsIgnoreCase(n, "Metal") ||
            ContainsIgnoreCase(n, "Iron") || ContainsIgnoreCase(n, "Steel") ||
            ContainsIgnoreCase(n, "Circuit") || ContainsIgnoreCase(n, "Electronic") ||
            ContainsIgnoreCase(n, "Copper") || ContainsIgnoreCase(n, "Battery"))
        {
            return 3;
        }

        if (ContainsIgnoreCase(n, "WetWood") || ContainsIgnoreCase(n, "Plastic") ||
            ContainsIgnoreCase(n, "Seaweed") || ContainsIgnoreCase(n, "Trash") ||
            ContainsIgnoreCase(n, "Bottle"))
        {
            return 1;
        }

        if (ContainsIgnoreCase(n, "DryWood") || ContainsIgnoreCase(n, "Plank") ||
            ContainsIgnoreCase(n, "Wood") || ContainsIgnoreCase(n, "Rope") ||
            ContainsIgnoreCase(n, "Fabric") || ContainsIgnoreCase(n, "Cloth") ||
            ContainsIgnoreCase(n, "Canvas") || ContainsIgnoreCase(n, "Glass"))
        {
            return 2;
        }

        return 1;
    }

    public static float ComputeMaxStructuralHp(bool isTownheart, int tier1Count, int tier2Count, int tier3Count)
    {
        var t1 = Math.Max(0, tier1Count);
        var t2 = Math.Max(0, tier2Count);
        var t3 = Math.Max(0, tier3Count);
        if (isTownheart)
            return 1200f + t2 * 20f + t3 * 40f;

        return MathF.Max(90f, 60f + t1 * 25f + t2 * 45f + t3 * 85f);
    }

    public static float ComputeArmorReduction(int tier3Count, int totalCount)
    {
        if (totalCount <= 0 || tier3Count <= 0)
            return 0f;
        var ratio = MathF.Min(1f, (float)tier3Count / totalCount);
        return MathF.Min(0.45f, ratio * 0.55f);
    }

    public static float ComputeCannonDamage(int ammoTier, float armorReduction)
    {
        var armor = Math.Clamp(armorReduction, 0f, 0.6f);
        switch (ammoTier)
        {
            case 0: // Makeshift WetWood / Seaweed shot
                return MathF.Max(14f, 44f * (1f - MathF.Min(0.75f, armor * 1.4f)));
            case 2: // Eel Overcharge EMP shell (bypasses armor + disables grid)
                return 95f;
            default: // Tier 1 Scrap Metal Shrapnel
                return MathF.Max(45f, 120f * (1f - armor * 0.45f));
        }
    }

    public static float ApplyDamageWithFloor(float currentHp, float maxHp, float damage, bool isTownheart)
    {
        if (maxHp <= 0f)
            return 0f;
        var floor = isTownheart ? maxHp * 0.15f : 0f;
        return MathF.Max(floor, currentHp - MathF.Max(0f, damage));
    }

    public static bool IsCrippledMalfunction(float currentHp, float maxHp)
    {
        return maxHp > 0f && (currentHp / maxHp) < 0.60f;
    }

    public static bool IsTownheartLeakParalyzed(float currentHp, float maxHp)
    {
        return maxHp > 0f && (currentHp / maxHp) <= 0.35f;
    }

    public static int ComputeRepairCostCount(float currentHp, float maxHp, int recipeTotalItems)
    {
        if (maxHp <= 0f || currentHp >= maxHp - 0.01f)
            return 0;
        var missingFraction = Math.Clamp(1f - currentHp / maxHp, 0f, 1f);
        var basePool = MathF.Max(2f, recipeTotalItems * 0.45f);
        return Math.Clamp((int)MathF.Ceiling(basePool * missingFraction), 1, 5);
    }

    public static int ComputeCollapseSalvageCount(int amount)
    {
        return amount <= 0 ? 0 : Math.Max(1, (amount + 1) / 2);
    }

    public static float GetVisionRadius(bool hasActiveWatchTower)
    {
        return hasActiveWatchTower ? WatchTowerVisionMeters : BaseVisionMeters;
    }

    public static bool IsShipSpotted(
        float observerX,
        float observerZ,
        float visionRadius,
        float targetX,
        float targetZ,
        bool allied,
        bool targetFlared)
    {
        if (allied || targetFlared)
            return true;
        var dx = targetX - observerX;
        var dz = targetZ - observerZ;
        return dx * dx + dz * dz <= visionRadius * visionRadius;
    }

    public static BoardingOutcome ResolveBoarding(
        int attackerAthletics,
        bool isStealthSwimmer,
        int awakeDefendersNearTarget,
        float deterministicRoll01,
        out float lootMultiplier,
        out float pollutionPenalty)
    {
        var effectiveDefenders = Math.Max(0, awakeDefendersNearTarget - (isStealthSwimmer ? 1 : 0));
        if (effectiveDefenders == 0)
        {
            lootMultiplier = 1f;
            pollutionPenalty = 0f;
            return BoardingOutcome.CleanSuccess;
        }

        var attackPower = 1.0f + Math.Clamp(attackerAthletics, 0, 10) * 0.22f + (isStealthSwimmer ? 0.25f : 0f);
        var defensePower = effectiveDefenders * 0.85f;
        var winRatio = attackPower / (attackPower + defensePower);
        var roll = Math.Clamp(deterministicRoll01, 0f, 1f);
        var margin = winRatio - roll * 0.55f;

        if (margin >= 0.48f)
        {
            lootMultiplier = 1f;
            pollutionPenalty = 0f;
            return BoardingOutcome.CleanSuccess;
        }

        if (margin >= 0.18f)
        {
            lootMultiplier = 0.5f;
            pollutionPenalty = 14f;
            return BoardingOutcome.BrawlPartial;
        }

        lootMultiplier = 0f;
        pollutionPenalty = 28f;
        return BoardingOutcome.Repelled;
    }

    public static bool SolveHullSlideAndRam(
        HullDisc[] movingDiscs,
        float stepX,
        float stepZ,
        HullObstacle[] obstacles,
        float closingSpeedMps,
        out float resolvedStepX,
        out float resolvedStepZ,
        out float ramDamage,
        out int hitObstacleIndex)
    {
        resolvedStepX = stepX;
        resolvedStepZ = stepZ;
        ramDamage = 0f;
        hitObstacleIndex = -1;

        if (movingDiscs == null || movingDiscs.Length == 0 || obstacles == null || obstacles.Length == 0)
            return false;

        var worstPenetration = 0f;
        var normalX = 0f;
        var normalZ = 1f;

        for (var i = 0; i < movingDiscs.Length; i++)
        {
            var disc = movingDiscs[i];
            var nextX = disc.X + stepX;
            var nextZ = disc.Z + stepZ;

            for (var j = 0; j < obstacles.Length; j++)
            {
                var obs = obstacles[j];
                var minSep = disc.Radius + obs.Radius;
                var dx = nextX - obs.X;
                var dz = nextZ - obs.Z;
                var distSq = dx * dx + dz * dz;
                if (distSq >= minSep * minSep)
                    continue;

                var dist = MathF.Sqrt(distSq);
                var pen = minSep - dist;
                if (pen <= worstPenetration)
                    continue;

                worstPenetration = pen;
                hitObstacleIndex = j;
                var curDx = disc.X - obs.X;
                var curDz = disc.Z - obs.Z;
                var curLen = MathF.Sqrt(curDx * curDx + curDz * curDz);
                if (curLen > 0.0001f)
                {
                    normalX = curDx / curLen;
                    normalZ = curDz / curLen;
                }
                else if (dist > 0.0001f)
                {
                    normalX = dx / dist;
                    normalZ = dz / dist;
                }
            }
        }

        if (hitObstacleIndex < 0)
            return false;

        var inwardDot = stepX * normalX + stepZ * normalZ;
        var slideX = stepX;
        var slideZ = stepZ;
        if (inwardDot < 0f)
        {
            slideX -= inwardDot * normalX;
            slideZ -= inwardDot * normalZ;
        }

        var pushOut = MathF.Min(0.45f, worstPenetration * 0.35f);
        var candidateX = slideX + normalX * pushOut;
        var candidateZ = slideZ + normalZ * pushOut;

        // Verify candidate doesn't worsen penetration into another obstacle in a tight channel.
        var candidateWorst = MaxPenetration(movingDiscs, candidateX, candidateZ, obstacles);
        var currentWorst = MaxPenetration(movingDiscs, 0f, 0f, obstacles);
        if (candidateWorst <= currentWorst + 0.02f)
        {
            resolvedStepX = candidateX;
            resolvedStepZ = candidateZ;
        }
        else
        {
            resolvedStepX = normalX * pushOut;
            resolvedStepZ = normalZ * pushOut;
        }

        var hitObs = obstacles[hitObstacleIndex];
        var stepLen = MathF.Sqrt(stepX * stepX + stepZ * stepZ);
        if (hitObs.IsShip && closingSpeedMps >= 8.5f && inwardDot < -0.0005f && stepLen > 0.0005f)
        {
            var headOn = Math.Clamp(-inwardDot / stepLen, 0.25f, 1f);
            ramDamage = Math.Clamp((closingSpeedMps - 7.0f) * 8.5f * headOn, 12f, 180f);
        }

        return true;
    }

    private static float MaxPenetration(HullDisc[] discs, float dxStep, float dzStep, HullObstacle[] obstacles)
    {
        var maxPen = 0f;
        for (var i = 0; i < discs.Length; i++)
        {
            var disc = discs[i];
            var px = disc.X + dxStep;
            var pz = disc.Z + dzStep;
            for (var j = 0; j < obstacles.Length; j++)
            {
                var obs = obstacles[j];
                var minSep = disc.Radius + obs.Radius;
                var dx = px - obs.X;
                var dz = pz - obs.Z;
                var distSq = dx * dx + dz * dz;
                if (distSq >= minSep * minSep)
                    continue;
                var pen = minSep - MathF.Sqrt(distSq);
                if (pen > maxPen)
                    maxPen = pen;
            }
        }
        return maxPen;
    }

    private static bool ContainsIgnoreCase(string haystack, string needle)
    {
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
