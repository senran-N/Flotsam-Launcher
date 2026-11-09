using System;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using M4.Session;
using PajamaLlama.Fltsm;
using UnityEngine;

namespace Flotsam.Multiplayer;

[BepInPlugin("local.flotsam.multiplayer", "Flotsam Multiplayer", "0.0.1")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static Plugin? Instance;
    internal static NetConfig Options = new NetConfig();
    private Harmony? _harmony;
    private float _nextReport;
    private bool _started;
    private FleetHud? _hud;
    internal GameSession? Network;
    internal string Status = "初始化";
    internal static string ReportRoot = "";

    internal static void Log(string message) => Instance?.Logger.LogInfo(message);

    private void Awake()
    {
        var path = Environment.GetEnvironmentVariable("FLOTSAM_MULTIPLAYER_CONFIG");
        if (string.IsNullOrWhiteSpace(path))
        {
            Logger.LogInfo("No multiplayer config; standalone game unchanged.");
            enabled = false;
            return;
        }
        try
        {
            Options = JsonCodec.Decode<NetConfig>(File.ReadAllBytes(path));
            if (Options == null || (Options.role != "host" && Options.role != "client"))
                throw new Exception("Invalid role: " + Options?.role);
            if (string.IsNullOrWhiteSpace(Options.profile) || !Path.IsPathRooted(Options.profile))
                throw new Exception("An isolated absolute profile path is required");

            Options.profile = Path.GetFullPath(Options.profile);
            Directory.CreateDirectory(Options.profile);
            ReportRoot = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Instance = this;
            ApplyDisplaySettings();
            _harmony = new Harmony("local.flotsam.multiplayer");
            var defaultDataPath = Path.GetFullPath(Application.persistentDataPath);
            if (!string.Equals(
                    Options.profile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    defaultDataPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                _harmony.Patch(AccessTools.PropertyGetter(typeof(Application), "persistentDataPath"),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(ProfilePath)));
            }

            if (Options.mode == "independent") FleetRuntime.Install(_harmony);
            GamePatches.Install(_harmony);
            Network = new GameSession(Options);
            _hud = FleetHud.Attach(gameObject);
            Status = "等待游戏会话";
            Logger.LogInfo($"Multiplayer {Options.role} ({Options.mode}); profile: {Options.profile}");
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            enabled = false;
        }
    }

    private static void ApplyDisplaySettings()
    {
        try
        {
            var nativeW = Math.Max(1280, Display.main.systemWidth);
            var nativeH = Math.Max(720, Display.main.systemHeight);
            if (Options.windowed || Options.displayMode == "windowed")
            {
                var winW = Math.Max(1280, Math.Min(1600, nativeW * 4 / 5));
                var winH = Math.Max(720, Math.Min(900, nativeH * 4 / 5));
                Screen.SetResolution(Options.windowed ? 1280 : winW, Options.windowed ? 720 : winH, FullScreenMode.Windowed);
            }
            else if (Options.displayMode == "fullscreen")
            {
                if (Screen.fullScreenMode == FullScreenMode.Windowed || Screen.width < nativeW)
                    Screen.SetResolution(nativeW, nativeH, FullScreenMode.FullScreenWindow);
            }
        }
        catch { }
    }

    private static bool ProfilePath(ref string __result)
    {
        __result = Options.profile;
        return false;
    }

    private void Update()
    {
        try
        {
            var menu = Options.autoStart && Options.role != "client" && !_started ? UnityEngine.Object.FindAnyObjectByType<MainMenu>() : null;
            if (menu != null && Session.IsReady && Session.Profile != null && !LoadingScreen.IsLoading)
            {
                var setup = (GameSetup)AccessTools.Field(typeof(MainMenu), "_gameSetup").GetValue(menu);
                _started = true;
                Status = "载入联机世界";
                Session.Profile.StartRun(setup);
            }
            Network?.Update();
            FleetOwnedNavigation.Tick(Time.deltaTime);
            FleetPhysicalActors.Tick(Time.deltaTime);
            if (Network != null && File.Exists(Path.Combine(ReportRoot, "stop"))) Network.Dispose();
            if (Time.realtimeSinceStartup >= _nextReport)
            {
                _nextReport = Time.realtimeSinceStartup + 2f;
                WriteRuntimeStatus();
            }
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            Status = e.Message;
        }
    }

    private void WriteRuntimeStatus()
    {
        var ready = GameManager.Initialized && !LoadingScreen.IsLoading && Community.PlayerCommunity != null;
        if (ready) Status = "游戏世界就绪";
        if (Network != null) Status = Network.Status;
        var regular = Community.Communities.Where(c => c != null && (c.IsPlayerCommunity() || FleetEngineBundle.IsSecondary(c))).ToArray();
        var ships = regular.Select(c => new ShipProbe
        {
            name = c.Name ?? "",
            primary = c.IsPlayerCommunity(),
            buildings = c.Buildables.Count(b => b != null),
            agents = c.Agents.Count(a => a != null),
            energy = FleetEngineBundle.EnergyOf(c)
        }).ToArray();
        var report = new RuntimeProbe
        {
            role = Options.role,
            mode = Options.mode,
            ready = ready,
            replicaReady = Network?.ReplicaReady ?? false,
            connected = Network?.Connected ?? false,
            peers = Network?.PeerCount ?? 0,
            ships = ships.Length,
            buildings = regular.Sum(c => c.Buildables.Count(b => b != null)),
            agents = regular.Sum(c => c.Agents.Count(a => a != null)),
            local = Community.PlayerCommunity?.Name ?? "",
            fleet = ships,
            status = Status
        };
        File.WriteAllText(Path.Combine(ReportRoot, "probe.json"), JsonCodec.Text(report));
    }

    private void LateUpdate()
    {
        try { LiveNavigation.Commit(); }
        catch (Exception e) { Logger.LogError(e); }
    }
}

public sealed class RuntimeProbe
{
    public string role = "", mode = "", local = "", status = "";
    public bool ready, replicaReady, connected;
    public int peers, ships, buildings, agents;
    public ShipProbe[] fleet = Array.Empty<ShipProbe>();
}

public sealed class ShipProbe
{
    public string name = "";
    public bool primary;
    public int buildings, agents;
    public float energy;
}
