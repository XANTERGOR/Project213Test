#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Read-only access to the near material, which is not necessarily the
    // renderer's current material (that can be the cheap global background).
    public struct LTFastLayerBenchmarkChunk
    {
        public LTChunk chunk;
        public MeshRenderer renderer;
        public Material liveMaterial;
        public bool roadSampling;
        public string exclusion;
    }
    public sealed partial class LTWorld
    {
        public LTFastLayerBenchmarkChunk[] CaptureFastLayerBenchmarkChunks()=>
            paintRuntime?.CaptureFastLayerBenchmarkChunks()??System.Array.Empty<LTFastLayerBenchmarkChunk>();
    }
    public sealed partial class LTPaintRuntime
    {
        internal LTFastLayerBenchmarkChunk[] CaptureFastLayerBenchmarkChunks()
        {
            var result=new List<LTFastLayerBenchmarkChunk>();
            foreach(var pair in chunks)
            {
                var s=pair.Value;
                bool roads=roadProjections.TryGetValue(s,out _)||asphaltStates.TryGetValue(s,out _);
                string reason=!s.ready||!s.material?"not ready":s.displacementActive?"displacement":
                    s.material.shader.name!="Local Terrain/Eight Layers HDRP"?"other shader":
                    s.material.GetFloat("_LTBaseOnly")>.5f?"base only":null;
                result.Add(new LTFastLayerBenchmarkChunk{chunk=pair.Key,renderer=s.renderer,liveMaterial=s.material,roadSampling=roads,exclusion=reason});
            }
            return result.ToArray();
        }
    }
}
#endif
