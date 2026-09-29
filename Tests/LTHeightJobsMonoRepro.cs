// Standalone numeric check for Unity's Mono runtime, NOT a Unity project.
// Compile against the built Assembly-CSharp and UnityEngine.CoreModule.
using System;
using System.Collections.Generic;
using LocalTerrainPrototype;
using UnityEngine;
static class LTHeightJobsMonoRepro
{
    struct Buffer<T>:ILTHeightBuffer<T>
    {public T[] values;public T this[int index]=>values[index];}
    static int Main()
    {
        float maxError=0;int failures=0;
        for(int fixture=0;fixture<12;fixture++)
        {
            var settings=LTRoadMath.Settings.Default;settings.mode=fixture%4==0?LTRoadMode.Asphalt:LTRoadMode.Offroad;
            settings.pattern=fixture%2==0?LTRoadPattern.Tracks:LTRoadPattern.Solid;
            settings.variation.enabled=fixture%3!=0;settings.straightStart=fixture%3==0;settings.straightEnd=fixture%4==0;
            settings.junctionStartLength=3;settings.junctionEndLength=5;settings.flatten=fixture%5*.25f;
            var road=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(1,2,1),-15),new LTRoadPoint(new Vector3(12,4,17),25),
                new LTRoadPoint(new Vector3(29,3,29),-8)},Matrix4x4.identity,settings);
            var samples=new List<LTRoadMath.Sample>();var nodes=new List<LTRoadHeightKernel.Node>();var r=road.CopyHeightData(samples,nodes);
            var sb=new Buffer<LTRoadMath.Sample>{values=samples.ToArray()};var nb=new Buffer<LTRoadHeightKernel.Node>{values=nodes.ToArray()};
            for(int z=0;z<=64;z++)for(int x=0;x<=64;x++)
            {
                float expected=road.ApplyHeight(x*.5f,z*.5f,3),actual=LTRoadHeightKernel.Apply(r,sb,nb,x*.5f,z*.5f,3);
                float error=Math.Abs(expected-actual);maxError=Math.Max(maxError,error);
                if(error>2e-5f){failures++;if(failures<8)Console.WriteLine($"fixture={fixture}, sample={z*65+x}, expected={expected:R}, scalar={actual:R}, error={error:R}");}
            }
        }
        Console.WriteLine($"Mono managed kernel/reference: 50700 samples, max error {maxError:R} m, failures {failures}. No Burst/native Jobs or scene execution.");
        return failures==0?0:1;
    }
}
