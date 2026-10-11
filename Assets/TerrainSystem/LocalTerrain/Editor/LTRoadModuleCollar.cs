using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Source-space surgery on copies only. Original faces strictly inside the centre are retained.
    // The two end bands come from fitted LOD0; directed cut contours are joined without welding UV seams.
    public static partial class LTRoadModuleCollar
    {
        const float Epsilon=.00001f;
        struct Vertex
        {
            public Vector3 p,n;
            public Vector4 t;
            public Vector2 uv,uv2,uv3,uv4;
            public Color color;
        }
        sealed class Edge
        {
            public int a,b,material;
            public Vector3 normal;
            public Edge Reverse()=>new Edge{a=b,b=a,material=material,normal=normal};
        }
        sealed class Contour
        {
            public List<Edge> edges=new List<Edge>();
            public bool closed;
        }
        sealed partial class Builder
        {
            public readonly List<Vertex> vertices=new List<Vertex>();
            public readonly List<List<int>> faces=new List<List<int>>();
            readonly Dictionary<(int,int,float),int> intersections=new Dictionary<(int,int,float),int>();
            readonly Dictionary<float,List<Vector3>> cutPositions=new Dictionary<float,List<Vector3>>();
            bool uv2,uv3,uv4,colors;
            public int Add(LTRoadMesh.Chunk mesh)
            {
                int start=vertices.Count;
                uv2|=mesh.uv2!=null;uv3|=mesh.uv3!=null;uv4|=mesh.uv4!=null;colors|=mesh.colors!=null;
                for(int i=0;i<mesh.vertices.Length;i++)
                {
                    if(!LTRoadMesh.Finite(mesh.vertices[i]))throw Error("нечисловые координаты исходного меша.");
                    vertices.Add(new Vertex{p=mesh.vertices[i],n=mesh.normals[i],t=mesh.tangents[i],uv=mesh.uv[i],
                        uv2=mesh.uv2==null?Vector2.zero:mesh.uv2[i],uv3=mesh.uv3==null?Vector2.zero:mesh.uv3[i],
                        uv4=mesh.uv4==null?Vector2.zero:mesh.uv4[i],color=mesh.colors==null?Color.white:mesh.colors[i]});
                }
                return start;
            }
            int Cross(int a,int b,float z)
            {
                if(vertices[a].p.z==z)return a;if(vertices[b].p.z==z)return b;
                if(a>b){int swap=a;a=b;b=swap;}
                var key=(a,b,z);if(intersections.TryGetValue(key,out int id))return id;
                var x=vertices[a];var y=vertices[b];double f=((double)z-x.p.z)/((double)y.p.z-x.p.z);float t=(float)f;
                var p=new Vector3((float)(x.p.x+((double)y.p.x-x.p.x)*f),(float)(x.p.y+((double)y.p.y-x.p.y)*f),z);
                if(!cutPositions.TryGetValue(z,out var positions))cutPositions[z]=positions=new List<Vector3>();
                int near=positions.FindIndex(q=>(p-q).sqrMagnitude<=Epsilon*Epsilon);
                if(near>=0)p=positions[near];else positions.Add(p);
                var n=Vector3.LerpUnclamped(x.n,y.n,t).normalized;
                var tangent=Vector4.LerpUnclamped(x.t,y.t,t);var dir=new Vector3(tangent.x,tangent.y,tangent.z);
                dir=(dir-n*Vector3.Dot(n,dir)).normalized;
                var v=new Vertex{p=p,n=n,t=new Vector4(dir.x,dir.y,dir.z,x.t.w),uv=Vector2.LerpUnclamped(x.uv,y.uv,t),
                    uv2=Vector2.LerpUnclamped(x.uv2,y.uv2,t),uv3=Vector2.LerpUnclamped(x.uv3,y.uv3,t),
                    uv4=Vector2.LerpUnclamped(x.uv4,y.uv4,t),color=Color.LerpUnclamped(x.color,y.color,t)};
                id=vertices.Count;vertices.Add(v);intersections[key]=id;return id;
            }
            List<int> Clip(List<int> polygon,float z,bool above)
            {
                var output=new List<int>();
                for(int i=0;i<polygon.Count;i++)
                {
                    int a=polygon[i],b=polygon[(i+1)%polygon.Count];
                    bool ina=above?vertices[a].p.z>=z:vertices[a].p.z<=z,inb=above?vertices[b].p.z>=z:vertices[b].p.z<=z;
                    if(ina)output.Add(a);if(ina!=inb)output.Add(Cross(a,b,z));
                }
                for(int i=output.Count-1;i>=0&&output.Count>1;i--)
                    if(vertices[output[i]].p==vertices[output[(i+1)%output.Count]].p)output.RemoveAt(i);
                return output;
            }
            public void Keep(LTRoadMesh.Chunk mesh,int start,float min,float max)
            {
                var subs=mesh.submeshes??new[]{mesh.triangles};
                for(int s=0;s<subs.Length;s++)for(int i=0;i<subs[s].Length;i+=3)
                {
                    var polygon=new List<int>{start+subs[s][i],start+subs[s][i+1],start+subs[s][i+2]};
                    if(!float.IsNegativeInfinity(min))polygon=Clip(polygon,min,true);
                    if(!float.IsPositiveInfinity(max))polygon=Clip(polygon,max,false);
                    for(int j=1;j+1<polygon.Count;j++)Face(polygon[0],polygon[j],polygon[j+1],s,false);
                }
            }
            public void Face(int a,int b,int c,int material,bool bridge,Vector3 expected=default)
            {
                var cross=Vector3.Cross(vertices[b].p-vertices[a].p,vertices[c].p-vertices[a].p);
                if(cross.sqrMagnitude<1e-22f){if(bridge)throw Error("вырожденная грань соединительной полосы.");return;}
                if(bridge&&Vector3.Dot(cross,expected)<=0)throw Error("соединение переворачивает поверхность. Увеличьте торцевую зону или исправьте профиль LOD. "+
                    $"Грань {Point(vertices[a].p)} / {Point(vertices[b].p)} / {Point(vertices[c].p)}; normal dot={Vector3.Dot(FaceNormal(vertices[a].p,vertices[b].p,vertices[c].p),expected):G6}.");
                while(faces.Count<=material)faces.Add(new List<int>());faces[material].AddRange(new[]{a,b,c});
            }
            public List<Contour> Contours(float plane)
            {
                var edges=new Dictionary<(Vector3,Vector3),Edge>();
                var uses=new Dictionary<(Vector3,Vector3),int>();
                for(int s=0;s<faces.Count;s++)for(int i=0;i<faces[s].Count;i+=3)
                {
                    int a=faces[s][i],b=faces[s][i+1],c=faces[s][i+2];
                    var normal=FaceNormal(vertices[a].p,vertices[b].p,vertices[c].p);
                    Add(a,b,c,s,normal);Add(b,c,a,s,normal);Add(c,a,b,s,normal);
                }
                void Add(int a,int b,int c,int material,Vector3 normal)
                {
                    var x=vertices[a].p;var y=vertices[b].p;
                    if(x.z!=plane||y.z!=plane||vertices[c].p.z==plane||x==y)return;
                    bool order=x.x<y.x||(x.x==y.x&&x.y<y.y);var key=order?(x,y):(y,x);
                    uses.TryGetValue(key,out int count);if(count>=2)throw Error("более двух граней на ребре сечения.");uses[key]=count+1;
                    if(edges.Remove((y,x)))return;
                    if(edges.ContainsKey((x,y)))throw Error("наложенные грани в сечении торцевой зоны.");
                    edges.Add((x,y),new Edge{a=a,b=b,material=material,normal=normal});
                }
                var outgoing=new Dictionary<Vector3,Edge>();var incoming=new HashSet<Vector3>();
                foreach(var e in edges.Values)
                {
                    if(outgoing.ContainsKey(vertices[e.a].p)||!incoming.Add(vertices[e.b].p))throw Error("ветвящийся контур сечения. Измените ширину торцевой зоны.");
                    outgoing.Add(vertices[e.a].p,e);
                }
                var contours=new List<Contour>();
                while(outgoing.Count>0)
                {
                    var open=outgoing.Keys.Where(p=>!incoming.Contains(p)).OrderBy(p=>p.x).ThenBy(p=>p.y).ToArray();
                    var first=open.Length>0?open[0]:outgoing.Keys.OrderBy(p=>p.x).ThenBy(p=>p.y).First();
                    var path=new Contour();var point=first;
                    while(outgoing.TryGetValue(point,out var edge))
                    {outgoing.Remove(point);path.edges.Add(edge);point=vertices[edge.b].p;if(point==first){path.closed=true;break;}}
                    contours.Add(path);
                }
                if(contours.Count>32||edges.Count>4096)throw Error("слишком сложное сечение торца.");
                return contours;
            }
            public void Join(float outer,float inner)
            {
                var a=Contours(outer);var b=Contours(inner);
                if(a.Count==0||a.Count!=b.Count)throw Error($"сечения имеют разное число отдельных контуров ({a.Count}/{b.Count}). Измените ширину торцевой зоны.");
                foreach(var path in b){path.edges.Reverse();path.edges=path.edges.Select(e=>e.Reverse()).ToList();}
                var available=new HashSet<int>(Enumerable.Range(0,b.Count));
                foreach(var first in a)
                {
                    var choices=available.Select(i=>(index:i,cost:Cost(first,b[i]))).OrderBy(v=>v.cost).ToArray();
                    if(choices.Length==0||double.IsInfinity(choices[0].cost)||
                        (choices.Length>1&&Math.Abs(choices[0].cost-choices[1].cost)<1e-10))throw Error("неоднозначное соответствие контуров разных LOD.");
                    var second=b[choices[0].index];available.Remove(choices[0].index);
                    if(first.closed)
                    {
                        int shift=0;double best=double.PositiveInfinity;
                        for(int i=0;i<second.edges.Count;i++)
                        {double distance=XY(vertices[first.edges[0].a].p,vertices[second.edges[i].a].p);if(distance<best){best=distance;shift=i;}}
                        second.edges=second.edges.Skip(shift).Concat(second.edges.Take(shift)).ToList();
                    }
                    Stitch(first,second);
                }
                if(Contours(outer).Count!=0||Contours(inner).Count!=0)throw Error("соединительная полоса оставила незашитое ребро.");
            }
            static double XY(Vector3 a,Vector3 b){double x=(double)a.x-b.x,y=(double)a.y-b.y;return x*x+y*y;}
            double Cost(Contour a,Contour b)
            {
                if(a.closed!=b.closed)return double.PositiveInfinity;
                Vector3 Centre(Contour p)
                {Vector3 sum=Vector3.zero;foreach(var e in p.edges)sum+=vertices[e.a].p+vertices[e.b].p;return sum/(2*p.edges.Count);}
                double cost=XY(Centre(a),Centre(b));
                if(!a.closed)cost+=XY(vertices[a.edges[0].a].p,vertices[b.edges[0].a].p)+XY(vertices[a.edges.Last().b].p,vertices[b.edges.Last().b].p);
                return cost;
            }
            void Stitch(Contour a,Contour b)
            {
                double[] Distances(Contour path)
                {
                    var d=new double[path.edges.Count+1];
                    for(int k=0;k<path.edges.Count;k++)d[k+1]=d[k]+Math.Sqrt(XY(vertices[path.edges[k].a].p,vertices[path.edges[k].b].p));
                    if(d.Last()<Epsilon)throw Error("нулевая длина контура.");
                    for(int k=1;k<d.Length;k++)d[k]/=d.Last();return d;
                }
                var ad=Distances(a);var bd=Distances(b);int i=0,j=0;
                while(i<a.edges.Count||j<b.edges.Count)
                {
                    int av=i<a.edges.Count?a.edges[i].a:a.edges.Last().b,bv=j<b.edges.Count?b.edges[j].a:b.edges.Last().b;
                    if(j==b.edges.Count||(i<a.edges.Count&&ad[i+1]<=bd[j+1]))
                    {var e=a.edges[i++];Face(e.b,e.a,bv,e.material,true,e.normal);}
                    else{var e=b.edges[j++];Face(av,e.a,e.b,e.material,true,e.normal);}
                }
            }
            public LTRoadMesh.Chunk Finish()
            {
                var mesh=new LTRoadMesh.Chunk{vertices=vertices.Select(v=>v.p).ToArray(),normals=vertices.Select(v=>v.n).ToArray(),
                    tangents=vertices.Select(v=>v.t).ToArray(),uv=vertices.Select(v=>v.uv).ToArray(),
                    uv2=uv2?vertices.Select(v=>v.uv2).ToArray():null,uv3=uv3?vertices.Select(v=>v.uv3).ToArray():null,
                    uv4=uv4?vertices.Select(v=>v.uv4).ToArray():null,colors=colors?vertices.Select(v=>v.color).ToArray():null};
                return LTRoadModuleMath.Compact(mesh,faces.Select(f=>f.ToArray()).ToArray());
            }
        }
        static InvalidOperationException Error(string message)=>new InvalidOperationException("Торцевые полосы LOD0: "+message);
        static string Point(Vector3 p)=>FormattableString.Invariant($"({p.x:G9}, {p.y:G9}, {p.z:G9})");
        // Unity's Vector3.normalized returns zero for vectors shorter than 1e-5.
        // Clipping can legitimately produce thin faces below that area; their orientation is not zero.
        static Vector3 FaceNormal(Vector3 a,Vector3 b,Vector3 c)
        {
            double ux=(double)b.x-a.x,uy=(double)b.y-a.y,uz=(double)b.z-a.z;
            double vx=(double)c.x-a.x,vy=(double)c.y-a.y,vz=(double)c.z-a.z;
            double x=uy*vz-uz*vy,y=uz*vx-ux*vz,z=ux*vy-uy*vx;
            double length=Math.Sqrt(x*x+y*y+z*z);
            return length>0?new Vector3((float)(x/length),(float)(y/length),(float)(z/length)):Vector3.zero;
        }
        public static LTRoadMesh.Chunk Prepare(LTRoadMesh.Chunk reference,LTRoadMesh.Chunk authored,LTRoadModuleEnds.Plan fit,float width,out bool restored)
        {
            restored=false;
            try
            {
                var compatible=fit==null?authored:LTRoadModuleEnds.Apply(authored,fit);
                LTRoadModuleEnds.ValidateRims(reference,compatible);return compatible;
            }
            catch(InvalidOperationException)
            {
                // A different topology cannot be displaced to match. Replace only its end zones.
                var result=Apply(reference,authored,width);restored=true;return result;
            }
        }
        public static LTRoadMesh.Chunk Apply(LTRoadMesh.Chunk reference,LTRoadMesh.Chunk authored,float width)
        {
            float min=reference.vertices.Min(v=>v.z),max=reference.vertices.Max(v=>v.z);
            if(!LTRoadMesh.Finite(width)||width<.01f||width>(max-min)*.45f)throw Error("ширина должна быть от 0.01 м до 45% длины модуля.");
            if(authored.vertices.Any(p=>p.z<min-width||p.z>max+width))throw Error("LOD выходит за длину LOD0 дальше торцевой зоны.");
            var builder=new Builder();int a=builder.Add(reference),b=builder.Add(authored);
            float outer0=min+width*.5f,inner0=min+width,inner1=max-width,outer1=max-width*.5f;
            builder.Keep(reference,a,float.NegativeInfinity,outer0);builder.Keep(reference,a,outer1,float.PositiveInfinity);
            builder.Keep(authored,b,inner0,inner1);
            builder.Join(outer0,inner0);builder.Join(outer1,inner1);
            var result=builder.Finish();LTRoadModuleEnds.ValidateRims(reference,result);return result;
        }
    }
}
