using UnityEngine;
namespace LocalTerrainPrototype
{
    public sealed class LTSpatialLODAsset : ScriptableObject
    {
        public Mesh vertexBank;
        public LTSpatialLODPatch[] patches;
        public LTSpatialLODCell[] cells;
        public int formatVersion,divisions;
        public int revision;
    }
}
