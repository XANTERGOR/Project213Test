using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Numeric copy of the emitter's cut stencil. Used only during LOD preparation;
    // no mesh/renderer is modified. All 16 transition masks must pass, not just
    // the currently visible one, because neighbours can change independently.
    internal sealed class LTHoleContourSampler
    {
        internal readonly struct Point
        {
            internal readonly Vector2 p;
            internal readonly Vector2Int key;
            internal Point(Vector2 p,Vector2Int key){this.p=p;this.key=key;}
        }
        internal readonly struct Segment
        {
            internal readonly Point a,b;
            internal Segment(Point a,Point b){this.a=a;this.b=b;}
        }
        const int N=LTSpatialLODMath.N;
        readonly Rect rect;
        readonly Func<int,int,bool> gridCut;
        readonly Func<Vector2,bool> cut;
        readonly Dictionary<(Vector2Int,Vector2Int),Point> hits=new Dictionary<(Vector2Int,Vector2Int),Point>();
        internal LTHoleContourSampler(Rect rect,Func<int,int,bool> gridCut,Func<Vector2,bool> cut)
        {this.rect=rect;this.gridCut=gridCut;this.cut=cut;}
        Vector2 Position(Vector2Int key)=>new Vector2(key.x*.5f/N*rect.width,key.y*.5f/N*rect.height);
        Point Hit(Vector2Int a,Vector2Int b,bool aCut)
        {
            if(a.x>b.x||a.x==b.x&&a.y>b.y){var swap=a;a=b;b=swap;aCut=!aCut;}
            if(hits.TryGetValue((a,b),out var result))return result;
            var inside=Position(aCut?a:b);var outside=Position(aCut?b:a);
            for(int i=0;i<18;i++){var middle=(inside+outside)*.5f;if(cut(middle))inside=middle;else outside=middle;}
            // Same deduplication lattice as LTStampMesh.Builder.Vertex.
            var key=new Vector2Int(Mathf.RoundToInt(outside.x/rect.width*N*2),Mathf.RoundToInt(outside.y/rect.height*N*2));
            result=new Point(outside,key);hits.Add((a,b),result);return result;
        }
        internal List<Segment> Segments(Vector3Int cell,int mask,bool regular)
        {
            int x=cell.x*2,z=cell.y*2,w=cell.z;
            var points=new[]{new Vector2Int(x,z),new Vector2Int(x,z+w),new Vector2Int(x,z+2*w),new Vector2Int(x+w,z+2*w),
                new Vector2Int(x+2*w,z+2*w),new Vector2Int(x+2*w,z+w),new Vector2Int(x+2*w,z),new Vector2Int(x+w,z),new Vector2Int(x+w,z+w)};
            var flags=new bool[9];for(int i=0;i<9;i++)flags[i]=gridCut(points[i].x,points[i].y);
            var output=new List<Segment>(8);
            void Triangle(int a,int b,int c)
            {
                if(flags[a]==flags[b]&&flags[b]==flags[c])return;
                Point p,q;
                if(flags[a]==flags[b]){p=Hit(points[a],points[c],flags[a]);q=Hit(points[b],points[c],flags[b]);}
                else if(flags[b]==flags[c]){p=Hit(points[a],points[b],flags[a]);q=Hit(points[a],points[c],flags[a]);}
                else{p=Hit(points[a],points[b],flags[a]);q=Hit(points[b],points[c],flags[b]);}
                if(p.key!=q.key)output.Add(new Segment(p,q));
            }
            if(mask==0)
            {
                if(LTPaintMath.UseRegularCellDiagonal(cell.x/cell.z,cell.y/cell.z,regular)){Triangle(0,2,6);Triangle(6,2,4);}
                else{Triangle(0,2,4);Triangle(4,6,0);}
                return output;
            }
            var edge=new List<int>{0};if((mask&1)!=0)edge.Add(1);edge.Add(2);if((mask&8)!=0)edge.Add(3);
            edge.Add(4);if((mask&2)!=0)edge.Add(5);edge.Add(6);if((mask&4)!=0)edge.Add(7);
            if(regular)
            {
                var polygon=new Vector2[edge.Count];for(int i=0;i<edge.Count;i++)polygon[i]=Position(points[edge[i]]);
                var indices=LTPaintMath.TriangulateTransition(polygon);
                if(indices.Length>0){for(int i=0;i<indices.Length;i+=3)Triangle(edge[indices[i]],edge[indices[i+1]],edge[indices[i+2]]);return output;}
            }
            for(int i=0;i<edge.Count;i++)Triangle(8,edge[i],edge[(i+1)%edge.Count]);return output;
        }
        static int Sides(Vector2Int p,Vector3Int cell)
        {
            int sides=0;if(p.x==cell.x*2)sides|=1;if(p.x==(cell.x+cell.z)*2)sides|=2;
            if(p.y==cell.y*2)sides|=4;if(p.y==(cell.y+cell.z)*2)sides|=8;return sides;
        }
        static bool Arc(List<Segment> segments,Vector3Int cell,out Point first,out Point last)
        {
            first=last=default;if(segments.Count==0)return false;
            var ids=new Dictionary<Vector2Int,int>();var points=new List<Point>();var degrees=new List<int>();var roots=new List<int>();
            int Id(Point p){if(ids.TryGetValue(p.key,out int id))return id;id=ids.Count;ids.Add(p.key,id);points.Add(p);degrees.Add(0);roots.Add(id);return id;}
            int Root(int p){while(roots[p]!=p){roots[p]=roots[roots[p]];p=roots[p];}return p;}
            foreach(var segment in segments){int a=Id(segment.a),b=Id(segment.b);degrees[a]++;degrees[b]++;roots[Root(a)]=Root(b);}
            int ends=0,root=Root(0);
            for(int i=0;i<points.Count;i++)
            {
                if(Root(i)!=root||degrees[i]<1||degrees[i]>2)return false;
                if(degrees[i]==1){if(Sides(points[i].key,cell)==0||ends>=2)return false;if(ends++==0)first=points[i];else last=points[i];}
            }
            // A closed loop, multiple arcs or an interior endpoint is not allowed
            // to collapse. This protects small holes, islands and thin bridges.
            return ends==2;
        }
        static double DistanceSquared(Vector2 p,Segment segment)
        {
            double x=p.x-(double)segment.a.p.x,y=p.y-(double)segment.a.p.y;
            double dx=segment.b.p.x-(double)segment.a.p.x,dy=segment.b.p.y-(double)segment.a.p.y;
            double length=dx*dx+dy*dy,t=length>0?Math.Max(0,Math.Min(1,(x*dx+y*dy)/length)):0;
            x-=t*dx;y-=t*dy;return x*x+y*y;
        }
        static bool Within(List<Segment> source,List<Segment> target,double error,ref int work)
        {
            foreach(var s in source)
            {
                double length=(s.b.p-s.a.p).magnitude;
                double required=Math.Ceiling(length/(error*.5));if(required>2048)return false;
                int pieces=Math.Max(1,(int)required);double radius=length/(2*pieces),allowed=error-radius;
                for(int i=0;i<pieces;i++)
                {
                    var p=Vector2.Lerp(s.a.p,s.b.p,(i+.5f)/pieces);double distance=double.PositiveInfinity;
                    foreach(var t in target)
                    {
                        if(++work>100000)return false; // deterministic conservative fallback
                        distance=Math.Min(distance,DistanceSquared(p,t));
                    }
                    // Distance-to-a-set is 1-Lipschitz: midpoint distance + half
                    // interval length bounds EVERY point, not just the samples.
                    if(distance>allowed*allowed)return false;
                }
            }
            return true;
        }
        internal bool CanMerge(Vector3Int cell,List<Segment> reference,float error,bool regular)
        {
            if(error<=0||reference.Count>256||!Arc(reference,cell,out var a,out var b))return false;
            int work=0;double tolerance=error*.999; // numerical margin
            bool Same(Point x,Point y)=>(Sides(x.key,cell)&Sides(y.key,cell))!=0&&(x.p-y.p).sqrMagnitude<=tolerance*tolerance;
            for(int mask=0;mask<16;mask++)
            {
                var candidate=Segments(cell,mask,regular);
                if(!Arc(candidate,cell,out var c,out var d)||!(Same(a,c)&&Same(b,d)||Same(a,d)&&Same(b,c)))return false;
                if(!Within(reference,candidate,tolerance,ref work)||!Within(candidate,reference,tolerance,ref work))return false;
            }
            return true;
        }
    }
}
