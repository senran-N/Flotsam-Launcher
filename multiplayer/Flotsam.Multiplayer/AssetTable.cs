using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Flotsam.Multiplayer;
internal static class AssetTable
{
    private static List<string> Current()
    {
        var data=(PersistentPropertiesData)AccessTools.Field(typeof(PersistenceManager),"_persistentProperties").GetValue(GameManager.PersistenceManager);
        return (List<string>)AccessTools.Field(typeof(PersistentPropertiesData),"_persistentPropertiesGuids").GetValue(data);
    }
    public static string[] Capture()=>Current().ToArray();
    public static void Apply(string[] entries)
    {
        if(entries.Length>20000)throw new InvalidOperationException("Asset table exceeds limit");
        foreach(var key in entries)if(string.IsNullOrEmpty(key)||key.Length>128)throw new InvalidOperationException("Invalid asset identity");
        var current=Current();var same=current.Count==entries.Length;
        if(same)for(var i=0;i<entries.Length;i++)if(current[i]!=entries[i]){same=false;break;}
        if(!same){current.Clear();current.AddRange(entries);}
    }
}
