using System;
using System.Linq;
using HarmonyLib;
using PajamaLlama.Fltsm;

namespace Flotsam.Multiplayer;

internal static class AgentReplication
{
    public static void Capture(Agent agent,AgentState state)
    {
        state.descriptorId=agent.Descriptor.UniqueID;
        state.descriptor=Convert.ToBase64String(JsonCodec.EncodeRuntime(agent.Descriptor.GetPersistentData()));
        state.hunger=agent.Vitals.Hunger.Amount;state.thirst=agent.Vitals.Thirst.Amount;state.pollution=agent.Vitals.Pollution.Level;
        state.assignments=agent.Assignments.Select(a=>new AssignmentState{type=(int)a.Type,priority=(int)a.Priority,enabled=a.Enabled}).ToArray();
    }
    public static Agent Spawn(AgentState state,Community owner)
    {
        if(state.descriptorId<0||state.descriptorId>ushort.MaxValue||state.descriptor.Length>32768)throw new InvalidOperationException("Invalid drifter descriptor");
        if(!ActorDescriptor.TryGet<AgentDescriptor>(out var descriptor,(ushort)state.descriptorId))
        {
            var data=(ActorDescriptor.PersistentDataBase)JsonCodec.DecodeRuntime(AccessTools.Inner(typeof(AgentDescriptor),"PersistentData"),Convert.FromBase64String(state.descriptor));
            descriptor=(AgentDescriptor)data.Restore();
        }
        return descriptor.Spawn<Agent>(owner??Community.PlayerCommunity,new UnityEngine.Vector3(state.x,state.y,state.z));
    }
    public static void Apply(Agent agent,AgentState state)
    {
        if(state.hunger<0||state.hunger>agent.Vitals.Hunger.Limit+1||state.thirst<0||state.thirst>agent.Vitals.Thirst.Limit+1||!Replication.Finite(state.pollution)||state.pollution<0||state.assignments.Length>128)throw new InvalidOperationException("Invalid drifter state");
        if(agent.Name!=state.name)agent.SetName(state.name);
        AccessTools.PropertySetter(typeof(DietVital),nameof(DietVital.Amount)).Invoke(agent.Vitals.Hunger,new object[]{state.hunger});
        AccessTools.PropertySetter(typeof(DietVital),nameof(DietVital.Amount)).Invoke(agent.Vitals.Thirst,new object[]{state.thirst});
        AccessTools.PropertySetter(typeof(Pollution),nameof(Pollution.Level)).Invoke(agent.Vitals.Pollution,new object[]{state.pollution});
        foreach(var entry in state.assignments)
        {
            if(!Enum.IsDefined(typeof(AssignmentType),entry.type)||!Enum.IsDefined(typeof(AssignmentPriority),entry.priority))throw new InvalidOperationException("Invalid assignment state");
            var assignment=agent.Assignments.FirstOrDefault(a=>(int)a.Type==entry.type);if(assignment==null)continue;
            if(assignment.Enabled!=entry.enabled||((int)assignment.Priority)!=entry.priority)agent.SetAssignmentEnabled((AssignmentType)entry.type,entry.enabled,(AssignmentPriority)entry.priority);
        }
    }
}
