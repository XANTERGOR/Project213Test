using UnityEngine;

namespace LocalTerrainPrototype
{
    // Immutable at runtime. Only the owning editor bake updates these subassets.
    public sealed class LTRockRenderAsset : ScriptableObject
    {
        public Mesh combined;
        public Mesh[] rocks,transitions;
        public int sourceSubmeshes;
        public bool atlasUV;
        // False on old assets: every LOD uses the original shared atlas.
        public bool rockAtlasPerLOD;
        public string ownerKey;
        public bool Valid=>combined&&rocks!=null&&transitions!=null&&rocks.Length>0&&rocks.Length==transitions.Length&&
            System.Array.TrueForAll(rocks,m=>m)&&System.Array.TrueForAll(transitions,m=>m);
        public bool Owns(Mesh mesh)=>mesh&&(mesh==combined||(rocks!=null&&System.Array.IndexOf(rocks,mesh)>=0)||(transitions!=null&&System.Array.IndexOf(transitions,mesh)>=0));
    }
}
