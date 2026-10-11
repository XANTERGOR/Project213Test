using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Replaces parts of emitted terrain triangles, never overlays another mesh.
    // Convex subtraction finds even a pit wholly inside one coarse triangle.
    // All new top vertices interpolate the ORIGINAL triangle plane: fine/coarse
    // stitched edges cannot acquire a newly sampled, incompatible height.
    public sealed class LTRoadGeometryMesh
    {
        struct Point
        {
            public double x,y,z;
            public Point(double x,double y,double z){this.x=x;this.y=y;this.z=z;}
            public static Point Lerp(Point a,Point b,double t)=>new Point(a.x+(b.x-a.x)*t,a.y+(b.y-a.y)*t,a.z+(b.z-a.z)*t);
        }
        readonly LTRoadGeometryMath.Field field;
        readonly Rect rect;readonly Vector2 worldSize;readonly int budget;
        readonly List<Vector3> vertices,normals;readonly List<Vector2> uv;readonly List<int> indices;
        readonly Dictionary<(Vector3,int),int> vertexIds=new Dictionary<(Vector3,int),int>();
        readonly Dictionary<LTRoadGeometryMath.Pit,int> pitIds=new Dictionary<LTRoadGeometryMath.Pit,int>();
        readonly Dictionary<(int,int),int> surfaceRoles=new Dictionary<(int,int),int>();
        readonly List<LTRoadGeometryMath.Pit> candidates=new List<LTRoadGeometryMath.Pit>();
        public LTRoadGeometryMesh(LTRoadGeometryMath.Field field,Rect rect,Vector2 worldSize,int budget,
            List<Vector3> vertices,List<Vector3> normals,List<Vector2> uv,List<int> indices)
        {this.field=field;this.rect=rect;this.worldSize=worldSize;this.budget=budget;this.vertices=vertices;this.normals=normals;this.uv=uv;this.indices=indices;}
        Point Get(int i){var p=vertices[i];return new Point((double)rect.xMin+p.x,p.y,(double)rect.yMin+p.z);}
        static double Side(Point p,Vector2 a,Vector2 b)=>((double)b.x-a.x)*(p.z-a.y)-((double)b.y-a.y)*(p.x-a.x);
        static double Side(Point p,Point a,Point b)=>(b.x-a.x)*(p.z-a.z)-(b.z-a.z)*(p.x-a.x);
        static Point Rim(Vector2 p)=>new Point(p.x,0,p.y);
        static double Area(List<Point> p)
        {double area=0;for(int i=1;i+1<p.Count;i++)area+=(p[i].x-p[0].x)*(p[i+1].z-p[0].z)-(p[i].z-p[0].z)*(p[i+1].x-p[0].x);return Math.Abs(area)*.5;}
        static void Add(List<Point> p,Point v)
        {if(p.Count==0||Math.Abs(p[p.Count-1].x-v.x)+Math.Abs(p[p.Count-1].z-v.z)>1e-9)p.Add(v);}
        static List<Point> Clip(List<Point> input,Vector2 a,Vector2 b,bool inside)
            =>Clip(input,Rim(a),Rim(b),inside);
        static List<Point> Clip(List<Point> input,Point a,Point b,bool inside)
        {
            var output=new List<Point>(input.Count+1);if(input.Count==0)return output;
            var previous=input[input.Count-1];double pd=Side(previous,a,b);bool pin=inside?pd>=0:pd<=0;
            foreach(var current in input)
            {
                double cd=Side(current,a,b);bool cin=inside?cd>=0:cd<=0;
                if(cin!=pin)Add(output,Point.Lerp(previous,current,pd/(pd-cd)));
                if(cin)Add(output,current);
                previous=current;pd=cd;pin=cin;
            }
            if(output.Count>1&&Math.Abs(output[0].x-output[output.Count-1].x)+Math.Abs(output[0].z-output[output.Count-1].z)<1e-9)output.RemoveAt(output.Count-1);
            return output;
        }
        static Rect Bounds(List<Point> points)
        {
            double x=double.PositiveInfinity,z=x,xx=double.NegativeInfinity,zz=xx;
            foreach(var p in points){x=Math.Min(x,p.x);z=Math.Min(z,p.z);xx=Math.Max(xx,p.x);zz=Math.Max(zz,p.z);}
            return Rect.MinMaxRect((float)x,(float)z,(float)xx,(float)zz);
        }
        public bool Emit(int a,int b,int c)
        {
            var pa=Get(a);var pb=Get(b);var pc=Get(c);
            var area=Rect.MinMaxRect((float)Math.Min(pa.x,Math.Min(pb.x,pc.x)),(float)Math.Min(pa.z,Math.Min(pb.z,pc.z)),
                (float)Math.Max(pa.x,Math.Max(pb.x,pc.x)),(float)Math.Max(pa.z,Math.Max(pb.z,pc.z)));
            candidates.Clear();field.Query(area,candidates);if(candidates.Count==0)return false;
            var original=new List<Point>{pa,pb,pc};
            // Seed original top vertices; a floor/wall always has its own normal.
            vertexIds[(vertices[a],0)]=a;vertexIds[(vertices[b],0)]=b;vertexIds[(vertices[c],0)]=c;
            var surfaces=new List<List<Point>>{original};bool changed=false,hasBevel=false,hasConcave=false;
            foreach(var pit in candidates)
            {
                if(pit.Concave)
                {
                    hasConcave=true;
                    hasBevel=true; // simplify coplanar fragments in the new path too
                    for(int part=0;part<pit.PartCount;part++)
                    {
                        int count=pit.PartSize(part);var floor=original;
                        for(int e=0;e<count&&floor.Count>=3;e++)floor=Clip(floor,pit.PartPoint(part,e),pit.PartPoint(part,(e+1)%count),true);
                        if(floor.Count<3||Area(floor)<1e-10)continue;
                        changed=true;
                        if(!pitIds.TryGetValue(pit,out int id)){id=pitIds.Count+1;pitIds.Add(pit,id);}
                        FloorAndWalls(floor,pit,id,false);
                        var outsideParts=new List<List<Point>>();
                        foreach(var polygon in surfaces)
                        {
                            var inner=polygon;
                            for(int e=0;e<count&&inner.Count>=3;e++)inner=Clip(inner,pit.PartPoint(part,e),pit.PartPoint(part,(e+1)%count),true);
                            if(inner.Count<3||Area(inner)<1e-10){outsideParts.Add(polygon);continue;}
                            var rest=polygon;
                            for(int e=0;e<count&&rest.Count>=3;e++)
                            {
                                var outside=Clip(rest,pit.PartPoint(part,e),pit.PartPoint(part,(e+1)%count),false);
                                if(outside.Count>=3&&Area(outside)>1e-10)outsideParts.Add(outside);
                                rest=Clip(rest,pit.PartPoint(part,e),pit.PartPoint(part,(e+1)%count),true);
                            }
                        }
                        surfaces=outsideParts;
                    }
                    continue;
                }
                // Pits are disjoint. Cut each bevel directly from the original
                // triangle, not from fragments made by other pits' infinite cut
                // planes. Otherwise distant, larger triangles accumulate fake
                // subdivisions across neighbouring damage features.
                if(pit.Beveled)
                {
                    hasBevel=true;
                    var bevel=original;
                    for(int e=0;e<pit.Count&&bevel.Count>=3;e++)bevel=Clip(bevel,pit[e],pit[(e+1)%pit.Count],true);
                    if(bevel.Count<3||Area(bevel)<1e-10)continue;
                    changed=true;
                    if(!pitIds.TryGetValue(pit,out int bevelId)){bevelId=pitIds.Count+1;pitIds.Add(pit,bevelId);}
                    Bevel(bevel,pit,bevelId);
                }
                var next=new List<List<Point>>();
                foreach(var polygon in surfaces)
                {
                    if(!LTRoadGeometryMath.Overlap(Bounds(polygon),pit.bounds)){next.Add(polygon);continue;}
                    var inner=polygon;
                    for(int e=0;e<pit.Count&&inner.Count>=3;e++)inner=Clip(inner,pit[e],pit[(e+1)%pit.Count],true);
                    if(inner.Count<3||Area(inner)<1e-10){next.Add(polygon);continue;}
                    changed=true;
                    if(!pitIds.TryGetValue(pit,out int id)){id=pitIds.Count+1;pitIds.Add(pit,id);}
                    var remaining=polygon;
                    for(int e=0;e<pit.Count&&remaining.Count>=3;e++)
                    {
                        var outside=Clip(remaining,pit[e],pit[(e+1)%pit.Count],false);
                        if(outside.Count>=3&&Area(outside)>1e-10)next.Add(outside);
                        remaining=Clip(remaining,pit[e],pit[(e+1)%pit.Count],true);
                    }
                    if(!pit.Beveled)FloorAndWalls(inner,pit,id,false);
                }
                surfaces=next;
            }
            if(!changed)return false;
            if(hasBevel)MergeConvex(surfaces);
            if(hasConcave)Conform(surfaces);
            foreach(var polygon in surfaces)
            {
                if(hasConcave)ConformedSurface(polygon,0,0,Vector3.up);
                else Surface(polygon,0,0,null,hasBevel);
            }
            return true;
        }
        static void Conform(List<List<Point>> polygons)
        {
            var points=new List<Point>();foreach(var p in polygons)points.AddRange(p);
            for(int k=0;k<polygons.Count;k++)
            {
                var input=polygons[k];var output=new List<Point>();
                for(int e=0;e<input.Count;e++)
                {
                    var a=input[e];var b=input[(e+1)%input.Count];Add(output,a);
                    double dx=b.x-a.x,dz=b.z-a.z,length=dx*dx+dz*dz;
                    var cuts=new List<(double t,Point p)>();
                    if(length<=1e-20)continue;
                    foreach(var p in points)
                    {
                        double t=((p.x-a.x)*dx+(p.z-a.z)*dz)/length;
                        if(t>1e-10&&t<1-1e-10&&Collinear(a,b,p))cuts.Add((t,p));
                    }
                    cuts.Sort((p,q)=>p.t.CompareTo(q.t));foreach(var p in cuts)Add(output,p.p);
                }
                polygons[k]=output;
            }
        }
        void ConformedSurface(List<Point> polygon,int role,float drop,Vector3 normal)
        {
            if(polygon.Count<3)return;
            bool split=false;for(int i=0;i<polygon.Count;i++)
                if(Collinear(polygon[(i+polygon.Count-1)%polygon.Count],polygon[i],polygon[(i+1)%polygon.Count])){split=true;break;}
            if(!split){Surface(polygon,role,drop,normal);return;}
            // A centre fan retains deliberate collinear edge splits. An ordinary
            // corner fan would silently coalesce the first/last split edges.
            var centre=new Point();foreach(var p in polygon){centre.x+=p.x/polygon.Count;centre.y+=p.y/polygon.Count;centre.z+=p.z/polygon.Count;}
            int mid=Vertex(centre,role,drop,normal);
            for(int i=0;i<polygon.Count;i++)Triangle(mid,Vertex(polygon[i],role,drop,normal),Vertex(polygon[(i+1)%polygon.Count],role,drop,normal),normal);
        }
        static void MergeConvex(List<List<Point>> polygons)
        {
            // Undo redundant extensions of pit clipping planes. All fragments
            // are disjoint and lie on ONE original triangle plane. Merge only
            // touching fragments whose convex hull has exactly the same area;
            // never bridge a concavity or a pothole. Bound editor bake work.
            if(polygons.Count>64)return;
            int attempts=0;
            for(int a=0;a<polygons.Count;a++)for(int b=a+1;b<polygons.Count;b++)
            {
                if(++attempts>2048)return;
                var pa=polygons[a];var pb=polygons[b];int shared=0;
                foreach(var p in pa)foreach(var q in pb)
                    if(Math.Abs(p.x-q.x)+Math.Abs(p.z-q.z)<1e-9){shared++;break;}
                if(shared<2)continue;
                var points=new List<Point>(pa);points.AddRange(pb);
                points.Sort((p,q)=>{int c=p.x.CompareTo(q.x);return c!=0?c:p.z.CompareTo(q.z);});
                var hull=new List<Point>();
                void Push(Point p,int start)
                {
                    while(hull.Count>start+1)
                    {
                        var q=hull[hull.Count-2];var r=hull[hull.Count-1];
                        if(Side(p,q,r)>0&&!Collinear(q,r,p))break;hull.RemoveAt(hull.Count-1);
                    }
                    hull.Add(p);
                }
                foreach(var p in points)Push(p,0);
                int lower=hull.Count-1;for(int i=points.Count-2;i>=0;i--)Push(points[i],lower);
                hull.RemoveAt(hull.Count-1);
                if(hull.Count<3)continue;
                double area=Area(pa)+Area(pb),merged=Area(hull);
                if(Math.Abs(merged-area)>Math.Max(1e-11,area*1e-12))continue;
                polygons[a]=hull;polygons.RemoveAt(b);a=-1;break;
            }
        }
        int Role(int pit,int surface)
        {
            var key=(pit,surface);if(!surfaceRoles.TryGetValue(key,out int role))
            {role=surfaceRoles.Count+1;surfaceRoles.Add(key,role);}return role;
        }
        static Point Inner(LTRoadGeometryMath.Pit pit,int i)=>new Point(pit.InnerX(i),0,pit.InnerZ(i));
        void Bevel(List<Point> polygon,LTRoadGeometryMath.Pit pit,int id)
        {
            // Eight analytic facets, NOT a densely sampled protected ring. Every
            // LOD clips the same planes, including across a mixed-LOD boundary.
            // Extra splits follow the road/terrain mesh and disappear with it.
            var floor=polygon;
            for(int e=0;e<pit.Count&&floor.Count>=3;e++)floor=Clip(floor,Inner(pit,e),Inner(pit,(e+1)%pit.Count),true);
            if(floor.Count>=3&&Area(floor)>1e-10)FloorAndWalls(floor,pit,id,true);
            for(int e=0;e<pit.Count;e++)
            {
                int next=(e+1)%pit.Count;
                var a=Rim(pit[e]);var b=Rim(pit[next]);var c=Inner(pit,next);var d=Inner(pit,e);
                var facet=Clip(polygon,a,b,true);facet=Clip(facet,b,c,true);
                facet=Clip(facet,c,d,true);facet=Clip(facet,d,a,true);
                if(facet.Count<3||Area(facet)<1e-10)continue;
                double full=Side(d,a,b);
                for(int i=0;i<facet.Count;i++)
                {
                    var p=facet[i];p.y-=pit.bevelDepth*Math.Max(0,Math.Min(1,Side(p,a,b)/full));facet[i]=p;
                }
                Vector3 normal=Vector3.up;
                Vector3 Delta(Point p,Point q)=>new Vector3((float)(p.x-q.x),(float)(p.y-q.y),(float)(p.z-q.z));
                for(int i=1;i+1<facet.Count;i++)
                {
                    var cross=Vector3.Cross(Delta(facet[i],facet[0]),Delta(facet[i+1],facet[0]));
                    if(cross.sqrMagnitude<=1e-18f)continue;
                    normal=(cross.y<0?-cross:cross).normalized;break;
                }
                Surface(facet,Role(id,pit.Count+e),0,normal,true);
            }
        }
        void FloorAndWalls(List<Point> polygon,LTRoadGeometryMath.Pit pit,int id,bool inset)
        {
            Surface(polygon,-id,pit.depth,Vector3.up,inset||pit.Concave);
            for(int i=0;i<polygon.Count;i++)
            {
                var p=polygon[i];var q=polygon[(i+1)%polygon.Count];
                for(int e=0;e<pit.Count;e++)
                {
                    var start=inset?Inner(pit,e):Rim(pit[e]);var end=inset?Inner(pit,(e+1)%pit.Count):Rim(pit[(e+1)%pit.Count]);
                    double dx=end.x-start.x,dz=end.z-start.z,length=Math.Sqrt(dx*dx+dz*dz);
                    if(Math.Abs(Side(p,start,end))>length*1e-7||Math.Abs(Side(q,start,end))>length*1e-7)continue;
                    if(pit.Concave)
                    {
                        double tp=((p.x-start.x)*dx+(p.z-start.z)*dz)/(length*length);
                        double tq=((q.x-start.x)*dx+(q.z-start.z)*dz)/(length*length);
                        if(tp< -1e-7||tp>1+1e-7||tq< -1e-7||tq>1+1e-7)continue;
                    }
                    var normal=new Vector3((float)-dz,0,(float)dx).normalized;
                    int role=Role(id,e);float drop=inset?pit.bevelDepth:0;
                    int topP=Vertex(p,role,drop,normal),topQ=Vertex(q,role,drop,normal);
                    int bottomP=Vertex(p,role,pit.depth,normal),bottomQ=Vertex(q,role,pit.depth,normal);
                    Triangle(topP,topQ,bottomP,normal);Triangle(bottomP,topQ,bottomQ,normal);break;
                }
            }
        }
        int Vertex(Point point,int role,float drop,Vector3 normal)
        {
            // Cast once, after double-precision intersection. Chunk-local values
            // avoid quantization worsening with distance from the world origin.
            var p=new Vector3((float)(point.x-rect.xMin),(float)(point.y-drop),(float)(point.z-rect.yMin));
            var key=(p,role);if(vertexIds.TryGetValue(key,out int found))return found;
            if(vertices.Count>=budget)throw new InvalidOperationException("Geometric Asphalt exceeded Max Vertices Per Chunk. Increase spacing / terrain cell size or the budget. Previous meshes are preserved.");
            int id=vertices.Count;vertexIds.Add(key,id);vertices.Add(p);normals.Add(normal);
            uv.Add(new Vector2((float)point.x/worldSize.x,(float)point.z/worldSize.y));return id;
        }
        static bool Collinear(Point p,Point q,Point r)
        {
            double left=(q.x-p.x)*(r.z-p.z),right=(q.z-p.z)*(r.x-p.x);
            double magnitude=Math.Max(Math.Max(Math.Abs(p.x),Math.Abs(p.z)),Math.Max(Math.Max(Math.Abs(q.x),Math.Abs(q.z)),Math.Max(Math.Abs(r.x),Math.Abs(r.z))));
            double span=Math.Abs(q.x-p.x)+Math.Abs(q.z-p.z)+Math.Abs(r.x-p.x)+Math.Abs(r.z-p.z);
            double rounding=magnitude*2.220446049250313e-16*span*16;
            return Math.Abs(left-right)<=Math.Max(rounding,Math.Max(1e-14,(Math.Abs(left)+Math.Abs(right))*1e-12));
        }
        void Surface(List<Point> polygon,int role,float drop,Vector3? surfaceNormal=null,bool simplify=false)
        {
            if(polygon.Count<3)return;
            if(simplify)
            {
                polygon=new List<Point>(polygon);
                for(int i=polygon.Count-1;i>=0&&polygon.Count>3;i--)
                    if(Collinear(polygon[(i+polygon.Count-1)%polygon.Count],polygon[i],polygon[(i+1)%polygon.Count]))polygon.RemoveAt(i);
            }
            var normal=surfaceNormal??Vector3.up;
            int a=Vertex(polygon[0],role,drop,normal);
            for(int i=1;i+1<polygon.Count;i++)
            {
                var p=polygon[0];var q=polygon[i];var r=polygon[i+1];
                // Clipping can leave three collinear fan points. Float casting
                // may turn them into a phantom sliver / third rim-edge use.
                // Reject in double BEFORE casting, not by enlarging a seam weld.
                if(Collinear(p,q,r))continue;
                Triangle(a,Vertex(q,role,drop,normal),Vertex(r,role,drop,normal),normal);
            }
        }
        void Triangle(int a,int b,int c,Vector3 normal)
        {
            var cross=Vector3.Cross(vertices[b]-vertices[a],vertices[c]-vertices[a]);
            if(cross.sqrMagnitude<1e-18f)return;
            if(Vector3.Dot(cross,normal)<0){int swap=b;b=c;c=swap;}
            indices.Add(a);indices.Add(b);indices.Add(c);
        }
    }
}
