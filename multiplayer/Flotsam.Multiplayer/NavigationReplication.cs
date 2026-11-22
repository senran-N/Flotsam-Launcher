using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

internal static class NavigationReplication
{
    private static readonly PoseTrackMap MapTownheartTrack = new PoseTrackMap();
    private static readonly FieldInfo? PlanningPathField = AccessTools.Field(typeof(WorldMap), "_planningPath");
    private static readonly FieldInfo? MapTilesField = AccessTools.Field(typeof(WorldMap), "_tiles");
    private static readonly MethodInfo? CalculatePathMethod = AccessTools.Method(typeof(MapPath), "CalculatePath", new[] { typeof(Vector3) });
    private static readonly FieldInfo? HasReachedMiddayField = AccessTools.Field(typeof(TimeManager), "_hasReachedMidday");
    private static readonly FieldInfo? NightLightsEnabledField = AccessTools.Field(typeof(TimeManager), "_nightLightsEnabled");
    private static readonly FieldInfo? NightLightsListField = AccessTools.Field(typeof(TimeManager), "_nightLights");
    private static readonly MethodInfo? EvalNightLightsMethod = AccessTools.Method(typeof(TimeManager), "ReturnEvaluateNightLightsEnabled");
    private static float _nextMapPhysicsSync;
    private static float _mapMovingHoldUntil;
    private static Day.E_DayTime? _lastDayPhase;

    public static void Reset()
    {
        MapTownheartTrack.Clear();
        _mapMovingHoldUntil = 0f;
        _lastDayPhase = null;
    }

    public static void Capture(WorldFrame frame)
    {
        var time = GameManager.TimeManager;
        frame.days = time.Days.Count;
        frame.dayTime = time.CurrentDay?.CurrentTime ?? 0;
        frame.playTime = time.CurrentPlayTime;
        var map = GameManager.WorldMapManager.WorldMap;
        frame.mapActive = LiveNavigation.ViewOpen;
        frame.navigationActive = LiveNavigation.Moving;
        frame.mapX = map.Townheart.transform.position.x;
        frame.mapZ = map.Townheart.transform.position.z;
        frame.mapRotation = map.Townheart.transform.eulerAngles.y;
    }

    private static void Set(object instance, string property, object value) =>
        AccessTools.PropertySetter(instance.GetType(), property).Invoke(instance, new[] { value });

    public static void Apply(WorldFrame frame)
    {
        LiveNavigation.ReceiveMovement(frame.navigationActive);
        if (frame.days < 1 || frame.days > 100000 || !Replication.Finite(frame.dayTime) || frame.dayTime < 0 || !Replication.Finite(frame.playTime) || !Enum.IsDefined(typeof(GameSpeed), frame.speed))
            throw new InvalidOperationException("Invalid world clock");

        var time = GameManager.TimeManager;
        if (time.Days.Count != frame.days)
        {
            var day = new Day(TimeManager.DayDuration, TimeManager.CycleDuration - TimeManager.DayDuration, 24);
            if (frame.days - time.Days.Count > 7) throw new InvalidOperationException("World clock requires resync");
            while (time.Days.Count < frame.days) time.Days.Add(day);
            if (time.Days.Count > frame.days) time.Days.RemoveRange(frame.days, time.Days.Count - frame.days);
            Set(time, nameof(time.CurrentDay), time.Days[frame.days - 1]);
        }
        if (time.CurrentDay != null)
        {
            var nextPhase = frame.dayTime > time.CurrentDay.DaytimeLength ? Day.E_DayTime.Night : Day.E_DayTime.Day;
            Set(time.CurrentDay, nameof(Day.CurrentTime), frame.dayTime);
            Set(time.CurrentDay, nameof(Day.CurrentHour), Mathf.FloorToInt(frame.dayTime / (time.CurrentDay.DayLength / 24)));
            Set(time.CurrentDay, nameof(Day.DayTime), nextPhase);
            HasReachedMiddayField?.SetValue(time, true);

            if (_lastDayPhase.HasValue && _lastDayPhase.Value != nextPhase)
            {
                GameEventDispatcher.Dispatch(nextPhase == Day.E_DayTime.Night
                    ? GameEventType.NighttimeStarted
                    : GameEventType.DaytimeStarted);
            }
            _lastDayPhase = nextPhase;

            if (EvalNightLightsMethod != null && NightLightsEnabledField != null)
            {
                var wantLights = (bool)EvalNightLightsMethod.Invoke(time, null)!;
                var curLights = (bool)NightLightsEnabledField.GetValue(time)!;
                if (wantLights != curLights)
                {
                    NightLightsEnabledField.SetValue(time, wantLights);
                    if (NightLightsListField?.GetValue(time) is System.Collections.IEnumerable lights)
                    {
                        foreach (var item in lights)
                        {
                            if (item is NightLight nl && nl != null)
                                nl.SetEnabled(wantLights, 0.15f);
                        }
                    }
                }
            }
        }
        Set(time, nameof(time.CurrentPlayTime), frame.playTime);
        if ((int)GameSpeedManager.GameSpeed != frame.speed)
            AccessTools.Method(typeof(GameSpeedManager), "SetGameSpeedAndTimeScale").Invoke(null, new object[] { (GameSpeed)frame.speed });

        if (!Replication.Finite(frame.mapX) || !Replication.Finite(frame.mapZ) || !Replication.Finite(frame.mapRotation))
            throw new InvalidOperationException("Invalid map position");

        var targetMapX = frame.mapX;
        var targetMapZ = frame.mapZ;
        var targetMapRot = frame.mapRotation;
        if (Plugin.Options.mode == "independent" && Community.PlayerCommunity != null && frame.ships != null)
        {
            var localKey = Replication.ShipKey(Community.PlayerCommunity);
            foreach (var ship in frame.ships)
            {
                if (ship != null && ship.name == localKey &&
                    Replication.Finite(ship.mapX) && Replication.Finite(ship.mapZ) && Replication.Finite(ship.mapRotation))
                {
                    targetMapX = ship.mapX;
                    targetMapZ = ship.mapZ;
                    targetMapRot = ship.mapRotation;
                    break;
                }
            }
        }

        var mapHeart = GameManager.WorldMapManager.WorldMap.Townheart;
        var targetPose = new PoseSample(targetMapX, 0f, targetMapZ, targetMapRot);
        if (MapTownheartTrack.UpdateTarget(1L, ReplicaFreeze.CurrentPose(mapHeart.transform), targetPose, 2500f))
            mapHeart.Teleport(new Vector3(targetMapX, 0f, targetMapZ), Quaternion.Euler(0f, targetMapRot, 0f));
    }

    public static void Interpolate(float alpha)
    {
        var map = GameManager.WorldMapManager?.WorldMap;
        if (map?.Townheart == null) return;
        if (MapTownheartTrack.Sample(1L, alpha, out var pose))
        {
            var cur = ReplicaFreeze.CurrentPose(map.Townheart.transform);
            var distSq = cur.DistanceSq(pose);
            if (distSq > 0.00001f || cur.AngleDeltaAbs(pose) > 0.05f)
            {
                map.Townheart.Teleport(new Vector3(pose.X, 0f, pose.Z), Quaternion.Euler(0f, pose.Yaw, 0f));
                if (distSq > 0.0002f)
                    _mapMovingHoldUntil = Time.unscaledTime + 0.24f;
            }
        }

        var mapMovingNow = Time.unscaledTime < _mapMovingHoldUntil;
        if (mapMovingNow && !map.Townheart.IsMoving)
            map.Townheart.OnStartMove();
        else if (!mapMovingNow && map.Townheart.IsMoving)
            map.Townheart.OnEndMove();

        var heartWorld = map.Townheart.transform.position;
        Shader.SetGlobalVector("TOWNHEART_POSITION", heartWorld);
        if (GameSettings.Instance?.GameplaySettings != null)
            Shader.SetGlobalFloat("TOWNHEART_RADIUS", GameSettings.Instance.GameplaySettings.SwimmingRadius);
        if (GameManager.WorldManager?.World?.TileProperties != null)
            Shader.SetGlobalFloat("TOWNHEART_RADIUS_NOISE_STRENGTH", GameManager.WorldManager.World.TileProperties.MapOverlayWeight);

        if (Time.unscaledTime >= _nextMapPhysicsSync)
        {
            _nextMapPhysicsSync = Time.unscaledTime + 0.25f;
            if (MapTilesField?.GetValue(map) is IEnumerable<WorldMapTile> tiles)
            {
                var pos2D = new Vector2(heartWorld.x, heartWorld.z);
                foreach (var tile in tiles)
                    tile?.UpdatePhyscis(pos2D, 62500f);
            }
        }
    }

    public static List<Vector2> ParseWaypoints(string? encoded, float fallbackX, float fallbackZ)
    {
        var list = new List<Vector2>();
        if (!string.IsNullOrWhiteSpace(encoded))
        {
            foreach (var token in encoded!.Split(';'))
            {
                var parts = token.Split(',');
                if (parts.Length == 2 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var wx) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var wz) &&
                    Replication.Finite(wx) && Replication.Finite(wz))
                {
                    list.Add(new Vector2(wx, wz));
                }
            }
        }
        if (list.Count == 0 && Replication.Finite(fallbackX) && Replication.Finite(fallbackZ))
            list.Add(new Vector2(fallbackX, fallbackZ));
        return list;
    }

    public static string EncodeMapPath(MapPath path)
    {
        var steps = Mathf.Clamp(Mathf.CeilToInt(path.Length / 25f), 1, 20);
        var parts = new List<string>(steps);
        for (var i = 1; i <= steps; i++)
        {
            var pt = path.ReturnLerpedPosition(i / (float)steps);
            parts.Add(pt.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + pt.y.ToString("0.0", CultureInfo.InvariantCulture));
        }
        return string.Join(";", parts);
    }

    public static string? Execute(GameCommand command)
    {
        var map = GameManager.WorldMapManager.WorldMap;
        switch (command.kind)
        {
            case "speed":
                if (!Enum.IsDefined(typeof(GameSpeed), command.value) || command.value < 1) return "速度无效";
                GameSpeedManager.SetGameSpeed((GameSpeed)command.value);
                return null;
            case "speedZero":
                GameSpeedManager.ToggleGameSpeedZero();
                return null;
            case "mapOpen":
                LiveNavigation.Ensure();
                return null;
            case "mapClose":
                LiveNavigation.Stop();
                map.ClearMouseMovementTarget();
                map.StopAllMovement();
                return null;
            case "navigate":
                LiveNavigation.Ensure();
                if (!map.isActiveAndEnabled || map.IsTownMovementBlocked) return "当前船只引擎冷却或电力不足，暂时无法航行";
                if (!Enum.IsDefined(typeof(MouseMovementCursorProperties.Gear), command.value)) return "航行方向无效";
                if ((map.Townheart.transform.position - new Vector3(command.x, 0, command.z)).sqrMagnitude > 100000000) return "航行目标过远";
                LiveNavigation.MouseInput();
                map.SetMouseMovementTarget(new Vector3(command.x, 0, command.z), (MouseMovementCursorProperties.Gear)command.value);
                return null;
            case "navigatePlan":
                LiveNavigation.Ensure();
                if (!map.isActiveAndEnabled || map.IsTownMovementBlocked) return "当前船只引擎冷却或电力不足，暂时无法航行";
                var dest = new Vector3(command.x, 0f, command.z);
                if ((map.Townheart.transform.position - dest).sqrMagnitude > 100000000) return "航行目标过远";
                if (PlanningPathField?.GetValue(map) is MapPath plan && CalculatePathMethod != null)
                {
                    CalculatePathMethod.Invoke(plan, new object[] { dest });
                    if (plan.EvaluatedState == MapPath.State.Ok)
                    {
                        LiveNavigation.PlannedInput();
                        map.MoveTo(plan);
                        return null;
                    }
                }
                LiveNavigation.MouseInput();
                map.SetMouseMovementTarget(dest, MouseMovementCursorProperties.Gear.Forward);
                return null;
            case "navigateKeys":
                LiveNavigation.Ensure();
                if (!map.isActiveAndEnabled || map.IsTownMovementBlocked) return "当前船只引擎冷却或电力不足，暂时无法航行";
                if (Math.Abs(command.x) > 1 || Math.Abs(command.z) > 1) return "航行输入无效";
                LiveNavigation.KeyInput(new Vector2(command.x, command.z));
                return null;
            case "navigateStop":
                LiveNavigation.Stop();
                map.ClearMouseMovementTarget();
                map.StopAllMovement();
                return null;
            default:
                return "未知航行指令";
        }
    }
}
