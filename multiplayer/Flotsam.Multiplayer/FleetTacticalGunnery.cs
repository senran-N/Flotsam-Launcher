using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

namespace Flotsam.Multiplayer;

internal static partial class FleetCombat
{
    private static bool _tactical;
    internal static bool Tactical=>Operating&&_tactical;
    private static GameObject? _tacticalVisuals;
    private static Mesh? _rangeMesh;
    private static Material? _rangeMaterial,_guideMaterial;
    private static LineRenderer? _rangeEdge,_trajectory,_impactRing;
    private static Vector3 _tacticalCenter,_dragPoint;
    private static Quaternion _tacticalHeading;
    private static float _tacticalHeight,_rangeHeight=float.NaN,_nextPreview;
    private static bool _dragging;
    private static string _tacticalHint="移动鼠标选择落点";
    private static readonly List<Vector3> PreviewPoints=new List<Vector3>(192);

    private static void SetTactical(bool tactical)
    {
        _tactical=tactical;_dragging=false;
        if(_operating==null||_camera==null)return;
        Cursor.lockState=tactical?CursorLockMode.None:CursorLockMode.Locked;Cursor.visible=tactical;
        if(!tactical){if(_tacticalVisuals!=null)_tacticalVisuals.SetActive(false);return;}
        var origin=Origin(_operating);Ballistics.RangeLimits(origin.y,out _,out var range);
        _tacticalHeading=Quaternion.Euler(0,_operating.transform.eulerAngles.y,0);
        _tacticalCenter=new Vector3(origin.x,0,origin.z);_tacticalHeight=Mathf.Max(90,range*1.1f);
        _tacticalHint="移动鼠标选择落点";_nextFire=Time.unscaledTime+.25f;_nextPreview=0;
        UpdateTacticalCamera();EnsureTacticalVisuals();
    }
    private static void UpdateTacticalCamera()
    {
        if(_camera==null)return;
        _camera.fieldOfView=65;
        _camera.transform.SetPositionAndRotation(_tacticalCenter+_tacticalHeading*new Vector3(0,_tacticalHeight,-_tacticalHeight*.58f),_tacticalHeading*Quaternion.Euler(60,0,0));
    }
    private static void TacticalInput(GameSession net)
    {
        if(_operating==null||_camera==null)return;
        Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
        var overUi=EventSystem.current!=null&&EventSystem.current.IsPointerOverGameObject();
        var dt=Mathf.Min(Time.unscaledDeltaTime,.1f);
        var move=new Vector3((Input.GetKey(KeyCode.D)?1:0)-(Input.GetKey(KeyCode.A)?1:0),0,(Input.GetKey(KeyCode.W)?1:0)-(Input.GetKey(KeyCode.S)?1:0));
        _tacticalCenter+=_tacticalHeading*move.normalized*(_tacticalHeight*.8f*dt);
        if(!overUi)_tacticalHeight=Mathf.Clamp(_tacticalHeight*Mathf.Exp(-Input.mouseScrollDelta.y*.12f),35,340);
        var sea=new Plane(Vector3.up,Vector3.zero);
        var ray=_camera.ScreenPointToRay(Input.mousePosition);
        if(Input.GetMouseButton(2)&&!overUi&&sea.Raycast(ray,out var dragDistance))
        {
            var point=ray.GetPoint(dragDistance);
            if(_dragging)_tacticalCenter+=_dragPoint-point;
            else _dragPoint=point;
            _dragging=true;
        }
        else _dragging=false;
        var origin=Origin(_operating);var center=new Vector3(origin.x,0,origin.z);
        _tacticalCenter=center+Vector3.ClampMagnitude(_tacticalCenter-center,450);
        UpdateTacticalCamera();
        // Recompute after camera movement so the arc remains under the cursor.
        ray=_camera.ScreenPointToRay(Input.mousePosition);
        EnsureTacticalVisuals();UpdateFiringRange(origin);
        var canAim=false;
        if(overUi)_tacticalHint="鼠标移至海面瞄准";
        else if(sea.Raycast(ray,out var distance))
        {
            var target=ray.GetPoint(distance);var offset=target-origin;offset.y=0;
            var local=Quaternion.Inverse(_operating.transform.rotation)*offset;
            var yaw=Mathf.Atan2(local.x,local.z)*Mathf.Rad2Deg;
            if(Mathf.Abs(yaw)>Ballistics.MaxYaw)_tacticalHint="超出炮位射界";
            else if(!Ballistics.TrySolve(offset.magnitude,origin.y,0,out var pitch))_tacticalHint="超出射程或距离过近";
            else
            {
                _yaw=yaw;_pitch=pitch;canAim=true;
                if(Time.unscaledTime>=_nextPreview||Input.GetMouseButtonDown(0))
                {
                    _nextPreview=Time.unscaledTime+.05f;
                    DrawTrajectory(origin,out var hitPoint,out var blocked);
                    DrawImpact(hitPoint,blocked?new Color(1,.55f,.2f):new Color(.3f,1,.72f));
                    _tacticalHint=(blocked?"弹道遇到遮挡 · 先命中橙色位置":$"落点距离 {offset.magnitude:0}m · 仰角 {pitch:0}°");
                }
            }
        }
        else _tacticalHint="鼠标移至海面瞄准";
        _trajectory!.enabled=canAim;_impactRing!.enabled=canAim;
        if(Time.unscaledTime>=_nextAim){_nextAim=Time.unscaledTime+.1f;net.ExecuteOrSubmit(new GameCommand{kind="cannonAim",target=net.Id(_operating),x=_yaw,z=_pitch});}
        // Clicks on controls and middle-button panning never fire a shot.
        if(canAim&&!_dragging&&Input.GetMouseButtonDown(0))Fire(net);
    }
    private static void EnsureTacticalVisuals()
    {
        if(_tacticalVisuals!=null){_tacticalVisuals.SetActive(true);return;}
        _tacticalVisuals=new GameObject("Cannon firing range and ballistic preview");
        _rangeMesh=new Mesh{name="Cannon reachable firing sector"};
        var fill=new GameObject("Firing sector",typeof(MeshFilter),typeof(MeshRenderer));fill.transform.SetParent(_tacticalVisuals.transform,false);
        fill.GetComponent<MeshFilter>().sharedMesh=_rangeMesh;
        if(_rangeMaterial==null)
        {
            _rangeMaterial=new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            _rangeMaterial.SetColor("_BaseColor",new Color(.18f,.8f,.9f,.10f));
            _rangeMaterial.SetFloat("_Surface",1);_rangeMaterial.SetFloat("_SrcBlend",(float)BlendMode.SrcAlpha);_rangeMaterial.SetFloat("_DstBlend",(float)BlendMode.OneMinusSrcAlpha);
            _rangeMaterial.SetFloat("_ZWrite",0);_rangeMaterial.SetFloat("_Cull",(float)CullMode.Off);_rangeMaterial.SetOverrideTag("RenderType","Transparent");_rangeMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");_rangeMaterial.renderQueue=3000;
        }
        fill.GetComponent<MeshRenderer>().sharedMaterial=_rangeMaterial;
        _rangeEdge=Guide("Range boundary",new Color(.25f,.85f,.95f),.35f);
        _trajectory=Guide("Ballistic trajectory",new Color(.3f,1,.72f),.45f);
        _impactRing=Guide("Predicted first impact",new Color(.3f,1,.72f),.28f);
        _rangeHeight=float.NaN;
    }
    private static LineRenderer Guide(string name,Color color,float width)
    {
        var go=new GameObject(name);go.transform.SetParent(_tacticalVisuals!.transform,false);
        var line=go.AddComponent<LineRenderer>();line.useWorldSpace=true;line.widthMultiplier=width;line.numCornerVertices=2;
        _guideMaterial??=new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
        line.sharedMaterial=_guideMaterial;line.startColor=line.endColor=color;line.shadowCastingMode=ShadowCastingMode.Off;line.receiveShadows=false;return line;
    }
    private static void UpdateFiringRange(Vector3 origin)
    {
        if(_operating==null||_tacticalVisuals==null)return;
        _tacticalVisuals.transform.SetPositionAndRotation(new Vector3(origin.x,.4f,origin.z),Quaternion.Euler(0,_operating.transform.eulerAngles.y,0));
        Ballistics.RangeLimits(origin.y,out var min,out var max);
        const int segments=96;
        if(float.IsNaN(_rangeHeight)||Mathf.Abs(_rangeHeight-origin.y)>.05f)
        {
            _rangeHeight=origin.y;var vertices=new Vector3[(segments+1)*2];var triangles=new int[segments*6];
            for(var i=0;i<=segments;i++)
            {
                var angle=Mathf.Lerp(-Ballistics.MaxYaw,Ballistics.MaxYaw,i/(float)segments)*Mathf.Deg2Rad;var direction=new Vector3(Mathf.Sin(angle),0,Mathf.Cos(angle));
                vertices[i*2]=direction*min;vertices[i*2+1]=direction*max;
                if(i==segments)continue;var j=i*6;var a=i*2;triangles[j]=a;triangles[j+1]=a+1;triangles[j+2]=a+3;triangles[j+3]=a;triangles[j+4]=a+3;triangles[j+5]=a+2;
            }
            _rangeMesh!.Clear();_rangeMesh.vertices=vertices;_rangeMesh.triangles=triangles;_rangeMesh.RecalculateBounds();
        }
        _rangeEdge!.positionCount=(segments+1)*2;_rangeEdge.loop=true;
        for(var i=0;i<=segments;i++)
        {
            var angle=Mathf.Lerp(-Ballistics.MaxYaw,Ballistics.MaxYaw,i/(float)segments)*Mathf.Deg2Rad;var direction=new Vector3(Mathf.Sin(angle),0,Mathf.Cos(angle));
            _rangeEdge.SetPosition(i,_tacticalVisuals.transform.TransformPoint(direction*max));
            _rangeEdge.SetPosition((segments+1)*2-1-i,_tacticalVisuals.transform.TransformPoint(direction*min));
        }
    }
    private static void DrawTrajectory(Vector3 origin,out Vector3 impact,out bool blocked)
    {
        var rotation=_operating!.transform.rotation*Quaternion.Euler(-_pitch,_yaw,0);
        var velocity=rotation*Vector3.forward*Ballistics.MuzzleSpeed;
        var point=origin+rotation*Vector3.forward*Ballistics.MuzzleOffset;
        PreviewPoints.Clear();PreviewPoints.Add(point);impact=point;blocked=false;
        for(var i=0;i<600;i++)
        {
            const float step=.02f;
            var next=point+velocity*step+Vector3.down*(.5f*Ballistics.Gravity*step*step);
            var hit=TraceShell(point,next);
            if(hit.collider!=null||next.y<=0)
            {
                // Match the authoritative simulation's first collision, including
                // buildings, residents, terrain and open-water misses.
                impact=hit.collider!=null?hit.point:next;blocked=hit.collider!=null;PreviewPoints.Add(impact);break;
            }
            point=next;velocity.y-=Ballistics.Gravity*step;impact=point;
            if(i%4==0)PreviewPoints.Add(point);
        }
        _trajectory!.enabled=true;_trajectory.positionCount=PreviewPoints.Count;
        for(var i=0;i<PreviewPoints.Count;i++)_trajectory.SetPosition(i,PreviewPoints[i]);
        _trajectory.startColor=_trajectory.endColor=blocked?new Color(1,.55f,.2f):new Color(.3f,1,.72f);
    }
    private static void DrawImpact(Vector3 position,Color color)
    {
        _impactRing!.enabled=true;_impactRing.loop=true;_impactRing.positionCount=32;_impactRing.startColor=_impactRing.endColor=color;
        position.y=Mathf.Max(.18f,position.y+.12f);
        for(var i=0;i<32;i++){var angle=i*Mathf.PI*2/32;_impactRing.SetPosition(i,position+new Vector3(Mathf.Sin(angle)*1.5f,0,Mathf.Cos(angle)*1.5f));}
    }
    private static void ClearTactical()
    {
        _tactical=false;_dragging=false;
        if(_tacticalVisuals!=null)UnityEngine.Object.Destroy(_tacticalVisuals);
        if(_rangeMesh!=null)UnityEngine.Object.Destroy(_rangeMesh);
        _tacticalVisuals=null;_rangeMesh=null;_rangeEdge=null;_trajectory=null;_impactRing=null;
    }
}
