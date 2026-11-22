using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Synchronizes ship decorations, walkway power poles (<see cref="EnergyPoleCursorProperties"/> /
/// <see cref="EnergyPoleDecorationProperties"/>), field crops (<see cref="CropDecorationBehaviour"/>),
/// and <see cref="EnergyGridDecorationComponent"/> power-line connections across Host and Clients.
/// </summary>
internal static class DecorationReplication
{
    private static bool _installed;
    private static readonly Dictionary<string, DecorationProperties> CatalogMap =
        new Dictionary<string, DecorationProperties>(StringComparer.Ordinal);

    private static readonly MethodInfo? CancelRemovalMethod =
        AccessTools.DeclaredMethod(typeof(DecorationSlots), "CancelDecorationRemoval", new[] { typeof(Decoration) });
    private static readonly PropertyInfo? CustomNameProp =
        AccessTools.Property(typeof(Decoration), "CustomName");
    private static readonly MethodInfo? RestoreConstructionMethod =
        AccessTools.Method(typeof(Decoration), "RestoreConstruction");
    private static readonly PropertyInfo? CropWaterConsumedProp =
        AccessTools.Property(typeof(CropDecorationBehaviour), "WaterConsumed");
    private static readonly PropertyInfo? CropWaterReqProp =
        AccessTools.Property(typeof(CropDecorationBehaviour), "WaterRequirement");

    private static GameSession? Network => Plugin.Instance?.Network;
    private static bool Client => Network?.IsClient == true && Network.ReplicaReady && !Network.Applying;

    internal static void Reset() => CatalogMap.Clear();

    private static void EnsureCatalog()
    {
        if (CatalogMap.Count != 0) return;
        foreach (var p in Resources.FindObjectsOfTypeAll<DecorationProperties>())
        {
            if (p != null && !string.IsNullOrEmpty(p.name) && !CatalogMap.ContainsKey(p.name))
                CatalogMap.Add(p.name, p);
        }
    }

    internal static bool Handles(string kind) =>
        kind == "decoPlace" || kind == "decoRemove" || kind == "decoCancelRemove" || kind == "decoRename";

    internal static void Install(Harmony harmony)
    {
        if (_installed) return;

        var addDeco = AccessTools.DeclaredMethod(
            typeof(DecorationSlots),
            nameof(DecorationSlots.AddDecoration),
            new[]
            {
                typeof(DecorationProperties),
                typeof(DecorationProperties.Turns),
                typeof(int),
                typeof(int[]),
                typeof(bool)
            });
        if (addDeco != null)
            harmony.Patch(addDeco, prefix: new HarmonyMethod(typeof(DecorationReplication), nameof(OnAddDecoration)));

        var removeDeco = AccessTools.DeclaredMethod(
            typeof(DecorationSlots),
            nameof(DecorationSlots.RemoveDecoration),
            new[] { typeof(Decoration), typeof(bool) });
        if (removeDeco != null)
            harmony.Patch(removeDeco, prefix: new HarmonyMethod(typeof(DecorationReplication), nameof(OnRemoveDecoration)));

        if (CancelRemovalMethod != null)
            harmony.Patch(CancelRemovalMethod, prefix: new HarmonyMethod(typeof(DecorationReplication), nameof(OnCancelDecorationRemoval)));

        var nameSetter = AccessTools.PropertySetter(typeof(Decoration), nameof(Decoration.Name));
        if (nameSetter != null)
            harmony.Patch(nameSetter, prefix: new HarmonyMethod(typeof(DecorationReplication), nameof(OnRenameDecoration)));

        foreach (var type in new[] { typeof(EnergyGridDecorationComponent), typeof(EnergyGridConnector) })
        {
            foreach (var m in AccessTools.GetDeclaredMethods(type))
            {
                if (m.Name == "Disconnect" && m.GetParameters().Length == 1)
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(DecorationReplication), nameof(OnConnectorDisconnect)));
            }
        }

        _installed = true;
    }

    private static bool OnAddDecoration(
        DecorationSlots __instance,
        DecorationProperties decorationProperties,
        DecorationProperties.Turns turn,
        int visual,
        int[] slotIndices,
        ref Decoration? __result)
    {
        if (!Client) return true;
        __result = null;
        var buildable = __instance?.Buildable ?? __instance?.GetComponent<Buildable>();
        if (buildable == null || decorationProperties == null || slotIndices == null || slotIndices.Length == 0)
            return false;

        var id = Network!.Id(buildable);
        if (id <= 0)
        {
            Network.ShowNotice("目标建筑尚未完成同步，请稍候再试", true);
            return false;
        }

        var slotsText = string.Join(",", slotIndices.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        Network.Submit(new GameCommand
        {
            kind = "decoPlace",
            target = id,
            properties = decorationProperties.name,
            value = (int)turn,
            visual = visual,
            text = slotsText
        });
        return false;
    }

    private static bool OnRemoveDecoration(DecorationSlots __instance, Decoration decoration, bool immediately)
    {
        if (!Client || immediately) return true;
        var buildable = __instance?.Buildable ?? __instance?.GetComponent<Buildable>();
        if (buildable == null || decoration?.SlotIndices == null || decoration.SlotIndices.Length == 0)
            return false;

        var id = Network!.Id(buildable);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "decoRemove",
                target = id,
                value = decoration.SlotIndices[0],
                properties = decoration.Properties?.name ?? ""
            });
        }
        return false;
    }

    private static bool OnCancelDecorationRemoval(DecorationSlots __instance, Decoration decoration)
    {
        if (!Client) return true;
        var buildable = __instance?.Buildable ?? __instance?.GetComponent<Buildable>();
        if (buildable == null || decoration?.SlotIndices == null || decoration.SlotIndices.Length == 0)
            return false;

        var id = Network!.Id(buildable);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "decoCancelRemove",
                target = id,
                value = decoration.SlotIndices[0],
                properties = decoration.Properties?.name ?? ""
            });
        }
        return false;
    }

    private static bool OnRenameDecoration(Decoration __instance, string value)
    {
        if (!Client) return true;
        var buildable = __instance?.Parent?.Buildable;
        if (buildable == null || __instance!.SlotIndices == null || __instance.SlotIndices.Length == 0)
            return false;

        var id = Network!.Id(buildable);
        if (id > 0)
        {
            Network.Submit(new GameCommand
            {
                kind = "decoRename",
                target = id,
                value = __instance.SlotIndices[0],
                properties = __instance.Properties?.name ?? "",
                text = value ?? ""
            });
        }
        return false;
    }

    private static bool OnConnectorDisconnect(EnergyGridConnector __instance, object[] __args)
    {
        if (!Client) return true;
        if (__instance is EnergyGridBuildableComponent) return true; // Handled by GamePatches.DisconnectEnergy
        var other = __args.Length > 0 ? __args[0] as EnergyGridConnector : null;
        if (other != null && TrySubmitEnergyCommand(Network!, "energyDisconnect", __instance, other, -1))
            return false;
        return false;
    }

    internal static bool TryEncodeConnector(EnergyGridConnector? connector, GameSession net, out long buildableId, out int decoSlot)
    {
        buildableId = 0;
        decoSlot = -1;
        if (connector is EnergyGridBuildableComponent bc && bc.Buildable != null)
        {
            buildableId = net.Id(bc.Buildable);
            decoSlot = -1;
            return buildableId > 0;
        }
        if (connector is EnergyGridDecorationComponent dc &&
            dc.Decoration?.Parent?.Buildable != null &&
            dc.Decoration.SlotIndices != null &&
            dc.Decoration.SlotIndices.Length > 0)
        {
            buildableId = net.Id(dc.Decoration.Parent.Buildable);
            decoSlot = dc.Decoration.SlotIndices[0];
            return buildableId > 0 && decoSlot >= 0;
        }
        return false;
    }

    internal static bool TrySubmitEnergyCommand(
        GameSession net,
        string kind,
        EnergyGridConnector? src,
        EnergyGridConnector? dst,
        int connectIndex)
    {
        if (!TryEncodeConnector(src, net, out var srcB, out var srcSlot) ||
            !TryEncodeConnector(dst, net, out var dstB, out var dstSlot))
            return false;

        return net.Submit(new GameCommand
        {
            kind = kind,
            target = srcB,
            reference = dstB,
            value = connectIndex,
            text = (srcSlot >= 0 || dstSlot >= 0)
                ? srcSlot.ToString(CultureInfo.InvariantCulture) + ";" + dstSlot.ToString(CultureInfo.InvariantCulture)
                : ""
        });
    }

    internal static bool TryResolveConnector(
        IReadOnlyDictionary<long, Buildable> buildings,
        long buildableId,
        int decoSlot,
        out EnergyGridConnector? connector,
        out Community? community)
    {
        connector = null;
        community = null;
        if (!buildings.TryGetValue(buildableId, out var b) || b == null) return false;
        community = b.Community;
        if (decoSlot < 0)
        {
            if (b.TryGetComponent<EnergyGridBuildableComponent>(out var bc))
            {
                connector = bc;
                return true;
            }
            return false;
        }
        if (b.TryGetComponent<DecorationSlots>(out var slots) && slots.Decorations != null)
        {
            foreach (var deco in slots.Decorations)
            {
                if (deco != null &&
                    deco.SlotIndices != null &&
                    deco.SlotIndices.Contains(decoSlot) &&
                    deco.TryGetComponent<EnergyGridDecorationComponent>(out var dc))
                {
                    connector = dc;
                    return true;
                }
            }
        }
        return false;
    }

    internal static void ParseSlotPair(string? text, out int srcSlot, out int dstSlot)
    {
        srcSlot = -1;
        dstSlot = -1;
        if (string.IsNullOrWhiteSpace(text)) return;
        var parts = text!.Split(';');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
        {
            srcSlot = s;
            dstSlot = d;
        }
    }

    internal static string? ExecuteEnergyCommand(
        GameCommand command,
        IReadOnlyDictionary<long, Buildable> buildings,
        Community targetCommunity)
    {
        ParseSlotPair(command.text, out var srcSlot, out var dstSlot);
        if (!TryResolveConnector(buildings, command.target, srcSlot, out var cA, out var commA) ||
            !TryResolveConnector(buildings, command.reference, dstSlot, out var cB, out var commB) ||
            cA == null || cB == null)
        {
            return "电力节点或电线杆已不存在";
        }
        if (commA != targetCommunity || commB != targetCommunity)
            return "只能连接同一艘船上的电力节点";

        if (command.kind == "energyDisconnect")
        {
            EnergyGrid.Disconnect(cA, cB);
            return null;
        }
        if (cA.IsConnected(cB)) return "两个电力节点已经连接";
        if (cB.ConnectionTransform != null && !cA.IsInRange(cB.ConnectionTransform.position))
            return "两个电力节点距离过远";

        if (command.value >= 0)
        {
            if (!cA.CanConnect((uint)command.value)) return "该电力接口已满";
            EnergyGrid.ConnectWithIndex(cA, (uint)command.value, cB);
        }
        else
        {
            EnergyGrid.Connect(cA, cB);
        }
        return null;
    }

    internal static void Capture(
        Buildable building,
        Func<UnityEngine.Object, long> hostId,
        Func<Inventory, ItemCount[]> captureInventory,
        out DecorationState[] decorations,
        out EnergyWireState[] poleLinks)
    {
        decorations = Array.Empty<DecorationState>();
        poleLinks = Array.Empty<EnergyWireState>();

        var wireList = new List<EnergyWireState>();
        if (building.TryGetComponent<EnergyGridBuildableComponent>(out var bConn) && bConn.Connections != null)
        {
            foreach (var c in bConn.Connections)
            {
                if (c is EnergyGridDecorationComponent otherDc &&
                    otherDc.Decoration?.Parent?.Buildable != null &&
                    otherDc.Decoration.SlotIndices?.Length > 0)
                {
                    wireList.Add(new EnergyWireState
                    {
                        srcSlot = -1,
                        dstBuilding = hostId(otherDc.Decoration.Parent.Buildable),
                        dstSlot = otherDc.Decoration.SlotIndices[0]
                    });
                }
            }
        }

        if (building.TryGetComponent<DecorationSlots>(out var slots) && slots.Decorations != null && slots.Decorations.Count > 0)
        {
            var decoList = new List<DecorationState>(slots.Decorations.Count);
            foreach (var d in slots.Decorations)
            {
                if (d == null || d.Properties == null || d.SlotIndices == null || d.SlotIndices.Length == 0) continue;
                var customName = CustomNameProp?.GetValue(d) as string ?? "";
                var phase = d.ConstructionHandler != null ? (int)d.ConstructionHandler.BuildPhase : (int)BuildPhase.Finished;
                var limit = d.ConstructionHandler != null ? d.ConstructionHandler.AssignmentLimit : 1;
                var cropProg = 0f;
                if (d.TryGetComponent<CropDecorationBehaviour>(out var crop))
                {
                    var consumed = CropWaterConsumedProp != null ? (float)CropWaterConsumedProp.GetValue(crop)! : 0f;
                    var req = CropWaterReqProp != null ? (float)CropWaterReqProp.GetValue(crop)! : 0f;
                    cropProg = req > 0.0001f ? Mathf.Clamp01(consumed / req) : 0f;
                }

                decoList.Add(new DecorationState
                {
                    properties = d.Properties.name,
                    name = customName,
                    turn = (int)d.Turn,
                    visual = d.Visual,
                    phase = phase,
                    limit = limit,
                    progress = phase == (int)BuildPhase.Finished ? 1f : 0.5f,
                    cropProgress = cropProg,
                    slots = d.SlotIndices.ToArray(),
                    inventory = d.Inventory != null ? captureInventory(d.Inventory) : Array.Empty<ItemCount>()
                });

                var mySlot = d.SlotIndices[0];
                if (d.TryGetComponent<EnergyGridDecorationComponent>(out var dConn) && dConn.Connections != null)
                {
                    foreach (var c in dConn.Connections)
                    {
                        if (c is EnergyGridBuildableComponent otherBc && otherBc.Buildable != null)
                        {
                            wireList.Add(new EnergyWireState
                            {
                                srcSlot = mySlot,
                                dstBuilding = hostId(otherBc.Buildable),
                                dstSlot = -1
                            });
                        }
                        else if (c is EnergyGridDecorationComponent otherDc &&
                                 otherDc.Decoration != null &&
                                 otherDc.Decoration != d &&
                                 otherDc.Decoration.Parent?.Buildable != null &&
                                 otherDc.Decoration.SlotIndices?.Length > 0)
                        {
                            wireList.Add(new EnergyWireState
                            {
                                srcSlot = mySlot,
                                dstBuilding = hostId(otherDc.Decoration.Parent.Buildable),
                                dstSlot = otherDc.Decoration.SlotIndices[0]
                            });
                        }
                    }
                }
            }
            decorations = decoList.ToArray();
        }

        if (wireList.Count > 0)
            poleLinks = wireList.ToArray();
    }

    internal static void ApplyDecorations(
        Buildable building,
        DecorationState[]? states,
        Action<Inventory, ItemCount[]> applyInventory)
    {
        if (!building.TryGetComponent<DecorationSlots>(out var slots) || slots.Slots == null) return;
        EnsureCatalog();

        var incoming = states ?? Array.Empty<DecorationState>();
        if (incoming.Length > 128) throw new InvalidOperationException("Decoration count exceeds limit");

        var wantedBySlotAndLayer = new Dictionary<(int slot, bool isBorder), (DecorationState state, DecorationProperties prop)>();
        foreach (var s in incoming)
        {
            if (s?.slots == null || s.slots.Length == 0 || string.IsNullOrEmpty(s.properties)) continue;
            if (s.slots.Any(idx => idx < 0 || idx >= slots.Slots.Length)) continue;
            if (!CatalogMap.TryGetValue(s.properties, out var prop) || prop.DecorationPrefab == null) continue;
            var isBorder = prop.DecorationType == DecorationType.Border;
            wantedBySlotAndLayer[(s.slots[0], isBorder)] = (s, prop);
        }

        foreach (var existing in slots.Decorations.ToArray())
        {
            if (existing == null || existing.SlotIndices == null || existing.SlotIndices.Length == 0) continue;
            var isBorder = existing.Properties != null && existing.Properties.DecorationType == DecorationType.Border;
            var key = (existing.SlotIndices[0], isBorder);
            if (!wantedBySlotAndLayer.TryGetValue(key, out var match) ||
                existing.Properties == null ||
                existing.Properties.name != match.state.properties ||
                (int)existing.Turn != match.state.turn ||
                existing.Visual != match.state.visual ||
                !existing.SlotIndices.SequenceEqual(match.state.slots))
            {
                slots.RemoveDecorationImmediate(existing);
                UnityEngine.Object.Destroy(existing.gameObject);
            }
        }

        foreach (var pair in wantedBySlotAndLayer)
        {
            var s = pair.Value.state;
            var prop = pair.Value.prop;
            var isBorder = pair.Key.isBorder;
            var deco = slots.Decorations.FirstOrDefault(d =>
                d != null &&
                d.SlotIndices != null &&
                d.SlotIndices.Length > 0 &&
                d.SlotIndices[0] == pair.Key.slot &&
                (d.Properties != null && d.Properties.DecorationType == DecorationType.Border) == isBorder);
            if (deco == null)
            {
                if (s.slots.Any(idx => !slots.Slots[idx].IsAvailable(prop))) continue;
                var turnVal = Enum.IsDefined(typeof(DecorationProperties.Turns), s.turn)
                    ? (DecorationProperties.Turns)s.turn
                    : DecorationProperties.Turns.Full;
                deco = slots.AddDecoration(prop, turnVal, Mathf.Max(0, s.visual), s.slots, instantFreeBuild: true);
            }
            if (deco == null) continue;

            ReplicaFreeze.FreezePhysics(deco);
            var curCustom = CustomNameProp?.GetValue(deco) as string ?? "";
            if (curCustom != (s.name ?? ""))
                deco.Name = s.name ?? "";

            if (Enum.IsDefined(typeof(BuildPhase), s.phase) &&
                deco.ConstructionHandler != null &&
                (int)deco.ConstructionHandler.BuildPhase != s.phase &&
                RestoreConstructionMethod != null)
            {
                try
                {
                    RestoreConstructionMethod.Invoke(deco, new object[]
                    {
                        Mathf.Clamp(s.limit <= 0 ? 1 : s.limit, 1, 20),
                        (BuildPhase)s.phase,
                        Mathf.Clamp01(s.progress)
                    });
                }
                catch (Exception e) { Plugin.Log("Deco phase sync skipped: " + e.GetType().Name); }
            }

            if (deco.Inventory != null && s.inventory != null)
                applyInventory(deco.Inventory, s.inventory);

            if (deco.TryGetComponent<CropDecorationBehaviour>(out var crop))
            {
                var req = CropWaterReqProp != null ? (float)CropWaterReqProp.GetValue(crop)! : 0f;
                if (req > 0f && CropWaterConsumedProp?.CanWrite == true)
                    CropWaterConsumedProp.SetValue(crop, Mathf.Clamp01(s.cropProgress) * req);
                if (deco.SpawnedVisual != null && s.phase == (int)BuildPhase.Finished)
                    deco.SpawnedVisual.SetProgress(Mathf.Clamp01(s.cropProgress));
            }
        }
    }

    internal static void ApplyPoleLinks(
        IReadOnlyDictionary<long, Buildable> buildings,
        IReadOnlyDictionary<int, long> ids,
        BuildingState[] states)
    {
        foreach (var bState in states)
        {
            if (bState.stored || !buildings.TryGetValue(bState.id, out var srcBuilding) || srcBuilding == null) continue;
            var wanted = bState.poleLinks ?? Array.Empty<EnergyWireState>();

            if (srcBuilding.TryGetComponent<DecorationSlots>(out var slots) && slots.Decorations != null)
            {
                foreach (var deco in slots.Decorations)
                {
                    if (deco == null || deco.SlotIndices == null || deco.SlotIndices.Length == 0 ||
                        !deco.TryGetComponent<EnergyGridDecorationComponent>(out var dConn))
                        continue;

                    var mySlot = deco.SlotIndices[0];
                    if (dConn.Connections != null)
                    {
                        foreach (var existing in dConn.Connections.ToArray())
                        {
                            long otherBId = 0;
                            var otherSlot = -1;
                            if (existing is EnergyGridBuildableComponent obc && obc.Buildable != null)
                                ids.TryGetValue(obc.Buildable.GetInstanceID(), out otherBId);
                            else if (existing is EnergyGridDecorationComponent odc &&
                                     odc.Decoration?.Parent?.Buildable != null &&
                                     odc.Decoration.SlotIndices?.Length > 0)
                            {
                                ids.TryGetValue(odc.Decoration.Parent.Buildable.GetInstanceID(), out otherBId);
                                otherSlot = odc.Decoration.SlotIndices[0];
                            }

                            if (otherBId > 0 && !wanted.Any(w => w.srcSlot == mySlot && w.dstBuilding == otherBId && w.dstSlot == otherSlot))
                            {
                                try { EnergyGrid.Disconnect(dConn, existing); }
                                catch (Exception e) { Plugin.Log("Replica pole disconnect skipped: " + e.GetType().Name); }
                            }
                        }
                    }
                }
            }

            foreach (var link in wanted)
            {
                if (!TryResolveConnector(buildings, bState.id, link.srcSlot, out var cA, out _) ||
                    !TryResolveConnector(buildings, link.dstBuilding, link.dstSlot, out var cB, out _) ||
                    cA == null || cB == null || cA.IsConnected(cB))
                    continue;

                try { EnergyGrid.Connect(cA, cB); }
                catch (Exception e) { Plugin.Log("Replica pole connect skipped: " + e.GetType().Name); }
            }
        }
    }

    internal static string? Execute(
        GameCommand command,
        IReadOnlyDictionary<long, Buildable> buildings,
        Community targetCommunity)
    {
        EnsureCatalog();
        if (!buildings.TryGetValue(command.target, out var building) || building == null)
            return "目标建筑已不存在";

        if (building.Community != targetCommunity)
        {
            if (building.Community != targetCommunity)
                return "不能在其他玩家的船上修改装饰或电杆";
            targetCommunity = building.Community;
        }

        if (!building.TryGetComponent<DecorationSlots>(out var slots) || slots.Slots == null)
            return "目标建筑没有可用的装饰/作物插槽";

        using var _scope = FleetRuntime.WithPlayerCommunity(targetCommunity);

        if (command.kind == "decoPlace")
        {
            if (!CatalogMap.TryGetValue(command.properties, out var prop) || prop.DecorationPrefab == null)
                return "装饰或作物类型不存在";
            if (!Enum.IsDefined(typeof(DecorationProperties.Turns), command.value))
                return "朝向参数无效";
            if (string.IsNullOrWhiteSpace(command.text))
                return "插槽位置无效";

            var parsedSlots = new List<int>();
            foreach (var token in command.text.Split(','))
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) ||
                    idx < 0 || idx >= slots.Slots.Length || parsedSlots.Contains(idx))
                    return "插槽索引超出范围";
                if (!slots.Slots[idx].IsAvailable(prop))
                    return "该插槽已被占用";
                parsedSlots.Add(idx);
            }
            if (parsedSlots.Count == 0 || parsedSlots.Count > 32)
                return "插槽数量无效";
            if (!prop.ReturnCanBePlaced(targetCommunity))
                return "材料不足或尚未解锁该装饰/作物";

            slots.AddDecoration(
                prop,
                (DecorationProperties.Turns)command.value,
                Mathf.Max(0, command.visual),
                parsedSlots.ToArray(),
                instantFreeBuild: false);
            return null;
        }

        var targetDeco = slots.Decorations?.FirstOrDefault(d =>
            d != null &&
            d.SlotIndices != null &&
            d.SlotIndices.Contains(command.value) &&
            (string.IsNullOrEmpty(command.properties) || d.Properties?.name == command.properties));
        if (targetDeco == null) return "目标装饰或作物已不存在";

        switch (command.kind)
        {
            case "decoRemove":
                slots.RemoveDecoration(targetDeco, immediately: false);
                return null;
            case "decoCancelRemove":
                CancelRemovalMethod?.Invoke(slots, new object[] { targetDeco });
                return null;
            case "decoRename":
                if (command.text.Length > 48 || command.text.Any(char.IsControl))
                    return "名称无效";
                targetDeco.Name = command.text;
                return null;
            default:
                return "未知装饰物指令";
        }
    }
}
