using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTRoadJunctionGroundBlendCheck
    {
        const string Folder="Assets/Data/Models/Environment/Roads/Roads_1/";
        // Archived validation menu: Tools/Local Terrain/Validate Junction Ground Blend (no scene changes)
        public static void Run()
        {
            const string tag="[Junction Ground Blend v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var files=AssetDatabase.GetDependencies(new[]{Folder+"Road_Perek_1.prefab",Folder+"Road_1.prefab"},true)
                .Concat(Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","LTRoad*",SearchOption.AllDirectories))
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();var roads=new List<LTRoad>();
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite,wire=GL.wireframe,async=ShaderUtil.allowAsyncCompilation;
            Exception failure=null;int checks=0,pixels=0;string report=null;
            void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            GameObject Child(string name,Transform parent=null)
            {var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,preview);if(parent)go.transform.SetParent(parent,false);return go;}
            Texture2D Solid(Color c)
            {var t=Own(new Texture2D(2,2,TextureFormat.RGBAFloat,false,true));t.SetPixels(new[]{c,c,c,c});t.Apply();return t;}
            Mesh Mesh(LTRoadMesh.Chunk c)
            {var m=Own(new Mesh{indexFormat=UnityEngine.Rendering.IndexFormat.UInt32});m.vertices=c.vertices;m.triangles=c.triangles;m.RecalculateBounds();return m;}
            LTRoadMesh.Chunk Combine(IEnumerable<LTRoadMesh.Chunk> pieces)
            {
                var v=new List<Vector3>();var n=new List<Vector3>();var t=new List<Vector4>();var uv=new List<Vector2>();var ids=new List<int>();
                foreach(var c in pieces){ids.AddRange(c.triangles.Select(i=>i+v.Count));v.AddRange(c.vertices);n.AddRange(c.normals);t.AddRange(c.tangents);uv.AddRange(c.uv);}
                return new LTRoadMesh.Chunk{vertices=v.ToArray(),normals=n.ToArray(),tangents=t.ToArray(),uv=uv.ToArray(),triangles=ids.ToArray()};
            }
            try
            {
                ShaderUtil.allowAsyncCompilation=false;GL.sRGBWrite=false;GL.wireframe=false;
                var root=Child("Owned junction blend validation");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;root.SetActive(true);
                var ground=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());ground.baseColorMap=Solid(new Color(.1f,.6f,.2f,1));
                var source=Own(new Material(Shader.Find("HDRP/Lit")));source.SetFloat("_MaterialID",1);
                source.SetTexture("_BaseColorMap",Solid(new Color(.8f,.1f,.1f,1)));source.SetColor("_BaseColor",Color.white);
                string sourceBefore=EditorJsonUtility.ToJson(source);
                var node=Child("Owned junction",root.transform).AddComponent<LTRoadJunction>();
                node.meshSource=LTRoadJunctionSource.OwnHierarchy;node.surface=LTRoadMode.Asphalt;node.groundBlend=true;
                node.groundBlendSource=LTRoadGroundSource.SingleLayer;node.groundBlendLayer=ground;node.groundBlendNoise=0;
                node.groundBlendWidth=.6f;node.groundBlendHoleWidth=.3f;node.gameObject.SetActive(true);
                var junction=Combine(LTRoadGroundBlendCheck.Fixture());var original=LTRoadMesh.ContentHash(junction);
                var prepared=new LTRoadJunctionPrefab.Prepared{levels=new[]{junction,junction,junction},materials=new[]{source}};
                var branch=LTRoadGroundBlendCheck.Fixture()[0];
                branch.vertices=new[]{new Vector3(-2,0,5),new Vector3(2,0,5),new Vector3(2,0,10000),new Vector3(-2,0,10000)};
                var road=Child("Owned branch",root.transform).AddComponent<LTRoad>();roads.Add(road);
                road.mode=LTRoadMode.Asphalt;road.startJunction=node;road.gameObject.SetActive(true);
                var connected=new Dictionary<LTRoad,LTRoadModuleSource.Prepared>{{road,new LTRoadModuleSource.Prepared{levels=new[]{new List<LTRoadMesh.Chunk>{branch}},materials=new[]{source}}}};
                using var blend=LTRoadGroundBlendBuild.Prepare(node,prepared,connected);
                Check(blend.mask.width<200&&blend.mask.height<300,"Long road expanded local junction texture.");
                Check(blend.mask.Sample(0,5).x>node.groundBlendWidth+node.groundBlendMaskCell&&blend.mask.Sample(0,0).y==0,"Entrance union/hole classification failed.");
                Check(original==LTRoadMesh.ContentHash(junction)&&node.groundLayer==null,"Blend modified geometry or foundation paint layer.");
                var texture=Own(LTRoadGroundBlendBuild.CreateTexture(blend.mask));
                var live=Own(LTRoadGroundBlendBuild.CreateMaterial(node,blend,source,texture));
                var probe=Own(new Material(UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/TerrainSystem/LocalTerrain/Editor/LTRoadGroundProbe.shader")));probe.CopyPropertiesFromMaterial(live);
                probe.SetVector("_LTProbeNormal",Vector3.up);probe.SetMatrix("_LTRoadBlendWorldToMask",Matrix4x4.identity);probe.SetMatrix("_LTSurfaceWorldToLocal",Matrix4x4.identity);
                var target=Own(new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));Check(target.Create(),"GPU allocation failed.");
                var read=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                Color Pixel(float x,float z)
                {
                    probe.SetVector("_LTProbePosition",new Vector4(x,0,z,0));probe.SetVector("_LTProbeUV",new Vector4(.5f,.5f,0,0));
                    RenderTexture.active=target;GL.Clear(false,true,Color.magenta);Check(probe.SetPass(0),"GPU probe pass unavailable.");
                    Graphics.DrawProceduralNow(MeshTopology.Triangles,3);read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();pixels++;return read.GetPixel(0,0);
                }
                void Near(Color actual,Color expected,string label)
                {Check(Math.Abs(actual.r-expected.r)<.025f&&Math.Abs(actual.g-expected.g)<.025f&&Math.Abs(actual.b-expected.b)<.025f,label+": "+actual);}
                Near(Pixel(0,5),new Color(.8f,.1f,.1f),"Connected entrance acquired dirt stripe");
                Near(Pixel(0,0),new Color(.1f,.6f,.2f),"Hole missing ground");
                Near(Pixel(3.05f,0),new Color(.1f,.6f,.2f),"Outer edge missing ground");
                Near(Pixel(2,-1),new Color(.8f,.1f,.1f),"Clean interior modified");
                road.startJunction=null;
                using(var detached=LTRoadGroundBlendBuild.Prepare(node,prepared,connected))
                    Check(detached.mask.Sample(0,5).x<.08f&&detached.key!=blend.key,"Disconnect did not restore rim/invalidate mask.");
                road.gameObject.SetActive(false);
                var output=Child("Owned generated output",node.transform);var renderer=output.AddComponent<MeshRenderer>();renderer.enabled=false;
                var filter=output.AddComponent<MeshFilter>();var collider=output.AddComponent<MeshCollider>();var lod=output.AddComponent<LTRoadLOD>();
                lod.junction=node;lod.meshes=prepared.levels.Select(Mesh).ToArray();collider.sharedMesh=lod.meshes[0];renderer.sharedMaterial=live;
                var binding=output.AddComponent<LTRoadGroundBlend>();binding.junction=node;binding.roadToMask=blend.roadToMask;output.SetActive(true);binding.Refresh();
                for(int l=0;l<3;l++){lod.Show(l);Check(filter.sharedMesh==lod.meshes[l]&&collider.sharedMesh==lod.meshes[0]&&renderer.sharedMaterial==live,"LOD changed material/mask/collider.");}
                var block=new MaterialPropertyBlock();renderer.GetPropertyBlock(block);
                Check(block.GetMatrix("_LTRoadBlendWorldToMask")==blend.roadToMask*node.transform.worldToLocalMatrix,"Initial junction matrix missing.");
                node.transform.localPosition=new Vector3(80,3,25);node.transform.localRotation=Quaternion.Euler(0,31,0);binding.Refresh();renderer.GetPropertyBlock(block);
                Check(block.GetMatrix("_LTRoadBlendWorldToMask")==blend.roadToMask*node.transform.worldToLocalMatrix,"Moved junction binding stale.");
                binding.enabled=false;renderer.GetPropertyBlock(block);Check(block.GetVector("_LTRoadBlendParams")==Vector4.zero,"Disabled binding retains blend.");
                binding.enabled=true;binding.Refresh();renderer.GetPropertyBlock(block);Check(!block.HasVector("_LTRoadBlendParams"),"Re-enable did not clear disabled material override.");
                Check(live.GetVector("_LTRoadBlendParams").z==1,"Re-enable lost material settings.");
                world.enableLayerPainting=true;world.baseLayer=ground;node.groundBlendSource=LTRoadGroundSource.Terrain;
                var automatic=Own(LTRoadGroundBlendBuild.CreateMaterial(node,blend,source,texture));
                Check(automatic.IsKeywordEnabled("_LT_ROAD_TERRAIN")&&!live.IsKeywordEnabled("_LT_ROAD_TERRAIN"),"Single/automatic sources not independent.");
                var regions=(List<Rect>)typeof(LTRoadGroundBlend).GetMethod("Regions",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{world});
                Check(regions.Count==1,"Automatic junction omitted from world terrain cache regions.");
                node.groundBlend=false;regions=(List<Rect>)typeof(LTRoadGroundBlend).GetMethod("Regions",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{world});
                Check(regions.Count==0&&LTRoadGroundBlendBuild.Prepare(node,prepared,connected)==null,"Disabled junction still allocates blend/cache.");
                foreach(var material in new[]{live,automatic})foreach(string pass in new[]{"GBuffer","Forward","DepthOnly","ShadowCaster","MotionVectors"})
                {int index=material.FindPass(pass);Check(index>=0,"Missing HDRP pass "+pass);ShaderUtil.CompilePass(material,index,true);}
                Check(!ShaderUtil.ShaderHasError(live.shader)&&!ShaderUtil.ShaderHasError(probe.shader),"Junction blend shader errors.");
                Check(sourceBefore==EditorJsonUtility.ToJson(source),"Source material modified.");
                node.gameObject.SetActive(false);

                // Imported user assets in an owned preview scene; never call a save/bake command.
                var asset=AssetDatabase.LoadAssetAtPath<GameObject>(Folder+"Road_Perek_1.prefab");Check(asset,"Missing junction prefab.");
                var instance=Own((GameObject)PrefabUtility.InstantiatePrefab(asset,preview));instance.SetActive(false);instance.transform.SetParent(root.transform,false);
                instance.transform.localPosition=new Vector3(902,4,137);instance.transform.localRotation=Quaternion.Euler(0,17,0);instance.transform.localScale=Vector3.one;
                var actual=instance.GetComponent<LTRoadJunction>();if(!actual)actual=instance.AddComponent<LTRoadJunction>();
                actual.meshSource=LTRoadJunctionSource.OwnHierarchy;actual.surface=LTRoadMode.Asphalt;actual.connectionFit=LTRoadJunctionFit.FitProfile;
                actual.profileSearchBand=.005f;actual.profileMaxShift=.1f;actual.profileBlendLength=1;actual.neckLength=3;actual.radius=50;
                actual.groundBlend=true;actual.groundBlendSource=LTRoadGroundSource.Terrain;instance.SetActive(true);
                var imported=LTRoadJunctionPrefab.Build(actual,true);var hashes=imported.levels.Select(LTRoadMesh.ContentHash).ToArray();
                var actualRoads=new Dictionary<LTRoad,LTRoadModuleSource.Prepared>();
                foreach(var socket in actual.Sockets)
                {
                    var r=Child("Owned approach "+socket.name,root.transform).AddComponent<LTRoad>();roads.Add(r);
                    r.mode=LTRoadMode.Asphalt;r.asphaltSource=LTRoadMeshSource.Module;r.asphaltModulePrefab=AssetDatabase.LoadAssetAtPath<GameObject>(Folder+"Road_1.prefab");
                    r.moduleFuseEnds=true;r.moduleFuseTolerance=.00102f;r.moduleFusePlaneTolerance=.00096f;r.moduleLength=11;r.moduleBaseY=.05f;r.width=socket.width;
                    var m=actual.SocketMatrix(socket);var p=m.MultiplyPoint3x4(Vector3.zero);var direction=m.MultiplyVector(Vector3.forward);
                    r.points=new List<LTRoadPoint>{new LTRoadPoint(p),new LTRoadPoint(p+direction*20),new LTRoadPoint(p+direction*45)};
                    r.startJunction=actual;r.startJunctionSocket=socket;r.gameObject.SetActive(true);
                    var built=LTRoadModuleSource.BuildPreview(r,r.Capture(world));LTRoadJunctionPrefab.MatchRoad(actual,r,true,built,imported,true);actualRoads.Add(r,built);
                }
                using var actualBlend=LTRoadGroundBlendBuild.Prepare(actual,imported,actualRoads);
                Check(imported.levels.Length>=3&&hashes.SequenceEqual(imported.levels.Select(LTRoadMesh.ContentHash)),"Imported authored LODs changed.");
                foreach(var socket in actual.Sockets)
                {var p=actualBlend.roadToMask.MultiplyPoint3x4(socket.InPrefab(actual.SourceRoot).MultiplyPoint3x4(Vector3.zero));Check(actualBlend.mask.Sample(p.x,p.z).x>actual.groundBlendWidth+actual.groundBlendMaskCell,"Imported connected socket has transverse rim: "+socket.name);}
                var actualTexture=Own(LTRoadGroundBlendBuild.CreateTexture(actualBlend.mask));
                foreach(var material in imported.materials)
                {
                    string before=EditorJsonUtility.ToJson(material);var adapted=Own(LTRoadGroundBlendBuild.CreateMaterial(actual,actualBlend,material,actualTexture));
                    foreach(string pass in new[]{"GBuffer","Forward","DepthOnly","ShadowCaster"})
                    {int index=adapted.FindPass(pass);Check(index>=0,"Missing authored pass "+pass);ShaderUtil.CompilePass(adapted,index,true);}
                    Check(!ShaderUtil.ShaderHasError(adapted.shader)&&before==EditorJsonUtility.ToJson(material),"Authored adapter error or source material changed.");
                }
                report=$"PASS {checks} checks, {pixels} GPU probes: junction edges/holes/clean connected entrance; bounded 10km branch, disconnect, single/terrain adapters, runtime matrices/re-enable/cache regions, unchanged authored LODs and collider LOD0. Imported Road_Perek_1 + Road_1 at all three ports; mask {actualBlend.mask.width}x{actualBlend.mask.height}. No scene/asset saved. Not a live visual/performance benchmark.";
            }
            catch(Exception e){failure=e;}
            finally
            {
                RenderTexture.active=previous;GL.sRGBWrite=srgb;GL.wireframe=wire;ShaderUtil.allowAsyncCompilation=async;
                foreach(var r in roads)if(r)LTRoadModuleSource.Forget(r);
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);else Debug.Log(tag+report);
        }
        static string Hash(string path)
        {using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream));}
    }
}
