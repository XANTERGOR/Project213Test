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
    public static partial class LTRoadTerrainLODCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Road Foundation LOD (no scene changes)
        public static void RunNative()
        {
            const string tag="[Road Foundation LOD v2] ";
            if(EditorApplication.isCompiling||EditorApplication.isPlayingOrWillChangePlaymode)return;
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var files=scenes.Select(s=>s.scene.path).Concat(Directory.GetFiles("ProjectSettings"))
                .Concat(Directory.GetFiles("Assets/TerrainSystem/LocalTerrain/Editor","LTRoadTerrainLOD*.cs"))
                .Concat(new[]{"Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs","Assets/TerrainSystem/LocalTerrain/Editor/LTHoleStampEditor.cs",
                    "Assets/TerrainSystem/LocalTerrain/LTRoad.cs","Packages/manifest.json","Packages/packages-lock.json"})
                .Where(File.Exists).Distinct().ToDictionary(p=>p,FileHash);
            var owned=new List<Object>();var preview=EditorSceneManager.NewPreviewScene();Exception failure=null;string data=null;
            T Own<T>(T item)where T:Object{item.hideFlags=HideFlags.HideAndDontSave;owned.Add(item);return item;}
            GameObject Child(string name,Transform parent=null)
            {var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,preview);if(parent)go.transform.SetParent(parent,false);return go;}
            try
            {
                data=RunData();
                var root=Child("Owned road foundation validation");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;root.SetActive(true);
                var go=Child("Owned road",root.transform);var road=go.AddComponent<LTRoad>();road.enabled=false;
                road.mode=LTRoadMode.Asphalt;road.simplifyTerrainUnderAsphalt=true;
                road.points=new List<LTRoadPoint>{new LTRoadPoint(new Vector3(8,0,0)),new LTRoadPoint(new Vector3(8,0,16))};
                go.SetActive(true);var snapshot=road.Capture(world);
                Require(LTRoadTerrainLODCapture.Capture(road,world,snapshot,out _)==null,"Missing road must retain the old protection.");
                var generated=Child("Owned generated",go.transform);road.generatedRoot=generated.transform;
                generated.AddComponent<LTRoadGenerated>().owner=road;generated.SetActive(true);
                var segment=Child("Owned segment",generated.transform);var lod=segment.AddComponent<LTRoadLOD>();lod.owner=road;
                var filter=segment.AddComponent<MeshFilter>();var collider=segment.AddComponent<MeshCollider>();segment.SetActive(true);
                Mesh Make(float height,int level)
                {
                    var input=Grid(height,level);var mesh=Own(new Mesh());mesh.vertices=input.vertices;mesh.triangles=input.indices;mesh.RecalculateBounds();return mesh;
                }
                lod.meshes=new[]{Make(.04f,0),Make(.02f,1)};filter.sharedMesh=collider.sharedMesh=lod.meshes[0];
                road.bakedGeometryHash=snapshot.geometryHash;
                var surface=LTRoadTerrainLODCapture.Capture(road,world,snapshot,out string key);
                Require(surface!=null&&surface.TriangleCount==lod.meshes.Sum(m=>(int)m.GetIndexCount(0)/3),"Did not capture all road LODs.");
                Require(ReferenceEquals(surface,LTRoadTerrainLODCapture.Capture(road,world,snapshot,out _)),"Unchanged road was recopied.");
                road.terrainLODEdgeError=.08f;
                var tolerance=LTRoadTerrainLODCapture.Capture(road,world,snapshot,out string toleranceKey);
                Require(tolerance!=null&&toleranceKey!=key&&tolerance.EdgeError==.08f&&surface.EdgeError==.03f,"Edge error did not invalidate the immutable snapshot.");
                road.terrainLODEdgeError=.03f;surface=LTRoadTerrainLODCapture.Capture(road,world,snapshot,out key);
                var area=new Rect(5,3,1,1);
                Require(!surface.Blocks(area,0,0,0,0),"Clear terrain cannot simplify.");
                foreach(int level in new[]{1,0,1})
                {
                    lod.Show(level);
                    Require(ReferenceEquals(surface,LTRoadTerrainLODCapture.Capture(road,world,snapshot,out string switched))&&switched==key,"Display LOD invalidates captured geometry.");
                    Require(collider.sharedMesh==lod.meshes[0],"LOD switch changed collider.");
                }
                // Mutating a retained Mesh object is tracked by its Unity dirty version.
                var points=lod.meshes[1].vertices;for(int i=0;i<points.Length;i++)points[i].y=-.01f;
                lod.meshes[1].vertices=points;EditorUtility.SetDirty(lod.meshes[1]);
                var lower=LTRoadTerrainLODCapture.Capture(road,world,snapshot,out string changed);
                Require(lower!=null&&changed!=key&&lower.Blocks(area,0,0,0,0),"Lower LOD mesh update was ignored.");
                Require(!surface.Blocks(area,0,0,0,0),"Accepted snapshot changed with native source.");
                segment.transform.localPosition=Vector3.up;
                var moved=LTRoadTerrainLODCapture.Capture(road,world,snapshot,out string movedKey);
                Require(moved!=null&&movedKey!=changed&&!moved.Blocks(area,0,0,0,0),"Generated transform did not invalidate geometry.");
                segment.transform.localPosition=Vector3.zero;
                world.transform.position=new Vector3(901,14,251);world.transform.rotation=Quaternion.Euler(0,23,0);
                var transformed=LTRoadTerrainLODCapture.Capture(road,world,snapshot,out _);
                Require(transformed!=null&&transformed.Blocks(area,0,0,0,0),"World transform not removed from surface snapshot.");
                road.simplifyTerrainUnderAsphalt=false;
                Require(LTRoadTerrainLODCapture.Capture(road,world,snapshot,out _)==null,"Opt-out ignored.");road.simplifyTerrainUnderAsphalt=true;
                road.bakedGeometryHash=unchecked(snapshot.geometryHash+1);
                Require(LTRoadTerrainLODCapture.Capture(road,world,snapshot,out _)==null,"Stale bake accepted.");road.bakedGeometryHash=snapshot.geometryHash;
                generated.SetActive(false);
                Require(LTRoadTerrainLODCapture.Capture(road,world,snapshot,out _)==null,"Hidden road accepted.");generated.SetActive(true);
                var last=lod.meshes[1];lod.meshes[1]=null;
                Require(LTRoadTerrainLODCapture.Capture(road,world,snapshot,out _)==null,"Partial LOD chain accepted.");lod.meshes[1]=last;
                last.UploadMeshData(true);
                Require(LTRoadTerrainLODCapture.Capture(road,world,snapshot,out _)==null,"Unreadable road accepted.");
            }
            catch(Exception e){failure=e;}
            finally
            {
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    failure=new InvalidOperationException("User scene state changed: "+s.scene.name,failure);
                foreach(var f in files)if(!File.Exists(f.Key)||FileHash(f.Key)!=f.Value)
                    failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);
            else Debug.Log(tag+data+" Native capture PASS: all LODs, cache, transforms, stale/missing/unreadable fallbacks, unchanged snapshots and LOD0 collider. No user scene/assets saved. Not a rendered-scene or performance check.");
        }
        static string FileHash(string path)
        {using var stream=File.OpenRead(path);using var sha=SHA256.Create();return Convert.ToBase64String(sha.ComputeHash(stream));}
    }
}
