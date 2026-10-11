using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    [Serializable]
    public struct LTRoadAsphaltDamage
    {
        [Range(0,1)] public float strength, potholes, brokenEdges, cracks;
        [Min(.1f)] public float spacing, size;
        [Range(0,2)] public float depth;
        public int seed;
        public static LTRoadAsphaltDamage Default=>new LTRoadAsphaltDamage
            {strength=1,potholes=.45f,brokenEdges=.25f,cracks=.65f,spacing=6,size=2,depth=.15f,seed=7351};
        public void Validate()
        {
            foreach(float v in new[]{strength,potholes,brokenEdges,cracks})
                if(!Finite(v)||v<0||v>1)throw new ArgumentException("Asphalt damage: strengths must be 0..1.");
            if(!Finite(spacing)||spacing<.1f||spacing>10000||!Finite(size)||size<.1f||size>spacing*.8f||
                !Finite(depth)||depth<0||depth>2)
                throw new ArgumentException("Asphalt damage: size must be 0.1..0.8 × spacing, depth 0..2 m.");
        }
        static bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
        public int GeometryHash()
        {
            unchecked {int h=seed;foreach(float v in new[]{strength,potholes,brokenEdges,spacing,size,depth})h=h*397^v.GetHashCode();return h;}
        }
    }

    // Shared scalar math for reference, Burst height/paint kernels and detail-mask bake.
    // Road-local metres and point envelopes: independent of camera, chunk and texture UV.
    public static class LTRoadAsphaltMath
    {
        public struct Damage { public float removed, depression, cracks; }
        // Follow the same ordered over-composite as terrain weights, retaining
        // only this asphalt slot's crack numerator and surviving coverage.
        public static void CompositeCracks(ref float coverage,ref float crack,float alpha,float strength,bool sameLayer)
        {
            alpha=Clamp(alpha);coverage*=1-alpha;crack*=1-alpha;
            if(sameLayer){coverage+=alpha;crack+=alpha*Clamp(strength);}
        }
        public static void CompositeSurface(ref float coverage,ref float crack,ref float repair,float alpha,float cracks,float patches,bool sameLayer)
        {
            alpha=Clamp(alpha);repair*=1-alpha;if(sameLayer)repair+=alpha*Clamp(patches);
            CompositeCracks(ref coverage,ref crack,alpha,cracks,sameLayer);
        }
        public static Damage Evaluate(LTRoadAsphaltDamage s,LTRoadMath.Hit hit,float width,float length)
        {
            var result=new Damage();float amount=s.strength*Clamp(hit.variationStrength);
            amount*=Smooth(Clamp(hit.distance/2))*Smooth(Clamp((length-hit.distance)/2));
            if(amount<=0)return result;
            float pit=0,cut=0,cell=Math.Max(.1f,s.spacing),radius=Math.Min(s.size*.5f,cell*.4f);
            int ix=(int)Math.Floor(hit.distance/cell),iy=(int)Math.Floor(hit.lateral/cell);
            for(int j=-1;j<=1;j++)for(int i=-1;i<=1;i++)
            {
                int x=ix+i,y=iy+j;
                if(Hash(x,y,s.seed)>=s.potholes)continue;
                float cx=(x+.15f+.7f*Hash(x,y,unchecked(s.seed+13)))*cell;
                float cy=(y+.15f+.7f*Hash(x,y,unchecked(s.seed+37)))*cell;
                if(Math.Abs(cy)>width*.5f||cx<0||cx>length)continue;
                float dx=(hit.distance-cx)/Math.Max(.05f,radius),dy=(hit.lateral-cy)/Math.Max(.05f,radius*.75f);
                float r=(float)Math.Sqrt(dx*dx+dy*dy);
                float ragged=.86f+.14f*Noise(hit.distance/.3f,hit.lateral/.3f,s.seed);
                float bowl=1-Smooth(Clamp(r/ragged));
                pit=Math.Max(pit,bowl);cut=Math.Max(cut,1-Smooth(Clamp((r/ragged-.58f)/.2f)));
            }
            float edge=0;
            if(s.brokenEdges>0)
            {
                float n=Noise(hit.distance/Math.Max(1,cell*.6f),hit.lateral>0?3.7f:.4f,unchecked(s.seed+71));
                float reach=width*.22f*s.brokenEdges*Smooth(Clamp((n-.25f)/.5f));
                edge=Smooth(Clamp((Math.Abs(hit.lateral)-(width*.5f-reach))/Math.Max(.08f,reach*.35f)));
                edge*=Smooth(Clamp(reach/.08f));
            }
            result.removed=Math.Max(cut,edge)*amount;
            result.depression=Math.Max(pit,edge*.35f)*s.depth*amount;
            result.cracks=s.cracks*amount*(.25f+.75f*Noise(hit.distance/cell,hit.lateral/cell,unchecked(s.seed+113)));
            return result;
        }
        // Broad repair patches in spline metres, sampled only when rebuilding
        // the existing influence map. G channel; no new terrain layer or mesh.
        public static float RepairPatch(LTRoadAsphaltDamage s,LTRoadMath.Hit hit,float width,float length)
        {
            float amount=s.strength*Clamp(hit.variationStrength)*Smooth(Clamp(hit.distance/2))*Smooth(Clamp((length-hit.distance)/2));
            if(amount<=0||Math.Abs(hit.lateral)>width*.5f)return 0;
            float cell=Math.Max(2,s.spacing*.8f);int row=(int)Math.Floor(hit.distance/cell);float result=0;
            for(int i=-1;i<=1;i++)
            {
                int r=row+i;if(Hash(r,0,s.seed+701)>.48f)continue;
                float centre=(r+.25f+.5f*Hash(r,0,s.seed+719))*cell;
                float lateral=(Hash(r,0,s.seed+733)-.5f)*width*.65f;
                float halfLength=cell*(.16f+.12f*Hash(r,0,s.seed+751));
                float halfWidth=width*(.12f+.09f*Hash(r,0,s.seed+769));
                float rough=.08f*(Noise(hit.distance/.45f,hit.lateral/.45f,s.seed+787)-.5f);
                float d=Math.Max(Math.Abs(hit.distance-centre)-halfLength,Math.Abs(hit.lateral-lateral)-halfWidth)+rough;
                result=Math.Max(result,1-Smooth(Clamp((d+.12f)/.24f)));
            }
            return result*amount;
        }
        static float Clamp(float x)=>Math.Max(0,Math.Min(1,x));
        static float Smooth(float x)=>x*x*(3-2*x);
        static float Noise(float x,float y,int seed)
        {
            int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);float u=Smooth(x-ix),v=Smooth(y-iy);
            float a=Hash(ix,iy,seed),b=Hash(ix+1,iy,seed),c=Hash(ix,iy+1,seed),d=Hash(ix+1,iy+1,seed);
            return (a+(b-a)*u)*(1-v)+(c+(d-c)*u)*v;
        }
        static float Hash(int x,int y,int seed)
        {unchecked{uint h=(uint)((seed*397^x)*397^y);h^=h>>16;h*=0x7feb352d;h^=h>>15;h*=0x846ca68b;h^=h>>16;return(h>>8)*(1f/16777216);}}
    }
}
