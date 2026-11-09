using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Flotsam.Multiplayer;

/// <summary>
/// Draggable, auto-sizing Flotsam-styled uGUI overlay for Shared and Independent multiplayer.
/// Replaces legacy fixed IMGUI debug boxes with warm parchment cards, crisp CJK typography,
/// hold-or-tap ship helm controls, fleet compass/distance cards, and non-blocking toast notices.
/// </summary>
internal sealed class FleetHud : MonoBehaviour
{
    private Canvas? _canvas;
    private RectTransform? _canvasRect;
    private RectTransform? _anchor;
    private GameObject? _cardRoot;
    private GameObject? _pillRoot;
    private GameObject? _waitBanner;
    private GameObject? _toastBox;

    private Text? _titleText;
    private Image? _statusDot;
    private Text? _statusText;
    private Text? _pillText;
    private Text? _waitText;
    private Text? _toastText;
    private Image? _toastBg;

    private GameObject? _rulesRow;
    private readonly Image[] _ruleBadges = new Image[4];
    private readonly Text[] _ruleTexts = new Text[4];

    private GameObject? _helmSection;
    private GameObject? _sharedSection;
    private Text? _captainLabel;
    private RectTransform? _crewListRoot;
    private RectTransform? _fleetListRoot;
    private GameObject? _resyncBtn;

    private readonly List<ShipRowView> _shipRows = new List<ShipRowView>();
    private readonly List<CrewRowView> _crewRows = new List<CrewRowView>();

    private Font? _font;
    private Sprite? _cardSprite;
    private Sprite? _headerSprite;
    private Sprite? _subCardSprite;
    private Sprite? _btnGoldSprite;
    private Sprite? _btnCreamSprite;
    private Sprite? _btnCoralSprite;
    private Sprite? _badgeSprite;
    private Sprite? _dotSprite;

    private bool _collapsed;
    private Vector2 _panelPos;
    private float _nextUiRefresh;
    private CommandResult? _seenRejected;

    // Continuous hold-to-steer state
    private bool _holdingHelm;
    private Vector2 _holdInput;
    private float _holdStartedAt;
    private float _nextHoldSend;

    private const string PrefKeyX = "FlotsamMpHudX_v2";
    private const string PrefKeyY = "FlotsamMpHudY_v2";

    private static readonly Color ColParchment = Hex(0xFCF6E6);
    private static readonly Color ColSand = Hex(0xF3E8CE);
    private static readonly Color ColCream = Hex(0xFFFDF6);
    private static readonly Color ColDriftwood = Hex(0x382317);
    private static readonly Color ColInk = Hex(0x1E2536);
    private static readonly Color ColMuted = Hex(0x526075);
    private static readonly Color ColGold = Hex(0xF6B93B);
    private static readonly Color ColCoral = Hex(0xEF6A3E);
    private static readonly Color ColSeafoam = Hex(0x289E7B);
    private static readonly Color ColDanger = Hex(0xC94637);

    private sealed class ShipRowView
    {
        public GameObject Root = null!;
        public Text Header = null!;
        public Text Stats = null!;
        public Button WatchBtn = null!;
        public GameObject ActionRow = null!;
        public Button GiftWood = null!;
        public Button GiftPlastic = null!;
        public Button GiftSupply = null!;
        public Button StealBtn = null!;
        public Button AttackBtn = null!;
        public string BoundShipName = "";
        public float ShipX, ShipZ;
    }

    private sealed class CrewRowView
    {
        public GameObject Root = null!;
        public Text Name = null!;
        public Button TransferBtn = null!;
        public string PeerId = "";
    }

    internal static FleetHud Attach(GameObject host)
    {
        var hud = host.GetComponent<FleetHud>();
        return hud != null ? hud : host.AddComponent<FleetHud>();
    }

    public void ToggleCollapse()
    {
        _collapsed = !_collapsed;
        SyncVisibility();
    }

    private void Awake()
    {
        _panelPos = new Vector2(
            PlayerPrefs.GetFloat(PrefKeyX, -22f),
            PlayerPrefs.GetFloat(PrefKeyY, -74f));
        BuildSprites();
        ResolveFont();
        BuildCanvas();
    }

    private void Update()
    {
        EnsureEventSystem();
        if (Input.GetKeyDown(KeyCode.F8)) ToggleCollapse();

        var net = Plugin.Instance?.Network;
        if (_holdingHelm && net != null && GameSession.Ready())
        {
            if (Time.unscaledTime >= _nextHoldSend)
            {
                _nextHoldSend = Time.unscaledTime + 0.14f;
                net.ExecuteOrSubmit(new GameCommand { kind = "navigateKeys", x = _holdInput.x, z = _holdInput.y, value = 1 });
            }
        }

        if (net?.LastRejected != null && !ReferenceEquals(net.LastRejected, _seenRejected))
        {
            _seenRejected = net.LastRejected;
            if (!string.IsNullOrWhiteSpace(_seenRejected.error))
                net.ShowNotice(_seenRejected.error, true);
        }

        if (Time.unscaledTime >= _nextUiRefresh)
        {
            _nextUiRefresh = Time.unscaledTime + 0.20f;
            RefreshContent();
        }
    }

    private static void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var go = new GameObject("FlotsamMpEventSystem");
        DontDestroyOnLoad(go);
        go.AddComponent<EventSystem>();
        go.AddComponent<StandaloneInputModule>();
    }

    private void ResolveFont()
    {
        try
        {
            _font = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI Bold", "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "PingFang SC", "Segoe UI", "Arial" },
                16);
        }
        catch { }

        if (_font == null)
        {
            _font = Resources.FindObjectsOfTypeAll<Font>()
                .FirstOrDefault(f => f != null && (f.name.IndexOf("YaHei", StringComparison.OrdinalIgnoreCase) >= 0
                    || f.name.IndexOf("Noto", StringComparison.OrdinalIgnoreCase) >= 0
                    || f.name.IndexOf("Chinese", StringComparison.OrdinalIgnoreCase) >= 0
                    || f.name.IndexOf("Aleo", StringComparison.OrdinalIgnoreCase) >= 0))
                ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
    }

    private void BuildSprites()
    {
        _cardSprite = MakeRoundedCard(64, 64, 14f, ColParchment, ColInk, 3f, 4);
        _headerSprite = MakeRoundedCard(64, 48, 11f, ColDriftwood, ColInk, 2.5f, 2);
        _subCardSprite = MakeRoundedCard(48, 48, 10f, ColSand, ColInk, 2f, 0);
        _btnGoldSprite = MakeRoundedCard(48, 40, 9f, ColGold, ColInk, 2f, 2);
        _btnCreamSprite = MakeRoundedCard(48, 40, 9f, ColCream, ColInk, 2f, 2);
        _btnCoralSprite = MakeRoundedCard(48, 40, 9f, ColCoral, ColInk, 2f, 2);
        _badgeSprite = MakeRoundedCard(36, 28, 8f, Color.white, ColInk, 1.8f, 0);
        _dotSprite = MakeCircle(24, Color.white);
    }

    private void BuildCanvas()
    {
        var root = new GameObject("FlotsamMultiplayerCanvas");
        root.transform.SetParent(transform, false);

        _canvas = root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 29000;

        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        root.AddComponent<GraphicRaycaster>();
        _canvasRect = root.GetComponent<RectTransform>();

        // 1. Guest Main-Menu Waiting Banner (top-center, only shown when Client is waiting on Main Menu)
        _waitBanner = CreatePanel("GuestWaitBanner", root.transform, _cardSprite!);
        var wbRect = _waitBanner.GetComponent<RectTransform>();
        wbRect.anchorMin = new Vector2(0.5f, 1f);
        wbRect.anchorMax = new Vector2(0.5f, 1f);
        wbRect.pivot = new Vector2(0.5f, 1f);
        wbRect.anchoredPosition = new Vector2(0f, -28f);
        wbRect.sizeDelta = new Vector2(560f, 0f);
        var wbLayout = _waitBanner.AddComponent<VerticalLayoutGroup>();
        wbLayout.padding = new RectOffset(20, 20, 14, 16);
        wbLayout.spacing = 6f;
        wbLayout.childControlWidth = true;
        wbLayout.childControlHeight = true;
        wbLayout.childForceExpandWidth = true;
        wbLayout.childForceExpandHeight = false;
        _waitBanner.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var wbTitle = CreateText(_waitBanner.transform, "正在连接房主海域世界…", 17, FontStyle.Bold, ColInk, TextAnchor.MiddleCenter);
        wbTitle.gameObject.name = "WaitTitle";
        _waitText = CreateText(_waitBanner.transform, "房主新建游戏或载入存档后将自动同步进入同一片海域，无需手动点击单人新游戏。", 13, FontStyle.Normal, ColMuted, TextAnchor.MiddleCenter);

        // 2. Draggable Top-Right Anchor Container
        var anchorGo = new GameObject("HudAnchor", typeof(RectTransform));
        anchorGo.transform.SetParent(root.transform, false);
        _anchor = anchorGo.GetComponent<RectTransform>();
        _anchor.anchorMin = new Vector2(1f, 1f);
        _anchor.anchorMax = new Vector2(1f, 1f);
        _anchor.pivot = new Vector2(1f, 1f);
        _anchor.anchoredPosition = _panelPos;
        _anchor.sizeDelta = new Vector2(364f, 0f);

        var anchorStack = anchorGo.AddComponent<VerticalLayoutGroup>();
        anchorStack.spacing = 8f;
        anchorStack.childAlignment = TextAnchor.UpperRight;
        anchorStack.childControlWidth = true;
        anchorStack.childControlHeight = true;
        anchorStack.childForceExpandWidth = false;
        anchorStack.childForceExpandHeight = false;
        anchorGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // 2a. Collapsed Draggable Pill
        _pillRoot = CreatePanel("CollapsedPill", _anchor, _headerSprite!);
        var pillLayout = _pillRoot.AddComponent<HorizontalLayoutGroup>();
        pillLayout.padding = new RectOffset(14, 14, 8, 10);
        pillLayout.spacing = 8f;
        pillLayout.childAlignment = TextAnchor.MiddleCenter;
        pillLayout.childControlWidth = true;
        pillLayout.childControlHeight = true;
        pillLayout.childForceExpandWidth = false;
        pillLayout.childForceExpandHeight = false;
        var pillFitter = _pillRoot.AddComponent<ContentSizeFitter>();
        pillFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        pillFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var pillDot = CreateDot(_pillRoot.transform, ColSeafoam, 9f);
        pillDot.name = "PillDot";
        _pillText = CreateText(_pillRoot.transform, "浮游联机 · 展开 (F8)", 14, FontStyle.Bold, ColCream, TextAnchor.MiddleLeft);
        var pillDrag = _pillRoot.AddComponent<DragHandle>();
        pillDrag.Bind(this, ToggleCollapse);

        // 2b. Expanded Parchment Card
        _cardRoot = CreatePanel("ExpandedCard", _anchor, _cardSprite!);
        var cardLe = _cardRoot.AddComponent<LayoutElement>();
        cardLe.preferredWidth = 364f;
        var cardLayout = _cardRoot.AddComponent<VerticalLayoutGroup>();
        cardLayout.padding = new RectOffset(12, 12, 11, 14);
        cardLayout.spacing = 8f;
        cardLayout.childControlWidth = true;
        cardLayout.childControlHeight = true;
        cardLayout.childForceExpandWidth = true;
        cardLayout.childForceExpandHeight = false;

        // Draggable Driftwood Header inside Card
        var header = CreatePanel("HeaderBar", _cardRoot.transform, _headerSprite!);
        var headerLayout = header.AddComponent<HorizontalLayoutGroup>();
        headerLayout.padding = new RectOffset(12, 8, 7, 9);
        headerLayout.spacing = 8f;
        headerLayout.childAlignment = TextAnchor.MiddleLeft;
        headerLayout.childControlWidth = true;
        headerLayout.childControlHeight = true;
        headerLayout.childForceExpandWidth = false;
        headerLayout.childForceExpandHeight = false;
        header.AddComponent<DragHandle>().Bind(this, null);

        _statusDot = CreateDot(header.transform, ColSeafoam, 10f);
        _titleText = CreateText(header.transform, "浮游独立船队", 15, FontStyle.Bold, ColCream, TextAnchor.MiddleLeft);
        _titleText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        var grip = CreateText(header.transform, "⋮⋮", 14, FontStyle.Bold, new Color(1f, 0.95f, 0.85f, 0.55f), TextAnchor.MiddleCenter);
        grip.raycastTarget = false;

        CreateButton(header.transform, "收起 F8", _btnGoldSprite!, ColInk, 12, 66f, 26f, ToggleCollapse);

        // Status subtitle row
        var statusRow = CreateRow(_cardRoot.transform, 6f);
        _statusText = CreateText(statusRow.transform, "初始化联机…", 13, FontStyle.Bold, ColInk, TextAnchor.MiddleLeft);
        _statusText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        var resyncBtn = CreateButton(statusRow.transform, "重同步", _btnCreamSprite!, ColInk, 12, 62f, 25f, () =>
        {
            Plugin.Instance?.Network?.Resync();
            Plugin.Instance?.Network?.ShowNotice("已请求房主重新同步世界");
        });
        _resyncBtn = resyncBtn.gameObject;

        // Rules Tag Strip (Independent Mode)
        _rulesRow = CreateRow(_cardRoot.transform, 5f);
        var ruleNames = new[] { "交易", "掠夺", "破坏", "交火" };
        for (var i = 0; i < 4; i++)
        {
            var badge = CreatePanel($"Rule_{i}", _rulesRow.transform, _badgeSprite!);
            badge.AddComponent<LayoutElement>().flexibleWidth = 1f;
            var bl = badge.AddComponent<HorizontalLayoutGroup>();
            bl.padding = new RectOffset(6, 6, 3, 3);
            bl.childAlignment = TextAnchor.MiddleCenter;
            bl.childControlWidth = true;
            bl.childControlHeight = true;
            _ruleBadges[i] = badge.GetComponent<Image>();
            _ruleTexts[i] = CreateText(badge.transform, ruleNames[i], 11, FontStyle.Bold, ColInk, TextAnchor.MiddleCenter);
        }

        // Ship Helm Section (Shown for everyone in Independent mode, and for Captain in Shared mode!)
        _helmSection = CreatePanel("HelmBox", _cardRoot.transform, _subCardSprite!);
        var helmLayout = _helmSection.AddComponent<VerticalLayoutGroup>();
        helmLayout.padding = new RectOffset(8, 8, 7, 7);
        helmLayout.spacing = 5f;
        helmLayout.childControlWidth = true;
        helmLayout.childControlHeight = true;
        helmLayout.childForceExpandWidth = true;
        helmLayout.childForceExpandHeight = false;

        var helmTop = CreateRow(_helmSection.transform, 5f);
        CreateButton(helmTop.transform, "定位我方船只", _btnCreamSprite!, ColInk, 12, 0f, 28f, FocusMyShip)
            .gameObject.AddComponent<LayoutElement>().flexibleWidth = 1.25f;
        AttachHelmHold(CreateButton(helmTop.transform, "◀ 左舵", _btnGoldSprite!, ColInk, 12, 0f, 28f, null), new Vector2(-1f, 0.75f));
        AttachHelmHold(CreateButton(helmTop.transform, "▲ 巡航", _btnGoldSprite!, ColInk, 12, 0f, 28f, null), new Vector2(0f, 1f));
        CreateButton(helmTop.transform, "■ 停船", _btnCreamSprite!, ColInk, 12, 0f, 28f, () =>
        {
            _holdingHelm = false;
            Plugin.Instance?.Network?.ExecuteOrSubmit(new GameCommand { kind = "navigateStop" }, "已下令停船抛锚");
        }).gameObject.AddComponent<LayoutElement>().flexibleWidth = 0.85f;
        AttachHelmHold(CreateButton(helmTop.transform, "▶ 右舵", _btnGoldSprite!, ColInk, 12, 0f, 28f, null), new Vector2(1f, 0.75f));

        CreateText(_helmSection.transform, "按住舵向持续航行，点按短距推进；按 M 打开海图可点击远航", 11, FontStyle.Normal, ColMuted, TextAnchor.MiddleCenter);

        // Shared Mode Captain & Crew Section
        _sharedSection = CreatePanel("SharedBox", _cardRoot.transform, _subCardSprite!);
        var sharedLayout = _sharedSection.AddComponent<VerticalLayoutGroup>();
        sharedLayout.padding = new RectOffset(10, 10, 8, 8);
        sharedLayout.spacing = 5f;
        sharedLayout.childControlWidth = true;
        sharedLayout.childControlHeight = true;
        sharedLayout.childForceExpandWidth = true;
        sharedLayout.childForceExpandHeight = false;

        _captainLabel = CreateText(_sharedSection.transform, "当前掌舵船长：等待同步", 13, FontStyle.Bold, ColInk, TextAnchor.MiddleLeft);
        var crewContainer = new GameObject("CrewList", typeof(RectTransform));
        crewContainer.transform.SetParent(_sharedSection.transform, false);
        var crewLayout = crewContainer.AddComponent<VerticalLayoutGroup>();
        crewLayout.spacing = 4f;
        crewLayout.childControlWidth = true;
        crewLayout.childControlHeight = true;
        crewLayout.childForceExpandWidth = true;
        crewLayout.childForceExpandHeight = false;
        _crewListRoot = crewContainer.GetComponent<RectTransform>();

        // Independent Fleet List Container
        var fleetContainer = new GameObject("FleetList", typeof(RectTransform));
        fleetContainer.transform.SetParent(_cardRoot.transform, false);
        var fleetLayout = fleetContainer.AddComponent<VerticalLayoutGroup>();
        fleetLayout.spacing = 6f;
        fleetLayout.childControlWidth = true;
        fleetLayout.childControlHeight = true;
        fleetLayout.childForceExpandWidth = true;
        fleetLayout.childForceExpandHeight = false;
        _fleetListRoot = fleetContainer.GetComponent<RectTransform>();

        // 2c. Non-Blocking Toast Banner below card
        _toastBox = CreatePanel("ToastNotice", _anchor, _cardSprite!);
        var toastLe = _toastBox.AddComponent<LayoutElement>();
        toastLe.preferredWidth = 364f;
        _toastBg = _toastBox.GetComponent<Image>();
        var toastLayout = _toastBox.AddComponent<HorizontalLayoutGroup>();
        toastLayout.padding = new RectOffset(12, 12, 8, 10);
        toastLayout.childControlWidth = true;
        toastLayout.childControlHeight = true;
        _toastText = CreateText(_toastBox.transform, "", 12, FontStyle.Bold, ColInk, TextAnchor.MiddleLeft);

        SyncVisibility();
        RefreshContent();
    }

    private void AttachHelmHold(Button btn, Vector2 dir)
    {
        btn.gameObject.AddComponent<LayoutElement>().flexibleWidth = 0.9f;
        var hold = btn.gameObject.AddComponent<HelmHoldTrigger>();
        hold.OnDown = () =>
        {
            _holdingHelm = true;
            _holdInput = dir;
            _holdStartedAt = Time.unscaledTime;
            _nextHoldSend = Time.unscaledTime + 0.14f;
            Plugin.Instance?.Network?.ExecuteOrSubmit(new GameCommand { kind = "navigateKeys", x = dir.x, z = dir.y, value = 1 });
        };
        hold.OnUp = () =>
        {
            if (!_holdingHelm) return;
            _holdingHelm = false;
            if (Time.unscaledTime - _holdStartedAt > 0.28f)
                Plugin.Instance?.Network?.ExecuteOrSubmit(new GameCommand { kind = "navigateStop" });
        };
    }

    private void FocusMyShip()
    {
        var net = Plugin.Instance?.Network;
        var localName = Community.PlayerCommunity?.Name ?? "";
        var myShip = net?.Ships.FirstOrDefault(s => s.name == localName);
        if (myShip != null) FocusCameraOn(myShip.x, myShip.z);
        else
        {
            try { CameraController.Instance?.CenterOnTownheart(); }
            catch { }
        }
    }

    internal static void FocusCameraOn(float x, float z)
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

    private void SyncVisibility()
    {
        if (_cardRoot != null) _cardRoot.SetActive(!_collapsed);
        if (_pillRoot != null) _pillRoot.SetActive(_collapsed);
    }

    internal void DragBy(Vector2 screenDelta)
    {
        if (_anchor == null || _canvas == null || _canvasRect == null) return;
        var scale = Mathf.Max(0.01f, _canvas.scaleFactor);
        _panelPos += screenDelta / scale;
        ClampAnchor();
        _anchor.anchoredPosition = _panelPos;
    }

    internal void SavePosition()
    {
        ClampAnchor();
        PlayerPrefs.SetFloat(PrefKeyX, _panelPos.x);
        PlayerPrefs.SetFloat(PrefKeyY, _panelPos.y);
        PlayerPrefs.Save();
    }

    private void ClampAnchor()
    {
        if (_canvasRect == null || _anchor == null) return;
        var canvasSize = _canvasRect.rect.size;
        if (canvasSize.x <= 10f || canvasSize.y <= 10f) return;
        var width = _collapsed ? 210f : 364f;
        var height = Mathf.Clamp(_anchor.rect.height, 44f, canvasSize.y - 24f);
        _panelPos.x = Mathf.Clamp(_panelPos.x, -(canvasSize.x - width - 12f), -12f);
        _panelPos.y = Mathf.Clamp(_panelPos.y, -(canvasSize.y - height - 12f), -12f);
    }

    private void RefreshContent()
    {
        var net = Plugin.Instance?.Network;
        var isInd = Plugin.Options.mode == "independent";
        var ready = GameSession.Ready();
        var status = net?.Status ?? Plugin.Instance?.Status ?? "等待连接";

        // Waiting banner on Main Menu for Guests
        if (_waitBanner != null)
        {
            var showWait = Plugin.Options.role == "client" && !ready;
            if (_waitBanner.activeSelf != showWait) _waitBanner.SetActive(showWait);
            if (showWait && _waitText != null)
                _waitText.text = $"{status} · 房主新建游戏或载入存档后将自动同步进入，请勿手动点击新游戏。";
        }

        var ships = net?.Ships ?? Array.Empty<FleetShipState>();
        var shipCount = Math.Max(1, ships.Length);
        if (_titleText != null)
            _titleText.text = isInd ? $"浮游独立船队 ({shipCount} 艘船)" : "浮游同船共建";
        if (_pillText != null)
            _pillText.text = isInd ? $"浮游船队 · {shipCount}船 (F8)" : "浮游共建 · (F8)";
        if (_statusText != null)
            _statusText.text = status;
        if (_statusDot != null)
            _statusDot.color = net == null ? ColGold : (net.Connected && ready ? ColSeafoam : (status.Contains("失败") || status.Contains("错误") || status.Contains("断开") ? ColDanger : ColGold));
        if (_resyncBtn != null)
            _resyncBtn.SetActive(Plugin.Options.role == "client" && ready);

        // Toast notice
        if (_toastBox != null && _toastText != null && _toastBg != null)
        {
            var showToast = net != null && Time.realtimeSinceStartup < net.NoticeUntil && !string.IsNullOrEmpty(net.NoticeText);
            if (_toastBox.activeSelf != showToast) _toastBox.SetActive(showToast);
            if (showToast && net != null)
            {
                _toastText.text = net.NoticeText;
                _toastText.color = net.NoticeError ? ColDanger : ColInk;
            }
        }

        // Rules row
        if (_rulesRow != null) _rulesRow.SetActive(isInd && ready);
        if (isInd && ready)
        {
            var r = Plugin.Options.rules;
            var flags = new[] { r.trading, r.stealing, r.sabotage, r.friendlyFire };
            var labels = new[] { "交易", "掠夺", "破坏", "交火" };
            for (var i = 0; i < 4; i++)
            {
                _ruleBadges[i].color = flags[i] ? new Color(0.83f, 0.95f, 0.89f, 1f) : new Color(0.92f, 0.88f, 0.80f, 0.75f);
                _ruleTexts[i].text = $"{labels[i]} {(flags[i] ? "开" : "关")}";
                _ruleTexts[i].color = flags[i] ? ColInk : ColMuted;
            }
        }

        // Helm visibility: always available in Independent mode once ready; available to Captain in Shared mode
        var canHelm = ready && net != null && (isInd || net.IsCaptain);
        if (_helmSection != null && _helmSection.activeSelf != canHelm)
            _helmSection.SetActive(canHelm);

        // Shared mode crew & captain transfer
        var showShared = !isInd && ready && net != null;
        if (_sharedSection != null && _sharedSection.activeSelf != showShared)
            _sharedSection.SetActive(showShared);
        if (showShared && net != null)
            RefreshSharedCrew(net);

        // Independent mode fleet cards
        var showFleet = isInd && ready && net != null;
        if (_fleetListRoot != null && _fleetListRoot.gameObject.activeSelf != showFleet)
            _fleetListRoot.gameObject.SetActive(showFleet);
        if (showFleet && net != null)
            RefreshFleetCards(net, ships);

        ClampAnchor();
        if (_anchor != null) _anchor.anchoredPosition = _panelPos;
    }

    private void RefreshSharedCrew(GameSession net)
    {
        var crew = net.Crew;
        var captain = crew.FirstOrDefault(m => m.id == net.Captain);
        if (_captainLabel != null)
            _captainLabel.text = $"掌舵船长：{captain?.name ?? "房主"}{(net.IsCaptain ? " (我方掌舵)" : "")}";

        var canTransfer = net.IsHost || net.IsCaptain;
        while (_crewRows.Count < crew.Length)
            _crewRows.Add(CreateCrewRow());

        for (var i = 0; i < _crewRows.Count; i++)
        {
            var view = _crewRows[i];
            if (i >= crew.Length)
            {
                if (view.Root.activeSelf) view.Root.SetActive(false);
                continue;
            }
            var m = crew[i];
            if (!view.Root.activeSelf) view.Root.SetActive(true);
            view.PeerId = m.id;
            var isCap = m.id == net.Captain;
            view.Name.text = $"{(isCap ? "★ " : "● ")}{m.name}{(m.host ? " (房主)" : "")}";
            view.TransferBtn.gameObject.SetActive(canTransfer && !isCap);
        }
    }

    private CrewRowView CreateCrewRow()
    {
        var row = CreateRow(_crewListRoot!, 6f);
        var nameTxt = CreateText(row.transform, "", 12, FontStyle.Bold, ColInk, TextAnchor.MiddleLeft);
        nameTxt.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        var view = new CrewRowView { Root = row, Name = nameTxt };
        view.TransferBtn = CreateButton(row.transform, "移交船长", _btnGoldSprite!, ColInk, 11, 72f, 24f, () =>
        {
            Plugin.Instance?.Network?.TransferCaptain(view.PeerId);
            Plugin.Instance?.Network?.ShowNotice("已移交掌舵船长权限");
        });
        return view;
    }

    private void RefreshFleetCards(GameSession net, FleetShipState[] ships)
    {
        var localName = Community.PlayerCommunity?.Name ?? "";
        FleetShipState? myShip = ships.FirstOrDefault(s => s.name == localName);
        if (ships.Length == 0 && Community.PlayerCommunity != null)
        {
            var c = Community.PlayerCommunity;
            var root = Construction.Townheart;
            var p = root != null ? root.transform.position : Vector3.zero;
            myShip = new FleetShipState
            {
                name = c.Name ?? "",
                ownerName = string.IsNullOrWhiteSpace(Plugin.Options.nickname) ? "我方船镇" : Plugin.Options.nickname,
                x = p.x,
                z = p.z,
                buildings = c.Buildables.Count(b => b != null),
                agents = c.Agents.Count(a => a != null),
                energy = FleetEngineBundle.EnergyOf(c)
            };
            ships = new[] { myShip };
        }

        while (_shipRows.Count < ships.Length)
            _shipRows.Add(CreateShipRow());

        var rules = Plugin.Options.rules;
        for (var i = 0; i < _shipRows.Count; i++)
        {
            var view = _shipRows[i];
            if (i >= ships.Length)
            {
                if (view.Root.activeSelf) view.Root.SetActive(false);
                continue;
            }
            var ship = ships[i];
            if (!view.Root.activeSelf) view.Root.SetActive(true);
            view.BoundShipName = ship.name;
            view.ShipX = ship.x;
            view.ShipZ = ship.z;

            var isMine = ship.name == localName || (ships.Length == 1 && i == 0);
            var distText = "";
            if (!isMine && myShip != null)
            {
                var dx = ship.x - myShip.x;
                var dz = ship.z - myShip.z;
                var dist = Mathf.RoundToInt(Mathf.Sqrt(dx * dx + dz * dz));
                distText = $" · {Compass(dx, dz)} {dist}m";
            }

            view.Header.text = $"{(isMine ? "★ " : "● ")}{ship.ownerName}{(isMine ? " (我方)" : distText)}";
            view.Stats.text = $"{ship.agents} 居民 · {ship.buildings} 建筑 · 电力 {Mathf.RoundToInt(ship.energy)}";
            view.WatchBtn.gameObject.SetActive(!isMine);

            var hasActions = !isMine && (rules.trading || rules.stealing || rules.friendlyFire);
            view.ActionRow.SetActive(hasActions);
            if (hasActions)
            {
                view.GiftWood.gameObject.SetActive(rules.trading);
                view.GiftPlastic.gameObject.SetActive(rules.trading);
                view.GiftSupply.gameObject.SetActive(rules.trading);
                view.StealBtn.gameObject.SetActive(rules.stealing);
                view.AttackBtn.gameObject.SetActive(rules.friendlyFire);
            }
        }
    }

    private ShipRowView CreateShipRow()
    {
        var card = CreatePanel("ShipCard", _fleetListRoot!, _subCardSprite!);
        var layout = card.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(9, 9, 7, 7);
        layout.spacing = 4f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var top = CreateRow(card.transform, 6f);
        var header = CreateText(top.transform, "", 13, FontStyle.Bold, ColInk, TextAnchor.MiddleLeft);
        header.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        var view = new ShipRowView { Root = card, Header = header };
        view.WatchBtn = CreateButton(top.transform, "观察", _btnCreamSprite!, ColInk, 11, 50f, 24f, () => FocusCameraOn(view.ShipX, view.ShipZ));
        view.Stats = CreateText(card.transform, "", 11, FontStyle.Normal, ColMuted, TextAnchor.MiddleLeft);

        view.ActionRow = CreateRow(card.transform, 4f);
        view.GiftWood = CreateButton(view.ActionRow.transform, "赠木×5", _btnCreamSprite!, ColInk, 11, 0f, 24f, () =>
            Plugin.Instance?.Network?.ExecuteOrSubmit(new GameCommand { kind = "shipTransfer", text = view.BoundShipName, properties = "Wood", amount = 5 }, "已发送木材补给 ×5"));
        view.GiftWood.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        view.GiftPlastic = CreateButton(view.ActionRow.transform, "赠塑×5", _btnCreamSprite!, ColInk, 11, 0f, 24f, () =>
            Plugin.Instance?.Network?.ExecuteOrSubmit(new GameCommand { kind = "shipTransfer", text = view.BoundShipName, properties = "Plastic", amount = 5 }, "已发送塑料补给 ×5"));
        view.GiftPlastic.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        view.GiftSupply = CreateButton(view.ActionRow.transform, "赠食水", _btnCreamSprite!, ColInk, 11, 0f, 24f, () =>
        {
            Plugin.Instance?.Network?.ExecuteOrSubmit(new GameCommand { kind = "shipTransfer", text = view.BoundShipName, properties = "Food", amount = 3 });
            Plugin.Instance?.Network?.ExecuteOrSubmit(new GameCommand { kind = "shipTransfer", text = view.BoundShipName, properties = "Drink", amount = 3 }, "已发送食物与淡水补给");
        });
        view.GiftSupply.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        view.StealBtn = CreateButton(view.ActionRow.transform, "掠夺", _btnCoralSprite!, ColCream, 11, 0f, 24f, () =>
            Plugin.Instance?.Network?.ExecuteOrSubmit(new GameCommand { kind = "shipSteal", text = view.BoundShipName, amount = 3 }, "已发起跨船物资掠夺"));
        view.StealBtn.gameObject.AddComponent<LayoutElement>().flexibleWidth = 0.85f;

        view.AttackBtn = CreateButton(view.ActionRow.transform, "开火", _btnCoralSprite!, ColCream, 11, 0f, 24f, () =>
            Plugin.Instance?.Network?.ExecuteOrSubmit(new GameCommand { kind = "shipAttack", text = view.BoundShipName }, "已向目标船只开火"));
        view.AttackBtn.gameObject.AddComponent<LayoutElement>().flexibleWidth = 0.85f;

        return view;
    }

    private static string Compass(float dx, float dz)
    {
        var angle = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        var dirs = new[] { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };
        return dirs[Mathf.RoundToInt(angle / 45f) % 8];
    }

    // --- UI Element Builders ---
    private static GameObject CreatePanel(string name, Transform parent, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.type = Image.Type.Sliced;
        img.color = Color.white;
        return go;
    }

    private static GameObject CreateRow(Transform parent, float spacing)
    {
        var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        go.transform.SetParent(parent, false);
        var hl = go.GetComponent<HorizontalLayoutGroup>();
        hl.spacing = spacing;
        hl.childAlignment = TextAnchor.MiddleLeft;
        hl.childControlWidth = true;
        hl.childControlHeight = true;
        hl.childForceExpandWidth = false;
        hl.childForceExpandHeight = false;
        return go;
    }

    private Image CreateDot(Transform parent, Color color, float size)
    {
        var go = new GameObject("Dot", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.sprite = _dotSprite;
        img.color = color;
        img.raycastTarget = false;
        var le = go.GetComponent<LayoutElement>();
        le.preferredWidth = size;
        le.preferredHeight = size;
        le.minWidth = size;
        le.minHeight = size;
        return img;
    }

    private Text CreateText(Transform parent, string content, int size, FontStyle style, Color color, TextAnchor align)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var txt = go.GetComponent<Text>();
        txt.font = _font;
        txt.text = content;
        txt.fontSize = size;
        txt.fontStyle = style;
        txt.color = color;
        txt.alignment = align;
        txt.horizontalOverflow = HorizontalWrapMode.Wrap;
        txt.verticalOverflow = VerticalWrapMode.Overflow;
        txt.raycastTarget = false;
        return txt;
    }

    private Button CreateButton(Transform parent, string label, Sprite sprite, Color textColor, int fontSize, float width, float height, Action? onClick)
    {
        var go = CreatePanel("Btn_" + label, parent, sprite);
        var btn = go.AddComponent<Button>();
        var img = go.GetComponent<Image>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1f, 0.97f, 0.90f, 1f);
        colors.pressedColor = new Color(0.88f, 0.84f, 0.76f, 1f);
        colors.fadeDuration = 0.06f;
        btn.colors = colors;

        var le = go.AddComponent<LayoutElement>();
        if (width > 0f) { le.preferredWidth = width; le.minWidth = width; }
        if (height > 0f) { le.preferredHeight = height; le.minHeight = height; }

        var hl = go.AddComponent<HorizontalLayoutGroup>();
        hl.padding = new RectOffset(8, 8, 2, 4);
        hl.childAlignment = TextAnchor.MiddleCenter;
        hl.childControlWidth = true;
        hl.childControlHeight = true;

        var txt = CreateText(go.transform, label, fontSize, FontStyle.Bold, textColor, TextAnchor.MiddleCenter);
        txt.horizontalOverflow = HorizontalWrapMode.Overflow;

        if (onClick != null) btn.onClick.AddListener(() => onClick());
        return btn;
    }

    // --- Procedural Anti-Aliased Nautical 9-Slice Sprites ---
    private static Sprite MakeRoundedCard(int w, int h, float radius, Color fill, Color border, float borderThickness, int shadowOffset)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        var pixels = new Color[w * h];
        var bodyBottom = shadowOffset;
        var bodyH = h - shadowOffset;
        var halfW = w * 0.5f - 1f;
        var halfH = bodyH * 0.5f - 1f;
        var center = new Vector2(w * 0.5f, bodyBottom + bodyH * 0.5f);
        var shadowCenter = new Vector2(w * 0.5f, bodyH * 0.5f);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var dBody = SdfRoundBox(p - center, new Vector2(halfW, halfH), radius);
                var bodyAlpha = Mathf.Clamp01(0.6f - dBody);

                var outCol = Color.clear;
                if (shadowOffset > 0)
                {
                    var dShadow = SdfRoundBox(p - shadowCenter, new Vector2(halfW, halfH), radius);
                    var sAlpha = Mathf.Clamp01(0.6f - dShadow);
                    if (sAlpha > 0f) outCol = new Color(border.r, border.g, border.b, sAlpha * 0.95f);
                }

                if (bodyAlpha > 0f)
                {
                    var innerBlend = Mathf.Clamp01(0.5f - (dBody + borderThickness));
                    var cardCol = Color.Lerp(border, fill, innerBlend);
                    cardCol.a = bodyAlpha;
                    outCol = AlphaOver(cardCol, outCol);
                }
                pixels[y * w + x] = outCol;
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        var slice = Mathf.CeilToInt(radius + borderThickness + 2f);
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
            new Vector4(slice, slice + shadowOffset, slice, slice));
    }

    private static Sprite MakeCircle(int size, Color color)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var r = size * 0.5f - 1f;
        var c = new Vector2(size * 0.5f, size * 0.5f);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c) - r;
                tex.SetPixel(x, y, new Color(color.r, color.g, color.b, Mathf.Clamp01(0.6f - d)));
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    private static float SdfRoundBox(Vector2 p, Vector2 b, float r)
    {
        var q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - (b - new Vector2(r, r));
        return Vector2.Max(q, Vector2.zero).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - r;
    }

    private static Color AlphaOver(Color src, Color dst)
    {
        var outA = src.a + dst.a * (1f - src.a);
        if (outA <= 0.0001f) return Color.clear;
        var rgb = (src * src.a + dst * (dst.a * (1f - src.a))) / outA;
        rgb.a = outA;
        return rgb;
    }

    private static Color Hex(uint rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    // --- Pointer Interaction Behaviours ---
    private sealed class DragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        private FleetHud? _hud;
        private Action? _onClick;
        private bool _dragged;
        private float _moved;

        public void Bind(FleetHud hud, Action? onClick)
        {
            _hud = hud;
            _onClick = onClick;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            _dragged = false;
            _moved = 0f;
        }

        public void OnDrag(PointerEventData eventData)
        {
            _moved += eventData.delta.magnitude;
            if (_moved > 4f) _dragged = true;
            _hud?.DragBy(eventData.delta);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            _hud?.SavePosition();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!_dragged && eventData.button == PointerEventData.InputButton.Left)
                _onClick?.Invoke();
        }
    }

    private sealed class HelmHoldTrigger : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public Action? OnDown;
        public Action? OnUp;
        private bool _pressed;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            _pressed = true;
            OnDown?.Invoke();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!_pressed) return;
            _pressed = false;
            OnUp?.Invoke();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!_pressed) return;
            _pressed = false;
            OnUp?.Invoke();
        }
    }
}
