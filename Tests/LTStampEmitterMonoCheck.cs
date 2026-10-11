// Standalone numeric check with Unity's Mono runtime, not a Unity project or
// a scene benchmark. References separately built C# assemblies, never opens Unity.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

static class LTStampEmitterMonoCheck
{
    sealed class Result
    {public Vector3[] positions,normals;public Vector2[] uv;public int[] indices;}
    static Result Emit(bool reference,List<Vector3Int> plan,int mask,int cutMode,bool regular,LTStampMesh.EmitDiagnostics diagnostics=null,
        LTBalancedForest forest=null,int chunk=0,LTBalancedForest.StitchSnapshot stitches=null)
    {
        var result=new Result();var rect=new Rect(96,32,32,32);
        float Height(float x,float z)=>.013f*x+(float)Math.Sin(z*.17)*.21f;
        bool Cut(float x,float z)=>cutMode==1||cutMode==2&&(x-110)*(x-110)+(z-48)*(z-48)<7.31f;
        bool Mid(Vector3Int p,int side)=>forest!=null?forest.Midpoint(chunk,p,side):(mask&(1<<side))!=0;
        if(reference)LTStampMeshReference.Emit(rect,new Vector2(1024,1024),300000,null,0,plan,Height,Cut,
            out result.positions,out result.normals,out result.uv,out result.indices,r=>regular,new LTLODMesh.BuildProgress(false),Mid);
        else LTStampMesh.Emit(rect,new Vector2(1024,1024),300000,forest,chunk,plan,Height,Cut,
            out result.positions,out result.normals,out result.uv,out result.indices,r=>regular,new LTLODMesh.BuildProgress(false),stitches==null?(Func<Vector3Int,int,bool>)Mid:null,diagnostics,stitches);
        return result;
    }
    static bool Bits(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
    static void Compare(Result a,Result b)
    {
        if(a.positions.Length!=b.positions.Length||!a.indices.SequenceEqual(b.indices))throw new Exception("Mono emitter topology mismatch");
        for(int i=0;i<a.positions.Length;i++)
        {
            for(int axis=0;axis<3;axis++)
                if(!Bits(a.positions[i][axis],b.positions[i][axis])||!Bits(a.normals[i][axis],b.normals[i][axis]))throw new Exception("Mono emitter vertex/normal mismatch");
            if(!Bits(a.uv[i].x,b.uv[i].x)||!Bits(a.uv[i].y,b.uv[i].y))throw new Exception("Mono emitter UV mismatch");
        }
    }
    static List<Vector3Int> Grid(int side)
    {
        var plan=new List<Vector3Int>();int step=LTBalancedForest.N/side;
        for(int z=0;z<side;z++)for(int x=0;x<side;x++)plan.Add(new Vector3Int(x*step,z*step,step));
        return plan;
    }
    static int Main()
    {
        CheckStitchSnapshots();
        int comparisons=0;
        foreach(int side in new[]{1,8})foreach(bool regular in new[]{false,true})for(int cut=0;cut<3;cut++)for(int mask=0;mask<16;mask++)
        {
            var plan=Grid(side);var d=new LTStampMesh.EmitDiagnostics();
            Compare(Emit(true,plan,mask,cut,regular),Emit(false,plan,mask,cut,regular,d));
            long sum=0;foreach(LTStampMesh.EmitDiagnostics.Part part in Enum.GetValues(typeof(LTStampMesh.EmitDiagnostics.Part)))sum+=d.SampledTicks(part);
            if(!d.Completed||sum!=d.SampleTicks||d.Cells!=plan.Count)throw new Exception("Mono diagnostic accounting");
            comparisons++;
        }
        Console.WriteLine("PASS Mono emitter: "+comparisons+" exact ordered meshes/attributes, clipping/stitches/diagonals.");
        var dense=Grid(128);Compare(Emit(true,dense,0,0,false),Emit(false,dense,0,0,false));
        for(int i=0;i<3;i++){Emit(true,dense,0,0,false);Emit(false,dense,0,0,false);}
        var oldTimes=new List<double>();var newTimes=new List<double>();long oldBytes=0,newBytes=0;
        for(int i=0;i<8;i++)for(int j=0;j<2;j++)
        {
            bool reference=((i+j)&1)==0;long before=GC.GetAllocatedBytesForCurrentThread();var timer=Stopwatch.StartNew();
            var mesh=Emit(reference,dense,0,0,false);timer.Stop();long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
            if(reference){oldTimes.Add(timer.Elapsed.TotalMilliseconds);oldBytes+=bytes;}else{newTimes.Add(timer.Elapsed.TotalMilliseconds);newBytes+=bytes;}
            if(mesh.positions.Length!=129*129)throw new Exception("Mono fixture vertex count");
        }
        oldTimes.Sort();newTimes.Sort();
        Console.WriteLine("Mono synthetic 128x128 median: old "+oldTimes[4].ToString("F2")+" ms / new "+newTimes[4].ToString("F2")+
            " ms; bytes per build "+oldBytes/8+" -> "+newBytes/8+". Outside Unity Editor, no Jobs/Burst, not a scene benchmark.");
        var off=new List<double>();var on=new List<double>();
        for(int i=0;i<4;i++){Emit(false,dense,0,0,false);Emit(false,dense,0,0,false,new LTStampMesh.EmitDiagnostics());}
        for(int i=0;i<12;i++)for(int j=0;j<2;j++)
        {
            bool enabled=((i+j)&1)==0;var d=enabled?new LTStampMesh.EmitDiagnostics():null;
            var timer=Stopwatch.StartNew();Emit(false,dense,0,0,false,d);timer.Stop();
            (enabled?on:off).Add(timer.Elapsed.TotalMilliseconds);
        }
        off.Sort();on.Sort();
        Console.WriteLine("Mono diagnostics off "+off[6].ToString("F2")+" ms / sampled on "+on[6].ToString("F2")+" ms; not live-scene timing.");
        var detail=new LTStampMesh.EmitDiagnostics();Emit(false,dense,0,0,false,detail);Console.WriteLine(detail.Report("synthetic 128x128"));
        return 0;
    }
    static LTBalancedForest.StitchSnapshot Capture(LTBalancedForest forest,int chunk,List<Vector3Int> plan)
    {
        LTBalancedForest.StitchSnapshot result=null;
        foreach(int _ in forest.StitchSnapshotSteps(chunk,plan,s=>result=s)){}
        return result;
    }
    static void CheckStitchSnapshots()
    {
        int comparisons=0;
        for(int mask=0;mask<16;mask++)
        {
            var raw=new Dictionary<int,List<Vector3Int>>();for(int id=0;id<9;id++)raw[id]=Grid(1);
            int[] neighbours={3,5,1,7};for(int side=0;side<4;side++)if((mask&(1<<side))!=0)raw[neighbours[side]]=Grid(2);
            var forest=new LTBalancedForest(3,3,300000,raw);forest.Balance(new LTLODMesh.BuildProgress(false));
            var plan=forest.Plan(4);var snapshot=Capture(forest,4,plan);
            if(snapshot.Mask(0)!=mask)throw new Exception("Mono stitch mask mismatch");
            foreach(bool regular in new[]{false,true})for(int cut=0;cut<3;cut++)
            {Compare(Emit(true,plan,mask,cut,regular,forest:forest,chunk:4),Emit(false,plan,mask,cut,regular,forest:forest,chunk:4,stitches:snapshot));comparisons++;}
        }
        Console.WriteLine("PASS Mono shared stitch snapshot: "+comparisons+" exact legacy comparisons across all real masks, cuts and diagonal modes.");
        // Bind the internal numeric iterator once; timing does not include repeated
        // reflection or a copied approximation of production prefetch behaviour.
        var math=typeof(LTStampMesh).Assembly.GetType("LocalTerrainPrototype.LTHeightJobMath",true);
        var oldPoints=(Func<Rect,List<Vector3Int>,Func<Vector3Int,int,bool>,LTBalancedForest.StitchSnapshot,IEnumerable<Vector2>>)Delegate.CreateDelegate(
            typeof(Func<Rect,List<Vector3Int>,Func<Vector3Int,int,bool>,LTBalancedForest.StitchSnapshot,IEnumerable<Vector2>>),
            math.GetMethod("EmitPoints",new[]{typeof(Rect),typeof(List<Vector3Int>),typeof(Func<Vector3Int,int,bool>),typeof(LTBalancedForest.StitchSnapshot)}));
        var newPoints=(Func<Rect,LTBalancedForest.StitchSnapshot,IEnumerable<Vector2>>)Delegate.CreateDelegate(
            typeof(Func<Rect,LTBalancedForest.StitchSnapshot,IEnumerable<Vector2>>),math.GetMethod("EmitPoints",new[]{typeof(Rect),typeof(LTBalancedForest.StitchSnapshot)}));
        var dense=Grid(128);var denseForest=new LTBalancedForest(1,1,300000,new Dictionary<int,List<Vector3Int>>{{0,dense}});
        denseForest.Balance(new LTLODMesh.BuildProgress(false));var rect=new Rect(96,32,32,32);
        Func<Vector3Int,int,bool> mid=(p,s)=>denseForest.Midpoint(0,p,s);
        var ready=Capture(denseForest,0,dense);
        var expected=oldPoints(rect,dense,mid,null).ToArray();var actual=newPoints(rect,ready).ToArray();
        if(expected.Length!=actual.Length)throw new Exception("Mono prefetch point count");
        for(int i=0;i<expected.Length;i++)if(!Bits(expected[i].x,actual[i].x)||!Bits(expected[i].y,actual[i].y))throw new Exception("Mono prefetch coordinates/order");
        void Run(bool shared)
        {
            var snapshot=shared?Capture(denseForest,0,dense):null;int visited=0;
            foreach(var point in shared?newPoints(rect,snapshot):oldPoints(rect,dense,mid,null))visited++;
            if(visited!=65536)throw new Exception("Mono benchmark point count");
            Emit(false,dense,0,0,false,forest:denseForest,stitches:snapshot);
        }
        for(int i=0;i<4;i++){Run(false);Run(true);}
        var before=new List<double>();var after=new List<double>();
        for(int i=0;i<12;i++)for(int j=0;j<2;j++)
        {bool shared=((i+j)&1)==0;var timer=Stopwatch.StartNew();Run(shared);timer.Stop();(shared?after:before).Add(timer.Elapsed.TotalMilliseconds);}
        before.Sort();after.Sort();
        Console.WriteLine("Mono shared stitches synthetic 128x128: capture + prefetch iteration + emission old "+before[6].ToString("F2")+" ms / shared "+after[6].ToString("F2")+" ms; outside Unity Editor, no Jobs/Burst or live-scene performance claim.");
    }
}
