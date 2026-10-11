using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Pure geometry; also runs under the managed harness. No scene or asset writes.
    internal static class LTRockNativeLODCheck
    {
        internal static string Run()
        {
            const int divisions=2,budget=50000;const float width=16;
            float H(float x,float z)=>.02f*x+.05f*Mathf.Sin(z);
            bool Cut(float x,float z)=>(x-15.8f)*(x-15.8f)+(z-7.8f)*(z-7.8f)<9;
            var plans=new Dictionary<int,List<Vector3Int>>();
            for(int c=0;c<2;c++)plans[c]=LTStampMesh.Plan(new Rect(c*width,0,width,width),32,false,.1f,budget,new List<LTStampMesh.Zone>(),H);
            var forest=new LTBalancedForest(2,1,budget,plans);forest.Balance();
            var outputs=new LTSpatialLODMath.Output[2];var seams=new LTRockSeamIndices.Plan[2];
            for(int c=0;c<2;c++)
            {
                int chunk=c;var region=new Rect(c*width,0,width,width);var fine=forest.Plan(c);
                var protection=new LTRockTerrainLODProtection();
                foreach(var step in protection.Prepare(fine,region,new[]{new Rect(11,3,10,10)},Cut)){}
                var levels=new List<Vector3Int>[5];levels[0]=fine;
                var preparation=new LTLODMesh.Preparation(fine,region,H,new LTLODMesh.BuildProgress(false));
                for(int l=1;l<5;l++)levels[l]=preparation.Coarsen(l,1,null,protection.Protects,divisions,false);
                int Mask(Vector3Int p){int mask=0;for(int s=0;s<4;s++)if(forest.Midpoint(chunk,p,s))mask|=1<<s;return mask;}
                bool Preserve(Vector3Int p)=>protection.Protects(new Rect(region.xMin+p.x/(float)LTSpatialLODMath.N*width,
                    p.y/(float)LTSpatialLODMath.N*width,p.z/(float)LTSpatialLODMath.N*width,p.z/(float)LTSpatialLODMath.N*width));
                var data=LTSpatialLODMath.BuildLayout(levels,divisions,budget,Mask,Preserve);
                LTStampMesh.EmitSpatialVariants(data,region,new Vector2(32,16),budget,H,Cut);outputs[c]=data;
                var identity=Enumerable.Range(0,data.vertices.Length).ToArray();
                var bands=LTRockSeamBandMath.BuildSpatial(data.vertices,data.baseIndices,data.cells,data.patches,
                    Enumerable.Repeat(true,data.vertices.Length).ToArray(),identity,1);
                Require(bands.Length>0&&bands.All(b=>b.invariant),"missing native footprint");
                seams[c]=new LTRockSeamIndices.Plan(identity,bands);
            }
            var inputs=outputs.Select(o=>new LTSpatialLODTopology.Chunk{cells=o.cells,patches=o.patches}).ToArray();
            var topology=new LTSpatialLODTopology(2,1,divisions,inputs);var random=new System.Random(824);
            var raw=new List<int>();var rendered=new List<int>();int reduction=-1,checks=0;
            // Keep seam level fixed: neighbouring native cells may change freely,
            // but the seam's contribution must NEVER vanish or become denser.
            for(int trial=0;trial<160;trial++)
            {
                for(int c=0;c<2;c++)for(int p=0;p<4;p++)topology.SetLevel(c,p,random.Next(5));
                topology.Update();topology.Validate();int delta=0;
                var before=new HashSet<(Vector3Int,Vector3Int)>();var after=new HashSet<(Vector3Int,Vector3Int)>();
                for(int c=0;c<2;c++)
                {
                    topology.WriteIndices(c,raw);topology.WriteIndices(c,rendered,seams[c]);
                    var faces=new HashSet<(int,int,int)>();for(int i=0;i<raw.Count;i+=3)faces.Add(LTRockSeamIndices.Face(raw[i],raw[i+1],raw[i+2]));
                    foreach(var band in seams[c].bands)for(int i=0;i<band.source.Length;i+=3)
                        Require(faces.Contains(LTRockSeamIndices.Face(band.source[i],band.source[i+1],band.source[i+2])),"owned face missing in mixed layout");
                    AddEdges(outputs[c].vertices,raw,c*width,before);AddEdges(outputs[c].vertices,rendered,c*width,after);
                    delta+=raw.Count-rendered.Count;
                    // Cached writes and cold writes must produce identical indices.
                    var cached=new List<int>();topology.WriteIndices(c,cached,seams[c]);Require(cached.SequenceEqual(rendered),"cache/seam state leaked");
                }
                Require(before.SetEquals(after),"changed hole, outer border or chunk/patch crack");
                Require(delta>0,"native region did not simplify");
                if(reduction<0)reduction=delta;Require(delta==reduction,"neighbour LOD switched native simplification off");checks++;
            }
            // No seam selected is exactly the untouched near terrain, not a remapped clone.
            for(int c=0;c<2;c++)for(int p=0;p<4;p++)topology.SetLevel(c,p,0);
            topology.Update();
            for(int c=0;c<2;c++)
            {
                topology.WriteIndices(c,raw);var expected=Faces(outputs[c].baseIndices);
                Require(Faces(raw).SetEquals(expected),"LOD0 not restored");
            }
            return $"native seam ownership: {checks} mixed two-chunk layouts, constant reduction {reduction/3} triangles, exact hole/borders, warm cache and LOD0 restore; "+CheckConstrainedMaps(outputs,topology,width);
        }
        static string CheckConstrainedMaps(LTSpatialLODMath.Output[] outputs,LTSpatialLODTopology topology,float width)
        {
            CheckVariantGuard();
            var parts=outputs.Select((o,c)=>new LTRockSeamMath.Part{
                positions=o.vertices.Select(v=>v+new Vector3(c*width,0,0)).ToArray(),triangles=new[]{o.baseIndices},terrain=true,
                movable=LTRockSeamMath.InteriorSpatialVertices(o.vertices,width/2,width/2),
                constraints=LTRockSeamMath.SpatialConstraints(o.cells,o.patches),heightErrors=new[]{0f,.05f,.1f,.5f,1f}}).ToArray();
            var errors=new[]{0f,.6f,1.2f,2.4f,4.8f};var result=LTRockSeamMath.Build(parts,errors,progressive:true);
            Require(result.collapses[4]>0,"all native stars remained locked");
            for(int l=0;l<5;l++)for(int p=0;p<parts.Length;p++)
            {
                var part=parts[p];var map=result.maps[l][p];
                for(int v=0;v<map.Length;v++)
                {
                    Require(Vector3.Distance(part.positions[v],part.positions[map[v]])<=errors[l]+.0001f,"native endpoint budget");
                    if(l==0||!part.movable[v])Require(map[v]==v,"LOD0 or patch border moved");
                    if(l>0)Require(map[result.maps[l-1][p][v]]==map[v],"far seam restored a contracted endpoint");
                }
                var height=LTRockSeamBandMath.HeightConstraint(part.positions,part.triangles[0]);
                for(int t=0;t<part.constraints.Length;t+=3)
                {
                    int a=part.constraints[t],b=part.constraints[t+1],c=part.constraints[t+2];
                    int x=map[a],y=map[b],z=map[c];if(x==y||y==z||x==z)continue;
                    float old=Vector3.Cross(part.positions[b]-part.positions[a],part.positions[c]-part.positions[a]).y;
                    float next=Vector3.Cross(part.positions[y]-part.positions[x],part.positions[z]-part.positions[x]).y;
                    Require(old*next>0,"native variant folded");
                    if(a!=x||b!=y||c!=z)Require(height(x,y,z,part.heightErrors[l]),"native terrain height budget");
                }
            }
            var random=new System.Random(915);int checks=0;
            (int raw,int final) Count(int[] levels,int seamLevel)
            {
                for(int c=0;c<2;c++)for(int p=0;p<4;p++)topology.SetLevel(c,p,levels[c*4+p]);topology.Update();topology.Validate();
                var before=new HashSet<(Vector3Int,Vector3Int)>();var after=new HashSet<(Vector3Int,Vector3Int)>();int n=0,m=0;
                for(int c=0;c<2;c++)
                {
                    var raw=new List<int>();topology.WriteIndices(c,raw);n+=raw.Count;
                    var final=new List<int>(raw);LTRockSeamIndices.Remap(final,result.maps[seamLevel][c]);m+=final.Count;
                    AddEdges(outputs[c].vertices,raw,c*width,before);AddEdges(outputs[c].vertices,final,c*width,after);
                }
                Require(before.SetEquals(after),"constrained map opened chunk/patch/hole crack");return(n,m);
            }
            for(int l=1;l<5;l++)for(int trial=0;trial<80;trial++)
            {
                var levels=Enumerable.Range(0,8).Select(_=>random.Next(4)).ToArray();var before=Count(levels,l);
                levels[random.Next(levels.Length)]++;var after=Count(levels,l);
                Require(after.final-before.final<=Math.Max(0,after.raw-before.raw),"native constrained map density backslide");checks++;
            }
            return $"all-variant contractions: {checks} paired two-chunk changes, {result.collapses[4]} far collapses, height/error bounds, every variant oriented and borders unchanged";
        }
        static void CheckVariantGuard()
        {
            var part=new LTRockSeamMath.Part{positions=new[]{new Vector3(-1,0,-1),new Vector3(1,0,-1),new Vector3(1,0,1),new Vector3(-1,0,1),Vector3.zero,
                new Vector3(-.5f,0,-1.5f),new Vector3(-.5f,0,.5f)},
                triangles=new[]{new[]{0,4,1,1,4,2,2,4,3,3,4,0}},movable=new[]{false,false,false,false,true,false,false},
                targets=new[]{true,false,false,false,false,false,false},terrain=true};
            var errors=new[]{0f,2f};
            Require(LTRockSeamMath.Build(new[]{part},errors).maps[1][0][4]==0,"variant guard fixture cannot contract");
            // Only a neighbour stitch uses this face. The LOD0 link condition
            // accepts the collapse, but it flips the alternative face.
            part.constraints=new[]{4,5,6};
            Require(LTRockSeamMath.Build(new[]{part},errors).maps[1][0][4]==4,"coarse-only triangle inversion accepted");
            part.constraints=null;part.positions[4]=new Vector3(0,.1f,0);part.heightErrors=new[]{0f,.01f};
            Require(LTRockSeamMath.Build(new[]{part},errors).maps[1][0][4]==4,"height peak exceeded terrain budget");
        }
        static HashSet<(int,int,int)> Faces(IReadOnlyList<int> indices)
        {
            var result=new HashSet<(int,int,int)>();for(int i=0;i<indices.Count;i+=3)
                Require(result.Add(LTRockSeamIndices.Face(indices[i],indices[i+1],indices[i+2])),"duplicate triangle");return result;
        }
        static void AddEdges(Vector3[] vertices,IReadOnlyList<int> indices,float offset,HashSet<(Vector3Int,Vector3Int)> edges)
        {
            Faces(indices);
            for(int i=0;i<indices.Count;i+=3)for(int k=0;k<3;k++)
            {
                var a=LTRockSeamMath.Key(vertices[indices[i+k]]+new Vector3(offset,0,0));
                var b=LTRockSeamMath.Key(vertices[indices[i+(k+1)%3]]+new Vector3(offset,0,0));
                Require(a!=b,"degenerate edge");if(!edges.Remove((b,a)))Require(edges.Add((a,b)),"overlap or nonmanifold edge");
            }
        }
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException("Native rock LOD: "+message);}
    }
}
