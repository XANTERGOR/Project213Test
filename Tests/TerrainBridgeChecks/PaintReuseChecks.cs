using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void PaintReuseChecks()
    {
        int checks=0;
        void Check(bool ok,string why){checks++;Require(ok,"paint preparation reuse: "+why);}
        List<int>[] OriginalBins(Rect rect,Vector3[] vertices,int[] indices)
        {
            int axis=LTPaintMath.TerrainBinAxis(indices.Length/3);var bins=new List<int>[axis*axis];
            for(int i=0;i+2<indices.Length;i+=3)
            {
                var range=LTPaintMath.TerrainTriangleBins(rect,vertices[indices[i]],vertices[indices[i+1]],vertices[indices[i+2]],axis);
                for(int z=range.yMin;z<range.yMax;z++)for(int x=range.xMin;x<range.xMax;x++)
                {int id=z*axis+x;if(bins[id]==null)bins[id]=new List<int>();bins[id].Add(i);}
            }
            return bins;
        }
        bool SameBins(List<int>[] a,List<int>[] b)=>a.Length==b.Length&&a.Zip(b,(x,y)=>(x??new List<int>()).SequenceEqual(y??new List<int>())).All(x=>x);
        const int side=129;var rect=new Rect(0,0,64,64);var vertices=new Vector3[side*side];var normals=new Vector3[vertices.Length];
        for(int z=0;z<side;z++)for(int x=0;x<side;x++){int i=z*side+x;vertices[i]=new Vector3(x*.5f,.01f*x+.03f*z,z*.5f);normals[i]=new Vector3(-.01f,1,-.03f).normalized;}
        var triangles=new List<int>();
        for(int z=0;z<side-1;z++)for(int x=0;x<side-1;x++){int a=z*side+x;triangles.AddRange(new[]{a,a+side,a+1,a+1,a+side,a+side+1});}
        var indices=triangles.ToArray();
        long before=GC.GetAllocatedBytesForCurrentThread();
        var original=LTPaintWeightTerrainCache.BuildSource(rect,vertices,normals,indices,null,out bool binsReused,out bool surfaceReused);
        long cold=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(!binsReused&&!surfaceReused&&SameBins(original.bins,OriginalBins(rect,vertices,indices)),"cold bins retain exact original triangle order");
        var identical=LTPaintWeightTerrainCache.BuildSource(rect,(Vector3[])vertices.Clone(),(Vector3[])normals.Clone(),(int[])indices.Clone(),original,out binsReused,out surfaceReused);
        Check(binsReused&&surfaceReused&&ReferenceEquals(original,identical),"mesh re-upload with identical values retains full source token");
        var heights=(Vector3[])vertices.Clone();heights[123].y+=1;
        before=GC.GetAllocatedBytesForCurrentThread();
        var raised=LTPaintWeightTerrainCache.BuildSource(rect,heights,normals,indices,original,out binsReused,out surfaceReused);
        long warm=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(binsReused&&!surfaceReused&&!ReferenceEquals(original,raised)&&ReferenceEquals(original.bins,raised.bins),"height edits share only immutable bins, not the surface identity");
        Check(raised.vertices[123].y!=original.vertices[123].y&&SameBins(raised.bins,OriginalBins(rect,heights,indices)),"old source stays intact; reused bins equal fresh bins");
        Check(warm<4096&&cold>warm*10,"height-only index update avoids building bin lists");
        Console.WriteLine($"Paint spatial-index allocation fixture ({indices.Length/3} triangles): cold {cold} bytes, height-only reuse {warm}. No mesh read/transform included; not Unity timings.");
        var changedNormals=(Vector3[])normals.Clone();changedNormals[0].x+=1e-7f;
        var shade=LTPaintWeightTerrainCache.BuildSource(rect,vertices,changedNormals,indices,original,out binsReused,out surfaceReused);
        Check(binsReused&&!surfaceReused&&ReferenceEquals(shade.bins,original.bins),"even sub-epsilon normal edits invalidate surface but keep bins");
        var shifted=(Vector3[])vertices.Clone();shifted[0].x=1e-7f;
        var moved=LTPaintWeightTerrainCache.BuildSource(rect,shifted,normals,indices,original,out binsReused,out surfaceReused);
        Check(!binsReused&&!surfaceReused&&SameBins(moved.bins,OriginalBins(rect,shifted,indices)),"sub-epsilon XZ edit cannot use approximate Vector3 equality");
        var reordered=(int[])indices.Clone();(reordered[0],reordered[1])=(reordered[1],reordered[0]);
        LTPaintWeightTerrainCache.BuildSource(rect,vertices,normals,reordered,original,out binsReused,out _);
        Check(!binsReused,"changed index order invalidates spatial index");
        LTPaintWeightTerrainCache.BuildSource(rect,vertices,normals,indices.Take(indices.Length-3).ToArray(),original,out binsReused,out _);
        Check(!binsReused,"triangle removal/cut invalidates index");
        LTPaintWeightTerrainCache.BuildSource(new Rect(1e-7f,0,64,64),vertices,normals,indices,original,out binsReused,out _);
        Check(!binsReused,"rect/layout changes invalidate index exactly");
        var noNormals=LTPaintWeightTerrainCache.BuildSource(rect,vertices,Array.Empty<Vector3>(),indices,original,out binsReused,out surfaceReused);
        Check(binsReused&&!surfaceReused&&noNormals.normals.Length==0,"removing normals keeps bins but activates geometric normal fallback");
        var signedZero=(Vector3[])vertices.Clone();signedZero[0].y=BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        LTPaintWeightTerrainCache.BuildSource(rect,signedZero,normals,indices,original,out binsReused,out surfaceReused);
        Check(binsReused&&!surfaceReused,"signed-zero change preserves bit-exact fingerprint invalidation");

        var layout=new LTPaintWeightMath.Terrain{countX=1,countZ=1,sizeX=64,sizeZ=64,width=64,depth=64};
        var snapshots=new LTPaintWeightTerrainCache();LTPaintWeightTerrainCache.Source source=original;
        LTPaintWeightTerrainCache.Source Lookup(int x,int z)=>source;
        snapshots.TryGet(layout,new RectInt(0,0,1,1),Lookup,out var held,out _);
        source=identical;snapshots.TryGet(layout,new RectInt(0,0,1,1),Lookup,out var shared,out bool hit);
        Check(hit&&ReferenceEquals(held,shared),"identical re-upload reuses regional snapshot");
        source=raised;snapshots.TryGet(layout,new RectInt(0,0,1,1),Lookup,out var refreshed,out hit);
        Check(!hit&&refreshed.vertices[123].y==heights[123].y&&held.vertices[123].y==vertices[123].y,"Y change invalidates packed snapshot without changing outstanding reader");

        Check(System.Runtime.InteropServices.Marshal.SizeOf<LTPaintWeightMath.Stamp>()<=LTPaintWeightInputPool.StampBytes,"descriptor byte allowance covers actual struct");
        var pool=new LTPaintWeightInputPool();
        before=GC.GetAllocatedBytesForCurrentThread();
        var first=pool.Rent(12,66049,66049,66049*8,out int created,out int reused,out int allocated);
        long firstBytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(created==4&&reused==0&&allocated>0&&pool.RetainedBytes==0,"first rent owns all new arrays, none idle");
        first.masks[3]=1;first.coordinates[7]=new Vector2(8,9);var oldMask=first.masks;var oldCoordinates=first.coordinates;
        var copiedCoordinates=(Vector2[])first.coordinates.Clone();
        first.Dispose();long retained=pool.RetainedBytes;first.Dispose();
        Check(retained>0&&retained<=LTPaintWeightInputPool.ByteLimit&&pool.RetainedBytes==retained&&first.coordinates==null&&first.terrain==null,"idempotent return detaches lease and stays bounded");
        before=GC.GetAllocatedBytesForCurrentThread();
        var second=pool.Rent(12,66049,66049,66049*8,out created,out reused,out allocated);
        long secondBytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(created==0&&reused==4&&allocated==0&&ReferenceEquals(second.masks,oldMask)&&ReferenceEquals(second.coordinates,oldCoordinates),"same shape reuses all arrays without payload allocation");
        Check(second.masks.All(v=>v==0),"sparse mask cannot retain previous covered texels");
        second.coordinates[7]=new Vector2(-10,-20);
        Check(copiedCoordinates[7].Equals(new Vector2(8,9)),"independent copied input survives reused scratch overwrite");
        var concurrent=pool.Rent(12,66049,66049,66049*8,out created,out reused,out _);
        Check(created==4&&reused==0&&!ReferenceEquals(second.coordinates,concurrent.coordinates),"outstanding leases never alias");
        second.Dispose();concurrent.Dispose();
        Check(pool.RetainedBytes<=LTPaintWeightInputPool.ByteLimit,"multiple returns still retain only one array per type");
        Check(secondBytes<4096&&secondBytes*10<firstBytes,"warm rent allocates only a small lease, not multi-megabyte arrays");
        Console.WriteLine($"Paint input allocation fixture (257x257, 12 descriptors): cold {firstBytes} bytes, warm {secondBytes}; retained {pool.RetainedBytes}. Native copies unchanged; not Unity timings.");
        var resize=pool.Rent(1,17,23,29,out created,out reused,out _);
        Check(created==4&&reused==0&&resize.coordinates.Length==29&&resize.masks.Length==23,"resize uses exact lengths, never stale capacity tail");
        pool.Clear();resize.Dispose();Check(pool.RetainedBytes==0,"clear prevents outstanding old lease from repopulating disposed-world cache");
        using(var empty=pool.Rent(0,0,0,0,out created,out reused,out _))Check(created==0&&reused==0&&empty.coordinates.Length==0,"empty payloads allocate nothing");
        bool rejected=false;try{pool.Rent(65,0,0,0,out _,out _,out _);}catch(ArgumentException){rejected=true;}Check(rejected,"descriptor limit enforced");
        rejected=false;try{pool.Rent(1,int.MaxValue,0,0,out _,out _,out _);}catch(ArgumentException){rejected=true;}Check(rejected,"overflow-safe total-byte rejection before allocation");
        // Mix different payload types while other leases are active, testing the
        // aggregate retained-byte cap rather than only per-array caps.
        var hugeHits=pool.Rent(0,900000,0,0,out _,out _,out _);
        var hugeCoords=pool.Rent(0,0,0,1800000,out _,out _,out _);
        hugeHits.Dispose();hugeCoords.Dispose();Check(pool.RetainedBytes<=LTPaintWeightInputPool.ByteLimit,"mixed returns cannot exceed total idle budget");pool.Clear();

        int pixels=0;
        for(int f=0;f<24;f++)
        {
            var fixture=f%2==0?LTPaintWeightFixture.Build(f):LTPaintWeightFixture.BuildRock(f);
            using(var input=fixture.PreparedInput(pool))
            {
                var prepared=new LTPaintWeightFixture{input=input,roads=fixture.roads};
                for(int p=0;p<input.size*input.size;p++)
                {
                    var expected=fixture.Reference(p);var actual=prepared.Scalar(p);if(actual.guard!=0)actual=expected;
                    Check(LTPaintWeightFixture.Same(actual,expected),"pooled+spatial-index final pixel parity");pixels++;
                }
            }
            fixture.OverwriteReturnedBuffers(pool); // next preparation must overwrite all required fields
        }
        var capture=new LTPaintCpuCapture();using(capture.BeginTick(true))
        {capture.TerrainTileReused();capture.TerrainTilePrepared(true,false);capture.TerrainTilePrepared(true,true);capture.WeightInputRented(4,8,1024);}
        Check(capture.LastTick.terrainTileReuses==1&&capture.LastTick.terrainTileBuilds==2&&capture.LastTick.terrainIndexReuses==2&&capture.LastTick.terrainSurfaceReuses==1,"terrain counters distinguish no-read, index reuse and identical surface");
        Check(capture.LastTick.weightInputAllocations==4&&capture.LastTick.weightInputReuses==8&&capture.LastTick.weightInputAllocatedBytes==1024,"pool counters record allocations/reuse/bytes");
        string runtime=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTPaintRuntime.cs"),rocks=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTPaintRocks.cs");
        Check(runtime.Contains("using(input)")&&rocks.Contains("using(input)")&&runtime.Contains("weightInputs.Clear()"),"both scheduling paths return staging leases and world disposal clears pool");
        Console.WriteLine($"PASS paint preparation reuse: {checks} checks, {pixels} exact pooled pixels; spatial-index invalidation/identity, immutable readers, pool ownership, overwrite, resizing, clearing and byte bounds. Managed only.");
    }
}
