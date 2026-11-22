extern alias Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PajamaLlama.Persistence;
using UnityEngine;
using Debris = Game::Flotsam;

namespace Flotsam.Multiplayer;

/// <summary>
/// Unlocked multi-ship runtime. Manages independent player ship communities, construction/graph
/// partitioning, resident/producer owner routing, and native save/restore without artificial fences.
/// </summary>
public static class FleetRuntime
{
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly FieldInfo? ResMgrCommunityField = AccessTools.Field(typeof(ResourceManager), "_community");
    private static readonly FieldInfo? ResMgrInventoryField = AccessTools.Field(typeof(ResourceManager), "_communityInventory");
    private static readonly FieldInfo? BirdPerchSpotField = AccessTools.Field(typeof(Bird), "_perchSpot");
    private static readonly FieldInfo? BirdHouseItemGroupsField = AccessTools.Field(typeof(BirdHouse), "_itemGroups");
    private static readonly MethodInfo? BirdResetMethod = AccessTools.DeclaredMethod(typeof(Bird), "Reset", Type.EmptyTypes);
    private static readonly FieldInfo? BoatCursorBoatField = AccessTools.Field(typeof(BoatMovementCursorProperties), "_boat");
    private static readonly FieldInfo? VitalsMapField = AccessTools.Field(typeof(Vitals), "_vitals");
    private static readonly MethodInfo? VitalInstantiateProjectMethod = AccessTools.DeclaredMethod(
        typeof(Vital),
        "InstantiateProject",
        new[] { typeof(ProjectProperties), typeof(GameObject), typeof(List<Item>) });
    private static readonly Dictionary<Project, Community> ProjectOwners = new Dictionary<Project, Community>();
    private static Producer? _eventProducer;
    private static World? _activeWorld;
    private static bool _installed;
    private static float _nextNeedsTick;

    public static bool Active => FleetEngineBundle.SecondaryCommunities.Any();

    private static MethodInfo Own(string name) => typeof(FleetRuntime).GetMethod(name, Flags) ?? throw new MissingMethodException(name);
    private static MethodInfo Native(Type t, string name, params Type[] args) => AccessTools.DeclaredMethod(t, name, args) ?? throw new MissingMethodException(t.FullName, name);

    public static void Install(Harmony harmony)
    {
        if (_installed) return;
        FleetEngineBundle.Install(harmony);
        FleetAnchorRebase.Install(harmony);
        FleetUnionTiles.Install(harmony);

        // Construction & nav-graph partitioning between independent ships
        harmony.Patch(Native(typeof(Construction), "AddNeighbourConstruction", typeof(Construction)), prefix: new HarmonyMethod(Own(nameof(AddNeighbour))));
        harmony.Patch(Native(typeof(Construction), "IsConnectedToTownheart"), prefix: new HarmonyMethod(Own(nameof(Connected))));
        harmony.Patch(Native(typeof(Construction), "IsConnectedToTownheart", typeof(Construction)), prefix: new HarmonyMethod(Own(nameof(ConnectedIgnoring))));
        harmony.Patch(Native(typeof(HierarchicalNode), "CanBeNavMeshNeighbor", typeof(HierarchicalNode), typeof(Vector3)), prefix: new HarmonyMethod(Own(nameof(GraphNeighbor))));
        harmony.Patch(Native(typeof(HierarchicalNode), "AddGridNeighbors"), prefix: new HarmonyMethod(Own(nameof(GridNeighbors))));
        harmony.Patch(Native(typeof(HierarchicalNodeMarker), "SetParent", typeof(HierarchicalNodeMarker)), prefix: new HarmonyMethod(Own(nameof(MarkerParent))));
        harmony.Patch(Native(typeof(GameManager), "LateUpdate"), postfix: new HarmonyMethod(Own(nameof(LateTick))));

        // Resident & project ownership routing
        harmony.Patch(Native(typeof(Agent), "LateUpdate"), prefix: new HarmonyMethod(Own(nameof(AgentLate))));
        harmony.Patch(Native(typeof(Agent), "Start"), prefix: new HarmonyMethod(Own(nameof(AgentStart))));
        harmony.Patch(Native(typeof(Agent), "JoinCommunity", typeof(Community)), postfix: new HarmonyMethod(Own(nameof(Joined))));
        harmony.Patch(Native(typeof(Agent), "ApplyAssignmentTemplate"), prefix: new HarmonyMethod(Own(nameof(ApplyTemplate))));
        harmony.Patch(Native(typeof(Agent), "RestoreSpawn", typeof(AgentDescriptor), typeof(Community), typeof(AgentPersistentData)), postfix: new HarmonyMethod(Own(nameof(RestoreAgentAssignments))));
        harmony.Patch(Native(typeof(Agent), "TryGoToTown", typeof(Project)), transpiler: new HarmonyMethod(Own(nameof(OwnerHeartIL))));
        harmony.Patch(Native(typeof(Agent), "ReturnClosestConstruction", typeof(bool)), prefix: new HarmonyMethod(Own(nameof(ClosestConstruction))));
        harmony.Patch(Native(typeof(Agent), "ReturnClosestWalkwayConstruction"), prefix: new HarmonyMethod(Own(nameof(ClosestWalkway))));
        harmony.Patch(Native(typeof(Agent), "ResetToTown", typeof(bool)), prefix: new HarmonyMethod(Own(nameof(ResetOwnTown))));
        harmony.Patch(Native(typeof(AgentManager), "OnDayStart", typeof(GameEvent)), postfix: new HarmonyMethod(Own(nameof(DayStart))));
        harmony.Patch(Native(typeof(Vital), "InstantiateProject", typeof(ProjectProperties), typeof(GameObject), typeof(List<Item>)), postfix: new HarmonyMethod(Own(nameof(VitalProject))));
        harmony.Patch(Native(typeof(Community), "QueueProject", typeof(Project)), prefix: new HarmonyMethod(Own(nameof(QueueProject))));
        harmony.Patch(Native(typeof(Project), "TryAssignAgent", typeof(ProjectAssignment).MakeByRefType(), typeof(Agent), typeof(bool)), prefix: new HarmonyMethod(Own(nameof(BeforeAssign))));
        harmony.Patch(Native(typeof(Project), "get_IsCommunityProject"), prefix: new HarmonyMethod(Own(nameof(IsCommunityProject))));
        harmony.Patch(Native(typeof(Project), "ReturnAssignmentPriority", typeof(Agent)), transpiler: new HarmonyMethod(Own(nameof(AgentOwnerIL))));
        harmony.Patch(Native(typeof(Project), "ReturnPriority", typeof(Agent)), transpiler: new HarmonyMethod(Own(nameof(AgentOwnerIL))));
        harmony.Patch(Native(typeof(Project), "ReturnBlocker"), transpiler: new HarmonyMethod(Own(nameof(ProjectOwnerIL))));
        harmony.Patch(Native(typeof(ReserveResourceProviderItemsToHaul), "ReturnBlockers", typeof(Project)), transpiler: new HarmonyMethod(Own(nameof(TaskOwnerIL))));
        harmony.Patch(Native(typeof(VariableHaulGeneralItems), "ReturnBlockers", typeof(Project)), transpiler: new HarmonyMethod(Own(nameof(TaskOwnerIL))));
        harmony.Patch(Native(typeof(Inventory), "OnItemAdded", typeof(Item), typeof(SubInventory)), transpiler: new HarmonyMethod(Own(nameof(InventoryOwnerIL))));
        harmony.Patch(Native(typeof(Inventory), "SelectUntilFull", typeof(List<Item>), typeof(bool), typeof(bool)), transpiler: new HarmonyMethod(Own(nameof(InventoryOwnerIL))));
        harmony.Patch(Native(typeof(ExpertiseManager), "IncreaseExperience", typeof(Agent), typeof(float), typeof(bool)), prefix: new HarmonyMethod(Own(nameof(Experience))));

        // Construction & producer ownership routing
        harmony.Patch(Native(typeof(Buildable), "AssignProject", typeof(ProjectProperties), typeof(List<Item>)), transpiler: new HarmonyMethod(Own(nameof(BuildableOwnerIL))));
        harmony.Patch(Native(typeof(ResourceManager), "ReserveClosestItems", typeof(IPathfindingNodeProvider), typeof(IReadOnlyList<CountedItemProperty>)), prefix: new HarmonyMethod(Own(nameof(ReserveList))));
        harmony.Patch(Native(typeof(ResourceManager), "ReserveClosestItems", typeof(IPathfindingNodeProvider), typeof(CountedItemProperty[])), prefix: new HarmonyMethod(Own(nameof(ReserveArray))));
        harmony.Patch(Native(typeof(Producer), "TryReserveRequiredItems", typeof(List<Item>).MakeByRefType(), typeof(Producer.Recipe), typeof(bool)), prefix: new HarmonyMethod(Own(nameof(ReserveRecipe))));
        harmony.Patch(Native(typeof(Producer), "AddNewFoundItems"), prefix: new HarmonyMethod(Own(nameof(FoundItems))));
        harmony.Patch(Native(typeof(ResourceManager), "AddProductionLimits", typeof(IItemProducer)), prefix: new HarmonyMethod(Own(nameof(ProductionLimits))));
        harmony.Patch(Native(typeof(BuildableEvent), "Dispatch", typeof(GameEventType), typeof(Buildable)), prefix: new HarmonyMethod(Own(nameof(BuildableEventFilter))));
        harmony.Patch(Native(typeof(Producer), "AddQueuedRecipe", typeof(QueuedRecipe)), prefix: new HarmonyMethod(Own(nameof(EventScope))), finalizer: new HarmonyMethod(Own(nameof(EndEventScope))));
        harmony.Patch(Native(typeof(Producer), "FinishQueuedRecipe", typeof(QueuedRecipe)), prefix: new HarmonyMethod(Own(nameof(EventScope))), finalizer: new HarmonyMethod(Own(nameof(EndEventScope))));
        harmony.Patch(Native(typeof(ItemEvent), "Dispatch", typeof(GameEventType), typeof(Item)), prefix: new HarmonyMethod(Own(nameof(ItemEventFilter))));
        harmony.Patch(Native(typeof(ItemEvent), "Dispatch", typeof(GameEventType), typeof(ItemProperties)), prefix: new HarmonyMethod(Own(nameof(ItemEventFilter))));
        harmony.Patch(Native(typeof(ItemEvent), "Dispatch", typeof(GameEventType), typeof(CountedItemProperty)), prefix: new HarmonyMethod(Own(nameof(ItemEventFilter))));
        harmony.Patch(Native(typeof(ItemEvent), "DispatchItemsProduced", typeof(IEnumerable<Item>)), prefix: new HarmonyMethod(Own(nameof(ItemEventFilter))));

        // Stranded Drifter & Bird rescue ownership routing so rescues join the rescuing ship
        var agentRescue = AccessTools.DeclaredMethod(typeof(Agent), nameof(Agent.Rescue), new[] { typeof(Project), typeof(Boat) });
        if (agentRescue != null)
            harmony.Patch(agentRescue, prefix: new HarmonyMethod(Own(nameof(BeforeActorRescue))), finalizer: new HarmonyMethod(Own(nameof(AfterActorRescue))));
        var birdRescue = AccessTools.DeclaredMethod(typeof(Bird), nameof(Bird.Rescue), new[] { typeof(Project), typeof(Boat) });
        if (birdRescue != null)
            harmony.Patch(birdRescue, prefix: new HarmonyMethod(Own(nameof(BeforeActorRescue))), finalizer: new HarmonyMethod(Own(nameof(AfterActorRescue))));

        // Independent-mode Seagull, BirdHouse, FishFarm, WaterDistributer, MedPod & BoatMovement routing
        var actorIsPlayer = AccessTools.DeclaredMethod(typeof(ActorBehaviour), nameof(ActorBehaviour.IsInPlayerCommunity), Type.EmptyTypes);
        if (actorIsPlayer != null)
            harmony.Patch(actorIsPlayer, postfix: new HarmonyMethod(Own(nameof(AfterBirdIsInPlayerCommunity))));
        var birdWorldStopped = AccessTools.DeclaredMethod(typeof(Bird), nameof(Bird.WorldStoppedMoving), Type.EmptyTypes);
        if (birdWorldStopped != null)
            harmony.Patch(birdWorldStopped, prefix: new HarmonyMethod(Own(nameof(BeforeBirdWorldStoppedMoving))));
        var birdHouseQueueFood = AccessTools.DeclaredMethod(typeof(BirdHouse), "TryToQueueFood", Type.EmptyTypes);
        if (birdHouseQueueFood != null)
            harmony.Patch(birdHouseQueueFood, prefix: new HarmonyMethod(Own(nameof(BeforeBirdHouseScope))), finalizer: new HarmonyMethod(Own(nameof(AfterPlayerScope))));
        var birdHousePopulate = AccessTools.DeclaredMethod(typeof(BirdHouse), "PopulateItemLists", new[] { typeof(Debris) });
        if (birdHousePopulate != null)
            harmony.Patch(birdHousePopulate, prefix: new HarmonyMethod(Own(nameof(BeforeBirdHousePopulateItems))));
        var birdHouseAvailable = AccessTools.DeclaredMethod(typeof(BirdHouse), nameof(BirdHouse.SalvageableItemAvailable), new[] { typeof(Item).MakeByRefType() });
        if (birdHouseAvailable != null)
            harmony.Patch(birdHouseAvailable, prefix: new HarmonyMethod(Own(nameof(BeforeBirdHouseSalvageableAvailable))));
        var distLateUpdate = AccessTools.DeclaredMethod(typeof(ItemToDistribute), nameof(ItemToDistribute.LateUpdate), new[] { typeof(bool) });
        if (distLateUpdate != null)
            harmony.Patch(distLateUpdate, prefix: new HarmonyMethod(Own(nameof(BeforeDistributerScope))), finalizer: new HarmonyMethod(Own(nameof(AfterPlayerScope))));
        var aquaAddBrood = AccessTools.DeclaredMethod(typeof(AquaFarm), nameof(AquaFarm.AddBroodFish), Type.EmptyTypes);
        if (aquaAddBrood != null)
            harmony.Patch(aquaAddBrood, prefix: new HarmonyMethod(Own(nameof(BeforeAquaFarmScope))), finalizer: new HarmonyMethod(Own(nameof(AfterPlayerScope))));
        foreach (var medName in new[] { "Update", "LateUpdate" })
        {
            var medMethod = typeof(MedPod).GetMethod(medName,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null);
            if (medMethod != null)
                harmony.Patch(medMethod, prefix: new HarmonyMethod(Own(nameof(BeforeMedPodScope))), finalizer: new HarmonyMethod(Own(nameof(AfterPlayerScope))));
        }
        foreach (var boatMoveMethod in new[]
        {
            AccessTools.DeclaredMethod(typeof(BoatMovementCursorProperties), nameof(BoatMovementCursorProperties.UpdateCursor), new[] { typeof(CursorManager) }),
            AccessTools.DeclaredMethod(typeof(BoatMovementCursorProperties), "UpdateClosestMooringPoint")
        })
        {
            if (boatMoveMethod != null)
                harmony.Patch(boatMoveMethod, prefix: new HarmonyMethod(Own(nameof(BeforeBoatMoveCursorScope))), finalizer: new HarmonyMethod(Own(nameof(AfterPlayerScope))));
        }

        // Native Save & Restore integration for secondary player communities
        harmony.Patch(typeof(Community).GetConstructor(new[] { typeof(string), typeof(Community.Type) }), postfix: new HarmonyMethod(Own(nameof(RestoreFleetCommunity))));
        var communityCtor = typeof(CommunityPersistentData).GetConstructor(new[] { typeof(Community) });
        if (communityCtor != null) harmony.Patch(communityCtor, postfix: new HarmonyMethod(Own(nameof(SerializeSecondaryAgents))));
        harmony.Patch(AccessTools.Method(typeof(Community), nameof(Community.AddStorage)), prefix: new HarmonyMethod(Own(nameof(EnsureCommunityInventory))));
        harmony.Patch(AccessTools.Method(typeof(Community), nameof(Community.AddAgent)), postfix: new HarmonyMethod(Own(nameof(AfterAddAgent))));
        harmony.Patch(AccessTools.Method(typeof(CommunityPersistentData), nameof(CommunityPersistentData.Restore)), postfix: new HarmonyMethod(Own(nameof(RestoreSecondaryCommunity))));
        harmony.Patch(AccessTools.Method(typeof(AgentPersistentData), nameof(AgentPersistentData.RestoreNavigatorState)), postfix: new HarmonyMethod(Own(nameof(RestoreSecondaryNavigator))));

        _installed = true;
    }

    /// <summary>Ensures a secondary player ship exists for the given peer ID when playing in independent mode.</summary>
    public static Community EnsurePlayerShip(string peerId, string playerName)
    {
        var world = GameManager.WorldManager?.World ?? throw new InvalidOperationException("World not ready.");
        if (!ReferenceEquals(_activeWorld, world))
        {
            _activeWorld = world;
            ProjectOwners.Clear();
            FleetEngineBundle.Reset();
            FleetEngineBundle.EnsurePrimary();
            RebindRestoredShips();
        }
        var existing = FleetEngineBundle.FindShipById(peerId);
        if (existing != null) return existing;

        var cleanPlayer = string.IsNullOrWhiteSpace(playerName) ? "同行者" : playerName.Replace(":", "").Trim();
        // Stable key identity is kept in Ship:<owner-id>:<display-name>. A nickname never grants ownership.

        FleetEngineBundle.EnsurePrimary();
        var primaryRoot = Construction.Townheart;
        var heartPrefab = primaryRoot.Buildable.Properties.Prefab;
        var index = FleetEngineBundle.SecondaryCommunities.Count() + 1;
        var angle = (index - 1) * 45f * Mathf.Deg2Rad;
        var radius = 65f + (index / 8) * 35f;
        var spawnPos = primaryRoot.transform.position + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

        var shipName = $"Ship:{peerId}:{cleanPlayer}";
        var community = new Community(shipName, Community.Type.Regular)
        {
            Inventory = new CommunityInventory(),
            Research = new CommunityResearch(null!)
        };
        typeof(CommunityResearch).GetField("_community", Flags)?.SetValue(community.Research, community);

        // Unlock only genuine 0-cost starter tech nodes & base starter buildables instead of copying Host's entire tech tree
        using (WithPlayerCommunity(community))
        {
            foreach (var node in Resources.FindObjectsOfTypeAll<TechTreeNode>())
            {
                if (node != null && node.Cost <= 0 && !node.IsUnlocked())
                    node.Unlock();
            }
        }
        if (GameSettings.Instance?.BuildableSettings?.Buildables != null)
        {
            var starterPatterns = new[] { "Walkway", "Storage", "Drying", "Chopping", "Desalinator", "Bed" };
            foreach (var bp in GameSettings.Instance.BuildableSettings.Buildables)
            {
                if (bp == null || community.Research.ResearchedBuildableProperties.Contains(bp)) continue;
                if (starterPatterns.Any(k => bp.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    community.Research.UnlockBuildable(bp);
            }
        }

        var heart = UnityEngine.Object.Instantiate(heartPrefab, spawnPos, primaryRoot.transform.rotation);
        var engine = heart.GetComponent<Engine>();
        var construction = heart.GetComponent<Construction>();
        FleetEngineBundle.RegisterShip(community, peerId, engine, construction);
        FleetOwnership.Bind(community,peerId);

        heart.Initialize(community, 0, null, false);
        heart.FinishBuilding(restored: true);
        heart.Activate();
        engine.EnergyGrid?.FillStorageEnergy(1000f);

        // Spawn 3 starting drifters, balanced survival supplies, and a starter ocean debris ring around the new ship
        for (var d = 0; d < 3; d++)
        {
            var drifter = AgentDescriptor.CreateInstance().Spawn<Agent>(community, construction.Target.PrimaryMarker.transform.position);
            drifter?.ReturnNavigator()?.AttachToTarget(construction.Target);
        }

        var items = Resources.FindObjectsOfTypeAll<ItemProperties>();
        var food = items.FirstOrDefault(p => (p.Tags & Item.Tags.Food) != 0);
        var drink = items.FirstOrDefault(p => (p.Tags & Item.Tags.Drink) != 0);
        var wood = items.FirstOrDefault(p => p.name == "DryWood") ?? items.FirstOrDefault(p => p.name == "WetWood");
        var plastic = items.FirstOrDefault(p => p.name == "Plastic");
        for (var i = 0; i < 12; i++)
        {
            if (wood != null) community.SpawnItemToAvailableStorage(wood);
            if (plastic != null) community.SpawnItemToAvailableStorage(plastic);
            if (i < 9 && food != null) community.SpawnItemToAvailableStorage(food);
            if (i < 9 && drink != null) community.SpawnItemToAvailableStorage(drink);
        }
        SeaLootAndLandmarks.SpawnStarterDebrisRing(spawnPos);
        Plugin.Log($"Spawned independent ship '{cleanPlayer}' ({peerId}) at {spawnPos.x:0.0},{spawnPos.z:0.0}");
        return community;
    }

    public static string ExtractShipOwnerName(string? communityName)
    {
        if (string.IsNullOrEmpty(communityName)) return "";
        if (!communityName.StartsWith("Ship:", StringComparison.Ordinal)) return communityName;
        var parts = communityName.Split(':');
        return parts.Length >= 3 ? string.Join(":", parts.Skip(2)) : communityName;
    }

    /// <summary>
    /// A guest drives its own ship. Once the replicated world is live, promote the
    /// community the host assigned to this peer into the local player community and
    /// demote the host's community to a regular (fleet) community.
    /// </summary>
    public static bool AdoptLocalShip(string communityName)
    {
        if (string.IsNullOrEmpty(communityName) || !GameSession.Ready()) return false;
        var world = GameManager.WorldManager?.World;
        if (world != null && !ReferenceEquals(_activeWorld, world))
        {
            _activeWorld = world;
            ProjectOwners.Clear();
            FleetEngineBundle.Reset();
            FleetEngineBundle.EnsurePrimary();
            RebindRestoredShips();
        }
        var ship = Replication.ResolveCommunity(communityName);
        if (ship == null) return false;
        var current = Community.PlayerCommunity;
        if (ReferenceEquals(current, ship)) return true;
        var heart = ship.Buildables.FirstOrDefault(b => b != null && b.GetComponent<Engine>() != null && b.GetComponent<Construction>() != null);
        if (heart == null)
        {
            Plugin.Log("AdoptLocalShip: assigned ship has no native heart: " + communityName);
            return false;
        }
        SetCommunityType(current, Community.Type.Regular);
        SetCommunityType(ship, Community.Type.Player);
        SetPlayerCommunity(ship);
        var localEngine = heart.GetComponent<Engine>();
        Construction.Townheart = heart.GetComponent<Construction>();
        FleetEngineBundle.AdoptLocal(current, ship, "host");
        RebindLocalCommunityUiAndMap(ship, localEngine);
        ProjectOwners.Clear();
        FleetOwnedNavigation.Clear();
        try { CameraController.Instance?.CenterOnTownheart(); }
        catch (Exception e) { Plugin.Log("AdoptLocalShip camera: " + e.GetType().Name); }
        Plugin.Log($"Adopted local ship '{communityName}'");
        return true;
    }

    private static void RebindLocalCommunityUiAndMap(Community ship, Engine? localEngine)
    {
        ResMgrCommunityField?.SetValue(null, ship);
        if (ship.Inventory != null)
            ResMgrInventoryField?.SetValue(null, ship.Inventory);

        var map = GameManager.WorldMapManager?.WorldMap;
        if (map != null)
        {
            AccessTools.Field(typeof(WorldMap), "_playerCommunity")?.SetValue(map, ship);
            if (localEngine != null)
                AccessTools.Field(typeof(WorldMap), "_engine")?.SetValue(map, localEngine);
        }

        foreach (var invPanel in Resources.FindObjectsOfTypeAll<CommunityInventoryPanel>())
        {
            if (invPanel == null || !invPanel.gameObject.scene.IsValid()) continue;
            AccessTools.Field(typeof(CommunityInventoryPanel), "_inventory")?.SetValue(invPanel, null);
            if (invPanel.gameObject.activeSelf)
            {
                invPanel.gameObject.SetActive(false);
                invPanel.gameObject.SetActive(true);
            }
        }
    }

    private static void SetCommunityType(Community? community, Community.Type type)
    {
        if (community == null) return;
        typeof(Community).GetField("_type", Flags)?.SetValue(community, type);
    }

    private static void SetPlayerCommunity(Community community)
    {
        typeof(Community).GetProperty("PlayerCommunity", BindingFlags.Public | BindingFlags.Static)?
            .GetSetMethod(true)?.Invoke(null, new object[] { community });
    }

    internal readonly struct PlayerCommunityScope : IDisposable
    {
        private readonly Community? _previous;
        private readonly Construction? _previousHeart;
        private readonly Community? _previousResCommunity;
        private readonly CommunityInventory? _previousResInventory;
        private readonly bool _changed;
        private readonly bool _heartChanged;
        private readonly bool _resChanged;
        public PlayerCommunityScope(Community? target)
        {
            _previous = Community.PlayerCommunity;
            _previousHeart = Construction.Townheart;
            _previousResCommunity = ResMgrCommunityField?.GetValue(null) as Community;
            _previousResInventory = ResMgrInventoryField?.GetValue(null) as CommunityInventory;
            if (target != null && _previous != null && !ReferenceEquals(target, _previous))
            {
                SetPlayerCommunity(target);
                _changed = true;
            }
            else _changed = false;

            if (target?.Inventory != null && !ReferenceEquals(target.Inventory, _previousResInventory))
            {
                ResMgrCommunityField?.SetValue(null, target);
                ResMgrInventoryField?.SetValue(null, target.Inventory);
                _resChanged = true;
            }
            else _resChanged = false;

            var targetRoot = target != null ? FleetEngineBundle.RootFor(target) : null;
            if (targetRoot != null && !ReferenceEquals(targetRoot, _previousHeart))
            {
                Construction.Townheart = targetRoot;
                _heartChanged = true;
            }
            else _heartChanged = false;
        }
        public void Dispose()
        {
            if (_heartChanged && _previousHeart != null) Construction.Townheart = _previousHeart;
            if (_resChanged)
            {
                if (_previousResCommunity != null) ResMgrCommunityField?.SetValue(null, _previousResCommunity);
                if (_previousResInventory != null) ResMgrInventoryField?.SetValue(null, _previousResInventory);
            }
            if (_changed && _previous != null) SetPlayerCommunity(_previous);
        }
    }

    internal static PlayerCommunityScope WithPlayerCommunity(Community? target) => new PlayerCommunityScope(target);

    private static void RebindRestoredShips()    {
        foreach (var c in Community.Communities)
        {
            if (c == null || c.IsPlayerCommunity() || c.CommunityType != Community.Type.Regular || c.Research == null) continue;
            var heart = c.Buildables.FirstOrDefault(b => b != null && b.TryGetComponent<TownheartStorage>(out _));
            if (heart == null) continue;
            var engine = heart.GetComponent<Engine>();
            var root = heart.GetComponent<Construction>();
            if (engine == null || root == null) continue;
            var id = c.Name != null && c.Name.StartsWith("Ship:", StringComparison.Ordinal)
                ? c.Name.Split(':')[1]
                : "ship-" + Community.Communities.IndexOf(c);
            FleetEngineBundle.RegisterShip(c, id, engine, root);
        }
    }

    private static bool IsOwned(Community? c) => FleetEngineBundle.IsSecondary(c);
    private static bool IsOwned(Agent? a) => a != null && IsOwned(a.Community);
    private static bool IsOwned(Buildable? b) => b != null && IsOwned(b.Community);
    private static bool IsOwned(Producer? p) => p != null && IsOwned(p.Buildable);

    private static void LateTick()
    {
        if (!GameSession.Ready()) return;
        var world = GameManager.WorldManager?.World;
        if (world != null && !ReferenceEquals(_activeWorld, world))
        {
            _activeWorld = world;
            ProjectOwners.Clear();
            FleetEngineBundle.Reset();
            FleetEngineBundle.EnsurePrimary();
            RebindRestoredShips();
        }
        // Guests are frozen replicas: only the authoritative host advances fleet
        // needs, ownership routing and native save hooks.
        if (Plugin.Options.role != "host") return;
        var runNeeds = Time.realtimeSinceStartup >= _nextNeedsTick;
        if (runNeeds) _nextNeedsTick = Time.realtimeSinceStartup + 2f;

        var secMapPositions = runNeeds ? new List<Vector2>() : null;
        foreach (var community in FleetEngineBundle.SecondaryCommunities.ToArray())
        {
            community.Inventory?.LateUpdate();
            if (!runNeeds) continue;
            var secHeart = FleetEngineBundle.HeartBuildable(community);
            if (secHeart != null)
            {
                var secMapPos = FleetOwnedNavigation.SceneToMap(secHeart.transform.position);
                secMapPositions!.Add(new Vector2(secMapPos.x, secMapPos.z));
            }
            var isNight = GameManager.TimeManager?.CurrentDay?.DayTime == Day.E_DayTime.Night;
            foreach (var agent in community.Agents)
            {
                if (agent == null || !agent.IsAlive) continue;
                foreach (var vital in new[] { VitalType.Hunger, VitalType.Thirst })
                {
                    if (!agent.Vitals.ReturnHasProject(vital) && agent.Vitals.TryReserveItemToConsume(vital, AssignmentPriority.Lowest))
                        agent.Vitals.TryAddConsumeProject(vital, true);
                }

                // Native Rest.OnNighttimeStarted skips secondary communities; schedule sleep on bed or own ship heart
                if (isNight && secHeart != null && !agent.Vitals.ReturnHasProject(VitalType.Rest) && agent.CurrentActivity != Activity.Sleeping)
                {
                    if (VitalsMapField?.GetValue(agent.Vitals) is System.Collections.IDictionary vitalsMap &&
                        vitalsMap.Contains(VitalType.Rest))
                    {
                        var restVital = vitalsMap[VitalType.Rest];
                        var projSettings = GameManager.Settings?.ProjectSettings;
                        if (restVital != null && projSettings != null)
                        {
                            var rejuvenator = agent.ReservedHouse != null && agent.ReservedHouse.IsEnabled() ? agent.ReservedHouse.Rejuvenator : null;
                            var sleepProps = rejuvenator != null ? projSettings.RejuvenateProperties : projSettings.SleepOnGround;
                            var sleepTarget = rejuvenator != null ? rejuvenator.gameObject : secHeart.gameObject;
                            if (sleepProps != null && sleepTarget != null)
                                VitalInstantiateProjectMethod?.Invoke(restVital, new object?[] { sleepProps, sleepTarget, null });
                        }
                    }
                }

                // Route diseased secondary crew to their own ship's MedPod / Clinic
                var disease = agent.Vitals.Pollution?.CurrentDisease;
                if (disease != null)
                {
                    using var _medScope = WithPlayerCommunity(community);
                    AccessTools.Method(disease.GetType(), "TryReserveMedPod")?.Invoke(disease, new object[] { agent });
                }
            }
        }
        if (runNeeds && world != null && secMapPositions != null && secMapPositions.Count > 0)
        {
            var savedMapPos = world.TownheartMapPosition;
            foreach (var pos in secMapPositions)
                world.UpdateTiles(pos);
            world.UpdateTiles(savedMapPos);
            AccessTools.PropertySetter(typeof(World), nameof(World.TownheartMapPosition))?.Invoke(world, new object[] { savedMapPos });
        }
    }

    private static Community? MarkerCommunity(HierarchicalNodeMarker? marker) =>
        marker == null ? null : marker.Construction?.Buildable?.Community ?? marker.GetComponentInParent<Buildable>()?.Community;

    private static bool AddNeighbour(Construction __instance, Construction neighbour, ref bool __result)
    {
        var a = __instance.Buildable?.Community;
        var b = neighbour.Buildable?.Community;
        if ((!IsOwned(a) && !IsOwned(b)) || ReferenceEquals(a, b)) return true;
        __result = false;
        return false;
    }

    private static bool Connected(Construction __instance, ref bool __result) => ConnectedImpl(__instance, null, ref __result);
    private static bool ConnectedIgnoring(Construction __instance, Construction ignoredConstruction, ref bool __result) => ConnectedImpl(__instance, ignoredConstruction, ref __result);
    private static bool ConnectedImpl(Construction start, Construction? ignored, ref bool result)
    {
        if (start.Buildable == null || !IsOwned(start.Buildable.Community)) return true;
        var owner = start.Buildable.Community;
        var root = FleetEngineBundle.RootFor(owner);
        var visited = new HashSet<Construction>();
        var stack = new Stack<Construction>();
        stack.Push(start);
        result = false;
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            if (c == null || ReferenceEquals(c, ignored) || !visited.Add(c) || !ReferenceEquals(c.Buildable?.Community, owner)) continue;
            if (ReferenceEquals(c, root)) { result = true; break; }
            foreach (var n in c.NeighbourConstructions)
                if (n != null && n.Buildable.BuildPhase == BuildPhase.Finished) stack.Push(n);
        }
        return false;
    }

    private static bool GraphNeighbor(HierarchicalNode __instance, HierarchicalNode node, ref bool __result)
    {
        var a = MarkerCommunity(__instance.Marker);
        var b = MarkerCommunity(node.Marker);
        if ((!IsOwned(a) && !IsOwned(b)) || (a != null && ReferenceEquals(a, b))) return true;
        __result = false;
        return false;
    }

    private static bool GridNeighbors(HierarchicalNode __instance) => !IsOwned(MarkerCommunity(__instance.Marker));

    private static bool MarkerParent(HierarchicalNodeMarker __instance, HierarchicalNodeMarker parent)
    {
        var a = MarkerCommunity(__instance);
        var b = MarkerCommunity(parent);
        return (!IsOwned(a) && !IsOwned(b)) || (a != null && ReferenceEquals(a, b));
    }

    internal static Community ResolveProjectOwner(Project project) => ResolveProject(project);

    private static Community ResolveProject(Project project)
    {
        if (ProjectOwners.TryGetValue(project, out var owner)) return owner;
        var b = project.TargetBuildable;
        if (b != null && IsOwned(b.Community)) { ProjectOwners[project] = b.Community; return b.Community; }
        var d = project.Target == null ? null : project.Target.GetComponent<Decoration>();
        if (d?.Parent?.Buildable != null && IsOwned(d.Parent.Buildable.Community))
        {
            ProjectOwners[project] = d.Parent.Buildable.Community;
            return d.Parent.Buildable.Community;
        }
        var a = project.Target == null ? null : project.Target.GetComponent<Agent>();
        if (IsOwned(a)) { ProjectOwners[project] = a!.Community; return a.Community; }
        return Community.PlayerCommunity;
    }

    private static bool QueueProject(Community __instance, Project project)
    {
        if (IsOwned(__instance)) ProjectOwners[project] = __instance;
        return true;
    }

    private static bool BeforeAssign(Project __instance, Agent agent, ref ProjectAssignment? assignment, ref bool __result)
    {
        var projectOwner = ResolveProject(__instance);
        if (IsOwned(agent))
        {
            if (!ReferenceEquals(projectOwner, agent.Community)) { assignment = null; __result = false; return false; }
            ProjectOwners[__instance] = agent.Community;
        }
        else if (IsOwned(projectOwner))
        {
            assignment = null;
            __result = false;
            return false;
        }
        return true;
    }

    private static bool IsCommunityProject(Project __instance, ref bool __result)
    {
        var owner = ResolveProject(__instance);
        if (!IsOwned(owner)) return true;
        __result = owner.Projects.Contains(__instance);
        return false;
    }

    private static bool AgentLate(Agent __instance)
    {
        if(FleetVoyages.IsBusy(__instance)||FleetCombat.IsBusy(__instance)||BuildingDurability.IsRepairWorker(__instance))return false;
        if (!IsOwned(__instance)) return true;
        if (__instance.IsAlive)
        {
            if (!(bool)Native(typeof(Agent), "ValidateNavigator").Invoke(__instance, null)!) return false;
            Native(typeof(Agent), "AssignProject").Invoke(__instance, null);
            if (__instance.Assignment == null) __instance.UpdateActivity(Activity.Idling);
        }
        return false;
    }

    private static bool AgentStart(Agent __instance)
    {
        if (!IsOwned(__instance)) return true;
        var root = FleetEngineBundle.RootFor(__instance.Community);
        if (root != null) __instance.ReturnNavigator().AttachToTarget(root.Target);
        __instance.TryGoToTown();
        return false;
    }

    private static void Joined(Agent __instance, Community community)
    {
        if (!IsOwned(community)) return;
        community.Research?.AddResearchPoints(__instance.Attributes.Level);
        __instance.ApplyAssignmentTemplate();
    }

    private static bool ApplyTemplate(Agent __instance)
    {
        if (!IsOwned(__instance)) return true;
        PopulateAssignmentTemplate(__instance);
        return false;
    }

    private static void PopulateAssignmentTemplate(Agent __instance)
    {
        var templates = GameManager.AgentManager.AssignmentPriorityTemplates;
        var settings = GameManager.Settings.ProjectSettings.AssignmentSettings;
        var order = settings.Count;
        foreach (var setting in settings)
        {
            if (setting.Type == AssignmentType.None) continue;
            var priority = templates.TryGetValue(setting.Type, out var saved) ? saved : AssignmentPriority.Default;
            if (__instance.Assignments.All(a => a.Type != setting.Type))
                __instance.Assignments.Add(new Assignment(setting, priority, order--, __instance, __instance.Properties.VitalProperties.AssignmentTypes.Contains(setting.Type)));
        }
        var bg = __instance.Descriptor?.PastBackground;
        if (bg != null) __instance.SetAssignmentEnabled(bg.Assignment, true, bg.AssignmentPriority);
        AccessTools.Field(typeof(Agent), "_sortAssignmentsByPriority").SetValue(__instance, true);
        __instance.GlobalHaulingPriorities.Update(__instance.Assignments);
    }

    // Native restoration creates residents before the fleet owner table is
    // rebound. Populate their work types before AgentPersistentData applies the
    // saved enabled flags and priorities, rather than losing those settings.
    private static void RestoreAgentAssignments(Agent __instance, Community community)
    {
        if (community != null && community.Name?.StartsWith("Ship:", StringComparison.Ordinal) == true)
            PopulateAssignmentTemplate(__instance);
    }

    private static void RestoreFleetCommunity(Community __instance)
    {
        if (!LoadingScreen.IsLoading || __instance.CommunityType != Community.Type.Regular ||
            __instance.Name?.StartsWith("Ship:", StringComparison.Ordinal) != true) return;
        __instance.Inventory ??= new CommunityInventory();
        __instance.Research ??= new CommunityResearch(__instance);
    }

    private static bool ClosestConstruction(Agent __instance, bool onlyFinished, ref Construction? __result)
    {
        if (!IsOwned(__instance)) return true;
        __result = __instance.Community.Constructions
            .Where(c => !onlyFinished || c.Buildable.BuildPhase == BuildPhase.Finished)
            .OrderBy(c => (c.transform.position - __instance.transform.position).sqrMagnitude)
            .FirstOrDefault() ?? FleetEngineBundle.RootFor(__instance.Community);
        return false;
    }

    private static bool ClosestWalkway(Agent __instance, ref Construction? __result) => ClosestConstruction(__instance, true, ref __result);

    private static void DayStart(GameEvent gameEvent)
    {
        foreach (var c in FleetEngineBundle.SecondaryCommunities)
            foreach (var a in c.Agents)
                if (a != null && a.IsAlive) a.Vitals.OnDayStarted(gameEvent);
    }

    private static void VitalProject(Vital __instance)
    {
        if (IsOwned(__instance.Agent) && __instance.Project != null)
            ProjectOwners[__instance.Project] = __instance.Agent.Community;
    }

    private static bool ResetOwnTown(Agent __instance)
    {
        if (!IsOwned(__instance)) return true;
        var root = FleetEngineBundle.RootFor(__instance.Community);
        if (root != null) __instance.ReturnNavigator().AttachToTarget(root.Target);
        return false;
    }

    private static bool Experience(Agent agent, float experience, bool applyMoraleEffect)
    {
        if (!IsOwned(agent)) return true;
        if (applyMoraleEffect && agent.Morale.TryReturnCurrentCategory(out var cat)) experience *= cat.ExperienceMultiplier;
        agent.Attributes.AddTotalExperience(experience, true);
        return false;
    }

    private static Construction Heart(Agent agent) => IsOwned(agent) ? (FleetEngineBundle.RootFor(agent.Community) ?? Construction.Townheart) : Construction.Townheart;
    private static Community AgentOwner(Agent agent) => IsOwned(agent) ? agent.Community : Community.PlayerCommunity;
    private static Community BuildableOwner(Buildable building) => IsOwned(building) ? building.Community : Community.PlayerCommunity;
    private static Community InventoryOwner(Inventory inventory)
    {
        var b = inventory.GetComponent<Buildable>();
        if (IsOwned(b)) return b!.Community;
        var d = inventory.GetComponent<Decoration>();
        if (d?.Parent?.Buildable != null && IsOwned(d.Parent.Buildable)) return d.Parent.Buildable.Community;
        var a = inventory.GetComponent<Agent>();
        if (IsOwned(a)) return a!.Community;
        return Community.PlayerCommunity;
    }

    private static bool ReserveList(IPathfindingNodeProvider destination, IReadOnlyList<CountedItemProperty> requiredResources, ref List<Item>? __result)
    {
        var b = destination as Buildable ?? (destination as Decoration)?.Parent?.Buildable;
        if (b == null || !IsOwned(b)) return true;
        __result = b.Community.Inventory.ReturnContainsItems(requiredResources) &&
                   b.Community.Inventory.TryReturnReserveClosestItems(destination, requiredResources, out var items)
            ? items
            : new List<Item>();
        return false;
    }

    private static bool ReserveArray(IPathfindingNodeProvider destination, CountedItemProperty[] requiredResources, ref List<Item>? __result) =>
        ReserveList(destination, requiredResources, ref __result);

    private static bool ReserveRecipe(Producer __instance, ref List<Item>? reservedItems, Producer.Recipe recipe, bool handleMalfunctions, ref bool __result)
    {
        if (!IsOwned(__instance)) return true;
        reservedItems = null;
        __result = false;
        if (!__instance.IsEnabled() || recipe == null) return false;
        var inv = __instance.Buildable.Community.Inventory;
        if (recipe.ProducedItems.Any(c => !inv.FitsItem(c.ItemProperties)) || !inv.ReturnContainsItems(recipe.Ingredients)) return false;
        if (handleMalfunctions) recipe.ClearMalfunctions();
        if (!inv.TryReturnReserveClosestItems(__instance.Buildable, recipe.Ingredients, out var items)) return false;
        reservedItems = items;
        __result = true;
        return false;
    }

    private static bool FoundItems(Producer __instance)
    {
        if (!IsOwned(__instance)) return true;
        foreach (var recipe in __instance.ProductionProperties.Recipes)
            foreach (var item in recipe.ProducedItems)
                __instance.Buildable.Community.AddFoundItem(item.ItemProperties);
        return false;
    }

    private static bool ProductionLimits(IItemProducer producer) => producer == null || !IsOwned(producer.Buildable);
    private static bool BuildableEventFilter(Buildable buildable) => !IsOwned(buildable);
    private static void EventScope(Producer __instance, out Producer? __state) { __state = _eventProducer; _eventProducer = IsOwned(__instance) ? __instance : null; }
    private static Exception? EndEventScope(Exception? __exception, Producer? __state) { _eventProducer = __state; return __exception; }
    private static bool ItemEventFilter() => !IsOwned(_eventProducer);

    private static void BeforeActorRescue(Project? __0, Boat? __1, out PlayerCommunityScope __state)
    {
        var owner = __1?.Buildable?.Community
                    ?? (__0 != null ? ResolveProject(__0) : LandmarkReplication.CurrentRescueOwner)
                    ?? Community.PlayerCommunity;
        __state = WithPlayerCommunity(owner);
    }

    private static Exception? AfterActorRescue(Exception? __exception, PlayerCommunityScope __state)
    {
        __state.Dispose();
        return __exception;
    }

    private static void AfterBirdIsInPlayerCommunity(ActorBehaviour __instance, ref bool __result)
    {
        if (!__result && Plugin.Options.role == "host" && __instance is Bird bird && IsOwned(bird.Community))
            __result = true;
    }

    private static bool BeforeBirdWorldStoppedMoving(Bird __instance)
    {
        if (__instance == null || !IsOwned(__instance.Community)) return true;
        if (BirdPerchSpotField?.GetValue(__instance) == null)
        {
            BirdResetMethod?.Invoke(__instance, Array.Empty<object>());
            var center = FleetEngineBundle.HeartBuildable(__instance.Community)?.transform.position ?? __instance.transform.position;
            var offset = __instance.transform.position - center;
            var maxR = GameSettings.Instance?.GameplaySettings?.SwimmingRadius ?? 85f;
            if (offset.magnitude > maxR)
                __instance.transform.position = center + offset.normalized * maxR;
        }
        return false;
    }

    private static void BeforeBirdHouseScope(BirdHouse? __instance, out PlayerCommunityScope __state) =>
        __state = WithPlayerCommunity(__instance?.Buildable?.Community);

    private static void BeforeDistributerScope(ItemToDistribute? __instance, out PlayerCommunityScope __state) =>
        __state = WithPlayerCommunity(__instance?.ItemDistributer?.Buildable?.Community);

    private static void BeforeAquaFarmScope(AquaFarm? __instance, out PlayerCommunityScope __state) =>
        __state = WithPlayerCommunity(__instance?.Buildable?.Community);

    private static void BeforeMedPodScope(MedPod? __instance, out PlayerCommunityScope __state) =>
        __state = WithPlayerCommunity(__instance?.Buildable?.Community);

    private static void BeforeBoatMoveCursorScope(BoatMovementCursorProperties? __instance, out PlayerCommunityScope __state)
    {
        var boat = __instance != null ? BoatCursorBoatField?.GetValue(__instance) as Boat : null;
        __state = WithPlayerCommunity(boat?.Buildable?.Community);
    }

    private static Exception? AfterPlayerScope(Exception? __exception, PlayerCommunityScope __state)
    {
        __state.Dispose();
        return __exception;
    }

    private static bool BeforeBirdHousePopulateItems(BirdHouse __instance, Debris flotsam)
    {
        if (!Active || __instance?.Buildable?.Community == null || flotsam == null) return true;
        var center = FleetEngineBundle.HeartBuildable(__instance.Buildable.Community)?.transform.position ?? __instance.transform.position;
        var r = GameSettings.Instance?.GameplaySettings?.InteractionRadius ?? 200f;
        var dx = flotsam.Position.x - center.x;
        var dz = flotsam.Position.z - center.z;
        if (dx * dx + dz * dz > r * r) return false;

        if (BirdHouseItemGroupsField?.GetValue(__instance) is not ItemPropertiesGroup[] groups) return true;
        foreach (var item in flotsam.Inventory.ReturnAllItems())
        {
            for (var i = 0; i < groups.Length && !groups[i].TryAddItem(item); i++) { }
        }
        return false;
    }

    private static bool BeforeBirdHouseSalvageableAvailable(BirdHouse __instance, out Item? item, ref bool __result)
    {
        item = null;
        if (__instance == null || !IsOwned(__instance.Buildable)) return true;
        __result = false;
        if (!__instance.Active ||
            __instance.Buildable.Inventory.ReturnCapacity() <= __instance.Buildable.Inventory.ReturnCount(SubInventoryType.Storage, includeReserved: true))
            return false;

        var bestDistSq = float.MaxValue;
        var housePos = __instance.transform.position;
        Item? candidate = null;
        foreach (var s in __instance.SalvageableItems)
        {
            if (s == null || s.IsReserved || s.InventoryType != InventoryType.Flotsam || s.Project != null || s.Owner == null) continue;
            var dSq = (s.Owner.transform.position - housePos).sqrMagnitude;
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                candidate = s;
            }
        }
        if (candidate == null) return false;
        if (candidate.Reserve())
        {
            if (__instance.Buildable.Inventory.ReserveIncomingItem(candidate, SubInventoryType.Storage))
            {
                item = candidate;
                __result = true;
                return false;
            }
            candidate.CancelReservation();
        }
        return false;
    }

    // Native Save & Restore hooks
    private static void SerializeSecondaryAgents(CommunityPersistentData __instance, Community community)
    {
        if (!IsOwned(community) || PersistenceLifeCycle.State != PersistenceState.Saving) return;
        __instance.Agents ??= new List<AgentPersistentData>();
        if (__instance.Agents.Count == 0)
            foreach (var agent in community.Agents)
                if (agent != null) __instance.Agents.Add(new AgentPersistentData(agent));
        __instance.Birds ??= new List<BirdPersistentData>();
        if (__instance.Birds.Count == 0 && community.Birds != null)
        {
            foreach (var bird in community.Birds)
                if (bird != null && BirdPersistentData.TryPersist(bird, out var bpd) && bpd != null)
                    __instance.Birds.Add(bpd);
        }
    }

    private static void EnsureCommunityInventory(Community __instance)
    {
        if (__instance != null && !__instance.IsPlayerCommunity() && __instance.Inventory == null)
            __instance.Inventory = new CommunityInventory();
    }

    private static void AfterAddAgent(Community __instance, Agent agent)
    {
        if (__instance == null || agent == null || __instance.IsPlayerCommunity()) return;
        if (agent.Assignments.Count == 0 && __instance.Research != null) agent.ApplyAssignmentTemplate();
    }

    private static void RestoreSecondaryCommunity(CommunityPersistentData __instance)
    {
        if (__instance == null || __instance.Type != Community.Type.Regular || __instance.ResearchData == null) return;
        var community = typeof(CommunityPersistentData).GetField("_instance", Flags)?.GetValue(__instance) as Community;
        if (community == null || community.IsPlayerCommunity()) return;
        community.Inventory ??= new CommunityInventory();
        if (community.Research == null)
        {
            community.Research = new CommunityResearch(community);
            __instance.ResearchData.Restore(community);
        }
        if (__instance.Birds != null && community.Birds.Count == 0)
        {
            foreach (var bpd in __instance.Birds)
                bpd?.Restore(community);
        }
    }

    private static void RestoreSecondaryNavigator(AgentPersistentData __instance)
    {
        var agent = __instance?.Instance;
        var community = agent?.Community;
        if (agent == null || community == null || community.IsPlayerCommunity() || community.Research == null) return;
        var closest = community.Constructions
            .OrderBy(c => (c.transform.position - agent.transform.position).sqrMagnitude)
            .FirstOrDefault();
        if (closest != null) agent.ReturnNavigator().AttachToTarget(closest.Target);
    }

    // Transpiler helpers
    private static void Metadata(CodeInstruction from, CodeInstruction to)
    {
        foreach (var name in new[] { "labels", "blocks" })
        {
            var field = AccessTools.Field(typeof(CodeInstruction), name);
            var a = (System.Collections.IList)field.GetValue(from)!;
            var b = (System.Collections.IList)field.GetValue(to)!;
            foreach (var v in a) b.Add(v);
            a.Clear();
        }
    }

    private static IEnumerable<CodeInstruction> ReplaceGetter(IEnumerable<CodeInstruction> instructions, MethodInfo getter, string helper, OpCode load)
    {
        foreach (var i in instructions)
        {
            if (!i.Calls(getter)) { yield return i; continue; }
            var arg = new CodeInstruction(load);
            Metadata(i, arg);
            yield return arg;
            i.opcode = OpCodes.Call;
            i.operand = Own(helper);
            yield return i;
        }
    }

    private static IEnumerable<CodeInstruction> OwnerHeartIL(IEnumerable<CodeInstruction> code) =>
        ReplaceGetter(code, AccessTools.PropertyGetter(typeof(Construction), "Townheart"), nameof(Heart), OpCodes.Ldarg_0);
    private static IEnumerable<CodeInstruction> AgentOwnerIL(IEnumerable<CodeInstruction> code) =>
        ReplaceGetter(code, AccessTools.PropertyGetter(typeof(Community), "PlayerCommunity"), nameof(AgentOwner), OpCodes.Ldarg_1);
    private static IEnumerable<CodeInstruction> ProjectOwnerIL(IEnumerable<CodeInstruction> code) =>
        ReplaceGetter(code, AccessTools.PropertyGetter(typeof(Community), "PlayerCommunity"), nameof(ResolveProject), OpCodes.Ldarg_0);
    private static IEnumerable<CodeInstruction> TaskOwnerIL(IEnumerable<CodeInstruction> code) =>
        ReplaceGetter(code, AccessTools.PropertyGetter(typeof(Community), "PlayerCommunity"), nameof(ResolveProject), OpCodes.Ldarg_1);
    private static IEnumerable<CodeInstruction> InventoryOwnerIL(IEnumerable<CodeInstruction> code) =>
        ReplaceGetter(code, AccessTools.PropertyGetter(typeof(Community), "PlayerCommunity"), nameof(InventoryOwner), OpCodes.Ldarg_0);
    private static IEnumerable<CodeInstruction> BuildableOwnerIL(IEnumerable<CodeInstruction> code) =>
        ReplaceGetter(code, AccessTools.PropertyGetter(typeof(Community), "PlayerCommunity"), nameof(BuildableOwner), OpCodes.Ldarg_0);
}
