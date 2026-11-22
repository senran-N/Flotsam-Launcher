using System;
using System.Collections.Generic;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Renders authentic 3D ocean sailing animations for every moving ship in the session
/// (Host &amp; Client, Shared Co-op &amp; Independent Fleet):
/// - Layered V-shaped parting whitewater bow waves ahead of the hull's foremost deck
/// - Ocean-anchored dual whitewater stern wake ribbons that stay pinned to the sea surface
/// - Periodic engine prop-wash ripples &amp; bow water splashes
/// - Subtle dynamic bow-lift pitch &amp; sea-swell roll on the Townheart visual mesh
/// - 3D ocean helm course target ring &amp; heading waterline for <c>Shift + RightClick</c> steering
/// </summary>
internal static class ShipSailingVisuals
{
    private struct WakeSample
    {
        public float MapX;
        public float MapZ;
        public float RightMapX;
        public float RightMapZ;
        public float CreatedAt;
    }

    private sealed class ShipWakeEntry
    {
        public GameObject Root = null!;
        public LineRenderer BowPrimary = null!;
        public LineRenderer BowSecondary = null!;
        public LineRenderer PortWake = null!;
        public LineRenderer StarboardWake = null!;
        public readonly List<WakeSample> PortSamples = new List<WakeSample>(40);
        public readonly List<WakeSample> StarboardSamples = new List<WakeSample>(40);
        public Vector3 LastOceanPos;
        public bool HasOceanPos;
        public Vector3 SmoothedOceanVel;
        public Vector3 LastMoveDir = Vector3.forward;
        public float CachedForemost = 6.5f;
        public float CachedAftmost = -5.5f;
        public float CachedHalfBeam = 4.2f;
        public float NextBoundsScanAt;
        public float NextWakeSampleAt;
        public float NextSplashAt;
        public float NextPropWashAt;
        public Transform? TiltedVisual;
        public Buildable? BoundHeart;
        public Animator[] PropAnimators = Array.Empty<Animator>();
        public Transform[] RotorTransforms = Array.Empty<Transform>();
    }

    private static readonly Dictionary<string, ShipWakeEntry> Entries =
        new Dictionary<string, ShipWakeEntry>(StringComparer.Ordinal);

    private static GameObject? _courseRoot;
    private static LineRenderer? _courseRing;
    private static LineRenderer? _courseLine;
    private static Vector2 _courseMapTarget;
    private static float _courseExpiresAt;
    private static bool _courseActive;

    private const float WakeLifetime = 4.2f;
    private const int BowSegments = 17;

    internal static void Reset()
    {
        foreach (var entry in Entries.Values)
        {
            if (entry.TiltedVisual != null)
                entry.TiltedVisual.localRotation = Quaternion.identity;
            if (entry.Root != null)
                UnityEngine.Object.Destroy(entry.Root);
        }
        Entries.Clear();
        ClearCourseIndicator();
    }

    internal static void SetCourseIndicator(float mapX, float mapZ)
    {
        if (!Replication.Finite(mapX) || !Replication.Finite(mapZ)) return;
        _courseMapTarget = new Vector2(mapX, mapZ);
        _courseExpiresAt = Time.unscaledTime + 45f;
        _courseActive = true;

        if (_courseRoot == null)
        {
            _courseRoot = new GameObject("Flotsam_HelmCourseIndicator");
            var ringGo = new GameObject("CourseTargetRing");
            ringGo.transform.SetParent(_courseRoot.transform, false);
            _courseRing = ringGo.AddComponent<LineRenderer>();
            _courseRing.useWorldSpace = false;
            _courseRing.loop = true;
            _courseRing.material = FleetPhysicalActors.GetEmissiveMaterial(new Color(0.42f, 0.95f, 0.80f, 0.90f));
            _courseRing.startWidth = 0.24f;
            _courseRing.endWidth = 0.24f;
            _courseRing.positionCount = 32;

            var lineGo = new GameObject("CourseWaterline");
            lineGo.transform.SetParent(_courseRoot.transform, false);
            _courseLine = lineGo.AddComponent<LineRenderer>();
            _courseLine.useWorldSpace = true;
            _courseLine.material = FleetPhysicalActors.GetEmissiveMaterial(new Color(0.42f, 0.95f, 0.80f, 0.42f));
            _courseLine.startWidth = 0.22f;
            _courseLine.endWidth = 0.08f;
            _courseLine.positionCount = 2;
        }

        var sceneTarget = FleetOwnedNavigation.MapToScene(mapX, mapZ);
        FleetPhysicalActors.SpawnWaterShockwaveRing(sceneTarget, new Color(0.42f, 0.95f, 0.80f, 0.85f), 5.2f);
    }

    internal static void ClearCourseIndicator()
    {
        _courseActive = false;
        if (_courseRoot != null)
        {
            UnityEngine.Object.Destroy(_courseRoot);
            _courseRoot = null;
            _courseRing = null;
            _courseLine = null;
        }
    }

    internal static void Tick(float dt)
    {
        if (!GameSession.Ready() || dt <= 0.0001f || dt > 0.5f) return;

        var now = Time.unscaledTime;
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);

        if (Community.PlayerCommunity != null)
            UpdateCommunityShip(Community.PlayerCommunity, dt, now, activeKeys);

        foreach (var sec in FleetEngineBundle.SecondaryCommunities)
        {
            if (sec != null)
                UpdateCommunityShip(sec, dt, now, activeKeys);
        }

        // On an Independent-mode client, the Host's ship and other peers' ships are regular communities
        if (Plugin.Instance?.Network?.IsClient == true)
        {
            foreach (var comm in Community.Communities)
            {
                if (comm == null || ReferenceEquals(comm, Community.PlayerCommunity)) continue;
                var key = Replication.ShipKey(comm);
                if (activeKeys.Contains(key)) continue;
                if (comm.Buildables == null || comm.Buildables.Count == 0) continue;
                UpdateCommunityShip(comm, dt, now, activeKeys);
            }
        }

        foreach (var kv in new List<KeyValuePair<string, ShipWakeEntry>>(Entries))
        {
            if (!activeKeys.Contains(kv.Key))
            {
                if (kv.Value.TiltedVisual != null)
                    kv.Value.TiltedVisual.localRotation = Quaternion.identity;
                if (kv.Value.Root != null)
                    UnityEngine.Object.Destroy(kv.Value.Root);
                Entries.Remove(kv.Key);
            }
        }

        TickCourseIndicator(now);
    }

    private static void UpdateCommunityShip(Community community, float dt, float now, HashSet<string> activeKeys)
    {
        Buildable? heart = FleetEngineBundle.HeartBuildable(community);
        if (heart == null && ReferenceEquals(community, Community.PlayerCommunity))
            heart = Construction.Townheart?.Buildable;
        if (heart == null)
        {
            foreach (var b in community.Buildables)
            {
                if (b != null && (b.TryGetComponent<TownheartStorage>(out _) || b.TryGetComponent<Engine>(out _)))
                {
                    heart = b;
                    break;
                }
            }
        }
        if (heart == null) return;

        var key = Replication.ShipKey(community);
        if (string.IsNullOrEmpty(key)) return;
        activeKeys.Add(key);

        if (!Entries.TryGetValue(key, out var entry) || entry.Root == null)
        {
            entry = CreateWakeEntry(key);
            Entries[key] = entry;
        }

        if (!ReferenceEquals(entry.BoundHeart, heart))
        {
            entry.BoundHeart = heart;
            entry.PropAnimators = heart.GetComponentsInChildren<Animator>(true);
            var rotors = new List<Transform>();
            foreach (var tr in heart.GetComponentsInChildren<Transform>(true))
            {
                if (tr == null || tr == heart.transform || (heart.SpawnedVisual != null && tr == heart.SpawnedVisual.transform))
                    continue;
                var n = tr.name;
                if (n.IndexOf("Propeller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Rotor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Paddle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Wheel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Fan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Blade", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    rotors.Add(tr);
                }
            }
            entry.RotorTransforms = rotors.ToArray();
        }

        var heartScenePos = heart.transform.position;
        var oceanPos = FleetOwnedNavigation.SceneToMap(heartScenePos);
        oceanPos.y = 0f;

        if (!entry.HasOceanPos)
        {
            entry.LastOceanPos = oceanPos;
            entry.HasOceanPos = true;
            return;
        }

        var oceanDelta = oceanPos - entry.LastOceanPos;
        oceanDelta.y = 0f;
        entry.LastOceanPos = oceanPos;

        // Ignore massive resync/rebase teleports (> 60m in a single frame)
        var rawSpeed = oceanDelta.magnitude / dt;
        var instOceanVel = oceanDelta.sqrMagnitude > 3600f || rawSpeed > 80f
            ? Vector3.zero
            : oceanDelta / dt;

        var velBlend = 1f - Mathf.Exp(-10f * dt);
        entry.SmoothedOceanVel = Vector3.Lerp(entry.SmoothedOceanVel, instOceanVel, velBlend);
        var speed = entry.SmoothedOceanVel.magnitude;

        // Project canonical ocean velocity direction into local 3D scene space
        Vector3 sceneMoveDir = entry.LastMoveDir;
        if (speed > 0.12f)
        {
            var oceanAhead = oceanPos + entry.SmoothedOceanVel.normalized * 4f;
            var sceneAhead = FleetOwnedNavigation.MapToScene(oceanAhead.x, oceanAhead.z);
            var sceneVec = sceneAhead - new Vector3(heartScenePos.x, 0f, heartScenePos.z);
            sceneVec.y = 0f;
            if (sceneVec.sqrMagnitude > 0.001f)
            {
                sceneMoveDir = Vector3.Slerp(entry.LastMoveDir, sceneVec.normalized, 1f - Mathf.Exp(-12f * dt)).normalized;
                entry.LastMoveDir = sceneMoveDir;
            }
        }

        if (now >= entry.NextBoundsScanAt)
        {
            entry.NextBoundsScanAt = now + 0.45f;
            MeasureHullExtents(community, heartScenePos, sceneMoveDir, out entry.CachedForemost, out entry.CachedAftmost, out entry.CachedHalfBeam);
        }

        var visible = !LiveNavigation.ViewOpen;
        if (visible && Plugin.Options.mode == "independent" && !ReferenceEquals(community, Community.PlayerCommunity))
        {
            var ships = Plugin.Instance?.Network?.Ships;
            var shipState = ships != null ? System.Array.Find(ships, s => s != null && s.name == key) : null;
            if (shipState != null && !FleetVisibility.IsShipVisibleToLocal(shipState, ships))
                visible = false;
        }
        if (entry.Root.activeSelf != visible)
            entry.Root.SetActive(visible);

        var intensity = Mathf.Clamp01((speed - 0.18f) / 2.4f);
        var isMoving = intensity > 0.02f;
        for (var a = 0; a < entry.PropAnimators.Length; a++)
        {
            var anim = entry.PropAnimators[a];
            if (anim != null && anim.isActiveAndEnabled && anim.runtimeAnimatorController != null)
            {
                foreach (var p in anim.parameters)
                {
                    if (p.type == AnimatorControllerParameterType.Bool && p.name == "IsMoving")
                    {
                        anim.SetBool("IsMoving", isMoving);
                        break;
                    }
                }
            }
        }
        if (isMoving)
        {
            var spinDeg = Mathf.Clamp(speed * 110f, 90f, 720f) * dt;
            for (var r = 0; r < entry.RotorTransforms.Length; r++)
            {
                var rotor = entry.RotorTransforms[r];
                if (rotor != null)
                    rotor.Rotate(Vector3.forward, spinDeg, Space.Self);
            }
        }
        var sceneRight = new Vector3(sceneMoveDir.z, 0f, -sceneMoveDir.x);

        // 1. Layered V-shaped parting bow waves ahead of the foremost hull edge
        if (visible && intensity > 0.02f)
        {
            entry.BowPrimary.enabled = true;
            entry.BowSecondary.enabled = true;
            var bowApex = new Vector3(heartScenePos.x, 0f, heartScenePos.z) + sceneMoveDir * (entry.CachedForemost + 1.15f);
            var ripple = Mathf.Sin(now * 7.2f) * 0.18f;
            UpdateBowWaveArc(entry.BowPrimary, bowApex, sceneMoveDir, sceneRight,
                halfSpan: entry.CachedHalfBeam * 0.95f + 1.8f,
                sweepBack: entry.CachedHalfBeam * 0.85f + 2.6f + ripple,
                waterY: 0.15f,
                maxWidth: Mathf.Lerp(0.16f, 0.52f, intensity),
                alpha: 0.82f * intensity);

            UpdateBowWaveArc(entry.BowSecondary, bowApex - sceneMoveDir * 1.1f, sceneMoveDir, sceneRight,
                halfSpan: entry.CachedHalfBeam * 1.18f + 2.8f,
                sweepBack: entry.CachedHalfBeam * 1.05f + 3.6f - ripple,
                waterY: 0.12f,
                maxWidth: Mathf.Lerp(0.10f, 0.32f, intensity),
                alpha: 0.48f * intensity);

            if (now >= entry.NextSplashAt && intensity > 0.25f)
            {
                entry.NextSplashAt = now + Mathf.Lerp(0.42f, 0.18f, intensity);
                FleetPhysicalActors.SpawnNativeSplash(bowApex);
            }
        }
        else
        {
            entry.BowPrimary.enabled = false;
            entry.BowSecondary.enabled = false;
        }

        // 2. Record canonical ocean-anchored stern wake points & engine prop-wash
        if (intensity > 0.04f && now >= entry.NextWakeSampleAt)
        {
            entry.NextWakeSampleAt = now + 0.075f;
            var sternCenter = new Vector3(heartScenePos.x, 0f, heartScenePos.z) + sceneMoveDir * (entry.CachedAftmost - 0.8f);
            var sternOffset = Mathf.Clamp(entry.CachedHalfBeam * 0.48f, 1.4f, 6.5f);
            var portScene = sternCenter - sceneRight * sternOffset;
            var stbdScene = sternCenter + sceneRight * sternOffset;

            var portMap = FleetOwnedNavigation.SceneToMap(portScene);
            var stbdMap = FleetOwnedNavigation.SceneToMap(stbdScene);
            var rightMapVec = stbdMap - portMap;
            var rightLen = Mathf.Sqrt(rightMapVec.x * rightMapVec.x + rightMapVec.z * rightMapVec.z);
            var rx = rightLen > 0.001f ? rightMapVec.x / rightLen : 1f;
            var rz = rightLen > 0.001f ? rightMapVec.z / rightLen : 0f;

            entry.PortSamples.Add(new WakeSample { MapX = portMap.x, MapZ = portMap.z, RightMapX = -rx, RightMapZ = -rz, CreatedAt = now });
            entry.StarboardSamples.Add(new WakeSample { MapX = stbdMap.x, MapZ = stbdMap.z, RightMapX = rx, RightMapZ = rz, CreatedAt = now });

            while (entry.PortSamples.Count > 38) entry.PortSamples.RemoveAt(0);
            while (entry.StarboardSamples.Count > 38) entry.StarboardSamples.RemoveAt(0);

            if (visible && now >= entry.NextPropWashAt)
            {
                entry.NextPropWashAt = now + Mathf.Lerp(0.55f, 0.26f, intensity);
                FleetPhysicalActors.SpawnWaterShockwaveRing(
                    sternCenter,
                    new Color(0.84f, 0.96f, 1.0f, 0.42f * intensity),
                    Mathf.Lerp(1.8f, 3.4f, intensity));
            }
        }

        PruneAndRenderWakeRibbon(entry.PortWake, entry.PortSamples, now, visible);
        PruneAndRenderWakeRibbon(entry.StarboardWake, entry.StarboardSamples, now, visible);

        // 3. Subtle physical bow-lift pitch & gentle sea-swell roll on SpawnedVisual
        var visualTr = heart.SpawnedVisual != null ? heart.SpawnedVisual.transform : null;
        if (visualTr != null)
        {
            entry.TiltedVisual = visualTr;
            Quaternion desiredLocalRot = Quaternion.identity;
            if (intensity > 0.02f)
            {
                var localDir = Quaternion.Inverse(heart.transform.rotation) * sceneMoveDir;
                localDir.y = 0f;
                if (localDir.sqrMagnitude > 0.001f)
                {
                    localDir.Normalize();
                    var localRight = new Vector3(localDir.z, 0f, -localDir.x);
                    var bowLiftDeg = -(1.35f * intensity + Mathf.Sin(now * 3.1f) * 0.45f * intensity);
                    var swellRollDeg = Mathf.Sin(now * 2.3f + 0.7f) * 1.15f * intensity;
                    desiredLocalRot = Quaternion.AngleAxis(bowLiftDeg, localRight) * Quaternion.AngleAxis(swellRollDeg, localDir);
                }
            }
            visualTr.localRotation = Quaternion.Slerp(visualTr.localRotation, desiredLocalRot, 1f - Mathf.Exp(-8f * dt));
        }
    }

    private static void MeasureHullExtents(
        Community community,
        Vector3 heartPos,
        Vector3 forwardDir,
        out float foremost,
        out float aftmost,
        out float halfBeam)
    {
        var rightDir = new Vector3(forwardDir.z, 0f, -forwardDir.x);
        var maxF = 4.2f;
        var minF = -4.2f;
        var maxSide = 3.2f;

        foreach (var b in community.Buildables)
        {
            if (b == null || MoveConstructionCommands.IsStored(b)) continue;
            if (b.TryGetComponent<Boat>(out var boat) && boat.CurrentMooringPoint == null) continue;
            var rel = b.transform.position - heartPos;
            rel.y = 0f;
            if (rel.sqrMagnitude > 3600f) continue;
            var projF = rel.x * forwardDir.x + rel.z * forwardDir.z;
            var projR = Mathf.Abs(rel.x * rightDir.x + rel.z * rightDir.z);
            if (projF + 2.4f > maxF) maxF = projF + 2.4f;
            if (projF - 2.4f < minF) minF = projF - 2.4f;
            if (projR + 2.2f > maxSide) maxSide = projR + 2.2f;
        }

        foremost = Mathf.Clamp(maxF, 3.8f, 44f);
        aftmost = Mathf.Clamp(minF, -44f, -3.5f);
        halfBeam = Mathf.Clamp(maxSide, 2.8f, 18f);
    }

    private static void UpdateBowWaveArc(
        LineRenderer lr,
        Vector3 apex,
        Vector3 forward,
        Vector3 right,
        float halfSpan,
        float sweepBack,
        float waterY,
        float maxWidth,
        float alpha)
    {
        lr.positionCount = BowSegments;
        lr.widthMultiplier = maxWidth;
        var col = new Color(0.94f, 0.99f, 1.0f, alpha);
        var edgeCol = new Color(0.80f, 0.95f, 1.0f, 0f);
        var grad = new Gradient();
        grad.SetKeys(
            new[]
            {
                new GradientColorKey(edgeCol, 0f),
                new GradientColorKey(col, 0.5f),
                new GradientColorKey(edgeCol, 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(alpha, 0.35f),
                new GradientAlphaKey(alpha, 0.65f),
                new GradientAlphaKey(0f, 1f)
            });
        lr.colorGradient = grad;

        for (var i = 0; i < BowSegments; i++)
        {
            var u = (i / (float)(BowSegments - 1)) * 2f - 1f; // -1 .. +1
            var absU = Mathf.Abs(u);
            var back = (absU * absU * 0.65f + absU * 0.35f) * sweepBack;
            var pt = apex + right * (u * halfSpan) - forward * back;
            pt.y = waterY;
            lr.SetPosition(i, pt);
        }
    }

    private static void PruneAndRenderWakeRibbon(LineRenderer lr, List<WakeSample> samples, float now, bool visible)
    {
        while (samples.Count > 0 && now - samples[0].CreatedAt > WakeLifetime)
            samples.RemoveAt(0);

        if (!visible || samples.Count < 2)
        {
            lr.enabled = false;
            return;
        }

        lr.enabled = true;
        var count = samples.Count;
        lr.positionCount = count;
        // Index 0 = oldest (faded downstream), count - 1 = newest (right behind stern)
        for (var i = 0; i < count; i++)
        {
            var s = samples[i];
            var age = Mathf.Clamp(now - s.CreatedAt, 0f, WakeLifetime);
            var spread = age * 0.58f;
            var mx = s.MapX + s.RightMapX * spread;
            var mz = s.MapZ + s.RightMapZ * spread;
            var scenePt = FleetOwnedNavigation.MapToScene(mx, mz);
            scenePt.y = 0.09f;
            lr.SetPosition(i, scenePt);
        }
    }

    private static ShipWakeEntry CreateWakeEntry(string key)
    {
        var root = new GameObject($"Flotsam_ShipWake_{key}");
        var foamMat = FleetPhysicalActors.GetEmissiveMaterial(new Color(0.94f, 0.99f, 1.0f, 0.85f));

        var bowCurve = new AnimationCurve(
            new Keyframe(0f, 0.15f),
            new Keyframe(0.5f, 1.0f),
            new Keyframe(1f, 0.15f));

        var bow1Go = new GameObject("BowWavePrimary");
        bow1Go.transform.SetParent(root.transform, false);
        var bow1 = bow1Go.AddComponent<LineRenderer>();
        bow1.useWorldSpace = true;
        bow1.material = foamMat;
        bow1.widthCurve = bowCurve;
        bow1.numCapVertices = 3;
        bow1.enabled = false;

        var bow2Go = new GameObject("BowWaveSecondary");
        bow2Go.transform.SetParent(root.transform, false);
        var bow2 = bow2Go.AddComponent<LineRenderer>();
        bow2.useWorldSpace = true;
        bow2.material = foamMat;
        bow2.widthCurve = bowCurve;
        bow2.numCapVertices = 3;
        bow2.enabled = false;

        var wakeCurve = new AnimationCurve(
            new Keyframe(0f, 1.85f), // oldest downstream widens out
            new Keyframe(0.7f, 1.05f),
            new Keyframe(1f, 0.55f)); // newest near stern

        var wakeGrad = new Gradient();
        wakeGrad.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.82f, 0.95f, 1.0f), 0f),
                new GradientColorKey(new Color(0.96f, 0.99f, 1.0f), 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(0.42f, 0.55f),
                new GradientAlphaKey(0.76f, 1f)
            });

        LineRenderer CreateRibbon(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.material = foamMat;
            lr.widthCurve = wakeCurve;
            lr.widthMultiplier = 0.48f;
            lr.colorGradient = wakeGrad;
            lr.numCapVertices = 2;
            lr.enabled = false;
            return lr;
        }

        return new ShipWakeEntry
        {
            Root = root,
            BowPrimary = bow1,
            BowSecondary = bow2,
            PortWake = CreateRibbon("PortSternWake"),
            StarboardWake = CreateRibbon("StarboardSternWake")
        };
    }

    private static void TickCourseIndicator(float now)
    {
        if (!_courseActive || _courseRoot == null || _courseRing == null || _courseLine == null) return;
        if (now >= _courseExpiresAt)
        {
            ClearCourseIndicator();
            return;
        }

        var heart = FleetEngineBundle.HeartBuildable(Community.PlayerCommunity) ?? Construction.Townheart?.Buildable;
        var shipPos = heart != null ? heart.transform.position : Construction.TownheartPosition;
        var targetScene = FleetOwnedNavigation.MapToScene(_courseMapTarget.x, _courseMapTarget.y);
        targetScene.y = 0.16f;

        var dx = targetScene.x - shipPos.x;
        var dz = targetScene.z - shipPos.z;
        if (dx * dx + dz * dz <= 100f) // Reached within 10m
        {
            ClearCourseIndicator();
            return;
        }

        var show = !LiveNavigation.ViewOpen;
        if (_courseRoot.activeSelf != show)
            _courseRoot.SetActive(show);
        if (!show) return;

        _courseRing.transform.position = targetScene;
        var pulse = 2.6f + Mathf.Sin(now * 4.2f) * 0.45f;
        var segs = _courseRing.positionCount;
        for (var i = 0; i < segs; i++)
        {
            var ang = i * Mathf.PI * 2f / segs + now * 0.8f;
            _courseRing.SetPosition(i, new Vector3(Mathf.Cos(ang) * pulse, 0f, Mathf.Sin(ang) * pulse));
        }

        var dir = new Vector3(dx, 0f, dz).normalized;
        var lineStart = new Vector3(shipPos.x, 0.14f, shipPos.z) + dir * 5.5f;
        _courseLine.SetPosition(0, lineStart);
        _courseLine.SetPosition(1, targetScene);
    }
}
