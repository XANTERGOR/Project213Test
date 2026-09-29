using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
namespace LocalTerrainPrototype
{
    // Opt-in native verification only. No scene lookup, asset writes or saves.
    [InitializeOnLoad]
    internal static class LTHeightJobCheck
    {
        [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High,CompileSynchronously=true)]
        struct TraceJob:IJob
        {
            public LTRoadHeightKernel.Road road;public float x,z,height;
            [ReadOnly] public NativeArray<LTRoadMath.Sample> samples;
            [ReadOnly] public NativeArray<LTRoadHeightKernel.Node> nodes;
            [WriteOnly] public NativeArray<LTRoadHeightKernel.Trace> output;
            [BurstDiscard] static void MarkManaged(ref int value){value=2;}
            public void Execute()
            {
                LTRoadHeightKernel.ApplyDetailed(road,new LTHeightJobWork.NativeBuffer<LTRoadMath.Sample>{values=samples},
                    new LTHeightJobWork.NativeBuffer<LTRoadHeightKernel.Node>{values=nodes},x,z,height,out var trace);
                int backend=1;MarkManaged(ref backend);trace.backend=backend;output[0]=trace;
            }
        }
        static IEnumerable<LTBuildStep> TracePoint(int fixture,LTHeightJobWork.Input input,Vector2 p,float height)
        {
            var sb=new LTHeightJobMath.Buffer<LTRoadMath.Sample>{values=input.samples};var nb=new LTHeightJobMath.Buffer<LTRoadHeightKernel.Node>{values=input.nodes};
            LTRoadHeightKernel.ApplyDetailed(input.roads[0],sb,nb,p.x,p.y,height,out var expected);
            using(var samples=new NativeArray<LTRoadMath.Sample>(input.samples,Allocator.Persistent))
            using(var nodes=new NativeArray<LTRoadHeightKernel.Node>(input.nodes,Allocator.Persistent))
            using(var output=new NativeArray<LTRoadHeightKernel.Trace>(1,Allocator.Persistent))
            {
                var handle=new TraceJob{road=input.roads[0],x=p.x,z=p.y,height=height,samples=samples,nodes=nodes,output=output}.Schedule();
                try
                {
                    JobHandle.ScheduleBatchedJobs();yield return LTBuildStep.Waiting;
                    while(!handle.IsCompleted)yield return LTBuildStep.Waiting;handle.Complete();
                    string Describe(LTRoadHeightKernel.Trace t)=>$"segment={t.segment}, t={t.fraction:R}, d2={t.squaredDistance:R}, rightLength={t.rightLength:R}, "+
                        $"position=({t.hit.position.x:R},{t.hit.position.y:R},{t.hit.position.z:R}), lateral={t.hit.lateral:R}, arc={t.hit.distance:R}, bank={t.hit.bank:R}, "+
                        $"halfWidth={t.halfWidth:R}, rutFade={t.rutFade:R}, weight={t.weight:R}, target={t.target:R}, result={t.result:R}";
                    Debug.LogWarning($"Height Jobs trace fixture {fixture}, x={p.x:R}, z={p.y:R}, input={height:R}, probe Burst={output[0].backend==1}. Managed: {Describe(expected)}. Native probe: {Describe(output[0])}.");
                }
                finally{handle.Complete();} // stop/reload owns and completes this tiny diagnostic job
            }
        }
        static IEnumerator<LTBuildStep> runner;
        static LTHeightJobCheck()
        {AssemblyReloadEvents.beforeAssemblyReload+=Stop;EditorApplication.quitting+=Stop;}
        [MenuItem("Tools/Local Terrain/Validate Height Jobs (no scene changes)")]
        static void Run()
        {
            if(runner!=null){Debug.Log("Height job validation is already running.");return;}
            runner=Check().GetEnumerator();EditorApplication.update+=Tick;
        }
        static void Stop(){EditorApplication.update-=Tick;runner?.Dispose();runner=null;}
        static void Tick()
        {try{if(!runner.MoveNext())Stop();}catch(Exception error){Stop();Debug.LogException(error);}}
        static IEnumerable<LTBuildStep> Check()
        {
            const float tolerance=2e-5f;
            int comparisons=0,burstJobs=0,failures=0,referenceFallbacks=0,rawMismatches=0;float maxError=0,maxScalarError=0,maxRawError=0;
            for(int fixture=0;fixture<12;fixture++)
            {
                var settings=LTRoadMath.Settings.Default;settings.mode=fixture%4==0?LTRoadMode.Asphalt:LTRoadMode.Offroad;
                settings.pattern=fixture%2==0?LTRoadPattern.Tracks:LTRoadPattern.Solid;
                settings.variation.enabled=fixture%3!=0;settings.straightStart=fixture%3==0;settings.straightEnd=fixture%4==0;
                settings.junctionStartLength=3;settings.junctionEndLength=5;settings.flatten=fixture%5*.25f;
                var road=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(1,2,1),-15),new LTRoadPoint(new Vector3(12,4,17),25),
                    new LTRoadPoint(new Vector3(29,3,29),-8)},Matrix4x4.identity,settings);
                var field=new float[33*33];for(int i=0;i<field.Length;i++)field[i]=.01f*(i%33)+.03f*(i/33);
                var source=new LTHeightSampling.Source(field,33,new Vector3(32,100,32));var rect=new Rect(0,0,32,32);
                var samples=new List<LTRoadMath.Sample>();var nodes=new List<LTRoadHeightKernel.Node>();
                var input=new LTHeightJobWork.Input{roads=new[]{road.CopyHeightData(samples,nodes)},samples=samples.ToArray(),nodes=nodes.ToArray()};
                input.source=source.CopyPatch(rect,out input.layout);
                var points=new List<LTHeightJobMath.Point>();var commands=new List<LTHeightJobMath.Command>();var expected=new List<float>();var scalar=new List<float>();
                var rb=new LTHeightJobMath.Buffer<LTRoadHeightKernel.Road>{values=input.roads};
                var sb=new LTHeightJobMath.Buffer<LTRoadMath.Sample>{values=input.samples};var nb=new LTHeightJobMath.Buffer<LTRoadHeightKernel.Node>{values=input.nodes};
                var sourceBuffer=new LTHeightJobMath.Buffer<float>{values=input.source};
                float Reference(LTHeightJobMath.Point point)
                {
                    float value=source.Sample(point.position.x,point.position.y);
                    for(int c=point.first;c<point.first+point.count;c++)
                    {
                        var command=commands[c];
                        value=command.kind==1?road.ApplyHeight(point.position.x,point.position.y,value):
                            LTBlend.Apply(value,command.target,command.weight,command.operation,command.blend,command.reference);
                    }
                    return value;
                }
                foreach(var p in LTHeightJobMath.BasePoints(rect,64))
                {
                    int first=commands.Count;
                    // Numeric operands model a masked stamp before the road and
                    // another ordered height modifier after it.
                    var before=new LTHeightJobMath.Command{target=1.3f,weight=.3f,operation=LTStampOperation.Add};
                    var after=new LTHeightJobMath.Command{target=2.1f,weight=.14f,operation=LTStampOperation.Max};
                    commands.Add(before);commands.Add(new LTHeightJobMath.Command{kind=1,road=0});commands.Add(after);
                    float h=source.Sample(p.x,p.y),worker=input.layout.Sample(sourceBuffer,p.x,p.y);
                    // Run all four prefixes through the real production job.
                    // This separates source interpolation, stamp and road errors.
                    for(int stage=0;stage<4;stage++)
                    {
                        if(stage>0)
                        {
                            var command=commands[first+stage-1];
                            h=stage==2?road.ApplyHeight(p.x,p.y,h):LTBlend.Apply(h,command.target,command.weight,command.operation,command.blend,command.reference);
                            worker=LTHeightJobMath.Apply(worker,p,command,rb,sb,nb);
                        }
                        points.Add(new LTHeightJobMath.Point{position=p,first=first,count=stage});expected.Add(h);scalar.Add(worker);
                    }
                }
                while(!LTHeightJobWork.CanSchedule)yield return LTBuildStep.Waiting;
                using(var job=new LTHeightJobWork(input,points,commands))
                {
                    yield return LTBuildStep.Waiting;while(!job.IsCompleted)yield return LTBuildStep.Waiting;
                    bool burst=job.UsedBurst;if(burst)burstJobs++;
                    int failed=0,worst=0;float fixtureError=0,scalarError=0,burstScalarError=0;
                    var stageErrors=new float[4];
                    for(int i=0;i<points.Count;i++)
                    {
                        float raw=job.Read(i),rawError=Math.Abs(raw-expected[i]);maxRawError=Math.Max(maxRawError,rawError);
                        if(rawError>tolerance)rawMismatches++;
                        bool referenceRequired=job.NeedsReference(i);if(referenceRequired)referenceFallbacks++;
                        // Same policy as PrefetchHeights: ONLY tagged ambiguous
                        // points use the previous evaluator. Never relax tolerance
                        // or replace untagged errors with the expected value.
                        float actual=referenceRequired?Reference(points[i]):raw,error=Math.Abs(actual-expected[i]);maxError=Math.Max(maxError,error);
                        float scalarDifference=Math.Abs(scalar[i]-expected[i]);scalarError=Math.Max(scalarError,scalarDifference);maxScalarError=Math.Max(maxScalarError,scalarDifference);
                        burstScalarError=Math.Max(burstScalarError,Math.Abs(actual-scalar[i]));
                        stageErrors[i%4]=Math.Max(stageErrors[i%4],error);
                        if(error>fixtureError){fixtureError=error;worst=i;}
                        if(error>tolerance||scalarDifference>tolerance){failed++;failures++;}
                        comparisons++;
                    }
                    if(failed>0)
                    {
                        var p=points[worst].position;
                        road.TrySample(p.x,p.y,out var hit);
                        Debug.LogWarning($"Height Jobs mismatch: fixture {fixture}, failures {failed}/{points.Count}, Burst {burst}; "+
                            $"max job/reference {fixtureError:R} m, scalar/reference {scalarError:R} m, job/scalar {burstScalarError:R} m; "+
                            $"stages source/before/road/after: {stageErrors[0]:R}, {stageErrors[1]:R}, {stageErrors[2]:R}, {stageErrors[3]:R} m. "+
                            $"Worst sample {worst/4}, stage {worst%4}, x={p.x:R}, z={p.y:R}: reference={expected[worst]:R}, scalar={scalar[worst]:R}, job={job.Read(worst):R}; "+
                            $"hit y={hit.position.y:R}, lateral={hit.lateral:R}, distance={hit.distance:R}, radial={hit.radialDistance:R}, bank={hit.bank:R}. Tolerance unchanged: {tolerance:R} m.");
                        foreach(var step in TracePoint(fixture,input,p,scalar[(worst/4)*4+1]))yield return step;
                    }
                }
                while(!LTHeightJobWork.CanSchedule)yield return LTBuildStep.Waiting;
                var stale=new LTHeightJobWork(input,points,commands);stale.Dispose();
                bool rejected=false;try{stale.Read(0);}catch(ObjectDisposedException){rejected=true;}
                if(!rejected)throw new InvalidOperationException("Retired height job remained readable.");
                while(!stale.IsCompleted)yield return LTBuildStep.Waiting;LTHeightJobWork.CollectRetired();
                yield return LTBuildStep.Working;
            }
            string detail=$"reference fallbacks {referenceFallbacks}/{comparisons}; raw job differences above tolerance {rawMismatches}, raw max {maxRawError:R} m; guarded projection v1";
            if(failures>0)throw new InvalidOperationException($"Height Jobs parity FAIL: {failures}/{comparisons} stage samples exceed unchanged {tolerance:R} m tolerance; max resolved/reference {maxError:R} m, scalar/reference {maxScalarError:R} m; Burst {burstJobs}/12; {detail}. See fixture diagnostics above. No scene/assets/settings changed.");
            string report=$"Height Jobs PASS: {comparisons} stage samples, max resolved error {maxError:R} m; retired result rejected; Burst executed {burstJobs}/12 jobs; {detail}. No scene/assets/settings changed. Not a performance benchmark.";
            if(burstJobs<12)Debug.LogWarning(report+" Run again after Burst compilation to verify every fixture with Burst.");else Debug.Log(report);
        }
    }
}
