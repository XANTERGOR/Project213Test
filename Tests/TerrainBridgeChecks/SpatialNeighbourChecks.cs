using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LocalTerrainPrototype;

partial class Checks
{
    static void SpatialNeighbourChecks(LTSpatialLODTopology.Chunk[] inputs,int columns,int rows,int divisions)
    {
        int compared=0;long oldBalance=0,newBalance=0,oldWrite=0,newWrite=0;
        const int fullBudget=16*1024*1024;
        foreach(bool sparse in new[]{false,true})foreach(int budget in new[]{0,4,inputs[0].cells.Length*4,fullBudget})
        {
            var source=(LTSpatialLODTopology.Chunk[])inputs.Clone();if(sparse)source[0]=null;
            var reference=new SpatialNeighbourReference(columns,rows,divisions,source.Select(c=>c==null?null:
                new SpatialNeighbourReference.Chunk{cells=c.cells,patches=c.patches}).ToArray());
            var actual=new LTSpatialLODTopology(columns,rows,divisions,source,64*1024*1024,budget);
            Require(actual.NeighbourCacheBytes<=budget,"bounded parent lookup cache");
            if(budget==fullBudget)Require(actual.NeighbourCacheBytes==source.Where(c=>c!=null).Sum(c=>(long)c.cells.Length*4),"one int per hierarchy cell");
            else if(budget>4)Require(actual.NeighbourCacheBytes>0&&actual.NeighbourCacheBytes<source.Where(c=>c!=null).Sum(c=>(long)c.cells.Length*4),"mixed cached and root-walk chunks");
            var expected=new List<int>();var output=new List<int>();var random=new Random(38041+divisions);
            void Set(int c,int p,int level){reference.SetLevel(c,p,level);actual.SetLevel(c,p,level);}
            void Step(int pass)
            {
                bool oldChanged=false,changed=false;
                void OldUpdate(){long t=Stopwatch.GetTimestamp();oldChanged=reference.Update();if(budget==fullBudget&&pass>0)oldBalance+=Stopwatch.GetTimestamp()-t;}
                void Update(){long t=Stopwatch.GetTimestamp();changed=actual.Update();if(budget==fullBudget&&pass>0)newBalance+=Stopwatch.GetTimestamp()-t;}
                if(pass%2==0){OldUpdate();Update();}else{Update();OldUpdate();}
                Require(oldChanged==changed&&reference.DirtyChunks.SetEquals(actual.DirtyChunks),"same update gate and dirty chunks");
                Require(reference.ProcessedCells==actual.ProcessedCells&&reference.ResetPatches==actual.ResetPatches,"same balance queue, splits and dependency resets");
                reference.Validate();actual.Validate();
                if(pass%7==0)return; // Several changes, and idle Update, before publication.
                for(int c=0;c<source.Length;c++)if(source[c]!=null)
                {
                    void OldWrite(){long t=Stopwatch.GetTimestamp();reference.WriteIndices(c,expected);if(budget==fullBudget&&pass>0)oldWrite+=Stopwatch.GetTimestamp()-t;}
                    void Write(){long t=Stopwatch.GetTimestamp();actual.WriteIndices(c,output);if(budget==fullBudget&&pass>0)newWrite+=Stopwatch.GetTimestamp()-t;}
                    if(pass%2==0){OldWrite();Write();}else{Write();OldWrite();}
                    Require(expected.SequenceEqual(output),"common ancestor lookup preserves exact ordered indices");compared++;
                }
                Require(reference.IndexCacheBytes==actual.IndexCacheBytes&&reference.IndexMaskQueries==actual.IndexMaskQueries,"same index cache and mask query counts");
            }
            for(int pass=0;pass<=90;pass++)
            {
                if(pass%9==0)
                {
                    for(int c=0;c<source.Length;c++)if(source[c]!=null)
                        for(int p=0;p<divisions*divisions;p++)Set(c,p,(pass/9)%2==0?0:4);
                }
                else for(int j=0;j<1+pass%7;j++)
                    {int c=random.Next(source.Length);if(source[c]!=null)Set(c,random.Next(divisions*divisions),random.Next(5));}
                Step(pass);if(pass%5==0)Step(pass);
            }
            Step(92);actual.Update();
            for(int c=0;c<source.Length;c++)if(source[c]!=null)actual.WriteIndices(c,output);
            long allocation=GC.GetAllocatedBytesForCurrentThread();
            for(int pass=0;pass<10;pass++){actual.Update();for(int c=0;c<source.Length;c++)if(source[c]!=null)actual.WriteIndices(c,output);}
            Require(GC.GetAllocatedBytesForCurrentThread()==allocation,"no warm idle allocation");
        }
        double Ms(long ticks)=>ticks*1000.0/Stopwatch.Frequency;
        Console.WriteLine($"PASS neighbour lookup divisions={divisions}: {compared} exact frozen outputs, same balance work; full/sparse, enabled/disabled/tiny/partial cache. Managed default-cache aggregate balance {Ms(oldBalance):F2}->{Ms(newBalance):F2} ms, indices {Ms(oldWrite):F2}->{Ms(newWrite):F2} ms (NOT live frame timings).");
    }
}
