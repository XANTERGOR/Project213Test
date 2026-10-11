using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTRoadGraphGroundCheck
    {
        const string Tag="[Road Shader Graph Blend v1] ";
        const string Source="Assets/Data/Models/Environment/Roads/Roads_1/Road.mat";
        // Archived validation menu: Tools/Local Terrain/Validate Road Shader Graph Blend (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var paths=AssetDatabase.GetDependencies(Source,true).Concat(scenes.Select(s=>s.scene.path))
                .Concat(Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*",SearchOption.AllDirectories))
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"});
            var files=paths.Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var owned=new List<Object>();var preview=EditorSceneManager.NewPreviewScene();
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite,wire=GL.wireframe,async=ShaderUtil.allowAsyncCompilation;
            Exception failure=null;int samples=0,passes=0,properties=0;
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            GameObject Child(string name,Transform parent=null)
            {var g=Own(new GameObject(name));g.SetActive(false);SceneManager.MoveGameObjectToScene(g,preview);if(parent)g.transform.SetParent(parent,false);return g;}
            Texture2D Solid(Color c)
            {var t=Own(new Texture2D(2,2,TextureFormat.RGBAFloat,false,true));t.SetPixels(new[]{c,c,c,c});t.Apply();return t;}
            void Fail(Exception e){failure=failure==null?e:new AggregateException(failure,e);}
            try
            {
                ShaderUtil.allowAsyncCompilation=false;GL.sRGBWrite=false;GL.wireframe=false;
                var source=AssetDatabase.LoadAssetAtPath<Material>(Source);Require(source,"Road.mat missing");
                string before=EditorJsonUtility.ToJson(source);
                Require(LTRoadGroundBlendBuild.Supported(source,out string reason),reason);
                var root=Child("Owned graph blend check");var world=root.AddComponent<LTWorld>();world.enabled=false;world.autoUpdate=false;root.SetActive(true);
                var road=Child("Owned graph road",root.transform).AddComponent<LTRoad>();road.enabled=false;
                road.mode=LTRoadMode.Asphalt;road.groundBlend=true;road.groundBlendSource=LTRoadGroundSource.SingleLayer;
                road.groundBlendNoise=0;road.groundBlendWidth=.6f;road.groundBlendHoleWidth=.3f;road.groundBlendStrength=1;
                road.groundLayer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());world.baseLayer=road.groundLayer;
                road.groundLayer.baseColorMap=Solid(new Color(.1f,.6f,.2f,1));road.groundLayer.maskMap=Solid(new Color(.4f,.5f,.7f,1));
                road.groundLayer.metallic=.2f;road.groundLayer.aoStrength=1;road.groundLayer.tint=Color.white;
                var chunks=LTRoadGroundBlendCheck.Fixture();var hashes=chunks.Select(LTRoadMesh.ContentHash).ToArray();
                using var prepared=LTRoadGroundBlendBuild.Prepare(road,new LTRoadModuleSource.Prepared{levels=new[]{chunks,chunks},materials=new[]{source}});
                var mask=Own(LTRoadGroundBlendBuild.CreateTexture(prepared.mask));
                var live=Own(LTRoadGroundBlendBuild.CreateMaterial(road,prepared,source,mask));
                Require(live.shader==Resources.Load<Shader>("LTRoadGraphGround"),"New graph adapter not selected");
                Require(live.renderQueue==source.renderQueue&&live.enableInstancing==source.enableInstancing,"Render state changed");
                for(int i=0;i<source.shader.GetPropertyCount();i++)
                {
                    string p=source.shader.GetPropertyName(i);Require(live.HasProperty(p),"Missing source property "+p);properties++;
                    switch(source.shader.GetPropertyType(i))
                    {
                        case ShaderPropertyType.Texture:
                            Require(live.GetTexture(p)==source.GetTexture(p)&&live.GetTextureScale(p)==source.GetTextureScale(p)&&live.GetTextureOffset(p)==source.GetTextureOffset(p),"Texture/UV changed: "+p);break;
                        case ShaderPropertyType.Color:Require(live.GetColor(p)==source.GetColor(p),"Color changed: "+p);break;
                        case ShaderPropertyType.Vector:Require(live.GetVector(p)==source.GetVector(p),"Vector changed: "+p);break;
                        case ShaderPropertyType.Int:Require(live.GetInteger(p)==source.GetInteger(p),"Int changed: "+p);break;
                        default:Require(live.GetFloat(p)==source.GetFloat(p),"Scalar changed: "+p);break;
                    }
                }
                foreach(bool automatic in new[]{false,true})
                {
                    if(automatic)live.EnableKeyword("_LT_ROAD_TERRAIN");else live.DisableKeyword("_LT_ROAD_TERRAIN");
                    foreach(string name in new[]{"GBuffer","Forward","DepthOnly","ShadowCaster","MotionVectors","META"})
                    {
                        int pass=live.FindPass(name);Require(pass>=0,"Missing pass "+name);
                        ShaderUtil.CompilePass(live,pass,true);passes++;
                    }
                    Require(!ShaderUtil.ShaderHasError(live.shader),string.Join("; ",ShaderUtil.GetShaderMessages(live.shader).Select(m=>m.message)));
                }
                live.DisableKeyword("_LT_ROAD_TERRAIN");
                var probe=Own(new Material(UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/TerrainSystem/LocalTerrain/Editor/LTRoadGroundProbe.shader")));probe.CopyPropertiesFromMaterial(live);
                probe.EnableKeyword("_LT_GRAPH_PROBE");probe.SetVector("_LTProbeNormal",Vector3.up);probe.SetVector("_LTGraphNormal",Vector3.forward);
                probe.SetVector("_LTRockTint",new Vector4(.8f,.1f,.1f,1));probe.SetVector("_LTRockSettings",new Vector4(1,.25f,0,0));
                probe.SetMatrix("_LTSurfaceWorldToLocal",Matrix4x4.identity);probe.SetMatrix("_LTRoadBlendWorldToMask",Matrix4x4.identity);
                var target=Own(new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));Require(target.Create(),"GPU target unavailable");
                var read=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                Color Pixel(float x,float z,int mode=0)
                {
                    probe.SetVector("_LTProbePosition",new Vector4(x,0,z,mode));RenderTexture.active=target;GL.Clear(false,true,Color.magenta);
                    Require(probe.SetPass(0),"Graph hook probe unavailable");Graphics.DrawProceduralNow(MeshTopology.Triangles,3);
                    read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();samples++;return read.GetPixel(0,0);
                }
                void Near(Color a,Color b,string label)
                {Require(Math.Abs(a.r-b.r)<.025f&&Math.Abs(a.g-b.g)<.025f&&Math.Abs(a.b-b.b)<.025f&&Math.Abs(a.a-b.a)<.025f,label+": "+a+" vs "+b);}
                Near(Pixel(2,-1),new Color(.8f,.1f,.1f,1),"Clean asphalt / module seam");
                Near(Pixel(3.05f,0),new Color(.1f,.6f,.2f,.4f),"Outer rim color / AO");
                Near(Pixel(0,0),new Color(.1f,.6f,.2f,.4f),"Hole rim color / AO");
                Near(Pixel(2,-1,1),new Color(0,0,1,.25f),"Asphalt normal / smoothness");
                Near(Pixel(0,0,1),new Color(0,0,1,.7f),"Ground normal / smoothness");
                Near(Pixel(0,0,2),new Color(.2f,1,0,1),"Ground metallic / weight");
                probe.SetTexture("_LTRoadBlendNormal",Solid(new Color(.75f,.5f,1,1)));probe.SetVector("_LTRoadBlendFlags",new Vector4(1,1,0,0));
                Near(Pixel(0,0,1),new Color(.5f,0,(float)Math.Sqrt(.75),.7f),"Nonflat ground normal");
                probe.SetVector("_LTRoadBlendFlags",new Vector4(0,1,0,0));
                var mixed=Pixel(1.15f,0);Require(mixed.r>.1f&&mixed.r<.8f&&mixed.g>.1f&&mixed.g<.6f,"Missing intermediate blend");
                probe.SetVector("_LTRoadBlendParams",new Vector4(.6f,0,1,0));Near(Pixel(0,0),new Color(.8f,.1f,.1f,1),"Hole switch");
                probe.SetVector("_LTRoadBlendParams",new Vector4(.6f,.3f,0,0));Near(Pixel(0,0),new Color(.8f,.1f,.1f,1),"Strength zero color");
                // Authored graph lerps its normals without renormalizing here.
                probe.SetVector("_LTGraphNormal",new Vector4(.2f,.1f,.8f,0));Near(Pixel(0,0,1),new Color(.2f,.1f,.8f,.25f),"Strength zero preserves exact authored normal");
                probe.SetVector("_LTRoadBlendParams",new Vector4(.6f,.3f,1,0));probe.SetMatrix("_LTRoadBlendWorldToMask",Matrix4x4.Translate(new Vector3(-100,0,0)));
                Near(Pixel(100,0),new Color(.1f,.6f,.2f,.4f),"Translated road mask");
                Require(!ShaderUtil.ShaderHasError(probe.shader),"Graph hook probe shader errors");
                Require(before==EditorJsonUtility.ToJson(source),"Source material modified");
                Require(hashes.SequenceEqual(chunks.Select(LTRoadMesh.ContentHash)),"Geometry/LOD data modified");
            }
            catch(Exception e){Fail(e);}
            finally
            {
                RenderTexture.active=previous;GL.sRGBWrite=srgb;GL.wireframe=wire;ShaderUtil.allowAsyncCompilation=async;
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])try{Object.DestroyImmediate(owned[i]);}catch(Exception e){Fail(e);}
                try{EditorSceneManager.ClosePreviewScene(preview);}catch(Exception e){Fail(e);}
                try
                {
                    foreach(var s in scenes)Require(s.scene.IsValid()&&s.scene.isLoaded&&s.scene.isDirty==s.dirty&&s.scene.rootCount==s.roots,"Scene state changed");
                    foreach(var f in files)Require(File.Exists(f.Key)&&Hash(f.Key)==f.Value,"File changed: "+f.Key);
                }
                catch(Exception e){Fail(e);}
            }
            if(failure!=null){Debug.LogError(Tag+"FAIL: "+failure);return;}
            Debug.Log(Tag+$"PASS: {properties} source properties preserved; {passes} native graph pass variants; {samples} GPU hook readbacks (outer/hole rims, zero strength, all surface channels). Source material, assets, LOD data and user scenes unchanged. Starting automatic terrain / SRP Batcher checks; their result is reported separately.");
            LTRoadTerrainBlendCheck.Run();
        }
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static string Hash(string path){using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f));}
    }
}
