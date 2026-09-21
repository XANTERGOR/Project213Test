using System;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // Explicit editor-only smoke test; never runs automatically or modifies scenes.
    public static class LTMudGpuCheck
    {
        [StructLayout(LayoutKind.Sequential)]
        struct ContactData { public int index; public float dose,limit,recovery,duration; }
        [MenuItem("Tools/Local Terrain/Checks/Mud GPU")]
        public static void Run()
        {
            if(!SystemInfo.supportsComputeShaders||!SystemInfo.supportsAsyncGPUReadback)
                throw new NotSupportedException("Compute/readback unavailable on this graphics device.");
            var shader=Resources.Load<ComputeShader>("LTMudSimulation");
            if(!shader)throw new InvalidOperationException("LTMudSimulation not imported.");
            RenderTexture depth=null,state=null;ComputeBuffer contacts=null,tiles=null;
            try
            {
                const int width=17,height=9;
                depth=new RenderTexture(width,height,0,RenderTextureFormat.RFloat){enableRandomWrite=true};
                state=new RenderTexture(width,height,0,RenderTextureFormat.RGFloat){enableRandomWrite=true};
                if(!depth.Create()||!state.Create())throw new InvalidOperationException("GPU mud textures unavailable.");
                int init=shader.FindKernel("Initialize"),contact=shader.FindKernel("Contact"),recover=shader.FindKernel("Recover");
                void Bind(int kernel,float now)
                {
                    shader.SetInt("_Width",width);shader.SetInt("_Height",height);
                    shader.SetInt("_TileColumns",3);shader.SetFloat("_Now",now);
                    shader.SetTexture(kernel,"_Depth",depth);shader.SetTexture(kernel,"_State",state);
                }
                void Check(float a,float b)
                {
                    // A deliberate blocking readback ONLY in this manually invoked test.
                    var request=AsyncGPUReadback.Request(depth,0);request.WaitForCompletion();
                    if(request.hasError)throw new InvalidOperationException("GPU readback failed.");
                    var values=request.GetData<float>();
                    for(int p=0;p<values.Length;p++)
                    {
                        float expected=p==0?a:p==width*height-1?b:0;
                        if(Mathf.Abs(values[p]-expected)>1e-5f)
                            throw new InvalidOperationException($"Mud GPU mismatch at {p}: {values[p]} expected {expected}");
                    }
                }
                Bind(init,0);shader.Dispatch(init,3,2,1);Check(0,0);
                contacts=new ComputeBuffer(2,20);
                contacts.SetData(new[]{
                    new ContactData{index=0,dose=.1f,limit=.2f,recovery=.02f,duration=.03f},
                    new ContactData{index=width*height-1,dose=.5f,limit=.2f,recovery=0,duration=.03f}});
                Bind(contact,1);shader.SetInt("_Count",2);shader.SetBuffer(contact,"_Contacts",contacts);
                shader.Dispatch(contact,1,1,1);Check(.1f,.2f);
                tiles=new ComputeBuffer(2,4);tiles.SetData(new[]{0,5});
                Bind(recover,3);shader.SetInt("_Count",2);shader.SetBuffer(recover,"_Tiles",tiles);
                shader.Dispatch(recover,2,1,1);Check(.06f,.2f);
                Bind(recover,10);shader.Dispatch(recover,2,1,1);Check(0,.2f);
                Debug.Log("PASS Mud GPU: init, sparse contacts, depth cap, rectangular edge tile, elapsed recovery, permanent track. Scene rendering/wheels still require testing.");
            }
            finally
            {
                contacts?.Release();tiles?.Release();
                if(depth){depth.Release();UnityEngine.Object.DestroyImmediate(depth);}
                if(state){state.Release();UnityEngine.Object.DestroyImmediate(state);}
            }
        }
    }
}
