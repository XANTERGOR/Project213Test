using System;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Reuse strict Fuse's geometric rim matching. Only owned output positions move;
    // all authored indices, vertex splits, shading, UV and color channels survive.
    public static class LTRoadJunctionSeams
    {
        public sealed class Port
        {
            internal LTRoadModuleFuse.Plan plan;
            public int PointCount=>plan.PointCount;
        }
        static LTRoadMesh.Chunk Positions(LTRoadMesh.Chunk source,Matrix4x4 matrix)
        {
            if(source?.vertices==null||source.triangles==null||source.triangles.Length%3!=0||
                source.triangles.Any(i=>i<0||i>=source.vertices.Length))throw new InvalidOperationException("Некорректная сетка въезда.");
            var vertices=source.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
            if(vertices.Any(p=>!LTRoadMesh.Finite(p)))throw new InvalidOperationException("Неконечные координаты въезда.");
            return new LTRoadMesh.Chunk{vertices=vertices,triangles=source.triangles};
        }
        public static Port Create(LTRoadMesh.Chunk reference,Matrix4x4 toSocket,float tolerance,float width)
        {
            if(!LTRoadMesh.Finite(tolerance)||tolerance<.00001f||tolerance>.01f||!LTRoadMesh.Finite(width)||width<=0)
                throw new InvalidOperationException("Проверьте ширину въезда и допуск Fuse (0.01–10 мм).");
            var local=Positions(reference,toSocket);
            if(local.vertices.Any(p=>p.z>tolerance))throw new InvalidOperationException("Синяя Z маркера должна смотреть наружу; плоскость въезда должна быть на краю, не внутри перекрёстка.");
            var profile=LTRoadModuleFuse.Read(local,0,tolerance);
            if(Math.Abs(profile.points.Min(p=>p.x)+width*.5f)>tolerance||Math.Abs(profile.points.Max(p=>p.x)-width*.5f)>tolerance)
                throw new InvalidOperationException("Маркер должен быть в центре торца; Width должен совпадать с шириной его контура.");
            return new Port{plan=new LTRoadModuleFuse.Plan{points=profile.points,edges=profile.edges,tolerance=tolerance,planeTolerance=tolerance}};
        }
        public static LTRoadMesh.Chunk Snap(LTRoadMesh.Chunk source,Matrix4x4 toSocket,Port port)
        {
            var local=Positions(source,toSocket);var plan=port.plan;
            var profile=LTRoadModuleFuse.Read(local,0,plan.planeTolerance);var map=LTRoadModuleFuse.Match(profile,plan);
            var vertices=(Vector3[])source.vertices.Clone();
            // Explicit affine inverse also runs in the managed geometry test harness.
            var origin=toSocket.MultiplyPoint3x4(Vector3.zero);var x=toSocket.MultiplyVector(Vector3.right);
            var y=toSocket.MultiplyVector(Vector3.up);var z=toSocket.MultiplyVector(Vector3.forward);
            var yz=Vector3.Cross(y,z);var zx=Vector3.Cross(z,x);var xy=Vector3.Cross(x,y);float det=Vector3.Dot(x,yz);
            if(!LTRoadMesh.Finite(det)||det<=1e-8f)throw new InvalidOperationException("Нулевой/отражённый transform стыка.");
            for(int i=0;i<vertices.Length;i++)
            {
                var p=local.vertices[i];if(Math.Abs(p.z)>plan.planeTolerance)continue;
                var q=p;q.z=0;int group=profile.groups[i];
                if(group>=0){var target=plan.points[map[group]];q.x=target.x;q.y=target.y;}
                if((q-p).sqrMagnitude>plan.tolerance*plan.tolerance*1.001f)
                    throw new InvalidOperationException("Стык перекрёстка: сдвиг превышает допуск Fuse. Треугольники не перестраиваются.");
                // Avoid round trips on exact points (important for repeated preparation).
                if(!q.Equals(p)){var d=q-origin;vertices[i]=new Vector3(Vector3.Dot(d,yz),Vector3.Dot(d,zx),Vector3.Dot(d,xy))/det;}
            }
            for(int i=0;i<source.triangles.Length;i+=3)
            {
                int a=source.triangles[i],b=source.triangles[i+1],c=source.triangles[i+2];
                if(vertices[a].Equals(source.vertices[a])&&vertices[b].Equals(source.vertices[b])&&vertices[c].Equals(source.vertices[c]))continue;
                var before=Cross(source.vertices[a],source.vertices[b],source.vertices[c]);var after=Cross(vertices[a],vertices[b],vertices[c]);
                double area=Dot(before,before);
                if(area>0&&(Dot(before,after)<=0||Dot(after,after)<area*1e-8))
                    throw new InvalidOperationException("Fuse въезда схлопывает или переворачивает грань. Исходная сетка сохранена.");
            }
            var result=new LTRoadMesh.Chunk{vertices=vertices,triangles=source.triangles,submeshes=source.submeshes,
                normals=source.normals,tangents=source.tangents,uv=source.uv,uv2=source.uv2,uv3=source.uv3,uv4=source.uv4,colors=source.colors};
            result.hash=LTRoadMesh.ContentHash(result);return result;
        }
        static (double x,double y,double z) Cross(Vector3 a,Vector3 b,Vector3 c)
        {
            double x=(double)b.x-a.x,y=(double)b.y-a.y,z=(double)b.z-a.z,u=(double)c.x-a.x,v=(double)c.y-a.y,w=(double)c.z-a.z;
            return(y*w-z*v,z*u-x*w,x*v-y*u);
        }
        static double Dot((double x,double y,double z) a,(double x,double y,double z) b)=>a.x*b.x+a.y*b.y+a.z*b.z;
    }
}
