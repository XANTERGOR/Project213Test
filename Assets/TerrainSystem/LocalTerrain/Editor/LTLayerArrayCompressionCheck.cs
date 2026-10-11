using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTLayerArrayCompressionCheck
    {
        const string Root="Assets/TerrainSystem/LocalTerrain/";
        static string Hash(string path)
        {using var stream=File.OpenRead(path);using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(stream));}
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}

        [MenuItem("Tools/Local Terrain/Validate Compressed Layer Arrays (no scene changes)")]
        public static void Run()=>RunValidation();

        internal static bool RunValidation()
        {
            if(EditorApplication.isCompiling||EditorApplication.isPlayingOrWillChangePlaymode||LTLayerArraySaving.IsSaving)return false;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var files=Directory.GetFiles(Root,"*",SearchOption.AllDirectories).Concat(scenes.Select(s=>s.scene.path))
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var owned=new List<Object>();T Own<T>(T value)where T:Object{owned.Add(value);return value;}
            var report=new StringBuilder("[Compressed Layer Arrays v2]\n");
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite,wire=GL.wireframe,async=ShaderUtil.allowAsyncCompilation;
            string folder="Logs/LayerArrayChecks/"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N");
            Exception failure=null;
            try
            {
                Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null,"A graphics device is required.");
                ShaderUtil.allowAsyncCompilation=false;GL.sRGBWrite=false;GL.wireframe=false;
                report.AppendLine("GPU: "+SystemInfo.graphicsDeviceName+" / "+SystemInfo.graphicsDeviceType);
                var probeShader=AssetDatabase.LoadAssetAtPath<Shader>(Root+"Editor/LTLayerArrayCompressionProbe.shader");
                Require(probeShader&&probeShader.isSupported,"Probe shader unavailable.");
                var probe=Own(new Material(probeShader));ShaderUtil.CompilePass(probe,0,true);
                Require(!ShaderUtil.ShaderHasError(probeShader),"Probe compilation failed.");
                var target=Own(new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));
                Require(target.Create(),"Probe render target allocation failed.");
                var read=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                Color Pixel(Material material,int pass,int vertices=3)
                {
                    var sentinel=new Color(-19,-23,-29,-31);
                    RenderTexture.active=target;GL.Clear(false,true,sentinel);
                    Require(material.SetPass(pass),"Probe pass unavailable.");Graphics.DrawProceduralNow(MeshTopology.Triangles,vertices);
                    read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply(false,false);
                    var pixel=read.GetPixel(0,0);
                    for(int channel=0;channel<4;channel++)Require(!float.IsNaN(pixel[channel])&&!float.IsInfinity(pixel[channel]),"Probe returned a non-finite pixel.");
                    Require(Mathf.Abs(pixel.r-sentinel.r)>.1f,"Probe did not render its target pixel.");
                    return pixel;
                }
                float Difference(Color a,Color b)=>Mathf.Max(Mathf.Abs(a.r-b.r),Mathf.Max(Mathf.Abs(a.g-b.g),Mathf.Abs(a.b-b.b)));
                var sources=new Texture2DArray[3];var packed=new Texture2DArray[3];var sourceHashes=new string[3];var packedHashes=new string[3];
                long sourceBytes=0,packedBytes=0;int samples=0;
                for(int kind=0;kind<3;kind++)
                {
                    const int size=32;
                    var source=sources[kind]=Own(new Texture2DArray(size,size,2,TextureFormat.RGBA32,true,kind!=0)
                    {name="Array source "+kind,wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Trilinear});
                    for(int slice=0;slice<2;slice++)
                    {
                        var pixels=new Color[size*size];
                        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                        {
                            float u=x/(float)(size-1),v=y/(float)(size-1);
                            var n=new Vector3((u-.5f)*.3f,(v-.5f)*.25f,1).normalized;
                            pixels[y*size+x]=kind==1?new Color(n.x*.5f+.5f,n.y*.5f+.5f,n.z*.5f+.5f,1):
                                new Color(.15f+.5f*u,.2f+.25f*v+slice*.1f,.3f+slice*.2f,1);
                        }
                        source.SetPixels(pixels,slice);
                    }
                    source.Apply(true,false);sourceHashes[kind]=LTLayerArraySaving.DataHash(source);
                    packed[kind]=Own(LTLayerArraySaving.ReadCompressedForSaving(source,"Compressed "+kind,
                        LTLayerArrayStorageUtility.Format(LTLayerArrayStorage.BC7_BC5,kind),false));
                    packedHashes[kind]=LTLayerArraySaving.DataHash(packed[kind]);
                    Require(sourceHashes[kind]==LTLayerArraySaving.DataHash(source),"Compression modified its source.");
                    Require(packed[kind].isDataSRGB==source.isDataSRGB,"Color space changed.");
                    sourceBytes+=LTLayerArrayStorageUtility.Bytes(source);packedBytes+=LTLayerArrayStorageUtility.Bytes(packed[kind]);
                }
                Directory.CreateDirectory(folder);
                string serialized=folder+"/compressed-arrays.bin";
                InternalEditorUtility.SaveToSerializedFileAndForget(packed.Cast<Object>().ToArray(),serialized,false);
                var reopened=InternalEditorUtility.LoadSerializedFileAndForget(serialized);
                foreach(var value in reopened)Own(value);
                Require(reopened.Length==3,"Serialized array count changed.");
                for(int kind=0;kind<3;kind++)
                {
                    var array=reopened.OfType<Texture2DArray>().Single(a=>a.name==packed[kind].name);
                    Require(array.format==packed[kind].format&&array.mipmapCount==sources[kind].mipmapCount&&array.depth==2,"Reloaded array layout changed.");
                    Require(LTLayerArraySaving.DataHash(array)==packedHashes[kind],"Compressed bytes changed after serialization.");
                    float maxError=0;
                    for(int slice=0;slice<2;slice++)for(int mip=0;mip<array.mipmapCount;mip++)foreach(float u in new[]{.13f,.41f,.77f})
                    foreach(float strength in kind==1?new[]{0f,.5f,1f,2f}:new[]{1f})
                    {
                        probe.SetVector("_ProbeUV",new Vector4(u,1-u,slice,mip));probe.SetFloat("_Strength",strength);
                        probe.SetTexture("_CheckArray",sources[kind]);probe.SetFloat("_NormalFlag",kind==1?1:0);var expected=Pixel(probe,0);
                        probe.SetTexture("_CheckArray",array);probe.SetFloat("_NormalFlag",kind==1?2:0);var actual=Pixel(probe,0);
                        float error=Difference(actual,expected);Require(!float.IsNaN(error)&&error<.045f,$"GPU sample error: kind {kind}, mip {mip}, slice {slice}, error {error}");
                        maxError=Mathf.Max(maxError,error);samples++;
                    }
                    report.AppendLine($"PASS {array.format}: 2 slices, {array.mipmapCount} mips, serialization hash, GPU samples; max RGB/vector error {maxError:F6}.");
                }
                Require(packedBytes<sourceBytes*.3,"Unexpected compressed memory size.");
                report.AppendLine($"PASS payload: {sourceBytes} -> {packedBytes} bytes; {samples} paired GPU samples.");

                // Actual shared terrain sampler: two overlapping height-blended layers,
                // planar and triplanar normals. Compare compressed against RGBA32.
                var bakerShader=Resources.Load<Shader>("LTGlobalLayerBake");Require(bakerShader,"Terrain baker shader missing.");
                var baker=Own(new Material(bakerShader));
                baker.SetVector("_LTBakeRect",new Vector4(0,0,1,1));
                int surfacePass=baker.FindPass("TerrainSurfaceProbe");Require(surfacePass>=0,"Terrain surface probe missing.");
                ShaderUtil.CompilePass(baker,surfacePass,true);
                Require(!ShaderUtil.ShaderHasError(bakerShader),"Terrain sampler compilation failed.");
                var weight=Own(new Texture2D(1,1,TextureFormat.RGBA32,false,true));weight.SetPixel(0,0,new Color(.55f,.45f,0,0));weight.Apply();
                // Weight alpha is a fourth layer, so require explicit RGBA zero.
                var emptyWeight=Own(new Texture2D(1,1,TextureFormat.RGBA32,false,true));emptyWeight.SetPixel(0,0,Color.clear);emptyWeight.Apply();
                baker.SetTexture("_LTWeights0",weight);baker.SetTexture("_LTWeights1",emptyWeight);baker.SetTexture("_LTWeights2",emptyWeight);
                baker.SetVector("_LTRect",new Vector4(0,0,1,1));baker.SetMatrix("_LTSurfaceWorldToLocal",Matrix4x4.identity);
                baker.SetVector("_LTLayerSlices0",new Vector4(0,1,0,0));baker.SetFloat("_LTBaseOnly",0);
                for(int i=0;i<12;i++)
                {
                    baker.SetVector("_LTTiling"+i,new Vector4(1,1,0,0));baker.SetVector("_LTTint"+i,Vector4.one);
                    baker.SetVector("_LTSettings"+i,new Vector4(1,.1f,.5f,i<2?1:0));
                }
                float blendError=0;
                foreach(float height in new[]{0f,1f})foreach(float projection in new[]{0f,1f})foreach(float mode in new[]{0f,1f})foreach(float u in new[]{.21f,.63f})
                {
                    baker.SetFloat("_LTHeightBlend",height);baker.SetFloat("_LTTriplanar",projection);
                    baker.SetVector("_LTProbeUV",new Vector4(u,1-u,mode,0));baker.SetVector("_LTProbeNormal",new Vector4(.3f,.9f,.2f,0));
                    Color Surface(bool compressed)
                    {
                        var arrays=compressed?packed:sources;
                        baker.SetTexture("_LTColorArray",arrays[0]);baker.SetTexture("_LTNormalArray",arrays[1]);baker.SetTexture("_LTMaskArray",arrays[2]);
                        for(int i=0;i<12;i++)baker.SetVector("_LTFlags"+i,new Vector4(i<2?(compressed?2:1):0,1,1,0));
                        // The baker uses a six-vertex quad, unlike the standalone triangle probe.
                        return Pixel(baker,surfacePass,6);
                    }
                    float error=Difference(Surface(false),Surface(true));Require(!float.IsNaN(error)&&error<.06f,"Height/triplanar blend exceeded tolerance: "+error);
                    blendError=Mathf.Max(blendError,error);
                }
                report.AppendLine($"PASS height blend + planar/triplanar: 16 paired GPU surface samples; max error {blendError:F6}.");
                foreach(string name in new[]{"LTEightLayers","LTEightLayersTessellation","LTRoadAuthoredGround","LTRoadGroundProbe"})
                {
                    var shader=name=="LTRoadGroundProbe"?UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/TerrainSystem/LocalTerrain/Editor/LTRoadGroundProbe.shader"):Resources.Load<Shader>(name);Require(shader&&shader.isSupported,"Shader unavailable: "+name);
                    var material=Own(new Material(shader));int compiled=0;
                    if(name.StartsWith("LTRoad",StringComparison.Ordinal))material.EnableKeyword("_LT_ROAD_TERRAIN");
                    for(int pass=0;pass<material.passCount;pass++)
                    {
                        string passName=material.GetPassName(pass);
                        if(passName.Contains("DXR")||passName.Contains("RayTracing")||passName.Contains("META"))continue;
                        ShaderUtil.CompilePass(material,pass,true);compiled++;
                    }
                    Require(compiled>0&&!ShaderUtil.ShaderHasError(shader),"Raster compilation failed: "+name+" / "+string.Join("; ",ShaderUtil.GetShaderMessages(shader).Select(m=>m.message)));
                    report.AppendLine("PASS raster shader: "+name+" / "+compiled+" passes.");
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
                report.AppendLine(failure==null?"PASS compressed arrays. Real terrain appearance and GPU performance still need comparison.":"FAIL: "+failure);
                Directory.CreateDirectory(folder);File.WriteAllText(folder+"/report.txt",report.ToString());
                if(failure==null)Debug.Log(report+"\nReport: "+folder+"/report.txt");else Debug.LogError(report+"\nReport: "+folder+"/report.txt");
            }
            return failure==null;
        }
    }
}
