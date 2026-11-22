extern alias Game;
using System;
using System.Reflection;
using HarmonyLib;
using Debris=Game::Flotsam;

namespace Flotsam.Multiplayer;
internal static class GamePatches
{
    private static GameSession? Network=>Plugin.Instance?.Network;
    private static bool SimulatingClient=>Network?.IsClient==true&&!Network.Applying;
    private static bool Client=>Network?.IsClient==true&&Network.ReplicaReady&&!Network.Applying;
    private static float _nextNavigation;
    private static bool _navigationActive;
    private static int _internalSpeedDepth;
    private static bool Helm=>Network==null||Network.Applying||!GameSession.Ready()||Network.IsCaptain||Plugin.Options.mode=="independent";
    internal static void Install(Harmony harmony)
    {
        foreach(var type in new[]{typeof(Buildable),typeof(Boat),typeof(Producer),typeof(WalkwaySegment),typeof(Marker),typeof(ProjectManager),typeof(TimeManager),typeof(Vitals),typeof(Agent),typeof(AgentManager),typeof(Navigator),typeof(PathfinderNavigator),typeof(UnityNavMeshNavigator),typeof(Debris),typeof(NoCollisionBuoyantFlotsam),typeof(PhysicsFlotsam),typeof(PhysicsManager),typeof(WorldManager),typeof(WorldMap),typeof(WorldMapTownheartPhycis2D),typeof(Bird),typeof(BirdHouse),typeof(BirdMovement),typeof(AquaFarm),typeof(CropDecorationBehaviour),typeof(ItemDistributer),typeof(MedPod),typeof(School),typeof(Decoration),typeof(PajamaLlama.Flotsam.Performance.UpdateManager)})
            foreach(var name in new[]{"Update","FixedUpdate","LateUpdate"}){var method=type.GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);if(method!=null)harmony.Patch(method,prefix:new HarmonyMethod(typeof(GamePatches),nameof(Simulate)));}
        foreach(var simInit in new MethodBase?[]{
            AccessTools.DeclaredMethod(typeof(Agent),"Start"),
            AccessTools.DeclaredMethod(typeof(Navigator),"Start"),
            AccessTools.DeclaredMethod(typeof(Agent),"OnTownheartMoved"),
            AccessTools.DeclaredMethod(typeof(Marker),"OnTownheartMoved"),
            AccessTools.DeclaredMethod(typeof(BirdHouse),"OnTownheartMoved")
        })
            if(simInit!=null)harmony.Patch(simInit,prefix:new HarmonyMethod(typeof(GamePatches),nameof(Simulate)));
        foreach(var physHook in new MethodBase?[]{
            AccessTools.DeclaredMethod(typeof(PhysicsController),nameof(PhysicsController.Initialize),new[]{typeof(PhysicsProperties),typeof(float)}),
            AccessTools.DeclaredMethod(typeof(PhysicsController),nameof(PhysicsController.PhysicsActive),new[]{typeof(bool)})
        })
            if(physHook!=null)harmony.Patch(physHook,postfix:new HarmonyMethod(typeof(GamePatches),nameof(ClampClientPhysics)));
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
        FleetAnchorRebase.Install(harmony);
        FleetUnionTiles.Install(harmony);
        LandmarkReplication.Install(harmony);
        DecorationReplication.Install(harmony);
        SpecialistReplication.Install(harmony);
        BirdReplication.Install(harmony);
        ProgressionReplication.Install(harmony);
        MoveConstructionCommands.Install(harmony);
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),"SetSelectedBuildable"),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientPickUp)));
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),"UpdateConstuctionPreview"),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientUpdate)));
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),"RemoveSelectedBuildable"),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientRemove)));
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),"DeactivateImmediately"),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientCancel)));
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),nameof(MoveBuildableCursorProperties.SetSelectedBuildableProperties)),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientPickStored)));
        harmony.Patch(AccessTools.Method(typeof(MoveBuildableCursorProperties),"MoveSelectedBuildable"),prefix:new HarmonyMethod(typeof(MoveConstructionCommands),nameof(MoveConstructionCommands.ClientPlace)));
        foreach(var internalSpeed in new MethodBase?[]{
            AccessTools.Method(typeof(MoveBuildableCursorProperties),"Activate"),
            AccessTools.Method(typeof(MoveBuildableCursorProperties),"DeactivateImmediately"),
            AccessTools.Method(typeof(DialoguePanel),"Open"),
            AccessTools.Method(typeof(DialoguePanel),"EndDialogue"),
            AccessTools.DeclaredMethod(typeof(DialogueEventSetGameSpeed),"IDialogueEvent.TriggerEvent"),
            AccessTools.Method(typeof(DPadMenuBase),"Enable"),
            AccessTools.Method(typeof(DPadMenuBase),"Disable"),
            AccessTools.Method(typeof(UIManager),"RestartGame")
        })
            if(internalSpeed!=null)
                harmony.Patch(internalSpeed,prefix:new HarmonyMethod(typeof(GamePatches),nameof(BeginInternalSpeed)),finalizer:new HarmonyMethod(typeof(GamePatches),nameof(EndInternalSpeed)));
        // Storing a road back into the building inventory also mutates the replica locally.
        harmony.Patch(AccessTools.Method(typeof(LineConstructionPreviewStrategy),"StoreBuildable"),prefix:new HarmonyMethod(typeof(GamePatches),nameof(StoreRoad)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LineConstructionPreviewStrategy),"Iterate",Type.EmptyTypes),prefix:new HarmonyMethod(typeof(GamePatches),nameof(StoreRoad)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LineConstructionPreviewStrategy),"ContinuousBuilding",Type.EmptyTypes),prefix:new HarmonyMethod(typeof(GamePatches),nameof(StoreRoad)));
        harmony.Patch(AccessTools.Method(typeof(WorldMapTownheartPhycis2D),"MoveWithKeys"),prefix:new HarmonyMethod(typeof(GamePatches),nameof(CanSteer)));
        harmony.Patch(AccessTools.PropertySetter(typeof(Buildable),nameof(Buildable.Name)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(Rename)));
        harmony.Patch(AccessTools.PropertySetter(typeof(Buildable),nameof(Buildable.AssignmentLimit)),prefix:new HarmonyMethod(typeof(GamePatches),nameof(Assignment)));
        var moorBoat=AccessTools.DeclaredMethod(typeof(BoatCursorProperties),"<UpdateCursor>g__MoorBoat|5_0");
        if(moorBoat!=null)harmony.Patch(moorBoat,prefix:new HarmonyMethod(typeof(GamePatches),nameof(PlaceBoat)));
        var energyConnect=AccessTools.DeclaredMethod(typeof(EnergyGridConnectCursorProperties),"Connect");
        if(energyConnect!=null)harmony.Patch(energyConnect,prefix:new HarmonyMethod(typeof(GamePatches),nameof(ConnectEnergy)));
        foreach(var disconnect in AccessTools.GetDeclaredMethods(typeof(EnergyGridBuildableComponent)))
            if(disconnect.Name=="Disconnect"&&disconnect.GetParameters().Length==1)
                harmony.Patch(disconnect,prefix:new HarmonyMethod(typeof(GamePatches),nameof(DisconnectEnergy)));
        var initBuildablePanel=AccessTools.DeclaredMethod(typeof(BuildablePanel),"Initialize",new[]{typeof(Buildable),typeof(bool)});
        if(initBuildablePanel!=null)harmony.Patch(initBuildablePanel,postfix:new HarmonyMethod(typeof(FleetHud),nameof(FleetHud.OnBuildablePanelInitialize)));
        var updateBuildablePanel=AccessTools.DeclaredMethod(typeof(BuildablePanel),"UpdatePanel",Type.EmptyTypes);
        if(updateBuildablePanel!=null)harmony.Patch(updateBuildablePanel,postfix:new HarmonyMethod(typeof(FleetHud),nameof(FleetHud.OnBuildablePanelUpdate)));
        foreach(var action in new[]{"SwitchActivation","OnClickSalvageButton","OnClickUpgradeButton","PopUpNameChange"})
            harmony.Patch(AccessTools.Method(typeof(BuildablePanel),action),prefix:new HarmonyMethod(typeof(GamePatches),nameof(OwnBuildingPanel)));
        foreach(var action in new[]{"OpenDrifterDutiesPanel","OpenDrifterExpertisePanel","PopUpNameChange"})
            harmony.Patch(AccessTools.Method(typeof(AgentPanel),action),prefix:new HarmonyMethod(typeof(GamePatches),nameof(OwnAgentPanel)));
    }
    private static bool OwnBuildingPanel(BuildablePanel __instance)=>__instance.Buildable==null||__instance.Buildable.Community==Community.PlayerCommunity;
    private static bool OwnAgentPanel(AgentPanel __instance)=>__instance.AgentReference==null||__instance.AgentReference.Community==Community.PlayerCommunity;
    private static bool Simulate()=>!SimulatingClient;
    private static void ClampClientPhysics(PhysicsController __instance){if(Network?.IsClient==true)ReplicaFreeze.ClampPhysicsController(__instance);}
    private static bool PlaceBoat(BoatCursorProperties __instance,object[] __args)
    {
        if(!Client)return true;
        var mooringPoint=__args.Length>0?__args[0] as MooringPoint:null;
        var boat=(Boat)AccessTools.Field(typeof(BoatCursorProperties),"_boat").GetValue(__instance);
        var visual=(int)AccessTools.Field(typeof(BuildableCursorProperties),"_visualIndex").GetValue(__instance);
        if(boat?.Buildable?.Properties!=null&&mooringPoint?.Buildable!=null)
        {
            Network!.Submit(new GameCommand{kind="placeBoat",properties=boat.Buildable.Properties.name,target=Network.Id(mooringPoint.Buildable),visual=visual});
            if(!RewiredActions.IsContinuousBuilding())GameManager.CursorManager.Deactivate();
        }
        return false;
    }
    private static bool ConnectEnergy(EnergyGridConnectCursorProperties __instance,object[] __args)
    {
        if(!Client)return true;
        var other=__args.Length>0?__args[0] as EnergyGridConnector:null;
        var cursor=__args.Length>1?__args[1] as CursorManager:null;
        var src=AccessTools.Field(typeof(EnergyGridConnectCursorProperties),"_component").GetValue(__instance) as EnergyGridConnector;
        var withIndex=(bool)AccessTools.Field(typeof(EnergyGridConnectCursorProperties),"_connectWithIndex").GetValue(__instance);
        var idx=(uint)AccessTools.Field(typeof(EnergyGridConnectCursorProperties),"_componentConnectIndex").GetValue(__instance);
        if(DecorationReplication.TrySubmitEnergyCommand(Network!,"energyConnect",src,other,withIndex?(int)idx:-1))
            cursor?.Deactivate();
        return false;
    }
    private static bool DisconnectEnergy(EnergyGridBuildableComponent __instance,object[] __args)
    {
        if(!Client)return true;
        var other=__args.Length>0?__args[0] as EnergyGridConnector:null;
        DecorationReplication.TrySubmitEnergyCommand(Network!,"energyDisconnect",__instance,other,-1);
        return false;
    }
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
        var p=ClientOceanAnchor.ClientToHostPos(__instance.transform.position);
        var yaw=ClientOceanAnchor.ClientToHostYaw(__instance.transform.eulerAngles.y);
        Network!.Submit(new GameCommand{kind="place",properties=__instance.Properties.name,x=p.x,z=p.z,rotation=yaw,visual=__instance.VisualIndex});
        // Retain no predicted inventory or project state; the authoritative frame recreates it.
        __instance.Remove();return false;
    }
    private static bool PlaceConstruction(ConstructionCursorProperties __instance,CursorManager cursorManager,ConstructionPreview constructionPreview)
    {
        if(FleetCombat.BlocksInput)return false;
        if(!Client)return true;
        if(!constructionPreview.CanPlace||!__instance.GetInteract())return false;
        var prefab=(Buildable)AccessTools.Field(typeof(ConstructionCursorProperties),"_buildable").GetValue(__instance);
        var visual=(int)AccessTools.Field(typeof(ConstructionCursorProperties),"_visualIndex").GetValue(__instance);
        var p=ClientOceanAnchor.ClientToHostPos(constructionPreview.Transform.position);
        var yaw=ClientOceanAnchor.ClientToHostYaw(constructionPreview.Transform.eulerAngles.y);
        Network!.Submit(new GameCommand{kind="place",properties=prefab.Properties.name,x=p.x,z=p.z,rotation=yaw,visual=visual});
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
        if(FleetCombat.BlocksInput){__result=false;return false;}
        if(!Client)return true;
        __result=false;
        if(!(bool)AccessTools.Field(typeof(MarkerCursorProperties),"_canPlace").GetValue(__instance))return false;
        var p=ClientOceanAnchor.ClientToHostPos((UnityEngine.Vector3)AccessTools.Field(typeof(MarkerCursorProperties),"_cursorPosition").GetValue(__instance));
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
    private static void BeginInternalSpeed()=>_internalSpeedDepth++;
    private static Exception? EndInternalSpeed(Exception? __exception){if(_internalSpeedDepth>0)_internalSpeedDepth--;return __exception;}
    private static bool Denied(string message="只有船长可以控制航行和全局速度"){Network?.ShowNotice(message,true);return false;}
    private static bool CanSteer()=>!FleetCombat.BlocksInput&&Helm;
    private static bool Speed(GameSpeed gameSpeedToSet)
    {
        if(_internalSpeedDepth>0)return !Client&&Helm;
        if(!Helm)return Denied();
        if(!Client)return true;
        if(Plugin.Options.mode=="independent")return Denied("独立模式下由房主统一控制全局时间倍速");
        Network!.Submit(new GameCommand{kind="speed",value=(int)gameSpeedToSet});
        return false;
    }
    private static bool SpeedZero()
    {
        if(_internalSpeedDepth>0)return !Client&&Helm;
        if(!Helm)return Denied();
        if(!Client)return true;
        if(Plugin.Options.mode=="independent")return Denied("独立模式下由房主统一控制全局时间倍速");
        Network!.Submit(new GameCommand{kind="speedZero"});
        return false;
    }
    private static bool MapOpen(WorldMap __instance)=>LiveNavigation.Open(__instance);
    private static bool MapClose(WorldMap __instance,ref bool __result)=>LiveNavigation.Close(__instance,ref __result);
    private static bool ToggleMap(ref bool __result){if(!LiveNavigation.Owns(GameManager.WorldMapManager.WorldMap))return true;__result=LiveNavigation.ViewOpen&&GameManager.WorldMapManager.WorldMap.Close();return false;}
    private static bool AutomaticMapView()=>!LiveNavigation.Owns(GameManager.WorldMapManager.WorldMap);
    private static bool PlannedNavigation(MapPath path)
    {
        if(!Helm)return Denied();
        if(!Client)return true;
        if(path==null||path.EvaluatedState!=MapPath.State.Ok)return false;
        var dest=path.Destination;
        var encoded=NavigationReplication.EncodeMapPath(path);
        GameManager.WorldMapManager?.WorldMap?.DisablePlanningPath();
        Network!.Submit(new GameCommand{kind="navigatePlan",x=dest.x,z=dest.z,text=encoded});
        return false;
    }
    private static bool Navigate(UnityEngine.Vector3 target,MouseMovementCursorProperties.Gear gear)
    {
        if(FleetCombat.BlocksInput)return false;
        if(!Helm)return Denied();if(!Client)return true;
        if(UnityEngine.Time.realtimeSinceStartup<_nextNavigation)return false;
        _nextNavigation=UnityEngine.Time.realtimeSinceStartup+.1f;_navigationActive=true;
        Network!.Submit(new GameCommand{kind="navigate",x=target.x,z=target.z,value=(int)gear});return false;
    }
    private static bool NavigateStop(){ShipSailingVisuals.ClearCourseIndicator();if(!Helm)return false;if(!Client)return true;if(_navigationActive){_navigationActive=false;Network!.Submit(new GameCommand{kind="navigateStop"});}return false;}
    private static bool LinePlacement(LineConstructionCursorProperties __instance,ref bool __result)
    {
        if(!Client||(LineConstructionCursorProperties.Iteration)AccessTools.Field(typeof(LineConstructionCursorProperties),"_iteration").GetValue(__instance)==LineConstructionCursorProperties.Iteration.StartPreview)return true;
        // Iterate(false) keeps the native cursor alive; true deactivates it. Submit resets only
        // preview state, never creates a local ponton/segment or a predicted inventory debit.
        ConstructionCommands.Submit(Network!,__instance);__result=false;return false;
    }
    private static bool StoreRoad()=>!Client;
}
