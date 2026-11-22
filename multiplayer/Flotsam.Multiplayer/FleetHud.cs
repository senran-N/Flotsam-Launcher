extern alias Game;
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Debris = Game::Flotsam;

namespace Flotsam.Multiplayer;

/// <summary>
/// Authentic Native Flotsam Multiplayer HUD &amp; Interaction Suite:
/// - 100% <see cref="TextMeshProUGUI"/> typography bound to Flotsam's own SDF <see cref="TMP_FontAsset"/>
/// - Independent multiplayer windows; native building panels only provide selection context
/// - Collapsible <c>F8</c> Fleet &amp; Co-op Drawer tailored to Shared Co-op (Crew &amp; Captain Handoff/Request)
///   and Independent Fleet (Live Fleet Radar, 1-click Ship/Map Camera Jump, Diplomacy Rules)
/// - <c>Alt + LeftClick</c> 3D/WorldMap Sea Beacons &amp; <c>F9</c> Beacon Quick-Jump
/// </summary>
internal sealed partial class FleetHud : MonoBehaviour
{
    private const string PrefKeyX = "Flotsam.Multiplayer.HudX";
    private const string PrefKeyY = "Flotsam.Multiplayer.HudY";
    private const string PrefKeyExpanded = "Flotsam.Multiplayer.HudExpanded";

    private const int GridSlotsPerPage = 18;

    private Canvas? _canvas;
    private RectTransform? _anchor;
    private Image? _statusDot;
    private TextMeshProUGUI? _pillText;
    private GameObject? _drawerRoot;
    private TextMeshProUGUI? _drawerHeader;
    private TextMeshProUGUI? _drawerSubHeader;
    private RectTransform? _drawerRowsRoot;
    private GameObject? _toastBox;
    private TextMeshProUGUI? _toastText;
    private GameObject? _waitBanner;
    private TextMeshProUGUI? _waitText;

    private readonly List<GameObject> _drawerDynamicRows = new List<GameObject>();
    private bool _drawerExpanded;
    private bool _rulesExpanded;
    private float _nextUiRefresh;
    private float _nextPingAllowed;
    private string _lastRosterSignature = "";
    private Vector2 _panelPos = new Vector2(-18f, -54f);

    // Harvested genuine Flotsam UI skin & item slot prefab
    private static TMP_FontAsset? _nativeTmpFont;
    private static Material? _nativeTmpMaterial;
    private static Sprite? _nativeCardBgSprite;
    private static Sprite? _nativeBtnSprite;
    private static Image.Type _nativeBtnSpriteType = Image.Type.Sliced;
    private static InventoryPanelItemSlot? _nativeSlotPrefab;
    private static Vector2 _nativeSlotCellSize = new Vector2(46f, 46f);
    private static Vector2 _nativeSlotSpacing = new Vector2(6f, 6f);
    private static Sprite? _fallbackPanelSprite;
    private static Sprite? _parchmentCardSprite;
    private static Sprite? _slotSelectBorderSprite;
    private static Sprite? _dotSprite;

    // Standalone operations window state
    private static GameObject? _operationsCard;
    private static RectTransform? _operationsCardRect;
    private static RectTransform? _operationsBody;
    private static TextMeshProUGUI? _operationsTitle;
    private static TextMeshProUGUI? _operationsSubtitle;
    private static Buildable? _operationsBuilding;
    private static float _nextOperationsPoll;
    private static string _operationsSignature = "";
    private static string _selectedItemKey = "";
    private static string _selectedTargetShip = "";
    private static int _selectedAmount = 4;
    private static int _selectedAmountMax = 999;
    private static int _stockPage;
    private static int _wantCategoryIdx = 2;
    private static int _wantAmount = 4;
    private static int _tradeMode;
    private static readonly string[] BarterWantCategories = { "Wood", "Plastic", "Metal", "Food", "Drink", "Tech" };
    private static readonly List<GameObject> _operationsNodes = new List<GameObject>();
    private static readonly List<Action<int>> _amountListeners = new List<Action<int>>();

    private static readonly Color ColInk = new Color(0.135f, 0.110f, 0.086f, 1f);
    private static readonly Color ColSubInk = new Color(0.32f, 0.25f, 0.18f, 0.92f);
    private static readonly Color ColDriftwood = new Color(0.215f, 0.145f, 0.098f, 0.96f);
    private static readonly Color ColDriftwoodCard = new Color(0.265f, 0.182f, 0.125f, 0.94f);
    private static readonly Color ColParchmentTray = new Color(0.18f, 0.125f, 0.085f, 0.16f);
    private static readonly Color ColParchmentCard = new Color(0.89f, 0.835f, 0.725f, 0.75f);
    private static readonly Color ColBtnWood = new Color(0.36f, 0.245f, 0.155f, 0.98f);
    private static readonly Color ColBtnAccent = new Color(0.22f, 0.48f, 0.40f, 0.98f);
    private static readonly Color ColParchment = new Color(0.976f, 0.945f, 0.875f, 0.98f);
    private static readonly Color ColCream = new Color(0.995f, 0.968f, 0.902f, 1f);
    private static readonly Color ColGold = new Color(0.975f, 0.765f, 0.265f, 1f);
    private static readonly Color ColSeafoam = new Color(0.34f, 0.86f, 0.68f, 1f);
    private static readonly Color ColDanger = new Color(0.84f, 0.27f, 0.20f, 0.98f);

    internal static FleetHud Attach(GameObject host)
    {
        var existing = host.GetComponent<FleetHud>();
        return existing != null ? existing : host.AddComponent<FleetHud>();
    }

    private void Awake()
    {
        _instance = this;
        if (PlayerPrefs.HasKey(PrefKeyX) && PlayerPrefs.HasKey(PrefKeyY))
        {
            var x = PlayerPrefs.GetFloat(PrefKeyX, -18f);
            var y = PlayerPrefs.GetFloat(PrefKeyY, -54f);
            if (float.IsFinite(x) && float.IsFinite(y))
                _panelPos = new Vector2(Mathf.Clamp(x, -1600f, -8f), Mathf.Clamp(y, -900f, -8f));
        }
        _drawerExpanded = PlayerPrefs.GetInt(PrefKeyExpanded, 0) == 1;
        EnsureAssets();
        BuildOverlay();
    }

    private void Update()
    {
        RefreshRequests();
        RefreshBattleHud();
        RefreshOperationsUi();
        if (RequestOpen || FleetCombat.Operating) { RefreshStatusAndDrawer(); return; }
        if (Input.GetKeyDown(KeyCode.F8))
            ToggleFleetDrawer();

        if (GameSession.Ready() && !ManagementOpen)
        {
            if (Input.GetKeyDown(KeyCode.F9))
                JumpToLatestBeacon();

            if (Input.GetKeyDown(KeyCode.Home))
                FocusCameraOnOwnShip();

            if (Input.GetKeyDown(KeyCode.V) && !IsTypingInInputField())
                ToggleFollowOwnShip();

            if ((Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) && Input.GetMouseButtonDown(0))
                TrySendCursorSeaPing();

            if ((Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) && Input.GetMouseButtonDown(1))
                TrySend3DHelmCourse();
        }

        if (Time.unscaledTime >= _nextUiRefresh)
        {
            _nextUiRefresh = Time.unscaledTime + 0.25f;
            TryHarvestNativeTheme();
            RefreshStatusAndDrawer();
        }
    }

    private void ToggleFleetDrawer()
    {
        CloseOperations();
        _drawerExpanded = !_drawerExpanded;
        if (!_drawerExpanded) _managementClosedFrame = Time.frameCount;
        if (_drawerExpanded)
        {
            UnityEngine.Object.FindAnyObjectByType<BuildablePanel>()?.Close();
            GameManager.CursorManager?.Deactivate();
        }
        PlayerPrefs.SetInt(PrefKeyExpanded, _drawerExpanded ? 1 : 0);
        PlayerPrefs.Save();
        _lastRosterSignature = "";
        RefreshStatusAndDrawer();
    }

    // =========================================================================
    // 1. CO-OP SEA BEACON PING (Alt + LeftClick in 3D or WorldMap, F9 Jump)
    // =========================================================================

    private static bool IsTypingInInputField()
    {
        var selected = EventSystem.current?.currentSelectedGameObject;
        if (selected == null) return false;
        return selected.GetComponent<TMP_InputField>() != null || selected.GetComponent<InputField>() != null;
    }

    private void ToggleFollowOwnShip()
    {
        ClientOceanAnchor.FollowOwnShip = !ClientOceanAnchor.FollowOwnShip;
        _lastRosterSignature = "";
        Plugin.Instance?.Network?.ShowNotice(
            ClientOceanAnchor.FollowOwnShip
                ? "视角模式：跟随船体航行 (按 V 切换回观海视角)"
                : "视角模式：锚定海面，观看船只破浪驶向资源 (按 V 切换)",
            false);
    }

    private static void TrySend3DHelmCourse()
    {
        if (LiveNavigation.ViewOpen) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (GameManager.CursorManager?.Properties != null) return;
        var net = Plugin.Instance?.Network;
        if (net == null || (!net.IsHost && !net.ReplicaReady)) return;
        if (!net.IsCaptain && Plugin.Options.mode != "independent")
        {
            net.ShowNotice("只有掌舵船长可以控制航向 (按 F8 可一键申请掌舵)", true);
            return;
        }

        var cam = Camera.main;
        if (cam == null) return;
        var ray = cam.ScreenPointToRay(Input.mousePosition);
        Vector3 hitPt;
        var targetDesc = "目标海域";
        if (Physics.Raycast(ray, out var hit, 700f, ~0, QueryTriggerInteraction.Collide))
        {
            hitPt = new Vector3(hit.point.x, 0f, hit.point.z);
            if (hit.collider.GetComponentInParent<Debris>() != null) targetDesc = "前方漂流物资源区";
            else if (hit.collider.GetComponentInParent<Landmark>() != null) targetDesc = "前方岛屿地标";
        }
        else
        {
            var waterPlane = new Plane(Vector3.up, Vector3.zero);
            if (!waterPlane.Raycast(ray, out var dist) || dist <= 0f || dist > 700f) return;
            hitPt = ray.GetPoint(dist);
        }

        var mapPt = FleetOwnedNavigation.SceneToMap(ClientOceanAnchor.ClientToHostPos(hitPt));
        ShipSailingVisuals.SetCourseIndicator(mapPt.x, mapPt.z);
        var encoded = mapPt.x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "," +
                      mapPt.z.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        net.ExecuteOrSubmit(
            new GameCommand { kind = "navigatePlan", x = mapPt.x, z = mapPt.z, text = encoded },
            $"已设定航向：船只正在破浪驶向{targetDesc}");
    }

    private void TrySendCursorSeaPing()
    {
        if (Time.unscaledTime < _nextPingAllowed) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        var net = Plugin.Instance?.Network;
        if (net == null || (!net.IsHost && !net.ReplicaReady)) return;

        if (LiveNavigation.ViewOpen)
        {
            var map = GameManager.WorldMapManager?.WorldMap;
            var mapCam = map != null ? map.GetComponentInChildren<Camera>() : null;
            if (mapCam == null) return;
            var mapRay = mapCam.ScreenPointToRay(Input.mousePosition);
            var waterPlane = new Plane(Vector3.up, new Vector3(0f, map!.Townheart.transform.position.y, 0f));
            if (waterPlane.Raycast(mapRay, out var enter))
            {
                var mapPt = mapRay.GetPoint(enter);
                var hostPt = ClientOceanAnchor.OceanToHostPos(mapPt.x, mapPt.z);
                _nextPingAllowed = Time.unscaledTime + 0.8f;
                net.ExecuteOrSubmit(new GameCommand { kind = "seaPing", x = hostPt.x, z = hostPt.z, text = "海图航标" });
            }
            return;
        }

        var cam = Camera.main;
        if (cam == null) return;
        var ray = cam.ScreenPointToRay(Input.mousePosition);
        var label = "海域集结";
        Vector3 targetScenePt;

        if (Physics.Raycast(ray, out var hit, 600f, ~0, QueryTriggerInteraction.Collide))
        {
            targetScenePt = new Vector3(hit.point.x, 0f, hit.point.z);
            var flotsam = hit.collider.GetComponentInParent<Debris>();
            var landmark = hit.collider.GetComponentInParent<Landmark>();
            var buildable = hit.collider.GetComponentInParent<Buildable>();
            var agent = hit.collider.GetComponentInParent<Agent>();
            if (flotsam != null) label = "发现海上漂流物";
            else if (landmark != null) label = "发现探索地标";
            else if (buildable != null && !string.IsNullOrWhiteSpace(buildable.Name)) label = buildable.Name;
            else if (agent != null && !string.IsNullOrWhiteSpace(agent.Name)) label = agent.Name;
        }
        else
        {
            var plane = new Plane(Vector3.up, Vector3.zero);
            if (!plane.Raycast(ray, out var dist)) return;
            targetScenePt = ray.GetPoint(dist);
        }

        var hostScenePt = ClientOceanAnchor.ClientToHostPos(targetScenePt);
        _nextPingAllowed = Time.unscaledTime + 0.8f;
        net.ExecuteOrSubmit(new GameCommand { kind = "seaPing", x = hostScenePt.x, z = hostScenePt.z, text = label });
    }

    private static void SendCameraCenterPing()
    {
        var net = Plugin.Instance?.Network;
        if (net == null) return;
        var pt = ClientOceanAnchor.ClientToHostPos(GetCameraGroundPoint() ?? Construction.TownheartPosition);
        net.ExecuteOrSubmit(new GameCommand { kind = "seaPing", x = pt.x, z = pt.z, text = "镜头关注点" });
    }

    private static void JumpToLatestBeacon()
    {
        var beacon = FleetPhysicalActors.LatestBeacon;
        if (beacon == null)
        {
            Plugin.Instance?.Network?.ShowNotice("当前没有活跃的海域信标 (按住 Alt+左键可发送信标)", false);
            return;
        }
        if (LiveNavigation.ViewOpen)
        {
            var mapPos = FleetOwnedNavigation.SceneToMap(ClientOceanAnchor.ClientToHostPos(beacon.ScenePos));
            FleetPhysicalActors.PanWorldMapTo(mapPos.x, mapPos.z);
        }
        else
        {
            FocusCameraOn(beacon.ScenePos.x, beacon.ScenePos.z);
        }
    }

    // =========================================================================
    // 2. SELECTION CONTEXT AND INDEPENDENT OPERATIONS
    // =========================================================================

    internal static void OnBuildablePanelInitialize(BuildablePanel __instance, bool refresh)
    {
        try
        {
            if (__instance == null) return;
            if (__instance.GetComponent<BuildablePanelCloseTracker>() == null)
                __instance.gameObject.AddComponent<BuildablePanelCloseTracker>();

            _selectedContext = __instance.Buildable;
            if (_selectedContext != null && _selectedContext.Community != Community.PlayerCommunity)
                OpenBuildableOperations(_selectedContext);

        }
        catch (Exception e)
        {
            Plugin.Log("NativePanel init error: " + e.Message);
        }
    }

    internal static void OnHostBuildablePanelClosed()
    {
        _selectedContext = null;
    }

    internal static void OnBuildablePanelUpdate(BuildablePanel __instance)
    {
        if (__instance == null) return;
        var b = __instance.Buildable;
        if (b == null || b.Community == null) return;

        _selectedContext = b;
        if (b.Community != Community.PlayerCommunity) OpenBuildableOperations(b);
    }

    private static TradeOfferState[] GetActiveOffers(GameSession net) =>
        net.IsHost
            ? FleetInteractions.CaptureOffers()
            : (net.AppliedFrame?.tradeOffers ?? Array.Empty<TradeOfferState>());

    private static string ComputeOperationsSignature(Buildable b)
    {
        var net = Plugin.Instance?.Network;
        if (net == null) return "";
        var myCommunity = Community.PlayerCommunity;
        var isForeign = b.Community != null && myCommunity != null && !ReferenceEquals(b.Community, myCommunity);
        var rules = Plugin.Options.rules;
        var watchedComm = myCommunity;
        Buildable? prio = null;
        var stock = Replication.CollectShipStock(watchedComm, prio);
        var stockSig = string.Join(",", stock.Take(24).Select(s => $"{s.Prop.name}:{s.Count}"));
        BuildingDurability.TryGetDurability(b, out var hp, out var maxHp, out var empRem, out var underRepair);
        var hasBoat = FleetInteractions.ShipHasOperationalBoat(myCommunity);
        var offers = GetActiveOffers(net);
        var offerSig = string.Join(",", offers.Select(o => $"{o.id}:{o.fromShip}>{o.toShip}"));
        var visSig = string.Join(",", net.Ships.Where(s => s != null).Select(s =>
            $"{s.name}:{(FleetVisibility.IsShipVisibleToLocal(s, net.Ships) ? 1 : 0)}:{(s.allies?.Length ?? 0)}:{(s.crippledHeart ? 1 : 0)}"));
        var structures = watchedComm == null ? "" : string.Join(",", watchedComm.Buildables.Where(x => x != null).Select(x =>
        {
            BuildingDurability.TryGetDurability(x, out var current, out _, out var emp, out var repairing);
            return $"{x.GetInstanceID()}:{x.BuildPhase}:{MoveConstructionCommands.IsStored(x)}:{Mathf.CeilToInt(current)}:{emp > 0}:{repairing}:{FleetCombat.HasMount(x)}:{Mathf.CeilToInt(FleetCombat.BuildRemaining(x))}";
        }));
        return $"{net.Ships.Length}|{net.Captain}|{_operationsTab}|{_wantCategoryIdx}:{_wantAmount}|{hasBoat}|{Mathf.CeilToInt(hp)}/{Mathf.CeilToInt(maxHp)}:{(empRem > 0.1f ? 1 : 0)}:{(underRepair ? 1 : 0)}|{rules.trading}:{rules.stealing}:{rules.friendlyFire}|{offerSig}|{visSig}|{stockSig}|{structures}|{string.Join(",",FleetVoyages.Visible.Select(v=>v.id+":"+v.status))}";
    }

    private static void HarvestNativePanelAssets(BuildablePanel panel)
    {
        EnsureAssets();

        var nameTmp = AccessTools.Field(typeof(BuildablePanel), "_name")?.GetValue(panel) as TextMeshProUGUI;
        if (nameTmp != null)
        {
            if (nameTmp.font != null) _nativeTmpFont = nameTmp.font;
            if (nameTmp.fontSharedMaterial != null) _nativeTmpMaterial = nameTmp.fontSharedMaterial;
        }

        if (_nativeSlotPrefab == null)
        {
            var invView = panel.GetComponentInChildren<InventoryView>(true)
                          ?? Resources.FindObjectsOfTypeAll<InventoryView>().FirstOrDefault(v => v != null && v.slotPrefab != null);
            if (invView != null && invView.slotPrefab != null)
            {
                _nativeSlotPrefab = invView.slotPrefab;
                var srcGrid = invView.inventoryGrid != null ? invView.inventoryGrid.GetComponent<GridLayoutGroup>() : null;
                if (srcGrid != null && srcGrid.cellSize.x >= 30f)
                {
                    var side = Mathf.Clamp(srcGrid.cellSize.x, 42f, 46f);
                    _nativeSlotCellSize = new Vector2(side, side);
                    _nativeSlotSpacing = new Vector2(
                        Mathf.Clamp(srcGrid.spacing.x, 4f, 6f),
                        Mathf.Clamp(srcGrid.spacing.y, 4f, 6f));
                }
            }
        }

        if (_nativeCardBgSprite == null)
        {
            var headerImg = AccessTools.Field(typeof(BuildablePanel), "_headerImage")?.GetValue(panel) as Image;
            var iconImg = AccessTools.Field(typeof(BuildablePanel), "_icon")?.GetValue(panel) as Image;
            Image? bestBg = null;
            var bestArea = 0f;
            foreach (var img in panel.GetComponentsInChildren<Image>(true))
            {
                if (img == null || img == headerImg || img == iconImg || img.sprite == null) continue;
                if (img.type != Image.Type.Sliced || img.sprite.border.sqrMagnitude < 4f) continue;
                if (img.color.a < 0.5f) continue;
                if (img.GetComponentInParent<Button>() != null
                    || img.GetComponentInParent<Slider>() != null
                    || img.GetComponentInParent<Toggle>() != null
                    || img.GetComponentInParent<InventoryView>() != null)
                    continue;

                var rect = img.rectTransform.rect;
                var area = rect.width * rect.height;
                if (rect.width >= 180f && rect.height >= 90f && area > bestArea)
                {
                    bestArea = area;
                    bestBg = img;
                }
            }

            if (bestBg != null && bestBg.sprite != null)
            {
                _nativeCardBgSprite = bestBg.sprite;
            }
        }

        if (_nativeBtnSprite == null)
        {
            var dlg = Resources.FindObjectsOfTypeAll<DialogPanel>().FirstOrDefault(d => d != null);
            var okGo = dlg != null ? AccessTools.Field(typeof(DialogPanel), "_buttonOk")?.GetValue(dlg) as GameObject : null;
            var okImg = okGo != null ? okGo.GetComponent<Image>() : null;
            if (okImg != null && okImg.sprite != null && okImg.sprite.border.sqrMagnitude > 0f)
            {
                _nativeBtnSprite = okImg.sprite;
                _nativeBtnSpriteType = okImg.type;
            }
        }
    }

    private static void RefreshOperations()
    {
        var buildable = _operationsBuilding;
        var net = Plugin.Instance?.Network;
        if (buildable == null || net == null || !GameSession.Ready()) { CloseOperations(); return; }
        EnsureOperationsWindow();
        if (_operationsCard == null || _operationsBody == null) return;
        ClearOperationNodes();
        _operationsCard.SetActive(true);
        var mine = buildable.Community == Community.PlayerCommunity;
        _operationsTitle!.text = mine ? "本船行动台" : "船只外交";
        _operationsSubtitle!.text = mine ? buildable.Name : ResolveOwnerLabel(Replication.ShipKey(buildable.Community));
        RefreshOperationTabs(mine);
        if (!mine)
            PopulateForeignBuildingCard(buildable, Community.PlayerCommunity, FleetEngineBundle.HeartBuildable(Community.PlayerCommunity), net);
        else if (_operationsTab == OperationsTab.Guns)
            BuildGunOperations(net, buildable);
        else if (_operationsTab == OperationsTab.Trade)
            PopulateOwnTownheartFleetCard(Community.PlayerCommunity, FleetEngineBundle.HeartBuildable(Community.PlayerCommunity), net);
        else if (_operationsTab == OperationsTab.Departure)
        {
            _operationsTitle.text = "本船出航";
            _operationsSubtitle.text = "居民与小艇实际出发；可在这里召回正在执行任务的船员";
            BuildDepartureControls(net);
            if (!FleetVoyages.Visible.Any(v => v.fromShip == Replication.ShipKey(Community.PlayerCommunity)))
                CreateSideHint("当前没有航行中的运输或掠夺小队。交易运输请前往交易页安排。 ");
        }
        else if (_operationsTab == OperationsTab.Repair)
            BuildRepairOperations(net, buildable);
        else
            PopulateSharedCoopCard(net);
        _operationsSignature = ComputeOperationsSignature(buildable);
    }

    private static void BuildInlineRepairBanner(
        Buildable buildable,
        float hp,
        float maxHp,
        float empRem,
        bool underRepair,
        GameSession net)
    {
        var targetId = net.Id(buildable);
        var reqs = buildable.Properties?.RequiredResources;
        var recipeTotal = reqs != null ? reqs.Sum(r => r?.Amount ?? 0) : 6;
        var costCount = Mathf.Max(1, FleetMechanicsMath.ComputeRepairCostCount(hp, maxHp, recipeTotal));

        var repairRow = CreateSideRow(_operationsBody!, 6f);
        if (underRepair)
        {
            CreateSideButton(repairRow, $"🔨 船员正在锤击抢修中 ({Mathf.CeilToInt(hp)}/{Mathf.CeilToInt(maxHp)})", ColBtnAccent, () => { }, height: 32f);
        }
        else
        {
            var statusTag = empRem > 0.1f ? "清除EMP并" : "";
            CreateSideButton(
                repairRow,
                $"🔨 派清醒船员{statusTag}抢修 (耗材×{costCount})",
                ColDanger,
                () => net.ExecuteOrSubmit(
                    new GameCommand { kind = "buildingRepair", target = targetId },
                    $"已调度船员抢修 {buildable.Name}"),
                height: 32f);
        }
    }

    private static void BuildBarterComposerSection(
        string targetShipName,
        long targetBuildingId,
        string ownerLabel,
        ItemProperties selectedGiveProp,
        GameSession net)
    {
        var box = CreatePanel("BarterComposerBox", _operationsBody!, _fallbackPanelSprite!, ColParchmentTray);
        var vl = box.AddComponent<VerticalLayoutGroup>();
        vl.padding = new RectOffset(8, 8, 6, 6);
        vl.spacing = 4f;
        vl.childControlWidth = true;
        vl.childControlHeight = true;
        vl.childForceExpandWidth = true;
        vl.childForceExpandHeight = false;
        _operationsNodes.Add(box);

        var wantKey = BarterWantCategories[Mathf.Clamp(_wantCategoryIdx, 0, BarterWantCategories.Length - 1)];
        var wantLabel = Replication.FormatCategoryLabel(wantKey);
        var giveName = Replication.CleanItemName(selectedGiveProp);
        var giveKey = selectedGiveProp.name;

        var ctrlRow = CreateRow(box.transform, 4f);
        CreateSideTmp(ctrlRow, "索取:", 11.5f, FontStyles.Bold, ColInk, TextAlignmentOptions.MidlineLeft)
            .gameObject.AddComponent<LayoutElement>().preferredWidth = 36f;
        CreateSideButton(ctrlRow, "◀", ColBtnWood, () =>
        {
            _wantCategoryIdx = (_wantCategoryIdx - 1 + BarterWantCategories.Length) % BarterWantCategories.Length;
            RefreshOperations();
        }, fixedWidth: 24f, height: 24f);
        var catTmp = CreateSideTmp(ctrlRow, wantLabel, 12f, FontStyles.Bold, ColInk, TextAlignmentOptions.Center);
        catTmp.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        CreateSideButton(ctrlRow, "▶", ColBtnWood, () =>
        {
            _wantCategoryIdx = (_wantCategoryIdx + 1) % BarterWantCategories.Length;
            RefreshOperations();
        }, fixedWidth: 24f, height: 24f);

        CreateSideButton(ctrlRow, "-", ColBtnWood, () =>
        {
            _wantAmount = Mathf.Clamp(_wantAmount - 1, 1, 25);
            RefreshOperations();
        }, fixedWidth: 24f, height: 24f);
        var amtTmp = CreateSideTmp(ctrlRow, $"×{_wantAmount}", 12f, FontStyles.Bold, ColInk, TextAlignmentOptions.Center);
        amtTmp.gameObject.AddComponent<LayoutElement>().preferredWidth = 34f;
        CreateSideButton(ctrlRow, "+", ColBtnWood, () =>
        {
            _wantAmount = Mathf.Clamp(_wantAmount + 1, 1, 25);
            RefreshOperations();
        }, fixedWidth: 24f, height: 24f);

        var submitRow = CreateRow(box.transform, 4f);
        submitRow.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;
        CreateDynamicSideButton(
            submitRow,
            n => $"📜 发起易货：出{giveName}×{n} ⇄ 换{wantLabel}×{_wantAmount}",
            ColBtnWood,
            () => net.ExecuteOrSubmit(
                new GameCommand
                {
                    kind = "shipBarterOffer",
                    target = targetBuildingId,
                    properties = giveKey,
                    amount = _selectedAmount,
                    value = _wantAmount,
                    text = $"{targetShipName}|{wantKey}"
                },
                $"已向 {ownerLabel} 发送双向易货契约"),
            height: 29f);
    }

    private static void PopulateForeignBuildingCard(
        Buildable buildable,
        Community? myCommunity,
        Buildable? myHeart,
        GameSession net)
    {
        var targetShip = Replication.ShipKey(buildable.Community);
        var label = ResolveOwnerLabel(targetShip);
        _operationsTitle!.text = label;
        _operationsSubtitle!.text = "他船 · 经营权属于船主";
        var local = net.Ships.FirstOrDefault(x=>x.name==Replication.ShipKey(myCommunity));
        var other = net.Ships.FirstOrDefault(x=>x.name==targetShip);
        var allied = FleetVisibility.AreAlliedState(local,other);
        var row = CreateSideRow(_operationsBody!,8);
        CreateSideButton(row,allied?"解除同盟":"申请结盟",ColBtnWood,()=>net.ExecuteOrSubmit(
            new GameCommand{kind=allied?"shipAllyBreak":"shipAllyRequest",text=targetShip},allied?"已解除同盟":"已发送结盟申请"));
        CreateSideButton(row,"提出交易",ColBtnAccent,()=>OpenTradePanelForTargetShip(targetShip));
        CreateSideHint("掠夺请从本船码头派员；炮击请进入自己的炮位。船舱物资需实际登船才能接触。");
    }

    private static void PopulateOwnTownheartFleetCard(
        Community? myCommunity,
        Buildable? myHeart,
        GameSession net)
    {
        var rules = Plugin.Options.rules;
        var localName = Replication.ShipKey(myCommunity);
        var otherShips = net.Ships.Where(s => s != null && !string.IsNullOrEmpty(s.name) && s.name != localName).ToArray();
        var myPos = myHeart != null ? myHeart.transform.position : Vector3.zero;

        if (otherShips.Length == 0)
        {
            _operationsTitle!.text = "海上商贸与舰队调度";
            _operationsSubtitle!.text = "当前海域仅有本船 · 好友加入后可在此安排运输";
            var soloRow = CreateSideRow(_operationsBody!, 6f);
            CreateSideButton(soloRow, "打开海图航行 (M)", ColBtnAccent, OpenWorldMapFromHud, height: 31f);
            CreateSideButton(soloRow, "发送海域信标", ColBtnWood, SendCameraCenterPing, height: 31f);
            return;
        }

        if (string.IsNullOrEmpty(_selectedTargetShip) || otherShips.All(s => s.name != _selectedTargetShip))
            _selectedTargetShip = otherShips[0].name;

        var localShipState = net.Ships.FirstOrDefault(s => s != null && s.name == localName);
        var activeTarget = otherShips.First(s => s.name == _selectedTargetShip);
        var activeSpotted = FleetVisibility.IsShipVisibleToLocal(activeTarget, net.Ships);
        var activeAllied = FleetVisibility.AreAlliedState(localShipState, activeTarget);
        var hasBoat = FleetInteractions.ShipHasOperationalBoat(myCommunity);
        var activePos = ResolveShipScenePos(activeTarget);
        var activeDx = activePos.x - myPos.x;
        var activeDz = activePos.z - myPos.z;
        var activeDist = Mathf.RoundToInt(Mathf.Sqrt(activeDx * activeDx + activeDz * activeDz));

        _operationsTitle!.text = $"海上商贸与舰队调度 · {otherShips.Length + 1} 艘同海";
        _operationsSubtitle!.text = activeSpotted
            ? $"当前目标：{activeTarget.ownerName}{(activeAllied ? " [同盟]" : "")} ({Compass(activeDx, activeDz)} {activeDist}m)"
            : $"当前目标：{activeTarget.ownerName} (迷雾遮蔽 · 视野外，可发起远洋易货或结盟)";


        var targetRow=CreateSideRow(_operationsBody!,6f);
        CreateSideButton(targetRow,"收货方："+activeTarget.ownerName,ColBtnWood,()=>
        {
            var next=(Array.FindIndex(otherShips,s=>s.name==_selectedTargetShip)+1)%otherShips.Length;
            _selectedTargetShip=otherShips[next].name;RefreshOperations();
        });
        CreateSideHint(hasBoat?"本次运量上限25件，安排本船居民与小艇运输":"泅渡运量上限4件，最远85米");

        // Genuine Flotsam Item Icon Grid
        var myStock = Replication.CollectShipStock(myCommunity);
        var (selectedProp, maxCount) = BuildNativeCargoGrid(
            myStock,
            $"本船可调配库存 ({myStock.Count} 种) · 点击物品图标装船",
            "本船仓库暂无空闲物资（建造材料与居民手持锁定物品不会误发）");

        if (selectedProp != null && maxCount > 0)
        {
            BuildSelectedItemAndStepper(selectedProp, maxCount);
            var itemName = Replication.CleanItemName(selectedProp);
            var itemKey = selectedProp.name;
            var destShip = activeTarget.name;
            var destLabel = activeTarget.ownerName;

            if (rules.trading)
            {
                var modes=CreateSideRow(_operationsBody!,6f);
                var labels=new[]{"易货报价","赠送运输","漂流货桶"};
                for(var i=0;i<labels.Length;i++)
                {
                    var mode=i;CreateSideButton(modes,labels[i],_tradeMode==i?ColBtnAccent:ColBtnWood,()=>{_tradeMode=mode;RefreshOperations();});
                }
                if(_tradeMode==1)
                {
                var tradeRow = CreateSideRow(_operationsBody!, 6f);
                CreateDynamicSideButton(
                    tradeRow,
                    n => $"赠送 {itemName} ×{n} 给 {destLabel}",
                    ColBtnAccent,
                    () => net.ExecuteOrSubmit(
                        new GameCommand { kind = "shipTransfer", text = destShip, properties = itemKey, amount = _selectedAmount },
                        $"已调度运送 {itemName} ×{_selectedAmount} 前往 {destLabel}"),
                    icon: selectedProp.InventorySprite,
                    height: 33f);
                }

                if (_tradeMode==2 && activeSpotted && activeDist <= 500)
                {
                    var barrelRow = CreateSideRow(_operationsBody!, 6f);
                    CreateDynamicSideButton(
                        barrelRow,
                        n => $"🛢 抛海流木桶漂送 {itemName} ×{Mathf.Min(n, 10)}",
                        ColBtnWood,
                        () => net.ExecuteOrSubmit(
                            new GameCommand { kind = "shipFloatBarrel", text = destShip, properties = itemKey, amount = Mathf.Min(_selectedAmount, 10) },
                            $"已向 {destLabel} 方向抛投海流漂流桶"),
                        height: 27f);
                    CreateSideHint("货桶随原生海流漂动，可能漂失或被他人打捞；不会追踪收货船。");
                }

                if(_tradeMode==0)BuildBarterComposerSection( destShip, 0L, destLabel, selectedProp, net);
            }
            else
            {
                CreateSideHint("跨船贸易规则当前已关闭（房主按 F8 可一键开启）");
            }
        }

    }

    private static void PopulateSharedCoopCard(GameSession net)
    {
        var others = net.Crew.Where(c => c != null && c.id != net.LocalPeer).ToArray();
        var captainMember = net.Crew.FirstOrDefault(c => c != null && c.id == net.Captain);
        var captainName = captainMember?.name ?? (net.IsCaptain ? "我方" : "房主");

        _operationsTitle!.text = $"同船共建指挥台 · 船长: {captainName}";
        _operationsSubtitle!.text = $"在线船员 {Math.Max(1, net.Crew.Length)} 人 · 全员共享仓库与科研 · 按 F8 管理船员";

        if (others.Length > 0 && (net.IsCaptain || net.IsHost))
        {
            var crewRow = CreateSideRow(_operationsBody!, 6f);
            foreach (var member in others.Where(m => m.id != net.Captain).Take(2))
            {
                var peerId = member.id;
                var peerName = member.name;
                CreateSideButton(crewRow, $"移交船长 → {peerName}", ColBtnAccent, () =>
                    net.TransferCaptain(peerId, $"已将掌舵船长移交给 {peerName}"), height: 30f);
            }
        }
        else if (!net.IsCaptain)
        {
            var reqRow = CreateSideRow(_operationsBody!, 6f);
            CreateSideButton(reqRow, "向船长申请接管掌舵权", ColBtnAccent, () =>
                net.ExecuteOrSubmit(new GameCommand { kind = "captainRequest" }, "已向船长发送掌舵申请"), height: 30f);
        }

        var navRow = CreateSideRow(_operationsBody!, 6f);
        CreateSideButton(navRow, "打开海图航行 (M)", ColBtnWood, OpenWorldMapFromHud, height: 30f);
        CreateSideButton(navRow, "发送海域信标", ColDriftwood, SendCameraCenterPing, height: 30f);
    }

    private static (ItemProperties? Selected, int MaxCount) BuildNativeCargoGrid(
        List<(ItemProperties Prop, int Count)> stock,
        string headerLabel,
        string emptyMessage)
    {
        // Header + optional pagination
        var headerRow = CreateSideRow(_operationsBody!, 4f);
        var hdrTmp = CreateSideTmp(headerRow, headerLabel, 11.5f, FontStyles.Bold, ColSubInk, TextAlignmentOptions.MidlineLeft);
        hdrTmp.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        if (stock.Count == 0)
        {
            CreateSideHint(emptyMessage);
            return (null, 0);
        }

        var selectedIdx = stock.FindIndex(x => x.Prop != null && x.Prop.name == _selectedItemKey);
        if (selectedIdx < 0)
        {
            selectedIdx = 0;
            _selectedItemKey = stock[0].Prop.name;
            _stockPage = 0;
        }

        var totalPages = Mathf.Max(1, Mathf.CeilToInt(stock.Count / (float)GridSlotsPerPage));
        _stockPage = Mathf.Clamp(_stockPage, 0, totalPages - 1);

        if (totalPages > 1)
        {
            CreateSideButton(headerRow, "◀", ColBtnWood, () =>
            {
                _stockPage = (_stockPage - 1 + totalPages) % totalPages;
                RefreshOperations();
            }, fixedWidth: 26f, height: 22f);
            var pageTmp = CreateSideTmp(headerRow, $"{_stockPage + 1}/{totalPages}", 11f, FontStyles.Bold, ColInk, TextAlignmentOptions.Center);
            pageTmp.gameObject.AddComponent<LayoutElement>().preferredWidth = 30f;
            CreateSideButton(headerRow, "▶", ColBtnWood, () =>
            {
                _stockPage = (_stockPage + 1) % totalPages;
                RefreshOperations();
            }, fixedWidth: 26f, height: 22f);
        }

        var trayGo = CreatePanel("NativeCargoGridTray", _operationsBody!, _fallbackPanelSprite!, ColParchmentTray);
        var grid = trayGo.AddComponent<GridLayoutGroup>();
        grid.padding = new RectOffset(7, 7, 7, 7);
        grid.cellSize = _nativeSlotCellSize;
        grid.spacing = _nativeSlotSpacing;
        grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        grid.startAxis = GridLayoutGroup.Axis.Horizontal;
        grid.childAlignment = TextAnchor.UpperLeft;
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = Mathf.Clamp(Mathf.FloorToInt(((_operationsCardRect?.rect.width ?? 640) - 56) / (_nativeSlotCellSize.x + _nativeSlotSpacing.x)), 1, 10);
        _operationsNodes.Add(trayGo);

        var pageItems = stock.Skip(_stockPage * GridSlotsPerPage).Take(GridSlotsPerPage).ToList();
        foreach (var entry in pageItems)
        {
            SpawnNativeCargoSlot(trayGo.transform, entry.Prop, entry.Count, entry.Prop.name == _selectedItemKey);
        }

        var chosen = stock[selectedIdx];
        return (chosen.Prop, chosen.Count);
    }

    private static void SpawnNativeCargoSlot(Transform gridParent, ItemProperties prop, int count, bool isSelected)
    {
        GameObject slotGo;
        if (_nativeSlotPrefab != null)
        {
            var slot = UnityEngine.Object.Instantiate(_nativeSlotPrefab, gridParent, false);
            slot.name = $"CargoSlot_{prop.name}";
            AccessTools.Field(typeof(InventoryPanelItemSlot), "_inventoryView")?.SetValue(slot, null);
            slot.Initialize(prop, count, showCounter: true);
            slotGo = slot.gameObject;
        }
        else
        {
            var bgCol = prop.ItemType != null ? prop.ItemType.Color : ColBtnWood;
            slotGo = CreatePanel($"CargoSlot_{prop.name}", gridParent, _fallbackPanelSprite!, bgCol);
            if (prop.InventorySprite != null)
            {
                var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconGo.transform.SetParent(slotGo.transform, false);
                var iconRt = iconGo.GetComponent<RectTransform>();
                iconRt.anchorMin = new Vector2(0.12f, 0.14f);
                iconRt.anchorMax = new Vector2(0.88f, 0.90f);
                iconRt.offsetMin = Vector2.zero;
                iconRt.offsetMax = Vector2.zero;
                var iconImg = iconGo.GetComponent<Image>();
                iconImg.sprite = prop.InventorySprite;
                iconImg.preserveAspect = true;
                iconImg.raycastTarget = false;
            }
            var cntTmp = CreateSideTmp(slotGo.transform, count.ToString(), 11.5f, FontStyles.Bold, ColCream, TextAlignmentOptions.BottomRight);
            var cntRt = cntTmp.GetComponent<RectTransform>();
            cntRt.anchorMin = Vector2.zero;
            cntRt.anchorMax = Vector2.one;
            cntRt.offsetMin = new Vector2(2f, 1f);
            cntRt.offsetMax = new Vector2(-3f, -2f);
        }

        if (isSelected && _slotSelectBorderSprite != null)
        {
            var ringGo = new GameObject("SelectedFrame", typeof(RectTransform), typeof(Image));
            ringGo.transform.SetParent(slotGo.transform, false);
            ringGo.transform.SetAsLastSibling();
            var ringRt = ringGo.GetComponent<RectTransform>();
            ringRt.anchorMin = Vector2.zero;
            ringRt.anchorMax = Vector2.one;
            ringRt.offsetMin = new Vector2(-2f, -2f);
            ringRt.offsetMax = new Vector2(2f, 2f);
            var ringImg = ringGo.GetComponent<Image>();
            ringImg.sprite = _slotSelectBorderSprite;
            ringImg.type = Image.Type.Sliced;
            ringImg.color = Color.white;
            ringImg.raycastTarget = false;
        }

        var bridge = slotGo.AddComponent<TradeSlotClickBridge>();
        bridge.ItemKey = prop.name;
        bridge.MaxCount = count;
    }

    private static void BuildSelectedItemAndStepper(ItemProperties selectedProp, int maxStock)
    {
        _selectedAmountMax = Mathf.Max(1, Mathf.Min(maxStock,FleetInteractions.ShipHasOperationalBoat(Community.PlayerCommunity)?25:4));
        _selectedAmount = Mathf.Clamp(_selectedAmount, 1, _selectedAmountMax);

        var box = CreatePanel("SelectedItemBar", _operationsBody!, _fallbackPanelSprite!, ColParchmentCard);
        var vl = box.AddComponent<VerticalLayoutGroup>();
        vl.padding = new RectOffset(9, 9, 7, 7);
        vl.spacing = 5f;
        vl.childControlWidth = true;
        vl.childControlHeight = true;
        vl.childForceExpandWidth = true;
        vl.childForceExpandHeight = false;
        box.AddComponent<QuantityWheelScrollBridge>();
        _operationsNodes.Add(box);

        // Top line: Icon + Localized Name + Stock count
        var infoRow = CreateRow(box.transform, 7f);
        if (selectedProp.InventorySprite != null)
        {
            var iconGo = new GameObject("SelectedIcon", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            iconGo.transform.SetParent(infoRow, false);
            var iconImg = iconGo.GetComponent<Image>();
            iconImg.sprite = selectedProp.InventorySprite;
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;
            var iconLe = iconGo.GetComponent<LayoutElement>();
            iconLe.preferredWidth = 26f;
            iconLe.preferredHeight = 26f;
            iconLe.minWidth = 26f;
            iconLe.minHeight = 26f;
        }

        var itemTitle = CreateSideTmp(infoRow, Replication.CleanItemName(selectedProp), 13.5f, FontStyles.Bold, ColInk, TextAlignmentOptions.MidlineLeft);
        itemTitle.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        var stockBadge = CreateSideTmp(infoRow, $"可用库存 {maxStock}", 11.5f, FontStyles.Bold, ColSubInk, TextAlignmentOptions.MidlineRight);
        stockBadge.gameObject.AddComponent<LayoutElement>().preferredWidth = 92f;

        // Bottom line: -5 | -1 | ×N / Max | +1 | +5 | 全部
        var stepRow = CreateRow(box.transform, 4f);
        CreateSideButton(stepRow, "-5", ColBtnWood, () => NotifyAmountChanged(_selectedAmount - 5), fixedWidth: 38f, height: 28f, trackRoot: false);
        CreateSideButton(stepRow, "-1", ColBtnWood, () => NotifyAmountChanged(_selectedAmount - 1), fixedWidth: 36f, height: 28f, trackRoot: false);

        var badgePill = CreatePanel("AmountBadge", stepRow, _fallbackPanelSprite!, ColDriftwood);
        var badgeLe = badgePill.AddComponent<LayoutElement>();
        badgeLe.flexibleWidth = 1f;
        badgeLe.minWidth = 74f;
        badgeLe.preferredHeight = 28f;
        var badgeLayout = badgePill.AddComponent<HorizontalLayoutGroup>();
        badgeLayout.childAlignment = TextAnchor.MiddleCenter;
        badgeLayout.childControlWidth = true;
        badgeLayout.childControlHeight = true;
        var valTmp = CreateSideTmp(badgePill.transform, $"× {_selectedAmount} / {maxStock}", 12.5f, FontStyles.Bold, ColGold, TextAlignmentOptions.Center);
        _amountListeners.Add(n =>
        {
            if (valTmp != null) valTmp.text = $"× {n} / {maxStock}";
        });

        CreateSideButton(stepRow, "+1", ColBtnWood, () => NotifyAmountChanged(_selectedAmount + 1), fixedWidth: 36f, height: 28f, trackRoot: false);
        CreateSideButton(stepRow, "+5", ColBtnWood, () => NotifyAmountChanged(_selectedAmount + 5), fixedWidth: 38f, height: 28f, trackRoot: false);
        CreateSideButton(stepRow, "全部", ColBtnAccent, () => NotifyAmountChanged(maxStock), fixedWidth: 46f, height: 28f, trackRoot: false);
    }

    private static RectTransform CreateSideRow(Transform parent, float spacing)
    {
        var row = CreateRow(parent, spacing);
        row.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;
        _operationsNodes.Add(row.gameObject);
        return row;
    }

    private static void CreateSideHint(string message)
    {
        var box = CreatePanel("SideHintBox", _operationsBody!, _fallbackPanelSprite!, ColParchmentTray);
        var hl = box.AddComponent<HorizontalLayoutGroup>();
        hl.padding = new RectOffset(10, 10, 8, 8);
        hl.childAlignment = TextAnchor.MiddleCenter;
        hl.childControlWidth = true;
        hl.childControlHeight = true;
        var tmp = CreateSideTmp(box.transform, message, 11.5f, FontStyles.Normal, ColSubInk, TextAlignmentOptions.Center);
        tmp.textWrappingMode = TextWrappingModes.Normal;
        _operationsNodes.Add(box);
    }

    private static void ClearOperationNodes()
    {
        foreach (var node in _operationsNodes)
            if (node != null) { node.SetActive(false); UnityEngine.Object.Destroy(node); }
        _operationsNodes.Clear();
        _amountListeners.Clear();
    }

    internal static void NotifyAmountChanged(int nextAmount)
    {
        _selectedAmount = Mathf.Clamp(nextAmount, 1, Mathf.Max(1, _selectedAmountMax));
        foreach (var listener in _amountListeners.ToArray())
            listener(_selectedAmount);
    }

    internal static void OnCargoSlotClicked( string itemKey, int maxCount, PointerEventData eventData)
    {
        var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        if (eventData.button == PointerEventData.InputButton.Right || ctrl)
        {
            var changedItem = _selectedItemKey != itemKey;
            _selectedItemKey = itemKey;
            _selectedAmountMax = Mathf.Max(1, maxCount);
            _selectedAmount = _selectedAmountMax;
            if (changedItem) RefreshOperations();
            else NotifyAmountChanged(_selectedAmount);
            return;
        }

        if (eventData.button != PointerEventData.InputButton.Left) return;

        if (_selectedItemKey != itemKey)
        {
            _selectedItemKey = itemKey;
            _selectedAmountMax = Mathf.Max(1, maxCount);
            _selectedAmount = shift ? Mathf.Min(5, _selectedAmountMax) : Mathf.Clamp(_selectedAmount, 1, _selectedAmountMax);
            RefreshOperations();
        }
        else
        {
            var delta = shift ? 5 : 1;
            var next = _selectedAmount >= maxCount ? 1 : Mathf.Min(maxCount, _selectedAmount + delta);
            NotifyAmountChanged(next);
        }
    }

    private static Button CreateDynamicSideButton(
        RectTransform parent,
        Func<int, string> labelFormatter,
        Color bgCol,
        Action onClick,
        Sprite? icon = null,
        float height = 32f)
    {
        var btn = CreateSideButton(parent, labelFormatter(_selectedAmount), bgCol, onClick, icon: icon, height: height, trackRoot: false);
        var tmp = btn.GetComponentInChildren<TextMeshProUGUI>(true);
        if (tmp != null)
            _amountListeners.Add(amt => { if (tmp != null) tmp.text = labelFormatter(amt); });
        return btn;
    }

    private static Button CreateSideButton(
        RectTransform parent,
        string text,
        Color bgCol,
        Action onClick,
        Sprite? icon = null,
        float fixedWidth = -1f,
        float height = 30f,
        bool trackRoot = false)
    {
        EnsureAssets();
        height = Mathf.Max(height, 36f);
        var btnSprite = _nativeBtnSprite ?? _fallbackPanelSprite!;
        var go = CreatePanel($"SideBtn_{text}", parent, btnSprite, bgCol);
        var img = go.GetComponent<Image>();
        if (_nativeBtnSprite != null) img.type = _nativeBtnSpriteType;
        img.raycastTarget = true;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.14f, 1.11f, 1.05f, 1f);
        colors.pressedColor = new Color(0.84f, 0.81f, 0.76f, 1f);
        btn.colors = colors;
        btn.onClick.AddListener(() => onClick());

        var le = go.AddComponent<LayoutElement>();
        if (fixedWidth > 0f)
        {
            le.preferredWidth = fixedWidth;
            le.minWidth = fixedWidth;
            le.flexibleWidth = 0f;
        }
        else
        {
            le.flexibleWidth = 1f;
        }
        le.preferredHeight = height;
        le.minHeight = height - 2f;

        var hl = go.AddComponent<HorizontalLayoutGroup>();
        hl.padding = new RectOffset(8, 8, 3, 3);
        hl.spacing = 5f;
        hl.childAlignment = TextAnchor.MiddleCenter;
        hl.childControlWidth = true;
        hl.childControlHeight = true;
        hl.childForceExpandWidth = false;

        if (icon != null)
        {
            var iconGo = new GameObject("BtnIcon", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            iconGo.transform.SetParent(go.transform, false);
            var iconImg = iconGo.GetComponent<Image>();
            iconImg.sprite = icon;
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;
            var iconLe = iconGo.GetComponent<LayoutElement>();
            iconLe.preferredWidth = 20f;
            iconLe.preferredHeight = 20f;
            iconLe.minWidth = 20f;
            iconLe.minHeight = 20f;
        }

        var label = CreateSideTmp(go.transform, text, 14f, FontStyles.Bold, ColCream, TextAlignmentOptions.Center);
        label.overflowMode = TextOverflowModes.Ellipsis;

        if (trackRoot) _operationsNodes.Add(go);
        return btn;
    }

    private static TextMeshProUGUI CreateSideTmp(
        Transform parent,
        string content,
        float size,
        FontStyles style,
        Color color,
        TextAlignmentOptions align)
    {
        var tmp = CreateTmp(parent, content, size, style, color, align);
        if (_nativeTmpMaterial != null && _nativeTmpFont != null && tmp.font == _nativeTmpFont)
            tmp.fontSharedMaterial = _nativeTmpMaterial;
        return tmp;
    }

    private static void OpenTradePanelForTargetShip(string targetShip)
    {
        _selectedTargetShip = targetShip;
        FocusCameraOnOwnShip();
        var myHeart = FleetEngineBundle.HeartBuildable(Community.PlayerCommunity) ?? Construction.Townheart?.Buildable;
        if (myHeart != null) OpenBuildableOperations(myHeart, OperationsTab.Trade);
    }

    private static void OpenForeignShipReconPanel(string targetShip, float sx, float sz)
    {
        _stockPage = 0;
        FocusCameraOn(sx, sz);
        var comm = Replication.ResolveCommunity(targetShip);
        var foreignBuilding = FleetEngineBundle.HeartBuildable(comm)
                              ?? comm?.Buildables.FirstOrDefault(b => b != null && !MoveConstructionCommands.IsStored(b));
        if (foreignBuilding != null) OpenBuildableOperations(foreignBuilding);
    }

    private static string ResolveOwnerLabel(string? communityName)
    {
        if (string.IsNullOrEmpty(communityName)) return "友方船镇";
        var ship = Plugin.Instance?.Network?.Ships.FirstOrDefault(s => s != null && s.name == communityName);
        if (ship != null && !string.IsNullOrWhiteSpace(ship.ownerName))
            return ship.ownerName;
        return FleetRuntime.ExtractShipOwnerName(communityName);
    }

    private static Vector3 ResolveShipScenePos(FleetShipState ship)
    {
        if (!string.IsNullOrEmpty(ship.name))
        {
            var comm = Replication.ResolveCommunity(ship.name);
            if (comm != null)
            {
                var heart = FleetEngineBundle.HeartBuildable(comm)
                            ?? comm.Buildables.FirstOrDefault(b => b != null && !MoveConstructionCommands.IsStored(b));
                if (heart != null)
                    return new Vector3(heart.transform.position.x, 0f, heart.transform.position.z);
            }
        }
        return ClientOceanAnchor.HostToClientPos(new Vector3(ship.x, 0f, ship.z));
    }

    // =========================================================================
    // 3. CAMERA FOCUS (Ground-Ray Offset Aware + Unlocks CameraController)
    // =========================================================================

    private static Vector3? GetCameraGroundPoint()
    {
        var cam = Camera.main;
        if (cam == null) return null;
        var ray = new Ray(cam.transform.position, cam.transform.forward);
        var plane = new Plane(Vector3.up, Vector3.zero);
        if (plane.Raycast(ray, out var dist) && dist > 0f && dist < 500f)
            return ray.GetPoint(dist);
        return null;
    }

    internal static void FocusCameraOnOwnShip()
    {
        var myHeart = FleetEngineBundle.HeartBuildable(Community.PlayerCommunity) ?? Construction.Townheart?.Buildable;
        var p = myHeart != null ? myHeart.transform.position : Construction.TownheartPosition;
        FocusCameraOn(p.x, p.z);
    }

    internal static void FocusCameraOn(float x, float z)
    {
        CloseManagementForNavigation();
        if (LiveNavigation.ViewOpen && GameManager.WorldMapManager?.WorldMap != null)
            GameManager.WorldMapManager.WorldMap.Close();

        var camCtrl = CameraController.Instance;
        if (camCtrl == null) return;

        AccessTools.Method(typeof(CameraController), "Unlock")?.Invoke(camCtrl, null);
        camCtrl.StopAllCoroutines();
        AccessTools.Field(typeof(CameraController), "_centerOnTownheartCoroutine")?.SetValue(camCtrl, null);

        var groundLook = GetCameraGroundPoint();
        if (groundLook.HasValue)
        {
            var offset = camCtrl.transform.position - new Vector3(groundLook.Value.x, 0f, groundLook.Value.z);
            camCtrl.transform.position = new Vector3(x, 0f, z) + offset;
        }
        else
        {
            var pos = camCtrl.transform.position;
            camCtrl.transform.position = new Vector3(x, pos.y, z - 18f);
        }
    }

    private static string Compass(float dx, float dz)
    {
        var angle = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
        if (angle < 0f) angle += 360f;
        var dirs = new[] { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };
        return dirs[Mathf.RoundToInt(angle / 45f) % 8];
    }

    // =========================================================================
    // 4. TOP-RIGHT F8 COLLAPSIBLE NATIVE FLEET & CO-OP OVERLAY (100% TMP)
    // =========================================================================

    private static void EnsureAssets()
    {
        _nativeTmpFont ??= FleetPhysicalActors.ResolveNativeTmpFont();
        _fallbackPanelSprite ??= MakeRoundedSprite(64, 10f, Color.white, new Color(0.10f, 0.07f, 0.05f, 0.88f), 2f);
        _parchmentCardSprite ??= MakeRoundedSprite(64, 12f, ColParchment, new Color(0.24f, 0.16f, 0.10f, 0.96f), 3.5f);
        _slotSelectBorderSprite ??= MakeRoundedSprite(64, 9f, new Color(1f, 0.86f, 0.30f, 0.14f), ColGold, 3f);
        _dotSprite ??= MakeRoundedSprite(24, 11f, Color.white, Color.clear, 0f);
    }

    private void TryHarvestNativeTheme()
    {
        if (_nativeTmpFont == null)
        {
            _nativeTmpFont = FleetPhysicalActors.ResolveNativeTmpFont();
            if (_nativeTmpFont != null && _canvas != null)
            {
                foreach (var tmp in _canvas.GetComponentsInChildren<TextMeshProUGUI>(true))
                    if (tmp != null) tmp.font = _nativeTmpFont;
            }
        }

        if (_nativeCardBgSprite == null || _nativeSlotPrefab == null)
        {
            var panel = Resources.FindObjectsOfTypeAll<BuildablePanel>().FirstOrDefault(p => p != null);
            if (panel != null) HarvestNativePanelAssets(panel);
        }
    }

    private void BuildOverlay()
    {
        var rootGo = new GameObject("FlotsamMultiplayerHudCanvas");
        DontDestroyOnLoad(rootGo);

        _canvas = rootGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 29990;

        var scaler = rootGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600f, 900f);
        scaler.matchWidthOrHeight = 0.5f;

        rootGo.AddComponent<GraphicRaycaster>();

        // 1. Guest Main-Menu Waiting Banner
        _waitBanner = CreatePanel("GuestWaitBanner", rootGo.transform, _fallbackPanelSprite!, ColDriftwood);
        var waitRt = _waitBanner.GetComponent<RectTransform>();
        waitRt.anchorMin = new Vector2(0.5f, 1f);
        waitRt.anchorMax = new Vector2(0.5f, 1f);
        waitRt.pivot = new Vector2(0.5f, 1f);
        waitRt.anchoredPosition = new Vector2(0f, -22f);
        waitRt.sizeDelta = new Vector2(650f, 46f);
        var waitLayout = _waitBanner.AddComponent<HorizontalLayoutGroup>();
        waitLayout.padding = new RectOffset(18, 18, 8, 8);
        waitLayout.childAlignment = TextAnchor.MiddleCenter;
        _waitText = CreateTmp(_waitBanner.transform, "正在连接房主世界...", 14.5f, FontStyles.Bold, ColGold, TextAlignmentOptions.Center);
        _waitBanner.SetActive(false);

        // 2. Top-Right Draggable Anchor
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

        // 2A. Clickable Compact Status Pill (Click or F8 toggles drawer; Drag moves HUD)
        var pillRoot = CreatePanel("CompactStatusPill", _anchor, _fallbackPanelSprite!, ColDriftwood);
        var pillLayout = pillRoot.AddComponent<HorizontalLayoutGroup>();
        pillLayout.padding = new RectOffset(13, 13, 7, 7);
        pillLayout.spacing = 8f;
        pillLayout.childAlignment = TextAnchor.MiddleCenter;
        pillLayout.childControlWidth = true;
        pillLayout.childControlHeight = true;
        pillLayout.childForceExpandWidth = false;

        var dotGo = CreatePanel("Dot", pillRoot.transform, _dotSprite!, ColSeafoam);
        _statusDot = dotGo.GetComponent<Image>();
        var dotLe = dotGo.AddComponent<LayoutElement>();
        dotLe.preferredWidth = 9f;
        dotLe.preferredHeight = 9f;

        _pillText = CreateTmp(pillRoot.transform, "联机就绪 (F8)", 13f, FontStyles.Bold, ColCream, TextAlignmentOptions.MidlineLeft);

        var dragAndClick = pillRoot.AddComponent<PillInteractHandle>();
        dragAndClick.Hud = this;

        // Fleet management owns its window, separate from the draggable status pill.
        _drawerRoot = CreateWindowBackdrop("FleetWindow", rootGo.transform, CloseFleetWindow);
        var fleetCard = CreateWindowCard(_drawerRoot.transform, "FleetCard", ColDriftwood);
        _fleetCardRect = fleetCard.GetComponent<RectTransform>();

        var drawerLayout = fleetCard.AddComponent<VerticalLayoutGroup>();
        drawerLayout.padding = new RectOffset(14, 14, 12, 12);
        drawerLayout.spacing = 7f;
        drawerLayout.childControlWidth = true;
        drawerLayout.childControlHeight = true;
        drawerLayout.childForceExpandWidth = true;
        drawerLayout.childForceExpandHeight = false;

        _drawerHeader = CreateTmp(fleetCard.transform, "舰队与船员管理", 14f, FontStyles.Bold, ColGold, TextAlignmentOptions.MidlineLeft);
        _drawerSubHeader = CreateTmp(fleetCard.transform, "", 13f, FontStyles.Normal, ColCream, TextAlignmentOptions.MidlineLeft);
        _drawerSubHeader.color = new Color(0.92f, 0.88f, 0.78f, 0.85f);
        _drawerSubHeader.textWrappingMode = TextWrappingModes.Normal;

        var fleetCloseRow = CreateRow(fleetCard.transform, 10);
        CreateHudButton(fleetCloseRow, "本船行动台 [F6]", ColBtnAccent, () => OpenBuildableOperations(DefaultOperationsBuilding()));
        CreateHudButton(fleetCloseRow, "关闭 [F8 / Esc]", ColBtnWood, CloseFleetWindow);
        _drawerRowsRoot = CreateWindowScroll(fleetCard.transform, "FleetRows");

        _drawerRoot.SetActive(_drawerExpanded);
        BuildOperationsEntry(rootGo.transform);

        // 2C. Crisp TMP Toast Banner
        _toastBox = CreatePanel("ToastNotice", rootGo.transform, _fallbackPanelSprite!, ColDriftwood);
        var toastRect = _toastBox.GetComponent<RectTransform>();
        toastRect.anchorMin = toastRect.anchorMax = toastRect.pivot = new Vector2(.5f, 1);
        toastRect.anchoredPosition = new Vector2(0, -24); toastRect.sizeDelta = new Vector2(680, 46);
        var toastLayout = _toastBox.AddComponent<HorizontalLayoutGroup>();
        toastLayout.padding = new RectOffset(13, 13, 7, 7);
        toastLayout.childControlWidth = true;
        toastLayout.childControlHeight = true;
        _toastText = CreateTmp(_toastBox.transform, "", 12.5f, FontStyles.Bold, ColGold, TextAlignmentOptions.MidlineLeft);
        _toastText.textWrappingMode = TextWrappingModes.Normal;
        _toastBox.SetActive(false);
    }

    private void RefreshStatusAndDrawer()
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
                _waitText.text = $"{status} · 房主进入世界后将自动同步载入，无需手动开始新游戏";
        }

        var synced = ready && net != null && net.Connected && (net.IsHost || net.ReplicaReady);
        var arrow = _drawerExpanded ? "▲" : "▼";

        if (_pillText != null)
        {
            if (synced)
            {
                if (isInd)
                {
                    var shipCount = Math.Max(1, net!.Ships.Length);
                    _pillText.text = $"独立船队 · {shipCount}艘在线  [F8 {arrow}]";
                }
                else
                {
                    var roleTag = net!.IsCaptain ? "掌舵船长" : "同船船员";
                    _pillText.text = $"同船共建 · {roleTag} · {Math.Max(1, net.Crew.Length)}人  [F8 {arrow}]";
                }
            }
            else
            {
                _pillText.text = $"{(isInd ? "独立船队" : "同船共建")} · {status}  [F8 {arrow}]";
            }
        }

        if (_statusDot != null)
            _statusDot.color = synced ? ColSeafoam : (ready && net != null && !net.Connected ? ColDanger : ColGold);

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

        if (_drawerRoot != null)
        {
            var shouldShowDrawer = _drawerExpanded && ready && net != null && !FleetCombat.Operating && !OperationsOpen;
            if (_drawerRoot.activeSelf != shouldShowDrawer)
                _drawerRoot.SetActive(shouldShowDrawer);
            if (shouldShowDrawer && net != null)
                RebuildDrawerContentsIfNeeded(net, isInd);
        }
    }

    private void RebuildDrawerContentsIfNeeded(GameSession net, bool isInd)
    {
        if (_drawerRowsRoot == null || _drawerHeader == null || _drawerSubHeader == null) return;

        var beacon = FleetPhysicalActors.LatestBeacon;
        var req = FleetPhysicalActors.PendingCaptainRequest;
        var myHeart = FleetEngineBundle.HeartBuildable(Community.PlayerCommunity) ?? Construction.Townheart?.Buildable;
        var myPos = myHeart != null ? myHeart.transform.position : Vector3.zero;

        var curRules = Plugin.Options.rules;
        var activeOffers = GetActiveOffers(net);
        var biomeCountdown = Mathf.CeilToInt(ProgressionReplication.BiomeCountdownClient);
        var readyCount = ProgressionReplication.ReadyPeersClient.Length;
        // Build lightweight signature + live telemetry update every refresh
        var sigParts = new List<string>
        {
            isInd ? "I" : "S",
            net.Captain,
            _rulesExpanded.ToString(),
            net.LocalPeer,
            beacon != null ? beacon.Label : "-",
            req.HasValue ? req.Value.PeerId : "-",
            ClientOceanAnchor.FollowOwnShip ? "F1" : "F0",
            ProgressionReplication.ActiveDialogue,
            $"{readyCount}:{biomeCountdown}",
            string.Join(",", activeOffers.Select(o => $"{o.id}:{o.toShip}")),
            $"{curRules.trading}:{curRules.stealing}:{curRules.sabotage}:{curRules.friendlyFire}"
        };
        if (isInd)
        {
            foreach (var s in net.Ships)
            {
                if (s == null) continue;
                var spotted = FleetVisibility.IsShipVisibleToLocal(s, net.Ships);
                var sp = ResolveShipScenePos(s);
                var distBucket = spotted ? Mathf.RoundToInt(Vector2.Distance(new Vector2(sp.x, sp.z), new Vector2(myPos.x, myPos.z)) / 4f) : -1;
                sigParts.Add($"{s.name}:{spotted}:{(s.hasWatchTower ? 1 : 0)}:{(s.crippledHeart ? 1 : 0)}:{(s.allies?.Length ?? 0)}:{(spotted ? s.agents : 0)}:{(spotted ? s.buildings : 0)}:{distBucket}");
            }
        }
        else
        {
            foreach (var c in net.Crew)
                if (c != null) sigParts.Add($"{c.id}:{c.name}");
        }

        var sig = string.Join("|", sigParts);
        if (sig == _lastRosterSignature) return;
        _lastRosterSignature = sig;

        foreach (var go in _drawerDynamicRows)
            if (go != null) { go.SetActive(false); UnityEngine.Object.Destroy(go); }
        _drawerDynamicRows.Clear();

        if (isInd)
        {
            var rules = Plugin.Options.rules;
            _drawerHeader.text = $"海上舰队雷达 ({Math.Max(1, net.Ships.Length)} 艘船)";
            _drawerSubHeader.text = net.IsHost
                ? "房间规则与船只列表 · 申请将在屏幕中央显示"
                : $"贸易:{(rules.trading ? "开" : "关")} · 劫掠:{(rules.stealing ? "开" : "关")} · 破坏:{(rules.sabotage ? "开" : "关")} · 火炮:{(rules.friendlyFire ? "开" : "关")}  |  Shift+右键直航";

            if(net.IsHost)
            {
                var settings=CreateRow(_drawerRowsRoot,5f);_drawerDynamicRows.Add(settings.gameObject);
                CreateHudButton(settings,_rulesExpanded?"收起房间规则":"房间规则",ColBtnWood,()=>{_rulesExpanded=!_rulesExpanded;_lastRosterSignature="";});
            }
            if (net.IsHost&&_rulesExpanded)
            {
                var ruleRow = CreateRow(_drawerRowsRoot, 5f);
                _drawerDynamicRows.Add(ruleRow.gameObject);
                CreateHudButton(ruleRow, $"贸易:{(rules.trading ? "开" : "关")}", rules.trading ? ColBtnAccent : ColBtnWood, () =>
                {
                    rules.trading = !rules.trading;
                    _lastRosterSignature = "";
                    net.BroadcastRulesChanged($"跨船贸易规则已{(rules.trading ? "开启" : "关闭")}");
                });
                CreateHudButton(ruleRow, $"劫掠:{(rules.stealing ? "开" : "关")}", rules.stealing ? ColBtnAccent : ColBtnWood, () =>
                {
                    rules.stealing = !rules.stealing;
                    _lastRosterSignature = "";
                    net.BroadcastRulesChanged($"登船劫掠规则已{(rules.stealing ? "开启" : "关闭")}");
                });
                CreateHudButton(ruleRow, $"破坏:{(rules.sabotage ? "开" : "关")}", rules.sabotage ? ColBtnAccent : ColBtnWood, () =>
                {
                    rules.sabotage = !rules.sabotage;
                    _lastRosterSignature = "";
                    net.BroadcastRulesChanged($"登船破坏规则已{(rules.sabotage ? "开启" : "关闭")}");
                });
                CreateHudButton(ruleRow, $"火炮:{(rules.friendlyFire ? "开" : "关")}", rules.friendlyFire ? ColDanger : ColBtnWood, () =>
                {
                    rules.friendlyFire = !rules.friendlyFire;
                    _lastRosterSignature = "";
                    net.BroadcastRulesChanged(rules.friendlyFire
                        ? "已开启炮战，请在本船炮位手动瞄准开火"
                        : "已关闭海战火炮规则");
                });
            }

            var localKey = Replication.ShipKey(Community.PlayerCommunity);
            var localShipState = net.Ships.FirstOrDefault(s => s != null && s.name == localKey);

            // Fleet Signal Flare & Cross-Biome Readiness Bar
            var fleetUtilRow = CreateRow(_drawerRowsRoot, 5f);
            _drawerDynamicRows.Add(fleetUtilRow.gameObject);
            CreateHudButton(fleetUtilRow, "🚨 信号弹(暴露35s)", ColDanger, () =>
                net.ExecuteOrSubmit(new GameCommand { kind = "shipFlare" }, "已发射海上信号弹，全图船只可见 35s！"));
            if (net.Ships.Length > 1)
            {
                var jumpText = ProgressionReplication.BiomeCountdownClient > 0f
                    ? $"⏳ 跃迁倒计时 {Mathf.CeilToInt(ProgressionReplication.BiomeCountdownClient)}s"
                    : $"🧭 跃迁就绪 ({ProgressionReplication.ReadyPeersClient.Length}/{net.Ships.Length})";
                CreateHudButton(fleetUtilRow, jumpText, ColBtnAccent, () =>
                {
                    _lastRosterSignature = "";
                    net.ExecuteOrSubmit(new GameCommand { kind = "nextBiomeReady" }, "已登记本船跨越海域就绪");
                });
            }

            var nowPlay = GameManager.TimeManager != null ? GameManager.TimeManager.CurrentPlayTime : 0f;
            foreach (var ship in net.Ships)
            {
                if (ship == null || string.IsNullOrEmpty(ship.name)) continue;
                var isMine = ship.name == localKey;
                var spotted = isMine || FleetVisibility.IsShipVisibleToLocal(ship, net.Ships);
                var allied = !isMine && FleetVisibility.AreAlliedState(localShipState, ship);
                var flared = ship.flareUntil > nowPlay;
                var shipPos = ResolveShipScenePos(ship);
                var dx = shipPos.x - myPos.x;
                var dz = shipPos.z - myPos.z;
                var dist = Mathf.RoundToInt(Mathf.Sqrt(dx * dx + dz * dz));
                var bearing = isMine
                    ? (ship.hasWatchTower ? "本船 · 瞭望750m" : "本船 · 视野400m")
                    : (spotted ? $"{Compass(dx, dz)} {dist}m" : "浓雾未知");
                var statusBadge = allied ? " <color=#57DBA8>[同盟]</color>" : (flared ? " <color=#FF6B4A>[信号弹]</color>" : "");

                var card = CreateDrawerCard();
                var topLine = CreateRow(card.transform, 6f);
                var titleTmp = CreateTmp(topLine, $"{(isMine ? "★ " : "⛵ ")}{ship.ownerName}{statusBadge}  <color=#8CE6C0>({bearing})</color>", 12.5f, FontStyles.Bold, ColCream, TextAlignmentOptions.MidlineLeft);
                var titleLe = titleTmp.gameObject.AddComponent<LayoutElement>();
                titleLe.flexibleWidth = 1f;

                var statsLine = spotted
                    ? $"居民 {ship.agents} · 建筑 {ship.buildings}"+(isMine?$" · 电力 {Mathf.RoundToInt(ship.energy)}":"")+(ship.crippledHeart?" · 核心受损":"")
                    : (FleetVisibility.TryGetGhost(ship.name, out _)
                        ? "迷雾遮蔽 · 海图留有最后目击残影"
                        : "迷雾遮蔽 · 驶入视野或结盟后显示实况");
                var statsTmp = CreateTmp(card.transform, statsLine, 11.5f, FontStyles.Normal, new Color(0.90f, 0.85f, 0.74f, 0.92f), TextAlignmentOptions.MidlineLeft);
                statsTmp.gameObject.AddComponent<LayoutElement>().preferredHeight = 18f;

                var actRow = CreateRow(card.transform, 6f);
                if (isMine)
                {
                    CreateHudButton(actRow, "回到我船 (Home)", ColBtnWood, FocusCameraOnOwnShip);
                    CreateHudButton(actRow, "打开海图 (M)", ColBtnAccent, OpenWorldMapFromHud);
                }
                else
                {
                    var sx = shipPos.x;
                    var sz = shipPos.z;
                    var mx = ship.mapX;
                    var mz = ship.mapZ;
                    var targetShip = ship.name;
                    var ownerLabel = ship.ownerName;

                    if(spotted) CreateHudButton(actRow,"查看船只与外交",ColBtnWood,()=>OpenForeignShipReconPanel(targetShip,sx,sz));
                    else if(FleetVisibility.TryGetGhost(targetShip,out var ghost))CreateHudButton(actRow,"海图最后目击位置",ColBtnWood,()=>FleetPhysicalActors.PanWorldMapTo(ghost.MapX,ghost.MapZ));
                }
            }
        }
        else
        {
            _drawerHeader.text = $"同船共建船员舱 ({Math.Max(1, net.Crew.Length)} 人在线)";
            _drawerSubHeader.text = !string.IsNullOrEmpty(ProgressionReplication.ActiveDialogue)
                ? $"剧情通讯进行中：{ProgressionReplication.ActiveDialogue}  |  船长 Shift+右键海面直航"
                : "全员共享库存、科研与搜刮  |  船长 Shift+右键海面直航  |  Alt+左键发信标";

            foreach (var member in net.Crew)
            {
                if (member == null) continue;
                var isCap = member.id == net.Captain;
                var isMe = member.id == net.LocalPeer;
                var card = CreateDrawerCard();
                var row = CreateRow(card.transform, 6f);

                var tags = new List<string>();
                if (member.host) tags.Add("房主");
                if (isCap) tags.Add("<color=#F8C344>掌舵船长</color>");
                if (isMe) tags.Add("我");
                var badgeStr = tags.Count > 0 ? $" ({string.Join("·", tags)})" : "";

                var nameTmp = CreateTmp(row, $"{member.name}{badgeStr}", 12.5f, FontStyles.Bold, ColCream, TextAlignmentOptions.MidlineLeft);
                nameTmp.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

                if (!isCap && (net.IsCaptain || net.IsHost))
                {
                    var targetId = member.id;
                    var targetName = member.name;
                    CreateHudButton(row, "移交船长", ColBtnAccent, () =>
                        net.TransferCaptain(targetId, $"已将掌舵船长移交给 {targetName}"), 82f);
                }
                else if (isMe && !net.IsCaptain)
                {
                    CreateHudButton(row, "申请掌舵", ColBtnWood, () =>
                        net.ExecuteOrSubmit(new GameCommand { kind = "captainRequest" }, "已向船长发送掌舵申请"), 82f);
                }
            }
        }

        // Shared Bottom Co-op Action Bar
        var bottomRow = CreateRow(_drawerRowsRoot, 6f);
        _drawerDynamicRows.Add(bottomRow.gameObject);
        CreateHudButton(bottomRow, "回到船镇", ColBtnWood, FocusCameraOnOwnShip);
        CreateHudButton(
            bottomRow,
            ClientOceanAnchor.FollowOwnShip ? "视角:随船 (V)" : "视角:观海 (V)",
            ClientOceanAnchor.FollowOwnShip ? ColBtnAccent : ColBtnWood,
            ToggleFollowOwnShip);
        CreateHudButton(bottomRow, "发信标", ColBtnAccent, SendCameraCenterPing);
        if (beacon != null)
            CreateHudButton(bottomRow, "跳信标(F9)", ColBtnAccent, JumpToLatestBeacon);
    }

    private GameObject CreateDrawerCard(Color? overrideBg = null)
    {
        var card = CreatePanel("RosterCard", _drawerRowsRoot!, _fallbackPanelSprite!, overrideBg ?? ColDriftwoodCard);
        var vl = card.AddComponent<VerticalLayoutGroup>();
        vl.padding = new RectOffset(10, 10, 8, 8);
        vl.spacing = 5f;
        vl.childControlWidth = true;
        vl.childControlHeight = true;
        vl.childForceExpandWidth = true;
        vl.childForceExpandHeight = false;
        _drawerDynamicRows.Add(card);
        return card;
    }

    private static RectTransform CreateRow(Transform parent, float spacing)
    {
        var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        go.transform.SetParent(parent, false);
        var hl = go.GetComponent<HorizontalLayoutGroup>();
        hl.spacing = spacing;
        hl.childAlignment = TextAnchor.MiddleCenter;
        hl.childControlWidth = true;
        hl.childControlHeight = true;
        hl.childForceExpandWidth = false;
        hl.childForceExpandHeight = false;
        return go.GetComponent<RectTransform>();
    }

    private static Button CreateHudButton(RectTransform parent, string text, Color bgCol, Action onClick, float fixedWidth = -1f)
    {
        var go = CreatePanel($"HudBtn_{text}", parent, _fallbackPanelSprite!, bgCol);
        var img = go.GetComponent<Image>();
        img.raycastTarget = true;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.15f, 1.12f, 1.05f, 1f);
        colors.pressedColor = new Color(0.85f, 0.82f, 0.78f, 1f);
        btn.colors = colors;
        btn.onClick.AddListener(() => onClick());

        var le = go.AddComponent<LayoutElement>();
        if (fixedWidth > 0f)
        {
            le.preferredWidth = fixedWidth;
            le.minWidth = fixedWidth;
            le.flexibleWidth = 0f;
        }
        else
        {
            le.flexibleWidth = 1f;
        }
        le.preferredHeight = 36f;
        le.minHeight = 34f;

        var hl = go.AddComponent<HorizontalLayoutGroup>();
        hl.padding = new RectOffset(8, 8, 3, 3);
        hl.childAlignment = TextAnchor.MiddleCenter;
        hl.childControlWidth = true;
        hl.childControlHeight = true;

        var label = CreateTmp(go.transform, text, 14f, FontStyles.Bold, ColCream, TextAlignmentOptions.Center);
        label.overflowMode = TextOverflowModes.Ellipsis;
        return btn;
    }

    internal void OnDragDelta(Vector2 delta)
    {
        if (_canvas == null || _anchor == null) return;
        var scale = _canvas.scaleFactor > 0.01f ? _canvas.scaleFactor : 1f;
        _panelPos += delta / scale;
        _panelPos.x = Mathf.Clamp(_panelPos.x, -1750f, -8f);
        _panelPos.y = Mathf.Clamp(_panelPos.y, -980f, -8f);
        _anchor.anchoredPosition = _panelPos;
    }

    internal void OnDragEnd()
    {
        PlayerPrefs.SetFloat(PrefKeyX, _panelPos.x);
        PlayerPrefs.SetFloat(PrefKeyY, _panelPos.y);
        PlayerPrefs.Save();
    }

    private static GameObject CreatePanel(string name, Transform parent, Sprite sprite, Color tint)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.type = sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        img.color = tint;
        return go;
    }

    private static TextMeshProUGUI CreateTmp(
        Transform parent,
        string content,
        float size,
        FontStyles style,
        Color color,
        TextAlignmentOptions align)
    {
        var go = new GameObject("TmpLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var tmp = go.GetComponent<TextMeshProUGUI>();
        if (_nativeTmpFont != null) tmp.font = _nativeTmpFont;
        tmp.text = content;
        tmp.fontSize = size;
        tmp.fontStyle = style;
        tmp.color = color;
        tmp.alignment = align;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.raycastTarget = false;
        return tmp;
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

    private sealed class PillInteractHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        public FleetHud? Hud;
        private bool _dragging;

        public void OnBeginDrag(PointerEventData eventData) => _dragging = true;

        public void OnDrag(PointerEventData eventData) => Hud?.OnDragDelta(eventData.delta);

        public void OnEndDrag(PointerEventData eventData)
        {
            Hud?.OnDragEnd();
            _dragging = false;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!_dragging && eventData.button == PointerEventData.InputButton.Left)
                Hud?.ToggleFleetDrawer();
        }
    }

    private sealed class BuildablePanelCloseTracker : MonoBehaviour
    {
        private void OnDisable() => OnHostBuildablePanelClosed();
    }

    private sealed class TradeSlotClickBridge : MonoBehaviour, IPointerClickHandler, IScrollHandler
    {
        public string ItemKey = "";
        public int MaxCount = 1;

        public void OnPointerClick(PointerEventData eventData)
        {
            OnCargoSlotClicked( ItemKey, MaxCount, eventData);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (Mathf.Abs(eventData.scrollDelta.y) < 0.01f) return;
            var step = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 5 : 1;
            NotifyAmountChanged(_selectedAmount + (eventData.scrollDelta.y > 0f ? step : -step));
        }
    }

    private sealed class QuantityWheelScrollBridge : MonoBehaviour, IScrollHandler
    {
        public void OnScroll(PointerEventData eventData)
        {
            if (Mathf.Abs(eventData.scrollDelta.y) < 0.01f) return;
            var step = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 5 : 1;
            NotifyAmountChanged(_selectedAmount + (eventData.scrollDelta.y > 0f ? step : -step));
        }
    }
}
