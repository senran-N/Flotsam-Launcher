using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace Flotsam.Multiplayer;

// The native map owns physics as well as its camera. Keep its simulation alive;
// switching a player's view must never stop another player's helm input.
internal static class LiveNavigation
{
    private static WorldMap? _map;
    private static World? _world;
    private static Camera[] _cameras=Array.Empty<Camera>();
    private static bool[] _cameraEnabled=Array.Empty<bool>();
    private static bool _viewOpen;
    private static float _nextCommit,_nextKeys;
    private static Vector2 _remoteKeys;
    private static int _remoteMode;
    private static bool _keysSent;
    private static bool _replicatedMoving;
    private static readonly FieldInfo Changing=AccessTools.Field(typeof(WorldMap),"_isChangingState");
    private static readonly FieldInfo Distance=AccessTools.Field(typeof(WorldMap),"_distanceMoved");
    private static readonly FieldInfo Physics=AccessTools.Field(typeof(WorldMap),"_townheartPhysics");
    private static readonly MethodInfo Activate=AccessTools.Method(typeof(WorldMap),"Activate");
    private static GameSession? Network=>Plugin.Instance?.Network;
    public static bool ViewOpen=>_map!=null?_viewOpen:UIManager.State==UIState.Map;
    public static bool Owns(WorldMap map)=>_map==map&&Network!=null&&GameSession.Ready();
    public static bool Moving=>Network?.IsClient==true?_replicatedMoving:_map!=null&&(_map.Townheart.IsMoving||_remoteMode!=0);
    public static void ReceiveMovement(bool moving)=>_replicatedMoving=moving;

    public static void Ensure()
    {
        var network=Network;
        if(network==null||!GameSession.Ready()||(network.IsClient&&!network.ReplicaReady))return;
        var map=GameManager.WorldMapManager.WorldMap;
        if(map==null||AccessTools.Field(typeof(WorldMap),"_playerCommunity").GetValue(map)==null)return;
        if(_map==map&&ReferenceEquals(_world,GameManager.WorldManager.World))return;
        map.InitializeTownheart();if(!map.Townheart.Initialized)return;
        _map=map;_world=GameManager.WorldManager.World;_viewOpen=map.isActiveAndEnabled&&UIManager.State==UIState.Map;
        _remoteMode=0;_keysSent=false;_nextCommit=0;
        _cameras=map.GetComponentsInChildren<Camera>(true);_cameraEnabled=new bool[_cameras.Length];
        for(var i=0;i<_cameras.Length;i++)_cameraEnabled[i]=_cameras[i].enabled;
        map.gameObject.SetActive(true);map.RemoveMovementBlocker(map);
        if(!_viewOpen)ShowCamera(false);
    }
    private static void ShowCamera(bool visible)
    {
        if(_map==null)return;
        for(var i=0;i<_cameras.Length;i++)if(_cameras[i]!=null)_cameras[i].enabled=visible&&_cameraEnabled[i];
        _map.WorldCameraController.enabled=visible;
        _map.WorldCameraController.SetAudioListenerEnabled(visible);
        CameraController.Instance.SetAudioListenerEnabled(!visible);
        if(!visible)_map.Townheart.FMODEventEmitter.Stop();
    }
    public static bool Open(WorldMap map)
    {
        Ensure();if(!Owns(map))return true;
        if(_viewOpen||(bool)Changing.GetValue(map))return false;
        Transition(map,()=>
        {
            Commit(true);_viewOpen=true;ShowCamera(true);
            Activate.Invoke(map,null);
        },()=>GameEventDispatcher.Dispatch(GameEventType.TransitionedToMapView));
        return false;
    }
    public static bool Close(WorldMap map,ref bool result)
    {
        Ensure();if(!Owns(map))return true;
        result=false;if(!_viewOpen)return false;
        if((bool)Changing.GetValue(map))return false;
        result=Transition(map,()=>
        {
            // Retain the physics root and all native movement blockers. This is
            // a local camera change, including when the town is still moving.
            Commit(true);_viewOpen=false;ShowCamera(false);
            UIManager.SetState(UIState.Normal);
            CameraController.Instance.Camera.enabled=true;
            CameraController.Instance.UICamera.enabled=true;
            CameraController.Instance.transform.SetPositionAndRotation(Quaternion.Inverse(map.Townheart.transform.rotation)*map.WorldCameraController.RelativePosition,Quaternion.Inverse(map.Townheart.transform.rotation)*map.WorldCameraController.transform.rotation);
            AudioManager.DisableMapAudio((AudioClipProperties)AccessTools.Field(typeof(WorldMap),"_closeAudioClip").GetValue(map));
            GameManager.CursorManager.Deactivate();
            GameEventDispatcher.Dispatch(GameEventType.MapDeactivated);
            Changing.SetValue(map,false);
            FlotsamInputManager.ResetCameraTownheartMovementInputToggle();
        },()=>GameEventDispatcher.Dispatch(GameEventType.TransitionedFromMapView));
        return false;
    }
    private static bool Transition(WorldMap map,UnityAction change,UnityAction completed)
    {
        var color=(Color)AccessTools.Field(typeof(WorldMap),"_fadeColor").GetValue(map);
        var duration=(float)AccessTools.Field(typeof(WorldMap),"_fadeTime").GetValue(map);
        var changing=Fade.InOut(color,duration,change,completed);Changing.SetValue(map,changing);return changing;
    }
    public static void Update()
    {
        Ensure();var network=Network;
        if(_map==null||network?.IsClient!=true||(!network.IsCaptain&&Plugin.Options.mode!="independent")||!_viewOpen)return;
        if(Time.realtimeSinceStartup<_nextKeys)return;_nextKeys=Time.realtimeSinceStartup+.1f;
        var physics=(WorldMapTownheartPhycis2D)Physics.GetValue(_map);
        var keys=FlotsamInputManager.GetMovementInput(physics.RotateLeft,physics.RotateRight,physics.Backward,physics.Forward);
        if(keys.sqrMagnitude>.001f){network.Submit(new GameCommand{kind="navigateKeys",x=keys.x,z=keys.y});_keysSent=true;}
        else if(_keysSent){network.Submit(new GameCommand{kind="navigateStop"});_keysSent=false;}
    }
    public static void Commit(bool force=false)
    {
        if(_map==null||Network?.IsHost!=true||!GameSession.Ready())return;
        if(!force&&Time.realtimeSinceStartup<_nextCommit)return;_nextCommit=Time.realtimeSinceStartup+.25f;
        var world=GameManager.WorldManager.World;var position=_map.Townheart.Position;var rotation=_map.Townheart.transform.rotation;
        var distance=(float)Distance.GetValue(_map);
        if((position-world.TownheartWorldPosition).sqrMagnitude<.0001f&&Quaternion.Angle(rotation,world.TownheartRotation)<.05f)return;
        MovementEvent.DispatchTownheartMove(world.TownheartWorldPosition,position,world.TownheartRotation,rotation,distance);
        Distance.SetValue(_map,0f);
        AccessTools.Field(typeof(WorldMap),"_startPosition").SetValue(_map,position);
        AccessTools.Field(typeof(WorldMap),"_startRotation").SetValue(_map,rotation);
        Community.PlayerCommunity.StopNonInteractableProjects(position,GameSettings.Instance.GameplaySettings.InteractionRadius);
    }
    public static void MouseInput(){_remoteMode=1;_remoteKeys=Vector2.zero;}
    public static void KeyInput(Vector2 keys)
    {
        _remoteMode=2;_remoteKeys=keys;
        if(_map!=null)_map.ClearMouseMovementTarget();
    }
    public static void Stop()
    {
        _remoteMode=0;_remoteKeys=Vector2.zero;
        if(_map!=null&&Network?.IsHost==true)
        {
            var physics=(WorldMapTownheartPhycis2D)Physics.GetValue(_map);
            physics.StopMouseMovement();AccessTools.Field(typeof(WorldMap),"_mouseMovementTargetActive").SetValue(_map,false);
            _map.StopAllMovement();
            if(_map.Townheart.IsMoving){_map.Townheart.OnEndMove();MovementEvent.DispatchStoppedMoving();}
            AccessTools.Method(typeof(WorldMapTownheartPhycis2D),"SetVelocity").Invoke(physics,new object[]{0f,0f});
        }
    }
    public static bool Input(WorldMapTownheartPhycis2D __instance)
    {
        var physics=__instance;
        if(_map==null||Network?.IsHost!=true)return true;
        if(Network.IsCaptain)return _viewOpen;
        if(_remoteMode==2)
        {
            AccessTools.Method(typeof(WorldMapTownheartPhycis2D),"ApplyMovementAndRotation").Invoke(physics,new object[]{_remoteKeys.y*_map.MovementSpeed,-_remoteKeys.x*_map.RotationSpeed});
            AccessTools.PropertySetter(typeof(WorldMapTownheartPhycis2D),nameof(WorldMapTownheartPhycis2D.ProcessedInput)).Invoke(physics,new object[]{true});
        }
        else if(_remoteMode==1&&(bool)AccessTools.Field(typeof(WorldMapTownheartPhycis2D),"_doMouseMovement").GetValue(physics))
        {
            var target=(Vector2)AccessTools.Field(typeof(WorldMapTownheartPhycis2D),"_mouseMovementTarget").GetValue(physics);
            var direction=(float)AccessTools.Field(typeof(WorldMapTownheartPhycis2D),"_mouseMovementDirection").GetValue(physics);
            AccessTools.Method(typeof(WorldMapTownheartPhycis2D),"MoveWithMouse").Invoke(physics,new object[]{target,direction,1f});
        }
        AccessTools.PropertySetter(typeof(WorldMapTownheartPhycis2D),nameof(WorldMapTownheartPhycis2D.Moved)).Invoke(physics,new object[]{_map.MovementSpeed*.5f*.033f<physics.DistanceMoved});
        return false;
    }
}
