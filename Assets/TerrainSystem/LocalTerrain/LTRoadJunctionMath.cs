using System;
using System.Collections.Generic;
using UnityEngine;
namespace LocalTerrainPrototype
{
    // A deliberately explicit, single-height junction; never inferred from crossings.
    public static class LTRoadJunctionMath
    {
        public struct Port { public Vector3 centre,right;public float width; }
        public sealed class Snapshot
        {
            public Vector3 centre;public Vector2[] outline;public Rect bounds;
            public float coreRadius,blend,cellSize;public int hash;
            public float Weight(float x,float z)
            {
                float dx=x-centre.x,dz=z-centre.z;
                return 1-Mathf.SmoothStep(0,1,(Mathf.Sqrt(dx*dx+dz*dz)-coreRadius)/Math.Max(.001f,blend));
            }
            public float ApplyHeight(float x,float z,float height)=>Mathf.Lerp(height,centre.y,Weight(x,z));
            public bool Intersects(Rect rect)
            {
                float x=Math.Max(rect.xMin,Math.Min(rect.xMax,centre.x))-centre.x;
                float z=Math.Max(rect.yMin,Math.Min(rect.yMax,centre.z))-centre.z;
                return x*x+z*z<=(coreRadius+blend)*(coreRadius+blend);
            }
        }
        public static Port MakePort(Vector3 centre,Vector3 anchor,float radius,float width)
        {
            if(!Finite(radius)||radius<=0||!Finite(width)||width<=0||!Finite(anchor.x)||!Finite(anchor.y)||!Finite(anchor.z))throw new ArgumentException("Некорректный радиус или направление въезда.");
            var direction=new Vector3(anchor.x-centre.x,0,anchor.z-centre.z);
            if(direction.sqrMagnitude<=(radius+.05f)*(radius+.05f))throw new ArgumentException("Следующая точка дороги должна находиться за радиусом перекрёстка.");
            direction.Normalize();return new Port{centre=centre+direction*radius,right=new Vector3(direction.z,0,-direction.x),width=width};
        }
        public static Snapshot Build(Vector3 centre,IReadOnlyList<Port> ports,float blend,float cellSize)
        {
            if(ports==null||ports.Count<2||ports.Count>8)throw new ArgumentException("Перекрёсток поддерживает 2–8 подключений начала/конца дороги.");
            if(!Finite(centre.x)||!Finite(centre.y)||!Finite(centre.z)||!Finite(blend)||blend<0||!Finite(cellSize)||cellSize<=0)throw new ArgumentException("Некорректные параметры перекрёстка.");
            var points=new List<Vector2>();float radius=0;int hash=centre.GetHashCode();
            foreach(var port in ports)
            {
                if(!Finite(port.width)||port.width<=0||!Finite(port.centre.x)||!Finite(port.centre.y)||!Finite(port.centre.z)||
                    !Finite(port.right.x)||!Finite(port.right.y)||!Finite(port.right.z)||Mathf.Abs(port.right.sqrMagnitude-1)>.001f||Mathf.Abs(port.right.y)>.001f||
                    Mathf.Abs(port.centre.y-centre.y)>.001f)throw new ArgumentException("Подключения перекрёстка должны иметь общую высоту и конечные горизонтальные направления.");
                for(int s=-1;s<=1;s+=2)
                {var v=port.centre+port.right*(port.width*.5f*s);var p=new Vector2(v.x,v.z);points.Add(p);radius=Math.Max(radius,(p-new Vector2(centre.x,centre.z)).magnitude);}
                hash=unchecked(hash*397^port.centre.GetHashCode());hash=unchecked(hash*397^port.width.GetHashCode());
                hash=unchecked(hash*397^port.right.GetHashCode());
            }
            points.Sort((a,b)=>a.x!=b.x?a.x.CompareTo(b.x):a.y.CompareTo(b.y));
            var hull=new List<Vector2>();
            foreach(var p in points){while(hull.Count>=2&&Cross(hull[hull.Count-2],hull[hull.Count-1],p)<=.00001f)hull.RemoveAt(hull.Count-1);hull.Add(p);}
            int lower=hull.Count;
            for(int i=points.Count-2;i>=0;i--){var p=points[i];while(hull.Count>lower&&Cross(hull[hull.Count-2],hull[hull.Count-1],p)<=.00001f)hull.RemoveAt(hull.Count-1);hull.Add(p);}
            hull.RemoveAt(hull.Count-1);
            // Every port must remain a complete boundary edge. Otherwise two mouths
            // overlap, or a too-wide road hides another: do not publish overlapping meshes.
            foreach(var port in ports)
            {
                var a=port.centre-port.right*port.width*.5f;var b=port.centre+port.right*port.width*.5f;bool found=false;
                for(int i=0;i<hull.Count;i++)
                {var u=hull[i];var v=hull[(i+1)%hull.Count];if((u-new Vector2(a.x,a.z)).sqrMagnitude<1e-6f&&(v-new Vector2(b.x,b.z)).sqrMagnitude<1e-6f||(u-new Vector2(b.x,b.z)).sqrMagnitude<1e-6f&&(v-new Vector2(a.x,a.z)).sqrMagnitude<1e-6f)found=true;}
                if(!found)throw new ArgumentException("Въезды перекрываются: увеличьте радиус перекрёстка или разведите направления дорог.");
            }
            for(int i=0;i<hull.Count;i++)if(Cross(hull[i],hull[(i+1)%hull.Count],new Vector2(centre.x,centre.z))<=.00001f)
                throw new ArgumentException("Дороги должны окружать центр узла; для поворота одной дороги используйте её сплайн.");
            hash=unchecked(hash*397^blend.GetHashCode());hash=unchecked(hash*397^cellSize.GetHashCode());
            return new Snapshot{centre=centre,outline=hull.ToArray(),coreRadius=radius,blend=blend,cellSize=cellSize,hash=hash,
                bounds=new Rect(centre.x-radius-blend,centre.z-radius-blend,2*(radius+blend),2*(radius+blend))};
        }
        // Preserve every module end subdivision in every junction LOD. Never leave
        // a long centre edge opposite several shorter road edges (a raster T-junction).
        public static Snapshot WithBoundaryPoints(Snapshot source,IReadOnlyList<Vector3> points)
        {
            if(points==null||points.Count==0)return source;
            var outline=new List<Vector2>();
            for(int i=0;i<source.outline.Length;i++)
            {
                var a=source.outline[i];var b=source.outline[(i+1)%source.outline.Length];var ab=b-a;
                var edge=new List<(float t,Vector2 p)>();outline.Add(a);
                foreach(var p in points)
                {
                    var q=new Vector2(p.x,p.z);float t=Vector2.Dot(q-a,ab)/ab.sqrMagnitude;
                    if(t<=.00001f||t>=.99999f||(q-(a+ab*t)).sqrMagnitude>1e-5f)continue;
                    edge.Add((t,q));
                }
                edge.Sort((x,y)=>x.t.CompareTo(y.t));
                foreach(var p in edge)if((p.p-outline[outline.Count-1]).sqrMagnitude>1e-8f)outline.Add(p.p);
            }
            return new Snapshot{centre=source.centre,outline=outline.ToArray(),bounds=source.bounds,
                coreRadius=source.coreRadius,blend=source.blend,cellSize=source.cellSize,hash=source.hash};
        }
        static float Cross(Vector2 a,Vector2 b,Vector2 c)=>(b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x);
        static bool Finite(float x)=>!float.IsNaN(x)&&!float.IsInfinity(x);
    }
}
