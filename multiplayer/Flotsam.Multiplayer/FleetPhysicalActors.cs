using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Spawns and animates genuine 3D in-world actors for cross-ship gameplay:
/// - 3D Courier Boats with a Drifter captain & 3D cargo crates for Trade
/// - 3D Raiding Skiffs that board enemy buildings, load stolen crates, and sail back home
/// - 3D Parabolic Artillery Salvos (3-round cannonballs with smoke trails & impact explosions)
/// - Native WorldMap miniature ship markers for all ships in the fleet
/// </summary>
internal static class FleetPhysicalActors
{
    private sealed class ActiveVoyage
    {
        public string Kind = "";
        public string Label = "";
        public Vector3 Start;
        public Vector3 End;
        public float Speed = 13.5f;
        public float Progress;
        public int Phase; // 0 = outbound, 1 = boarding/unloading, 2 = return home
        public float BoardTimer;
        public float NextWakeTime;
        public GameObject? Root;
        public GameObject? CargoGroup;
        public Action? OnArriveTarget;
        public Action? OnReturnHome;
    }

    private sealed class ActiveShell
    {
        public Vector3 Start;
        public Vector3 End;
        public float Delay;
        public float Elapsed;
        public float Duration;
        public float ArcHeight;
        public bool Launched;
        public GameObject? Root;
        public LineRenderer? Trail;
        public readonly List<Vector3> TrailPoints = new List<Vector3>();
        public Action? OnImpact;
    }

    private static readonly List<ActiveVoyage> Voyages = new List<ActiveVoyage>();
    private static readonly List<ActiveShell> Shells = new List<ActiveShell>();
    private static readonly List<(float expires, FleetActionState state)> OutgoingActions = new List<(float, FleetActionState)>();
    private static readonly HashSet<long> SeenActionIds = new HashSet<long>();
    private static readonly Dictionary<string, GameObject> WorldMapMarkers = new Dictionary<string, GameObject>(StringComparer.Ordinal);
    private static long _nextActionId = 1;
    private static GameObject? _practiceTargetShip;
    private static Material? _litMat;
    private static Material? _emissiveMat;

    internal static FleetActionState[] CaptureRecentActions()
    {
        var now = Time.realtimeSinceStartup;
        OutgoingActions.RemoveAll(x => x.expires < now);
        return OutgoingActions.Select(x => x.state).ToArray();
    }

    internal static void ApplyRemoteActions(FleetActionState[]? actions)
    {
        if (actions == null || actions.Length == 0) return;
        foreach (var act in actions)
        {
            if (act == null || act.id <= 0 || !SeenActionIds.Add(act.id)) continue;
            var start = new Vector3(act.startX, 0f, act.startZ);
            var end = new Vector3(act.endX, 0f, act.endZ);
            if (act.kind == "shipTransfer")
                SpawnBoatVoyage("shipTransfer", act.label, start, end, null, null);
            else if (act.kind == "shipSteal")
                SpawnBoatVoyage("shipSteal", act.label, start, end, null, null);
            else if (act.kind == "shipAttack")
                SpawnCannonSalvo(start, end, null);
        }
    }

    internal static void RecordAndSpawn(
        string kind,
        string label,
        Vector3 start,
        Vector3 end,
        Action? onArriveTarget = null,
        Action? onReturnHome = null)
    {
        var id = (_nextActionId++) + ((long)(Time.realtimeSinceStartup * 1000f) << 16);
        SeenActionIds.Add(id);
        OutgoingActions.Add((Time.realtimeSinceStartup + 4.5f, new FleetActionState
        {
            id = id,
            kind = kind,
            label = label,
            startX = start.x,
            startZ = start.z,
            endX = end.x,
            endZ = end.z
        }));

        if (kind == "shipAttack")
            SpawnCannonSalvo(start, end, onArriveTarget);
        else
            SpawnBoatVoyage(kind, label, start, end, onArriveTarget, onReturnHome);
    }

    internal static Vector3 GetOrSpawnPracticeTarget(Vector3 myHeartPos)
    {
        var targetPos = myHeartPos + new Vector3(36f, 0f, 32f);
        if (_practiceTargetShip == null)
        {
            _practiceTargetShip = new GameObject("Flotsam_PracticeTargetShip");
            _practiceTargetShip.transform.position = targetPos;
            AttachGenuineBoatMesh(_practiceTargetShip.transform, 1.35f);
            AttachCargoCrates(_practiceTargetShip.transform, true);
            AttachWorldBanner(_practiceTargetShip.transform, "🎯 海上演练靶船", new Color(0.96f, 0.73f, 0.23f));
            SpawnNativeSplash(targetPos);
        }
        else
        {
            _practiceTargetShip.transform.position = targetPos;
        }
        return targetPos;
    }

    private static void SpawnBoatVoyage(
        string kind,
        string label,
        Vector3 start,
        Vector3 end,
        Action? onArriveTarget,
        Action? onReturnHome)
    {
        var dir = end - start;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1f) dir = Vector3.forward * 20f;
        var launchPos = start + dir.normalized * 5.5f;
        var targetPos = end - dir.normalized * 4.0f;

        var root = new GameObject($"Flotsam_Voyage_{kind}");
        root.transform.position = launchPos;
        root.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);

        AttachGenuineBoatMesh(root.transform, 1.0f);
        AttachDrifterCrewVisual(root.transform);
        var cargo = AttachCargoCrates(root.transform, kind == "shipTransfer");

        var isRaid = kind == "shipSteal";
        var bannerColor = isRaid ? new Color(0.94f, 0.42f, 0.24f) : new Color(0.96f, 0.73f, 0.23f);
        var bannerTitle = isRaid ? "🏴‍☠️ 登船掠夺艇" : $"📦 运输船 · {label}";
        AttachWorldBanner(root.transform, bannerTitle, bannerColor);

        SpawnNativeSplash(launchPos);
        SpawnNativePopup(launchPos + Vector3.up * 2.2f, bannerTitle);

        Voyages.Add(new ActiveVoyage
        {
            Kind = kind,
            Label = label,
            Start = launchPos,
            End = targetPos,
            Speed = isRaid ? 15.5f : 13.0f,
            Root = root,
            CargoGroup = cargo,
            OnArriveTarget = onArriveTarget,
            OnReturnHome = onReturnHome
        });
    }

    private static void SpawnCannonSalvo(Vector3 start, Vector3 end, Action? onFinalImpact)
    {
        var dir = end - start;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1f) dir = Vector3.forward * 25f;
        var dist = dir.magnitude;
        var duration = Mathf.Clamp(dist / 36f, 0.65f, 1.75f);
        var arc = Mathf.Clamp(dist * 0.26f, 7.5f, 22f);

        SpawnNativePopup(start + Vector3.up * 3.5f, "💣 船首火炮齐射！");

        for (var i = 0; i < 3; i++)
        {
            var spread = new Vector3(UnityEngine.Random.Range(-1.2f, 1.2f), 0f, UnityEngine.Random.Range(-1.2f, 1.2f));
            var shellStart = start + Vector3.up * 2.4f + dir.normalized * 2.5f;
            var shellEnd = end + Vector3.up * 1.0f + (i == 2 ? Vector3.zero : spread);

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"Flotsam_CannonShell_{i}";
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
            go.transform.localScale = Vector3.one * 0.52f;
            go.transform.position = shellStart;
            var r = go.GetComponent<Renderer>();
            if (r != null) r.sharedMaterial = GetEmissiveMaterial(new Color(1f, 0.48f, 0.12f));

            var trail = go.AddComponent<LineRenderer>();
            trail.material = GetEmissiveMaterial(new Color(0.95f, 0.65f, 0.25f, 0.85f));
            trail.startWidth = 0.26f;
            trail.endWidth = 0.04f;
            trail.positionCount = 0;
            trail.useWorldSpace = true;

            go.SetActive(false);
            Shells.Add(new ActiveShell
            {
                Start = shellStart,
                End = shellEnd,
                Delay = i * 0.22f,
                Duration = duration,
                ArcHeight = arc + i * 1.4f,
                Root = go,
                Trail = trail,
                OnImpact = i == 2 ? onFinalImpact : null
            });
        }
    }

    internal static void Tick(float dt)
    {
        if (dt <= 0f || dt > 0.5f) return;

        // 1. Update active 3D boat voyages
        for (var i = Voyages.Count - 1; i >= 0; i--)
        {
            var v = Voyages[i];
            if (v.Root == null) { Voyages.RemoveAt(i); continue; }

            if (v.Phase == 0)
            {
                var dist = Mathf.Max(1f, Vector3.Distance(v.Start, v.End));
                v.Progress += (v.Speed * dt) / dist;
                var pos = Vector3.Lerp(v.Start, v.End, Mathf.Clamp01(v.Progress));
                pos.y = Mathf.Sin(Time.time * 4.2f + i) * 0.14f;
                v.Root.transform.position = pos;

                var heading = (v.End - v.Start).normalized;
                if (heading.sqrMagnitude > 0.001f)
                {
                    var waveRoll = Quaternion.Euler(Mathf.Sin(Time.time * 3.5f) * 3.2f, 0f, Mathf.Cos(Time.time * 2.8f) * 4.0f);
                    v.Root.transform.rotation = Quaternion.LookRotation(heading, Vector3.up) * waveRoll;
                }

                if (Time.time >= v.NextWakeTime)
                {
                    v.NextWakeTime = Time.time + 0.35f;
                    SpawnNativeSplash(pos - heading * 1.6f);
                }

                if (v.Progress >= 1f)
                {
                    v.Progress = 0f;
                    v.Phase = 1;
                    v.BoardTimer = v.Kind == "shipSteal" ? 1.8f : 0.7f;
                    SpawnNativeSplash(v.End);
                    try { v.OnArriveTarget?.Invoke(); } catch { }
                    if (v.Kind == "shipSteal")
                    {
                        if (v.CargoGroup != null) v.CargoGroup.SetActive(true);
                        SpawnNativePopup(v.End + Vector3.up * 2.6f, "🏴‍☠️ 登船搜刮物资！");
                    }
                    else
                    {
                        if (v.CargoGroup != null) v.CargoGroup.SetActive(false);
                        SpawnNativePopup(v.End + Vector3.up * 2.6f, $"📦 物资送达: {v.Label}");
                    }
                }
            }
            else if (v.Phase == 1)
            {
                v.BoardTimer -= dt;
                var pos = v.End;
                pos.y = Mathf.Sin(Time.time * 5.5f) * 0.18f;
                v.Root.transform.position = pos;
                if (v.BoardTimer <= 0f)
                {
                    v.Phase = 2;
                    v.Progress = 0f;
                }
            }
            else
            {
                var dist = Mathf.Max(1f, Vector3.Distance(v.End, v.Start));
                v.Progress += (v.Speed * dt) / dist;
                var pos = Vector3.Lerp(v.End, v.Start, Mathf.Clamp01(v.Progress));
                pos.y = Mathf.Sin(Time.time * 4.2f + i) * 0.14f;
                v.Root.transform.position = pos;

                var heading = (v.Start - v.End).normalized;
                if (heading.sqrMagnitude > 0.001f)
                    v.Root.transform.rotation = Quaternion.LookRotation(heading, Vector3.up);

                if (Time.time >= v.NextWakeTime)
                {
                    v.NextWakeTime = Time.time + 0.38f;
                    SpawnNativeSplash(pos - heading * 1.6f);
                }

                if (v.Progress >= 1f)
                {
                    SpawnNativeSplash(v.Start);
                    try { v.OnReturnHome?.Invoke(); } catch { }
                    if (v.Kind == "shipSteal")
                        SpawnNativePopup(v.Start + Vector3.up * 2.5f, "🏴‍☠️ 掠夺艇满载返航！");
                    UnityEngine.Object.Destroy(v.Root);
                    Voyages.RemoveAt(i);
                }
            }
        }

        // 2. Update active 3D artillery shells
        for (var i = Shells.Count - 1; i >= 0; i--)
        {
            var s = Shells[i];
            if (s.Root == null) { Shells.RemoveAt(i); continue; }
            if (s.Delay > 0f)
            {
                s.Delay -= dt;
                continue;
            }
            if (!s.Launched)
            {
                s.Launched = true;
                s.Root.SetActive(true);
                SpawnNativeSplash(s.Start);
                SpawnImpactBurst(s.Start, 0.55f);
            }
            s.Elapsed += dt;
            var t = Mathf.Clamp01(s.Elapsed / Mathf.Max(0.1f, s.Duration));
            var pos = Vector3.Lerp(s.Start, s.End, t);
            pos.y += 4f * s.ArcHeight * t * (1f - t);
            s.Root.transform.position = pos;

            if (s.Trail != null)
            {
                s.TrailPoints.Add(pos);
                if (s.TrailPoints.Count > 14) s.TrailPoints.RemoveAt(0);
                s.Trail.positionCount = s.TrailPoints.Count;
                for (var p = 0; p < s.TrailPoints.Count; p++)
                    s.Trail.SetPosition(p, s.TrailPoints[p]);
            }

            if (t >= 1f)
            {
                SpawnNativeSplash(s.End);
                SpawnImpactBurst(s.End, 1.35f);
                if (s.OnImpact != null)
                {
                    try { s.OnImpact.Invoke(); } catch { }
                    SpawnNativePopup(s.End + Vector3.up * 2.8f, "💥 火炮命中！耐久受损 -25%");
                }
                UnityEngine.Object.Destroy(s.Root);
                Shells.RemoveAt(i);
            }
        }

        // 3. Sync WorldMap miniature ships when WorldMap is open
        SyncWorldMapShips();
    }

    private static void SyncWorldMapShips()
    {
        try
        {
            var map = GameManager.WorldMapManager?.WorldMap;
            var net = Plugin.Instance?.Network;
            if (map == null || map.Townheart == null || net == null || Plugin.Options.mode != "independent") return;
            var ships = net.Ships;
            var localName = Community.PlayerCommunity?.Name ?? "";
            var activeNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var ship in ships)
            {
                if (ship == null || string.IsNullOrEmpty(ship.name) || ship.name == localName) continue;
                activeNames.Add(ship.name);
                if (!WorldMapMarkers.TryGetValue(ship.name, out var marker) || marker == null)
                {
                    marker = CreateWorldMapShipMarker(map, ship.ownerName);
                    WorldMapMarkers[ship.name] = marker;
                }
                var mapX = Replication.Finite(ship.mapX) && (Mathf.Abs(ship.mapX) > 0.01f || Mathf.Abs(ship.mapZ) > 0.01f) ? ship.mapX : ship.x;
                var mapZ = Replication.Finite(ship.mapZ) && (Mathf.Abs(ship.mapX) > 0.01f || Mathf.Abs(ship.mapZ) > 0.01f) ? ship.mapZ : ship.z;
                marker.transform.position = new Vector3(mapX, map.Townheart.transform.position.y, mapZ);
                marker.transform.rotation = Quaternion.Euler(0f, ship.mapRotation, 0f);
                if (marker.activeSelf != LiveNavigation.ViewOpen)
                    marker.SetActive(LiveNavigation.ViewOpen);
            }

            foreach (var kv in WorldMapMarkers.ToArray())
            {
                if (!activeNames.Contains(kv.Key))
                {
                    if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
                    WorldMapMarkers.Remove(kv.Key);
                }
            }
        }
        catch { }
    }

    private static GameObject CreateWorldMapShipMarker(WorldMap map, string ownerName)
    {
        var root = new GameObject($"WorldMap_FleetShip_{ownerName}");
        root.transform.SetParent(map.Townheart.transform.parent, false);
        root.layer = map.Townheart.gameObject.layer;

        var visuals = AccessTools.Field(typeof(WorldMapTownheart), "_visuals")?.GetValue(map.Townheart) as Array;
        if (visuals != null && visuals.Length > 0 && visuals.GetValue(0) is Component firstVis && firstVis != null)
        {
            var clone = UnityEngine.Object.Instantiate(firstVis.gameObject, root.transform, false);
            clone.transform.localPosition = Vector3.zero;
            clone.SetActive(true);
        }
        else
        {
            var hull = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hull.transform.SetParent(root.transform, false);
            hull.transform.localScale = new Vector3(1.5f, 0.8f, 2.8f);
            hull.layer = root.layer;
        }
        return root;
    }

    private static void AttachGenuineBoatMesh(Transform parent, float scale)
    {
        try
        {
            var boats = GameSettings.Instance?.BoatSettings?.Boats;
            if (boats != null)
            {
                foreach (var boat in boats)
                {
                    if (boat != null && boat.gameObject != null)
                    {
                        var clone = UnityEngine.Object.Instantiate(boat.gameObject, parent, false);
                        clone.transform.localPosition = Vector3.zero;
                        clone.transform.localRotation = Quaternion.identity;
                        clone.transform.localScale = Vector3.one * scale;
                        StripInteractiveComponents(clone);
                        clone.SetActive(true);
                        return;
                    }
                }
            }
        }
        catch { }

        // Fallback procedural wooden skiff if boat prefab isn't loaded yet
        var hull = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hull.transform.SetParent(parent, false);
        hull.transform.localPosition = new Vector3(0f, 0.25f, 0f);
        hull.transform.localScale = new Vector3(1.6f * scale, 0.55f * scale, 3.4f * scale);
        if (hull.TryGetComponent<Collider>(out var c1)) UnityEngine.Object.Destroy(c1);
        hull.GetComponent<Renderer>().sharedMaterial = GetLitMaterial(new Color(0.45f, 0.28f, 0.15f));

        var mast = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        mast.transform.SetParent(parent, false);
        mast.transform.localPosition = new Vector3(0f, 1.3f * scale, 0.2f * scale);
        mast.transform.localScale = new Vector3(0.12f * scale, 1.1f * scale, 0.12f * scale);
        if (mast.TryGetComponent<Collider>(out var c2)) UnityEngine.Object.Destroy(c2);
        mast.GetComponent<Renderer>().sharedMaterial = GetLitMaterial(new Color(0.32f, 0.20f, 0.11f));
    }

    private static void AttachDrifterCrewVisual(Transform parent)
    {
        try
        {
            var sampleAgent = Community.PlayerCommunity?.Agents?.FirstOrDefault(a => a != null);
            var smr = sampleAgent != null ? sampleAgent.GetComponentInChildren<SkinnedMeshRenderer>() : null;
            var crewRoot = smr != null && smr.transform.parent != null && smr.transform.parent != sampleAgent!.transform
                ? smr.transform.parent.gameObject
                : smr?.gameObject;
            if (crewRoot != null)
            {
                var crewClone = UnityEngine.Object.Instantiate(crewRoot, parent, false);
                crewClone.transform.localPosition = new Vector3(0f, 0.45f, -0.45f);
                crewClone.transform.localRotation = Quaternion.identity;
                StripInteractiveComponents(crewClone);
                crewClone.SetActive(true);
                return;
            }
        }
        catch { }
    }

    private static GameObject AttachCargoCrates(Transform parent, bool activeInitially)
    {
        var group = new GameObject("CargoCrates");
        group.transform.SetParent(parent, false);
        group.transform.localPosition = new Vector3(0f, 0.42f, 0.45f);

        var spawnedNative = false;
        try
        {
            var items = GameSettings.Instance?.ItemSettings?.ItemProperties;
            var sample = items?.FirstOrDefault(p => p != null && p.StorageVisualPrefab != null);
            if (sample?.StorageVisualPrefab != null)
            {
                for (var i = 0; i < 2; i++)
                {
                    var crate = UnityEngine.Object.Instantiate(sample.StorageVisualPrefab.gameObject, group.transform, false);
                    crate.transform.localPosition = new Vector3((i - 0.5f) * 0.45f, 0f, i * 0.25f);
                    StripInteractiveComponents(crate);
                    crate.SetActive(true);
                }
                spawnedNative = true;
            }
        }
        catch { }

        if (!spawnedNative)
        {
            for (var i = 0; i < 3; i++)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.transform.SetParent(group.transform, false);
                box.transform.localPosition = new Vector3((i - 1) * 0.38f, 0.22f, (i % 2) * 0.25f);
                box.transform.localScale = new Vector3(0.36f, 0.36f, 0.36f);
                if (box.TryGetComponent<Collider>(out var col)) UnityEngine.Object.Destroy(col);
                box.GetComponent<Renderer>().sharedMaterial = GetLitMaterial(new Color(0.76f, 0.54f, 0.26f));
            }
        }

        group.SetActive(activeInitially);
        return group;
    }

    private static void AttachWorldBanner(Transform parent, string text, Color accent)
    {
        var go = new GameObject("WorldBanner");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, 3.1f, 0f);
        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.fontSize = 36;
        tm.characterSize = 0.11f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = accent;
        go.AddComponent<BillboardToCamera>();
    }

    private static void StripInteractiveComponents(GameObject root)
    {
        foreach (var comp in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (comp == null) continue;
            var tName = comp.GetType().Name;
            if (tName.Contains("Selection") || tName.Contains("Buildable") || tName.Contains("Collider") || tName.Contains("Agent") || tName.Contains("Navigator") || tName.Contains("Boat"))
                UnityEngine.Object.Destroy(comp);
        }
        foreach (var col in root.GetComponentsInChildren<Collider>(true))
            if (col != null) UnityEngine.Object.Destroy(col);
    }

    private static void SpawnImpactBurst(Vector3 pos, float scale)
    {
        var burst = new GameObject("Flotsam_CannonBurst");
        burst.transform.position = pos;
        var ps = burst.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.startLifetime = 0.75f;
        main.startSpeed = 6.5f * scale;
        main.startSize = 0.55f * scale;
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.55f, 0.12f), new Color(0.22f, 0.22f, 0.24f));
        main.maxParticles = 32;
        main.loop = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 0.6f;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 24) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.45f * scale;

        var psr = burst.GetComponent<ParticleSystemRenderer>();
        if (psr != null) psr.sharedMaterial = GetEmissiveMaterial(new Color(1f, 0.6f, 0.18f));
        ps.Play();
        UnityEngine.Object.Destroy(burst, 1.5f);
    }

    internal static void SpawnNativeSplash(Vector3 pos)
    {
        try
        {
            var splash = GameSettings.Instance?.FXSettings?.Splash;
            if (splash != null)
                ParticleController.Spawn(splash, new Vector3(pos.x, 0f, pos.z), Quaternion.identity);
        }
        catch { }
    }

    internal static void SpawnNativePopup(Vector3 pos, string text)
    {
        try { TextPopup.Spawn(pos, text); }
        catch { }
    }

    private static Material GetLitMaterial(Color color)
    {
        if (_litMat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            _litMat = new Material(sh);
        }
        var mat = new Material(_litMat) { color = color };
        return mat;
    }

    private static Material GetEmissiveMaterial(Color color)
    {
        if (_emissiveMat == null)
        {
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            _emissiveMat = new Material(sh);
        }
        var mat = new Material(_emissiveMat) { color = color };
        return mat;
    }

    private sealed class BillboardToCamera : MonoBehaviour
    {
        private void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null)
                transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position);
        }
    }
}
