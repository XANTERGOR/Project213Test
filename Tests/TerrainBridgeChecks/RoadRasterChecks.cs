using System;
using System.Linq;
using System.IO;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadRasterChecks()
    {
        int hits=0,weights=0,maps=0,fallbacks=0;
        for(int fixture=0;fixture<24;fixture++)
        {
            var settings=LTRoadMath.Settings.Default;
            settings.projection=fixture%4==0?LTRoadProjection.World:LTRoadProjection.Spline;
            settings.mode=fixture%5==0?LTRoadMode.Asphalt:LTRoadMode.Offroad;
            settings.variation.enabled=fixture%2==0;settings.pattern=fixture%3==0?LTRoadPattern.Tracks:LTRoadPattern.Solid;
            settings.wheelTracks.enabled=true;settings.wheelLayerId=31;
            settings.wheelTracks.projection=fixture%3==0?LTRoadWheelProjection.World:fixture%3==1?LTRoadWheelProjection.Spline:LTRoadWheelProjection.Inherit;
            settings.wheelTracks.independentTiling=true;settings.wheelTracks.tileSizeMetres=new Vector2(.87f,3.14f);
            settings.straightStart=fixture%2==0;settings.straightEnd=fixture%3==0;
            settings.junctionStartLength=3;settings.junctionEndLength=4;
            settings.textureAcrossMetres=3.17f;settings.textureOffset=new Vector2(.32f,.17f);
            float shift=fixture>=16?100000:fixture>=8?-512:0;
            var points=Enumerable.Range(0,9).Select(i=>new LTRoadPoint(new Vector3(shift+8+(float)Math.Sin(i*.7)*12,i*.41f,shift+i*8),i%3*5)).ToArray();
            var a=LTRoadMath.Build(points,Matrix4x4.identity,settings);
            var b=LTRoadMath.Build(points.Reverse().Select(p=>new LTRoadPoint(p.position+new Vector3(2,0,-1),p.bank)).ToArray(),Matrix4x4.identity,settings);
            var sources=new[]{a,b};var rect=new Rect(shift-16,shift-8,64,80);
            const int size=65;
            var input=new LTRoadRasterInput(sources,rect,size);var segments=new int[input.needed.Length];
            var sb=new LTHeightJobMath.Buffer<LTRoadMath.Sample>{values=input.samples};
            var nb=new LTHeightJobMath.Buffer<LTRoadHeightKernel.Node>{values=input.nodes};
            for(int i=0;i<segments.Length;i++)
            {
                int pixel=i%(size*size),r=i/(size*size);
                float x=rect.xMin+rect.width*(pixel%size)/(size-1),z=rect.yMin+rect.height*(pixel/size)/(size-1);
                segments[i]=input.needed[i]==0?-1:LTRoadRasterKernel.Segment(input.roads[r],sb,nb,x,z);
                if(segments[i]==-2)fallbacks++;
            }
            var raster=new LTRoadRasterSet(sources,rect,size,segments);
            for(int r=0;r<sources.Length;r++)for(int i=0;i<size*size;i++)
            {
                var road=sources[r];float x=rect.xMin+rect.width*(i%size)/(size-1),z=rect.yMin+rect.height*(i/size)/(size-1);
                bool got=raster.TrySample(r,i,out var hit);
                if(got)
                {
                    road.TrySample(x,z,out var expected);
                    Require(hit.Equals(expected),$"exact raster hit fixture {fixture}, pixel {i}, segment {segments[r*size*size+i]}");hits++;
                }
                road.PaintWeights(x,z,out float ground,out float wheels);
                float actualGround=0,actualWheels=0;
                if(got)road.PaintWeights(hit,x,z,out actualGround,out actualWheels);
                Require(ground.Equals(actualGround)&&wheels.Equals(actualWheels),"exact ground/wheel paint from raster, including sparse zero pixels");weights++;
            }
            foreach(var group in new[]{new[]{a},new[]{a.WheelLayerProjection()},sources,new[]{a.WheelLayerProjection(),b.WheelLayerProjection()}})
            {
                if(group[0].projection!=LTRoadProjection.Spline)continue;
                var reference=LTRoadProjectionMath.Bake(group,rect,size);var actual=LTRoadProjectionMath.Bake(group,rect,size,raster);
                Require(reference.coordinates.SequenceEqual(actual.coordinates),"exact road UV / shared-layer ownership from raster");maps++;
                Require(reference.derivatives==null?actual.derivatives==null:reference.derivatives.SequenceEqual(actual.derivatives),"exact ownership seam Jacobian");
                Require(actual.queries<reference.queries,"raster must replace actual UV searches");
            }
            Require(raster.RoadIndex(a.WheelLayerProjection())==0,"wheel tiling shares geometric snapshot");
            var changed=LTRoadMath.Build(points,Matrix4x4.identity,settings);
            Require(raster.RoadIndex(changed)==-1,"new road version cannot consume an old sample set");
            Require(!LTRoadRasterInput.Fits(Enumerable.Repeat(a,17).ToArray(),257),"bounded road count");
        }
        const string root="Assets/TerrainSystem/LocalTerrain/";
        var work=File.ReadAllText(root+"LTRoadRasterWork.cs");var paint=File.ReadAllText(root+"LTPaintRuntime.cs");
        Require(work.Contains("if(!IsCompleted)throw")&&work.Contains("retired||released")&&work.Contains("live.Count<Capacity"),"job capacity / premature and retired read guards");
        Require(paint.Contains("!state.roadSampling.Matches(rect,weightRoads)")&&paint.Contains("roadResultsApplied>=1")&&paint.Contains("PendingRoadSampling=true;continue"),"version matching / nonblocking / apply budget wiring");
        Require(File.ReadAllText(root+"Editor/LTEditor.cs").Contains("!w.PaintingPending&&state.dirty.Count==0&&state.colliders.Count>0"),"colliders wait for pending paint coverage");
        Console.WriteLine($"PASS road raster: {hits} exact hits, {weights} exact weight pairs, {maps} exact UV/owner maps; {fallbacks} guarded queries. Managed kernel only; native Jobs require editor validation.");
    }
}
