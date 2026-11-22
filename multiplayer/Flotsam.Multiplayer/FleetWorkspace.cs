using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Flotsam.Multiplayer;

internal sealed partial class FleetHud
{
    private enum OperationsTab { Guns, Trade, Departure, Repair, Crew }
    private static FleetHud? _instance;
    private static Buildable? _selectedContext;
    private static OperationsTab _operationsTab;
    private static GameObject? _operationsBackdrop;
    private static RectTransform? _operationsTabs;
    private static ScrollRect? _operationsScroll;
    private RectTransform? _fleetCardRect;
    private GameObject? _operationsEntry;
    private TextMeshProUGUI? _operationsEntryText;
    private static int _managementClosedFrame = -1;
    internal static bool OperationsOpen => _operationsBackdrop != null && _operationsBackdrop.activeSelf;
    internal static bool ManagementOpen => OperationsOpen || (_instance?._drawerRoot != null && _instance._drawerRoot.activeSelf);
    internal static bool ManagementInputBlocked => ManagementOpen || _managementClosedFrame == Time.frameCount;

    private void BuildOperationsEntry(Transform parent)
    {
        _instance = this;
        _operationsEntry = CreatePanel("MultiplayerOperationsEntry", parent, _fallbackPanelSprite!, ColDriftwood);
        var rect = _operationsEntry.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 0);
        rect.anchoredPosition = new Vector2(-24, 24);
        rect.sizeDelta = new Vector2(250, 44);
        var button = _operationsEntry.AddComponent<Button>();
        button.targetGraphic = _operationsEntry.GetComponent<Image>();
        button.onClick.AddListener(() => OpenBuildableOperations(_selectedContext ?? DefaultOperationsBuilding()));
        _operationsEntryText = CreateTmp(rect, "本船行动台 [F6]", 15, FontStyles.Bold, ColCream, TextAlignmentOptions.Center);
        Stretch(_operationsEntryText.rectTransform);
        _operationsEntry.SetActive(false);
    }

    private static Buildable? DefaultOperationsBuilding() =>
        FleetEngineBundle.HeartBuildable(Community.PlayerCommunity) ?? Construction.Townheart?.Buildable;

    private static void OpenBuildableOperations(Buildable? building, OperationsTab? tab = null)
    {
        if (building == null || _instance == null || !GameSession.Ready() || FleetCombat.Operating || RequestOpen) return;
        _operationsBuilding = building;
        _operationsTab = tab ?? (building.Community != Community.PlayerCommunity ? OperationsTab.Trade :
            FleetCombat.HasMount(building) || FleetCombat.CanBuildMount(building) ? OperationsTab.Guns :
            building.GetComponent<MooringPoint>() != null ? OperationsTab.Departure :
            BuildingDurability.TryGetDurability(building, out var hp, out var max, out _, out _) && hp < max ? OperationsTab.Repair :
            Plugin.Options.mode == "independent" ? OperationsTab.Trade : OperationsTab.Crew);
        _instance._drawerExpanded = false;
        _instance.RefreshStatusAndDrawer();
        UnityEngine.Object.FindAnyObjectByType<BuildablePanel>()?.Close();
        GameManager.CursorManager?.Deactivate();
        EnsureOperationsWindow();
        _operationsBackdrop!.SetActive(true);
        _operationsBackdrop.transform.SetAsLastSibling();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        RefreshOperations();
        if (_operationsScroll != null) _operationsScroll.verticalNormalizedPosition = 1;
    }

    internal static void CloseOperations()
    {
        if (OperationsOpen) _managementClosedFrame = Time.frameCount;
        if (_operationsBackdrop != null) _operationsBackdrop.SetActive(false);
        _operationsBuilding = null;
        _operationsSignature = "";
    }

    private void CloseFleetWindow()
    {
        _managementClosedFrame = Time.frameCount;
        _drawerExpanded = false;
        RefreshStatusAndDrawer();
    }

    private static void CloseManagementForNavigation()
    {
        CloseOperations();
        if (_instance != null && _instance._drawerExpanded) _instance.CloseFleetWindow();
    }

    private static void OpenWorldMapFromHud()
    {
        CloseManagementForNavigation();
        GameManager.WorldMapManager?.WorldMap?.Open();
    }

    private void RefreshOperationsUi()
    {
        var ready = GameSession.Ready() && Plugin.Instance?.Network?.Connected == true;
        if (!ready || FleetCombat.Operating)
        {
            CloseOperations();
            if (_drawerRoot != null) _drawerRoot.SetActive(false);
        }
        if (_anchor != null) _anchor.gameObject.SetActive(!FleetCombat.Operating);
        ResizeWindow(_fleetCardRect);
        ResizeWindow(_operationsCardRect);
        if (_operationsEntry != null)
        {
            _operationsEntry.SetActive(ready && !FleetCombat.Operating && !RequestOpen && !ManagementOpen);
            if (_operationsEntryText != null)
                _operationsEntryText.text = _selectedContext != null && _selectedContext.Community != Community.PlayerCommunity ? "船只外交 [F6]" :
                    _selectedContext != null && (FleetCombat.HasMount(_selectedContext) || FleetCombat.CanBuildMount(_selectedContext)) ? "火炮与出航 [F6]" : "本船行动台 [F6]";
        }
        if (!ready || RequestOpen || FleetCombat.Operating) return;
        if (Input.GetKeyDown(KeyCode.Escape) && ManagementOpen)
        {
            CloseOperations(); CloseFleetWindow(); return;
        }
        if (Input.GetKeyDown(KeyCode.F6))
        {
            if (OperationsOpen) CloseOperations();
            else OpenBuildableOperations(_selectedContext ?? DefaultOperationsBuilding());
        }
        if (!OperationsOpen) return;
        if (_operationsBuilding == null || MoveConstructionCommands.IsStored(_operationsBuilding)) { CloseOperations(); return; }
        if (Time.unscaledTime < _nextOperationsPoll || Input.GetMouseButton(0) || Input.GetMouseButton(1)) return;
        _nextOperationsPoll = Time.unscaledTime + .45f;
        if (ComputeOperationsSignature(_operationsBuilding) != _operationsSignature) RefreshOperations();
    }

    private static void EnsureOperationsWindow()
    {
        if (_operationsBackdrop != null || _instance?._canvas == null) return;
        _operationsBackdrop = CreateWindowBackdrop("MultiplayerOperationsWindow", _instance._canvas.transform, CloseOperations);
        _operationsCard = CreateWindowCard(_operationsBackdrop.transform, "OperationsCard", ColParchment);
        _operationsCardRect = _operationsCard.GetComponent<RectTransform>();
        var layout = _operationsCard.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(24, 24, 22, 22); layout.spacing = 16;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandHeight = false;
        var header = CreateRow(_operationsCard.transform, 16);
        _operationsTitle = CreateSideTmp(header, "本船行动台", 23, FontStyles.Bold, ColInk, TextAlignmentOptions.MidlineLeft);
        _operationsTitle.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
        CreateSideButton(header, "关闭 [Esc]", ColBtnWood, CloseOperations, fixedWidth: 120, height: 38);
        _operationsSubtitle = CreateSideTmp(_operationsCard.transform, "", 14, FontStyles.Normal, ColSubInk, TextAlignmentOptions.MidlineLeft);
        _operationsSubtitle.textWrappingMode = TextWrappingModes.Normal;
        _operationsTabs = CreateRow(_operationsCard.transform, 10);
        _operationsBody = CreateWindowScroll(_operationsCard.transform, "OperationsContent");
        _operationsScroll = _operationsBody.GetComponentInParent<ScrollRect>();
        _operationsBackdrop.SetActive(false);
    }

    private static void RefreshOperationTabs(bool mine)
    {
        if (_operationsTabs == null) return;
        foreach (Transform child in _operationsTabs) { child.gameObject.SetActive(false); UnityEngine.Object.Destroy(child.gameObject); }
        _operationsTabs.gameObject.SetActive(mine);
        if (!mine) return;
        var tabs = Plugin.Options.mode == "independent"
            ? new[] { OperationsTab.Guns, OperationsTab.Trade, OperationsTab.Departure, OperationsTab.Repair }
            : new[] { OperationsTab.Guns, OperationsTab.Departure, OperationsTab.Repair, OperationsTab.Crew };
        foreach (var tab in tabs)
            CreateSideButton(_operationsTabs, TabLabel(tab), _operationsTab == tab ? ColBtnAccent : ColBtnWood, () =>
            {
                _operationsTab = tab; RefreshOperations();
                if (_operationsScroll != null) _operationsScroll.verticalNormalizedPosition = 1;
            }, height: 40);
    }

    private static string TabLabel(OperationsTab tab) => tab == OperationsTab.Guns ? "火炮" : tab == OperationsTab.Trade ? "交易" :
        tab == OperationsTab.Departure ? "出航" : tab == OperationsTab.Repair ? "损管" : "船员";

    private static void BuildGunOperations(GameSession net, Buildable selected)
    {
        _operationsTitle!.text = "本船炮位";
        _operationsSubtitle!.text = "选择炮位后，使用俯视炮击或进入炮位手动瞄准";
        var platforms = Community.PlayerCommunity?.Buildables.Where(b => b != null && b.BuildPhase == BuildPhase.Finished &&
            !MoveConstructionCommands.IsStored(b) && (FleetCombat.HasMount(b) || FleetCombat.CanBuildMount(b))).ToArray() ?? Array.Empty<Buildable>();
        if (platforms.Length == 0) { CreateSideHint("先在本船建造栈道浮台，再安装废铁火炮。安装需要木材6、金属8，以及一名空闲清醒居民。"); return; }
        var gun = platforms.Contains(selected) ? selected : platforms[0];
        foreach (var platform in platforms)
        {
            var target = platform;
            var row = CreateSideRow(_operationsBody!, 10);
            var installed = FleetCombat.HasMount(target);
            CreateSideButton(row, (target == gun ? "当前 · " : "选择 · ") + target.Name + (installed ? " · 已安装" : " · 可安装"),
                target == gun ? ColBtnAccent : ColBtnWood, () => { _operationsBuilding = target; RefreshOperations(); });
        }
        var controls = CreateSideRow(_operationsBody!, 12);
        if (!FleetCombat.HasMount(gun))
        {
            CreateSideButton(controls, "安装废铁火炮 · 木材6 / 金属8", ColBtnAccent, () =>
                net.ExecuteOrSubmit(new GameCommand { kind = "cannonBuild", target = net.Id(gun) }, "已备料，等待空闲居民安装炮位"), height: 46);
            return;
        }
        var remaining = FleetCombat.BuildRemaining(gun);
        if (remaining > 0) CreateSideHint($"炮位安装中 · 剩余 {remaining:0} 秒，需要空闲清醒的居民施工。");
        else
        {
            CreateSideButton(controls, "俯视炮击", ColBtnAccent, () => StartGunnery(gun, true), height: 48);
            CreateSideButton(controls, "进入炮位瞄准", ColBtnWood, () => StartGunnery(gun, false), height: 48);
            CreateSideHint("俯视：射程与抛物线跟随鼠标，左键炮击。WASD / 中键移动，滚轮缩放。Tab切换视角，Esc退出。");
        }
        var remove = CreateSideRow(_operationsBody!, 10);
        CreateSideButton(remove, "拆卸火炮 · 回收一半材料", ColBtnWood, () => net.ExecuteOrSubmit(new GameCommand
            { kind = "cannonRemove", target = net.Id(gun) }, "火炮已拆卸"));
    }

    private static void StartGunnery(Buildable gun, bool tactical)
    {
        CloseOperations();
        FleetCombat.Enter(gun, tactical);
    }

    private static void BuildRepairOperations(GameSession net, Buildable selected)
    {
        _operationsTitle!.text = "本船损管";
        _operationsSubtitle!.text = "结构耐久和维修材料独立显示；派遣清醒居民处理受损建筑";
        var found = false;
        foreach (var building in Community.PlayerCommunity?.Buildables.ToArray() ?? Array.Empty<Buildable>())
        {
            if (building == null || MoveConstructionCommands.IsStored(building) ||
                !BuildingDurability.TryGetDurability(building, out var hp, out var max, out var emp, out var repair) ||
                (building != selected && hp >= max && emp <= 0 && !repair)) continue;
            found = true;
            CreateSideHint($"{building.Name} · 结构耐久 {Mathf.CeilToInt(hp)}/{Mathf.CeilToInt(max)}" + (emp > 0 ? $" · 电磁瘫痪 {emp:0}s" : ""));
            if (hp < max || emp > 0 || repair) BuildInlineRepairBanner(building, hp, max, emp, repair, net);
        }
        if (!found) CreateSideHint("本船建筑结构完好，暂无需要抢修的建筑。");
    }

    private static GameObject CreateWindowBackdrop(string name, Transform parent, Action close)
    {
        var root = CreatePanel(name, parent, _fallbackPanelSprite!, new Color(0, 0, 0, .45f));
        Stretch(root.GetComponent<RectTransform>());
        var button = root.AddComponent<Button>(); button.targetGraphic = root.GetComponent<Image>();
        button.transition = Selectable.Transition.None; button.onClick.AddListener(() => close());
        return root;
    }

    private static GameObject CreateWindowCard(Transform parent, string name, Color color)
    {
        var card = CreatePanel(name, parent, _fallbackPanelSprite!, color);
        var rect = card.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        ResizeWindow(rect);
        // Stop blank space in the card from bubbling clicks to the backdrop.
        var blocker = card.AddComponent<Button>(); blocker.targetGraphic = card.GetComponent<Image>(); blocker.transition = Selectable.Transition.None;
        return card;
    }

    private static void ResizeWindow(RectTransform? rect)
    {
        if (rect == null || _instance?._canvas == null) return;
        var size = _instance._canvas.GetComponent<RectTransform>().rect.size;
        rect.sizeDelta = new Vector2(Mathf.Min(780, Mathf.Max(240, size.x - 48)), Mathf.Min(720, Mathf.Max(180, size.y - 72)));
    }

    private static RectTransform CreateWindowScroll(Transform parent, string name)
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(ScrollRect), typeof(LayoutElement), typeof(RectMask2D));
        root.transform.SetParent(parent, false);
        root.GetComponent<Image>().color = new Color(0, 0, 0, .025f);
        var element = root.GetComponent<LayoutElement>(); element.flexibleHeight = 1; element.minHeight = 80;
        var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        content.transform.SetParent(root.transform, false);
        var rect = content.GetComponent<RectTransform>(); rect.anchorMin = new Vector2(0, 1); rect.anchorMax = Vector2.one; rect.pivot = new Vector2(.5f, 1); rect.sizeDelta = Vector2.zero;
        var layout = content.GetComponent<VerticalLayoutGroup>(); layout.padding = new RectOffset(4, 4, 8, 16); layout.spacing = 12;
        layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandHeight = false;
        content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = root.GetComponent<ScrollRect>(); scroll.viewport = root.GetComponent<RectTransform>(); scroll.content = rect;
        scroll.horizontal = false; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 32;
        rect.offsetMax = new Vector2(-16, 0);
        var track = CreatePanel("Scrollbar", root.transform, _fallbackPanelSprite!, new Color(.35f, .3f, .25f, .12f));
        var trackRect = track.GetComponent<RectTransform>(); trackRect.anchorMin = new Vector2(1, 0); trackRect.anchorMax = Vector2.one;
        trackRect.pivot = Vector2.one; trackRect.sizeDelta = new Vector2(8, -8); trackRect.anchoredPosition = new Vector2(0, -4);
        var handle = CreatePanel("Handle", track.transform, _fallbackPanelSprite!, new Color(.5f, .45f, .4f, .7f));
        Stretch(handle.GetComponent<RectTransform>());
        var bar = track.AddComponent<Scrollbar>(); bar.direction = Scrollbar.Direction.BottomToTop;
        bar.handleRect = handle.GetComponent<RectTransform>(); bar.targetGraphic = handle.GetComponent<Image>();
        scroll.verticalScrollbar = bar; scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        return rect;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
    }
}
