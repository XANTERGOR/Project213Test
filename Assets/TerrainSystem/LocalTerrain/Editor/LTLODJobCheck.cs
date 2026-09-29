using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Explicit non-scene validation: actual scheduled NativeArrays/JobHandle and
    // a BurstDiscard marker distinguish native Burst from the managed fallback.
    [InitializeOnLoad]
    internal static class LTLODJobCheck
    {
        static IEnumerator<LTBuildStep> runner;
        static LTLODJobCheck()
        {
            AssemblyReloadEvents.beforeAssemblyReload+=Stop;
            EditorApplication.quitting+=Stop;
        }
        [MenuItem("Tools/Local Terrain/Validate LOD Jobs (no scene changes)")]
        static void Run()
        {
            if(runner!=null){Debug.Log("LOD job validation already running.");return;}
            runner=Check().GetEnumerator();EditorApplication.update+=Tick;
        }
        static void Stop()
        {EditorApplication.update-=Tick;runner?.Dispose();runner=null;}
        static void Tick()
        {
            try{if(!runner.MoveNext())Stop();}
            catch(Exception error){Stop();Debug.LogException(error);}
        }
        static IEnumerable<LTBuildStep> Check()
        {
            int comparisons=0,burstJobs=0;const int n=LTBalancedForest.N;
            for(int fixture=0;fixture<12;fixture++)
            {
                var fine=new List<Vector3Int>();int side=fixture%3==0?64:16,step=n/side;
                for(int z=0;z<n;z+=step)for(int x=0;x<n;x+=step)
                {
                    if(fixture%2==0&&x<n/2&&z>n/2)
                    {int h=step/2;for(int j=0;j<2;j++)for(int i=0;i<2;i++)fine.Add(new Vector3Int(x+i*h,z+j*h,h));}
                    else fine.Add(new Vector3Int(x,z,step));
                }
                var rect=new Rect(-13.5f,23.75f,32,48);bool boundary=fixture%2==0;int divisions=fixture%3==0?4:1;
                var settings=Enumerable.Range(0,5).Select(level=>new LTLODJobMath.Level{steps=level,tolerance=level*.08f}).ToArray();
                float Height(float x,float z)=>.001f*x*x+.04f*(float)Math.Sin(z*.3)+.2f*(float)Math.Exp(-Math.Pow(x+z*.2,2));
                var protectedRegions=new List<Rect>{new Rect(-5,33,2,6)};
                bool Protected(Rect area)=>area.xMin<0&&area.xMax>-.5f;
                var input=new LTLODJobInput();
                foreach(var stepResult in input.Prepare(fine,rect,settings,Height,protectedRegions,Protected,divisions,boundary))yield return stepResult;
                while(!LTLODJobWork.CanSchedule)yield return LTBuildStep.Waiting;
                using(var job=new LTLODJobWork(input))
                {
                    yield return LTBuildStep.Waiting;
                    while(!job.IsCompleted)yield return LTBuildStep.Waiting;
                    if(job.UsedBurst)burstJobs++;
                    var reference=new LTLODMesh.Preparation(fine,rect,Height,new LTLODMesh.BuildProgress(false));
                    for(int level=0;level<settings.Length;level++)
                    {
                        var expected=reference.Coarsen(settings[level].steps,settings[level].tolerance,protectedRegions,Protected,divisions,boundary);
                        LTLODJobInput.SortPlan(expected);
                        if(!job.ReadLevel(level).SequenceEqual(expected))throw new InvalidOperationException($"LOD Jobs parity failed: fixture {fixture}, level {level}.");
                        comparisons++;yield return LTBuildStep.Working;
                    }
                }
                // Explicitly reject an already-scheduled result; no Mesh exists
                // in this fixture, so rejection cannot accidentally alter a scene.
                while(!LTLODJobWork.CanSchedule)yield return LTBuildStep.Waiting;
                var stale=new LTLODJobWork(input);stale.Dispose();
                bool rejected=false;try{stale.ReadLevel(0);}catch(ObjectDisposedException){rejected=true;}
                if(!rejected)throw new InvalidOperationException("Retired LOD result was readable.");
                while(!stale.IsCompleted)yield return LTBuildStep.Waiting;
                LTLODJobWork.CollectRetired();
            }
            string report=$"LOD Jobs PASS: {comparisons} exact reference plans; scheduled-result rejection; Burst executed {burstJobs}/12 jobs. No scene, assets or settings changed. Not a live performance benchmark.";
            if(burstJobs==0)Debug.LogWarning(report+" Burst was disabled or still compiling: run again after compilation to validate the native Burst backend.");
            else Debug.Log(report);
        }
    }
}
