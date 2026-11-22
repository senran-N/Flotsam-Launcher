extern alias Game;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Debris = Game::Flotsam;

namespace Flotsam.Multiplayer;

/// <summary>
/// Full physical &amp; tactical fleet interaction engine:
/// - Minute-1 Frogman Swimming (≤85m without a boat) &amp; Floating Ocean Barrels (&gt;85m real <see cref="Debris"/>)
/// - Real <see cref="Boat"/> + <see cref="Agent"/> occupation when boats exist
/// - Two-way Barter Contracts (<c>shipBarterOffer</c> / <c>shipBarterAccept</c> / <c>shipBarterCancel</c>)
/// - Dynamic Boarding Defense (Raider Athletics vs awake nearby defenders, with pollution penalty on defeat)
/// - 3-Tier Real Cannon Ammunition (0: WetWood/Seaweed Makeshift, 1: ScrapMetal Shrapnel, 2: Eel Overcharge EMP)
/// </summary>
internal static class FleetInteractions
{
    private sealed class ActiveOffer
    {
        public TradeOfferState State = new TradeOfferState();
        public float ExpiresAtRealtime;
    }

    private sealed class DriftingBarrel
    {
        public Debris Flotsam = null!;
        public string TargetShipKey = "";
        public string Label = "";
        public float CreatedAt;
        public bool DeliveredNoticeShown;
    }

    private static readonly List<ActiveOffer> Offers = new List<ActiveOffer>();
    private static readonly List<DriftingBarrel> Barrels = new List<DriftingBarrel>();
    private static long _nextOfferId = 1;

    internal static void Reset()
    {
        Offers.Clear();
        Barrels.Clear();
    }

    internal static bool Handles(string kind) =>
        kind == "shipTransfer" ||
        kind == "shipFloatBarrel" ||
        kind == "shipSteal" ||
        kind == "shipAttack" ||
        kind == "shipBarterOffer" ||
        kind == "shipBarterAccept" ||
        kind == "shipBarterCancel";

    internal static bool IsAgentBusy(Agent? a) =>
        a != null && (FleetVoyages.IsBusy(a) || FleetCombat.IsBusy(a) || BuildingDurability.IsRepairWorker(a));

    internal static bool IsBoatBusy(Buildable? b) =>
        b != null && (FleetVoyages.IsBusy(b));

    internal static bool ShipHasOperationalBoat(Community? comm)
    {
        if (comm?.Buildables == null) return false;
        foreach (var b in comm.Buildables)
        {
            if (b != null && b.BuildPhase == BuildPhase.Finished && !MoveConstructionCommands.IsStored(b) && b.TryGetComponent<Boat>(out _))
                return true;
        }
        return false;
    }

    internal static TradeOfferState[] CaptureOffers()
    {
        var now = Time.realtimeSinceStartup;
        Offers.RemoveAll(o => o.ExpiresAtRealtime <= now);
        return Offers.Select(o => o.State).ToArray();
    }

    internal static void TickHost(float dt)
    {
        if (!GameSession.Ready() || dt <= 0f) return;
        var now = Time.realtimeSinceStartup;

        // Advance drifting ocean trade barrels toward their destination ship
        for (var i = Barrels.Count - 1; i >= 0; i--)
        {
            var barrel = Barrels[i];
            if (barrel.Flotsam == null || barrel.Flotsam.Pooled || now - barrel.CreatedAt > 180f)
            {
                Barrels.RemoveAt(i);
                continue;
            }

            var targetComm = Replication.ResolveShipCommunity(barrel.TargetShipKey);
            if (targetComm == null)
            {
                Barrels.RemoveAt(i);
                continue;
            }

            var targetPos = FleetEngineBundle.HeartBuildable(targetComm)?.transform.position ?? Construction.TownheartPosition;
            var curPos = barrel.Flotsam.transform.position;
            var toTarget = new Vector3(targetPos.x - curPos.x, 0f, targetPos.z - curPos.z);
            var dist = toTarget.magnitude;

            if (dist <= 9.5f)
            {
                // Auto-scoop into target ship storage when barrel bumps right against the hull!
                var items = barrel.Flotsam.Inventory.ReturnAllItems().ToArray();
                var stored = 0;
                foreach (var it in items)
                {
                    if (it?.Properties != null && targetComm.SpawnItemToAvailableStorage(it.Properties))
                    {
                        barrel.Flotsam.Inventory.TakeItem(it);
                        stored++;
                    }
                }
                if (stored > 0)
                {
                    FleetPhysicalActors.SpawnNativeSplash(curPos);
                    FleetPhysicalActors.PlayFleetSound("cargo_deliver", curPos, 0.8f);
                    FleetPhysicalActors.SpawnNativePopup(targetPos + Vector3.up * 2.8f, $"🛢 捞起漂流木桶：{barrel.Label}");
                    if (!barrel.Flotsam.Inventory.ReturnAllItems().Any())
                    {
                        barrel.Flotsam.SafelyDestroy();
                        Barrels.RemoveAt(i);
                    }
                }
                else if (!barrel.DeliveredNoticeShown)
                {
                    barrel.DeliveredNoticeShown = true;
                    FleetPhysicalActors.SpawnNativePopup(curPos + Vector3.up * 2.2f, $"漂流木桶已抵船舷 ({barrel.Label})");
                }
                continue;
            }

            // Pooled native debris follows ocean current; the destination is only
            // an intended recipient, never a homing target. Any crew can salvage it.
        }
    }

    internal static string? Execute(
        GameCommand command,
        Community actor,
        Vector3 actorPos,
        IReadOnlyDictionary<long, Buildable> buildings,
        Func<UnityEngine.Object, long> hostId)
    {
        if (!Plugin.Options.rules.Allows(command.kind))
            return "当前房间规则未开启该跨船操作";

        if (command.kind == "shipBarterAccept" || command.kind == "shipBarterCancel")
            return ExecuteBarterResponse(command, actor);

        Buildable? clickedBuilding = null;
        if (command.target > 0)
            buildings.TryGetValue(command.target, out clickedBuilding);

        var targetShipToken = command.text ?? "";
        var wantItemToken = "";
        if (command.kind == "shipBarterOffer")
        {
            var sep = targetShipToken.IndexOf('|');
            if (sep <= 0 || sep >= targetShipToken.Length - 1)
                return "易货契约参数不完整";
            wantItemToken = targetShipToken.Substring(sep + 1).Trim();
            targetShipToken = targetShipToken.Substring(0, sep).Trim();
        }

        var other = clickedBuilding?.Community != null && !ReferenceEquals(clickedBuilding.Community, actor)
            ? clickedBuilding.Community
            : Replication.ResolveShipCommunity(targetShipToken);

        if (other == null || ReferenceEquals(other, actor))
            return "目标船只不存在";

        var otherPos = clickedBuilding != null
            ? clickedBuilding.transform.position
            : (FleetEngineBundle.RootFor(other)?.transform.position ?? Construction.TownheartPosition);

        var actorShipName = Replication.ShipKey(actor);
        var otherShipName = Replication.ShipKey(other);
        var clickedId = clickedBuilding != null ? hostId(clickedBuilding) : 0L;

        if (command.kind == "shipBarterOffer")
            return CreateBarterOffer(command, actor, actorShipName, otherShipName, wantItemToken);

        if (command.kind == "shipFloatBarrel")
            return LaunchFloatingBarrel(command, actor, actorPos, otherPos, otherShipName);

        if (command.kind == "shipTransfer")
            return ExecuteTransfer(command, actor, other, actorPos, otherPos, actorShipName, otherShipName, clickedId);

        if (command.kind == "shipSteal")
            return ExecuteBoardingRaid(command, actor, other, clickedBuilding, actorPos, otherPos, actorShipName, otherShipName, clickedId);

        if (command.kind == "shipAttack")
            return ExecuteCannonAttack(command, actor, other, clickedBuilding, actorPos, otherPos, actorShipName, otherShipName, hostId);

        return "未知跨船操作";
    }

    private static string? CreateBarterOffer(
        GameCommand command,
        Community actor,
        string actorShipKey,
        string otherShipKey,
        string wantItemName)
    {
        var giveAmt = Mathf.Clamp(command.amount <= 0 ? 4 : command.amount, 1, 25);
        var wantAmt = Mathf.Clamp(command.value <= 0 ? 4 : command.value, 1, 25);
        if(command.amount>25||command.value>25)return "单次交易最多25件，小艇不足时最多4件";
        var giveName = command.properties ?? "";
        if (string.IsNullOrEmpty(giveName) || string.IsNullOrEmpty(wantItemName))
            return "请选择双方交易的物资种类";

        var stock = Replication.CollectShipStock(actor);
        var available = stock.Where(s => MatchesItemOrCategory(s.Prop, giveName)).Sum(s => s.Count);
        if (available < giveAmt)
            return $"本船库存不足：{Replication.FormatCategoryLabel(giveName)} 仅有 {available} (需要 {giveAmt})";

        Offers.RemoveAll(o => o.State.fromShip == actorShipKey && o.State.toShip == otherShipKey);
        var offer = new TradeOfferState
        {
            id = _nextOfferId++,
            fromShip = actorShipKey,
            fromOwner = FleetVisibility.ResolveOwnerDisplayName(actor),
            toShip = otherShipKey,
            giveItem = giveName,
            giveAmount = giveAmt,
            wantItem = wantItemName,
            wantAmount = wantAmt
        };
        Offers.Add(new ActiveOffer { State = offer, ExpiresAtRealtime = Time.realtimeSinceStartup + 120f });

        var label = $"{offer.fromOwner} 发起易货：{Replication.FormatCategoryLabel(giveName)}×{giveAmt} ⇄ {Replication.FormatCategoryLabel(wantItemName)}×{wantAmt}";
        var targetComm = Replication.ResolveShipCommunity(otherShipKey);
        var pos = FleetEngineBundle.HeartBuildable(targetComm)?.transform.position ?? Construction.TownheartPosition;
        FleetPhysicalActors.RecordAndSpawn("barterNotice", label, pos, pos, sourceShip: actorShipKey, targetShip: otherShipKey);
        return null;
    }

    private static string? ExecuteBarterResponse(GameCommand command, Community actor)
    {
        var actorKey = Replication.ShipKey(actor);
        var idx = Offers.FindIndex(o => o.State.id == command.target);
        if (idx < 0 || Offers[idx].ExpiresAtRealtime<=Time.realtimeSinceStartup)
            return "该易货报价已过期或已被处理";

        var offer = Offers[idx].State;
        if (command.kind == "shipBarterCancel")
        {
            if (offer.fromShip != actorKey && offer.toShip != actorKey)
                return "无权取消此报价";
            Offers.RemoveAt(idx);
            return null;
        }

        if (offer.toShip != actorKey)
            return "只能签收发给我方船只的易货契约";

        var fromComm = Replication.ResolveShipCommunity(offer.fromShip);
        if (fromComm == null)
        {
            Offers.RemoveAt(idx);
            return "发起方船只已离线";
        }

        var capacityError=FleetVoyages.CanLaunch(fromComm,actor,offer.giveAmount,out var fromWorker,out var fromBoat);
        if(capacityError!=null)return capacityError;
        capacityError=FleetVoyages.CanLaunch(actor,fromComm,offer.wantAmount,out var toWorker,out var toBoat);
        if(capacityError!=null)return capacityError;
        var giveItems=ExtractItemsFromShip(fromComm,offer.giveItem,offer.giveAmount);
        var wantItems=ExtractItemsFromShip(actor,offer.wantItem,offer.wantAmount);
        if(giveItems.Count!=offer.giveAmount||wantItems.Count!=offer.wantAmount)
        {
            FleetVoyages.ReturnCargo(fromComm,giveItems);FleetVoyages.ReturnCargo(actor,wantItems);
            return "报价物资不足，双方物资已退回";
        }
        Offers.RemoveAt(idx);
        FleetVoyages.Start(fromComm,actor,fromWorker!,fromBoat,giveItems,false,0);
        FleetVoyages.Start(actor,fromComm,toWorker!,toBoat,wantItems,false,0);
        return null;
    }

    private static string? LaunchFloatingBarrel(
        GameCommand command,
        Community actor,
        Vector3 actorPos,
        Vector3 otherPos,
        string otherShipName)
    {
        var dist = Vector3.Distance(actorPos, otherPos);
        if (dist > 500f)
            return "距离超过 500m，海流过远无法准确漂送木桶";

        var reqAmount = Mathf.Clamp(command.amount <= 0 ? 5 : command.amount, 1, 10);
        var extracted = ExtractItemsFromShip(actor, command.properties, reqAmount);
        if (extracted.Count == 0)
            return $"我方船只库存中没有可装桶的 {Replication.FormatCategoryLabel(command.properties)}";

        var grouped = extracted
            .GroupBy(p => p)
            .Select(g => (Prop: g.Key, Amount: g.Count()))
            .ToList();

        var dir = (otherPos - actorPos);
        dir.y = 0f;
        var launchDir = dir.sqrMagnitude > 0.2f ? dir.normalized : Vector3.forward;
        var spawnPos = actorPos + launchDir * 7.5f;

        var flotsam = SeaLootAndLandmarks.SpawnCustomFlotsam(spawnPos, grouped);
        if (flotsam == null)
        {
            FleetVoyages.ReturnCargo(actor,extracted);
            return "海面漂流池尚未就绪";
        }

        var label = $"{Replication.FormatCategoryLabel(command.properties)} ×{extracted.Count}";
        Barrels.Add(new DriftingBarrel
        {
            Flotsam = flotsam,
            TargetShipKey = otherShipName,
            Label = label,
            CreatedAt = Time.realtimeSinceStartup
        });

        FleetPhysicalActors.SpawnNativeSplash(spawnPos);
        FleetPhysicalActors.SpawnWaterShockwaveRing(spawnPos, new Color(0.95f, 0.78f, 0.30f, 0.85f), 4.0f);
        FleetPhysicalActors.PlayFleetSound("boat_depart", spawnPos, 0.72f);
        FleetPhysicalActors.SpawnNativePopup(spawnPos + Vector3.up * 2.4f, $"🛢 抛投漂流货桶：{label}");
        return null;
    }

    private static string? ExecuteTransfer(
        GameCommand command,
        Community actor,
        Community other,
        Vector3 actorPos,
        Vector3 otherPos,
        string actorShipName,
        string otherShipName,
        long clickedId)
    {
        return FleetVoyages.Launch(actor,other,command.properties,command.amount,false,clickedId);
    }

    private static string? ExecuteBoardingRaid(GameCommand command, Community actor, Community other, Buildable? clickedBuilding,
        Vector3 actorPos, Vector3 otherPos, string actorShipName, string otherShipName, long clickedId)
    {
        return FleetVoyages.Launch(actor,other,"",command.amount,true,clickedId);
    }

    private static string? ExecuteCannonAttack(
        GameCommand command,
        Community actor,
        Community other,
        Buildable? clickedBuilding,
        Vector3 actorPos,
        Vector3 otherPos,
        string actorShipName,
        string otherShipName,
        Func<UnityEngine.Object, long> hostId)
    {
        return "请进入本船炮位，手动瞄准后开火";
    }

    internal static bool TryConsumeCannonAmmo(Community actor, int ammoTier, out string ammoLabel, out string? error)
    {
        ammoLabel = "";
        error = null;

        if (ammoTier == 0)
        {
            var consumed = ExtractByNames(actor, new[] { "WetWood", "Seaweed", "Plastic" }, 2);
            if (consumed.Count < 2)
            {
                FleetVoyages.ReturnCargo(actor,consumed);
                error = "发射【土制散弹】需要 2 份湿木、海藻或塑料";
                return false;
            }
            ammoLabel = "湿木土弹";
            return true;
        }

        if (ammoTier == 1)
        {
            var consumed = ExtractByNames(actor, new[] { "MetalScrap", "MetalPlate", "ScrapMetal", "Metal", "Iron" }, 2);
            if (consumed.Count < 2)
            {
                FleetVoyages.ReturnCargo(actor,consumed);
                error = "发射【废铁破片弹】需要 2 份废铁/金属，可切换土弹或搜刮金属资源";
                return false;
            }
            ammoLabel = "废铁破片弹";
            return true;
        }

        // Tier 2: Eel Overcharge EMP shell (1 ScrapMetal/Electronics + 160 Engine Grid Energy)
        var engine = FleetEngineBundle.HeartBuildable(actor)?.GetComponent<Engine>() ?? actor.Engine;
        if(engine?.EnergyGrid==null){error="本船尚未接通引擎电网";return false;}
        var availEnergy = FleetEngineBundle.EnergyOf(actor);
        if (availEnergy < 160f)
        {
            error = $"发射【电鳗电磁弹】需要 160 引擎电力 (当前 {Mathf.FloorToInt(availEnergy)})";
            return false;
        }
        var empCore = ExtractByNames(actor, new[] { "Electronics", "Circuit", "MetalScrap", "MetalPlate", "ScrapMetal", "Metal" }, 1);
        if (empCore.Count < 1)
        {
            error = "发射【电鳗电磁弹】需要 1 份废铁或电子元件 + 160 电力";
            return false;
        }
        var spent=engine.EnergyGrid.RequestStorageEnergy(160f);
        if(spent<159.99f){engine.EnergyGrid.FillStorageEnergy(spent);FleetVoyages.ReturnCargo(actor,empCore);error="引擎可用电力不足160，弹药已退回";return false;}
        ammoLabel = "电鳗过载EMP弹";
        return true;
    }

    internal static List<ItemProperties> ExtractRepairMaterials(Community from, IReadOnlyList<string> preferredNames, int maxCount)
    {
        var list = new List<ItemProperties>();
        if (preferredNames.Count > 0)
            list.AddRange(ExtractByNames(from, preferredNames, maxCount));
        if (list.Count < maxCount)
            list.AddRange(ExtractByNames(from, new[] { "DryWood", "Planks", "WetWood", "Plastic", "ScrapMetal" }, maxCount - list.Count));
        return list;
    }

    private static List<ItemProperties> ExtractByNames(Community from, IReadOnlyList<string> allowedSubstrings, int maxCount)
    {
        var extracted = new List<ItemProperties>();
        if (from.Buildables == null || maxCount <= 0) return extracted;
        foreach (var building in from.Buildables)
        {
            if (building == null || MoveConstructionCommands.IsStored(building) || building.Inventory == null || extracted.Count >= maxCount)
                continue;
            foreach (var section in building.Inventory.SubInventoryTypes.ToArray())
            {
                if (extracted.Count >= maxCount || !Replication.IsTradableSection(section)) continue;
                foreach (var item in building.Inventory.ReturnAllItems(section, new List<Item>()).ToArray())
                {
                    if (extracted.Count >= maxCount) break;
                    var p = item?.Properties;
                    if (item == null || p == null || item.IsReserved || p.IsQuestItem) continue;
                    var match = false;
                    for (var i = 0; i < allowedSubstrings.Count; i++)
                    {
                        if (p.name.IndexOf(allowedSubstrings[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            match = true;
                            break;
                        }
                    }
                    if (!match) continue;
                    if (building.Inventory.RemoveItem(p, section) != null)
                        extracted.Add(p);
                }
            }
        }
        return extracted;
    }

    internal static bool MatchesItemOrCategory(ItemProperties p, string? selector)
    {
        if (string.IsNullOrEmpty(selector)) return true;
        var n = p.name ?? "";
        return selector switch
        {
            "Food" => (p.Tags & Item.Tags.Food) != 0,
            "Drink" => (p.Tags & Item.Tags.Drink) != 0,
            "Wood" => n == "DryWood" || n == "WetWood" || n == "Planks" || n.IndexOf("Wood", StringComparison.OrdinalIgnoreCase) >= 0,
            "Plastic" => n == "Plastic" || n.IndexOf("Plastic", StringComparison.OrdinalIgnoreCase) >= 0,
            "Metal" => n == "Metal" || n == "ScrapMetal" || n.IndexOf("Metal", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Iron", StringComparison.OrdinalIgnoreCase) >= 0,
            "Tech" => n == "Electronics" || n.IndexOf("Book", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Tech", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Electro", StringComparison.OrdinalIgnoreCase) >= 0,
            _ => n == selector
        };
    }

    internal static bool HasStealableItems(Community from)
    {
        foreach (var building in from.Buildables)
        {
            if (building == null || MoveConstructionCommands.IsStored(building) || building.Inventory == null) continue;
            foreach (var section in building.Inventory.SubInventoryTypes)
            {
                if (!Replication.IsTradableSection(section)) continue;
                foreach (var item in building.Inventory.ReturnAllItems(section, new List<Item>()))
                    if (item?.Properties != null && !item.IsReserved && !item.Properties.IsQuestItem) return true;
            }
        }
        return false;
    }

    internal static List<ItemProperties> ExtractItemsFromShip(Community from, string? selector, int maxCount, Buildable? preferredBuilding = null,bool onlyPreferred=false)
    {
        var extracted = new List<ItemProperties>();
        var ordered = preferredBuilding != null && preferredBuilding.Community == from
            ? onlyPreferred?new[] {preferredBuilding}:new[] { preferredBuilding }.Concat(from.Buildables.Where(b => b != preferredBuilding))
            : from.Buildables;
        foreach (var building in ordered)
        {
            if (building == null || MoveConstructionCommands.IsStored(building) || building.Inventory == null || extracted.Count >= maxCount) continue;
            foreach (var section in building.Inventory.SubInventoryTypes.ToArray())
            {
                if (extracted.Count >= maxCount || !Replication.IsTradableSection(section)) continue;
                foreach (var item in building.Inventory.ReturnAllItems(section, new List<Item>()).ToArray())
                {
                    if (extracted.Count >= maxCount) break;
                    if (item?.Properties == null || item.IsReserved || item.Properties.IsQuestItem) continue;
                    var p = item.Properties;
                    if (!MatchesItemOrCategory(p, selector)) continue;
                    if (building.Inventory.RemoveItem(p, section) != null)
                        extracted.Add(p);
                }
            }
        }
        return extracted;
    }
}
