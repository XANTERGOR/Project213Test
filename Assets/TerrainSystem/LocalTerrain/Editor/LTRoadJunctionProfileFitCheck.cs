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
    public static class LTRoadJunctionProfileFitCheck
    {
        const string Folder="Assets/Data/Models/Environment/Roads/Roads_1/";
        // Archived validation menu: Tools/Local Terrain/Validate Junction Profile Fit (no scene changes)
        public static void Run()
        {
            const string tag="[Junction Profile Fit v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Folder,"*",SearchOption.AllDirectories)).Concat(Directory.GetFiles("ProjectSettings"))
                .Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"}).Concat(scenes.Select(s=>s.scene.path))
                .Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();LTRoad road=null;
            Exception failure=null;int checks=0,seams=0;string report=null;
            void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            GameObject Child(string name,Transform parent=null)
            {
                var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,preview);
                if(parent)go.transform.SetParent(parent,false);return go;
            }
            try
            {
                var asset=AssetDatabase.LoadAssetAtPath<GameObject>(Folder+"Road_Perek_1.prefab");Check(asset,"Road_Perek_1.prefab not found.");
                var root=Child("Owned profile-fit validation");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;root.SetActive(true);
                var instance=Own((GameObject)PrefabUtility.InstantiatePrefab(asset,preview));instance.SetActive(false);instance.transform.SetParent(root.transform,false);
                instance.transform.localPosition=new Vector3(902,4,137);instance.transform.localRotation=Quaternion.Euler(0,17,0);instance.transform.localScale=Vector3.one;
                var node=instance.GetComponent<LTRoadJunction>();if(!node)node=instance.AddComponent<LTRoadJunction>();
                node.meshSource=LTRoadJunctionSource.OwnHierarchy;node.surface=LTRoadMode.Asphalt;node.connectionFit=LTRoadJunctionFit.FitProfile;
                node.profileSearchBand=.005f;node.profileMaxShift=.1f;node.profileBlendLength=1;node.neckLength=3;node.surfaceOffset=.06f;
                node.meshDepth=0;
                var sources=LTRoadModuleLODPlan.ReadPrefabLevels(instance,node.SourceMember);Check(sources!=null&&sources.Length>=3,"Authored junction LODGroup missing.");
                var raw=sources.Select(rs=>LTRoadJunctionPrefab.Read(instance,rs,null,new List<Material>())).ToArray();
                var hashes=raw.Select(LTRoadMesh.ContentHash).ToArray();node.radius=raw.SelectMany(c=>c.vertices).Max(p=>new Vector2(p.x,p.z).magnitude)+1;
                var sockets=node.Sockets;Check(sockets.Length==3,"Expected three authored sockets.");instance.SetActive(true);
                var prepared=LTRoadJunctionPrefab.Build(node,true);
                Check(prepared.levels.Length==sources.Length,"Dropped authored junction LOD.");
                for(int l=0;l<raw.Length;l++)Check(raw[l].triangles.SequenceEqual(prepared.levels[l].triangles)&&raw[l].uv.SequenceEqual(prepared.levels[l].uv),"Junction topology/UV changed.");
                road=Child("Owned fit road",root.transform).AddComponent<LTRoad>();
                road.mode=LTRoadMode.Asphalt;road.asphaltSource=LTRoadMeshSource.Module;road.asphaltLODMode=LTRoadLODMode.Automatic;
                road.asphaltLODs=new[]{new LTLODSettings(1,.01f,30),new LTLODSettings(2,.01f,60),new LTLODSettings(3,.01f,140)};
                road.moduleFuseEnds=true;road.moduleFuseTolerance=.00102f;road.moduleFusePlaneTolerance=.00096f;
                road.moduleLength=11;road.meshChunkLength=32;road.moduleBaseY=.05f;road.surfaceOffset=.17f;road.moduleFitWidth=true;
                road.transform.localPosition=new Vector3(870,1,110);road.transform.localRotation=Quaternion.Euler(0,-13,0);
                for(int variant=1;variant<=5;variant++)
                {
                    road.asphaltModulePrefab=AssetDatabase.LoadAssetAtPath<GameObject>(Folder+$"Road_{variant}.prefab");Check(road.asphaltModulePrefab,"Road module missing.");
                    foreach(var socket in sockets)foreach(bool start in new[]{true,false})
                    {
                        road.startJunction=null;road.endJunction=null;road.startJunctionSocket=null;road.endJunctionSocket=null;
                        road.width=socket.width;var m=node.SocketMatrix(socket);var p=m.MultiplyPoint3x4(Vector3.zero);
                        var outward=m.MultiplyVector(Vector3.forward);var right=m.MultiplyVector(Vector3.right);
                        LTRoadPoint Point(Vector3 v)=>new LTRoadPoint(road.transform.InverseTransformPoint(world.transform.TransformPoint(v)));
                        road.points=new List<LTRoadPoint>{Point(p),Point(p+outward*20),Point(p+outward*45+right*5)};
                        if(!start)road.points.Reverse();road.gameObject.SetActive(true);
                        string serialized=EditorJsonUtility.ToJson(road);var cached=road.Capture(world);
                        LTRoadJunctionConnection.Prepare(road,start,node,socket,true);
                        Check(serialized==EditorJsonUtility.ToJson(road)&&ReferenceEquals(cached,road.Capture(world)),"Preflight changed serialized road/cache.");
                        LTRoadJunctionConnection.Connect(road,start,node,socket,true);
                        var result=LTRoadModuleSource.BuildPreview(road,road.Capture(world));
                        var before=result.levels.Select(cs=>cs[start?0:cs.Count-1]).ToArray();
                        LTRoadJunctionPrefab.MatchRoad(node,road,start,result,prepared,true);
                        var frame=node.transform.localToWorldMatrix*Matrix4x4.Translate(Vector3.up*node.surfaceOffset)*socket.InPrefab(instance);
                        for(int l=0;l<result.levels.Length;l++)
                        {
                            var after=result.levels[l][start?0:result.levels[l].Count-1];
                            Check(after.triangles.SequenceEqual(before[l].triangles)&&after.vertices.Length==before[l].vertices.Length&&after.uv.SequenceEqual(before[l].uv),"Road authored topology/UV changed.");
                            var edge=LTRoadJunctionProfileFit.Read(after,frame.inverse*road.transform.localToWorldMatrix,node.profileSearchBand);
                            for(int j=0;j<prepared.levels.Length;j++)
                            {
                                var target=LTRoadJunctionProfileFit.Read(prepared.levels[j],frame.inverse*node.transform.localToWorldMatrix,node.profileSearchBand);
                                Check(edge.points.Length==target.points.Length&&edge.points.Zip(target.points,(a,b)=>(a-b).magnitude<.0003f).All(x=>x),"Mixed authored LOD seam mismatch.");seams++;
                            }
                        }
                        // A bad limit is rejected before any references are written.
                        LTRoadJunctionConnection.Disconnect(road,start,true);serialized=EditorJsonUtility.ToJson(road);float limit=node.profileMaxShift;node.profileMaxShift=.005f;
                        bool rejected=false;try{LTRoadJunctionConnection.Connect(road,start,node,socket,true);}catch(ArgumentException){rejected=true;}
                        finally{node.profileMaxShift=limit;}
                        Check(rejected&&serialized==EditorJsonUtility.ToJson(road),"Invalid shift partially connected road.");
                        road.gameObject.SetActive(false);
                    }
                }
                var go=Child("Owned fit LOD collider",root.transform);var filter=go.AddComponent<MeshFilter>();var collider=go.AddComponent<MeshCollider>();
                var lod=go.AddComponent<LTRoadLOD>();lod.junction=node;
                lod.meshes=prepared.levels.Select(c=>{var mesh=Own(new Mesh());mesh.vertices=c.vertices;mesh.triangles=c.triangles;mesh.RecalculateBounds();return mesh;}).ToArray();
                collider.sharedMesh=lod.meshes[0];
                for(int l=0;l<lod.meshes.Length;l++){lod.Show(l);Check(filter.sharedMesh==lod.meshes[l]&&collider.sharedMesh==lod.meshes[0],"LOD changed collider LOD0.");}
                for(int l=0;l<raw.Length;l++)Check(hashes[l]==LTRoadMesh.ContentHash(LTRoadJunctionPrefab.Read(instance,sources[l],null,new List<Material>())),"Imported source mesh changed.");
                report=$"PASS {checks} checks, {seams} mixed LOD seams; imported Road_Perek_1 + Road_1..5, all authored LODs, 3 sockets, start/end, rotated/translated curved approaches, base height 0.05, read-only preflight/failure, unchanged topology/UV/source, collider LOD0. No scene or asset saved. Scene interaction and live appearance still need manual verification.";
            }
            catch(Exception e){failure=e;}
            finally
            {
                if(road)LTRoadModuleSource.Forget(road);
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene state changed: "+s.scene.name,failure);
                foreach(var file in files)if(!File.Exists(file.Key)||Hash(file.Key)!=file.Value)failure=new InvalidOperationException("File changed during validation: "+file.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+failure);else Debug.Log(tag+report);
        }
        static string Hash(string path)
        {using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream));}
    }
}
