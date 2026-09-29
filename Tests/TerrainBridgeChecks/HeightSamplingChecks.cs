using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void HeightSamplingChecks()
    {
        static bool Bits(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
        static void Stale(Action action)
        {
            bool threw=false;try{action();}catch(InvalidOperationException){threw=true;}
            Require(threw,"superseded height binding must not return or publish stale data");
        }
        // Frozen LTSource.Sample expression: compare exact bits including outside
        // bounds, non-power-of-two resolutions and non-square physical extents.
        var random=new System.Random(847136);int sourceSamples=0;
        foreach(int res in new[]{2,17,129})
        {
            var values=Enumerable.Range(0,res*res).Select(_=>(float)(random.NextDouble()*240-80)).ToArray();
            var size=new Vector3(293.25f,100,187.7f);
            var snapshot=new LTHeightSampling.Source(values,res,size);
            float Reference(float x,float z)
            {
                float fx=Mathf.Clamp01(x/size.x)*(res-1),fz=Mathf.Clamp01(z/size.z)*(res-1);
                int ix=Mathf.Min(Mathf.FloorToInt(fx),res-2),iz=Mathf.Min(Mathf.FloorToInt(fz),res-2);
                float tx=fx-ix,tz=fz-iz;
                return Mathf.Lerp(Mathf.Lerp(values[iz*res+ix],values[iz*res+ix+1],tx),
                    Mathf.Lerp(values[(iz+1)*res+ix],values[(iz+1)*res+ix+1],tx),tz);
            }
            for(int i=0;i<2000;i++)
            {
                float x=(float)(random.NextDouble()*1.4-.2)*size.x,z=(float)(random.NextDouble()*1.4-.2)*size.z;
                Require(Bits(snapshot.Sample(x,z),Reference(x,z)),"source interpolation exact parity");sourceSamples++;
            }
            foreach(float x in new[]{0,size.x,-1,size.x+1})foreach(float z in new[]{0,size.z,-1,size.z+1})
            {Require(Bits(snapshot.Sample(x,z),Reference(x,z)),"source endpoint parity");sourceSamples++;}
            float old=snapshot.Sample(0,0);values[0]+=20;size.x*=2;
            Require(Bits(snapshot.Sample(0,0),old)&&snapshot.size.x==293.25f,"snapshot owns its height array and layout");
        }
        Stale(()=>new LTHeightSampling.Source(new float[3],2,Vector3.one));
        Stale(()=>new LTHeightSampling.Source(new float[4],2,new Vector3(float.NaN,1,1)));

        var cache=new LTHeightSampling.Cache(100,4,50);int calls=0;
        float H(float x,float z){calls++;return x+z;}
        var a=cache.Bind(1,H);var b=cache.Bind(2,H);
        Require(a.Sample(1,2)==3&&a.Sample(1,2)==3&&b.Sample(1,2)==3&&calls==2,"per-chunk reuse, no cross-chunk aliases");
        var another=cache.Bind(1,(x,z)=>throw new Exception("cache should be shared between stages"));
        Require(another.Sample(1,2)==3,"new stage reuses accepted chunk samples");
        float next=BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(1f)+1);
        a.Sample(next,2);a.Sample(0,0);a.Sample(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),0);
        Require(calls==5,"adjacent floats and signed zero are never quantised together");
        cache.Invalidate(1);Stale(()=>a.Sample(1,2));Stale(()=>another.Sample(1,2));
        int before=calls;Require(b.Sample(1,2)==3&&calls==before,"unrelated chunk cache survives an edit");
        Require(cache.Bind(1,(x,z)=>30).Sample(1,2)==30,"edited chunk recomputes rather than inheriting old values");
        cache.Clear();Stale(()=>b.Sample(1,2));Require(cache.Count==0&&cache.ChunkCount==0,"global clear releases entries");
        var epoch=cache.Bind(2,H);cache.Clear();Stale(()=>epoch.Sample(1,2));

        cache=new LTHeightSampling.Cache(6,2,4);
        a=cache.Bind(1,H);b=cache.Bind(2,H);var c=cache.Bind(3,H);
        a.Sample(0,0);b.Sample(0,0);a.Sample(0,0);c.Sample(0,0);
        before=calls;a.Sample(0,0);Require(calls==before,"recently used chunk remains cached");
        b.Sample(0,0);Require(calls==before+1&&cache.ChunkCount==2&&cache.Evictions==2,"LRU chunk limit evicts, not invalidates bindings");
        cache.Clear();a=cache.Bind(1,H);b=cache.Bind(2,H);
        for(int i=0;i<4;i++)a.Sample(i,0);
        for(int i=0;i<3;i++)b.Sample(i,0);
        Require(cache.Count==3&&cache.ChunkCount==1,"global sample cap evicts an old chunk as a unit");
        b.Sample(3,0);b.Sample(4,0);b.Sample(4,0);
        Require(cache.Count==4&&cache.Bypassed==2,"per-chunk cap does not grow or retain overflow values");
        cache.ResetStatistics();Require(cache.Count==4&&cache.Hits==0&&cache.Evaluations==0&&cache.Bypassed==0&&cache.Evictions==0,"counter reset preserves samples");

        cache=new LTHeightSampling.Cache(50,3,20);
        for(int id=0;id<256;id++)
        {
            var binding=cache.Bind(id,H);
            for(int p=0;p<25;p++)Require(binding.Sample(p,id)==p+id,"eviction must not change values");
            Require(cache.Count<=50&&cache.ChunkCount<=3,"full-world rebuild stays bounded");
        }
        cache.Clear();var invalid=cache.Bind(0,(x,z)=>float.NaN);
        Require(float.IsNaN(invalid.Sample(0,0))&&float.IsNaN(invalid.Sample(0,0))&&cache.Count==0,"nonfinite height is not cached or silently fixed");
        var badPoint=cache.Bind(0,(x,z)=>1);badPoint.Sample(float.PositiveInfinity,0);Require(cache.Count==0,"nonfinite coordinates not cached");
        int attempts=0;var retry=cache.Bind(0,(x,z)=>++attempts==1?throw new Exception("expected evaluator error"):2);
        try{retry.Sample(0,0);}catch(Exception e){Require(e.Message=="expected evaluator error","original evaluator exception retained");}
        Require(retry.Sample(0,0)==2&&attempts==2,"exceptions do not poison cache");
        var reentrant=cache.Bind(1,(x,z)=>{cache.Invalidate(1);return 8;});Stale(()=>reentrant.Sample(0,0));
        cache=new LTHeightSampling.Cache(5,1,5);a=cache.Bind(1,H);a.Sample(0,0);
        b=cache.Bind(2,H);
        var evict=cache.Bind(1,(x,z)=>{b.Sample(0,0);return 7;});
        Require(evict.Sample(1,1)==7&&cache.Count==1&&cache.ChunkCount==1,"reentrant eviction cannot leave an orphan entry");
        Require(cache.Bind(1,(x,z)=>throw new Exception("orphan entry")).Sample(1,1)==7,"reentrant result lives in resident cache");
        Console.WriteLine($"PASS height snapshots/cache: {sourceSamples} exact source samples, immutable inputs, exact keys, local/global invalidation, bounded LRU, overflow, errors and reentrant guards.");
        HeightPipelineChecks();HeightSamplingContracts();
    }

    static void HeightPipelineChecks()
    {
        const int budget=150000;
        var settings=LTRoadMath.Settings.Default;
        settings.mode=LTRoadMode.Offroad;settings.width=4;settings.terrainCellSize=.5f;
        settings.pattern=LTRoadPattern.Tracks;settings.rutDepth=.12f;settings.variation.enabled=true;
        var road=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(1,2,1)),new LTRoadPoint(new Vector3(12,3,15),7),
            new LTRoadPoint(new Vector3(29,1,30),-3)},Matrix4x4.identity,settings);
        var rect=new Rect(0,0,32,32);var size=new Vector2(32,32);
        var zones=new List<LTStampMesh.Zone>{new LTStampMesh.Zone{bounds=road.bounds,cellSize=.5f,customCellSize=(x,z)=>.5f,coverageIntersects=road.Intersects}};
        float Raw(float x,float z)=>road.ApplyHeight(x,z,.014f*x+.009f*z+.04f*(float)Math.Sin(z*.3));
        bool Cut(float x,float z)=>x>14.13f&&x<16.65f&&z>20.28f&&z<23.5f;
        long evaluations=0;
        float Counted(float x,float z){evaluations++;return Raw(x,z);}
        var cache=new LTHeightSampling.Cache();
        var expectedPlans=new List<Vector3Int>[5];Vector3[] expectedV=null,expectedN=null;Vector2[] expectedUV=null;int[] expectedI=null;
        LTSpatialLODMath.Output expectedSpatial=null;long rawCalls=0;int comparisons=0;
        foreach(bool cached in new[]{false,true})
        {
            evaluations=0;
            Func<float,float,float> Stage()=>cached?cache.Bind(5,Counted).Sample:Counted;
            var fine=LTStampMesh.Plan(rect,16,true,.015f,budget,zones,Stage());
            var forest=new LTBalancedForest(1,1,budget,new Dictionary<int,List<Vector3Int>>{{0,fine}});forest.Balance();fine=forest.Plan(0);
            LTStampMesh.Emit(rect,size,budget,forest,0,fine,Stage(),Cut,out var v,out var n,out var uv,out var indices);
            var input=new LTLODJobInput();var levels=Enumerable.Range(1,4).Select(l=>new LTLODJobMath.Level{steps=l,tolerance=l*.1f}).ToArray();
            foreach(var _ in input.Prepare(fine,rect,levels,Stage(),null,null,4,false)){}
            var output=SolveJobInput(input);var plans=new[]{fine}.Concat(output).ToArray();
            // Also exercise the synchronous/manual path with the same shared cache.
            var preparation=new LTLODMesh.Preparation(fine,rect,Stage(),new LTLODMesh.BuildProgress(false));
            for(int i=0;i<levels.Length;i++)Require(output[i].SequenceEqual(preparation.Coarsen(levels[i].steps,levels[i].tolerance,null,null,4,false)),"height cache manual/job preparation parity");
            int Mask(Vector3Int p){int mask=0;for(int side=0;side<4;side++)if(forest.Midpoint(0,p,side))mask|=1<<side;return mask;}
            var spatial=LTSpatialLODMath.BuildLayout(plans,4,budget,Mask);
            LTStampMesh.EmitSpatialVariants(spatial,rect,size,budget,Stage(),Cut);
            if(!cached)
            {expectedPlans=plans;expectedV=v;expectedN=n;expectedUV=uv;expectedI=indices;expectedSpatial=spatial;rawCalls=evaluations;}
            else
            {
                for(int i=0;i<plans.Length;i++){Require(plans[i].SequenceEqual(expectedPlans[i]),"shared cache changes no plan");comparisons++;}
                Require(v.SequenceEqual(expectedV)&&n.SequenceEqual(expectedN)&&uv.SequenceEqual(expectedUV)&&indices.SequenceEqual(expectedI),"LOD0/cut mesh exact parity");
                Require(spatial.vertices.SequenceEqual(expectedSpatial.vertices)&&spatial.normals.SequenceEqual(expectedSpatial.normals)&&
                    spatial.uv.SequenceEqual(expectedSpatial.uv)&&spatial.baseIndices.SequenceEqual(expectedSpatial.baseIndices),"spatial vertex streams exact parity");
                Require(spatial.cells.Length==expectedSpatial.cells.Length,"spatial cell count exact parity");
                for(int cell=0;cell<spatial.cells.Length;cell++)
                {
                    var actual=spatial.cells[cell].variants;var expected=expectedSpatial.cells[cell].variants;
                    if(actual==null||expected==null){Require(actual==null&&expected==null,"search-only cell parity");continue;}
                    Require(actual.Length==expected.Length,"stitch variant count parity");
                    for(int variant=0;variant<actual.Length;variant++)
                        Require(actual[variant].indices.SequenceEqual(expected[variant].indices),"stitch variant indices exact parity");
                }
                Require(evaluations<rawCalls*.7&&cache.Hits>0,"repeated composed height evaluations substantially reduced");
                Console.WriteLine($"PASS height pipeline: {comparisons} exact plans, LOD0/normals/UV/cut and spatial stitch variants identical; composed evaluations {rawCalls} -> {evaluations}, {cache.Hits} hits, {cache.Count} resident. Managed fixture only.");
            }
        }
        // Warm, alternating managed sampling-only measurement of repeated stages.
        // This intentionally excludes native Unity masks, workers and mesh upload.
        var requests=Enumerable.Range(0,128*128).Select(i=>new Vector2(i%128*.25f,i/128*.25f)).ToArray();
        double Run(bool cached)
        {
            var clock=Stopwatch.StartNew();var pool=cached?new LTHeightSampling.Cache():null;
            double checksum=0;
            for(int stage=0;stage<4;stage++)
            {
                Func<float,float,float> sample=cached?pool.Bind(0,Raw).Sample:Raw;
                foreach(var p in requests)checksum+=sample(p.x,p.y);
            }
            Require(!double.IsNaN(checksum),"benchmark used results");return clock.Elapsed.TotalMilliseconds;
        }
        Run(false);Run(true);var oldTimes=new List<double>();var newTimes=new List<double>();
        for(int i=0;i<6;i++)if(i%2==0){oldTimes.Add(Run(false));newTimes.Add(Run(true));}else{newTimes.Add(Run(true));oldTimes.Add(Run(false));}
        oldTimes.Sort();newTimes.Sort();
        Console.WriteLine($"Height sampling-only managed road fixture, 16384 points x 4 stages: median uncached {(oldTimes[2]+oldTimes[3])/2:F2} ms, cached {(newTimes[2]+newTimes[3])/2:F2} ms. NOT native Unity/Burst or live-scene timing.");
    }

    static void HeightSamplingContracts()
    {
        var editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        Require(editor.Contains("state.dirty.Add(id);state.heightSamples.Invalidate(id);state.chunkLODs.Touch(id)"),"chunk edit invalidates composed heights and pending LOD together");
        Require(editor.Contains("if(all){s.heightSource=null;s.heightSamples.Clear();}"),"manual full rebuild forces a fresh source snapshot");
        Require(editor.Contains("state.heightSourceInput!=source.heights")&&editor.Contains("state.heightSourceVersion!=version")&&editor.Contains("state.heightSourceAsset!=asset"),"source array/dirty/import versions checked");
        Require(editor.Contains("source-layout:")&&editor.Contains("RuntimeHelpers.GetHashCode(w.source.heights)"),"source layout/replacement participates in scheduler config invalidation");
        int queueStart=editor.IndexOf("static void QueueChunkLODs(");int deferredStart=editor.IndexOf("static IEnumerable<LTBuildStep> DeferredChunkLODs(");
        string queue=editor.Substring(queueStart,deferredStart-queueStart);
        Require(!queue.Contains("heightSamples.")&&queue.Contains("state.chunkLODs.Touch(id)"),"LOD0 publication keeps its reusable heights even when replacing coarse topology");
        Require(editor.Contains("var sampleHeight=HeightSampler(w,state,id,local)")&&editor.Contains("input.Prepare(fine,r,settings,height,")&&
            editor.Split("new LTLODMesh.Preparation(data.balanced,r,height,lodProgress)").Length==3,"LOD0 and both manual/deferred LOD paths use chunk sampling");
        Require(editor.Contains("ApplyHeightStamps(w.source.Sample(x,z),stamps,x,z,includeMeshStamps)")&&editor.Contains("Evaluate(world,stamps,x,z,false)"),"rock-excluded contour queries remain separate from full-terrain cache");
        Require(editor.Contains("s.mask.GetPixelBilinear(u,v).r"),"native mask interpolation is unchanged");
        Require(editor.Contains("if(!local.Any(s=>s.affectHeight))return snapshot.Sample"),"unmodified terrain bypasses composed cache overhead");
        Console.WriteLine("PASS height-cache production wiring contracts (source checks, not an Editor integration execution).");
    }
}
