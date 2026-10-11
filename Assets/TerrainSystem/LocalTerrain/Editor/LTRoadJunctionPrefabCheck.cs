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
    public static class LTRoadJunctionPrefabCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Authored Junction Prefab (no scene changes)
        public static void Run()=>RunCore(false);
        // Archived validation menu: Tools/Local Terrain/Validate Simple Junction Workflow (no scene changes)
        public static void RunSimple()=>RunCore(true);
        static void RunCore(bool single)
        {
            string tag=single?"[Simple Junction Workflow v1] ":"[Authored Junction Prefab v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();var roads=new List<LTRoad>();
            Exception failure=null;string report=null;int checks=0;
            void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            void Reject(Action action,string message){bool failed=false;try{action();}catch(ArgumentException){failed=true;}Require(failed,message);}
            T Own<T>(T value)where T:Object{value.hideFlags=HideFlags.HideAndDontSave;owned.Add(value);return value;}
            GameObject Child(string name,Transform parent=null)
            {
                var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,preview);
                if(parent)go.transform.SetParent(parent,false);return go;
            }
            Mesh MeshFrom(LTRoadMesh.Chunk c)
            {
                var mesh=Own(new Mesh());mesh.vertices=c.vertices;mesh.normals=c.normals;mesh.tangents=c.tangents;
                mesh.uv=c.uv;mesh.uv2=c.uv2;mesh.uv3=c.uv3;mesh.uv4=c.uv4;mesh.colors=c.colors;
                mesh.subMeshCount=c.submeshes.Length;
                for(int s=0;s<c.submeshes.Length;s++)mesh.SetTriangles(c.submeshes[s],s);mesh.RecalculateBounds();return mesh;
            }
            try
            {
                var shader=Shader.Find("HDRP/Lit");Require(shader,"HDRP/Lit unavailable.");var material=Own(new Material(shader));
                var root=Child("Owned junction validation");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;root.SetActive(true);
                var prefab=Child("Owned T prefab");var sourceChunks=new[]{Fixture(true,true),Fixture(true,false)};
                var renderers=new Renderer[sourceChunks.Length];var sourceMeshes=new Mesh[sourceChunks.Length];
                for(int l=0;l<sourceChunks.Length;l++)
                {
                    var child=Child("LOD"+l,prefab.transform);sourceMeshes[l]=MeshFrom(sourceChunks[l]);
                    child.AddComponent<MeshFilter>().sharedMesh=sourceMeshes[l];var renderer=child.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;
                    renderers[l]=renderer;
                }
                prefab.AddComponent<LODGroup>().SetLODs(new[]{new LOD(.6f,new[]{renderers[0]}),new LOD(.15f,new[]{renderers[1]})});
                var sockets=new List<LTRoadJunctionSocket>();
                foreach(var entry in new[]{(new Vector3(-6,0,0),-90f),(new Vector3(6,0,0),90f),(new Vector3(0,0,-6),180f)})
                {
                    var go=Child("Port"+sockets.Count,prefab.transform);go.transform.localPosition=entry.Item1;go.transform.localRotation=Quaternion.Euler(0,entry.Item2,0);
                    sockets.Add(go.AddComponent<LTRoadJunctionSocket>());
                }
                if(single)prefab.transform.SetParent(root.transform,false);
                var node=(single?prefab:Child("Owned junction",root.transform)).AddComponent<LTRoadJunction>();
                node.meshSource=single?LTRoadJunctionSource.OwnHierarchy:LTRoadJunctionSource.AuthoredPrefab;node.surface=LTRoadMode.Asphalt;node.junctionPrefab=single?null:prefab;node.radius=9;
                node.foundationMode=LTRoadJunctionFoundation.Circle; // This legacy fixture explicitly tests the radius guard.
                node.transform.localPosition=new Vector3(902,4,137);node.transform.localRotation=Quaternion.Euler(0,17,0);node.gameObject.SetActive(true);
                var raw=Enumerable.Range(0,2).Select(l=>LTRoadJunctionPrefab.Read(prefab,new[]{renderers[l]},null,new List<Material>())).ToArray();
                var hashes=raw.Select(LTRoadMesh.ContentHash).ToArray();var prepared=LTRoadJunctionPrefab.Build(node,true);
                Require(prepared.levels.Length==2&&prepared.materials.Length==1&&prepared.materials[0]==material,"Authored LOD/material import.");
                for(int l=0;l<2;l++)
                {
                    var a=raw[l];var b=prepared.levels[l];
                    Require(a.vertices.Length==b.vertices.Length&&a.triangles.SequenceEqual(b.triangles),"Prefab topology changed.");
                    Require(a.uv.SequenceEqual(b.uv)&&a.uv2.SequenceEqual(b.uv2)&&a.uv3.SequenceEqual(b.uv3)&&a.uv4.SequenceEqual(b.uv4)&&a.colors.SequenceEqual(b.colors),"Authored channels changed.");
                    Require(a.normals.SequenceEqual(b.normals)&&a.tangents.SequenceEqual(b.tangents),"Authored shading changed.");
                }
                var mask=LTRoadBlendMask.Build(new[]{prepared.levels[0]},Matrix4x4.identity,.1f);
                Require(mask.holePixels>0,"Authored hole was filled.");
                var roadMeshes=new[]{MeshFrom(Fixture(false,true)),MeshFrom(Fixture(false,false))};
                int seams=0;
                foreach(var socket in sockets)foreach(bool start in new[]{true,false})
                {
                    var road=Child("Owned road",root.transform).AddComponent<LTRoad>();roads.Add(road);
                    road.mode=LTRoadMode.Asphalt;road.asphaltSource=LTRoadMeshSource.Module;road.asphaltModule=roadMeshes[0];road.moduleLODMeshes=new[]{roadMeshes[1]};
                    road.asphaltLODMode=LTRoadLODMode.Authored;road.asphaltLODs=new[]{new LTLODSettings(1,.01f,50)};
                    road.asphaltMaterial=material;road.width=4;road.moduleLength=8;road.moduleBaseY=0;road.moduleFuseEnds=true;road.surfaceOffset=single?.17f:node.surfaceOffset;
                    road.transform.localPosition=new Vector3(870,1,110);road.transform.localRotation=Quaternion.Euler(0,-13,0);
                    var sm=node.SocketMatrix(socket);var p=sm.MultiplyPoint3x4(Vector3.zero);var end=p+sm.MultiplyVector(Vector3.forward)*30;
                    LTRoadPoint Point(Vector3 v)=>new LTRoadPoint(road.transform.InverseTransformPoint(world.transform.TransformPoint(v)));
                    road.points=start?new List<LTRoadPoint>{Point(p),Point(end)}:new List<LTRoadPoint>{Point(end),Point(p)};
                    road.gameObject.SetActive(true);
                    if(single)
                    {
                        string initial=EditorJsonUtility.ToJson(road);var cached=road.Capture(world);
                        LTRoadJunctionConnection.Prepare(road,start,node,socket,true);
                        Require(initial==EditorJsonUtility.ToJson(road)&&ReferenceEquals(cached,road.Capture(world)),"Connection preview mutated road/cache.");
                        road.width=5;string invalid=EditorJsonUtility.ToJson(road);
                        Reject(()=>LTRoadJunctionConnection.Connect(road,start,node,socket,true),"Incompatible width connected.");
                        Require(invalid==EditorJsonUtility.ToJson(road),"Failed connection left serialized edits.");road.width=4;
                        road.moduleBaseY=.05f;invalid=EditorJsonUtility.ToJson(road);
                        Reject(()=>LTRoadJunctionConnection.Connect(road,start,node,socket,true),"Geometric profile mismatch connected.");
                        Require(invalid==EditorJsonUtility.ToJson(road),"Failed geometric preflight changed road references/points.");road.moduleBaseY=0;
                        LTRoadJunctionConnection.Connect(road,start,node,socket,true);
                        Require((start?road.startJunction:road.endJunction)==node,"Connection not applied.");
                    }
                    else if(start){road.startJunction=node;road.startJunctionSocket=socket;}else{road.endJunction=node;road.endJunctionSocket=socket;}
                    var snapshot=road.Capture(world);var result=LTRoadModuleSource.BuildPreview(road,snapshot);
                    var before=result.levels.Select(level=>level[start?0:level.Count-1]).ToArray();
                    LTRoadJunctionPrefab.MatchRoad(node,road,start,result,prepared,true);
                    for(int l=0;l<result.levels.Length;l++)
                    {
                        var after=result.levels[l][start?0:result.levels[l].Count-1];
                        Require(before[l].triangles.SequenceEqual(after.triangles)&&before[l].uv.SequenceEqual(after.uv),"Road seam changed topology/UV.");
                        // Compare against every independently switched junction LOD.
                        var frame=node.transform.localToWorldMatrix*Matrix4x4.Translate(Vector3.up*node.surfaceOffset)*socket.InPrefab(prefab);
                        for(int j=0;j<prepared.levels.Length;j++)
                        {
                            var canonical=LTRoadJunctionSeams.Create(prepared.levels[j],frame.inverse*node.transform.localToWorldMatrix,node.socketTolerance,4);
                            LTRoadJunctionSeams.Snap(after,frame.inverse*road.transform.localToWorldMatrix,canonical);seams++;
                        }
                    }
                    // Changing the selected socket must not return the cached old path.
                    var other=sockets.First(s=>s!=socket);
                    if(start)road.startJunctionSocket=other;else road.endJunctionSocket=other;
                    Reject(()=>road.Capture(world),"Stale endpoint cache after socket selection.");
                    if(start)road.startJunctionSocket=socket;else road.endJunctionSocket=socket;
                    Require(road.Capture(world).geometryHash==snapshot.geometryHash,"Restored socket path changed.");
                    // A duplicate active connection cannot occupy the same entrance.
                    var duplicate=Child("Occupied socket check",root.transform).AddComponent<LTRoad>();roads.Add(duplicate);
                    duplicate.startJunction=node;duplicate.startJunctionSocket=socket;duplicate.gameObject.SetActive(true);
                    Reject(()=>node.Port(road,start),"Duplicate connection accepted.");duplicate.gameObject.SetActive(false);
                    if(single)
                    {
                        node.Endpoint(road,start,out var attached,out _);LTRoadJunctionConnection.Disconnect(road,start,true);
                        Require(!(start?road.startJunction:road.endJunction)&&!(start?road.startJunctionSocket:road.endJunctionSocket),"Disconnect retained binding.");
                        Require(road.points[start?0:road.points.Count-1].position==attached.position,"Disconnected endpoint jumped to stale original.");
                        LTRoadJunctionConnection.Connect(road,start,node,socket,true);
                    }
                    road.gameObject.SetActive(false);
                }
                string sourceKey=LTRoadJunctionPrefab.DependencyKey(node);
                var output=Child("Owned LOD output",single?node.transform:root.transform);var filter=output.AddComponent<MeshFilter>();var collider=output.AddComponent<MeshCollider>();
                var lod=output.AddComponent<LTRoadLOD>();lod.junction=node;lod.meshes=prepared.levels.Select(MeshFrom).ToArray();collider.sharedMesh=lod.meshes[0];
                for(int l=0;l<2;l++){lod.Show(l);Require(filter.sharedMesh==lod.meshes[l]&&collider.sharedMesh==lod.meshes[0],"LOD changed collider.");}
                if(single)
                {
                    output.AddComponent<MeshRenderer>().sharedMaterial=material;output.AddComponent<LTRoadJunctionGenerated>().owner=node;node.generatedRoot=output.transform;
                    output.SetActive(true);var group=prefab.GetComponent<LODGroup>();var sourceCollider=renderers[0].gameObject.AddComponent<MeshCollider>();sourceCollider.sharedMesh=sourceMeshes[0];
                    // Keep intentionally disabled renderer state, not just the enabled case.
                    renderers[1].enabled=false;bool[] beforeVisibility=renderers.Select(r=>r.enabled).ToArray();
                    node.SetSourceSuppressed(true);
                    Require(renderers.All(r=>!r.enabled)&&!group.enabled&&!sourceCollider.enabled,"Duplicate source render/collider not hidden.");
                    Require(output.activeSelf&&collider.enabled,"Generated output was suppressed with its sources.");
                    Require(sourceKey==LTRoadJunctionPrefab.DependencyKey(node),"Publishing recursively changed source dependency.");
                    var again=LTRoadJunctionPrefab.Build(node,true);
                    Require(again.levels.Select(c=>c.hash).SequenceEqual(prepared.levels.Select(c=>c.hash)),"Second bake ingested generated geometry or lost authored LODs.");
                    node.SetSourceSuppressed(false);
                    Require(renderers.Select(r=>r.enabled).SequenceEqual(beforeVisibility)&&group.enabled&&sourceCollider.enabled,"Original visibility not restored.");
                    node.SetSourceSuppressed(true);node.enabled=false;
                    Require(!output.activeSelf&&sourceCollider.enabled&&group.enabled,"Disable lifecycle lost source preview.");node.enabled=true;
                    Require(output.activeSelf&&!sourceCollider.enabled&&!group.enabled,"Enable lifecycle duplicated source/output.");
                    node.transform.SetParent(null,true);
                    Require(!node.World&&!output.activeSelf&&sourceCollider.enabled&&group.enabled,"Moving outside LTWorld lost source preview.");
                    node.transform.SetParent(root.transform,true);
                    Require(node.World==world&&output.activeSelf&&!sourceCollider.enabled&&!group.enabled,"Moving back into LTWorld did not restore generated output.");
                }
                Require(LTRoadLODPolicy.Select(10000,0,node.lods,2,5)==1,"Distance LOD policy.");
                var wrong=sourceMeshes[1].vertices;int changed=Array.FindIndex(wrong,v=>Mathf.Abs(v.x-6)<1e-5f&&Mathf.Abs(v.z)<1e-5f);
                Require(changed>=0,"Broken seam fixture vertex missing.");var saved=wrong[changed];wrong[changed]+=Vector3.up*.05f;sourceMeshes[1].vertices=wrong;
                Reject(()=>LTRoadJunctionPrefab.Build(node,true),"Incompatible authored LOD accepted.");wrong[changed]=saved;sourceMeshes[1].vertices=wrong;
                var savedRotation=sockets[0].transform.localRotation;sockets[0].transform.localRotation*=Quaternion.Euler(0,180,0);
                Reject(()=>LTRoadJunctionPrefab.Build(node,true),"Inward socket accepted.");sockets[0].transform.localRotation=savedRotation;
                var savedWidth=sockets[0].width;sockets[0].width=5;Reject(()=>LTRoadJunctionPrefab.Build(node,true),"Wrong socket width accepted.");sockets[0].width=savedWidth;
                var savedRadius=node.radius;node.radius=1;Reject(()=>LTRoadJunctionPrefab.Build(node,true),"Uncovered foundation accepted.");node.radius=savedRadius;
                for(int l=0;l<2;l++)Require(hashes[l]==LTRoadMesh.ContentHash(LTRoadJunctionPrefab.Read(prefab,new[]{renderers[l]},null,new List<Material>())) ,"Source mesh changed.");
                report=$"PASS {checks} checks; "+(single?"one-object source, read-only preflight/failed binding, connect/disconnect, source visibility/lifecycle, repeated bake exclusion; ":"")+$"synthetic T, 2 authored LODs, 3 sockets, start/end approaches, {seams} mixed-LOD seams, unchanged indices/UV/shading, hole, invalid input, collider LOD0. No scene/assets saved. Circular foundation. Scene GUI input, Undo gestures, junction material blending and live appearance require manual checks.";
            }
            catch(Exception e){failure=e;}
            finally
            {
                foreach(var road in roads)if(road)LTRoadModuleSource.Forget(road);
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene state changed: "+s.scene.name,failure);
                foreach(var file in files)if(!File.Exists(file.Key)||Hash(file.Key)!=file.Value)failure=new InvalidOperationException("File changed during validation: "+file.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+failure);else Debug.Log(tag+report);
        }
        static string Hash(string path)
        {using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input));}

        // Fine/coarse interiors share their authored boundaries; no retopology in the baker.
        static LTRoadMesh.Chunk Fixture(bool junction,bool fine)
        {
            var vertices=new List<Vector3>();var indices=new List<int>();var ids=new Dictionary<Vector3,int>();
            int Id(float x,float z){var p=new Vector3(x,0,z);if(!ids.TryGetValue(p,out int id)){id=vertices.Count;vertices.Add(p);ids.Add(p,id);}return id;}
            for(int z=junction?-6:0;z<(junction?2:8);z++)for(int x=junction?-6:-2;x<(junction?6:2);x++)
            {
                if(junction&&(z< -2&&(x< -2||x>=2)||x==0&&z==0))continue;
                int a=Id(x,z),b=Id(x,z+1),c=Id(x+1,z),d=Id(x+1,z+1);
                if(fine){int mid=Id(x+.5f,z+.5f);indices.AddRange(new[]{a,b,mid,b,d,mid,d,c,mid,c,a,mid});}
                else indices.AddRange(new[]{a,b,c,c,b,d});
            }
            var v=vertices.ToArray();return new LTRoadMesh.Chunk{vertices=v,triangles=indices.ToArray(),submeshes=new[]{indices.ToArray()},
                normals=v.Select(_=>Vector3.up).ToArray(),tangents=v.Select(_=>new Vector4(1,0,0,-1)).ToArray(),
                uv=v.Select(p=>new Vector2(p.x,p.z)).ToArray(),uv2=v.Select(p=>new Vector2(p.z,p.x)).ToArray(),
                uv3=v.Select(p=>new Vector2(p.x,.5f)).ToArray(),uv4=v.Select(p=>new Vector2(.5f,p.z)).ToArray(),colors=v.Select(_=>Color.white).ToArray()};
        }
    }
}
