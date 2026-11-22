using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Flotsam.Multiplayer;

/// <summary>
/// Synchronizes radio distress/quest signals (<see cref="RadioMessagePanelOption"/>),
/// story dialogue choices (<see cref="DialogueNodePlayerChoices"/>), and cross-region biome
/// fleet transitions (<see cref="WorldMap"/> <c>MoveToNextBiome</c> / <c>MoveToNextBiomeTask</c>)
/// including independent-mode fleet readiness &amp; 15s jump countdown.
/// </summary>
internal static class ProgressionReplication
{
    private static bool _installed;
    private static bool _executingAuthoritativeJump;
    private static DialogueNodePlayerChoices? _activeChoiceNode;
    private static readonly HashSet<string> ReadyPeersHost = new HashSet<string>(StringComparer.Ordinal);
    private static float _biomeCountdownDeadline = -1f;

    internal static bool TransitioningBiome { get; private set; }
    internal static string ActiveDialogue { get; private set; } = "";
    internal static string[] ReadyPeersClient { get; private set; } = Array.Empty<string>();
    internal static float BiomeCountdownClient { get; private set; } = -1f;

    private static readonly MethodInfo? MoveToNextBiomeMethod =
        AccessTools.DeclaredMethod(typeof(WorldMap), "MoveToNextBiome", Type.EmptyTypes);
    private static readonly MethodInfo? MoveToNextBiomeTaskMethod =
        AccessTools.DeclaredMethod(typeof(WorldMap), "MoveToNextBiomeTask", Type.EmptyTypes);
    private static readonly FieldInfo? CurrentDialogueField =
        AccessTools.Field(typeof(DialoguePanel), "_currentDialogue");

    private static GameSession? Network => Plugin.Instance?.Network;
    private static bool Client => Network?.IsClient == true && Network.ReplicaReady && !Network.Applying;

    internal static void Reset()
    {
        TransitioningBiome = false;
        ActiveDialogue = "";
        _activeChoiceNode = null;
        ReadyPeersHost.Clear();
        _biomeCountdownDeadline = -1f;
        ReadyPeersClient = Array.Empty<string>();
        BiomeCountdownClient = -1f;
        _executingAuthoritativeJump = false;
    }

    internal static bool Handles(string kind) =>
        kind == "radioSelect" ||
        kind == "dialogueChoice" ||
        kind == "nextBiome" ||
        kind == "nextBiomeReady";

    internal static void Install(Harmony harmony)
    {
        if (_installed) return;

        var radioClick = AccessTools.DeclaredMethod(typeof(RadioMessagePanelOption), nameof(RadioMessagePanelOption.OnClick), Type.EmptyTypes);
        if (radioClick != null)
            harmony.Patch(radioClick, prefix: new HarmonyMethod(typeof(ProgressionReplication), nameof(OnRadioOptionClick)));

        var enterNode = AccessTools.DeclaredMethod(typeof(DialogueNodePlayerChoices), nameof(DialogueNodePlayerChoices.EnterNode), new[] { typeof(DialoguePanel), typeof(Dialogue) });
        if (enterNode != null)
            harmony.Patch(enterNode, postfix: new HarmonyMethod(typeof(ProgressionReplication), nameof(OnEnterChoiceNode)));

        var exitNode = AccessTools.DeclaredMethod(typeof(DialogueNodePlayerChoices), nameof(DialogueNodePlayerChoices.ExitNode), new[] { typeof(DialoguePanel), typeof(Dialogue) });
        if (exitNode != null)
            harmony.Patch(exitNode, postfix: new HarmonyMethod(typeof(ProgressionReplication), nameof(OnExitChoiceNode)));

        var choose = AccessTools.DeclaredMethod(typeof(DialogueNodePlayerChoices), nameof(DialogueNodePlayerChoices.Choose), new[] { typeof(int) });
        if (choose != null)
            harmony.Patch(choose, prefix: new HarmonyMethod(typeof(ProgressionReplication), nameof(OnDialogueChoose)));

        if (MoveToNextBiomeMethod != null)
            harmony.Patch(MoveToNextBiomeMethod, prefix: new HarmonyMethod(typeof(ProgressionReplication), nameof(OnMoveToNextBiomePrefix)));

        if (MoveToNextBiomeTaskMethod != null)
        {
            harmony.Patch(
                MoveToNextBiomeTaskMethod,
                prefix: new HarmonyMethod(typeof(ProgressionReplication), nameof(BeforeMoveToNextBiomeTask)),
                finalizer: new HarmonyMethod(typeof(ProgressionReplication), nameof(AfterMoveToNextBiomeTask)));
        }

        _installed = true;
    }

    private static void OnEnterChoiceNode(DialogueNodePlayerChoices __instance) => _activeChoiceNode = __instance;
    private static void OnExitChoiceNode(DialogueNodePlayerChoices __instance)
    {
        if (ReferenceEquals(_activeChoiceNode, __instance))
            _activeChoiceNode = null;
    }

    private static bool OnRadioOptionClick(RadioMessagePanelOption __instance)
    {
        if (!Client) return true;
        var msg = __instance?.RadioMessage;
        if (msg?.Properties == null) return false;
        Network!.Submit(new GameCommand
        {
            kind = "radioSelect",
            properties = msg.Properties.name
        });
        GameManager.UIManager?.ClosePanel(PanelID.RadioMessagePanel);
        return false;
    }

    private static bool OnDialogueChoose(int choiceIndex)
    {
        if (!Client) return true;
        if (Plugin.Options.mode != "shared") return true;
        if (!Network!.IsCaptain)
        {
            Network.ShowNotice("共享剧情分支由当前掌舵船长决定", true);
            return false;
        }
        Network.Submit(new GameCommand
        {
            kind = "dialogueChoice",
            value = choiceIndex
        });
        return false;
    }

    private static bool OnMoveToNextBiomePrefix()
    {
        if (_executingAuthoritativeJump)
            return true;

        var net = Network;
        if (net == null) return true;

        if (Client)
        {
            if (Plugin.Options.mode == "shared")
            {
                if (!net.IsCaptain)
                {
                    net.ShowNotice("只有掌舵船长可以发动跨海域跃迁", true);
                    return false;
                }
                if (net.Submit(new GameCommand { kind = "nextBiome" }))
                    net.ShowNotice("正在通知房主率领城镇跃迁至下一海域...", false);
                return false;
            }

            if (net.Submit(new GameCommand { kind = "nextBiomeReady" }))
                net.ShowNotice("已登记本船跨海域跃迁就绪，等待舰队集结", false);
            return false;
        }

        // Host in independent mode with online peers: coordinate readiness + 15s countdown
        if (net.IsHost && Plugin.Options.mode == "independent" && net.PeerCount > 0)
        {
            var err = RegisterReadyAndMaybeJump(Community.PlayerCommunity, initiateCountdown: true);
            if (err != null)
                net.ShowNotice(err, true);
            return false;
        }

        return true;
    }

    private static void BeforeMoveToNextBiomeTask()
    {
        TransitioningBiome = true;
        ReadyPeersHost.Clear();
        _biomeCountdownDeadline = -1f;
        LiveNavigation.Stop();
        if (!FleetRuntime.Active) return;

        foreach (var sec in FleetEngineBundle.SecondaryCommunities.ToArray())
        {
            if (sec == null) continue;
            FleetOwnedNavigation.Stop(sec);
            try { sec.StopNonInteractableProjects(Vector3.zero); }
            catch (Exception e) { Plugin.Log("Secondary StopNonInteractableProjects skipped: " + e.GetType().Name); }

            foreach (var b in sec.Buildables.ToArray())
            {
                if (b != null && b.TryGetComponent<Boat>(out var boat))
                {
                    try { boat.ResetToTown(disembarkAll: true); }
                    catch (Exception e) { Plugin.Log("Secondary boat ResetToTown skipped: " + e.GetType().Name); }
                }
            }
        }
    }

    private static Exception? AfterMoveToNextBiomeTask(Exception? __exception)
    {
        TransitioningBiome = false;
        if (__exception != null) return __exception;

        if (FleetRuntime.Active && Construction.Townheart != null)
        {
            var center = Construction.TownheartPosition;
            var heading = Construction.Townheart.transform.rotation;
            var secondaries = FleetEngineBundle.SecondaryCommunities.Where(c => c != null).ToArray();
            for (var i = 0; i < secondaries.Length; i++)
            {
                var sec = secondaries[i];
                var secHeart = FleetEngineBundle.HeartBuildable(sec);
                if (secHeart == null) continue;
                var angle = (i * 45f + 35f) * Mathf.Deg2Rad;
                var radius = 70f + (i / 8) * 35f;
                var targetPos = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                FleetKinematics.ApplyContinuousPose(sec, secHeart, targetPos, heading, 0f);
            }
        }

        Plugin.Instance?.Network?.NotifyWorldChanged();
        return null;
    }

    internal static void Capture(WorldFrame frame)
    {
        // Advance independent-mode biome jump countdown on Host
        if (_biomeCountdownDeadline > 0f && Time.realtimeSinceStartup >= _biomeCountdownDeadline && !TransitioningBiome)
        {
            _biomeCountdownDeadline = -1f;
            TriggerAuthoritativeBiomeJump();
        }

        var active = "";
        if (GameManager.UIManager != null && GameManager.UIManager.IsPanelOpen(PanelID.DialoguePanel))
        {
            var panel = Resources.FindObjectsOfTypeAll<DialoguePanel>()
                .FirstOrDefault(p => p != null && p.gameObject.scene.IsValid() && p.gameObject.activeInHierarchy);
            if (panel != null && CurrentDialogueField?.GetValue(panel) is Dialogue dlg)
                active = dlg.CurrentSpeaker != null ? dlg.CurrentSpeaker.Name : "剧情通讯中";
            else
                active = "剧情通讯中";
        }
        frame.activeDialogue = active;
        ActiveDialogue = active;

        frame.biomeReadyPeers = ReadyPeersHost.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        frame.biomeCountdown = _biomeCountdownDeadline > 0f
            ? Mathf.Max(0f, _biomeCountdownDeadline - Time.realtimeSinceStartup)
            : -1f;
        ReadyPeersClient = frame.biomeReadyPeers;
        BiomeCountdownClient = frame.biomeCountdown;
    }

    internal static void Apply(WorldFrame frame)
    {
        ActiveDialogue = frame.activeDialogue ?? "";
        ReadyPeersClient = frame.biomeReadyPeers ?? Array.Empty<string>();
        BiomeCountdownClient = frame.biomeCountdown;
    }

    internal static string? Execute(GameCommand command, Community? actorCommunity = null)
    {
        switch (command.kind)
        {
            case "radioSelect":
            {
                var pending = GameManager.RadioMessagesManager?.ReturnPendingRadioMessages();
                if (pending == null || pending.Count == 0)
                    return "当前没有待处理的无线电信号";
                var match = pending.FirstOrDefault(m => m?.Properties != null && m.Properties.name == command.properties);
                if (match == null)
                    return "该无线电信号已被处理";
                RadioMessageEvent.DispatchReceived(match);
                GameManager.UIManager?.ClosePanel(PanelID.RadioMessagePanel);
                return null;
            }
            case "dialogueChoice":
            {
                if (_activeChoiceNode == null)
                    return "当前没有待选择的对话分支";
                if (command.value < 0 || command.value >= 16)
                    return "对话选项索引无效";
                _activeChoiceNode.Choose(command.value);
                return null;
            }
            case "nextBiomeReady":
            {
                return RegisterReadyAndMaybeJump(actorCommunity ?? Community.PlayerCommunity, initiateCountdown: false);
            }
            case "nextBiome":
            {
                if (Plugin.Options.mode == "independent" && (Network?.PeerCount ?? 0) > 0)
                    return RegisterReadyAndMaybeJump(actorCommunity ?? Community.PlayerCommunity, initiateCountdown: ReferenceEquals(actorCommunity ?? Community.PlayerCommunity, Community.PlayerCommunity));
                return TriggerAuthoritativeBiomeJump();
            }
            default:
                return "未知剧情/区域推进指令";
        }
    }

    private static string? RegisterReadyAndMaybeJump(Community ship, bool initiateCountdown)
    {
        var shipKey = Replication.ShipKey(ship);
        var shipOwner = FleetVisibility.ResolveOwnerDisplayName(ship);
        ReadyPeersHost.Add(shipKey);

        var allKeys = new List<string> { "host" };
        foreach (var sec in FleetEngineBundle.SecondaryCommunities)
        {
            if (sec != null)
                allKeys.Add(Replication.ShipKey(sec));
        }

        var allReady = allKeys.All(k => ReadyPeersHost.Contains(k));
        if (allReady)
        {
            _biomeCountdownDeadline = -1f;
            return TriggerAuthoritativeBiomeJump();
        }

        if (initiateCountdown && _biomeCountdownDeadline <= 0f)
        {
            var cost = GameplaySettings.ReturnNextBiomeEnergyCost();
            var avail = FleetEngineBundle.EnergyOf(Community.PlayerCommunity);
            if (cost > 0f && avail + 0.5f < cost)
            {
                ReadyPeersHost.Remove(shipKey);
                return $"跨越海域所需燃料/电力不足 ({Mathf.RoundToInt(avail)}/{Mathf.RoundToInt(cost)})";
            }
            _biomeCountdownDeadline = Time.realtimeSinceStartup + 15f;
        }

        var readyCount = allKeys.Count(k => ReadyPeersHost.Contains(k));
        var pos = FleetEngineBundle.HeartBuildable(ship)?.transform.position ?? Construction.TownheartPosition;
        var msg = _biomeCountdownDeadline > 0f
            ? $"🧭 {shipOwner} 已就绪 ({readyCount}/{allKeys.Count}) · 舰队将在 {Mathf.CeilToInt(_biomeCountdownDeadline - Time.realtimeSinceStartup)}s 后跃迁至新海域！"
            : $"🧭 {shipOwner} 请求跨越海域 ({readyCount}/{allKeys.Count} 已就绪，按 F8 一键准备)";
        FleetPhysicalActors.RecordAndSpawn("allyNotice", msg, pos, pos, sourceShip: shipKey, targetShip: "host");
        return null;
    }

    private static string? TriggerAuthoritativeBiomeJump()
    {
        var worldMap = GameManager.WorldMapManager?.WorldMap;
        if (worldMap == null || MoveToNextBiomeMethod == null)
            return "世界海图尚未就绪";
        var cost = GameplaySettings.ReturnNextBiomeEnergyCost();
        var avail = FleetEngineBundle.EnergyOf(Community.PlayerCommunity);
        if (cost > 0f && avail + 0.5f < cost)
            return $"跨越海域所需燃料/电力不足 ({Mathf.RoundToInt(avail)}/{Mathf.RoundToInt(cost)})";

        _executingAuthoritativeJump = true;
        try
        {
            MoveToNextBiomeMethod.Invoke(worldMap, Array.Empty<object>());
        }
        finally
        {
            _executingAuthoritativeJump = false;
        }
        return null;
    }
}
