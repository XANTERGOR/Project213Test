using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Constrained value-type accessors specialise to managed or NativeArray
    // storage. No scene objects, delegates, allocation or recursive BVH traversal.
    public interface ILTHeightBuffer<T> { T this[int index] { get; } }
    public static class LTRoadHeightKernel
    {
        public struct Road
        {
            public LTRoadMath.Settings settings;
            public Rect bounds;
            public int firstNode,endNode,firstSample,lastSample;
            public float length,maxHalfWidth;
        }
        public struct Node
        {
            public int start,end,left,escape;
            public float minX,minZ,maxX,maxZ;
            public float DistanceSquared(float x,float z)
            {
                float dx=Math.Max(0,Math.Max(minX-x,x-maxX)),dz=Math.Max(0,Math.Max(minZ-z,z-maxZ));
                return dx*dx+dz*dz;
            }
        }
        public struct Trace
        {
            public int segment,backend,ambiguous;
            public float fraction,squaredDistance,rightLength,halfWidth,rutFade,weight,target,result;
            public LTRoadMath.Hit hit;
        }
        public static float Apply<S,B>(Road road,S samples,B nodes,float x,float z,float original)
            where S:struct,ILTHeightBuffer<LTRoadMath.Sample> where B:struct,ILTHeightBuffer<Node>
            =>ApplyDetailed(road,samples,nodes,x,z,original,out _);
        // Opt-in numeric diagnostics. The normal entry point discards this
        // value; Burst eliminates the unused trace stores.
        public static float ApplyDetailed<S,B>(Road road,S samples,B nodes,float x,float z,float original,out Trace trace)
            where S:struct,ILTHeightBuffer<LTRoadMath.Sample> where B:struct,ILTHeightBuffer<Node>
        {
            trace=new Trace{segment=-1,result=original};
            var s=road.settings;
            if(s.mode==LTRoadMode.Offroad&&s.flatten<=0&&!s.straightStart&&!s.straightEnd)return original;
            if(!Finite(x)||!Finite(z)||Math.Abs(x)>1e7f||Math.Abs(z)>1e7f||x<road.bounds.xMin||x>road.bounds.xMax||z<road.bounds.yMin||z>road.bounds.yMax)return original;
            float radius=road.maxHalfWidth+s.shoulderWidth+s.blendWidth;radius+=Math.Max(.001f,radius*.00001f);
            float best=radius*radius,fraction=0;int segment=-1,index=road.firstNode;
            // Projection subtracts rounded world-space coordinates. Near ties
            // can select a different adjacent segment in Mono and Burst even in
            // Strict mode. Keep the legacy CPU result for that narrow band.
            float coordinateScale=Math.Max(1,Math.Max(Math.Abs(x),Math.Abs(z)));
            coordinateScale=Math.Max(coordinateScale,Math.Max(Math.Max(Math.Abs(road.bounds.xMin),Math.Abs(road.bounds.xMax)),
                Math.Max(Math.Abs(road.bounds.yMin),Math.Abs(road.bounds.yMax))));
            float roundoffBand=8*1.192092896e-7f*coordinateScale*Math.Max(1,radius);
            while(index<road.endNode)
            {
                var node=nodes[index];
                if(node.DistanceSquared(x,z)>best+roundoffBand){index=node.escape;continue;}
                if(node.left>=0){index++;continue;}
                for(int i=node.start;i<node.end;i++)
                {
                    var a=samples[i].position;var b=samples[i+1].position;
                    float dx=b.x-a.x,dz=b.z-a.z;
                    float t=Clamp01(((x-a.x)*dx+(z-a.z)*dz)/(dx*dx+dz*dz));
                    float rx=x-(a.x+dx*t),rz=z-(a.z+dz*t),d=rx*rx+rz*rz;
                    if(segment>=0&&Math.Abs(d-best)<=roundoffBand)
                    {
                        // The shared vertex of two adjacent segments has exactly
                        // the same sample attributes; it needs no CPU fallback.
                        bool sameEnd=(i==segment+1&&fraction==1&&t==0)||(segment==i+1&&fraction==0&&t==1);
                        if(!sameEnd)trace.ambiguous=1;
                    }
                    if(d<best||(d==best&&(segment<0||i<segment)))
                    {
                        if(best-d>roundoffBand)trace.ambiguous=0;
                        best=d;segment=i;fraction=t;
                    }
                }
                index++;
            }
            if(segment<0)return original;
            trace.segment=segment;trace.fraction=fraction;trace.squaredDistance=best;
            var sa=samples[segment];var sb=samples[segment+1];float f=fraction;
            var position=sa.position+(sb.position-sa.position)*f;
            var right=sa.right+(sb.right-sa.right)*f;
            float rightLength=(float)Math.Sqrt((double)right.x*right.x+(double)right.z*right.z);
            trace.rightLength=rightLength;
            if(rightLength<.000001f)return float.NaN; // caller rejects invalid results
            right=new Vector3(right.x/rightLength,0,right.z/rightLength);
            var hit=new LTRoadMath.Hit{position=position,right=right,lateral=(x-position.x)*right.x+(z-position.z)*right.z,
                radialDistance=(float)Math.Sqrt(best),distance=sa.distance+(sb.distance-sa.distance)*f,
                bank=sa.bank+(sb.bank-sa.bank)*f,variationStrength=sa.variationStrength+(sb.variationStrength-sa.variationStrength)*f};
            trace.hit=hit;
            if(s.straightStart&&hit.distance<=0&&Vector3.Dot(new Vector3(x-hit.position.x,0,z-hit.position.z),
                new Vector3(-samples[road.firstSample].right.z,0,samples[road.firstSample].right.x))<-.00001f)return original;
            if(s.straightEnd&&hit.distance>=road.length&&Vector3.Dot(new Vector3(x-hit.position.x,0,z-hit.position.z),
                new Vector3(-samples[road.lastSample].right.z,0,samples[road.lastSample].right.x))>.00001f)return original;
            float rutFade=1;
            if(s.straightStart)rutFade=Math.Min(rutFade,Mathf.SmoothStep(0,1,hit.distance/Math.Max(.001f,s.junctionStartLength)));
            if(s.straightEnd)rutFade=Math.Min(rutFade,Mathf.SmoothStep(0,1,(road.length-hit.distance)/Math.Max(.001f,s.junctionEndLength)));
            float halfWidth=HalfWidth(road,hit),weight=Fade(hit.radialDistance,halfWidth,s.shoulderWidth+s.blendWidth);
            weight*=s.mode==LTRoadMode.Asphalt?1:Math.Max(s.flatten,1-rutFade);
            trace.halfWidth=halfWidth;trace.rutFade=rutFade;trace.weight=weight;
            if(weight<=0)return original;
            float target=hit.position.y+hit.lateral*(float)Math.Tan(Math.Max(-80,Math.Min(80,hit.bank))*Math.PI/180);
            if(s.mode==LTRoadMode.Offroad&&s.pattern==LTRoadPattern.Tracks)target-=s.rutDepth*TrackWeight(road,hit,x,z)*rutFade;
            else if(Varied(s)&&s.variation.solidRuts)target-=s.rutDepth*TrackWeight(road,hit,x,z)*rutFade*VariationStrength(road,hit);
            trace.target=target;trace.result=original+(target-original)*weight;
            return original+(target-original)*weight;
        }
        static bool Varied(LTRoadMath.Settings s)=>s.mode==LTRoadMode.Offroad&&s.variation.enabled&&s.variation.strength>0;
        static float VariationStrength(Road r,LTRoadMath.Hit h)
        {
            var s=r.settings;if(!Varied(s))return 0;float envelope=1;
            float start=s.straightStart?s.junctionStartLength:0,end=s.straightEnd?s.junctionEndLength:0;
            envelope=Math.Min(envelope,Smooth(Clamp01((h.distance-start)/Math.Max(2,start))));
            envelope=Math.Min(envelope,Smooth(Clamp01((r.length-h.distance-end)/Math.Max(2,end))));
            return s.variation.strength*h.variationStrength*envelope;
        }
        static float HalfWidth(Road r,LTRoadMath.Hit h)
        {
            var s=r.settings;float half=s.width*.5f;if(!Varied(s)||s.variation.widthAmount<=0)return half;
            var v=s.variation;float amount=VariationStrength(r,h)*v.widthAmount;if(amount<=0)return half;
            float left=Noise(h.distance/v.widthLength,.37f,unchecked(v.seed^0x7153));
            float right=Noise(h.distance/v.widthLength,9.71f,unchecked(v.seed^0x19b5));
            float side=Smooth(Clamp01(.5f+h.lateral/half));return half*(1+amount*(2*(left+(right-left)*side)-1));
        }
        static float TrackWeight(Road r,LTRoadMath.Hit h,float x,float z)
        {
            var s=r.settings;float half=s.rutWidth*.5f,extent=half*(1-s.edgeNoise*Noise(x/s.noiseSize,z/s.noiseSize,s.seed)*.8f);
            float across=Math.Abs(Math.Abs(h.lateral)-s.rutSeparation*.5f),beyond=Math.Max(0,h.radialDistance*h.radialDistance-h.lateral*h.lateral);
            float radial=(float)Math.Sqrt(across*across+beyond),result=Fade(radial,extent*.65f,extent*.35f);
            if(result>0&&Varied(s)&&s.variation.rutVariation>0)
            {var v=s.variation;float noise=Noise(h.distance/v.rutLength,.43f,unchecked(v.seed^0x47591));result*=1-v.rutVariation*VariationStrength(r,h)*Smooth(Clamp01((noise-.2f)/.6f));}
            return result;
        }
        static float Noise(float x,float z,int seed)
        {
            int ix=(int)Math.Floor(x),iz=(int)Math.Floor(z);float u=Smooth(x-ix),v=Smooth(z-iz);
            float a=Corner(ix,iz,seed),b=Corner(ix+1,iz,seed),c=Corner(ix,iz+1,seed),d=Corner(ix+1,iz+1,seed);
            return (a+(b-a)*u)*(1-v)+(c+(d-c)*u)*v;
        }
        static float Corner(int x,int z,int seed)
        {unchecked{uint h=(uint)((seed*397^x)*397^z);h^=h>>16;h*=0x7feb352d;h^=h>>15;h*=0x846ca68b;h^=h>>16;return(h>>8)*(1f/16777216);}}
        static float Fade(float distance,float inner,float feather)=>distance<=inner?1:feather<=0?0:1-Smooth(Clamp01((distance-inner)/feather));
        static float Clamp01(float value)=>Math.Max(0,Math.Min(1,value));
        static float Smooth(float value)=>value*value*(3-2*value);
        static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
    }
}
