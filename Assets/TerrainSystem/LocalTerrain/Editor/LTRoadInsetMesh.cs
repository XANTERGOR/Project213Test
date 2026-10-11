using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Build-time only. Subtract the ribbon footprint from land, insert the spline
    // grid, then simplify the CONNECTED surface. Never overlay a second road.
    public sealed class LTRoadInsetMesh
    {
        public struct Point
        {
            public double x,y,z;
            public Point(double x,double y,double z){this.x=x;this.y=y;this.z=z;}
            public static Point Lerp(Point a,Point b,double t)=>new Point(a.x+(b.x-a.x)*t,a.y+(b.y-a.y)*t,a.z+(b.z-a.z)*t);
        }
        internal sealed class Span
        {
            public Point[] rim;
            public readonly List<Point[]> faces=new List<Point[]>();
            public Rect bounds;
            public float step,landStep,reduction;
        }
        public sealed class Field
        {
            readonly Rect area;
            readonly List<Span> spans=new List<Span>();
            readonly Dictionary<(int,int),List<int>> bins=new Dictionary<(int,int),List<int>>();
            public readonly LTRoadGeometryMath.Field damage;
            const double BinSize=16;
            static int Bin(double value)=>(int)Math.Floor(value/BinSize);
            public Field(IEnumerable<LTRoadMath.Snapshot> roads,Rect area)
            {
                this.area=area;
                var pits=new List<LTRoadGeometryMath.Pit>();int faceCount=0;
                foreach(var road in roads)
                {
                    if(road==null||road.mode!=LTRoadMode.GeometricAsphalt)continue;
                    road.geometricPits.Query(area,pits);
                    for(int i=0;i+1<road.samples.Count;i++)
                    {
                        Point Edge(LTRoadMath.Sample s,float lateral)
                        {
                            var h=new LTRoadMath.Hit{position=s.position,right=s.right,distance=s.distance,bank=s.bank};
                            // Offset the profile in double, before any chunk
                            // subtraction. Adding it in world-space float bends
                            // otherwise straight rims by a rounding unit at km
                            // offsets and creates artificial clipping slivers.
                            return new Point((double)s.position.x+(double)s.right.x*lateral,
                                road.SurfaceHeight(h,lateral),(double)s.position.z+(double)s.right.z*lateral);
                        }
                        var a=road.samples[i];var b=road.samples[i+1];float half=road.width*.5f;
                        var rim=new[]{Edge(a,-half),Edge(a,half),Edge(b,half),Edge(b,-half)};
                        var bounds=Bounds(rim);if(!Overlap(bounds,area))continue;
                        for(int e=0;e<4;e++)if(Side(rim[(e+2)%4],rim[e],rim[(e+1)%4])<=1e-10)
                            throw Invalid("полотно складывается на повороте. Уменьшите ширину или смягчите сплайн");
                        var neighbours=new List<Span>();Query(bounds,neighbours);
                        foreach(var previous in neighbours)
                        {
                            var intersection=new List<Point>(rim);
                            for(int e=0;e<4&&intersection.Count>=3;e++)intersection=Clip(intersection,previous.rim[e],previous.rim[(e+1)%4],true);
                            if(Area(intersection)>1e-7)
                                throw Invalid("полотна пересекаются в плане. Перекрёстки и самопересечения пока не поддерживаются");
                        }
                        var span=new Span{rim=rim,bounds=bounds,step=road.insetCellSize,landStep=road.terrainCellSize,reduction=road.insetLodReduction};
                        double Length(Point p,Point q)=>Math.Sqrt((p.x-q.x)*(p.x-q.x)+(p.z-q.z)*(p.z-q.z));
                        int along=Math.Max(1,(int)Math.Ceiling(Math.Max(Length(rim[0],rim[3]),Length(rim[1],rim[2]))/span.step));
                        int across=Math.Max(1,(int)Math.Ceiling(road.width/span.step));
                        if((long)along*across*2+faceCount>300000)throw Invalid("слишком плотная сетка полотна в чанке. Увеличьте шаг сетки полотна");
                        Point At(int x,int z)=>Point.Lerp(Point.Lerp(rim[0],rim[3],z/(double)along),Point.Lerp(rim[1],rim[2],z/(double)along),x/(double)across);
                        for(int z=0;z<along;z++)for(int x=0;x<across;x++)
                        {
                            var p=At(x,z);var q=At(x+1,z);var r=At(x+1,z+1);var s=At(x,z+1);
                            span.faces.Add(new[]{p,s,q});span.faces.Add(new[]{q,s,r});faceCount+=2;
                        }
                        int id=spans.Count;spans.Add(span);
                        for(int z=Bin(Math.Max(bounds.yMin,area.yMin));z<=Bin(Math.Min(bounds.yMax,area.yMax));z++)for(int x=Bin(Math.Max(bounds.xMin,area.xMin));x<=Bin(Math.Min(bounds.xMax,area.xMax));x++)
                        {
                            if(!bins.TryGetValue((x,z),out var list))bins[(x,z)]=list=new List<int>();list.Add(id);
                        }
                    }
                }
                damage=pits.Count==0?null:LTRoadGeometryMath.Field.Combine(pits);
            }
            internal void Query(Rect rect,List<Span> output)
            {
                var ids=new HashSet<int>();
                for(int z=Bin(Math.Max(rect.yMin,area.yMin));z<=Bin(Math.Min(rect.yMax,area.yMax));z++)for(int x=Bin(Math.Max(rect.xMin,area.xMin));x<=Bin(Math.Min(rect.xMax,area.xMax));x++)
                    if(bins.TryGetValue((x,z),out var list))foreach(int id in list)if(Overlap(rect,spans[id].bounds))ids.Add(id);
                var sorted=new List<int>(ids);sorted.Sort();foreach(int id in sorted)output.Add(spans[id]);
            }
        }
        public static float TerrainDensity(LTRoadMath.Snapshot road,Rect region)
        {
            if(road.mode!=LTRoadMode.GeometricAsphalt||road.insetCellSize<=road.terrainCellSize)return road.terrainCellSize;
            var centre=region.center;
            float margin=region.size.magnitude*.5f+road.sampleSpacing;
            if(road.TrySample(centre.x,centre.y,out var hit)&&Math.Abs(hit.lateral)+margin<road.width*.5f&&hit.distance>margin&&hit.distance<road.length-margin)
                return road.insetCellSize;
            return road.terrainCellSize;
        }
        readonly Field field;readonly Rect chunk;readonly Vector2 worldSize;readonly int budget;
        readonly Func<float,float,float> height;
        readonly double precision;
        readonly List<Vector3> vertices,normals;readonly List<Vector2> uv;
        readonly Dictionary<(long,long,long),List<int>> ids=new Dictionary<(long,long,long),List<int>>();
        readonly Dictionary<int,Point> precise=new Dictionary<int,Point>();
        readonly List<Span> active=new List<Span>();
        readonly List<int> faces=new List<int>();
        readonly List<(Point a,Point b,List<(double t,double y)> anchors)> cellEdges=new List<(Point,Point,List<(double,double)>)>();
        Rect cell;
        public LTRoadInsetMesh(Field field,Rect chunk,Vector2 worldSize,int budget,List<Vector3> vertices,List<Vector3> normals,List<Vector2> uv,Func<float,float,float> height)
        {
            this.field=field;this.chunk=chunk;this.worldSize=worldSize;this.budget=budget;this.vertices=vertices;this.normals=normals;this.uv=uv;this.height=height;
            precision=1e-5;
        }
        public bool Begin(Rect region)
        {cell=region;faces.Clear();active.Clear();cellEdges.Clear();field.Query(region,active);return active.Count>0;}
        public void Boundary(List<int> edge)
        {
            for(int i=0;i<edge.Count;i++)
            {
                var a=Get(edge[i]);var b=Get(edge[(i+1)%edge.Count]);
                var anchors=new List<(double t,double y)>{(0,a.y),(1,b.y)};
                foreach(var span in active)for(int e=0;e<4;e++)
                {
                    var p=span.rim[e];var q=span.rim[(e+1)%4];
                    double dx=b.x-a.x,dz=b.z-a.z,ex=q.x-p.x,ez=q.z-p.z,den=dx*ez-dz*ex;
                    if(Math.Abs(den)<1e-14)continue;
                    double t=((p.x-a.x)*ez-(p.z-a.z)*ex)/den;
                    double u=((p.x-a.x)*dz-(p.z-a.z)*dx)/den;
                    if(t<0||t>1||u<0||u>1)continue;
                    anchors.Add((t,p.y+(q.y-p.y)*u));
                }
                anchors.Sort((p,q)=>p.t.CompareTo(q.t));cellEdges.Add((a,b,anchors));
            }
        }
        Point Get(int id){if(precise.TryGetValue(id,out var exact))return exact;var p=vertices[id];return new Point((double)chunk.xMin+p.x,p.y,(double)chunk.yMin+p.z);}
        public int CutVertex(Vector3 local)=>Vertex(new Point((double)chunk.xMin+local.x,local.y,(double)chunk.yMin+local.z));
        (long,long,long) Key(Point p)=>((long)Math.Round((p.x-chunk.xMin)/precision),(long)Math.Round(p.y/precision),(long)Math.Round((p.z-chunk.yMin)/precision));
        int Vertex(Point p)
        {
            var key=Key(p);int id=-1;double best=precision*precision;
            for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                if(ids.TryGetValue((key.Item1+x,key.Item2+y,key.Item3+z),out var candidates))foreach(int candidate in candidates)
                {
                    var q=Get(candidate);double d=(p.x-q.x)*(p.x-q.x)+(p.y-q.y)*(p.y-q.y)+(p.z-q.z)*(p.z-q.z);
                    if(d<=best){best=d;id=candidate;}
                }
            if(id>=0)return id;
            if(vertices.Count>=budget)throw Invalid("превышен Max Vertices Per Chunk. Увеличьте шаг полотна или лимит вершин");
            id=vertices.Count;if(!ids.TryGetValue(key,out var bucket))ids[key]=bucket=new List<int>();bucket.Add(id);
            precise.Add(id,p);vertices.Add(new Vector3((float)(p.x-chunk.xMin),(float)p.y,(float)(p.z-chunk.yMin)));
            normals.Add(Vector3.up);uv.Add(new Vector2((float)p.x/worldSize.x,(float)p.z/worldSize.y));return id;
        }
        Point ContactHeight(Point p)
        {
            double borderWeight=0,borderSum=0;
            double band=Math.Min(cell.width,cell.height)*.125;
            // A road contact changes the height of the shared land edge. All
            // extra subdivision points use the SAME piecewise edge, not the old
            // uncut triangle plane. Essential for banked roads and mixed LODs.
            foreach(var edge in cellEdges)
            {
                double dx=edge.b.x-edge.a.x,dz=edge.b.z-edge.a.z,l=dx*dx+dz*dz;
                if(l<=1e-20)continue;
                double t=((p.x-edge.a.x)*dx+(p.z-edge.a.z)*dz)/l;
                double distance=Math.Abs(Side(p,edge.a,edge.b))/Math.Sqrt(l);
                // Keep the accurate XZ partition, but match the edge height when
                // float world reconstruction would place this point ON the edge.
                if(edge.a.x==edge.b.x&&(float)p.x==(float)edge.a.x||edge.a.z==edge.b.z&&(float)p.z==(float)edge.a.z)distance=0;
                if(t<0||t>1||distance>=band)continue;
                for(int i=1;i<edge.anchors.Count;i++)
                {
                    var a=edge.anchors[i-1];var b=edge.anchors[i];if(t>b.t||b.t-a.t<1e-12)continue;
                    double borderHeight=a.y+(b.y-a.y)*(t-a.t)/(b.t-a.t);
                    if(distance<1e-6){p.y=borderHeight;return p;}
                    double ratio=distance/band,weight=(1-ratio)*(1-ratio)/Math.Max(1e-12,ratio*ratio);
                    borderWeight+=weight;borderSum+=borderHeight*weight;break;
                }
            }
            // Unity reconstructs terrain-local positions as floats. At kilometre
            // offsets a sub-millimetre split must not become a centimetre-high lip.
            // The canonical cell border above always wins over this proximity rule.
            double epsilon=Math.Max(2e-5,Math.Max(Math.Abs(p.x),Math.Abs(p.z))*2.384185791015625e-7);
            foreach(var span in active)for(int e=0;e<4;e++)
            {
                var a=span.rim[e];var b=span.rim[(e+1)%4];double dx=b.x-a.x,dz=b.z-a.z,l=dx*dx+dz*dz;
                if(l<=1e-20)continue;
                double t=((p.x-a.x)*dx+(p.z-a.z)*dz)/l;
                double tolerance=epsilon/Math.Sqrt(l);
                if(t>=-tolerance&&t<=1+tolerance&&Math.Abs(Side(p,a,b))<=Math.Sqrt(l)*epsilon)
                {p.y=a.y+(b.y-a.y)*Math.Max(0,Math.Min(1,t));return p;}
            }
            p.y=height((float)p.x,(float)p.z);
            if(borderWeight>0)
            {
                // Blend continuously into the accepted LOD edge. Merely snapping
                // points ON that edge would leave an arbitrarily steep sliver
                // next to it when the coarse plane differs from the heightfield.
                p.y=(p.y+borderSum)/(1+borderWeight);
            }
            return p;
        }
        public void Land(int a,int b,int c)
        {
            var polygons=new List<List<Point>>{new List<Point>{Get(a),Get(b),Get(c)}};
            foreach(var span in active)
            {
                var next=new List<List<Point>>();
                foreach(var polygon in polygons)
                {
                    var inner=polygon;
                    for(int e=0;e<4&&inner.Count>=3;e++)inner=Clip(inner,span.rim[e],span.rim[(e+1)%4],true);
                    if(Area(inner)<1e-12){next.Add(polygon);continue;}
                    var remaining=polygon;
                    for(int e=0;e<4&&remaining.Count>=3;e++)
                    {
                        var outside=Clip(remaining,span.rim[e],span.rim[(e+1)%4],false);
                        if(Area(outside)>1e-12)next.Add(outside);
                        remaining=Clip(remaining,span.rim[e],span.rim[(e+1)%4],true);
                    }
                }
                polygons=next;
            }
            foreach(var polygon in polygons)
            {
                for(int i=0;i<polygon.Count;i++)polygon[i]=ContactHeight(polygon[i]);
                Surface(polygon,faces);
            }
        }
        // Generated road triangles pass through the SAME authored-hole clipping
        // as land. The caller sends accepted pieces back to AcceptRoad.
        public void Road(Action<int,int,int> triangle)
        {
            var corners=new[]{new Point(cell.xMin,0,cell.yMin),new Point(cell.xMax,0,cell.yMin),new Point(cell.xMax,0,cell.yMax),new Point(cell.xMin,0,cell.yMax)};
            foreach(var span in active)foreach(var face in span.faces)
            {
                if(!Overlap(Bounds(face),cell))continue;
                var polygon=new List<Point>(face);
                for(int e=0;e<4&&polygon.Count>=3;e++)polygon=Clip(polygon,corners[e],corners[(e+1)%4],true);
                if(Area(polygon)<1e-12)continue;
                int a=Vertex(polygon[0]);for(int i=1;i+1<polygon.Count;i++)triangle(a,Vertex(polygon[i]),Vertex(polygon[i+1]));
            }
        }
        public void AcceptRoad(int a,int b,int c)=>Triangle(a,b,c,faces);
        void Surface(List<Point> p,List<int> result)
        {if(p.Count<3)return;int a=Vertex(p[0]);for(int i=1;i+1<p.Count;i++)Triangle(a,Vertex(p[i]),Vertex(p[i+1]),result);}
        void Triangle(int a,int b,int c,List<int> output)
        {
            var pa=Get(a);var pb=Get(b);var pc=Get(c);
            double area=Side(pc,pa,pb),scale=Math.Max(Math.Abs(pb.x-pa.x)+Math.Abs(pb.z-pa.z),Math.Abs(pc.x-pa.x)+Math.Abs(pc.z-pa.z));
            if(Math.Abs(area)<=Math.Max(1e-12,scale*1e-5))return;
            var cross=Vector3.Cross(vertices[b]-vertices[a],vertices[c]-vertices[a]);
            if(Math.Abs(cross.y)<1e-9)return;
            if(cross.y<0){int swap=b;b=c;c=swap;}output.Add(a);output.Add(b);output.Add(c);
        }
        public void Finish(Action<int,int,int> triangle)
        {
            if(faces.Count==0)return;
            // Clip planes introduce T vertices. Split only incident edges and
            // weld by position BEFORE reducing, so the road rim is not locked.
            var unique=new List<int>(new HashSet<int>(faces));unique.Sort();
            if(unique.Count>12000)throw Invalid("слишком много вершин в одной LOD-ячейке. Увеличьте шаг полотна");
            long Edge(int a,int b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
            var uses=new Dictionary<long,int>();
            for(int f=0;f<faces.Count;f+=3)for(int e=0;e<3;e++)
            {long key=Edge(faces[f+e],faces[f+(e+1)%3]);uses.TryGetValue(key,out int count);uses[key]=count+1;}
            var boundary=new HashSet<int>();
            foreach(var edge in uses)if(edge.Value==1){boundary.Add((int)(edge.Key>>32));boundary.Add((int)edge.Key);}
            unique.RemoveAll(id=>!boundary.Contains(id));
            var conformed=new List<int>();
            for(int f=0;f<faces.Count;f+=3)
            {
                var loop=new List<int>();bool split=false;
                for(int e=0;e<3;e++)
                {
                    int a=faces[f+e],b=faces[f+(e+1)%3];loop.Add(a);
                    if(uses[Edge(a,b)]==2)continue;
                    var p=Get(a);var q=Get(b);double dx=q.x-p.x,dz=q.z-p.z,l=dx*dx+dz*dz;
                    var points=new List<(double t,int id)>();
                    foreach(int id in unique)
                    {
                        if(id==a||id==b||l<1e-20)continue;var v=Get(id);
                        double t=((v.x-p.x)*dx+(v.z-p.z)*dz)/l;
                        if(t<=1e-7||t>=1-1e-7)continue;
                        if(Math.Abs(dx*(v.z-p.z)-dz*(v.x-p.x))>Math.Sqrt(l)*precision*2)continue;
                        points.Add((t,id));
                    }
                    points.Sort((a,b)=>a.t.CompareTo(b.t));foreach(var point in points){loop.Add(point.id);split=true;}
                }
                if(!split){Triangle(faces[f],faces[f+1],faces[f+2],conformed);continue;}
                if(loop.Count<=64)
                {
                    var polygon=new Vector2[loop.Count];for(int i=0;i<loop.Count;i++)polygon[i]=new Vector2(vertices[loop[i]].x,vertices[loop[i]].z);
                    var topology=LTPaintMath.TriangulateTransition(polygon);
                    if(topology.Length>0)
                    {
                        for(int i=0;i<topology.Length;i+=3)Triangle(loop[topology[i]],loop[topology[i+1]],loop[topology[i+2]],conformed);
                        continue;
                    }
                }
                var a0=Get(faces[f]);var b0=Get(faces[f+1]);var c0=Get(faces[f+2]);
                int centre=Vertex(new Point((a0.x+b0.x+c0.x)/3,(a0.y+b0.y+c0.y)/3,(a0.z+b0.z+c0.z)/3));
                for(int i=0;i<loop.Count;i++)Triangle(centre,loop[i],loop[(i+1)%loop.Count],conformed);
            }
            float error=float.PositiveInfinity;
            foreach(var span in active)
            {
                // No LOD0 collapse. Increasing cell extent drives coarsening;
                // all exterior cell edges remain fixed for mixed-LOD neighbours.
                float extent=Mathf.Max(cell.width,cell.height);
                float allowance=Mathf.Max(0,extent-span.landStep*1.01f)*.35f*span.reduction;
                error=Mathf.Min(error,allowance);
            }
            // Two independently clipped faces can contribute the SAME tiny
            // triangle when their near-collinear T-junctions are reconciled.
            // Keep one exact index triplet, never two overlapping copies. This
            // does not discard unique faces or merge nearby boundary positions.
            var seen=new HashSet<(int,int,int)>();var single=new List<int>(conformed.Count);
            for(int i=0;i<conformed.Count;i+=3)
            {
                int a=conformed[i],b=conformed[i+1],c=conformed[i+2];
                int lo=Math.Min(a,Math.Min(b,c)),hi=Math.Max(a,Math.Max(b,c));
                if(seen.Add((lo,a+b+c-lo-hi,hi))){single.Add(a);single.Add(b);single.Add(c);}
            }
            int[] result=single.ToArray();
            if(error>1e-5&&result.Length>18)
            {
                var local=new Dictionary<int,int>();var positions=new List<Vector3>();var global=new List<int>();
                for(int i=0;i<result.Length;i++)
                {
                    int id=result[i];if(!local.TryGetValue(id,out int mapped)){mapped=positions.Count;local.Add(id,mapped);positions.Add(vertices[id]);global.Add(id);}result[i]=mapped;
                }
                var points=positions.ToArray();
                result=PruneStraightBorder(points,result,error);
                var protectedVertices=new bool[points.Length];
                for(int i=0;i<points.Length;i++)
                {
                    float x=chunk.xMin+points[i].x,z=chunk.yMin+points[i].z;
                    protectedVertices[i]=x==cell.xMin||x==cell.xMax||z==cell.yMin||z==cell.yMax;
                }
                result=LTRockLODMath.ReduceHeightfield(points,result,error,48,protectedVertices);
                for(int i=0;i<result.Length;i++)result[i]=global[result[i]];
            }
            for(int i=0;i<result.Length;i+=3)triangle(result[i],result[i+1],result[i+2]);
        }
        // Subtraction can extend a span's clipping line to a cell border. Those
        // redundant COLLINEAR points must not pin a dense fence at far LOD.
        // Removing one leaves exactly the same shared 3D edge; a neighbour may
        // retain it without a crack. Never remove corners or height breakpoints.
        int[] PruneStraightBorder(Vector3[] positions,int[] source,float error)
        {
            long Edge(int a,int b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
            bool OnBorder(Vector3 a,Vector3 b,Vector3 c)
            {
                double x0=cell.xMin-chunk.xMin,x1=cell.xMax-chunk.xMin,z0=cell.yMin-chunk.yMin,z1=cell.yMax-chunk.yMin;
                bool X(double x)=>Math.Abs(a.x-x)<2e-5&&Math.Abs(b.x-x)<2e-5&&Math.Abs(c.x-x)<2e-5;
                bool Z(double z)=>Math.Abs(a.z-z)<2e-5&&Math.Abs(b.z-z)<2e-5&&Math.Abs(c.z-z)<2e-5;
                return X(x0)||X(x1)||Z(z0)||Z(z1);
            }
            var faces=new List<int>(source);
            for(int pass=0;pass<48;pass++)
            {
                var uses=new Dictionary<long,int>();var neighbours=new HashSet<int>[positions.Length];var incident=new List<int>[positions.Length];
                for(int f=0;f<faces.Count;f+=3)for(int e=0;e<3;e++)
                {
                    int a=faces[f+e],b=faces[f+(e+1)%3];long key=Edge(a,b);uses.TryGetValue(key,out int n);uses[key]=n+1;
                    (neighbours[a]??=new HashSet<int>()).Add(b);(neighbours[b]??=new HashSet<int>()).Add(a);
                    (incident[a]??=new List<int>()).Add(f);
                }
                var border=new List<int>[positions.Length];
                foreach(var edge in uses)if(edge.Value==1)
                {int a=(int)(edge.Key>>32),b=(int)edge.Key;(border[a]??=new List<int>()).Add(b);(border[b]??=new List<int>()).Add(a);}
                var map=new int[positions.Length];var touched=new bool[map.Length];for(int i=0;i<map.Length;i++)map[i]=i;int changes=0;
                for(int b=0;b<border.Length;b++)
                {
                    if(border[b]==null||border[b].Count!=2||touched[b])continue;
                    int a=border[b][0],c=border[b][1];var p=positions[a];var q=positions[b];var r=positions[c];
                    if(!OnBorder(p,q,r))continue;
                    var d=r-p;double l=d.sqrMagnitude,t=Vector3.Dot(q-p,d)/l;
                    if(l<1e-12||t<=0||t>=1||(q-(p+d*(float)t)).sqrMagnitude>1e-12)continue;
                    if((r-q).sqrMagnitude<(p-q).sqrMagnitude){a=c;p=r;}
                    if(touched[a]||Vector3.Distance(p,q)>error)continue;
                    int shared=0;foreach(int v in neighbours[a])if(neighbours[b].Contains(v))shared++;
                    if(shared!=1)continue;
                    bool safe=true;
                    foreach(int f in incident[b])
                    {
                        int u=faces[f],v=faces[f+1],w=faces[f+2];if(u==a||v==a||w==a)continue;
                        var old=Vector3.Cross(positions[v]-positions[u],positions[w]-positions[u]);
                        var next=Vector3.Cross(positions[v==b?a:v]-positions[u==b?a:u],positions[w==b?a:w]-positions[u==b?a:u]);
                        if(next.y<1e-9||Vector3.Dot(old.normalized,next.normalized)<.999f){safe=false;break;}
                    }
                    if(!safe)continue;
                    map[b]=a;changes++;touched[a]=touched[b]=true;foreach(int v in neighbours[a])touched[v]=true;foreach(int v in neighbours[b])touched[v]=true;
                }
                if(changes==0)break;
                var nextFaces=new List<int>();for(int f=0;f<faces.Count;f+=3)
                {int a=map[faces[f]],b=map[faces[f+1]],c=map[faces[f+2]];if(a!=b&&b!=c&&a!=c){nextFaces.Add(a);nextFaces.Add(b);nextFaces.Add(c);}}
                faces=nextFaces;
            }
            return faces.ToArray();
        }
        static InvalidOperationException Invalid(string message)=>new InvalidOperationException("Inset asphalt: "+message+". Предыдущие меши сохранены.");
        static bool Overlap(Rect a,Rect b)=>a.xMin<=b.xMax&&a.xMax>=b.xMin&&a.yMin<=b.yMax&&a.yMax>=b.yMin;
        static double Side(Point p,Point a,Point b)=>(b.x-a.x)*(p.z-a.z)-(b.z-a.z)*(p.x-a.x);
        static double Area(IList<Point> p)
        {double a=0;for(int i=1;i+1<p.Count;i++)a+=Side(p[i+1],p[0],p[i]);return Math.Abs(a)*.5;}
        static Rect Bounds(IList<Point> points)
        {
            double x=double.PositiveInfinity,z=x,xx=double.NegativeInfinity,zz=xx;
            foreach(var p in points){x=Math.Min(x,p.x);z=Math.Min(z,p.z);xx=Math.Max(xx,p.x);zz=Math.Max(zz,p.z);}
            return Rect.MinMaxRect((float)x,(float)z,(float)xx,(float)zz);
        }
        static List<Point> Clip(List<Point> input,Point a,Point b,bool inside)
        {
            var result=new List<Point>();if(input.Count==0)return result;
            void Add(Point p){if(result.Count==0||Math.Abs(result[result.Count-1].x-p.x)+Math.Abs(result[result.Count-1].z-p.z)>1e-9)result.Add(p);}
            var p=input[input.Count-1];double pd=Side(p,a,b);bool pin=inside?pd>=0:pd<=0;
            foreach(var q in input)
            {
                double qd=Side(q,a,b);bool qin=inside?qd>=0:qd<=0;
                if(pin!=qin)Add(Point.Lerp(p,q,pd/(pd-qd)));if(qin)Add(q);
                p=q;pd=qd;pin=qin;
            }
            if(result.Count>1&&Math.Abs(result[0].x-result[result.Count-1].x)+Math.Abs(result[0].z-result[result.Count-1].z)<1e-9)result.RemoveAt(result.Count-1);
            return result;
        }
    }
}
