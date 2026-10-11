using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Compatibility data ONLY for assets saved by the retired experimental road baker.
    // Keep this MonoScript GUID and serialized fields so existing assets remain readable.
    // Roads no longer reference, generate or activate this format.
    public sealed class LTRoadSurfaceBake : ScriptableObject
    {
        public int version;
        public LTSurfaceLayer layer;
        public LTSurfaceLayer groundSource, wheelSource;
        [HideInInspector] public LTSurfaceLayer groundSnapshot, wheelSnapshot;
        [HideInInspector] public TextureHashes groundContent, wheelContent;
        public Inputs inputs;

        [Serializable]
        public struct TextureHashes
        {
            public Hash128 color, normal, mask;
        }

        [Serializable]
        public struct Inputs
        {
            public Vector4 groundUV, wheelUV, tracks;
            public float across, repeat, heightBlend;
            public int resolution;
            public Vector3Int sourceResolution;
            public ColorSpace colorSpace;
        }
    }
}
