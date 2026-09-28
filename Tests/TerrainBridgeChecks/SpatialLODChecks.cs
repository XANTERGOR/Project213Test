using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;
partial class Checks
{
    static void SpatialLODChecks()
    {
        int tested=0,mixed=0;long idleAlloc=0;var clock=System.Diagnostics.Stopwatch.StartNew();
        foreach(int divisions in new[]{1,2,4,8})foreach(bool irregular in new[]{false,true})
        {
            const int columns=2,rows=2,count=4;const float width=64,extent=128;
            float H(float x,float z)=>.001f*x*x+.0003f*z*z;
            bool Cut(float x,float z)=>x>61.25f&&x<66.75f&&z>29.25f&&z<34.75f;
            var protection=new Rect(57,25,14,14);
            var plans=new Dictionary<int,List<Vector3Int>>();
            Rect RectOf(int c)=>new Rect(c%columns*width,c/columns*width,width,width);
            for(int c=0;c<count;c++)
            {
                // Vary base resolution across chunks as well as within a chunk.
                var r=RectOf(c);
                var fine=LTStampMesh.Plan(r,irregular&&c%2==1?16:32,false,.01f,100000,new List<LTStampMesh.Zone>(),H);
                if(irregular)
                {
                    int n=LTSpatialLODMath.N;var refined=new List<Vector3Int>();
                    foreach(var p in fine)
                    {
                        if(p.x<n/4&&p.y>n/2)
                        {int h=p.z/2;for(int z=0;z<2;z++)for(int x=0;x<2;x++)refined.Add(new Vector3Int(p.x+x*h,p.y+z*h,h));}
                        else refined.Add(p);
                    }
                    fine=refined;
                }
                plans[c]=LTSpatialLODMath.PartitionLeaves(fine,divisions);
            }
            var forest=new LTBalancedForest(columns,rows,100000,plans);forest.Balance();
            var outputs=new LTSpatialLODMath.Output[count];var inputs=new LTSpatialLODTopology.Chunk[count];
            for(int c=0;c<count;c++)
            {
                int chunk=c;var r=RectOf(c);var fine=forest.Plan(c);var levels=new List<Vector3Int>[5];levels[0]=fine;
                Rect Area(Vector3Int p)=>new Rect(r.xMin+p.x/(float)LTSpatialLODMath.N*width,r.yMin+p.y/(float)LTSpatialLODMath.N*width,p.z/(float)LTSpatialLODMath.N*width,p.z/(float)LTSpatialLODMath.N*width);
                bool Protected(Vector3Int p)=>irregular&&LTStampMesh.Overlap(Area(p),protection);
                for(int l=1;l<5;l++)levels[l]=LTLODMesh.Coarsen(fine,r,l,100,H,irregular?new List<Rect>{protection}:null,null,divisions,false);
                int Mask(Vector3Int p){int mask=0;for(int s=0;s<4;s++)if(forest.Midpoint(chunk,p,s))mask|=1<<s;return mask;}
                var data=LTSpatialLODMath.BuildLayout(levels,divisions,100000,Mask,Protected);
                LTStampMesh.EmitSpatialVariants(data,r,new Vector2(extent,extent),100000,H,irregular?Cut:null);
                outputs[c]=data;inputs[c]=new LTSpatialLODTopology.Chunk{cells=data.cells,patches=data.patches};
                // Verify the alternate emitter retains the actual legacy LOD0 cut contour.
                LTStampMesh.Emit(r,new Vector2(extent,extent),100000,forest,c,fine,H,irregular?Cut:null,out var v,out _,out _,out var indices);
                Require(SpatialTriangles(v,indices).SetEquals(SpatialTriangles(data.vertices,data.baseIndices)),"spatial LOD0 must match production mesh exactly");
            }
            var topology=new LTSpatialLODTopology(columns,rows,divisions,inputs);
            topology.Update();topology.Validate();
            var requested=new int[count,divisions*divisions];
            int[][] Read(LTSpatialLODTopology t)
            {
                var result=new int[count][];var list=new List<int>();
                for(int c=0;c<count;c++){t.WriteIndices(c,list);result[c]=list.ToArray();}
                return result;
            }
            var baseline=Read(topology);
            var holes=SpatialBoundary(outputs,baseline,columns,width,extent);
            Require(irregular?holes.Count>0:holes.Count==0,"expected hole contour");
            void Check()
            {
                topology.Update();topology.Validate();var actual=Read(topology);
                Require(SpatialBoundary(outputs,actual,columns,width,extent).SetEquals(holes),"no patch/chunk cracks or changed cut contour");
                var fresh=new LTSpatialLODTopology(columns,rows,divisions,inputs);
                for(int c=0;c<count;c++)for(int p=0;p<divisions*divisions;p++)fresh.SetLevel(c,p,requested[c,p]);
                fresh.Update();fresh.Validate();var expected=Read(fresh);
                for(int c=0;c<count;c++)Require(SpatialIndexTriangles(actual[c]).SetEquals(SpatialIndexTriangles(expected[c])),"incremental balance must equal fresh balance; no sticky refinement");
                mixed++;
            }
            void All(int level)
            {
                for(int c=0;c<count;c++)for(int p=0;p<divisions*divisions;p++){requested[c,p]=level;topology.SetLevel(c,p,level);}
                Check();
            }
            All(4);var far=Read(topology);
            Require(far.Sum(t=>t.Length)<baseline.Sum(t=>t.Length)/2,"LOD must reduce full geometry including boundaries");
            if(!irregular)
            {
                int side=Math.Max(2,divisions);
                Require(far.Sum(t=>t.Length)/3==count*side*side*2,"uniform far surface has no LOD0 grid strips on any patch/chunk edge");
            }
            All(0);All(4);
            var random=new System.Random(901+divisions);
            for(int pass=0;pass<24;pass++)
            {
                int changes=pass%3==0?count*divisions*divisions:1;
                for(int i=0;i<changes;i++)
                {
                    int c=random.Next(count),p=random.Next(divisions*divisions),level=random.Next(5);
                    requested[c,p]=level;topology.SetLevel(c,p,level);
                }
                Check();
            }
            All(4);var returned=Read(topology);
            for(int c=0;c<count;c++)Require(SpatialIndexTriangles(far[c]).SetEquals(SpatialIndexTriangles(returned[c])),"returning far must release all forced refinement");
            topology.Update();long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<100;i++)topology.Update();
            idleAlloc+=GC.GetAllocatedBytesForCurrentThread()-before;
            Require(topology.ProcessedCells==0&&topology.ResetPatches==0&&topology.DirtyChunks.Count==0,"idle performs no topology work/uploads");
            if(!irregular&&divisions==4)
            {
                // Same flat topology replicated to a 16x16 world (no native meshes).
                var largeInputs=Enumerable.Repeat(inputs[0],256).ToArray();
                var large=new LTSpatialLODTopology(16,16,divisions,largeInputs);
                for(int c=0;c<256;c++)for(int p=0;p<divisions*divisions;p++)large.SetLevel(c,p,4);
                large.Update();large.Validate();
                large.SetLevel(119,5,0);large.Update();large.Validate();
                Require(large.ResetPatches==1&&large.DirtyChunks.Count<16,"one nearby patch must not reset/upload the whole 256-chunk world");
                large.SetLevel(119,5,4);large.Update();large.Validate();
                Require(large.ResetPatches<32,"refinement dependency closure must remain local");
                var buffer=new List<int>();
                for(int c=0;c<256;c++){large.WriteIndices(c,buffer);Require(buffer.Count==divisions*divisions*6,"large world returns to uniform far topology");}
            }
            tested++;
        }
        Require(idleAlloc==0,"idle topology must allocate zero bytes after warmup");
        var coarse=new List<Vector3Int>{new Vector3Int(0,0,1<<24)};
        Require(LTSpatialLODMath.PartitionLeaves(coarse,4).Count==16,"coarse base split to patch grid");
        Console.WriteLine($"PASS adaptive spatial LOD: {tested} multi-chunk layouts; {mixed} mixed/incremental comparisons; LOD0 parity, watertight edges, preserved holes, far border reduction, return-far cleanup; idle allocation {idleAlloc} B; {clock.Elapsed.TotalSeconds:F2}s managed tests, not native rendering.");
    }
    static HashSet<(int,int,int)> SpatialIndexTriangles(int[] indices)
    {
        var result=new HashSet<(int,int,int)>();
        for(int i=0;i<indices.Length;i+=3)
        {
            int a=indices[i],b=indices[i+1],c=indices[i+2];
            if(b<a&&b<c)result.Add((b,c,a));else if(c<a&&c<b)result.Add((c,a,b));else result.Add((a,b,c));
        }
        return result;
    }
    static Vector3Int SpatialKey(Vector3 v)=>new Vector3Int((int)Math.Round(v.x*10000),(int)Math.Round(v.y*10000),(int)Math.Round(v.z*10000));
    static HashSet<(Vector3Int,Vector3Int,Vector3Int)> SpatialTriangles(Vector3[] vertices,int[] indices)
    {
        var result=new HashSet<(Vector3Int,Vector3Int,Vector3Int)>();
        bool Before(Vector3Int a,Vector3Int b)=>a.x<b.x||a.x==b.x&&(a.z<b.z||a.z==b.z&&a.y<b.y);
        for(int i=0;i<indices.Length;i+=3)
        {
            var a=SpatialKey(vertices[indices[i]]);var b=SpatialKey(vertices[indices[i+1]]);var c=SpatialKey(vertices[indices[i+2]]);
            if(Before(b,a)&&Before(b,c))result.Add((b,c,a));else if(Before(c,a)&&Before(c,b))result.Add((c,a,b));else result.Add((a,b,c));
        }
        return result;
    }
    static HashSet<(Vector3Int,Vector3Int)> SpatialBoundary(LTSpatialLODMath.Output[] data,int[][] indices,int columns,float width,float extent)
    {
        var edges=new Dictionary<(Vector3Int,Vector3Int),int>();
        for(int c=0;c<data.Length;c++)
        {
            var offset=new Vector3(c%columns*width,0,c/columns*width);
            var v=data[c].vertices;
            for(int i=0;i<indices[c].Length;i+=3)for(int j=0;j<3;j++)
            {
                var a=SpatialKey(v[indices[c][i+j]]+offset);var b=SpatialKey(v[indices[c][i+(j+1)%3]]+offset);
                if(edges.TryGetValue((b,a),out int count)){if(count==1)edges.Remove((b,a));else edges[(b,a)]=count-1;}
                else{edges.TryGetValue((a,b),out count);edges[(a,b)]=count+1;}
            }
        }
        int end=(int)(extent*10000);
        var result=new HashSet<(Vector3Int,Vector3Int)>();
        foreach(var edge in edges)
        {
            Require(edge.Value==1,"non-manifold oriented boundary");
            var a=edge.Key.Item1;var b=edge.Key.Item2;
            if(a.x==b.x&&(a.x==0||a.x==end)||a.z==b.z&&(a.z==0||a.z==end))continue;
            result.Add(edge.Key);
        }
        return result;
    }
}
