using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Flotsam.Multiplayer;

/// <summary>
/// Recipe-material structural durability system for all buildings:
/// - Computes MaxHP &amp; armor reduction from actual <see cref="BuildableProperties.RequiredResources"/>
/// - Never regresses finished buildings into construction scaffolding (<see cref="Buildable.Health"/> stays 1.0)
/// - Activates native <see cref="BuildablePanel"/> <c>_healthSlider</c> &amp; <c>_healthText</c>
/// - Below 60% HP: triggers malfunction &amp; <see cref="Buildable.Deactivate"/> + dark damage smoke
/// - Crew repair: consumes matching building materials and dispatches an idle Drifter to hammer-repair
/// - At 0% HP: non-Townheart buildings collapse, safely dropping crew into water and spawning 50% material debris as ocean Flotsam
/// - Townheart clamps at 15% HP floor ("核心漏水瘫痪", reducing ship navigation speed to 20%)
/// </summary>
internal static class BuildingDurability
{
    private sealed class Record
    {
        public float Hp;
        public float MaxHp;
        public float Armor;
        public float EmpUntil;
        public Agent? RepairWorker;
        public float RepairUntil;
        public float RepairRemaining;
        public int RestoreWorker=-1;
        public float NextHammerAt;
        public GameObject? DamageFx;
        public PlaceableAlertProperties? AlertApplied;
    }

    private static readonly Dictionary<int, Record> Records = new Dictionary<int, Record>();
    private static readonly MethodInfo? AddMalfunctionMethod = AccessTools.Method(typeof(Buildable), "AddMalfunction");
    private static readonly MethodInfo? RemoveMalfunctionMethod = AccessTools.Method(typeof(Buildable), "RemoveMalfunction");
    private static PlaceableAlertProperties? _cachedAlert;
    internal static bool IsRepairWorker(Agent a)=>Records.Values.Any(r=>r.RepairWorker==a&&r.RepairUntil>Time.realtimeSinceStartup);
    internal static void Tick(float dt)
    {
        if(Plugin.Instance?.Network?.IsHost!=true||!GameSession.Ready())return;
        var now=GameManager.TimeManager?.CurrentPlayTime??0;
        foreach(var owner in Community.Communities)foreach(var b in owner.Buildables.ToArray())if(b!=null&&Records.TryGetValue(b.GetInstanceID(),out var r))
        {
            if(r.RestoreWorker>=0){r.RepairWorker=owner.Agents.FirstOrDefault(a=>a!=null&&a.Descriptor.UniqueID==r.RestoreWorker);r.RestoreWorker=-1;}
            if(r.RepairRemaining>0){r.RepairRemaining=Mathf.Max(0,r.RepairRemaining-dt);r.RepairUntil=Time.realtimeSinceStartup+r.RepairRemaining;}
            TickHostBuilding(b,r,now);
        }
    }

    internal static void Reset()
    {
        foreach (var rec in Records.Values)
        {
            if (rec.DamageFx != null)
                UnityEngine.Object.Destroy(rec.DamageFx);
        }
        Records.Clear();
    }

    internal static bool IsDisabled(Buildable b)
    {
        var r=GetOrCreate(b);
        return FleetMechanicsMath.IsCrippledMalfunction(r.Hp,r.MaxHp)||r.EmpUntil>(GameManager.TimeManager?.CurrentPlayTime??0);
    }
    internal static float[] Save(Buildable b)
    {
        var r=GetOrCreate(b);
        return new[]{r.Hp,r.MaxHp,r.EmpUntil-(GameManager.TimeManager?.CurrentPlayTime??0),r.RepairRemaining,r.RepairWorker!=null?r.RepairWorker.Descriptor.UniqueID:-1};
    }
    internal static void Restore(Buildable b,float[] values)
    {
        if(values==null||values.Length<3)return;
        var r=GetOrCreate(b);r.Hp=Mathf.Clamp(values[0],0,r.MaxHp);r.EmpUntil=(GameManager.TimeManager?.CurrentPlayTime??0)+Mathf.Max(0,values[2]);
        if(values.Length>=5&&values[3]>0){r.RepairRemaining=values[3];r.RepairUntil=Time.realtimeSinceStartup+r.RepairRemaining;r.RestoreWorker=(int)values[4];}
    }
    internal static bool Handles(string kind) => kind == "buildingRepair";

    internal static bool IsTownheart(Buildable? b) =>
        b != null && (b.TryGetComponent<TownheartStorage>(out _) || b.TryGetComponent<Engine>(out _));

    private static Record GetOrCreate(Buildable b)
    {
        var key = b.GetInstanceID();
        if (Records.TryGetValue(key, out var existing) && existing.MaxHp > 1f)
            return existing;

        ComputeMaterialProfile(b, out var maxHp, out var armor);
        var created = new Record
        {
            Hp = maxHp,
            MaxHp = maxHp,
            Armor = armor
        };
        Records[key] = created;
        return created;
    }

    internal static void ComputeMaterialProfile(Buildable b, out float maxHp, out float armor)
    {
        var isHeart = IsTownheart(b);
        int t1 = 0, t2 = 0, t3 = 0;
        var reqs = b.Properties?.RequiredResources;
        if (reqs != null)
        {
            foreach (var r in reqs)
            {
                if (r?.ItemProperties == null || r.Amount <= 0) continue;
                var tier = FleetMechanicsMath.ClassifyMaterialTier(r.ItemProperties.name);
                if (tier == 3) t3 += r.Amount;
                else if (tier == 2) t2 += r.Amount;
                else t1 += r.Amount;
            }
        }
        maxHp = FleetMechanicsMath.ComputeMaxStructuralHp(isHeart, t1, t2, t3);
        armor = FleetMechanicsMath.ComputeArmorReduction(t3, t1 + t2 + t3);
    }

    internal static float GetArmorFor(Buildable? b)
    {
        if (b == null) return 0f;
        return GetOrCreate(b).Armor;
    }

    internal static bool IsShipHeartCrippled(Community? owner)
    {
        if (owner == null) return false;
        var heart = FleetEngineBundle.HeartBuildable(owner);
        if (heart == null && ReferenceEquals(owner, Community.PlayerCommunity))
            heart = Construction.Townheart?.Buildable;
        if (heart == null) return false;
        var rec = GetOrCreate(heart);
        return FleetMechanicsMath.IsTownheartLeakParalyzed(rec.Hp, rec.MaxHp);
    }

    internal static bool TryGetDurability(
        Buildable? b,
        out float hp,
        out float maxHp,
        out float empRemaining,
        out bool underRepair)
    {
        hp = 0f;
        maxHp = 0f;
        empRemaining = 0f;
        underRepair = false;
        if (b == null || b.BuildPhase != BuildPhase.Finished) return false;
        var rec = GetOrCreate(b);
        var nowPlay = GameManager.TimeManager != null ? GameManager.TimeManager.CurrentPlayTime : 0f;
        hp = rec.Hp;
        maxHp = rec.MaxHp;
        empRemaining = Mathf.Max(0f, rec.EmpUntil - nowPlay);
        underRepair = rec.RepairUntil > Time.realtimeSinceStartup;
        return maxHp > 0f;
    }

    internal static void CaptureBuilding(Buildable b, BuildingState state, float nowPlayTime)
    {
        var rec = GetOrCreate(b);
        state.structuralHp = rec.Hp;
        state.maxStructuralHp = rec.MaxHp;
        state.empUntil = rec.EmpUntil > nowPlayTime ? rec.EmpUntil : 0f;
        state.underRepair = rec.RepairUntil > Time.realtimeSinceStartup;
        UpdateDamageFx(b, rec, nowPlayTime);
    }

    internal static void ApplyBuildingClient(Buildable b, BuildingState state, float nowPlayTime)
    {
        var rec = GetOrCreate(b);
        if (state.maxStructuralHp > 1f)
        {
            rec.MaxHp = state.maxStructuralHp;
            rec.Hp = state.structuralHp > 0f ? Mathf.Clamp(state.structuralHp, 0f, state.maxStructuralHp) : state.maxStructuralHp;
        }
        rec.EmpUntil = state.empUntil;
        rec.RepairUntil = state.underRepair ? Time.realtimeSinceStartup + 0.35f : 0f;
        UpdateDamageFx(b, rec, nowPlayTime);
    }

    private static void TickHostBuilding(Buildable b, Record rec, float nowPlayTime)
    {
        var nowReal = Time.realtimeSinceStartup;

        // Complete ongoing crew repair
        if (rec.RepairUntil > 0f)
        {
            if (rec.RepairRemaining > 0)
            {
                if (rec.RepairWorker != null && rec.RepairWorker.IsAlive)
                {
                    rec.RepairWorker.UpdateActivity(Activity.Building);
                    if (nowReal >= rec.NextHammerAt)
                    {
                        rec.NextHammerAt = nowReal + 0.85f;
                        FleetPhysicalActors.PlayFleetSound("cargo_deliver", b.transform.position, 0.45f);
                    }
                }
            }
            else
            {
                rec.RepairUntil = 0f;
                if(rec.RepairWorker==null||!rec.RepairWorker.IsAlive){rec.RepairWorker=null;return;}
                rec.Hp = rec.MaxHp;
                rec.EmpUntil = 0f;
                ClearMalfunctionAlert(b, rec);
                if (!b.IsActive) b.Activate();
                if (rec.RepairWorker != null && rec.RepairWorker.IsAlive)
                {
                    rec.RepairWorker.UpdateActivity(Activity.Idling);
                    rec.RepairWorker.TryGoToTown();
                }
                rec.RepairWorker = null;
                FleetPhysicalActors.SpawnNativePopup(b.transform.position + Vector3.up * 2.5f, $"{b.Name} 抢修完毕 · 恢复运转");
                FleetPhysicalActors.PlayFleetSound("cargo_deliver", b.transform.position, 0.75f);
            }
        }

        // Enforce malfunction/EMP shutdown while crippled or EMP'd (non-Townheart)
        var isHeart = IsTownheart(b);
        var crippled = FleetMechanicsMath.IsCrippledMalfunction(rec.Hp, rec.MaxHp);
        var empActive = rec.EmpUntil > nowPlayTime;
        if ((crippled || empActive) && b.BuildPhase == BuildPhase.Finished)
        {
            if (!isHeart && b.IsActive)
                b.Deactivate();
            ApplyMalfunctionAlert(b, rec);
        }
        else if (!crippled && !empActive && rec.AlertApplied != null)
        {
            ClearMalfunctionAlert(b, rec);
            if (!b.IsActive && b.BuildPhase == BuildPhase.Finished && !MoveConstructionCommands.IsStored(b))
                b.Activate();
        }
    }

    internal static void ApplyDamage(Buildable target, float rawDamage, bool applyEmp, Vector3 hitWorldPos)
    {
        if (target == null || MoveConstructionCommands.IsStored(target)) return;
        var rec = GetOrCreate(target);
        var isHeart = IsTownheart(target);
        var nowPlay = GameManager.TimeManager != null ? GameManager.TimeManager.CurrentPlayTime : 0f;

        rec.Hp = FleetMechanicsMath.ApplyDamageWithFloor(rec.Hp, rec.MaxHp, rawDamage, isHeart);

        if (applyEmp)
        {
            rec.EmpUntil = Mathf.Max(rec.EmpUntil, nowPlay + 14f);
            var heartEngine = FleetEngineBundle.HeartBuildable(target.Community)?.GetComponent<Engine>();
            heartEngine?.EnergyGrid?.RequestStorageEnergy(180f);
            FleetPhysicalActors.SpawnWaterShockwaveRing(hitWorldPos, new Color(0.28f, 0.88f, 1f, 0.95f), 6.2f);
            FleetPhysicalActors.SpawnNativePopup(hitWorldPos + Vector3.up * 3.1f, $"电鳗EMP过载！瘫痪 14s · 电网 -180");
        }

        if (!isHeart && rec.Hp <= 0.05f)
        {
            CollapseBuildingIntoFlotsam(target, hitWorldPos, rec);
            return;
        }

        if (FleetMechanicsMath.IsCrippledMalfunction(rec.Hp, rec.MaxHp) || rec.EmpUntil > nowPlay)
        {
            if (!isHeart && target.IsActive)
                target.Deactivate();
            ApplyMalfunctionAlert(target, rec);
            if (isHeart && FleetMechanicsMath.IsTownheartLeakParalyzed(rec.Hp, rec.MaxHp))
                FleetPhysicalActors.SpawnNativePopup(hitWorldPos + Vector3.up * 3.2f, "镇心重创漏水！引擎航速降至 20%，请立即抢修！");
            else
                FleetPhysicalActors.SpawnNativePopup(hitWorldPos + Vector3.up * 2.8f, $"{target.Name} 结构重创停机 ({Mathf.CeilToInt(rec.Hp)}/{Mathf.CeilToInt(rec.MaxHp)})");
        }
        else
        {
            FleetPhysicalActors.SpawnNativePopup(
                hitWorldPos + Vector3.up * 2.6f,
                $"{target.Name} 耐久 -{Mathf.RoundToInt(rawDamage)} ({Mathf.CeilToInt(rec.Hp)}/{Mathf.CeilToInt(rec.MaxHp)})");
        }

        UpdateDamageFx(target, rec, nowPlay);
    }

    private static void CollapseBuildingIntoFlotsam(Buildable target, Vector3 hitPos, Record rec)
    {
        var owner = target.Community;
        var bName = string.IsNullOrWhiteSpace(target.Name) ? (target.Properties?.name ?? "建筑") : target.Name;

        // Avoid breaking ship graph connectivity if removing this building would orphan neighbours:
        // check if any neighbour relies solely on this construction to reach Townheart.
        if (target.TryGetComponent<Construction>(out var construction) && owner != null)
        {
            using var _scope = FleetRuntime.WithPlayerCommunity(owner);
            var wouldSplit = construction.NeighbourConstructions.Any(n => n != null && !n.IsConnectedToTownheart(construction));
            if (wouldSplit)
            {
                // Clamp at 1 HP crippled hulk so outer ship wings aren't orphaned from native graph
                rec.Hp = 1f;
                if (target.IsActive) target.Deactivate();
                ApplyMalfunctionAlert(target, rec);
                FleetPhysicalActors.SpawnNativePopup(hitPos + Vector3.up * 2.8f, $"{bName} 濒临解体(承重连接保留 1 HP，已彻底瘫痪)");
                return;
            }
        }

        // Safely evacuate any Drifters standing on or working inside this building into the water
        if (owner?.Agents != null)
        {
            foreach (var agent in owner.Agents.ToArray())
            {
                if (agent == null || !agent.IsAlive) continue;
                var dSq = (agent.transform.position - target.transform.position).sqrMagnitude;
                if (dSq <= 25f)
                {
                    FleetPhysicalActors.SpawnNativeSplash(agent.transform.position);
                    agent.ResetToTown(false);
                    agent.TryGoToTown();
                }
            }
        }

        // Spawn 50% of the building's recipe materials into the surrounding ocean as salvageable Flotsam
        var drops = new List<(ItemProperties Prop, int Amount)>();
        var reqs = target.Properties?.RequiredResources;
        if (reqs != null)
        {
            foreach (var r in reqs)
            {
                if (r?.ItemProperties == null) continue;
                var salvageAmt = FleetMechanicsMath.ComputeCollapseSalvageCount(r.Amount);
                if (salvageAmt > 0)
                    drops.Add((r.ItemProperties, salvageAmt));
            }
        }

        if(target.Inventory!=null)
        {
            foreach(var item in target.Inventory.SubInventoryTypes.SelectMany(section=>target.Inventory.ReturnAllItems(section,new List<Item>())).ToArray())
            {
                if(item?.Properties==null)continue;
                if(item.IsReserved)item.CancelReservation();
                drops.Add((item.Properties,1));target.Inventory.TakeItem(item);
            }
        }
        if (drops.Count > 0)
        {
            var scatterPos = new Vector3(hitPos.x + UnityEngine.Random.Range(-3.5f, 3.5f), 0f, hitPos.z + UnityEngine.Random.Range(-3.5f, 3.5f));
            FleetVoyages.Drop(scatterPos,drops.SelectMany(d=>Enumerable.Repeat(d.Prop,d.Amount)));
        }

        if (rec.DamageFx != null)
            UnityEngine.Object.Destroy(rec.DamageFx);
        Records.Remove(target.GetInstanceID());

        FleetPhysicalActors.SpawnWaterShockwaveRing(hitPos, new Color(1f, 0.36f, 0.14f, 0.95f), 6.5f);
        FleetPhysicalActors.PlayFleetSound("shell_impact", hitPos, 1.0f);
        FleetPhysicalActors.SpawnNativePopup(hitPos + Vector3.up * 3.0f, $"💥 {bName} 被轰塌解体！半数建材散落海面");

        if (target.IsActive) target.Deactivate();
        target.Remove();
    }

    internal static string? ExecuteRepair(GameCommand command, IReadOnlyDictionary<long, Buildable> buildings, Community actor)
    {
        if (!buildings.TryGetValue(command.target, out var building) || building == null)
            return "目标建筑已不存在";
        if (!ReferenceEquals(building.Community, actor))
            return "只能抢修本船所属建筑";
        if (building.BuildPhase != BuildPhase.Finished || MoveConstructionCommands.IsStored(building))
            return "该建筑当前无需抢修";

        var rec = GetOrCreate(building);
        var nowPlay = GameManager.TimeManager != null ? GameManager.TimeManager.CurrentPlayTime : 0f;
        if (rec.RepairUntil > Time.realtimeSinceStartup)
            return "船员正在抢修此建筑";
        if (rec.Hp >= rec.MaxHp - 0.5f && rec.EmpUntil <= nowPlay)
            return "建筑结构完好，无需维修";

        // Pick an awake Drifter on this ship to perform the repair
        var worker = actor.Agents?
            .Where(a => a != null && a.IsAlive && a.CurrentActivity != Activity.Sleeping && !FleetInteractions.IsAgentBusy(a))
            .OrderBy(a => (a.transform.position - building.transform.position).sqrMagnitude)
            .FirstOrDefault();
        if (worker == null)
            return "本船暂无空闲清醒船员可执行抢修";

        var reqs = building.Properties?.RequiredResources;
        var recipeTotal = reqs != null ? reqs.Sum(r => r?.Amount ?? 0) : 6;
        var neededCount = Mathf.Max(1, FleetMechanicsMath.ComputeRepairCostCount(rec.Hp, rec.MaxHp, recipeTotal));

        // Prefer consuming matching recipe materials; fall back to Wood or Plastic
        var preferredNames = reqs != null
            ? reqs.Where(r => r?.ItemProperties != null).Select(r => r.ItemProperties.name).ToList()
            : new List<string>();
        var consumed = FleetInteractions.ExtractRepairMaterials(actor, preferredNames, neededCount);
        if (consumed.Count < neededCount)
        {
            FleetVoyages.ReturnCargo(actor,consumed);
            return "本船仓库缺少抢修所需的木材、塑料或对应建材";
        }
        worker.Assignment?.Stop(ProjectFlags.Cancelled);
        worker.ReturnNavigator(true)?.StopNavigation(ProjectFlags.Cancelled);

        // Walk/position worker beside the damaged building and start hammering for 3.2s
        var standOffset = (worker.transform.position - building.transform.position);
        standOffset.y = 0f;
        var standDir = standOffset.sqrMagnitude > 0.1f ? standOffset.normalized : Vector3.forward;
        var repairSpot = building.transform.position + standDir * 1.4f;
        repairSpot.y = building.transform.position.y;
        worker.transform.position = repairSpot;
        worker.transform.rotation = Quaternion.LookRotation(-standDir, Vector3.up);
        worker.UpdateActivity(Activity.Building);

        rec.RepairWorker = worker;
        rec.RepairUntil = Time.realtimeSinceStartup + 3.2f;
        rec.RepairRemaining=3.2f;
        rec.NextHammerAt = Time.realtimeSinceStartup + 0.3f;

        var matLabel = Replication.CleanItemName(consumed[0]);
        FleetPhysicalActors.SpawnNativePopup(
            building.transform.position + Vector3.up * 2.5f,
            $"派出 {worker.Name} 抢修 (消耗 {matLabel} ×{consumed.Count})");
        FleetPhysicalActors.PlayFleetSound("cargo_deliver", building.transform.position, 0.65f);
        return null;
    }

    private static void ApplyMalfunctionAlert(Buildable b, Record rec)
    {
        if (rec.AlertApplied != null || AddMalfunctionMethod == null) return;
        if (_cachedAlert == null)
        {
            _cachedAlert = Resources.FindObjectsOfTypeAll<PlaceableAlertProperties>()
                .FirstOrDefault(a => a != null);
        }
        if (_cachedAlert == null) return;
        try
        {
            AddMalfunctionMethod.Invoke(b, new object[] { _cachedAlert });
            rec.AlertApplied = _cachedAlert;
        }
        catch (Exception e)
        {
            Plugin.Log("AddMalfunction skipped: " + e.GetType().Name);
        }
    }

    private static void ClearMalfunctionAlert(Buildable b, Record rec)
    {
        if (rec.AlertApplied == null || RemoveMalfunctionMethod == null) return;
        try
        {
            RemoveMalfunctionMethod.Invoke(b, new object[] { rec.AlertApplied });
        }
        catch (Exception e)
        {
            Plugin.Log("RemoveMalfunction skipped: " + e.GetType().Name);
        }
        rec.AlertApplied = null;
    }

    private static void UpdateDamageFx(Buildable b, Record rec, float nowPlayTime)
    {
        var crippled = FleetMechanicsMath.IsCrippledMalfunction(rec.Hp, rec.MaxHp);
        var emp = rec.EmpUntil > nowPlayTime;
        var shouldShow = !MoveConstructionCommands.IsStored(b) && (crippled || emp);

        if (!shouldShow)
        {
            if (rec.DamageFx != null)
            {
                UnityEngine.Object.Destroy(rec.DamageFx);
                rec.DamageFx = null;
            }
            return;
        }

        if (rec.DamageFx != null) return;

        var fxGo = new GameObject("Flotsam_BuildingDamageSmoke");
        fxGo.transform.SetParent(b.transform, false);
        fxGo.transform.localPosition = new Vector3(0f, 1.8f, 0f);

        var ps = fxGo.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.startLifetime = 1.6f;
        main.startSpeed = 1.4f;
        main.startSize = emp ? 0.35f : 0.65f;
        main.startColor = emp
            ? new ParticleSystem.MinMaxGradient(new Color(0.28f, 0.88f, 1.0f, 0.85f), new Color(0.55f, 0.40f, 1.0f, 0.6f))
            : new ParticleSystem.MinMaxGradient(new Color(0.18f, 0.16f, 0.15f, 0.72f), new Color(0.95f, 0.42f, 0.12f, 0.65f));
        main.maxParticles = 28;
        main.loop = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = ps.emission;
        emission.rateOverTime = emp ? 10f : 7f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.55f;

        var psr = fxGo.GetComponent<ParticleSystemRenderer>();
        if (psr != null)
            psr.sharedMaterial = FleetPhysicalActors.GetEmissiveMaterial(emp ? new Color(0.32f, 0.88f, 1f, 0.8f) : new Color(0.32f, 0.28f, 0.26f, 0.7f));
        ps.Play();
        rec.DamageFx = fxGo;
    }
}
