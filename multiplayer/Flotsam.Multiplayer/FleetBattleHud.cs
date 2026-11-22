using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Flotsam.Multiplayer;

internal sealed partial class FleetHud
{
    private GameObject? _gunHud;
    private TextMeshProUGUI? _gunText;
    private TextMeshProUGUI? _gunCrosshair;
    private readonly System.Collections.Generic.Dictionary<int,(GameObject root,Image fill,TextMeshProUGUI text,float hp,float until)> _healthBars = new System.Collections.Generic.Dictionary<int,(GameObject,Image,TextMeshProUGUI,float,float)>();
    private static void BuildDepartureControls(GameSession net)
    {
        var key=Replication.ShipKey(Community.PlayerCommunity);
        var targets=net.Ships.Where(s=>s.name!=key&&FleetVisibility.IsShipVisibleToLocal(s,net.Ships)).ToArray();
        if(targets.Length>0)
        {
        if(!targets.Any(s=>s.name==_selectedTargetShip))_selectedTargetShip=targets[0].name;
        var targetRow=CreateSideRow(_operationsBody!,10);
        CreateSideButton(targetRow,"目标: "+ResolveOwnerLabel(_selectedTargetShip),ColBtnWood,()=>
        {
            var index=System.Array.FindIndex(targets,s=>s.name==_selectedTargetShip);
            _selectedTargetShip=targets[(index+1)%targets.Length].name;
            RefreshOperations();
        });
        if(Plugin.Options.rules.stealing)CreateSideButton(targetRow,"派员掠夺",ColDanger,()=>net.ExecuteOrSubmit(new GameCommand{kind="shipSteal",text=_selectedTargetShip,amount=4},"掠夺小队已出发"));
        }
        var active=FleetVoyages.Visible.Where(v=>v.fromShip==key).ToArray();
        foreach(var v in active)
        {
            CreateSideHint($"{v.workerName} · {v.status}");
            CreateSideButton(CreateSideRow(_operationsBody!,10),$"召回 {v.workerName}",ColBtnWood,()=>net.ExecuteOrSubmit(new GameCommand{kind="voyageRecall",reference=v.id},"已下令撤退返航"));
        }
    }
    private readonly System.Collections.Generic.Dictionary<int,float> _previousHp=new System.Collections.Generic.Dictionary<int,float>();
    private float _nextBars;
    private void RefreshBattleHud()
    {
        if(_canvas==null)return;
        if(_gunHud==null)
        {
            _gunHud=new GameObject("GunneryHud",typeof(RectTransform));_gunHud.transform.SetParent(_canvas.transform,false);
            var r=_gunHud.GetComponent<RectTransform>();r.anchorMin=Vector2.zero;r.anchorMax=Vector2.one;r.sizeDelta=Vector2.zero;
            var cross=_gunCrosshair=CreateTmp(r,"＋",32,FontStyles.Normal,ColCream,TextAlignmentOptions.Center);cross.raycastTarget=false;
            var cr=cross.rectTransform;cr.anchorMin=cr.anchorMax=new Vector2(.5f,.5f);cr.sizeDelta=new Vector2(40,40);
            _gunText=CreateTmp(r,"",17,FontStyles.Bold,ColCream,TextAlignmentOptions.Center);
            _gunText.raycastTarget=false;
            var tr=_gunText.rectTransform;tr.anchorMin=tr.anchorMax=new Vector2(.5f,0);tr.anchoredPosition=new Vector2(0,65);tr.sizeDelta=new Vector2(1000,90);
        }
        _gunHud.SetActive(FleetCombat.Operating&&!RequestOpen);
        if(_gunCrosshair!=null)_gunCrosshair.gameObject.SetActive(!FleetCombat.Tactical);
        if(_gunText!=null)_gunText.text=FleetCombat.GunStatus;
        if(_requestModal!=null)_requestModal.transform.SetAsLastSibling();
        if(!GameSession.Ready()||ManagementOpen||RequestOpen){foreach(var bar in _healthBars.Values)bar.root.SetActive(false);foreach(var bar in _voyageBars.Values)bar.root.SetActive(false);return;}
        if(Time.unscaledTime<_nextBars)return;
        _nextBars=Time.unscaledTime+.1f;
        var cam=Camera.main;if(cam==null)return;
        var live=new System.Collections.Generic.HashSet<int>();
        foreach(var owner in Community.Communities)
        foreach(var b in owner.Buildables)
        {
            if(b==null||b.BuildPhase!=BuildPhase.Finished||MoveConstructionCommands.IsStored(b))continue;
            var state=Plugin.Instance?.Network?.Ships.FirstOrDefault(s=>s.name==Replication.ShipKey(owner));
            var visible=owner==Community.PlayerCommunity||(state!=null&&FleetVisibility.IsShipVisibleToLocal(state));
            if(!BuildingDurability.TryGetDurability(b,out var hp,out var max,out _,out _)||max<=0)continue;
            var id=b.GetInstanceID();
            live.Add(id);
            var exists=_healthBars.TryGetValue(id,out var entry);
            var recent=_previousHp.TryGetValue(id,out var previous)&&Mathf.Abs(previous-hp)>.1f;
            _previousHp[id]=hp;
            var selected=_selectedContext==b;
            var screen=cam.WorldToScreenPoint(b.transform.position+Vector3.up*4);
            var inView=screen.z>0&&screen.x>=0&&screen.x<=Screen.width&&screen.y>=0&&screen.y<=Screen.height;
            var nearAim=FleetCombat.Tactical?Vector2.Distance(new Vector2(screen.x,screen.y),Input.mousePosition)<80:
                Vector2.Distance(new Vector2(screen.x,screen.y),new Vector2(Screen.width*.5f,Screen.height*.5f))<80;
            var combatFocus=FleetCombat.Operating&&inView&&(nearAim||BuildingDurability.IsTownheart(b)||FleetCombat.HasMount(b)||hp<max);
            var show=visible&&(selected||recent||(BuildingDurability.IsTownheart(b)&&hp<max)||(exists&&entry.until>Time.unscaledTime)||combatFocus);
            if(!show){if(exists)entry.root.SetActive(false);continue;}
            if(screen.z<0){if(exists)entry.root.SetActive(false);continue;}
            if(!exists)
            {
                var root=CreatePanel("StructuralHealth",_canvas.transform,_fallbackPanelSprite!,new Color(.08f,.1f,.09f,.9f));
                root.GetComponent<Image>().raycastTarget=false;root.GetComponent<RectTransform>().sizeDelta=new Vector2(110,8);
                var fill=CreatePanel("Health",root.transform,_fallbackPanelSprite!,ColSeafoam).GetComponent<Image>();fill.raycastTarget=false;
                var fr=fill.rectTransform;fr.anchorMin=Vector2.zero;fr.anchorMax=Vector2.one;fr.sizeDelta=Vector2.zero;
                var text=CreateTmp(root.transform,"",12,FontStyles.Bold,ColCream,TextAlignmentOptions.Center);
                text.rectTransform.anchoredPosition=new Vector2(0,17);text.rectTransform.sizeDelta=new Vector2(140,22);
                entry=(root,fill,text,hp,0);
            }
            entry.root.SetActive(true);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas.GetComponent<RectTransform>(),screen,null,out var point);
            entry.root.GetComponent<RectTransform>().anchoredPosition=point;
            entry.fill.rectTransform.anchorMax=new Vector2(Mathf.Clamp01(hp/max),1);
            entry.fill.color=hp/max<.35f?ColDanger:ColSeafoam;
            entry.text.text=$"{Mathf.CeilToInt(hp)}/{Mathf.CeilToInt(max)}";
            if(recent)entry.until=Time.unscaledTime+5;
            entry.hp=hp;_healthBars[id]=entry;
        }
        foreach(var pair in _healthBars.ToArray())if(!live.Contains(pair.Key)){Destroy(pair.Value.root);_healthBars.Remove(pair.Key);_previousHp.Remove(pair.Key);}
        RefreshVoyageBars(cam);
    }
    private readonly System.Collections.Generic.Dictionary<long,(GameObject root,Image fill,TextMeshProUGUI label)> _voyageBars=new System.Collections.Generic.Dictionary<long,(GameObject,Image,TextMeshProUGUI)>();
    private void RefreshVoyageBars(Camera cam)
    {
        var net=Plugin.Instance?.Network;if(net==null)return;
        var local=Replication.ShipKey(Community.PlayerCommunity);var ids=new System.Collections.Generic.HashSet<long>();
        foreach(var v in FleetVoyages.Visible)
        {
            ids.Add(v.id);
            var pos=new Vector3(v.x,v.y,v.z);if(net.IsClient)pos=ClientOceanAnchor.HostToClientPos(pos);
            var localHeart=FleetEngineBundle.HeartBuildable(Community.PlayerCommunity);
            var visible=v.fromShip==local||(localHeart!=null&&Vector3.Distance(localHeart.transform.position,pos)<85);
            var screen=cam.WorldToScreenPoint(pos+Vector3.up*4);
            var exists=_voyageBars.TryGetValue(v.id,out var bar);
            if(!visible||screen.z<=0){if(exists)bar.root.SetActive(false);continue;}
            if(!exists)
            {
                var root=CreatePanel("ExpeditionHealth",_canvas!.transform,_fallbackPanelSprite!,new Color(.08f,.1f,.09f,.9f));root.GetComponent<RectTransform>().sizeDelta=new Vector2(110,8);root.GetComponent<Image>().raycastTarget=false;
                var fill=CreatePanel("Health",root.transform,_fallbackPanelSprite!,ColSeafoam).GetComponent<Image>();fill.raycastTarget=false;fill.rectTransform.anchorMin=Vector2.zero;fill.rectTransform.anchorMax=Vector2.one;fill.rectTransform.sizeDelta=Vector2.zero;
                var label=CreateTmp(root.transform,"",12,FontStyles.Bold,ColCream,TextAlignmentOptions.Center);label.rectTransform.anchoredPosition=new Vector2(0,24);label.rectTransform.sizeDelta=new Vector2(200,40);bar=(root,fill,label);_voyageBars[v.id]=bar;
            }
            bar.root.SetActive(true);RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas!.GetComponent<RectTransform>(),screen,null,out var point);bar.root.GetComponent<RectTransform>().anchoredPosition=point;
            bar.fill.rectTransform.anchorMax=new Vector2(Mathf.Clamp01(v.hp/v.maxHp),1);bar.fill.color=v.fromShip==local?ColSeafoam:ColDanger;
            bar.label.text=v.workerName+" "+Mathf.CeilToInt(v.hp)+"/"+Mathf.CeilToInt(v.maxHp)+(v.fromShip==local?"\n"+v.status+" · 货物"+v.cargo.Sum(c=>c.count):"");
        }
        foreach(var pair in _voyageBars.ToArray())if(!ids.Contains(pair.Key)){Destroy(pair.Value.root);_voyageBars.Remove(pair.Key);}
    }
}
