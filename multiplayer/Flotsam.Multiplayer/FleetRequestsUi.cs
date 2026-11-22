using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Flotsam.Multiplayer;

internal sealed partial class FleetHud
{
    private GameObject? _requestModal;
    private string _shownRequest = "";
    internal static bool RequestOpen;
    private float _requestPoll;
    private void RefreshRequests()
    {
        var net = Plugin.Instance?.Network;
        if (net == null || !GameSession.Ready() || !net.Connected || (net.IsClient && !net.ReplicaReady))
        {
            CloseRequest();
            return;
        }
        if (Time.unscaledTime < _requestPoll) return;
        _requestPoll = Time.unscaledTime + .15f;
        var local = Replication.ShipKey(Community.PlayerCommunity);
        var requests = net.IsHost ? FleetVisibility.CaptureRequests() : net.AppliedFrame?.allianceRequests ?? Array.Empty<AllianceRequestState>();
        var offers = net.IsHost ? FleetInteractions.CaptureOffers() : net.AppliedFrame?.tradeOffers ?? Array.Empty<TradeOfferState>();
        var request = requests.FirstOrDefault(r => r.toShip == local);
        var offer = offers.FirstOrDefault(r => r.toShip == local);
        var ownership=net.IsHost?net.OwnershipRequests.FirstOrDefault():null;
        var captain=Plugin.Options.mode=="shared"&&(net.IsCaptain||net.IsHost)?FleetPhysicalActors.PendingCaptainRequest:null;
        if(captain.HasValue&&(!net.Crew.Any(c=>c.id==captain.Value.PeerId)||captain.Value.PeerId==net.Captain))captain=null;
        var key = ownership!=null?"owner:"+ownership.Id:request != null ? "alliance:" + request.id : offer != null ? "trade:" + offer.id : captain.HasValue?"captain:"+captain.Value.PeerId:"";
        if (key == "") { CloseRequest(); return; }
        if (_shownRequest == key) return;
        CloseRequest();
        _shownRequest = key;
        FleetCombat.SuspendInput();
        _requestModal = CreatePanel("IncomingRequest", _canvas!.transform, _fallbackPanelSprite!, new Color(0,0,0,.55f));
        var screen = _requestModal.GetComponent<RectTransform>();
        screen.anchorMin = Vector2.zero; screen.anchorMax = Vector2.one; screen.sizeDelta = Vector2.zero;
        _requestModal.GetComponent<Image>().raycastTarget = true;
        var card = CreatePanel("RequestCard", screen, _nativeCardBgSprite ?? _fallbackPanelSprite!, ColParchment);
        var rect = card.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f,.5f);
        rect.sizeDelta = new Vector2(500,260);
        var layout = card.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(26,26,24,24); layout.spacing = 16;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandHeight = false;
        var title = CreateTmp(rect, ownership!=null?"确认旧版船只归属":request != null ? "收到结盟申请" : offer!=null?"收到交易报价":"收到掌舵申请",22,FontStyles.Bold,ColInk,TextAlignmentOptions.Center);
        title.gameObject.AddComponent<LayoutElement>().preferredHeight = 32;
        var copy = ownership!=null?$"玩家 {ownership.Player} 希望领回旧版船只\n身份 {ownership.Owner.Substring(0,8)} · 居民 {ownership.Ship.Agents.Count}\n接受后绑定新身份；拒绝后为该玩家建立新船。":request != null
            ? request.fromOwner + " 希望与你结盟\n双方共享海图视野，各自保留船只经营权。"
            : offer!=null?offer.fromOwner + " 向你提出交易\n你付出 " + Replication.FormatCategoryLabel(offer.wantItem) + " ×" + offer.wantAmount +
              "，获得 " + Replication.FormatCategoryLabel(offer.giveItem) + " ×" + offer.giveAmount + "\n接受后双方备货并安排运输。":captain!.Value.PlayerName+" 希望接管掌舵\n接受后将向对方移交航行和游戏速度控制权。";
        var body = CreateTmp(rect,copy,17,FontStyles.Normal,ColInk,TextAlignmentOptions.Center);
        body.textWrappingMode = TextWrappingModes.Normal;
        body.gameObject.AddComponent<LayoutElement>().preferredHeight = 104;
        var buttons = CreateRow(rect,16);
        Button yes=null!, no=null!;
        yes = CreateHudButton(buttons,"接受",ColBtnAccent,()=>Respond(true));
        no = CreateHudButton(buttons,"拒绝",ColBtnWood,()=>Respond(false));
        yes.GetComponent<LayoutElement>().preferredHeight = no.GetComponent<LayoutElement>().preferredHeight = 38;
        void Respond(bool accept)
        {
            if(ownership==null&&request==null&&offer==null&&!accept){FleetPhysicalActors.ClearCaptainRequest();CloseRequest();return;}
            var command = ownership!=null?new GameCommand{kind=accept?"shipOwnerAccept":"shipOwnerReject",reference=ownership.Id}:request != null
                ? new GameCommand { kind = accept ? "shipAllyAccept" : "shipAllyReject", reference = request.id, text = request.fromShip }
                : offer!=null?new GameCommand { kind = accept ? "shipBarterAccept" : "shipBarterCancel", target = offer.id }:new GameCommand{kind="captainTransfer",text=captain!.Value.PeerId};
            yes.interactable = no.interactable = false;
            title.text = "等待房主确认…";
            net.ExecuteOrSubmit(command, accept ? "申请已接受" : "申请已拒绝",accepted=>
            {
                if(_shownRequest!=key||title==null)return;
                if(accepted){if(captain.HasValue)FleetPhysicalActors.ClearCaptainRequest();CloseRequest();}
                else{yes.interactable=no.interactable=true;title.text="处理未完成，请重试或拒绝";}
            });
        }
        RequestOpen = true;
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
    }
    private void CloseRequest()
    {
        if (_requestModal != null) Destroy(_requestModal);
        _requestModal = null; _shownRequest = ""; RequestOpen = false;
    }
}
