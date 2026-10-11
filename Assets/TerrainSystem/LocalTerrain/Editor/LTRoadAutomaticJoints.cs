using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public static partial class LTRoadModuleCollar
    {
        internal sealed class JointProfile
        {
            internal Vector3[] points;
            internal bool closed;
        }
        public sealed class AutomaticPlan
        {
            internal JointProfile[] profiles;
            internal float min,max,width,maxChange;
            public int PointCount=>profiles.Sum(p=>p.points.Length);
        }
        static InvalidOperationException JointError(string message)=>new InvalidOperationException("Автоматические стыки: "+message);

        // One source-space profile for ALL modules and levels, not dense LOD0 end strips.
        // The first active LOD0 supplies the cross-section at the retained centre's start.
        public static AutomaticPlan CreateAutomatic(LTRoadMesh.Chunk reference,float width,float maxChange,float precision)
        {
            float min=reference.vertices.Min(p=>p.z),max=reference.vertices.Max(p=>p.z);
            if(!LTRoadMesh.Finite(width)||width<.01f||width>(max-min)*.45f)
                throw JointError("зона перехода должна быть от 0.01 м до 45% длины первого модуля.");
            if(!LTRoadMesh.Finite(maxChange)||maxChange<=0||!LTRoadMesh.Finite(precision)||precision<.00001f||precision>maxChange)
                throw JointError("допуск формы должен быть положительным, точность профиля — от 0.00001 м до допуска формы.");
            var builder=new Builder();int start=builder.Add(reference);builder.Keep(reference,start,min+width,max-width);
            var paths=builder.Contours(min+width);
            if(paths.Count==0)throw JointError("нет сечения у начала сохраняемой середины. Измените зону перехода.");
            var profiles=paths.Select(path=>
            {
                var points=path.edges.Select(e=>builder.vertices[e.a].p).ToList();
                if(!path.closed)points.Add(builder.vertices[path.edges.Last().b].p);
                return new JointProfile{closed=path.closed,points=SimplifyProfile(points,path.closed,precision)};
            }).ToArray();
            if(profiles.Sum(p=>p.points.Length)>512)throw JointError("общий профиль превышает 512 точек. Увеличьте допуск точности профиля.");
            ValidateProfiles(profiles);
            return new AutomaticPlan{profiles=profiles,min=min,max=max,width=width,maxChange=maxChange};
        }
        public static LTRoadMesh.Chunk ApplyAutomatic(LTRoadMesh.Chunk authored,AutomaticPlan plan)
        {
            if(plan==null)throw new ArgumentNullException(nameof(plan));
            if(authored.vertices.Any(p=>!LTRoadMesh.Finite(p)||p.z<plan.min-plan.width||p.z>plan.max+plan.width))
                throw JointError("исходный меш выходит за продольный допуск или содержит NaN/Infinity.");
            CheckRemovedBoundaries(authored,plan);
            var builder=new Builder();int start=builder.Add(authored);
            builder.Keep(authored,start,plan.min+plan.width,plan.max-plan.width);
            builder.AutomaticEnd(authored,start,plan,true);
            builder.AutomaticEnd(authored,start,plan,false);
            var result=builder.Finish();
            LTRoadModuleEnds.ValidateRims(result,result);
            return result;
        }

        static bool HasCap(LTRoadMesh.Chunk source,bool front)
        {
            float end=front?source.vertices.Min(p=>p.z):source.vertices.Max(p=>p.z);
            var normal=front?Vector3.back:Vector3.forward;
            for(int i=0;i<source.triangles.Length;i+=3)
            {
                var a=source.vertices[source.triangles[i]];var b=source.vertices[source.triangles[i+1]];var c=source.vertices[source.triangles[i+2]];
                if(Math.Abs(a.z-end)<=Epsilon&&Math.Abs(b.z-end)<=Epsilon&&Math.Abs(c.z-end)<=Epsilon&&
                    Vector3.Dot(FaceNormal(a,b,c),normal)>.99f)return true;
            }
            return false;
        }
        static void CheckRemovedBoundaries(LTRoadMesh.Chunk source,AutomaticPlan plan)
        {
            var uses=new Dictionary<(Vector3,Vector3),int>();
            for(int i=0;i<source.triangles.Length;i+=3)for(int j=0;j<3;j++)
            {
                var a=source.vertices[source.triangles[i+j]];var b=source.vertices[source.triangles[i+(j+1)%3]];
                if(a.Equals(b))continue;
                bool order=a.x<b.x||(a.x==b.x&&(a.y<b.y||(a.y==b.y&&a.z<b.z)));var edge=order?(a,b):(b,a);
                uses.TryGetValue(edge,out int n);uses[edge]=n+1;
            }
            if(uses.Values.Any(n=>n>2))throw JointError("исходная поверхность имеет более двух граней на одном ребре; сначала исправьте non-manifold участок.");
            var neighbours=new Dictionary<Vector3,List<Vector3>>();
            foreach(var edge in uses.Where(p=>p.Value==1).Select(p=>p.Key))
            {
                if(!neighbours.TryGetValue(edge.Item1,out var a))neighbours[edge.Item1]=a=new List<Vector3>();a.Add(edge.Item2);
                if(!neighbours.TryGetValue(edge.Item2,out var b))neighbours[edge.Item2]=b=new List<Vector3>();b.Add(edge.Item1);
            }
            float originalMin=source.vertices.Min(p=>p.z),originalMax=source.vertices.Max(p=>p.z);
            bool cap0=HasCap(source,true),cap1=HasCap(source,false);var visited=new HashSet<Vector3>();
            foreach(var first in neighbours.Keys)
            {
                if(!visited.Add(first))continue;
                float min=first.z,max=first.z;var pending=new Stack<Vector3>();pending.Push(first);
                while(pending.Count>0)
                {
                    var p=pending.Pop();min=Math.Min(min,p.z);max=Math.Max(max,p.z);
                    foreach(var q in neighbours[p])if(visited.Add(q))pending.Push(q);
                }
                // An uncapped end ring is intentional; an isolated hole in the removed
                // band (or in an end cap) must never be silently filled by a connector.
                bool endRing=(!cap0&&Math.Abs(min-originalMin)<=Epsilon&&Math.Abs(max-originalMin)<=Epsilon)||
                    (!cap1&&Math.Abs(min-originalMax)<=Epsilon&&Math.Abs(max-originalMax)<=Epsilon);
                if(!endRing&&(max<plan.min+plan.width-Epsilon||min>plan.max-plan.width+Epsilon))
                    throw JointError($"отдельный открытый контур/отверстие целиком попадает в зону перехода (Z {min:G5}…{max:G5}). Уменьшите зону: повреждение нельзя закрыть молча.");
            }
        }

        static double SegmentDistance(Vector3 p,Vector3 a,Vector3 b)
        {
            double x=(double)b.x-a.x,y=(double)b.y-a.y,d=x*x+y*y;
            double t=d>0?Math.Max(0,Math.Min(1,(((double)p.x-a.x)*x+((double)p.y-a.y)*y)/d)):0;
            x=p.x-(a.x+t*x);y=p.y-(a.y+t*y);return x*x+y*y;
        }
        static Vector3[] SimplifyProfile(List<Vector3> source,bool closed,float precision)
        {
            // RDP against original samples, so repeated removals cannot accumulate error.
            var points=source.Select(p=>new Vector3(p.x,p.y,0)).ToList();
            if(closed)points.Add(points[0]);
            var keep=new SortedSet<int>{0,points.Count-1};
            void Reduce(int a,int b)
            {
                var pending=new Stack<(int,int)>();pending.Push((a,b));
                while(pending.Count>0)
                {
                    var range=pending.Pop();double far=(double)precision*precision;int at=-1;
                    for(int i=range.Item1+1;i<range.Item2;i++)
                    {double d=SegmentDistance(points[i],points[range.Item1],points[range.Item2]);if(d>far){far=d;at=i;}}
                    if(at<0)continue;keep.Add(at);pending.Push((range.Item1,at));pending.Push((at,range.Item2));
                }
            }
            if(closed)
            {
                int far=Enumerable.Range(1,source.Count-1).OrderByDescending(i=>(points[i]-points[0]).sqrMagnitude).First();
                keep.Add(far);Reduce(0,far);Reduce(far,points.Count-1);
                if(keep.Count<4)throw JointError("точность профиля схлопывает замкнутое сечение. Уменьшите допуск точности.");
                keep.Remove(points.Count-1);
            }
            else Reduce(0,points.Count-1);
            return keep.Select(i=>points[i]).ToArray();
        }
        static void ValidateProfiles(JointProfile[] profiles)
        {
            var segments=new List<(Vector3 a,Vector3 b,int path,int index,int count,bool closed)>();
            for(int p=0;p<profiles.Length;p++)
            {
                var profile=profiles[p];int count=profile.points.Length-(profile.closed?0:1);
                for(int i=0;i<count;i++)
                {
                    var a=profile.points[i];var b=profile.points[(i+1)%profile.points.Length];
                    if((a-b).sqrMagnitude<=Epsilon*Epsilon)throw JointError("схлопнутое ребро общего профиля.");
                    segments.Add((a,b,p,i,count,profile.closed));
                }
            }
            double Side(Vector3 a,Vector3 b,Vector3 c)=>((double)b.x-a.x)*(c.y-a.y)-((double)b.y-a.y)*(c.x-a.x);
            bool Intersects(Vector3 a,Vector3 b,Vector3 c,Vector3 d)
            {
                if(Math.Max(a.x,b.x)<Math.Min(c.x,d.x)||Math.Max(c.x,d.x)<Math.Min(a.x,b.x)||
                    Math.Max(a.y,b.y)<Math.Min(c.y,d.y)||Math.Max(c.y,d.y)<Math.Min(a.y,b.y))return false;
                return Side(a,b,c)*Side(a,b,d)<=0&&Side(c,d,a)*Side(c,d,b)<=0;
            }
            for(int i=0;i<segments.Count;i++)for(int j=i+1;j<segments.Count;j++)
            {
                var a=segments[i];var b=segments[j];
                if(a.path==b.path&&(b.index==a.index+1||(a.closed&&a.index==0&&b.index==a.count-1)))continue;
                if(Intersects(a.a,a.b,b.a,b.b))throw JointError("пересечение контуров общего профиля. Уменьшите допуск точности или измените зону перехода.");
            }
        }
        sealed partial class Builder
        {
            public void AutomaticEnd(LTRoadMesh.Chunk source,int start,AutomaticPlan plan,bool front)
            {
                float cut=front?plan.min+plan.width:plan.max-plan.width,end=front?plan.min:plan.max;
                var local=Contours(cut);
                if(local.Count!=plan.profiles.Length)throw JointError($"у сечения {cut:G5} другое число контуров ({local.Count}/{plan.profiles.Length}). Отверстия/разрывы у стыка не зашиваются автоматически; измените зону перехода.");
                var available=new HashSet<int>(Enumerable.Range(0,local.Count));
                foreach(var profile in plan.profiles)
                {
                    var points=front?profile.points:profile.points.Reverse().ToArray();
                    double MatchCost(Contour path)
                    {
                        if(path.closed!=profile.closed)return double.PositiveInfinity;
                        var centre=path.edges.Select(e=>vertices[e.a].p).Aggregate(Vector3.zero,(a,b)=>a+b)/path.edges.Count;
                        var target=points.Aggregate(Vector3.zero,(a,b)=>a+b)/points.Length;
                        double cost=XY(centre,target);
                        if(!path.closed)cost+=XY(vertices[path.edges[0].a].p,points[0])+XY(vertices[path.edges.Last().b].p,points.Last());
                        return cost;
                    }
                    var choices=available.Select(i=>(index:i,cost:MatchCost(local[i]))).OrderBy(v=>v.cost).ThenBy(v=>v.index).ToArray();
                    if(choices.Length==0||double.IsInfinity(choices[0].cost)||(choices.Length>1&&Math.Abs(choices[1].cost-choices[0].cost)<1e-10))
                        throw JointError("неоднозначное соответствие контуров. Измените зону перехода.");
                    var path=local[choices[0].index];available.Remove(choices[0].index);
                    if(path.closed)
                    {
                        int shift=Enumerable.Range(0,path.edges.Count).OrderBy(i=>XY(vertices[path.edges[i].a].p,points[0])).First();
                        path.edges=path.edges.Skip(shift).Concat(path.edges.Take(shift)).ToList();
                    }
                    int edgeCount=points.Length-(profile.closed?0:1);
                    double deviation=0;
                    foreach(var p in points)deviation=Math.Max(deviation,path.edges.Min(e=>SegmentDistance(p,vertices[e.a].p,vertices[e.b].p)));
                    foreach(var e in path.edges)foreach(int id in new[]{e.a,e.b})
                        deviation=Math.Max(deviation,Enumerable.Range(0,edgeCount).Min(i=>SegmentDistance(vertices[id].p,points[i],points[(i+1)%points.Length])));
                    if(Math.Sqrt(deviation)>plan.maxChange)
                        throw JointError($"профиль отличается на {Math.Sqrt(deviation):G4} м при допуске {plan.maxChange:G4} м. Увеличьте допуск формы либо измените зону перехода.");
                    if(!profile.closed&&(Math.Sqrt(XY(vertices[path.edges[0].a].p,points[0]))>plan.maxChange||
                        Math.Sqrt(XY(vertices[path.edges.Last().b].p,points.Last()))>plan.maxChange))
                        throw JointError("направление или концы открытого профиля несовместимы.");
                    if((long)source.triangles.Length/3*edgeCount>20000000)throw JointError("превышен бюджет переноса атрибутов торца. Упростите профиль или исходный модуль.");
                    var ring=new Contour{closed=profile.closed};
                    for(int i=0;i<edgeCount;i++)
                    {
                        var a=points[i];var b=points[(i+1)%points.Length];a.z=b.z=end;
                        var normal=FaceNormal(a,b,b+(front?Vector3.forward:Vector3.back));
                        int material;SampleSurface(source,start,(a+b)*.5f,normal,-1,plan.maxChange,out material);
                        int AddRing(Vector3 p)
                        {
                            var v=SampleSurface(source,start,p,normal,material,plan.maxChange,out _);v.p=p;v.n=normal;
                            var t=new Vector3(v.t.x,v.t.y,v.t.z);t=(t-normal*Vector3.Dot(t,normal)).normalized;
                            if(t.sqrMagnitude<.5f)t=Vector3.Cross(normal,Vector3.forward).normalized;
                            v.t=new Vector4(t.x,t.y,t.z,v.t.w);int id=vertices.Count;vertices.Add(v);return id;
                        }
                        ring.edges.Add(new Edge{a=AddRing(a),b=AddRing(b),material=material,normal=normal});
                    }
                    Stitch(path,ring);
                }
                if(Contours(cut).Count!=0)throw JointError("осталось незашитое ребро внутреннего сечения.");
                PreserveCaps(source,start,plan,front);
            }
            void PreserveCaps(LTRoadMesh.Chunk source,int start,AutomaticPlan plan,bool front)
            {
                var normal=front?Vector3.back:Vector3.forward;
                if(!HasCap(source,front))return; // Preserve open-ended surfaces; do not invent end caps.
                // An open U-shaped road section can still have a front/back wall
                // (no underside). Close that wall's polygon, not the road's underside.
                if(plan.profiles.Any(p=>p.points.Length<3))throw JointError("плоский общий профиль не описывает исходную торцевую стенку. Измените зону перехода/точность.");
                ValidateProfiles(plan.profiles.Select(p=>new JointProfile{points=p.points,closed=true}).ToArray());
                bool Inside(Vector3 p,Vector3[] loop)
                {
                    bool inside=false;
                    for(int i=0,j=loop.Length-1;i<loop.Length;j=i++)
                    {var a=loop[i];var b=loop[j];if((a.y>p.y)!=(b.y>p.y)&&p.x<(double)(b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x)inside=!inside;}
                    return inside;
                }
                for(int i=0;i<plan.profiles.Length;i++)for(int j=0;j<plan.profiles.Length;j++)
                    if(i!=j&&Inside(plan.profiles[i].points[0],plan.profiles[j].points))
                        throw JointError("вложенные контуры торцевой крышки пока не поддерживаются; отверстие не будет закрыто молча.");
                foreach(var profile in plan.profiles)
                {
                    var p=profile.points.Select(v=>new Vector3(v.x,v.y,front?plan.min:plan.max)).ToArray();
                    double area=0;for(int i=0;i<p.Length;i++)area+=(double)p[i].x*p[(i+1)%p.Length].y-(double)p[i].y*p[(i+1)%p.Length].x;
                    var remaining=Enumerable.Range(0,p.Length).ToList();if(area*normal.z<0)remaining.Reverse();
                    double Turn(Vector3 a,Vector3 b,Vector3 c)=>(((double)b.x-a.x)*(c.y-a.y)-((double)b.y-a.y)*(c.x-a.x))*normal.z;
                    int guard=p.Length*p.Length;
                    while(remaining.Count>2)
                    {
                        bool added=false;
                        for(int i=0;i<remaining.Count;i++)
                        {
                            int a=remaining[(i+remaining.Count-1)%remaining.Count],b=remaining[i],c=remaining[(i+1)%remaining.Count];
                            if(Turn(p[a],p[b],p[c])<=1e-12)continue;
                            if(remaining.Any(j=>j!=a&&j!=b&&j!=c&&Turn(p[a],p[b],p[j])>=0&&Turn(p[b],p[c],p[j])>=0&&Turn(p[c],p[a],p[j])>=0))continue;
                            int material;SampleSurface(source,start,(p[a]+p[b]+p[c])/3,normal,-1,plan.maxChange,out material);
                            int AddCap(int at)
                            {
                                var v=SampleSurface(source,start,p[at],normal,material,plan.maxChange,out _);v.n=normal;
                                var t=new Vector3(v.t.x,v.t.y,0).normalized;if(t.sqrMagnitude<.5f)t=Vector3.right;
                                v.t=new Vector4(t.x,t.y,t.z,v.t.w);int id=vertices.Count;vertices.Add(v);return id;
                            }
                            Face(AddCap(a),AddCap(b),AddCap(c),material,true,normal);remaining.RemoveAt(i);added=true;break;
                        }
                        if(!added||--guard<=0)throw JointError("не удалось безопасно построить торцевую крышку.");
                    }
                }
            }
            Vertex SampleSurface(LTRoadMesh.Chunk source,int start,Vector3 p,Vector3 normal,int material,float maxChange,out int selectedMaterial)
            {
                var subs=source.submeshes??new[]{source.triangles};double best=double.PositiveInfinity,nearestSurface=double.PositiveInfinity;
                int a=-1,b=-1,c=-1;Vector3 bary=default;selectedMaterial=-1;
                for(int s=0;s<subs.Length;s++)
                {
                    for(int i=0;i<subs[s].Length;i+=3)
                    {
                        int x=start+subs[s][i],y=start+subs[s][i+1],z=start+subs[s][i+2];
                        var pa=vertices[x].p;var pb=vertices[y].p;var pc=vertices[z].p;
                        var faceNormal=FaceNormal(pa,pb,pc);if(faceNormal==Vector3.zero)continue;
                        var w=ClosestBarycentric(p,pa,pb,pc);if(!LTRoadMesh.Finite(w))continue;
                        var q=pa*w.x+pb*w.y+pc*w.z;double distance=(p-q).sqrMagnitude;
                        nearestSurface=Math.Min(nearestSurface,distance);
                        if(material>=0&&material!=s)continue;
                        // Side UVs come from the nearest lateral face, even when a coarse
                        // authored LOD has a different local slope. Caps keep their own UVs.
                        if(Math.Abs(normal.z)>.99f?Vector3.Dot(faceNormal,normal)<.99f:Math.Abs(faceNormal.z)>.99f)continue;
                        if(distance>=best)continue;best=distance;a=x;b=y;c=z;bary=w;selectedMaterial=s;
                    }
                }
                if(a<0)throw JointError("не найдена поверхность для переноса UV/материала торца.");
                if(nearestSurface>(double)maxChange*maxChange)throw JointError($"новый край удалён от исходной поверхности на {Math.Sqrt(nearestSurface):G4} м при допуске {maxChange:G4} м.");
                var va=vertices[a];var vb=vertices[b];var vc=vertices[c];
                var tangent=va.t*bary.x+vb.t*bary.y+vc.t*bary.z;
                tangent.w=bary.x>=bary.y&&bary.x>=bary.z?va.t.w:bary.y>=bary.z?vb.t.w:vc.t.w;
                return new Vertex{p=p,n=normal,t=tangent,
                    uv=va.uv*bary.x+vb.uv*bary.y+vc.uv*bary.z,uv2=va.uv2*bary.x+vb.uv2*bary.y+vc.uv2*bary.z,
                    uv3=va.uv3*bary.x+vb.uv3*bary.y+vc.uv3*bary.z,uv4=va.uv4*bary.x+vb.uv4*bary.y+vc.uv4*bary.z,
                    color=va.color*bary.x+vb.color*bary.y+vc.color*bary.z};
            }
            static Vector3 ClosestBarycentric(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
            {
                double Dot(Vector3 x,Vector3 y)=>(double)x.x*y.x+(double)x.y*y.y+(double)x.z*y.z;
                var ab=b-a;var ac=c-a;var ap=p-a;double d1=Dot(ab,ap),d2=Dot(ac,ap);
                if(d1<=0&&d2<=0)return new Vector3(1,0,0);
                var bp=p-b;double d3=Dot(ab,bp),d4=Dot(ac,bp);
                if(d3>=0&&d4<=d3)return new Vector3(0,1,0);
                double vc=d1*d4-d3*d2;
                if(vc<=0&&d1>=0&&d3<=0){float v=(float)(d1/(d1-d3));return new Vector3(1-v,v,0);}
                var cp=p-c;double d5=Dot(ab,cp),d6=Dot(ac,cp);
                if(d6>=0&&d5<=d6)return new Vector3(0,0,1);
                double vb=d5*d2-d1*d6;
                if(vb<=0&&d2>=0&&d6<=0){float w=(float)(d2/(d2-d6));return new Vector3(1-w,0,w);}
                double va=d3*d6-d5*d4;
                if(va<=0&&d4-d3>=0&&d5-d6>=0){float w=(float)((d4-d3)/(d4-d3+d5-d6));return new Vector3(0,1-w,w);}
                double sum=va+vb+vc;return new Vector3((float)(va/sum),(float)(vb/sum),(float)(vc/sum));
            }
        }
    }
}
