using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // A geometric edge can have one long use opposite several short uses after
    // convex road clipping. Count the same atomic segments on both sides before
    // looking for openings. Uses the strict contour walker's existing 0.1 mm
    // endpoint weld BEFORE counting. Does not move render/collider vertices or
    // weaken the degree-two check. Resolvable unmatched segments survive.
    public static class LTRoadGeometryBoundary
    {
        // Topology needs the same 0.1 mm scale as the strict contour walker.
        // The separate 1 mm seam-normal key must NOT merge nearby cut corners.
        // Multiply in double so float rounding of large scaled coordinates does
        // not enlarge the quantization cell unpredictably with world distance.
        public static Vector3Int PointKey(Vector3 p)=>new Vector3Int(
            checked((int)Math.Round((double)p.x*10000)),checked((int)Math.Round((double)p.y*10000)),checked((int)Math.Round((double)p.z*10000)));
        public static Vector3Int TranslatedKey(Vector3 p,Vector3 offset)=>new Vector3Int(
            checked((int)Math.Round(((double)p.x+offset.x)*10000)),
            checked((int)Math.Round(((double)p.y+offset.y)*10000)),checked((int)Math.Round(((double)p.z+offset.z)*10000)));
        public readonly struct Edge
        {
            public readonly Vector3Int a,b;
            public readonly int count;
            public Edge(Vector3Int a,Vector3Int b,int count=1)
            {
                if(Compare(a,b)>0){var swap=a;a=b;b=swap;}
                this.a=a;this.b=b;this.count=count;
            }
        }
        struct Point {public Vector3Int key;public Vector3 position;}
        public static List<(int count,int a,int b)> MeshEdges(Vector3[] relative,int[] indices)
        {
            var referenced=new bool[relative.Length];foreach(int i in indices)referenced[i]=true;
            var representatives=new Dictionary<Vector3Int,int>();var ids=new int[relative.Length];
            for(int i=0;i<relative.Length;i++)if(referenced[i])
            {
                var key=PointKey(relative[i]);if(!representatives.TryGetValue(key,out int id))representatives.Add(key,id=i);ids[i]=id;
            }
            var counts=new Dictionary<(int,int),int>();
            for(int i=0;i+2<indices.Length;i+=3)
            {
                int a=ids[indices[i]],b=ids[indices[i+1]],c=ids[indices[i+2]];
                if(a==b||b==c||c==a)continue;
                for(int e=0;e<3;e++)
                {
                    int p=ids[indices[i+e]],q=ids[indices[i+(e+1)%3]];var key=(Math.Min(p,q),Math.Max(p,q));
                    counts.TryGetValue(key,out int count);counts[key]=count+1;
                }
            }
            var edges=new List<(int,int,int)>();foreach(var item in counts)edges.Add((item.Value,item.Key.Item1,item.Key.Item2));
            return NormalizeChunkEdges(relative,edges);
        }
        // Cancel interior T-junction uses BEFORE adding the chunk translation.
        // At kilometre offsets, world-space float rounding can turn a straight
        // sub-mm split into a false corner. Only surviving borders need global
        // accounting. Retain multi-use edges for cross-chunk overlap detection.
        public static List<(int count,int a,int b)> NormalizeChunkEdges(Vector3[] relative,List<(int count,int a,int b)> edges)
        {
            var ids=new Dictionary<Vector3Int,int>();var open=new List<Edge>();
            var result=new List<(int count,int a,int b)>();
            foreach(var edge in edges)
            {
                if(edge.count!=1){result.Add(edge);continue;}
                var a=PointKey(relative[edge.a]);var b=PointKey(relative[edge.b]);
                if(!ids.ContainsKey(a))ids.Add(a,edge.a);if(!ids.ContainsKey(b))ids.Add(b,edge.b);
                open.Add(new Edge(a,b));
            }
            foreach(var edge in Resolve(open,key=>relative[ids[key]],out _,preciseFrame:true))result.Add((1,ids[edge.a],ids[edge.b]));
            return result;
        }
        struct Node {public Vector3 min,max;public int start,end,left,right;}
        sealed class AxisComparer:IComparer<Point>
        {
            public int axis;
            public int Compare(Point a,Point b)
            {int c=a.position[axis].CompareTo(b.position[axis]);return c!=0?c:LTRoadGeometryBoundary.Compare(a.key,b.key);}
        }
        static int Compare(Vector3Int a,Vector3Int b)
        {int c=a.x.CompareTo(b.x);if(c==0)c=a.y.CompareTo(b.y);return c!=0?c:a.z.CompareTo(b.z);}
        static IReadOnlyList<Edge> WeldEndpoints(IReadOnlyList<Edge> source,Func<Vector3Int,Vector3> position)
        {
            // Rounded keys alone split points on opposite sides of a bucket
            // boundary, even when the strict walker would weld them. Counting
            // those points first can invent a three-use microscopic edge.
            var unique=new HashSet<Vector3Int>();foreach(var e in source)if(e.a!=e.b){unique.Add(e.a);unique.Add(e.b);}
            var keys=new List<Vector3Int>(unique);keys.Sort(Compare);
            var buckets=new Dictionary<Vector3Int,List<Vector3Int>>();
            var representatives=new Dictionary<Vector3Int,Vector3>();var aliases=new Dictionary<Vector3Int,Vector3Int>();
            foreach(var key in keys)
            {
                var p=position(key);var bucket=PointKey(p);var chosen=key;float nearest=1e-8f;bool found=false;
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                    if(buckets.TryGetValue(bucket+new Vector3Int(x,y,z),out var candidates))foreach(var candidate in candidates)
                    {
                        float distance=(p-representatives[candidate]).sqrMagnitude;
                        if(distance>nearest||found&&distance==nearest&&Compare(candidate,chosen)>=0)continue;
                        found=true;nearest=distance;chosen=candidate;
                    }
                aliases.Add(key,chosen);
                if(found)continue;
                representatives.Add(key,p);
                if(!buckets.TryGetValue(bucket,out var list))buckets[bucket]=list=new List<Vector3Int>();list.Add(key);
            }
            // Do not union chains: every alias must be within the fixed radius
            // of its representative, not merely close to another alias.
            var result=new List<Edge>(source.Count);
            var groups=new Dictionary<(Vector3Int,Vector3Int),List<Edge>>();
            foreach(var e in source)if(e.a!=e.b)
            {
                var a=aliases[e.a];var b=aliases[e.b];if(a==b)continue;
                var mapped=new Edge(a,b,e.count);var key=(mapped.a,mapped.b);
                if(!groups.TryGetValue(key,out var list))groups[key]=list=new List<Edge>();list.Add(e);
            }
            foreach(var group in groups)
            {
                var list=group.Value;
                // A triangle collapsed by the existing endpoint weld contributes
                // two NEW coincident uses. Cancel that pair before it can make a
                // neighbouring edge look collinear and invent a triple use.
                // Original duplicate edges and explicit multi-use counts stay.
                if(list.Count==2&&list[0].count==1&&list[1].count==1&&
                    (list[0].a!=list[1].a||list[0].b!=list[1].b))continue;
                foreach(var e in list)result.Add(new Edge(aliases[e.a],aliases[e.b],e.count));
            }
            return result;
        }
        public static List<Edge> Resolve(IReadOnlyList<Edge> source,Func<Vector3Int,Vector3> position,out int splitEdges,bool preciseFrame=false)
        {
            splitEdges=0;
            source=WeldEndpoints(source,position);
            var lookup=new Dictionary<Vector3Int,Vector3>();
            foreach(var e in source)if(e.a!=e.b){if(!lookup.ContainsKey(e.a))lookup.Add(e.a,position(e.a));if(!lookup.ContainsKey(e.b))lookup.Add(e.b,position(e.b));}
            var neighbours=new Dictionary<Vector3Int,List<Vector3Int>>();
            foreach(var e in source)if(e.a!=e.b)
            {
                if(!neighbours.TryGetValue(e.a,out var a))neighbours[e.a]=a=new List<Vector3Int>();a.Add(e.b);
                if(!neighbours.TryGetValue(e.b,out var b))neighbours[e.b]=b=new List<Vector3Int>();b.Add(e.a);
            }
            double LineDistance(Vector3 p,Vector3 a,Vector3 b)
            {
                double dx=(double)b.x-a.x,dy=(double)b.y-a.y,dz=(double)b.z-a.z;
                double x=(double)p.x-a.x,y=(double)p.y-a.y,z=(double)p.z-a.z;
                double length=dx*dx+dy*dy+dz*dz;if(length<1e-20)return double.PositiveInfinity;
                double t=(x*dx+y*dy+z*dz)/length;x-=dx*t;y-=dy*t;z-=dz*t;return x*x+y*y+z*z;
            }
            bool HasCollinearUse(Point p,Vector3 a,Vector3 b,double epsilon)
            {
                // Near a corner, proximity to the line alone also captures the
                // other side of a skinny triangle. Require an incident edge on
                // the same supporting line in BOTH directions before splitting.
                // This keeps a nearby, differently oriented edge from becoming
                // three/four fake uses of a tiny atomic segment.
                foreach(var key in neighbours[p.key])
                {
                    var q=lookup[key];
                    // Extrapolating a shorter incident edge magnifies endpoint
                    // round-off. Scale only that angular check by the length
                    // ratio; proximity to the target line remains epsilon.
                    double useLength=(q-p.position).magnitude;
                    double targetLength=(b-a).magnitude;
                    // Absolute line-distance tolerances are not angular evidence
                    // when both edges are tiny. Otherwise any short triangular
                    // contour can be mistaken for overlapping collinear uses.
                    if(Math.Min(useLength,targetLength)<=epsilon*8)
                    {
                        double ux=(double)q.x-p.position.x,uy=(double)q.y-p.position.y,uz=(double)q.z-p.position.z;
                        double tx=(double)b.x-a.x,ty=(double)b.y-a.y,tz=(double)b.z-a.z;
                        double dot=ux*tx+uy*ty+uz*tz,product=(ux*ux+uy*uy+uz*uz)*(tx*tx+ty*ty+tz*tz);
                        // A fixed angle is also wrong: a one-ULP X step at
                        // 4 km can rotate a sub-mm edge considerably. Bound
                        // angular uncertainty using the varying float axes;
                        // a constant, unrelated large axis gives no allowance.
                        double noise2=0;
                        for(int axis=0;axis<3;axis++)
                        {
                            if(a[axis]==b[axis]&&a[axis]==p.position[axis]&&a[axis]==q[axis])continue;
                            double magnitude=Math.Max(Math.Max(Math.Abs(a[axis]),Math.Abs(b[axis])),Math.Max(Math.Abs(p.position[axis]),Math.Abs(q[axis])));
                            double rounding=magnitude*1.1920928955078125e-7;noise2+=rounding*rounding;
                        }
                        double sin=Math.Min(1,Math.Max(.1,2*Math.Sqrt(noise2)/Math.Max(1e-15,Math.Min(useLength,targetLength))));
                        if(product<=1e-30||dot*dot<product*(1-sin*sin))continue;
                    }
                    double reverse=useLength<=epsilon*8?epsilon*2:epsilon*(2+targetLength/useLength);
                    if(LineDistance(q,a,b)<=epsilon*epsilon&&LineDistance(a,p.position,q)<=reverse*reverse&&LineDistance(b,p.position,q)<=reverse*reverse)return true;
                }
                return false;
            }
            var points=new Point[lookup.Count];int index=0;
            foreach(var p in lookup)points[index++]=new Point{key=p.Key,position=p.Value};
            var nodes=new List<Node>();var comparer=new AxisComparer();
            int Build(int start,int end)
            {
                var lo=new Vector3(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity);var hi=-lo;
                for(int i=start;i<end;i++){lo=Vector3.Min(lo,points[i].position);hi=Vector3.Max(hi,points[i].position);}
                int id=nodes.Count;var n=new Node{min=lo,max=hi,start=start,end=end,left=-1,right=-1};nodes.Add(n);
                if(end-start>12)
                {
                    var size=hi-lo;comparer.axis=size.x>=size.y&&size.x>=size.z?0:size.y>=size.z?1:2;
                    Array.Sort(points,start,end-start,comparer);int mid=(start+end)/2;
                    n.left=Build(start,mid);n.right=Build(mid,end);nodes[id]=n;
                }
                return id;
            }
            if(points.Length==0)return new List<Edge>();Build(0,points.Length);
            var cuts=new List<(double t,Vector3Int key)>();
            void Query(int node,Vector3 a,Vector3 b,Vector3Int ka,Vector3Int kb,double epsilon)
            {
                var n=nodes[node];double near=0,far=1;
                for(int axis=0;axis<3;axis++)
                {
                    double d=(double)b[axis]-a[axis],lo=n.min[axis]-epsilon-a[axis],hi=n.max[axis]+epsilon-a[axis];
                    if(Math.Abs(d)<1e-15){if(lo>0||hi<0)return;}
                    else{double t0=lo/d,t1=hi/d;near=Math.Max(near,Math.Min(t0,t1));far=Math.Min(far,Math.Max(t0,t1));if(near>far)return;}
                }
                if(n.left>=0){Query(n.left,a,b,ka,kb,epsilon);Query(n.right,a,b,ka,kb,epsilon);return;}
                double dx=(double)b.x-a.x,dy=(double)b.y-a.y,dz=(double)b.z-a.z,len=dx*dx+dy*dy+dz*dz;
                if(len<1e-20)return;
                for(int i=n.start;i<n.end;i++)
                {
                    var p=points[i];if(p.key==ka||p.key==kb)continue;
                    double x=(double)p.position.x-a.x,y=(double)p.position.y-a.y,z=(double)p.position.z-a.z;
                    double t=(x*dx+y*dy+z*dz)/len;if(t<=0||t>=1)continue;
                    x-=dx*t;y-=dy*t;z-=dz*t;
                    if(x*x+y*y+z*z<=epsilon*epsilon&&HasCollinearUse(p,a,b,epsilon))cuts.Add((t,p.key));
                }
            }
            void CutEdge(Edge edge)
            {
                cuts.Clear();cuts.Add((0,edge.a));cuts.Add((1,edge.b));
                var a=lookup[edge.a];var b=lookup[edge.b];
                // World-space float reconstruction loses sub-millimetre bits at
                // kilometre offsets. Allow one relative float rounding unit,
                // capped at 1 mm, with the incident-edge collinearity guard.
                // Keys/endpoints themselves are never merged or moved here.
                double magnitude=Math.Max(Math.Max(Math.Abs(a.x),Math.Abs(b.x)),Math.Max(Math.Max(Math.Abs(a.y),Math.Abs(b.y)),Math.Max(Math.Abs(a.z),Math.Abs(b.z))));
                double epsilon=Math.Min(.001,Math.Max(.0002,magnitude*1.1920928955078125e-7));
                // In a translation-free chunk frame there is no kilometre-scale
                // reconstruction error. One endpoint-weld radius plus float
                // rounding is sufficient; a wider world tolerance can flatten
                // a real shallow corner into a false overlapping edge.
                if(preciseFrame)epsilon=Math.Min(.001,.0001+magnitude*1.1920928955078125e-7);
                Query(0,a,b,edge.a,edge.b,epsilon);
                cuts.Sort((a,b)=>{int c=a.t.CompareTo(b.t);return c!=0?c:Compare(a.key,b.key);});
            }
            string Failure((Vector3Int a,Vector3Int b) key,int count)
            {
                string P(Vector3Int k)
                {var p=lookup[k];return FormattableString.Invariant($"key={k.x},{k.y},{k.z} pos={p.x:R},{p.y:R},{p.z:R}");}
                var message=new System.Text.StringBuilder($"terrain boundaries v4: overlapping/non-manifold edge after subdividing collinear uses (count={count}). Previous geometry is preserved.\nAtomic: {P(key.a)} -> {P(key.b)}\n");
                int contributors=0;
                for(int s=0;s<source.Count;s++)
                {
                    var edge=source[s];if(edge.a==edge.b)continue;CutEdge(edge);
                    for(int i=1;i<cuts.Count;i++)
                    {
                        var part=new Edge(cuts[i-1].key,cuts[i].key);
                        if(part.a!=key.a||part.b!=key.b)continue;
                        if(contributors++<24)message.Append($"Source[{s}] count={edge.count}: {P(edge.a)} -> {P(edge.b)}\n");
                    }
                }
                message.Append($"Contributors={contributors}; candidates={source.Count}. Diagnostic only; no geometry repaired or discarded.");return message.ToString();
            }
            var counts=new Dictionary<(Vector3Int,Vector3Int),int>();
            foreach(var edge in source)
            {
                if(edge.a==edge.b)continue;CutEdge(edge);
                if(cuts.Count>2)splitEdges++;
                for(int i=1;i<cuts.Count;i++)
                {
                    var e=new Edge(cuts[i-1].key,cuts[i].key,edge.count);if(e.a==e.b)continue;
                    var key=(e.a,e.b);counts.TryGetValue(key,out int count);counts[key]=count+e.count;
                }
            }
            var result=new List<Edge>();
            foreach(var entry in counts)
            {
                if(entry.Value>2)throw new InvalidOperationException(Failure(entry.Key,entry.Value));
                if(entry.Value==1)result.Add(new Edge(entry.Key.Item1,entry.Key.Item2));
            }
            result.Sort((a,b)=>{int c=Compare(a.a,b.a);return c!=0?c:Compare(a.b,b.b);});
            return result;
        }
    }
}
