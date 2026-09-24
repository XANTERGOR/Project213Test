using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // Explicit, transient GPU smoke test. Never opens/saves scenes or edits assets.
    public static class LTRoadCheck
    {
        [MenuItem("Tools/Local Terrain/Validate Road Rendering")]
        public static void Run()
        {
            var owned=new List<UnityEngine.Object>();
            var previous=RenderTexture.active;
            using var arrays=new LTLayerTextureArrays();
            var diagnostics=new List<string>();
            var diagnosticLock=new object();
            // ShaderHasError does NOT include native keyword assertions or the
            // device's texture-parameter limit. Those can be warnings/plain logs.
            // This callback may run on a render worker: never call Unity APIs here.
            void Capture(string message,string stack,LogType type)
            {
                if(type==LogType.Log &&
                    message.IndexOf("texture parameters",StringComparison.OrdinalIgnoreCase)<0 &&
                    message.IndexOf("keyword space",StringComparison.OrdinalIgnoreCase)<0)return;
                lock(diagnosticLock)
                    if(diagnostics.Count<16&&!diagnostics.Contains(message))diagnostics.Add(message);
            }
            void RequireClean(string stage)
            {
                lock(diagnosticLock)
                    if(diagnostics.Count>0)throw new InvalidOperationException(
                        "Road rendering check FAIL during "+stage+":\n"+string.Join("\n",diagnostics));
            }
            T Own<T>(T value) where T:UnityEngine.Object {value.hideFlags=HideFlags.HideAndDontSave;owned.Add(value);return value;}
            void Require(bool value,string message){if(!value)throw new InvalidOperationException("Road rendering check: "+message);}
            Texture2D Solid(Color value)
            {
                var texture=Own(new Texture2D(1,1,TextureFormat.RGBA32,false,true));
                texture.SetPixel(0,0,value);texture.Apply();return texture;
            }
            Application.logMessageReceivedThreaded+=Capture;
            try
            {
                foreach(var name in new[]{"LTEightLayers","LTEightLayersTessellation","LTGlobalLayerBake"})
                {
                    var shader=Resources.Load<Shader>(name);Require(shader,"Missing shader "+name);
                    Require(shader.isSupported,"Unsupported shader "+name);
                    var material=Own(new Material(shader));
                    Require(material.passCount>0,"No passes in "+name);
                    if(name!="LTGlobalLayerBake")
                    {
                        foreach(var required in new[]{"GBuffer","DepthOnly","ShadowCaster","Forward"})
                            Require(material.FindPass(required)>=0,"Missing HDRP pass "+required+" in "+name);
                    }
                    // Test both the normal terrain and the separate far-material variant.
                    int variants=name=="LTGlobalLayerBake"?1:2;
                    for(int variant=0;variant<variants;variant++)
                    {
                        if(variant==1)material.EnableKeyword("_LT_FAR_ONLY");
                        for(int pass=0;pass<material.passCount;pass++)
                        {
                            string stage=name+" / "+material.GetPassName(pass)+" / far="+(variant==1);
                            ShaderUtil.CompilePass(material,pass,true);
                            RequireClean(stage);
                            Require(!ShaderUtil.ShaderHasError(shader),"Shader compilation failed: "+stage);
                        }
                    }
                    // Some compiler diagnostics are stored without a Console event.
                    foreach(var message in ShaderUtil.GetShaderMessages(shader))
                        Require(message.severity.ToString()!="Error"&&message.severity.ToString()!="Warning",
                            name+" compiler "+message.severity+": "+message.message);
                }
                var baker=Own(new Material(Resources.Load<Shader>("LTGlobalLayerBake")));
                var texture=Own(new Texture2D(2,2,TextureFormat.RGBA32,false,true));
                texture.SetPixels(new[]{Color.red,Color.green,Color.blue,Color.yellow});texture.Apply();
                var palette=new List<LTSurfaceLayer>();
                var sourceNormal=Solid(new Color(1,.5f,1,.75f));
                for(int slice=0;slice<16;slice++)
                {
                    var layer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());
                    layer.baseColorMap=texture;layer.normalMap=sourceNormal;
                    palette.Add(layer);
                }
                Require(arrays.Ensure(palette,256,256,256,64,0,true,1),arrays.Status);
                var originalArray=arrays.color;
                Require(arrays.Ensure(palette,256,256,256,64,0,false,2)&&arrays.color==originalArray,"unchanged textures must reuse arrays");
                var localLayers=new List<LTSurfaceLayer>();
                for(int slot=0;slot<12;slot++)localLayers.Add(palette[(slot*5+3)%16]);
                arrays.Bind(baker,localLayers);
                for(int slot=0;slot<12;slot++)
                    Require(baker.GetVector("_LTLayerSlices"+(slot/4))[slot%4]==(slot*5+3)%16,"world slice remapping "+slot);
                var coordinates=Own(new Texture2DArray(257,257,1,TextureFormat.RGBAFloat,false,true));
                var map=new Color[257*257];for(int i=0;i<map.Length;i++)map[i]=new Color(.75f,.25f,0,1);
                coordinates.SetPixels(map,0);coordinates.Apply();
                baker.SetVector("_LTWorldSize",Vector4.one);baker.SetVector("_LTRect",new Vector4(0,0,1,1));
                baker.SetVector("_LTBakeRect",new Vector4(0,0,1,1));
                baker.SetTexture("_LTWeights0",Solid(new Color(0,1,0,0)));baker.SetTexture("_LTWeights1",Solid(Color.clear));
                baker.SetTexture("_LTWeights2",Solid(Color.clear));
                baker.SetFloat("_LTBaseOnly",0);baker.SetFloat("_LTHeightBlend",0);
                baker.SetVector("_LTTint1",Vector4.one);
                baker.SetVector("_LTTiling1",new Vector4(1,1,0,0));baker.SetVector("_LTSettings1",new Vector4(1,0,.5f,0));
                baker.SetVector("_LTFlags1",new Vector4(1,1,1,0));
                baker.SetTexture("_LTRoadProjectionMap",coordinates);
                baker.SetVector("_LTRoadProjectionSlots0",new Vector4(0,1,0,0));
                var target=Own(new RenderTexture(4,4,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));
                Require(target.Create(),"Cannot allocate render target");
                var read=Own(new Texture2D(4,4,TextureFormat.RGBAFloat,false,true));
                Color[] Draw(int pass,bool spline)
                {
                    baker.SetFloat("_LTRoadProjectionEnabled",spline?1:0);
                    using(var commands=new CommandBuffer())
                    {
                        commands.SetRenderTarget(target);commands.ClearRenderTarget(false,true,Color.magenta);
                        commands.SetViewport(new Rect(0,0,4,4));
                        commands.DrawProcedural(Matrix4x4.identity,baker,pass,MeshTopology.Triangles,6);
                        Graphics.ExecuteCommandBuffer(commands);
                    }
                    RenderTexture.active=target;read.ReadPixels(new Rect(0,0,4,4),0,0);read.Apply();return read.GetPixels();
                }
                foreach(var color in Draw(0,true))Require(color.g>.97f&&color.r<.03f&&color.b<.03f,"Spline UV did not select green texel");
                bool different=false;foreach(var color in Draw(0,false))different|=color.r>.1f||color.b>.1f;
                Require(different,"Disabling spline projection did not restore world mapping");
                var worldNormal=Draw(1,false)[0];var roadNormal=Draw(1,true)[0];
                Require(worldNormal.r>worldNormal.g+.05f&&roadNormal.g>roadNormal.r+.05f,"Normal orientation does not follow the road");
                baker.SetTexture("_LTWeights0",Solid(Color.clear));
                baker.SetVector("_LTRoadProjectionSlots0",Vector4.zero);
                for(int slot=0;slot<12;slot++)
                {
                    var weight=Color.clear;weight[slot%4]=1;
                    var mapping=Vector4.zero;mapping[slot%4]=1;
                    for(int group=0;group<3;group++)
                    {
                        baker.SetTexture("_LTWeights"+group,Solid(group==slot/4?weight:Color.clear));
                        baker.SetVector("_LTRoadProjectionSlots"+group,group==slot/4?mapping:Vector4.zero);
                    }
                    baker.SetVector("_LTTint"+slot,Vector4.one);
                    baker.SetVector("_LTTiling"+slot,new Vector4(1,1,0,0));
                    baker.SetVector("_LTSettings"+slot,new Vector4(1,0,.5f,0));
                    baker.SetVector("_LTFlags"+slot,new Vector4(1,1,1,0));
                    foreach(var color in Draw(0,true))Require(color.g>.97f&&color.r<.03f&&color.b<.03f,"Spline/weight mapping failed for slot "+slot);
                    worldNormal=Draw(1,false)[0];roadNormal=Draw(1,true)[0];
                    Require(worldNormal.r>worldNormal.g+.05f&&roadNormal.g>roadNormal.r+.05f,"Normal rotation failed for slot "+slot);
                }
                // Unique linear source colors exercise actual slice selection + sRGB
                // conversion through the production packer, not just mapping uniforms.
                var expected=new Color[16];
                for(int slice=0;slice<16;slice++)
                {
                    expected[slice]=new Color(.03f+.05f*slice,.8f-.04f*slice,.2f+.02f*slice,1);
                    palette[slice].baseColorMap=Solid(expected[slice]);
                }
                Require(arrays.Ensure(palette,256,256,256,64,0,true,1),arrays.Status);
                Require(arrays.color!=originalArray,"changed sources must replace arrays");
                arrays.Bind(baker,localLayers);
                for(int slot=0;slot<12;slot++)
                {
                    for(int group=0;group<3;group++)
                    {
                        var w=Color.clear;if(group==slot/4)w[slot%4]=1;
                        baker.SetTexture("_LTWeights"+group,Solid(w));
                    }
                    var actual=Draw(0,false)[0];var wanted=expected[(slot*5+3)%16];
                    Require(Mathf.Abs(actual.r-wanted.r)<.015f&&Mathf.Abs(actual.g-wanted.g)<.015f&&Mathf.Abs(actual.b-wanted.b)<.015f,
                        "array slice/sRGB roundtrip failed at local slot "+slot+": "+actual+" expected "+wanted);
                }
                RequireClean("GPU bake/readback");
                // Do not claim a full HDRP scene draw: readback above uses the baker.
                Debug.Log("PASS Road Rendering: requested near/far shader variants compiled without captured warnings/errors; production array packing/cache, 16-slice palette, all 12 remapped slots, color roundtrip, spline UV and normal rotation rendered/read back through baker. Full HDRP terrain visibility still requires a scene check. User scene/assets unchanged; not a performance test.");
            }
            finally
            {
                Application.logMessageReceivedThreaded-=Capture;
                RenderTexture.active=previous;
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])UnityEngine.Object.DestroyImmediate(owned[i]);
            }
        }
    }
}
