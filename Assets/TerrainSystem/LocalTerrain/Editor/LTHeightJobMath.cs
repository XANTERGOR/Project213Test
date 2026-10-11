using System;
using System.Collections.Generic;
using UnityEngine;
namespace LocalTerrainPrototype
{
    internal static class LTHeightJobMath
    {
        public struct Buffer<T>:ILTHeightBuffer<T>
        {public T[] values;public T this[int index]=>values[index];}
        public struct Source
        {
            public int resolution,x,z,width;public Vector3 size;
            public float Sample<B>(B values,float px,float pz) where B:struct,ILTHeightBuffer<float>
            {
                float fx=Mathf.Clamp01(px/size.x)*(resolution-1),fz=Mathf.Clamp01(pz/size.z)*(resolution-1);
                int ix=Mathf.Min(Mathf.FloorToInt(fx),resolution-2),iz=Mathf.Min(Mathf.FloorToInt(fz),resolution-2);
                float tx=fx-ix,tz=fz-iz;int i=(iz-z)*width+ix-x;
                return Mathf.Lerp(Mathf.Lerp(values[i],values[i+1],tx),Mathf.Lerp(values[i+width],values[i+width+1],tx),tz);
            }
        }
        public struct Point { public Vector2 position;public int first,count; }
        public struct Command
        {
            public int kind,road;public float target,weight,blend,reference;public LTStampOperation operation;
        }
        public static float Apply<R,S,N>(float height,Vector2 p,Command cmd,R roads,S samples,N nodes)
            where R:struct,ILTHeightBuffer<LTRoadHeightKernel.Road> where S:struct,ILTHeightBuffer<LTRoadMath.Sample>
            where N:struct,ILTHeightBuffer<LTRoadHeightKernel.Node>
            =>Apply(height,p,cmd,roads,samples,nodes,out _);
        public static float Apply<R,S,N>(float height,Vector2 p,Command cmd,R roads,S samples,N nodes,out bool needsReference)
            where R:struct,ILTHeightBuffer<LTRoadHeightKernel.Road> where S:struct,ILTHeightBuffer<LTRoadMath.Sample>
            where N:struct,ILTHeightBuffer<LTRoadHeightKernel.Node>
        {
            needsReference=false;
            if(cmd.kind==1)
            {
                float result=LTRoadHeightKernel.ApplyDetailed(roads[cmd.road],samples,nodes,p.x,p.y,height,out var trace);
                needsReference=trace.ambiguous!=0;return result;
            }
            if(cmd.kind==2)return Mathf.Lerp(height,Mathf.Max(height,cmd.target),cmd.weight);
            if(cmd.kind==3)return Mathf.Lerp(height,cmd.target,cmd.weight);
            return LTBlend.Apply(height,cmd.target,cmd.weight,cmd.operation,cmd.blend,cmd.reference);
        }
        // Exact coordinates shared with planner, emitter and LOD snapshot sampler.
        public static IEnumerable<Vector2> BasePoints(Rect r,int baseCells)
        {
            int count=1<<LTStampMesh.Depth(1,1f/baseCells);int step=LTBalancedForest.N/count;
            for(int z=0;z<=count;z++)for(int x=0;x<=count;x++)yield return Position(r,x*step,z*step);
        }
        public static IEnumerable<Vector2> FinePoints(Rect r,List<Vector3Int> leaves)
        {
            foreach(var p in leaves)for(int z=0;z<3;z++)for(int x=0;x<3;x++)
                yield return Position(r,p.x+x*(p.z*.5f),p.y+z*(p.z*.5f));
        }
        // LOD0 needs corners plus the balanced forest's actual stitch vertices,
        // not the complete nine-point coarsening stencil. Keep the original
        // coordinate arithmetic and relative order. A stitched cell may use a
        // centre fan; requesting its centre conservatively also covers alternate
        // diagonals without evaluating their material/road callback twice.
        // Cut intersections remain on the existing on-demand reference path.
        public static IEnumerable<Vector2> EmitPoints(Rect r,LTBalancedForest.StitchSnapshot stitches)
            =>EmitPoints(r,null,null,stitches);
        public static IEnumerable<Vector2> EmitPoints(Rect r,List<Vector3Int> leaves,Func<Vector3Int,int,bool> midpoint,
            LTBalancedForest.StitchSnapshot stitches=null)
        {
            int count=stitches!=null?stitches.Count:leaves.Count;
            for(int i=0;i<count;i++)
            {
                var p=stitches!=null?stitches.Cell(i):leaves[i];
                int mask=stitches!=null?stitches.Mask(i):0;
                if(stitches==null)for(int side=0;side<4;side++)if(midpoint(p,side))mask|=1<<side;
                for(int z=0;z<3;z++)for(int x=0;x<3;x++)
                {
                    if(x==1&&z==1){if(mask==0)continue;}
                    else if(x==1){if((mask&(z==0?4:8))==0)continue;}
                    else if(z==1){if((mask&(x==0?1:2))==0)continue;}
                    yield return Position(r,p.x+x*(p.z*.5f),p.y+z*(p.z*.5f));
                }
            }
        }
        static Vector2 Position(Rect r,float x,float z)=>new Vector2((float)(r.xMin+(double)x/LTBalancedForest.N*r.width),(float)(r.yMin+(double)z/LTBalancedForest.N*r.height));
    }
}
