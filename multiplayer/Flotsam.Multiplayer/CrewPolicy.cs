using System;
using System.Collections.Generic;
using System.Linq;

namespace Flotsam.Multiplayer;

// Authority stays with the host even when another player steers the shared town.
public sealed class CrewPolicy
{
    private readonly Dictionary<string,string> _names=new Dictionary<string,string>(StringComparer.Ordinal);
    public string Host {get;}
    public string Captain {get;private set;}
    public CrewPolicy(string host,string name){Host=host;Captain=host;_names.Add(host,name);}
    public void Join(string peer,string name)=>_names[peer]=name;
    public bool Leave(string peer){if(peer==Host)return false;_names.Remove(peer);if(Captain!=peer)return false;Captain=Host;return true;}
    public CrewMember[] Members()=>_names.Select(e=>new CrewMember{id=e.Key,name=e.Value,host=e.Key==Host}).OrderByDescending(e=>e.host).ThenBy(e=>e.id,StringComparer.Ordinal).ToArray();
    public static bool CaptainOnly(string kind)=>kind=="speed"||kind=="speedZero"||kind=="navigate"||kind=="navigateKeys"||kind=="navigateStop"||kind=="mapOpen"||kind=="mapClose";
    public string? Authorize(string peer,string kind)=>!_names.ContainsKey(peer)?"玩家已离线":CaptainOnly(kind)&&peer!=Captain?"只有船长可以控制航行和全局速度":null;
    public string? Transfer(string peer,string next)
    {
        if(peer!=Host&&peer!=Captain)return "只有房主或船长可以交接船长";
        if(!_names.ContainsKey(next))return "目标玩家已离线";
        Captain=next;return null;
    }
}
