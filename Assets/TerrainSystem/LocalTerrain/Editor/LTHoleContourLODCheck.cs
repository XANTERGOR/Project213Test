using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTHoleContourLODCheck
    {
        const int Budget=150000;
        internal static string Run(Action<Vector3[],int[],Vector2,bool> checkCollider=null,
            Action<List<Vector3Int>,Rect,Func<float,float,float>,Func<Rect,bool>> comparePlans=null)
        {
            var centre=new Vector2(16.13f,15.77f);
            LTHoleStampSnapshot Hole(Vector2 location,float radius,float error=.5f,float maxCell=4,bool nested=false,bool tilted=false)
            {
                var vertices=new List<Vector3>();var indices=new List<int>();
                void Shell(float r,float halfHeight)
                {
                    int offset=vertices.Count,n=40;
                    for(int y=0;y<2;y++)
                    {
                        for(int i=0;i<n;i++)
                        {
                            float a=i*Mathf.PI*2/n,rad=r*(1+.12f*Mathf.Sin(a*5));
                            vertices.Add(new Vector3(Mathf.Cos(a)*rad,(y==0?-1:1)*halfHeight,Mathf.Sin(a)*rad));
                        }
                        vertices.Add(new Vector3(0,(y==0?-1:1)*halfHeight,0));
                    }
                    for(int i=0;i<n;i++)
                    {
                        int a=offset+i,b=offset+(i+1)%n,c=a+n+1,d=b+n+1;
                        indices.AddRange(new[]{offset+n,a,b,offset+2*n+1,d,c,a,c,d,a,d,b});
                    }
                }
                Shell(radius,5);if(nested)Shell(radius*.5f,3);
                var matrix=Matrix4x4.identity;
                if(tilted){matrix.m11=.8f;matrix.m12=-.6f;matrix.m21=.6f;matrix.m22=.8f;}
                matrix.m03=location.x;matrix.m23=location.y;
                return new LTHoleStampSnapshot(new LTHoleMeshVolume(new LTHoleMeshData(vertices.ToArray(),indices.ToArray()),matrix),.125f,maxCell,error);
            }
            var cases=new[]{new[]{Hole(centre,3)},new[]{Hole(centre,3)},new[]{Hole(centre,.19f)},
                new[]{Hole(centre-new Vector2(2.3f,0),2),Hole(centre+new Vector2(2.3f,0),2)},
                new[]{Hole(centre,3,nested:true)},new[]{Hole(centre,3,tilted:true)},
                new[]{Hole(centre,3,error:0)},new[]{Hole(centre,3,maxCell:0)},new[]{Hole(centre,3,error:.03f)}};
            int comparisons=0;var messages=new List<string>();
            for(int fixture=0;fixture<cases.Length;fixture++)
            {
                var holes=cases[fixture];bool regular=fixture==1,slope=fixture==5;
                float Height(float x,float z)=>slope?.2f*(x-16)+.2f*Mathf.Sin(z):0;
                bool Cut(float x,float z)=>holes.Any(h=>h.Contains(x,Height(x,z),z));
                Rect Region(int id)=>new Rect(id%2*16,id/2*16,16,16);
                var progress=new LTLODMesh.BuildProgress(false);var plans=new Dictionary<int,List<Vector3Int>>();
                for(int id=0;id<4;id++)plans[id]=LTSpatialLODMath.PartitionLeaves(LTStampMesh.Plan(Region(id),16,false,.1f,Budget,holes.Select(h=>h.CreateZone()).ToList(),Height,progress),2);
                var forest=new LTBalancedForest(2,2,Budget,plans);forest.Balance();
                var outputs=new LTSpatialLODMath.Output[4];var previews=new LTSpatialLODMath.Output[4];int releases=0;
                var legacyVertices=new List<Vector3>();var legacyIndices=new List<int>();
                for(int id=0;id<4;id++)
                {
                    int chunk=id;var rect=Region(id);var fine=forest.Plan(id);
                    int Mask(Vector3Int p){int bits=0;for(int side=0;side<4;side++)if(forest.Midpoint(chunk,p,side))bits|=1<<side;return bits;}
                    Func<Rect,bool> diagonal=regular?r=>true:null;
                    var protection=new LTHoleVolumeLODProtection();
                    foreach(var _ in protection.Prepare(holes,fine,rect,Height,Mask,diagonal)){}
                    releases+=protection.ReleasedMergeCount;
                    comparePlans?.Invoke(fine,rect,Height,protection.Protects);
                    var levels=new List<Vector3Int>[5];levels[0]=fine;var prep=new LTLODMesh.Preparation(fine,rect,Height,progress);
                    for(int l=1;l<5;l++)levels[l]=prep.Coarsen(l,100,null,protection.Protects,2,false);
                    bool Preserve(Vector3Int p)=>protection.Protects(new Rect(rect.xMin+p.x/(float)LTSpatialLODMath.N*16,rect.yMin+p.y/(float)LTSpatialLODMath.N*16,
                        p.z/(float)LTSpatialLODMath.N*16,p.z/(float)LTSpatialLODMath.N*16));
                    outputs[id]=LTSpatialLODMath.BuildLayout(levels,2,Budget,Mask,Preserve);
                    LTStampMesh.EmitSpatialVariants(outputs[id],rect,new Vector2(32,32),Budget,Height,Cut,diagonal);
                    previews[id]=LTSpatialLODMath.BuildLayout(new[]{fine},2,Budget,Mask,Preserve);
                    LTStampMesh.EmitSpatialVariants(previews[id],rect,new Vector2(32,32),Budget,Height,Cut,diagonal);
                    var legacy=prep.Coarsen(4,100,null,protection.Protects,1,true);
                    var legacyForest=new LTBalancedForest(1,1,Budget,new Dictionary<int,List<Vector3Int>>{{0,legacy}});legacyForest.Balance();
                    var boundaryMasks=LTLODMesh.BoundaryMasks(fine,forest.BoundaryStitches(id,fine));
                    LTStampMesh.Emit(rect,new Vector2(32,32),Budget,legacyForest,0,legacyForest.Plan(0),Height,Cut,
                        out var v,out _,out _,out var indices,diagonal,progress,(p,s)=>LTLODMesh.LocalMidpoint(legacyForest,boundaryMasks,p,s));
                    int offset=legacyVertices.Count;
                    foreach(var index in indices)legacyIndices.Add(offset+index);
                    foreach(var vertex in v)legacyVertices.Add(vertex+new Vector3(rect.xMin,0,rect.yMin));
                }
                LTSpatialLODTopology Topology(LTSpatialLODMath.Output[] data)=>new LTSpatialLODTopology(2,2,2,data.Select(o=>new LTSpatialLODTopology.Chunk{cells=o.cells,patches=o.patches}).ToArray());
                var topology=Topology(outputs);topology.Update();var near=LTHoleEdgeLODCheck.Read(outputs,topology,16);
                var rim=LTHoleEdgeLODCheck.Rim(near.v,near.i,32);int loops=LTHoleEdgeLODCheck.Loops(rim),farSegments=0;float worst=0;
                Require(loops==(fixture==3||fixture==4?2:1),"baseline opening/bridge/island count");
                void Check(LTSpatialLODMath.Output[] data,LTSpatialLODTopology selected)
                {
                    selected.Update();selected.Validate();CheckMesh(LTHoleEdgeLODCheck.Read(data,selected,16));
                }
                void CheckMesh((Vector3[] v,int[] i) mesh)
                {
                    var actual=LTHoleEdgeLODCheck.Rim(mesh.v,mesh.i,32);
                    Require(LTHoleEdgeLODCheck.Loops(actual)==loops,"hole disappeared, bridge merged or seam opened");
                    float error=Mathf.Max(LTHoleEdgeLODCheck.Deviation(actual,rim),LTHoleEdgeLODCheck.Deviation(rim,actual));worst=Mathf.Max(worst,error);
                    Require(error<=holes[0].LODContourError+.0003f,"contour error exceeded configured tolerance");
                    if(fixture==6||fixture==7)Require(actual.SetEquals(rim),"zero setting must preserve exact contour");
                    void Probe(Vector2 p,bool hasGround)
                    {
                        Require(HasSurface(mesh.v,mesh.i,p)==hasGround,"sampled opening or bridge/island changed");
                        checkCollider?.Invoke(mesh.v,mesh.i,p,hasGround);
                    }
                    Probe(centre,!Cut(centre.x,centre.y));Probe(new Vector2(1,1),true);
                    if(fixture==3)Probe(centre-new Vector2(2.3f,0),false);
                    comparisons++;
                }
                foreach(int level in new[]{4,0,2,4})
                {
                    for(int c=0;c<4;c++)for(int p=0;p<4;p++)topology.SetLevel(c,p,level);Check(outputs,topology);
                    var mesh=LTHoleEdgeLODCheck.Read(outputs,topology,16);
                    if(level==4)farSegments=LTHoleEdgeLODCheck.Rim(mesh.v,mesh.i,32).Count;
                    if(level==0)Require(mesh.i.SequenceEqual(near.i),"LOD0 indices changed after return-near");
                }
                for(int c=0;c<4;c++)for(int p=0;p<4;p++)topology.SetLevel(c,p,(c+p)%5);Check(outputs,topology);
                var staged=outputs.Select((o,c)=>c==0?previews[c]:o).ToArray();var stagedTopology=Topology(staged);
                for(int c=0;c<4;c++)for(int p=0;p<4;p++)stagedTopology.SetLevel(c,p,4);Check(staged,stagedTopology);
                CheckMesh((legacyVertices.ToArray(),legacyIndices.ToArray()));
                if(fixture==0||fixture==1||fixture==5)Require(releases>0&&farSegments<rim.Count*.8f,"actual mesh-hole contour did not simplify");
                messages.Add($"case {fixture}: segments {rim.Count}->{farSegments}, XZ deviation {worst:F5} m, accepted merges {releases}");
            }
            return $"{comparisons} near/far/mixed/staged/legacy checks, 4-chunk seams, regular diagonals, tiny opening, bridge/island, exact-lock settings; "+string.Join("; ",messages);
        }
        static bool HasSurface(Vector3[] vertices,int[] indices,Vector2 p)
        {
            double Side(Vector3 a,Vector3 b)=>(b.x-(double)a.x)*(p.y-(double)a.z)-(b.z-(double)a.z)*(p.x-(double)a.x);
            for(int i=0;i<indices.Length;i+=3)
            {
                var a=vertices[indices[i]];var b=vertices[indices[i+1]];var c=vertices[indices[i+2]];
                if(Side(a,b)<=1e-10&&Side(b,c)<=1e-10&&Side(c,a)<=1e-10)return true;
            }
            return false;
        }
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("Mesh hole contour LOD FAIL: "+message);}
    }
}
