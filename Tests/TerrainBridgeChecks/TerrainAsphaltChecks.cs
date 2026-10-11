using System;
using System.Collections.Generic;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void TerrainAsphaltChecks()
    {
        Require((int)LTRoadMode.Offroad==0&&(int)LTRoadMode.Asphalt==1&&(int)LTRoadMode.TerrainAsphalt==2,"serialized modes preserved");
        Require(!LTLODMesh.RequiresFixedRoadSurface(LTRoadMode.TerrainAsphalt),"new asphalt does not pin terrain LOD");
        var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.TerrainAsphalt;settings.projection=LTRoadProjection.Spline;
        settings.width=12;settings.asphaltDamage.potholes=1;settings.asphaltDamage.size=3;settings.asphaltDamage.depth=.4f;
        settings.textureAcrossMetres=2.4f;settings.terrainCellSize=.25f;
        var path=new[]{new LTRoadPoint(new Vector3(0,2,0)),new LTRoadPoint(new Vector3(0,2,24)),new LTRoadPoint(new Vector3(8,3,48))};
        LTRoadMath.Snapshot Build(LTRoadMath.Settings s)=>LTRoadMath.Build(path,Matrix4x4.identity,s);
        var road=Build(settings);Require(road.textureAcrossMetres==2.4f,"asphalt layer spline tiling");
        var samples=new List<LTRoadMath.Sample>();var nodes=new List<LTRoadHeightKernel.Node>();var kernel=road.CopyHeightData(samples,nodes);
        var sb=new LTHeightJobMath.Buffer<LTRoadMath.Sample>{values=samples.ToArray()};
        var nb=new LTHeightJobMath.Buffer<LTRoadHeightKernel.Node>{values=nodes.ToArray()};
        int count=0,pits=0,removed=0;var random=new System.Random(557);
        for(int i=0;i<16000;i++)
        {
            float x=(float)random.NextDouble()*32-12,z=(float)random.NextDouble()*56-4;
            float expected=road.ApplyHeight(x,z,1),actual=LTRoadHeightKernel.Apply(kernel,sb,nb,x,z,1);
            Require(Math.Abs(expected-actual)<.00003f,"reference/worker height parity");
            road.TrySample(x,z,out var hit);
            LTRoadHeightKernel.Paint(kernel,hit,x,z,out float weight,out float wheels);
            Require(Math.Abs(weight-road.PaintWeight(x,z))<.00001f&&wheels==0,"reference/worker coverage and no wheel slot");
            var damage=road.AsphaltDamage(hit);
            Require(damage.removed>=0&&damage.removed<=1&&damage.depression>=0&&damage.depression<=.4f&&damage.cracks>=0&&damage.cracks<=1,"bounded damage");
            Require(damage.Equals(road.AsphaltDamage(hit)),"deterministic damage");
            if(Math.Abs(hit.lateral)<5&&hit.distance>2&&hit.distance<road.length-2)
            {
                if(damage.depression>.03f)pits++;
                if(weight<.01f&&damage.removed>.99f){removed++;Require(road.ClearWeight(x,z,LTDetailCategory.Vegetation)>.9f,"missing asphalt does not regrow vegetation");}
            }
            count++;
        }
        Require(pits>100&&removed>20,"test contains real depressions and fully exposed substrate");
        var cracks=settings;cracks.asphaltDamage.cracks=0;
        Require(Build(cracks).geometryHash==road.geometryHash&&Build(cracks).paintHash!=road.paintHash,"cracks are paint-only");
        var deep=settings;deep.asphaltDamage.depth=.7f;
        Require(Build(deep).geometryHash!=road.geometryHash,"depth invalidates mesh/collider");
        var shifted=settings;shifted.textureOffset=new Vector2(.2f,.7f);shifted.textureRepeatMetres=8;
        Require(Build(shifted).geometryHash==road.geometryHash,"texture phase never moves potholes");
        var off=settings;off.asphaltDamage.strength=0;var clean=Build(off);
        var neutralSettings=off;neutralSettings.mode=LTRoadMode.Offroad;neutralSettings.pattern=LTRoadPattern.Solid;
        var neutral=Build(neutralSettings);
        for(int i=3;i<20;i++)Require(clean.ApplyHeight(0,i,1)==neutral.ApplyHeight(0,i,1)&&clean.PaintWeight(0,i)==neutral.PaintWeight(0,i),"zero damage restores the same curved/banked clean surface");
        path[1]=new LTRoadPoint(path[1].position){overrideVariation=true,variationStrength=0};
        var local=Build(settings);local.TrySample(0,24,out var h);
        Require(local.AsphaltDamage(h).Equals(default(LTRoadAsphaltMath.Damage)),"point multiplier removes local damage");
        var middle=new Rect(-4,16,8,16);
        Require(!road.SamePaintRegion(local,middle)&&!road.SameTerrainRegion(local,middle),"point damage override invalidates cached local weights and terrain");
        var extended=new[]{new LTRoadPoint(new Vector3(0,2,-16)),new LTRoadPoint(new Vector3(0,2,0)),new LTRoadPoint(new Vector3(0,2,24)),new LTRoadPoint(new Vector3(8,3,48))};
        var longer=LTRoadMath.Build(extended,Matrix4x4.identity,settings);
        Require(!road.SamePaintRegion(longer,middle)&&!road.SameTerrainRegion(longer,middle),"upstream extension shifts damage and invalidates downstream caches");
        Require(local.SamePaintRegion(Build(shifted),middle),"UV-only changes preserve damage coverage cache");
        foreach(var mode in new[]{LTRoadMode.Offroad,LTRoadMode.Asphalt})
        {
            var old=settings;old.mode=mode;var a=Build(old);old.asphaltDamage.depth=1;old.asphaltDamage.cracks=0;var b=Build(old);
            Require(a.geometryHash==b.geometryHash&&a.paintHash==b.paintHash&&a.detailHash==b.detailHash,"legacy modes ignore all experimental settings");
            for(int i=0;i<20;i++)Require(a.ApplyHeight(1,i,1)==b.ApplyHeight(1,i,1)&&a.PaintWeight(1,i)==b.PaintWeight(1,i),"legacy surface unchanged");
        }
        bool rejected=false;try{var bad=settings;bad.asphaltDamage.size=bad.asphaltDamage.spacing;Build(bad);}catch(ArgumentException){rejected=true;}
        Require(rejected,"invalid pit size rejected");
        float coverage=0,crack=0;
        LTRoadAsphaltMath.CompositeCracks(ref coverage,ref crack,1,1,true);
        LTRoadAsphaltMath.CompositeCracks(ref coverage,ref crack,1,0,false); // later ground hides the first road
        LTRoadAsphaltMath.CompositeCracks(ref coverage,ref crack,.5f,0,true); // clean road over that ground
        Require(coverage==.5f&&crack==0,"occluded cracks never leak through a clean overlapping road");
        LTRoadAsphaltMath.CompositeCracks(ref coverage,ref crack,.5f,1,true);
        Require(coverage==.75f&&crack==.5f,"same-layer crack mix preserves ordered weights");
        Console.WriteLine($"PASS terrain asphalt: {count} reference/worker queries, {pits} depression samples, {removed} exposed-substrate samples; modes, neutral/local controls, hashes and no fixed LOD fence.");
    }
}
