using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Flotsam.Multiplayer;

/// <summary>
/// Native In-Game Multiplayer UI Integration:
/// 1. Injects fleet radar, courier boat trade, boarding raid, and artillery barrage controls
///    directly into Flotsam's native <see cref="BuildablePanel"/> when clicking Townheart or enemy buildings,
///    cloning Flotsam's own <c>_upgradeButton</c> so fonts, textures, sounds, and transitions are 100% native.
/// 2. Keeps only a tiny, unobtrusive single-line connection status badge in the top-right corner (F8 to hide).
/// </summary>
internal sealed class FleetHud : MonoBehaviour
{
    private const string PrefKeyX = "Flotsam.Multiplayer.HudX";
    private const string PrefKeyY = "Flotsam.Multiplayer.HudY";

    private Canvas? _canvas;
    private RectTransform? _canvasRect;
    private RectTransform? _anchor;
    private GameObject? _pillRoot;
    private Image? _statusDot;
    private Text? _pillText;
    private GameObject? _toastBox;
    private Text? _toastText;
    private GameObject? _waitBanner;
    private Text? _waitText;

    private bool _hidden;
    private float _nextUiRefresh;
    private Vector2 _panelPos = new Vector2(-18f, -18f);

    private static Font? _cjkFont;
    private static Sprite? _pillSprite;
    private static Sprite? _dotSprite;

    // Native BuildablePanel integration state
    private static GameObject? _nativeDrawer;
    private static TextMeshProUGUI? _nativeTitle;
    private static RectTransform? _nativeRow1;
    private static RectTransform? _nativeRow2;
    private static RectTransform? _nativeRow3;
    private static readonly List<GameObject> _nativeNodes = new List<GameObject>();
    private static readonly List<Action<int>> _amountListeners = new List<Action<int>>();

    private static readonly (string Key, string Label)[] TradeCategories = new[]
    {
        ("Wood", "木材"),
        ("Plastic", "塑料"),
        ("Metal", "金属"),
        ("Food", "食物"),
        ("Drink", "淡水"),
        ("Tech", "电子")
    };
    private static int _selectedCategoryIdx;
    private static int _selectedAmount = 5;

    private static readonly Color ColInk = new Color(0.118f, 0.145f, 0.212f, 1f);
    private static readonly Color ColDriftwood = new Color(0.220f, 0.137f, 0.090f, 0.94f);
    private static readonly Color ColParchment = new Color(0.988f, 0.965f, 0.902f, 0.97f);
    private static readonly Color ColCream = new Color(1.000f, 0.973f, 0.906f, 1f);
    private static readonly Color ColGold = new Color(0.965f, 0.725f, 0.231f, 1f);
    private static readonly Color ColSeafoam = new Color(0.322f, 0.839f, 0.659f, 1f);
    private static readonly Color ColDanger = new Color(0.839f, 0.271f, 0.208f, 1f);

    internal static FleetHud Attach(GameObject host)
    {
        var existing = host.GetComponent<FleetHud>();
        return existing != null ? existing : host.AddComponent<FleetHud>();
    }

    private void Awake()
    {
        if (PlayerPrefs.HasKey(PrefKeyX) && PlayerPrefs.HasKey(PrefKeyY))
        {
            var x = PlayerPrefs.GetFloat(PrefKeyX, -18f);
            var y = PlayerPrefs.GetFloat(PrefKeyY, -18f);
            if (float.IsFinite(x) && float.IsFinite(y))
                _panelPos = new Vector2(x, y);
        }
        EnsureAssets();
        BuildMinimalOverlay();
    }

    private void Update()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                _hidden = !_hidden;
                if (_pillRoot != null) _pillRoot.SetActive(!_hidden);
            }

            if (Time.unscaledTime >= _nextUiRefresh)
            {
                _nextUiRefresh = Time.unscaledTime + 0.25f;
                RefreshStatusOverlay();
            }
        }
        catch { }
    }

    // =========================================================================
    // 1. NATIVE BUILDABLE PANEL INTEGRATION (Harmony Postfixes on BuildablePanel)
    // =========================================================================

    internal static void OnBuildablePanelInitialize(BuildablePanel __instance)
    {
        try
        {
            PopulateNativeBuildablePanel(__instance);
        }
        catch (Exception e)
        {
            Plugin.Log("NativePanel init error: " + e.Message);
        }
    }

    internal static void OnBuildablePanelUpdate(BuildablePanel __instance)
    {
        try
        {
            var b = __instance.Buildable;
            if (b == null || b.Community == null) return;
            var nameField = AccessTools.Field(typeof(BuildablePanel), "_name")?.GetValue(__instance) as TextMeshProUGUI;
            if (nameField != null && !ReferenceEquals(b.Community, Community.PlayerCommunity))
            {
                var ownerName = ResolveOwnerLabel(b.Community.Name);
                var baseName = b.Name ?? "";
                if (!baseName.Contains(ownerName))
                    nameField.text = $"[{ownerName}] {baseName}";
            }
        }
        catch { }
    }

    private static void PopulateNativeBuildablePanel(BuildablePanel panel)
    {
        var buildable = panel.Buildable;
        var net = Plugin.Instance?.Network;
        if (buildable == null || net == null || !GameSession.Ready())
        {
            if (_nativeDrawer != null) _nativeDrawer.SetActive(false);
            return;
        }

        var upgradeBtn = AccessTools.Field(typeof(BuildablePanel), "_upgradeButton")?.GetValue(panel) as Button;
        var nameTmp = AccessTools.Field(typeof(BuildablePanel), "_name")?.GetValue(panel) as TextMeshProUGUI;
        var headerImg = AccessTools.Field(typeof(BuildablePanel), "_headerImage")?.GetValue(panel) as Image;
        if (upgradeBtn == null || nameTmp == null) return;

        EnsureNativeDrawer(panel, headerImg, nameTmp);
        if (_nativeDrawer == null || _nativeRow1 == null || _nativeRow2 == null || _nativeRow3 == null || _nativeTitle == null) return;

        ClearNativeButtons();

        var myCommunity = Community.PlayerCommunity;
        var isForeignBuilding = buildable.Community != null && myCommunity != null && !ReferenceEquals(buildable.Community, myCommunity);
        var myHeart = FleetEngineBundle.HeartBuildable(myCommunity) ?? Construction.Townheart?.Buildable;
        var isMyTownheart = !isForeignBuilding && (ReferenceEquals(buildable, myHeart) || buildable.GetComponent<Engine>() != null);

        if (!isForeignBuilding && !isMyTownheart)
        {
            _nativeDrawer.SetActive(false);
            return;
        }

        _nativeDrawer.SetActive(true);
        var rules = Plugin.Options.rules;
        var isInd = Plugin.Options.mode == "independent";

        var cat = TradeCategories[_selectedCategoryIdx % TradeCategories.Length];
        var myStock = Replication.CountItemsOnShip(myCommunity, cat.Key);
        _selectedAmount = Mathf.Clamp(_selectedAmount, 1, 9999);

        if (isForeignBuilding && isInd)
        {
            var targetShipName = buildable.Community!.Name ?? "";
            var ownerLabel = ResolveOwnerLabel(targetShipName);
            var targetId = net.Id(buildable);
            var targetStock = Replication.CountItemsOnShip(buildable.Community, cat.Key);
            var sliderMax = Mathf.Max(20, Mathf.Max(myStock, targetStock));

            void UpdateForeignHeader(int curAmt) =>
                _nativeTitle!.text = $"友船交互 · {ownerLabel} (我方{cat.Label}:{myStock} · 对方:{targetStock} · 数量:{curAmt})";
            UpdateForeignHeader(_selectedAmount);
            _amountListeners.Add(UpdateForeignHeader);

            // Row 1: Category selector + Continuous Slider + Arbitrary Integer Input + Max button
            SpawnNativeButton(_nativeRow1, upgradeBtn, $"品类: {cat.Label} [切换]", () =>
            {
                _selectedCategoryIdx = (_selectedCategoryIdx + 1) % TradeCategories.Length;
                PopulateNativeBuildablePanel(panel);
            });
            SpawnAmountSliderAndInput(_nativeRow1, upgradeBtn, sliderMax);

            // Row 2: Execute Trade / Raid / Artillery with chosen category & arbitrary amount
            if (rules.trading)
            {
                SpawnDynamicAmountButton(_nativeRow2, upgradeBtn,
                    n => $"遣船运送 {cat.Label} ×{n}",
                    () => net.ExecuteOrSubmit(
                        new GameCommand { kind = "shipTransfer", text = targetShipName, target = targetId, properties = cat.Key, amount = _selectedAmount },
                        $"已派出商船运送 {cat.Label} ×{_selectedAmount} 前往 {ownerLabel}"));
            }

            if (rules.stealing)
            {
                SpawnDynamicAmountButton(_nativeRow2, upgradeBtn,
                    n => $"派艇劫掠 {cat.Label} ×{n}",
                    () => net.ExecuteOrSubmit(
                        new GameCommand { kind = "shipSteal", text = targetShipName, target = targetId, properties = cat.Key, amount = _selectedAmount },
                        $"已派出快艇前往 {buildable.Name} 优先搜刮 {cat.Label} ×{_selectedAmount}"));
            }

            if (rules.friendlyFire)
            {
                SpawnNativeButton(_nativeRow3, upgradeBtn, "火炮轰击此建筑", () =>
                    net.ExecuteOrSubmit(new GameCommand { kind = "shipAttack", text = targetShipName, target = targetId }, $"已向 {buildable.Name} 发射船首火炮齐射"));
            }

            SpawnNativeButton(_nativeRow3, upgradeBtn, "返回我方船镇", () =>
            {
                var p = myHeart != null ? myHeart.transform.position : Vector3.zero;
                FocusCameraOn(p.x, p.z);
                if (myHeart != null) SelectBuildableInGame(myHeart);
            });
            return;
        }

        if (isMyTownheart && isInd)
        {
            var localName = myCommunity?.Name ?? "";
            var otherShips = net.Ships.Where(s => s != null && !string.IsNullOrEmpty(s.name) && s.name != localName).ToArray();
            var myPos = myHeart != null ? myHeart.transform.position : Vector3.zero;
            var sliderMax = Mathf.Max(20, myStock);

            // Row 1: Category selector + Continuous Slider + Arbitrary Integer Input + Max button
            SpawnNativeButton(_nativeRow1, upgradeBtn, $"品类: {cat.Label} (库存 {myStock})", () =>
            {
                _selectedCategoryIdx = (_selectedCategoryIdx + 1) % TradeCategories.Length;
                PopulateNativeBuildablePanel(panel);
            });
            SpawnAmountSliderAndInput(_nativeRow1, upgradeBtn, sliderMax);

            if (otherShips.Length > 0)
            {
                void UpdateFleetHeader(int curAmt) =>
                    _nativeTitle!.text = $"海上舰队指挥台 ({otherShips.Length + 1} 艘船在线 · 当前选定: {cat.Label} ×{curAmt})";
                UpdateFleetHeader(_selectedAmount);
                _amountListeners.Add(UpdateFleetHeader);

                foreach (var ship in otherShips.Take(2))
                {
                    var dx = ship.x - myPos.x;
                    var dz = ship.z - myPos.z;
                    var dist = Mathf.RoundToInt(Mathf.Sqrt(dx * dx + dz * dz));
                    var dir = Compass(dx, dz);
                    var targetShip = ship.name;
                    var label = ship.ownerName;

                    SpawnNativeButton(_nativeRow2, upgradeBtn, $"切至: {label} ({dir}{dist}m)", () => FocusCameraOn(ship.x, ship.z));
                    if (rules.trading)
                        SpawnDynamicAmountButton(_nativeRow2, upgradeBtn,
                            n => $"运送{cat.Label}×{n}→{label}",
                            () => net.ExecuteOrSubmit(
                                new GameCommand { kind = "shipTransfer", text = targetShip, properties = cat.Key, amount = _selectedAmount },
                                $"已向 {label} 派出商船 ({cat.Label} ×{_selectedAmount})"));
                    if (rules.stealing)
                        SpawnDynamicAmountButton(_nativeRow3, upgradeBtn,
                            n => $"劫掠{cat.Label}×{n}→{label}",
                            () => net.ExecuteOrSubmit(
                                new GameCommand { kind = "shipSteal", text = targetShip, properties = cat.Key, amount = _selectedAmount },
                                $"已向 {label} 派出登船掠夺快艇 ({cat.Label} ×{_selectedAmount})"));
                    if (rules.friendlyFire)
                        SpawnNativeButton(_nativeRow3, upgradeBtn, $"火炮齐射→{label}", () =>
                            net.ExecuteOrSubmit(new GameCommand { kind = "shipAttack", text = targetShip }, $"已向 {label} 发射 3 发重炮齐射"));
                }
            }
            else
            {
                void UpdatePracticeHeader(int curAmt) =>
                    _nativeTitle!.text = $"舰队指挥台 (好友加入后点击对方建筑即可交互 · 当前已选: {cat.Label} ×{curAmt})";
                UpdatePracticeHeader(_selectedAmount);
                _amountListeners.Add(UpdatePracticeHeader);

                SpawnDynamicAmountButton(_nativeRow2, upgradeBtn,
                    n => $"遣船运{cat.Label}×{n} (演练)",
                    () => net.ExecuteOrSubmit(
                        new GameCommand { kind = "shipTransfer", text = "__practice__", properties = cat.Key, amount = _selectedAmount },
                        $"已派出 3D 商船运送 {cat.Label} ×{_selectedAmount} 驶向靶船"));
                SpawnDynamicAmountButton(_nativeRow2, upgradeBtn,
                    n => $"派艇抢{cat.Label}×{n} (演练)",
                    () => net.ExecuteOrSubmit(
                        new GameCommand { kind = "shipSteal", text = "__practice__", properties = cat.Key, amount = _selectedAmount },
                        $"已派出 3D 劫掠快艇前往靶船搜刮 {cat.Label} ×{_selectedAmount}"));
                SpawnNativeButton(_nativeRow2, upgradeBtn, "船首火炮齐射 (演练)", () =>
                    net.ExecuteOrSubmit(new GameCommand { kind = "shipAttack", text = "__practice__" }, "已向海面靶船发射 3 发抛物线火炮齐射"));
            }
            return;
        }

        if (isMyTownheart && !isInd)
        {
            var crew = net.Crew.Where(c => c != null && c.id != net.Captain).ToArray();
            if (crew.Length == 0)
            {
                _nativeTitle.text = "同船共建指挥台 (等待更多船员加入 · 按 M 打开海图掌舵航行)";
                _nativeRow1.gameObject.SetActive(false);
                _nativeRow2.gameObject.SetActive(false);
                _nativeRow3.gameObject.SetActive(false);
            }
            else
            {
                _nativeTitle.text = $"同船共建指挥台 · 在线船员 ({net.Crew.Length}人)";
                foreach (var member in crew.Take(3))
                {
                    var peerId = member.id;
                    var peerName = member.name;
                    SpawnNativeButton(_nativeRow1, upgradeBtn, $"移交船长: {peerName}", () =>
                    {
                        net.TransferCaptain(peerId);
                        net.ShowNotice($"已将掌舵船长移交给 {peerName}");
                    });
                }
            }
        }
    }

    private static void EnsureNativeDrawer(BuildablePanel panel, Image? headerImg, TextMeshProUGUI templateTmp)
    {
        if (_nativeDrawer != null && _nativeDrawer.transform.parent == panel.transform)
            return;

        if (_nativeDrawer != null) UnityEngine.Object.Destroy(_nativeDrawer);

        _nativeDrawer = new GameObject("MultiplayerNativeSection", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        _nativeDrawer.transform.SetParent(panel.transform, false);

        var rt = _nativeDrawer.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = new Vector2(0f, 8f);
        rt.sizeDelta = new Vector2(0f, 0f);

        var bg = _nativeDrawer.GetComponent<Image>();
        if (headerImg != null && headerImg.sprite != null)
        {
            bg.sprite = headerImg.sprite;
            bg.type = headerImg.type;
            bg.color = new Color(0.98f, 0.95f, 0.88f, 0.98f);
        }
        else
        {
            EnsureAssets();
            bg.sprite = _pillSprite;
            bg.type = Image.Type.Sliced;
            bg.color = ColParchment;
        }

        var vlayout = _nativeDrawer.GetComponent<VerticalLayoutGroup>();
        vlayout.padding = new RectOffset(14, 14, 10, 10);
        vlayout.spacing = 6f;
        vlayout.childControlWidth = true;
        vlayout.childControlHeight = true;
        vlayout.childForceExpandWidth = true;
        vlayout.childForceExpandHeight = false;

        var fitter = _nativeDrawer.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // Native TMP Title cloned from BuildablePanel._name
        var titleGo = UnityEngine.Object.Instantiate(templateTmp.gameObject, _nativeDrawer.transform, false);
        titleGo.name = "NativeFleetTitle";
        _nativeTitle = titleGo.GetComponent<TextMeshProUGUI>();
        _nativeTitle.fontSize = 14f;
        _nativeTitle.color = ColInk;
        _nativeTitle.alignment = TextAlignmentOptions.MidlineLeft;
        var titleLe = titleGo.GetComponent<LayoutElement>() ?? titleGo.AddComponent<LayoutElement>();
        titleLe.preferredHeight = 22f;

        _nativeRow1 = CreateNativeRow(_nativeDrawer.transform, "NativeActionRow1");
        _nativeRow2 = CreateNativeRow(_nativeDrawer.transform, "NativeActionRow2");
        _nativeRow3 = CreateNativeRow(_nativeDrawer.transform, "NativeActionRow3");
    }

    private static RectTransform CreateNativeRow(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup));
        go.transform.SetParent(parent, false);
        var hl = go.GetComponent<HorizontalLayoutGroup>();
        hl.spacing = 6f;
        hl.childControlWidth = true;
        hl.childControlHeight = true;
        hl.childForceExpandWidth = true;
        hl.childForceExpandHeight = false;
        return go.GetComponent<RectTransform>();
    }

    private static void ClearNativeButtons()
    {
        foreach (var node in _nativeNodes)
            if (node != null) UnityEngine.Object.Destroy(node);
        _nativeNodes.Clear();
        _amountListeners.Clear();
        if (_nativeRow1 != null) _nativeRow1.gameObject.SetActive(false);
        if (_nativeRow2 != null) _nativeRow2.gameObject.SetActive(false);
        if (_nativeRow3 != null) _nativeRow3.gameObject.SetActive(false);
    }

    private static void NotifyAmountChanged(int nextAmount)
    {
        _selectedAmount = Mathf.Clamp(nextAmount, 1, 9999);
        foreach (var listener in _amountListeners.ToArray())
        {
            try { listener(_selectedAmount); } catch { }
        }
    }

    private static void SpawnAmountSliderAndInput(RectTransform row, Button templateBtn, int maxStock)
    {
        EnsureAssets();
        row.gameObject.SetActive(true);

        var container = new GameObject("AmountSliderBox", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        container.transform.SetParent(row, false);
        var hl = container.GetComponent<HorizontalLayoutGroup>();
        hl.spacing = 6f;
        hl.childAlignment = TextAnchor.MiddleCenter;
        hl.childControlWidth = true;
        hl.childControlHeight = true;
        hl.childForceExpandWidth = false;
        hl.childForceExpandHeight = false;

        var boxLe = container.GetComponent<LayoutElement>();
        boxLe.flexibleWidth = 1.6f;
        boxLe.preferredHeight = 32f;

        // 1. Continuous Integer Slider (1 .. max(20, maxStock, _selectedAmount))
        var sliderGo = new GameObject("QuantitySlider", typeof(RectTransform), typeof(Slider), typeof(LayoutElement));
        sliderGo.transform.SetParent(container.transform, false);
        var sliderLe = sliderGo.GetComponent<LayoutElement>();
        sliderLe.flexibleWidth = 1f;
        sliderLe.minWidth = 96f;
        sliderLe.preferredHeight = 22f;

        var bgGo = CreatePanel("TrackBg", sliderGo.transform, _pillSprite!);
        bgGo.GetComponent<Image>().color = new Color(0.18f, 0.12f, 0.08f, 0.90f);
        var bgRt = bgGo.GetComponent<RectTransform>();
        bgRt.anchorMin = new Vector2(0f, 0.25f);
        bgRt.anchorMax = new Vector2(1f, 0.75f);
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        var fillArea = new GameObject("FillArea", typeof(RectTransform));
        fillArea.transform.SetParent(sliderGo.transform, false);
        var fillAreaRt = fillArea.GetComponent<RectTransform>();
        fillAreaRt.anchorMin = new Vector2(0f, 0.25f);
        fillAreaRt.anchorMax = new Vector2(1f, 0.75f);
        fillAreaRt.offsetMin = new Vector2(4f, 0f);
        fillAreaRt.offsetMax = new Vector2(-4f, 0f);

        var fillGo = CreatePanel("Fill", fillArea.transform, _pillSprite!);
        fillGo.GetComponent<Image>().color = ColGold;
        var fillRt = fillGo.GetComponent<RectTransform>();
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;

        var handleArea = new GameObject("HandleArea", typeof(RectTransform));
        handleArea.transform.SetParent(sliderGo.transform, false);
        var handleAreaRt = handleArea.GetComponent<RectTransform>();
        handleAreaRt.anchorMin = Vector2.zero;
        handleAreaRt.anchorMax = Vector2.one;
        handleAreaRt.offsetMin = new Vector2(6f, 0f);
        handleAreaRt.offsetMax = new Vector2(-6f, 0f);

        var handleGo = CreatePanel("Handle", handleArea.transform, _dotSprite!);
        handleGo.GetComponent<Image>().color = ColCream;
        var handleRt = handleGo.GetComponent<RectTransform>();
        handleRt.sizeDelta = new Vector2(16f, 16f);

        var slider = sliderGo.GetComponent<Slider>();
        slider.fillRect = fillRt;
        slider.handleRect = handleRt;
        slider.targetGraphic = handleGo.GetComponent<Image>();
        slider.direction = Slider.Direction.LeftToRight;
        slider.wholeNumbers = true;
        slider.minValue = 1f;
        slider.maxValue = Mathf.Max(20, Mathf.Max(maxStock, _selectedAmount));
        slider.SetValueWithoutNotify(_selectedAmount);

        // 2. Direct Arbitrary Integer InputField (1 .. 9999)
        var inputGo = CreatePanel("QuantityInput", container.transform, _pillSprite!);
        inputGo.GetComponent<Image>().color = new Color(0.14f, 0.10f, 0.07f, 0.94f);
        var inputLe = inputGo.AddComponent<LayoutElement>();
        inputLe.preferredWidth = 54f;
        inputLe.minWidth = 50f;
        inputLe.preferredHeight = 28f;

        var inputText = CreateText(inputGo.transform, _selectedAmount.ToString(), 13, FontStyle.Bold, ColCream, TextAnchor.MiddleCenter);
        var textRt = inputText.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(4f, 2f);
        textRt.offsetMax = new Vector2(-4f, -2f);

        var inputField = inputGo.AddComponent<InputField>();
        inputField.textComponent = inputText;
        inputField.contentType = InputField.ContentType.IntegerNumber;
        inputField.characterLimit = 4;
        inputField.SetTextWithoutNotify(_selectedAmount.ToString());

        var suppressSync = false;
        slider.onValueChanged.AddListener(v =>
        {
            if (suppressSync) return;
            suppressSync = true;
            var val = Mathf.Clamp(Mathf.RoundToInt(v), 1, 9999);
            inputField.SetTextWithoutNotify(val.ToString());
            NotifyAmountChanged(val);
            suppressSync = false;
        });

        inputField.onValueChanged.AddListener(raw =>
        {
            if (suppressSync) return;
            if (!int.TryParse(raw, out var parsed) || parsed < 1) return;
            suppressSync = true;
            var val = Mathf.Clamp(parsed, 1, 9999);
            if (val > slider.maxValue) slider.maxValue = val;
            slider.SetValueWithoutNotify(val);
            NotifyAmountChanged(val);
            suppressSync = false;
        });

        inputField.onEndEdit.AddListener(raw =>
        {
            if (!int.TryParse(raw, out var parsed) || parsed < 1)
                inputField.SetTextWithoutNotify(_selectedAmount.ToString());
        });

        // 3. One-click "全部" (Max stock) button
        SpawnNativeButton(container.GetComponent<RectTransform>(), templateBtn, "全部", () =>
        {
            var targetMax = Mathf.Clamp(maxStock > 0 ? maxStock : 20, 1, 9999);
            suppressSync = true;
            if (targetMax > slider.maxValue) slider.maxValue = targetMax;
            slider.SetValueWithoutNotify(targetMax);
            inputField.SetTextWithoutNotify(targetMax.ToString());
            NotifyAmountChanged(targetMax);
            suppressSync = false;
        }, preferredWidth: 52f);

        _nativeNodes.Add(container);
    }

    private static Button SpawnDynamicAmountButton(RectTransform row, Button templateBtn, Func<int, string> labelFormatter, Action onClick)
    {
        var btn = SpawnNativeButton(row, templateBtn, labelFormatter(_selectedAmount), onClick);
        var tmp = btn.GetComponentInChildren<TextMeshProUGUI>(true);
        if (tmp != null)
            _amountListeners.Add(amt => { if (tmp != null) tmp.text = labelFormatter(amt); });
        return btn;
    }

    private static Button SpawnNativeButton(RectTransform row, Button templateBtn, string label, Action onClick, float preferredWidth = -1f)
    {
        row.gameObject.SetActive(true);
        var cloneGo = UnityEngine.Object.Instantiate(templateBtn.gameObject, row, false);
        cloneGo.name = $"NativeBtn_{label}";
        cloneGo.SetActive(true);

        // Hide any research lock indicator inside cloned upgrade button
        for (var i = 0; i < cloneGo.transform.childCount; i++)
        {
            var child = cloneGo.transform.GetChild(i);
            if (child.GetComponent<TextMeshProUGUI>() == null && child.GetComponentInChildren<TextMeshProUGUI>() == null)
                child.gameObject.SetActive(false);
        }

        // Remove any tooltip from the original upgrade button
        foreach (var comp in cloneGo.GetComponents<MonoBehaviour>())
        {
            if (comp != null && comp.GetType().Name.Contains("Tooltip"))
                UnityEngine.Object.Destroy(comp);
        }

        var btn = cloneGo.GetComponent<Button>();
        btn.interactable = true;
        btn.onClick = new Button.ButtonClickedEvent();
        btn.onClick.AddListener(() => onClick());

        var tmp = cloneGo.GetComponentInChildren<TextMeshProUGUI>(true);
        if (tmp != null)
        {
            tmp.gameObject.SetActive(true);
            tmp.text = label;
            tmp.fontSize = 13f;
            tmp.enableAutoSizing = false;
        }

        var le = cloneGo.GetComponent<LayoutElement>() ?? cloneGo.AddComponent<LayoutElement>();
        if (preferredWidth > 0f)
        {
            le.flexibleWidth = 0f;
            le.preferredWidth = preferredWidth;
            le.minWidth = preferredWidth;
        }
        else
        {
            le.flexibleWidth = 1f;
        }
        le.preferredHeight = 32f;
        le.minHeight = 30f;

        _nativeNodes.Add(cloneGo);
        return btn;
    }

    private static string ResolveOwnerLabel(string? communityName)
    {
        if (string.IsNullOrEmpty(communityName)) return "友方船镇";
        var ship = Plugin.Instance?.Network?.Ships.FirstOrDefault(s => s != null && s.name == communityName);
        if (ship != null && !string.IsNullOrWhiteSpace(ship.ownerName))
            return ship.ownerName;
        return communityName!;
    }

    internal static void FocusCameraOn(float x, float z)
    {
        try
        {
            if (LiveNavigation.ViewOpen && GameManager.WorldMapManager?.WorldMap != null)
                GameManager.WorldMapManager.WorldMap.Close();
            var cam = CameraController.Instance;
            if (cam == null) return;
            var pos = cam.transform.position;
            cam.transform.position = new Vector3(x, pos.y, z);
        }
        catch { }
    }

    internal static void SelectBuildableInGame(Buildable buildable)
    {
        try
        {
            var panel = Resources.FindObjectsOfTypeAll<BuildablePanel>().FirstOrDefault(p => p != null && p.gameObject.scene.IsValid());
            if (panel != null)
            {
                panel.gameObject.SetActive(true);
                AccessTools.DeclaredMethod(typeof(BuildablePanel), "Initialize", new[] { typeof(Buildable), typeof(bool) })
                    ?.Invoke(panel, new object[] { buildable, true });
            }
        }
        catch { }
    }

    private static string Compass(float dx, float dz)
    {
        var angle = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        var dirs = new[] { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };
        return dirs[Mathf.RoundToInt(angle / 45f) % 8];
    }

    // =========================================================================
    // 2. MINIMAL TOP-RIGHT CONNECTION BADGE & TOAST (Zero Bloated Control Tables)
    // =========================================================================

    private static void EnsureAssets()
    {
        if (_cjkFont == null)
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (f != null && f.HasCharacter('船') && f.HasCharacter('联'))
                {
                    _cjkFont = f;
                    break;
                }
            }
            if (_cjkFont == null)
            {
                try
                {
                    _cjkFont = Font.CreateDynamicFontFromOSFont(
                        new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Segoe UI" }, 15);
                }
                catch { }
            }
            if (_cjkFont == null)
                _cjkFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        if (_pillSprite == null)
            _pillSprite = MakeRoundedSprite(64, 14f, ColDriftwood, ColInk, 2.5f);
        if (_dotSprite == null)
            _dotSprite = MakeRoundedSprite(24, 11f, Color.white, Color.clear, 0f);
    }

    private void BuildMinimalOverlay()
    {
        var rootGo = new GameObject("FlotsamMultiplayerHudCanvas");
        DontDestroyOnLoad(rootGo);

        _canvas = rootGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 29990;

        var scaler = rootGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        rootGo.AddComponent<GraphicRaycaster>();
        _canvasRect = rootGo.GetComponent<RectTransform>();

        // Guest Main-Menu Waiting Banner
        _waitBanner = CreatePanel("GuestWaitBanner", rootGo.transform, _pillSprite!);
        var waitRt = _waitBanner.GetComponent<RectTransform>();
        waitRt.anchorMin = new Vector2(0.5f, 1f);
        waitRt.anchorMax = new Vector2(0.5f, 1f);
        waitRt.pivot = new Vector2(0.5f, 1f);
        waitRt.anchoredPosition = new Vector2(0f, -24f);
        waitRt.sizeDelta = new Vector2(640f, 48f);
        var waitLayout = _waitBanner.AddComponent<HorizontalLayoutGroup>();
        waitLayout.padding = new RectOffset(18, 18, 8, 8);
        waitLayout.childAlignment = TextAnchor.MiddleCenter;
        _waitText = CreateText(_waitBanner.transform, "正在连接房主世界...", 14, FontStyle.Bold, ColGold, TextAnchor.MiddleCenter);
        _waitBanner.SetActive(false);

        // Top-Right Compact Status Pill (Draggable)
        var anchorGo = new GameObject("HudAnchor", typeof(RectTransform));
        anchorGo.transform.SetParent(rootGo.transform, false);
        _anchor = anchorGo.GetComponent<RectTransform>();
        _anchor.anchorMin = new Vector2(1f, 1f);
        _anchor.anchorMax = new Vector2(1f, 1f);
        _anchor.pivot = new Vector2(1f, 1f);
        _anchor.anchoredPosition = _panelPos;

        var anchorLayout = anchorGo.AddComponent<VerticalLayoutGroup>();
        anchorLayout.spacing = 6f;
        anchorLayout.childAlignment = TextAnchor.UpperRight;
        anchorLayout.childControlWidth = true;
        anchorLayout.childControlHeight = true;
        anchorLayout.childForceExpandWidth = false;
        anchorLayout.childForceExpandHeight = false;
        var anchorFitter = anchorGo.AddComponent<ContentSizeFitter>();
        anchorFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        anchorFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _pillRoot = CreatePanel("CompactStatusPill", _anchor, _pillSprite!);
        var pillLayout = _pillRoot.AddComponent<HorizontalLayoutGroup>();
        pillLayout.padding = new RectOffset(12, 12, 6, 6);
        pillLayout.spacing = 7f;
        pillLayout.childAlignment = TextAnchor.MiddleCenter;
        pillLayout.childControlWidth = true;
        pillLayout.childControlHeight = true;
        pillLayout.childForceExpandWidth = false;

        var dotGo = CreatePanel("Dot", _pillRoot.transform, _dotSprite!);
        _statusDot = dotGo.GetComponent<Image>();
        _statusDot.color = ColSeafoam;
        var dotLe = dotGo.AddComponent<LayoutElement>();
        dotLe.preferredWidth = 9f;
        dotLe.preferredHeight = 9f;

        _pillText = CreateText(_pillRoot.transform, "独立船队 · 点击城镇核心或友船建筑交互", 12, FontStyle.Bold, ColCream, TextAnchor.MiddleLeft);

        var drag = _pillRoot.AddComponent<DragHandle>();
        drag.Hud = this;

        // Non-blocking Toast below pill
        _toastBox = CreatePanel("ToastNotice", _anchor, _pillSprite!);
        var toastLayout = _toastBox.AddComponent<HorizontalLayoutGroup>();
        toastLayout.padding = new RectOffset(12, 12, 6, 6);
        toastLayout.childControlWidth = true;
        toastLayout.childControlHeight = true;
        _toastText = CreateText(_toastBox.transform, "", 12, FontStyle.Bold, ColGold, TextAnchor.MiddleLeft);
        _toastBox.SetActive(false);
    }

    private void RefreshStatusOverlay()
    {
        var net = Plugin.Instance?.Network;
        var ready = GameSession.Ready();
        var isInd = Plugin.Options.mode == "independent";
        var status = net?.Status ?? Plugin.Instance?.Status ?? "等待连接";

        if (_waitBanner != null)
        {
            var showWait = Plugin.Options.role == "client" && !ready;
            if (_waitBanner.activeSelf != showWait) _waitBanner.SetActive(showWait);
            if (showWait && _waitText != null)
                _waitText.text = $"{status} · 房主载入世界后将自动同步进入，请勿手动点击新游戏";
        }

        if (_pillText != null)
        {
            var ships = net?.Ships ?? Array.Empty<FleetShipState>();
            var count = Math.Max(1, ships.Length);
            _pillText.text = ready
                ? (isInd ? $"独立船队 · {count}艘船 · 点击城镇核心或友船建筑操作 (F8)" : $"同船共建 · {Math.Max(1, net?.Crew.Length ?? 1)}人 (F8)")
                : $"联机就绪 · {status} (F8)";
        }

        if (_statusDot != null)
            _statusDot.color = net != null && net.Connected && ready ? ColSeafoam : ColGold;

        if (_toastBox != null && _toastText != null)
        {
            var showToast = net != null && Time.realtimeSinceStartup < net.NoticeUntil && !string.IsNullOrEmpty(net.NoticeText);
            if (_toastBox.activeSelf != showToast) _toastBox.SetActive(showToast);
            if (showToast && net != null)
            {
                _toastText.text = net.NoticeText;
                _toastText.color = net.NoticeError ? ColDanger : ColGold;
            }
        }
    }

    internal void OnDragDelta(Vector2 delta)
    {
        if (_canvas == null || _anchor == null) return;
        var scale = _canvas.scaleFactor > 0.01f ? _canvas.scaleFactor : 1f;
        _panelPos += delta / scale;
        _anchor.anchoredPosition = _panelPos;
    }

    internal void OnDragEnd()
    {
        PlayerPrefs.SetFloat(PrefKeyX, _panelPos.x);
        PlayerPrefs.SetFloat(PrefKeyY, _panelPos.y);
        PlayerPrefs.Save();
    }

    private static GameObject CreatePanel(string name, Transform parent, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.type = sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        return go;
    }

    private static Text CreateText(Transform parent, string content, int size, FontStyle style, Color color, TextAnchor align)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var txt = go.GetComponent<Text>();
        txt.font = _cjkFont;
        txt.text = content;
        txt.fontSize = size;
        txt.fontStyle = style;
        txt.color = color;
        txt.alignment = align;
        txt.horizontalOverflow = HorizontalWrapMode.Overflow;
        txt.verticalOverflow = VerticalWrapMode.Overflow;
        txt.raycastTarget = false;
        return txt;
    }

    private static Sprite MakeRoundedSprite(int size, float radius, Color fill, Color border, float borderThickness)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        var half = size * 0.5f;
        var pixels = new Color[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var px = x + 0.5f - half;
                var py = y + 0.5f - half;
                var qx = Mathf.Abs(px) - (half - radius - 1f);
                var qy = Mathf.Abs(py) - (half - radius - 1f);
                var dist = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f))
                           + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;

                var outerAlpha = Mathf.Clamp01(0.5f - dist);
                if (outerAlpha <= 0f)
                {
                    pixels[y * size + x] = Color.clear;
                    continue;
                }

                var col = fill;
                if (borderThickness > 0.01f)
                {
                    var innerBlend = Mathf.Clamp01(0.5f - (dist + borderThickness));
                    col = Color.Lerp(border, fill, innerBlend);
                }
                col.a *= outerAlpha;
                pixels[y * size + x] = col;
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        var slice = Mathf.CeilToInt(radius + borderThickness + 2f);
        var borderVec = borderThickness > 0f || radius > 4f ? new Vector4(slice, slice, slice, slice) : Vector4.zero;
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, borderVec);
    }

    private sealed class DragHandle : MonoBehaviour, IDragHandler, IEndDragHandler
    {
        public FleetHud? Hud;
        public void OnDrag(PointerEventData eventData) => Hud?.OnDragDelta(eventData.delta);
        public void OnEndDrag(PointerEventData eventData) => Hud?.OnDragEnd();
    }
}
