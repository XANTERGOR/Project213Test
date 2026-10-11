// Standalone numeric Mono diagnostic, not a Unity project or native Jobs test.
using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;
class LTRoadRasterMonoRepro
{
    struct Buffer<T>:ILTHeightBuffer<T>{public T[] values;public T this[int index]=>values[index];}
    static string Describe(LTRoadMath.Hit h)=>$"p={h.position.x:R},{h.position.y:R},{h.position.z:R}; right={h.right.x:R},{h.right.z:R}; lateral={h.lateral:R}; radial={h.radialDistance:R}; arc={h.distance:R}; bank={h.bank:R}; variation={h.variationStrength:R}";
    static int Main()
    {
        int failed=0,comparisons=0,maps=0;
        for(int fixture=0;fixture<12;fixture++)
        {
        var settings=LTRoadMath.Settings.Default;settings.projection=LTRoadProjection.Spline;settings.mode=LTRoadMode.Asphalt;
        settings.mode=fixture%4==0?LTRoadMode.Asphalt:LTRoadMode.Offroad;
        settings.pattern=fixture%2==0?LTRoadPattern.Tracks:LTRoadPattern.Solid;
        settings.variation.enabled=fixture%3!=0;settings.wheelTracks.enabled=true;settings.wheelLayerId=21;
        settings.wheelTracks.independentTiling=true;settings.wheelTracks.tileSizeMetres=new Vector2(.67f,2.14f);
        settings.straightStart=fixture%2==0;settings.straightEnd=fixture%3==0;settings.junctionStartLength=3;settings.junctionEndLength=4;
        float shift=fixture>=8?100000:fixture>=4?-512:0;
        var points=Enumerable.Range(0,9).Select(i=>new LTRoadPoint(new Vector3(shift+8+(float)Math.Sin(i*.7)*12,i*.41f,shift+i*8),i%3*5)).ToArray();
        var road=LTRoadMath.Build(points,Matrix4x4.identity,settings);var rect=new Rect(shift-16,shift-8,64,80);const int size=65;
        var input=new LTRoadRasterInput(new[]{road},rect,size);
        var samples=new Buffer<LTRoadMath.Sample>{values=input.samples};var nodes=new Buffer<LTRoadHeightKernel.Node>{values=input.nodes};
        var choices=new int[size*size];
        for(int i=0;i<choices.Length;i++)choices[i]=input.needed[i]==0?-1:LTRoadRasterKernel.Segment(input.roads[0],samples,nodes,
            rect.xMin+rect.width*(i%size)/(size-1),rect.yMin+rect.height*(i/size)/(size-1));
        var raster=new LTRoadRasterSet(new[]{road},rect,size,choices);
        for(int i=0;i<size*size;i++)
        {
            if(input.needed[i]==0)continue;
            float x=rect.xMin+rect.width*(i%size)/(size-1),z=rect.yMin+rect.height*(i/size)/(size-1);
            int segment=LTRoadRasterKernel.Segment(input.roads[0],samples,nodes,x,z);
            road.TrySample(x,z,out var expected);raster.TrySample(0,i,out var actual);
            if(!expected.Equals(actual)){if(failed++<8)Console.WriteLine($"fixture={fixture}, pixel={i}, x={x:R}, z={z:R}, segment={segment}\nexpected {Describe(expected)}\nactual   {Describe(actual)}");}
            road.PaintWeights(x,z,out float g,out float w);road.PaintWeights(actual,x,z,out float ag,out float aw);
            if(g!=ag||w!=aw){if(failed++<8)Console.WriteLine($"weight mismatch fixture={fixture}, pixel={i}");}
            comparisons++;
        }
        foreach(var group in new[]{new[]{road},new[]{road.WheelLayerProjection()}})
        {
            var expected=LTRoadProjectionMath.Bake(group,rect,size);var actual=LTRoadProjectionMath.Bake(group,rect,size,raster);
            if(!expected.coordinates.SequenceEqual(actual.coordinates))failed++;maps++;
        }
        }
        Console.WriteLine($"Mono raster: {comparisons} exact hits/weights, {maps} UV maps, failures {failed}. Managed only, no Burst.");return failed==0?0:1;
    }
}
