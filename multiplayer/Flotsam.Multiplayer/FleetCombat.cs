using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Flotsam.Multiplayer;

// Host simulation owns projectiles and gun leases. Rendering never applies damage.
internal static partial class FleetCombat
{
    private sealed class Mount
    {
        internal Buildable Building=null!;
        internal Transform Root=null!,Turret=null!,Barrel=null!;
        internal float Yaw,Pitch=15,Reload,Lease;
        internal string Operator="";
        internal Agent? Gunner;
        internal float Recoil,BuildRemaining;
    }
    private sealed class Shell
    {
        internal CannonShellState State=new CannonShellState();
        internal GameObject Visual=null!;
    }
    private static readonly Dictionary<int,Mount> Mounts=new Dictionary<int,Mount>();
    private static readonly Dictionary<long,Shell> Shells=new Dictionary<long,Shell>();
    private static RaycastHit[] _shellHits=new RaycastHit[32];
    private static long _nextShell;
    private static Buildable? _operating;
    private static Camera? _camera;
    private static bool _cameraEnabled;
    private static Vector3 _cameraPos;
    private static Quaternion _cameraRot;
    private static float _cameraFov;
    private static CursorLockMode _previousLock;
    private static bool _previousCursorVisible;
    private static float _yaw,_pitch=15,_nextAim,_nextFire;
    private static int _ammo;
    private static float _statusAt;
    private static int _metal;
    private static bool _suspended;
    private static readonly Dictionary<Color,Material> Materials=new Dictionary<Color,Material>();
    private static Mesh? _barrelMesh;
    internal static bool Operating=>_operating!=null;
    internal static bool BlocksInput=>Operating||FleetHud.RequestOpen||FleetHud.ManagementInputBlocked;
    internal static bool HasMount(Buildable b)=>b!=null&&Mounts.ContainsKey(b.GetInstanceID());
    internal static bool CanBuildMount(Buildable b)=>b!=null&&b.GetComponent<WalkwayPonton>()!=null;
    internal static bool IsOccupied(Buildable b)=>Mounts.TryGetValue(b.GetInstanceID(),out var m)&&(m.Gunner!=null||m.Reload>0);
    internal static bool IsBusy(Agent a)=>Mounts.Values.Any(m=>m.Gunner==a&&(m.BuildRemaining>0||m.Reload>0||m.Lease>Time.realtimeSinceStartup));
    internal static bool Handles(string kind)=>kind=="cannonBuild"||kind=="cannonRemove"||kind=="cannonEnter"||kind=="cannonLeave"||kind=="cannonAim"||kind=="cannonFire";
    internal static void Install(Harmony h)
    {
        h.Patch(AccessTools.DeclaredMethod(typeof(CursorManager),"Update"),prefix:new HarmonyMethod(typeof(FleetCombat),nameof(CameraInput)));
        h.Patch(AccessTools.DeclaredMethod(typeof(CameraController),"Update"),prefix:new HarmonyMethod(typeof(FleetCombat),nameof(CameraInput)));
        h.Patch(AccessTools.DeclaredMethod(typeof(CameraZoomController),"Update"),prefix:new HarmonyMethod(typeof(FleetCombat),nameof(CameraInput)));
        h.Patch(AccessTools.DeclaredMethod(typeof(FlotsamInputManager),"Update"),prefix:new HarmonyMethod(typeof(FleetCombat),nameof(GameInput)));
        h.Patch(AccessTools.Method(typeof(CameraController),"ReturnHandleInput"),prefix:new HarmonyMethod(typeof(FleetCombat),nameof(HandleInput)));
    }
    private static bool CameraInput()=>!BlocksInput;
    // Gunnery owns Escape and the mouse buttons while active; the same key
    // must not also open a native menu or change the selected resident.
    private static bool GameInput()=>!BlocksInput;
    private static bool HandleInput(ref bool __result){if(!BlocksInput)return true;__result=false;return false;}
    internal static void Enter(Buildable b,bool tactical=false)
    {
        if(b.Community!=Community.PlayerCommunity||!HasMount(b))return;
        FleetHud.CloseOperations();
        Exit();
        _operating=b;_yaw=Mounts[b.GetInstanceID()].Yaw;_pitch=Mounts[b.GetInstanceID()].Pitch;
        _camera=CameraController.Instance?.Camera??Camera.main;
        if(_camera!=null){_cameraPos=_camera.transform.position;_cameraRot=_camera.transform.rotation;_cameraFov=_camera.fieldOfView;_cameraEnabled=true;}
        _previousLock=Cursor.lockState;_previousCursorVisible=Cursor.visible;
        GameManager.CursorManager?.Deactivate();
        Plugin.Instance?.Network?.ExecuteOrSubmit(new GameCommand{kind="cannonEnter",target=Plugin.Instance.Network.Id(b)},completed:ok=>{if(!ok&&_operating==b)Exit();});
        _suspended=false;if(!Operating)return;
        _nextFire=Time.unscaledTime+.25f;_nextAim=0;
        UnityEngine.Object.FindAnyObjectByType<BuildablePanel>()?.Close();
        SetTactical(tactical);
    }
    internal static void SuspendInput(){_suspended=true;Cursor.lockState=CursorLockMode.None;Cursor.visible=true;if(_tacticalVisuals!=null)_tacticalVisuals.SetActive(false);}
    internal static void Exit()
    {
        if(_operating!=null)Plugin.Instance?.Network?.ExecuteOrSubmit(new GameCommand{kind="cannonLeave",target=Plugin.Instance.Network.Id(_operating)});
        var wasOperating=Operating;
        _operating=null;_suspended=false;
        ClearTactical();
        if(wasOperating){Cursor.lockState=_previousLock;Cursor.visible=_previousCursorVisible;}
        if(_camera!=null&&_cameraEnabled){_camera.transform.SetPositionAndRotation(_cameraPos,_cameraRot);_camera.fieldOfView=_cameraFov;_cameraEnabled=false;}
    }
    internal static string? Execute(GameCommand c,Community owner,string peer)
    {
        var b=Plugin.Instance?.Network?.ResolveBuilding(c.target);
        if(b==null||b.Community!=owner||MoveConstructionCommands.IsStored(b)||b.BuildPhase!=BuildPhase.Finished)return "请选择本船已完工的炮位平台";
        if(c.kind=="cannonBuild")
        {
            if(HasMount(b))return "此平台已有火炮";
            if(!CanBuildMount(b))return "请先建造栈道浮台，再在本船浮台上安装火炮";
            var wood=FleetInteractions.ExtractItemsFromShip(owner,"Wood",6);
            var metal=FleetInteractions.ExtractItemsFromShip(owner,"Metal",8);
            if(wood.Count<6||metal.Count<8){FleetVoyages.ReturnCargo(owner,wood.Concat(metal));return "建造炮位需要木材6、金属8";}
            var created=CreateMount(b);created.BuildRemaining=12;return null;
        }
        if(!Mounts.TryGetValue(b.GetInstanceID(),out var m))return "该建筑没有炮位";
        if(c.kind=="cannonRemove")
        {
            if(m.Lease>Time.realtimeSinceStartup||m.Reload>0)return "请等待炮手退出和装填结束";
            ReleaseGunner(m);UnityEngine.Object.Destroy(m.Root.gameObject);Mounts.Remove(b.GetInstanceID());
            var catalog=Resources.FindObjectsOfTypeAll<ItemProperties>();
            var wood=catalog.FirstOrDefault(p=>p.name=="DryWood")??catalog.FirstOrDefault(p=>p.name=="WetWood");var metal=catalog.FirstOrDefault(p=>p.name=="MetalScrap")??catalog.FirstOrDefault(p=>p.name=="ScrapMetal")??catalog.FirstOrDefault(p=>p.name=="Metal");
            FleetVoyages.ReturnCargo(owner,(wood!=null?Enumerable.Repeat(wood,3):Enumerable.Empty<ItemProperties>()).Concat(metal!=null?Enumerable.Repeat(metal,4):Enumerable.Empty<ItemProperties>()));return null;
        }
        if(m.BuildRemaining>0)return "炮位仍在施工，需要一名空闲清醒居民完成安装";
        if(c.kind=="cannonLeave"){if(m.Operator==peer){m.Operator="";m.Lease=0;if(m.Reload<=0)ReleaseGunner(m);}return null;}
        var now=Time.realtimeSinceStartup;
        if(m.Operator!=""&&m.Operator!=peer&&m.Lease>now)return "炮位正由其他船员操纵";
        if(c.kind=="cannonEnter")
        {
            if(m.Gunner==null||!m.Gunner.IsAlive)
            {
                m.Gunner=owner.Agents.FirstOrDefault(a=>a!=null&&a.IsAlive&&a.CurrentActivity!=Activity.Sleeping&&!FleetInteractions.IsAgentBusy(a)&&(a.Assignment==null||a.Assignment.IsIdleProject));
                if(m.Gunner==null)return "需要一名清醒空闲的本船居民担任炮手";
                m.Gunner.Assignment?.Stop(ProjectFlags.Cancelled);
                m.Gunner.ReturnNavigator(true)?.StopNavigation(ProjectFlags.Cancelled);
            }
            m.Operator=peer;m.Lease=now+1.5f;return null;
        }
        if(m.Operator!=peer||m.Lease<now)return "请先进入自己的炮位";
        if(!Ballistics.ValidAim(c.x,c.z))return "炮口角度超出射界";
        m.Yaw=c.x;m.Pitch=c.z;m.Lease=now+1.5f;
        if(c.kind=="cannonAim")return null;
        if(!Plugin.Options.rules.friendlyFire)return "房间尚未开启炮战";
        if(m.Reload>0)return "火炮正在装填";
        if(!b.IsActive||BuildingDurability.IsDisabled(b))return "炮位已损坏或停用";
        if(m.Gunner==null||!m.Gunner.IsAlive||m.Gunner.CurrentActivity==Activity.Sleeping)return "炮手已离开炮位，请重新进入";
        var tier=Mathf.Clamp(c.value,0,2);
        if(!FleetInteractions.TryConsumeCannonAmmo(owner,tier,out _,out var error))return error;
        m.Reload=Ballistics.ReloadSeconds;m.Recoil=1;
        var rotation=b.transform.rotation*Quaternion.Euler(-m.Pitch,m.Yaw,0);
        var velocity=rotation*Vector3.forward*Ballistics.MuzzleSpeed;
        var position=Origin(b)+rotation*Vector3.forward*Ballistics.MuzzleOffset;
        var state=new CannonShellState{id=++_nextShell,x=position.x,y=position.y,z=position.z,vx=velocity.x,vy=velocity.y,vz=velocity.z,tier=tier,owner=Replication.ShipKey(owner)};
        Shells.Add(state.id,new Shell{State=state,Visual=ShellVisual(tier)});
        FleetPhysicalActors.SpawnMuzzleFlash(position,velocity.normalized);FleetPhysicalActors.TriggerCameraShake(position,.12f,.15f);
        FleetPhysicalActors.PlayFleetSound("cannon_fire",position,.9f);
        return null;
    }
    private static Vector3 Origin(Buildable b)=>Mounts.TryGetValue(b.GetInstanceID(),out var m)?m.Root.TransformPoint(Vector3.up*.65f):b.transform.TransformPoint(Vector3.up);
    private static void ReleaseGunner(Mount m){if(m.Gunner!=null&&m.Gunner.IsAlive){m.Gunner.UpdateActivity(Activity.Idling);m.Gunner.TryGoToTown();}m.Gunner=null;}
    private static Mount CreateMount(Buildable b)
    {
        if(Mounts.TryGetValue(b.GetInstanceID(),out var prior))return prior;
        var root=new GameObject("ScrapCannon").transform;root.SetParent(b.transform,false);root.localPosition=Vector3.up*.35f;
        var wood=new Color(.31f,.22f,.14f);var iron=new Color(.20f,.23f,.23f);
        Part(root,PrimitiveType.Cube,new Vector3(0,0,0),new Vector3(2.3f,.28f,2f),wood);
        for(var i=-1;i<=1;i+=2)Part(root,PrimitiveType.Cube,new Vector3(i*.8f,.45f,0),new Vector3(.25f,.9f,1.5f),wood);
        for(var x=-1;x<=1;x+=2)for(var z=-1;z<=1;z+=2)
            Part(root,PrimitiveType.Cylinder,new Vector3(x*1.05f,.1f,z*.65f),new Vector3(.65f,.12f,.65f),iron).localRotation=Quaternion.Euler(0,0,90);
        var turret=new GameObject("Traverse").transform;turret.SetParent(root,false);turret.localPosition=Vector3.up*.65f;
        Part(turret,PrimitiveType.Cylinder,Vector3.zero,new Vector3(.28f,1.1f,.28f),iron).localRotation=Quaternion.Euler(0,0,90);
        var barrel=new GameObject("Elevation").transform;barrel.SetParent(turret,false);
        // Open muzzle uses a purpose-built hollow mesh rather than a solid cylinder.
        var tube=new GameObject("ForgedBarrel",typeof(MeshFilter),typeof(MeshRenderer));tube.transform.SetParent(barrel,false);
        tube.GetComponent<MeshFilter>().sharedMesh=_barrelMesh??=BarrelMesh();tube.GetComponent<MeshRenderer>().sharedMaterial=Material(iron);
        for(var i=0;i<3;i++)Part(barrel,PrimitiveType.Cylinder,new Vector3(0,0,.4f+i*.65f),new Vector3(.83f,.065f,.83f),new Color(.12f,.13f,.13f)).localRotation=Quaternion.Euler(90,0,0);
        var layer=b.GetComponentsInChildren<Renderer>().FirstOrDefault(r=>r.gameObject!=tube)?.gameObject.layer??b.gameObject.layer;
        foreach(var t in root.GetComponentsInChildren<Transform>(true))t.gameObject.layer=layer;
        var m=new Mount{Building=b,Root=root,Turret=turret,Barrel=barrel};Mounts.Add(b.GetInstanceID(),m);return m;
    }
    private static Mesh BarrelMesh()
    {
        const int n=12;var vertices=new List<Vector3>();var triangles=new List<int>();
        for(var ring=0;ring<4;ring++)for(var i=0;i<n;i++){var a=i*Mathf.PI*2/n;var r=ring<2?.38f:.27f;vertices.Add(new Vector3(Mathf.Cos(a)*r,Mathf.Sin(a)*r,ring%2==0?-.45f:2.25f));}
        for(var i=0;i<n;i++){var j=(i+1)%n;Quad(i,j,n+j,n+i);Quad(2*n+j,2*n+i,3*n+i,3*n+j);Quad(n+i,n+j,3*n+j,3*n+i);Quad(j,i,2*n+i,2*n+j);}
        var mesh=new Mesh{name="TwelveSidedHollowCannon"};mesh.SetVertices(vertices);mesh.SetTriangles(triangles,0);mesh.RecalculateNormals();return mesh;
        void Quad(int a,int b,int c,int d){triangles.AddRange(new[]{a,b,c,a,c,d});}
    }
    private static Material Material(Color c)
    {
        if(Materials.TryGetValue(c,out var prior))return prior;
        // Flotsam uses URP: built-in Standard has no compatible render pass.
        var shader=Shader.Find("Universal Render Pipeline/Lit")??Shader.Find("Universal Render Pipeline/Unlit")??Shader.Find("Sprites/Default");
        var m=new Material(shader){name="Scrap cannon",color=c};
        if(m.HasProperty("_BaseColor"))m.SetColor("_BaseColor",c);
        if(m.HasProperty("_Smoothness"))m.SetFloat("_Smoothness",.2f);
        Materials.Add(c,m);return m;
    }
    private static Transform Part(Transform parent,PrimitiveType kind,Vector3 p,Vector3 scale,Color c)
    {
        var go=GameObject.CreatePrimitive(kind);go.transform.SetParent(parent,false);go.transform.localPosition=p;go.transform.localScale=scale;
        UnityEngine.Object.Destroy(go.GetComponent<Collider>());go.GetComponent<Renderer>().sharedMaterial=Material(c);return go.transform;
    }
    private static GameObject ShellVisual(int tier)
    {
        var go=GameObject.CreatePrimitive(PrimitiveType.Sphere);UnityEngine.Object.Destroy(go.GetComponent<Collider>());
        go.transform.localScale=Vector3.one*.25f;go.GetComponent<Renderer>().sharedMaterial=Material(tier==2?new Color(.2f,.6f,.7f):new Color(.15f,.13f,.10f));
        var trail=go.AddComponent<TrailRenderer>();trail.time=.35f;trail.startWidth=.12f;trail.endWidth=.01f;trail.material=FleetPhysicalActors.GetEmissiveMaterial(tier==2?new Color(.3f,.8f,1):new Color(.8f,.7f,.4f));return go;
    }
    internal static CannonState[] Capture()=>Mounts.Values.Where(m=>m.Building!=null).Select(m=>new CannonState{building=Plugin.Instance!.Network!.Id(m.Building),yaw=m.Yaw,pitch=m.Pitch,reload=m.Reload,buildRemaining=m.BuildRemaining,operatorId=m.Lease>Time.realtimeSinceStartup?m.Operator:""}).ToArray();
    internal static CannonShellState[] CaptureShells()=>Shells.Values.Select(s=>new CannonShellState{id=s.State.id,x=s.State.x,y=s.State.y,z=s.State.z,vx=s.State.vx,vy=s.State.vy,vz=s.State.vz,age=s.State.age,tier=s.State.tier,owner=s.State.owner}).ToArray();
    internal static void Apply(WorldFrame f)
    {
        var net=Plugin.Instance!.Network!;
        var mounted=new HashSet<int>();
        foreach(var c in f.cannons??Array.Empty<CannonState>()){var b=net.ResolveBuilding(c.building);if(b==null)continue;mounted.Add(b.GetInstanceID());var m=CreateMount(b);m.Yaw=c.yaw;m.Pitch=c.pitch;m.Reload=c.reload;m.BuildRemaining=c.buildRemaining;m.Operator=c.operatorId;m.Lease=Time.realtimeSinceStartup+1;}
        foreach(var pair in Mounts.ToArray())if(!mounted.Contains(pair.Key)){if(_operating==pair.Value.Building)Exit();UnityEngine.Object.Destroy(pair.Value.Root.gameObject);Mounts.Remove(pair.Key);}
        var ids=new HashSet<long>();
        foreach(var c in f.shells??Array.Empty<CannonShellState>()){ids.Add(c.id);if(!Shells.TryGetValue(c.id,out var s)){s=new Shell{Visual=ShellVisual(c.tier)};Shells.Add(c.id,s);var point=ClientOceanAnchor.HostToClientPos(new Vector3(c.x,c.y,c.z));FleetPhysicalActors.PlayFleetSound("cannon_fire",point,.5f);FleetPhysicalActors.SpawnMuzzleFlash(point,new Vector3(c.vx,c.vy,c.vz).normalized);s.Visual.transform.position=point;s.Visual.GetComponent<TrailRenderer>().Clear();}s.State=c;}
        foreach(var pair in Shells.ToArray())if(!ids.Contains(pair.Key)){UnityEngine.Object.Destroy(pair.Value.Visual);Shells.Remove(pair.Key);}
    }
    internal static void Tick(float dt)
    {
        if(!GameSession.Ready())return;
        foreach(var pair in Mounts.ToArray())
        {
            var m=pair.Value;if(m.Building==null){ReleaseGunner(m);if(m.Root!=null)UnityEngine.Object.Destroy(m.Root.gameObject);Mounts.Remove(pair.Key);continue;}
            m.Root.gameObject.SetActive(!MoveConstructionCommands.IsStored(m.Building));
            m.Reload=Mathf.Max(0,m.Reload-dt);
            m.Recoil=Mathf.Max(0,m.Recoil-dt*3);m.Barrel.localPosition=Vector3.back*(m.Recoil*.45f);
            if(Plugin.Instance?.Network?.IsHost==true)
            {
                if(m.BuildRemaining>0)
                {
                    if(m.Gunner==null||!m.Gunner.IsAlive){m.Gunner=m.Building.Community.Agents.FirstOrDefault(a=>a!=null&&a.IsAlive&&a.CurrentActivity!=Activity.Sleeping&&!FleetInteractions.IsAgentBusy(a)&&(a.Assignment==null||a.Assignment.IsIdleProject));m.Gunner?.Assignment?.Stop(ProjectFlags.Cancelled);}
                    if(m.Gunner!=null){m.BuildRemaining=Mathf.Max(0,m.BuildRemaining-dt);if(m.BuildRemaining==0){ReleaseGunner(m);FleetPhysicalActors.SpawnNativePopup(m.Building.transform.position+Vector3.up*4,"炮位安装完成");}}
                }
                if(m.Gunner!=null&&m.BuildRemaining<=0&&m.Lease<=Time.realtimeSinceStartup&&m.Reload<=0)ReleaseGunner(m);
                else if(m.Gunner!=null){m.Gunner.transform.position=m.Building.transform.TransformPoint(new Vector3(1.4f,.35f,-.7f));m.Gunner.UpdateActivity(Activity.Building);}
            }
            var yaw=_operating==m.Building?_yaw:m.Yaw;var pitch=_operating==m.Building?_pitch:m.Pitch;
            m.Turret.localRotation=Quaternion.Euler(0,yaw,0);m.Barrel.localRotation=Quaternion.Euler(-pitch,0,0);
        }
        var host=Plugin.Instance?.Network?.IsHost==true;
        foreach(var pair in Shells.ToArray())
        {
            var s=pair.Value;var c=s.State;var p=new Vector3(c.x,c.y,c.z);
            if(host)
            {
                var remaining=dt;
                while(remaining>0&&Shells.ContainsKey(pair.Key))
                {
                    var step=Mathf.Min(remaining,.02f);remaining-=step;
                    var next=p+new Vector3(c.vx,c.vy,c.vz)*step+Vector3.down*(.5f*Ballistics.Gravity*step*step);
                    var hit=TraceShell(p,next);
                    if(hit.collider!=null||next.y<=0||c.age>12)
                    {
                        var point=hit.collider!=null?hit.point:next;
                        var target=hit.collider!=null?hit.collider.GetComponentInParent<Buildable>():null;
                        if(hit.collider!=null)FleetVoyages.Damage(hit.collider.GetComponentInParent<Agent>(),target,point,FleetMechanicsMath.ComputeCannonDamage(c.tier,0));
                        if(target!=null){var owner=Replication.ResolveShipCommunity(c.owner);if(owner!=null&&target.Community!=owner)FleetVisibility.CheckAndBreakAllianceOnHostileAction(owner,target.Community);BuildingDurability.ApplyDamage(target,FleetMechanicsMath.ComputeCannonDamage(c.tier,BuildingDurability.GetArmorFor(target)),c.tier==2,point);}
                        FleetPhysicalActors.RecordAndSpawn("cannonImpact",target!=null?"炮弹命中":"炮弹落水或被遮挡",point,point,sourceShip:c.owner,targetBuilding:target!=null?Plugin.Instance!.Network!.Id(target):0);
                        UnityEngine.Object.Destroy(s.Visual);Shells.Remove(pair.Key);break;
                    }
                    p=next;c.vy-=Ballistics.Gravity*step;c.age+=step;c.x=p.x;c.y=p.y;c.z=p.z;
                }
            }
            if(!host){p+=new Vector3(c.vx,c.vy,c.vz)*dt;c.x=p.x;c.y=p.y;c.z=p.z;c.vy-=Ballistics.Gravity*dt;}
            if(s.Visual!=null)s.Visual.transform.position=host?p:ClientOceanAnchor.HostToClientPos(p);
        }
    }
    internal static void LateInput()
    {
        if(!Operating)return;
        var net=Plugin.Instance?.Network;
        if(_operating==null||net==null||!net.Connected||(net.IsClient&&!net.ReplicaReady)||!GameSession.Ready()||Input.GetKeyDown(KeyCode.Escape)){Exit();return;}
        if(FleetHud.RequestOpen||!Application.isFocused){SuspendInput();return;}
        if(_suspended){_suspended=false;var b=_operating;net.ExecuteOrSubmit(new GameCommand{kind="cannonEnter",target=net.Id(b)},completed:ok=>{if(!ok&&_operating==b)Exit();});if(!Operating)return;}
        if(Input.GetKeyDown(KeyCode.Tab))SetTactical(!Tactical);
        if(Input.GetKeyDown(KeyCode.Alpha1))_ammo=0;if(Input.GetKeyDown(KeyCode.Alpha2))_ammo=1;if(Input.GetKeyDown(KeyCode.Alpha3))_ammo=2;
        if(Tactical){TacticalInput(net);return;}
        Cursor.lockState=CursorLockMode.Locked;Cursor.visible=false;
        // These are the actual mouse axes shipped in Flotsam's InputManager.
        _yaw=Mathf.Clamp(_yaw+Input.GetAxisRaw("Rotate Horizontal Mouse")*1.4f,-120,120);_pitch=Mathf.Clamp(_pitch+Input.GetAxisRaw("Rotate Vertical Mouse")*1.1f,5,65);
        if(Time.unscaledTime>=_nextAim){_nextAim=Time.unscaledTime+.1f;net.ExecuteOrSubmit(new GameCommand{kind="cannonAim",target=net.Id(_operating),x=_yaw,z=_pitch});}
        if(Input.GetMouseButtonDown(0))Fire(net);
        var rotation=_operating.transform.rotation*Quaternion.Euler(-_pitch,_yaw,0);
        if(_camera!=null)
        {
            var origin=Origin(_operating);var forward=rotation*Vector3.forward;
            var position=origin-_operating.transform.rotation*Quaternion.Euler(0,_yaw,0)*Vector3.forward*5f+Vector3.up*1.1f;
            _camera.transform.SetPositionAndRotation(position,Quaternion.LookRotation(origin+forward*120f-position,Vector3.up));
            _camera.fieldOfView=Input.GetMouseButton(1)?35f:65f;
        }
    }
    internal static string GunStatus
    {
        get {if(_operating==null||!Mounts.TryGetValue(_operating.GetInstanceID(),out var m))return "";if(Time.unscaledTime>_statusAt){_statusAt=Time.unscaledTime+.5f;_metal=Replication.CollectShipStock(_operating.Community).Where(s=>FleetInteractions.MatchesItemOrCategory(s.Prop,"Metal")).Sum(s=>s.Count);}
            return new[]{"简易弹 · 湿木/海草/塑料×2","废铁弹 · 金属×2 (库存 "+_metal+")","电磁弹 · 核心×1 + 电力160"}[_ammo]+" · "+(m.Reload>0?$"装填 {m.Reload:0.0}s":"可以开火")+"\n"+(Tactical?_tacticalHint+"\n左键炮击 · WASD/中键移动 · 滚轮缩放 · Tab炮位视角":"鼠标瞄准 · 左键开火 · 右键观察 · Tab俯视炮击")+" · 1/2/3选弹 · Esc退出";}
    }
    private static RaycastHit TraceShell(Vector3 from,Vector3 to)
    {
        var travel=to-from;
        if(travel.sqrMagnitude<.0000001f)return default;
        int count;
        while((count=Physics.RaycastNonAlloc(from,travel.normalized,_shellHits,travel.magnitude,~0,QueryTriggerInteraction.Collide))==_shellHits.Length)Array.Resize(ref _shellHits,_shellHits.Length*2);
        var closest=default(RaycastHit);var distance=float.PositiveInfinity;
        for(var i=0;i<count;i++)
        {
            var hit=_shellHits[i];var collider=hit.collider;
            if(hit.distance>=distance||collider==null)continue;
            if(collider.isTrigger&&collider.GetComponentInParent<Buildable>()==null&&collider.GetComponentInParent<Landmark>()==null&&collider.GetComponentInParent<Agent>()==null)continue;
            closest=hit;distance=hit.distance;
        }
        return closest;
    }
    private static void Fire(GameSession net)
    {
        if(_operating==null||Time.unscaledTime<_nextFire)return;
        _nextFire=Time.unscaledTime+.3f;
        net.ExecuteOrSubmit(new GameCommand{kind="cannonFire",target=net.Id(_operating),x=_yaw,z=_pitch,value=_ammo},completed:ok=>{if(ok&&_operating!=null&&Mounts.TryGetValue(_operating.GetInstanceID(),out var fired))fired.Recoil=1;});
    }
    internal static CannonState? Save(Buildable b)
    {
        if(!Mounts.TryGetValue(b.GetInstanceID(),out var m))return null;
        return new CannonState{yaw=m.Yaw,pitch=m.Pitch,reload=m.Reload,buildRemaining=m.BuildRemaining};
    }
    internal static void Restore(Buildable b,CannonState? data)
    {
        if(data==null)return;
        var m=CreateMount(b);m.Yaw=data.yaw;m.Pitch=data.pitch;m.Reload=data.reload;m.BuildRemaining=data.buildRemaining;
    }
    internal static void RestoreShells(CannonShellState[]? states)
    {
        foreach(var c in states??Array.Empty<CannonShellState>())
        {
            if(Shells.ContainsKey(c.id))continue;
            var visual=ShellVisual(c.tier);visual.transform.position=new Vector3(c.x,c.y,c.z);visual.GetComponent<TrailRenderer>().Clear();
            Shells.Add(c.id,new Shell{State=c,Visual=visual});_nextShell=Math.Max(_nextShell,c.id);
        }
    }
    internal static float BuildRemaining(Buildable b)=>Mounts.TryGetValue(b.GetInstanceID(),out var m)?m.BuildRemaining:0;
    internal static void Reset()
    {
        Exit();foreach(var m in Mounts.Values){ReleaseGunner(m);if(m.Root!=null)UnityEngine.Object.Destroy(m.Root.gameObject);}Mounts.Clear();foreach(var s in Shells.Values)if(s.Visual!=null)UnityEngine.Object.Destroy(s.Visual);Shells.Clear();
    }
    internal static void Rebase(Vector3 oldPosition,Quaternion oldRotation,Vector3 newPosition,Quaternion newRotation)
    {
        var inverse=Quaternion.Inverse(newRotation);var rotation=inverse*oldRotation;
        foreach(var s in Shells.Values){var c=s.State;var p=inverse*(oldPosition+oldRotation*new Vector3(c.x,c.y,c.z)-newPosition);var v=rotation*new Vector3(c.vx,c.vy,c.vz);c.x=p.x;c.y=p.y;c.z=p.z;c.vx=v.x;c.vy=v.y;c.vz=v.z;}
    }
}
