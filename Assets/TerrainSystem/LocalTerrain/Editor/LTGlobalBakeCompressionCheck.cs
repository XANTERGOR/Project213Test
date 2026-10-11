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
    public static class LTGlobalBakeCompressionCheck
    {
        const string Root="Assets/TerrainSystem/LocalTerrain/";
        static void Require(bool value,string text){if(!value)throw new InvalidOperationException(text);}
        static string Hash(string path)
        {using var file=File.OpenRead(path);using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(file));}
        static string Pixels(Texture2D texture)
        {using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(texture.GetRawTextureData<byte>().ToArray()));}

        [MenuItem("Tools/Local Terrain/Checks/Validate Compressed Global Maps (no scene changes)")]
        public static void Run()
        {
            if(EditorApplication.isCompiling||EditorApplication.isPlayingOrWillChangePlaymode||LTGlobalBakeSaving.IsSaving)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var files=Directory.GetFiles(Root,"*",SearchOption.AllDirectories).Concat(scenes.Select(s=>s.scene.path))
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var owned=new List<Object>();T Own<T>(T item)where T:Object{owned.Add(item);return item;}
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite,wire=GL.wireframe,async=ShaderUtil.allowAsyncCompilation;
            string folder="Logs/GlobalBakeBC/"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N");
            var report=new StringBuilder("[LT Compressed Global Maps v1]\n");Exception failure=null;
            try
            {
                Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null,"Graphics device required.");
                GL.sRGBWrite=false;GL.wireframe=false;ShaderUtil.allowAsyncCompilation=false;
                report.AppendLine(SystemInfo.graphicsDeviceName+" / "+SystemInfo.graphicsDeviceType);
                var shader=AssetDatabase.LoadAssetAtPath<Shader>(Root+"Editor/LTGlobalBakeCompressionProbe.shader");
                Require(shader&&shader.isSupported,"Probe unavailable.");var probe=Own(new Material(shader));
                ShaderUtil.CompilePass(probe,0,true);Require(!ShaderUtil.ShaderHasError(shader),"Normal decoder probe compile failed.");
                var target=Own(new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));Require(target.Create(),"Readback target failed.");
                var read=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                Color Sample(Texture texture,int kind,int mip,float u,float v)
                {
                    probe.SetTexture("_CheckMap",texture);probe.SetFloat("_NormalFlag",kind==1?1+LTGlobalBakeAsset.NormalEncoding(texture):0);
                    probe.SetVector("_ProbeUV",new Vector4(u,v,mip,0));
                    RenderTexture.active=target;GL.Clear(false,true,new Color(-19,-23,-29,-31));
                    Require(probe.SetPass(0),"Probe pass unavailable.");Graphics.DrawProceduralNow(MeshTopology.Triangles,3);
                    read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply(false,false);var pixel=read.GetPixel(0,0);
                    for(int c=0;c<4;c++)Require(!float.IsNaN(pixel[c])&&!float.IsInfinity(pixel[c]),"Non-finite GPU result.");
                    Require(pixel.r>-2,"Probe did not draw.");return pixel;
                }
                int samples=0;Directory.CreateDirectory(folder);
                for(int kind=0;kind<2;kind++)
                {
                    var source=Own(new Texture2D(32,32,TextureFormat.RGBA32,true,true){name="Owned global source",wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Trilinear});
                    for(int mip=0;mip<source.mipmapCount;mip++)
                    {
                        int size=Math.Max(1,32>>mip);var pixels=new Color[size*size];
                        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                        {
                            float u=(x+.5f)/size,v=(y+.5f)/size;
                            var n=new Vector3((u-.5f)*.6f,(v-.5f)*.5f,1).normalized;
                            pixels[y*size+x]=kind==0?new Color(.1f+u*.5f,.2f+v*.4f,.2f+mip*.08f,1):new Color(n.x*.5f+.5f,n.y*.5f+.5f,n.z*.5f+.5f,1);
                        }
                        source.SetPixels(pixels,mip);
                    }
                    source.Apply(false,false);string originalHash=Pixels(source);
                    var rt=Own(new RenderTexture(32,32,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear)
                        {name="Owned global RT",useMipMap=true,autoGenerateMips=false,filterMode=FilterMode.Trilinear,wrapMode=TextureWrapMode.Clamp});
                    Require(rt.Create(),"Source RT unavailable.");Graphics.Blit(source,rt);rt.GenerateMips();
                    foreach(Texture input in new Texture[]{source,rt})
                    {
                        var format=kind==0?TextureFormat.BC7:TextureFormat.BC5;
                        var packed=Own(LTGlobalBakeSaving.ReadCompressed(input,"Owned BC global",format,false));
                        Require(packed.format==format&&!packed.isDataSRGB&&packed.mipmapCount==source.mipmapCount,"Format/linear/mip contract failed.");
                        Require(Pixels(source)==originalHash,"Compression modified source.");
                        Require(LTGlobalBakeAsset.NormalEncoding(packed)==(kind==1?1:0)&&LTGlobalBakeAsset.NormalEncoding(input)==0,"Normal binding flag.");
                        string path=folder+"/"+kind+"-"+(input==source?"texture":"rt")+".bin";
                        InternalEditorUtility.SaveToSerializedFileAndForget(new Object[]{packed},path,false);
                        var restored=InternalEditorUtility.LoadSerializedFileAndForget(path);foreach(var item in restored)Own(item);
                        Require(restored.Length==1&&restored[0] is Texture2D,"Serialization result.");var reopened=(Texture2D)restored[0];
                        Require(reopened.format==format&&reopened.mipmapCount==source.mipmapCount&&!reopened.isDataSRGB&&Pixels(reopened)==Pixels(packed),"BC payload/metadata changed after serialization.");
                        float maximum=0;
                        for(int mip=0;mip<source.mipmapCount;mip++)foreach(float u in new[]{.13f,.41f,.79f})foreach(float v in new[]{.19f,.83f})
                        {
                            var expected=Sample(input,kind,mip,u,v);var actual=Sample(reopened,kind,mip,u,v);
                            for(int c=0;c<3;c++)maximum=Mathf.Max(maximum,Mathf.Abs(actual[c]-expected[c]));
                            Require(maximum<.045f,$"Compression error/orientation/mip mismatch: {format}, {input.GetType().Name}, mip {mip}, {maximum}");samples++;
                        }
                        long expectedBytes=LTLayerArrayStorageUtility.Bytes(32,32,1,source.mipmapCount,format);
                        Require(packed.GetRawTextureData<byte>().Length==expectedBytes,"BC payload size.");
                        Require(expectedBytes<source.GetRawTextureData<byte>().Length*.3,"Expected approximately 4x reduction.");
                        report.AppendLine($"PASS {format} / {input.GetType().Name}: all mips, orientation, linear space, serialization, source unchanged; max GPU error {maximum:F6}.");
                    }
                }
                report.AppendLine($"PASS {samples} GPU comparisons. Existing XYZ and BC5 decoder paths exercised. Not a scene quality/FPS benchmark.");
            }
            catch(Exception error){failure=error;}
            finally
            {
                RenderTexture.active=previous;GL.sRGBWrite=srgb;GL.wireframe=wire;ShaderUtil.allowAsyncCompilation=async;
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                try
                {
                    foreach(var scene in scenes)Require(scene.scene.IsValid()&&scene.scene.isDirty==scene.dirty&&scene.scene.rootCount==scene.roots,"User scene changed.");
                    foreach(var file in files)Require(File.Exists(file.Key)&&Hash(file.Key)==file.Value,"Protected file changed: "+file.Key);
                    report.AppendLine("PASS user scene/source/settings/package guards.");
                }
                catch(Exception error){failure=failure==null?error:new AggregateException(failure,error);}
                report.AppendLine(failure==null?"PASS":"FAIL: "+failure);Directory.CreateDirectory(folder);File.WriteAllText(folder+"/report.txt",report.ToString());
                if(failure==null)Debug.Log(report+"\n"+folder+"/report.txt");else Debug.LogError(report+"\n"+folder+"/report.txt");
            }
        }
    }
}
