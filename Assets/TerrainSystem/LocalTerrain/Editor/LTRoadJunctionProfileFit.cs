using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Opt-in, position-only topology preservation. Never adds/removes indices, nor
    // pretends a different number of visible seam corners can join without cracks.
    public static class LTRoadJunctionProfileFit
    {
        public sealed class Profile
        {
            public Vector2[] points;
            public float band;
        }
        static InvalidOperationException Error(string message)=>new InvalidOperationException("Подгонка въезда: "+message);
        static LTRoadMesh.Chunk Local(LTRoadMesh.Chunk source,Matrix4x4 matrix)
        {
            if(source?.vertices==null||source.triangles==null||source.vertices.Length==0||source.triangles.Length%3!=0||
                source.triangles.Any(i=>i<0||i>=source.vertices.Length))throw Error("некорректный меш.");
            var v=source.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
            if(v.Any(p=>!LTRoadMesh.Finite(p)))throw Error("неконечные координаты.");
            return new LTRoadMesh.Chunk{vertices=v,triangles=source.triangles};
        }
        public static Profile Read(LTRoadMesh.Chunk source,Matrix4x4 toSocket,float band)
        {
            if(!LTRoadMesh.Finite(band)||band<.00001f||band>.02f)throw Error("полоса поиска должна быть 0.01–20 мм.");
            var local=Local(source,toSocket);var rim=LTRoadModuleFuse.Read(local,0,band);var p=rim.points;
            var neighbours=Enumerable.Range(0,p.Length).Select(_=>new List<int>()).ToArray();
            foreach(var e in rim.edges){neighbours[e.Item1].Add(e.Item2);neighbours[e.Item2].Add(e.Item1);}
            int left=Enumerable.Range(0,p.Length).OrderBy(i=>p[i].x).ThenByDescending(i=>p[i].y).First();
            int right=Enumerable.Range(0,p.Length).OrderByDescending(i=>p[i].x).ThenByDescending(i=>p[i].y).First();
            if(p[right].x-p[left].x<.01f)throw Error("не найден поперечный верхний контур.");
            var paths=new List<List<int>>();
            foreach(int first in neighbours[left])
            {
                var path=new List<int>{left};int previous=left,current=first;
                while(!path.Contains(current))
                {
                    path.Add(current);if(current==right){paths.Add(path);break;}
                    var next=neighbours[current].Where(i=>i!=previous).ToArray();if(next.Length!=1)break;
                    previous=current;current=next[0];
                }
            }
            // A closed slab has an upper and a lower route. An open slab may have
            // lower cap/underside tails. Only the upper route joins visible asphalt.
            double Area(List<int> path)
            {
                double area=0;for(int i=1;i<path.Count;i++)
                {var a=p[path[i-1]];var b=p[path[i]];if(b.x<a.x-1e-5f)return double.NegativeInfinity;area+=(b.x-a.x)*(double)(a.y+b.y)*.5;}
                return area;
            }
            paths=paths.Where(path=>!double.IsNegativeInfinity(Area(path))).OrderByDescending(Area).ToList();
            if(paths.Count==0||paths.Count>1&&Math.Abs(Area(paths[0])-Area(paths[1]))<1e-9)
                throw Error("неоднозначный верх: нужен один непрерывный профиль слева направо, без нависающих складок.");
            var chosen=paths[0];var used=new HashSet<int>(chosen);
            void Side(bool atLeft)
            {
                int end=atLeft?chosen[0]:chosen[chosen.Count-1];
                while(true)
                {
                    var next=neighbours[end].Where(i=>!used.Contains(i)&&p[i].y<p[end].y-1e-6f).ToArray();
                    if(next.Length!=1)break;
                    end=next[0];used.Add(end);if(atLeft)chosen.Insert(0,end);else chosen.Add(end);
                }
            }
            Side(true);Side(false);
            if(chosen.Count<2)throw Error("пустой видимый профиль.");
            // Separate disconnected pieces must not disappear from validation.
            var reachable=new HashSet<int>();var pending=new Stack<int>();pending.Push(left);
            while(pending.Count>0){int i=pending.Pop();if(!reachable.Add(i))continue;foreach(int n in neighbours[i])pending.Push(n);}
            if(reachable.Count!=p.Length)throw Error("несколько несвязанных контуров в полосе въезда.");
            return new Profile{points=chosen.Select(i=>p[i]).ToArray(),band=band};
        }
        public static Profile Create(LTRoadMesh.Chunk source,Matrix4x4 toSocket,float band,float width)
        {
            var local=Local(source,toSocket);
            if(local.vertices.Any(p=>p.z>band))throw Error("синяя Z должна смотреть наружу, маркер должен стоять на торце.");
            var profile=Read(source,toSocket,band);
            if(!LTRoadMesh.Finite(width)||width<=0||Math.Abs(profile.points.Min(p=>p.x)+width*.5f)>band||
                Math.Abs(profile.points.Max(p=>p.x)-width*.5f)>band)
                throw Error("центр/ширина Port не совпадают с верхним контуром (с учётом полосы поиска).");
            return profile;
        }
        public static LTRoadMesh.Chunk Apply(LTRoadMesh.Chunk source,Matrix4x4 toSocket,Profile target,float length,float maxShift,int side)
        {
            if(target==null||target.points==null||target.points.Length<2||target.points.Any(p=>!LTRoadMesh.Finite(p.x)||!LTRoadMesh.Finite(p.y))||
                !LTRoadMesh.Finite(length)||!LTRoadMesh.Finite(maxShift)||length<Math.Max(.02f,target.band*4)||
                maxShift<target.band||maxShift>.5f||(side!=1&&side!=-1))throw Error("неверная длина перехода или предел смещения.");
            var local=Local(source,toSocket);var original=Read(source,toSocket,target.band);
            if(original.points.Length!=target.points.Length)
                throw Error($"верх/бока: {original.points.Length} опор, у въезда {target.points.Length}. Число опор видимого контура должно совпадать; нижняя заглушка может отличаться. Новые вершины не добавляются.");
            var delta=original.points.Zip(target.points,(a,b)=>b-a).ToArray();
            if(delta.Any(d=>d.magnitude>maxShift))throw Error("разница профилей превышает максимальный сдвиг. Проверьте базовую высоту/ширину или явно увеличьте предел.");
            if(local.vertices.Any(p=>side*p.z< -target.band))throw Error("концевой сегмент заходит за плоскость стыка. Уменьшите длину сегмента дороги или исправьте направление.");
            // Inverse-distance interpolation transports cap/interior vertices too, without
            // interpreting arbitrary cap triangulation as extra surface seam anchors.
            Vector2 Displacement(float x,float y)
            {
                double sum=0,dx=0,dy=0;
                for(int i=0;i<delta.Length;i++)
                {
                    double u=(double)x-original.points[i].x,v=(double)y-original.points[i].y,d=u*u+v*v;
                    if(d<1e-16)return delta[i];double w=1/d;sum+=w;dx+=w*delta[i].x;dy+=w*delta[i].y;
                }
                return new Vector2((float)(dx/sum),(float)(dy/sum));
            }
            float Weight(float z)
            {float t=Mathf.Clamp01((side*z-target.band)/(length-target.band));return 1-t*t*(3-2*t);}
            Vector3 Warp(Vector3 p,bool flatten)
            {
                float w=Weight(p.z);if(w<=0)return p;var d=Displacement(p.x,p.y);
                return new Vector3(p.x+d.x*w,p.y+d.y*w,flatten&&Math.Abs(p.z)<=target.band?0:p.z);
            }
            var origin=toSocket.MultiplyPoint3x4(Vector3.zero);var bx=toSocket.MultiplyVector(Vector3.right);
            var by=toSocket.MultiplyVector(Vector3.up);var bz=toSocket.MultiplyVector(Vector3.forward);
            var yz=Vector3.Cross(by,bz);var zx=Vector3.Cross(bz,bx);var xy=Vector3.Cross(bx,by);float det=Vector3.Dot(bx,yz);
            if(!LTRoadMesh.Finite(det)||det<=1e-8f)throw Error("нулевой/отражённый transform.");
            Vector3 InverseVector(Vector3 v)=>new Vector3(Vector3.Dot(v,yz),Vector3.Dot(v,zx),Vector3.Dot(v,xy))/det;
            var vertices=(Vector3[])source.vertices.Clone();var normals=source.normals==null?null:(Vector3[])source.normals.Clone();
            var tangents=source.tangents==null?null:(Vector4[])source.tangents.Clone();
            bool shading=normals?.Length==vertices.Length&&tangents?.Length==vertices.Length;
            const float h=.0001f;
            for(int i=0;i<vertices.Length;i++)
            {
                var p=local.vertices[i];if(side*p.z>=length)continue;
                var q=Warp(p,true);
                if((q-p).magnitude>maxShift*1.0001f)throw Error("сдвиг вершины превышает заданный предел.");
                if(q.Equals(p))continue;
                vertices[i]=InverseVector(q-origin);
                if(!shading)continue;
                // Transport authored shading through the smooth deformation; UV and
                // hard-edge splits survive. Tiny end-plane flattening keeps its frame.
                Vector3 Axis(Vector3 axis)=>(Warp(p+axis*h,false)-Warp(p-axis*h,false))/(2*h);
                var x=Axis(Vector3.right);var y=Axis(Vector3.up);var z=Axis(Vector3.forward);
                float jac=Vector3.Dot(x,Vector3.Cross(y,z));
                if(!LTRoadMesh.Finite(jac)||jac<=1e-6f)throw Error("деформация выворачивает локальный профиль; уменьшите сдвиг.");
                var sn=source.normals[i];var n=(bx*sn.x+by*sn.y+bz*sn.z).normalized;
                n=(Vector3.Cross(y,z)*n.x+Vector3.Cross(z,x)*n.y+Vector3.Cross(x,y)*n.z).normalized;
                normals[i]=InverseVector(n).normalized;
                var st=source.tangents[i];var t=toSocket.MultiplyVector(new Vector3(st.x,st.y,st.z));
                t=InverseVector(x*t.x+y*t.y+z*t.z);t=(t-normals[i]*Vector3.Dot(t,normals[i])).normalized;
                if(!LTRoadMesh.Finite(normals[i])||!LTRoadMesh.Finite(t)||t.sqrMagnitude<.5f)throw Error("некорректная нормаль/тангент после подгонки.");
                tangents[i]=new Vector4(t.x,t.y,t.z,st.w);
            }
            for(int i=0;i<source.triangles.Length;i+=3)
            {
                int a=source.triangles[i],b=source.triangles[i+1],c=source.triangles[i+2];
                if(vertices[a].Equals(source.vertices[a])&&vertices[b].Equals(source.vertices[b])&&vertices[c].Equals(source.vertices[c]))continue;
                var before=Cross(source.vertices[a],source.vertices[b],source.vertices[c]);var after=Cross(vertices[a],vertices[b],vertices[c]);double area=Dot(before,before);
                if(area>0&&(Dot(before,after)<=0||Dot(after,after)<area*1e-8))
                    throw Error("подгонка схлопывает или переворачивает грань. Увеличьте длину перехода или подготовьте более близкий профиль.");
            }
            var result=new LTRoadMesh.Chunk{vertices=vertices,triangles=source.triangles,submeshes=source.submeshes,normals=normals,tangents=tangents,
                uv=source.uv,uv2=source.uv2,uv3=source.uv3,uv4=source.uv4,colors=source.colors};
            var verified=Read(result,toSocket,target.band);
            if(verified.points.Length!=target.points.Length||verified.points.Where((p,i)=>(p-target.points[i]).sqrMagnitude>1e-8f).Any())
                throw Error("подогнанный верхний край не совпал с эталоном.");
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
