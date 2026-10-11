using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Immutable XZ projection of generated LOD0 triangles. No Physics, GPU readback,
    // allocations or dependence on the currently displayed LOD during root queries.
    public sealed class LTRoadFootprint
    {
        struct Triangle { public Vector2 a,b,c,min,max; }
        struct Node { public Vector2 min,max;public int start,count,left,right; }
        readonly Triangle[] triangles;
        readonly List<Node> nodes=new List<Node>();
        readonly float padding;
        string contentKey;
        public string ContentKey
        {
            get
            {
                if(contentKey!=null)return contentKey;
                var hash=new LTPaintMath.GeometryFingerprint();hash.Add(padding);hash.Add(triangles.Length);
                foreach(var t in triangles){hash.Add(t.a.x);hash.Add(t.a.y);hash.Add(t.b.x);hash.Add(t.b.y);hash.Add(t.c.x);hash.Add(t.c.y);}
                return contentKey=hash.ToString();
            }
        }
        public LTRoadFootprint(Vector3[] vertices,int[] indices,float padding)
        {
            if(float.IsNaN(padding)||float.IsInfinity(padding)||padding<0)throw new ArgumentException("Invalid road vegetation padding.");
            if(vertices==null||indices==null||indices.Length%3!=0)throw new ArgumentException("Invalid footprint mesh.");
            foreach(var v in vertices)if(!float.IsFinite(v.x)||!float.IsFinite(v.z))throw new ArgumentException("Nonfinite footprint vertex.");
            foreach(int index in indices)if(index<0||index>=vertices.Length)throw new ArgumentException("Invalid footprint index.");
            this.padding=padding;var list=new List<Triangle>();
            for(int i=0;i<indices.Length;i+=3)
            {
                Vector2 P(int id)=>new Vector2(vertices[id].x,vertices[id].z);
                var a=P(indices[i]);var b=P(indices[i+1]);var c=P(indices[i+2]);
                if(Math.Abs(Cross(a,b,c))<1e-12)continue; // Vertical sides have no ground footprint.
                list.Add(new Triangle{a=a,b=b,c=c,min=Vector2.Min(a,Vector2.Min(b,c)),max=Vector2.Max(a,Vector2.Max(b,c))});
            }
            triangles=list.ToArray();if(triangles.Length>0)Build(0,triangles.Length);
        }
        int Build(int start,int count)
        {
            var min=triangles[start].min;var max=triangles[start].max;
            for(int i=start+1;i<start+count;i++){min=Vector2.Min(min,triangles[i].min);max=Vector2.Max(max,triangles[i].max);}
            int id=nodes.Count;var n=new Node{min=min,max=max,start=start,count=count,left=-1,right=-1};nodes.Add(n);
            if(count>12)
            {
                bool x=max.x-min.x>=max.y-min.y;
                Array.Sort(triangles,start,count,Comparer<Triangle>.Create((a,b)=>(x?a.min.x+a.max.x:a.min.y+a.max.y).CompareTo(x?b.min.x+b.max.x:b.min.y+b.max.y)));
                int half=count/2;n.left=Build(start,half);n.right=Build(start+half,count-half);nodes[id]=n;
            }
            return id;
        }
        public bool Intersects(Rect rect)=>nodes.Count>0&&nodes[0].max.x+padding>=rect.xMin&&nodes[0].min.x-padding<=rect.xMax&&
            nodes[0].max.y+padding>=rect.yMin&&nodes[0].min.y-padding<=rect.yMax;
        public bool Contains(float x,float z)=>nodes.Count>0&&Visit(0,new Vector2(x,z));
        public bool Empty=>nodes.Count==0;
        public Rect Bounds=>Empty?default:Rect.MinMaxRect(nodes[0].min.x,nodes[0].min.y,nodes[0].max.x,nodes[0].max.y);
        // Exact distance to the UNION of projected triangles, including concavities
        // and through-holes. No topology stitching or allocations in point queries.
        // Padding belongs to Contains only; callers choose their own falloff here.
        public float SurfaceDistance(float x,float z,float maximum)
        {
            if(!float.IsFinite(maximum)||maximum<0)throw new ArgumentException("Invalid footprint distance limit.");
            if(Empty)return float.PositiveInfinity;
            var p=new Vector2(x,z);double best=(double)maximum*maximum;bool found=false;
            Search(0,p,ref best,ref found);return found?(float)Math.Sqrt(best):float.PositiveInfinity;
        }
        static double BoxDistance(Node n,Vector2 p)
        {
            double x=Math.Max(n.min.x-p.x,Math.Max(0,p.x-n.max.x));
            double y=Math.Max(n.min.y-p.y,Math.Max(0,p.y-n.max.y));return x*x+y*y;
        }
        void Search(int id,Vector2 p,ref double best,ref bool found)
        {
            var n=nodes[id];if(BoxDistance(n,p)>best)return;
            if(n.left>=0)
            {
                int a=n.left,b=n.right;if(BoxDistance(nodes[a],p)>BoxDistance(nodes[b],p)){a=n.right;b=n.left;}
                Search(a,p,ref best,ref found);if(best>0)Search(b,p,ref best,ref found);return;
            }
            for(int i=n.start;i<n.start+n.count;i++)
            {
                var t=triangles[i];double a=Cross(t.a,t.b,p),b=Cross(t.b,t.c,p),c=Cross(t.c,t.a,p);
                if(a>=0&&b>=0&&c>=0||a<=0&&b<=0&&c<=0){found=true;best=0;return;}
                double d=Math.Min(Distance(p,t.a,t.b),Math.Min(Distance(p,t.b,t.c),Distance(p,t.c,t.a)));
                if(d<=best){best=d;found=true;}
            }
        }
        public bool Influences(Rect rect,float distance)
        {
            if(Empty)return false;
            var bounds=Bounds;
            if(rect.xMax<bounds.xMin-distance||rect.xMin>bounds.xMax+distance||rect.yMax<bounds.yMin-distance||rect.yMin>bounds.yMax+distance)return false;
            // Conservative for a quadtree cell: no missed triangle or narrow branch.
            float reach=distance+new Vector2(rect.width,rect.height).magnitude*.5f;
            return SurfaceDistance(rect.center.x,rect.center.y,reach)<=reach;
        }
        bool Visit(int id,Vector2 p)
        {
            var n=nodes[id];
            if(p.x<n.min.x-padding||p.x>n.max.x+padding||p.y<n.min.y-padding||p.y>n.max.y+padding)return false;
            if(n.left>=0)return Visit(n.left,p)||Visit(n.right,p);
            for(int i=n.start;i<n.start+n.count;i++)
            {
                var t=triangles[i];double a=Cross(t.a,t.b,p),b=Cross(t.b,t.c,p),c=Cross(t.c,t.a,p);
                if(a>=0&&b>=0&&c>=0||a<=0&&b<=0&&c<=0)return true;
                if(padding>0&&(Distance(p,t.a,t.b)<=padding*padding||Distance(p,t.b,t.c)<=padding*padding||Distance(p,t.c,t.a)<=padding*padding))return true;
            }
            return false;
        }
        static double Cross(Vector2 a,Vector2 b,Vector2 c)=>((double)b.x-a.x)*((double)c.y-a.y)-((double)b.y-a.y)*((double)c.x-a.x);
        static double Distance(Vector2 p,Vector2 a,Vector2 b)
        {
            double x=(double)b.x-a.x,y=(double)b.y-a.y,l=x*x+y*y;
            double t=l>0?Math.Max(0,Math.Min(1,(((double)p.x-a.x)*x+((double)p.y-a.y)*y)/l)):0;
            x=p.x-a.x-t*x;y=p.y-a.y-t*y;return x*x+y*y;
        }
    }
}
