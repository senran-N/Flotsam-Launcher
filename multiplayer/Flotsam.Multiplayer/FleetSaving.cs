using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using M4.Session;
using PajamaLlama.Flotsam;
using PajamaLlama.Persistence;
using UnityEngine;

namespace Flotsam.Multiplayer;

// Native disk saves and joining snapshots share global reference tables and a
// persistence lifecycle. Only capture a joining snapshot between disk saves.
internal static class FleetSaving
{
    private static readonly FieldInfo Queued=AccessTools.Field(typeof(SaveTaskBase),"_queued");
    private static readonly FieldInfo LastSave=AccessTools.Field(typeof(SaveTaskBase),"_timeStamp");
    private static readonly Queue<(string name,SaveType type,string peer,PlayerRun run)> Pending=new Queue<(string,SaveType,string,PlayerRun)>();
    private static string _activePeer="";
    private static bool _awaitingClientSave;
    internal static bool Busy=>PersistenceLifeCycle.State!=PersistenceState.None||(bool)Queued.GetValue(null);
    internal static void Install(Harmony h)
    {
        h.Patch(AccessTools.Method(typeof(PlayerRun),nameof(PlayerRun.Save),new[]{typeof(SaveInfo)}),prefix:new HarmonyMethod(typeof(FleetSaving),nameof(Save)));
        // LoadingScreen clears all game-event listeners. Patch the real completion
        // callback so results still arrive after loading or switching worlds.
        h.Patch(AccessTools.Method(typeof(SaveTaskBase),nameof(SaveTaskBase.UnityCompletedCallback)),postfix:new HarmonyMethod(typeof(FleetSaving),nameof(Completed)));
    }
    private static bool Save(SaveInfo saveInfo)
    {
        var net=Plugin.Instance?.Network;
        if(net?.IsClient!=true)return true;
        var manual=saveInfo.Type!=SaveType.Autosave;
        if(manual)_awaitingClientSave=true;
        net.ExecuteOrSubmit(new GameCommand{kind="saveWorld",text=saveInfo.Name,value=(int)saveInfo.Type},manual?"保存请求已发送，等待房主写入完成":null,accepted=>{if(!accepted&&manual)_awaitingClientSave=false;});
        return false;
    }
    internal static string? Request(string name,int type,string peer)
    {
        if(!GameSession.Ready()||Session.Profile?.ActiveRun==null)return "世界尚未载入，稍后重试保存";
        if(string.IsNullOrWhiteSpace(name)||name.Length>100||name.Any(c=>char.IsControl(c)||Path.GetInvalidFileNameChars().Contains(c))||name=="."||name=="..")return "存档名称无效";
        if(type!=(int)SaveType.Manual&&type!=(int)SaveType.Autosave)return "存档类型无效";
        var kind=(SaveType)type;
        if(Pending.Any(p=>p.name==name&&p.type==kind&&p.peer==peer))return null;
        if(Pending.Count>=16)return "保存请求较多，请等待当前保存完成";
        Pending.Enqueue((name,kind,peer,Session.Profile.ActiveRun));return null;
    }
    internal static void Tick()
    {
        var net=Plugin.Instance?.Network;
        if(net?.IsHost!=true||Pending.Count==0||!GameSession.Ready()||Busy)return;
        if(Time.realtimeSinceStartup-(float)LastSave.GetValue(null)<Session.Platform.MinimumSaveInterval)return;
        var next=Pending.Dequeue();_activePeer=next.peer;
        if(!ReferenceEquals(next.run,Session.Profile.ActiveRun)){net.SaveCompleted(false,next.name,"世界已切换，请在当前世界重新保存",_activePeer);_activePeer="";return;}
        try
        {
            Session.Profile.ActiveRun.Save(next.name,next.type);
            if(!(bool)Queued.GetValue(null)){net.SaveCompleted(false,next.name,"原生保存任务未能进入队列",_activePeer);_activePeer="";}
        }
        catch(Exception e){net.SaveCompleted(false,next.name,e.Message,_activePeer);_activePeer="";Plugin.Log(e.ToString());}
    }
    private static void Completed(SaveTaskBase __instance)
    {
        if(__instance is not FlotsamSaveTask||Plugin.Instance?.Network?.IsHost!=true)return;
        var task=__instance;
        if(task.Success)
        {
            try{var directory=Path.GetDirectoryName(task.SaveInfo.Path);if(!string.IsNullOrEmpty(directory)){if(task.SaveInfo.Type==SaveType.Autosave)directory=Path.GetDirectoryName(directory);if(directory!=null)File.WriteAllText(Path.Combine(directory,".flotsam-multiplayer"),Plugin.Options.mode);}}
            catch(Exception error){Plugin.Log("Save marker: "+error.Message);}
        }
        if(!task.Success)Plugin.Log("Save failed: "+task.SaveInfo.Path+"\n"+task.Exception);
        Plugin.Instance.Network.SaveCompleted(task.Success,task.SaveInfo.Name,task.Exception?.Message??"请检查房主的存储空间与写入权限",_activePeer);_activePeer="";
    }
    internal static void ReceiveCompleted(bool success,string peer)
    {
        if(!_awaitingClientSave||peer!=Plugin.Instance?.Network?.LocalPeer)return;
        _awaitingClientSave=false;
        if(success)UnityEngine.Object.FindAnyObjectByType<SaveAsWindow>()?.Disable();
    }
}
