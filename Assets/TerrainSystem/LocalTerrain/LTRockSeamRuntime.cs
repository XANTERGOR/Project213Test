using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Called by LTWorld inside the same pre-render transaction as terrain indices.
    // No geometry analysis/collapse on camera movement; only baked map selection.
    public sealed class LTRockSeamRuntime : IDisposable
    {
        LTRockSeamAsset asset;
        LTMeshStamp[] stamps;
        int revision=-1;
        int[] levels;
        int[] next;
        LTMeshStamp[][] owners;
        LTChunk[] cachedTerrain;
        bool mapsReady;
        readonly Dictionary<Mesh,LTRockSeamIndices.Plan> maps=new Dictionary<Mesh,LTRockSeamIndices.Plan>();
        readonly Dictionary<LTRockSeamAsset.Map,LTRockSeamIndices.Plan> prepared=new Dictionary<LTRockSeamAsset.Map,LTRockSeamIndices.Plan>();
        readonly Dictionary<Mesh,Mesh> transitions=new Dictionary<Mesh,Mesh>();
        readonly List<LTRockRenderOutput> managed=new List<LTRockRenderOutput>();
        readonly Dictionary<int,LTChunk> chunks=new Dictionary<int,LTChunk>();
        public LTRockSeamIndices.Plan Map(Mesh mesh)=>mesh&&maps.TryGetValue(mesh,out var map)?map:null;
        public void Prepare(LTWorld world,LTChunk[] terrain,LTSpatialLODTopology topology,bool spatialReady)
        {
            if(asset!=world.rockSeams||(asset&&revision!=asset.revision)||stamps==null)
            {
                Dispose();asset=world.rockSeams;revision=asset?asset.revision:0;stamps=world.GetComponentsInChildren<LTMeshStamp>();
                if(asset)
                {
                    levels=new int[asset.groups.Length];next=new int[levels.Length];owners=new LTMeshStamp[levels.Length][];
                    for(int g=0;g<owners.Length;g++)
                    {
                        owners[g]=new LTMeshStamp[asset.groups[g].rocks.Length];
                        for(int r=0;r<owners[g].Length;r++)foreach(var stamp in stamps)if(stamp&&stamp.renderAsset==asset.groups[g].rocks[r])
                        {if(owners[g][r]){owners[g][r]=null;break;}owners[g][r]=stamp;}
                    }
                    foreach(var group in asset.groups)foreach(var binding in group.bindings)
                    {
                        if(binding.simplifiedTransition)transitions[binding.mesh]=binding.simplifiedTransition;
                        foreach(var data in binding.levels)prepared[data]=new LTRockSeamIndices.Plan(data.indices,data.bands);
                    }
                }
            }
            if(!asset)return;
            if(!ReferenceEquals(cachedTerrain,terrain)){chunks.Clear();foreach(var c in terrain)if(c)chunks[world.ChunkId(c.x,c.z)]=c;cachedTerrain=terrain;}
            bool changed=false;
            for(int g=0;g<next.Length;g++)
            {
                var group=asset.groups[g];int level=Math.Min(4,group.collapses.Length-1);bool valid=!world.rockSeamBakePending&&group.matrices!=null;
                var members=owners[g];
                for(int r=0;r<members.Length;r++)
                {
                    var owner=members[r];
                    if(!owner||!owner.isActiveAndEnabled||owner.renderAsset!=group.rocks[r]||owner.renderOutput==null||group.matrices==null||r>=group.matrices.Length){valid=false;continue;}
                    if(world.transform.worldToLocalMatrix*owner.transform.localToWorldMatrix!=group.matrices[r])valid=false;
                    level=Math.Min(level,owner.renderOutput.DesiredLOD());
                }
                foreach(var binding in group.terrain)
                {
                    if(!chunks.TryGetValue(world.ChunkId(binding.x,binding.z),out var c)||c.mesh!=binding.lod0||c.lodsPending){valid=false;continue;}
                    if(world.UseSpatialLODs)
                    {
                        var spatial=c.ActiveSpatialLOD;
                        if(!spatialReady||!spatial||spatial.vertexBank!=binding.bank||spatial.revision!=binding.spatialRevision){valid=false;continue;}
                        for(int p=0;p<spatial.patches.Length;p++)
                        {
                            var box=spatial.patches[p].bounds;box.center+=c.transform.localPosition;
                            if(box.Intersects(group.bounds))level=Math.Min(level,topology.Requested(world.ChunkId(binding.x,binding.z),p));
                        }
                    }
                    else
                    {
                        var saved=binding.legacy??Array.Empty<Mesh>();var live=c.lodMeshes??Array.Empty<Mesh>();
                        if(saved.Length!=live.Length)valid=false;
                        else for(int l=0;l<live.Length;l++)if(saved[l]!=live[l])valid=false;
                        int wanted=world.TerrainForcedLOD>=0?world.TerrainForcedLOD:world.ResolveRockSeamChunkLOD(c);
                        level=Math.Min(level,Math.Min(wanted,live.Length));
                    }
                }
                if(level>0)foreach(var owner in members)
                    if(!owner||!owner.renderAsset||level>=owner.renderAsset.transitions.Length||!transitions.ContainsKey(owner.renderAsset.transitions[level]))valid=false;
                next[g]=valid?Math.Max(0,level):0;changed|=next[g]!=levels[g];
            }
            if(changed||!mapsReady)
            {
                maps.Clear();Array.Copy(next,levels,next.Length);mapsReady=true;
                for(int g=0;g<levels.Length;g++)if(levels[g]>0)
                {
                    foreach(var binding in asset.groups[g].bindings)
                    {
                        if(!binding.mesh||levels[g]>=binding.levels.Length)continue;
                        var input=prepared[binding.levels[levels[g]]];
                        // Groups have disjoint bounds/triangle footprints. Reuse their
                        // prepared dictionaries; camera updates only link the plans.
                        maps[binding.mesh]=maps.TryGetValue(binding.mesh,out var output)?LTRockSeamIndices.Plan.Combine(output,input):input;
                    }
                }
            }
            for(int g=0;g<levels.Length;g++)foreach(var owner in owners[g])if(owner&&owner.renderOutput!=null)
            {
                var output=owner.renderOutput;output.SeamManaged=true;if(!managed.Contains(output))managed.Add(output);
                // Invalid/unready plans keep the original attachment; independent interior LOD still works.
                int level=levels[g]>0?levels[g]:output.DesiredLOD();
                if(!owner.renderAsset||!owner.renderAsset.Valid){output.SeamManaged=false;output.ApplySeamLOD(level,null);continue;}
                level=Math.Min(level,owner.renderAsset.transitions.Length-1);
                transitions.TryGetValue(owner.renderAsset.transitions[level],out var baked);
                output.ApplySeamLOD(level,null,levels[g]>0?baked:null);
            }
        }
        public void Dispose()
        {
            foreach(var output in managed){output.SeamManaged=false;output.ApplySeamLOD(output.CurrentLOD,null);}
            managed.Clear();maps.Clear();prepared.Clear();transitions.Clear();chunks.Clear();asset=null;stamps=null;revision=-1;cachedTerrain=null;mapsReady=false;
        }
    }
}
