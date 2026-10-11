using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Opt-in, source-space correction BEFORE bending and automatic simplification.
    // Only close, bijectively matched profiles with identical rim connectivity qualify.
    public static class LTRoadModuleEnds
    {
        const float Epsilon=.00001f;
        internal sealed class Profile
        {
            public Vector2[] points;
            public int[] vertexGroup;
            public HashSet<(int,int)> edges;
        }
        public sealed class Plan
        {
            internal Profile front,back;
            internal Vector2[] frontTarget,backTarget;
            internal float minZ,maxZ,blend,maxMismatch;
            public float Mismatch { get; internal set; }
            public float BlendLength=>blend;
            public int PointCount=>front.points.Length;
        }
        static InvalidOperationException Error(string message)=>new InvalidOperationException("Автоподгонка торцов: "+message);
        static (int,int) Edge(int a,int b)=>(Math.Min(a,b),Math.Max(a,b));
        static Profile Read(LTRoadMesh.Chunk source,float z)
        {
            var ids=Enumerable.Range(0,source.vertices.Length).Where(i=>Math.Abs(source.vertices[i].z-z)<Epsilon)
                .OrderBy(i=>source.vertices[i].x).ThenBy(i=>source.vertices[i].y).ToArray();
            var points=new List<Vector2>();var groups=Enumerable.Repeat(-1,source.vertices.Length).ToArray();
            foreach(int id in ids)
            {
                var v=source.vertices[id];var p=new Vector2(v.x,v.y);int found=-1;
                for(int j=0;j<points.Count;j++)if((p-points[j]).sqrMagnitude<=Epsilon*Epsilon)
                {if(found>=0)throw Error("неоднозначные почти совпадающие вершины торца.");found=j;}
                if(found<0){found=points.Count;points.Add(p);}
                groups[id]=found;
                if(points.Count>1024)throw Error("более 1024 точек профиля. Подготовьте совместимые торцы в модели.");
            }
            if(points.Count<2)throw Error("на каждом торце нужны хотя бы две точки. Проверьте продольную ось.");
            var edges=new HashSet<(int,int)>();var tris=source.triangles;
            for(int i=0;i<tris.Length;i+=3)
            {
                int a=groups[tris[i]],b=groups[tris[i+1]],c=groups[tris[i+2]];
                // Cap triangulation may differ. Only the rim of longitudinal faces matters.
                if(a>=0&&b>=0&&c<0&&a!=b)edges.Add(Edge(a,b));
                if(b>=0&&c>=0&&a<0&&b!=c)edges.Add(Edge(b,c));
                if(c>=0&&a>=0&&b<0&&c!=a)edges.Add(Edge(c,a));
            }
            if(edges.Count==0)throw Error("не найден связный край продольной поверхности.");
            return new Profile{points=points.ToArray(),vertexGroup=groups,edges=edges};
        }
        static int[] Match(Profile from,Profile to,float tolerance)
        {
            if(from.points.Length!=to.points.Length)throw Error("разное число точек на торцах. Нужна одинаковая топология, а не новая триангуляция.");
            int Closest(Vector2 p,Vector2[] points)
            {
                double best=double.PositiveInfinity,second=double.PositiveInfinity;int at=-1;
                for(int i=0;i<points.Length;i++)
                {
                    double dx=(double)p.x-points[i].x,dy=(double)p.y-points[i].y,d=dx*dx+dy*dy;
                    if(d<best){second=best;best=d;at=i;}else if(d<second)second=d;
                }
                if(best>(double)tolerance*tolerance)throw Error($"расхождение превышает допуск {tolerance:G4} м. Проверьте масштаб и профиль модуля.");
                if(second-best<1e-12)throw Error("соответствие вершин неоднозначно. Подготовьте одинаковые торцы в модели.");
                return at;
            }
            var map=new int[from.points.Length];var reverse=new int[to.points.Length];
            for(int i=0;i<map.Length;i++)map[i]=Closest(from.points[i],to.points);
            for(int i=0;i<reverse.Length;i++)reverse[i]=Closest(to.points[i],from.points);
            for(int i=0;i<map.Length;i++)if(reverse[map[i]]!=i)throw Error("нельзя однозначно сопоставить вершины. Торцы должны иметь близкую форму.");
            if(!new HashSet<(int,int)>(from.edges.Select(e=>Edge(map[e.Item1],map[e.Item2]))).SetEquals(to.edges))
                throw Error("соединения вершин на торцах различаются. Подгонка не меняет топологию.");
            return map;
        }
        public static Plan Create(LTRoadMesh.Chunk source,float blendLength,float maxMismatch)
        {
            if(!LTRoadMesh.Finite(blendLength)||blendLength<=0||!LTRoadMesh.Finite(maxMismatch)||maxMismatch<Epsilon)
                throw Error("длина сглаживания и допуск должны быть положительными конечными числами.");
            float min=source.vertices.Min(p=>p.z),max=source.vertices.Max(p=>p.z);
            if(max-min<.001f)throw Error("нулевая длина модуля.");
            var a=Read(source,min);var b=Read(source,max);var map=Match(a,b,maxMismatch);
            var plan=new Plan{front=a,back=b,frontTarget=new Vector2[a.points.Length],backTarget=new Vector2[b.points.Length],
                minZ=min,maxZ=max,blend=Math.Min(blendLength,(max-min)*.45f),maxMismatch=maxMismatch};
            for(int i=0;i<map.Length;i++)
            {
                int j=map[i];var target=(a.points[i]+b.points[j])*.5f;
                plan.frontTarget[i]=plan.backTarget[j]=target;
                plan.Mismatch=Math.Max(plan.Mismatch,(a.points[i]-b.points[j]).magnitude);
            }
            return plan;
        }
        public static void ValidateRims(LTRoadMesh.Chunk reference,LTRoadMesh.Chunk source)
        {
            float min=reference.vertices.Min(p=>p.z),max=reference.vertices.Max(p=>p.z);
            Match(Read(reference,min),Read(source,min),Epsilon);
            Match(Read(reference,min),Read(source,max),Epsilon);
        }
        // Interpolating inverse-distance field, with double weights to avoid tiny-distance overflow.
        // It is zero outside the end band and has zero longitudinal derivative at both ends.
        static Vector2 Offset(Vector2 p,Vector2[] anchors,Vector2[] delta)
        {
            double sum=0,x=0,y=0;
            for(int i=0;i<anchors.Length;i++)
            {
                double dx=(double)p.x-anchors[i].x,dy=(double)p.y-anchors[i].y,d=dx*dx+dy*dy;
                if(d<1e-18)return delta[i];
                double w=1/(d*d);sum+=w;x+=w*delta[i].x;y+=w*delta[i].y;
            }
            return new Vector2((float)(x/sum),(float)(y/sum));
        }
        public static LTRoadMesh.Chunk Apply(LTRoadMesh.Chunk source,Plan plan)
        {
            if(source.vertices.Any(p=>!LTRoadMesh.Finite(p)||p.z<plan.minZ-Epsilon||p.z>plan.maxZ+Epsilon))
                throw Error("LOD выходит за продольные границы LOD0 или содержит NaN/Infinity.");
            var front=Read(source,plan.minZ);var back=Read(source,plan.maxZ);
            var fm=Match(front,plan.front,plan.maxMismatch);var bm=Match(back,plan.back,plan.maxMismatch);
            Vector2[] Targets(int[] map,Vector2[] values)=>map.Select(i=>values[i]).ToArray();
            var ft=Targets(fm,plan.frontTarget);var bt=Targets(bm,plan.backTarget);
            Vector2[] Deltas(Profile p,Vector2[] target)=>p.points.Select((v,i)=>target[i]-v).ToArray();
            var fd=Deltas(front,ft);var bd=Deltas(back,bt);
            Vector3 Warp(Vector3 p)
            {
                float distance=Math.Min(p.z-plan.minZ,plan.maxZ-p.z);
                if(distance>=plan.blend)return p;
                bool first=p.z<(plan.minZ+plan.maxZ)*.5f;float u=Mathf.Clamp01(distance/plan.blend);
                float weight=1-u*u*(3-2*u);var d=Offset(new Vector2(p.x,p.y),first?front.points:back.points,first?fd:bd)*weight;
                return new Vector3(p.x+d.x,p.y+d.y,p.z);
            }
            T[] Copy<T>(T[] a)=>a==null?null:(T[])a.Clone();
            var result=new LTRoadMesh.Chunk{vertices=Copy(source.vertices),normals=Copy(source.normals),tangents=Copy(source.tangents),
                uv=Copy(source.uv),uv2=Copy(source.uv2),uv3=Copy(source.uv3),uv4=Copy(source.uv4),colors=Copy(source.colors),
                triangles=Copy(source.triangles),submeshes=source.submeshes?.Select(Copy).ToArray()};
            float h=Math.Min(.0001f,plan.blend*.001f);
            for(int i=0;i<source.vertices.Length;i++)
            {
                var p=source.vertices[i];var q=Warp(p);
                int f=front.vertexGroup[i],b=back.vertexGroup[i];
                // Shared targets are assigned, not independently rounded displacements.
                if(f>=0)q=new Vector3(ft[f].x,ft[f].y,plan.minZ);
                else if(b>=0)q=new Vector3(bt[b].x,bt[b].y,plan.maxZ);
                if(q.Equals(p))continue;
                result.vertices[i]=q;
                Vector3 D(Vector3 axis)=>(Warp(p+axis*h)-Warp(p-axis*h))/(2*h);
                var dx=D(Vector3.right);var dy=D(Vector3.up);var dz=D(Vector3.forward);
                if(Vector3.Dot(dx,Vector3.Cross(dy,dz))<=.01f)throw Error("подгонка складывает поверхность. Увеличьте длину сглаживания или исправьте торец модели.");
                var n=source.normals[i];n=(Vector3.Cross(dy,dz)*n.x+Vector3.Cross(dz,dx)*n.y+Vector3.Cross(dx,dy)*n.z).normalized;
                var st=source.tangents[i];var t=dx*st.x+dy*st.y+dz*st.z;t=(t-n*Vector3.Dot(n,t)).normalized;
                result.normals[i]=n;result.tangents[i]=new Vector4(t.x,t.y,t.z,st.w);
            }
            for(int i=0;i<source.triangles.Length;i+=3)
            {
                int a=source.triangles[i],b=source.triangles[i+1],c=source.triangles[i+2];
                if(result.vertices[a].Equals(source.vertices[a])&&result.vertices[b].Equals(source.vertices[b])&&result.vertices[c].Equals(source.vertices[c]))continue;
                var before=Vector3.Cross(source.vertices[b]-source.vertices[a],source.vertices[c]-source.vertices[a]);
                var after=Vector3.Cross(result.vertices[b]-result.vertices[a],result.vertices[c]-result.vertices[a]);
                if(before.sqrMagnitude>1e-20f&&(Vector3.Dot(before,after)<=0||after.sqrMagnitude<before.sqrMagnitude*.0001f))
                    throw Error("обнаружена перевёрнутая/схлопнутая грань. Исходная модель не изменена.");
            }
            MatchShading(result,front,back,Match(front,back,plan.maxMismatch));
            result.hash=LTRoadMesh.ContentHash(result);return result;
        }
        static void MatchShading(LTRoadMesh.Chunk mesh,Profile front,Profile back,int[] pairs)
        {
            // Keep hard-edge/UV tangent splits. Only already similar surface frames blend;
            // opposite cap normals must never be averaged into the road surface.
            var normals=(Vector3[])mesh.normals.Clone();var tangents=(Vector4[])mesh.tangents.Clone();
            for(int group=0;group<pairs.Length;group++)
            {
                var ids=Enumerable.Range(0,mesh.vertices.Length).Where(i=>front.vertexGroup[i]==group||back.vertexGroup[i]==pairs[group]).ToArray();
                var remaining=new List<int>(ids);
                while(remaining.Count>0)
                {
                    int first=remaining[0];var n=normals[first];var t=tangents[first];
                    Vector3 T(Vector4 v)=>new Vector3(v.x,v.y,v.z);
                    var members=remaining.Where(i=>Vector3.Dot(n,normals[i])>.98f&&tangents[i].w==t.w&&Vector3.Dot(T(t),T(tangents[i]))>.98f).ToArray();
                    if(members.Length==0){remaining.RemoveAt(0);continue;}
                    foreach(int i in members)remaining.Remove(i);
                    if(!members.Any(i=>front.vertexGroup[i]>=0)||!members.Any(i=>back.vertexGroup[i]>=0))continue;
                    n=Vector3.zero;var tangent=Vector3.zero;
                    foreach(int i in members){n+=normals[i];tangent+=T(tangents[i]);}
                    n=n.normalized;tangent=(tangent-n*Vector3.Dot(n,tangent)).normalized;
                    foreach(int i in members){mesh.normals[i]=n;mesh.tangents[i]=new Vector4(tangent.x,tangent.y,tangent.z,t.w);}
                }
            }
        }
    }
}
