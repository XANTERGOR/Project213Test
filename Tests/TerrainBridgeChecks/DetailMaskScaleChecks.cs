using System;
using System.Collections.Generic;
using System.IO;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void DetailMaskScaleChecks()
    {
        var entry=new LTDetailEntry{densityMask=true,patchCoverage=.5f,patchSize=6,patchSeed=123,
            patchScaleEdge=.35f,patchScaleInside=1,patchScaleSoftness=.4f};
        Require(!entry.patchScaleEnabled&&!new LTDetailDensityMask().patchScaleEnabled,"scale opt-in preserves existing assets");
        LTDetailMath.DensityMask(entry,3,7,19,out float disabled);
        Require(disabled==1,"disabled size mask identity");
        entry.patchScaleEnabled=true;
        Require(LTDetailMath.PatchScale(entry,0)==.35f&&LTDetailMath.PatchScale(entry,.5f)==.35f,"gap and boundary retain edge size");
        Require(LTDetailMath.PatchScale(entry,.8f)==1,"inside reaches full scale");
        float previous=0;
        for(int i=0;i<=1000;i++)
        {
            float current=LTDetailMath.PatchScale(entry,i/1000f);
            Require(current>=previous&&current>=.35f&&current<=1,"monotonic bounded scale ramp");previous=current;
        }
        float ramp=LTDetailMath.PatchScale(entry,.6f);
        entry.patchSoftness=0;entry.patchMinimumDensity=1;
        Require(LTDetailMath.PatchScale(entry,.6f)==ramp,"density softness and minimum independent from size");
        entry.patchScaleSoftness=.8f;
        Require(LTDetailMath.PatchScale(entry,.6f)<ramp,"larger scale softness widens inward transition");
        entry.patchScaleSoftness=0;
        Require(LTDetailMath.PatchScale(entry,.5f)==.35f&&LTDetailMath.PatchScale(entry,.5001f)==1,"zero softness is finite hard transition");
        entry.patchCoverage=0;Require(LTDetailMath.PatchScale(entry,1)==.35f,"zero coverage uses edge size even for gap vegetation");
        entry.patchCoverage=1;Require(LTDetailMath.PatchScale(entry,0)==1,"full coverage uses inside size");
        entry.patchCoverage=.5f;entry.patchScaleSoftness=.4f;entry.patchScaleInside=.2f;entry.patchScaleEdge=1.5f;
        Require(Math.Abs(LTDetailMath.PatchScale(entry,.8f)-.2f)<.000001f&&LTDetailMath.PatchScale(entry,0)==1.5f,"reversed artist multipliers retained, not reordered");
        entry.patchScaleEdge=.35f;entry.patchScaleInside=1;
        entry.patchSoftness=.2f;entry.patchMinimumDensity=.1f;
        int distinctSizes=0;
        for(int z=-30;z<30;z++)for(int x=-30;x<30;x++)
        {
            float px=x*.91f,pz=z*1.31f;
            entry.patchScaleEnabled=false;
            float before=LTDetailMath.DensityMask(entry,px,pz,17,out float identity);
            entry.patchScaleEnabled=true;
            float after=LTDetailMath.DensityMask(entry,px,pz,17,out float scale);
            Require(before==after&&identity==1,"size toggle does not alter density or accepted population");
            LTDetailMath.DensityMask(entry,px,pz,17,out float repeat);
            Require(scale==repeat&&scale>=.35f&&scale<=1,"deterministic scale independent of camera/order");
            entry.patchSize=12;
            LTDetailMath.DensityMask(entry,px*2,pz*2,17,out float stretched);
            Require(scale==stretched,"same terrain-space patch coordinates preserve scale");entry.patchSize=6;
            if(scale>.36f&&scale<.99f)distinctSizes++;
        }
        Require(distinctSizes>100,"noise produces intermediate scales");
        var common=new LTDetailDensityMask{enabled=true,patchScaleEnabled=true,patchScaleEdge=.25f,patchScaleInside=1.3f,patchScaleSoftness=.7f};
        var inherited=new LTDetailEntry();inherited.ResolveDensityMask(common);
        Require(inherited.patchScaleEnabled&&inherited.patchScaleEdge==.25f&&inherited.patchScaleInside==1.3f&&inherited.patchScaleSoftness==.7f,"common size settings inherited into snapshot");
        common.enabled=false;inherited.ResolveDensityMask(common);
        Require(LTDetailMath.PatchScale(inherited,1)==1,"disabled common mask bypasses scale");
        var own=new LTDetailEntry{densityMaskMode=LTDetailMaskMode.Own,patchScaleEnabled=true,patchScaleEdge=.7f};own.ResolveDensityMask(common);
        Require(own.patchScaleEdge==.7f&&own.densityMask,"own scale settings independent of common");
        var none=new LTDetailEntry{densityMaskMode=LTDetailMaskMode.None,patchScaleEnabled=true};none.ResolveDensityMask(common);
        Require(LTDetailMath.PatchScale(none,1)==1,"no mask bypasses size even with dormant settings");
        entry.patchScaleEdge=0;entry.patchScaleInside=-1;entry.patchScaleSoftness=3;entry.Validate(new HashSet<string>());
        Require(entry.patchScaleEdge==.01f&&entry.patchScaleInside==.01f&&entry.patchScaleSoftness==1,"entry clamps degenerate scale");
        common.patchScaleEdge=-1;common.patchScaleInside=0;common.patchScaleSoftness=-2;common.Validate();
        Require(common.patchScaleEdge==.01f&&common.patchScaleInside==.01f&&common.patchScaleSoftness==0,"common clamps degenerate scale");
        var placement=entry.PlacementSettings();
        Require(!ReferenceEquals(entry,placement)&&placement.scaleRange==Vector2.one&&
            !placement.patchScaleEnabled&&placement.patchScaleEdge==1&&placement.patchScaleInside==1&&placement.patchScaleSoftness==0,
            "placement snapshot strips only size settings");
        Require(entry.patchScaleEnabled&&entry.patchScaleEdge==.01f&&entry.patchScaleSoftness==1,
            "placement snapshot does not mutate authored size");
        var sizeFields=new HashSet<string>{"scaleRange","patchScaleEnabled","patchScaleEdge","patchScaleInside","patchScaleSoftness"};
        foreach(var field in typeof(LTDetailEntry).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public))
            if(!sizeFields.Contains(field.Name))Require(Equals(field.GetValue(entry),field.GetValue(placement)),"placement key retains "+field.Name);
        var renderer=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTDetailRenderer.cs");
        int scaleAt=renderer.IndexOf("scale*=maskScale;");
        Require(scaleAt>=0&&scaleAt<renderer.IndexOf("var matrix=Matrix4x4.TRS(position+normalWS*offset")&&
            renderer.Contains("lodSize=recipe.lodSize*scale*rootScale"),"scale applied before shared matrices, bounds and LOD construction");
        Console.WriteLine("PASS density-mask scale: 3600 stable samples, unchanged density, inward ramp, independent softness/floor, inheritance, opt-out and bounds/LOD source contract. Live appearance still requires Unity.");
    }
}
