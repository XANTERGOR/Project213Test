using System;
using LocalTerrainPrototype;

partial class Checks
{
    static void RoadAutoLODChecks()
    {
        Require(LTRoadLODPolicy.AutomaticCount(3,new[]{3})==2,"prefab count overrides configured auto count");
        Require(LTRoadLODPolicy.AutomaticCount(3,new[]{1})==0,"single authored level stays single");
        Require(LTRoadLODPolicy.AutomaticCount(0,new[]{3})==2,"empty settings do not disable prefab LODs");
        Require(LTRoadLODPolicy.AutomaticCount(3,new[]{2,3,0})==3,"mixed prefab and auto counts");
        Require(LTRoadLODPolicy.AutomaticCount(3,new[]{8,0})==7,"eight prefab levels retained");
        for(int authored=1;authored<=8;authored++)for(int l=0;l<8;l++)
            Require(LTRoadLODPolicy.SourceLevel(l,authored)==Math.Min(l,authored-1),"hold last authored level");
        var settings=new[]{new LTLODSettings(50),new LTLODSettings(120),new LTLODSettings(250)};
        for(int l=1;l<=7;l++)
        {
            float distance=LTRoadLODPolicy.Distance(settings,l);
            Require(distance==LTRoadLODPolicy.DefaultDistance(l),"default distance extension");
            Require(LTRoadLODPolicy.Select(distance+6,0,settings,8,5)==l,"outward threshold activates authored LOD"+l);
            Require(LTRoadLODPolicy.Select(distance-4,l,settings,8,5)==l,"hysteresis holds authored LOD"+l);
            Require(LTRoadLODPolicy.Select(distance-6,l,settings,8,5)==l-1,"inward threshold"+l);
        }
        Require(LTRoadLODPolicy.Select(100000,0,settings,8,5)==7,"distant road not culled");
        Require(LTRoadLODPolicy.Select(100000,0,settings,2,5)==1,"selection respects actual baked levels");
        Require(LTRoadLODPolicy.Select(100000,7,settings,1,5)==0,"single level");
        Require(LTRoadLODPolicy.Select(121,0,Array.Empty<LTLODSettings>(),3,0)==2,"defaults without settings");
        Require(LTRoadLODPolicy.Distance(new[]{new LTLODSettings(1000)},3)==4000,"custom distance extension remains increasing");
        foreach(float invalid in new[]{-1,float.NaN,float.PositiveInfinity})
            Require(LTRoadLODPolicy.Select(10000,0,new[]{new LTLODSettings(invalid)},8,0)==0,"invalid threshold safe LOD0");
        Require(LTRoadLODPolicy.Select(10000,0,new[]{new LTLODSettings(100),new LTLODSettings(50)},8,0)==0,"unordered thresholds safe LOD0");
        bool rejected=false;try{LTRoadLODPolicy.AutomaticCount(3,new[]{9});}catch(InvalidOperationException){rejected=true;}
        Require(rejected,"unsupported level counts rejected, not truncated");
        int previous=0;
        for(int d=0;d<5000;d++)
        {int level=LTRoadLODPolicy.Select(d,previous,settings,8,5);Require(level>=previous,"outward flight monotonic");previous=level;}
        for(int d=5000;d>=0;d--)
        {int level=LTRoadLODPolicy.Select(d,previous,settings,8,5);Require(level<=previous,"inward flight monotonic");previous=level;}
        Console.WriteLine("Road auto LOD policy PASS: per-prefab counts, mixed fallback, hold last, LOD0–7 distances, hysteresis, 10,001 flight samples, invalid thresholds.");
    }
}

namespace LocalTerrainPrototype
{
    // Only the production policy is linked; the MonoBehaviour world/settings container is not.
    public sealed class LTLODSettings
    {
        public float startDistance;
        public LTLODSettings(float distance){startDistance=distance;}
    }
}
