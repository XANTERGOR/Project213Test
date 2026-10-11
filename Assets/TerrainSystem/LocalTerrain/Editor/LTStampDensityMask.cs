using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Immutable, editor-owned max pyramid. Read a texture once per content/remap
    // revision, not once per poll/chunk/LOD. No Unity objects are retained here.
    internal sealed class LTStampDensityMask
    {
        readonly float[][] levels;
        readonly int[] widths,heights;
        public static LTStampDensityMask Capture(Texture2D texture,bool invert,Vector2 range,float power)
        {
            if(!texture||!texture.isReadable)throw new InvalidOperationException("Density mask requires a readable texture.");
            int width=texture.width;
            // Rows avoid a full-resolution temporary RGBA-float image.
            return new LTStampDensityMask(width,texture.height,y=>texture.GetPixels(0,y,width,1),invert,range,power);
        }
        internal LTStampDensityMask(int width,int height,Func<int,Color[]> readRow,bool invert,Vector2 range,float power)
        {
            if(width<1||height<1||readRow==null)throw new ArgumentException("Invalid density mask dimensions or reader.");
            if(!Finite(range.x)||!Finite(range.y)||range.x<0||range.y>1||range.y<=range.x||!Finite(power)||power<.01f)
                throw new ArgumentException("Density mask range must satisfy 0 <= black < white <= 1, and power must be at least 0.01.");
            var values=new float[checked(width*height)];
            for(int y=0;y<height;y++)
            {
                var row=readRow(y);
                if(row==null||row.Length!=width)throw new ArgumentException("Invalid density mask row.");
                for(int x=0;x<width;x++)
                {
                    float v=row[x].r;
                    if(!Finite(v))throw new ArgumentException("Density mask contains a non-finite value.");
                    v=Mathf.Clamp01(v);if(invert)v=1-v;
                    v=Mathf.Clamp01((v-range.x)/(range.y-range.x));
                    values[y*width+x]=power==1?v:Mathf.Pow(v,power);
                }
            }
            var data=new List<float[]>{values};var ws=new List<int>{width};var hs=new List<int>{height};
            while(width>1||height>1)
            {
                int nw=(width+1)/2,nh=(height+1)/2;var next=new float[checked(nw*nh)];
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                {int id=(y/2)*nw+x/2;next[id]=Mathf.Max(next[id],values[y*width+x]);}
                data.Add(next);ws.Add(nw);hs.Add(nh);values=next;width=nw;height=nh;
            }
            levels=data.ToArray();widths=ws.ToArray();heights=hs.ToArray();
        }
        static bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
        // Clamp footprint UVs; texture repeat/filter settings cannot introduce
        // density from the opposite edge. Include bilinear support texels.
        internal float Maximum(Rect uv)
        {
            if(!Finite(uv.xMin)||!Finite(uv.xMax)||!Finite(uv.yMin)||!Finite(uv.yMax))throw new ArgumentException("Invalid density mask UVs.");
            int w=widths[0],h=heights[0];
            int x0=Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(uv.xMin)*w-.5f),0,w-1);
            int x1=Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(uv.xMax)*w-.5f),0,w-1);
            int y0=Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(uv.yMin)*h-.5f),0,h-1);
            int y1=Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(uv.yMax)*h-.5f),0,h-1);
            return Query(levels.Length-1,0,0,x0,y0,x1,y1);
        }
        float Query(int level,int x,int y,int x0,int y0,int x1,int y1)
        {
            if(x>=widths[level]||y>=heights[level])return 0;
            int size=1<<level,left=x*size,bottom=y*size;
            int right=Math.Min(widths[0]-1,left+size-1),top=Math.Min(heights[0]-1,bottom+size-1);
            if(right<x0||left>x1||top<y0||bottom>y1)return 0;
            float max=levels[level][y*widths[level]+x];
            if(max==0||level==0||(left>=x0&&right<=x1&&bottom>=y0&&top<=y1))return max;
            float found=0;
            for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
            {found=Mathf.Max(found,Query(level-1,x*2+dx,y*2+dy,x0,y0,x1,y1));if(found==max)return found;}
            return found;
        }
        internal float Sample(float u,float v)
        {
            int w=widths[0],h=heights[0];
            float x=Mathf.Clamp(Mathf.Clamp01(u)*w-.5f,0,w-1),y=Mathf.Clamp(Mathf.Clamp01(v)*h-.5f,0,h-1);
            int ix=Mathf.FloorToInt(x),iy=Mathf.FloorToInt(y),jx=Math.Min(ix+1,w-1),jy=Math.Min(iy+1,h-1);
            var data=levels[0];
            return Mathf.Lerp(Mathf.Lerp(data[iy*w+ix],data[iy*w+jx],x-ix),Mathf.Lerp(data[jy*w+ix],data[jy*w+jx],x-ix),y-iy);
        }
        internal LTStampMesh.Zone CreateZone(Rect bounds,Matrix4x4 inverse,Vector2 size,LTStampShape shape,float minCell,float maxCell)
        {
            if(!(size.x>0)||!(size.y>0)||!Finite(minCell)||!Finite(maxCell)||minCell<=0||maxCell<minCell)
                throw new ArgumentException("Invalid density mask footprint or cell sizes.");
            Vector2 UV(float x,float z)
            {var p=inverse.MultiplyPoint3x4(new Vector3(x,0,z));return new Vector2(p.x/size.x+.5f,p.z/size.y+.5f);}
            float Minimum(Rect r)
            {
                var lo=new Vector2(float.MaxValue,float.MaxValue);var hi=new Vector2(float.MinValue,float.MinValue);
                for(int i=0;i<4;i++)
                {var uv=UV((i&1)==0?r.xMin:r.xMax,(i&2)==0?r.yMin:r.yMax);lo=Vector2.Min(lo,uv);hi=Vector2.Max(hi,uv);}
                return Mathf.Lerp(maxCell,minCell,Maximum(Rect.MinMaxRect(lo.x,lo.y,hi.x,hi.y)));
            }
            return new LTStampMesh.Zone{bounds=bounds,inverse=inverse,size=size,shape=shape,cellSize=minCell,edgeCellSize=maxCell,
                customCellSize=(x,z)=>{var uv=UV(x,z);return Mathf.Lerp(maxCell,minCell,Sample(uv.x,uv.y));},minimumCellSize=Minimum};
        }
    }
}
