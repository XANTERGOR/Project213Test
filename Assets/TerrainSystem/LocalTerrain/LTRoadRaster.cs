using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Numeric, non-recursive BVH traversal shared by managed tests and Burst.
    // Near ties are deliberately resolved by the legacy managed query, never
    // by widening a visual tolerance. Output is a local segment index only.
    public static class LTRoadRasterKernel
    {
        public static int Segment<S,N>(LTRoadHeightKernel.Road road,S samples,N nodes,float x,float z)
            where S:struct,ILTHeightBuffer<LTRoadMath.Sample>
            where N:struct,ILTHeightBuffer<LTRoadHeightKernel.Node>
        {
            if(float.IsNaN(x)||float.IsNaN(z)||float.IsInfinity(x)||float.IsInfinity(z)||Math.Abs(x)>1e7f||Math.Abs(z)>1e7f)return -2;
            float scale=Math.Max(1,Math.Max(Math.Abs(x),Math.Abs(z)));
            scale=Math.Max(scale,Math.Max(Math.Max(Math.Abs(road.bounds.xMin),Math.Abs(road.bounds.xMax)),
                Math.Max(Math.Abs(road.bounds.yMin),Math.Abs(road.bounds.yMax))));
            float best=float.PositiveInfinity;int segment=-1,index=road.firstNode;bool ambiguous=false;
            while(index<road.endNode)
            {
                var node=nodes[index];
                float band=32*1.192092896e-7f*scale*Math.Max(1,(float)Math.Sqrt(best));
                if(node.DistanceSquared(x,z)>best+band){index=node.escape;continue;}
                if(node.left>=0){index++;continue;}
                for(int i=node.start;i<node.end;i++)
                {
                    var a=samples[i].position;var b=samples[i+1].position;
                    float dx=b.x-a.x,dz=b.z-a.z;
                    float t=Math.Max(0,Math.Min(1,((x-a.x)*dx+(z-a.z)*dz)/(dx*dx+dz*dz)));
                    float rx=x-(a.x+dx*t),rz=z-(a.z+dz*t),d=rx*rx+rz*rz;
                    band=32*1.192092896e-7f*scale*Math.Max(1,(float)Math.Sqrt(Math.Max(d,best)));
                    if(segment>=0&&Math.Abs(d-best)<=band)ambiguous=true;
                    if(d<best||(d==best&&(segment<0||i<segment)))
                    {
                        if(segment<0||best-d>band)ambiguous=false;
                        best=d;segment=i;
                    }
                }
                index++;
            }
            return ambiguous?-2:segment<0?-1:segment-road.firstSample;
        }
    }

    // One request per chunk; primary + wheel UV share the same geometric query.
    // Only needed pixels are sampled: weight bounds + the existing UV block guard.
    public sealed class LTRoadRasterInput
    {
        public readonly Rect rect;
        public readonly int size;
        public readonly LTRoadMath.Snapshot[] sources;
        public readonly LTRoadHeightKernel.Road[] roads;
        public readonly LTRoadMath.Sample[] samples;
        public readonly LTRoadHeightKernel.Node[] nodes;
        public readonly byte[] needed;
        public const int MaxRoads=16;
        public const long MaxBytes=32L*1024*1024;
        public static bool Fits(IReadOnlyList<LTRoadMath.Snapshot> roads,int size)
        {
            if(roads.Count==0||roads.Count>MaxRoads||size<2||size>257)return false;
            long bytes=(long)size*size*roads.Count*24; // preparation/native/result + managed resolved projection cache
            foreach(var road in roads)bytes+=(long)road.samples.Count*256+2048;
            return bytes<=MaxBytes;
        }
        public LTRoadRasterInput(IReadOnlyList<LTRoadMath.Snapshot> sources,Rect rect,int size)
        {
            if(!Fits(sources,size)||rect.width<=0||rect.height<=0)throw new ArgumentException("Road raster request exceeds its bounded layout.");
            this.rect=rect;this.size=size;this.sources=new LTRoadMath.Snapshot[sources.Count];
            roads=new LTRoadHeightKernel.Road[sources.Count];needed=new byte[sources.Count*size*size];
            var points=new List<LTRoadMath.Sample>();var tree=new List<LTRoadHeightKernel.Node>();
            for(int i=0;i<sources.Count;i++)
            {
                var road=sources[i];this.sources[i]=road;roads[i]=road.CopyHeightData(points,tree);
                int offset=i*size*size;var region=road.TextureBakeRegion(rect,size);
                // Also covers all paint pixels (the UV ROI is a superset).
                for(int z=region.yMin;z<region.yMax;z++)for(int x=region.xMin;x<region.xMax;x++)
                {
                    float px=rect.xMin+rect.width*x/(size-1),pz=rect.yMin+rect.height*z/(size-1);
                    if(px>=road.bounds.xMin&&px<=road.bounds.xMax&&pz>=road.bounds.yMin&&pz<=road.bounds.yMax)needed[offset+z*size+x]=1;
                }
                if(!road.HasSplineProjection)continue;
                int blockSize=LTRoadMath.TextureBakeBlockSize;
                for(int z=region.yMin;z<region.yMax;z+=blockSize)for(int x=region.xMin;x<region.xMax;x+=blockSize)
                {
                    var block=new RectInt(x,z,Math.Min(blockSize,region.xMax-x),Math.Min(blockSize,region.yMax-z));
                    if(!road.TextureBakeBlockIntersects(rect,size,block))continue;
                    for(int bz=block.yMin;bz<block.yMax;bz++)for(int bx=block.xMin;bx<block.xMax;bx++)needed[offset+bz*size+bx]=1;
                }
            }
            samples=points.ToArray();nodes=tree.ToArray();
        }
    }

    public sealed class LTRoadRasterSet
    {
        readonly LTRoadMath.Snapshot[] sources;
        readonly Rect rect;
        readonly int size;
        readonly int[] segments;
        Vector2[] projections;
        byte[] resolved;
        public int ReferenceFallbacks {get;private set;}
        public LTRoadRasterSet(LTRoadMath.Snapshot[] sources,Rect rect,int size,int[] segments)
        {
            if(segments.Length!=sources.Length*size*size)throw new ArgumentException("Invalid raster result length.");
            this.sources=sources;this.rect=rect;this.size=size;this.segments=segments;
            foreach(int value in segments)if(value==-2)ReferenceFallbacks++;
        }
        public int RoadIndex(LTRoadMath.Snapshot road)
        {
            // Wheel views share the immutable samples, but retain their own UV scale.
            for(int i=0;i<sources.Length;i++)if(ReferenceEquals(sources[i].samples,road.samples))return i;
            return -1;
        }
        public bool Matches(Rect area,int resolution)=>rect==area&&size==resolution;
        public int SegmentIndex(int road,int pixel)=>segments[road*size*size+pixel];
        public bool TrySample(int road,int pixel,out LTRoadMath.Hit hit)
        {
            hit=default;if(road<0||road>=sources.Length||pixel<0||pixel>=size*size)return false;
            float x=rect.xMin+rect.width*(pixel%size)/(size-1),z=rect.yMin+rect.height*(pixel/size)/(size-1);
            int at=road*size*size+pixel;
            if(segments[at]==-1)return false;
            // Main-thread-only, compact cache. Resolve the original leaf arithmetic
            // once; ground, wheel UV and weight passes reuse the exact projection.
            if(projections==null){projections=new Vector2[segments.Length];resolved=new byte[segments.Length];}
            if(resolved[at]==0)
            {
                if(!sources[road].ResolveRasterSegment(x,z,segments[at],out int chosen,out float t,out float best))
                {segments[at]=-1;return false;}
                segments[at]=chosen;projections[at]=new Vector2(t,best);resolved[at]=1;
            }
            var projection=projections[at];
            hit=sources[road].ResolvedRasterHit(segments[at],x,z,projection.x,projection.y);return true;
        }
    }
}
