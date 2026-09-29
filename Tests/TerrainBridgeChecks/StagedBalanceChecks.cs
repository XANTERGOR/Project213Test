using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void StagedBalanceChecks()
    {
        const int budget=100000,n=LTBalancedForest.N;
        List<Vector3Int> Grid(int side)
        {
            var cells=new List<Vector3Int>();int width=n/side;
            for(int z=0;z<n;z+=width)for(int x=0;x<n;x+=width)cells.Add(new Vector3Int(x,z,width));
            return cells;
        }
        var dense=new Dictionary<int,List<Vector3Int>>{{0,Grid(128)}};
        // Reproduce the screenshot without any clock delay: old UI was based on
        // cell count, not elapsed time. It opens at exactly 16,384 live visits.
        UnityEditor.EditorUtility.ProgressCalls=0;
        new BalancedForestReference(1,1,budget,dense).Balance();
        Require(UnityEditor.EditorUtility.ProgressCalls==1,"old balancer opens the modal on a uniform 128x128 grid");
        UnityEditor.EditorUtility.ProgressCalls=0;
        int yields=0;var incremental=new LTBalancedForest(1,1,budget,dense);
        foreach(int step in incremental.BalanceSteps()){yields++;}
        Require(yields==16&&UnityEditor.EditorUtility.ProgressCalls==0,"same work yields sixteen batches without modal calls");

        int comparisons=0;var random=new System.Random(15423);
        for(int fixture=0;fixture<8;fixture++)
        {
            var raw=Enumerable.Range(0,4).ToDictionary(c=>c,c=>Grid(1<<random.Next(0,5)));
            foreach(var plan in raw.Values)for(int i=0;i<20;i++)
            {
                int index=random.Next(plan.Count);var p=plan[index];plan.RemoveAt(index);int h=p.z/2;
                plan.AddRange(new[]{new Vector3Int(p.x,p.y,h),new Vector3Int(p.x+h,p.y,h),new Vector3Int(p.x,p.y+h,h),new Vector3Int(p.x+h,p.y+h,h)});
            }
            var before=raw.ToDictionary(p=>p.Key,p=>p.Value.ToArray());
            var reference=new BalancedForestReference(2,2,budget,raw);reference.Balance();reference.Validate();
            foreach(int batch in new[]{1,37,1024})
            {
                var staged=new LTBalancedForest(2,2,budget,raw);int previous=0;
                foreach(int processed in staged.BalanceSteps(batch))
                {Require(processed-previous<=batch,"a balancing slice visits at most its budget");previous=processed;}
                staged.Validate();Require(staged.ProcessedCells==reference.ProcessedCells,"identical queue processing order and visited count");
                foreach(int id in raw.Keys)
                {
                    var a=reference.Plan(id);var b=staged.Plan(id);
                    Require(a.SequenceEqual(b)&&reference.BoundaryStitches(id,a).SequenceEqual(staged.BoundaryStitches(id,b)),"cooperative output exactly matches frozen legacy topology/stitches");
                    Require(raw[id].SequenceEqual(before[id]),"yielding does not mutate raw plans");comparisons++;
                }
            }
        }
        var cache=new LTBalancedForest.InternalPlanCache();var proposal=new Dictionary<int,List<Vector3Int>>();
        using(var steps=cache.PrepareSteps(dense,budget,proposal,17).GetEnumerator())
        {Require(steps.MoveNext()&&proposal.Count==0&&cache.Rebuilt==0,"unfinished local balance is not cached or published");}
        var completed=cache.Prepare(dense,budget);
        Require(cache.Rebuilt==1&&cache.Reused==0&&completed[0].Count==16384,"cancelled partial cache entry is recomputed");
        foreach(int step in cache.PrepareSteps(dense,budget,proposal)){}
        Require(cache.Rebuilt==0&&cache.Reused==1&&proposal[0].SequenceEqual(completed[0]),"completed pure cache entry is reusable across builds");
        bool invalid=false;try{foreach(int step in incremental.BalanceSteps(0)){};}catch(ArgumentOutOfRangeException){invalid=true;}
        Require(invalid,"invalid slice size is rejected");

        // One synthetic clock drives nested planning/emission and balancing. Test
        // a long duration without sleeping, and verify silence is a policy rather
        // than merely postponing the window until the next helper.
        double now=0;int shows=0;
        UnityEditor.EditorUtility.ProgressCalls=0;
        var silent=new LTLODMesh.BuildProgress(()=>now,(message,fraction)=>{shows++;return true;},false);
        now=100;
        var area=new Rect(0,0,32,32);var size=new Vector2(32,32);
        var fine=LTStampMesh.Plan(area,128,false,0,budget,new List<LTStampMesh.Zone>(),(x,z)=>x*.02f,silent);
        var forest=new LTBalancedForest(1,1,budget,new Dictionary<int,List<Vector3Int>>{{0,fine}});forest.Balance(silent);
        cache.Prepare(dense,budget,silent);
        LTStampMesh.Emit(area,size,budget,forest,0,forest.Plan(0),(x,z)=>x*.02f,null,out var vertices,out _,out _,out var triangles,null,silent);
        var layout=LTSpatialLODMath.BuildLayout(new[]{forest.Plan(0)},4,budget,cell=>0);
        LTStampMesh.EmitSpatialVariants(layout,area,size,budget,(x,z)=>x*.02f,null,null,silent);
        new LTLODMesh.Preparation(fine,area,(x,z)=>x*.02f,silent).Coarsen(2,1);
        Require(shows==0&&UnityEditor.EditorUtility.ProgressCalls==0&&SpatialTriangles(vertices,triangles).SetEquals(SpatialTriangles(layout.vertices,layout.baseIndices)),"all nested helpers obey silent mode with unchanged geometry");
        now=0;var messages=new List<string>();bool cancel=false;
        var manual=new LTLODMesh.BuildProgress(()=>now,(message,fraction)=>{messages.Add(message);return cancel;});
        now=.99;manual.Report("early",.5f);Require(messages.Count==0,"manual progress has a time delay");
        now=1;manual.Report("balance",.5f);now=1.05;manual.Report("vertices",.7f);
        Require(messages.SequenceEqual(new[]{"balance"}),"nested stages share repaint throttling");
        now=1.2;cancel=true;bool cancelled=false;
        try{new LTBalancedForest(1,1,budget,dense).Balance(manual);}catch(OperationCanceledException){cancelled=true;}
        Require(cancelled&&messages.Last().StartsWith("Balancing local transitions"),"manual balancing remains cancellable");

        const string root="Assets/TerrainSystem/LocalTerrain/Editor/";
        string engine=File.ReadAllText(root+"LTEditor.cs"),balancer=File.ReadAllText(root+"LTBalancedForest.cs"),emitter=File.ReadAllText(root+"LTStampMesh.cs");
        Require(!balancer.Contains("DisplayCancelableProgressBar")&&!emitter.Contains("DisplayCancelableProgressBar"),"nested helpers cannot bypass the common progress policy");
        Require(engine.Contains("if(staged)foreach(int step in lodForest.BalanceSteps())")&&engine.Contains("state.balanceCache.PrepareSteps(plans,budget,internalPlans)")&&engine.Contains("if(staged)foreach(int step in forest.BalanceSteps())"),"all production balance paths yield in automatic mode");
        Require(engine.Contains("terrainTimings.Pause();timer.Stop();yield return LTBuildStep.Working;timer.Start();terrainTimings.Resume();"),"balance timing excludes pauses between editor updates");
        Console.WriteLine($"PASS staged balance/progress: reproduced old 16384-cell modal; 16 silent slices, {comparisons} frozen-reference chunk comparisons, cache cancellation/reuse, all nested progress policies and manual cancellation. Managed only; not live-scene timing.");
    }
}
