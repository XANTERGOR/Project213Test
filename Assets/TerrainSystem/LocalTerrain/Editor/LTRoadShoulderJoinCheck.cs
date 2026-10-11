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
    public static class LTRoadShoulderJoinCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Road Shoulder Joins (no scene changes)
        public static void Run()
        {
            const string tag="[Road Shoulder Joins v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var scene=EditorSceneManager.NewPreviewScene();GameObject root=null;var meshes=new List<Mesh>();LTSurfaceLayer ground=null;
            Exception failure=null;int checks=0;
            void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            try
            {
                root=new GameObject("Owned shoulder join validation"){hideFlags=HideFlags.HideAndDontSave};root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root,scene);var world=root.AddComponent<LTWorld>();world.enabled=false;world.autoUpdate=false;
                root.transform.position=new Vector3(90,5,130);root.transform.rotation=Quaternion.Euler(0,17,0);root.SetActive(true);
                GameObject Child(string name)
                {var go=new GameObject(name){hideFlags=HideFlags.HideAndDontSave};go.SetActive(false);go.transform.SetParent(root.transform,false);return go;}
                var model=Child("Owned junction");var node=model.AddComponent<LTRoadJunction>();node.meshSource=LTRoadJunctionSource.OwnHierarchy;
                node.surface=LTRoadMode.Asphalt;node.foundationMode=LTRoadJunctionFoundation.MeshFootprint;node.foundationShoulder=2;
                node.blendWidth=4;node.neckLength=6;node.shoulderJoinLength=6;
                var renderers=new List<Renderer>();
                for(int lod=0;lod<2;lod++)
                {
                    var mesh=new Mesh{name="Owned source LOD"+lod,hideFlags=HideFlags.HideAndDontSave};meshes.Add(mesh);
                    mesh.vertices=new[]{new Vector3(-2,0,-10),new Vector3(2,0,-10),new Vector3(2,0,0),new Vector3(-2,0,0)};
                    mesh.uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up};mesh.triangles=new[]{0,2,1,0,3,2};mesh.RecalculateNormals();mesh.RecalculateBounds();
                    var part=Child("Owned LOD"+lod);part.transform.SetParent(model.transform,false);
                    part.AddComponent<MeshFilter>().sharedMesh=mesh;renderers.Add(part.AddComponent<MeshRenderer>());part.SetActive(true);
                }
                var group=model.AddComponent<LODGroup>();group.SetLODs(new[]{new LOD(.5f,new[]{renderers[0]}),new LOD(.1f,new[]{renderers[1]})});group.RecalculateBounds();
                LTRoadJunctionSocket Socket(string name,Vector3 p,float yaw)
                {
                    var go=Child(name);go.transform.SetParent(model.transform,false);go.transform.localPosition=p;go.transform.localRotation=Quaternion.Euler(0,yaw,0);
                    var socket=go.AddComponent<LTRoadJunctionSocket>();socket.width=4;go.SetActive(true);return socket;
                }
                var port=Socket("Owned front",Vector3.zero,0);Socket("Owned back",new Vector3(0,0,-10),180);model.SetActive(true);
                var goRoad=Child("Owned road");var road=goRoad.AddComponent<LTRoad>();road.mode=LTRoadMode.Asphalt;road.width=4;
                road.shoulderWidth=.3f;road.blendWidth=1;road.projection=LTRoadProjection.World;
                road.points=new List<LTRoadPoint>{new LTRoadPoint(Vector3.zero),new LTRoadPoint(new Vector3(0,0,15)),new LTRoadPoint(new Vector3(0,0,40))};
                road.startJunction=node;road.startJunctionSocket=port;goRoad.SetActive(true);
                ground=ScriptableObject.CreateInstance<LTSurfaceLayer>();ground.hideFlags=HideFlags.HideAndDontSave;road.groundLayer=ground;node.groundLayer=ground;
                var baseline=road.Capture(world);var sourceMeshes=meshes.Select(m=>(vertices:m.vertices,uv:m.uv,triangles:m.triangles)).ToArray();
                var sourceRoad=EditorJsonUtility.ToJson(road);int roadDirty=EditorUtility.GetDirtyCount(road);
                node.coordinateShoulders=true;var joined=road.Capture(world);
                Check(joined.HasShoulderJoins&&joined.geometryHash!=baseline.geometryHash,"Join not captured / terrain key unchanged.");
                Check(ReferenceEquals(joined,road.Capture(world)),"Unchanged road does not reuse join cache.");
                Check(joined.samples.SequenceEqual(baseline.samples)&&joined.width==baseline.width,"Asphalt path/width changed.");
                var before=LTRoadMesh.Build(baseline,12,4,.06f,Matrix4x4.identity).Select(LTRoadMesh.ContentHash).ToArray();
                var after=LTRoadMesh.Build(joined,12,4,.06f,Matrix4x4.identity).Select(LTRoadMesh.ContentHash).ToArray();
                Check(before.SequenceEqual(after),"Generated asphalt vertices, indices, shading or UV changed.");
                var foundation=node.Capture();
                foreach(float x in new[]{-7f,-5f,-3f,0f,3f,5f,7f})
                {
                    float Height(float z)=>joined.ApplyHeight(x,z,foundation.ApplyHeight(x,z,-4));
                    Check(Math.Abs(Height(-.0001f)-Height(.0001f))<.002f,"Height discontinuity across port.");
                }
                group.ForceLOD(1);Check(road.Capture(world).FoundationKey==joined.FoundationKey,"Display LOD changed terrain foundation.");group.ForceLOD(-1);
                node.shoulderJoinLength=3;var shorter=road.Capture(world);
                Check(shorter.FoundationKey!=joined.FoundationKey&&shorter.detailHash!=joined.detailHash,"Length did not invalidate terrain/grass.");
                node.shoulderJoinLength=6;node.coordinateShoulders=false;var restored=road.Capture(world);
                Check(restored.geometryHash==baseline.geometryHash&&restored.paintHash==baseline.paintHash&&restored.detailHash==baseline.detailHash,"Disabling does not restore legacy keys.");
                Check(EditorJsonUtility.ToJson(road)==sourceRoad&&EditorUtility.GetDirtyCount(road)==roadDirty,"Read-only capture changed road settings.");
                Check(!road.generatedRoot&&!node.generatedRoot,"Capture produced scene output.");
                for(int i=0;i<meshes.Count;i++)Check(meshes[i].vertices.SequenceEqual(sourceMeshes[i].vertices)&&
                    meshes[i].uv.SequenceEqual(sourceMeshes[i].uv)&&meshes[i].triangles.SequenceEqual(sourceMeshes[i].triangles),"Source LOD mesh was modified.");
            }
            catch(Exception e){failure=e;}
            finally
            {
                if(root)Object.DestroyImmediate(root);foreach(var m in meshes)if(m)Object.DestroyImmediate(m);if(ground)Object.DestroyImmediate(ground);
                EditorSceneManager.ClosePreviewScene(scene);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);
            else Debug.Log(tag+$"PASS {checks} checks; isolated translated/yawed hierarchy, LOD0 foundation, same asphalt vertices/UV, cache/length invalidation, disabled round-trip, source LOD immutability. No scene/assets saved. Live terrain appearance and Burst execution are not covered.");
        }
        static string Hash(string path)
        {using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream));}
    }
}
