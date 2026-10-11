using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTCoverageSamplingCheck
    {
        const string Root="Assets/TerrainSystem/LocalTerrain/";
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        static string Hash(string path)
        {using var stream=File.OpenRead(path);using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(stream));}
        static float Difference(Color a,Color b)
        {float result=0;for(int i=0;i<4;i++)result=Mathf.Max(result,Mathf.Abs(a[i]-b[i]));return result;}

        [MenuItem("Tools/Local Terrain/Validate Shared Coverage Sampling (no scene changes)")]
        public static void Run()=>RunValidation(false);

        [MenuItem("Tools/Local Terrain/Validate Fast Live Layers (no scene changes)")]
        public static void RunFast()=>RunValidation(true);

        static void RunValidation(bool fast)
        {
            if(EditorApplication.isCompiling||EditorApplication.isPlayingOrWillChangePlaymode||LTLayerArraySaving.IsSaving)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var files=Directory.GetFiles(Root,"*",SearchOption.AllDirectories).Concat(scenes.Select(s=>s.scene.path))
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var owned=new List<Object>();T Own<T>(T value)where T:Object{owned.Add(value);return value;}
            var report=new StringBuilder(fast?"[Fast Live Layers v1]\n":"[Shared Coverage Sampling v2: active road UV]\n");
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite,wire=GL.wireframe,async=ShaderUtil.allowAsyncCompilation;
            string folder="Logs/LayerArrayChecks/"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-coverage-"+Guid.NewGuid().ToString("N");
            Exception failure=null;
            try
            {
                Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null,"A graphics device is required.");
                ShaderUtil.allowAsyncCompilation=false;GL.sRGBWrite=false;GL.wireframe=false;
                report.AppendLine("GPU: "+SystemInfo.graphicsDeviceName+" / "+SystemInfo.graphicsDeviceType);
                var shader=AssetDatabase.LoadAssetAtPath<Shader>(Root+"Editor/LTCoverageSamplingProbe.shader");
                Require(shader&&shader.isSupported,"Coverage probe shader unavailable.");
                var material=Own(new Material(shader));
                if(fast){material.EnableKeyword("_LT_FAST_LAYERS");material.EnableKeyword("_LT_FAST_ROADS");}
                material.SetVector("_LTFastLayerParams",Vector4.zero);
                int reference=material.FindPass("Reference"),optimized=material.FindPass("Optimized");
                Require(reference>=0&&optimized>=0,"A/B probe passes missing.");
                ShaderUtil.CompilePass(material,reference,true);ShaderUtil.CompilePass(material,optimized,true);
                Require(!ShaderUtil.ShaderHasError(shader),"A/B shader compilation failed: "+string.Join("; ",ShaderUtil.GetShaderMessages(shader).Select(m=>m.message)));
                var target=Own(new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));
                Require(target.Create(),"Probe target allocation failed.");
                var read=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                Color Pixel(int pass)
                {
                    var sentinel=new Color(-19,-23,-29,-31);
                    RenderTexture.active=target;GL.Clear(false,true,sentinel);
                    Require(material.SetPass(pass),"Probe SetPass failed.");Graphics.DrawProceduralNow(MeshTopology.Triangles,3);
                    read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply(false,false);var pixel=read.GetPixel(0,0);
                    for(int i=0;i<4;i++)Require(!float.IsNaN(pixel[i])&&!float.IsInfinity(pixel[i]),"Non-finite probe pixel.");
                    Require(Difference(pixel,sentinel)>.1f,"Probe did not draw: clear pixel survived.");
                    return pixel;
                }
                var source=new Texture2DArray[3];var packed=new Texture2DArray[3];
                for(int kind=0;kind<3;kind++)
                {
                    const int size=16;
                    source[kind]=Own(new Texture2DArray(size,size,12,TextureFormat.RGBA32,true,kind!=0)
                    {name="Coverage test array "+kind,wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Trilinear});
                    for(int slice=0;slice<12;slice++)
                    {
                        var pixels=new Color[size*size];
                        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                        {
                            float u=x/(float)(size-1),v=y/(float)(size-1),s=slice/11f;
                            var n=new Vector3((u-.5f)*.5f,(v-.5f)*.4f,1).normalized;
                            pixels[y*size+x]=kind==0?new Color(.1f+.7f*u,.1f+.5f*v,.15f+.65f*s,1):
                                kind==1?new Color(n.x*.5f+.5f,n.y*.5f+.5f,n.z*.5f+.5f,1):
                                new Color(.3f+.6f*v,.1f+.8f*Mathf.Repeat(u+s,1),.2f+.7f*s,1);
                        }
                        source[kind].SetPixels(pixels,slice);
                    }
                    source[kind].Apply(true,false);
                    packed[kind]=Own(LTLayerArraySaving.ReadCompressedForSaving(source[kind],"Coverage packed "+kind,
                        LTLayerArrayStorageUtility.Format(LTLayerArrayStorage.BC7_BC5,kind),false));
                }
                var coverage=new Texture2D[3];
                for(int i=0;i<3;i++)
                {
                    coverage[i]=Own(new Texture2D(257,257,TextureFormat.RGBAFloat,false,true)
                        {name="Coverage weights "+i,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp});
                    material.SetTexture("_LTWeights"+i,coverage[i]);
                }
                void Coverage(int profile)
                {
                    for(int group=0;group<3;group++)
                    {
                        var pixels=new Color[257*257];
                        for(int y=0;y<257;y++)for(int x=0;x<257;x++)
                        {
                            float u=x/256f,v=y/256f;var pixel=Color.clear;
                            for(int channel=0;channel<4;channel++)
                            {
                                int slot=group*4+channel;
                                pixel[channel]=profile==0?(slot==0?1:0):
                                    profile==1?(slot==4?u:slot==11?1-u:0):
                                    profile==2?.01f+Mathf.Repeat((slot+1)*.071f+u*.3f+v*.17f,.11f):
                                    profile==3?0:slot==0?.7f:slot==7?.3f:slot==11?.000009f:slot==4?.000011f:0;
                            }
                            pixels[y*257+x]=pixel;
                        }
                        coverage[group].SetPixels(pixels);coverage[group].Apply(false,false);
                    }
                }
                material.SetVector("_LTRect",new Vector4(-3,-2,7,9));
                material.SetMatrix("_LTSurfaceWorldToLocal",Matrix4x4.identity);
                material.SetVector("_LTLayerSlices0",new Vector4(0,1,2,3));
                material.SetVector("_LTLayerSlices1",new Vector4(4,5,6,7));
                material.SetVector("_LTLayerSlices2",new Vector4(8,9,10,11));
                for(int slot=0;slot<12;slot++)
                {
                    material.SetVector("_LTTiling"+slot,new Vector4(1+slot*.13f,1+slot*.19f,slot*.017f,slot*.031f));
                    material.SetVector("_LTTint"+slot,new Vector4(.7f+slot*.02f,1,.9f,1));
                    material.SetVector("_LTSettings"+slot,new Vector4(.8f+slot*.03f,slot*.04f,.3f,slot%4==0?0:1));
                }
                // Real 257x257 spline map: off, legacy bilinear and owner/Jacobian variants.
                var road=Own(new Texture2DArray(257,257,2,TextureFormat.RGBAFloat,false,true));
                var roadPixels=new Color[257*257];var jacobian=new Color[257*257];
                for(int y=0;y<257;y++)for(int x=0;x<257;x++)
                {
                    roadPixels[y*257+x]=new Color(x/256f,y/256f,1,0);
                    jacobian[y*257+x]=new Color(1f/7,0,0,1f/9);
                }
                road.SetPixels(roadPixels,0);road.SetPixels(jacobian,1);road.Apply(false,false);
                material.SetTexture("_LTRoadProjectionMap",road);
                var points=new[]{new Vector3(-3,.7f,-2),new Vector3(4,-.4f,7),new Vector3(.21f,1.37f,2.83f),new Vector3(-3.01f,.3f,7.01f)};
                var normals=new[]{Vector3.up,Vector3.right,Vector3.forward,new Vector3(.55f,.72f,.42f).normalized,new Vector3(-.8f,-.3f,.5f).normalized};
                float maxError=0;int samples=0,zeroSamples=0;
                for(int profile=0;profile<5;profile++)
                {
                    Coverage(profile);
                    for(int test=0;test<60;test++)
                    {
                        bool compressed=(test&1)!=0;int baseOnly=test%17==0?1:0,roadMode=test%3;
                        var arrays=compressed?packed:source;
                        material.SetTexture("_LTColorArray",arrays[0]);material.SetTexture("_LTNormalArray",arrays[1]);material.SetTexture("_LTMaskArray",arrays[2]);
                        material.SetFloat("_LTBaseOnly",baseOnly);material.SetFloat("_LTHeightBlend",(test/3)%3*.5f);
                        material.SetFloat("_LTTriplanar",test%4==0?0:1);
                        material.SetFloat("_LTRoadProjectionEnabled",roadMode==0?0:1);
                        material.SetVector("_LTRoadProjectionSlots0",new Vector4(roadMode==2?-1:1,0,0,0));
                        material.SetVector("_LTRoadProjectionSlots1",new Vector4(roadMode==2?-1:1,0,0,0));
                        material.SetVector("_LTRoadProjectionSlots2",new Vector4(0,0,0,roadMode==2?-1:1));
                        material.SetVector("_CheckPoint",points[test%points.Length]);
                        material.SetVector("_LTProbeNormal",normals[test%normals.Length]);
                        material.SetVector("_CheckDx",test%2==0?Vector4.zero:new Vector4(.007f,.013f,.021f,0));
                        material.SetVector("_CheckDy",test%2==0?Vector4.zero:new Vector4(.011f,.017f,.009f,0));
                        for(int slot=0;slot<12;slot++)material.SetVector("_LTFlags"+slot,
                            new Vector4(slot%5==0?0:compressed?2:1,.8f,.7f+slot*.05f,(slot%3-1)*.1f));
                        for(int mode=0;mode<3;mode++)
                        {
                            material.SetVector("_LTProbeUV",new Vector4(0,0,mode,0));
                            Color expected=Pixel(reference),actual=Pixel(optimized);
                            float error=Difference(expected,actual);
                            Require(error<.00005f,$"A/B mismatch: coverage {profile}, case {test}, output {mode}: {error}");
                            if(mode==0&&profile==3&&baseOnly==0)
                            {Require(Difference(actual,Color.clear)<.00005f,"Zero coverage must yield zero color/AO.");zeroSamples++;}
                            if(mode==0&&profile==0&&baseOnly==0)Require(actual.r>.001f&&actual.a>.05f,"Single-layer liveness check failed.");
                            maxError=Mathf.Max(maxError,error);samples++;
                        }
                    }
                }
                // Sensitivity control: a broken readback/constant shader must not pass A/B.
                Coverage(0);material.SetFloat("_LTBaseOnly",1);material.SetFloat("_LTTriplanar",0);
                material.SetVector("_LTProbeUV",Vector4.zero);material.SetVector("_LTTint0",Vector4.one);var bright=Pixel(optimized);
                material.SetVector("_LTTint0",Vector4.zero);var black=Pixel(optimized);
                Require(bright.r>.01f&&black.r<.00001f&&Difference(bright,black)>.01f,"Probe failed tint sensitivity control.");
                report.AppendLine($"PASS frozen-reference A/B: {samples} paired GPU samples; all 12 slots, RGBA32/BC7/BC5, planar/triplanar, height blend, normals/AO/smoothness/metallic, chunk edges; fast={fast}, zero tolerances. Max error {maxError:G9}.");
                report.AppendLine($"PASS liveness: sentinel + finite checks, single layer, {zeroSamples} zero-coverage samples, tint sensitivity.");
                if(fast)
                {
                    material.SetFloat("_LTBaseOnly",0);material.SetFloat("_LTHeightBlend",0);
                    material.SetFloat("_LTTriplanar",0);material.SetFloat("_LTRoadProjectionEnabled",0);
                    material.SetVector("_LTTint0",Vector4.one);
                    for(int group=0;group<3;group++)
                    {
                        var weights=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                        weights.SetPixel(0,0,group==0?new Color(.998f,.001f,.001f,0):Color.clear);weights.Apply();
                        material.SetTexture("_LTWeights"+group,weights);
                    }
                    material.SetVector("_LTFastLayerParams",new Vector4(.03f,0,0,0));
                    for(int mode=0;mode<3;mode++)
                    {
                        material.SetVector("_LTProbeUV",new Vector4(0,0,mode,0));
                        var pruned=Pixel(optimized);
                        material.SetFloat("_LTBaseOnly",1);var one=Pixel(reference);material.SetFloat("_LTBaseOnly",0);
                        Require(Difference(pruned,one)<.00005f,"Weak layers were not pruned to the surviving layer.");
                    }
                    var raster=Own(new Material(Resources.Load<Shader>("LTEightLayers")));
                    raster.EnableKeyword("_LT_FAST_LAYERS");
                    foreach(string passName in new[]{"GBuffer","Forward","DepthOnly","ShadowCaster"})
                    {
                        int pass=raster.FindPass(passName);Require(pass>=0,"Missing raster pass: "+passName);
                        ShaderUtil.CompilePass(raster,pass,true);
                    }
                    Require(!ShaderUtil.ShaderHasError(raster.shader),"Fast HDRP raster shader failed: "+string.Join("; ",ShaderUtil.GetShaderMessages(raster.shader).Select(m=>m.message)));
                    report.AppendLine("PASS weak-layer pruning GPU readback + four fast HDRP raster passes. Live-scene FPS and visual projection quality still require A/B.");
                }
                else
                {
                // Isolate every slot around the sampling threshold. A tiny weight must
                // retain the old default material contribution, not become a skipped layer.
                // Both sides of an ownership boundary exercise the network Jacobian path.
                for(int y=0;y<257;y++)for(int x=0;x<257;x++)
                {
                    float owner=x<128?1:2,angle=x<128?.7f:-1.2f;
                    roadPixels[y*257+x]=new Color(x/256f+(owner-1)*.23f,y/256f+.11f,Mathf.Cos(angle)*owner,Mathf.Sin(angle)*owner);
                }
                road.SetPixels(roadPixels,0);road.Apply(false,false);
                var constantWeights=new Texture2D[3];
                for(int group=0;group<3;group++)
                {
                    constantWeights[group]=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                    material.SetTexture("_LTWeights"+group,constantWeights[group]);
                }
                var thresholds=new[]{0f,.000009f,.00001f,.000011f,.5f};
                int thresholdSamples=0;float thresholdError=0;
                for(int slot=0;slot<12;slot++)foreach(float weightValue in thresholds)
                {
                    for(int group=0;group<3;group++)
                    {
                        var value=Color.clear;if(group==slot/4)value[slot%4]=weightValue;
                        constantWeights[group].SetPixel(0,0,value);constantWeights[group].Apply(false,false);
                    }
                    for(int storage=0;storage<2;storage++)for(int roadMode=1;roadMode<=2;roadMode++)for(int height=0;height<2;height++)
                    {
                        var arrays=storage==0?source:packed;
                        material.SetTexture("_LTColorArray",arrays[0]);material.SetTexture("_LTNormalArray",arrays[1]);material.SetTexture("_LTMaskArray",arrays[2]);
                        material.SetFloat("_LTBaseOnly",0);material.SetFloat("_LTHeightBlend",height);
                        material.SetFloat("_LTTriplanar",height);material.SetFloat("_LTRoadProjectionEnabled",1);
                        float roadSlot=roadMode==1?1:-1;
                        for(int group=0;group<3;group++)material.SetVector("_LTRoadProjectionSlots"+group,Vector4.one*roadSlot);
                        material.SetVector("_CheckPoint",new Vector3(-3+7*(height==0?127.4f:127.6f)/256f,.83f,2.4f));
                        material.SetVector("_LTProbeNormal",normals[3]);
                        material.SetVector("_CheckDx",new Vector4(.009f,.011f,.013f,0));
                        material.SetVector("_CheckDy",new Vector4(.017f,.019f,.023f,0));
                        for(int i=0;i<12;i++)
                        {
                            material.SetVector("_LTTint"+i,Vector4.one);
                            material.SetVector("_LTFlags"+i,new Vector4(storage==0?1:2,.8f,.7f+i*.05f,(i%3-1)*.1f));
                        }
                        for(int mode=0;mode<3;mode++)
                        {
                            material.SetVector("_LTProbeUV",new Vector4(0,0,mode,0));
                            Color expected=Pixel(reference),actual=Pixel(optimized);float error=Difference(expected,actual);
                            Require(error<.00005f,$"Threshold A/B mismatch: slot {slot}, weight {weightValue}, storage {storage}, road {roadMode}, height {height}, output {mode}: {error}");
                            if(mode==0&&weightValue<=.00001f)Require(Mathf.Max(actual.r,Mathf.Max(actual.g,actual.b))<.00001f,"An unsampled slot acquired color.");
                            if(mode==0&&weightValue<=.00001f)Require(Mathf.Abs(actual.a-weightValue/.00001f)<.00005f,"Tiny positive weight lost its default AO contribution.");
                            if(mode==0&&weightValue>.00001f)Require(actual.r>.001f,"Active slot lost its color.");
                            if(mode==1&&weightValue<=.00001f)Require(Mathf.Max(Mathf.Abs(actual.r),Mathf.Max(Mathf.Abs(actual.g),Mathf.Abs(actual.b)))<.00001f,"An unsampled flat normal acquired a slope.");
                            thresholdError=Mathf.Max(thresholdError,error);thresholdSamples++;
                        }
                    }
                }
                report.AppendLine($"PASS active road UV thresholds: {thresholdSamples} paired GPU samples; all slots; zero/below/equal/above threshold and active weight; RGBA32/BC7/BC5; rotated legacy/network map with owner boundary; default normal preserved. Max error {thresholdError:G9}.");
                // Includes the corrected six-vertex baker probe and production raster compilation.
                Require(LTLayerArrayCompressionCheck.RunValidation(),"Compressed Layer Arrays v2 failed; see its report.");
                report.AppendLine("PASS Compressed Layer Arrays v2, corrected full-quad terrain probe and production raster shaders.");
                }
            }
            catch(Exception error){failure=error;}
            finally
            {
                RenderTexture.active=previous;GL.sRGBWrite=srgb;GL.wireframe=wire;ShaderUtil.allowAsyncCompilation=async;
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                try
                {
                    foreach(var scene in scenes)Require(scene.scene.IsValid()&&scene.scene.isLoaded&&scene.scene.isDirty==scene.dirty&&scene.scene.rootCount==scene.roots,"User scene changed.");
                    foreach(var file in files)Require(File.Exists(file.Key)&&Hash(file.Key)==file.Value,"Source/scene/settings file changed: "+file.Key);
                    report.AppendLine("PASS scene state + source/settings file hashes unchanged.");
                }
                catch(Exception error){failure=failure==null?error:new AggregateException(failure,error);}
                report.AppendLine(failure==null?$"PASS {(fast?"fast live layers":"shared coverage")}. Live GPU performance is not measured by this test.":"FAIL: "+failure);
                Directory.CreateDirectory(folder);File.WriteAllText(folder+"/report.txt",report.ToString());
                if(failure==null)Debug.Log(report+"\nReport: "+folder+"/report.txt");else Debug.LogError(report+"\nReport: "+folder+"/report.txt");
            }
        }
    }
}
