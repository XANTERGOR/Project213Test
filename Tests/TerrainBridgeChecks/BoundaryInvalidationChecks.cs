using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void BoundaryInvalidationChecks()
    {
        int checks=0;
        void Check(bool ok,string message){checks++;Require(ok,"boundary invalidation: "+message);}
        var key=new LTBoundaryCacheKey(0,Matrix4x4.identity,"lod0-v1");
        LTBoundaryCacheMiss Reason(LTBoundaryCacheKey current,bool proposed=false,bool present=true,bool sameMesh=true)
            =>LTBoundaryCacheKey.Compare(proposed,present,sameMesh,key,current);
        Check(Reason(key)==LTBoundaryCacheMiss.None,"unchanged key reuses entry");
        Check(Reason(key,proposed:true)==LTBoundaryCacheMiss.Proposed,"unpublished pending geometry cannot reuse old entry");
        Check(Reason(key,present:false)==LTBoundaryCacheMiss.Missing,"cold or removed entry misses");
        Check(Reason(key,sameMesh:false)==LTBoundaryCacheMiss.Mesh,"mesh replacement invalidates identical metadata");
        Check(Reason(new LTBoundaryCacheKey(1,key.matrix,key.assetHash))==LTBoundaryCacheMiss.Revision,"in-memory geometry/normal changes invalidate");
        Check(Reason(new LTBoundaryCacheKey(0,key.matrix,"lod0-v2"))==LTBoundaryCacheMiss.Asset,"reimport with same identity and reset dirty counter invalidates");
        var transform=key.matrix;transform.m03=1e-7f;
        Check(Reason(new LTBoundaryCacheKey(0,transform,key.assetHash))==LTBoundaryCacheMiss.Transform,"transform comparison is exact, not approximate");
        Check(Reason(new LTBoundaryCacheKey(0,key.matrix,"LOD0-v1"))==LTBoundaryCacheMiss.Asset,"dependency keys compare ordinally");
        Check(LTBoundaryCacheKey.Compare(false,true,true,new LTBoundaryCacheKey(3,key.matrix,""),new LTBoundaryCacheKey(3,key.matrix,""))==LTBoundaryCacheMiss.None,"transient meshes use identity/revision/transform without an asset path");

        // Unused vertices in a shared LOD bank must never become representatives
        // of referenced vertices in the same topology quantization bucket.
        var polluted=new[]{new Vector3(0,.00002f,0),Vector3.zero,new Vector3(1,0,0),new Vector3(0,0,1)};
        var activeData=BuildChunkBoundary(polluted,new[]{Vector3.right,Vector3.up,Vector3.up,Vector3.up},new[]{1,3,2},Matrix4x4.identity,Matrix4x4.identity);
        Check(activeData.pointIndices.Values.All(ids=>!ids.Contains(0)),"inactive LOD vertices excluded from contour position/normal lookup");
        Check(activeData.edges.All(e=>e.a!=0&&e.b!=0),"inactive LOD vertex cannot own an active edge");

        // Model asset-import notifications by refreshing the observed per-asset
        // hash, NOT clearing geometry. The production key is linked directly.
        const int count=256;
        var present=new bool[count];var versions=new int[count];var geometry=new int[count];var meshIds=new int[count];var assets=new string[count];var matrices=new Matrix4x4[count];
        for(int i=0;i<count;i++)
        {present[i]=true;meshIds[i]=i+1;assets[i]="lod0-"+i+"-a";matrices[i]=Matrix4x4.identity;matrices[i].m03=i%16*2;matrices[i].m23=i/16*2;}
        var cache=new BoundaryCache();var keys=new Dictionary<int,LTBoundaryCacheKey>();var identities=new Dictionary<int,int>();
        ChunkBoundaryData Build(int id)
        {
            var vertices=new Vector3[9];var normals=new Vector3[9];var indices=new List<int>();
            for(int z=0;z<3;z++)for(int x=0;x<3;x++)
            {int i=z*3+x;vertices[i]=new Vector3(x,i==4?geometry[id]*.03f:0,z);normals[i]=new Vector3(i==4?geometry[id]*.01f:0,1,0).normalized;}
            for(int z=0;z<2;z++)for(int x=0;x<2;x++)
            {if(geometry[id]%3==1&&x==1&&z==1)continue;int a=z*3+x;indices.AddRange(new[]{a,a+3,a+1,a+1,a+3,a+4});}
            // These fixtures use translation only, so the normal transform is
            // identity. Matrix.inverse itself is a native Unity API, unavailable
            // in this standalone managed runner.
            return BuildChunkBoundary(vertices,normals,indices.ToArray(),matrices[id],Matrix4x4.identity);
        }
        (int hits,int[] misses) Refresh(HashSet<int> pending=null,bool publish=true)
        {
            var proposal=new BoundaryCache(cache);var nextKeys=new Dictionary<int,LTBoundaryCacheKey>(keys);var nextIds=new Dictionary<int,int>(identities);
            int hits=0;var misses=new int[Enum.GetValues(typeof(LTBoundaryCacheMiss)).Length];
            var committedTotals=new Dictionary<(Vector3Int,Vector3Int),int>(cache.edgeTotals);
            var committedEntries=cache.ToDictionary(p=>p.Key,p=>p.Value);
            foreach(int id in proposal.Keys.Where(i=>!present[i]).ToArray()){proposal.RemoveChunk(id);nextKeys.Remove(id);nextIds.Remove(id);}
            for(int id=0;id<count;id++)if(present[id])
            {
                var current=new LTBoundaryCacheKey(versions[id],matrices[id],assets[id]);
                bool found=nextKeys.TryGetValue(id,out var previous);
                var miss=LTBoundaryCacheKey.Compare(pending?.Contains(id)==true,found,found&&nextIds[id]==meshIds[id],previous,current);
                if(miss==LTBoundaryCacheMiss.None)hits++;
                else{misses[(int)miss]++;proposal.Replace(id,Build(id));nextKeys[id]=current;nextIds[id]=meshIds[id];}
            }
            var reference=new BoundaryCache();
            for(int id=0;id<count;id++)if(present[id])
            {
                var data=Build(id);reference.Replace(id,data);
                Check(data.points.SequenceEqual(proposal[id].points)&&data.normals.SequenceEqual(proposal[id].normals),"cached and fresh positions/normals match");
            }
            Check(reference.edgeTotals.Count==proposal.edgeTotals.Count&&reference.edgeTotals.All(p=>proposal.edgeTotals[p.Key]==p.Value),"incremental/full edge totals and open-edge endpoints match");
            Check(committedTotals.Count==cache.edgeTotals.Count&&committedTotals.All(p=>cache.edgeTotals[p.Key]==p.Value)&&committedEntries.All(p=>ReferenceEquals(cache[p.Key],p.Value)),"private proposal does not mutate committed entries or edge totals");
            if(publish){cache=proposal;keys=nextKeys;identities=nextIds;}
            return(hits,misses);
        }
        var r=Refresh();Check(r.hits==0&&r.misses[(int)LTBoundaryCacheMiss.Missing]==256,"cold start builds 256 entries");
        for(int notification=0;notification<8;notification++)
        {
            // A different project's texture or our Chunk_*_SpatialLOD changed;
            // none of the observed LOD0 mesh dependency hashes changed.
            r=Refresh();Check(r.hits==256&&r.misses.Sum()==0,"unrelated/coarse-LOD imports preserve all 256 entries");
        }
        geometry[51]++;versions[51]++;r=Refresh(new HashSet<int>{51});
        Check(r.hits==255&&r.misses[(int)LTBoundaryCacheMiss.Proposed]==1,"single edited chunk rebuilds once, 255 remain cached");
        assets[117]+="-import";geometry[117]++;r=Refresh();
        Check(r.hits==255&&r.misses[(int)LTBoundaryCacheMiss.Asset]==1,"only reimported LOD0 changes even when version remains zero");
        versions[118]++;geometry[118]++;r=Refresh();Check(r.hits==255&&r.misses[(int)LTBoundaryCacheMiss.Revision]==1,"unknown in-memory write invalidates only its chunk");
        meshIds[119]+=1000;geometry[119]++;r=Refresh();Check(r.hits==255&&r.misses[(int)LTBoundaryCacheMiss.Mesh]==1,"replaced mesh cannot reuse an old entry");
        matrices[120].m13=.25f;r=Refresh();Check(r.hits==255&&r.misses[(int)LTBoundaryCacheMiss.Transform]==1,"changed chunk transform invalidates locally");
        present[121]=false;r=Refresh();Check(r.hits==255&&r.misses.Sum()==0&&!cache.ContainsKey(121),"deleted chunk removes its edge contribution");
        present[121]=true;r=Refresh();Check(r.hits==255&&r.misses[(int)LTBoundaryCacheMiss.Missing]==1,"reinserted chunk contributes exactly once");
        geometry[51]++;versions[51]++;r=Refresh(new HashSet<int>{51},false);
        Check(r.hits==255&&r.misses[(int)LTBoundaryCacheMiss.Proposed]==1,"cancelled proposal reports local work only");
        geometry[51]--;versions[51]--;r=Refresh();Check(r.hits==256,"discarded proposal did not poison original keys");
        // An explicit Undo barrier still clears conservatively. This task removes
        // only the blanket projectChanged reset, not known lifecycle barriers.
        cache.Clear();keys.Clear();identities.Clear();r=Refresh();
        Check(r.misses[(int)LTBoundaryCacheMiss.Missing]==256,"explicit reset rebuilds safely");

        string engine=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        string project=engine.Substring(engine.IndexOf("EditorApplication.projectChanged +="));project=project.Substring(0,project.IndexOf("EditorApplication.playModeStateChanged"));
        Check(project.Contains("assetInputHashes.Clear()")&&!project.Contains("boundaryCache.Clear()")&&!project.Contains("CancelBuilds"),"project notifications refresh asset observations without deleting geometry or cancelling jobs");
        Check(engine.Contains("LTBoundaryCacheKey.Compare(replacement!=null,present,present&&cached.mesh==chunk.mesh")&&engine.Contains("new LTBoundaryCacheKey(version,toTerrain,assetHash)"),"actual boundary lookup uses the tested key and mesh identity");
        Check(engine.Contains("foreach(int index in indices)referenced[index]=true")&&engine.Contains("if(!referenced[i])continue"),"production collector excludes inactive shared-bank vertices too");
        Check(engine.Contains("data.assetHash=assetHash")&&engine.Contains("matrix=matrix,assetHash=assetHash"),"new entries and normal-only copies retain the asset dependency key");
        Check(engine.Contains("cached.assetHash!=assetHash")&&engine.Contains("rect=entry.rect,assetHash=entry.assetHash,contributions=entry.contributions"),"seam cache also detects imports without blessing an unchecked asset revision");
        Check(engine.Contains("entry.matrix.Equals(matrix)&&entry.assetHash==AssetInputHash(mesh)"),"normal-only acknowledgement cannot bless a different imported mesh");
        Check(engine.Contains("new BoundaryCache(state.boundaryCache)")&&engine.Contains("cache.RemoveChunk(id)")&&engine.Contains("Boundary misses: proposed"),"transactional cache proposal, deletion cleanup and miss diagnostics retained");
        Console.WriteLine($"PASS boundary invalidation: {checks} checks; 256-entry cold/warm, unrelated/coarse imports 256 hits, local edit and same-version reimport 255 hits/1 miss, exact full edge/normal parity, replacement/deletion/transform, cancelled proposals and explicit reset. Managed production-key/model checks, not native import or scene timings.");
    }
}
