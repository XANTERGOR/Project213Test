using UnityEngine;

namespace LocalTerrainPrototype
{
    // Imported adapter, never a replacement for the user's material/shader asset.
    public sealed class LTDetailGpuShader : ScriptableObject
    {
        public Shader source;
        public Shader indirect;
        // Serialized references retain the runtime clone's material keyword variants in players.
        [HideInInspector] public Material[] buildVariants;
        public string error;
    }
}
