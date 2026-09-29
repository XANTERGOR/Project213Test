using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadPaintCacheChecks()
    {
        int pairs=0,pixels=0,reused=0;
        var random=new System.Random(82917);
        var settings=LTRoadMath.Settings.Default;
        settings.projection=LTRoadProjection.Spline;
        settings.wheelTracks.enabled=true;settings.wheelLayerId=14;
        var points=Enumerable.Range(0,12).Select(i=>new LTRoadPoint(new Vector3(16+(float)Math.Sin(i*.65)*12,i*.1f,i*24),i%4)).ToArray();
        LTRoadMath.Snapshot Build(LTRoadPoint[] p,LTRoadMath.Settings s)=>LTRoadMath.Build(p,Matrix4x4.identity,s);
        void SamePixel(LTRoadMath.Snapshot a,LTRoadMath.Snapshot b,float x,float z)
        {
            Require(a.PaintWeight(x,z).Equals(b.PaintWeight(x,z))&&a.WheelPaintWeight(x,z).Equals(b.WheelPaintWeight(x,z)),
                $"reused region must retain exact mask values at {x:R},{z:R}");pixels++;
        }
        void CheckRegion(LTRoadMath.Snapshot a,LTRoadMath.Snapshot b,Rect rect)
        {
            bool same=a.SamePaintRegion(b,rect);pairs++;
            Require(same==b.SamePaintRegion(a,rect),"regional comparison is symmetric for Undo");
            if(!same)return;reused++;
            for(int z=0;z<=32;z++)for(int x=0;x<=32;x++)
                SamePixel(a,b,rect.xMin+rect.width*x/32,rect.yMin+rect.height*z/32);
        }
        // Baseline, local movement, distant resampling, length changes and Undo.
        foreach(bool variation in new[]{false,true})
        foreach(bool tracks in new[]{false,true})
        foreach(bool endpoints in new[]{false,true})
        {
            var s=settings;s.variation.enabled=variation;s.pattern=tracks?LTRoadPattern.Tracks:LTRoadPattern.Solid;
            s.straightStart=s.straightEnd=endpoints;s.junctionStartLength=4;s.junctionEndLength=5;
            var original=Build(points,s);
            for(int edit=0;edit<8;edit++)
            {
                var changed=(LTRoadPoint[])points.Clone();int at=edit%2==0?2:9;
                changed[at].position+=new Vector3(edit%3==0?3:0,edit%3==1?2:0,edit%3==2?3:0);
                if(edit==6){changed[at].overrideVariation=true;changed[at].variationStrength=.1f;}
                if(edit==7)changed=points.Where((_,i)=>i!=9).ToArray();
                var next=Build(changed,s);
                for(int z=0;z<9;z++)for(int x=0;x<3;x++)CheckRegion(original,next,new Rect(x*16-8,z*32,16,32));
            }
        }
        Require(reused>50,"distant changes should actually reuse regions, not just always return false");
        var variedSettings=settings;variedSettings.variation.enabled=true;
        var tailMoved=(LTRoadPoint[])points.Clone();tailMoved[9].position.x+=3;
        var prefix=new Rect(8,32,24,24);
        var variedRoad=Build(points,variedSettings);var variedTail=Build(tailMoved,variedSettings);
        Require(variedRoad.SamePaintRegion(variedTail,prefix)&&variedRoad.PaintWeight(26,44)>0,
            "moving the far tail must reuse a genuinely painted prefix, including varied roads with changed total length");
        CheckRegion(variedRoad,variedTail,prefix);
        var headMoved=(LTRoadPoint[])points.Clone();headMoved[1].position.y+=2;
        var plain=Build(points,settings);var plainHead=Build(headMoved,settings);var downstream=new Rect(0,208,32,24);
        Require(plain.SamePaintRegion(plainHead,downstream)&&plain.PaintWeight(points[9].position.x,216)>0,
            "upstream length-only change can retain genuinely painted downstream weights");
        plain.TryTextureCoordinates(points[9].position.x,216,out var oldUV,out _);
        plainHead.TryTextureCoordinates(points[9].position.x,216,out var newUV,out _);
        Require(oldUV.y!=newUV.y&&!LTRoadProjectionMath.SameInputs(plain,plainHead,false),
            "downstream V shift must still invalidate UV even when the weights match");
        CheckRegion(plain,plainHead,downstream);
        // Identical copied data and independent UV settings preserve weights, but
        // the separate projection dependency MUST still change.
        var road=Build(points,settings);
        var tile=settings;tile.textureRepeatMetres=7;tile.textureAcrossMetres=2;tile.textureOffset=new Vector2(.1f,.7f);
        tile.wheelTracks.independentTiling=true;tile.wheelTracks.tileSizeMetres=new Vector2(.75f,3);tile.wheelTracks.textureOffset=new Vector2(2,.4f);
        var tiled=Build(points,tile);var whole=new Rect(0,0,48,280);
        Require(road.SamePaintRegion(tiled,whole),"independent road/wheel tiling is not a weight dependency");
        Require(!LTRoadProjectionMath.SameInputs(road,tiled,false)&&!LTRoadProjectionMath.SameInputs(road.WheelLayerProjection(),tiled.WheelLayerProjection(),false),
            "both UV maps retain independent invalidation");
        CheckRegion(road,tiled,whole);
        Require(!road.SamePaintRegion(null,whole)&&!road.SamePaintRegion(road.WheelLayerProjection(),whole),"missing/replaced view cannot reuse weights");
        var moved=points.Select(p=>new LTRoadPoint(p.position+new Vector3(70,0,0),p.bank)).ToArray();
        Require(!road.SamePaintRegion(Build(moved,settings),whole),"moving a road out must clear its old footprint");
        var narrow=settings;narrow.wheelTracks.width*=.5f;
        Require(!road.SamePaintRegion(Build(points,narrow),whole),"wheel width changes invalidate");
        narrow=settings;narrow.wheelLayerId=99;
        Require(!road.SamePaintRegion(Build(points,narrow),whole),"wheel material assignment changes invalidate");

        // Combined evaluation matches the two legacy entry points exactly, including
        // wheel views, asphalt, caps, edge noise, disabled/unassigned/zero-strength
        // wheel paint and normalized layer compositing at crossings.
        for(int fixture=0;fixture<24;fixture++)
        {
            var s=settings;s.variation.enabled=fixture%2==0;s.pattern=fixture%3==0?LTRoadPattern.Tracks:LTRoadPattern.Solid;
            s.mode=fixture%7==0?LTRoadMode.Asphalt:LTRoadMode.Offroad;s.edgeNoise=fixture%4*.2f;
            s.wheelTracks.enabled=fixture%5!=0;s.wheelTracks.strength=fixture%6==0?0:.83f;s.wheelLayerId=fixture%11==0?0:14;
            s.straightStart=s.straightEnd=fixture%3==0;s.junctionStartLength=s.junctionEndLength=4;
            var a=Build(points,s);
            foreach(var source in new[]{a,a.WheelLayerProjection()})for(int i=0;i<6000;i++)
            {
                float x=(float)random.NextDouble()*52-10,z=(float)random.NextDouble()*290-12;
                if(i<source.samples.Count){var p=source.samples[i];x=p.position.x+p.right.x*s.wheelTracks.separation*.5f;z=p.position.z+p.right.z*s.wheelTracks.separation*.5f;}
                source.PaintWeights(x,z,out float ground,out float wheels);
                Require(ground.Equals(source.PaintWeight(x,z))&&wheels.Equals(source.WheelPaintWeight(x,z)),"combined mask query must equal individual reference calls exactly");
                var old=new float[12];old[0]=1;var next=new float[12];next[0]=1;
                foreach(int slot in new[]{1,2,1})
                {
                    LTPaintMath.Composite(old,slot,source.PaintWeight(x,z));LTPaintMath.Composite(old,3,source.WheelPaintWeight(x,z));
                    LTPaintMath.Composite(next,slot,ground);LTPaintMath.Composite(next,3,wheels);
                }
                Require(old.SequenceEqual(next),"same ordered road/wheel composites including shared slots");pixels++;
            }
        }
        // Invalid coordinates retain safe zeros and hot pair queries allocate nothing.
        road.PaintWeights(float.NaN,1,out float ng,out float nw);Require(ng==0&&nw==0,"nonfinite point");
        float Hot(){float sum=0;for(int i=0;i<10000;i++){road.PaintWeights(16,i%260,out float a,out float b);sum+=a+b;}return sum;}
        Hot();long before=GC.GetAllocatedBytesForCurrentThread();float total=Hot();long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Require(allocated==0&&float.IsFinite(total),"combined query allocates nothing");
        const string root="Assets/TerrainSystem/LocalTerrain/";
        var runtime=File.ReadAllText(root+"LTPaintRuntime.cs");var capture=File.ReadAllText(root+"LTPaintCpuCapture.cs");
        Require(runtime.Contains("SamePaintRoads(state.weightRoads,weightRoads,rect)")&&runtime.Contains("state.weightHash!=weightInput"),"production uses regional road and nonroad dependencies");
        Require(runtime.Contains("weightInput=Mix(weightInput,Id(layer))")&&runtime.Contains("Id(stamp.SecondaryLayer)"),"palette/slot changes participate in invalidation");
        Require(runtime.IndexOf("BakeRoadProjection(world,rect,stamps,layers,state,asphaltInputs)")<runtime.IndexOf("if(!bakeWeights)"),"UV/suppression update even when weights are reused");
        Require(runtime.Contains("coverage=Mix(coverage,road.geometryHash)")&&runtime.Contains("values.Add(road.Capture(world).paintHash)"),"density, UV and material consumers retain full road dependencies");
        Require(runtime.Contains("state.weightHash=weightInput;state.weightRoads=weightRoads.ToArray()")&&runtime.Contains("catch(InvalidOperationException error)"),"successful updates capture snapshots; failure removes owned state");
        foreach(string stage in new[]{"RoadProjection","RoadUVCompute","RoadUVUpload","WeightCompute","WeightUpload"})Require(capture.Contains(stage),"split stage "+stage);
        Console.WriteLine($"PASS road paint reuse: {pairs} region pairs, {reused} exact reused regions, {pixels} pixel/composite comparisons; independent tiling, movement/deletion, Undo symmetry, caps, varied profiles, {allocated} B hot pair allocation. Managed/source checks, not native paint timing.");
    }
}
