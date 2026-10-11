using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed class LTRockSeamAsset : ScriptableObject
    {
        [Serializable] public sealed class Map { public int[] indices; public LTRockSeamIndices.Band[] bands; }
        [Serializable] public sealed class Terrain
        {
            public int x,z,spatialRevision;
            public Mesh lod0,bank;
            public Mesh[] legacy;
        }
        [Serializable] public sealed class Binding
        {
            public Mesh mesh;
            public Map[] levels;
            public Mesh simplifiedTransition;
        }
        [Serializable] public sealed class Group
        {
            public Bounds bounds;
            public LTRockRenderAsset[] rocks;
            public Matrix4x4[] matrices;
            public Terrain[] terrain;
            public Binding[] bindings;
            public int[] collapses;
        }
        public Group[] groups=Array.Empty<Group>();
        public string ownerKey;
        public int revision;
    }
}
