using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using LocalTerrainPrototype;
partial class Checks
{
    static void CachedBalanceChecks()
    {
        const int n=LTBalancedForest.N,budget=100000;
        List<Vector3Int> Grid(int side)
        {
            int w=n/side;var cells=new List<Vector3Int>();
            for(int z=0;z<n;z+=w)for(int x=0;x<n;x+=w)cells.Add(new Vector3Int(x,z,w));
            return cells;
        }
        void Split(List<Vector3Int> cells,int index)
        {
            var p=cells[index];cells.RemoveAt(index);int h=p.z/2;
            cells.AddRange(new[]{new Vector3Int(p.x,p.y,h),new Vector3Int(p.x+h,p.y,h),
                new Vector3Int(p.x,p.y+h,h),new Vector3Int(p.x+h,p.y+h,h)});
        }
        var raw=Enumerable.Range(0,9).ToDictionary(i=>i,i=>Grid(4));
        var cache=new LTBalancedForest.InternalPlanCache();var random=new System.Random(180917);
        LTBalancedForest Check()
        {
            var before=raw.ToDictionary(p=>p.Key,p=>p.Value.ToArray());
            var full=new LTBalancedForest(3,3,budget,raw);full.Balance();
            var prepared=cache.Prepare(raw,budget);
            var fast=new LTBalancedForest(3,3,budget,prepared,new HashSet<int>(prepared.Keys));fast.Balance();fast.Validate();
            foreach(int id in raw.Keys)
            {
                var a=full.Plan(id);var b=fast.Plan(id);
                Require(a.SequenceEqual(b),"cached internal closure changed global topology");
                Require(full.BoundaryStitches(id,a).SequenceEqual(fast.BoundaryStitches(id,b)),"cached internal closure changed stitches");
                Require(raw[id].SequenceEqual(before[id]),"cache changed raw plans");
            }
            return fast;
        }
        Check();Check();Require(cache.Reused==9&&cache.Rebuilt==0,"warm balance cache missed");
        raw[0].Reverse();Check();Require(cache.Reused==9,"source order invalidated balance cache");
        for(int iteration=0;iteration<60;iteration++)
        {
            int id=iteration%9;
            raw[id]=Grid(1<<random.Next(0,5));
            for(int j=0;j<18;j++)Split(raw[id],random.Next(raw[id].Count));
            Check();Require(cache.Rebuilt<=1&&cache.Reused>=8,"unchanged raw plans rebuilt");
            // A rejected proposal can remain cached; the old source must still
            // reconstruct exactly when restored, including coarsening neighbours.
            if(iteration%5==0){raw[id]=Grid(1);Check();}
        }
        raw=Enumerable.Range(0,9).ToDictionary(i=>i,i=>Grid(1));
        var coarse=Check();Require(raw.Keys.All(i=>coarse.Plan(i).Count==1),"cached neighbour detail failed to coarsen");
        bool rejected=false;
        raw[0]=Grid(4);Check();
        try{cache.Prepare(raw,1);}catch(InvalidOperationException){rejected=true;}
        Require(rejected,"cached result ignored reduced budget");Check();

        // Repeated edits: irregular dense raw plans, one changed chunk each run.
        raw=Enumerable.Range(0,36).ToDictionary(i=>i,i=>Grid(32));
        foreach(var cells in raw.Values)for(int j=0;j<5;j++)Split(cells,j==0?0:cells.Count-4);
        cache=new LTBalancedForest.InternalPlanCache();cache.Prepare(raw,budget);
        var previous=new LTBalancedForest(6,6,budget,raw);previous.Balance();
        var trusted=new HashSet<int>(raw.Keys.Where(i=>new HashSet<Vector3Int>(raw[i]).SetEquals(previous.Plan(i))));
        double Run(bool cached,out int visited)
        {
            var timer=Stopwatch.StartNew();
            var input=cached?cache.Prepare(raw,budget):raw;
            var forest=new LTBalancedForest(6,6,budget,input,cached?new HashSet<int>(input.Keys):trusted);
            forest.Balance();timer.Stop();visited=forest.ProcessedCells+(cached?cache.ProcessedCells:0);
            return timer.Elapsed.TotalMilliseconds;
        }
        Run(false,out _);Run(true,out _);
        var oldTimes=new List<double>();var newTimes=new List<double>();int oldCells=0,newCells=0;
        for(int i=0;i<7;i++)
        {
            raw[14]=Grid(32);for(int j=0;j<i+2;j++)Split(raw[14],j==0?0:raw[14].Count-4);
            oldTimes.Add(Run(false,out oldCells));newTimes.Add(Run(true,out newCells));
        }
        oldTimes.Sort();newTimes.Sort();
        Require(newCells<oldCells,"internal cache did not reduce visited cells");
        Console.WriteLine($"PASS internal balance cache: 60 edits, exact full-pass topology/stitches, order independence, source preservation, coarsening and budget. Synthetic 36 chunks: {oldTimes[3]:F2} -> {newTimes[3]:F2} ms, visited {oldCells} -> {newCells}; not Unity timings.");
    }
}
