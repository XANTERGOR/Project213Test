using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    sealed class EmittedStamp
    {
        public Vector3[] positions,normals;
        public Vector2[] uv;
        public int[] indices;
        public readonly List<int> heightCalls=new List<int>(),cutCalls=new List<int>();
    }
    static EmittedStamp EmitStamp(bool reference,Rect rect,List<Vector3Int> plan,LTBalancedForest forest,
        int chunk,Func<float,float,bool> cut,Func<Rect,bool> diagonals,Func<Vector3Int,int,bool> midpoint=null,
        int budget=300000,bool trace=true,LTLODMesh.BuildProgress progress=null,LTStampMesh.EmitDiagnostics diagnostics=null,LTBalancedForest.StitchSnapshot stitches=null)
    {
        var result=new EmittedStamp();
        void Record(List<int> list,float x,float z)
        {if(trace){list.Add(BitConverter.SingleToInt32Bits(x));list.Add(BitConverter.SingleToInt32Bits(z));}}
        float Height(float x,float z)
        {Record(result.heightCalls,x,z);return .0013f*x*x+.031f*z+(float)Math.Sin(x*.37)*.13f;}
        bool Cut(float x,float z){Record(result.cutCalls,x,z);return cut(x,z);}
        var size=new Vector2(1024,768);progress??=new LTLODMesh.BuildProgress(false);
        if(reference)LTStampMeshReference.Emit(rect,size,budget,forest,chunk,plan,Height,cut==null?null:Cut,
            out result.positions,out result.normals,out result.uv,out result.indices,diagonals,progress,midpoint);
        else LTStampMesh.Emit(rect,size,budget,forest,chunk,plan,Height,cut==null?null:Cut,
            out result.positions,out result.normals,out result.uv,out result.indices,diagonals,progress,midpoint,diagnostics,stitches);
        return result;
    }
    static void StampEmitterChecks()
    {
        int cases=0;long comparedVertices=0;
        static bool Bits(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
        void CompareArrays(Vector3[] a,Vector3[] b,string label)
        {
            Require(a.Length==b.Length,label+" length");
            for(int i=0;i<a.Length;i++)Require(Bits(a[i].x,b[i].x)&&Bits(a[i].y,b[i].y)&&Bits(a[i].z,b[i].z),label+" bits");
        }
        void CompareUv(Vector2[] a,Vector2[] b)
        {
            Require(a.Length==b.Length,"emitter UV length");
            for(int i=0;i<a.Length;i++)Require(Bits(a[i].x,b[i].x)&&Bits(a[i].y,b[i].y),"emitter UV bits");
        }
        void Compare(EmittedStamp expected,EmittedStamp actual)
        {
            CompareArrays(expected.positions,actual.positions,"emitter position");
            CompareArrays(expected.normals,actual.normals,"emitter normal");CompareUv(expected.uv,actual.uv);
            Require(expected.indices.SequenceEqual(actual.indices),"emitter exact indices/order");
            Require(expected.heightCalls.SequenceEqual(actual.heightCalls),"emitter height call coordinates/order");
            Require(expected.cutCalls.SequenceEqual(actual.cutCalls),"emitter cut call coordinates/order");
            comparedVertices+=actual.positions.Length;cases++;
        }
        var rect=new Rect(-13.375f,23.125f,31.75f,47.875f);
        var cuts=new Func<float,float,bool>[] {null,(x,z)=>false,(x,z)=>true,
            (x,z)=>(x-2.3f)*(x-2.3f)+(z-44.1f)*(z-44.1f)<30,
            (x,z)=>x+z*.173f>10.513f,
            (x,z)=>x>1.113f&&x<5.731f&&z>40.03f&&z<56.87f};
        var diagonals=new Func<Rect,bool>[] {null,r=>true,r=>r.center.x+r.center.y*.27f>12};
        int sampleCases=0;
        void CheckSamples(Rect area,List<Vector3Int> leaves,LTBalancedForest forest,int chunk,Func<Vector3Int,int,bool> mid)
        {
            var old=LTHeightJobMath.FinePoints(area,leaves).Select(p=>(BitConverter.SingleToInt32Bits(p.x),BitConverter.SingleToInt32Bits(p.y))).ToArray();
            var next=LTHeightJobMath.EmitPoints(area,leaves,mid).Select(p=>(BitConverter.SingleToInt32Bits(p.x),BitConverter.SingleToInt32Bits(p.y))).ToArray();
            int cursor=0;
            foreach(var p in next){while(cursor<old.Length&&old[cursor]!=p)cursor++;Require(cursor<old.Length,"prefetch exact ordered subset of original coordinates");cursor++;}
            var required=new HashSet<(int,int)>(next);
            foreach(var diagonal in diagonals)
            {
                var mesh=EmitStamp(true,area,leaves,forest,chunk,null,diagonal,mid);
                for(int i=0;i<mesh.heightCalls.Count;i+=2)
                    Require(required.Contains((mesh.heightCalls[i],mesh.heightCalls[i+1])),"all emitted corner/midpoint/fan heights are prefetched");
                sampleCases++;
            }
        }
        // All 16 boundary masks, both diagonals, empty/full cuts, clipping
        // through fans and shared edges. A new builder must own its scratch.
        var single=new List<Vector3Int>{new Vector3Int(0,0,LTBalancedForest.N)};
        foreach(var cut in cuts)foreach(var diagonal in diagonals)for(int mask=0;mask<16;mask++)
        {
            int bits=mask;
            bool Midpoint(Vector3Int p,int side)=>(bits&(1<<side))!=0;
            Compare(EmitStamp(true,rect,single,null,0,cut,diagonal,Midpoint),EmitStamp(false,rect,single,null,0,cut,diagonal,Midpoint));
            if(cut==null&&diagonal==null)CheckSamples(rect,single,null,0,Midpoint);
        }
        foreach(bool irregular in new[]{false,true})
        {
            var raw=new Dictionary<int,List<Vector3Int>>{{0,LODFixture(16,irregular)},{1,LODFixture(8)}};
            var forest=new LTBalancedForest(2,1,300000,raw);forest.Balance(new LTLODMesh.BuildProgress(false));
            for(int id=0;id<2;id++)
            {
                int chunk=id;var r=new Rect(rect.x+chunk*rect.width,rect.y,rect.width,rect.height);
                CheckSamples(r,forest.Plan(chunk),forest,chunk,(p,side)=>forest.Midpoint(chunk,p,side));
            }
            foreach(var cut in cuts)foreach(var diagonal in diagonals)for(int chunk=0;chunk<2;chunk++)
            {
                var r=new Rect(rect.x+chunk*rect.width,rect.y,rect.width,rect.height);var fine=forest.Plan(chunk);
                Compare(EmitStamp(true,r,fine,forest,chunk,cut,diagonal),EmitStamp(false,r,fine,forest,chunk,cut,diagonal));
            }
            // Spatial variants repeatedly call Emit on the SAME builder: catches
            // stale reusable edge/clip lists and cut-intersection ordering changes.
            foreach(var cut in cuts)foreach(var diagonal in diagonals)
            {
                var fine=forest.Plan(0);var levels=new[]{fine,new LTLODMesh.Preparation(fine,rect,(x,z)=>0).Coarsen(2,1,null,null,1,false)};
                int Mask(Vector3Int p){int m=0;for(int side=0;side<4;side++)if(forest.Midpoint(0,p,side))m|=1<<side;return m;}
                var a=LTSpatialLODMath.BuildLayout(levels,1,300000,Mask,null);
                var b=LTSpatialLODMath.BuildLayout(levels,1,300000,Mask,null);
                float Height(float x,float z)=>.0013f*x*x+.031f*z;
                LTStampMeshReference.EmitSpatialVariants(a,rect,new Vector2(1024,768),300000,Height,cut,diagonal,new LTLODMesh.BuildProgress(false));
                LTStampMesh.EmitSpatialVariants(b,rect,new Vector2(1024,768),300000,Height,cut,diagonal,new LTLODMesh.BuildProgress(false));
                CompareArrays(a.vertices,b.vertices,"spatial positions");CompareArrays(a.normals,b.normals,"spatial normals");CompareUv(a.uv,b.uv);
                Require(a.baseIndices.SequenceEqual(b.baseIndices),"spatial exact LOD0 indices");
                Require(a.cells.Length==b.cells.Length,"spatial cell count");
                for(int c=0;c<a.cells.Length;c++)
                {
                    var av=a.cells[c].variants;var bv=b.cells[c].variants;
                    Require((av==null)==(bv==null),"spatial unused cell");if(av==null)continue;
                    Require(av.Length==bv.Length,"spatial variants count");
                    for(int v=0;v<av.Length;v++)Require(av[v].indices.SequenceEqual(bv[v].indices),"spatial exact variant indices");
                }
                comparedVertices+=b.vertices.Length;cases++;
            }
        }
        // Re-entrant callbacks construct a DIFFERENT emitter. No static scratch
        // may corrupt the suspended parent cell/polygon.
        foreach(bool reference in new[]{false,true})
        {
            bool entered=false;
            bool Reentrant(float x,float z)
            {
                if(!entered){entered=true;EmitStamp(reference,rect,single,null,0,(u,v)=>u>2,r=>true,(p,s)=>true);}
                return x>2;
            }
            Compare(EmitStamp(reference,rect,single,null,0,(x,z)=>x>2,r=>true,(p,s)=>true),
                EmitStamp(reference,rect,single,null,0,Reentrant,r=>true,(p,s)=>true));
        }
        string Failure(Action action)
        {try{action();return "no exception";}catch(Exception e){return e.GetType().Name+":"+e.Message;}}
        var grid=LODFixture(32);bool NoMidpoint(Vector3Int p,int side)=>false;
        var oldBudget=Failure(()=>EmitStamp(true,rect,grid,null,0,null,null,NoMidpoint,10));
        Require(oldBudget.StartsWith("InvalidOperationException:"),"reference budget guard exercised");
        Require(oldBudget==Failure(()=>EmitStamp(false,rect,grid,null,0,null,null,NoMidpoint,10)),"unchanged budget guard");
        LTLODMesh.BuildProgress Cancel(){double now=0;return new LTLODMesh.BuildProgress(()=>now+=2,()=>true);}
        var oldCancel=Failure(()=>EmitStamp(true,rect,grid,null,0,null,null,NoMidpoint,progress:Cancel()));
        Require(oldCancel.StartsWith("OperationCanceledException:"),"reference cancel exercised");
        Require(oldCancel==Failure(()=>EmitStamp(false,rect,grid,null,0,null,null,NoMidpoint,progress:Cancel())),"unchanged cancellation");
        var editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        int prep=editor.IndexOf("terrainTimings.Lap(\"Terrain: emit height preparation (CPU)\")",StringComparison.Ordinal);
        Require(prep>=0&&editor.IndexOf("long emitStart=",prep,StringComparison.Ordinal)>prep,"height preparation has separate CPU timing before emitter");
        Require(editor.Contains("LTHeightJobMath.EmitPoints(r,stitches)")&&editor.Contains("stitches:stitches"),"LOD0 height prefetch and emitter share the actual forest stitch snapshot");
        Require(editor.Contains("LTHeightJobMath.FinePoints(r,fine)"),"coarse LOD height stencil remains complete");
        Console.WriteLine($"PASS stamp emitter: {cases} exact legacy comparisons, {comparedVertices} vertices; all stitch masks, clipping, diagonals, spatial variants, call order, re-entrancy, budget and cancellation.");

        // Timing is descriptive only; allocation is deterministic enough to catch
        // reintroducing per-cell/per-triangle arrays. This is NOT a Unity benchmark.
        var dense=LODFixture(128);var denseRect=new Rect(96,32,32,32);
        int oldVisits=LTHeightJobMath.FinePoints(denseRect,dense).Count(),newVisits=LTHeightJobMath.EmitPoints(denseRect,dense,NoMidpoint).Count();
        int oldUnique=LTHeightJobMath.FinePoints(denseRect,dense).Distinct().Count(),newUnique=LTHeightJobMath.EmitPoints(denseRect,dense,NoMidpoint).Distinct().Count();
        Require(oldVisits==147456&&newVisits==65536&&oldUnique==66049&&newUnique==16641,"regular grid requests only needed corner heights");
        Console.WriteLine($"PASS LOD0 prefetch: {sampleCases} coordinate/coverage comparisons; 128x128 candidates {oldVisits} -> {newVisits}, unique heights {oldUnique} -> {newUnique}. Coarse LOD stencil unchanged.");
        EmittedStamp Run(bool reference)=>EmitStamp(reference,denseRect,dense,null,0,(x,z)=>false,null,NoMidpoint,trace:false);
        for(int warm=0;warm<3;warm++){Run(true);Run(false);}
        var oldTimes=new List<double>();var newTimes=new List<double>();long oldBytes=0,newBytes=0;
        for(int run=0;run<8;run++)for(int step=0;step<2;step++)
        {
            bool reference=((run+step)&1)==0;long start=GC.GetAllocatedBytesForCurrentThread();
            var timer=Stopwatch.StartNew();var mesh=Run(reference);timer.Stop();
            long bytes=GC.GetAllocatedBytesForCurrentThread()-start;
            if(reference){oldBytes+=bytes;oldTimes.Add(timer.Elapsed.TotalMilliseconds);}else{newBytes+=bytes;newTimes.Add(timer.Elapsed.TotalMilliseconds);}
            Require(mesh.positions.Length==129*129,"dense fixture shape");
        }
        oldTimes.Sort();newTimes.Sort();Require(newBytes<oldBytes*.85,"emitter allocations reduced by at least 15% in dense cut-enabled fixture");
        Console.WriteLine($"Emitter synthetic 128x128: median old {oldTimes[4]:F2} ms / new {newTimes[4]:F2} ms; managed bytes per build {oldBytes/8} -> {newBytes/8}. Same ordered output; outside Unity, no height Jobs.");
    }
}
