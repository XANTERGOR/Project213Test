using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Approximate display contours only. Terrain/paint/detail queries use the exact BVH.
    // Cached by immutable LOD0 footprint/settings, never by active display LOD.
    internal static class LTRoadJunctionFoundationGizmos
    {
        sealed class Cache
        {
            public LTRoadFootprint shape;public float shoulder,blend;
            public readonly List<Vector2> core=new List<Vector2>(),outer=new List<Vector2>();
        }
        static readonly ConditionalWeakTable<LTRoadJunction,Cache> caches=new ConditionalWeakTable<LTRoadJunction,Cache>();
        public static void Draw(LTRoadJunction node,LTRoadJunctionMath.Snapshot data,Matrix4x4 matrix)
        {
            var cache=caches.GetValue(node,n=>new Cache());
            if(cache.shape!=data.footprint||cache.shoulder!=data.shoulder||cache.blend!=data.blend)
            {
                cache.shape=data.footprint;cache.shoulder=data.shoulder;cache.blend=data.blend;
                cache.core.Clear();cache.outer.Clear();
                var b=data.bounds;float margin=Mathf.Max(.05f,Mathf.Max(b.width,b.height)/120);
                b=Rect.MinMaxRect(b.xMin-margin,b.yMin-margin,b.xMax+margin,b.yMax+margin);
                int nx=Mathf.Clamp(Mathf.CeilToInt(b.width/.2f),2,160),nz=Mathf.Clamp(Mathf.CeilToInt(b.height/.2f),2,160);
                var points=new Vector2[(nx+1)*(nz+1)];var distance=new float[points.Length];
                float cap=data.shoulder+data.blend+margin*4;
                for(int z=0;z<=nz;z++)for(int x=0;x<=nx;x++)
                {
                    int i=z*(nx+1)+x;points[i]=new Vector2(b.xMin+b.width*x/nx,b.yMin+b.height*z/nz);
                    distance[i]=Mathf.Min(cap,data.footprint.SurfaceDistance(points[i].x,points[i].y,cap));
                }
                void Triangle(int a,int b0,int c,float iso,List<Vector2> result)
                {
                    int count=0;Vector2 first=default,second=default;
                    void Edge(int u,int v)
                    {
                        if((distance[u]<iso)==(distance[v]<iso))return;
                        var p=Vector2.Lerp(points[u],points[v],(iso-distance[u])/(distance[v]-distance[u]));
                        if(count++==0)first=p;else second=p;
                    }
                    Edge(a,b0);Edge(b0,c);Edge(c,a);if(count==2){result.Add(first);result.Add(second);}
                }
                for(int z=0;z<nz;z++)for(int x=0;x<nx;x++)
                {
                    int a=z*(nx+1)+x,b0=a+1,c=a+nx+1,d=c+1;
                    float core=Mathf.Max(.001f,data.shoulder),outer=Mathf.Max(.001f,data.shoulder+data.blend);
                    Triangle(a,b0,d,core,cache.core);Triangle(a,d,c,core,cache.core);
                    if(data.blend>0){Triangle(a,b0,d,outer,cache.outer);Triangle(a,d,c,outer,cache.outer);}
                }
            }
            void Lines(List<Vector2> points)
            {
                for(int i=0;i<points.Count;i+=2)
                    Gizmos.DrawLine(matrix.MultiplyPoint3x4(new Vector3(points[i].x,data.centre.y,points[i].y)),
                        matrix.MultiplyPoint3x4(new Vector3(points[i+1].x,data.centre.y,points[i+1].y)));
            }
            var color=Gizmos.color;Lines(cache.core);Gizmos.color=new Color(color.r,color.g,color.b,color.a*.35f);
            Lines(cache.outer);Gizmos.color=color;
        }
    }
}
