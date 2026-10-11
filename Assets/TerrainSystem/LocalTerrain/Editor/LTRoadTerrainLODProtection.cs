using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Immutable CPU geometry of ALL reachable road LODs in terrain-world local space.
    // No current MeshFilter LOD, renderer, Physics or shader evaluation is retained.
    internal sealed class LTRoadTerrainSurface
    {
        internal struct Part {public Vector3[] vertices;public int[] indices;public int level;}
        struct Triangle {public Vector3 a,b,c;}
        struct Edge {public Vector3 a,b;public int count;}
        struct Point
        {
            public double x,y,z;
            public Point(Vector3 p){x=p.x;y=p.y;z=p.z;}
            public static Point Lerp(Point a,Point b,double t)=>new Point{x=a.x+(b.x-a.x)*t,y=a.y+(b.y-a.y)*t,z=a.z+(b.z-a.z)*t};
        }
        sealed class Tree
        {
            struct Item {public Rect bounds;public int id;}
            struct Node {public Rect bounds;public int start,count,left,right;}
            readonly Item[] items;readonly List<Node> nodes=new List<Node>();
            internal Tree(Rect[] bounds)
            {items=bounds.Select((r,i)=>new Item{bounds=r,id=i}).ToArray();if(items.Length>0)Build(0,items.Length);}
            int Build(int start,int count)
            {
                var r=items[start].bounds;for(int i=start+1;i<start+count;i++)r=Union(r,items[i].bounds);
                int id=nodes.Count;var n=new Node{bounds=r,start=start,count=count,left=-1,right=-1};nodes.Add(n);
                if(count>12)
                {
                    bool x=r.width>=r.height;
                    Array.Sort(items,start,count,Comparer<Item>.Create((a,b)=>(x?a.bounds.center.x:a.bounds.center.y).CompareTo(x?b.bounds.center.x:b.bounds.center.y)));
                    int half=count/2;n.left=Build(start,half);n.right=Build(start+half,count-half);nodes[id]=n;
                }
                return id;
            }
            internal bool Any(Rect rect,Func<int,bool> test)=>nodes.Count>0&&Visit(0,rect,test);
            bool Visit(int id,Rect rect,Func<int,bool> test)
            {
                var n=nodes[id];if(!Overlap(n.bounds,rect))return false;
                if(n.left>=0)return Visit(n.left,rect,test)||Visit(n.right,rect,test);
                for(int i=n.start;i<n.start+n.count;i++)if(Overlap(items[i].bounds,rect)&&test(items[i].id))return true;
                return false;
            }
        }
        readonly Triangle[] triangles;readonly Edge[] edges;readonly Tree topTree,edgeTree;
        internal Rect Bounds {get;}
        internal int TriangleCount=>triangles.Length;
        internal float EdgeError {get;}
        internal static bool Overlap(Rect a,Rect b)=>a.xMin<=b.xMax&&a.xMax>=b.xMin&&a.yMin<=b.yMax&&a.yMax>=b.yMin;
        internal static Rect Union(Rect a,Rect b)=>Rect.MinMaxRect(Math.Min(a.xMin,b.xMin),Math.Min(a.yMin,b.yMin),Math.Max(a.xMax,b.xMax),Math.Max(a.yMax,b.yMax));
        static Rect Box(Vector3 a,Vector3 b)=>Rect.MinMaxRect(Math.Min(a.x,b.x),Math.Min(a.z,b.z),Math.Max(a.x,b.x),Math.Max(a.z,b.z));
        static bool Less(Vector3 a,Vector3 b)=>a.x<b.x||a.x==b.x&&(a.y<b.y||a.y==b.y&&a.z<b.z);
        internal LTRoadTerrainSurface(IEnumerable<Part> parts,float edgeError=.03f,bool trackEdges=true)
        {
            if(!LTRoadMesh.Finite(edgeError)||edgeError<0)throw new ArgumentException("Invalid road edge height error.");
            EdgeError=edgeError;
            var top=new List<Triangle>();var rim=new Dictionary<(int,Vector3,Vector3),Edge>();
            foreach(var part in parts)
            {
                if(part.vertices==null||part.indices==null||part.indices.Length%3!=0||part.vertices.Any(p=>!LTRoadMesh.Finite(p))||part.indices.Any(i=>i<0||i>=part.vertices.Length))throw new ArgumentException("Invalid road LOD surface.");
                for(int i=0;i<part.indices.Length;i+=3)
                {
                    var a=part.vertices[part.indices[i]];var b=part.vertices[part.indices[i+1]];var c=part.vertices[part.indices[i+2]];
                    // Ignore undersides and vertical end caps. Keeping every upward face is
                    // conservative for overlapping sheets, damage, cambers and thin side bevels.
                    double ny=((double)b.z-a.z)*(c.x-a.x)-((double)b.x-a.x)*(c.z-a.z);
                    if(ny<=1e-12)continue;
                    top.Add(new Triangle{a=a,b=b,c=c});Add(a,b,part.level);Add(b,c,part.level);Add(c,a,part.level);
                    if(top.Count>2000000)throw new InvalidOperationException("Road terrain LOD surface exceeds 2M triangles.");
                }
            }
            void Add(Vector3 a,Vector3 b,int level)
            {
                if(!trackEdges)return;
                if(Less(b,a)){var swap=a;a=b;b=swap;}var key=(level,a,b);
                if(rim.TryGetValue(key,out var edge)){edge.count++;rim[key]=edge;}else rim.Add(key,new Edge{a=a,b=b,count=1});
            }
            if(top.Count==0)throw new InvalidOperationException("Road LOD has no upward surface.");
            triangles=top.ToArray();edges=rim.Values.Where(e=>e.count!=2).ToArray();
            var boxes=triangles.Select(t=>Union(Box(t.a,t.b),Box(t.a,t.c))).ToArray();Bounds=boxes.Aggregate(Union);
            topTree=new Tree(boxes);edgeTree=new Tree(edges.Select(e=>Box(e.a,e.b)).ToArray());
        }
        internal bool CrossesEdge(Rect area)=>edgeTree.Any(area,i=>SegmentRect(edges[i].a,edges[i].b,area));
        static bool SegmentRect(Vector3 a,Vector3 b,Rect r)
        {
            double lo=0,hi=1;
            bool Clip(double p,double d,double min,double max)
            {
                if(d==0)return p>=min&&p<=max;
                double x=(min-p)/d,y=(max-p)/d;if(x>y){double t=x;x=y;y=t;}
                lo=Math.Max(lo,x);hi=Math.Min(hi,y);return lo<=hi;
            }
            return Clip(a.x,(double)b.x-a.x,r.xMin,r.xMax)&&Clip(a.z,(double)b.z-a.z,r.yMin,r.yMax);
        }
        // Any emitted cell triangle is an affine interpolation of a subset of the
        // 3x3 stencil. Plane + largest positive stencil residual is an upper bound
        // for EVERY diagonal, centre fan and regular transition triangulation.
        // Clip road triangles to the cell and compare at all polygon vertices:
        // the difference is affine there, so no interior intersection can be missed.
        internal bool Blocks(Rect area,double h00,double sx,double sz,double residual)
        {
            if(!Overlap(Bounds,area))return false;
            var a=new Point[12];var b=new Point[12];
            return topTree.Any(area,id=>
            {
                var t=triangles[id];a[0]=new Point(t.a);a[1]=new Point(t.b);a[2]=new Point(t.c);int count=3;
                for(int side=0;side<4&&count>0;side++)
                {
                    double Distance(Point p)=>side==0?p.x-area.xMin:side==1?area.xMax-p.x:side==2?p.z-area.yMin:area.yMax-p.z;
                    int written=0;
                    for(int i=0;i<count;i++)
                    {
                        var p=a[i];var q=a[(i+1)%count];double dp=Distance(p),dq=Distance(q);
                        if(dp>=0)b[written++]=p;
                        if((dp>=0)!=(dq>=0))b[written++]=Point.Lerp(p,q,dp/(dp-dq));
                    }
                    var swap=a;a=b;b=swap;count=written;
                }
                for(int i=0;i<count;i++)
                {
                    var p=a[i];double upper=h00+sx*(p.x-area.xMin)+sz*(p.z-area.yMin)+residual;
                    // 0.1 mm clearance also covers floating-point interpolation noise.
                    if(upper>p.y-.0001)return true;
                }
                return false;
            });
        }
        // Exact triangle/triangle XZ overlap, not an AABB or stencil-only test.
        // On each clipped polygon the height difference is affine, so its extrema
        // are at polygon vertices. Used both for asphalt clearance and LOD0 error.
        internal bool Exceeds(Vector3 ca,Vector3 cb,Vector3 cc,double maxAbove,double maxBelow=double.PositiveInfinity)
        {
            var bounds=Union(Box(ca,cb),Box(ca,cc));if(!Overlap(Bounds,bounds))return false;
            double bx=(double)cb.x-ca.x,bz=(double)cb.z-ca.z,cx=(double)cc.x-ca.x,cz=(double)cc.z-ca.z;
            double det=bx*cz-bz*cx;if(Math.Abs(det)<1e-20)return true;
            double sx=(((double)cb.y-ca.y)*cz-((double)cc.y-ca.y)*bz)/det;
            double sz=(bx*((double)cc.y-ca.y)-cx*((double)cb.y-ca.y))/det,sign=Math.Sign(det);
            var a=new Point[12];var b=new Point[12];
            return topTree.Any(bounds,id=>
            {
                var t=triangles[id];a[0]=new Point(t.a);a[1]=new Point(t.b);a[2]=new Point(t.c);int count=3;
                for(int side=0;side<3&&count>0;side++)
                {
                    var p=side==0?ca:side==1?cb:cc;var q=side==0?cb:side==1?cc:ca;
                    double dx=(double)q.x-p.x,dz=(double)q.z-p.z;
                    double Distance(Point v)=>sign*(dx*(v.z-p.z)-dz*(v.x-p.x));
                    int written=0;
                    for(int i=0;i<count;i++)
                    {
                        var u=a[i];var v=a[(i+1)%count];double du=Distance(u),dv=Distance(v);
                        if(du>=0)b[written++]=u;
                        if((du>=0)!=(dv>=0))b[written++]=Point.Lerp(u,v,du/(du-dv));
                    }
                    var swap=a;a=b;b=swap;count=written;
                }
                for(int i=0;i<count;i++)
                {
                    var p=a[i];double delta=ca.y+sx*(p.x-ca.x)+sz*(p.z-ca.z)-p.y;
                    if(delta>maxAbove+1e-7||-delta>maxBelow+1e-7)return true;
                }
                return false;
            });
        }
    }

    // Per-chunk lookup; contains only immutable road geometry and sampled CPU heights.
    internal sealed class LTRoadTerrainLODProtection
    {
        const int N=LTSpatialLODMath.N;
        readonly Dictionary<Vector3Int,bool> answers=new Dictionary<Vector3Int,bool>();
        readonly Dictionary<Vector2Int,float> heights=new Dictionary<Vector2Int,float>();
        LTRoadTerrainSurface[] surfaces;LTRoadTerrainSurface edgeReference;
        Func<float,float,float> height;Func<Rect,bool> regularTopology;Func<Vector3Int,int> baseMask;
        HashSet<Vector3Int> leaves;Rect rect;bool prepared;float edgeError;
        Rect[] remeshedRegions;
        internal int ReferenceEdgeCells {get;private set;}
        internal int SafetyQueries {get;private set;}
        internal int ExactQueries {get;private set;}
        internal IEnumerable<LTBuildStep> Prepare(IReadOnlyList<Vector3Int> fine,Rect region,IReadOnlyList<LTRoadTerrainSurface> roads,
            Func<float,float,float> evaluate,Func<Vector3Int,int> originalMask=null,Func<Rect,bool> regular=null,IReadOnlyList<Rect> remeshed=null)
        {
            if(prepared||surfaces!=null)throw new InvalidOperationException("Road terrain LOD protection already prepared.");
            if(fine==null||roads==null||evaluate==null||region.width<=0||region.height<=0)throw new ArgumentException("Invalid road terrain LOD input.");
            rect=region;height=evaluate;surfaces=roads.Where(r=>r!=null&&LTRoadTerrainSurface.Overlap(r.Bounds,rect)).ToArray();
            baseMask=originalMask;regularTopology=regular;edgeError=surfaces.Length>0?surfaces.Min(s=>s.EdgeError):0;
            remeshedRegions=remeshed?.ToArray()??Array.Empty<Rect>();
            leaves=new HashSet<Vector3Int>(fine);var edges=new List<Vector3Int>();var referenceLeaves=new HashSet<Vector3Int>();
            int visits=0;var clock=Stopwatch.StartNew();
            bool Yield(){if(++visits<256&&clock.Elapsed.TotalMilliseconds<2)return false;visits=0;clock.Restart();return true;}
            foreach(var cell in fine)
            {var area=Area(cell);if(surfaces.Any(r=>r.CrossesEdge(area)))edges.Add(cell);if(Yield())yield return LTBuildStep.Working;}
            // Keep the ORIGINAL surface in the edge/hole band as an error reference,
            // not a permanently fixed band. A flat band can now merge all the way.
            var stack=new Stack<Vector3Int>();
            foreach(var edge in edges)
            {
                int minX=edge.x-edge.z,minZ=edge.y-edge.z,maxX=edge.x+2*edge.z,maxZ=edge.y+2*edge.z;
                stack.Push(new Vector3Int(0,0,N));
                while(stack.Count>0)
                {
                    var p=stack.Pop();
                    if(p.x>=maxX||p.y>=maxZ||p.x+p.z<=minX||p.y+p.z<=minZ)continue;
                    if(leaves.Contains(p))
                    {
                        referenceLeaves.Add(p);
                    }
                    else
                    {
                        if(p.z<=1)throw new InvalidOperationException("Incomplete terrain LOD0 plan.");int half=p.z/2;
                        stack.Push(new Vector3Int(p.x,p.y,half));stack.Push(new Vector3Int(p.x+half,p.y,half));
                        stack.Push(new Vector3Int(p.x,p.y+half,half));stack.Push(new Vector3Int(p.x+half,p.y+half,half));
                    }
                    if(Yield())yield return LTBuildStep.Working;
                }
            }
            var vertices=new List<Vector3>();var indices=new List<int>();
            foreach(var cell in referenceLeaves)
            {
                var points=Points(cell);int offset=vertices.Count;vertices.AddRange(points);
                foreach(int i in Topology(cell,baseMask?.Invoke(cell)??0))indices.Add(offset+i);
                if(Yield())yield return LTBuildStep.Working;
            }
            if(indices.Count>0)edgeReference=new LTRoadTerrainSurface(new[]{new LTRoadTerrainSurface.Part{vertices=vertices.ToArray(),indices=indices.ToArray()}},trackEdges:false);
            ReferenceEdgeCells=referenceLeaves.Count;prepared=true;
        }
        // Match LTStampMesh.Builder exactly, including the final float rounding.
        float X(int twice)=>(float)(rect.xMin+twice*.5/N*rect.width);
        float Z(int twice)=>(float)(rect.yMin+twice*.5/N*rect.height);
        float LocalX(int twice)=>twice*.5f/N*rect.width;
        float LocalZ(int twice)=>twice*.5f/N*rect.height;
        Rect Area(Vector3Int cell)=>Rect.MinMaxRect(rect.xMin+LocalX(cell.x*2),rect.yMin+LocalZ(cell.y*2),rect.xMin+LocalX((cell.x+cell.z)*2),rect.yMin+LocalZ((cell.y+cell.z)*2));
        Vector3[] Points(Vector3Int cell)
        {
            var points=new Vector3[9];for(int z=0;z<3;z++)for(int x=0;x<3;x++)
            {int ix=cell.x*2+x*cell.z,iz=cell.y*2+z*cell.z;points[z*3+x]=new Vector3(rect.xMin+LocalX(ix),H(ix,iz),rect.yMin+LocalZ(iz));}
            return points;
        }
        // Same ring, diagonal and transition routine as LTStampMesh.Builder.Emit.
        // Coordinates passed to TriangulateTransition are chunk-local, like emission.
        internal int[] Topology(Vector3Int cell,int mask)
        {
            var ring=new List<int>{0};if((mask&1)!=0)ring.Add(3);ring.Add(6);if((mask&8)!=0)ring.Add(7);
            ring.Add(8);if((mask&2)!=0)ring.Add(5);ring.Add(2);if((mask&4)!=0)ring.Add(1);
            var query=new Rect(X(cell.x*2),Z(cell.y*2),cell.z/(float)N*rect.width,cell.z/(float)N*rect.height);
            bool regular=regularTopology?.Invoke(query)??false;
            if(mask==0)return LTPaintMath.UseRegularCellDiagonal(cell.x/cell.z,cell.y/cell.z,regular)?new[]{0,6,2,2,6,8}:new[]{0,6,8,8,2,0};
            if(regular)
            {
                var polygon=ring.Select(i=>new Vector2(LocalX(cell.x*2+i%3*cell.z),LocalZ(cell.y*2+i/3*cell.z))).ToArray();
                var result=LTPaintMath.TriangulateTransition(polygon);if(result.Length>0)return result.Select(i=>ring[i]).ToArray();
            }
            var fan=new List<int>();for(int i=0;i<ring.Count;i++)fan.AddRange(new[]{4,ring[i],ring[(i+1)%ring.Count]});return fan.ToArray();
        }
        internal int[] AllTriangles(Vector3Int cell)
        {
            int allowed=leaves.Contains(cell)&&baseMask!=null?baseMask(cell):15;
            var result=new List<int>();var seen=new HashSet<int>();
            for(int mask=0;mask<16;mask++)if((mask&~allowed)==0)
            {
                var indices=Topology(cell,mask);
                for(int i=0;i<indices.Length;i+=3)
                {
                    int a=indices[i],b=indices[i+1],c=indices[i+2],lo=Math.Min(a,Math.Min(b,c)),hi=Math.Max(a,Math.Max(b,c));
                    if(seen.Add(lo*81+(a+b+c-lo-hi)*9+hi))result.AddRange(new[]{a,b,c});
                }
            }
            return result.ToArray();
        }
        float H(int x,int z)
        {
            var key=new Vector2Int(x,z);
            if(!heights.TryGetValue(key,out float value))
            {
                // Same coordinate expression as LTStampMesh's vertex emission.
                value=height(X(x),Z(z));
                if(!LTRoadMesh.Finite(value))throw new InvalidOperationException("Non-finite road foundation height.");heights.Add(key,value);
            }
            return value;
        }
        internal bool Protects(Rect area)
        {
            if(!prepared)throw new InvalidOperationException("Road terrain protection not prepared.");
            int size=Mathf.RoundToInt(area.width/rect.width*N);if(size<1)return false;
            var cell=new Vector3Int(Mathf.RoundToInt((area.xMin-rect.xMin)/rect.width*N/size)*size,Mathf.RoundToInt((area.yMin-rect.yMin)/rect.height*N/size)*size,size);
            if(answers.TryGetValue(cell,out bool blocked))return blocked;
            area=Area(cell);
            if(area.width<=0||area.height<=0){answers.Add(cell,true);return true;}
            bool atEdge=edgeReference!=null&&LTRoadTerrainSurface.Overlap(edgeReference.Bounds,area);
            var nearby=surfaces.Where(r=>LTRoadTerrainSurface.Overlap(r.Bounds,area)).ToArray();
            if(nearby.Length==0&&!atEdge){answers.Add(cell,false);return false;}
            // Hole/rock cutters can insert new height samples inside emitted edges;
            // those are not members of the 3x3 stencil. Keep the local old protection
            // there until the post-cut geometry can be included in this exact check.
            if(remeshedRegions.Any(r=>LTRoadTerrainSurface.Overlap(r,area))){answers.Add(cell,true);return true;}
            SafetyQueries++;var points=Points(cell);double h00=points[0].y,sx=(points[2].y-h00)/area.width,sz=(points[6].y-h00)/area.height,residual=0;
            for(int z=0;z<3;z++)for(int x=0;x<3;x++)
            {
                var p=points[z*3+x];residual=Math.Max(residual,p.y-(h00+sx*((double)p.x-area.xMin)+sz*((double)p.z-area.yMin)));
            }
            // The cheap upper bound is only an ACCEPT fast path; it never rejects
            // a merge now. Reject only against the actual reachable triangles.
            var exact=nearby.Where(r=>r.Blocks(area,h00,sx,sz,residual)).ToArray();
            blocked=false;
            if(exact.Length>0||atEdge)
            {
                ExactQueries++;var indices=AllTriangles(cell);
                for(int i=0;i<indices.Length&&!blocked;i+=3)
                {
                    var a=points[indices[i]];var b=points[indices[i+1]];var c=points[indices[i+2]];
                    blocked=exact.Any(r=>r.Exceeds(a,b,c,-.0001))||(atEdge&&edgeReference.Exceeds(a,b,c,edgeError,edgeError));
                }
            }
            answers.Add(cell,blocked);return blocked;
        }
    }
}
