using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Rasterize the UNION of the complete LOD0 footprint, not mesh index boundaries.
    // Consequently UV splits, submeshes, thick sides and module/section joins cannot
    // introduce false rims. Empty regions connected to the frame are exterior;
    // enclosed empty regions are holes. Geometry and all authored channels are read-only.
    public static class LTRoadBlendMask
    {
        public const int MaxPixels = 4 * 1024 * 1024, MaxDimension = 8192;
        public sealed class Data
        {
            public int width, height, holePixels;
            public float cell;
            public Vector4 rect;
            public float[] exterior, holes;
            public Vector2 Sample(float x, float z)
            {
                int ix=(int)Math.Round((x-rect.x)/cell), iz=(int)Math.Round((z-rect.y)/cell);
                ix=Math.Max(0,Math.Min(width-1,ix));iz=Math.Max(0,Math.Min(height-1,iz));
                return new Vector2(exterior[iz*width+ix],holes[iz*width+ix]);
            }
        }
        // Optional local window (min X/Z, size X/Z). Callers must keep rendered
        // geometry at least maximum blend width + filter padding inside it.
        public static Data Build(IReadOnlyList<LTRoadMesh.Chunk> chunks, Matrix4x4 roadToMask, float cell, Vector4? window=null)
        {
            if(!Finite(cell)||cell<.01f||cell>1)throw new InvalidOperationException("Шаг маски бленда должен быть от 0.01 до 1 м.");
            var points=new List<Vector3[]>();
            double minX=double.PositiveInfinity,minZ=minX,maxX=double.NegativeInfinity,maxZ=maxX;
            foreach(var chunk in chunks)
            {
                var p=new Vector3[chunk.vertices.Length];
                for(int i=0;i<p.Length;i++)
                {
                    p[i]=roadToMask.MultiplyPoint3x4(chunk.vertices[i]);
                    if(!Finite(p[i].x)||!Finite(p[i].z))throw new InvalidOperationException("Неконечные координаты дороги в маске бленда.");
                    minX=Math.Min(minX,p[i].x);minZ=Math.Min(minZ,p[i].z);maxX=Math.Max(maxX,p[i].x);maxZ=Math.Max(maxZ,p[i].z);
                }
                points.Add(p);
            }
            if(window.HasValue)
            {
                var r=window.Value;
                if(!Finite(r.x)||!Finite(r.y)||!Finite(r.z)||!Finite(r.w)||r.z<=0||r.w<=0)
                    throw new InvalidOperationException("Некорректная область маски бленда.");
                minX=r.x;minZ=r.y;maxX=(double)r.x+r.z;maxZ=(double)r.y+r.w;
            }
            minX=Math.Floor(minX/cell)*cell-2*cell;minZ=Math.Floor(minZ/cell)*cell-2*cell;
            double w=Math.Ceiling((maxX-minX)/cell)+3,h=Math.Ceiling((maxZ-minZ)/cell)+3;
            if(double.IsInfinity(w)||double.IsNaN(w)||w<5||h<5||w>MaxDimension||h>MaxDimension||w*h>MaxPixels)
                throw new InvalidOperationException($"Маска бленда слишком большая ({w:0} × {h:0}); увеличьте «Шаг маски бленда, м» или разделите длинную дорогу. Лимит {MaxPixels:N0} пикселей / {MaxDimension} по стороне.");
            var data=new Data{width=(int)w,height=(int)h,cell=cell,rect=new Vector4((float)minX,(float)minZ,(float)((w-1)*cell),(float)((h-1)*cell))};
            var coverage=new byte[data.width*data.height];
            for(int c=0;c<chunks.Count;c++)
            {
                var indices=chunks[c].triangles;var p=points[c];
                for(int i=0;i<indices.Length;i+=3)Raster(p[indices[i]],p[indices[i+1]],p[indices[i+2]],data,coverage);
            }
            // A clipped branch reaches the raster frame. Keep the frame empty so
            // every outside component is classified as exterior (never a false hole).
            // Its artificial end is beyond the caller's visible blend support.
            if(window.HasValue)
            {
                for(int x=0;x<data.width;x++){coverage[x]=0;coverage[(data.height-1)*data.width+x]=0;}
                for(int z=0;z<data.height;z++){coverage[z*data.width]=0;coverage[z*data.width+data.width-1]=0;}
            }
            // Flood from a guaranteed empty padded frame. Four-connectivity retains
            // small diagonal holes rather than letting them leak through a single corner.
            var queue=new int[coverage.Length];int head=0,tail=0;
            void Push(int i){if(coverage[i]==0){coverage[i]=2;queue[tail++]=i;}}
            Push(0);
            while(head<tail)
            {
                int i=queue[head++],x=i%data.width,z=i/data.width;
                if(x>0)Push(i-1);if(x+1<data.width)Push(i+1);
                if(z>0)Push(i-data.width);if(z+1<data.height)Push(i+data.width);
            }
            foreach(byte b in coverage)if(b==0)data.holePixels++;
            data.exterior=Distance(coverage,2,data);data.holes=Distance(coverage,0,data);
            return data;
        }
        static bool Finite(float f)=>!float.IsNaN(f)&&!float.IsInfinity(f);
        static double Cross(double ax,double az,double bx,double bz)=>ax*bz-az*bx;
        static void Raster(Vector3 a,Vector3 b,Vector3 c,Data d,byte[] coverage)
        {
            // Both windings are intentional: bottom/side faces of solid modules
            // project to the same footprint. A real through-hole has no bottom cap.
            double area=Cross(b.x-a.x,b.z-a.z,c.x-a.x,c.z-a.z);
            if(Math.Abs(area)<1e-12)return;
            int x0=Math.Max(0,(int)Math.Floor((Math.Min(a.x,Math.Min(b.x,c.x))-d.rect.x)/d.cell));
            int x1=Math.Min(d.width-1,(int)Math.Ceiling((Math.Max(a.x,Math.Max(b.x,c.x))-d.rect.x)/d.cell));
            int z0=Math.Max(0,(int)Math.Floor((Math.Min(a.z,Math.Min(b.z,c.z))-d.rect.y)/d.cell));
            int z1=Math.Min(d.height-1,(int)Math.Ceiling((Math.Max(a.z,Math.Max(b.z,c.z))-d.rect.y)/d.cell));
            double sign=area>0?1:-1,tolerance=-Math.Abs(area)*1e-7-1e-10;
            for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)
            {
                int index=z*d.width+x;if(coverage[index]==1)continue;
                double px=d.rect.x+x*(double)d.cell,pz=d.rect.y+z*(double)d.cell;
                if(sign*Cross(b.x-a.x,b.z-a.z,px-a.x,pz-a.z)>=tolerance&&
                   sign*Cross(c.x-b.x,c.z-b.z,px-b.x,pz-b.z)>=tolerance&&
                   sign*Cross(a.x-c.x,a.z-c.z,px-c.x,pz-c.z)>=tolerance)coverage[index]=1;
            }
        }
        // Exact separable squared Euclidean distance transform, O(pixel count).
        // The half-cell correction places the rim halfway between filled/empty samples.
        static float[] Distance(byte[] labels,byte target,Data d)
        {
            const float infinity=1e12f;
            var result=new float[labels.Length];int max=Math.Max(d.width,d.height);
            var f=new float[max];var outLine=new float[max];var sites=new int[max];var cuts=new double[max+1];
            for(int y=0;y<d.height;y++)
            {
                for(int x=0;x<d.width;x++)f[x]=labels[y*d.width+x]==target?0:infinity;
                Transform(f,outLine,d.width,sites,cuts);
                for(int x=0;x<d.width;x++)result[y*d.width+x]=outLine[x];
            }
            for(int x=0;x<d.width;x++)
            {
                for(int y=0;y<d.height;y++)f[y]=result[y*d.width+x];
                Transform(f,outLine,d.height,sites,cuts);
                for(int y=0;y<d.height;y++)result[y*d.width+x]=Math.Min(64,Math.Max(0,((float)Math.Sqrt(outLine[y])-.5f)*d.cell));
            }
            return result;
        }
        static void Transform(float[] f,float[] output,int n,int[] sites,double[] cuts)
        {
            int k=0;sites[0]=0;cuts[0]=double.NegativeInfinity;cuts[1]=double.PositiveInfinity;
            for(int q=1;q<n;q++)
            {
                double s;
                do{s=((double)f[q]+q*(double)q-f[sites[k]]-sites[k]*(double)sites[k])/(2*(q-sites[k]));if(s>cuts[k])break;k--;}while(k>=0);
                k++;sites[k]=q;cuts[k]=s;cuts[k+1]=double.PositiveInfinity;
            }
            k=0;for(int q=0;q<n;q++){while(cuts[k+1]<q)k++;double delta=q-sites[k];output[q]=(float)(delta*delta+f[sites[k]]);}
        }
    }
}
