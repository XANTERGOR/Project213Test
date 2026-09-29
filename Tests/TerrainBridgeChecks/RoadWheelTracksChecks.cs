using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadWheelTracksChecks()
    {
        var points=new[]{new LTRoadPoint(new Vector3(0,2,0)),new LTRoadPoint(new Vector3(0,2,30)),new LTRoadPoint(new Vector3(0,2,60))};
        LTRoadMath.Snapshot Build(LTRoadMath.Settings value)=>LTRoadMath.Build(points,Matrix4x4.identity,value);
        var off=LTRoadMath.Settings.Default;off.edgeNoise=0;off.projection=LTRoadProjection.Spline;
        var original=Build(off);var s=off;s.wheelTracks.enabled=true;s.wheelLayerId=42;
        var road=Build(s);var view=road.WheelLayerProjection();
        Require(ReferenceEquals(view,road.WheelLayerProjection())&&ReferenceEquals(view.samples,road.samples),"wheel UV view must reuse immutable samples and the cached view");
        Require(road.geometryHash==original.geometryHash&&road.paintHash!=original.paintHash&&road.detailHash!=original.detailHash,"wheel paint/clear must not invalidate terrain geometry");
        float centre=s.wheelTracks.separation*.5f,half=s.wheelTracks.width*.5f;
        Require(road.WheelPaintWeight(0,30)==0&&road.WheelPaintWeight(2,30)==0,"wheel mask retains centre and road shoulders");
        Require(Math.Abs(road.WheelPaintWeight(centre,30)-s.wheelTracks.strength)<1e-6f&&road.WheelPaintWeight(-centre,30)==road.WheelPaintWeight(centre,30),"two equally strong strips");
        float fade=road.WheelPaintWeight(centre+half*(1-s.wheelTracks.softness*.5f),30);
        Require(fade>0&&fade<s.wheelTracks.strength,"wheel edges fade rather than hard clip");
        Require(road.WheelPaintWeight(centre,-half*2)==0&&road.WheelPaintWeight(centre,-half*.5f)>0,"wheel caps are finite and rounded");
        int probes=0;
        for(float z=-3;z<=63;z+=.23f)for(float x=-6;x<=6;x+=.17f)
        {
            float mask=road.WheelPaintWeight(x,z);
            Require(float.IsFinite(mask)&&mask>=0&&mask<=1,"finite normalized wheel mask");
            Require(view.PaintWeight(x,z)==mask,"shared projection ownership must use wheel coverage");
            Require(road.ApplyHeight(x,z,0)==original.ApplyHeight(x,z,0)&&road.PaintWeight(x,z)==original.PaintWeight(x,z),"wheel layer never changes the road height/base coverage");
            Require(road.ClearWeight(x,z,LTDetailCategory.Vegetation)==original.ClearWeight(x,z,LTDetailCategory.Vegetation),"wheel clearing does not affect vegetation removal");
            Require(Math.Abs(road.ClearWeight(x,z,LTDetailCategory.Stones)*s.wheelTracks.strength-mask)<1e-6f,"stones use the same soft footprint, independent of paint strength");
            if(mask>0)Require(road.bounds.Contains(new Vector2(x,z))&&road.Intersects(new Rect(x,z,.0001f,.0001f)),"wheel influence stays in terrain/detail dirty bounds");
            var weights=new float[12];weights[0]=1;
            LTPaintMath.Composite(weights,1,road.PaintWeight(x,z));
            LTPaintMath.Composite(weights,2,mask);
            Require(Math.Abs(weights.Sum()-1)<1e-6f&&Math.Abs(weights[2]-mask)<1e-6f,"two wheel strips composite into one normalized layer slot");
            probes++;
        }
        var edit=s;edit.wheelTracks.strength=.2f;var faded=Build(edit);
        Require(faded.geometryHash==road.geometryHash&&faded.detailHash==road.detailHash&&faded.paintHash!=road.paintHash,"strength is paint-only");
        edit.wheelLayerId=99;var otherLayer=Build(edit);
        Require(faded.detailHash==otherLayer.detailHash&&faded.geometryHash==otherLayer.geometryHash&&faded.paintHash!=otherLayer.paintHash,"layer assignment is paint-only");
        edit.wheelTracks.strength=0;edit.wheelLayerId=0;var invisible=Build(edit);
        Require(invisible.WheelPaintWeight(centre,30)==0&&invisible.ClearWeight(centre,30,LTDetailCategory.Stones)==1&&invisible.detailHash==road.detailHash,"unassigned/transparent paint can still clear stones");
        edit=s;edit.wheelTracks.width=.8f;var wider=Build(edit);
        Require(wider.geometryHash==road.geometryHash&&wider.paintHash!=road.paintHash&&wider.detailHash!=road.detailHash,"wheel width dirties paint/stone masks, not geometry");
        Require(LTRoadProjectionMath.SameInputs(view,wider.WheelLayerProjection(),false)&&!LTRoadProjectionMath.SameInputs(view,wider.WheelLayerProjection(),true),"single-road UV reuses width edits; shared ownership refreshes");
        edit=s;edit.wheelTracks.clearStones=false;var noClear=Build(edit);
        Require(noClear.ClearWeight(centre,30,LTDetailCategory.Stones)==0&&noClear.detailHash==original.detailHash&&noClear.paintHash==road.paintHash,"stone toggle independent of paint");
        edit=s;edit.clearStones=true;var clearAll=Build(edit);
        Require(clearAll.ClearWeight(0,30,LTDetailCategory.Stones)==1&&clearAll.ClearWeight(centre,30,LTDetailCategory.Stones)==1,"global road clearing still clears the centre strip");
        edit=s;edit.wheelTracks.enabled=false;var disabled=Build(edit);
        Require(disabled.geometryHash==original.geometryHash&&disabled.paintHash==original.paintHash&&disabled.detailHash==original.detailHash&&disabled.WheelPaintWeight(centre,30)==0,"disabled tracks exactly preserve legacy signatures/output");
        edit=s;edit.mode=LTRoadMode.Asphalt;var asphalt=Build(edit);edit.wheelTracks.enabled=false;var asphaltOff=Build(edit);
        Require(asphalt.geometryHash==asphaltOff.geometryHash&&asphalt.paintHash==asphaltOff.paintHash&&asphalt.detailHash==asphaltOff.detailHash&&asphalt.WheelPaintWeight(centre,30)==0,"asphalt ignores wheel paint");
        edit=s;edit.straightStart=edit.straightEnd=true;edit.junctionStartLength=edit.junctionEndLength=4;var connected=Build(edit);
        Require(connected.WheelPaintWeight(centre,0)==0&&connected.WheelPaintWeight(centre,60)==0&&connected.WheelPaintWeight(centre,2)>0&&connected.WheelPaintWeight(centre,2)<connected.WheelPaintWeight(centre,4),"tracks fade at explicit junction ports");
        edit=s;edit.variation.enabled=true;var varied=Build(edit);edit.variation.patchStrength=.9f;edit.variation.patchSize=3;var patches=Build(edit);
        Require(varied.detailHash==patches.detailHash&&varied.wheelPaintHash==patches.wheelPaintHash,"underlying road patches do not rebake wheel ownership/stone clearing");
        for(float z=1;z<59;z+=.37f)Require(varied.WheelPaintWeight(centre,z)==patches.WheelPaintWeight(centre,z),"underlying patch mask leaves wheel paint unchanged");
        // Curvature/banking, eroded edges and narrowing remain inside conservative bounds.
        points[1]=new LTRoadPoint(new Vector3(12,4,30),12);edit=s;edit.variation.enabled=true;edit.variation.widthAmount=.35f;edit.edgeNoise=.8f;
        var curve=Build(edit);
        foreach(var sample in curve.samples)
        {
            var pos=sample.position+sample.right*centre;float mask=curve.WheelPaintWeight(pos.x,pos.z);
            Require(float.IsFinite(mask)&&mask>=0&&mask<=1&&curve.bounds.Contains(new Vector2(pos.x,pos.z)),"curved/banked wheel strip finite and bounded");
            curve.TryTextureCoordinates(pos.x,pos.z,out var uv,out var right);curve.WheelLayerProjection().TryTextureCoordinates(pos.x,pos.z,out var wheelUV,out var wheelRight);
            Require(uv==wheelUV&&right==wheelRight,"wheel material retains the continuous road UV frame");
        }
        // Parallel roads with one shared wheel material: a full-road footprint must
        // not steal the other road's UV where only the latter has visible wheel paint.
        var parallelPoints=new[]{new LTRoadPoint(new Vector3(.9f,2,0)),new LTRoadPoint(new Vector3(.9f,2,60))};
        var parallel=LTRoadMath.Build(parallelPoints,Matrix4x4.identity,s);
        var rect=new Rect(-4,0,8,60);const int size=81;
        var network=LTRoadProjectionMath.Bake(new[]{view,parallel.WheelLayerProjection()},rect,size);
        Require(LTRoadProjectionMath.Owner(network.coordinates[40*size+49])==1,"first wheel strip must own x=.9 despite second road centre being closer");
        Require(LTRoadProjectionMath.Owner(network.coordinates[40*size+40])==2,"second wheel strip must own x=0 despite first road centre being closer");
        float Hot(){float sum=0;for(int i=0;i<3000;i++){float x=i%81*.2f-4,z=i%293*.19f;sum+=curve.WheelPaintWeight(x,z)+curve.ClearWeight(x,z,LTDetailCategory.Stones);}return sum;}
        Hot();long before=GC.GetAllocatedBytesForCurrentThread();float total=Hot();long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Require(allocated==0&&float.IsFinite(total),"hot wheel mask/clearing queries allocate nothing");
        foreach(int invalidCase in Enumerable.Range(0,4))
        {
            edit=s;if(invalidCase==0)edit.wheelTracks.width=edit.wheelTracks.separation;
            if(invalidCase==1)edit.wheelTracks.separation=edit.width;if(invalidCase==2)edit.wheelTracks.softness=float.NaN;
            if(invalidCase==3)edit.wheelTracks.strength=2;
            bool rejected=false;try{Build(edit);}catch(ArgumentException){rejected=true;}Require(rejected,"invalid wheel authoring must fail safely");
        }
        const string root="Assets/TerrainSystem/LocalTerrain/";
        string runtime=File.ReadAllText(root+"LTPaintRuntime.cs"),stamp=File.ReadAllText(root+"LTPaintStamp.cs"),editor=File.ReadAllText(root+"Editor/LTRoadEditor.cs");
        Require(runtime.Contains("roads[i].PaintWeights(point.x,point.z,out float ground,out float wheel)")&&runtime.Contains("LTPaintMath.Composite(weights,wheelSlots[i],wheel)")&&runtime.Contains("wheelSlots[i]=layers.IndexOf(stamps[i].SecondaryLayer)"),"production painter composites secondary road layer from shared query");
        Require(stamp.Contains("includeDisabled&&road?road.wheelLayer:SecondaryLayer")&&runtime.Contains("stamp.AppendLayers(palette,true)")&&runtime.Contains("active[i].AppendLayers(layers)"),"full palette preserves disabled wheel assignment; live palette filters it");
        Require(File.ReadAllText(root+"LTLayerArrayBakeAsset.cs").Contains("stamp.AppendLayers(result,true)"),"saved arrays include assigned wheel layers");
        Require(File.ReadAllText(root+"LTPaintRocks.cs").Contains("!active[i].Road&&Touches(rect,bounds[i])"),"road overlays remain excluded from rock-stamp painting");
        Require(File.ReadAllText(root+"LTPaintRoadProjection.cs").Contains("snapshot.WheelLayerProjection()"),"shared road UV ownership uses the wheel mask view");
        Require(File.ReadAllText(root+"LTGlobalBakeAsset.cs").Contains("Layer(stamp.SecondaryLayer)")&&File.ReadAllText(root+"LTGlobalBakeAsset.cs").Contains("ToJson(road.wheelTracks)"),"saved global maps include wheel settings and material");
        Require(editor.Contains("SaveFilePanelInProject")&&editor.Contains("GenerateUniqueAssetPath(path)")&&editor.Contains("Instantiate(Road.groundLayer)")&&editor.Contains("copy.displacement=false;copy.deformation=false;copy.details=new List<LTDetailEntry>()")&&editor.Contains("SaveAssetIfDirty(copy)"),"explicit wheel-copy action must preserve original material, avoid overwrites and duplicated spawn rules");
        Console.WriteLine($"PASS wheel tracks: {probes} bounded samples; normalized two-strip overlay; exact legacy/asphalt and geometry parity; selective stone clearing; scoped invalidation; owner-aware shared UVs; {allocated} B hot query allocation. Managed math/source checks, not native scene rendering.");
    }
}
