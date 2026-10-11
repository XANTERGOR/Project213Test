using UnityEngine;

namespace LocalTerrainPrototype
{
    // Exact shader inputs, independent of texture pixel revisions. Updating pixels in
    // the same RenderTexture is visible to existing bindings without an MPB rewrite.
    public readonly struct LTGrassTerrainBinding
    {
        readonly Texture texture;
        readonly bool ready;
        readonly Vector4 u,v;
        static readonly int Map=Shader.PropertyToID("_LTGrassTerrainMap"),Ready=Shader.PropertyToID("_LTGrassTerrainReady"),
            U=Shader.PropertyToID("_LTGrassTerrainU"),V=Shader.PropertyToID("_LTGrassTerrainV");
        public LTGrassTerrainBinding(Texture texture,bool ready,Vector4 u,Vector4 v)
        {this.texture=texture;this.ready=ready;this.u=u;this.v=v;}
        public bool SameInputs(LTGrassTerrainBinding other)=>ReferenceEquals(texture,other.texture)&&
            ready==other.ready&&u.Equals(other.u)&&v.Equals(other.v);
        public void Apply(MaterialPropertyBlock block)
        {
            block.SetFloat(Ready,ready?1:0);block.SetTexture(Map,texture);
            block.SetVector(U,u);block.SetVector(V,v);
        }
        // Use only with a privately owned block; external writes/Clear require Reset.
        // Each GPU Draw owns its block and this receipt for exactly the same lifetime.
        public struct Cache
        {
            MaterialPropertyBlock target;
            LTGrassTerrainBinding previous;
            public bool Apply(MaterialPropertyBlock block,LTGrassTerrainBinding value)
            {
                if(ReferenceEquals(target,block)&&previous.SameInputs(value))return false;
                value.Apply(block);previous=value;target=block;return true;
            }
            public void Reset(){target=null;previous=default;}
        }
    }
}
