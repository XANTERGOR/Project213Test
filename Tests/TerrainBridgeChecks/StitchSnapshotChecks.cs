using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static LTBalancedForest.StitchSnapshot CaptureStitches(LTBalancedForest forest,int chunk,List<Vector3Int> plan,int batch=512)
    {
        LTBalancedForest.StitchSnapshot result=null;int previous=0;
        foreach(int done in forest.StitchSnapshotSteps(chunk,plan,value=>result=value,batch))
        {Require(result==null&&done-previous<=batch&&done>previous,"partial stitch snapshot remains private and bounded");previous=done;}
        Require(result!=null&&result.Count==plan.Count,"snapshot publishes once complete");return result;
    }
    static void StitchSnapshotChecks()
    {
        int comparisons=0;var rect=new Rect(-13.375f,23.125f,31.75f,47.875f);
        IEnumerable<int> Bits3(Vector3[] values)=>values.SelectMany(v=>new[]{BitConverter.SingleToInt32Bits(v.x),BitConverter.SingleToInt32Bits(v.y),BitConverter.SingleToInt32Bits(v.z)});
        IEnumerable<int> Bits2(IEnumerable<Vector2> values)=>values.SelectMany(v=>new[]{BitConverter.SingleToInt32Bits(v.x),BitConverter.SingleToInt32Bits(v.y)});
        void Same(EmittedStamp expected,EmittedStamp actual)
        {
            Require(Bits3(expected.positions).SequenceEqual(Bits3(actual.positions))&&Bits3(expected.normals).SequenceEqual(Bits3(actual.normals)),"snapshot exact vertex/normal bits");
            Require(Bits2(expected.uv).SequenceEqual(Bits2(actual.uv))&&expected.indices.SequenceEqual(actual.indices),"snapshot exact UV/indices");
            Require(expected.heightCalls.SequenceEqual(actual.heightCalls)&&expected.cutCalls.SequenceEqual(actual.cutCalls),"snapshot preserves height/cut callback coordinates and order");comparisons++;
        }
        void Reject(Action action,string message)
        {bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Require(rejected,message);}
        var cuts=new Func<float,float,bool>[]{null,(x,z)=>false,(x,z)=>true,(x,z)=>x>2.113f&&z<51.789f,
            (x,z)=>(x-2.3f)*(x-2.3f)+(z-44.1f)*(z-44.1f)<30};
        var diagonals=new Func<Rect,bool>[]{null,r=>true,r=>r.center.x+r.center.y*.27f>12};
        void Check(LTBalancedForest forest,int chunk,List<Vector3Int> plan)
        {
            var snapshot=CaptureStitches(forest,chunk,plan,7);snapshot.Validate(forest,chunk,plan);
            for(int i=0;i<plan.Count;i++)for(int side=0;side<4;side++)
                Require(((snapshot.Mask(i)&(1<<side))!=0)==forest.Midpoint(chunk,plan[i],side),"snapshot flags exactly match legacy neighbour search");
            Require(Bits2(LTHeightJobMath.EmitPoints(rect,snapshot)).SequenceEqual(Bits2(LTHeightJobMath.EmitPoints(rect,plan,(p,s)=>forest.Midpoint(chunk,p,s)))),"snapshot prefetch coordinates and order exact");
            foreach(var cut in cuts)foreach(var diagonal in diagonals)
                Same(EmitStamp(true,rect,plan,forest,chunk,cut,diagonal),EmitStamp(false,rect,plan,forest,chunk,cut,diagonal,stitches:snapshot));
            var original=plan[0];plan[0]=new Vector3Int(0,0,2);
            Require(snapshot.Cell(0)==original,"snapshot owns its plan copy");
            Reject(()=>EmitStamp(false,rect,plan,forest,chunk,null,null,stitches:snapshot),"mutated plan rejected before mesh creation");
            plan[0]=original;
            Reject(()=>EmitStamp(false,rect,plan,forest,chunk+1,null,null,stitches:snapshot),"wrong chunk rejected");
        }
        // Real balanced forests producing each of the 16 masks on the centre
        // chunk. Covers all four cross-chunk sides, not just artificial callbacks.
        for(int mask=0;mask<16;mask++)
        {
            var raw=new Dictionary<int,List<Vector3Int>>();for(int id=0;id<9;id++)raw[id]=LODFixture(1);
            int[] neighbours={3,5,1,7};for(int side=0;side<4;side++)if((mask&(1<<side))!=0)raw[neighbours[side]]=LODFixture(2);
            var forest=new LTBalancedForest(3,3,300000,raw);forest.Balance(new LTLODMesh.BuildProgress(false));
            var plan=forest.Plan(4);Require(CaptureStitches(forest,4,plan).Mask(0)==mask,"fixture covers requested mask");Check(forest,4,plan);
        }
        foreach(bool irregular in new[]{false,true})
        {
            var forest=new LTBalancedForest(2,1,300000,new Dictionary<int,List<Vector3Int>>{{0,LODFixture(16,irregular)},{1,LODFixture(8)}});
            forest.Balance(new LTLODMesh.BuildProgress(false));forest.Validate();
            for(int chunk=0;chunk<2;chunk++)Check(forest,chunk,forest.Plan(chunk));
        }
        var dense=LODFixture(128);var denseForest=new LTBalancedForest(1,1,300000,new Dictionary<int,List<Vector3Int>>{{0,dense}});
        Reject(()=>CaptureStitches(denseForest,0,dense),"unfinished balancing rejected");denseForest.Balance(new LTLODMesh.BuildProgress(false));
        LTBalancedForest.StitchSnapshot cancelled=null;
        using(var iterator=denseForest.StitchSnapshotSteps(0,dense,s=>cancelled=s,4).GetEnumerator())
        {Require(iterator.MoveNext()&&iterator.Current==4,"capture can yield before completion");}
        Require(cancelled==null,"discarded capture never publishes partial data");
        var valid=CaptureStitches(denseForest,0,dense);
        var other=new LTBalancedForest(1,1,300000,new Dictionary<int,List<Vector3Int>>{{0,dense}});other.Balance(new LTLODMesh.BuildProgress(false));
        Reject(()=>valid.Validate(other,0,dense),"same coordinates in a different build do not accept stale snapshot");
        var reordered=new List<Vector3Int>(dense);reordered.Reverse();Reject(()=>valid.Validate(denseForest,0,reordered),"wrong plan order rejected");
        var empty=CaptureStitches(denseForest,0,new List<Vector3Int>());Require(empty.Count==0&&!LTHeightJobMath.EmitPoints(rect,empty).Any(),"empty snapshot");
        bool ambiguous=false;try{EmitStamp(false,rect,dense,denseForest,0,null,null,(p,s)=>false,stitches:valid);}catch(ArgumentException){ambiguous=true;}
        Require(ambiguous,"snapshot and override callback cannot silently disagree");
        bool budget=false;try{EmitStamp(false,rect,dense,denseForest,0,null,null,budget:1,stitches:valid);}catch(InvalidOperationException){budget=true;}
        Require(budget,"snapshot retains emitter budget guard");
        double seconds=0;bool stopped=false;
        try{EmitStamp(false,rect,dense,denseForest,0,null,null,progress:new LTLODMesh.BuildProgress(()=>seconds+=2,()=>true),stitches:valid);}catch(OperationCanceledException){stopped=true;}
        Require(stopped,"snapshot retains manual cancellation");
        // Snapshot creation and its consumption do not change the forest or plans.
        Same(EmitStamp(true,rect,dense,denseForest,0,null,null),EmitStamp(false,rect,dense,denseForest,0,null,null,stitches:valid));
        var editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        int creation=editor.IndexOf("forest.StitchSnapshotSteps(id,balanced",StringComparison.Ordinal);
        Require(creation>editor.IndexOf("terrainTimings.Lap(\"Terrain: balancing\")",StringComparison.Ordinal)&&editor.IndexOf("terrainTimings.Lap(\"Terrain: stitch snapshot preparation (CPU)\")",creation,StringComparison.Ordinal)>creation,"snapshot prepared after balance with separate non-overlapping CPU timing");
        Require(editor.Contains("LTHeightJobMath.EmitPoints(r,stitches)")&&editor.Contains("diagnostics:emitDiagnostics,stitches:stitches"),"same snapshot wired into both LOD0 consumers");
        Console.WriteLine($"PASS stitch snapshot: {comparisons} exact meshes/callback sequences, all 16 real masks, irregular/internal/world edges; prefetch order, immutable ownership, cancel/discard, stale build/plan/chunk, budgets and empty inputs.");
        // Include capture AND coordinate iteration AND emission; do not hide the
        // relocated search cost by benchmarking only the now-cheaper emitter.
        EmittedStamp Run(bool useSnapshot)
        {
            var snapshot=useSnapshot?CaptureStitches(denseForest,0,dense):null;
            int visited=0;foreach(var point in useSnapshot?LTHeightJobMath.EmitPoints(rect,snapshot):LTHeightJobMath.EmitPoints(rect,dense,(p,s)=>denseForest.Midpoint(0,p,s)))visited++;
            Require(visited==65536,"benchmark does equal prefetch work");
            return EmitStamp(false,rect,dense,denseForest,0,(x,z)=>false,null,trace:false,stitches:snapshot);
        }
        for(int i=0;i<3;i++){Run(false);Run(true);}
        var before=new List<double>();var after=new List<double>();
        for(int i=0;i<10;i++)for(int j=0;j<2;j++)
        {bool use=((i+j)&1)==0;var timer=Stopwatch.StartNew();Run(use);timer.Stop();(use?after:before).Add(timer.Elapsed.TotalMilliseconds);}
        before.Sort();after.Sort();
        Console.WriteLine($"Stitch snapshot synthetic 128x128 total capture + prefetch iteration + emission: old {before[5]:F2} ms / shared {after[5]:F2} ms. No Unity scene or Jobs time; no timing threshold assertion.");
    }
}
