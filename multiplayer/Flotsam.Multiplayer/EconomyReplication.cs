using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace Flotsam.Multiplayer;

// Display state uses backing fields so applying a frame cannot queue work or spend materials.
internal sealed class EconomyReplication
{
    private readonly Dictionary<QueuedRecipe,long> _queueIds=new Dictionary<QueuedRecipe,long>();
    private readonly Dictionary<string,TechTreeNode> _tech=new Dictionary<string,TechTreeNode>(StringComparer.Ordinal);
    private long _nextQueueId;

    public void Reset(){_queueIds.Clear();_tech.Clear();}
    private void Catalog()
    {
        if(_tech.Count!=0)return;
        foreach(var node in UnityEngine.Resources.FindObjectsOfTypeAll<TechTreeNode>())
            if(!string.IsNullOrEmpty(node.Guid)&&!_tech.ContainsKey(node.Guid))_tech.Add(node.Guid,node);
    }
    public ProducerState? Capture(Buildable building)
    {
        if(!building.TryGetComponent<Producer>(out var producer))return null;
        var queue=new List<QueueState>();
        foreach(var item in producer.QueuedRecipes)
        {
            if(!_queueIds.TryGetValue(item,out var id)){id=++_nextQueueId;_queueIds.Add(item,id);}
            queue.Add(new QueueState{id=id,recipe=item.Recipe.Index,stage=(int)item.RecipeStage,progress=item.Progress});
        }
        return new ProducerState{selected=producer.SelectedRecipeIndex,priority=producer.Priority.Score,threshold=producer.QueueContinuousRecipeThreshold,
            recipes=producer.Recipes.Select(r=>new RecipeState{index=r.Index,amount=r.AmountToProduce,prioritized=r.IsPrioritized,properties=r.Properties.name}).ToArray(),queue=queue.ToArray()};
    }
    public void Prune(IEnumerable<Buildable> buildings)
    {
        var active=new HashSet<QueuedRecipe>();
        foreach(var building in buildings)if(building!=null&&building.TryGetComponent<Producer>(out var producer))foreach(var entry in producer.QueuedRecipes)active.Add(entry);
        foreach(var entry in _queueIds.Keys.ToArray())if(!active.Contains(entry))_queueIds.Remove(entry);
    }
    public ResearchState CaptureResearch(Community? owner=null)
    {
        Catalog();var research=(owner??Community.PlayerCommunity)?.Research;
        if(research==null)return new ResearchState();
        return new ResearchState{current=research.CurrentResearch?.Guid??"",progresses=research.ResearchProgresses.Select(r=>new ResearchProgress{guid=r.Guid,progress=r.Progress}).ToArray()};
    }
    public string[] UnlockedTech(){Catalog();return _tech.Values.Where(n=>n.IsUnlocked()).Select(n=>n.Guid).OrderBy(s=>s,StringComparer.Ordinal).ToArray();}
    public string[] ResearchableTech(){Catalog();return _tech.Values.Where(n=>n.IsResearchable()).Select(n=>n.Guid).OrderBy(s=>s,StringComparer.Ordinal).ToArray();}
    private static void Set(object instance,string property,object? value)=>AccessTools.Field(instance.GetType(),"<"+property+">k__BackingField").SetValue(instance,value);
    public void Apply(Buildable building,ProducerState? state)
    {
        if(state==null)return;
        if(!building.TryGetComponent<Producer>(out var producer)||state.recipes.Length!=producer.Recipes.Count||state.queue.Length>512)throw new InvalidOperationException("Producer catalog mismatch");
        if(state.selected< -1||state.selected>=producer.Recipes.Count)throw new InvalidOperationException("Invalid selected recipe");
        var indices=new HashSet<int>();
        foreach(var entry in state.recipes)
        {
            if(entry.index<0||entry.index>=producer.Recipes.Count||!indices.Add(entry.index)||entry.amount< -1||entry.amount>99)throw new InvalidOperationException("Invalid recipe state");
            var recipe=producer.Recipes[entry.index];if(recipe.Properties.name!=entry.properties)throw new InvalidOperationException("Recipe properties mismatch");
            Set(recipe,nameof(recipe.AmountToProduce),entry.amount);Set(recipe,nameof(recipe.IsPrioritized),entry.prioritized);
        }
        Set(producer,nameof(producer.SelectedRecipeIndex),state.selected);
        var priorities=GameSettings.Instance.ProjectSettings.ProducerProjectPriorities;
        if(!priorities.Any(p=>p.Score==state.priority))throw new InvalidOperationException("Invalid producer priority");
        Set(producer,nameof(producer.Priority),priorities.First(p=>p.Score==state.priority));
        if(state.threshold< -1||state.threshold>producer.MaximumQueuedRecipes)throw new InvalidOperationException("Invalid queue threshold");
        AccessTools.Field(typeof(Producer),"_queueContinuousRecipeThreshold").SetValue(producer,state.threshold);
        var rebuilt=new List<QueuedRecipe>();var ids=new HashSet<long>();
        foreach(var entry in state.queue)
        {
            if(entry.id<=0||!ids.Add(entry.id)||entry.recipe<0||entry.recipe>=producer.Recipes.Count||!Enum.IsDefined(typeof(QueuedRecipe.Stage),entry.stage)||!Replication.Finite(entry.progress)||entry.progress<0)throw new InvalidOperationException("Invalid production queue");
            var item=producer.QueuedRecipes.FirstOrDefault(q=>_queueIds.TryGetValue(q,out var id)&&id==entry.id);
            if(item!=null&&item.Recipe.Index!=entry.recipe)item.Initialize(producer.Recipes[entry.recipe]);
            item=item??QueuedRecipe.Get(producer.Recipes[entry.recipe]);item.Progress=entry.progress;item.RecipeStage=(QueuedRecipe.Stage)entry.stage;
            rebuilt.Add(item);_queueIds[item]=entry.id;
        }
        foreach(var item in producer.QueuedRecipes)if(!rebuilt.Contains(item)){_queueIds.Remove(item);item.Release();}
        producer.QueuedRecipes.Clear();producer.QueuedRecipes.AddRange(rebuilt);
        AccessTools.Field(typeof(Producer),"_queuedUpdated").SetValue(producer,true);
        AccessTools.Method(typeof(Producer),"LateUpdate")?.Invoke(producer,null);
    }
    public void ApplyResearch(ResearchState state,string[] unlocked,Community? owner=null)
    {
        Catalog();if(state.progresses.Length>2048||unlocked.Length>2048)throw new InvalidOperationException("Research state exceeds limit");
        foreach(var key in unlocked)if(_tech.TryGetValue(key,out var node)&&!node.IsUnlocked())node.Unlock();
        var research=(owner??Community.PlayerCommunity)?.Research;
        if(research==null)return;
        var progresses=new List<CommunityResearch.Research>();var ids=new HashSet<string>();
        foreach(var progress in state.progresses)
        {
            if(!_tech.TryGetValue(progress.guid,out var node)||!ids.Add(progress.guid)||progress.progress<0||progress.progress>node.Cost)throw new InvalidOperationException("Invalid research progress");
            progresses.Add(new CommunityResearch.Research(node,progress.progress));
        }
        var previousResearch=research.CurrentResearch;var previous=previousResearch?.Guid??"";
        research.ResearchProgresses.Clear();research.ResearchProgresses.AddRange(progresses);
        var current=progresses.FirstOrDefault(r=>r.Guid==state.current);
        if(state.current.Length!=0&&current==null)throw new InvalidOperationException("Missing current research");
        Set(research,nameof(research.CurrentResearch),current);
        if(ReferenceEquals(owner??Community.PlayerCommunity,Community.PlayerCommunity))
        {
            if(current!=null)new ResearchEvent(previous==state.current?GameEventType.ResearchProgressPointsUpdated:GameEventType.ResearchStarted,current).Dispatch();
            else if(previousResearch!=null)new ResearchEvent(GameEventType.ResearchCancelled,previousResearch).Dispatch();
        }
    }
    public string? ExecuteGlobal(GameCommand command,Community? owner=null)
    {
        Catalog();var research=(owner??Community.PlayerCommunity)?.Research;
        if(research==null)return "当前船只研究组件未就绪";
        if(command.kind=="researchStart")
        {
            if(!_tech.TryGetValue(command.text,out var node)||!node.IsResearchable())return "研究前置条件不满足";
            research.StartResearch(node);return null;
        }
        if(command.kind=="researchCancel"){if(research.CurrentResearch!=null)research.CancelResearch();return null;}
        return "未知研究指令";
    }
    public string? Execute(Buildable building,GameCommand command)
    {
        if(!building.TryGetComponent<Producer>(out var producer))return "目标不是生产建筑";
        if(building.BuildPhase!=BuildPhase.Finished)return "建筑尚未建成";
        if(command.kind=="queueCancel")
        {
            var index=producer.QueuedRecipes.FindIndex(item=>_queueIds.TryGetValue(item,out var id)&&id==command.reference);
            if(index<0)return "该生产任务已结束";producer.CancelRecipe(index);return null;
        }
        if(command.kind=="producerPriority")
        {
            if(command.value==1)producer.IncreasePriority();else if(command.value== -1)producer.DecreasePriority();else return "优先级参数无效";return null;
        }
        if(command.value<0||command.value>=producer.Recipes.Count)return "配方不存在";
        var recipe=producer.Recipes[command.value];if(!recipe.IsUnlocked())return "配方尚未解锁";
        if(command.amount<0||command.amount>99)return "生产数量无效";
        switch(command.kind)
        {
            case "recipeSelect":producer.SetSelectedRecipe(command.value);return null;
            case "recipeIncrease":
                producer.SetSelectedRecipe(command.value);
                for(var i=0;i<command.amount;i++)producer.IncreaseSelectedRecipeAmountToProduce();return null;
            case "recipeDecrease":recipe.DecreaseAmountToProduce(command.amount);return null;
            case "recipeAmount":recipe.SetAmountToProduce(command.amount);return null;
            case "recipeContinuous":producer.SetSelectedRecipe(command.value);producer.ToggleSelecteRecipeContinuous();return null;
            case "recipeDisableContinuous":recipe.DisableContinuous();return null;
            default:return "未知生产指令";
        }
    }
    public long QueueId(Producer producer,int index)=>index>=0&&index<producer.QueuedRecipes.Count&&_queueIds.TryGetValue(producer.QueuedRecipes[index],out var id)?id:0;
    public void Released(QueuedRecipe item)=>_queueIds.Remove(item);
}
