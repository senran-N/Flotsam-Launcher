using System;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using M4.Session;
using PajamaLlama.Fltsm;
using UnityEngine;

namespace Flotsam.Multiplayer;

[BepInPlugin("local.flotsam.multiplayer", "Flotsam Multiplayer", "0.5.0")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static Plugin? Instance;
    internal static NetConfig Options = new NetConfig();
    private Harmony? _harmony;
    private float _nextReport;
    private bool _started;
    private bool _showCrew;
    private bool _hudCollapsed;
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
            if (Input.GetKeyDown(KeyCode.F8)) _hudCollapsed = !_hudCollapsed;
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

    private static void FocusCameraOn(float x, float z)
    {
        try
        {
            var cam = CameraController.Instance;
            if (cam == null) return;
            var pos = cam.transform.position;
            cam.transform.position = new Vector3(x, pos.y, z);
        }
        catch { }
    }

    private void OnGUI()
    {
        if (_hudCollapsed)
        {
            if (GUI.Button(new Rect(14, 14, 136, 30), "⚓ 联机面板 (F8)")) _hudCollapsed = false;
            return;
        }
        var ships = Network?.Ships ?? Array.Empty<FleetShipState>();
        var isIndependent = Options.mode == "independent";
        var baseHeight = isIndependent ? 235 + Math.Max(1, ships.Length) * 58 : (_showCrew ? 210 + 34 * (Network?.Crew.Length ?? 0) : 190);
        GUILayout.BeginArea(new Rect(14, 14, 380, Mathf.Min(baseHeight, Screen.height - 28)), GUI.skin.box);

        GUILayout.BeginHorizontal();
        GUILayout.Label(isIndependent ? $"⚓ 浮游独立船队 ({Math.Max(1, ships.Length)} 艘船)" : "⚓ 浮游共建单船");
        if (GUILayout.Button("收起 F8", GUILayout.Width(68))) _hudCollapsed = true;
        GUILayout.EndHorizontal();

        GUILayout.Label(Network?.Status ?? Status);

        if (Network != null && GameSession.Ready())
        {
            if (!isIndependent)
            {
                var captain = Network.Crew.FirstOrDefault(m => m.id == Network.Captain);
                GUILayout.Label("当前掌舵船长：" + (captain?.name ?? "等待同步"));
                if ((Network.IsHost || Network.IsCaptain) && GUILayout.Button(_showCrew ? "收起成员列表" : "移交船长权限"))
                    _showCrew = !_showCrew;
                if (_showCrew)
                    foreach (var member in Network.Crew)
                        if (member.id != Network.Captain && GUILayout.Button("移交给 " + member.name))
                            Network.TransferCaptain(member.id);
            }
            else
            {
                var r = Options.rules;
                GUILayout.Label($"规则：交易{(r.trading ? "✓" : "✕")} · 掠夺{(r.stealing ? "✓" : "✕")} · 破坏{(r.sabotage ? "✓" : "✕")} · 交火{(r.friendlyFire ? "✓" : "✕")}");

                var localName = Community.PlayerCommunity?.Name ?? "";
                var myShip = ships.FirstOrDefault(s => s.name == localName);

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("定位我方船只"))
                {
                    if (myShip != null) FocusCameraOn(myShip.x, myShip.z);
                    else CameraController.Instance?.CenterOnTownheart();
                }
                if (Options.role == "client")
                {
                    if (GUILayout.Button("◀左舵", GUILayout.Width(52))) Network.Submit(new GameCommand { kind = "navigateKeys", x = -1f, z = 0.8f, value = 1 });
                    if (GUILayout.Button("▲前进", GUILayout.Width(52))) Network.Submit(new GameCommand { kind = "navigateKeys", x = 0f, z = 1f, value = 1 });
                    if (GUILayout.Button("■停船", GUILayout.Width(52))) Network.Submit(new GameCommand { kind = "navigateStop" });
                    if (GUILayout.Button("▶右舵", GUILayout.Width(52))) Network.Submit(new GameCommand { kind = "navigateKeys", x = 1f, z = 0.8f, value = 1 });
                }
                GUILayout.EndHorizontal();

                foreach (var ship in ships)
                {
                    var isMine = ship.name == localName;
                    var dist = myShip != null && !isMine ? Mathf.RoundToInt(Vector2.Distance(new Vector2(myShip.x, myShip.z), new Vector2(ship.x, ship.z))) : 0;
                    GUILayout.BeginVertical(GUI.skin.box);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"{(isMine ? "★ " : "")}{ship.ownerName} ({ship.agents}人/{ship.buildings}建/⚡{Mathf.RoundToInt(ship.energy)}{(isMine ? "" : $" · {dist}m")})");
                    if (!isMine && GUILayout.Button("观察", GUILayout.Width(46))) FocusCameraOn(ship.x, ship.z);
                    GUILayout.EndHorizontal();

                    if (!isMine)
                    {
                        GUILayout.BeginHorizontal();
                        if (r.trading)
                        {
                            if (GUILayout.Button("赠木×5")) Network.ExecuteOrSubmit(new GameCommand { kind = "shipTransfer", text = ship.name, properties = "Wood", amount = 5 });
                            if (GUILayout.Button("赠塑×5")) Network.ExecuteOrSubmit(new GameCommand { kind = "shipTransfer", text = ship.name, properties = "Plastic", amount = 5 });
                            if (GUILayout.Button("赠食水"))
                            {
                                Network.ExecuteOrSubmit(new GameCommand { kind = "shipTransfer", text = ship.name, properties = "Food", amount = 3 });
                                Network.ExecuteOrSubmit(new GameCommand { kind = "shipTransfer", text = ship.name, properties = "Drink", amount = 3 });
                            }
                        }
                        if (r.stealing && GUILayout.Button("掠夺"))
                            Network.ExecuteOrSubmit(new GameCommand { kind = "shipSteal", text = ship.name, amount = 3 });
                        if (r.friendlyFire && GUILayout.Button("开火"))
                            Network.ExecuteOrSubmit(new GameCommand { kind = "shipAttack", text = ship.name });
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndVertical();
                }
            }
        }
        if (Network?.LastResult != null && !Network.LastResult.accepted && !string.IsNullOrEmpty(Network.LastResult.error))
            GUILayout.Label("提示：" + Network.LastResult.error);
        if (Options.role == "client" && GUILayout.Button("重新同步世界")) Network?.Resync();
        GUILayout.EndArea();
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
