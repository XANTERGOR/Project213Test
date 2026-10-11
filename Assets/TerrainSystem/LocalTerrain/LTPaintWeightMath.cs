using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed class LTPaintWeightInput:IDisposable
    {
        public Rect rect;public int size;
        public LTPaintWeightMath.Stamp[] stamps;
        public LTPaintWeightMath.RoadHit[] hits;
        public float[] masks;
        public Vector2[] coordinates; // world grid first, then exact managed stamp-local coordinates
        public LTPaintTerrainSnapshot terrain;
        internal LTPaintWeightInputPool owner;
        internal long poolGeneration;
        public void Dispose()=>owner?.Return(this);
    }
    public interface ILTPaintTerrainData
    {
        LTPaintWeightMath.Tile Tile(int i);
        Vector3 Vertex(int i);
        Vector3 Normal(int i);
        int Index(int i);
        Vector2Int Bin(int i);
        int Triangle(int i);
    }
    // Pure value-only paint loop. One pixel owns all twelve weights: stamp order,
    // including primary BEFORE wheel, remains sequential within the pixel.
    public static class LTPaintWeightMath
    {
        public struct Tile {public Rect rect;public int valid,axis,firstBin,hasNormals;}
        public struct Terrain {public float sizeX,sizeZ,width,depth,minX,minZ;public int countX,countZ;}
        public struct Filter
        {
            public int enabled;public Vector2 range;public float feather;
            public float Evaluate(float value)=>enabled!=0?LTPaintMath.RangeWeight(value,range,feather):1;
        }
        public struct Stamp
        {
            public int kind,slot,wheelSlot,hitOffset,maskOffset,localOffset; // ordinary=0, road=1, circle junction=2, precomputed coverage (asphalt/mesh junction)=3
            public Matrix4x4 inverse;
            public Vector2 size;
            public int rectangle,noiseEnabled,noiseSeed;
            public float strength,falloff,noiseSize,noiseStrength,noiseThreshold,noiseSoftness;
            public Filter height,slope,curve;
            public float curveRadius;
            public LTRoadHeightKernel.Road road;
            public Vector3 centre;public float coreRadius,blend;
        }
        public struct RoadHit
        {
            public float lateral,radial,distance,variation;
            public LTRoadMath.Hit Expand()=>new LTRoadMath.Hit{lateral=lateral,radialDistance=radial,distance=distance,variationStrength=variation};
            public static RoadHit From(LTRoadMath.Hit h)=>new RoadHit{lateral=h.lateral,radial=h.radialDistance,distance=h.distance,variation=h.variationStrength};
        }
        public struct Pixel
        {
            public Color32 first,second,third;
            public byte guard;
        }
        public struct Weights
        {
            public Vector4 a,b,c;
            public void Composite(int slot,float alpha)
            {
                if(slot<0||slot>=12)return;
                alpha=Mathf.Clamp01(alpha);float keep=1-alpha;
                a*=keep;b*=keep;c*=keep;
                if(slot<4)a[slot]+=alpha;else if(slot<8)b[slot-4]+=alpha;else c[slot-8]+=alpha;
            }
            static byte Pack(float value,ref byte guard)
            {
                float scaled=Mathf.Clamp01(value)*255;
                // Rare quantization boundaries use the original complete CPU pixel.
                // This is a fallback, not a widened mask-comparison tolerance.
                if(float.IsNaN(value)||float.IsInfinity(value)||Math.Abs(scaled-((float)Math.Floor(scaled)+.5f))<.002f)guard=1;
                return (byte)Mathf.RoundToInt(scaled);
            }
            static Color32 Pack(Vector4 v,ref byte guard)=>new Color32(Pack(v.x,ref guard),Pack(v.y,ref guard),Pack(v.z,ref guard),Pack(v.w,ref guard));
            public Pixel Pack(){byte guard=0;var p=new Pixel{first=Pack(a,ref guard),second=Pack(b,ref guard),third=Pack(c,ref guard)};p.guard=guard;return p;}
        }
        public static bool Surface<T>(Terrain t,T data,float x,float z,out float height,out float slope) where T:struct,ILTPaintTerrainData
        {
            height=slope=0;if(t.countX<=0||t.countZ<=0)return false;
            x=Mathf.Clamp(x,t.minX,t.minX+t.sizeX);z=Mathf.Clamp(z,t.minZ,t.minZ+t.sizeZ);
            int tx=Mathf.Clamp(Mathf.FloorToInt((x-t.minX)/t.width),0,t.countX-1),tz=Mathf.Clamp(Mathf.FloorToInt((z-t.minZ)/t.depth),0,t.countZ-1);
            var tile=data.Tile(tz*t.countX+tx);if(tile.valid==0)return false;
            var bin=data.Bin(tile.firstBin+LTPaintMath.TerrainBin(z,tile.rect.yMin,t.depth,tile.axis)*tile.axis+LTPaintMath.TerrainBin(x,tile.rect.xMin,t.width,tile.axis));
            float top=float.NegativeInfinity;bool found=false;Vector3 normal=Vector3.up;
            for(int i=0;i<bin.y;i++)
            {
                int at=data.Triangle(bin.x+i),ia=data.Index(at),ib=data.Index(at+1),ic=data.Index(at+2);
                var a=data.Vertex(ia);var b=data.Vertex(ib);var c=data.Vertex(ic);
                if(!LTPaintMath.TriangleWeights(new Vector2(x,z),a,b,c,out var weights))continue;
                float u=weights.x,v=weights.y,w=weights.z;
                float y=u*a.y+v*b.y+w*c.y;if(y<top)continue;
                top=y;found=true;
                normal=tile.hasNormals!=0?(u*data.Normal(ia)+v*data.Normal(ib)+w*data.Normal(ic)).normalized:Vector3.Cross(b-a,c-a).normalized;
            }
            if(!found)return false;
            height=top;slope=Mathf.Acos(Mathf.Clamp(Mathf.Abs(normal.y),0,1))*Mathf.Rad2Deg;return true;
        }
        public static float Shape(Stamp s,Vector3 point,out Vector2 uv,out Vector2 local)
        {
            var p=s.inverse.MultiplyPoint3x4(point);local=new Vector2(p.x,p.z);
            var scaled=new Vector2(p.x/Mathf.Max(.01f,s.size.x)*2,p.z/Mathf.Max(.01f,s.size.y)*2);
            uv=scaled*.5f+new Vector2(.5f,.5f);
            return LTPaintMath.Coverage(scaled,s.rectangle!=0,s.falloff,s.strength);
        }
        public static Pixel Evaluate<S,H,M,C,T>(int pixel,int stampCount,S stamps,H hits,M masks,C coordinates,T data,Terrain terrain)
            where S:struct,ILTHeightBuffer<Stamp> where H:struct,ILTHeightBuffer<RoadHit>
            where M:struct,ILTHeightBuffer<float> where C:struct,ILTHeightBuffer<Vector2> where T:struct,ILTPaintTerrainData
        {
            var xy=coordinates[pixel];var point=new Vector3(xy.x,0,xy.y);
            var weights=new Weights{a=new Vector4(1,0,0,0)};
            bool sampled=false,found=false;float height=0,slope=0;
            // Common radii are adjacent in authoring; reuse the last one without
            // allocating per-pixel scratch. Nonadjacent radii remain correct.
            float lastRadius=float.NaN,curvature=0;bool curveFound=false;
            for(int i=0;i<stampCount;i++)
            {
                var s=stamps[i];
                // Exact immutable contour mask prepared alongside the spline hit.
                // Composite still runs in ordered Burst pixel jobs; no Unity objects.
                if(s.kind==3){weights.Composite(s.slot,masks[s.maskOffset+pixel]);continue;}
                if(s.kind==1)
                {
                    var hit=hits[s.hitOffset+pixel];if(hit.radial<0)continue;
                    LTRoadHeightKernel.Paint(s.road,hit.Expand(),point.x,point.z,out float ground,out float wheel);
                    weights.Composite(s.slot,ground);weights.Composite(s.wheelSlot,wheel);continue;
                }
                if(s.kind==2)
                {
                    float dx=point.x-s.centre.x,dz=point.z-s.centre.z;
                    weights.Composite(s.slot,1-Mathf.SmoothStep(0,1,(Mathf.Sqrt(dx*dx+dz*dz)-s.coreRadius)/Math.Max(.001f,s.blend)));continue;
                }
                // Matrix cancellation at large world coordinates differs between
                // Mono and Burst. Reuse the original managed projection, as roads do.
                var local=coordinates[s.localOffset+pixel];
                var scaled=new Vector2(local.x/Mathf.Max(.01f,s.size.x)*2,local.y/Mathf.Max(.01f,s.size.y)*2);
                float alpha=LTPaintMath.Coverage(scaled,s.rectangle!=0,s.falloff,s.strength);if(alpha<=0)continue;
                if(s.maskOffset>=0)alpha*=masks[s.maskOffset+pixel];
                if(s.noiseEnabled!=0)alpha*=LTPaintMath.NoiseCoverage(local,s.noiseSize,s.noiseSeed,s.noiseStrength,s.noiseThreshold,s.noiseSoftness);
                if(alpha<=0)continue;
                if(s.height.enabled!=0||s.slope.enabled!=0||s.curve.enabled!=0)
                {
                    if(!sampled){found=Surface(terrain,data,point.x,point.z,out height,out slope);sampled=true;}
                    if(!found)continue;
                    alpha*=s.height.Evaluate(height)*s.slope.Evaluate(slope);
                    if(alpha>0&&s.curve.enabled!=0)
                    {
                        float radius=Mathf.Max(.1f,s.curveRadius);
                        radius=Mathf.Min(radius,Mathf.Min(Mathf.Min(point.x-terrain.minX,terrain.minX+terrain.sizeX-point.x),Mathf.Min(point.z-terrain.minZ,terrain.minZ+terrain.sizeZ-point.z)));
                        if(radius!=lastRadius)
                        {
                            lastRadius=radius;curvature=0;curveFound=true;
                            if(radius>=.1f)
                            {
                                float left=0,right=0,back=0,front=0;
                                curveFound=Surface(terrain,data,point.x-radius,point.z,out left,out _)&&
                                    Surface(terrain,data,point.x+radius,point.z,out right,out _)&&
                                    Surface(terrain,data,point.x,point.z-radius,out back,out _)&&
                                    Surface(terrain,data,point.x,point.z+radius,out front,out _);
                                if(curveFound)curvature=LTPaintMath.Curvature(height,left,right,back,front,radius);
                            }
                        }
                        if(!curveFound)continue;alpha*=s.curve.Evaluate(curvature);
                    }
                }
                weights.Composite(s.slot,alpha);
            }
            return weights.Pack();
        }
    }
    public sealed class LTPaintTerrainSnapshot
    {
        public LTPaintWeightMath.Terrain terrain;
        public LTPaintWeightMath.Tile[] tiles=Array.Empty<LTPaintWeightMath.Tile>();
        public Vector3[] vertices=Array.Empty<Vector3>(),normals=Array.Empty<Vector3>();
        public int[] indices=Array.Empty<int>(),triangles=Array.Empty<int>();
        public Vector2Int[] bins=Array.Empty<Vector2Int>();
        public struct Access:ILTPaintTerrainData
        {
            public LTPaintTerrainSnapshot value;
            public LTPaintWeightMath.Tile Tile(int i)=>value.tiles[i];
            public Vector3 Vertex(int i)=>value.vertices[i];public Vector3 Normal(int i)=>value.normals[i];
            public int Index(int i)=>value.indices[i];public Vector2Int Bin(int i)=>value.bins[i];public int Triangle(int i)=>value.triangles[i];
        }
    }
}
