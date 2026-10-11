using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTRockTerrainLODCheck
    {
#if UNITY_EDITOR
        // Archived validation menu: Tools/Local Terrain/Validate Mesh Stamp Terrain LOD (no scene changes)
        static void Menu()
        {
            try{Debug.Log("Mesh Stamp Terrain LOD v1 PASS: "+Run());}
            catch(Exception error){Debug.LogError("Mesh Stamp Terrain LOD v1 FAIL: "+error);}
        }
#endif
        internal static string Run(Action<List<Vector3Int>,Rect,Func<float,float,float>,Func<Rect,bool>> compareJobs=null)
        {
            const int budget=200000,divisions=2;const float width=16,extent=32;
            int checks=0;var summary=new List<string>();
            for(int fixture=0;fixture<7;fixture++)
            {
                var centre=fixture==0?new Vector2(8.13f,7.77f):new Vector2(16.13f,15.77f);
                var bounds=new Rect(2,2,28,28);bool regular=fixture==5;
                bool Cut(float x,float z)
                {
                    if(fixture==4)return false; // stamp projection without surface contact
                    if(fixture==6)return true; // wholly removed terrain may also coarsen
                    float dx=x-centre.x,dz=z-centre.y;
                    if(fixture==2){float u=dx*.8f+dz*.6f;dz=-dx*.6f+dz*.8f;dx=u;return dx*dx/16+dz*dz/4<1;}
                    if(fixture==3)return dx*dx+dz*dz<16&&dx*dx+dz*dz>2.25f; // retained island / cave footprint
                    return dx*dx+dz*dz<9;
                }
                float Height(float x,float z)=>.03f*x+.01f*z+.03f*Mathf.Sin(x*.5f);
                Rect Region(int id)=>new Rect(id%2*width,id/2*width,width,width);
                Rect Area(Rect r,Vector3Int p)=>new Rect(r.xMin+p.x/(float)LTSpatialLODMath.N*width,r.yMin+p.y/(float)LTSpatialLODMath.N*width,
                    p.z/(float)LTSpatialLODMath.N*width,p.z/(float)LTSpatialLODMath.N*width);
                Func<Rect,bool> diagonals=regular?(r=>true):null;
                var zone=new LTStampMesh.Zone{bounds=bounds,inverse=Matrix4x4.Translate(new Vector3(-16,0,-16)),size=new Vector2(28,28),
                    shape=LTStampShape.Rectangle,cellSize=.25f,edgeCellSize=fixture==5?1:0};
                var plans=new Dictionary<int,List<Vector3Int>>();var progress=new LTLODMesh.BuildProgress(false);
                for(int id=0;id<4;id++)plans[id]=LTSpatialLODMath.PartitionLeaves(LTStampMesh.Plan(Region(id),16,false,.1f,budget,
                    new List<LTStampMesh.Zone>{zone},Height,progress),divisions);
                var forest=new LTBalancedForest(2,2,budget,plans);forest.Balance();
                var outputs=new LTSpatialLODMath.Output[4];var previews=new LTSpatialLODMath.Output[4];
                var legacyVertices=new List<Vector3>();var legacyIndices=new List<int>();
                int fineCells=0,farCells=0,oldCells=0,protectedCells=0,edgeCells=0;
                for(int id=0;id<4;id++)
                {
                    int chunk=id;var r=Region(id);var fine=forest.Plan(id);var protection=new LTRockTerrainLODProtection();
                    var candidates=fixture==6?new[]{new Rect(0,0,32,32)}:new[]{bounds};
                    // Interrupted preparation must not publish a partial protection map.
                    using(var pending=protection.Prepare(fine,r,candidates,Cut).GetEnumerator())
                    {
                        Require(pending.MoveNext(),"fixture did not exercise staged preparation");
                        bool unpublished=false;try{protection.Protects(r);}catch(InvalidOperationException){unpublished=true;}
                        Require(unpublished,"partly prepared protection became visible");while(pending.MoveNext()){}
                    }
                    protectedCells+=protection.ProtectedCellCount;edgeCells+=protection.BoundaryCellCount;
                    compareJobs?.Invoke(fine,r,Height,protection.Protects);
                    var prep=new LTLODMesh.Preparation(fine,r,Height,progress);var levels=new List<Vector3Int>[5];levels[0]=fine;
                    for(int l=1;l<5;l++)levels[l]=prep.Coarsen(l,10,null,protection.Protects,divisions,false);
                    fineCells+=fine.Count;farCells+=levels[4].Count;oldCells+=prep.Coarsen(4,10,new List<Rect>{bounds},null,divisions,false).Count;
                    int Mask(Vector3Int p){int mask=0;for(int side=0;side<4;side++)if(forest.Midpoint(chunk,p,side))mask|=1<<side;return mask;}
                    bool Preserve(Vector3Int p)=>protection.Protects(Area(r,p));
                    outputs[id]=LTSpatialLODMath.BuildLayout(levels,divisions,budget,Mask,Preserve);
                    LTStampMesh.EmitSpatialVariants(outputs[id],r,new Vector2(extent,extent),budget,Height,Cut,diagonals);
                    previews[id]=LTSpatialLODMath.BuildLayout(new[]{fine},divisions,budget,Mask,Preserve);
                    LTStampMesh.EmitSpatialVariants(previews[id],r,new Vector2(extent,extent),budget,Height,Cut,diagonals);
                    var legacy=prep.Coarsen(4,10,null,protection.Protects,1,true);
                    var legacyForest=new LTBalancedForest(1,1,budget,new Dictionary<int,List<Vector3Int>>{{0,legacy}});legacyForest.Balance();
                    var border=LTLODMesh.BoundaryMasks(fine,forest.BoundaryStitches(chunk,fine));
                    LTStampMesh.Emit(r,new Vector2(extent,extent),budget,legacyForest,0,legacyForest.Plan(0),Height,Cut,
                        out var v,out _,out _,out var indices,diagonals,progress,(p,s)=>LTLODMesh.LocalMidpoint(legacyForest,border,p,s));
                    int offset=legacyVertices.Count;legacyVertices.AddRange(v.Select(p=>p+new Vector3(r.xMin,0,r.yMin)));legacyIndices.AddRange(indices.Select(i=>i+offset));
                }
                Require(farCells<oldCells*.6f,$"whole footprint still protected: {oldCells}->{farCells}");
                Require(protectedCells<fineCells*.4f,"protection is not a narrow contour band");
                if(fixture==4||fixture==6)Require(edgeCells==0&&protectedCells==0,"uniform cut state pins whole footprint");
                LTSpatialLODTopology Topology(LTSpatialLODMath.Output[] data)=>new LTSpatialLODTopology(2,2,divisions,
                    data.Select(o=>new LTSpatialLODTopology.Chunk{cells=o.cells,patches=o.patches}).ToArray());
                var topology=Topology(outputs);topology.Update();var reference=LTHoleEdgeLODCheck.Read(outputs,topology,width);
                var rim=LTHoleEdgeLODCheck.Rim(reference.v,reference.i,extent);
                Require(LTHoleEdgeLODCheck.Loops(rim)==(fixture==3?2:fixture==4||fixture==6?0:1),"baseline cut contour");
                // Exact 3D positions at the rim: the bridge still uses the same LOD0 points.
                HashSet<(Vector3,Vector3)> Edges3D(Vector3[] vertices,int[] indices)
                {
                    var edges=new HashSet<(Vector3,Vector3)>();
                    for(int i=0;i<indices.Length;i+=3)for(int k=0;k<3;k++)
                    {
                        var a=vertices[indices[i+k]];var b=vertices[indices[i+(k+1)%3]];
                        if(!edges.Remove((b,a)))Require(edges.Add((a,b)),"nonmanifold/overlapping triangles");
                    }
                    edges.RemoveWhere(e=>e.Item1.x==e.Item2.x&&(e.Item1.x==0||e.Item1.x==extent)||
                        e.Item1.z==e.Item2.z&&(e.Item1.z==0||e.Item1.z==extent));return edges;
                }
                var exact=Edges3D(reference.v,reference.i);
                void Check((Vector3[] v,int[] i) mesh)
                {
                    Require(LTHoleEdgeLODCheck.Rim(mesh.v,mesh.i,extent).SetEquals(rim),"cut segment changed or extra crack appeared");
                    Require(Edges3D(mesh.v,mesh.i).SetEquals(exact),"3D bridge attachment changed");checks++;
                }
                Check((legacyVertices.ToArray(),legacyIndices.ToArray()));
                foreach(int level in new[]{4,0,2,4})
                {
                    for(int c=0;c<4;c++)for(int p=0;p<divisions*divisions;p++)topology.SetLevel(c,p,level);
                    topology.Update();topology.Validate();var mesh=LTHoleEdgeLODCheck.Read(outputs,topology,width);Check(mesh);
                    if(level==0)Require(mesh.i.SequenceEqual(reference.i),"return to LOD0 changed indices");
                }
                var random=new System.Random(412+fixture);
                for(int trial=0;trial<12;trial++)
                {
                    for(int c=0;c<4;c++)for(int p=0;p<divisions*divisions;p++)topology.SetLevel(c,p,random.Next(5));
                    topology.Update();topology.Validate();Check(LTHoleEdgeLODCheck.Read(outputs,topology,width));
                }
                var staged=outputs.Select((o,c)=>c==0?previews[c]:o).ToArray();var stagedTopology=Topology(staged);
                for(int c=0;c<4;c++)for(int p=0;p<divisions*divisions;p++)stagedTopology.SetLevel(c,p,4);
                stagedTopology.Update();stagedTopology.Validate();Check(LTHoleEdgeLODCheck.Read(staged,stagedTopology,width));
                summary.Add($"case {fixture}: cells {fineCells}->{farCells} (old AABB {oldCells}); contact {edgeCells}, protected {protectedCells}; exact rim {rim.Count}");
            }
            return $"{checks} spatial/legacy/mixed/staged comparisons, exact 3D rim, slope, adaptive grid, chunk/patch seams, retained island, empty/full cut, unpublished partial preparation; "+string.Join("; ",summary);
        }
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException("Rock terrain LOD: "+message);}
    }
}
