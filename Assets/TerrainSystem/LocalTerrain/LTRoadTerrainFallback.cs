using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // Immutable, domain-owned defaults. D3D12 requires valid SRVs even when a
    // uniform Ready=0 skips their reads. Globals also cover disabled bindings and
    // the first draw after reload; renderer blocks override them only with live data.
    public static class LTRoadTerrainFallback
    {
        [StructLayout(LayoutKind.Sequential)] struct Chunk {public Vector4 rect,options,fastLayerParams;}
        [StructLayout(LayoutKind.Sequential)] struct Layer {public Vector4 tiling,tint,settings,flags,indices,asphaltStyle;}
        static ComputeBuffer chunks,layers;
        static Texture2D lookup;
        static Texture2DArray empty;
        static bool closing;
        static readonly string[] Arrays={"_LTRoadTerrainWeights","_LTRoadTerrainProjection","_LTRoadTerrainDamage",
            "_LTRoadTerrainColor","_LTRoadTerrainNormal","_LTRoadTerrainMask","_LTRoadTerrainCracks"};
        public static Texture EmptyArray {get{Ensure();return empty;}}

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            closing=false;
            RenderPipelineManager.beginContextRendering-=BeforeRender;
            RenderPipelineManager.beginContextRendering+=BeforeRender;
#if !UNITY_EDITOR
            Application.quitting-=Release;Application.quitting+=Release;
#endif
        }
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void InstallEditor()
        {
            Install();
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload-=Release;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload+=Release;
            UnityEditor.EditorApplication.quitting-=Release;
            UnityEditor.EditorApplication.quitting+=Release;
        }
#endif
        static void BeforeRender(ScriptableRenderContext context,List<Camera> cameras)=>Ensure();
        static void Ensure()
        {
            if(closing||chunks!=null||SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null)return;
            try
            {
                chunks=new ComputeBuffer(1,48);chunks.SetData(new Chunk[1]);
                layers=new ComputeBuffer(12,96);layers.SetData(new Layer[12]);
                lookup=new Texture2D(1,1,TextureFormat.RFloat,false,true)
                    {name="Road empty chunk lookup",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point};
                lookup.SetPixelData(new float[]{0},0);lookup.Apply(false,true);
                empty=new Texture2DArray(1,1,3,TextureFormat.RGBA32,false,true)
                    {name="Road empty terrain arrays",hideFlags=HideFlags.HideAndDontSave,wrapMode=TextureWrapMode.Clamp};
                for(int i=0;i<3;i++)empty.SetPixels(new[]{Color.clear},i);
                empty.Apply(false,true);
                Shader.SetGlobalBuffer("_LTRoadTerrainChunks",chunks);Shader.SetGlobalBuffer("_LTRoadTerrainLayers",layers);
                Shader.SetGlobalTexture("_LTRoadTerrainLookup",lookup);
                foreach(var name in Arrays)Shader.SetGlobalTexture(name,empty);
            }
            catch{Release();throw;}
        }
        public static void Bind(MaterialPropertyBlock block)
        {
            Ensure();
            block.SetFloat("_LTRoadTerrainReady",0);
            block.SetVector("_LTRoadTerrainGrid",Vector4.zero);block.SetVector("_LTRoadTerrainCell",new Vector4(1,1,0,0));
            if(chunks==null)return; // No graphics device or domain shutdown: no draw follows.
            block.SetBuffer("_LTRoadTerrainChunks",chunks);block.SetBuffer("_LTRoadTerrainLayers",layers);
            block.SetTexture("_LTRoadTerrainLookup",lookup);
            foreach(var name in Arrays)block.SetTexture(name,empty);
        }
        static void Release()
        {
            closing=true;RenderPipelineManager.beginContextRendering-=BeforeRender;
            Shader.SetGlobalBuffer("_LTRoadTerrainChunks",(ComputeBuffer)null);Shader.SetGlobalBuffer("_LTRoadTerrainLayers",(ComputeBuffer)null);
            Shader.SetGlobalTexture("_LTRoadTerrainLookup",null);foreach(var name in Arrays)Shader.SetGlobalTexture(name,null);
            chunks?.Dispose();layers?.Dispose();chunks=null;layers=null;
            Destroy(lookup);Destroy(empty);lookup=null;empty=null;
        }
        static void Destroy(Object obj)
        {if(obj){if(Application.isPlaying)Object.Destroy(obj);else Object.DestroyImmediate(obj);}}
    }
}
