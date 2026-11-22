using System;
using System.Collections.Generic;

namespace Flotsam.Multiplayer;

public readonly struct PoseSample
{
    public readonly float X;
    public readonly float Y;
    public readonly float Z;
    public readonly float Yaw;

    public PoseSample(float x, float y, float z, float yaw)
    {
        X = x;
        Y = y;
        Z = z;
        Yaw = NormalizeDegrees(yaw);
    }

    public float DistanceSq(PoseSample other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    public float AngleDeltaAbs(PoseSample other) => MathF.Abs(ShortestAngleDelta(Yaw, other.Yaw));

    public static float NormalizeDegrees(float degrees)
    {
        var wrapped = degrees % 360f;
        if (wrapped < 0f) wrapped += 360f;
        return wrapped;
    }

    public static float ShortestAngleDelta(float fromDegrees, float toDegrees)
    {
        var delta = (toDegrees - fromDegrees) % 360f;
        if (delta > 180f) delta -= 360f;
        else if (delta < -180f) delta += 360f;
        return delta;
    }

    public static PoseSample Lerp(PoseSample from, PoseSample to, float alpha)
    {
        var t = Math.Clamp(alpha, 0f, 1f);
        return new PoseSample(
            from.X + (to.X - from.X) * t,
            from.Y + (to.Y - from.Y) * t,
            from.Z + (to.Z - from.Z) * t,
            from.Yaw + ShortestAngleDelta(from.Yaw, to.Yaw) * t);
    }
}

/// <summary>
/// Synchronizes snapshot-to-snapshot linear interpolation across all replica entity tables.
/// Using a shared span parameter eliminates the 4:1 sawtooth velocity stutter caused by
/// memoryless exponential Lerp at 2x/4x+ GameSpeed and keeps co-moving entities in lockstep.
/// </summary>
public sealed class SnapshotSpanClock
{
    private const float DefaultInterval = 0.05f;
    private const float MinDuration = 0.035f;
    private const float MaxDuration = 0.25f;
    private const float JitterCushion = 1.08f;

    private float _lastArrivalTime = -1f;
    private float _smoothedInterval = DefaultInterval;
    private float _spanDuration = DefaultInterval * JitterCushion;
    private float _elapsed;

    public float SpanDuration => _spanDuration;
    public float Alpha => _spanDuration > 0.0001f ? Math.Clamp(_elapsed / _spanDuration, 0f, 1f) : 1f;

    public void Reset()
    {
        _lastArrivalTime = -1f;
        _smoothedInterval = DefaultInterval;
        _spanDuration = DefaultInterval * JitterCushion;
        _elapsed = 0f;
    }

    public float BeginFrame(float unscaledNow)
    {
        if (_lastArrivalTime >= 0f)
        {
            var rawInterval = unscaledNow - _lastArrivalTime;
            if (rawInterval >= 0.02f && rawInterval <= MaxDuration)
                _smoothedInterval += (rawInterval - _smoothedInterval) * 0.25f;
        }
        _lastArrivalTime = unscaledNow;
        _elapsed = 0f;
        _spanDuration = Math.Clamp(_smoothedInterval * JitterCushion, MinDuration, MaxDuration);
        return _spanDuration;
    }

    public float Advance(float unscaledDt)
    {
        if (unscaledDt > 0f && unscaledDt <= MaxDuration)
            _elapsed += unscaledDt;
        return Alpha;
    }
}

public sealed class PoseTrackMap
{
    private struct Track
    {
        public PoseSample From;
        public PoseSample To;
    }

    private readonly Dictionary<long, Track> _tracks = new Dictionary<long, Track>();

    public void Clear() => _tracks.Clear();

    public void Remove(long id) => _tracks.Remove(id);

    public void ShiftAll(Func<PoseSample, PoseSample> transform)
    {
        if (_tracks.Count == 0) return;
        var keys = new List<long>(_tracks.Keys);
        foreach (var key in keys)
        {
            var t = _tracks[key];
            _tracks[key] = new Track { From = transform(t.From), To = transform(t.To) };
        }
    }

    public bool UpdateTarget(long id, PoseSample currentVisual, PoseSample target, float snapDistanceSq, bool forceSnap = false)
    {
        if (forceSnap || !_tracks.ContainsKey(id) || currentVisual.DistanceSq(target) > snapDistanceSq)
        {
            _tracks[id] = new Track { From = target, To = target };
            return true;
        }
        _tracks[id] = new Track { From = currentVisual, To = target };
        return false;
    }

    public bool Sample(long id, float alpha, out PoseSample pose)
    {
        if (_tracks.TryGetValue(id, out var track))
        {
            pose = PoseSample.Lerp(track.From, track.To, alpha);
            return true;
        }
        pose = default;
        return false;
    }
}
