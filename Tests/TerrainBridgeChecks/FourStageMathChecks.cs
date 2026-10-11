using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

// Pure production math, also compiled unchanged against Unity's Mono assemblies.
static class FourStageMathChecks
{
    const int N=LTBalancedForest.N,Budget=300000;
    static void Need(bool ok,string message){if(!ok)throw new Exception(message);}
    static List<Vector3Int> Grid(int side)
    {
        var result=new List<Vector3Int>();int w=N/side;
        for(int z=0;z<side;z++)for(int x=0;x<side;x++)result.Add(new Vector3Int(x*w,z*w,w));return result;
    }
    static void Split(List<Vector3Int> plan,int index)
    {
        var p=plan[index];plan.RemoveAt(index);int h=p.z/2;
        plan.AddRange(new[]{new Vector3Int(p.x,p.y,h),new Vector3Int(p.x+h,p.y,h),new Vector3Int(p.x,p.y+h,h),new Vector3Int(p.x+h,p.y+h,h)});
    }
    public static void Run(){Topology();Spatial();}
    static void Topology()
    {
        const int columns=6,rows=4;int checks=0;
        var random=new System.Random(180402);
        var raw=Enumerable.Range(0,columns*rows).ToDictionary(id=>id,id=>Grid(4));
        var inputs=raw.ToDictionary(p=>p.Key,p=>new LTBalancedForest.PlanSnapshot(p.Value));
        var cache=new LTBalancedForest.InternalPlanCache();var world=new LTBalancedForest.TopologyCache();
        LTBalancedForest accepted=null;
        LTBalancedForest Build(bool commit=true,int budget=Budget)
        {
            var prepared=new Dictionary<int,List<Vector3Int>>();
            foreach(int _ in cache.PrepareOwnedSteps(inputs,budget,prepared,31)){}
            var proposal=world.Begin(columns,rows,budget,prepared);
            foreach(int _ in proposal.BalanceSteps(17)){}
            proposal.Validate();
            var reference=new BalancedForestReference(columns,rows,budget,raw);reference.Balance();reference.Validate();
            foreach(int id in raw.Keys)
            {
                var actual=proposal.Plan(id);var expected=reference.Plan(id);
                Need(actual.SequenceEqual(expected),"incremental forest differs from full legacy closure at chunk "+id);
                Need(proposal.BoundaryStitches(id,actual).SequenceEqual(reference.BoundaryStitches(id,expected)),"incremental boundary mask mismatch");
                foreach(var p in actual)for(int side=0;side<4;side++)
                    Need(proposal.Midpoint(id,p,side)==reference.Midpoint(id,p,side),"direct balanced stitch lookup differs from frozen root search");
                checks++;
            }
            if(commit){world.Commit(proposal);accepted=proposal;}return proposal;
        }
        void Change(int id,List<Vector3Int> cells){raw[id]=cells;inputs[id]=new LTBalancedForest.PlanSnapshot(cells);}
        Build();Build();Need(cache.IdentityHits==24&&cache.ComparedPlans==0&&accepted.ReusedChunks==24&&accepted.ProcessedCells==0,"warm immutable topology must not scan/rebuild any cells");
        // Interior-only refinement: neighbours' dictionaries stay shared.
        var interior=Grid(4);Split(interior,5);Change(7,interior);Build();
        Need(accepted.ChangedChunks==1&&accepted.ReusedChunks==23,"one interior edit clones only its chunk");
        // Altering the original mutable source cannot alter an owned snapshot.
        var copy=inputs[7];interior.Reverse();Build();Need(cache.IdentityHits==24,"mutable list order cannot invalidate or mutate its immutable snapshot");
        for(int iteration=0;iteration<100;iteration++)
        {
            int id=random.Next(raw.Count);var next=Grid(1<<random.Next(0,5));
            for(int s=0;s<14;s++)Split(next,random.Next(next.Count));
            Change(id,next);
            var before=accepted;var saved=raw.Keys.ToDictionary(k=>k,k=>before.Plan(k));
            bool commit=iteration%7!=0;Build(commit);
            foreach(int k in raw.Keys)Need(saved[k].SequenceEqual(before.Plan(k)),"proposal mutated accepted chunk dictionaries");
            if(!commit)
            {
                // Different source after a discarded proposal; acceptance must
                // still be based on the last committed forest, not the abandoned one.
                Change(id,Grid(1));Build();
            }
            if(iteration%9==0){Change(id,Grid(1));Build();}
        }
        // Long refinement chain and then complete coarsening: no pinned old detail.
        foreach(int id in raw.Keys.ToArray())Change(id,Grid(1));Change(0,Grid(64));Build();
        Change(0,Grid(1));Build();Need(raw.Keys.All(id=>accepted.Plan(id).Count==1),"neighbour refinement failed to coarsen back to raw plans");
        // Cancel while global border propagation is still incomplete.
        Change(0,Grid(64));var stagedInput=new Dictionary<int,List<Vector3Int>>();
        foreach(int _ in cache.PrepareOwnedSteps(inputs,Budget,stagedInput)){}
        var cancelled=world.Begin(columns,rows,Budget,stagedInput);
        using(var iterator=cancelled.BalanceSteps(1).GetEnumerator())Need(iterator.MoveNext(),"fixture must yield during propagation");
        bool rejected=false;try{world.Commit(cancelled);}catch(InvalidOperationException){rejected=true;}
        Need(rejected,"unfinished topology cannot be accepted");Change(0,Grid(1));Build();
        Need(raw.Keys.All(id=>accepted.Plan(id).Count==1),"cancelled proposal must not retain stale refinement");
        Change(0,Grid(4));Build();rejected=false;
        try{Build(budget:1);}catch(InvalidOperationException){rejected=true;}Need(rejected,"reused topology must respect a lower budget");Build();
        // Layout changes and explicit resets must reconstruct safely.
        var preparedFinal=new Dictionary<int,List<Vector3Int>>();foreach(int _ in cache.PrepareOwnedSteps(inputs,Budget,preparedFinal)){}
        var resized=world.Begin(4,6,Budget,preparedFinal);foreach(int _ in resized.BalanceSteps()){}resized.Validate();
        var resizedReference=new BalancedForestReference(4,6,Budget,raw);resizedReference.Balance();
        foreach(int id in raw.Keys)Need(resized.Plan(id).SequenceEqual(resizedReference.Plan(id)),"layout resize invalidation");
        var stale=world.Begin(columns,rows,Budget,preparedFinal);foreach(int _ in stale.BalanceSteps()){}
        var replacement=world.Begin(columns,rows,Budget,preparedFinal);foreach(int _ in replacement.BalanceSteps()){}world.Commit(replacement);
        rejected=false;try{world.Commit(stale);}catch(InvalidOperationException){rejected=true;}Need(rejected,"old proposal cannot overwrite newer accepted topology");
        var invalidated=world.Begin(columns,rows,Budget,preparedFinal);foreach(int _ in invalidated.BalanceSteps()){}world.Clear();
        rejected=false;try{world.Commit(invalidated);}catch(InvalidOperationException){rejected=true;}Need(rejected,"clear invalidates in-flight proposals");
        world.Clear();Build();Need(accepted.ReusedChunks==0,"explicit clear rebuilds topology");
        Console.WriteLine("PASS four-stage topology: "+checks+" frozen-reference chunk comparisons; direct stitch masks, immutable identity hits, interior-only COW, cross-border refine/coarsen chains, rejected proposals, mid-balance cancel, budgets and layout reset.");

        // One-cell edit, many unchanged chunks. Include source-cache checks,
        // construction, balancing and plan/border inspection in both alternatives.
        var dense=Enumerable.Range(0,64).ToDictionary(id=>id,id=>Grid(16));
        var owned=dense.ToDictionary(p=>p.Key,p=>new LTBalancedForest.PlanSnapshot(p.Value));
        var warm=new LTBalancedForest.InternalPlanCache();var topology=new LTBalancedForest.TopologyCache();
        double Run(bool incremental)
        {
            var timer=Stopwatch.StartNew();var p=new Dictionary<int,List<Vector3Int>>();
            if(incremental)foreach(int _ in warm.PrepareOwnedSteps(owned,Budget,p)){}
            else foreach(int _ in warm.PrepareSteps(dense,Budget,p)){}
            var f=incremental?topology.Begin(8,8,Budget,p):new LTBalancedForest(8,8,Budget,p,new HashSet<int>(p.Keys));
            foreach(int _ in f.BalanceSteps()){}
            foreach(int id in p.Keys)if(!incremental||f.MayChangeOutput(id)){var plan=f.Plan(id);f.BoundaryStitches(id,plan);}
            if(incremental)topology.Commit(f);timer.Stop();return timer.Elapsed.TotalMilliseconds;
        }
        Run(true);var oldTimes=new List<double>();var newTimes=new List<double>();
        // Separate old cache so its legacy (unversioned) calls do not clear the
        // identity tokens measured by the production versioned path.
        for(int iteration=0;iteration<10;iteration++)
        {
            var next=Grid(16);if(iteration%2==0)Split(next,17);dense[27]=next;owned[27]=new LTBalancedForest.PlanSnapshot(next);
            // Legacy run deliberately uses a separate internal cache below.
            var start=Stopwatch.StartNew();var p=legacyBenchmarkCache.Prepare(dense,Budget,new LTLODMesh.BuildProgress(false));
            var full=new LTBalancedForest(8,8,Budget,p,new HashSet<int>(p.Keys));full.Balance(new LTLODMesh.BuildProgress(false));
            foreach(int id in p.Keys){var plan=full.Plan(id);full.BoundaryStitches(id,plan);}start.Stop();oldTimes.Add(start.Elapsed.TotalMilliseconds);
            newTimes.Add(Run(true));
        }
        oldTimes.Sort();newTimes.Sort();Console.WriteLine("Topology synthetic 64 chunks / one interior edit: full setup median "+oldTimes[5].ToString("F2")+" ms; incremental "+newTimes[5].ToString("F2")+" ms. Managed math only, not live-scene timings.");
    }
    static readonly LTBalancedForest.InternalPlanCache legacyBenchmarkCache=new LTBalancedForest.InternalPlanCache();
    static IEnumerable<int> Bits(Vector3[] values)=>values.SelectMany(p=>new[]{BitConverter.SingleToInt32Bits(p.x),BitConverter.SingleToInt32Bits(p.y),BitConverter.SingleToInt32Bits(p.z)});
    static IEnumerable<int> Bits(Vector2[] values)=>values.SelectMany(p=>new[]{BitConverter.SingleToInt32Bits(p.x),BitConverter.SingleToInt32Bits(p.y)});
    static void SameLayout(LTSpatialLODMath.Output a,LTSpatialLODMath.Output b)
    {
        Need(a.cells.Length==b.cells.Length&&a.baseCellOrder.SequenceEqual(b.baseCellOrder)&&a.renderable.SequenceEqual(b.renderable),"sliced layout IDs/order/renderability");
        for(int i=0;i<a.cells.Length;i++)
        {
            var x=a.cells[i];var y=b.cells[i];
            Need(x.x==y.x&&x.z==y.z&&x.size==y.size&&x.patch==y.patch&&x.possibleMask==y.possibleMask&&x.requiredMask==y.requiredMask&&Enumerable.Range(0,4).All(k=>x.Child(k)==y.Child(k)),"sliced layout cell parity");
        }
        for(int p=0;p<a.patches.Length;p++)
        {
            Need(a.patches[p].root==b.patches[p].root,"sliced patch root");
            for(int l=0;l<a.patches[p].levels.Length;l++)Need(a.patches[p].levels[l].cells.SequenceEqual(b.patches[p].levels[l].cells),"sliced patch levels/order");
        }
    }
    static void Spatial()
    {
        int cases=0,slices=0;var rect=new Rect(-13.375f,23.125f,31.75f,47.875f);var size=new Vector2(512,512);
        foreach(int divisions in new[]{1,2,4})foreach(bool coarse in new[]{false,true})foreach(int cut in new[]{0,1,2,3})foreach(bool regular in new[]{false,true})
        {
            var fine=Grid(8);Split(fine,13);Split(fine,fine.Count-1);
            var forest=new LTBalancedForest(1,1,Budget,new Dictionary<int,List<Vector3Int>>{{0,fine}});forest.Balance(new LTLODMesh.BuildProgress(false));fine=forest.Plan(0);
            var plans=coarse?new[]{fine,Grid(4)}:new[]{fine};
            int Mask(Vector3Int cell){int m=0;for(int s=0;s<4;s++)if(forest.Midpoint(0,cell,s))m|=1<<s;return m;}
            var beforeMask=new List<Vector3Int>();var afterMask=new List<Vector3Int>();
            var reference=SpatialLayoutReference.BuildLayout(plans,divisions,Budget,p=>{beforeMask.Add(p);return Mask(p);},p=>p.x==0);
            LTSpatialLODMath.Output sliced=null;int previous=0;
            foreach(int count in LTSpatialLODMath.BuildLayoutSteps(plans,divisions,Budget,p=>{afterMask.Add(p);return Mask(p);},r=>sliced=r,p=>p.x==0,7))
            {Need(sliced==null&&count-previous==7,"layout slice bounded and unpublished");previous=count;slices++;}
            Need(beforeMask.SequenceEqual(afterMask),"sliced layout callback order");SameLayout(reference,sliced);
            var oldCalls=new List<int>();var newCalls=new List<int>();
            float Height(float x,float z,List<int> calls){calls.Add(BitConverter.SingleToInt32Bits(x));calls.Add(BitConverter.SingleToInt32Bits(z));return .013f*x+(float)Math.Sin(z*.17)*.21f;}
            bool Cut(float x,float z)=>cut==1||cut==2&&x>2.113f&&z<57.911f||cut==3&&(x-2)*(x-2)+(z-44)*(z-44)<27;
            LTStampMeshReference.EmitSpatialVariants(reference,rect,size,Budget,(x,z)=>Height(x,z,oldCalls),cut==0?null:(Func<float,float,bool>)Cut,r=>regular,new LTLODMesh.BuildProgress(false));
            previous=0;
            foreach(int count in LTStampMesh.EmitSpatialVariantsSteps(sliced,rect,size,Budget,(x,z)=>Height(x,z,newCalls),cut==0?null:(Func<float,float,bool>)Cut,r=>regular,new LTLODMesh.BuildProgress(false),11))
            {Need(count-previous==11&&sliced.vertices==null,"spatial emission slice bounded; vertex arrays not published early");previous=count;slices++;}
            Need(oldCalls.SequenceEqual(newCalls),"sliced spatial height callback bitwise order");
            Need(Bits(reference.vertices).SequenceEqual(Bits(sliced.vertices))&&Bits(reference.normals).SequenceEqual(Bits(sliced.normals))&&Bits(reference.uv).SequenceEqual(Bits(sliced.uv))&&reference.baseIndices.SequenceEqual(sliced.baseIndices),"sliced spatial exact mesh streams");
            for(int c=0;c<reference.cells.Length;c++)
            {
                var a=reference.cells[c].variants;var b=sliced.cells[c].variants;Need((a==null)==(b==null),"unused cell variants");
                if(a!=null)for(int i=0;i<a.Length;i++)Need(a[i].indices.SequenceEqual(b[i].indices),"sliced exact variant indices");
            }
            for(int p=0;p<reference.patches.Length;p++)Need(reference.patches[p].bounds==sliced.patches[p].bounds,"sliced exact patch bounds");
            cases++;
        }
        var testPlans=new[]{Grid(16)};LTSpatialLODMath.Output aborted=null;
        using(var e=LTSpatialLODMath.BuildLayoutSteps(testPlans,1,Budget,p=>15,o=>aborted=o,workPerStep:1).GetEnumerator())Need(e.MoveNext(),"layout cancellation fixture");
        Need(aborted==null,"cancelled layout not published");
        var layout=LTSpatialLODMath.BuildLayout(testPlans,1,Budget,p=>15);int calls=0;
        using(var e=LTStampMesh.EmitSpatialVariantsSteps(layout,rect,size,Budget,(x,z)=>{calls++;return 0;},null,workPerStep:1).GetEnumerator())Need(e.MoveNext(),"emitter cancellation fixture");
        Need(layout.vertices==null&&layout.normals==null&&calls>0,"cancelled emitter has no completed arrays");
        bool rejected=false;try{LTSpatialLODMath.BuildLayout(testPlans,1,1,p=>0);}catch(InvalidOperationException){rejected=true;}Need(rejected,"layout budget guard");
        Console.WriteLine("PASS sliced spatial preparation: "+cases+" frozen layout/emitter comparisons, "+slices+" bounded slices; exact callbacks/IDs/UV/normals/indices/bounds, coarse variants, clipping, cancellation and budgets.");
    }
}
