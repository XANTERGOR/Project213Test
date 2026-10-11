using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    [InitializeOnLoad]
    static class LTPaintWeightCheck
    {
        static IEnumerator<int> runner;
        static LTPaintWeightCheck(){AssemblyReloadEvents.beforeAssemblyReload+=Stop;EditorApplication.quitting+=Stop;}
        // Archived validation menu: Tools/Local Terrain/Validate Paint Weight Jobs (no scene changes)
        static void Run()=>Start(false);
        // Archived validation menu: Tools/Local Terrain/Validate Rock Paint Jobs (no scene changes)
        static void RunRocks()=>Start(true);
        // Archived validation menu: Tools/Local Terrain/Validate Paint Preparation Jobs (no scene changes)
        static void RunPreparation()=>Start(false,true);
        static void Start(bool rocks,bool preparation=false)
        {
            if(runner!=null){Debug.Log("Paint weight validation is already running.");return;}
            runner=Check(rocks,preparation).GetEnumerator();EditorApplication.update+=Tick;
        }
        static void Stop(){EditorApplication.update-=Tick;runner?.Dispose();runner=null;}
        static void Tick(){try{if(!runner.MoveNext())Stop();}catch(Exception e){Stop();Debug.LogException(e);}}
        static IEnumerable<int> Check(bool rocks,bool preparation)
        {
            string label=preparation?"Paint Preparation Jobs":rocks?"Rock Paint Jobs":"Paint Weight Jobs";
            string version="v2";
            var pool=new LTPaintWeightInputPool();
            int count=LTPaintWeightFixture.Count*(preparation?2:1);
            int pixels=0,guards=0,burstJobs=0,failures=0;
            for(int fixture=0;fixture<count;fixture++)
            {
                bool rockFixture=rocks||preparation&&fixture>=LTPaintWeightFixture.Count;int index=fixture%LTPaintWeightFixture.Count;
                var f=rockFixture?LTPaintWeightFixture.BuildRock(index):LTPaintWeightFixture.Build(index);
                using(var input=preparation?f.PreparedInput(pool):f.input)
                {
                    while(!LTPaintWeightWork.CanSchedule)yield return 0;
                    using(var work=new LTPaintWeightWork(input))
                    {
                        if(preparation){input.Dispose();f.OverwriteReturnedBuffers(pool);}
                        yield return 0;while(!work.IsCompleted)yield return 0;
                        var actual=work.Result(out bool burst);if(burst)burstJobs++;
                        for(int p=0;p<actual.Length;p++)
                        {
                            var expected=f.Reference(p);var value=actual[p];
                            if(value.guard!=0){guards++;value=expected;}
                            if(!LTPaintWeightFixture.Same(value,expected))
                            {
                                var scalar=f.Scalar(p);
                                failures++;
                                if(failures<=8)Debug.LogWarning($"{label} mismatch {version}: fixture {fixture}, pixel {p}, Burst={burst}; native={value.first}/{value.second}/{value.third}, reference={expected.first}/{expected.second}/{expected.third}, scalar={scalar.first}/{scalar.second}/{scalar.third}, scalarGuard={scalar.guard}. No scene/assets/settings changed.");
                            }
                            pixels++;if(p%512==511)yield return 0;
                        }
                    }
                    while(!LTPaintWeightWork.CanSchedule)yield return 0;
                    using(var retirementInput=preparation?f.PreparedInput(pool):f.input)
                    {
                        var retired=new LTPaintWeightWork(retirementInput);retired.Dispose();bool rejected=false;
                        if(preparation){retirementInput.Dispose();f.OverwriteReturnedBuffers(pool);}
                        try{retired.Result(out _);}catch(ObjectDisposedException){rejected=true;}
                        if(!rejected)throw new InvalidOperationException("Retired paint weights remained readable.");
                        while(!retired.IsCompleted)yield return 0;LTPaintWeightWork.CollectRetired();
                    }
                }
            }
            if(failures>0)throw new InvalidOperationException($"{label} FAIL {version}: {failures}/{pixels} pixels differ; CPU guards {guards}; Burst {burstJobs}/{count}. See first mismatches above. No scene/assets/settings changed.");
            string report=$"{label} PASS {version}: {pixels} exact 12-layer pixels (3 RGBA32 maps), CPU rounding guards {guards}; Burst {burstJobs}/{count}. Ordered overlap, {(preparation?"packed terrain + rocks, spatial index reuse, snapshot eviction, returned input overwrite during Jobs":rocks?"no road stamps":"roads/wheels")}, junction, mask, noise, terrain filters and retirement. No scene/assets/settings changed. Not a performance benchmark.";
            if(burstJobs<count)Debug.LogWarning(report+" Repeat after Burst compilation for native confirmation.");else Debug.Log(report);
        }
    }
}
