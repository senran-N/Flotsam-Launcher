using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Spawns and animates genuine 3D in-world and WorldMap actors for multiplayer gameplay:
/// - Co-op Sea Beacons (3D ocean + WorldMap) for pointing out resources, islands, and hazards
/// - Authentic WorldMap miniature 3D ships, nameplates, and shared fleet Fog-of-War scouting
/// </summary>
internal static class FleetPhysicalActors
{
    internal sealed class ActiveBeacon
    {
        public string Label = "";
        public Vector3 ScenePos;
        public float CreatedAt;
        public float ExpiresAt;
        public float NextPulseAt;
        public GameObject? SceneRoot;
        public GameObject? MapRoot;
    }

    private sealed class WorldMapShipEntry
    {
        public GameObject Root = null!;
        public TextMeshPro? Label;
        public LineRenderer? Ring;
        public Animator[] Animators = Array.Empty<Animator>();
        public Vector3 SmoothedPos;
        public float SmoothedYaw;
        public bool Initialized;
    }

    private static readonly List<ActiveBeacon> Beacons = new List<ActiveBeacon>();
    private static readonly List<(float expires, FleetActionState state)> OutgoingActions = new List<(float, FleetActionState)>();
    private static readonly HashSet<long> SeenActionIds = new HashSet<long>();
    private static readonly Dictionary<string, WorldMapShipEntry> WorldMapShips = new Dictionary<string, WorldMapShipEntry>(StringComparer.Ordinal);
    private static long _nextActionId = 1;
    private static Material? _litMat;
    private static Material? _emissiveMat;
    private static readonly Dictionary<Color,Material> LitMaterials=new Dictionary<Color,Material>();
    private static readonly Dictionary<Color,Material> EmissiveMaterials=new Dictionary<Color,Material>();
    private static TMP_FontAsset? _cachedTmpFont;
    private static float _shakeRemaining;
    private static float _shakeIntensity;
    private static float _nextScoutTime;
    private static readonly Dictionary<string, AudioClip> CachedSfx = new Dictionary<string, AudioClip>(StringComparer.Ordinal);

    internal static (string PeerId, string PlayerName, float ExpiresAt)? PendingCaptainRequest { get; private set; }

    internal static void ClearCaptainRequest() => PendingCaptainRequest = null;

    internal static ActiveBeacon? LatestBeacon
    {
        get
        {
            var now = Time.realtimeSinceStartup;
            for (var i = Beacons.Count - 1; i >= 0; i--)
                if (Beacons[i].ExpiresAt > now) return Beacons[i];
            return null;
        }
    }

    internal static bool IsNearActiveBeacon(Vector3 position, float radius)
    {
        var now = Time.realtimeSinceStartup;
        var rSq = radius * radius;
        for (var i = 0; i < Beacons.Count; i++)
        {
            var b = Beacons[i];
            if (b.ExpiresAt <= now) continue;
            var dx = position.x - b.ScenePos.x;
            var dz = position.z - b.ScenePos.z;
            if (dx * dx + dz * dz <= rSq) return true;
        }
        return false;
    }

    internal static void Reset()
    {
        foreach (var b in Beacons)
        {
            if (b.SceneRoot != null) UnityEngine.Object.Destroy(b.SceneRoot);
            if (b.MapRoot != null) UnityEngine.Object.Destroy(b.MapRoot);
        }
        Beacons.Clear();
        foreach (var kv in WorldMapShips.Values)
            if (kv.Root != null) UnityEngine.Object.Destroy(kv.Root);
        WorldMapShips.Clear();
        OutgoingActions.Clear();
        SeenActionIds.Clear();
        PendingCaptainRequest = null;
        ShipSailingVisuals.Reset();
    }

    internal static TMP_FontAsset? ResolveNativeTmpFont()
    {
        if (_cachedTmpFont != null) return _cachedTmpFont;
        var allTmp = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
        foreach (var t in allTmp)
        {
            if (t != null && t.font != null)
            {
                _cachedTmpFont = t.font;
                return _cachedTmpFont;
            }
        }
        var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        if (fonts != null && fonts.Length > 0)
            _cachedTmpFont = fonts.FirstOrDefault(f => f != null);
        return _cachedTmpFont;
    }

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
            var start = ClientOceanAnchor.HostToClientPos(new Vector3(act.startX, 0f, act.startZ));
            var end = act.kind == "seaChartReveal"
                ? new Vector3(act.endX, 0f, act.endZ)
                : ClientOceanAnchor.HostToClientPos(new Vector3(act.endX, 0f, act.endZ));
            DispatchLocalEffect(act.kind, act.label, start, end, act.sourceShip, act.targetShip, act.targetBuilding, act.subKind, null, null);
        }
    }

    internal static void RecordAndSpawn(
        string kind,
        string label,
        Vector3 start,
        Vector3 end,
        string sourceShip = "",
        string targetShip = "",
        long targetBuilding = 0,
        int subKind = 0,
        Action? onArriveTarget = null,
        Action? onReturnHome = null)
    {
        var id = (_nextActionId++) + ((long)(Time.realtimeSinceStartup * 1000f) << 16);
        SeenActionIds.Add(id);
        OutgoingActions.Add((Time.realtimeSinceStartup + 4.5f, new FleetActionState
        {
            id = id,
            kind = kind,
            subKind = subKind,
            label = label,
            sourceShip = sourceShip,
            targetShip = targetShip,
            targetBuilding = targetBuilding,
            startX = start.x,
            startZ = start.z,
            endX = end.x,
            endZ = end.z
        }));

        DispatchLocalEffect(kind, label, start, end, sourceShip, targetShip, targetBuilding, subKind, onArriveTarget, onReturnHome);
    }

    private static void DispatchLocalEffect(
        string kind,
        string label,
        Vector3 start,
        Vector3 end,
        string sourceShip,
        string targetShip,
        long targetBuilding,
        int subKind,
        Action? onArriveTarget,
        Action? onReturnHome)
    {
        if (kind == "cannonImpact")
        {
            SpawnNativeSplash(end);SpawnImpactBurst(end,1f);PlayFleetSound("shell_impact",end,.7f);
            var local=Community.PlayerCommunity;
            if(local!=null&&sourceShip==Replication.ShipKey(local))SpawnNativePopup(end+Vector3.up*2,label);
            return;
        }
        if (kind == "seaPing")
        {
            SpawnSeaBeacon(label, start, sourceShip);
            return;
        }
        if (kind == "seaFlare")
        {
            SpawnSeaFlare(label, start, sourceShip);
            return;
        }
        if (kind == "seaChartReveal")
        {
            FleetVisibility.ApplyChartRevealIfAuthorized(sourceShip, end.x, end.z, subKind > 0 ? subKind : 1200f);
            var myKey = Replication.ShipKey(Community.PlayerCommunity);
            if (Plugin.Options.mode != "independent" || sourceShip == myKey)
            {
                Plugin.Instance?.Network?.ShowNotice("🗺️ 旧海图揭开了周边 1200m 海域迷雾！", false);
                PlayFleetSound("cargo_deliver", Construction.TownheartPosition, 0.78f);
            }
            return;
        }
        if (kind == "allyRequest" || kind == "allyNotice" || kind == "allyBreak" ||
            kind == "betrayalNotice" || kind == "barterNotice" || kind == "lootNotice")
        {
            var net = Plugin.Instance?.Network;
            var myKey = Replication.ShipKey(Community.PlayerCommunity);
            var isErr = kind == "betrayalNotice" || kind == "allyBreak";
            var relevant = kind == "allyNotice" || kind == "allyBreak" || kind == "betrayalNotice" || kind == "lootNotice" ||
                           sourceShip == myKey || targetShip == myKey;
            if (relevant && net != null)
                net.ShowNotice(label, isErr);
            var fxColor = kind == "betrayalNotice"
                ? new Color(1.0f, 0.22f, 0.18f, 0.95f)
                : kind.StartsWith("ally", StringComparison.Ordinal)
                    ? new Color(0.28f, 0.90f, 0.85f, 0.92f)
                    : new Color(0.98f, 0.82f, 0.28f, 0.90f);
            var ringPos = ResolveEndpoint(targetShip, 0, end);
            SpawnWaterShockwaveRing(ringPos, fxColor, 6.8f);
            PlayFleetSound(kind == "betrayalNotice" ? "raid_board" : "cargo_deliver", ringPos, 0.82f);
            return;
        }
        if (kind == "captainRequest")
        {
            PendingCaptainRequest = (sourceShip, label, Time.realtimeSinceStartup + 18f);
            var net = Plugin.Instance?.Network;
            if (net != null)
            {
                if (net.IsCaptain || net.IsHost)
                    net.ShowNotice($"{label} 请求接管掌舵权 (按 F8 一键移交)", false);
                else if (net.LocalPeer == sourceShip)
                    net.ShowNotice("已向当前船长发送掌舵请求", false);
            }
            PlayFleetSound("cargo_deliver", Construction.TownheartPosition, 0.65f);
            return;
        }
    }

    internal static void RebaseAnchor(Vector3 oldPos, Quaternion oldRot, Vector3 newPos, Quaternion newRot)
    {
        var inv = Quaternion.Inverse(newRot);
        Vector3 Rebase(Vector3 p)
        {
            var canonical = oldPos + oldRot * p;
            var r = inv * (canonical - newPos);
            r.y = p.y;
            return r;
        }

        foreach (var b in Beacons)
        {
            b.ScenePos = Rebase(b.ScenePos);
            if (b.SceneRoot != null) b.SceneRoot.transform.position = b.ScenePos;
        }
    }

    private static Vector3 ResolveEndpoint(string shipName, long buildingId, Vector3 fallback)
    {
        var net = Plugin.Instance?.Network;
        if (buildingId > 0 && net != null)
        {
            var b = net.ResolveBuilding(buildingId);
            if (b != null && !MoveConstructionCommands.IsStored(b))
                return new Vector3(b.transform.position.x, 0f, b.transform.position.z);
        }
        if (!string.IsNullOrEmpty(shipName))
        {
            var comm = Replication.ResolveCommunity(shipName);
            if (comm != null)
            {
                var heart = FleetEngineBundle.HeartBuildable(comm)
                            ?? comm.Buildables.FirstOrDefault(x => x != null && !MoveConstructionCommands.IsStored(x) && (x.TryGetComponent<TownheartStorage>(out _) || x.TryGetComponent<Engine>(out _)))
                            ?? comm.Buildables.FirstOrDefault(x => x != null && !MoveConstructionCommands.IsStored(x));
                if (heart != null)
                    return new Vector3(heart.transform.position.x, 0f, heart.transform.position.z);
            }
            var shipState = net?.Ships.FirstOrDefault(s => s != null && s.name == shipName);
            if (shipState != null)
                return new Vector3(shipState.x, 0f, shipState.z);
        }
        return fallback;
    }

    private static void SpawnSeaFlare(string label, Vector3 scenePos, string senderShipKey)
    {
        var pos = new Vector3(scenePos.x, 0.2f, scenePos.z);
        var root = new GameObject("Flotsam_SeaFlare");
        root.transform.position = pos;

        var flareCol = new Color(1.0f, 0.34f, 0.16f, 0.96f);

        var beamGo = new GameObject("FlareColumn");
        beamGo.transform.SetParent(root.transform, false);
        var beam = beamGo.AddComponent<LineRenderer>();
        beam.useWorldSpace = false;
        beam.material = GetEmissiveMaterial(new Color(1.0f, 0.42f, 0.18f, 0.92f));
        beam.startWidth = 0.65f;
        beam.endWidth = 0.12f;
        beam.positionCount = 2;
        beam.SetPosition(0, Vector3.zero);
        beam.SetPosition(1, new Vector3(0f, 38f, 0f));

        var star = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        star.transform.SetParent(root.transform, false);
        star.transform.localPosition = new Vector3(0f, 38f, 0f);
        star.transform.localScale = Vector3.one * 1.45f;
        if (star.TryGetComponent<Collider>(out var c)) UnityEngine.Object.Destroy(c);
        star.GetComponent<Renderer>().sharedMaterial = GetEmissiveMaterial(new Color(1.0f, 0.78f, 0.25f));

        AttachWorldBanner(root.transform, $"🚨 信号弹 · {label}", new Color(1.0f, 0.58f, 0.24f), 5.6f);
        SpawnWaterShockwaveRing(pos, flareCol, 12.5f);
        PlayFleetSound("cannon_fire", pos, 0.92f);

        GameObject? mapRoot = null;
        var map = GameManager.WorldMapManager?.WorldMap;
        if (map?.Townheart != null && map.Townheart.transform.parent != null)
        {
            mapRoot = new GameObject("WorldMap_SeaFlare");
            mapRoot.transform.SetParent(map.Townheart.transform.parent, false);
            mapRoot.layer = map.Townheart.gameObject.layer;
            var mapPos = FleetOwnedNavigation.SceneToMap(ClientOceanAnchor.ClientToHostPos(pos));
            mapRoot.transform.position = new Vector3(mapPos.x, map.Townheart.transform.position.y + 0.6f, mapPos.z);

            var mapRing = mapRoot.AddComponent<LineRenderer>();
            mapRing.useWorldSpace = false;
            mapRing.loop = true;
            mapRing.material = GetEmissiveMaterial(flareCol);
            mapRing.startWidth = 1.6f;
            mapRing.endWidth = 1.6f;
            mapRing.positionCount = 28;
            for (var i = 0; i < 28; i++)
            {
                var ang = i * Mathf.PI * 2f / 28;
                mapRing.SetPosition(i, new Vector3(Mathf.Cos(ang) * 14f, 0.25f, Mathf.Sin(ang) * 14f));
            }
            AttachWorldBanner(mapRoot.transform, $"🚨 {label}", new Color(1.0f, 0.58f, 0.24f), 8.5f, mapRoot.layer, 28f);
            mapRoot.SetActive(LiveNavigation.ViewOpen);
        }

        var now = Time.realtimeSinceStartup;
        Beacons.Add(new ActiveBeacon
        {
            Label = label,
            ScenePos = pos,
            CreatedAt = now,
            ExpiresAt = now + 35f,
            NextPulseAt = now + 1.4f,
            SceneRoot = root,
            MapRoot = mapRoot
        });

        var myKey = Replication.ShipKey(Community.PlayerCommunity);
        Plugin.Instance?.Network?.ShowNotice(
            senderShipKey == myKey ? "🚨 已发射全图信号弹，周边海域已对所有船长暴露 35 秒！" : $"🚨 远海升起信号弹：{label} (按 M 海图或 F9 查看)",
            false);
    }

    private static void SpawnSeaBeacon(string label, Vector3 scenePos, string senderPeerId)
    {
        var pos = new Vector3(scenePos.x, 0.15f, scenePos.z);
        var root = new GameObject("Flotsam_SeaBeacon");
        root.transform.position = pos;

        var accent = new Color(0.32f, 0.88f, 0.72f, 0.95f);

        // Vertical light column
        var beamGo = new GameObject("BeaconBeam");
        beamGo.transform.SetParent(root.transform, false);
        var beam = beamGo.AddComponent<LineRenderer>();
        beam.useWorldSpace = false;
        beam.material = GetEmissiveMaterial(new Color(0.35f, 0.92f, 0.76f, 0.78f));
        beam.startWidth = 0.42f;
        beam.endWidth = 0.05f;
        beam.positionCount = 2;
        beam.SetPosition(0, Vector3.zero);
        beam.SetPosition(1, new Vector3(0f, 18f, 0f));

        // Base ring on water
        var ringGo = new GameObject("BeaconBaseRing");
        ringGo.transform.SetParent(root.transform, false);
        var ring = ringGo.AddComponent<LineRenderer>();
        ring.useWorldSpace = false;
        ring.loop = true;
        ring.material = GetEmissiveMaterial(accent);
        ring.startWidth = 0.24f;
        ring.endWidth = 0.24f;
        const int segs = 32;
        ring.positionCount = segs;
        for (var i = 0; i < segs; i++)
        {
            var ang = i * Mathf.PI * 2f / segs;
            ring.SetPosition(i, new Vector3(Mathf.Cos(ang) * 2.6f, 0.08f, Mathf.Sin(ang) * 2.6f));
        }

        AttachWorldBanner(root.transform, $"信标 · {label}", new Color(0.98f, 0.84f, 0.32f), 4.2f);
        SpawnWaterShockwaveRing(pos, accent, 7.5f);
        PlayFleetSound("cargo_deliver", pos, 0.80f);

        // Optional WorldMap beacon marker
        GameObject? mapRoot = null;
        var map = GameManager.WorldMapManager?.WorldMap;
        if (map?.Townheart != null && map.Townheart.transform.parent != null)
        {
            mapRoot = new GameObject("WorldMap_SeaBeacon");
            mapRoot.transform.SetParent(map.Townheart.transform.parent, false);
            mapRoot.layer = map.Townheart.gameObject.layer;
            var mapPos = FleetOwnedNavigation.SceneToMap(ClientOceanAnchor.ClientToHostPos(pos));
            mapRoot.transform.position = new Vector3(mapPos.x, map.Townheart.transform.position.y + 0.5f, mapPos.z);

            var mapRing = mapRoot.AddComponent<LineRenderer>();
            mapRing.useWorldSpace = false;
            mapRing.loop = true;
            mapRing.material = GetEmissiveMaterial(new Color(0.98f, 0.80f, 0.24f, 0.95f));
            mapRing.startWidth = 1.2f;
            mapRing.endWidth = 1.2f;
            mapRing.positionCount = 24;
            for (var i = 0; i < 24; i++)
            {
                var ang = i * Mathf.PI * 2f / 24;
                mapRing.SetPosition(i, new Vector3(Mathf.Cos(ang) * 8f, 0.2f, Mathf.Sin(ang) * 8f));
            }
            AttachWorldBanner(mapRoot.transform, $"信标 · {label}", new Color(0.98f, 0.84f, 0.32f), 6.5f, mapRoot.layer, 26f);
            mapRoot.SetActive(LiveNavigation.ViewOpen);
        }

        var now = Time.realtimeSinceStartup;
        Beacons.Add(new ActiveBeacon
        {
            Label = label,
            ScenePos = pos,
            CreatedAt = now,
            ExpiresAt = now + 16f,
            NextPulseAt = now + 1.6f,
            SceneRoot = root,
            MapRoot = mapRoot
        });

        var net = Plugin.Instance?.Network;
        if (net != null && net.LocalPeer != senderPeerId)
            net.ShowNotice($"收到海域信标：{label} (按 F9 跳转镜头)", false);
    }

    internal static void Tick(float dt)
    {
        if (dt <= 0f || dt > 0.5f) return;
        var uTime = Time.unscaledTime;

        if (PendingCaptainRequest.HasValue && Time.realtimeSinceStartup > PendingCaptainRequest.Value.ExpiresAt)
            PendingCaptainRequest = null;

        // 3. Update active Co-op Sea Beacons & Flares
        var now = Time.realtimeSinceStartup;
        var mapOpen = LiveNavigation.ViewOpen;
        var map = GameManager.WorldMapManager?.WorldMap;
        for (var i = Beacons.Count - 1; i >= 0; i--)
        {
            var b = Beacons[i];
            if (now >= b.ExpiresAt)
            {
                if (b.SceneRoot != null) UnityEngine.Object.Destroy(b.SceneRoot);
                if (b.MapRoot != null) UnityEngine.Object.Destroy(b.MapRoot);
                Beacons.RemoveAt(i);
                continue;
            }
            if (now >= b.NextPulseAt)
            {
                b.NextPulseAt = now + 2.0f;
                SpawnWaterShockwaveRing(b.ScenePos, new Color(0.32f, 0.88f, 0.72f, 0.85f), 6.2f);
            }
            if (b.MapRoot != null && map?.Townheart != null)
            {
                var mapPos = FleetOwnedNavigation.SceneToMap(ClientOceanAnchor.ClientToHostPos(b.ScenePos));
                b.MapRoot.transform.position = new Vector3(mapPos.x, map.Townheart.transform.position.y + 0.5f, mapPos.z);
                if (b.MapRoot.activeSelf != mapOpen)
                    b.MapRoot.SetActive(mapOpen);
            }
        }

        // 4. Apply damped camera shake when cannons fire or impact nearby
        UpdateCameraShake(dt);

        // 5. Sync WorldMap miniature ships & per-ship Fog-of-War scouting
        SyncWorldMapShipsAndScouting(dt);

        // 6. Enforce 3D renderer culling for unspotted ships in independent mode
        FleetVisibility.Tick3DVisibility();

        // 7. Render 3D bow waves, ocean-anchored stern wakes & course indicator for moving ships
        ShipSailingVisuals.Tick(dt);
    }

    internal static void PanWorldMapTo(float mapX, float mapZ)
    {
        var map = GameManager.WorldMapManager?.WorldMap;
        if (map == null || map.Townheart == null) return;
        if (!LiveNavigation.ViewOpen)
            map.Open();
        var heartPos = map.Townheart.transform.position;
        map.WorldCameraController.SetRelativePosition(new Vector3(mapX - heartPos.x, 0f, mapZ - heartPos.z));
    }

    private static void SyncWorldMapShipsAndScouting(float dt)
    {
        var map = GameManager.WorldMapManager?.WorldMap;
        var net = Plugin.Instance?.Network;
        if (map == null || map.Townheart == null || net == null || !GameSession.Ready()) return;

        var ships = net.Ships;
        if (ships == null || ships.Length == 0) return;

        // Per-ship Fog-of-War & Tile Discovery (~2.5 Hz, sharing only with allies / active flares)
        if (Time.unscaledTime >= _nextScoutTime)
        {
            _nextScoutTime = Time.unscaledTime + 0.4f;
            FleetVisibility.ScoutAuthorizedFogOfWar(map, ships);
        }

        if (Plugin.Options.mode != "independent") return;

        var localKey = Replication.ShipKey(Community.PlayerCommunity);
        var localShip = ships.FirstOrDefault(s => s != null && s.name == localKey);
        var nowPlay = GameManager.TimeManager != null ? GameManager.TimeManager.CurrentPlayTime : 0f;
        var activeNames = new HashSet<string>(StringComparer.Ordinal);
        var mapVisible = LiveNavigation.ViewOpen;
        var lerpFactor = 1f - Mathf.Exp(-14f * Mathf.Max(0.001f, dt));

        foreach (var ship in ships)
        {
            if (ship == null || string.IsNullOrEmpty(ship.name) || ship.name == localKey) continue;
            if (!Replication.Finite(ship.mapX) || !Replication.Finite(ship.mapZ)) continue;
            activeNames.Add(ship.name);

            if (!WorldMapShips.TryGetValue(ship.name, out var entry) || entry.Root == null)
            {
                entry = CreateWorldMapShipMarker(map, ship.ownerName);
                WorldMapShips[ship.name] = entry;
            }

            var visible = FleetVisibility.IsShipVisibleToLocal(ship, ships);
            if (visible)
            {
                FleetVisibility.RecordSpottedGhost(ship);
                var targetPos = new Vector3(ship.mapX, map.Townheart.transform.position.y, ship.mapZ);
                var prevPos = entry.SmoothedPos;
                if (!entry.Initialized || (targetPos - entry.SmoothedPos).sqrMagnitude > 2500f)
                {
                    entry.SmoothedPos = targetPos;
                    entry.SmoothedYaw = ship.mapRotation;
                    entry.Initialized = true;
                }
                else
                {
                    entry.SmoothedPos = Vector3.Lerp(entry.SmoothedPos, targetPos, lerpFactor);
                    entry.SmoothedYaw = Mathf.LerpAngle(entry.SmoothedYaw, ship.mapRotation, lerpFactor);
                }

                var moving = (targetPos - prevPos).sqrMagnitude > 0.002f;
                SetMarkerMoving(entry, moving);

                entry.Root.transform.position = entry.SmoothedPos;
                entry.Root.transform.rotation = Quaternion.Euler(0f, entry.SmoothedYaw, 0f);

                var allied = FleetVisibility.AreAlliedState(localShip, ship);
                var flared = ship.flareUntil > nowPlay;
                if (entry.Label != null)
                {
                    entry.Label.text = flared
                        ? $"🚨 {ship.ownerName} (信号弹)"
                        : allied
                            ? $"🤝 {ship.ownerName}"
                            : $"⚓ {ship.ownerName}";
                    entry.Label.color = flared
                        ? new Color(1.0f, 0.56f, 0.24f)
                        : allied
                            ? new Color(0.45f, 0.96f, 0.88f)
                            : new Color(1.0f, 0.94f, 0.72f);
                }
                if (entry.Ring != null)
                {
                    var rc = flared
                        ? new Color(1.0f, 0.36f, 0.18f, 0.95f)
                        : allied
                            ? new Color(0.28f, 0.92f, 0.86f, 0.95f)
                            : new Color(0.95f, 0.78f, 0.28f, 0.90f);
                    entry.Ring.startColor = rc;
                    entry.Ring.endColor = rc;
                }
                if (entry.Root.activeSelf != mapVisible)
                    entry.Root.SetActive(mapVisible);
            }
            else if (FleetVisibility.TryGetGhost(ship.name, out var ghost))
            {
                SetMarkerMoving(entry, false);
                var ghostPos = new Vector3(ghost.MapX, map.Townheart.transform.position.y, ghost.MapZ);
                entry.Root.transform.position = ghostPos;
                if (entry.Label != null)
                {
                    entry.Label.text = $"👻 最后目击 · {ghost.OwnerName}";
                    entry.Label.color = new Color(0.76f, 0.74f, 0.68f, 0.88f);
                }
                if (entry.Ring != null)
                {
                    var gc = new Color(0.65f, 0.62f, 0.56f, 0.55f);
                    entry.Ring.startColor = gc;
                    entry.Ring.endColor = gc;
                }
                if (entry.Root.activeSelf != mapVisible)
                    entry.Root.SetActive(mapVisible);
            }
            else
            {
                if (entry.Root.activeSelf)
                    entry.Root.SetActive(false);
            }
        }

        foreach (var kv in WorldMapShips.ToArray())
        {
            if (!activeNames.Contains(kv.Key))
            {
                if (kv.Value.Root != null) UnityEngine.Object.Destroy(kv.Value.Root);
                WorldMapShips.Remove(kv.Key);
            }
        }
    }

    private static void SetMarkerMoving(WorldMapShipEntry entry, bool moving)
    {
        for (var i = 0; i < entry.Animators.Length; i++)
        {
            var anim = entry.Animators[i];
            if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null) continue;
            foreach (var p in anim.parameters)
            {
                if (p.type == AnimatorControllerParameterType.Bool && p.name == "IsMoving")
                {
                    anim.SetBool("IsMoving", moving);
                    break;
                }
            }
        }
    }

    private static WorldMapShipEntry CreateWorldMapShipMarker(WorldMap map, string ownerName)
    {
        var root = new GameObject($"WorldMap_FleetShip_{ownerName}");
        root.transform.SetParent(map.Townheart.transform.parent, false);
        root.layer = map.Townheart.gameObject.layer;

        var clonedMesh = false;
        var visuals = AccessTools.Field(typeof(WorldMapTownheart), "_visuals")?.GetValue(map.Townheart) as Array;
        if (visuals != null && visuals.Length > 0)
        {
            for (var i = 0; i < visuals.Length; i++)
            {
                var visStruct = visuals.GetValue(i);
                if (visStruct == null) continue;
                var visGo = AccessTools.Field(visStruct.GetType(), "GameObject")?.GetValue(visStruct) as GameObject;
                if (visGo != null)
                {
                    var clone = UnityEngine.Object.Instantiate(visGo, root.transform, false);
                    clone.transform.localPosition = Vector3.zero;
                    clone.transform.localRotation = Quaternion.identity;
                    SetLayerRecursively(clone, root.layer);
                    clone.SetActive(true);
                    clonedMesh = true;
                    break;
                }
            }
        }

        if (!clonedMesh)
            AttachGenuineBoatMesh(root.transform, 3.2f, root.layer);

        // Sea-chart locator ring around the miniature ship so it stands out at wide zoom
        var ringGo = new GameObject("MapShipRing");
        ringGo.transform.SetParent(root.transform, false);
        ringGo.layer = root.layer;
        var ring = ringGo.AddComponent<LineRenderer>();
        ring.useWorldSpace = false;
        ring.loop = true;
        ring.material = GetEmissiveMaterial(new Color(0.28f, 0.86f, 0.68f, 0.92f));
        ring.startWidth = 1.1f;
        ring.endWidth = 1.1f;
        const int segs = 28;
        ring.positionCount = segs;
        for (var i = 0; i < segs; i++)
        {
            var ang = i * Mathf.PI * 2f / segs;
            ring.SetPosition(i, new Vector3(Mathf.Cos(ang) * 10.5f, 0.35f, Mathf.Sin(ang) * 10.5f));
        }

        var label = AttachWorldBanner(root.transform, $"⚓ {ownerName}", new Color(1.0f, 0.94f, 0.72f), 12f, root.layer, 34f);

        return new WorldMapShipEntry
        {
            Root = root,
            Label = label,
            Ring = ring,
            Animators = root.GetComponentsInChildren<Animator>(true)
        };
    }

    private static void SetLayerRecursively(GameObject obj, int layer)
    {
        if (obj == null) return;
        obj.layer = layer;
        var t = obj.transform;
        for (var i = 0; i < t.childCount; i++)
            SetLayerRecursively(t.GetChild(i).gameObject, layer);
    }

    private static void AttachGenuineBoatMesh(Transform parent, float scale, int? overrideLayer = null)
    {
        var spawned = false;
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
                    if (overrideLayer.HasValue) SetLayerRecursively(clone, overrideLayer.Value);
                    clone.SetActive(true);
                    spawned = true;
                    break;
                }
            }
        }

        if (spawned) return;

        var hull = GameObject.CreatePrimitive(PrimitiveType.Cube);
        hull.transform.SetParent(parent, false);
        hull.transform.localPosition = new Vector3(0f, 0.25f, 0f);
        hull.transform.localScale = new Vector3(1.6f * scale, 0.55f * scale, 3.4f * scale);
        if (hull.TryGetComponent<Collider>(out var c1)) UnityEngine.Object.Destroy(c1);
        hull.GetComponent<Renderer>().sharedMaterial = GetLitMaterial(new Color(0.45f, 0.28f, 0.15f));
        if (overrideLayer.HasValue) hull.layer = overrideLayer.Value;

        var mast = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        mast.transform.SetParent(parent, false);
        mast.transform.localPosition = new Vector3(0f, 1.3f * scale, 0.2f * scale);
        mast.transform.localScale = new Vector3(0.12f * scale, 1.1f * scale, 0.12f * scale);
        if (mast.TryGetComponent<Collider>(out var c2)) UnityEngine.Object.Destroy(c2);
        mast.GetComponent<Renderer>().sharedMaterial = GetLitMaterial(new Color(0.32f, 0.20f, 0.11f));
        if (overrideLayer.HasValue) mast.layer = overrideLayer.Value;
    }

    internal static GameObject AttachCargoCrates(Transform parent, bool activeInitially)
    {
        var group = new GameObject("CargoCrates");
        group.transform.SetParent(parent, false);
        group.transform.localPosition = new Vector3(0f, 0.42f, 0.45f);

        var spawnedNative = false;
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

    private static TextMeshPro AttachWorldBanner(
        Transform parent,
        string text,
        Color accent,
        float height = 3.1f,
        int? overrideLayer = null,
        float fontSize = 5.5f)
    {
        var go = new GameObject("WorldBannerTMP");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, height, 0f);
        if (overrideLayer.HasValue) go.layer = overrideLayer.Value;

        var tmp = go.AddComponent<TextMeshPro>();
        var nativeFont = ResolveNativeTmpFont();
        if (nativeFont != null) tmp.font = nativeFont;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = accent;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.outlineWidth = 0.22f;
        tmp.outlineColor = new Color32(22, 18, 14, 235);

        var billboard = go.AddComponent<BillboardToCamera>();
        billboard.UseWorldMapCamera = overrideLayer.HasValue;
        return tmp;
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
    internal static void SpawnMuzzleFlash(Vector3 position,Vector3 direction)
    {
        var flash=new GameObject("CannonMuzzleSmoke");flash.transform.SetPositionAndRotation(position,Quaternion.LookRotation(direction));
        var ps=flash.AddComponent<ParticleSystem>();var main=ps.main;main.loop=false;main.duration=.2f;main.startLifetime=.35f;main.startSpeed=6;main.startSize=.45f;main.maxParticles=16;main.simulationSpace=ParticleSystemSimulationSpace.World;
        main.startColor=new ParticleSystem.MinMaxGradient(new Color(1,.7f,.2f),new Color(.3f,.3f,.3f,.6f));
        var emission=ps.emission;emission.rateOverTime=0;emission.SetBursts(new[]{new ParticleSystem.Burst(0,12)});var shape=ps.shape;shape.shapeType=ParticleSystemShapeType.Cone;shape.angle=18;
        ps.GetComponent<ParticleSystemRenderer>().sharedMaterial=GetEmissiveMaterial(new Color(1,.7f,.3f,.8f));ps.Play();UnityEngine.Object.Destroy(flash,1.2f);
    }

    internal static void SpawnNativeSplash(Vector3 pos)
    {
        var splash = GameSettings.Instance?.FXSettings?.Splash;
        if (splash != null)
        {
            try { ParticleController.Spawn(splash, new Vector3(pos.x, 0f, pos.z), Quaternion.identity); }
            catch (Exception e) { Plugin.Log("Splash spawn skipped: " + e.GetType().Name); }
        }
    }

    internal static void SpawnNativePopup(Vector3 pos, string text)
    {
        try { TextPopup.Spawn(pos, text); }
        catch (Exception e) { Plugin.Log("TextPopup skipped: " + e.GetType().Name); }
    }

    internal static void TriggerCameraShake(Vector3 worldPos, float intensity, float duration)
    {
        var cam = Camera.main;
        if (cam == null) return;
        var horizDist = Vector2.Distance(new Vector2(cam.transform.position.x, cam.transform.position.z), new Vector2(worldPos.x, worldPos.z));
        var falloff = Mathf.Clamp01(1f - horizDist / 120f);
        if (falloff <= 0.05f) return;
        _shakeIntensity = Mathf.Max(_shakeIntensity, intensity * falloff);
        _shakeRemaining = Mathf.Max(_shakeRemaining, duration);
    }

    private static void UpdateCameraShake(float dt)
    {
        if (_shakeRemaining <= 0f) return;
        _shakeRemaining -= dt;
        var camCtrl = CameraController.Instance;
        if (camCtrl != null)
        {
            var damper = Mathf.Clamp01(_shakeRemaining / 0.3f);
            var jitter = new Vector3(
                UnityEngine.Random.Range(-1f, 1f) * _shakeIntensity * damper,
                0f,
                UnityEngine.Random.Range(-1f, 1f) * _shakeIntensity * damper);
            camCtrl.transform.position += jitter * (dt * 12f);
        }
        if (_shakeRemaining <= 0f) _shakeIntensity = 0f;
    }

    internal static void SpawnWaterShockwaveRing(Vector3 pos, Color color, float maxRadius)
    {
        var ringGo = new GameObject("Flotsam_WaterShockwave");
        ringGo.transform.position = new Vector3(pos.x, 0.18f, pos.z);
        var lr = ringGo.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.loop = true;
        lr.material = GetEmissiveMaterial(color);
        lr.startWidth = 0.28f;
        lr.endWidth = 0.28f;
        const int segments = 28;
        lr.positionCount = segments;
        var expander = ringGo.AddComponent<ShockwaveRingAnimator>();
        expander.Line = lr;
        expander.MaxRadius = maxRadius;
        expander.BaseColor = color;
    }

    internal static void PlayUiClick()
    {
        var cam = Camera.main;
        if (cam == null) return;
        PlayFleetSound("ui_click", cam.transform.position + cam.transform.forward * 1.2f, 0.35f);
    }

    internal static void PlayFleetSound(string kind, Vector3 worldPos, float volume = 0.8f)
    {
        if (!CachedSfx.TryGetValue(kind, out var clip) || clip == null)
        {
            clip = FindOrSynthesizeAudioClip(kind);
            CachedSfx[kind] = clip;
        }
        if (clip != null)
            AudioSource.PlayClipAtPoint(clip, worldPos, Mathf.Clamp01(volume));
    }

    private static AudioClip FindOrSynthesizeAudioClip(string kind)
    {
        var loaded = Resources.FindObjectsOfTypeAll<AudioClip>();
        if (loaded != null && loaded.Length > 0)
        {
            string[] keywords = kind switch
            {
                "cannon_fire" => new[] { "cannon", "shoot", "launch", "explosion", "thud" },
                "shell_impact" => new[] { "explosion", "impact", "crash", "destroy", "splash" },
                "cargo_deliver" => new[] { "complete", "pickup", "store", "bell", "pop" },
                "raid_board" => new[] { "alert", "warning", "sword", "hit", "salvage" },
                "ui_click" => new[] { "click", "button", "tap", "select", "pop" },
                _ => new[] { "boat", "horn", "splash", "water" }
            };
            foreach (var kw in keywords)
            {
                var match = loaded.FirstOrDefault(c => c != null && c.name != null && c.length > 0.03f && c.length < 4.5f && c.name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);
                if (match != null) return match;
            }
        }

        const int sampleRate = 22050;
        var duration = kind == "ui_click" ? 0.06f : kind == "shell_impact" ? 0.55f : kind == "cannon_fire" ? 0.42f : 0.35f;
        var count = Mathf.CeilToInt(sampleRate * duration);
        var samples = new float[count];
        var rng = new System.Random(kind.GetHashCode());

        for (var i = 0; i < count; i++)
        {
            var t = (float)i / sampleRate;
            var env = Mathf.Exp(-t * (kind == "ui_click" ? 45f : kind == "shell_impact" ? 5.5f : 8.0f));
            var noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            if (kind == "ui_click")
            {
                samples[i] = Mathf.Sin(2f * Mathf.PI * 880f * t) * env * 0.4f;
            }
            else if (kind == "cannon_fire" || kind == "shell_impact")
            {
                var freq = kind == "cannon_fire" ? Mathf.Lerp(140f, 38f, t / duration) : Mathf.Lerp(110f, 28f, t / duration);
                var boom = Mathf.Sin(2f * Mathf.PI * freq * t);
                samples[i] = Mathf.Clamp((boom * 0.65f + noise * 0.45f) * env, -1f, 1f);
            }
            else if (kind == "cargo_deliver")
            {
                var freq = i < count / 2 ? 523.25f : 659.25f;
                samples[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * env * 0.55f;
            }
            else
            {
                var horn = Mathf.Sin(2f * Mathf.PI * 196f * t) * 0.45f + Mathf.Sin(2f * Mathf.PI * 293.66f * t) * 0.35f;
                samples[i] = (horn + noise * 0.15f) * env;
            }
        }

        var synth = AudioClip.Create($"FlotsamSfx_{kind}", count, 1, sampleRate, false);
        synth.SetData(samples, 0);
        return synth;
    }

    private sealed class ShockwaveRingAnimator : MonoBehaviour
    {
        public LineRenderer? Line;
        public float MaxRadius = 4f;
        public Color BaseColor = Color.white;
        private float _elapsed;
        private const float Lifetime = 0.65f;

        private void Update()
        {
            _elapsed += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            var t = Mathf.Clamp01(_elapsed / Lifetime);
            if (Line != null)
            {
                var r = Mathf.Lerp(0.4f, MaxRadius, 1f - (1f - t) * (1f - t));
                var count = Line.positionCount;
                for (var i = 0; i < count; i++)
                {
                    var ang = i * Mathf.PI * 2f / count;
                    Line.SetPosition(i, new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r));
                }
                var width = Mathf.Lerp(0.32f, 0.02f, t);
                Line.startWidth = width;
                Line.endWidth = width;
            }
            if (t >= 1f) Destroy(gameObject);
        }
    }

    private static Material GetLitMaterial(Color color)
    {
        if(LitMaterials.TryGetValue(color,out var cached))return cached;
        if (_litMat == null)
        {
            var sh = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Sprites/Default");
            _litMat = new Material(sh);
        }
        var result=new Material(_litMat){color=color};LitMaterials.Add(color,result);return result;
    }

    internal static Material GetEmissiveMaterial(Color color)
    {
        if(EmissiveMaterials.TryGetValue(color,out var cached))return cached;
        if (_emissiveMat == null)
        {
            var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            _emissiveMat = new Material(sh);
        }
        var result=new Material(_emissiveMat){color=color};EmissiveMaterials.Add(color,result);return result;
    }

    private sealed class BillboardToCamera : MonoBehaviour
    {
        public bool UseWorldMapCamera;

        private void LateUpdate()
        {
            Camera? cam = null;
            if (UseWorldMapCamera)
            {
                var map = GameManager.WorldMapManager?.WorldMap;
                if (map != null) cam = map.GetComponentInChildren<Camera>();
            }
            cam ??= Camera.main;
            if (cam == null) return;
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, Vector3.up);
        }
    }
}
