using System;

namespace Flotsam.Multiplayer;

public static class Ballistics
{
    public const float Gravity=16f, MuzzleSpeed=52f, ReloadSeconds=3.8f;
    public const float MuzzleOffset=2.8f, MinPitch=5f, MaxPitch=65f, MaxYaw=120f;
    public static bool ValidAim(float yaw,float pitch) => float.IsFinite(yaw)&&float.IsFinite(pitch)&&yaw>=-MaxYaw&&yaw<=MaxYaw&&pitch>=MinPitch&&pitch<=MaxPitch;
    public static float FlightHeight(float height,float verticalVelocity,float seconds) => height+verticalVelocity*seconds-.5f*Gravity*seconds*seconds;
    public static float Range(float height,float pitch)
    {
        var angle=pitch*MathF.PI/180f;var sin=MathF.Sin(angle);var cos=MathF.Cos(angle);
        var vertical=MuzzleSpeed*sin;var muzzleHeight=height+MuzzleOffset*sin;
        return MuzzleOffset*cos+MuzzleSpeed*cos*(vertical+MathF.Sqrt(MathF.Max(0,vertical*vertical+2*Gravity*muzzleHeight)))/Gravity;
    }
    public static void RangeLimits(float height,out float minimum,out float maximum)
    {
        minimum=float.MaxValue;maximum=0;
        for(var p=MinPitch;p<=MaxPitch;p+=.5f){var r=Range(height,p);minimum=MathF.Min(minimum,r);maximum=MathF.Max(maximum,r);}
    }
    // Includes the muzzle's displacement from the elevation pivot. The preview
    // and authoritative shot therefore use the same launch position and speed.
    public static bool TrySolve(float distance,float height,float targetHeight,out float pitch)
    {
        pitch=MinPitch;
        if(!float.IsFinite(distance)||!float.IsFinite(height)||!float.IsFinite(targetHeight)||distance<=MuzzleOffset)return false;
        var previous=HeightAtDistance(distance,height,MinPitch)-targetHeight;
        if(MathF.Abs(previous)<.0001f)return true;
        for(var p=MinPitch+.25f;p<=MaxPitch;p+=.25f)
        {
            var current=HeightAtDistance(distance,height,p)-targetHeight;
            if((previous<=0&&current>=0)||(previous>=0&&current<=0))
            {
                var low=p-.25f;var high=p;var below=previous;
                for(var i=0;i<20;i++){var mid=(low+high)*.5f;var error=HeightAtDistance(distance,height,mid)-targetHeight;if((below<=0&&error>=0)||(below>=0&&error<=0))high=mid;else{low=mid;below=error;}}
                pitch=(low+high)*.5f;return true;
            }
            previous=current;
        }
        return false;
    }
    private static float HeightAtDistance(float distance,float height,float pitch)
    {
        var a=pitch*MathF.PI/180f;var cos=MathF.Cos(a);var sin=MathF.Sin(a);
        var t=(distance-MuzzleOffset*cos)/(MuzzleSpeed*cos);
        return FlightHeight(height+MuzzleOffset*sin,MuzzleSpeed*sin,t);
    }
}
