using System;
using UnityEngine;
using LocalTerrainPrototype;
partial class Checks
{
    static void GpuSamplingChecks()
    {
        var p=new[]{new Color32(0,0,0,255),new Color32(255,0,0,255),
            new Color32(0,255,0,255),new Color32(255,255,0,255)};
        Color Sample(float u,float v,bool repeat=false)=>LTPaintMath.SampleGpuBilinear(p,2,2,u,v,repeat);
        void Near(float a,float b,string message)=>Require(Math.Abs(a-b)<.00001f,message);
        Near(Sample(.25f,.25f).r,0,"first texel centre");
        Near(Sample(.75f,.75f).g,1,"last texel centre");
        Near(Sample(.5f,.5f).r,.5f,"horizontal midpoint");
        Near(Sample(.5f,.5f).g,.5f,"vertical midpoint");
        Near(Sample(-2,3).r,0,"clamp left");Near(Sample(-2,3).g,1,"clamp top");
        Near(Sample(0,0,true).r,.5f,"repeat seam blends last and first");
        Near(Sample(-.25f,1.75f,true).g,1,"negative and positive repeat");
        var weights=new Color32[257];
        for(int i=0;i<257;i++)weights[i]=new Color32((byte)Math.Min(i,255),0,0,255);
        for(int i=0;i<257;i++)
        {
            float u=(i+.5f)/257;
            Near(LTPaintMath.SampleGpuBilinear(weights,257,1,u,.5f,false).r,Math.Min(i,255)/255f,"endpoint grid centres have no half-texel shift");
        }
        // A weight transition at sample 128 must remain at 128, not 127.5.
        for(int i=0;i<257;i++)weights[i]=new Color32((byte)(i>=128?255:0),0,0,255);
        Near(LTPaintMath.SampleGpuBilinear(weights,257,1,(127.5f+.5f)/257,.5f,false).r,.5f,"transition position agrees with GPU convention");
        Console.WriteLine("PASS GPU-convention CPU sampling: centres, endpoint grid, transition, clamp and repeat seams.");
    }
}
