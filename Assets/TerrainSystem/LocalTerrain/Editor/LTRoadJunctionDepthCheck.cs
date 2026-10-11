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
    public static class LTRoadJunctionDepthCheck
    {
        const string Folder="Assets/Data/Models/Environment/Roads/Roads_1/";
        // Archived validation menu: Tools/Local Terrain/Validate Junction Mesh Depth (no scene changes)
        public static void Run()
        {
            const string tag="[Junction Mesh Depth v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var files=AssetDatabase.GetDependencies(new[]{Folder+"Road_Perek_1.prefab",Folder+"Road_1.prefab"},true)
                .Concat(Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories))
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();var roads=new List<LTRoad>();
            Exception failure=null;int checks=0,seams=0;string report=null;
            void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            GameObject Child(string name,Transform parent=null)
            {var g=Own(new GameObject(name));g.SetActive(false);SceneManager.MoveGameObjectToScene(g,preview);if(parent)g.transform.SetParent(parent,false);return g;}
            Mesh Mesh(LTRoadMesh.Chunk c)
            {var m=Own(new Mesh{indexFormat=UnityEngine.Rendering.IndexFormat.UInt32});m.vertices=c.vertices;m.triangles=c.triangles;m.RecalculateBounds();return m;}
            try
            {
                var asset=AssetDatabase.LoadAssetAtPath<GameObject>(Folder+"Road_Perek_1.prefab");Check(asset,"Missing Road_Perek_1 prefab.");
                var module=AssetDatabase.LoadAssetAtPath<GameObject>(Folder+"Road_1.prefab");Check(module,"Missing Road_1 prefab.");
                var root=Child("Owned depth validation");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;root.SetActive(true);
                foreach(bool own in new[]{true,false})
                {
                    var instance=own?Own((GameObject)PrefabUtility.InstantiatePrefab(asset,preview)):Child("Separate-prefab depth node");
                    instance.SetActive(false);instance.transform.SetParent(root.transform,false);
                    instance.transform.localPosition=new Vector3(902,4,137);instance.transform.localRotation=Quaternion.Euler(0,17,0);instance.transform.localScale=Vector3.one;
                    var node=instance.GetComponent<LTRoadJunction>();if(!node)node=instance.AddComponent<LTRoadJunction>();
                    node.meshSource=own?LTRoadJunctionSource.OwnHierarchy:LTRoadJunctionSource.AuthoredPrefab;node.junctionPrefab=own?null:asset;
                    node.surface=LTRoadMode.Asphalt;node.connectionFit=LTRoadJunctionFit.FitProfile;node.profileSearchBand=.005f;
                    node.profileMaxShift=.1f;node.profileBlendLength=1;node.neckLength=3;node.surfaceOffset=.06f;node.meshDepth=0;node.radius=50;
                    node.groundLayer=null;node.groundBlend=false;instance.SetActive(true);
                    var original=LTRoadJunctionPrefab.Build(node,true);Check(original.levels.Length>=3,"Authored LODs missing.");
                    var foundation=node.Capture();int originalKey=node.EndpointHash;
                    var r=Child("Owned attached road",root.transform).AddComponent<LTRoad>();roads.Add(r);
                    r.mode=LTRoadMode.Asphalt;r.asphaltSource=LTRoadMeshSource.Module;r.asphaltModulePrefab=module;
                    r.moduleFuseEnds=true;r.moduleFuseTolerance=.00102f;r.moduleFusePlaneTolerance=.00096f;
                    r.moduleLength=11;r.meshChunkLength=32;r.moduleBaseY=.05f;r.surfaceOffset=own?.17f:node.surfaceOffset;
                    r.transform.localPosition=new Vector3(870,1,110);r.transform.localRotation=Quaternion.Euler(0,-13,0);
                    foreach(var socket in node.Sockets)foreach(bool start in new[]{true,false})
                    {
                        r.startJunction=null;r.endJunction=null;r.startJunctionSocket=null;r.endJunctionSocket=null;
                        r.width=socket.width;var sm=node.SocketMatrix(socket);var p=sm.MultiplyPoint3x4(Vector3.zero);var forward=sm.MultiplyVector(Vector3.forward);
                        LTRoadPoint Point(Vector3 v)=>new LTRoadPoint(r.transform.InverseTransformPoint(world.transform.TransformPoint(v)));
                        r.points=new List<LTRoadPoint>{Point(p),Point(p+forward*20),Point(p+forward*45+sm.MultiplyVector(Vector3.right)*5)};
                        if(!start)r.points.Reverse();
                        if(start){r.startJunction=node;r.startJunctionSocket=socket;}else{r.endJunction=node;r.endJunctionSocket=socket;}
                        r.gameObject.SetActive(true);node.meshDepth=0;
                        var baseline=r.Capture(world);node.Endpoint(r,start,out var endpoint,out var neck);
                        string roadBefore=EditorJsonUtility.ToJson(r);
                        foreach(float depth in new[]{0,.05f,.12f})
                        {
                            node.meshDepth=depth;var prepared=LTRoadJunctionPrefab.Build(node,true);var data=node.Capture();
                            Check(node.MeshSurfaceOffset==node.surfaceOffset-depth,"Incorrect signed mesh placement.");
                            Check(data.centre==foundation.centre&&data.outline.SequenceEqual(foundation.outline),"Depth moved foundation geometry.");
                            for(int z=-12;z<=12;z+=2)for(int x=-12;x<=12;x+=2)
                                Check(data.ApplyHeight(902+x,137+z,7)==foundation.ApplyHeight(902+x,137+z,7),"Terrain follows mesh depth.");
                            Check(depth==0?node.EndpointHash==originalKey:node.EndpointHash!=originalKey,"Depth did not invalidate dependent road bakes.");
                            for(int l=0;l<prepared.levels.Length;l++)
                            {
                                var a=original.levels[l];var b=prepared.levels[l];
                                Check(a.triangles.SequenceEqual(b.triangles)&&a.uv.SequenceEqual(b.uv)&&a.normals.SequenceEqual(b.normals)&&a.tangents.SequenceEqual(b.tangents),"Depth changed topology/UV/shading.");
                                Check(a.vertices.Zip(b.vertices,(u,v)=>(v-(u-Vector3.up*depth)).sqrMagnitude<1e-10f).All(v=>v),"Not every LOD vertex was lowered equally.");
                            }
                            node.Endpoint(r,start,out var end,out var straight);
                            Check(endpoint.position==end.position&&neck.position==straight.position,"Depth moved terrain/spline reference points.");
                            var snapshot=r.Capture(world);Check(baseline.samples.Select(s=>s.position).SequenceEqual(snapshot.samples.Select(s=>s.position)),"Depth changed connected road's ground height.");
                            var built=LTRoadModuleSource.BuildPreview(r,snapshot);
                            LTRoadJunctionPrefab.MatchRoad(node,r,start,built,prepared,true);
                            var frame=node.transform.localToWorldMatrix*Matrix4x4.Translate(Vector3.up*node.MeshSurfaceOffset)*socket.InPrefab(node.SourceRoot);
                            foreach(var level in built.levels)
                            {
                                var edge=LTRoadJunctionProfileFit.Read(level[start?0:level.Count-1],frame.inverse*r.transform.localToWorldMatrix,node.profileSearchBand);
                                foreach(var chunk in prepared.levels)
                                {
                                    var target=LTRoadJunctionProfileFit.Read(chunk,frame.inverse*node.transform.localToWorldMatrix,node.profileSearchBand);
                                    Check(edge.points.Length==target.points.Length&&edge.points.Zip(target.points,(a,b)=>(a-b).magnitude<.0003f).All(v=>v),"Sunken mixed-LOD seam mismatch.");seams++;
                                }
                            }
                            Check(roadBefore==EditorJsonUtility.ToJson(r),"Depth rewrote road settings.");
                        }
                        r.gameObject.SetActive(false);
                    }
                    node.meshDepth=.12f;var sunken=LTRoadJunctionPrefab.Build(node,true);
                    var output=Child("Owned sunken collider",node.transform);var filter=output.AddComponent<MeshFilter>();var collider=output.AddComponent<MeshCollider>();
                    var lod=output.AddComponent<LTRoadLOD>();lod.junction=node;lod.meshes=sunken.levels.Select(Mesh).ToArray();collider.sharedMesh=lod.meshes[0];
                    for(int l=0;l<lod.meshes.Length;l++){lod.Show(l);Check(filter.sharedMesh==lod.meshes[l]&&collider.sharedMesh==lod.meshes[0],"Sunken collider does not stay at LOD0.");}
                    node.meshDepth=0;Check(original.levels.Select(LTRoadMesh.ContentHash).SequenceEqual(LTRoadJunctionPrefab.Build(node,true).levels.Select(LTRoadMesh.ContentHash)),"Zero depth does not restore original meshes.");
                    foreach(float bad in new[]{float.NaN,float.PositiveInfinity,-.01f,11})
                    {node.meshDepth=bad;bool rejected=false;try{LTRoadJunctionPrefab.Build(node,true);}catch(ArgumentException){rejected=true;}Check(rejected,"Invalid depth accepted.");}
                    node.meshDepth=0;instance.SetActive(false);
                }
                report=$"PASS {checks} checks, {seams} mixed LOD seams; OwnHierarchy + separate prefab, imported Road_Perek_1 + Road_1, 0/5/12 cm depth, 3 ports/start/end, signed offset, unchanged foundation and connected road ground, translated/yawed placement, topology/UV, lowered collider LOD0, invalid depth and zero restore. No scene/asset saved. Visual burial still needs scene verification.";
            }
            catch(Exception e){failure=e;}
            finally
            {
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
