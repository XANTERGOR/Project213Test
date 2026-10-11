using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTWorld
    {
        [HideInInspector] public Vector2Int chunkOrigin;
        // Zero is the backward-compatible layout of existing scenes. Freeze before expansion.
        [HideInInspector] public Vector2 fixedChunkSize;
        public Vector2 ChunkSize=>fixedChunkSize.x>0&&fixedChunkSize.y>0?fixedChunkSize:
            source?new Vector2(source.size.x/Mathf.Max(1,chunksX),source.size.z/Mathf.Max(1,chunksZ)):Vector2.one;
        public LTChunkGrid Grid=>new LTChunkGrid(chunkOrigin.x,chunkOrigin.y,chunksX,chunksZ,ChunkSize);
        public Rect TerrainRect=>Grid.Bounds;
        public int ChunkId(int x,int z)=>Grid.Id(x,z);
        public Vector2Int ChunkCoordinate(int id)=>Grid.Coordinate(id);
        public Rect ChunkRect(int x,int z)=>Grid.Rect(x,z);
        public void NotifyChunkLayoutChanged(){RefreshLODCache();}
    }
}
