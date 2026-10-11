using System;
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
    public static partial class LTEditorEngine
    {
        [MenuItem("Tools/Local Terrain/Validate Editor Reload Recovery (no scene changes)")]
        public static void ValidateEditorReloadRecovery()
        {
            const string tag="[Terrain Reload Recovery v3] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            string Hash(string path){using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));}
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();GameObject root=null;LTSource source=null;Mesh mesh=null;
            LTDetailPlacementAsset placement=null;int worldId=0,checks=0;Exception failure=null;
            void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            try
            {
                root=new GameObject("Owned reload recovery check"){hideFlags=HideFlags.HideAndDontSave};root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root,preview);
                var w=root.AddComponent<LTWorld>();w.enabled=false;w.autoUpdate=false;worldId=w.GetInstanceID();
                source=ScriptableObject.CreateInstance<LTSource>();source.hideFlags=HideFlags.HideAndDontSave;
                source.size=new Vector3(16,10,16);source.resolution=2;source.heights=new float[4];
                w.source=source;w.chunksX=w.chunksZ=1;w.enableLayerPainting=false;w.enableLayerDisplacement=false;w.enableLODs=false;
                var generated=new GameObject("Owned generated"){hideFlags=HideFlags.HideAndDontSave};generated.transform.SetParent(root.transform,false);w.generatedRoot=generated.transform;
                var go=new GameObject("Owned chunk"){hideFlags=HideFlags.HideAndDontSave};go.transform.SetParent(generated.transform,false);
                var chunk=go.AddComponent<LTChunk>();chunk.enabled=false;
                var filter=go.AddComponent<MeshFilter>();go.AddComponent<MeshRenderer>();
                mesh=new Mesh{name="Owned output",hideFlags=HideFlags.HideAndDontSave};
                mesh.vertices=new[]{Vector3.zero,new Vector3(16,0,0),new Vector3(0,0,16)};
                mesh.triangles=new[]{0,2,1};mesh.RecalculateNormals();mesh.RecalculateBounds();chunk.mesh=mesh;filter.sharedMesh=mesh;
                var collider=go.AddComponent<MeshCollider>();collider.sharedMesh=mesh;
                var stampGo=new GameObject("Owned height stamp"){hideFlags=HideFlags.HideAndDontSave};stampGo.transform.SetParent(root.transform,false);
                var stamp=stampGo.AddComponent<LTHeightStamp>();stamp.densityZone=true;
                var detail=root.AddComponent<LTDetailRenderer>();detail.enabled=false;
                root.SetActive(true);
                var state=GetState(w);Detect(w,state,Capture(w));state.dirty.Clear();state.colliders.Clear();state.cycleActive=false;w.rockSeamBakePending=false;
                var snapshot=CaptureReloadWorld(w,state,"test-code",out string captureReason);Check(snapshot!=null&&captureReason==null,"Settled checkpoint missing or reported a skip.");
                Check(CaptureReloadWorld(w,state,"",out captureReason)==null&&captureReason=="implementation-key-unavailable","Missing code key has no precise reason.");
                snapshot=JsonUtility.FromJson<ReloadWorld>(JsonUtility.ToJson(snapshot));
                bool Resume(out State restored,out string reason)
                {restored=new State();return ApplyReloadWorld(w,restored,Capture(w),snapshot,"test-code",out reason);}
                Check(Resume(out var restored,out _),"Unchanged snapshot rejected.");
                Detect(w,restored,Capture(w));Check(restored.dirty.Count==0&&restored.initialized,"Unchanged Detect scheduled geometry work.");
                Check(restored.reloadDensityPending,"Paint density baseline was not deferred.");
                restored.reloadDensityPending=false;DetectDensity(w,restored);Check(restored.dirty.Count==0,"Unchanged density scheduled a rebuild.");
                Check(restored.densities.All(d=>d.zones!=null),"Restored density has no usable zones after paint restoration.");
                source.heights=(float[])source.heights.Clone();Check(Resume(out _,out _),"Managed source array identity invalidated reload.");
                source.heights[0]=.5f;Check(!Resume(out _,out _),"Changed height source accepted.");source.heights[0]=0;
                stamp.strength+=.125f;Check(!Resume(out _,out _),"Changed stamp accepted.");stamp.strength-=.125f;
                stampGo.transform.position=Vector3.one;Check(!Resume(out _,out _),"Moved stamp accepted.");stampGo.transform.position=Vector3.zero;
                stamp.enabled=false;Check(!Resume(out _,out _),"Removed/disabled stamp accepted.");stamp.enabled=true;
                chunk.updatedAt=900;Check(Resume(out _,out _),"Nonserialized update timestamp invalidated output.");
                var vertices=mesh.vertices;vertices[0].y=.125f;mesh.vertices=vertices;
                Check(!Resume(out _,out _),"Same-count generated vertex edit accepted.");vertices[0].y=0;mesh.vertices=vertices;
                mesh.triangles=new[]{0,1,2};Check(!Resume(out _,out _),"Same-count generated index edit accepted.");mesh.triangles=new[]{0,2,1};
                var originalSubmesh=mesh.GetSubMesh(0);var changedSubmesh=originalSubmesh;changedSubmesh.bounds=new Bounds(Vector3.one,Vector3.one);
                mesh.SetSubMesh(0,changedSubmesh,UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
                Check(!Resume(out _,out _),"Changed submesh metadata accepted.");mesh.SetSubMesh(0,originalSubmesh,UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
                chunk.lodsPending=true;Check(!Resume(out _,out _),"Pending LOD output accepted.");chunk.lodsPending=false;
                chunk.mesh=null;Check(!Resume(out _,out _),"Missing chunk mesh accepted.");
                Check(ReloadOutputKey(w,out captureReason)==null&&captureReason.Contains("missing mesh"),"Missing mesh has no precise reason.");chunk.mesh=mesh;
                Check(!ApplyReloadWorld(w,new State(),Capture(w),snapshot,"new-code",out _),"Changed implementation accepted.");
                Check(Resume(out _,out _),"Restored original inputs/output still rejected.");
                state.config=Config(w);state.previous=Capture(w);state.densities=CaptureDensity(w,state);
                state.dirty.Add(0);Check(CaptureReloadWorld(w,state,"test-code",out captureReason)==null&&captureReason.Contains("dirty-chunks=1"),"Dirty geometry rejection/reason incorrect.");state.dirty.Clear();
                state.colliders.Add(0);Check(CaptureReloadWorld(w,state,"test-code",out captureReason)==null&&captureReason.Contains("pending-colliders=1"),"Pending collider rejection/reason incorrect.");state.colliders.Clear();
                state.reloadDensityPending=true;Check(CaptureReloadWorld(w,state,"test-code",out captureReason)==null&&captureReason.Contains("density-restore-pending"),"Unrestored density rejection/reason incorrect.");state.reloadDensityPending=false;
                w.rockSeamBakePending=true;Check(CaptureReloadWorld(w,state,"test-code",out captureReason)==null&&captureReason.Contains("rock-seam-bake-pending"),"Rock seam rejection/reason incorrect.");w.rockSeamBakePending=false;

                ValidateReloadSourceRocks(w,Check);

                // Exercise the production placement matching path, including
                // invalidation/payload checking. This is not a real domain reload.
                var cell=new LTDetailPlacementAsset.Cell{coordinate=Vector2Int.zero,inputKey="key",sourceCount=1,
                    references=new Object[]{source},groups=new[]{new LTDetailPlacementAsset.Group{source=0,items=new[]{
                        new LTDetailPlacementAsset.Item{position=Vector3.one,maskPosition=Vector2.one,rotation=Quaternion.identity,scale=1,id=17}}}}};
                cell.payloadKey=LTDetailPlacementAsset.PayloadKey(cell);
                placement=ScriptableObject.CreateInstance<LTDetailPlacementAsset>();placement.hideFlags=HideFlags.HideAndDontSave;placement.Initialize(new[]{cell});
                var savedBefore=detail.savedPlacement;string detailBefore=EditorJsonUtility.ToJson(detail);
                detail.AcceptReloadPlacement(placement);
                Check(detail.ReloadPlacementCellCount==1&&detail.savedPlacement==savedBefore&&EditorJsonUtility.ToJson(detail)==detailBefore,"Transient cache serialized or changed saved asset reference.");
                var match=typeof(LTDetailRenderer).GetMethod("MatchingPlacement",BindingFlags.Instance|BindingFlags.NonPublic);
                object Match(string key,Object[] refs)=>match.Invoke(detail,new object[]{Vector2Int.zero,new Rect(0,0,16,16),key,refs,1});
                Check(ReferenceEquals(Match("key",cell.references),cell),"Unchanged transient placement not reused.");
                Check(Match("changed",cell.references)==null,"Changed cell settings accepted.");
                Check(Match("key",Array.Empty<Object>())==null,"Changed layer/prefab references accepted.");
                cell.groups[0].items[0].scale=2;Check(Match("key",cell.references)==null,"Corrupt payload accepted.");
                Check(files.All(f=>Hash(f.Key)==f.Value),"Validation wrote project files.");
            }
            catch(Exception e){failure=e;}
            finally
            {
                if(worldId!=0)states.Remove(worldId);
                if(root)Object.DestroyImmediate(root);if(placement)Object.DestroyImmediate(placement);if(mesh)Object.DestroyImmediate(mesh);if(source)Object.DestroyImmediate(source);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);
            else Debug.Log(tag+$"PASS {checks} checks; checkpoint roundtrip, source-only floating rocks, required contact output, exact source payload, unchanged geometry/density, source/mesh/code invalidation, pending-work rejection with reasons and transient detail matching. Owned preview scene only; no scene/assets saved. Real assembly reload and live recovery time remain separate checks.");
        }
        static void ValidateReloadSourceRocks(LTWorld w,Action<bool,string> check)
        {
            GameObject go=null;Mesh source=null,temporary=null;LTRockRenderAsset asset=null;
            try
            {
                go=new GameObject("Owned floating reload rock"){hideFlags=HideFlags.HideAndDontSave};go.SetActive(false);
                go.transform.SetParent(w.transform,false);go.transform.localPosition=new Vector3(8,5,8);
                source=new Mesh{name="Owned reload rock source",hideFlags=HideFlags.HideAndDontSave};
                source.vertices=new[]{new Vector3(-1,-1,-1),new Vector3(1,-1,-1),new Vector3(1,-1,1),new Vector3(-1,-1,1),
                    new Vector3(-1,1,-1),new Vector3(1,1,-1),new Vector3(1,1,1),new Vector3(-1,1,1)};
                source.triangles=new[]{0,1,2,0,2,3,4,6,5,4,7,6,0,4,5,0,5,1,1,5,6,1,6,2,2,6,7,2,7,3,3,7,4,3,4,0};
                source.RecalculateNormals();source.RecalculateBounds();
                temporary=Object.Instantiate(source);temporary.name="Owned transient generated output";
                var rock=go.AddComponent<LTMeshStamp>();rock.sourceMesh=source;
                var filter=go.GetComponent<MeshFilter>();filter.sharedMesh=source;go.AddComponent<MeshRenderer>();
                var collider=go.AddComponent<MeshCollider>();collider.sharedMesh=source;go.SetActive(true);
                string reason=null;
                // Read-only snapshots also ensure that negative fixture cases do
                // not silently adopt replacement meshes while being inspected.
                string Key()=>ReloadOutputKey(w,Capture(w,true),out reason);
                var current=Capture(w,true);var captured=current.Single(s=>s.meshComponent==rock);
                check(captured.contactSegments==null,"Fixture must start with an unprepared contour.");
                string before=EditorJsonUtility.ToJson(rock),filterBefore=EditorJsonUtility.ToJson(filter),colliderBefore=EditorJsonUtility.ToJson(collider);
                int dirty=EditorUtility.GetDirtyCount(rock);
                string original=ReloadOutputKey(w,current,out reason);
                check(original!=null&&reason==null&&captured.contactSegments!=null&&captured.contactSegments.Count==0,"Floating source-only rock rejected or no-contact not proved.");
                check(before==EditorJsonUtility.ToJson(rock)&&filterBefore==EditorJsonUtility.ToJson(filter)&&colliderBefore==EditorJsonUtility.ToJson(collider)
                    &&dirty==EditorUtility.GetDirtyCount(rock)&&!rock.renderAsset,"Source-only proof modified serialized objects.");
                check(Key()==original,"Repeated source-only output proof is unstable.");

                var state=new State{initialized=true,config=Config(w),previous=Capture(w)};
                state.densities=CaptureDensity(w,state);
                var checkpoint=CaptureReloadWorld(w,state,"source-rock-code",out reason);
                check(checkpoint!=null&&reason==null,"Floating rock prevented whole-world checkpoint.");
                checkpoint=JsonUtility.FromJson<ReloadWorld>(JsonUtility.ToJson(checkpoint));
                bool Resume()=>ApplyReloadWorld(w,new State(),Capture(w),checkpoint,"source-rock-code",out reason);
                check(Resume(),"Floating rock checkpoint roundtrip failed.");
                rock.enabled=false;rock.enabled=true;
                check(filter.sharedMesh==source&&collider.sharedMesh==source&&Resume(),"Source-only disable/enable lost output or invalidated checkpoint.");

                go.transform.localPosition=new Vector3(8,0,8);
                check(Key()==null&&reason.Contains("terrain contact requires"),"Intersecting rock without generated output accepted.");
                check(!Resume(),"Moving floating rock into terrain reused stale checkpoint.");
                go.transform.localPosition=new Vector3(8,5,8);
                check(Key()==original,"Returning to source-only output did not restore its key.");
                rock.mode=LTMeshConformMode.Cave;
                check(Key()==null&&reason.Contains("Union cutter"),"Cave without saved output accepted as source-only.");rock.mode=LTMeshConformMode.Union;
                rock.cutTerrain=false;
                check(Key()==null&&reason.Contains("Union cutter"),"Non-cutting trimmed output accepted as source-only.");rock.cutTerrain=true;

                rock.sourceMesh=null;
                check(Key()==null&&reason.Contains("source mesh is missing"),"Missing source accepted.");rock.sourceMesh=source;
                filter.sharedMesh=temporary;
                check(Key()==null&&reason.Contains("not assigned"),"Transient displayed mesh accepted.");filter.sharedMesh=source;
                rock.generatedTrimmedMesh=temporary;rock.generatedMeshOwner=rock.GetInstanceID();
                check(Key()==null&&reason.Contains("still present"),"Unpersisted generated output accepted.");rock.generatedTrimmedMesh=null;rock.generatedMeshOwner=0;
                rock.terrainMaterialApplied=true;
                check(Key()==null&&reason.Contains("still present"),"Generated material state accepted.");rock.terrainMaterialApplied=false;
                rock.unionGroupOutputApplied=true;
                check(Key()==null&&reason.Contains("still present"),"Union group output accepted.");rock.unionGroupOutputApplied=false;
                var bridge=new GameObject("Owned stale transition"){hideFlags=HideFlags.HideAndDontSave};bridge.transform.SetParent(go.transform,false);
                bridge.AddComponent<LTRockTransitionRenderer>();
                check(Key()==null&&reason.Contains("still present"),"Stale transition accepted.");Object.DestroyImmediate(bridge);
                collider.sharedMesh=temporary;
                check(Key()==null&&reason.Contains("collider still references"),"Non-source collider accepted.");collider.sharedMesh=source;
                collider.sharedMesh=null;
                check(Key()!=null&&Key()!=original,"Source-only collider assignment not fingerprinted.");collider.sharedMesh=source;

                var vertices=source.vertices;vertices[0].x+=.125f;source.vertices=vertices;
                check(Key()!=null&&Key()!=original,"Same-count source vertex edit was not fingerprinted.");vertices[0].x-=.125f;source.vertices=vertices;
                var triangles=source.triangles;var changed=(int[])triangles.Clone();(changed[0],changed[1])=(changed[1],changed[0]);source.triangles=changed;
                check(Key()!=null&&Key()!=original,"Same-count source index edit was not fingerprinted.");source.triangles=triangles;
                check(Key()==original,"Restoring source payload did not restore output key.");

                asset=ScriptableObject.CreateInstance<LTRockRenderAsset>();asset.hideFlags=HideFlags.HideAndDontSave;rock.renderAsset=asset;
                check(Key()==null&&reason.Contains("invalid saved render asset"),"Invalid existing render asset bypassed by source-only branch.");
                asset.combined=temporary;asset.rocks=new[]{temporary};asset.transitions=new[]{temporary};
                check(Key()!=null&&Key()!=original,"Previously supported valid render output rejected or confused with source-only output.");
                go.transform.localPosition=new Vector3(8,0,8);
                check(Key()!=null,"Intersecting rock with valid render output rejected.");
                asset.transitions[0]=null;
                check(Key()==null&&reason.Contains("invalid saved render asset"),"Broken generated transition accepted.");
            }
            finally
            {
                if(go)Object.DestroyImmediate(go);if(asset)Object.DestroyImmediate(asset);
                if(temporary)Object.DestroyImmediate(temporary);if(source)Object.DestroyImmediate(source);
            }
        }
    }
}
