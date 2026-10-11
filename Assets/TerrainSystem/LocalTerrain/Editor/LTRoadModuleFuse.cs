using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Position-only seam snapping, on owned copies before bending. Never weld UV/normal splits,
    // remove triangles, cut an inner plane, or fall back to topology surgery implicitly.
    public static class LTRoadModuleFuse
    {
        public const float DefaultTolerance=.0001f;
        internal sealed class Profile
        {
            public Vector2[] points;
            public int[] groups;
            public HashSet<(int,int)> edges;
        }
        public sealed class Plan
        {
            internal float min,max,tolerance,planeTolerance;
            internal Vector2[] points;
            internal HashSet<(int,int)> edges;
            public int PointCount=>points.Length;
        }
        static InvalidOperationException Error(string message,Exception inner=null)=>new InvalidOperationException("Fuse торцов: "+message,inner);
        static (int,int) Edge(int a,int b)=>(Math.Min(a,b),Math.Max(a,b));
        static double Distance(Vector2 a,Vector2 b)
        {double x=(double)a.x-b.x,y=(double)a.y-b.y;return x*x+y*y;}
        static void Check(LTRoadMesh.Chunk mesh)
        {
            if(mesh?.vertices==null||mesh.vertices.Length==0||mesh.triangles==null||mesh.triangles.Length%3!=0)
                throw Error("пустая или некорректная сетка.");
            if(mesh.vertices.Any(p=>!LTRoadMesh.Finite(p))||mesh.triangles.Any(i=>i<0||i>=mesh.vertices.Length))
                throw Error("NaN/Infinity или неверный индекс вершины.");
        }
        // Only longitudinal surface rims define compatibility; interior cap vertices and cap
        // triangulation need not match. Coincident import splits keep separate vertex indices.
        internal static Profile Read(LTRoadMesh.Chunk mesh,float z,float tolerance)
        {
            bool[] at=mesh.vertices.Select(p=>Math.Abs((double)p.z-z)<=tolerance).ToArray();
            var ids=new HashSet<int>();var rim=new List<(int,int)>();
            for(int i=0;i<mesh.triangles.Length;i+=3)
            {
                int a=mesh.triangles[i],b=mesh.triangles[i+1],c=mesh.triangles[i+2];
                if(at[a]&&at[b]&&!at[c])Add(a,b);
                if(at[b]&&at[c]&&!at[a])Add(b,c);
                if(at[c]&&at[a]&&!at[b])Add(c,a);
            }
            void Add(int a,int b){ids.Add(a);ids.Add(b);rim.Add((a,b));}
            var groups=Enumerable.Repeat(-1,mesh.vertices.Length).ToArray();
            var points=new List<Vector2>();var positions=new Dictionary<Vector2,int>();
            foreach(int id in ids.OrderBy(i=>mesh.vertices[i].x).ThenBy(i=>mesh.vertices[i].y))
            {
                var v=mesh.vertices[id];var p=new Vector2(v.x,v.y);
                if(!positions.TryGetValue(p,out int group)){group=points.Count;positions.Add(p,group);points.Add(p);}
                groups[id]=group;
            }
            if(points.Count<2||points.Count>1024)throw Error($"в плоскости {z:G6} найдено {points.Count} точек края; ожидается 2–1024. Проверьте ось и допуск.");
            // Include coincident vertices used only by caps without welding their attributes.
            for(int i=0;i<groups.Length;i++)if(at[i]&&positions.TryGetValue(new Vector2(mesh.vertices[i].x,mesh.vertices[i].y),out int group))groups[i]=group;
            var edges=new HashSet<(int,int)>();var degree=new int[points.Count];
            foreach(var e in rim)
            {
                int a=groups[e.Item1],b=groups[e.Item2];
                if(a==b||!edges.Add(Edge(a,b)))throw Error("схлопнутое или наложенное ребро торца.");
                if(++degree[a]>2||++degree[b]>2)throw Error("ветвящийся край торца.");
            }
            return new Profile{points=points.ToArray(),groups=groups,edges=edges};
        }
        internal static int[] Match(Profile source,Plan plan)
        {
            if(source.points.Length!=plan.points.Length)
                throw Error($"число точек края {source.points.Length}, общий профиль — {plan.points.Length}. Сохраните одинаковый контур на всех модулях и LOD либо выберите режим перестройки стыков.");
            var map=new int[source.points.Length];var used=new bool[map.Length];double limit=(double)plan.tolerance*plan.tolerance;
            for(int i=0;i<map.Length;i++)
            {
                double best=double.PositiveInfinity,second=double.PositiveInfinity;int found=-1;
                for(int j=0;j<plan.points.Length;j++)
                {
                    double d=Distance(source.points[i],plan.points[j]);
                    if(d<best){second=best;best=d;found=j;}else second=Math.Min(second,d);
                }
                if(best>limit)throw Error($"расхождение края {Math.Sqrt(best)*1000:G5} мм превышает допуск {plan.tolerance*1000:G5} мм.");
                if(second-best<=1e-20||used[found])throw Error("неоднозначное соответствие точек. Уменьшите допуск или исправьте край.");
                used[found]=true;map[i]=found;
            }
            if(!new HashSet<(int,int)>(source.edges.Select(e=>Edge(map[e.Item1],map[e.Item2]))).SetEquals(plan.edges))
                throw Error("соединения точек края различаются. Fuse не перестраивает треугольники.");
            return map;
        }
        public static Plan Create(LTRoadMesh.Chunk reference,float tolerance=DefaultTolerance,float planeTolerance=DefaultTolerance)
        {
            Check(reference);
            if(!LTRoadMesh.Finite(tolerance)||tolerance<.00001f||tolerance>.01f)throw Error("допуск должен быть от 0.01 до 10 мм.");
            if(!LTRoadMesh.Finite(planeTolerance)||planeTolerance<.00001f||planeTolerance>tolerance)throw Error("полоса поиска торца должна быть от 0.01 мм до допуска Fuse.");
            float min=reference.vertices.Min(p=>p.z),max=reference.vertices.Max(p=>p.z);
            if(max-min<=Math.Max(.001f,tolerance*4))throw Error("длина модуля слишком мала для допуска.");
            var front=Read(reference,min,planeTolerance);
            var plan=new Plan{min=min,max=max,tolerance=tolerance,planeTolerance=planeTolerance,points=front.points,edges=front.edges};
            Match(Read(reference,max,planeTolerance),plan);
            return plan;
        }
        public static LTRoadMesh.Chunk Apply(LTRoadMesh.Chunk source,Plan plan,out int moved,out float maxMove)
        {
            if(plan==null)throw new ArgumentNullException(nameof(plan));Check(source);
            if(source.vertices.Any(p=>(double)p.z<plan.min-plan.tolerance||(double)p.z>plan.max+plan.tolerance))
                throw Error("LOD выходит за длину LOD0 больше допуска.");
            Profile front,back;
            int[] fm,bm;
            try{front=Read(source,plan.min,plan.planeTolerance);fm=Match(front,plan);}catch(InvalidOperationException e){throw Error("начало модуля: "+e.Message.Substring("Fuse торцов: ".Length),e);}
            try{back=Read(source,plan.max,plan.planeTolerance);bm=Match(back,plan);}catch(InvalidOperationException e){throw Error("конец модуля: "+e.Message.Substring("Fuse торцов: ".Length),e);}
            T[] Copy<T>(T[] a)=>a==null?null:(T[])a.Clone();
            var result=new LTRoadMesh.Chunk{vertices=Copy(source.vertices),triangles=Copy(source.triangles),
                submeshes=source.submeshes?.Select(Copy).ToArray(),normals=Copy(source.normals),tangents=Copy(source.tangents),
                uv=Copy(source.uv),uv2=Copy(source.uv2),uv3=Copy(source.uv3),uv4=Copy(source.uv4),colors=Copy(source.colors)};
            moved=0;maxMove=0;double limit=(double)plan.tolerance*plan.tolerance;
            for(int i=0;i<source.vertices.Length;i++)
            {
                var p=source.vertices[i];bool first=Math.Abs((double)p.z-plan.min)<=plan.planeTolerance;
                if(!first&&Math.Abs((double)p.z-plan.max)>plan.planeTolerance)continue;
                var q=p;q.z=first?plan.min:plan.max;int group=first?front.groups[i]:back.groups[i];
                if(group>=0){var target=plan.points[(first?fm:bm)[group]];q.x=target.x;q.y=target.y;}
                double dx=(double)q.x-p.x,dy=(double)q.y-p.y,dz=(double)q.z-p.z,d=dx*dx+dy*dy+dz*dz;
                if(d>limit)throw Error($"сдвиг вершины {Math.Sqrt(d)*1000:G5} мм превышает допуск {plan.tolerance*1000:G5} мм.");
                if(q.Equals(p))continue;
                result.vertices[i]=q;moved++;maxMove=Math.Max(maxMove,(float)Math.Sqrt(d));
            }
            // Reject folds, including very small triangles, without float-normalization epsilon.
            for(int i=0;i<source.triangles.Length;i+=3)
            {
                int a=source.triangles[i],b=source.triangles[i+1],c=source.triangles[i+2];
                if(source.vertices[a].Equals(result.vertices[a])&&source.vertices[b].Equals(result.vertices[b])&&source.vertices[c].Equals(result.vertices[c]))continue;
                var before=Cross(source.vertices[a],source.vertices[b],source.vertices[c]);var after=Cross(result.vertices[a],result.vertices[b],result.vertices[c]);
                double area=Dot(before,before);
                if(area>0&&(Dot(before,after)<=0||Dot(after,after)<area*1e-8))
                    throw Error("привязка схлопывает или переворачивает грань. Уменьшите допуск; сетка сохранена без изменений.");
            }
            Validate(result,plan);
            result.hash=LTRoadMesh.ContentHash(result);return result;
        }
        static (double x,double y,double z) Cross(Vector3 a,Vector3 b,Vector3 c)
        {
            double x=(double)b.x-a.x,y=(double)b.y-a.y,z=(double)b.z-a.z,u=(double)c.x-a.x,v=(double)c.y-a.y,w=(double)c.z-a.z;
            return(y*w-z*v,z*u-x*w,x*v-y*u);
        }
        static double Dot((double x,double y,double z) a,(double x,double y,double z) b)=>a.x*b.x+a.y*b.y+a.z*b.z;
        static void Validate(LTRoadMesh.Chunk mesh,Plan plan)
        {
            // Exact planes after snapping, not a broad band which might swallow another row.
            foreach(float z in new[]{plan.min,plan.max})
            {
                var profile=Read(mesh,z,0);var map=Match(profile,plan);
                for(int i=0;i<map.Length;i++)if(!profile.points[i].Equals(plan.points[map[i]]))throw Error("край не совпал с общим профилем.");
            }
        }
        public static void ValidateRims(LTRoadMesh.Chunk reference,LTRoadMesh.Chunk source)
        {
            Check(reference);Check(source);float min=reference.vertices.Min(p=>p.z),max=reference.vertices.Max(p=>p.z);
            var front=Read(reference,min,0);
            var plan=new Plan{min=min,max=max,tolerance=DefaultTolerance,points=front.points,edges=front.edges};
            Validate(reference,plan);Validate(source,plan);
        }
    }
}
