using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Paint owns this cache. Camera callbacks only bind an existing pair; they never bake.
    public sealed class LTRockLODAtlases : IDisposable
    {
        readonly LTRockRenderOutput output;
        readonly LTMeshStamp stamp;
        readonly Material material;
        readonly Mesh[] meshes;
        readonly LTRockMaterialBake[] bakes;
        bool disposed;
        public int Count=>bakes.Length;
        public bool Ready=>Array.TrueForAll(bakes,b=>b!=null);
        public LTRockMaterialBake Get(int level)=>bakes[output.rockAtlasPerLOD?Mathf.Clamp(level,0,bakes.Length-1):0];
        public static int Resolution(int full,int level)=>Math.Min(full,Math.Max(128,full>>Mathf.Clamp(level,0,4)));
        public LTRockLODAtlases(LTRockRenderOutput output,LTMeshStamp stamp,Material material)
        {
            this.output=output;this.stamp=stamp;this.material=material;meshes=output.RockLODs;
            bakes=new LTRockMaterialBake[output.rockAtlasPerLOD?meshes.Length:1];
            output.LODChanged+=Bind;RefreshBinding();
        }
        public bool BakeNext(LTWorld world,Material[] sources)
        {
            if(disposed||!output.atlasUV)return false;
            for(int i=0;i<bakes.Length;i++)if(bakes[i]==null)
            {
                bakes[i]=LTRockMaterialBake.Build(meshes[i],stamp.transform,world,sources,Resolution(stamp.materialFarResolution,i));
                RefreshBinding();return true;
            }
            return false;
        }
        public void RefreshBinding()=>Bind(output.CurrentLOD);
        void Bind(int level)
        {
            if(disposed||!material)return;
            var bake=Get(level);
            if(bake!=null)bake.Bind(material,stamp);
            else Clear(); // Pending LOD uses live layers, never another LOD's UV atlas.
        }
        void Clear()
        {
            if(!material)return;
            material.SetVector("_LTRockFarParams",Vector4.zero);
            material.SetTexture("_LTRockFarColor",null);material.SetTexture("_LTRockFarNormal",null);
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;output.LODChanged-=Bind;Clear();
            foreach(var bake in bakes)bake?.Dispose();
        }
    }
}
