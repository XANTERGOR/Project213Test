using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LocalTerrainPrototype;

partial class Checks
{
    static void SpatialMaskCacheChecks(LTSpatialLODTopology.Chunk[] inputs,int columns,int rows,int divisions)
    {
        long oldQueries=0,newQueries=0,oldTicks=0,newTicks=0,defaultOldQueries=0,defaultNewQueries=0,defaultOldTicks=0,defaultNewTicks=0;int compared=0;
        foreach(bool sparse in new[]{false,true})foreach(int budget in new[]{0,512,64*1024*1024})
        {
            var source=(LTSpatialLODTopology.Chunk[])inputs.Clone();
            if(sparse)source[source.Length-1]=null;
            var before=new SpatialIndexMaskReference(columns,rows,divisions,source.Select(c=>c==null?null:
                new SpatialIndexMaskReference.Chunk{cells=c.cells,patches=c.patches}).ToArray(),budget);
            var after=new LTSpatialLODTopology(columns,rows,divisions,source,budget);
            var oldOutput=new List<int>();var output=new List<int>();
            void Set(int c,int p,int level){before.SetLevel(c,p,level);after.SetLevel(c,p,level);}
            void Update()
            {
                Require(before.Update()==after.Update(),"border mask cache preserves update gate");
                Require(before.DirtyChunks.SetEquals(after.DirtyChunks),"border mask cache preserves atomic dirty set");
                Require(before.ProcessedCells==after.ProcessedCells&&before.ResetPatches==after.ResetPatches,"mask bits do not alter balancing");
                before.Validate();after.Validate();
            }
            void Compare(int pass)
            {
                for(int c=0;c<source.Length;c++)if(source[c]!=null)
                {
                    int priorOld=before.IndexMaskQueries,priorNew=after.IndexMaskQueries;
                    void Old(){long start=Stopwatch.GetTimestamp();before.WriteIndices(c,oldOutput);long dt=Stopwatch.GetTimestamp()-start;oldTicks+=dt;if(budget>512)defaultOldTicks+=dt;}
                    void New(){long start=Stopwatch.GetTimestamp();after.WriteIndices(c,output);long dt=Stopwatch.GetTimestamp()-start;newTicks+=dt;if(budget>512)defaultNewTicks+=dt;}
                    if(pass%2==0){Old();New();}else{New();Old();}
                    Require(output.SequenceEqual(oldOutput),"exact pre-optimization index order, holes and winding");compared++;
                    int oldCount=before.IndexMaskQueries-priorOld,newCount=after.IndexMaskQueries-priorNew;
                    Require(newCount<=oldCount,"no additional neighbour lookup work");oldQueries+=oldCount;newQueries+=newCount;
                    if(budget>512){defaultOldQueries+=oldCount;defaultNewQueries+=newCount;}
                    Require(before.IndexCacheBytes==after.IndexCacheBytes,"same bounded index cache payload");
                }
            }
            Update();Compare(0);
            var random=new Random(61271+divisions+(sparse?100:0));
            for(int pass=1;pass<=80;pass++)
            {
                if(pass%10==0)
                {
                    for(int c=0;c<source.Length;c++)if(source[c]!=null)
                        for(int p=0;p<divisions*divisions;p++)Set(c,p,(pass/10)%2==0?4:0);
                }
                else
                    for(int j=0;j<1+pass%5;j++)
                    {int c=random.Next(source.Length);if(source[c]!=null)Set(c,random.Next(divisions*divisions),random.Next(5));}
                Update();
                if(pass%3==0)Update(); // DirtyChunks clears; unread mask invalidation must survive.
                if(pass%4!=0)Compare(pass); // Multiple changes before reading, including opposing rims.
            }
            Compare(81);
            // Exhausted/zero payload budget must still reuse masks, not stale indices.
            Update();Compare(82);Update();
            for(int c=0;c<source.Length;c++)if(source[c]!=null)after.WriteIndices(c,output);
            Require(after.IndexMaskQueries==0,"warm unchanged masks need no tree walks even at zero index-cache budget");
            long allocated=GC.GetAllocatedBytesForCurrentThread();
            for(int pass=0;pass<12;pass++)
            {after.Update();for(int c=0;c<source.Length;c++)if(source[c]!=null)after.WriteIndices(c,output);}
            Require(GC.GetAllocatedBytesForCurrentThread()==allocated,"warm mask reuse has no managed allocations");
        }
        Require(newQueries<oldQueries,"border cache saves neighbour queries on mixed layout");
        Console.WriteLine($"PASS border mask cache: divisions={divisions}, {compared} exact frozen-reference outputs, full/sparse layouts and 3 budgets; neighbour queries {oldQueries}->{newQueries}; write CPU aggregate {oldTicks*1000.0/Stopwatch.Frequency:F2}->{newTicks*1000.0/Stopwatch.Frequency:F2} ms. Managed fixtures, not live frame timings.");
        Console.WriteLine($"  Default 64 MiB index budget ONLY: queries {defaultOldQueries}->{defaultNewQueries}; write CPU aggregate {defaultOldTicks*1000.0/Stopwatch.Frequency:F2}->{defaultNewTicks*1000.0/Stopwatch.Frequency:F2} ms.");
    }
}
