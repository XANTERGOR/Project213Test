using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Display-only isolines of the real spline distance field. In particular, tight
    // bends don't draw crossed offset ribbons. Never reads/modifies generated meshes.
    public static class LTRoadFoundationOutline
    {
        public sealed class Lines
        {
            public Vector3[] shoulder=Array.Empty<Vector3>(),transition=Array.Empty<Vector3>();
            public float cellSize;
        }
        struct Point {public Vector2 position;public float inner,outer;}
        sealed class BudgetExceeded:Exception { }
        const int MaxCells=32768;
        public static Lines Build(LTRoadMath.Snapshot road,bool cutStart,bool cutEnd)
        {
            if(road==null)throw new ArgumentNullException(nameof(road));
            float radius=road.FoundationRadius;
            double area=Math.Min((double)road.bounds.width*road.bounds.height,2d*radius*road.length+Math.PI*radius*radius);
            float step=Math.Max(Math.Min(.35f,road.width/8),.05f);
            step=Math.Max(step,(float)Math.Sqrt(area/(MaxCells*.5)));
            for(int attempt=0;attempt<5;attempt++,step*=2)
            {
                try{return BuildGrid(road,cutStart,cutEnd,step);}
                catch(BudgetExceeded){ }
            }
            // A pathological path must not stall the editor or return a partial outline.
            return new Lines{cellSize=step};
        }
        static Lines BuildGrid(LTRoadMath.Snapshot road,bool cutStart,bool cutEnd,float step)
        {
            var origin=new Vector2(road.bounds.xMin-step,road.bounds.yMin-step);
            int axis=1;double extent=Math.Max(road.bounds.width,road.bounds.height)+2d*step;
            while(axis*step<extent){if(axis>=1<<22)throw new BudgetExceeded();axis*=2;}
            var points=new Dictionary<long,Point>();var inner=new List<Vector3>();var outer=new List<Vector3>();int cells=0;
            var first=road.samples[0];var last=road.samples[road.samples.Count-1];
            Point At(int x,int z)
            {
                long key=((long)x<<32)|(uint)z;if(points.TryGetValue(key,out var value))return value;
                var p=origin+new Vector2(x*step,z*step);value=new Point{position=p,inner=step,outer=step};
                if(road.TrySample(p.x,p.y,out var hit))
                {
                    float clip=float.NegativeInfinity;
                    // Extend each cap's negative field into the interior. A sudden
                    // jump from -width to +epsilon would shift an interpolated cut
                    // outside the socket plane. Interior values stay strictly negative
                    // even when a later bend returns behind that plane.
                    if(cutStart)
                    {
                        float plane=-Vector3.Dot(new Vector3(p.x-first.position.x,0,p.y-first.position.z),new Vector3(-first.right.z,0,first.right.x));
                        clip=hit.distance<=0||plane<0?plane:-hit.distance;
                    }
                    if(cutEnd)
                    {
                        float plane=Vector3.Dot(new Vector3(p.x-last.position.x,0,p.y-last.position.z),new Vector3(-last.right.z,0,last.right.x));
                        clip=Math.Max(clip,hit.distance>=road.length||plane<0?plane:hit.distance-road.length);
                    }
                    road.FoundationWidths(hit,out float shoulder,out float blend);
                    float d=hit.radialDistance-road.HalfWidth(hit)-shoulder;
                    value.inner=Math.Max(d,clip);value.outer=Math.Max(d-blend,clip);
                }
                points.Add(key,value);return value;
            }
            Vector3 Height(Vector2 p)
            {
                if(!road.TrySample(p.x,p.y,out var hit))throw new ArgumentException("Invalid road outline coordinate.");
                // Like the junction gizmo: reference surface, not asphalt offset or burial.
                return new Vector3(p.x,road.SurfaceHeight(hit,hit.lateral),p.y);
            }
            void Triangle(Point a,Point b,Point c,bool transition,List<Vector3> result)
            {
                int count=0;Vector2 p=default,q=default;
                void Edge(Point u,Point v)
                {
                    float x=transition?u.outer:u.inner,y=transition?v.outer:v.inner;
                    if((x<0)==(y<0))return;
                    var point=Vector2.Lerp(u.position,v.position,x/(x-y));
                    if(count++==0)p=point;else q=point;
                }
                Edge(a,b);Edge(b,c);Edge(c,a);
                if(count==2&&(p-q).sqrMagnitude>1e-12f){result.Add(Height(p));result.Add(Height(q));}
            }
            void Visit(int x,int z,int size)
            {
                var areaRect=new Rect(origin.x+x*step,origin.y+z*step,size*step,size*step);
                if(!road.Intersects(areaRect))return;
                if(size>1)
                {int h=size/2;Visit(x,z,h);Visit(x+h,z,h);Visit(x,z+h,h);Visit(x+h,z+h,h);return;}
                if(++cells>MaxCells)throw new BudgetExceeded();
                var a=At(x,z);var b=At(x+1,z);var c=At(x,z+1);var d=At(x+1,z+1);
                Triangle(a,b,d,false,inner);Triangle(a,d,c,false,inner);
                if(road.HasFoundationBlend){Triangle(a,b,d,true,outer);Triangle(a,d,c,true,outer);}
            }
            Visit(0,0,axis);return new Lines{shoulder=inner.ToArray(),transition=outer.ToArray(),cellSize=step};
        }
    }
}
