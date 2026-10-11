using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    // Deliberately retain the previous List/Add/ToArray packer as an independent
    // structural reference. Tests compare every array, not only rendered weights.
    static LTPaintTerrainSnapshot OldPaintSnapshot(LTPaintWeightMath.Terrain layout,RectInt range,Func<int,int,LTPaintWeightTerrainCache.Source> lookup)
    {
        var result=new LTPaintTerrainSnapshot{terrain=layout,tiles=new LTPaintWeightMath.Tile[layout.countX*layout.countZ]};
        var vertices=new List<Vector3>();var normals=new List<Vector3>();var indices=new List<int>();
        var bins=new List<Vector2Int>();var triangles=new List<int>();
        for(int z=range.yMin;z<range.yMax;z++)for(int x=range.xMin;x<range.xMax;x++)
        {
            var t=lookup(x,z);if(t==null)continue;bool hasNormals=t.normals.Length==t.vertices.Length;
            result.tiles[z*layout.countX+x]=new LTPaintWeightMath.Tile{valid=1,rect=t.rect,axis=t.axis,firstBin=bins.Count,hasNormals=hasNormals?1:0};
            int firstVertex=vertices.Count,firstIndex=indices.Count;vertices.AddRange(t.vertices);
            for(int i=0;i<t.vertices.Length;i++)normals.Add(hasNormals?t.normals[i]:Vector3.zero);
            foreach(int index in t.indices)indices.Add(firstVertex+index);
            foreach(var bin in t.bins)
            {bins.Add(new Vector2Int(triangles.Count,bin?.Count??0));if(bin!=null)foreach(int triangle in bin)triangles.Add(firstIndex+triangle);}
        }
        result.vertices=vertices.ToArray();result.normals=normals.ToArray();result.indices=indices.ToArray();
        result.bins=bins.ToArray();result.triangles=triangles.ToArray();return result;
    }
    static bool SamePaintSnapshot(LTPaintTerrainSnapshot a,LTPaintTerrainSnapshot b)=>a.terrain.Equals(b.terrain)&&
        a.tiles.SequenceEqual(b.tiles)&&a.vertices.SequenceEqual(b.vertices)&&a.normals.SequenceEqual(b.normals)&&
        a.indices.SequenceEqual(b.indices)&&a.bins.SequenceEqual(b.bins)&&a.triangles.SequenceEqual(b.triangles);
    static void PaintPreparationChecks()
    {
        var random=new System.Random(23467);int checks=0;
        void Check(bool ok,string why){checks++;Require(ok,"paint preparation: "+why);}
        var layout=new LTPaintWeightMath.Terrain{countX=4,countZ=3,sizeX=32,sizeZ=24,width=8,depth=8};
        var sources=new LTPaintWeightTerrainCache.Source[12];
        LTPaintWeightTerrainCache.Source Source(int id,int revision=0)
        {
            var rect=new Rect(id%4*8,id/4*8,8,8);var vertices=new Vector3[9];var normals=new Vector3[9];
            for(int i=0;i<9;i++){vertices[i]=new Vector3(rect.xMin+i%3*4,revision*.1f+i*.02f,rect.yMin+i/3*4);normals[i]=new Vector3(.01f*i,1,.03f*revision).normalized;}
            var indices=new List<int>();
            for(int z=0;z<2;z++)for(int x=0;x<2;x++){int a=z*3+x;indices.AddRange(new[]{a,a+3,a+1,a+1,a+3,a+4});}
            if(id%3==0)indices.AddRange(new[]{0,3,1}); // equal-height duplicate winner order
            var bins=new List<int>[16];
            for(int b=0;b<bins.Length;b++)
            {if(b%7==0)continue;bins[b]=new List<int>();for(int j=0;j<indices.Count;j+=3)bins[b].Add(j);}
            return new LTPaintWeightTerrainCache.Source(rect,4,vertices,id%2==0?normals:Array.Empty<Vector3>(),indices.ToArray(),bins);
        }
        for(int i=0;i<sources.Length;i++)sources[i]=i%5==0?null:Source(i);
        LTPaintWeightTerrainCache.Source Lookup(int x,int z)=>sources[z*4+x];
        var cache=new LTPaintWeightTerrainCache();
        for(int f=0;f<80;f++)
        {
            int x=random.Next(4),z=random.Next(3);var range=new RectInt(x,z,random.Next(1,5-x),random.Next(1,4-z));
            var expected=OldPaintSnapshot(layout,range,Lookup);
            Check(cache.TryGet(layout,range,Lookup,out var packed,out _),"bounded fixture packs");
            Check(SamePaintSnapshot(expected,packed),"all arrays/offsets/holes/normals/order match previous packer");
            Check(cache.TryGet(layout,range,Lookup,out var repeated,out bool reused)&&reused&&ReferenceEquals(packed,repeated),"identical region reuses exact snapshot");
            for(int p=0;p<70;p++)
            {
                float px=(float)random.NextDouble()*32,pz=(float)random.NextDouble()*24;
                bool a=LTPaintWeightMath.Surface(layout,new LTPaintTerrainSnapshot.Access{value=expected},px,pz,out float ah,out float an);
                bool b=LTPaintWeightMath.Surface(layout,new LTPaintTerrainSnapshot.Access{value=packed},px,pz,out float bh,out float bn);
                Check(a==b&&ah.Equals(bh)&&an.Equals(bn),"packed surface sampling exactly unchanged");
            }
            Check(cache.Count<=4&&cache.RetainedBytes<=LTPaintWeightTerrainCache.PayloadLimit,"bounded entry count and bytes");
        }
        cache.Clear();var localRange=new RectInt(0,0,2,2);
        cache.TryGet(layout,localRange,Lookup,out var old,out _);var frozen=OldPaintSnapshot(layout,localRange,Lookup);
        sources[11]=Source(11,3);
        Check(cache.TryGet(layout,localRange,Lookup,out var same,out bool hit)&&hit&&ReferenceEquals(old,same),"distant edits do not invalidate local snapshot");
        sources[1]=Source(1,9);
        Check(cache.TryGet(layout,localRange,Lookup,out var changed,out hit)&&!hit&&!ReferenceEquals(changed,old),"new tile identity invalidates local snapshot");
        Check(SamePaintSnapshot(changed,OldPaintSnapshot(layout,localRange,Lookup))&&SamePaintSnapshot(old,frozen),"changed copy is correct; outstanding old reader stays immutable");
        sources[1]=null;cache.TryGet(layout,localRange,Lookup,out var missing,out hit);Check(!hit&&missing.tiles[1].valid==0,"deleted tile invalidates snapshot");
        sources[1]=Source(1);cache.TryGet(layout,localRange,Lookup,out var restored,out hit);Check(!hit&&restored.tiles[1].valid==1,"reinserted/Undo tile invalidates snapshot");
        var resized=layout;resized.sizeX=33;cache.TryGet(resized,localRange,Lookup,out _,out hit);Check(!hit,"layout/world size is part of exact key");
        cache.Clear();Check(cache.Count==0&&cache.RetainedBytes==0&&SamePaintSnapshot(old,frozen),"cache clear never mutates an outstanding snapshot");

        // Exercise the byte limit independently of the entry-count limit.
        var largeLayout=new LTPaintWeightMath.Terrain{countX=5,countZ=1,width=8,depth=8,sizeX=40,sizeZ=8};
        var large=new LTPaintWeightTerrainCache.Source[5];
        for(int i=0;i<5;i++)large[i]=new LTPaintWeightTerrainCache.Source(new Rect(i*8,0,8,8),1,new Vector3[200000],Array.Empty<Vector3>(),Array.Empty<int>(),new List<int>[1]);
        LTPaintWeightTerrainCache.Source Big(int x,int z)=>large[x];
        var one=new RectInt(0,0,1,1);long before=GC.GetAllocatedBytesForCurrentThread();
        var referenceLarge=OldPaintSnapshot(largeLayout,one,Big);long oldAllocated=GC.GetAllocatedBytesForCurrentThread()-before;
        before=GC.GetAllocatedBytesForCurrentThread();var cold=new LTPaintWeightTerrainCache();
        cold.TryGet(largeLayout,one,Big,out var packedLarge,out _);long packedAllocated=GC.GetAllocatedBytesForCurrentThread()-before;
        before=GC.GetAllocatedBytesForCurrentThread();cold.TryGet(largeLayout,one,Big,out _,out bool warmHit);
        long warmAllocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(SamePaintSnapshot(referenceLarge,packedLarge)&&packedAllocated<oldAllocated&&warmHit&&warmAllocated<4096,"exact cold packing reduces allocations; warm lookup does not recopy payload");
        Console.WriteLine($"Paint snapshot allocation fixture (200k vertices, managed only): old {oldAllocated} bytes, exact-sized cold {packedAllocated}, warm {warmAllocated}; not Unity frame timings.");
        cache.TryGet(largeLayout,new RectInt(0,0,1,1),Big,out var held,out _);
        for(int i=1;i<5;i++)cache.TryGet(largeLayout,new RectInt(i,0,1,1),Big,out _,out _);
        Check(cache.Count==3&&cache.RetainedBytes<=LTPaintWeightTerrainCache.PayloadLimit,"byte budget evicts before allocating next retained snapshot");
        cache.TryGet(largeLayout,new RectInt(0,0,1,1),Big,out var evicted,out hit);
        Check(!hit&&!ReferenceEquals(held,evicted)&&held.vertices.Length==200000,"LRU eviction leaves held snapshot valid");
        large[0]=new LTPaintWeightTerrainCache.Source(new Rect(0,0,8,8),1,new Vector3[700000],Array.Empty<Vector3>(),Array.Empty<int>(),new List<int>[1]);
        Check(!cache.TryGet(largeLayout,new RectInt(0,0,1,1),Big,out var rejected,out hit)&&rejected==null&&!hit,"oversized payload rejects before output allocation");
        Check(!cache.TryGet(largeLayout,new RectInt(-1,0,1,1),Big,out _,out _),"invalid ranges fail safely");
        var invalidLayout=largeLayout;invalidLayout.countX=invalidLayout.countZ=int.MaxValue;
        Check(!cache.TryGet(invalidLayout,new RectInt(0,0,1,1),Big,out _,out _),"metadata budget cannot overflow");
        Check(!cache.TryGet(largeLayout,new RectInt(1,0,int.MaxValue,1),Big,out _,out _),"range-end overflow rejected before allocation");

        // Compare preparation+kernel with the independent original equations.
        int pixels=0;
        for(int fixture=0;fixture<24;fixture++)
        {
            var f=fixture%2==0?LTPaintWeightFixture.Build(fixture):LTPaintWeightFixture.BuildRock(fixture);
            var prepared=f.PreparedInput();
            var preparedFixture=new LTPaintWeightFixture{input=prepared,roads=f.roads};
            for(int p=0;p<prepared.size*prepared.size;p++)
            {
                var actual=preparedFixture.Scalar(p);var reference=f.Reference(p);
                if(actual.guard!=0)actual=reference;
                Check(LTPaintWeightFixture.Same(actual,reference),"prepared terrain/rock weights retain exact final bytes");pixels++;
            }
        }
        var capture=new LTPaintCpuCapture();using(capture.BeginTick(true))
        {capture.TerrainSnapshotPrepared(true,false);capture.TerrainSnapshotPrepared(true,true);capture.TerrainSnapshotPrepared(false,false);}
        Check(capture.LastTick.terrainSnapshotBuilds==1&&capture.LastTick.terrainSnapshotReuses==1&&capture.LastTick.terrainSnapshotRejects==1,"snapshot metrics distinguish build/reuse/reject");
        const string root="Assets/TerrainSystem/LocalTerrain/";
        var runtime=File.ReadAllText(root+"LTPaintRuntime.cs");var prep=File.ReadAllText(root+"LTPaintWeightPreparation.cs");var work=File.ReadAllText(root+"LTPaintWeightWork.cs");
        Check(runtime.Contains("tile.snapshotSource=LTPaintWeightTerrainCache.BuildSource")&&runtime.Contains("snapshots.Clear()"),"runtime publishes immutable sources and clears owned cache");
        foreach(string stage in new[]{"WeightTerrainSnapshot","WeightInputSetup","WeightCoordinates","WeightRoadHits","WeightMasks"})Check(prep.Contains("Stage."+stage),"measure preparation stage "+stage);
        Check(work.Contains("Stage.WeightNativeCopy")&&work.Contains("Stage.WeightSchedule")&&work.Contains("new NativeArray<Vector3>(t.vertices,Allocator.Persistent)"),"native copies remain job-owned and measured separately from scheduling");
        Console.WriteLine($"PASS paint preparation: {checks} checks, {pixels} exact packed-input pixels; structural old/new parity, local invalidation, deletion/Undo/layout, LRU byte limits and immutable outstanding readers. Managed only; not a Unity benchmark.");
    }
}
