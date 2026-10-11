using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Explicit, numeric-only native test; never opens/saves a scene or an asset.
    [InitializeOnLoad]
    static class LTRoadRasterCheck
    {
        struct Buffer<T>:ILTHeightBuffer<T>{public T[] values;public T this[int index]=>values[index];}
        static string Describe(LTRoadMath.Hit h)=>$"position=({h.position.x:R},{h.position.y:R},{h.position.z:R}), right=({h.right.x:R},{h.right.z:R}), lateral={h.lateral:R}, radial={h.radialDistance:R}, arc={h.distance:R}, bank={h.bank:R}, variation={h.variationStrength:R}";
        static IEnumerator<int> runner;
        static LTRoadRasterCheck()
        {AssemblyReloadEvents.beforeAssemblyReload+=Stop;EditorApplication.quitting+=Stop;}
        // Archived validation menu: Tools/Local Terrain/Validate Road Paint Jobs (no scene changes)
        static void Run()
        {
            if(runner!=null){Debug.Log("Road paint job validation is already running.");return;}
            runner=Check().GetEnumerator();EditorApplication.update+=Tick;
        }
        static void Stop(){EditorApplication.update-=Tick;runner?.Dispose();runner=null;}
        static void Tick(){try{if(!runner.MoveNext())Stop();}catch(Exception e){Stop();Debug.LogException(e);}}
        static IEnumerable<int> Check()
        {
            int pairs=0,maps=0,burstJobs=0,fallbacks=0;
            for(int fixture=0;fixture<12;fixture++)
            {
                var settings=LTRoadMath.Settings.Default;settings.projection=LTRoadProjection.Spline;
                settings.mode=fixture%4==0?LTRoadMode.Asphalt:LTRoadMode.Offroad;
                settings.pattern=fixture%2==0?LTRoadPattern.Tracks:LTRoadPattern.Solid;
                settings.variation.enabled=fixture%3!=0;settings.wheelTracks.enabled=true;settings.wheelLayerId=21;
                if(fixture%3==1){settings.projection=LTRoadProjection.World;settings.wheelTracks.projection=LTRoadWheelProjection.Spline;}
                settings.wheelTracks.independentTiling=true;settings.wheelTracks.tileSizeMetres=new Vector2(.67f,2.14f);
                settings.straightStart=fixture%2==0;settings.straightEnd=fixture%3==0;
                settings.junctionStartLength=3;settings.junctionEndLength=4;
                float shift=fixture>=8?100000:fixture>=4?-512:0;
                var points=Enumerable.Range(0,9).Select(i=>new LTRoadPoint(new Vector3(shift+8+(float)Math.Sin(i*.7)*12,i*.41f,shift+i*8),i%3*5)).ToArray();
                var road=LTRoadMath.Build(points,Matrix4x4.identity,settings);
                var other=LTRoadMath.Build(points.Reverse().Select(p=>new LTRoadPoint(p.position+new Vector3(2,0,-1),p.bank)).ToArray(),Matrix4x4.identity,settings);
                var sources=new[]{road,other};var rect=new Rect(shift-16,shift-8,64,80);const int size=65;
                var input=new LTRoadRasterInput(sources,rect,size);
                while(!LTRoadRasterWork.CanSchedule)yield return 0;
                using(var work=new LTRoadRasterWork(input))
                {
                    yield return 0;while(!work.IsCompleted)yield return 0;
                    if(!work.Matches(rect,sources)||work.Matches(rect,new[]{other,road}))throw new InvalidOperationException("Road raster revision matching failed.");
                    var raster=work.Result(out bool burst);if(burst)burstJobs++;fallbacks+=raster.ReferenceFallbacks;
                    for(int r=0;r<sources.Length;r++)
                    {
                        var source=sources[r];
                        for(int i=0;i<size*size;i++)
                        {
                            float x=rect.xMin+rect.width*(i%size)/(size-1),z=rect.yMin+rect.height*(i/size)/(size-1);
                            source.PaintWeights(x,z,out float g,out float w);float ag=0,aw=0;
                            int selected=raster.SegmentIndex(r,i);
                            if(raster.TrySample(r,i,out var hit))
                            {
                                source.TrySample(x,z,out var reference);
                                if(!hit.Equals(reference))
                                {
                                    int scalar=LTRoadRasterKernel.Segment(input.roads[r],new Buffer<LTRoadMath.Sample>{values=input.samples},new Buffer<LTRoadHeightKernel.Node>{values=input.nodes},x,z);
                                    source.TrySampleSegment(x,z,scalar,out var scalarHit);
                                    source.TrySampleSegment(x,z,raster.SegmentIndex(r,i),out var repeated);
                                    throw new InvalidOperationException($"Road Paint Jobs hit mismatch v2: fixture {fixture}, road {r}, pixel {i}, x={x:R}, z={z:R}, Burst={burst}, native segment={selected}, resolved={raster.SegmentIndex(r,i)}, scalar={scalar}. Reference: {Describe(reference)}; raster: {Describe(hit)}; scalar hit: {Describe(scalarHit)}; repeated native segment: {Describe(repeated)}. No scene changes.");
                                }
                                source.PaintWeights(hit,x,z,out ag,out aw);
                            }
                            if(!g.Equals(ag)||!w.Equals(aw))throw new InvalidOperationException($"Road Paint Jobs weight mismatch: fixture {fixture}, road {r}, pixel {i}. No scene changes.");
                            pairs++;
                        }
                        yield return 0;
                    }
                    foreach(var group in new[]{new[]{road},new[]{road.WheelLayerProjection()},sources,new[]{road.WheelLayerProjection(),other.WheelLayerProjection()}})
                    {
                        if(group[0].projection!=LTRoadProjection.Spline)continue;
                        var expected=LTRoadProjectionMath.Bake(group,rect,size);var actual=LTRoadProjectionMath.Bake(group,rect,size,raster);
                        if(!expected.coordinates.SequenceEqual(actual.coordinates)||(expected.derivatives!=null&&!expected.derivatives.SequenceEqual(actual.derivatives)))
                            throw new InvalidOperationException($"Road Paint Jobs UV/owner/Jacobian mismatch: fixture {fixture}. No scene changes.");
                        maps++;yield return 0;
                    }
                }
                while(!LTRoadRasterWork.CanSchedule)yield return 0;
                var retired=new LTRoadRasterWork(input);retired.Dispose();bool rejected=false;
                try{retired.Result(out _);}catch(ObjectDisposedException){rejected=true;}
                if(!rejected)throw new InvalidOperationException("Retired road paint result remained readable.");
                while(!retired.IsCompleted)yield return 0;LTRoadRasterWork.CollectRetired();
            }
            string report=$"Road Paint Jobs PASS: {pairs} exact weight pairs + exact hits, {maps} exact UV/owner/Jacobian maps; CPU tie guards {fallbacks}; Burst {burstJobs}/12. Retired result rejected. No scene/assets/settings changed. Not a performance benchmark.";
            if(burstJobs<12)Debug.LogWarning(report+" Run again after Burst compilation to verify all fixtures.");else Debug.Log(report);
        }
    }
}
