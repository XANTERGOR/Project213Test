using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;
// Numeric enum only; production LTHeightStamp is a scene component.
namespace LocalTerrainPrototype
{public enum LTStampOperation {Override=1,Max=2,Min=3,Add=0,Subtract=4,Multiply=5,Average=6,Difference=7,SqrtMultiply=8,Blend=9}}
partial class Checks
{
    static void HeightJobChecks()
    {
        static bool Bits(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
        var random=new System.Random(836199);int comparisons=0;
        var samples=new List<LTRoadMath.Sample>();var nodes=new List<LTRoadHeightKernel.Node>();
        var sourceRoads=new List<LTRoadMath.Snapshot>();var roads=new List<LTRoadHeightKernel.Road>();
        for(int variant=0;variant<24;variant++)
        {
            var s=LTRoadMath.Settings.Default;s.sampleSpacing=.6f;s.width=6;s.shoulderWidth=variant%3*.4f;s.blendWidth=variant%4;
            s.mode=variant%4==0?LTRoadMode.Asphalt:LTRoadMode.Offroad;s.pattern=variant%2==0?LTRoadPattern.Tracks:LTRoadPattern.Solid;
            s.flatten=variant%5*.25f;s.variation.enabled=variant%3!=0;s.variation.solidRuts=true;s.variation.strength=.7f;
            s.straightStart=variant%3==0;s.straightEnd=variant%4==0;s.junctionStartLength=4;s.junctionEndLength=7;
            s.edgeNoise=variant%4*.2f;s.seed=-47391+variant;s.variation.seed=731+variant;
            var path=new[]{new LTRoadPoint(new Vector3(-30,-1,-20),12),new LTRoadPoint(new Vector3(-9,4,10),-20),
                new LTRoadPoint(new Vector3(10,2,15),35),new LTRoadPoint(new Vector3(36,5,4),-5)};
            var road=LTRoadMath.Build(path,Matrix4x4.identity,s);sourceRoads.Add(road);roads.Add(road.CopyHeightData(samples,nodes));
        }
        var sb=new LTHeightJobMath.Buffer<LTRoadMath.Sample>{values=samples.ToArray()};
        var nb=new LTHeightJobMath.Buffer<LTRoadHeightKernel.Node>{values=nodes.ToArray()};
        // Exact native-check fixture, including the point reported by the live
        // Editor (fixture 0, grid sample 147). Isolate kernel vs Burst rounding.
        int ambiguousQueries=0,fixtureQueries=0;
        for(int fixture=0;fixture<12;fixture++)
        {
            var settings=LTRoadMath.Settings.Default;settings.mode=fixture%4==0?LTRoadMode.Asphalt:LTRoadMode.Offroad;
            settings.pattern=fixture%2==0?LTRoadPattern.Tracks:LTRoadPattern.Solid;
            settings.variation.enabled=fixture%3!=0;settings.straightStart=fixture%3==0;settings.straightEnd=fixture%4==0;
            settings.junctionStartLength=3;settings.junctionEndLength=5;settings.flatten=fixture%5*.25f;
            var road=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(1,2,1),-15),new LTRoadPoint(new Vector3(12,4,17),25),
                new LTRoadPoint(new Vector3(29,3,29),-8)},Matrix4x4.identity,settings);
            var rs=new List<LTRoadMath.Sample>();var ns=new List<LTRoadHeightKernel.Node>();var r=road.CopyHeightData(rs,ns);
            var s=new LTHeightJobMath.Buffer<LTRoadMath.Sample>{values=rs.ToArray()};var n=new LTHeightJobMath.Buffer<LTRoadHeightKernel.Node>{values=ns.ToArray()};
            int pointIndex=0;
            foreach(var p in LTHeightJobMath.BasePoints(new Rect(0,0,32,32),64))
            {
                float expected=road.ApplyHeight(p.x,p.y,3),actual=LTRoadHeightKernel.ApplyDetailed(r,s,n,p.x,p.y,3,out var trace);
                if(trace.ambiguous!=0)ambiguousQueries++;fixtureQueries++;
                Require(Bits(expected,actual),$"native-check managed fixture {fixture}, sample {pointIndex}, expected {expected:R}, actual {actual:R}");
                pointIndex++;
            }
            if(fixture==0||fixture==9)
            {
                var p=fixture==0?new Vector2(14,9):new Vector2(31,25);
                LTRoadHeightKernel.ApplyDetailed(r,s,n,p.x,p.y,3,out var trace);
                Require(trace.ambiguous!=0,"reported native adjacent-segment ambiguity requests legacy height evaluation");
            }
        }
        Require(ambiguousQueries>0&&ambiguousQueries<fixtureQueries/10,"ambiguity guard does not move the whole managed fixture back to CPU");
        var straight=LTRoadMath.Build(new[]{new LTRoadPoint(Vector3.zero),new LTRoadPoint(new Vector3(32,0,0))},Matrix4x4.identity,LTRoadMath.Settings.Default);
        var straightSamples=new List<LTRoadMath.Sample>();var straightNodes=new List<LTRoadHeightKernel.Node>();var straightRoad=straight.CopyHeightData(straightSamples,straightNodes);
        var straightSB=new LTHeightJobMath.Buffer<LTRoadMath.Sample>{values=straightSamples.ToArray()};var straightNB=new LTHeightJobMath.Buffer<LTRoadHeightKernel.Node>{values=straightNodes.ToArray()};
        int middle=straightSamples.Count/2;float endX=straightSamples[middle].position.x;
        float interiorX=(endX+straightSamples[middle+1].position.x)*.5f;
        LTRoadHeightKernel.ApplyDetailed(straightRoad,straightSB,straightNB,endX,2,3,out var sharedEnd);
        LTRoadHeightKernel.ApplyDetailed(straightRoad,straightSB,straightNB,interiorX,2,3,out var interior);
        Require(sharedEnd.ambiguous==0&&interior.ambiguous==0,$"identical shared endpoints and ordinary projections remain on worker path: end {sharedEnd.ambiguous}, t={sharedEnd.fraction:R}; interior {interior.ambiguous}, t={interior.fraction:R}");
        Console.WriteLine($"Height ambiguity guard: {ambiguousQueries}/{fixtureQueries} managed fixture queries flagged; known native mismatch points covered. Not a live-scene ratio.");
        for(int variant=0;variant<roads.Count;variant++)
        {
            for(int i=0;i<6000;i++)
            {
                float x=(float)(random.NextDouble()*86-43),z=(float)(random.NextDouble()*66-30),original=(float)(random.NextDouble()*20-5);
                float expected=sourceRoads[variant].ApplyHeight(x,z,original),actual=LTRoadHeightKernel.Apply(roads[variant],sb,nb,x,z,original);
                Require(Bits(expected,actual),$"road height worker scalar parity variant {variant}, ({x:R},{z:R}): {expected:R} != {actual:R}");comparisons++;
            }
            foreach(var p in sourceRoads[variant].samples)
            foreach(float lateral in new[]{0f,-3,3,-4,4,-7,7})
            {
                float x=p.position.x+p.right.x*lateral,z=p.position.z+p.right.z*lateral;
                Require(Bits(sourceRoads[variant].ApplyHeight(x,z,2),LTRoadHeightKernel.Apply(roads[variant],sb,nb,x,z,2)),"endpoints, sides and tie planes exact");comparisons++;
            }
        }
        var values=Enumerable.Range(0,65*65).Select(i=>(float)Math.Sin(i*.13)*20).ToArray();
        var source=new LTHeightSampling.Source(values,65,new Vector3(112.13f,100,87.76f));
        int patches=0;
        foreach(var rect in new[]{new Rect(0,0,112.13f,87.76f),new Rect(13.6f,19.2f,12.8f,14.72f),new Rect(-8,-2,17,20),new Rect(107,84,20,20)})
        {
            var patch=source.CopyPatch(rect,out var info);var buffer=new LTHeightJobMath.Buffer<float>{values=patch};
            foreach(var point in LTHeightJobMath.BasePoints(rect,32))
            {Require(Bits(source.Sample(point.x,point.y),info.Sample(buffer,point.x,point.y)),"cropped source interpolation exact");patches++;}
        }
        var rb=new LTHeightJobMath.Buffer<LTRoadHeightKernel.Road>{values=roads.ToArray()};
        foreach(LTStampOperation op in Enum.GetValues(typeof(LTStampOperation)))for(int i=0;i<100;i++)
        {
            float height=(float)(random.NextDouble()*10-5),target=(float)(random.NextDouble()*10-5),weight=(float)random.NextDouble();
            var command=new LTHeightJobMath.Command{target=target,weight=weight,operation=op,blend=.31f,reference=2};
            Require(Bits(LTBlend.Apply(height,target,weight,op,.31f,2),LTHeightJobMath.Apply(height,new Vector2(0,0),command,rb,sb,nb)),"stamp operand programme exact");
        }
        // Exercise mixed command order and every worker command kind, rather
        // than only calling the standalone road kernel.
        var programme=new[]{new LTHeightJobMath.Command{target=1,weight=.8f,operation=LTStampOperation.Add},
            new LTHeightJobMath.Command{kind=1,road=2},new LTHeightJobMath.Command{kind=2,target=7,weight=.3f},
            new LTHeightJobMath.Command{kind=3,target=2,weight=.2f},new LTHeightJobMath.Command{kind=1,road=5}};
        var request=new LTHeightJobMath.Point{position=new Vector2(4,12),first=0,count=programme.Length};
        float composed=3;
        for(int i=request.first;i<request.first+request.count;i++)composed=LTHeightJobMath.Apply(composed,request.position,programme[i],rb,sb,nb);
        float reference=LTBlend.Apply(3,1,.8f,LTStampOperation.Add,0,0);
        reference=sourceRoads[2].ApplyHeight(4,12,reference);reference=Mathf.Lerp(reference,Mathf.Max(reference,7),.3f);
        reference=Mathf.Lerp(reference,2,.2f);reference=sourceRoads[5].ApplyHeight(4,12,reference);
        Require(Bits(reference,composed),"road/stamp/rock/junction order is preserved");
        var cache=new LTHeightSampling.Cache(4,2,4);var old=cache.Bind(0,(x,z)=>-1);
        old.Accept(0,0,8);Require(old.Sample(0,0)==8&&cache.FromJobs==1,"worker heights bypass evaluator, populate shared cache");
        cache.Invalidate(0);bool stale=false;try{old.Accept(0,0,9);}catch(InvalidOperationException){stale=true;}
        Require(stale&&cache.Count==0,"late worker result cannot repopulate a changed chunk");
        var current=cache.Bind(0,(x,z)=>-1);cache.Invalidate(1);current.Accept(0,0,11);
        Require(current.Sample(0,0)==11,"unrelated chunk edit does not reject current worker result");
        cache.Clear();stale=false;try{current.Accept(0,0,12);}catch(InvalidOperationException){stale=true;}
        Require(stale&&cache.Count==0,"source/global reset rejects pending worker acceptance");
        var dense=new LTHeightSampling.Cache();var denseBinding=dense.Bind(0,(x,z)=>-1);
        for(int i=0;i<150000;i++)denseBinding.Accept(i,0,i);
        Require(dense.Count==150000&&dense.Bypassed==0&&dense.Count<=dense.Limit,"dense chunk can borrow budget without raising total cap");
        var dirty=new HashSet<int>{0};var staged=new LTStagedBuild();bool publish=false;
        IEnumerable<LTBuildStep> Wait(){yield return LTBuildStep.Waiting;publish=true;yield return LTBuildStep.LOD0Ready;}
        staged.Begin(1,dirty,_=>Wait().GetEnumerator());staged.Advance(1,dirty);Require(staged.Waiting&&!publish,"LOD0 job wait is visible to scheduler");
        staged.Advance(2,dirty);Require(!staged.Active&&!staged.Waiting&&!publish,"new edit cancels before publication");
        Console.WriteLine($"PASS height-job numeric kernels: {comparisons} exact road comparisons, {patches} cropped-source samples, all blend modes, stale cache acceptance and LOD0 wait cancellation. Managed, not native Burst execution.");
        HeightJobPipelineChecks(sourceRoads[2],roads[2],sb,nb);
        var editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        Require(editor.Contains("if(staged&&w.adaptive)foreach(var step in PrefetchHeights")&&editor.Contains("if(staged)foreach(var step in PrefetchHeights")&&editor.Contains("LTHeightJobMath.FinePoints(r,fine)"),"height jobs wired into planning, LOD0 emission and deferred LOD");
        Require(editor.Contains("!state.build.Waiting")&&editor.Contains("LTHeightJobWork.CollectRetired();")&&editor.Contains("LTHeightJobWork.Drain();"),"base waiting and owned native cleanup");
        var job=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTHeightJobWork.cs");
        Require(job.Contains("struct SampleJob:IJobParallelFor")&&job.Contains("FloatMode.Strict")&&job.Contains("FloatPrecision.High")&&job.Contains("Schedule(requests.Count,64)"),"strict high-precision native parallel job is actually scheduled");
        Require(job.Contains("retired=true;if(IsCompleted)Release()")&&job.Contains("Capacity=2")&&!job.Contains("Allocator.TempJob"),"bounded persistent jobs, nonblocking retirement");
        Require(job.Contains("status[index]=float.IsNaN(h)")&&job.Contains("(status[i]&~7)!=0")&&job.Contains("(status[i]&3)!=1&&(status[i]&3)!=2"),"per-element success validation prevents partial publication");
        Require(job.Contains("needsReference?4:0")&&editor.Contains("if(job.NeedsReference(i)){fallback(p.x,p.y);"),"ambiguous worker selection uses existing version-guarded evaluator, not a relaxed comparison");
    }
    static void HeightJobPipelineChecks(LTRoadMath.Snapshot road,LTRoadHeightKernel.Road native,
        LTHeightJobMath.Buffer<LTRoadMath.Sample> samples,LTHeightJobMath.Buffer<LTRoadHeightKernel.Node> nodes)
    {
        var rect=new Rect(-20,-12,48,40);const int budget=180000;var zones=new List<LTStampMesh.Zone>{new LTStampMesh.Zone
            {bounds=road.bounds,cellSize=.5f,customCellSize=(x,z)=>.5f,coverageIntersects=road.Intersects}};
        float Raw(float x,float z)=>road.ApplyHeight(x,z,.012f*x+.008f*z);
        var cache=new LTHeightSampling.Cache();int fallback=0;var binding=cache.Bind(0,(x,z)=>{fallback++;return Raw(x,z);});
        foreach(var p in LTHeightJobMath.BasePoints(rect,32))binding.Accept(p.x,p.y,LTRoadHeightKernel.Apply(native,samples,nodes,p.x,p.y,.012f*p.x+.008f*p.y));
        var fine=LTStampMesh.Plan(rect,32,true,.02f,budget,zones,binding.Sample);
        Require(fallback==0&&fine.SequenceEqual(LTStampMesh.Plan(rect,32,true,.02f,budget,zones,Raw)),"prefetch base lattice covers every planner height without changing plan");
        var forest=new LTBalancedForest(1,1,budget,new Dictionary<int,List<Vector3Int>>{{0,fine}});forest.Balance();fine=forest.Plan(0);
        foreach(var p in LTHeightJobMath.FinePoints(rect,fine))if(!binding.Contains(p.x,p.y))binding.Accept(p.x,p.y,LTRoadHeightKernel.Apply(native,samples,nodes,p.x,p.y,.012f*p.x+.008f*p.y));
        LTStampMesh.Emit(rect,new Vector2(48,40),budget,forest,0,fine,binding.Sample,null,out var v,out var n,out var uv,out var t);
        Require(fallback==0,"prefetch fine lattice covers uncut LOD0 emitter");
        LTStampMesh.Emit(rect,new Vector2(48,40),budget,forest,0,fine,Raw,null,out var expectedV,out var expectedN,out var expectedUV,out var expectedT);
        Require(v.SequenceEqual(expectedV)&&n.SequenceEqual(expectedN)&&uv.SequenceEqual(expectedUV)&&t.SequenceEqual(expectedT),"prefetched mesh streams exact parity");
        var levels=Enumerable.Range(1,4).Select(l=>new LTLODJobMath.Level{steps=l,tolerance=l*.2f}).ToArray();
        var jobInput=new LTLODJobInput();foreach(var _ in jobInput.Prepare(fine,rect,levels,binding.Sample,null,null,4,false)){}
        Require(fallback==0,"fine lattice covers all coarse hierarchy height requests");
        var output=SolveJobInput(jobInput);
        for(int i=0;i<4;i++)Require(output[i].SequenceEqual(LTLODMesh.Coarsen(fine,rect,levels[i].steps,levels[i].tolerance,Raw,null,null,4,false)),"prefetched LOD exact parity");
        Console.WriteLine("PASS height-job pipeline: zero synchronous height fallbacks in planning/uncut LOD0/coarse input; exact plans and mesh streams. Managed worker simulation only.");
    }
}
