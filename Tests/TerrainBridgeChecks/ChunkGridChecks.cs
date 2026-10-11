using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void ChunkGridChecks()
    {
        var original=new LTChunkGrid(0,0,16,16,new Vector2(256,128));
        var expanded=original.Include(-1,-2).Include(17,18);
        Require(expanded.Valid&&expanded.columns==19&&expanded.rows==21,"expanded envelope");
        for(int z=0;z<16;z++)for(int x=0;x<16;x++)
        {
            Require(original.Rect(x,z)==expanded.Rect(x,z),"expansion moved/resized old terrain");
            Require(expanded.Coordinate(expanded.Id(x,z))==new Vector2Int(x,z),"coordinate/id round trip");
        }
        Require(expanded.Id(-2,0)==-1&&expanded.Id(18,0)==-1,"out of envelope aliases a live chunk");
        bool rejected=false;try{original.Include(100,0);}catch(ArgumentOutOfRangeException){rejected=true;}
        Require(rejected,"unsafe topology width accepted");
        const int columns=3,rows=3,n=LTSpatialLODMath.N;int mixed=0;
        var plans=new Dictionary<int,List<Vector3Int>>();
        for(int c=0;c<9;c++)
        {
            int side=c%3==0?16:c%3==1?8:4;var leaves=new List<Vector3Int>();
            for(int z=0;z<side;z++)for(int x=0;x<side;x++)leaves.Add(new Vector3Int(x*n/side,z*n/side,n/side));
            plans[c]=leaves;
        }
        var internalCache=new LTBalancedForest.InternalPlanCache();var cache=new LTBalancedForest.TopologyCache();
        LTSpatialLODTopology.Chunk[] savedInputs=null;LTSpatialLODMath.Output[] savedOutputs=null;
        foreach(var missing in new[]{Array.Empty<int>(),new[]{4},new[]{0,3,6},new[]{1,2,3,4,5,6,7},Enumerable.Range(0,9).ToArray(),Array.Empty<int>()})
        {
            var present=plans.Where(p=>!missing.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value);
            var balanced=internalCache.Prepare(present,100000);var forest=cache.Begin(columns,rows,100000,balanced);
            forest.Balance();forest.Validate();cache.Commit(forest);
            var inputs=new LTSpatialLODTopology.Chunk[9];var outputs=new LTSpatialLODMath.Output[9];
            foreach(int id in present.Keys)
            {
                var rect=new Rect((id%3-1)*16,(id/3-1)*16,16,16);var fine=forest.Plan(id);
                int Mask(Vector3Int p){int value=0;for(int side=0;side<4;side++)if(forest.Midpoint(id,p,side))value|=1<<side;return value;}
                float Height(float x,float z)=>.1f*x+.03f*z;
                var preparation=new LTLODMesh.Preparation(fine,rect,Height);var levels=new List<Vector3Int>[5];levels[0]=fine;
                for(int level=1;level<5;level++)levels[level]=preparation.Coarsen(level,100,null,null,2,false);
                var output=LTSpatialLODMath.BuildLayout(levels,2,100000,Mask,_=>true);
                LTStampMesh.EmitSpatialVariants(output,rect,new Vector2(48,48),100000,Height,null);
                for(int v=0;v<output.vertices.Length;v++)Require(Math.Abs(output.uv[v].x*48-output.vertices[v].x-rect.xMin)<.0001f&&Math.Abs(output.uv[v].y*48-output.vertices[v].z-rect.yMin)<.0001f,"expanded mesh changed source/world UV mapping");
                inputs[id]=new LTSpatialLODTopology.Chunk{cells=output.cells,patches=output.patches};outputs[id]=output;
            }
            if(savedInputs==null){savedInputs=inputs;savedOutputs=outputs;}
            // Deleting objects must also work BEFORE their surviving neighbours are rebaked.
            var retained=(LTSpatialLODTopology.Chunk[])savedInputs.Clone();foreach(int id in missing)retained[id]=null;
            var stale=new LTSpatialLODTopology(columns,rows,2,retained);
            foreach(int id in present.Keys)for(int p=0;p<4;p++)stale.SetLevel(id,p,4);
            stale.Update();stale.Validate();CheckSparseEdges(stale,savedOutputs,present.Keys);
            var topology=new LTSpatialLODTopology(columns,rows,2,inputs);topology.Update();topology.Validate();
            var list=new List<int>();int near=0,far=0;
            foreach(int id in present.Keys){topology.WriteIndices(id,list);near+=list.Count;}
            for(int step=0;step<16;step++)
            {
                foreach(int id in present.Keys)for(int p=0;p<4;p++)topology.SetLevel(id,p,step%3==0?4:(id+p+step)%5);
                topology.Update();topology.Validate();mixed++;
                CheckSparseEdges(topology,outputs,present.Keys);
                foreach(int id in present.Keys)
                {
                    topology.WriteIndices(id,list);Require(list.All(i=>i>=0&&i<outputs[id].vertices.Length),"invalid sparse index");
                    Require(topology.DirtyChunks.All(present.ContainsKey),"missing chunk queued for rendering");
                }
            }
            foreach(int id in present.Keys)for(int p=0;p<4;p++)topology.SetLevel(id,p,4);
            topology.Update();topology.Validate();
            foreach(int id in present.Keys){topology.WriteIndices(id,list);far+=list.Count;}
            Require(present.Count==0?far==0:far<near,"sparse terrain stopped simplifying");
        }
        var cells=new List<Vector2Int>();var extent=new Rect(-33,-17,97,81);
        Require(LTDetailStreaming.Plan(new List<Vector2>{new Vector2(-20,-10)},extent.size,16,100,200,cells,extent.min),"expanded vegetation plan");
        Require(cells.Any(c=>c.x<0&&c.y<0),"negative-coordinate vegetation lost");
        foreach(var key in cells){var rect=LTDetailStreaming.CellRect(key,16,extent.size,extent.min);Require(rect.width>0&&rect.height>0&&rect.xMin>=extent.xMin&&rect.yMin>=extent.yMin&&rect.xMax<=extent.xMax&&rect.yMax<=extent.yMax,"stream cell exceeds expanded bounds");}
        var revisions=new LTPaintMath.TerrainAuthoringRevisions();revisions.Configure(3,3,16,16,"negative",new Vector2(-16,-16));
        int before=revisions.RegionSignature(new Rect(-15,-15,1,1),0);revisions.Touch(0);
        Require(before!=revisions.RegionSignature(new Rect(-15,-15,1,1),0),"negative authoring revision ignored");
        var terrainCache=new LTPaintWeightTerrainCache();
        var tileRect=new Rect(-16,-16,16,16);
        var vtx=new[]{new Vector3(-16,-4.8f,-16),new Vector3(0,-3.2f,-16),new Vector3(-16,-1.6f,0),Vector3.zero};
        var source=LTPaintWeightTerrainCache.BuildSource(tileRect,vtx,Array.Empty<Vector3>(),new[]{0,2,1,1,2,3},null,out _,out _);
        var layout=new LTPaintWeightMath.Terrain{minX=-16,minZ=-16,sizeX=32,sizeZ=32,width=16,depth=16,countX=2,countZ=2};
        LTPaintWeightTerrainCache.Source Lookup(int x,int z)=>x==0&&z==0?source:null;
        Require(terrainCache.TryGet(layout,new RectInt(0,0,2,2),Lookup,out var snapshot,out _),"negative paint snapshot");
        var access=new LTPaintTerrainSnapshot.Access{value=snapshot};
        Require(LTPaintWeightMath.Surface(layout,access,-8,-8,out float height,out _)&&Math.Abs(height+2.4f)<.0001f,"negative paint sample clamped to old origin");
        Require(!LTPaintWeightMath.Surface(layout,access,8,8,out _,out _),"missing tile produced paint surface");
        layout.minX=-32;
        Require(terrainCache.TryGet(layout,new RectInt(0,0,2,2),Lookup,out _,out bool reused)&&!reused,"paint cache reused shifted world bounds");
        Console.WriteLine($"Chunk grid PASS: stable signed expansion, 6 sparse/delete/restore layouts, {mixed} mixed LOD states, empty world, negative vegetation and authoring bounds.");
    }
    static void CheckSparseEdges(LTSpatialLODTopology topology,LTSpatialLODMath.Output[] outputs,IEnumerable<int> ids)
    {
        var present=new HashSet<int>(ids);var edges=new Dictionary<(Vector3Int,Vector3Int),int>();var indices=new List<int>();
        bool Before(Vector3Int a,Vector3Int b)=>a.x<b.x||a.x==b.x&&(a.z<b.z||a.z==b.z&&a.y<b.y);
        foreach(int id in present)
        {
            topology.WriteIndices(id,indices);var vertices=outputs[id].vertices;var offset=new Vector3((id%3-1)*16,0,(id/3-1)*16);
            for(int t=0;t<indices.Count;t+=3)for(int e=0;e<3;e++)
            {
                var a=SpatialKey(vertices[indices[t+e]]+offset);var b=SpatialKey(vertices[indices[t+(e+1)%3]]+offset);
                var key=Before(a,b)?(a,b):(b,a);edges.TryGetValue(key,out int count);edges[key]=count+1;
            }
        }
        bool At(float x,float z)
        {int cx=Mathf.FloorToInt((x+16)/16),cz=Mathf.FloorToInt((z+16)/16);return cx>=0&&cz>=0&&cx<3&&cz<3&&present.Contains(cz*3+cx);}
        foreach(var edge in edges)
        {
            Require(edge.Value<=2,"nonmanifold sparse terrain");if(edge.Value==2)continue;
            var a=edge.Key.Item1;var b=edge.Key.Item2;float x=(a.x+b.x)/20000f,z=(a.z+b.z)/20000f;
            Require(a.x==b.x?At(x-.001f,z)!=At(x+.001f,z):a.z==b.z&&At(x,z-.001f)!=At(x,z+.001f),"crack at a live sparse chunk/patch boundary");
        }
    }
}
