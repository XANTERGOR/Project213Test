using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadVariationChecks()
    {
        var points=new[]{new LTRoadPoint(new Vector3(0,2,0)),new LTRoadPoint(new Vector3(0,2,40)),new LTRoadPoint(new Vector3(0,2,80))};
        var s=LTRoadMath.Settings.Default;s.edgeNoise=0;s.clearStones=true;s.projection=LTRoadProjection.Spline;
        LTRoadMath.Snapshot Build(LTRoadMath.Settings settings)=>LTRoadMath.Build(points,Matrix4x4.identity,settings);
        var original=Build(s);s.variation.enabled=true;var road=Build(s);var repeat=Build(s);
        int probes=0;bool leftRight=false,widthChanged=false,patches=false,ruts=false;
        for(float z=-6;z<=86;z+=.31f)for(float x=-9;x<=9;x+=.23f)
        {
            float paint=road.PaintWeight(x,z),height=road.ApplyHeight(x,z,0),clear=road.ClearWeight(x,z,LTDetailCategory.Vegetation);
            Require(float.IsFinite(height)&&paint>=0&&paint<=1&&clear>=0&&clear<=1,"variation queries stay finite and normalized");
            Require(paint==repeat.PaintWeight(x,z)&&height==repeat.ApplyHeight(x,z,0),"variation must be deterministic");
            if(paint>0||height!=0)
            {
                Require(road.bounds.Contains(new Vector2(x,z)),"all varied surfaces must stay inside conservative bounds");
                Require(road.Intersects(new Rect(x,z,.0001f,.0001f)),"density/dirty footprint must include widened shoulders");
            }
            if(z>4&&z<76&&Math.Abs(x)<.2f)patches|=paint<.9f;
            if(z>4&&z<76&&Math.Abs(x-.9f)<.1f)ruts|=height<1.99f;
            if(z>4&&z<76&&Math.Abs(x)>2.7f&&Math.Abs(x)<3.4f)widthChanged|=Math.Abs(clear-original.ClearWeight(x,z,LTDetailCategory.Vegetation))>.03f;
            probes++;
        }
        foreach(var sample in road.samples)
        {
            var hit=new LTRoadMath.Hit{position=sample.position,distance=sample.distance,variationStrength=sample.variationStrength,lateral=-3};
            float left=road.HalfWidth(hit);hit.lateral=3;float right=road.HalfWidth(hit);
            Require(left>0&&right>0&&left<=road.MaxHalfWidth&&right<=road.MaxHalfWidth,"preview widths stay inside maximum influence");
            leftRight|=Math.Abs(left-right)>.02f;
        }
        Require(patches&&ruts&&widthChanged&&leftRight,"enabled defaults produce patches, solid-road ruts and asymmetric edges");
        // Off is an exact fallback, including stale/inactive local overrides.
        s.variation.enabled=false;s.variation.seed++;var disabled=Build(s);
        Require(disabled.geometryHash==original.geometryHash&&disabled.paintHash==original.paintHash&&disabled.detailHash==original.detailHash,"disabled variation retains old signatures");
        s.variation.enabled=true;s.variation.strength=0;var zero=Build(s);
        for(float z=-3;z<84;z+=.73f)for(float x=-7;x<7;x+=.41f)
        {
            Require(disabled.PaintWeight(x,z)==original.PaintWeight(x,z)&&zero.PaintWeight(x,z)==original.PaintWeight(x,z),"disabled/zero paint exact parity");
            Require(disabled.ApplyHeight(x,z,0)==original.ApplyHeight(x,z,0)&&zero.ApplyHeight(x,z,0)==original.ApplyHeight(x,z,0),"disabled/zero height exact parity");
        }
        s.variation=LTRoadVariation.Default;s.variation.enabled=true;
        var baseline=Build(s);s.variation.patchStrength=.9f;s.variation.patchSize=3;var paintEdit=Build(s);
        Require(baseline.geometryHash==paintEdit.geometryHash&&baseline.detailHash==paintEdit.detailHash&&baseline.paintHash!=paintEdit.paintHash,"patch-only edits must not invalidate road geometry or removal masks");
        Require(LTRoadProjectionMath.SameInputs(baseline,paintEdit,false)&&!LTRoadProjectionMath.SameInputs(baseline,paintEdit,true),"single-road UV cache reuses patches; shared-layer ownership rebakes");
        var rect=new Rect(-10,-8,20,96);
        var mapA=LTRoadProjectionMath.Bake(new[]{baseline},rect,65);
        var mapB=LTRoadProjectionMath.Bake(new[]{paintEdit},rect,65);
        Require(mapA.coordinates.SequenceEqual(mapB.coordinates),"retained single-road projection must equal a fresh bake after patch edit");
        for(float z=2;z<79;z+=.37f)
        {
            Require(baseline.ApplyHeight(1,z,0)==paintEdit.ApplyHeight(1,z,0),"patches cannot deform the road");
            Require(baseline.ClearWeight(1,z,LTDetailCategory.Vegetation)==paintEdit.ClearWeight(1,z,LTDetailCategory.Vegetation),"patches cannot regrow vegetation");
            Require(baseline.ClearWeight(1,z,LTDetailCategory.Stones)==paintEdit.ClearWeight(1,z,LTDetailCategory.Stones),"patches cannot leave stones on the carriageway");
        }
        var previous=Build(s);s.variation.widthAmount=.3f;var wider=Build(s);
        Require(previous.geometryHash!=wider.geometryHash&&previous.detailHash!=wider.detailHash&&previous.paintHash!=wider.paintHash,"width affects terrain, mask and detail placement");
        Require(!LTRoadProjectionMath.SameInputs(previous,wider,false),"widening must invalidate the sparse UV footprint");
        float savedRut=s.variation.rutVariation;s.variation.rutVariation=.05f;var rutEdit=Build(s);
        Require(wider.geometryHash!=rutEdit.geometryHash&&wider.detailHash==rutEdit.detailHash,"solid-road rut edit changes heights but not carriageway clearing");
        s.variation.rutVariation=savedRut;s.pattern=LTRoadPattern.Tracks;var tracks=Build(s);
        for(float z=-2;z<83;z+=.17f)
        {
            Require(tracks.PaintWeight(0,z)==0&&tracks.ClearWeight(0,z,LTDetailCategory.Vegetation)==0,"Tracks retains the central gap");
        }
        var noPatch=s;noPatch.variation.patchStrength=0;
        var trackNoPatch=Build(noPatch);
        Require(trackNoPatch.geometryHash==tracks.geometryHash&&trackNoPatch.detailHash==tracks.detailHash,"track patch edits also remain paint-only");
        // Local overrides are copied into the immutable sampled profile and cache input.
        s.pattern=LTRoadPattern.Solid;
        for(int i=0;i<points.Length;i++){points[i].overrideVariation=true;points[i].variationStrength=0;}
        var localZero=Build(s);
        Require(localZero.samples.All(p=>p.variationStrength==0),"zero local profile is sampled");
        for(float z=3;z<78;z+=.41f)
            Require(localZero.PaintWeight(0,z)==original.PaintWeight(0,z)&&localZero.ApplyHeight(.9f,z,0)==original.ApplyHeight(.9f,z,0),"zero point profile restores original surface");
        points[1].variationStrength=1;var localMiddle=Build(s);
        Require(localMiddle.geometryHash!=localZero.geometryHash&&localMiddle.paintHash!=localZero.paintHash,"profile edit invalidates consumers");
        Require(localZero.samples.All(p=>p.variationStrength==0),"subsequent point edits cannot mutate captured profile");
        localMiddle.TrySample(0,40,out var middle);localMiddle.TrySample(0,0,out var start);
        Require(middle.variationStrength==1&&start.variationStrength==0,"authored point profile endpoints preserved");
        foreach(var p in localMiddle.samples)Require(p.variationStrength>=0&&p.variationStrength<=1,"interpolated profile cannot overshoot");
        for(int i=0;i<points.Length;i++)points[i].overrideVariation=false;
        // Junction mouths and the full straight neck remain the existing shape.
        s.straightStart=s.straightEnd=true;s.junctionStartLength=s.junctionEndLength=4;
        var connected=Build(s);var connectedOff=s;connectedOff.variation.enabled=false;var normalConnected=Build(connectedOff);
        foreach(float z in new[]{-1f,0f,1f,3.99f,76.01f,79f,80f,81f})for(float x=-4;x<=4;x+=.2f)
        {
            Require(connected.ApplyHeight(x,z,0)==normalConnected.ApplyHeight(x,z,0),"junction entrance height must not change with variation");
            Require(connected.PaintWeight(x,z)==normalConnected.PaintWeight(x,z),"junction entrance coverage must not change with variation");
        }
        s.mode=LTRoadMode.Asphalt;var asphalt=Build(s);s.variation.enabled=false;var asphaltOff=Build(s);
        Require(asphalt.geometryHash==asphaltOff.geometryHash&&asphalt.paintHash==asphaltOff.paintHash&&asphalt.detailHash==asphaltOff.detailHash,"asphalt must ignore offroad variation");
        for(float z=-3;z<84;z+=.73f)Require(asphalt.ApplyHeight(2,z,0)==asphaltOff.ApplyHeight(2,z,0)&&asphalt.PaintWeight(2,z)==asphaltOff.PaintWeight(2,z),"asphalt output parity");
        // Hot queries remain allocation-free; this is not a live Unity benchmark.
        float Run(){float sum=0;for(int i=0;i<2000;i++){float x=i%37*.2f-3.5f,z=i%241*.31f;sum+=road.PaintWeight(x,z)+road.ApplyHeight(x,z,0)+road.ClearWeight(x,z,LTDetailCategory.Vegetation);}return sum;}
        Run();long before=GC.GetAllocatedBytesForCurrentThread();float checksum=Run();long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Require(allocated==0&&float.IsFinite(checksum),"variation queries must not allocate");
        var invalid=LTRoadMath.Settings.Default;invalid.variation.enabled=true;invalid.variation.widthLength=float.NaN;
        bool rejected=false;try{Build(invalid);}catch(ArgumentException){rejected=true;}Require(rejected,"invalid enabled settings fail safely");
        string runtime=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTRoad.cs"),editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTRoadEditor.cs");
        Require(runtime.Contains("variation=variation")&&runtime.Contains("points[i].VariationStrength!=cachedPoints[i].VariationStrength"),"runtime captures variation and invalidates point-profile edits");
        Require(editor.Contains("snapshot.HalfWidth(hit)")&&editor.Contains("variation.enabled")&&editor.Contains("overrideVariation"),"preview and inspector expose the tested road shape/profile");
        Console.WriteLine($"PASS road variation: {probes} bounded deterministic samples; exact disabled/asphalt parity; patches paint-only; single-road UV reuse; asymmetric edges; local profiles; unchanged junction mouths; {allocated} B query allocation. Managed math/source checks, not native scene rendering.");
    }
}
