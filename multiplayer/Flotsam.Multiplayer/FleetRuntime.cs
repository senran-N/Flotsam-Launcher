using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PajamaLlama.Persistence;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Unlocked multi-ship runtime. Manages independent player ship communities, construction/graph
/// partitioning, resident/producer owner routing, and native save/restore without artificial fences.
/// </summary>
public static class FleetRuntime
{
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
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

        // Native Save & Restore integration for secondary player communities
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

        // Reconnect / Saved-run recovery: match a restored or disconnected ship with the same player nickname
        var cleanPlayer = string.IsNullOrWhiteSpace(playerName) ? "同行者" : playerName.Replace(":", "").Trim();
        var activePeers = new HashSet<string>((Plugin.Instance?.Network?.Crew ?? Array.Empty<CrewMember>()).Where(c => c.id != peerId).Select(c => c.id));
        foreach (var candidate in FleetEngineBundle.SecondaryCommunities)
        {
            var currentId = FleetEngineBundle.OwnerIdOf(candidate);
            if (activePeers.Contains(currentId)) continue;
            var savedOwner = ExtractShipOwnerName(candidate.Name);
            if (!string.IsNullOrEmpty(savedOwner) && string.Equals(savedOwner, cleanPlayer, StringComparison.OrdinalIgnoreCase))
            {
                typeof(Community).GetProperty("Name", Flags)?.SetValue(candidate, $"Ship:{peerId}:{cleanPlayer}");
                FleetEngineBundle.RebindShipId(candidate, peerId);
                Plugin.Log($"Rebound existing ship '{cleanPlayer}' to peer {peerId}");
                return candidate;
            }
        }

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
        if (Community.PlayerCommunity?.Research != null)
        {
            foreach (var unlocked in Community.PlayerCommunity.Research.ResearchedBuildableProperties)
                if (unlocked != null && !community.Research.ResearchedBuildableProperties.Contains(unlocked))
                    community.Research.UnlockBuildable(unlocked);
        }

        var heart = UnityEngine.Object.Instantiate(heartPrefab, spawnPos, primaryRoot.transform.rotation);
        var engine = heart.GetComponent<Engine>();
        var construction = heart.GetComponent<Construction>();
        FleetEngineBundle.RegisterShip(community, peerId, engine, construction);

        heart.Initialize(community, 0, null, false);
        heart.FinishBuilding(restored: true);
        heart.Activate();
        engine.EnergyGrid?.FillStorageEnergy(1000f);

        // Spawn 2 starting drifters and balanced survival & building supplies for the new ship
        for (var d = 0; d < 2; d++)
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
            if (i < 8 && food != null) community.SpawnItemToAvailableStorage(food);
            if (i < 8 && drink != null) community.SpawnItemToAvailableStorage(drink);
        }
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
        Construction.Townheart = heart.GetComponent<Construction>();
        FleetEngineBundle.AdoptLocal(current, ship, "host");
        ProjectOwners.Clear();
        FleetOwnedNavigation.Clear();
        try { CameraController.Instance?.CenterOnTownheart(); }
        catch (Exception e) { Plugin.Log("AdoptLocalShip camera: " + e.GetType().Name); }
        Plugin.Log($"Adopted local ship '{communityName}'");
        return true;
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

        foreach (var community in FleetEngineBundle.SecondaryCommunities.ToArray())
        {
            community.Inventory?.LateUpdate();
            if (!runNeeds) continue;
            foreach (var agent in community.Agents)
            {
                if (agent == null || !agent.IsAlive) continue;
                foreach (var vital in new[] { VitalType.Hunger, VitalType.Thirst })
                {
                    if (!agent.Vitals.ReturnHasProject(vital) && agent.Vitals.TryReserveItemToConsume(vital, AssignmentPriority.Lowest))
                        agent.Vitals.TryAddConsumeProject(vital, true);
                }
            }
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

    private static Community ResolveProject(Project project)
    {
        if (ProjectOwners.TryGetValue(project, out var owner)) return owner;
        var b = project.TargetBuildable;
        if (b != null && IsOwned(b.Community)) { ProjectOwners[project] = b.Community; return b.Community; }
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
        return false;
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
        var a = inventory.GetComponent<Agent>();
        if (IsOwned(a)) return a!.Community;
        return Community.PlayerCommunity;
    }

    private static bool ReserveList(IPathfindingNodeProvider destination, IReadOnlyList<CountedItemProperty> requiredResources, ref List<Item>? __result)
    {
        if (destination is not Buildable b || !IsOwned(b)) return true;
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
        recipe.ClearMalfunctions();
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

    // Native Save & Restore hooks
    private static void SerializeSecondaryAgents(CommunityPersistentData __instance, Community community)
    {
        if (!IsOwned(community) || PersistenceLifeCycle.State != PersistenceState.Saving) return;
        __instance.Agents ??= new List<AgentPersistentData>();
        if (__instance.Agents.Count == 0)
            foreach (var agent in community.Agents)
                if (agent != null) __instance.Agents.Add(new AgentPersistentData(agent));
        __instance.Birds ??= new List<BirdPersistentData>();
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
