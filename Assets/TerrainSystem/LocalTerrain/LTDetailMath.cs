using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // Stable hashing, independent of frame timing, list order and runtime string hashes.
    public static class LTDetailMath
    {
        public static ShadowCastingMode ShadowMode(LTDetailEntry entry,ShadowCastingMode prefabMode,float distanceSquared)
            =>ShadowMode(prefabMode,ShadowsInRange(entry,distanceSquared));
        public static bool ShadowsInRange(LTDetailEntry entry,float distanceSquared)
            =>!entry.limitShadowDistance||distanceSquared<entry.shadowDistance*entry.shadowDistance;
        public static ShadowCastingMode ShadowMode(ShadowCastingMode prefabMode,bool shadowsInRange)
            =>shadowsInRange?prefabMode:ShadowCastingMode.Off;
        public static bool SubmitPart(ShadowCastingMode prefabMode,bool inView,bool shadowsInRange)
        {
            var mode=ShadowMode(prefabMode,shadowsInRange);
            if(prefabMode==ShadowCastingMode.ShadowsOnly&&mode==ShadowCastingMode.Off)return false;
            return inView||mode!=ShadowCastingMode.Off;
        }
        public static float BoundsDistanceSquared(Bounds bounds,Vector3 point)
        {
            var d=point-bounds.center;var e=bounds.extents;
            float x=Mathf.Max(0,Mathf.Abs(d.x)-e.x),y=Mathf.Max(0,Mathf.Abs(d.y)-e.y),z=Mathf.Max(0,Mathf.Abs(d.z)-e.z);
            return x*x+y*y+z*z;
        }
        // originBounds must enclose the instance pivots, not just their offset meshes.
        public static bool CanSkipOffscreen(bool inView,Bounds originBounds,Vector3 camera,float shadowRange,float maxDistance)
        {
            if(inView)return false;
            float range=Mathf.Min(shadowRange,maxDistance);
            return range<=0||BoundsDistanceSquared(originBounds,camera)>=range*range;
        }

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
        // Conservative bounds, not centre-point probes: thin painted strips and bilinear
        // edge contributions must survive the editor's sparse bake planning.
        public static bool HasLayerWeight(Color32[] pixels,int width,int height,Rect tile,Rect area,int channel)
        {
            if(pixels==null||width<1||height<1||pixels.Length!=(long)width*height||tile.width<=0||tile.height<=0)return true;
            float left=Mathf.Max(tile.xMin,area.xMin),right=Mathf.Min(tile.xMax,area.xMax);
            float bottom=Mathf.Max(tile.yMin,area.yMin),top=Mathf.Min(tile.yMax,area.yMax);
            if(left>=right||bottom>=top)return false;
            // Matches LTDetailSurface.Weights endpoint UV mapping and GPU bilinear footprint.
            float X(float x)=>(((x-tile.xMin)/tile.width*256+.5f)/257)*width-.5f;
            float Z(float z)=>(((z-tile.yMin)/tile.height*256+.5f)/257)*height-.5f;
            int x0=Mathf.Clamp(Mathf.FloorToInt(X(left)),0,width-1),x1=Mathf.Clamp(Mathf.CeilToInt(X(right)),0,width-1);
            int z0=Mathf.Clamp(Mathf.FloorToInt(Z(bottom)),0,height-1),z1=Mathf.Clamp(Mathf.CeilToInt(Z(top)),0,height-1);
            for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)
            {
                var p=pixels[z*width+x];
                if((channel==0?p.r:channel==1?p.g:channel==2?p.b:p.a)>0)return true;
            }
            return false;
        }
        static float NoiseUpperBound(Rect area,uint seed)
        {
            int x0=Mathf.FloorToInt(area.xMin),x1=Mathf.FloorToInt(area.xMax)+1;
            int z0=Mathf.FloorToInt(area.yMin),z1=Mathf.FloorToInt(area.yMax)+1;
            if((long)(x1-x0+1)*(z1-z0+1)>4096)return 1;
            float maximum=0;
            for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)maximum=Mathf.Max(maximum,NoiseCorner(x,z,seed));
            return maximum;
        }
        public static bool DensityMaskMayAffect(LTDetailEntry entry,Rect area,int worldSeed)
        {
            if(!entry.densityMask||entry.patchMinimumDensity>0)return true;
            float coverage=Mathf.Clamp01(entry.patchCoverage);
            if(coverage<=0)return false;if(coverage>=1)return true;
            float size=Mathf.Max(.1f,entry.patchSize);
            var first=new Rect(area.xMin/size,area.yMin/size,area.width/size,area.height/size);
            var second=new Rect(first.xMin*2+17.13f,first.yMin*2-9.71f,first.width*2,first.height*2);
            uint seed=Hash(unchecked((uint)worldSeed)^Hash(unchecked((uint)entry.patchSeed)));
            float maximum=.8f*NoiseUpperBound(first,seed)+.2f*NoiseUpperBound(second,Hash(seed));
            return maximum+0.00001f>=1-coverage-Mathf.Clamp01(entry.patchSoftness)*.25f;
        }
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
            =>DensityMask(entry,x,z,worldSeed,out _);
        public static float DensityMask(LTDetailEntry entry,float x,float z,int worldSeed,out float scale)
        {
            scale=1;
            if(!entry.densityMask)return 1;
            float minimum=Mathf.Clamp01(entry.patchMinimumDensity),coverage=Mathf.Clamp01(entry.patchCoverage);
            if(coverage<=0){scale=PatchScale(entry,0);return minimum;}
            if(coverage>=1){scale=PatchScale(entry,1);return 1;}
            if(minimum>=1&&!entry.patchScaleEnabled)return 1;
            float size=Mathf.Max(.1f,entry.patchSize);
            x/=size;z/=size;
            uint seed=Hash(unchecked((uint)worldSeed)^Hash(unchecked((uint)entry.patchSeed)));
            // A smaller second octave breaks the regular shapes without a texture allocation.
            float value=.8f*Noise(x,z,seed)+.2f*Noise(x*2+17.13f,z*2-9.71f,Hash(seed));
            scale=PatchScale(entry,value);
            float threshold=1-coverage,width=Mathf.Clamp01(entry.patchSoftness)*.5f;
            float mask=width<=0?(value>=threshold?1:0):
                Mathf.SmoothStep(0,1,Mathf.InverseLerp(threshold-width*.5f,threshold+width*.5f,value));
            return Mathf.Lerp(minimum,1,mask);
        }
        // Same noise sample as density, but a separate inward ramp. Density floor
        // must not enlarge plants in gaps. Not geometric distance to the boundary.
        public static float PatchScale(LTDetailEntry entry,float noise)
        {
            if(!entry.densityMask||!entry.patchScaleEnabled)return 1;
            float edge=Mathf.Max(.01f,entry.patchScaleEdge),inside=Mathf.Max(.01f,entry.patchScaleInside);
            float coverage=Mathf.Clamp01(entry.patchCoverage);
            if(coverage<=0)return edge;
            if(coverage>=1)return inside;
            float boundary=1-coverage,width=Mathf.Clamp01(entry.patchScaleSoftness)*coverage;
            float t=width<=0?(noise>boundary?1:0):Mathf.SmoothStep(0,1,Mathf.InverseLerp(boundary,boundary+width,noise));
            return Mathf.Lerp(edge,inside,t);
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
