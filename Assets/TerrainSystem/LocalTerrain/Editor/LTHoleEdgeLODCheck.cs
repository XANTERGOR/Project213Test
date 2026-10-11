using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTHoleEdgeLODCheck
    {
        const int Budget=150000;
        static LTHoleStampSnapshot Hole(Vector2 centre,Vector2 size,LTStampShape shape,float angle=0,float edge=.125f,float lod=4)
        {
            float c=(float)Math.Cos(angle*Math.PI/180),s=(float)Math.Sin(angle*Math.PI/180);
            var m=Matrix4x4.identity;m.m00=c;m.m02=s;m.m20=-s;m.m22=c;m.m03=centre.x;m.m23=centre.y;
            var inv=Matrix4x4.identity;inv.m00=c;inv.m02=-s;inv.m20=s;inv.m22=c;
            inv.m03=-c*centre.x+s*centre.y;inv.m23=-s*centre.x-c*centre.y;
            return new LTHoleStampSnapshot(m,inv,size,shape,edge,lodEdgeCellSize:lod);
        }
        internal static string Run(Action<Vector3[],int[],Vector2> checkMesh=null)
        {
            CheckLimits();
            var centre=new Vector2(16.13f,15.77f);
            var cases=new[]{
                new[]{Hole(centre,new Vector2(6,6),LTStampShape.Rectangle)},
                new[]{Hole(centre,new Vector2(6,4),LTStampShape.Rectangle,27)},
                new[]{Hole(centre,new Vector2(7.5f,4.2f),LTStampShape.Ellipse,27)},
                new[]{Hole(centre,new Vector2(.12f,.12f),LTStampShape.Ellipse,edge:2)},
                new[]{Hole(centre,new Vector2(4,4),LTStampShape.Ellipse),Hole(centre+new Vector2(2,0),new Vector2(4,4),LTStampShape.Ellipse)},
                new[]{Hole(centre-new Vector2(2.1f,0),new Vector2(4,4),LTStampShape.Rectangle),Hole(centre+new Vector2(2.1f,0),new Vector2(4,4),LTStampShape.Rectangle)},
                new[]{Hole(centre,new Vector2(20.4432f,20.4432f),LTStampShape.Rectangle,edge:15,lod:20)},
                new[]{Hole(new Vector2(248.05f,109.14f),new Vector2(20.4432f,20.4432f),LTStampShape.Rectangle,edge:15,lod:20)}
            };
            int comparisons=0;var messages=new List<string>();
            for(int fixture=0;fixture<cases.Length;fixture++)
            {
                float width=fixture==7?256:16,extent=width*2;int divisions=fixture==7?4:2,baseCells=fixture==7?128:16;
                var rayPoint=fixture==7?new Vector2(248.05f,109.14f):fixture==5?centre-new Vector2(2.1f,0):centre;
                var holes=cases[fixture];bool Cut(float x,float z)=>holes.Any(h=>h.Contains(x,z));
                bool Protect(Rect r)=>LTHoleStampSnapshot.ProtectsAnyLOD(holes,r);
                float Height(float x,float z)=>.01f*x+.02f*z;
                Rect Region(int id)=>new Rect(id%2*width,id/2*width,width,width);
                Rect Area(int id,Vector3Int p){var r=Region(id);return new Rect(r.x+p.x/(float)LTSpatialLODMath.N*width,r.y+p.y/(float)LTSpatialLODMath.N*width,p.z/(float)LTSpatialLODMath.N*width,p.z/(float)LTSpatialLODMath.N*width);}
                var plans=new Dictionary<int,List<Vector3Int>>();var progress=new LTLODMesh.BuildProgress(false);
                var zones=holes.Select(h=>h.CreateZone()).ToList();
                for(int id=0;id<4;id++)plans[id]=LTSpatialLODMath.PartitionLeaves(LTStampMesh.Plan(Region(id),baseCells,false,.1f,Budget,zones,Height,progress),divisions);
                var forest=new LTBalancedForest(2,2,Budget,plans);forest.Balance();
                var outputs=new LTSpatialLODMath.Output[4];var previews=new LTSpatialLODMath.Output[4];
                for(int id=0;id<4;id++)
                {
                    int chunk=id;var fine=forest.Plan(id);var r=Region(id);var prep=new LTLODMesh.Preparation(fine,r,Height,progress);
                    var levels=new List<Vector3Int>[5];levels[0]=fine;
                    for(int l=1;l<5;l++)levels[l]=prep.Coarsen(l,10,null,Protect,divisions,false);
                    int Mask(Vector3Int p){int mask=0;for(int side=0;side<4;side++)if(forest.Midpoint(chunk,p,side))mask|=1<<side;return mask;}
                    bool Preserve(Vector3Int p)=>Protect(Area(chunk,p));
                    outputs[id]=LTSpatialLODMath.BuildLayout(levels,divisions,Budget,Mask,Preserve);
                    LTStampMesh.EmitSpatialVariants(outputs[id],r,new Vector2(extent,extent),Budget,Height,Cut);
                    previews[id]=LTSpatialLODMath.BuildLayout(new[]{fine},divisions,Budget,Mask,Preserve);
                    LTStampMesh.EmitSpatialVariants(previews[id],r,new Vector2(extent,extent),Budget,Height,Cut);
                }
                LTSpatialLODTopology Topology(LTSpatialLODMath.Output[] data)=>new LTSpatialLODTopology(2,2,divisions,data.Select(d=>new LTSpatialLODTopology.Chunk{cells=d.cells,patches=d.patches}).ToArray());
                var topology=Topology(outputs);topology.Update();
                var baseline=Read(outputs,topology,width);var reference=Rim(baseline.v,baseline.i,extent);
                int loops=Loops(reference),nearRing=RingTriangles(baseline.v,baseline.i,holes[0].ProtectedBounds),farRing=0,farSegments=0;
                Require(loops==(fixture==5?2:1),"fixture has the expected open holes");
                float tolerance=fixture==3?.0001f:holes.Max(h=>Mathf.Min(h.LODEdgeCellSize*.5f,Mathf.Sqrt(2)*h.LODCornerError))+.0001f;
                float worst=0;
                void Check(LTSpatialLODMath.Output[] data,LTSpatialLODTopology selected)
                {
                    selected.Update();selected.Validate();var mesh=Read(data,selected,width);var rim=Rim(mesh.v,mesh.i,extent);
                    Require(Loops(rim)==loops,"hole disappeared, holes merged or an unintended crack appeared");
                    float deviation=Mathf.Max(Deviation(rim,reference),Deviation(reference,rim));worst=Mathf.Max(worst,deviation);
                    Require(deviation<=tolerance,$"contour deviation {deviation} exceeds {tolerance}");
                    // All boundary loops must still be at the holes, not on chunk/patch seams.
                    Require(mesh.i.Length>0,"ground outside the cut disappeared");
                    checkMesh?.Invoke(mesh.v,mesh.i,rayPoint);
                    comparisons++;
                }
                foreach(int l in new[]{4,0,2,4})
                {
                    for(int c=0;c<4;c++)for(int p=0;p<divisions*divisions;p++)topology.SetLevel(c,p,l);
                    Check(outputs,topology);
                    var mesh=Read(outputs,topology,width);
                    if(l==0)Require(mesh.i.Length==baseline.i.Length,"near LOD did not restore original detail");
                    if(l==4){farRing=RingTriangles(mesh.v,mesh.i,holes[0].ProtectedBounds);farSegments=Rim(mesh.v,mesh.i,extent).Count;}
                }
                for(int c=0;c<4;c++)for(int p=0;p<divisions*divisions;p++)topology.SetLevel(c,p,(c+p)%5);
                Check(outputs,topology);
                var staged=outputs.Select((data,c)=>c==0?previews[c]:data).ToArray();var stagedTopology=Topology(staged);
                for(int c=0;c<4;c++)for(int p=0;p<divisions*divisions;p++)stagedTopology.SetLevel(c,p,4);
                Check(staged,stagedTopology);
                if(fixture<3||fixture>=6)Require(farRing<nearRing*.7f,$"dense rim was not simplified: {nearRing}->{farRing}");
                if(fixture<3||fixture>=6)Require(farSegments<reference.Count*.7f,$"actual cut contour was not simplified: {reference.Count}->{farSegments} segments");
                messages.Add($"case {fixture}: contour segments {reference.Count}->{farSegments}, nearby triangles {nearRing}->{farRing}, deviation {worst:F5} m, {loops} hole(s)");
            }
            return $"effective caps/corner release/shear checks; {comparisons} near/far/mixed/staged checks; "+string.Join("; ",messages);
        }
        static void CheckLimits()
        {
            var rectangle=Hole(Vector2.zero,new Vector2(20.4432f,20.4432f),LTStampShape.Rectangle,edge:15,lod:20);
            Require(Mathf.Abs(rectangle.CellSize-2.5554f)<.00001f,"LOD0 sampling cap changed");
            Require(Mathf.Abs(rectangle.LODEdgeCellSize-10.2216f)<.00001f,"straight-side cap still pins distant edge");
            Require(Mathf.Abs(rectangle.LODCornerError-1)<.00001f,"corner error differs from inspector limits");
            Require(!rectangle.ProtectsLOD(new Rect(8,-2,4,4)),"straight edge cannot coarsen beyond old cap");
            Require(!rectangle.ProtectsLOD(new Rect(10,10,.5f,.5f)),"corner still pins finest cells");
            Require(!rectangle.ProtectsLOD(new Rect(9.5f,9.5f,2,2)),"accurate corner still blocked by cell size");
            Require(rectangle.ProtectsLOD(new Rect(8,8,4,4)),"inaccurate corner exceeded its error cap");
            Require(rectangle.ProtectsLOD(new Rect(0,-5,11,11)),"edge exceeded its opening-preservation cap");
            var lockedCorner=new LTHoleStampSnapshot(Matrix4x4.identity,Matrix4x4.identity,new Vector2(20.4432f,20.4432f),LTStampShape.Rectangle,15,lodEdgeCellSize:20,lodCornerError:0);
            Require(lockedCorner.ProtectsLOD(new Rect(10,10,.5f,.5f)),"zero corner error must retain original corner cells");
            foreach(float bad in new[]{float.NaN,float.PositiveInfinity,-1})
            {
                bool failed=false;
                try{new LTHoleStampSnapshot(Matrix4x4.identity,Matrix4x4.identity,new Vector2(8,8),LTStampShape.Rectangle,1,lodCornerError:bad);}
                catch(ArgumentException){failed=true;}
                Require(failed,"invalid corner error must be rejected");
            }
            var ellipse=Hole(Vector2.zero,new Vector2(20.4432f,20.4432f),LTStampShape.Ellipse,edge:15,lod:20);
            Require(Mathf.Abs(ellipse.LODEdgeCellSize-2.5554f)<.00001f,"curved rim lost its curvature guard");
            // Strong shear has long basis vectors but a narrow actual footprint.
            var matrix=Matrix4x4.identity;matrix.m02=8;
            var inverse=Matrix4x4.identity;inverse.m02=-8;
            var sheared=new LTHoleStampSnapshot(matrix,inverse,new Vector2(4,4),LTStampShape.Ellipse,15,lodEdgeCellSize:20);
            float safeSpan=(float)(4*(Math.Sqrt(17)-4));
            Require(Mathf.Abs(sheared.CellSize-safeSpan/8)<.000001f&&Mathf.Abs(sheared.LODEdgeCellSize-safeSpan/8)<.000001f,
                "sheared footprint cap must use minimum singular span");
            matrix.m22=0;bool rejected=false;
            try{new LTHoleStampSnapshot(matrix,inverse,new Vector2(4,4),LTStampShape.Rectangle,1);}
            catch(ArgumentException){rejected=true;}
            Require(rejected,"collapsed footprint must be rejected");
        }
        internal static (Vector3[] v,int[] i) Read(LTSpatialLODMath.Output[] outputs,LTSpatialLODTopology topology,float width)
        {
            var vertices=new List<Vector3>();var indices=new List<int>();var local=new List<int>();
            for(int c=0;c<4;c++)
            {
                int offset=vertices.Count;var position=new Vector3(c%2*width,0,c/2*width);
                vertices.AddRange(outputs[c].vertices.Select(v=>v+position));topology.WriteIndices(c,local);indices.AddRange(local.Select(i=>i+offset));
            }
            return(vertices.ToArray(),indices.ToArray());
        }
        static Vector2Int Key(Vector3 p)=>new Vector2Int((int)Math.Round(p.x*10000),(int)Math.Round(p.z*10000));
        internal static HashSet<(Vector2Int a,Vector2Int b)> Rim(Vector3[] vertices,int[] indices,float extent)
        {
            var edges=new Dictionary<(Vector2Int,Vector2Int),int>();
            for(int i=0;i<indices.Length;i+=3)
            {
                var a=vertices[indices[i]];var b=vertices[indices[i+1]];var c=vertices[indices[i+2]];
                Require(Vector3.Cross(b-a,c-a).y>0,"degenerate or inverted face");
                for(int side=0;side<3;side++)
                {
                    var x=Key(vertices[indices[i+side]]);var y=Key(vertices[indices[i+(side+1)%3]]);if(x==y)continue;
                    if(edges.TryGetValue((y,x),out int n)){if(n==1)edges.Remove((y,x));else edges[(y,x)]=n-1;}
                    else{edges.TryGetValue((x,y),out n);edges[(x,y)]=n+1;}
                }
            }
            var result=new HashSet<(Vector2Int,Vector2Int)>();int end=(int)(extent*10000);
            foreach(var e in edges)
            {
                Require(e.Value==1,"non-manifold boundary");var a=e.Key.Item1;var b=e.Key.Item2;
                if(a.x==b.x&&(a.x==0||a.x==end)||a.y==b.y&&(a.y==0||a.y==end))continue;
                result.Add(e.Key);
            }
            return result;
        }
        internal static int Loops(HashSet<(Vector2Int a,Vector2Int b)> edges)
        {
            var next=new Dictionary<Vector2Int,Vector2Int>();var incoming=new HashSet<Vector2Int>();
            foreach(var e in edges){Require(!next.ContainsKey(e.a)&&incoming.Add(e.b),"branched boundary / unmatched LOD seam");next.Add(e.a,e.b);}
            Require(incoming.SetEquals(next.Keys),"open boundary / LOD crack");var visited=new HashSet<Vector2Int>();int count=0;
            foreach(var p in next.Keys)if(!visited.Contains(p)){count++;var v=p;while(visited.Add(v))v=next[v];Require(v==p,"boundary loop does not close");}
            return count;
        }
        internal static float Deviation(HashSet<(Vector2Int a,Vector2Int b)> a,HashSet<(Vector2Int a,Vector2Int b)> b)
        {
            float worst=0;
            foreach(var e in a)for(int sample=0;sample<=2;sample++)
            {
                Vector2 p=Vector2.Lerp((Vector2)e.a,(Vector2)e.b,sample*.5f);float nearest=float.MaxValue;
                foreach(var f in b){Vector2 x=f.a,d=(Vector2)f.b-x;float t=Mathf.Clamp01(Vector2.Dot(p-x,d)/d.sqrMagnitude);nearest=Mathf.Min(nearest,(p-x-d*t).sqrMagnitude);}
                worst=Mathf.Max(worst,Mathf.Sqrt(nearest)*.0001f);
            }
            return worst;
        }
        static int RingTriangles(Vector3[] vertices,int[] indices,Rect region)
        {
            int count=0;for(int i=0;i<indices.Length;i+=3)
            {var p=(vertices[indices[i]]+vertices[indices[i+1]]+vertices[indices[i+2]])/3;if(region.Contains(new Vector2(p.x,p.z)))count++;}
            return count;
        }
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException("Hole edge LOD FAIL: "+message);}
    }
}
