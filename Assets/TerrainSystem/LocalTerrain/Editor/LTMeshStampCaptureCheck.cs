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
    public static partial class LTEditorEngine
    {
        // Live reload probe 2 (2026-10-10): comment-only change with skip diagnostics; validator behavior is unchanged.
        [MenuItem("Tools/Local Terrain/Validate Mesh Stamp Capture Cache (no scene changes)")]
        public static void ValidateMeshStampCaptureCache()
        {
            const string tag="[Mesh Stamp Capture Cache v2] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            string Hash(string path){using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));}
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();GameObject root=null;int worldId=0,checks=0;
            var ownedMeshes=new List<Mesh>();Exception failure=null;
            void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            try
            {
                root=new GameObject("Owned mesh stamp cache validation"){hideFlags=HideFlags.HideAndDontSave};root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root,preview);var world=root.AddComponent<LTWorld>();world.enabled=false;world.autoUpdate=false;
                worldId=world.GetInstanceID();root.transform.position=new Vector3(90,4,70);
                var source=new Mesh{name="Owned cache source",hideFlags=HideFlags.HideAndDontSave};ownedMeshes.Add(source);
                source.vertices=new[]{new Vector3(-2,0,-2),new Vector3(2,0,-2),new Vector3(2,0,2),new Vector3(-2,0,2),new Vector3(-2,0,-2)};
                source.subMeshCount=2;source.SetTriangles(new[]{0,1,2},0);source.SetTriangles(new[]{4,2,3},1);source.RecalculateBounds();
                var child=new GameObject("Owned mesh stamp"){hideFlags=HideFlags.HideAndDontSave};child.SetActive(false);child.transform.SetParent(root.transform,false);
                var stamp=child.AddComponent<LTMeshStamp>();stamp.sourceMesh=source;var filter=child.GetComponent<MeshFilter>();filter.sharedMesh=source;
                child.SetActive(true);root.SetActive(true);
                int id=stamp.GetInstanceID();var state=GetState(world);
                Stamp Snapshot(bool cold=false)=>Capture(world,cold).Single(s=>s.id==id);
                void Equal(Stamp a,Stamp b)
                {
                    Check(a.meshVertices.SequenceEqual(b.meshVertices)&&a.meshTriangles.SequenceEqual(b.meshTriangles)&&
                        a.meshAllTriangles.SequenceEqual(b.meshAllTriangles)&&a.meshBoundaryEdges.SequenceEqual(b.meshBoundaryEdges)&&
                        a.bounds.Equals(b.bounds)&&a.meshFootprintBounds.Equals(b.meshFootprintBounds)&&a.signature==b.signature,"Cached / cold capture differ.");
                }
                var originalVertices=source.vertices;var originalTriangles=source.triangles;
                string originalStamp=EditorJsonUtility.ToJson(stamp);int dirty=EditorUtility.GetDirtyCount(stamp);
                var first=Snapshot();var second=Snapshot();Equal(first,Snapshot(true));
                Check(ReferenceEquals(first.meshVertices,second.meshVertices)&&ReferenceEquals(first.meshBoundaryEdges,second.meshBoundaryEdges),"Unchanged capture does not reuse geometry.");
                Check(first!=second&&second.contactSegments==null&&second.cutRows==null,"Mutable build state leaked between captures.");
                Check(EditorJsonUtility.ToJson(stamp)==originalStamp&&EditorUtility.GetDirtyCount(stamp)==dirty,"Read-only cache population dirtied stamp.");
                Check(source.vertices.SequenceEqual(originalVertices)&&source.triangles.SequenceEqual(originalTriangles),"Capture modified source mesh.");
                stamp.blendDistance+=2;stamp.terrainCutOffset+=.4f;stamp.weldDistance+=.05f;stamp.mode=LTMeshConformMode.Cave;
                var settings=Snapshot();Equal(settings,Snapshot(true));
                Check(settings.signature!=first.signature&&ReferenceEquals(first.meshVertices,settings.meshVertices),"Settings were ignored or unnecessarily rebuilt geometry.");
                child.transform.localPosition=new Vector3(3,2,-1);child.transform.localRotation=Quaternion.Euler(13,21,7);
                child.transform.localScale=new Vector3(-1,2,.8f);var moved=Snapshot();Equal(moved,Snapshot(true));
                Check(!ReferenceEquals(settings.meshVertices,moved.meshVertices),"Transform did not invalidate geometry.");
                var oldPositions=(Vector3[])moved.meshVertices.Clone();
                var changed=source.vertices;changed[1].y+=.75f;source.vertices=changed;EditorUtility.SetDirty(source);
                var edited=Snapshot();Equal(edited,Snapshot(true));Check(!ReferenceEquals(edited.meshVertices,moved.meshVertices),"Mesh revision not detected.");
                Check(moved.meshVertices.SequenceEqual(oldPositions),"An in-flight snapshot was mutated.");
                // Force the audit deadline on our temporary entry only, without
                // touching global Undo history or importing any user assets.
                state.meshCapture.ConfirmAudit(id,EditorApplication.timeSinceStartup-2);
                var audited=Snapshot();Check(ReferenceEquals(edited.meshVertices,audited.meshVertices),"Unchanged audit rebuilt geometry.");
                changed=source.vertices;changed[1].x+=.125f;source.vertices=changed;
                state.meshCapture.ConfirmAudit(id,EditorApplication.timeSinceStartup-2);
                var auditedEdit=Snapshot();Equal(auditedEdit,Snapshot(true));Check(auditedEdit.signature!=audited.signature,"Content change not reflected in stamp signature.");
                source.SetTriangles(new[]{0,2,1},0);state.meshCapture.ConfirmAudit(id,EditorApplication.timeSinceStartup-2);
                var topology=Snapshot();Equal(topology,Snapshot(true));Check(topology.signature!=auditedEdit.signature,"Same-count topology edit not detected.");
                // A generated preview is deliberately NOT a replacement source.
                // v1 incorrectly expected this protected case to adopt the mesh.
                var generatedPreview=Object.Instantiate(source);generatedPreview.hideFlags=HideFlags.HideAndDontSave;ownedMeshes.Add(generatedPreview);
                filter.sharedMesh=generatedPreview;
                string beforePreview=EditorJsonUtility.ToJson(stamp);int previewDirty=EditorUtility.GetDirtyCount(stamp);
                var ignoredPreview=Snapshot();Equal(ignoredPreview,Snapshot(true));
                Check(stamp.sourceMesh==source&&ignoredPreview.meshSource==source&&ReferenceEquals(ignoredPreview.meshVertices,topology.meshVertices),"Generated preview was adopted as the source.");
                Check(EditorJsonUtility.ToJson(stamp)==beforePreview&&EditorUtility.GetDirtyCount(stamp)==previewDirty,"Ignoring a generated preview changed the stamp.");
                // Still owned and destroyed by this check, never saved as an asset.
                // Omit DontSaveInEditor to model a genuinely assigned source mesh.
                var replacement=Object.Instantiate(source);replacement.hideFlags=HideFlags.HideInHierarchy|HideFlags.DontSaveInBuild|HideFlags.DontUnloadUnusedAsset;
                ownedMeshes.Add(replacement);filter.sharedMesh=replacement;
                var replaced=Snapshot();Check(stamp.sourceMesh==replacement&&!ReferenceEquals(replaced.meshVertices,topology.meshVertices),"Replacement mesh not captured.");Equal(replaced,Snapshot(true));
                state.meshCapture.Clear();var reset=Snapshot();Check(!ReferenceEquals(reset.meshVertices,replaced.meshVertices),"Explicit reset did not invalidate.");Equal(reset,replaced);
                stamp.enabled=false;Capture(world);Check(state.meshCapture.Count==0,"Disabled stamp retained geometry.");stamp.enabled=true;
                Snapshot();Object.DestroyImmediate(child);Capture(world);Check(state.meshCapture.Count==0&&state.meshCapture.Bytes==0,"Deleted stamp retained geometry.");
                CaptureMeshGeometry(id,replacement,Matrix4x4.identity,state.meshCapture);replacement.UploadMeshData(true);
                bool rejected=false;
                try{CaptureMeshGeometry(id,replacement,Matrix4x4.identity,state.meshCapture);}catch(InvalidOperationException){rejected=true;}
                Check(rejected&&state.meshCapture.Count==0,"Unreadable mesh incorrectly reused its former readable snapshot.");
            }
            catch(Exception e){failure=e;}
            finally
            {
                if(worldId!=0)states.Remove(worldId);
                if(root)Object.DestroyImmediate(root);foreach(var mesh in ownedMeshes)if(mesh)Object.DestroyImmediate(mesh);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);
            else Debug.Log(tag+$"PASS {checks} checks; cached/cold capture parity, settings, transforms, mesh revisions, content audits, generated-preview rejection, real source replacement, immutable snapshots and deletion. Owned preview scene only; no scene/assets saved. Actual Undo/reimport UI and live-frame speed are not measured.");
        }
    }
}
