using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTRoadGroundBlendCheck
    {
        const string Tag="[Road Ground Blend v3] ";
        const string Asset="Assets/Data/Models/Environment/Roads/Road_1_Line.prefab";
        // Archived validation menu: Tools/Local Terrain/Validate Road Ground Blend (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var files=AssetDatabase.GetDependencies(Asset,true).Concat(scenes.Select(s=>s.scene.path))
                .Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","LTRoad*",SearchOption.AllDirectories))
                .Where(File.Exists).Distinct().ToDictionary(p=>p,FileHash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite,wire=GL.wireframe,async=ShaderUtil.allowAsyncCompilation;
            LTRoadGroundBlendBuild.Prepared mask=null,actualMask=null;Exception failure=null;string report=null;
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            GameObject Child(string name,Transform parent=null)
            {var g=Own(new GameObject(name));g.SetActive(false);SceneManager.MoveGameObjectToScene(g,preview);if(parent)g.transform.SetParent(parent,false);return g;}
            Texture2D Solid(Color c)
            {var t=Own(new Texture2D(2,2,TextureFormat.RGBAFloat,false,true));t.SetPixels(new[]{c,c,c,c});t.Apply();return t;}
            try
            {
                ShaderUtil.allowAsyncCompilation=false;GL.sRGBWrite=false;GL.wireframe=false;
                var root=Child("Owned ground blend check");var world=root.AddComponent<LTWorld>();world.enabled=false;world.autoUpdate=false;world.enableLayerPainting=false;
                root.SetActive(true);
                var road=Child("Owned road",root.transform).AddComponent<LTRoad>();road.enabled=false;
                road.mode=LTRoadMode.Asphalt;road.groundBlend=true;road.groundBlendNoise=0;road.groundBlendWidth=.6f;road.groundBlendHoleWidth=.3f;
                road.groundLayer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());
                // Reproduce the live scene: an Offroad road switched to mesh
                // Asphalt retains a hidden Spline value and reuses a biome layer.
                road.projection=LTRoadProjection.Spline;road.groundBlend=false;
                var legacyProjection=road.Capture(world);
                Require(road.CapturePaintProjection(world,false)!=null,"Legacy asphalt projection was changed.");
                road.groundBlend=true;
                var blendProjection=road.Capture(world);
                Require(road.CapturePaintProjection(world,false)==null&&blendProjection.projection==LTRoadProjection.World,"Blended asphalt still allocates conflicting spline UVs for the terrain layer.");
                Require(road.projection==LTRoadProjection.Spline,"Saved Offroad projection was overwritten.");
                Require(legacyProjection.geometryHash==blendProjection.geometryHash&&legacyProjection.detailHash==blendProjection.detailHash&&legacyProjection.paintHash!=blendProjection.paintHash,"Projection routing changed geometry/removal or failed to invalidate paint.");
                var legacyRibbon=LTRoadMesh.Build(legacyProjection,12,4,.06f,Matrix4x4.identity);
                var blendedRibbon=LTRoadMesh.Build(blendProjection,12,4,.06f,Matrix4x4.identity);
                Require(legacyRibbon.Select(LTRoadMesh.ContentHash).SequenceEqual(blendedRibbon.Select(LTRoadMesh.ContentHash)),"Ground projection changed asphalt ribbon geometry/UV.");
                world.baseLayer=road.groundLayer;
                var palette=new List<LTSurfaceLayer>{world.baseLayer};road.GetComponent<LTPaintStamp>().AppendLayers(palette);
                Require(palette.Count==1,"Sharing the biome ground layer allocated another terrain slot.");
                road.groundLayer.baseColorMap=Solid(new Color(.1f,.6f,.2f,1));
                road.groundLayer.maskMap=Solid(new Color(.4f,.5f,.7f,1));road.groundLayer.metallic=.2f;
                var source=Own(new Material(Shader.Find("HDRP/Lit")));
                source.SetTexture("_BaseColorMap",Solid(new Color(.8f,.1f,.1f,1)));source.SetColor("_BaseColor",Color.white);
                source.SetFloat("_Smoothness",.25f);source.SetFloat("_Metallic",0);source.SetFloat("_MaterialID",1);
                string sourceBefore=EditorJsonUtility.ToJson(source);
                var pieces=Fixture();var synthetic=new LTRoadModuleSource.Prepared{levels=new[]{pieces,pieces},materials=new[]{source}};
                mask=LTRoadGroundBlendBuild.Prepare(road,synthetic);
                Require(mask.mask.holePixels>0,"Hole not detected.");
                Require(mask.mask.Sample(2,-1).x>.9f&&mask.mask.Sample(2,-1).y>.9f,"False rim at repeat seam.");
                var texture=Own(LTRoadGroundBlendBuild.CreateTexture(mask.mask));
                var live=Own(LTRoadGroundBlendBuild.CreateMaterial(road,mask,source,texture));
                foreach(string passName in new[]{"GBuffer","Forward","DepthOnly","ShadowCaster","MotionVectors"})
                {int pass=live.FindPass(passName);Require(pass>=0,"Missing live pass "+passName);ShaderUtil.CompilePass(live,pass,true);}
                Require(!ShaderUtil.ShaderHasError(live.shader),"Live shader: "+string.Join("; ",ShaderUtil.GetShaderMessages(live.shader).Select(m=>m.message)));
                var probeShader=UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/TerrainSystem/LocalTerrain/Editor/LTRoadGroundProbe.shader");Require(probeShader,"Probe shader missing.");
                var probe=Own(new Material(probeShader));probe.CopyPropertiesFromMaterial(live);
                // Copying the production material clears probe-only properties.
                probe.SetVector("_LTProbeNormal",Vector3.up);
                probe.SetMatrix("_LTSurfaceWorldToLocal",Matrix4x4.identity);probe.SetMatrix("_LTRoadBlendWorldToMask",Matrix4x4.identity);
                var target=Own(new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));Require(target.Create(),"GPU allocation failed.");
                var read=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));int pixels=0;
                Color Pixel(float x,float z,int mode=0)
                {
                    probe.SetVector("_LTProbePosition",new Vector4(x,0,z,mode));probe.SetVector("_LTProbeUV",new Vector4(.5f,.5f,0,0));
                    RenderTexture.active=target;GL.Clear(false,true,Color.magenta);Require(probe.SetPass(0),"Probe pass unavailable.");
                    Graphics.DrawProceduralNow(MeshTopology.Triangles,3);read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();pixels++;return read.GetPixel(0,0);
                }
                void Near(Color actual,Color expected,string label)
                {Require(Math.Abs(actual.r-expected.r)<.025&&Math.Abs(actual.g-expected.g)<.025&&Math.Abs(actual.b-expected.b)<.025&&Math.Abs(actual.a-expected.a)<.025,label+": "+actual+" expected "+expected);}
                Near(Pixel(2,-1),new Color(.8f,.1f,.1f,1),"Clean centre/module seam");
                Near(Pixel(3.05f,0),new Color(.1f,.6f,.2f,.4f),"Outer edge ground/AO");
                Near(Pixel(0,0),new Color(.1f,.6f,.2f,.4f),"Hole/far filled hole ground/AO");
                Near(Pixel(2,-1,1),new Color(0,0,1,.25f),"Asphalt normal/smoothness");
                Near(Pixel(0,0,1),new Color(0,0,1,.7f),"Ground normal/smoothness");
                Near(Pixel(0,0,2),new Color(.2f,1,0,1),"Ground metallic/weight");
                probe.SetTexture("_LTRoadBlendNormal",Solid(new Color(.75f,.5f,1,1)));
                probe.SetVector("_LTRoadBlendFlags",new Vector4(1,1,0,0));
                Near(Pixel(0,0,1),new Color(.5f,0,(float)Math.Sqrt(.75),.7f),"Projected non-flat ground normal");
                probe.SetVector("_LTRoadBlendFlags",new Vector4(0,1,0,0));
                var mixed=Pixel(1.15f,0);Require(mixed.r>.1f&&mixed.r<.8f&&mixed.g>.1f&&mixed.g<.6f,"No smooth intermediate blend.");
                probe.SetVector("_LTRoadBlendParams",new Vector4(.6f,0,1,0));Near(Pixel(0,0),new Color(.8f,.1f,.1f,1),"Hole switch independent from outer edge");
                probe.SetVector("_LTRoadBlendParams",new Vector4(.6f,.3f,0,0));Near(Pixel(0,0),new Color(.8f,.1f,.1f,1),"Strength zero restores asphalt");
                probe.SetVector("_LTRoadBlendParams",new Vector4(.6f,.3f,1,0));
                probe.SetMatrix("_LTRoadBlendWorldToMask",Matrix4x4.Translate(new Vector3(-100,0,0)));
                Near(Pixel(100,0),new Color(.1f,.6f,.2f,.4f),"Translated road mask binding");
                Require(!ShaderUtil.ShaderHasError(probeShader),"GPU probe shader errors.");
                Require(EditorJsonUtility.ToJson(source)==sourceBefore,"Source material changed.");
                // Read the user's imported LODGroup, never instantiate it in a user scene.
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Asset);Require(prefab,"Road_1_Line missing.");
                road.asphaltSource=LTRoadMeshSource.Module;road.asphaltModulePrefab=prefab;road.moduleFitEnds=true;road.moduleRestoreLODEnds=true;
                road.moduleLODEndWidth=.5f;road.moduleEndMaxMismatch=.05f;road.meshChunkLength=1;
                road.points=new List<LTRoadPoint>{new LTRoadPoint(Vector3.zero),new LTRoadPoint(new Vector3(2,1,35)),new LTRoadPoint(new Vector3(5,2,55))};
                var actual=LTRoadModuleSource.Build(road,road.Capture(world));var hashes=actual.levels.SelectMany(l=>l).Select(LTRoadMesh.ContentHash).ToArray();
                road.groundBlend=false;
                var legacyModule=LTRoadModuleSource.Build(road,road.Capture(world));road.groundBlend=true;
                Require(hashes.SequenceEqual(legacyModule.levels.SelectMany(l=>l).Select(LTRoadMesh.ContentHash)),"Ground projection changed authored module LOD geometry/UV.");
                actualMask=LTRoadGroundBlendBuild.Prepare(road,actual);Require(actual.levels.Length==3,"Authored LOD0/1/2 missing.");
                Require(hashes.SequenceEqual(actual.levels.SelectMany(l=>l).Select(LTRoadMesh.ContentHash)),"Blend modified authored LOD geometry/channels.");
                var actualTexture=Own(LTRoadGroundBlendBuild.CreateTexture(actualMask.mask));
                int authoredPasses=0;
                foreach(var m in actual.materials)
                {
                    string originalMaterial=EditorJsonUtility.ToJson(m);
                    var blended=Own(LTRoadGroundBlendBuild.CreateMaterial(road,actualMask,m,actualTexture));
                    Require(blended.shader.name=="Local Terrain/Authored Road Ground HDRP","Actual RoadShader adapter not selected.");
                    // Every original exposed shader property must survive the adapter.
                    for(int i=0;i<m.shader.GetPropertyCount();i++)
                    {
                        string property=m.shader.GetPropertyName(i);Require(blended.HasProperty(property),"Lost authored property "+property);
                        switch(m.shader.GetPropertyType(i))
                        {
                            case UnityEngine.Rendering.ShaderPropertyType.Texture:
                                Require(blended.GetTexture(property)==m.GetTexture(property)&&blended.GetTextureScale(property)==m.GetTextureScale(property)&&blended.GetTextureOffset(property)==m.GetTextureOffset(property),"Authored texture/UV changed: "+property);break;
                            case UnityEngine.Rendering.ShaderPropertyType.Color:
                                Require(blended.GetColor(property)==m.GetColor(property),"Authored color changed: "+property);break;
                            case UnityEngine.Rendering.ShaderPropertyType.Vector:
                                Require(blended.GetVector(property)==m.GetVector(property),"Authored vector changed: "+property);break;
                            case UnityEngine.Rendering.ShaderPropertyType.Int:
                                Require(blended.GetInteger(property)==m.GetInteger(property),"Authored integer changed: "+property);break;
                            default:Require(blended.GetFloat(property)==m.GetFloat(property),"Authored scalar changed: "+property);break;
                        }
                    }
                    foreach(string name in new[]{"GBuffer","Forward","DepthOnly","ShadowCaster","META"})
                    {int pass=blended.FindPass(name);Require(pass>=0,"Authored pass missing: "+name);ShaderUtil.CompilePass(blended,pass,true);authoredPasses++;}
                    Require(!ShaderUtil.ShaderHasError(blended.shader),"Authored adapter shader: "+string.Join("; ",ShaderUtil.GetShaderMessages(blended.shader).Select(m=>m.message)));
                    Require(EditorJsonUtility.ToJson(m)==originalMaterial,"Authored material asset changed.");
                }
                var segment=Child("Owned LOD binding",road.transform);var renderer=segment.AddComponent<MeshRenderer>();var filter=segment.AddComponent<MeshFilter>();
                var collider=segment.AddComponent<MeshCollider>();var lod=segment.AddComponent<LTRoadLOD>();lod.owner=road;
                lod.meshes=actual.levels.Select(l=>{var c=l[0];var m=Own(new Mesh{indexFormat=UnityEngine.Rendering.IndexFormat.UInt32});m.vertices=c.vertices;m.triangles=c.triangles;m.RecalculateBounds();return m;}).ToArray();
                collider.sharedMesh=lod.meshes[0];renderer.sharedMaterial=live;
                var binding=segment.AddComponent<LTRoadGroundBlend>();binding.owner=road;binding.roadToMask=actualMask.roadToMask;binding.Refresh();
                for(int l=0;l<3;l++){lod.Show(l);Require(filter.sharedMesh==lod.meshes[l]&&collider.sharedMesh==lod.meshes[0]&&renderer.sharedMaterial==live,"LOD changed material/mask/collider.");}
                report=$"PASS: inherited Spline ground resolves to World while preserving the saved setting and one terrain slot; {pixels} GPU probes (edge/hole/centre, normal/AO/smoothness/metallic, independent switches, transform), five live HDRP passes + {authoredPasses} authored RoadShader passes and all source properties; actual Road_1_Line LOD0/1/2 unchanged, shared mask, collider LOD0. Actual mask {actualMask.mask.width}x{actualMask.mask.height}, holes {actualMask.mask.holePixels}. Source assets and user scenes unchanged. Not a live-scene visual/performance benchmark.";
            }
            catch(Exception e){failure=e;}
            finally
            {
                RenderTexture.active=previous;GL.sRGBWrite=srgb;GL.wireframe=wire;ShaderUtil.allowAsyncCompilation=async;
                mask?.Dispose();actualMask?.Dispose();
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene state changed: "+s.scene.name,failure);
                foreach(var f in files)if(!File.Exists(f.Key)||FileHash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(Tag+"FAIL: "+failure);else Debug.Log(Tag+report);
        }
        public static List<LTRoadMesh.Chunk> Fixture()
        {
            LTRoadMesh.Chunk Rect(float x0,float z0,float x1,float z1)
            {
                var c=new LTRoadMesh.Chunk{vertices=new[]{new Vector3(x0,0,z0),new Vector3(x1,0,z0),new Vector3(x1,0,z1),new Vector3(x0,0,z1)},
                    normals=Enumerable.Repeat(Vector3.up,4).ToArray(),tangents=Enumerable.Repeat(new Vector4(1,0,0,-1),4).ToArray(),
                    uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up},triangles=new[]{0,2,1,0,3,2}};
                c.hash=LTRoadMesh.ContentHash(c);return c;
            }
            return new List<LTRoadMesh.Chunk>{Rect(-3,-5,3,-1),Rect(-3,1,3,5),Rect(-3,-1,-1,1),Rect(1,-1,3,1)};
        }
        static string FileHash(string path){using(var s=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(s));}
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    }
}
