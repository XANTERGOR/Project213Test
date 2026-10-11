using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Pure handle conversion; values are full width, one-sided shoulder, one-sided blend.
    public static class LTRoadDimensionMath
    {
        public static float Radius(Vector3 values,int parameter,float sideFactor)
        {
            Validate(values,parameter,sideFactor);
            return values.x*.5f*sideFactor+(parameter>=1?values.y:0)+(parameter>=2?values.z:0);
        }
        public static Vector3 Drag(Vector3 original,int parameter,float outwardDistance,float sideFactor)
        {
            Validate(original,parameter,sideFactor);
            if(!float.IsFinite(outwardDistance))throw new ArgumentException("Nonfinite road handle position.");
            var next=original;
            if(parameter==0)next.x=Mathf.Clamp(2*outwardDistance/sideFactor,.01f,10000);
            else if(parameter==1)next.y=Mathf.Clamp(outwardDistance-original.x*.5f*sideFactor,0,10000);
            else next.z=Mathf.Clamp(outwardDistance-original.x*.5f*sideFactor-original.y,0,10000);
            return next;
        }
        static void Validate(Vector3 values,int parameter,float factor)
        {
            if(parameter<0||parameter>2||!float.IsFinite(factor)||factor<=0||
                !float.IsFinite(values.x)||!float.IsFinite(values.y)||!float.IsFinite(values.z)||
                values.x<.01f||values.x>10000||values.y<0||values.y>10000||values.z<0||values.z>10000)
                throw new ArgumentException("Invalid road handle dimensions.");
        }
        public static LTRoadMath.Hit Frame(LTRoadMath.Snapshot road,float distance)
        {
            if(road==null||!float.IsFinite(distance))throw new ArgumentException("Invalid road handle station.");
            var samples=road.samples;distance=Mathf.Clamp(distance,0,road.length);
            int low=0,high=samples.Count-1;
            while(high-low>1){int middle=(low+high)/2;if(samples[middle].distance<=distance)low=middle;else high=middle;}
            var a=samples[low];var b=samples[high];float t=(distance-a.distance)/Math.Max(.000001f,b.distance-a.distance);
            return new LTRoadMath.Hit{position=Vector3.Lerp(a.position,b.position,t),right=Vector3.Lerp(a.right,b.right,t).normalized,
                bank=Mathf.Lerp(a.bank,b.bank,t),distance=distance,variationStrength=Mathf.Lerp(a.variationStrength,b.variationStrength,t)};
        }
    }
}
