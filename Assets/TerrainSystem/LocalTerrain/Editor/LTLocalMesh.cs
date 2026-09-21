using System;
using System.Collections.Generic;
using UnityEngine;
namespace LocalTerrainPrototype
{
    // Sparse quadtree sampling with explicit edge stitching. Height samples are evaluated
    // at their real positions (not interpolated from the old coarse mesh).
    public static class LTLocalMesh
    {
        public struct Zone { public Rect bounds; public float cellSize; }
        struct Leaf { public int x,z,w,h; public Leaf(int a,int b,int c,int d){x=a;z=b;w=c;h=d;} }
        sealed class Builder
        {
            public int n, baseStride; public Rect rect;public Vector2 worldSize;
            public float error,normalStepX,normalStepZ;
            public Func<float,float,float> evaluate;public List<Zone> zones;
            public readonly List<Vector3> vertices=new List<Vector3>(),normals=new List<Vector3>();
            public readonly List<Vector2> uv=new List<Vector2>();public readonly List<int> triangles=new List<int>();
            readonly List<Leaf> leaves=new List<Leaf>();
            readonly Dictionary<Vector2Int,float> heights=new Dictionary<Vector2Int,float>();
            readonly Dictionary<Vector2Int,int> vertexIDs=new Dictionary<Vector2Int,int>();
            readonly Dictionary<int,SortedSet<int>> horizontal=new Dictionary<int,SortedSet<int>>(),vertical=new Dictionary<int,SortedSet<int>>();
            static bool Overlap(Rect a,Rect b)=>a.xMin<=b.xMax&&a.xMax>=b.xMin&&a.yMin<=b.yMax&&a.yMax>=b.yMin;
            float X(float x)=>(Mathf.RoundToInt(rect.xMin/rect.width)*n+x)*(rect.width/n);
            float Z(float z)=>(Mathf.RoundToInt(rect.yMin/rect.height)*n+z)*(rect.height/n);
            float Height(float x,float z)
            {
                var key=new Vector2Int(Mathf.RoundToInt(x*2),Mathf.RoundToInt(z*2));
                if(!heights.TryGetValue(key,out float h))heights[key]=h=evaluate(X(x),Z(z));
                return h;
            }
            bool MustSplit(int x,int z,int w,int h)
            {
                var r=new Rect(X(x),Z(z),w*rect.width/n,h*rect.height/n);
                foreach(var zone in zones)if(Overlap(r,zone.bounds))
                    if((w>1&&r.width>zone.cellSize*1.00001f)||(h>1&&r.height>zone.cellSize*1.00001f))return true;
                return false;
            }
            bool Fits(int x,int z,int w,int h)
            {
                float a=Height(x,z),sx=(Height(x+w,z)-a)/w,sz=(Height(x,z+h)-a)/h;
                // Half tolerance around a plane bounds errors of its triangulated approximation
                // at checked samples. This is a sampled, not continuous, error guarantee.
                Func<float,float,bool> fits=(px,pz)=>Mathf.Abs(Height(px,pz)-(a+(px-x)*sx+(pz-z)*sz))<=error*.5f;
                int stride=Mathf.Max(1,Mathf.Min(baseStride,Mathf.Min(w,h)));
                for(int j=z;j<=z+h;j+=stride)for(int i=x;i<=x+w;i+=stride)if(!fits(i,j))return false;
                for(int j=z;j<=z+h;j+=stride)if(!fits(x+w,j))return false;
                for(int i=x;i<=x+w;i+=stride)if(!fits(i,z+h))return false;
                if(!fits(x+w,z+h)||!fits(x+w*.5f,z+h*.5f))return false;
                // Every possible stitched edge vertex must satisfy the same plane error.
                // Neighbour leaves may insert fine corners later, so check the whole edge now.
                for(int j=z;j<=z+h;j++)if(!fits(x,j)||!fits(x+w,j))return false;
                for(int i=x;i<=x+w;i++)if(!fits(i,z)||!fits(i,z+h))return false;
                return true;
            }
            void Split(int x,int z,int w,int h)
            {
                if((w==1&&h==1)||(!MustSplit(x,z,w,h)&&Fits(x,z,w,h)))
                {leaves.Add(new Leaf(x,z,w,h));return;}
                int wx=w/2,hz=h/2;
                if(w>1&&h>1){Split(x,z,wx,hz);Split(x+wx,z,w-wx,hz);Split(x,z+hz,wx,h-hz);Split(x+wx,z+hz,w-wx,h-hz);}
                else if(w>1){Split(x,z,wx,h);Split(x+wx,z,w-wx,h);}
                else {Split(x,z,w,hz);Split(x,z+hz,w,h-hz);}
            }
            static void Add(Dictionary<int,SortedSet<int>> map,int line,int value)
            {if(!map.TryGetValue(line,out var set))map[line]=set=new SortedSet<int>();set.Add(value);}
            void Corner(int x,int z){Add(horizontal,z,x);Add(vertical,x,z);}
            int Vertex(float x,float z)
            {
                var key=new Vector2Int(Mathf.RoundToInt(x*2),Mathf.RoundToInt(z*2));
                if(vertexIDs.TryGetValue(key,out int id))return id;
                id=vertices.Count;vertexIDs[key]=id;
                float gx=X(x),gz=Z(z);
                vertices.Add(new Vector3(x*rect.width/n,Height(x,z),z*rect.height/n));
                float sx=(evaluate(gx+normalStepX,gz)-evaluate(gx-normalStepX,gz))/(2*normalStepX);
                float sz=(evaluate(gx,gz+normalStepZ)-evaluate(gx,gz-normalStepZ))/(2*normalStepZ);
                normals.Add(new Vector3(-sx,1,-sz).normalized);uv.Add(new Vector2(gx/worldSize.x,gz/worldSize.y));return id;
            }
            void Triangle(int a,int b,int c){triangles.Add(a);triangles.Add(b);triangles.Add(c);}
            public void Run()
            {
                Split(0,0,n,n);
                foreach(var l in leaves){Corner(l.x,l.z);Corner(l.x+l.w,l.z);Corner(l.x,l.z+l.h);Corner(l.x+l.w,l.z+l.h);}
                // Conservative stable interface: refinement of one chunk never forces a cascade
                // of topology rebuilds through unchanged neighbours.
                for(int i=0;i<=n;i++){Corner(0,i);Corner(n,i);Corner(i,0);Corner(i,n);}
                foreach(var l in leaves)
                {
                    if(l.w==1&&l.h==1)
                    {int a=Vertex(l.x,l.z),b=Vertex(l.x+1,l.z),c=Vertex(l.x,l.z+1),d=Vertex(l.x+1,l.z+1);Triangle(a,c,b);Triangle(b,c,d);continue;}
                    var edge=new List<int>();
                    foreach(int z in vertical[l.x].GetViewBetween(l.z,l.z+l.h-1))edge.Add(Vertex(l.x,z));
                    foreach(int x in horizontal[l.z+l.h].GetViewBetween(l.x,l.x+l.w-1))edge.Add(Vertex(x,l.z+l.h));
                    var right=new List<int>(vertical[l.x+l.w].GetViewBetween(l.z+1,l.z+l.h));right.Reverse();
                    foreach(int z in right)edge.Add(Vertex(l.x+l.w,z));
                    var bottom=new List<int>(horizontal[l.z].GetViewBetween(l.x+1,l.x+l.w));bottom.Reverse();
                    foreach(int x in bottom)edge.Add(Vertex(x,l.z));
                    int centre=Vertex(l.x+l.w*.5f,l.z+l.h*.5f);
                    for(int i=0;i<edge.Count;i++)Triangle(centre,edge[i],edge[(i+1)%edge.Count]);
                }
            }
        }
        public static void Build(Rect rect,Vector2 worldSize,int baseCells,int refinement,float error,List<Zone> zones,
            Func<float,float,float> evaluate,out Vector3[] vertices,out Vector3[] normals,out Vector2[] uv,out int[] triangles)
        {
            int factor=1<<Mathf.Clamp(refinement,0,4);
            int n=Mathf.Min(512,baseCells*factor);
            var b=new Builder {n=n,baseStride=Mathf.Max(1,n/baseCells),rect=rect,worldSize=worldSize,error=error,zones=zones,evaluate=evaluate,
                normalStepX=rect.width/n,normalStepZ=rect.height/n};b.Run();
            vertices=b.vertices.ToArray();normals=b.normals.ToArray();uv=b.uv.ToArray();triangles=b.triangles.ToArray();
        }
    }
}
