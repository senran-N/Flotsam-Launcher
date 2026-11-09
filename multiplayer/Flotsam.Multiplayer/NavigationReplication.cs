using System;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;
internal static class NavigationReplication
{
    public static void Capture(WorldFrame frame)
    {
        var time=GameManager.TimeManager;frame.days=time.Days.Count;frame.dayTime=time.CurrentDay?.CurrentTime??0;frame.playTime=time.CurrentPlayTime;
        var map=GameManager.WorldMapManager.WorldMap;frame.mapActive=LiveNavigation.ViewOpen;frame.navigationActive=LiveNavigation.Moving;
        frame.mapX=map.Townheart.transform.position.x;frame.mapZ=map.Townheart.transform.position.z;frame.mapRotation=map.Townheart.transform.eulerAngles.y;
    }
    private static void Set(object instance,string property,object value)=>AccessTools.PropertySetter(instance.GetType(),property).Invoke(instance,new[]{value});
    public static void Apply(WorldFrame frame)
    {
        LiveNavigation.ReceiveMovement(frame.navigationActive);
        if(frame.days<1||frame.days>100000||!Replication.Finite(frame.dayTime)||frame.dayTime<0||!Replication.Finite(frame.playTime)||!Enum.IsDefined(typeof(GameSpeed),frame.speed))throw new InvalidOperationException("Invalid world clock");
        var time=GameManager.TimeManager;
        if(time.Days.Count!=frame.days)
        {
            var day=new Day(TimeManager.DayDuration,TimeManager.CycleDuration-TimeManager.DayDuration,24);
            if(frame.days-time.Days.Count>7)throw new InvalidOperationException("World clock requires resync");
            while(time.Days.Count<frame.days)time.Days.Add(day);
            if(time.Days.Count>frame.days)time.Days.RemoveRange(frame.days,time.Days.Count-frame.days);
            Set(time,nameof(time.CurrentDay),time.Days[frame.days-1]);
        }
        if(time.CurrentDay!=null){Set(time.CurrentDay,nameof(Day.CurrentTime),frame.dayTime);Set(time.CurrentDay,nameof(Day.CurrentHour),Mathf.FloorToInt(frame.dayTime/(time.CurrentDay.DayLength/24)));Set(time.CurrentDay,nameof(Day.DayTime),frame.dayTime>time.CurrentDay.DaytimeLength?Day.E_DayTime.Night:Day.E_DayTime.Day);}
        Set(time,nameof(time.CurrentPlayTime),frame.playTime);
        if((int)GameSpeedManager.GameSpeed!=frame.speed)AccessTools.Method(typeof(GameSpeedManager),"SetGameSpeedAndTimeScale").Invoke(null,new object[]{(GameSpeed)frame.speed});
        if(!Replication.Finite(frame.mapX)||!Replication.Finite(frame.mapZ)||!Replication.Finite(frame.mapRotation))throw new InvalidOperationException("Invalid map position");
        var targetMapX=frame.mapX;var targetMapZ=frame.mapZ;var targetMapRot=frame.mapRotation;
        if(Plugin.Options.mode=="independent"&&Community.PlayerCommunity!=null)
        {
            foreach(var ship in frame.ships)
                if(ship!=null&&ship.name==Community.PlayerCommunity.Name&&Replication.Finite(ship.mapX)&&Replication.Finite(ship.mapZ)&&Replication.Finite(ship.mapRotation))
                {targetMapX=ship.mapX;targetMapZ=ship.mapZ;targetMapRot=ship.mapRotation;break;}
        }
        GameManager.WorldMapManager.WorldMap.Townheart.Teleport(new Vector3(targetMapX,0,targetMapZ),Quaternion.Euler(0,targetMapRot,0));
    }
    public static string? Execute(GameCommand command)
    {
        var map=GameManager.WorldMapManager.WorldMap;
        switch(command.kind)
        {
            case "speed":if(!Enum.IsDefined(typeof(GameSpeed),command.value)||command.value<1)return "速度无效";GameSpeedManager.SetGameSpeed((GameSpeed)command.value);return null;
            case "speedZero":GameSpeedManager.ToggleGameSpeedZero();return null;
            case "mapOpen":LiveNavigation.Ensure();return null;
            case "mapClose":LiveNavigation.Stop();map.ClearMouseMovementTarget();map.StopAllMovement();return null;
            case "navigate":
                LiveNavigation.Ensure();
                if(!map.isActiveAndEnabled||map.IsTownMovementBlocked)return "当前船只引擎冷却或电力不足，暂时无法航行";
                if(!Enum.IsDefined(typeof(MouseMovementCursorProperties.Gear),command.value))return "航行方向无效";
                if((map.Townheart.transform.position-new Vector3(command.x,0,command.z)).sqrMagnitude>100000000)return "航行目标过远";
                LiveNavigation.MouseInput();map.SetMouseMovementTarget(new Vector3(command.x,0,command.z),(MouseMovementCursorProperties.Gear)command.value);return null;
            case "navigateKeys":
                LiveNavigation.Ensure();
                if(!map.isActiveAndEnabled||map.IsTownMovementBlocked)return "当前船只引擎冷却或电力不足，暂时无法航行";
                if(Math.Abs(command.x)>1||Math.Abs(command.z)>1)return "航行输入无效";
                LiveNavigation.KeyInput(new Vector2(command.x,command.z));return null;
            case "navigateStop":LiveNavigation.Stop();map.ClearMouseMovementTarget();map.StopAllMovement();return null;
            default:return "未知航行指令";
        }
    }
}
