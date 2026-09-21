using UnityEngine;
namespace LocalTerrainPrototype
{
    // Explicit prototype definitions; not claimed to reproduce MicroVerse's private formulas.
    public static class LTBlend
    {
        public static float Apply(float a,float b,float weight,LTStampOperation mode,float blend,float reference)
        {
            float r=b;
            switch(mode)
            {
                case LTStampOperation.Max:r=Mathf.Max(a,b);break;
                case LTStampOperation.Min:r=Mathf.Min(a,b);break;
                case LTStampOperation.Add:r=a+b;break;
                case LTStampOperation.Subtract:r=a-b;break;
                case LTStampOperation.Multiply:r=a*b/Mathf.Max(.001f,reference);break;
                case LTStampOperation.Average:r=(a+b)*.5f;break;
                case LTStampOperation.Difference:r=Mathf.Abs(a-b);break;
                case LTStampOperation.SqrtMultiply:r=Mathf.Sqrt(Mathf.Max(0,a)*Mathf.Max(0,b));break;
                case LTStampOperation.Blend:r=Mathf.Lerp(a,b,Mathf.Clamp01(blend));break;
            }
            return Mathf.Lerp(a,r,Mathf.Clamp01(weight));
        }
    }
}
