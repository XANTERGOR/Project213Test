using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;

partial class Checks
{
    static void SpatialIndexCacheChecks(LTSpatialLODTopology.Chunk[] inputs,int columns,int rows,int divisions)
    {
        var referenceInputs=inputs.Select(c=>new SpatialTopologyReference.Chunk{cells=c.cells,patches=c.patches}).ToArray();
        foreach(int budget in new[]{0,512,64*1024*1024})
        {
            var topology=new LTSpatialLODTopology(columns,rows,divisions,inputs,budget);
            var reference=new SpatialTopologyReference(columns,rows,divisions,referenceInputs);
            var actual=new List<int>();var expected=new List<int>();
            void Set(int c,int p,int level){topology.SetLevel(c,p,level);reference.SetLevel(c,p,level);}
            void Update()
            {
                Require(topology.Update()==reference.Update(),"cache does not change topology update gate");
                topology.Validate();reference.Validate();
                Require(topology.DirtyChunks.SetEquals(reference.DirtyChunks),"same synchronous dirty chunk publication");
            }
            void Compare()
            {
                for(int c=0;c<inputs.Length;c++)
                {
                    topology.WriteIndices(c,actual);reference.WriteIndices(c,expected);
                    Require(actual.SequenceEqual(expected),"patch cache matches frozen uncached index sequence, including cut/stitch variants");
                    if(actual.Count>0)actual[0]=-1;actual.Clear();
                    topology.WriteIndices(c,actual);
                    Require(actual.SequenceEqual(expected),"caller mutation cannot damage cached indices");
                }
                Require(topology.IndexCacheBytes<=budget,"retained cache capacity respects budget");
            }
            Update();Compare();
            if(budget==0)Require(topology.IndexCacheFallbacks>0&&topology.IndexCacheBytes==0,"zero budget uses original assembly without loss");
            var random=new Random(4301+divisions);
            for(int pass=0;pass<64;pass++)
            {
                if(pass%8==0)
                    for(int c=0;c<inputs.Length;c++)for(int p=0;p<divisions*divisions;p++)Set(c,p,(pass/8)%2==0?4:0);
                else
                    for(int i=0;i<1+pass%7;i++)Set(random.Next(inputs.Length),random.Next(divisions*divisions),random.Next(5));
                Update();
                // Defer reads across an idle Update, or several changed Updates:
                // invalidation is not allowed to disappear with DirtyChunks.Clear.
                if(pass%4==0)Update();
                if(pass%5!=0)Compare();
            }
            Compare();
            if(budget>512)
            {
                Update();Compare();
                Require(topology.IndexCacheBuilds==0&&topology.IndexCacheCells==0&&topology.IndexCacheHits==inputs.Length*divisions*divisions*2,"unchanged patches skip mask/neighbor traversal entirely");
                long before=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<20;i++){topology.Update();for(int c=0;c<inputs.Length;c++)topology.WriteIndices(c,actual);}
                Require(GC.GetAllocatedBytesForCurrentThread()==before,"warm unchanged index reuse allocates zero bytes");
            }
        }
        SpatialMaskCacheChecks(inputs,columns,rows,divisions);
        SpatialNeighbourChecks(inputs,columns,rows,divisions);
        SpatialIndexCopyChecks(inputs,columns,rows,divisions);
    }
}
