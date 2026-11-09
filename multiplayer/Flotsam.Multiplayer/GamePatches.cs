using System;
using System.Reflection;
using HarmonyLib;

namespace Flotsam.Multiplayer;
internal static class GamePatches
{
    private static GameSession? Network=>Plugin.Instance?.Network;
    private static bool Client=>Network?.IsClient==true&&Network.ReplicaReady&&!Network.Applying;
    private static float _nextNavigation;
    private static bool _navigationActive;
    private static bool Helm=>Network==null||Network.Applying||!GameSession.Ready()||Network.IsCaptain||Plugin.Options.mode=="independent";
    internal static void Install(Harmony harmony)
    {
        foreach(var type in new[]{typeof(Buildable),typeof(Boat),typeof(Producer),typeof(WalkwaySegment),typeof(Marker),typeof(ProjectManager),typeof(TimeManager),typeof(Vitals),typeof(Agent),typeof(AgentManager),typeof(WorldManager),typeof(WorldMap),typeof(WorldMapTownheartPhycis2D),typeof(PajamaLlama.Flotsam.Performance.UpdateManager)})
            foreach(var name in new[]{"Update","FixedUpdate","LateUpdate"}){var method=type.GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);if(method!=null)harmony.Patch(method,prefix:new HarmonyMethod(typeof(GamePatches),nameof(Simulate)));}
        foreach(var name in new[]{"Activate","Deactivate","Salvage","Upgrade","CancelUpgrade"})harmony.Patch(AccessTools.Method(typeof(Buildable),name,Type.EmptyTypes),prefix:new HarmonyMethod(typeof(GamePatches),nameof(BuildingAction)));
        harmony.Patch(AccessTools.Method(typeof(Buildable),nameof(Buildable.StartBuilding)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(StartBuilding)));
        harmony.Patch(AccessTools.Method(typeof(ConstructionCursorProperties),"TryPlacingConstruction"),prefix:new HarmonyMethod(typeof(GamePatches),nameof(PlaceConstruction)));
        foreach(var name in new[]{"SetSelectedRecipe","ToggleSelecteRecipeContinuous","IncreaseSelectedRecipeAmountToProduce","CancelRecipe","IncreasePriority","DecreasePriority"})
            harmony.Patch(AccessTools.Method(typeof(Producer),name),prefix:new HarmonyMethod(typeof(GamePatches),nameof(Production)));
        foreach(var name in new[]{"SetAmountToProduce","IncreaseAmountToProduce","DecreaseAmountToProduce","ToggleContinuous","DisableContinuous"})
            harmony.Patch(AccessTools.Method(typeof(Producer.Recipe),name),prefix:new HarmonyMethod(typeof(GamePatches),nameof(Recipe)));
        foreach(var name in new[]{"StartResearch","CancelResearch"})harmony.Patch(AccessTools.Method(typeof(CommunityResearch),name),prefix:new HarmonyMethod(typeof(GamePatches),nameof(Research)));
        harmony.Patch(AccessTools.Method(typeof(QueuedRecipe),nameof(QueuedRecipe.Release)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(QueueReleased)));
        harmony.Patch(AccessTools.Method(typeof(Agent),nameof(Agent.SetName)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(AgentName)));
        harmony.Patch(AccessTools.Method(typeof(Agent),nameof(Agent.SetAssignmentEnabled)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(AgentAssignment)));
        harmony.Patch(AccessTools.Method(typeof(Agent),nameof(Agent.TryUpdateAssignmentPriority)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(AgentPriority)));
        harmony.Patch(AccessTools.Method(typeof(MarkerCursorProperties),"TryToPlaceMarker"),prefix:new HarmonyMethod(typeof(GamePatches),nameof(PlaceMarker)));
        foreach(var name in new[]{"Remove","SetSizeIndex","SetAgentAmount","ToggleItemFilter"})harmony.Patch(AccessTools.Method(typeof(Marker),name),prefix:new HarmonyMethod(typeof(GamePatches),nameof(MarkerAction)));
        harmony.Patch(AccessTools.Method(typeof(GameSpeedManager),nameof(GameSpeedManager.SetGameSpeed)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(Speed)));
        harmony.Patch(AccessTools.Method(typeof(GameSpeedManager),nameof(GameSpeedManager.ToggleGameSpeedZero)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(SpeedZero)));
        harmony.Patch(AccessTools.Method(typeof(WorldMap),nameof(WorldMap.SetMouseMovementTarget)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(Navigate)));
        harmony.Patch(AccessTools.Method(typeof(WorldMap),nameof(WorldMap.ClearMouseMovementTarget)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(NavigateStop)));
        harmony.Patch(AccessTools.Method(typeof(WorldMap),nameof(WorldMap.Open)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(MapOpen)));
        harmony.Patch(AccessTools.Method(typeof(WorldMap),nameof(WorldMap.Close)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(MapClose)));
        harmony.Patch(AccessTools.Method(typeof(WorldMap),nameof(WorldMap.MoveTo)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(PlannedNavigation)));
        harmony.Patch(AccessTools.Method(typeof(WorldMapManager),"TryCloseMap"),prefix:new HarmonyMethod(typeof(GamePatches),nameof(ToggleMap)));
        foreach(var name in new[]{"OnCameraMaximumZoom","OnCameraMinimumZoom"})harmony.Patch(AccessTools.Method(typeof(WorldMapManager),name),prefix:new HarmonyMethod(typeof(GamePatches),nameof(AutomaticMapView)));
        harmony.Patch(AccessTools.Method(typeof(WorldMapTownheartPhycis2D),"UpdateMovementInputs"),prefix:new HarmonyMethod(typeof(LiveNavigation),nameof(LiveNavigation.Input)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LineConstructionCursorProperties),"Iterate",Type.EmptyTypes),prefix:new HarmonyMethod(typeof(GamePatches),nameof(LinePlacement)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(BuildingGrid),nameof(BuildingGrid.SetPlacement),new[]{typeof(UnityEngine.Vector3),typeof(Hookable.Hook)}),prefix:new HarmonyMethod(typeof(ConstructionCommands),nameof(ConstructionCommands.PreserveHostGrid)));
        // Pickup already removes roads / attached segments in the preview strategy's Activate.
        // Block BEFORE selection or consuming a stored buildable, not merely at the final click.
        MoveConstructionCommands.Install(harmony);
        MoveConstructionCommands.StorageReplicationReady=true;
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),"SetSelectedBuildable"),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientPickUp)));
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),"UpdateConstuctionPreview"),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientUpdate)));
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),"RemoveSelectedBuildable"),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientRemove)));
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),"DeactivateImmediately"),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientCancel)));
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),nameof(MoveBuildableCursorProperties.SetSelectedBuildableProperties)),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientPickStored)));
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),"MoveSelectedBuildable"),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientPlace)));
        // Storing a road back into the building inventory also mutates the replica locally.
        harmony.Patch(AccessTools.Method(typeof(LineConstructionPreviewStrategy),"StoreBuildable"),prefix:new HarmonyMethod(typeof(GamePatches),nameof(StoreRoad)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LineConstructionPreviewStrategy),"Iterate",Type.EmptyTypes),prefix:new HarmonyMethod(typeof(GamePatches),nameof(StoreRoad)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LineConstructionPreviewStrategy),"ContinuousBuilding",Type.EmptyTypes),prefix:new HarmonyMethod(typeof(GamePatches),nameof(StoreRoad)));
        harmony.Patch(AccessTools.Method(typeof(WorldMapTownheartPhycis2D),"MoveWithKeys"),prefix:new HarmonyMethod(typeof(GamePatches),nameof(CanSteer)));
        harmony.Patch(AccessTools.PropertySetter(typeof(Buildable),nameof(Buildable.Name)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(Rename)));
        harmony.Patch(AccessTools.PropertySetter(typeof(Buildable),nameof(Buildable.AssignmentLimit)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(Assignment)));
        harmony.Patch(AccessTools.Method(typeof(GameSpeedManager),"SetGameSpeedAndTimeScale"),postfix:new HarmonyMethod(typeof(GamePatches),nameof(Freeze)));
        // A client replica never writes an authoritative save or triggers auto-save achievements.
        foreach(var method in AccessTools.GetDeclaredMethods(typeof(PlayerRun)))if(method.Name=="Save")harmony.Patch(method,prefix:new HarmonyMethod(typeof(GamePatches),nameof(CanSave)));
    }
    private static bool Simulate()=>!Client;
    private static bool CanSave()=>Network?.IsClient!=true;
    private static void Freeze(){if(Client)UnityEngine.Time.timeScale=0;}
    private static bool BuildingAction(Buildable __instance,MethodBase __originalMethod)
    {
        if(!Client)return true;
        Network!.Submit(new GameCommand{kind=__originalMethod.Name.ToLowerInvariant(),target=Network.Id(__instance)});return false;
    }
    private static bool Rename(Buildable __instance,string value)
    {
        if(!Client)return true;Network!.Submit(new GameCommand{kind="rename",target=Network.Id(__instance),text=value??""});return false;
    }
    private static bool Assignment(Buildable __instance,int value)
    {
        if(!Client)return true;Network!.Submit(new GameCommand{kind="assignmentLimit",target=Network.Id(__instance),value=value});return false;
    }
    private static bool StartBuilding(Buildable __instance)
    {
        if(!Client)return true;
        var p=__instance.transform.position;
        Network!.Submit(new GameCommand{kind="place",properties=__instance.Properties.name,x=p.x,z=p.z,rotation=__instance.transform.eulerAngles.y,visual=__instance.VisualIndex});
        // Retain no predicted inventory or project state; the authoritative frame recreates it.
        __instance.Remove();return false;
    }
    private static bool PlaceConstruction(ConstructionCursorProperties __instance,CursorManager cursorManager,ConstructionPreview constructionPreview)
    {
        if(!Client)return true;
        if(!constructionPreview.CanPlace||!__instance.GetInteract())return false;
        var prefab=(Buildable)AccessTools.Field(typeof(ConstructionCursorProperties),"_buildable").GetValue(__instance);
        var visual=(int)AccessTools.Field(typeof(ConstructionCursorProperties),"_visualIndex").GetValue(__instance);
        var p=constructionPreview.Transform.position;
        Network!.Submit(new GameCommand{kind="place",properties=prefab.Properties.name,x=p.x,z=p.z,rotation=constructionPreview.Transform.eulerAngles.y,visual=visual});
        if(!RewiredActions.IsContinuousBuilding())cursorManager.Deactivate();return false;
    }
    private static bool Production(Producer __instance,MethodBase __originalMethod,object[] __args)
    {
        if(!Client)return true;
        var command=new GameCommand{target=Network!.Id(__instance.Buildable),value=__instance.SelectedRecipeIndex,amount=1};
        switch(__originalMethod.Name)
        {
            case "SetSelectedRecipe":command.kind="recipeSelect";command.value=(int)__args[0];break;
            case "ToggleSelecteRecipeContinuous":command.kind="recipeContinuous";break;
            case "IncreaseSelectedRecipeAmountToProduce":command.kind="recipeIncrease";break;
            case "CancelRecipe":command.kind="queueCancel";command.reference=Network.QueueId(__instance,(int)__args[0]);break;
            default:command.kind="producerPriority";command.value=__originalMethod.Name=="IncreasePriority"?1:-1;break;
        }
        Network.Submit(command);return false;
    }
    private static bool Recipe(Producer.Recipe __instance,MethodBase __originalMethod,object[] __args)
    {
        if(!Client)return true;
        var kind=__originalMethod.Name;
        var command=new GameCommand{target=Network!.Id(__instance.Producer.Buildable),value=__instance.Index,amount=__args.Length>0?(int)__args[0]:0};
        command.kind=kind=="SetAmountToProduce"?"recipeAmount":kind=="IncreaseAmountToProduce"?"recipeIncrease":kind=="DecreaseAmountToProduce"?"recipeDecrease":kind=="DisableContinuous"?"recipeDisableContinuous":"recipeContinuous";
        Network.Submit(command);return false;
    }
    private static bool Research(MethodBase __originalMethod,object[] __args)
    {
        if(!Client)return true;
        Network!.Submit(new GameCommand{kind=__originalMethod.Name=="StartResearch"?"researchStart":"researchCancel",text=__args.Length>0?((TechTreeNode)__args[0]).Guid:""});return false;
    }
    private static void QueueReleased(QueuedRecipe __instance)=>Network?.QueueReleased(__instance);
    private static bool AgentName(Agent __instance,string newName){if(!Client)return true;Network!.Submit(new GameCommand{kind="agentRename",target=Network.Id(__instance),text=newName??""});return false;}
    private static bool AgentAssignment(Agent __instance,AssignmentType assignmentType,bool enabled,AssignmentPriority priority)
    {
        if(!Client)return true;Network!.Submit(new GameCommand{kind="agentAssignment",target=Network.Id(__instance),value=(int)assignmentType,amount=(int)priority,visual=enabled?1:0});return false;
    }
    private static bool AgentPriority(Agent __instance,AssignmentType assignmentType,AssignmentPriority newPriority)
    {
        if(!Client)return true;
        var assignment=__instance.Assignments.Find(a=>a.Type==assignmentType);
        Network!.Submit(new GameCommand{kind="agentAssignment",target=Network.Id(__instance),value=(int)assignmentType,amount=(int)newPriority,visual=assignment?.Enabled==true?1:0});return false;
    }
    private static bool PlaceMarker(MarkerCursorProperties __instance,ref bool __result)
    {
        if(!Client)return true;
        __result=false;
        if(!(bool)AccessTools.Field(typeof(MarkerCursorProperties),"_canPlace").GetValue(__instance))return false;
        var p=(UnityEngine.Vector3)AccessTools.Field(typeof(MarkerCursorProperties),"_cursorPosition").GetValue(__instance);
        Network!.Submit(new GameCommand{kind="markerPlace",properties=__instance.name,x=p.x,z=p.z,value=(int)AccessTools.Field(typeof(MarkerCursorProperties),"_radiusIntervalIndex").GetValue(__instance),amount=(int)AccessTools.Field(typeof(MarkerCursorProperties),"_agentLimit").GetValue(__instance)});
        __result=true;return false;
    }
    private static bool MarkerAction(Marker __instance,MethodBase __originalMethod,object[] __args)
    {
        if(!Client)return true;
        var command=new GameCommand{target=Network!.Id(__instance)};
        switch(__originalMethod.Name)
        {
            case "Remove":command.kind="markerRemove";break;
            case "SetSizeIndex":command.kind="markerSize";command.value=(int)__args[0];break;
            case "SetAgentAmount":command.kind="markerLimit";command.value=(int)__args[0];break;
            default:command.kind="markerFilter";command.properties=((ItemProperties)__args[0]).name;break;
        }
        Network.Submit(command);return false;
    }
    private static bool Denied(){if(Network!=null)Network.LastResult=new CommandResult{error="只有船长可以控制航行和全局速度"};return false;}
    private static bool CanSteer()=>Helm;
    private static bool Speed(GameSpeed gameSpeedToSet){if(!Helm)return Denied();if(!Client)return true;Network!.Submit(new GameCommand{kind="speed",value=(int)gameSpeedToSet});return false;}
    private static bool SpeedZero(){if(!Helm)return Denied();if(!Client)return true;Network!.Submit(new GameCommand{kind="speedZero"});return false;}
    private static bool MapOpen(WorldMap __instance)=>LiveNavigation.Open(__instance);
    private static bool MapClose(WorldMap __instance,ref bool __result)=>LiveNavigation.Close(__instance,ref __result);
    private static bool ToggleMap(ref bool __result){if(!LiveNavigation.Owns(GameManager.WorldMapManager.WorldMap))return true;__result=LiveNavigation.ViewOpen&&GameManager.WorldMapManager.WorldMap.Close();return false;}
    private static bool AutomaticMapView()=>!LiveNavigation.Owns(GameManager.WorldMapManager.WorldMap);
    private static bool PlannedNavigation(){if(!Helm)return Denied();if(!Client)return true;Network!.LastResult=new CommandResult{error="规划航线仍在适配，请使用鼠标操舵"};return false;}
    private static bool Navigate(UnityEngine.Vector3 target,MouseMovementCursorProperties.Gear gear)
    {
        if(!Helm)return Denied();if(!Client)return true;
        if(UnityEngine.Time.realtimeSinceStartup<_nextNavigation)return false;
        _nextNavigation=UnityEngine.Time.realtimeSinceStartup+.1f;_navigationActive=true;
        Network!.Submit(new GameCommand{kind="navigate",x=target.x,z=target.z,value=(int)gear});return false;
    }
    private static bool NavigateStop(){if(!Helm)return false;if(!Client)return true;if(_navigationActive){_navigationActive=false;Network!.Submit(new GameCommand{kind="navigateStop"});}return false;}
    private static bool LinePlacement(LineConstructionCursorProperties __instance,ref bool __result)
    {
        if(!Client||(LineConstructionCursorProperties.Iteration)AccessTools.Field(typeof(LineConstructionCursorProperties),"_iteration").GetValue(__instance)==LineConstructionCursorProperties.Iteration.StartPreview)return true;
        // Iterate(false) keeps the native cursor alive; true deactivates it. Submit resets only
        // preview state, never creates a local ponton/segment or a predicted inventory debit.
        ConstructionCommands.Submit(Network!,__instance);__result=false;return false;
    }
    private static bool MoveSelectedBuildable()
    {
        if(!Client)return true;
        // Roads and every other movable buildable would otherwise be detached and re-spawned
        // on the replica, producing local ghost pontons that never reach the host.
        Network!.LastResult=new CommandResult{accepted=false,error="移动建筑仍在适配，请由房主操作"};return false;
    }
    private static bool StoreRoad()
    {
        if(!Client)return true;
        Network!.LastResult=new CommandResult{accepted=false,error="道路移动、库存放置和收纳尚未适配，请由房主操作"};return false;
    }
}
