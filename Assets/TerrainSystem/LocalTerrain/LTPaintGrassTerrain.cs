using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTPaintRuntime
    {
        internal readonly LTGrassTerrainMap GrassTerrain=new LTGrassTerrainMap();
        public string GrassTerrainStatus=>GrassTerrain.Status;
        void TickGrassTerrain(LTWorld world)
        {
            var details=world.GetComponent<LTDetailRenderer>();
            if(!details||!details.isActiveAndEnabled||!details.UsesGrassTerrainTint){GrassTerrain.Dispose();return;}
            var tiles=new List<LTGrassTerrainMap.Tile>(chunks.Count);
            foreach(var pair in chunks)
            {
                var chunk=pair.Key;var state=pair.Value;
                if(!chunk||!chunk.isActiveAndEnabled||!chunk.mesh||!state.ready||!state.material)continue;
                var localToWorld=chunk.transform.localToWorldMatrix;
                tiles.Add(new LTGrassTerrainMap.Tile{id=chunk.GetInstanceID(),revision=state.grassColorRevision,
                    rect=new Rect(chunk.x*world.ChunkSize.x,chunk.z*world.ChunkSize.y,world.ChunkSize.x,world.ChunkSize.y),material=state.material,
                    mesh=world.triplanarTexturing?chunk.mesh:null,geometryRevision=world.triplanarTexturing?chunk.updatedAt:0,
                    localToWorld=world.triplanarTexturing?localToWorld:Matrix4x4.identity,
                    localToTerrain=world.triplanarTexturing?world.transform.worldToLocalMatrix*localToWorld:Matrix4x4.identity});
            }
            GrassTerrain.Synchronize(world.TerrainRect,world.transform.worldToLocalMatrix,tiles,world.triplanarTexturing);
            GrassTerrain.Advance();
        }
    }
}
