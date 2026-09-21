using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using LocalTerrainPrototype;
partial class Checks
{
    static void LocalBalanceChecks()
    {
        const int n=LTBalancedForest.N;
        List<Vector3Int> Grid(int side)
        {
            var result=new List<Vector3Int>();int w=n/side;
            for(int z=0;z<n;z+=w)for(int x=0;x<n;x+=w)result.Add(new Vector3Int(x,z,w));
            return result;
        }
        var plans=Enumerable.Range(0,36).ToDictionary(i=>i,i=>Grid(16));
        var reordered=plans[0].AsEnumerable().Reverse().ToList();
        Require(!plans[0].SequenceEqual(reordered)&&new HashSet<Vector3Int>(plans[0]).SetEquals(reordered),"plan order must not change topology equality");
        var random=new System.Random(917);
        for(int iteration=0;iteration<40;iteration++)
        {
            int changed=iteration%36;
            // Include aggressive coarsening, edge refinement and nonuniform plans.
            plans[changed]=Grid(1<<random.Next(0,6));
            for(int j=0;j<12;j++)
            {
                var list=plans[changed];int index=random.Next(list.Count);var p=list[index];
                list.RemoveAt(index);int h=p.z/2;
                list.AddRange(new[]{new Vector3Int(p.x,p.y,h),new Vector3Int(p.x+h,p.y,h),
                    new Vector3Int(p.x,p.y+h,h),new Vector3Int(p.x+h,p.y+h,h)});
            }
            var trusted=new HashSet<int>(plans.Keys.Where(i=>i!=changed));
            var full=new LTBalancedForest(6,6,100000,plans);
            var local=new LTBalancedForest(6,6,100000,plans,trusted);
            full.Balance();local.Balance();full.Validate();local.Validate();
            foreach(int id in plans.Keys.ToArray())
            {
                var a=full.Plan(id);var b=local.Plan(id);
                Require(a.SequenceEqual(b),"localized balancing changed topology");
                Require(full.BoundaryStitches(id,a).SequenceEqual(local.BoundaryStitches(id,b)),"localized balancing changed stitches");
                plans[id]=a;
            }
        }
        plans=Enumerable.Range(0,36).ToDictionary(i=>i,i=>Grid(32));
        plans[14]=Grid(64);
        var known=new HashSet<int>(plans.Keys.Where(i=>i!=14));
        double Run(bool optimized,out int processed)
        {
            var timer=Stopwatch.StartNew();
            var forest=new LTBalancedForest(6,6,100000,plans,optimized?known:null);
            forest.Balance();timer.Stop();processed=forest.ProcessedCells;
            return timer.Elapsed.TotalMilliseconds;
        }
        Run(false,out _);Run(true,out _);
        var baseline=new List<double>();var optimized=new List<double>();int oldCount=0,newCount=0;
        for(int i=0;i<7;i++){baseline.Add(Run(false,out oldCount));optimized.Add(Run(true,out newCount));}
        baseline.Sort();optimized.Sort();
        Require(newCount<oldCount/2,"localized balancing did not reduce queue work");
        Console.WriteLine($"PASS local balance: 40 refine/coarsen cases, exact topology/stitches. Synthetic 36 chunks construction+balance median: {baseline[3]:F2} -> {optimized[3]:F2} ms; visited {oldCount} -> {newCount}.");
    }
}
