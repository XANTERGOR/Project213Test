using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEditor;
namespace LocalTerrainPrototype
{
    public static class LTStampMesh
    {
        // Optional, build-owned diagnostics. Detailed clocks run on one cell per
        // block, with a different deterministic position in each block to avoid
        // always sampling the same checkerboard diagonal/row. No global state,
        // retained meshes or scene settings. Timings are never extrapolated.
        public sealed class EmitDiagnostics
        {
            public enum Part {Traversal,VertexLookup,VertexCreate,Height,Stitches,Topology,Triangulate,Clip,Intersection,Indices}
            readonly int stride;
            readonly long[] ticks=new long[10];
            long last,started,allocatedBefore;
            int gc0,gc1,gc2;
            Part current;
            bool active,sampling;
            public bool Completed {get;private set;}
            public long TotalTicks,SetupTicks,CellTicks,CompactTicks,ArrayTicks,SampleTicks;
            public long Cells,SampledCells,VertexRequests,VertexHits,HeightRequests,HeightHits;
            public long TriangleRequests,IntersectionRequests,IntersectionHits;
            public long AllocatedBytes;
            public int Gen0,Gen1,Gen2,VerticesBeforeCompact,OutputVertices,OutputIndices;
            // Payload only, excluding array headers and every temporary allocation.
            // A completed Emit necessarily allocates at least this much in ToArray.
            public long OutputPayloadBytes=>OutputVertices*32L+OutputIndices*4L;
            public bool AllocationAvailable=>AllocatedBytes>0&&(!Completed||AllocatedBytes>=OutputPayloadBytes);
            public EmitDiagnostics(int sampleStride=64)
            {
                if(sampleStride<1||sampleStride>1024||(sampleStride&(sampleStride-1))!=0)
                    throw new ArgumentOutOfRangeException(nameof(sampleStride),"Sampling stride must be a power of two from 1 to 1024.");
                stride=sampleStride;
            }
            internal void Begin()
            {
                if(active)throw new InvalidOperationException("Emitter diagnostics cannot be shared by simultaneous builds.");
                Array.Clear(ticks,0,ticks.Length);Completed=false;sampling=false;active=true;
                TotalTicks=SetupTicks=CellTicks=CompactTicks=ArrayTicks=SampleTicks=0;
                Cells=SampledCells=VertexRequests=VertexHits=HeightRequests=HeightHits=0;
                TriangleRequests=IntersectionRequests=IntersectionHits=AllocatedBytes=0;
                Gen0=Gen1=Gen2=VerticesBeforeCompact=OutputVertices=OutputIndices=0;
                gc0=GC.CollectionCount(0);gc1=GC.CollectionCount(1);gc2=GC.CollectionCount(2);
                allocatedBefore=GC.GetAllocatedBytesForCurrentThread();started=Stopwatch.GetTimestamp();
            }
            internal void Finish(bool completed)
            {
                TotalTicks=Stopwatch.GetTimestamp()-started;
                AllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocatedBefore;
                Gen0=GC.CollectionCount(0)-gc0;Gen1=GC.CollectionCount(1)-gc1;Gen2=GC.CollectionCount(2)-gc2;
                Completed=completed;active=false;sampling=false;
            }
            internal bool BeginCell(int count)
            {
                long index=Cells++;uint block=(uint)(index/stride);
                uint mixed=unchecked(block*0x9e3779b1u);mixed^=mixed>>16;
                sampling=count<=stride||index%stride==(mixed&(uint)(stride-1));
                if(sampling){SampledCells++;current=Part.Traversal;last=Stopwatch.GetTimestamp();}
                return sampling;
            }
            internal void EndCell()
            {long now=Stopwatch.GetTimestamp(),elapsed=now-last;ticks[(int)current]+=elapsed;SampleTicks+=elapsed;sampling=false;}
            void Switch(Part next)
            {
                long now=Stopwatch.GetTimestamp(),elapsed=now-last;
                ticks[(int)current]+=elapsed;SampleTicks+=elapsed;last=now;current=next;
            }
            internal readonly struct Scope:IDisposable
            {
                readonly EmitDiagnostics owner;readonly Part previous;
                internal Scope(EmitDiagnostics owner,Part part)
                {this.owner=owner;previous=owner.current;owner.Switch(part);}
                public void Dispose(){if(owner!=null)owner.Switch(previous);}
            }
            internal Scope Measure(Part part)=>new Scope(this,part);
            public long SampledTicks(Part part)=>ticks[(int)part];
            public static double Milliseconds(long value)=>value*(1000.0/Stopwatch.Frequency);
            public string Report(string chunk)
            {
                var text=new StringBuilder();
                text.AppendLine($"Emit detail {chunk}: {(Completed?"complete":"incomplete")}; {Cells} cells; vertices {VerticesBeforeCompact} -> {OutputVertices}; triangles {OutputIndices/3}");
                text.AppendLine($"Emit exact: setup {Milliseconds(SetupTicks):F2}; cell loop {Milliseconds(CellTicks):F2}; compact {Milliseconds(CompactTicks):F2}; arrays {Milliseconds(ArrayTicks):F2} ms (parts of emit, not extra work)");
                text.AppendLine($"Emit counts: vertex cache {VertexHits}/{VertexRequests} hits; height cache {HeightHits}/{HeightRequests}; cut-edge cache {IntersectionHits}/{IntersectionRequests}; input triangles {TriangleRequests}");
                string allocation=AllocationAvailable?$"{AllocatedBytes/1048576.0:F2} MiB allocated on calling thread":"unavailable (allocation counter returned an invalid or zero delta)";
                text.AppendLine($"Emit memory: {allocation}; output array payload {OutputPayloadBytes/1048576.0:F2} MiB (not total allocation); GC delta {Gen0}/{Gen1}/{Gen2} (process-wide counts, not pause time)");
                text.AppendLine($"Emit samples: {SampledCells}/{Cells} cells, {Milliseconds(SampleTicks):F3} ms measured; exclusive parts of sampled cells only, NOT whole-chunk estimates (includes timer overhead):");
                var names=new[]{"cell traversal / diagnostics","vertex keys + lookup","new vertices + UV","height cache + callback","stitch lookup","diagonal policy","transition triangulation","triangle clipping / cut flags","cut intersections","winding + indices"};
                for(int i=0;i<ticks.Length;i++)
                    text.AppendLine($"  {names[i]}: {Milliseconds(ticks[i]):F3} ms ({(SampleTicks>0?100.0*ticks[i]/SampleTicks:0):F1}%)");
                return text.ToString().TrimEnd();
            }
        }
        // This emitter uses U = X/worldWidth, V = Z/worldDepth (positive sizes).
        // At constant V, the surface tangent is (1, -Nx/Ny, 0). Derive it from
        // the FINAL shared normal, not per-triangle UV determinants: tiny cut
        // triangles can make RecalculateTangents choose inconsistent mirror signs.
        // Only for our XZ heightfield; arbitrary rock UVs still use Unity tangents.
        public static Vector4[] TerrainTangents(Vector3[] normals)
        {
            var result=new Vector4[normals.Length];
            for(int i=0;i<normals.Length;i++)
            {
                var n=normals[i];
                float side=n.y<0?-1:1;
                var t=new Vector3(n.y*side,-n.x*side,0);
                float lengthSquared=t.sqrMagnitude;
                if(lengthSquared>1e-20f && !float.IsInfinity(lengthSquared))t/=Mathf.Sqrt(lengthSquared);
                else t=Vector3.right; // zero normal / vertical Z plane: deterministic limiting frame
                // Up x Right = -Forward; V grows toward +Z, hence w=-1 for upward terrain.
                result[i]=new Vector4(t.x,t.y,t.z,-side);
            }
            return result;
        }
        // Fixed coordinate precision only, NOT a density derived from Cells Per Chunk.
        const int N=1<<24;
        public sealed class Zone
        {
            public Rect bounds; public Matrix4x4 inverse;public Vector2 size;
            public LTStampShape shape;public float cellSize,edgeCellSize;
            public Func<float,float,float> customCellSize;
            // Conservative footprint query for masks: a small bright island must
            // refine a cell even when the cell centre falls on black texels.
            public Func<Rect,float> minimumCellSize;
            public Func<Rect,bool> coverageIntersects;
            public float CellSizeForRegion(Rect r)=>minimumCellSize!=null?minimumCellSize(r):CellSizeAt(r.center.x,r.center.y);
            public float CellSizeAt(float x,float z)
            {
                if(customCellSize!=null)return customCellSize(x,z);
                if(edgeCellSize<=0)return cellSize;
                var p=inverse.MultiplyPoint3x4(new Vector3(x,0,z));
                float u=p.x/(size.x*.5f),v=p.z/(size.y*.5f);
                float radius=shape==LTStampShape.Rectangle?Mathf.Max(Mathf.Abs(u),Mathf.Abs(v)):Mathf.Sqrt(u*u+v*v);
                // Finish the density transition before the footprint boundary.
                // The outer 20% stays at edge density, preventing a refined ring
                // when intersecting cells are balanced against the world mesh.
                float t=Mathf.Clamp01(radius/.8f);
                t=t*t*(3f-2f*t);
                return Mathf.Lerp(cellSize,edgeCellSize,t);
            }
            public bool Contains(float x,float z)
            {
                if(coverageIntersects!=null)return coverageIntersects(new Rect(x,z,0,0));
                var p=inverse.MultiplyPoint3x4(new Vector3(x,0,z));
                float u=p.x/(size.x*.5f),v=p.z/(size.y*.5f);
                return shape==LTStampShape.Rectangle?Mathf.Abs(u)<=1&&Mathf.Abs(v)<=1:u*u+v*v<=1;
            }
            public bool Intersects(Rect r)
            {
                if(!Overlap(bounds,r))return false;
                if(coverageIntersects!=null)return coverageIntersects(r);
                float minX=float.MaxValue,minZ=float.MaxValue,maxX=float.MinValue,maxZ=float.MinValue;
                for(int i=0;i<4;i++)
                {
                    var p=inverse.MultiplyPoint3x4(new Vector3((i&1)==0?r.xMin:r.xMax,0,(i&2)==0?r.yMin:r.yMax));
                    minX=Mathf.Min(minX,p.x);maxX=Mathf.Max(maxX,p.x);minZ=Mathf.Min(minZ,p.z);maxZ=Mathf.Max(maxZ,p.z);
                }
                if(shape==LTStampShape.Rectangle)return minX<=size.x*.5f&&maxX>=-size.x*.5f&&minZ<=size.y*.5f&&maxZ>=-size.y*.5f;
                float x=Mathf.Clamp(0,minX,maxX)/(size.x*.5f),z=Mathf.Clamp(0,minZ,maxZ)/(size.y*.5f);
                return x*x+z*z<=1;
            }
            public bool Covers(Rect r)=>coverageIntersects==null&&Contains(r.xMin,r.yMin)&&Contains(r.xMax,r.yMin)&&Contains(r.xMin,r.yMax)&&Contains(r.xMax,r.yMax);
        }
        public static bool Overlap(Rect a,Rect b)=>a.xMin<=b.xMax&&a.xMax>=b.xMin&&a.yMin<=b.yMax&&a.yMax>=b.yMin;
        public static int Depth(float extent,float cell)
        {
            if(float.IsNaN(cell)||float.IsInfinity(cell)||cell<=0)throw new InvalidOperationException("Density Cell Size must be a finite positive number.");
            double d=Math.Max(0,Math.Ceiling(Math.Log(extent/cell,2)-1e-9));
            if(d>23)throw new InvalidOperationException("Requested density exceeds float coordinate precision for this chunk size. Use smaller chunks.");
            return (int)d;
        }
        public static int RegionDepth(Rect region,int baseDepth,List<Zone> zones)
        {
            int depth=baseDepth;
            foreach(var zone in zones)if(zone.Intersects(region))depth=Math.Max(depth,Depth(Mathf.Max(region.width,region.height),zone.cellSize));
            return depth;
        }
        struct Leaf {public int x,z,w;public Leaf(int a,int b,int c){x=a;z=b;w=c;}}
        sealed class Builder
        {
            public Rect rect;public Vector2 worldSize;public int baseDepth,vertexBudget;public float error;
            public bool adaptive;public List<Zone> zones;public LTBalancedForest forest;public int chunk;
            public Func<float,float,float> evaluate;
            public Func<float,float,bool> cut;
            public LTRoadGeometryMath.Field asphaltPits;
            LTRoadGeometryMesh asphaltMesh;
            public LTRoadInsetMesh.Field insetRoads;
            LTRoadInsetMesh insetMesh;
            bool insetCell,addingRoad;
            public Func<Rect,bool> transitionDiagonals;
            public LTLODMesh.BuildProgress progress;
            public EmitDiagnostics diagnostics;
            bool profileCell;
            public int forcedMask=-1;
            int cellMask=-1;
            public Func<Vector3Int,int,bool> midpoint;
            bool Midpoint(Vector3Int p,int side)
            {
                using var timing=profileCell?diagnostics.Measure(EmitDiagnostics.Part.Stitches):default;
                return forcedMask>=0?(forcedMask&(1<<side))!=0:
                    cellMask>=0?(cellMask&(1<<side))!=0:
                    midpoint!=null?midpoint(p,side):forest.Midpoint(chunk,p,side);
            }
            public readonly List<Vector3> vertices=new List<Vector3>(),normals=new List<Vector3>();
            public readonly List<Vector2> uv=new List<Vector2>();public readonly List<int> triangles=new List<int>();
            readonly List<Leaf> leaves=new List<Leaf>();
            int visits;
            readonly Dictionary<Vector2Int,float> heights=new Dictionary<Vector2Int,float>();
            readonly Dictionary<Vector2Int,int> ids=new Dictionary<Vector2Int,int>();
            // Scratch storage belongs to this builder, never to another build or
            // a background job. Reuse it across cells/triangles/spatial variants.
            readonly List<int> cellEdge=new List<int>(8),clippedTriangle=new List<int>(4);
            readonly Dictionary<int,bool> cutVertices=new Dictionary<int,bool>();
            // Builder-local: shared triangle edges use the same intersection.
            // A new Emit always gets a fresh cache after any stamp change.
            readonly Dictionary<long,int> cutIntersections=new Dictionary<long,int>();
            float X(float x)=>(float)(rect.xMin+(double)x/N*rect.width);
            float Z(float z)=>(float)(rect.yMin+(double)z/N*rect.height);
            float H(float x,float z)
            {
                using var timing=profileCell?diagnostics.Measure(EmitDiagnostics.Part.Height):default;
                if(diagnostics!=null)diagnostics.HeightRequests++;
                var key=new Vector2Int(Mathf.RoundToInt(x*2),Mathf.RoundToInt(z*2));
                if(!heights.TryGetValue(key,out float h))
                {
                    if(heights.Count>vertexBudget*4L)throw new InvalidOperationException("Height sample budget exceeded. Reduce the density area or raise Max Vertices Per Chunk.");
                    heights[key]=h=evaluate(X(x),Z(z));
                }
                else if(diagnostics!=null)diagnostics.HeightHits++;
                return h;
            }
            bool Fits(int x,int z,int w)
            {
                float a=H(x,z),sx=(H(x+w,z)-a)/w,sz=(H(x,z+w)-a)/w;
                int step=Math.Max(1,Math.Min(w/2,N>>baseDepth));
                for(int j=z;j<=z+w;j+=step)for(int i=x;i<=x+w;i+=step)
                    if(Mathf.Abs(H(i,j)-(a+(i-x)*sx+(j-z)*sz))>error*.5f)return false;
                return true;
            }
            void Split(int x,int z,int w,int depth)
            {
                visits++;
                if((visits&4095)==0)
                    progress.Report("Building requested density; Cancel preserves the previous mesh.",Mathf.Min(.99f,leaves.Count/(float)vertexBudget));
                if(leaves.Count>vertexBudget)throw new InvalidOperationException("Mesh budget exceeded before generation. The requested Cell Size was NOT clamped. Reduce the area or raise Max Vertices Per Chunk.");
                var r=new Rect(X(x),Z(z),w/(float)N*rect.width,w/(float)N*rect.height);
                Zone full=null;float partialStep=float.PositiveInfinity;
                for(int i=zones.Count-1;i>=0;i--)
                {
                    var zone=zones[i];if(!zone.Intersects(r))continue;
                    if(zone.Covers(r)){full=zone;break;}
                    partialStep=Mathf.Min(partialStep,zone.CellSizeForRegion(r));
                }
                float extent=Mathf.Max(r.width,r.height);bool split;
                if(!float.IsPositiveInfinity(partialStep))
                {
                    // Resolve the footprint boundary at the requested scale.
                        float target=Mathf.Min(partialStep,full!=null?full.CellSizeForRegion(r):Mathf.Max(rect.width,rect.height)/(1<<baseDepth));
                    split=extent>target*1.00001f;
                    if(!split)
                    {
                        Zone centre=null;
                        for(int i=zones.Count-1;i>=0;i--)if(zones[i].Contains(r.center.x,r.center.y)){centre=zones[i];break;}
                        split=(centre!=null&&extent>centre.CellSizeForRegion(r)*1.00001f)||depth<baseDepth&&(!adaptive||!Fits(x,z,w));
                    }
                }
                else if(full!=null)split=extent>full.CellSizeForRegion(r)*1.00001f||depth<baseDepth&&(!adaptive||!Fits(x,z,w));
                else split=depth<baseDepth&&(!adaptive||!Fits(x,z,w));
                if(!split){leaves.Add(new Leaf(x,z,w));return;}
                if(depth>=23)throw new InvalidOperationException("Density exceeds coordinate precision; no silently reduced resolution was generated.");
                int half=w/2;Split(x,z,half,depth+1);Split(x+half,z,half,depth+1);Split(x,z+half,half,depth+1);Split(x+half,z+half,half,depth+1);
            }
            int Vertex(float x,float z)
            {
                if(diagnostics!=null)diagnostics.VertexRequests++;
                Vector2Int key;int id;
                using(profileCell?diagnostics.Measure(EmitDiagnostics.Part.VertexLookup):default)
                {
                    key=new Vector2Int(Mathf.RoundToInt(x*2),Mathf.RoundToInt(z*2));
                    if(ids.TryGetValue(key,out id)){if(diagnostics!=null)diagnostics.VertexHits++;return id;}
                }
                using var timing=profileCell?diagnostics.Measure(EmitDiagnostics.Part.VertexCreate):default;
                if((vertices.Count&4095)==0)
                    progress.Report("Creating mesh vertices; Cancel preserves the previous mesh.",Mathf.Min(.99f,vertices.Count/(float)vertexBudget));
                if(vertices.Count>=vertexBudget)throw new InvalidOperationException("Vertex budget exceeded. Previous mesh is preserved; raise Max Vertices Per Chunk or reduce the area.");
                id=vertices.Count;ids[key]=id;float gx=X(x),gz=Z(z);
                vertices.Add(new Vector3(x/N*rect.width,H(x,z),z/N*rect.height));
                // Final normals are calculated from the completed triangle mesh.
                normals.Add(Vector3.up);uv.Add(new Vector2(gx/worldSize.x,gz/worldSize.y));return id;
            }
            void Tri(int a,int b,int c)
            {
                using var timing=profileCell?diagnostics.Measure(EmitDiagnostics.Part.Clip):default;
                if(diagnostics!=null)diagnostics.TriangleRequests++;
                if(cut==null){RawTri(a,b,c);return;}
                var output=clippedTriangle;output.Clear();
                int previous=c;bool previousCut=IsCutVertex(previous);
                for(int corner=0;corner<3;corner++)
                {
                    int current=corner==0?a:corner==1?b:c;
                    bool currentCut=IsCutVertex(current);
                    if(currentCut!=previousCut)AddUnique(output,CutIntersection(previous,current,previousCut));
                    if(!currentCut)AddUnique(output,current);
                    previous=current;previousCut=currentCut;
                }
                if(output.Count>1&&output[0]==output[output.Count-1])output.RemoveAt(output.Count-1);
                for(int i=1;i+1<output.Count;i++)RawTri(output[0],output[i],output[i+1]);
            }
            static void AddUnique(List<int> values,int value)
            {
                if(values.Count==0||values[values.Count-1]!=value)values.Add(value);
            }
            int CutIntersection(int a,int b,bool aIsCut)
            {
                using var timing=profileCell?diagnostics.Measure(EmitDiagnostics.Part.Intersection):default;
                if(diagnostics!=null)diagnostics.IntersectionRequests++;
                long edgeKey=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
                if(cutIntersections.TryGetValue(edgeKey,out int intersection))
                {if(diagnostics!=null)diagnostics.IntersectionHits++;return intersection;}
                Vector3 inside=aIsCut?vertices[a]:vertices[b],outside=aIsCut?vertices[b]:vertices[a];
                // Binary search the implicit rock/terrain intersection in XZ. The
                // final vertex is kept just outside the cut and receives a freshly
                // evaluated terrain height instead of a stretched linear height.
                for(int i=0;i<18;i++)
                {
                    Vector3 middle=(inside+outside)*.5f;
                    if(cut(rect.xMin+middle.x,rect.yMin+middle.z))inside=middle;else outside=middle;
                }
                float fx=outside.x/Mathf.Max(.000001f,rect.width)*N;
                float fz=outside.z/Mathf.Max(.000001f,rect.height)*N;
                intersection=addingRoad?insetMesh.CutVertex(outside):Vertex(fx,fz);
                cutIntersections.Add(edgeKey,intersection);
                return intersection;
            }
            void RawTri(int a,int b,int c)
            {
                using var timing=profileCell?diagnostics.Measure(EmitDiagnostics.Part.Indices):default;
                if(a==b||b==c||c==a)return;
                // Every generated triangle belongs to an XZ heightfield and must
                // face upward. Correct winding here for regular quads, stitched
                // boundaries and centre fans alike.
                Vector3 ab=vertices[b]-vertices[a],ac=vertices[c]-vertices[a];
                if(Vector3.Cross(ab,ac).y<0){int swap=b;b=c;c=swap;}
                if(insetCell)
                {
                    if(addingRoad)insetMesh.AcceptRoad(a,b,c);else insetMesh.Land(a,b,c);
                    return;
                }
                AcceptedTri(a,b,c);
            }
            void AcceptedTri(int a,int b,int c)
            {
                if(asphaltPits!=null&&asphaltPits.Count>0)
                {
                    asphaltMesh??=new LTRoadGeometryMesh(asphaltPits,rect,worldSize,vertexBudget,vertices,normals,uv,triangles);
                    if(asphaltMesh.Emit(a,b,c))return;
                }
                triangles.Add(a);triangles.Add(b);triangles.Add(c);
            }
            void FinishInset()
            {
                if(!insetCell)return;
                addingRoad=true;
                try{insetMesh.Road(Tri);insetMesh.Finish(AcceptedTri);}
                finally{addingRoad=false;insetCell=false;}
            }
            bool IsCutVertex(int id)
            {
                if(cutVertices.TryGetValue(id,out bool result))return result;
                result=cut(rect.xMin+vertices[id].x,rect.yMin+vertices[id].z);
                cutVertices[id]=result;return result;
            }
            public List<Vector3Int> MakePlan()
            {
                Split(0,0,N,0);
                var result=new List<Vector3Int>(leaves.Count);
                foreach(var l in leaves)result.Add(new Vector3Int(l.x,l.z,l.w));return result;
            }
            public void Emit(List<Vector3Int> plan,LTBalancedForest.StitchSnapshot stitches=null)
            {
                int cellIndex=0;
                foreach(var p in plan)
                {
                    profileCell=diagnostics!=null&&diagnostics.BeginCell(plan.Count);
                    cellMask=stitches!=null?stitches.Mask(cellIndex):-1;cellIndex++;
                    try
                    {
                        int x=p.x,z=p.y,w=p.z;
                        if(insetRoads!=null)
                        {
                            insetMesh??=new LTRoadInsetMesh(insetRoads,rect,worldSize,vertexBudget,vertices,normals,uv,evaluate);
                            insetCell=insetMesh.Begin(new Rect(X(x),Z(z),w/(float)N*rect.width,w/(float)N*rect.height));
                        }
                        // Clockwise boundary with at most one midpoint per edge.
                        var edge=cellEdge;edge.Clear();edge.Add(Vertex(x,z));
                        if(Midpoint(p,0))edge.Add(Vertex(x,z+w*.5f));
                        edge.Add(Vertex(x,z+w));
                        if(Midpoint(p,3))edge.Add(Vertex(x+w*.5f,z+w));
                        edge.Add(Vertex(x+w,z+w));
                        if(Midpoint(p,1))edge.Add(Vertex(x+w,z+w*.5f));
                        edge.Add(Vertex(x+w,z));
                        if(Midpoint(p,2))edge.Add(Vertex(x+w*.5f,z));
                        if(insetCell)insetMesh.Boundary(edge);
                        bool regularTopology;
                        using(profileCell?diagnostics.Measure(EmitDiagnostics.Part.Topology):default)
                        {
                            var region=new Rect(X(x),Z(z),w/(float)N*rect.width,w/(float)N*rect.height);
                            regularTopology=transitionDiagonals!=null&&transitionDiagonals(region);
                        }
                        if(edge.Count==4)
                        {
                            // The regular-topology prototype removes checkerboard
                            // eight-way hubs, not just centre fans in stitched cells.
                            int cellX=x/Math.Max(1,w),cellZ=z/Math.Max(1,w);
                            if(LTPaintMath.UseRegularCellDiagonal(cellX,cellZ,regularTopology)){Tri(edge[0],edge[1],edge[3]);Tri(edge[3],edge[1],edge[2]);}
                            else {Tri(edge[0],edge[1],edge[2]);Tri(edge[2],edge[3],edge[0]);}
                        }
                        else
                        {
                            if(regularTopology)
                            {
                                // Same boundary vertices: shared edges and balanced-forest
                                // stitching remain unchanged. Only internal diagonals differ.
                                int[] topology;
                                using(profileCell?diagnostics.Measure(EmitDiagnostics.Part.Triangulate):default)
                                {
                                    var polygon=new Vector2[edge.Count];
                                    for(int i=0;i<edge.Count;i++)polygon[i]=new Vector2(vertices[edge[i]].x,vertices[edge[i]].z);
                                    topology=LTPaintMath.TriangulateTransition(polygon);
                                }
                                if(topology.Length>0)
                                {
                                    for(int i=0;i<topology.Length;i+=3)Tri(edge[topology[i]],edge[topology[i+1]],edge[topology[i+2]]);
                                    FinishInset();
                                    continue;
                                }
                            }
                            int centre=Vertex(x+w*.5f,z+w*.5f);
                            for(int i=0;i<edge.Count;i++)Tri(centre,edge[i],edge[(i+1)%edge.Count]);
                        }
                        FinishInset();
                    }
                    finally
                    {
                        if(profileCell)diagnostics.EndCell();
                        profileCell=false;
                    }
                }
            }
            public void CompactCutVertices()
            {
                if(cut==null&&asphaltPits==null&&insetRoads==null)return;
                var remap=new int[vertices.Count];
                for(int i=0;i<remap.Length;i++)remap[i]=-1;
                var compactVertices=new List<Vector3>();
                var compactNormals=new List<Vector3>();
                var compactUv=new List<Vector2>();
                for(int i=0;i<triangles.Count;i++)
                {
                    int old=triangles[i],mapped=remap[old];
                    if(mapped<0)
                    {
                        mapped=compactVertices.Count;remap[old]=mapped;
                        compactVertices.Add(vertices[old]);compactNormals.Add(normals[old]);compactUv.Add(uv[old]);
                    }
                    triangles[i]=mapped;
                }
                vertices.Clear();vertices.AddRange(compactVertices);
                normals.Clear();normals.AddRange(compactNormals);
                uv.Clear();uv.AddRange(compactUv);
            }
        }
        public static List<Vector3Int> Plan(Rect rect,int baseCells,bool adaptive,float error,int budget,List<Zone> zones,Func<float,float,float> evaluate,LTLODMesh.BuildProgress progress=null)
        {
            var b=new Builder{rect=rect,baseDepth=Depth(1,1f/baseCells),adaptive=adaptive,error=error,vertexBudget=budget,zones=zones,evaluate=evaluate,progress=progress??new LTLODMesh.BuildProgress()};return b.MakePlan();
        }
        public static void Emit(Rect rect,Vector2 worldSize,int budget,LTBalancedForest forest,int chunk,List<Vector3Int> plan,Func<float,float,float> evaluate,
            out Vector3[] v,out Vector3[] normals,out Vector2[] uv,out int[] triangles)
        {
            Emit(rect,worldSize,budget,forest,chunk,plan,evaluate,null,out v,out normals,out uv,out triangles);
        }
        public static void Emit(Rect rect,Vector2 worldSize,int budget,LTBalancedForest forest,int chunk,List<Vector3Int> plan,Func<float,float,float> evaluate,Func<float,float,bool> cut,
            out Vector3[] v,out Vector3[] normals,out Vector2[] uv,out int[] triangles,Func<Rect,bool> transitionDiagonals=null,LTLODMesh.BuildProgress progress=null,Func<Vector3Int,int,bool> midpoint=null,EmitDiagnostics diagnostics=null,LTBalancedForest.StitchSnapshot stitches=null,LTRoadGeometryMath.Field asphaltPits=null,LTRoadInsetMesh.Field insetRoads=null)
        {
            diagnostics?.Begin();bool completed=false;
            try
            {
                long phase=diagnostics!=null?Stopwatch.GetTimestamp():0;
                if(stitches!=null)
                {
                    if(midpoint!=null)throw new ArgumentException("Specify either stitch snapshot or midpoint callback, not both.");
                    stitches.Validate(forest,chunk,plan);
                }
                var b=new Builder{rect=rect,worldSize=worldSize,vertexBudget=budget,evaluate=evaluate,cut=cut,forest=forest,chunk=chunk,transitionDiagonals=transitionDiagonals,progress=progress??new LTLODMesh.BuildProgress(),midpoint=midpoint,diagnostics=diagnostics};
                if(diagnostics!=null){long now=Stopwatch.GetTimestamp();diagnostics.SetupTicks=now-phase;phase=now;}
                b.asphaltPits=insetRoads?.damage??asphaltPits;b.insetRoads=insetRoads;b.Emit(plan,stitches);
                if(diagnostics!=null){long now=Stopwatch.GetTimestamp();diagnostics.CellTicks=now-phase;phase=now;diagnostics.VerticesBeforeCompact=b.vertices.Count;}
                b.CompactCutVertices();
                if(diagnostics!=null){long now=Stopwatch.GetTimestamp();diagnostics.CompactTicks=now-phase;phase=now;}
                v=b.vertices.ToArray();normals=b.normals.ToArray();uv=b.uv.ToArray();triangles=b.triangles.ToArray();
                if(diagnostics!=null){diagnostics.ArrayTicks=Stopwatch.GetTimestamp()-phase;diagnostics.OutputVertices=v.Length;diagnostics.OutputIndices=triangles.Length;}
                completed=true;
            }
            finally{diagnostics?.Finish(completed);}
        }
        public static void EmitSpatialVariants(LTSpatialLODMath.Output data,Rect rect,Vector2 worldSize,int budget,
            Func<float,float,float> evaluate,Func<float,float,bool> cut,Func<Rect,bool> transitionDiagonals=null,LTLODMesh.BuildProgress progress=null,LTRoadGeometryMath.Field asphaltPits=null,LTRoadInsetMesh.Field insetRoads=null)
        {
            foreach(int step in EmitSpatialVariantsSteps(data,rect,worldSize,budget,evaluate,cut,transitionDiagonals,progress,asphaltPits:asphaltPits,insetRoads:insetRoads)){}
        }
        // Caller owns data until completion. Work is bounded by cells/variants/
        // indices, not a hard time cap on an individual height or cut callback.
        public static IEnumerable<int> EmitSpatialVariantsSteps(LTSpatialLODMath.Output data,Rect rect,Vector2 worldSize,int budget,
            Func<float,float,float> evaluate,Func<float,float,bool> cut,Func<Rect,bool> transitionDiagonals=null,LTLODMesh.BuildProgress progress=null,int workPerStep=256,LTRoadGeometryMath.Field asphaltPits=null,LTRoadInsetMesh.Field insetRoads=null)
        {
            if(workPerStep<1)throw new ArgumentOutOfRangeException(nameof(workPerStep));
            int work=0;
            var b=new Builder{rect=rect,worldSize=worldSize,vertexBudget=budget,evaluate=evaluate,cut=cut,transitionDiagonals=transitionDiagonals,progress=progress??new LTLODMesh.BuildProgress()};
            b.asphaltPits=insetRoads?.damage??asphaltPits;b.insetRoads=insetRoads;
            var single=new List<Vector3Int>(1){default};long indexCount=0;
            // Seed shared cut intersections in precisely LOD0's emission order. Unused
            // coarse variants must not win a quantized vertex-cache collision at a rim.
            foreach(int id in data.baseCellOrder)
            {
                var c=data.cells[id];single[0]=new Vector3Int(c.x,c.z,c.size);
                b.forcedMask=c.possibleMask;b.Emit(single);
                if(++work%workPerStep==0)yield return work;
            }
            var baseIndices=b.triangles.ToArray();
            for(int id=0;id<data.cells.Length;id++)
            {
                // Ancestors above every requested level are search-only nodes. In
                // particular, never bake unreachable coarse triangles through cut rims.
                if(!data.renderable[id])continue;
                var c=data.cells[id];single[0]=new Vector3Int(c.x,c.z,c.size);
                c.variants=new LTSpatialLODVariant[LTSpatialLODMath.VariantIndex(c.possibleMask,c.possibleMask)+1];
                for(int mask=0;mask<16;mask++)if((mask&~c.possibleMask)==0)
                {
                    b.forcedMask=mask;b.triangles.Clear();b.Emit(single);
                    indexCount+=b.triangles.Count;
                    if(indexCount>budget*192L)throw new InvalidOperationException("Spatial LOD index budget exceeded. Previous meshes preserved.");
                    c.variants[LTSpatialLODMath.VariantIndex(mask,c.possibleMask)]=new LTSpatialLODVariant{indices=b.triangles.ToArray()};
                    if(++work%workPerStep==0)yield return work;
                }
                data.cells[id]=c;
            }
            var initial=new List<int>();
            for(int p=0;p<data.patches.Length;p++)
            {
                float minY=float.PositiveInfinity,maxY=float.NegativeInfinity;
                foreach(int id in data.patches[p].levels[0].cells)
                {
                    var c=data.cells[id];var indices=c.variants[LTSpatialLODMath.VariantIndex(c.possibleMask,c.possibleMask)].indices;
                    initial.AddRange(indices);
                    foreach(int v in indices){minY=Math.Min(minY,b.vertices[v].y);maxY=Math.Max(maxY,b.vertices[v].y);if(++work%workPerStep==0)yield return work;}
                }
                if(float.IsInfinity(minY))minY=maxY=0;
                float width=rect.width/data.divisions,depth=rect.height/data.divisions;
                data.patches[p].bounds=new Bounds(new Vector3((p%data.divisions+.5f)*width,(minY+maxY)*.5f,(p/data.divisions+.5f)*depth),new Vector3(width,maxY-minY,depth));
            }
            var outputVertices=b.vertices.ToArray();var outputUV=b.uv.ToArray();
            // LOD-independent normals from LOD0, including future coarse-cell corners.
            // The upload step overlays the world's authoritative seam/bridge normals.
            var normals=new Vector3[outputVertices.Length];
            for(int i=0;i<initial.Count;i+=3)
            {
                int a=initial[i],d=initial[i+1],c=initial[i+2];
                var n=Vector3.Cross(outputVertices[d]-outputVertices[a],outputVertices[c]-outputVertices[a]);
                normals[a]+=n;normals[d]+=n;normals[c]+=n;
                if(++work%workPerStep==0)yield return work;
            }
            for(int i=0;i<normals.Length;i++)
            {normals[i]=Math.Abs(b.normals[i].y)<.000001f?b.normals[i]:normals[i].sqrMagnitude>1e-20f?normals[i].normalized:Vector3.up;if(++work%workPerStep==0)yield return work;}
            data.vertices=outputVertices;data.uv=outputUV;data.baseIndices=baseIndices;data.normals=normals;
        }
        public static void Build(Rect rect,Vector2 worldSize,int baseCells,bool adaptive,float error,int budget,List<Zone> zones,int[] edges,
            Func<float,float,float> evaluate,out Vector3[] v,out Vector3[] normals,out Vector2[] uv,out int[] triangles)
        {
            try
            {
                var raw=Plan(rect,baseCells,adaptive,error,budget,zones,evaluate);
                var forest=new LTBalancedForest(1,1,budget,new Dictionary<int,List<Vector3Int>>{{0,raw}});forest.Balance();
                Emit(rect,worldSize,budget,forest,0,forest.Plan(0),evaluate,out v,out normals,out uv,out triangles);
            }
            finally{EditorUtility.ClearProgressBar();}
        }
    }
}
