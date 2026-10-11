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
    public static class LTRoadSegmentCleanupCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Road Segment Cleanup (no scene changes)
        public static void Run()
        {
            const string tag="[Road Segment Cleanup v1] ";
            if(EditorApplication.isCompiling||EditorApplication.isPlayingOrWillChangePlaymode)return;
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var files=scenes.Select(s=>s.scene.path).Concat(Directory.GetFiles("ProjectSettings"))
                .Concat(new[]{"Assets/TerrainSystem/LocalTerrain/Editor/LTRoadSegmentCleanup.cs","Assets/TerrainSystem/LocalTerrain/Editor/LTRoadEditor.cs",
                    "Assets/TerrainSystem/LocalTerrain/LTRoadGenerated.cs","Packages/manifest.json","Packages/packages-lock.json"})
                .Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();Exception failure=null;
            T Own<T>(T item)where T:Object{item.hideFlags=HideFlags.HideAndDontSave;owned.Add(item);return item;}
            GameObject Child(string name,Transform parent=null)
            {var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,preview);if(parent)go.transform.SetParent(parent,false);return go;}
            void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
            try
            {
                var owner=Child("Owned cleanup road");var road=owner.AddComponent<LTRoad>();road.enabled=false;road.bakeId="owned-cleanup-fixture";
                var root=Child("Road Generated",owner.transform);road.generatedRoot=root.transform;
                var marker=root.AddComponent<LTRoadGenerated>();marker.owner=road;marker.bakeId=road.bakeId;
                var mesh=Own(new Mesh());mesh.vertices=new[]{Vector3.zero,Vector3.forward,Vector3.right};mesh.triangles=new[]{0,1,2};
                GameObject Segment(string name,bool active,bool obsolete)
                {
                    var go=Child(name,root.transform);var m=go.AddComponent<LTRoadGenerated>();m.owner=road;m.bakeId=road.bakeId;
                    var lod=go.AddComponent<LTRoadLOD>();lod.owner=road;lod.meshes=new[]{mesh};
                    go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>();go.AddComponent<MeshCollider>().sharedMesh=mesh;
                    LTRoadSegmentCleanup.Mark(road,go.transform,obsolete,false);go.SetActive(active);return go;
                }
                var old=Segment("Segment_old",false,true);var old2=Segment("Segment_old_2",false,true);
                var active=Segment("Segment_current",true,false);var manuallyHidden=Segment("Segment_hidden",false,false);
                var children=Segment("Segment_user_children",false,true);Child("User child",children.transform);
                var custom=Segment("Segment_user_component",false,true);custom.AddComponent<BoxCollider>();
                var foreign=Segment("Segment_other_owner",false,true);foreign.GetComponent<LTRoadGenerated>().owner=null;
                var otherBake=Segment("Segment_other_bake",false,true);otherBake.GetComponent<LTRoadGenerated>().bakeId="another";
                var activeOld=Segment("Segment_active_obsolete",true,true);
                Require(LTRoadSegmentCleanup.Candidates(road).Select(t=>t.gameObject).ToHashSet().SetEquals(new[]{old,old2}),"Cleanup selected a manually hidden/current/custom/foreign segment.");
                LTRoadSegmentCleanup.Mark(road,old.transform,false,false);
                Require(LTRoadSegmentCleanup.Candidates(road).Length==1,"Reused segment kept obsolete flag.");
                LTRoadSegmentCleanup.Mark(road,old.transform,true,false);
                int count=LTRoadSegmentCleanup.Remove(road,false);
                Require(count==2&&!old&&!old2,"Did not remove exactly the two obsolete fixture segments.");
                Require(active&&manuallyHidden&&children&&custom&&foreign&&otherBake&&activeOld,"Removed unrelated object.");
                Require(mesh&&mesh.vertexCount==3&&mesh.GetIndexCount(0)==3&&active.GetComponent<MeshFilter>().sharedMesh==mesh,"Deleted or changed the shared mesh.");
                Require(LTRoadSegmentCleanup.Remove(road,false)==0,"Cleanup is not idempotent.");
                // No PerformUndo/ClearAll: global Undo callbacks could queue real user roads.
                // Only temporary object selection/removal is exercised; the public button
                // uses Undo.DestroyObjectImmediate and needs a separate user Undo check.
            }
            catch(Exception e){failure=e;}
            finally
            {
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                foreach(var file in files)if(!File.Exists(file.Key)||Hash(file.Key)!=file.Value)
                    failure=new InvalidOperationException("File changed: "+file.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);
            else Debug.Log(tag+"PASS: explicit obsolete flags, activeSelf filtering with inactive parent, reuse, manual/foreign/custom/child guards, shared meshes preserved, idempotent owned-object removal. No user scenes/assets saved or deleted. Global Undo stack not exercised or cleared.");
        }
        static string Hash(string path)
        {using var stream=File.OpenRead(path);using var sha=SHA256.Create();return Convert.ToBase64String(sha.ComputeHash(stream));}
    }
}
