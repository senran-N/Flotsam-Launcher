extern alias Game;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Debris = Game::Flotsam;

namespace Flotsam.Multiplayer;

/// <summary>
/// Freezes autonomous physics, buoyancy, water-height callbacks, and pathfinding navigators on
/// client-side replicas while keeping selection colliders active for hover/click UI interaction.
/// </summary>
internal static class ReplicaFreeze
{
    private static readonly FieldInfo? PhysicsRigidBodyField = AccessTools.Field(typeof(PhysicsController), "_rigidBody");
    private static readonly FieldInfo? PhysicsBuoyancyField = AccessTools.Field(typeof(PhysicsController), "_buoyancy");
    private static readonly FieldInfo? NoCollisionBuoyantField = AccessTools.Field(typeof(NoCollisionBuoyantFlotsam), "_buoyant");
    private static readonly PropertyInfo? NavigatorTerrainProp = AccessTools.Property(typeof(Navigator), nameof(Navigator.Terrain));
    private static readonly System.Collections.Generic.Dictionary<int, float> NextSwimSplashAt =
        new System.Collections.Generic.Dictionary<int, float>();

    internal static void Reset() => NextSwimSplashAt.Clear();

    internal static PoseSample CurrentPose(Transform transform)
    {
        var p = transform.position;
        return new PoseSample(p.x, p.y, p.z, transform.eulerAngles.y);
    }

    internal static void ApplyPose(Transform transform, PoseSample pose)
    {
        transform.SetPositionAndRotation(new Vector3(pose.X, pose.Y, pose.Z), Quaternion.Euler(0f, pose.Yaw, 0f));
    }

    internal static void ClampPhysicsController(PhysicsController? controller)
    {
        if (controller == null) return;
        if (PhysicsBuoyancyField?.GetValue(controller) is Buoyancy buoyancy && buoyancy != null)
        {
            buoyancy._buoyancyEnabled = false;
            if (buoyancy.enabled) buoyancy.enabled = false;
        }
        if (PhysicsRigidBodyField?.GetValue(controller) is Rigidbody body && body != null)
        {
            body.useGravity = false;
            body.isKinematic = true;
        }
    }

    internal static void FreezePhysics(Component? root)
    {
        if (root == null) return;
        foreach (var buoyancy in root.GetComponentsInChildren<Buoyancy>(true))
        {
            if (buoyancy == null) continue;
            buoyancy._buoyancyEnabled = false;
            if (buoyancy.enabled) buoyancy.enabled = false;
        }
        foreach (var controller in root.GetComponentsInChildren<PhysicsController>(true))
            ClampPhysicsController(controller);
        foreach (var body in root.GetComponentsInChildren<Rigidbody>(true))
        {
            if (body == null) continue;
            body.useGravity = false;
            body.isKinematic = true;
        }
    }

    internal static void FreezeNavigators(Component? root)
    {
        if (root == null) return;
        foreach (var navigator in root.GetComponentsInChildren<Navigator>(true))
            if (navigator != null && navigator.enabled) navigator.enabled = false;
        foreach (var pathfinder in root.GetComponentsInChildren<PathfinderNavigator>(true))
            if (pathfinder != null && pathfinder.enabled) pathfinder.enabled = false;
        foreach (var navMesh in root.GetComponentsInChildren<UnityNavMeshNavigator>(true))
            if (navMesh != null && navMesh.enabled) navMesh.enabled = false;
    }

    internal static void FreezeBuilding(Buildable? building)
    {
        if (building == null) return;
        FreezeNavigators(building);
        FreezePhysics(building);
    }

    internal static void FreezeAgent(Agent? agent)
    {
        if (agent == null) return;
        agent.StopAllCoroutines();
        FreezeNavigators(agent);
        FreezePhysics(agent);
        if (agent.DrifterRig?.MeshAnimator?.Animator != null)
            agent.DrifterRig.MeshAnimator.Animator.applyRootMotion = false;
    }

    internal static void SyncAgentVisualState(Agent agent, Activity activity, int terrainValue, Boat? boat)
    {
        var navigator = agent.ReturnNavigator(true);
        var stateChanged = false;
        if (agent.Boat != boat)
        {
            agent.Boat = boat;
            stateChanged = true;
        }
        if (navigator != null && System.Enum.IsDefined(typeof(Navigator.TerrainType), terrainValue))
        {
            var desiredTerrain = (Navigator.TerrainType)terrainValue;
            if (navigator.Terrain != desiredTerrain)
            {
                NavigatorTerrainProp?.SetValue(navigator, desiredTerrain);
                agent.IsInWater = desiredTerrain == Navigator.TerrainType.WaterSurface || desiredTerrain == Navigator.TerrainType.Underwater;
                stateChanged = true;
            }
        }
        if (agent.CurrentActivity != activity)
        {
            agent.UpdateActivity(activity);
            stateChanged = true;
        }
        if (stateChanged)
            agent.DrifterRig?.MeshAnimator?.UpdateAnimator();
    }

    internal static void DriveClientAgentMotion(Agent agent, float planarDistance, float dt)
    {
        if (agent == null || dt <= 0.0001f) return;
        var speed = Mathf.Clamp(planarDistance / dt, 0f, 12f);
        var anim = agent.DrifterRig?.MeshAnimator?.Animator;
        if (anim != null)
        {
            var isLocomoting = agent.IsInWater ||
                               agent.CurrentActivity == Activity.Moving ||
                               agent.CurrentActivity == Activity.MovingWithItem;
            anim.speed = isLocomoting && speed > 0.15f
                ? Mathf.Clamp(0.85f + speed * 0.14f, 0.85f, 1.65f)
                : 1f;
        }

        if (agent.IsInWater && speed > 0.35f)
        {
            var now = Time.unscaledTime;
            var key = agent.GetInstanceID();
            if (!NextSwimSplashAt.TryGetValue(key, out var nextAt) || now >= nextAt)
            {
                NextSwimSplashAt[key] = now + 0.48f;
                FleetPhysicalActors.SpawnNativeSplash(agent.transform.position);
            }
        }
    }

    internal static void ApplyBuildingWaveBob(Buildable? building, float time)
    {
        if (building == null || MoveConstructionCommands.IsStored(building)) return;
        if (building.SpawnedVisual == null) return;
        if (building.TryGetComponent<WalkwaySegment>(out _) ||
            building.TryGetComponent<Boat>(out _) ||
            building.TryGetComponent<TownheartStorage>(out _))
        {
            return;
        }

        var vis = building.SpawnedVisual.transform;
        var pos = building.transform.position;
        var phase = pos.x * 0.19f + pos.z * 0.15f;
        var bobY = Mathf.Sin(time * 2.1f + phase) * 0.055f + Mathf.Cos(time * 1.35f - phase * 0.7f) * 0.025f;
        var pitch = Mathf.Sin(time * 1.85f + phase) * 0.85f;
        var roll = Mathf.Cos(time * 1.55f + phase * 0.8f) * 0.95f;
        vis.localPosition = new Vector3(0f, bobY, 0f);
        vis.localRotation = Quaternion.Euler(pitch, 0f, roll);
    }

    internal static void ApplyFlotsamWaveBob(Debris? flotsam, float time)
    {
        if (flotsam == null || flotsam.Pooled || flotsam.VisualPrefab == null) return;
        var vis = flotsam.VisualPrefab.transform;
        var pos = flotsam.transform.position;
        var phase = pos.x * 0.23f + pos.z * 0.21f;
        var bobY = Mathf.Sin(time * 2.4f + phase) * 0.085f;
        var tiltX = Mathf.Sin(time * 2.0f + phase) * 2.4f;
        var tiltZ = Mathf.Cos(time * 1.7f + phase * 0.9f) * 2.6f;
        vis.localPosition = new Vector3(0f, bobY, 0f);
        vis.localRotation = Quaternion.Euler(tiltX, 0f, tiltZ);
    }

    internal static void SyncProducerVisualActivity(Buildable? building, ProducerState? state)
    {
        if (building == null || building.SpawnedVisual == null) return;
        var working = building.IsActive &&
                      building.BuildPhase == BuildPhase.Finished &&
                      state?.queue != null &&
                      state.queue.Length > 0;

        foreach (var ps in building.SpawnedVisual.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (ps == null) continue;
            var em = ps.emission;
            if (em.enabled != working)
                em.enabled = working;
            if (working && !ps.isPlaying)
                ps.Play();
        }
    }

    internal static void FreezeFlotsam(Debris? flotsam)
    {
        if (flotsam == null) return;
        flotsam.StopAllCoroutines();
        if (flotsam is NoCollisionBuoyantFlotsam buoyantFlotsam)
            NoCollisionBuoyantField?.SetValue(buoyantFlotsam, false);
        FreezePhysics(flotsam);
    }

    internal static void FreezeMarker(Marker? marker)
    {
        if (marker == null) return;
        marker.StopAllCoroutines();
        FreezePhysics(marker);
    }

    internal static void FreezeBird(Bird? bird)
    {
        if (bird == null) return;
        bird.StopAllCoroutines();
        if (bird.TryGetComponent<BirdMovement>(out var movement) && movement.enabled)
            movement.enabled = false;
        FreezePhysics(bird);
    }
}
