using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;

partial class Checks
{
    static void SpatialIndexCopyChecks(LTSpatialLODTopology.Chunk[] inputs,int columns,int rows,int divisions)
    {
        int compared=0;
        foreach(int budget in new[]{0,512,64*1024*1024})foreach(bool empty in new[]{false,true})
        {
            // Empty payload exercises fully cut terrain with a valid partition.
            // Clone the fixture; never replace an input cell/variant in place.
            var source=inputs.Select(c=>new LTSpatialLODTopology.Chunk{patches=c.patches,cells=c.cells.Select(cell=>
            {if(empty&&cell.variants!=null)cell.variants=cell.variants.Select(_=>new LTSpatialLODVariant{indices=Array.Empty<int>()}).ToArray();return cell;}).ToArray()}).ToArray();
            source[source.Length-1]=null; // Exposed border and non-contiguous slots.
            var reference=new SpatialIndexCopyReference(columns,rows,divisions,source.Select(c=>c==null?null:
                new SpatialIndexCopyReference.Chunk{cells=c.cells,patches=c.patches}).ToArray(),budget);
            var actual=new LTSpatialLODTopology(columns,rows,divisions,source,budget);
            var expected=new List<int>();var output=new List<int>{-1,-2,-3};var random=new Random(41172+divisions);
            void Counters()
            {
                Require(actual.IndexCacheBytes==reference.IndexCacheBytes&&actual.IndexCacheBytes<=budget,"same bounded payload capacity after growth/shrink/empty");
                Require(actual.IndexCacheHits==reference.IndexCacheHits&&actual.IndexCacheBuilds==reference.IndexCacheBuilds&&
                    actual.IndexCacheCells==reference.IndexCacheCells&&actual.IndexCacheFallbacks==reference.IndexCacheFallbacks&&
                    actual.IndexMaskQueries==reference.IndexMaskQueries,"bulk copy changes no cache or query decisions");
            }
            void Update()
            {
                Require(reference.Update()==actual.Update(),"same update gate");
                Require(reference.DirtyChunks.SetEquals(actual.DirtyChunks)&&reference.ProcessedCells==actual.ProcessedCells&&
                    reference.ResetPatches==actual.ResetPatches,"same balancing and publication");
                Counters();
            }
            void Compare()
            {
                for(int c=0;c<source.Length;c++)if(source[c]!=null)
                {
                    reference.WriteIndices(c,expected);actual.WriteIndices(c,output);
                    Require(output.SequenceEqual(expected),"bulk copy preserves exact index order and logical count");compared++;
                    if(empty)Require(output.Count==0,"empty payload clears prior caller output");
                    if(output.Count>0)output[0]=-999;output.Add(-999);
                    reference.WriteIndices(c,expected);actual.WriteIndices(c,output);
                    Require(output.SequenceEqual(expected),"caller mutation cannot reach cached storage");compared++;
                    Counters();
                }
            }
            for(int pass=0;pass<72;pass++)
            {
                for(int c=0;c<source.Length;c++)if(source[c]!=null)for(int p=0;p<divisions*divisions;p++)
                {
                    int level=pass%8==0?0:pass%8==1?4:random.Next(5);
                    reference.SetLevel(c,p,level);actual.SetLevel(c,p,level);
                }
                Update();if(pass%3==0)Update();if(pass%5!=0)Compare();
            }
            Compare();actual.Validate();reference.Validate();
            actual.Update();for(int c=0;c<source.Length;c++)if(source[c]!=null)actual.WriteIndices(c,output);
            long start=GC.GetAllocatedBytesForCurrentThread();
            for(int pass=0;pass<20;pass++){actual.Update();for(int c=0;c<source.Length;c++)if(source[c]!=null)actual.WriteIndices(c,output);}
            Require(GC.GetAllocatedBytesForCurrentThread()==start,"bulk cache hits allocate no enumerators or boxed segments");
        }
        Console.WriteLine($"PASS bulk index copy divisions={divisions}: {compared} exact frozen outputs, ordered triangles/count/capacity/counters; zero/tiny/full budgets, growth/shrink/empty, caller mutation, deferred reads and zero warm GC.");
    }
}
