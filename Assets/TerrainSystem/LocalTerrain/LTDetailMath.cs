using UnityEngine;

namespace LocalTerrainPrototype
{
    // Stable hashing, independent of frame timing, list order and runtime string hashes.
    public static class LTDetailMath
    {
        public static uint Hash(uint value)
        {
            unchecked{value^=value>>16;value*=0x7feb352du;value^=value>>15;value*=0x846ca68bu;return value^(value>>16);}
        }
        public static uint TextHash(string value)
        {
            unchecked{uint h=2166136261u;if(value!=null)foreach(char c in value){h^=c;h*=16777619u;}return h;}
        }
        public static uint Candidate(int seed,string ownerId,string entryId,int x,int z,int index)
        {
            unchecked
            {
                uint h=Hash((uint)seed^TextHash(ownerId));h=Hash(h^TextHash(entryId));
                h=Hash(h^(uint)x);h=Hash(h^(uint)z);return Hash(h^(uint)index);
            }
        }
        public static float Unit(uint id,uint channel)=>(Hash(id^Hash(channel))>>8)*(1f/16777216f);
        public static bool Accept(uint id,float weight)=>Unit(id,17)<Mathf.Clamp01(weight);
        static float NoiseCorner(int x,int z,uint seed)
            =>(Hash(Hash(seed^(uint)x)^(uint)z)>>8)*(1f/16777216f);
        static float Noise(float x,float z,uint seed)
        {
            int ix=Mathf.FloorToInt(x),iz=Mathf.FloorToInt(z);
            float u=x-ix,v=z-iz;
            // Quintic interpolation: continuous value and slope at every lattice edge.
            u=u*u*u*(u*(u*6-15)+10);v=v*v*v*(v*(v*6-15)+10);
            return Mathf.Lerp(Mathf.Lerp(NoiseCorner(ix,iz,seed),NoiseCorner(ix+1,iz,seed),u),
                Mathf.Lerp(NoiseCorner(ix,iz+1,seed),NoiseCorner(ix+1,iz+1,seed),u),v);
        }
        public static float DensityMask(LTDetailEntry entry,float x,float z,int worldSeed)
        {
            if(!entry.densityMask)return 1;
            float minimum=Mathf.Clamp01(entry.patchMinimumDensity),coverage=Mathf.Clamp01(entry.patchCoverage);
            if(coverage<=0)return minimum;
            if(coverage>=1||minimum>=1)return 1;
            float size=Mathf.Max(.1f,entry.patchSize);
            x/=size;z/=size;
            uint seed=Hash(unchecked((uint)worldSeed)^Hash(unchecked((uint)entry.patchSeed)));
            // A smaller second octave breaks the regular shapes without a texture allocation.
            float value=.8f*Noise(x,z,seed)+.2f*Noise(x*2+17.13f,z*2-9.71f,Hash(seed));
            float threshold=1-coverage,width=Mathf.Clamp01(entry.patchSoftness)*.5f;
            float mask=width<=0?(value>=threshold?1:0):
                Mathf.SmoothStep(0,1,Mathf.InverseLerp(threshold-width*.5f,threshold+width*.5f,value));
            return Mathf.Lerp(minimum,1,mask);
        }
        // Each integer candidate owns one unit of density: increasing density only adds candidates.
        public static bool DensityAccept(uint id,int index,float density)=>Unit(id,18)<Mathf.Clamp01(density-index);
        public static void HeightBlend(float[] weights,float[] heights,int count,float blend)
        {
            float highest=-1,total=0;
            for(int i=0;i<count;i++)if(weights[i]>.00001f)highest=Mathf.Max(highest,heights[i]+weights[i]);
            for(int i=0;i<count;i++)
            {
                if(blend>.0001f)weights[i]*=Mathf.Lerp(1,Mathf.Clamp01((heights[i]+weights[i]-highest+.2f)/.2f),blend);
                total+=weights[i];
            }
            for(int i=0;i<count;i++)weights[i]/=Mathf.Max(total,.00001f);
        }
        public static float ScreenHeight(float size,float depth,bool orthographic,float orthoSize,float fieldOfView,float bias)
            =>size*Mathf.Max(.01f,bias)/(orthographic?Mathf.Max(.001f,2*orthoSize):
                2*Mathf.Max(.001f,depth)*Mathf.Tan(fieldOfView*Mathf.Deg2Rad*.5f));
        public static float Suppress(float weight,float area,bool additive)=>additive?weight:weight*(1-Mathf.Clamp01(area));
        public static float AreaWeight(float x,float z,float width,float length,bool ellipse,float falloff)
        {
            float u=2*x/Mathf.Max(.01f,width),v=2*z/Mathf.Max(.01f,length);
            float distance=ellipse?Mathf.Sqrt(u*u+v*v):Mathf.Max(Mathf.Abs(u),Mathf.Abs(v));
            if(distance>=1)return 0;
            if(falloff<=0)return 1;
            return 1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(1-Mathf.Clamp01(falloff),1,distance));
        }
    }
}
