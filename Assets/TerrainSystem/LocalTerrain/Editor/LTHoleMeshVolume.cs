using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Immutable numeric snapshots, shared by generation, deferred LODs and preview.
    // Even/odd containment supports concavity and gaps between disconnected shells;
    // it must not be replaced with a bottom/top envelope or an XZ projection.
    internal sealed class LTHoleMeshData
    {
        internal const int MaxTriangles = 50000, MaxVertices = 150000;
        internal readonly Vector3[] Vertices;
        internal readonly int[] Indices;
        internal readonly string ContentHash;
        internal LTHoleMeshData(Vector3[] vertices, int[] indices)
        {
            if(vertices==null||indices==null||vertices.Length<4||vertices.Length>MaxVertices||indices.Length<12||indices.Length%3!=0||indices.Length/3>MaxTriangles)
                throw new ArgumentException("Hole Mesh: нужен замкнутый треугольный меш (до 50 000 треугольников / 150 000 вершин).");
            Vertices=(Vector3[])vertices.Clone();Indices=(int[])indices.Clone();
            ulong hash=14695981039346656037UL;
            void Mix(int value){unchecked{hash^=(uint)value;hash*=1099511628211UL;}}
            foreach(var v in vertices){Mix(BitConverter.SingleToInt32Bits(v.x));Mix(BitConverter.SingleToInt32Bits(v.y));Mix(BitConverter.SingleToInt32Bits(v.z));}
            foreach(int index in indices)Mix(index);ContentHash=hash.ToString("X16");
            // Exact position welding permits ordinary UV/normal splits without
            // silently closing real cracks or changing the cutter geometry.
            var byPosition=new Dictionary<Vector3,int>();var weld=new int[vertices.Length];
            for(int i=0;i<vertices.Length;i++)
            {
                var p=vertices[i];if(!Finite(p.x)||!Finite(p.y)||!Finite(p.z))throw new ArgumentException("Hole Mesh: неконечные координаты.");
                if(!byPosition.TryGetValue(p,out int id)){id=byPosition.Count;byPosition.Add(p,id);}weld[i]=id;
            }
            var edges=new Dictionary<long,(int count,int balance)>();
            var connected=new int[byPosition.Count];for(int i=0;i<connected.Length;i++)connected[i]=i;
            int Root(int a){while(connected[a]!=a){connected[a]=connected[connected[a]];a=connected[a];}return a;}
            void Edge(int a,int b)
            {
                long key=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
                edges.TryGetValue(key,out var state);edges[key]=(state.count+1,state.balance+(a<b?1:-1));
                connected[Root(a)]=Root(b);
            }
            for(int i=0;i<indices.Length;i+=3)
            {
                int a=indices[i],b=indices[i+1],c=indices[i+2];
                if((uint)a>=vertices.Length||(uint)b>=vertices.Length||(uint)c>=vertices.Length)throw new ArgumentException("Hole Mesh: индекс вне массива вершин.");
                var ab=vertices[b]-vertices[a];var ac=vertices[c]-vertices[a];
                double nx=(double)ab.y*ac.z-(double)ab.z*ac.y,ny=(double)ab.z*ac.x-(double)ab.x*ac.z,nz=(double)ab.x*ac.y-(double)ab.y*ac.x;
                if(nx*nx+ny*ny+nz*nz==0)throw new ArgumentException("Hole Mesh: вырожденный треугольник.");
                Edge(weld[a],weld[b]);Edge(weld[b],weld[c]);Edge(weld[c],weld[a]);
            }
            foreach(var edge in edges.Values)if(edge.count!=2||edge.balance!=0)
                throw new ArgumentException("Hole Mesh: меш открыт, неманифолдный или треугольники имеют несогласованный winding. Нужна замкнутая оболочка.");
            // Reject flat doubled sheets, independently for disconnected shells.
            var volumes=new Dictionary<int,double>();var origins=new Dictionary<int,Vector3>();
            for(int i=0;i<indices.Length;i+=3)
            {
                int root=Root(weld[indices[i]]);var a=vertices[indices[i]];
                if(!origins.TryGetValue(root,out var origin))origins[root]=origin=a;
                a-=origin;var b=vertices[indices[i+1]]-origin;var c=vertices[indices[i+2]]-origin;
                double v=(double)a.x*((double)b.y*c.z-(double)b.z*c.y)+(double)a.y*((double)b.z*c.x-(double)b.x*c.z)+(double)a.z*((double)b.x*c.y-(double)b.y*c.x);
                volumes.TryGetValue(root,out double old);volumes[root]=old+v;
            }
            foreach(double volume in volumes.Values)if(double.IsNaN(volume)||double.IsInfinity(volume)||volume==0)
                throw new ArgumentException("Hole Mesh: оболочка не имеет корректного объёма.");
        }
        internal static bool Finite(float x)=>!float.IsNaN(x)&&!float.IsInfinity(x);
    }

    internal sealed class LTHoleMeshVolume
    {
        struct Triangle {internal Vector3 a,b,c,min,max;}
        struct Node {internal Vector3 min,max;internal int left,right,start,count;}
        readonly Triangle[] triangles;
        readonly int[] order;
        readonly List<Node> nodes=new List<Node>();
        internal readonly Vector3 Min,Max;
        internal Rect BoundsXZ=>Rect.MinMaxRect(Min.x,Min.z,Max.x,Max.z);
        sealed class CentroidComparer:IComparer<int>
        {
            internal Triangle[] data;internal int axis;
            public int Compare(int a,int b)
            {
                var x=data[a];var y=data[b];return ((double)x.min[axis]+x.max[axis]).CompareTo((double)y.min[axis]+y.max[axis]);
            }
        }
        internal LTHoleMeshVolume(LTHoleMeshData source,Matrix4x4 matrix)
        {
            if(source==null)throw new ArgumentException("Hole Mesh: отсутствуют данные.");
            var v=new Vector3[source.Vertices.Length];
            for(int i=0;i<v.Length;i++)
            {
                v[i]=matrix.MultiplyPoint3x4(source.Vertices[i]);
                if(!LTHoleMeshData.Finite(v[i].x)||!LTHoleMeshData.Finite(v[i].y)||!LTHoleMeshData.Finite(v[i].z))throw new ArgumentException("Hole Mesh: некорректный Transform.");
            }
            triangles=new Triangle[source.Indices.Length/3];order=new int[triangles.Length];
            for(int i=0;i<triangles.Length;i++)
            {
                var a=v[source.Indices[i*3]];var b=v[source.Indices[i*3+1]];var c=v[source.Indices[i*3+2]];
                triangles[i]=new Triangle{a=a,b=b,c=c,min=Vector3.Min(a,Vector3.Min(b,c)),max=Vector3.Max(a,Vector3.Max(b,c))};order[i]=i;
            }
            var comparer=new CentroidComparer{data=triangles};Build(0,order.Length,comparer);
            Min=nodes[0].min;Max=nodes[0].max;
            if(!(Max.x>Min.x&&Max.y>Min.y&&Max.z>Min.z))throw new ArgumentException("Hole Mesh: нулевой объём после Transform.");
        }
        int Build(int start,int count,CentroidComparer comparer)
        {
            var min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue);var max=-min;
            for(int i=start;i<start+count;i++){var t=triangles[order[i]];min=Vector3.Min(min,t.min);max=Vector3.Max(max,t.max);}
            int id=nodes.Count;nodes.Add(default);
            var node=new Node{min=min,max=max,start=start,count=count,left=-1,right=-1};
            if(count>8)
            {
                comparer.axis=max.x-min.x>=max.z-min.z?0:2;
                Array.Sort(order,start,count,comparer);int half=count/2;
                node.left=Build(start,half,comparer);node.right=Build(start+half,count-half,comparer);node.count=0;
            }
            nodes[id]=node;return id;
        }
        // Canonical edge equation: reversing an edge gives the exact negation.
        // Half-open raster ownership counts a shared diagonal/vertex only once.
        static double Edge(Vector3 a,Vector3 b,double x,double z)=>(a.z-(double)b.z)*x+(b.x-(double)a.x)*z+(double)a.x*b.z-(double)a.z*b.x;
        static bool Owned(double e,Vector3 a,Vector3 b)=>e>0||(e==0&&(b.z>a.z||(b.z==a.z&&b.x<a.x)));
        static bool VerticalHit(Triangle t,float x,float z,out double height)
        {
            var a=t.a;var b=t.b;var c=t.c;double area=Edge(a,b,c.x,c.z);height=0;
            if(area==0)return false;
            if(area<0){var swap=b;b=c;c=swap;area=-area;}
            double ea=Edge(b,c,x,z),eb=Edge(c,a,x,z),ec=Edge(a,b,x,z);
            if(!Owned(ea,b,c)||!Owned(eb,c,a)||!Owned(ec,a,b))return false;
            height=(ea*a.y+eb*b.y+ec*c.y)/area;return true;
        }
        internal bool Contains(float x,float y,float z)
        {
            if(!LTHoleMeshData.Finite(x)||!LTHoleMeshData.Finite(y)||!LTHoleMeshData.Finite(z)||x<Min.x||x>Max.x||z<Min.z||z>Max.z||y<Min.y||y>Max.y)return false;
            int count=0;bool surface=false;Count(0,x,y,z,ref count,ref surface);return surface||(count&1)!=0;
        }
        void Count(int id,float x,float y,float z,ref int count,ref bool surface)
        {
            var n=nodes[id];if(x<n.min.x||x>n.max.x||z<n.min.z||z>n.max.z||y>n.max.y)return;
            if(n.count==0){Count(n.left,x,y,z,ref count,ref surface);Count(n.right,x,y,z,ref count,ref surface);return;}
            for(int i=n.start;i<n.start+n.count;i++)if(VerticalHit(triangles[order[i]],x,z,out double hit))
            {
                if(hit>=y)count++;
                if(Math.Abs(hit-y)<=1e-7*Math.Max(1,Max.y-Min.y))surface=true;
            }
        }
        // Conservative projected BVH overlap. Fine cut cells stay protected at
        // distant LODs just like masked holes; unrelated cells can still simplify.
        internal bool Intersects(Rect area)=>Intersects(0,area);
        bool Intersects(int id,Rect r)
        {
            var n=nodes[id];if(n.max.x<r.xMin||n.min.x>r.xMax||n.max.z<r.yMin||n.min.z>r.yMax)return false;
            if(n.count==0)return Intersects(n.left,r)||Intersects(n.right,r);
            for(int i=n.start;i<n.start+n.count;i++)
            {var t=triangles[order[i]];if(t.max.x>=r.xMin&&t.min.x<=r.xMax&&t.max.z>=r.yMin&&t.min.z<=r.yMax)return true;}
            return false;
        }
    }
}
