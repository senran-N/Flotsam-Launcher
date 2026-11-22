using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Manages independent per-ship Fog-of-War (<see cref="WorldMapFogOfWar"/>), asymmetric
/// <see cref="WatchTower"/> vision radii (400m base / 750m tower), global distress/rally flares,
/// bidirectional vision-sharing alliances, automatic betrayal on hostile actions, 3D renderer
/// culling for unspotted ships, and WorldMap last-known ghost markers.
/// </summary>
internal static class FleetVisibility
{
    internal struct GhostMarkerInfo
    {
        public float MapX;
        public float MapZ;
        public float SeenAtPlayTime;
        public string OwnerName;
    }

    private static readonly HashSet<string> Alliances = new HashSet<string>(StringComparer.Ordinal);
    private static readonly DiplomacyRequests Requests = new DiplomacyRequests();
    private static readonly Dictionary<string, float> FlareUntilByShip = new Dictionary<string, float>(StringComparer.Ordinal);
    private static readonly Dictionary<string, GhostMarkerInfo> LastKnownGhosts = new Dictionary<string, GhostMarkerInfo>(StringComparer.Ordinal);
    private static readonly Dictionary<Community, HashSet<Renderer>> HiddenRenderers = new Dictionary<Community, HashSet<Renderer>>();
    private static readonly Dictionary<Community, HashSet<Canvas>> HiddenCanvases = new Dictionary<Community, HashSet<Canvas>>();
    private static readonly HashSet<int> SanitizedClientFogMeshes = new HashSet<int>();

    private static readonly FieldInfo? FogInstancesField = AccessTools.Field(typeof(WorldMapFogOfWar), "_instances");
    private static readonly FieldInfo? FogColorsField = AccessTools.Field(typeof(WorldMapFogOfWar), "_colors");
    private static readonly FieldInfo? FogMeshField = AccessTools.Field(typeof(WorldMapFogOfWar), "_mesh");
    private static readonly FieldInfo? MapTilesField = AccessTools.Field(typeof(WorldMap), "_tiles");

    private static bool _clientNeedsInitialFogWipe;
    private static float _next3DCullAt;

    internal static void Reset()
    {
        RestoreAllHidden3D();
        if(Plugin.Instance?.Network?.IsHost==true&&GameSession.Ready())return;
        Alliances.Clear();
        Requests.Clear();
        FlareUntilByShip.Clear();
        LastKnownGhosts.Clear();
        SanitizedClientFogMeshes.Clear();
        _clientNeedsInitialFogWipe = false;
        _next3DCullAt = 0f;
    }

    internal static void ClearDiplomacy(){Alliances.Clear();Requests.Clear();}
    internal static string[] SaveAlliances()=>Alliances.ToArray();
    internal static void RestoreAlliances(string[] values){foreach(var v in values??Array.Empty<string>())Alliances.Add(v);}
    internal static bool Handles(string kind) =>
        kind == "shipFlare" ||
        kind == "shipAllyRequest" ||
        kind == "shipAllyAccept" || kind == "shipAllyReject" ||
        kind == "shipAllyBreak";

    private static string PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

    private static string DirectedKey(string from, string to) => from + "->" + to;

    internal static bool HasActiveWatchTower(Community? owner)
    {
        if (owner?.Buildables == null) return false;
        foreach (var b in owner.Buildables)
        {
            if (b == null || MoveConstructionCommands.IsStored(b)) continue;
            if (b.BuildPhase != BuildPhase.Finished || !b.IsActive) continue;
            if (b.TryGetComponent<WatchTower>(out _)) return true;
            var pName = b.Properties?.name;
            if (!string.IsNullOrEmpty(pName) && pName!.IndexOf("WatchTower", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    internal static float GetFlareUntil(string shipKey, float nowPlayTime)
    {
        if (string.IsNullOrEmpty(shipKey)) return 0f;
        return FlareUntilByShip.TryGetValue(shipKey, out var until) && until > nowPlayTime ? until : 0f;
    }

    internal static string[] GetAlliesFor(string shipKey, IEnumerable<Community> activeCommunities)
    {
        if (string.IsNullOrEmpty(shipKey) || Alliances.Count == 0)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var comm in activeCommunities)
        {
            if (comm == null) continue;
            var otherKey = Replication.ShipKey(comm);
            if (string.IsNullOrEmpty(otherKey) || otherKey == shipKey) continue;
            if (Alliances.Contains(PairKey(shipKey, otherKey)))
                result.Add(otherKey);
        }
        result.Sort(StringComparer.Ordinal);
        return result.ToArray();
    }

    internal static bool AreAlliedHost(string shipA, string shipB)
    {
        if (string.IsNullOrEmpty(shipA) || string.IsNullOrEmpty(shipB)) return false;
        if (shipA == shipB) return true;
        return Alliances.Contains(PairKey(shipA, shipB));
    }

    internal static bool AreAlliedState(FleetShipState? a, FleetShipState? b)
    {
        if (a == null || b == null) return false;
        if (a.name == b.name) return true;
        if (a.allies != null && Array.IndexOf(a.allies, b.name) >= 0) return true;
        if (b.allies != null && Array.IndexOf(b.allies, a.name) >= 0) return true;
        return false;
    }

    internal static AllianceRequestState[] CaptureRequests() => Requests.Capture(Time.realtimeSinceStartup);

    internal static void CheckAndBreakAllianceOnHostileAction(Community attacker, Community victim)
    {
        var aKey = Replication.ShipKey(attacker);
        var vKey = Replication.ShipKey(victim);
        if (string.IsNullOrEmpty(aKey) || string.IsNullOrEmpty(vKey) || aKey == vKey) return;
        if (!Alliances.Remove(PairKey(aKey, vKey))) return;

        Requests.RemoveBetween(aKey, vKey);

        var aName = ResolveOwnerDisplayName(attacker);
        var vName = ResolveOwnerDisplayName(victim);
        var pos = FleetEngineBundle.HeartBuildable(victim)?.transform.position ?? Construction.TownheartPosition;
        FleetPhysicalActors.RecordAndSpawn(
            "betrayalNotice",
            $"背信弃义！{aName} 撕毁同盟突袭了 {vName}！",
            pos,
            pos,
            sourceShip: aKey,
            targetShip: vKey);
    }

    internal static string? Execute(GameCommand command, Community actor)
    {
        var actorKey = Replication.ShipKey(actor);
        var actorName = ResolveOwnerDisplayName(actor);
        var actorPos = FleetEngineBundle.HeartBuildable(actor)?.transform.position ?? Construction.TownheartPosition;
        var nowPlay = GameManager.TimeManager != null ? GameManager.TimeManager.CurrentPlayTime : Time.realtimeSinceStartup;

        if (command.kind == "shipFlare")
        {
            FlareUntilByShip[actorKey] = nowPlay + 35f;
            var label = string.IsNullOrWhiteSpace(command.text)
                ? $"{actorName} 发射了海上信号弹 (全图可见 35s)"
                : $"{actorName} · {command.text.Trim()}";
            FleetPhysicalActors.RecordAndSpawn("seaFlare", label, actorPos, actorPos, sourceShip: actorKey);
            return null;
        }

        var other = Replication.ResolveShipCommunity(command.text);
        if (other == null || ReferenceEquals(other, actor))
            return "目标船只不存在";

        var otherKey = Replication.ShipKey(other);
        var otherName = ResolveOwnerDisplayName(other);
        var otherPos = FleetEngineBundle.HeartBuildable(other)?.transform.position ?? Construction.TownheartPosition;

        switch (command.kind)
        {
            case "shipAllyRequest":
            {
                if (Alliances.Contains(PairKey(actorKey, otherKey)))
                    return $"已与 {otherName} 处于同盟状态";

                var requestError = Requests.Request(actorKey, otherKey, actorName, Time.realtimeSinceStartup);
                if (requestError != null) return requestError;
                FleetPhysicalActors.RecordAndSpawn(
                    "allyRequest",
                    $"{actorName} 请求与 {otherName} 结盟共享视野",
                    otherPos,
                    otherPos,
                    sourceShip: actorKey,
                    targetShip: otherKey);
                return null;
            }
            case "shipAllyAccept":
            {
                var responseError = Requests.Respond(command.reference, actorKey, otherKey, Time.realtimeSinceStartup);
                if (responseError != null) return responseError;
                Requests.RemoveBetween(actorKey,otherKey);
                Alliances.Add(PairKey(actorKey, otherKey));
                FleetPhysicalActors.RecordAndSpawn(
                    "allyNotice",
                    $"{actorName} 与 {otherName} 已缔结海上同盟，共享实时海图视野！",
                    actorPos,
                    otherPos,
                    sourceShip: actorKey,
                    targetShip: otherKey);
                return null;
            }
            case "shipAllyReject":
                return Requests.Respond(command.reference, actorKey, otherKey, Time.realtimeSinceStartup);
            case "shipAllyBreak":
            {
                if (!Alliances.Remove(PairKey(actorKey, otherKey)))
                    return $"当前未与 {otherName} 结盟";
                FleetPhysicalActors.RecordAndSpawn(
                    "allyBreak",
                    $"{actorName} 解除了与 {otherName} 的同盟关系",
                    actorPos,
                    otherPos,
                    sourceShip: actorKey,
                    targetShip: otherKey);
                return null;
            }
            default:
                return "未知外交指令";
        }
    }

    internal static void RevealChartAreaForShip(Community? targetShip, Vector2 mapPos, float radius)
    {
        var shipKey = Replication.ShipKey(targetShip);
        var scenePos = FleetOwnedNavigation.MapToScene(mapPos.x, mapPos.y);
        FleetPhysicalActors.RecordAndSpawn(
            "seaChartReveal",
            "旧海图揭开周边迷雾",
            scenePos,
            new Vector3(mapPos.x, radius, mapPos.y),
            sourceShip: shipKey,
            targetShip: shipKey,
            subKind: Mathf.RoundToInt(radius));
    }

    internal static void ApplyChartRevealIfAuthorized(string ownerShipKey, float mapX, float mapZ, float radius)
    {
        var net = Plugin.Instance?.Network;
        var localKey = Replication.ShipKey(Community.PlayerCommunity);
        var allowed = Plugin.Options.mode != "independent" || ownerShipKey == localKey;
        if (!allowed && net?.Ships != null)
        {
            var localShip = net.Ships.FirstOrDefault(s => s != null && s.name == localKey);
            var ownerShip = net.Ships.FirstOrDefault(s => s != null && s.name == ownerShipKey);
            allowed = AreAlliedState(localShip, ownerShip);
        }
        if (!allowed) return;

        var center = new Vector3(mapX, 0f, mapZ);
        WorldMapFogOfWar.ScoutArea(center, radius);
        var map = GameManager.WorldMapManager?.WorldMap;
        if (map != null && MapTilesField?.GetValue(map) is IEnumerable<WorldMapTile> tiles)
        {
            foreach (var tile in tiles)
                tile?.UpdateScouting(center, radius, radius * 0.65f);
        }
    }

    internal static void OnLocalShipAdoptedOnClient()
    {
        if (Plugin.Options.mode != "independent" || Plugin.Instance?.Network?.IsClient != true)
            return;
        _clientNeedsInitialFogWipe = true;
        SanitizedClientFogMeshes.Clear();
        WipeClientFogMeshes();
    }

    internal static void WipeClientFogMeshes()
    {
        if (FogInstancesField?.GetValue(null) is not IEnumerable instances)
            return;

        foreach (var obj in instances)
        {
            if (obj is not WorldMapFogOfWar fow || fow == null) continue;
            var id = fow.GetInstanceID();
            if (!SanitizedClientFogMeshes.Add(id)) continue;

            if (FogColorsField?.GetValue(fow) is Color[] colors &&
                FogMeshField?.GetValue(fow) is Mesh mesh &&
                mesh != null)
            {
                for (var i = 0; i < colors.Length; i++)
                    colors[i].a = 1f;
                mesh.colors = colors;
            }
        }
    }

    internal static bool IsShipVisibleToLocal(FleetShipState? targetShip, FleetShipState[]? allShips = null)
    {
        if (targetShip == null) return false;
        if (Plugin.Options.mode != "independent") return true;

        var localKey = Replication.ShipKey(Community.PlayerCommunity);
        if (string.IsNullOrEmpty(localKey) || targetShip.name == localKey)
            return true;

        var nowPlay = GameManager.TimeManager != null ? GameManager.TimeManager.CurrentPlayTime : 0f;
        var targetFlared = targetShip.flareUntil > nowPlay;
        if (targetFlared) return true;

        var ships = allShips ?? Plugin.Instance?.Network?.Ships ?? Array.Empty<FleetShipState>();
        var localShip = ships.FirstOrDefault(s => s != null && s.name == localKey);
        if (localShip == null)
        {
            var myHeart = Construction.TownheartPosition;
            var dx = targetShip.x - myHeart.x;
            var dz = targetShip.z - myHeart.z;
            var r = FleetMechanicsMath.GetVisionRadius(HasActiveWatchTower(Community.PlayerCommunity));
            return dx * dx + dz * dz <= r * r;
        }

        if (AreAlliedState(localShip, targetShip))
            return true;

        var myRadius = FleetMechanicsMath.GetVisionRadius(localShip.hasWatchTower);
        if (FleetMechanicsMath.IsShipSpotted(localShip.mapX, localShip.mapZ, myRadius, targetShip.mapX, targetShip.mapZ, false, false))
            return true;

        // Allied ships also share their live WatchTower / hull vision rings with us
        foreach (var ally in ships)
        {
            if (ally == null || ally.name == localKey || ally.name == targetShip.name) continue;
            if (!AreAlliedState(localShip, ally)) continue;
            var allyRadius = FleetMechanicsMath.GetVisionRadius(ally.hasWatchTower);
            if (FleetMechanicsMath.IsShipSpotted(ally.mapX, ally.mapZ, allyRadius, targetShip.mapX, targetShip.mapZ, false, false))
                return true;
        }

        return false;
    }

    internal static void ScoutAuthorizedFogOfWar(WorldMap map, FleetShipState[] ships)
    {
        if (_clientNeedsInitialFogWipe)
        {
            WipeClientFogMeshes();
            _clientNeedsInitialFogWipe = false;
        }
        else if (Plugin.Options.mode == "independent" && Plugin.Instance?.Network?.IsClient == true)
        {
            // Sanitize any newly streamed WorldTile fog meshes that inherited Host's explored bytes
            WipeClientFogMeshes();
        }

        var localKey = Replication.ShipKey(Community.PlayerCommunity);
        var localShip = ships.FirstOrDefault(s => s != null && s.name == localKey);
        var nowPlay = GameManager.TimeManager != null ? GameManager.TimeManager.CurrentPlayTime : 0f;
        var tiles = MapTilesField?.GetValue(map) as IEnumerable<WorldMapTile>;

        foreach (var ship in ships)
        {
            if (ship == null || !Replication.Finite(ship.mapX) || !Replication.Finite(ship.mapZ)) continue;

            var canScoutFromShip = Plugin.Options.mode != "independent" ||
                                   ship.name == localKey ||
                                   AreAlliedState(localShip, ship);
            var isFlared = ship.flareUntil > nowPlay;
            if (!canScoutFromShip && !isFlared) continue;

            var fowRange = canScoutFromShip
                ? FleetMechanicsMath.GetVisionRadius(ship.hasWatchTower)
                : 140f; // Flare reveals immediate waters around the distress flare
            var scoutRange = ship.hasWatchTower ? 420f : 260f;
            var scoutPos = new Vector3(ship.mapX, 0f, ship.mapZ);

            WorldMapFogOfWar.ScoutArea(scoutPos, fowRange);
            if (tiles != null && canScoutFromShip)
            {
                foreach (var tile in tiles)
                    tile?.UpdateScouting(scoutPos, fowRange, scoutRange);
            }
        }
    }

    internal static void RecordSpottedGhost(FleetShipState ship)
    {
        if (ship == null || string.IsNullOrEmpty(ship.name)) return;
        var nowPlay = GameManager.TimeManager != null ? GameManager.TimeManager.CurrentPlayTime : 0f;
        LastKnownGhosts[ship.name] = new GhostMarkerInfo
        {
            MapX = ship.mapX,
            MapZ = ship.mapZ,
            SeenAtPlayTime = nowPlay,
            OwnerName = ship.ownerName
        };
    }

    internal static bool TryGetGhost(string shipName, out GhostMarkerInfo ghost) =>
        LastKnownGhosts.TryGetValue(shipName, out ghost);

    internal static void Tick3DVisibility()
    {
        if (!GameSession.Ready()) return;
        if (Plugin.Options.mode != "independent")
        {
            RestoreAllHidden3D();
            return;
        }
        if (Time.unscaledTime < _next3DCullAt) return;
        _next3DCullAt = Time.unscaledTime + 0.25f;

        var ships = Plugin.Instance?.Network?.Ships;
        if (ships == null || ships.Length == 0) return;

        foreach (var comm in Community.Communities)
        {
            if (comm == null) continue;
            if (ReferenceEquals(comm, Community.PlayerCommunity))
            {
                SetCommunity3DVisible(comm, true);
                continue;
            }
            var key = Replication.ShipKey(comm);
            var shipState = ships.FirstOrDefault(s => s != null && s.name == key);
            if (shipState == null) continue;
            var visible = IsShipVisibleToLocal(shipState, ships);
            SetCommunity3DVisible(comm, visible);
        }
    }

    private static void SetCommunity3DVisible(Community comm, bool visible)
    {
        if (visible)
        {
            if (HiddenRenderers.TryGetValue(comm, out var rSet))
            {
                foreach (var r in rSet)
                    if (r != null) r.enabled = true;
                HiddenRenderers.Remove(comm);
            }
            if (HiddenCanvases.TryGetValue(comm, out var cSet))
            {
                foreach (var c in cSet)
                    if (c != null) c.enabled = true;
                HiddenCanvases.Remove(comm);
            }
            return;
        }

        if (!HiddenRenderers.TryGetValue(comm, out var hiddenR))
        {
            hiddenR = new HashSet<Renderer>();
            HiddenRenderers[comm] = hiddenR;
        }
        if (!HiddenCanvases.TryGetValue(comm, out var hiddenC))
        {
            hiddenC = new HashSet<Canvas>();
            HiddenCanvases[comm] = hiddenC;
        }

        void HideHierarchy(Component? root)
        {
            if (root == null) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r != null && r.enabled)
                {
                    r.enabled = false;
                    hiddenR.Add(r);
                }
            }
            foreach (var c in root.GetComponentsInChildren<Canvas>(false))
            {
                if (c != null && c.enabled)
                {
                    c.enabled = false;
                    hiddenC.Add(c);
                }
            }
        }

        if (comm.Buildables != null)
        {
            foreach (var b in comm.Buildables)
                if (b != null && !MoveConstructionCommands.IsStored(b)) HideHierarchy(b);
        }
        if (comm.Agents != null)
        {
            foreach (var a in comm.Agents)
                if (a != null) HideHierarchy(a);
        }
        if (comm.Birds != null)
        {
            foreach (var bird in comm.Birds)
                if (bird != null) HideHierarchy(bird);
        }
    }

    private static void RestoreAllHidden3D()
    {
        foreach (var set in HiddenRenderers.Values)
            foreach (var r in set)
                if (r != null) r.enabled = true;
        HiddenRenderers.Clear();

        foreach (var set in HiddenCanvases.Values)
            foreach (var c in set)
                if (c != null) c.enabled = true;
        HiddenCanvases.Clear();
    }

    internal static string ResolveOwnerDisplayName(Community? comm)
    {
        if (comm == null) return "船长";
        var net = Plugin.Instance?.Network;
        var key = Replication.ShipKey(comm);
        var state = net?.Ships.FirstOrDefault(s => s != null && s.name == key);
        if (state != null && !string.IsNullOrWhiteSpace(state.ownerName))
            return state.ownerName;
        var extracted = FleetRuntime.ExtractShipOwnerName(comm.Name);
        if (!string.IsNullOrWhiteSpace(extracted))
            return extracted;
        return string.IsNullOrWhiteSpace(Plugin.Options.nickname) ? "房主" : Plugin.Options.nickname;
    }
}
