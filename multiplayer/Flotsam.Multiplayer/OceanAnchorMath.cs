using System;

namespace Flotsam.Multiplayer;

/// <summary>
/// Pure rigid-body 2D/3D XZ+Yaw reference-frame math decoupling the Client's stationary ocean
/// anchor from the Host's floating-origin treadmill.
/// Keeps floating ocean resources &amp; islands 100% stationary in Client scene space while ships
/// physically sail across the water toward them, and round-trips Client cursor commands back
/// into Host scene space with sub-millimeter precision.
/// </summary>
public sealed class OceanAnchorMath
{
    public const float DefaultRebaseThresholdMeters = 750f;

    public bool Initialized { get; private set; }
    public float AnchorX { get; private set; }
    public float AnchorZ { get; private set; }
    public float AnchorYaw { get; private set; }
    public float HostX { get; private set; }
    public float HostZ { get; private set; }
    public float HostYaw { get; private set; }

    public void Reset()
    {
        Initialized = false;
        AnchorX = 0f;
        AnchorZ = 0f;
        AnchorYaw = 0f;
        HostX = 0f;
        HostZ = 0f;
        HostYaw = 0f;
    }

    public void UpdateHostFrame(float hostX, float hostZ, float hostYaw)
    {
        var normYaw = PoseSample.NormalizeDegrees(hostYaw);
        if (!Initialized)
        {
            AnchorX = hostX;
            AnchorZ = hostZ;
            AnchorYaw = normYaw;
            Initialized = true;
        }
        HostX = hostX;
        HostZ = hostZ;
        HostYaw = normYaw;
    }

    public static void RotateXZ(float x, float z, float yawDegrees, out float rx, out float rz)
    {
        var rad = yawDegrees * (MathF.PI / 180f);
        var sin = MathF.Sin(rad);
        var cos = MathF.Cos(rad);
        // Matches Unity Quaternion.Euler(0, yawDegrees, 0) * new Vector3(x, 0, z)
        rx = x * cos + z * sin;
        rz = -x * sin + z * cos;
    }

    public void HostSceneToOcean(float hx, float hz, float hyaw, out float ox, out float oz, out float oyaw)
    {
        if (!Initialized)
        {
            ox = hx;
            oz = hz;
            oyaw = PoseSample.NormalizeDegrees(hyaw);
            return;
        }
        RotateXZ(hx, hz, HostYaw, out var rx, out var rz);
        ox = HostX + rx;
        oz = HostZ + rz;
        oyaw = PoseSample.NormalizeDegrees(hyaw + HostYaw);
    }

    public void OceanToClientScene(float ox, float oz, float oyaw, out float cx, out float cz, out float cyaw)
    {
        if (!Initialized)
        {
            cx = ox;
            cz = oz;
            cyaw = PoseSample.NormalizeDegrees(oyaw);
            return;
        }
        RotateXZ(ox - AnchorX, oz - AnchorZ, -AnchorYaw, out cx, out cz);
        cyaw = PoseSample.NormalizeDegrees(oyaw - AnchorYaw);
    }

    public void HostToClient(float hx, float hz, float hyaw, out float cx, out float cz, out float cyaw)
    {
        if (!Initialized)
        {
            cx = hx;
            cz = hz;
            cyaw = PoseSample.NormalizeDegrees(hyaw);
            return;
        }
        HostSceneToOcean(hx, hz, hyaw, out var ox, out var oz, out var oyaw);
        OceanToClientScene(ox, oz, oyaw, out cx, out cz, out cyaw);
    }

    public void ClientSceneToOcean(float cx, float cz, float cyaw, out float ox, out float oz, out float oyaw)
    {
        if (!Initialized)
        {
            ox = cx;
            oz = cz;
            oyaw = PoseSample.NormalizeDegrees(cyaw);
            return;
        }
        RotateXZ(cx, cz, AnchorYaw, out var rx, out var rz);
        ox = AnchorX + rx;
        oz = AnchorZ + rz;
        oyaw = PoseSample.NormalizeDegrees(cyaw + AnchorYaw);
    }

    public void OceanToHostScene(float ox, float oz, float oyaw, out float hx, out float hz, out float hyaw)
    {
        if (!Initialized)
        {
            hx = ox;
            hz = oz;
            hyaw = PoseSample.NormalizeDegrees(oyaw);
            return;
        }
        RotateXZ(ox - HostX, oz - HostZ, -HostYaw, out hx, out hz);
        hyaw = PoseSample.NormalizeDegrees(oyaw - HostYaw);
    }

    public void ClientToHost(float cx, float cz, float cyaw, out float hx, out float hz, out float hyaw)
    {
        if (!Initialized)
        {
            hx = cx;
            hz = cz;
            hyaw = PoseSample.NormalizeDegrees(cyaw);
            return;
        }
        ClientSceneToOcean(cx, cz, cyaw, out var ox, out var oz, out var oyaw);
        OceanToHostScene(ox, oz, oyaw, out hx, out hz, out hyaw);
    }

    public bool ShouldRebase(float localShipClientX, float localShipClientZ, float thresholdMeters = DefaultRebaseThresholdMeters)
    {
        if (!Initialized) return false;
        return localShipClientX * localShipClientX + localShipClientZ * localShipClientZ > thresholdMeters * thresholdMeters;
    }

    public void RebaseToClientScenePoint(
        float localShipClientX,
        float localShipClientZ,
        out float oldAnchorX,
        out float oldAnchorZ,
        out float oldAnchorYaw)
    {
        oldAnchorX = AnchorX;
        oldAnchorZ = AnchorZ;
        oldAnchorYaw = AnchorYaw;
        if (!Initialized) return;
        ClientSceneToOcean(localShipClientX, localShipClientZ, 0f, out var newOx, out var newOz, out _);
        AnchorX = newOx;
        AnchorZ = newOz;
        // Preserve AnchorYaw so translational rebasing never twists camera or ocean wave orientation.
    }

    public static PoseSample RebasePose(
        float oldAnchorX,
        float oldAnchorZ,
        float oldAnchorYaw,
        float newAnchorX,
        float newAnchorZ,
        float newAnchorYaw,
        PoseSample sample)
    {
        RotateXZ(sample.X, sample.Z, oldAnchorYaw, out var ox, out var oz);
        var worldX = oldAnchorX + ox;
        var worldZ = oldAnchorZ + oz;
        var worldYaw = sample.Yaw + oldAnchorYaw;
        RotateXZ(worldX - newAnchorX, worldZ - newAnchorZ, -newAnchorYaw, out var nx, out var nz);
        return new PoseSample(nx, sample.Y, nz, worldYaw - newAnchorYaw);
    }
}
