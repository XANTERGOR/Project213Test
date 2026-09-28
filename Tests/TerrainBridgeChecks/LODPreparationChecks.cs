using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static List<Vector3Int> LODFixture(int side,bool irregular=false)
    {
        int step=LTBalancedForest.N/side;
        var fine=new List<Vector3Int>();
        for(int z=0;z<side;z++)for(int x=0;x<side;x++)
        {
            if(irregular&&x<side/2&&z>side/2)
            {int h=step/2;for(int j=0;j<2;j++)for(int i=0;i<2;i++)fine.Add(new Vector3Int(x*step+i*h,z*step+j*h,h));}
            else fine.Add(new Vector3Int(x*step,z*step,step));
        }
        return fine;
    }
    static void LODPreparationChecks()
    {
        int comparisons=0;
        var rect=new Rect(-13.5f,23.75f,32,48);
        float H(float x,float z)=>.001f*x*x+.04f*(float)Math.Sin(z*.3)+.2f*(float)Math.Exp(-Math.Pow(x+z*.2,2));
        foreach(bool irregular in new[]{false,true})foreach(bool boundary in new[]{false,true})foreach(int divisions in new[]{1,4,8})
        {
            var fine=LODFixture(32,irregular);var original=fine.ToArray();
            int sharedCalls=0;var preparation=new LTLODMesh.Preparation(fine,rect,(x,z)=>{sharedCalls++;return H(x,z);});
            var protection=new List<Rect>{new Rect(-5,33,2,6)};
            bool Protected(Rect area)=>area.xMin<0&&area.xMax>-.5f;
            var expected=new List<Vector3Int>[5];
            for(int level=0;level<5;level++)
            {
                float error=level*.08f;
                expected[level]=LODPreparationReference.Coarsen(fine,rect,level,error,H,protection,Protected,divisions,boundary);
                var actual=preparation.Coarsen(level,error,protection,Protected,divisions,boundary);
                Require(actual.SequenceEqual(expected[level]),"shared LOD preparation must match frozen old algorithm exactly");comparisons++;
                if(level==0)Require(sharedCalls==0,"LOD0-only request must not prepare heights");
                if(boundary)LTLODMesh.ValidateBoundary(fine,actual);
            }
            int sampled=sharedCalls;
            for(int level=4;level>=0;level--)
            {
                var actual=preparation.Coarsen(level,level*.08f,protection,Protected,divisions,boundary);
                Require(actual.SequenceEqual(expected[level]),"reverse/repeated levels must not retain another level's merges");
                actual.Clear();comparisons++;
            }
            Require(sharedCalls==sampled&&sampled==preparation.HeightSampleCount,"all level height requests share one chunk-local cache");
            var changedProtection=preparation.Coarsen(3,.24f,null,null,1,!boundary);
            Require(changedProtection.SequenceEqual(LODPreparationReference.Coarsen(fine,rect,3,.24f,H,null,null,1,!boundary)),"protection/boundary settings remain per-level, not cached merge decisions");
            Require(fine.SequenceEqual(original),"LOD0/collider input unchanged");
            fine.Clear();
            Require(preparation.Coarsen(2,.16f,protection,Protected,divisions,boundary).SequenceEqual(expected[2]),"preparation snapshots its input list");
            var movedRect=new Rect(67,-9,16,32);
            float Changed(float x,float z)=>H(x,z)+.1f*(float)Math.Sin(x);
            var afterEdit=new LTLODMesh.Preparation(original.ToList(),movedRect,Changed);
            Require(afterEdit.Coarsen(3,.1f).SequenceEqual(LODPreparationReference.Coarsen(original.ToList(),movedRect,3,.1f,Changed)),"next edit/chunk samples its own rectangle and height source");
        }
        int invalidCalls=0;
        var lazy=new LTLODMesh.Preparation(LODFixture(8),rect,(x,z)=>{invalidCalls++;return 0;});
        foreach(var invalid in new[]{(-1,1f),(13,1f),(1,-1f),(1,float.NaN),(1,float.PositiveInfinity)})
        {
            bool failed=false;try{lazy.Coarsen(invalid.Item1,invalid.Item2);}catch(InvalidOperationException){failed=true;}
            Require(failed,"invalid LOD settings must still fail");
        }
        Require(invalidCalls==0,"invalid settings must fail before sampling");
        LODProgressChecks();
        LODPreparationBenchmark();
        var engine=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        Require(engine.Split("new LTLODMesh.Preparation(data.balanced,r,").Length==3,"both production LOD paths use chunk-local preparations");
        Require(engine.Split("new LTLODMesh.BuildProgress()").Length==2&&engine.Split(",lodProgress)").Length==3,"one rebuild-wide progress budget for both paths");
        Require(engine.Contains("finally{EditorUtility.ClearProgressBar();}")&&engine.Contains("var preparedLODPlans=new Dictionary<int,List<Vector3Int>[]>();"),"cancellation clears UI and caches are local to the rebuild");
        Console.WriteLine($"PASS LOD preparation: {comparisons} exact old/new level comparisons, rectangular chunks, borders/protection, immutable input, edit isolation, delayed/throttled progress and cancellation. Managed checks only.");
    }
    static void LODProgressChecks()
    {
        double now=0;int shows=0;bool cancel=false;
        var progress=new LTLODMesh.BuildProgress(()=>now,()=>{shows++;return cancel;});
        void Poll(){for(int i=0;i<1024;i++)progress.Poll();}
        now=.99;Poll();Require(shows==0,"no progress popup below one second");
        now=1;Poll();Require(shows==1,"long operation shows progress");
        now=1.05;Poll();Require(shows==1,"progress repaint throttled");
        now=1.11;Poll();Require(shows==2,"progress updates after interval");
        cancel=true;now=1.22;bool aborted=false;
        try{Poll();}catch(OperationCanceledException){aborted=true;}
        Require(aborted,"long work remains cancellable");

        now=0;shows=0;cancel=true;
        progress=new LTLODMesh.BuildProgress(()=>now,()=>{shows++;return cancel;});
        var fine=LODFixture(32);var original=fine.ToArray();var rect=new Rect(0,0,32,32);
        var prepared=new LTLODMesh.Preparation(fine,rect,(x,z)=>x*x*.001f,progress);
        now=2;aborted=false;
        try{prepared.Coarsen(2,.1f);}catch(OperationCanceledException){aborted=true;}
        Require(aborted&&fine.SequenceEqual(original),"cancelled preparation does not mutate LOD0");
        cancel=false;now=3;
        Require(prepared.Coarsen(2,.1f).SequenceEqual(LODPreparationReference.Coarsen(fine,rect,2,.1f,(x,z)=>x*x*.001f)),"partial preparation after cancellation is safe to retry");

        now=0;shows=0;
        progress=new LTLODMesh.BuildProgress(()=>now,()=>{shows++;return false;});
        for(int chunk=0;chunk<8;chunk++)
        {
            now+=.25;
            new LTLODMesh.Preparation(LODFixture(16),rect,(x,z)=>0,progress).Coarsen(1,1);
        }
        Require(shows>0,"many small chunks must share delay, not postpone UI indefinitely");
    }
    static void LODPreparationBenchmark()
    {
        var fine=LODFixture(128);var rect=new Rect(0,0,256,256);
        long oldCalls=0,newCalls=0;int checksum=0;
        (double ms,long bytes) Run(bool shared)
        {
            long calls=0;float H(float x,float z){calls++;return .0002f*x*x+.01f*(float)Math.Sin(z*.1);}
            var timer=Stopwatch.StartNew();long start=GC.GetAllocatedBytesForCurrentThread();
            var prepared=shared?new LTLODMesh.Preparation(fine,rect,H):null;
            for(int level=1;level<=4;level++)
            {
                var output=shared?prepared.Coarsen(level,level*.5f,null,null,4,false):
                    LODPreparationReference.Coarsen(fine,rect,level,level*.5f,H,null,null,4,false);
                checksum+=output.Count;
            }
            long bytes=GC.GetAllocatedBytesForCurrentThread()-start;timer.Stop();
            if(shared)newCalls=calls;else oldCalls=calls;
            return(timer.Elapsed.TotalMilliseconds,bytes);
        }
        Run(false);Run(true);
        var oldTimes=new List<double>();var newTimes=new List<double>();long oldBytes=0,newBytes=0;
        for(int run=0;run<5;run++)for(int order=0;order<2;order++)
        {
            bool shared=((run+order)&1)!=0;var sample=Run(shared);
            (shared?newTimes:oldTimes).Add(sample.ms);
            if(shared)newBytes=sample.bytes;else oldBytes=sample.bytes;
        }
        oldTimes.Sort();newTimes.Sort();
        Require(newCalls*3<=oldCalls,"four levels should reuse at least two thirds of height evaluations");
        Require(newBytes<oldBytes&&checksum>0,"shared preparation reduces managed allocation");
        Console.WriteLine($"LOD preparation synthetic 16384 cells / 4 levels: height calls {oldCalls} -> {newCalls}; allocated {oldBytes} -> {newBytes} B; median {oldTimes[2]:F2} -> {newTimes[2]:F2} ms. No Unity scene/GPU measurement.");
    }
}
