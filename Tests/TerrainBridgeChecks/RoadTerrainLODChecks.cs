using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadTerrainLODChecks()
    {
        const int count=4,divisions=4,budget=100000;
        const float width=16,extent=32;
        var tolerances=new[]{0f,.04f,.15f,.5f};
        int samples=0;
        foreach(string scenario in new[]{"flat offroad","rutted/banked offroad","varied offroad","offroad junction","asphalt"})
        {
            bool flat=scenario=="flat offroad",junction=scenario=="offroad junction";
            var settings=LTRoadMath.Settings.Default;
            settings.mode=scenario=="asphalt"?LTRoadMode.Asphalt:LTRoadMode.Offroad;
            settings.width=3;settings.shoulderWidth=.5f;settings.blendWidth=1.5f;
            settings.terrainCellSize=.25f;settings.sampleSpacing=.5f;settings.edgeNoise=0;
            settings.pattern=flat?LTRoadPattern.Solid:LTRoadPattern.Tracks;settings.rutDepth=.12f;
            if(scenario=="varied offroad"){settings.variation.enabled=true;settings.variation.widthAmount=.3f;}
            bool fixedSurface=LTLODMesh.RequiresFixedRoadSurface(settings.mode);
            Require(fixedSurface==(scenario=="asphalt"),"only separate asphalt surfaces require a fixed foundation");
            var road=LTRoadMath.Build(new[]{
                new LTRoadPoint(new Vector3(2,flat?0:1,2)),
                new LTRoadPoint(new Vector3(11,flat?0:1.1f,9),flat?0:7),
                new LTRoadPoint(new Vector3(19,flat?0:1.2f,22),flat?0:-5),
                new LTRoadPoint(new Vector3(30,flat?0:1.3f,30))},Matrix4x4.identity,settings);
            var centre=new Vector3(16,1,16);
            var ports=new[]{Vector3.right,Vector3.left,Vector3.forward,Vector3.back}
                .Select(d=>LTRoadJunctionMath.MakePort(centre,centre+d*14,4,3)).ToArray();
            var node=LTRoadJunctionMath.Build(centre,ports,2,.25f);
            float H(float x,float z)
            {
                float ground=flat?0:.015f*x+.008f*z+.06f*(float)(Math.Sin(x*.3)*Math.Sin(z*.25));
                return junction?node.ApplyHeight(x,z,ground):road.ApplyHeight(x,z,ground);
            }
            bool Influence(Rect r)=>junction?node.Intersects(r):road.Intersects(r);
            bool Protected(Rect r)=>fixedSurface&&Influence(r);
            Rect RectOf(int c)=>new Rect(c%2*width,c/2*width,width,width);
            Rect CellArea(Rect r,Vector3Int p)=>new Rect(r.xMin+p.x/(float)LTSpatialLODMath.N*width,
                r.yMin+p.y/(float)LTSpatialLODMath.N*width,p.z/(float)LTSpatialLODMath.N*width,p.z/(float)LTSpatialLODMath.N*width);
            var zone=new LTStampMesh.Zone{bounds=junction?node.bounds:road.bounds,cellSize=.25f,
                customCellSize=(x,z)=>.25f,coverageIntersects=Influence};
            var plans=new Dictionary<int,List<Vector3Int>>();
            for(int c=0;c<count;c++)plans[c]=LTSpatialLODMath.PartitionLeaves(
                LTStampMesh.Plan(RectOf(c),8,true,.02f,budget,new List<LTStampMesh.Zone>{zone},H),divisions);
            var forest=new LTBalancedForest(2,2,budget,plans);forest.Balance();
            var outputs=new LTSpatialLODMath.Output[count];var inputs=new LTSpatialLODTopology.Chunk[count];
            int oldRoadCells=0,newRoadCells=0;
            for(int c=0;c<count;c++)
            {
                int chunk=c;var r=RectOf(c);var fine=forest.Plan(c);var copy=fine.ToArray();
                var levels=new List<Vector3Int>[tolerances.Length];levels[0]=fine;
                var preparation=new LTLODMesh.Preparation(fine,r,H);
                for(int l=1;l<levels.Length;l++)
                    levels[l]=preparation.Coarsen(l,tolerances[l],null,Protected,divisions,false);
                var old=LTLODMesh.Coarsen(fine,r,3,tolerances[3],H,null,Influence,divisions,false);
                oldRoadCells+=old.Count(p=>Influence(CellArea(r,p)));
                newRoadCells+=levels[3].Count(p=>Influence(CellArea(r,p)));
                var legacy=LTLODMesh.Coarsen(fine,r,3,tolerances[3],H,null,Protected,divisions);
                LTLODMesh.ValidateBoundary(fine,legacy);
                if(!fixedSurface)Require(legacy.Count<fine.Count,"WholeChunk must simplify offroad too");
                int Mask(Vector3Int p){int mask=0;for(int s=0;s<4;s++)if(forest.Midpoint(chunk,p,s))mask|=1<<s;return mask;}
                var output=LTSpatialLODMath.BuildLayout(levels,divisions,budget,Mask,p=>Protected(CellArea(r,p)));
                if(!fixedSurface)Require(output.cells.All(p=>p.requiredMask==0),"offroad must not pin old LOD0 midpoint fences");
                else
                {
                    var retained=new HashSet<Vector3Int>(levels[3]);
                    foreach(var p in fine.Where(p=>Protected(CellArea(r,p))))
                        Require(retained.Contains(p),"asphalt contact cells must not simplify independently of the ribbon");
                    foreach(var p in output.cells.Where(p=>p.child0<0&&Protected(CellArea(r,new Vector3Int(p.x,p.z,p.size)))))
                        Require(p.requiredMask==p.possibleMask,"asphalt contact stitch masks stay fixed");
                }
                LTStampMesh.EmitSpatialVariants(output,r,new Vector2(extent,extent),budget,H,null);
                LTStampMesh.Emit(r,new Vector2(extent,extent),budget,forest,c,fine,H,null,out var vertices,out _,out _,out var indices);
                Require(fine.SequenceEqual(copy),"LOD preparation must not mutate collider/LOD0 plans");
                Require(SpatialTriangles(vertices,indices).SetEquals(SpatialTriangles(output.vertices,output.baseIndices)),"road LOD0 must match the collider emitter exactly");
                outputs[c]=output;inputs[c]=new LTSpatialLODTopology.Chunk{cells=output.cells,patches=output.patches};
            }
            if(fixedSurface)Require(newRoadCells==oldRoadCells,"asphalt foundation protection unchanged");
            else Require(newRoadCells<oldRoadCells/2,$"{scenario}: distant road should lose its permanent density lock ({oldRoadCells} -> {newRoadCells})");
            var topology=new LTSpatialLODTopology(2,2,divisions,inputs);
            var buffer=new List<int>();
            int[][] Read()
            {
                topology.Update();topology.Validate();var result=new int[count][];
                for(int c=0;c<count;c++){topology.WriteIndices(c,buffer);result[c]=buffer.ToArray();}
                Require(SpatialBoundary(outputs,result,2,width,extent).Count==0,"no cracks across road/patch/chunk boundaries");
                return result;
            }
            var baseline=Read();
            var baselineHeights=new RoadLODHeightSampler[count];
            for(int c=0;c<count;c++)baselineHeights[c]=new RoadLODHeightSampler(outputs[c].vertices,baseline[c],width);
            void CheckHeights(int[][] indices,float tolerance)
            {
                for(int c=0;c<count;c++)
                {
                    var v=outputs[c].vertices;var coarse=new RoadLODHeightSampler(v,indices[c],width);
                    void Check(Vector3 p)
                    {
                        float expected=baselineHeights[c].Height(p.x,p.z);
                        Require(Math.Abs(coarse.Height(p.x,p.z)-expected)<=tolerance+.0005f,$"{scenario}: LOD exceeds height budget {tolerance} at {p.x},{p.z}");
                        samples++;
                    }
                    foreach(int index in baseline[c].Distinct())Check(v[index]);
                    foreach(int index in indices[c].Distinct())Check(v[index]);
                    for(int i=0;i<baseline[c].Length;i+=3)
                        Check((v[baseline[c][i]]+v[baseline[c][i+1]]+v[baseline[c][i+2]])/3);
                    if(fixedSurface)
                    {
                        var offset=new Vector3(c%2*width,0,c/2*width);
                        for(int i=0;i<baseline[c].Length;i+=3)
                        {
                            var p=(v[baseline[c][i]]+v[baseline[c][i+1]]+v[baseline[c][i+2]])/3;
                            var world=p+offset;
                            if(Protected(new Rect(world.x,world.z,0,0)))
                                Require(Math.Abs(coarse.Height(p.x,p.z)-p.y)<.0005f,"terrain under asphalt must not rise or fall with LOD");
                        }
                    }
                }
            }
            int farTriangles=0;
            for(int l=1;l<tolerances.Length;l++)
            {
                for(int c=0;c<count;c++)for(int p=0;p<divisions*divisions;p++)topology.SetLevel(c,p,l);
                var indices=Read();CheckHeights(indices,tolerances[l]);farTriangles=indices.Sum(a=>a.Length)/3;
            }
            // Neighbouring road sections select different levels; return-near must be exact.
            for(int c=0;c<count;c++)for(int p=0;p<divisions*divisions;p++)topology.SetLevel(c,p,(p+c)%4);
            CheckHeights(Read(),tolerances[3]);
            for(int c=0;c<count;c++)for(int p=0;p<divisions*divisions;p++)topology.SetLevel(c,p,0);
            var returned=Read();
            for(int c=0;c<count;c++)Require(SpatialIndexTriangles(returned[c]).SetEquals(SpatialIndexTriangles(baseline[c])),"near camera restores road LOD0 exactly");
            Console.WriteLine($"PASS road terrain LOD {scenario}: protected-policy road cells {oldRoadCells} -> {newRoadCells}; whole-world triangles {baseline.Sum(a=>a.Length)/3} -> {farTriangles}; watertight mixed levels, height budgets, exact return-near.");
        }
        var source=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        Require(source.Contains("fixedRoadSurface=LTLODMesh.RequiresFixedRoadSurface(node.surface)")&&source.Contains("fixedRoadSurface=LTLODMesh.RequiresFixedRoadSurface(snapshot.mode)"),"production roads/nodes capture the tested policy");
        Require(source.Split("s.fixedRoadSurface&&s.road!=null").Length==3&&source.Split("s.fixedRoadSurface&&s.junction!=null").Length==3,"both terrain LOD paths filter protected roads and junctions");
        Require(source.Contains("return protectedRegions.Any(")&&source.Contains("||roads.Any(road=>road.Intersects(area))"),"spatial fixed masks use the filtered protection too");
        Require(source.Contains("offroad-height-error-lod-v1")&&source.Contains("snapshot.hash+\":\"+node.surface"),"LOD policy and node surface changes invalidate old plans");
        Require(source.Contains("GetComponent<MeshCollider>().sharedMesh=c.mesh;"),"colliders remain on terrain LOD0");
        Console.WriteLine($"PASS road terrain LOD regression: {samples} emitted-surface height comparisons; no Unity native meshes or live-scene performance measurements.");
    }

    // Small XZ buckets keep dense-surface comparisons linear in sampled points.
    sealed class RoadLODHeightSampler
    {
        readonly Vector3[] vertices;readonly int[] indices;
        readonly List<int>[] buckets=new List<int>[16*16];readonly float size;
        public RoadLODHeightSampler(Vector3[] v,int[] t,float width)
        {
            vertices=v;indices=t;size=width/16;
            for(int i=0;i<t.Length;i+=3)
            {
                var a=v[t[i]];var b=v[t[i+1]];var c=v[t[i+2]];
                int x0=Cell(Math.Min(a.x,Math.Min(b.x,c.x))),x1=Cell(Math.Max(a.x,Math.Max(b.x,c.x)));
                int z0=Cell(Math.Min(a.z,Math.Min(b.z,c.z))),z1=Cell(Math.Max(a.z,Math.Max(b.z,c.z)));
                for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)(buckets[z*16+x]??=new List<int>()).Add(i);
            }
        }
        int Cell(float value)=>Math.Clamp((int)Math.Floor(value/size),0,15);
        public float Height(float x,float z)
        {
            var bucket=buckets[Cell(z)*16+Cell(x)];
            if(bucket!=null)foreach(int i in bucket)
            {
                var a=vertices[indices[i]];var b=vertices[indices[i+1]];var c=vertices[indices[i+2]];
                double det=(b.z-c.z)*(a.x-c.x)+(c.x-b.x)*(a.z-c.z);
                if(Math.Abs(det)<1e-12)continue;
                double u=((b.z-c.z)*(x-c.x)+(c.x-b.x)*(z-c.z))/det;
                double v=((c.z-a.z)*(x-c.x)+(a.x-c.x)*(z-c.z))/det;
                if(u>=-1e-5&&v>=-1e-5&&u+v<=1.00001)return (float)(u*a.y+v*b.y+(1-u-v)*c.y);
            }
            throw new InvalidOperationException($"Road LOD surface probe outside emitted triangles at {x},{z}");
        }
    }
}
