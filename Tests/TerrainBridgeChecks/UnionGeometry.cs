using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
namespace LocalTerrainPrototype {
    public static class LTUnionGeometry
    {
        public struct Vertex
        {
            public Vector3 p;public Vector2 uv;
            public Vertex(Vector3 position,Vector2 tex){p=position;uv=tex;}
            public static Vertex Lerp(Vertex a,Vertex b,float t)=>new Vertex(Vector3.LerpUnclamped(a.p,b.p,t),Vector2.LerpUnclamped(a.uv,b.uv,t));
        }
        public sealed class Face
        {
            public List<Vertex> vertices;public int material,owner;public Vector3 normal;public float offset;
            public Face(IEnumerable<Vertex> v,int m,int o)
            {
                vertices=v.ToList();material=m;owner=o;
                for(int i=1;i+1<vertices.Count;i++)
                {normal=Vector3.Cross(vertices[i].p-vertices[0].p,vertices[i+1].p-vertices[0].p).normalized;if(normal.sqrMagnitude>.5f)break;}
                offset=Vector3.Dot(normal,vertices[0].p);
            }
            public Face Copy()=>new Face(vertices,material,owner){normal=normal,offset=offset};
            public void Flip(){vertices.Reverse();normal=-normal;offset=-offset;}
        }
        sealed class Work
        {
            public readonly Stopwatch timer=Stopwatch.StartNew();public int visits;public readonly int budget;
            public Work(int limit){budget=limit;}
            public void Check(int count=0){if(++visits>2000000||count>budget||timer.Elapsed.TotalSeconds>15)throw new InvalidOperationException("Union geometry budget exceeded; previous meshes preserved. Reduce source complexity or seam density.");}
        }
        const float Epsilon=.00002f;
        sealed class Node
        {
            Vector3 normal;float offset;bool plane;List<Face> faces=new List<Face>();Node front,back;readonly Work work;
            public Node(Work w){work=w;}
            void Split(Face f,List<Face> copFront,List<Face> copBack,List<Face> ahead,List<Face> behind)
            {
                work.Check();int kind=0;var types=new int[f.vertices.Count];
                for(int i=0;i<types.Length;i++){float d=Vector3.Dot(normal,f.vertices[i].p)-offset;types[i]=d>Epsilon?1:d<-Epsilon?2:0;kind|=types[i];}
                if(kind==0){(Vector3.Dot(normal,f.normal)>=0?copFront:copBack).Add(f);return;}
                if(kind==1){ahead.Add(f);return;}if(kind==2){behind.Add(f);return;}
                var a=new List<Vertex>();var b=new List<Vertex>();
                for(int i=0;i<types.Length;i++)
                {
                    int j=(i+1)%types.Length;var x=f.vertices[i];var y=f.vertices[j];
                    if(types[i]!=2)a.Add(x);if(types[i]!=1)b.Add(x);
                    if((types[i]|types[j])==3)
                    {
                        float t=(offset-Vector3.Dot(normal,x.p))/Vector3.Dot(normal,y.p-x.p);
                        var v=Vertex.Lerp(x,y,Mathf.Clamp01(t));a.Add(v);b.Add(v);
                    }
                }
                // A fragment is still on its original plane. Re-fitting a plane to
                // a tiny clipped sliver amplifies float error throughout a deep tree.
                if(a.Count>=3){var p=new Face(a,f.material,f.owner);if(p.normal.sqrMagnitude>.5f){p.normal=f.normal;p.offset=f.offset;ahead.Add(p);}}
                if(b.Count>=3){var p=new Face(b,f.material,f.owner);if(p.normal.sqrMagnitude>.5f){p.normal=f.normal;p.offset=f.offset;behind.Add(p);}}
            }
            public void Build(List<Face> input)
            {
                // Convex meshes naturally make a chain of face planes. Its depth can
                // exceed 256 without any invalid geometry; use bounded explicit work
                // stacks for every BSP operation, not the CLR call stack.
                var pending=new Stack<(Node node,List<Face> polygons)>();pending.Push((this,input));
                while(pending.Count>0)
                {
                    var item=pending.Pop();var node=item.node;var polygons=item.polygons;
                    work.Check(polygons.Count);if(polygons.Count==0)continue;
                    if(!node.plane){var face=polygons[polygons.Count/2];node.normal=face.normal;node.offset=face.offset;node.plane=true;}
                    var a=new List<Face>();var b=new List<Face>();
                    foreach(var f in polygons)node.Split(f,node.faces,node.faces,a,b);
                    if(b.Count>0){if(node.back==null)node.back=new Node(work);pending.Push((node.back,b));}
                    if(a.Count>0){if(node.front==null)node.front=new Node(work);pending.Push((node.front,a));}
                }
            }
            IEnumerable<Node> Nodes()
            {
                var pending=new Stack<Node>();pending.Push(this);
                while(pending.Count>0)
                {
                    var node=pending.Pop();work.Check();
                    // Queue children before yielding so inversion can safely swap them.
                    if(node.back!=null)pending.Push(node.back);if(node.front!=null)pending.Push(node.front);
                    yield return node;
                }
            }
            public void Invert()
            {
                foreach(var node in Nodes())
                {foreach(var f in node.faces)f.Flip();node.normal=-node.normal;node.offset=-node.offset;var temp=node.front;node.front=node.back;node.back=temp;}
            }
            List<Face> Clip(List<Face> input)
            {
                var result=new List<Face>();var pending=new Stack<(Node node,List<Face> polygons)>();pending.Push((this,input));
                while(pending.Count>0)
                {
                    var item=pending.Pop();var node=item.node;work.Check(item.polygons.Count);
                    if(item.polygons.Count==0)continue;
                    if(!node.plane){result.AddRange(item.polygons);continue;}
                    var a=new List<Face>();var b=new List<Face>();
                    foreach(var f in item.polygons)node.Split(f,a,b,a,b);
                    if(node.back!=null&&b.Count>0)pending.Push((node.back,b));
                    if(node.front!=null&&a.Count>0)pending.Push((node.front,a));else if(node.front==null)result.AddRange(a);
                    work.Check(result.Count);
                }
                return result;
            }
            public void ClipTo(Node other){foreach(var node in Nodes())node.faces=other.Clip(node.faces);}
            public List<Face> All()
            {var all=new List<Face>();foreach(var node in Nodes()){all.AddRange(node.faces);work.Check(all.Count);}return all;}
        }
        public static List<Face> Union(List<Face> first,List<Face> second,int budget)
        {
            var work=new Work(budget);var a=new Node(work);var b=new Node(work);
            a.Build(first.Select(f=>f.Copy()).ToList());b.Build(second.Select(f=>f.Copy()).ToList());
            a.ClipTo(b);b.ClipTo(a);b.Invert();b.ClipTo(a);b.Invert();a.Build(b.All());return a.All();
        }
        public sealed class Surface
        {
            public List<Vertex> vertices=new List<Vertex>();public List<int[]> triangles=new List<int[]>();
            public List<int> materials=new List<int>(),owners=new List<int>();public List<Vector3> seam=new List<Vector3>();
        }
        static long Edge(int a,int b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
        static Dictionary<long,List<int>> Edges(Surface s)
        {
            var edges=new Dictionary<long,List<int>>();
            for(int i=0;i<s.triangles.Count;i++)for(int k=0;k<3;k++)
            {var t=s.triangles[i];long e=Edge(t[k],t[(k+1)%3]);if(!edges.TryGetValue(e,out var list))edges[e]=list=new List<int>();list.Add(i);}
            return edges;
        }
        public static void ValidateClosed(Surface s)
        {
            foreach(var e in Edges(s))
            {
                if(e.Value.Count!=2)throw new InvalidOperationException($"Union source/result is not a closed manifold: edge {s.vertices[(int)(e.Key>>32)].p.ToString("R")} -> {s.vertices[(int)(e.Key&0xffffffff)].p.ToString("R")}, {e.Value.Count} incident faces.");
                int direction=0;
                foreach(int f in e.Value){var t=s.triangles[f];for(int k=0;k<3;k++)if(Edge(t[k],t[(k+1)%3])==e.Key)direction+=t[k]<t[(k+1)%3]?1:-1;}
                if(direction!=0)throw new InvalidOperationException("Union face winding is inconsistent.");
            }
        }
        public static Surface Triangulate(List<Face> faces,int budget)
        {
            // Independent plane clips can approach the same junction from opposite
            // sides of the classification tolerance. Reconcile that bounded error.
            const float weldTolerance=Epsilon*4;
            var s=new Surface();var work=new Work(budget);var buckets=new Dictionary<Vector3Int,List<int>>();
            Func<Vertex,int> vertex=v=>
            {
                var key=new Vector3Int(Mathf.FloorToInt(v.p.x/weldTolerance),Mathf.FloorToInt(v.p.y/weldTolerance),Mathf.FloorToInt(v.p.z/weldTolerance));
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                    if(buckets.TryGetValue(key+new Vector3Int(x,y,z),out var near))foreach(int i in near)if((s.vertices[i].p-v.p).sqrMagnitude<=weldTolerance*weldTolerance)return i;
                int id=s.vertices.Count;s.vertices.Add(v);if(!buckets.TryGetValue(key,out var own))buckets[key]=own=new List<int>();own.Add(id);work.Check(s.vertices.Count);return id;
            };
            var polygons=faces.Select(f=>f.vertices.Select(vertex).ToList()).ToList();int original=s.vertices.Count;
            for(int f=0;f<polygons.Count;f++)
            {
                var polygon=polygons[f];var boundary=new List<int>();
                for(int k=0;k<polygon.Count;k++)
                {
                    int a=polygon[k],b=polygon[(k+1)%polygon.Count];if(a==b)continue;
                    var p=s.vertices[a].p;var delta=s.vertices[b].p-p;float length=delta.sqrMagnitude;
                    var onEdge=new List<(float,int)>();boundary.Add(a);
                    for(int j=0;j<original;j++)
                    {
                        if(j==a||j==b)continue;float t=Vector3.Dot(s.vertices[j].p-p,delta)/length;
                        // Match the positional reconciliation tolerance above. A
                        // representative moved within that tolerance must still
                        // split every incident edge, not leave a T-junction.
                        if(t>0&&t<1&&(s.vertices[j].p-p-t*delta).sqrMagnitude<=weldTolerance*weldTolerance)onEdge.Add((t,j));
                    }
                    boundary.AddRange(onEdge.OrderBy(v=>v.Item1).Select(v=>v.Item2));work.Check();
                }
                boundary=boundary.Distinct().ToList();if(boundary.Count<3)continue;
                if(boundary.Count==3){s.triangles.Add(boundary.ToArray());s.materials.Add(faces[f].material);s.owners.Add(faces[f].owner);continue;}
                Vector3 centre=Vector3.zero;Vector2 uv=Vector2.zero;foreach(int id in boundary){centre+=s.vertices[id].p;uv+=s.vertices[id].uv;}
                int middle=vertex(new Vertex(centre/boundary.Count,uv/boundary.Count));
                for(int k=0;k<boundary.Count;k++){s.triangles.Add(new[]{boundary[k],boundary[(k+1)%boundary.Count],middle});s.materials.Add(faces[f].material);s.owners.Add(faces[f].owner);}
            }
            ValidateClosed(s);
            foreach(var edge in Edges(s))if(s.owners[edge.Value[0]]!=s.owners[edge.Value[1]])
            {s.seam.Add(s.vertices[(int)(edge.Key>>32)].p);s.seam.Add(s.vertices[(int)(edge.Key&0xffffffff)].p);}
            return s;
        }
        static float SeamDistance(Surface s,Vector3 p)
        {
            float best=float.MaxValue;
            for(int i=0;i+1<s.seam.Count;i+=2){var a=s.seam[i];var d=s.seam[i+1]-a;float t=d.sqrMagnitude>1e-12f?Mathf.Clamp01(Vector3.Dot(p-a,d)/d.sqrMagnitude):0;best=Mathf.Min(best,(p-a-t*d).sqrMagnitude);}
            return Mathf.Sqrt(best);
        }
        public static void RemeshSeam(Surface s,Func<Vector3,float> cellAt,float width,int budget)
        {
            if(width<=0||s.seam.Count==0)return;var work=new Work(budget);
            // Conforming edge splits only in the common seam band; no whole-mesh subdivision.
            for(int pass=0;pass<12;pass++)
            {
                var splits=new Dictionary<long,int>();
                foreach(var e in Edges(s))
                {
                    int a=(int)(e.Key>>32),b=(int)(e.Key&0xffffffff);var va=s.vertices[a];var vb=s.vertices[b];var mid=Vertex.Lerp(va,vb,.5f);
                    if(SeamDistance(s,va.p)>=width||SeamDistance(s,vb.p)>=width||Vector3.Distance(va.p,vb.p)<=1.4f*cellAt(mid.p))continue;
                    splits[e.Key]=s.vertices.Count;s.vertices.Add(mid);work.Check(s.vertices.Count);
                }
                if(splits.Count==0)break;
                var triangles=new List<int[]>();var materials=new List<int>();var owners=new List<int>();
                for(int i=0;i<s.triangles.Count;i++)
                {
                    var t=s.triangles[i];var ring=new List<int>();for(int k=0;k<3;k++){ring.Add(t[k]);if(splits.TryGetValue(Edge(t[k],t[(k+1)%3]),out int split))ring.Add(split);}
                    if(ring.Count==3){triangles.Add(t);materials.Add(s.materials[i]);owners.Add(s.owners[i]);continue;}
                    void Emit(int a,int b,int c){triangles.Add(new[]{a,b,c});materials.Add(s.materials[i]);owners.Add(s.owners[i]);}
                    int[] midpoints=Enumerable.Range(0,3).Select(k=>splits.TryGetValue(Edge(t[k],t[(k+1)%3]),out int m)?m:-1).ToArray();
                    if(ring.Count==6)
                    {Emit(t[0],midpoints[0],midpoints[2]);Emit(midpoints[0],t[1],midpoints[1]);Emit(midpoints[2],midpoints[1],t[2]);Emit(midpoints[0],midpoints[1],midpoints[2]);}
                    else if(ring.Count==4)
                    {int k=Array.FindIndex(midpoints,m=>m>=0);Emit(t[k],midpoints[k],t[(k+2)%3]);Emit(midpoints[k],t[(k+1)%3],t[(k+2)%3]);}
                    else
                    {int k=(Array.FindIndex(midpoints,m=>m<0)+1)%3;int a=t[k],b=t[(k+1)%3],c=t[(k+2)%3],ab=midpoints[k],bc=midpoints[(k+1)%3];Emit(ab,b,bc);Emit(a,ab,c);Emit(ab,bc,c);}
                }
                s.triangles=triangles;s.materials=materials;s.owners=owners;work.Check(triangles.Count);
                if(pass==11)throw new InvalidOperationException("Local Union density did not converge within the remesh budget.");
            }
            // Improve local triangle shapes without touching the outer surface or
            // crossing a material/ownership boundary. A flip is accepted only when
            // both new faces keep their orientation and the worst quality improves.
            float Quality(Vector3 a,Vector3 b,Vector3 c)
            {return Vector3.Cross(b-a,c-a).magnitude/Mathf.Max(1e-12f,(b-a).sqrMagnitude+(c-b).sqrMagnitude+(a-c).sqrMagnitude);}
            for(int pass=0;pass<4;pass++)
            {
                var edges=Edges(s);var reserved=new HashSet<int>();bool changed=false;
                foreach(var edge in edges.ToArray())
                {
                    if(edge.Value.Count!=2)continue;int f=edge.Value[0],g=edge.Value[1];
                    if(reserved.Contains(f)||reserved.Contains(g)||s.materials[f]!=s.materials[g]||s.owners[f]!=s.owners[g])continue;
                    var t=s.triangles[f];int k=Array.FindIndex(t,v=>v==(int)(edge.Key>>32));
                    int a=t[k],b=t[(k+1)%3],c=t[(k+2)%3];
                    if(Edge(a,b)!=edge.Key){b=t[(k+2)%3];c=t[(k+1)%3];int swap=a;a=b;b=swap;}
                    int d=s.triangles[g].First(v=>v!=a&&v!=b);long diagonal=Edge(c,d);
                    if(c==d||edges.ContainsKey(diagonal))continue;
                    Vector3 pa=s.vertices[a].p,pb=s.vertices[b].p,pc=s.vertices[c].p,pd=s.vertices[d].p;
                    if(new[]{pa,pb,pc,pd}.Any(p=>SeamDistance(s,p)>=width))continue;
                    Vector3 oldA=Vector3.Cross(pb-pa,pc-pa),oldB=Vector3.Cross(pa-pb,pd-pb);
                    if(Vector3.Dot(oldA.normalized,oldB.normalized)<.99999f)continue;
                    if(Vector3.Dot(oldA,Vector3.Cross(pd-pc,pb-pc))<=0||Vector3.Dot(oldB,Vector3.Cross(pc-pd,pa-pd))<=0)continue;
                    float before=Mathf.Min(Quality(pa,pb,pc),Quality(pb,pa,pd));
                    float after=Mathf.Min(Quality(pc,pd,pb),Quality(pd,pc,pa));if(after<=before+1e-5f)continue;
                    s.triangles[f]=new[]{c,d,b};s.triangles[g]=new[]{d,c,a};
                    edges.Remove(edge.Key);edges[diagonal]=new List<int>{f,g};reserved.Add(f);reserved.Add(g);changed=true;work.Check();
                }
                if(!changed)break;
            }
            var neighbours=Enumerable.Range(0,s.vertices.Count).Select(_=>new HashSet<int>()).ToArray();
            var incident=Enumerable.Range(0,s.vertices.Count).Select(_=>new List<int>()).ToArray();
            for(int f=0;f<s.triangles.Count;f++)foreach(int a in s.triangles[f]){incident[a].Add(f);foreach(int b in s.triangles[f])if(a!=b)neighbours[a].Add(b);}
            var original=s.vertices.Select(v=>v.p).ToArray();
            for(int pass=0;pass<8;pass++)for(int i=0;i<s.vertices.Count;i++)
            {
                float d=SeamDistance(s,original[i]);if(d>=width||neighbours[i].Count==0)continue;
                float w=1-d/width;w=w*w*(3-2*w);Vector3 mean=Vector3.zero;foreach(int j in neighbours[i])mean+=s.vertices[j].p;mean/=neighbours[i].Count;
                Vector3 p=Vector3.Lerp(s.vertices[i].p,mean,.25f*w);
                p=original[i]+Vector3.ClampMagnitude(p-original[i],width*.25f*w);bool safe=true;
                foreach(int face in incident[i])
                {
                    var t=s.triangles[face];Vector3 a=s.vertices[t[0]].p,b=s.vertices[t[1]].p,c=s.vertices[t[2]].p;
                    var before=Vector3.Cross(b-a,c-a);if(t[0]==i)a=p;if(t[1]==i)b=p;if(t[2]==i)c=p;var after=Vector3.Cross(b-a,c-a);
                    if(after.sqrMagnitude<1e-14f||Vector3.Dot(before,after)<=0){safe=false;break;}
                }
                if(safe){var v=s.vertices[i];v.p=p;s.vertices[i]=v;}work.Check();
            }
            ValidateClosed(s);
        }
    }
}
