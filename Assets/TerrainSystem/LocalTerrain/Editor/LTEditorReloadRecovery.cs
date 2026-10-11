using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static partial class LTEditorEngine
    {
        [Serializable] sealed class ReloadDensity
        {
            public int id;
            public string signature;
            public Rect bounds;
            public float cellSize;
        }
        [Serializable] sealed class ReloadWorld
        {
            public int world,detail,placement;
            public string placementName;
            public LTEditorReloadProof proof;
            public ReloadDensity[] densities;
        }
        [Serializable] sealed class ReloadSession
        {
            public string diagnosticId;
            public List<string> skipped=new List<string>();
            public List<ReloadWorld> worlds=new List<ReloadWorld>();
        }
        static ReloadSession reloadSession;
        static string reloadCodeKey;
        static bool reloadPlacementsRestored;
        static string ReloadSessionKey=>"LocalTerrain.Reload.v1."+Hash128.Compute(Application.dataPath);

        static void InitializeReloadRecovery()
        {
            // Capture at assembly initialization, NOT before unload: by then the
            // files on disk may already describe the next compiled implementation.
            try{reloadCodeKey=ReloadImplementationKey();}
            catch(Exception e){Debug.LogWarning("[Terrain Reload] Recovery disabled: "+e.Message);}
            string json=SessionState.GetString(ReloadSessionKey,"");
            SessionState.EraseString(ReloadSessionKey); // one handoff, never a disk/save cache
            try{reloadSession=string.IsNullOrEmpty(json)?new ReloadSession():JsonUtility.FromJson<ReloadSession>(json);}
            catch(Exception e){reloadSession=new ReloadSession();Debug.LogWarning("[Terrain Reload] Handoff parse failed: "+e.Message);}
            Debug.Log(string.IsNullOrEmpty(json)?"[Terrain Reload] Init: no handoff in SessionState (first load or no previous capture).":
                $"[Terrain Reload] Init: handoff={reloadSession?.diagnosticId??"legacy"}; checkpoints={reloadSession?.worlds?.Count??0}; skipped={reloadSession?.skipped?.Count??0}.");
            EditorApplication.delayCall+=RestoreReloadPlacements;
            EditorApplication.quitting+=DiscardReloadSession;
            EditorApplication.playModeStateChanged+=s=>{if(s==PlayModeStateChange.ExitingEditMode)DiscardReloadSession();};
        }

        static string ReloadImplementationKey()
        {
            // Deliberately conservative. Any production terrain/shader/helper or
            // locked package change invalidates. Check-only files don't invalidate.
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*",SearchOption.AllDirectories)
                .Where(p=>new[]{".cs",".shader",".hlsl",".compute",".shadergraph",".asmdef"}.Contains(Path.GetExtension(p)))
                .Where(p=>!Path.GetFileNameWithoutExtension(p).EndsWith("Check",StringComparison.Ordinal)
                    &&!Path.GetFileNameWithoutExtension(p).EndsWith("Tests",StringComparison.Ordinal))
                .Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json","ProjectSettings/ProjectVersion.txt"})
                .OrderBy(p=>p,StringComparer.Ordinal);
            using(var sha=SHA256.Create())
            {
                foreach(var path in files)
                {
                    byte[] name=Encoding.UTF8.GetBytes(path.Replace('\\','/'));sha.TransformBlock(name,0,name.Length,null,0);
                    byte[] bytes=File.ReadAllBytes(path);sha.TransformBlock(bytes,0,bytes.Length,null,0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(),0,0);return Convert.ToBase64String(sha.Hash);
            }
        }
        static string ReloadOwner(LTWorld w)=>w.GetInstanceID()+":"+w.gameObject.scene.handle+":"+w.gameObject.scene.path;
        static string ReloadSourceKey(LTSource source)
        {
            if(!source)return "missing";
            var h=new LTDetailPlacementFingerprint();h.Add(source.resolution);h.Add(source.size);
            h.Add(source.heights==null?-1:source.heights.Length);
            if(source.heights!=null)foreach(float height in source.heights)h.Add(height);
            return h.ToString();
        }
        static string ReloadInputKey(LTWorld w,List<Stamp> stamps)
        {
            var h=new LTDetailPlacementFingerprint();h.Add(Config(w,true));h.Add(stamps.Count);
            foreach(var s in stamps){h.Add(s.id);h.Add(s.signature);}
            return h.ToString();
        }
        static string ReloadOutputKey(LTWorld w)=>ReloadOutputKey(w,out _);
        static string ReloadOutputKey(LTWorld w,out string reason)=>ReloadOutputKey(w,Capture(w),out reason);
        static string ReloadOutputKey(LTWorld w,List<Stamp> current,out string reason)
        {
            reason=null;
            var h=new LTDetailPlacementFingerprint();var meshes=new HashSet<Mesh>();
            var chunks=w.generatedRoot.GetComponentsInChildren<LTChunk>();
            if(chunks.Length==0){reason="output: no active chunks";return null;}
            foreach(var c in chunks.OrderBy(c=>w.ChunkId(c.x,c.z)))
            {
                if(!c.mesh){reason=$"output: chunk ({c.x},{c.z}) missing mesh";return null;}
                if(c.lodsPending){reason=$"output: chunk ({c.x},{c.z}) lodsPending";return null;}
                h.Add(c.GetInstanceID());h.Add(c.transform.localToWorldMatrix);h.Add(JsonUtility.ToJson(c));
                meshes.Add(c.mesh);
                if(c.lodMeshes!=null)foreach(var m in c.lodMeshes)
                {if(!m){reason=$"output: chunk ({c.x},{c.z}) has a missing LOD mesh";return null;}meshes.Add(m);}
                if(c.spatialLOD)
                {
                    h.Add(JsonUtility.ToJson(c.spatialLOD));
                    if(!c.spatialLOD.vertexBank){reason=$"output: chunk ({c.x},{c.z}) missing spatial vertex bank";return null;}meshes.Add(c.spatialLOD.vertexBank);
                }
                var collider=c.GetComponent<MeshCollider>();h.Add(collider?collider.GetInstanceID():0);
                h.Add(collider&&collider.sharedMesh?collider.sharedMesh.GetInstanceID():0);
                if(collider&&collider.sharedMesh)meshes.Add(collider.sharedMesh);
            }
            foreach(var stamp in w.GetComponentsInChildren<LTMeshStamp>())
            {
                if(!stamp.isActiveAndEnabled||!stamp.affectHeight||(!stamp.trimRockInsideTerrain&&stamp.mode!=LTMeshConformMode.Cave))continue;
                h.Add(stamp.GetInstanceID());
                if(!stamp.renderAsset)
                {
                    // Union without a terrain intersection deliberately keeps its
                    // source mesh. Absence of an optional render asset is not loss
                    // of generated output, but prove this on both sides of reload.
                    if(!ReloadSourceRock(w,stamp,current,h,meshes,out string sourceReason))
                    {reason=$"output: mesh stamp '{stamp.name}' ({stamp.GetInstanceID()}) has no saved render asset; {sourceReason}";return null;}
                    continue;
                }
                // An existing invalid asset must NOT be reinterpreted as source-only.
                // Transient-only generated output is destroyed on disable.
                if(!stamp.renderAsset.Valid)
                {reason=$"output: mesh stamp '{stamp.name}' ({stamp.GetInstanceID()}) has an invalid saved render asset";return null;}
                h.Add("saved-rock");h.Add(JsonUtility.ToJson(stamp.renderAsset));
                meshes.Add(stamp.renderAsset.combined);
                foreach(var m in stamp.renderAsset.rocks)meshes.Add(m);
                foreach(var m in stamp.renderAsset.transitions)meshes.Add(m);
            }
            // Exact raw vertex/index payload, not just counts/bounds/InstanceID.
            // Once at handoff and once at restore, never in the normal Tick poll.
            using(var sha=SHA256.Create())
            {
                byte[] header=Encoding.UTF8.GetBytes(h.ToString());sha.TransformBlock(header,0,header.Length,null,0);
                foreach(var mesh in meshes.OrderBy(m=>m?m.GetInstanceID():0))
                {
                    if(!mesh){reason="output: missing mesh in generated mesh set";return null;}
                    var description=new LTDetailPlacementFingerprint();description.Add(mesh.GetInstanceID());
                    description.Add(mesh.vertexCount);description.Add((int)mesh.indexFormat);description.Add(mesh.subMeshCount);
                    description.Add(mesh.bounds.center);description.Add(mesh.bounds.size);
                    foreach(var attribute in mesh.GetVertexAttributes())
                    {description.Add((int)attribute.attribute);description.Add((int)attribute.format);description.Add(attribute.dimension);description.Add(attribute.stream);}
                    for(int sub=0;sub<mesh.subMeshCount;sub++)
                    {
                        var part=mesh.GetSubMesh(sub);description.Add(part.indexStart);description.Add(part.indexCount);
                        description.Add((int)part.topology);description.Add(part.baseVertex);description.Add(part.firstVertex);description.Add(part.vertexCount);
                        description.Add(part.bounds.center);description.Add(part.bounds.size);
                    }
                    byte[] info=Encoding.UTF8.GetBytes(description.ToString());sha.TransformBlock(info,0,info.Length,null,0);
                    using(var data=Mesh.AcquireReadOnlyMeshData(mesh))
                    {
                        var part=data[0];
                        for(int stream=0;stream<part.vertexBufferCount;stream++)
                        {var bytes=part.GetVertexData<byte>(stream).ToArray();sha.TransformBlock(bytes,0,bytes.Length,null,0);}
                        var indices=part.GetIndexData<byte>().ToArray();sha.TransformBlock(indices,0,indices.Length,null,0);
                    }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(),0,0);return Convert.ToBase64String(sha.Hash);
            }
        }
        static bool ReloadSourceRock(LTWorld w,LTMeshStamp stamp,List<Stamp> current,
            LTDetailPlacementFingerprint h,HashSet<Mesh> meshes,out string reason)
        {
            reason=null;
            var captured=current.FirstOrDefault(s=>s.meshStamp&&s.meshComponent==stamp);
            if(captured==null||captured.meshMode!=LTMeshConformMode.Union||!captured.meshCutTerrain||!captured.meshTrimRock)
            {reason="source-only recovery requires a captured Union cutter";return false;}
            var filter=stamp.GetComponent<MeshFilter>();
            if(!stamp.sourceMesh||captured.meshSource!=stamp.sourceMesh||!filter||filter.sharedMesh!=stamp.sourceMesh)
            {reason="source mesh is missing or not assigned to the renderer";return false;}
            if(stamp.renderOutput!=null||stamp.generatedTrimmedMesh||stamp.generatedMeshOwner!=0||
                stamp.terrainMaterialApplied||stamp.unionGroupOutputApplied||
                stamp.GetComponentsInChildren<LTRockTransitionRenderer>(true).Any(r=>r.transform.parent==stamp.transform))
            {reason="generated rock/transition output is still present";return false;}
            var collider=stamp.GetComponent<MeshCollider>();
            if(collider&&collider.sharedMesh&&collider.sharedMesh!=stamp.sourceMesh)
            {reason="collider still references a non-source mesh";return false;}
            // Capture() alone leaves contactSegments null. Recompute using the
            // exact build path and ALL terrain inputs, on this private snapshot
            // only. Never treat an unprepared null contour as proof of no contact.
            PrepareContactContours(w,current,captured);
            if(captured.contactSegments==null||captured.contactSegments.Count!=0)
            {reason="terrain contact requires saved generated output";return false;}
            h.Add("source-only-rock");h.Add(filter.GetInstanceID());h.Add(stamp.sourceMesh.GetInstanceID());
            h.Add(stamp.transform.localToWorldMatrix);meshes.Add(stamp.sourceMesh);
            h.Add(collider?collider.GetInstanceID():0);
            h.Add(collider&&collider.sharedMesh?collider.sharedMesh.GetInstanceID():0);
            return true;
        }
        static bool ReloadSettled(LTWorld w,State s)=>s.initialized&&!s.reloadDensityPending&&s.dirty.Count==0&&s.colliders.Count==0
            &&!s.build.Active&&s.chunkLODs.Count==0&&!w.PaintingPending&&!w.rockSeamBakePending;
        static string ReloadPendingReason(LTWorld w,State s)=>LTEditorReloadDiagnostics.Pending(s.initialized,s.reloadDensityPending,
            s.dirty.Count,s.colliders.Count,s.build.Active,s.chunkLODs.Count,w.PaintingPending,w.rockSeamBakePending);
        static string ReloadSequenceDifference((int id,string signature)[] previous,(int id,string signature)[] current)
        {
            if(previous.Length!=current.Length)return $"count {previous.Length} -> {current.Length}";
            for(int i=0;i<previous.Length;i++)
            {
                var a=previous[i];var b=current[i];
                if(a.id!=b.id)return $"order/identity at index {i}: {a.id} -> {b.id}";
                if(a.signature!=b.signature)
                {
                    var obj=EditorUtility.EntityIdToObject(b.id);
                    return $"signature at index {i}, id={b.id}, name='{(obj?obj.name:"deleted")}', hash={Hash128.Compute(a.signature??"")} -> {Hash128.Compute(b.signature??"")}";
                }
            }
            return "unknown difference";
        }
        static ReloadWorld CaptureReloadWorld(LTWorld w,State s,string code)=>CaptureReloadWorld(w,s,code,out _);
        static ReloadWorld CaptureReloadWorld(LTWorld w,State s,string code,out string reason)
        {
            reason=null;
            if(string.IsNullOrEmpty(code)){reason="implementation-key-unavailable";return null;}
            if(!ReloadSettled(w,s)){reason=ReloadPendingReason(w,s);return null;}
            if(s.config!=Config(w)){reason="configuration/source revision changed since last detection";return null;}
            var current=Capture(w);
            if(!s.previous.Select(p=>(p.id,p.signature)).SequenceEqual(current.Select(p=>(p.id,p.signature))))
            {reason="stamp-input-changed: "+ReloadSequenceDifference(s.previous.Select(p=>(p.id,p.signature)).ToArray(),current.Select(p=>(p.id,p.signature)).ToArray());return null;}
            // An edit may have happened since the last 100 ms poll.
            var densities=CaptureDensity(w,s);
            if(!s.densities.Select(d=>(d.id,d.signature)).SequenceEqual(densities.Select(d=>(d.id,d.signature))))
            {reason="density-input-changed: "+ReloadSequenceDifference(s.densities.Select(d=>(d.id,d.signature)).ToArray(),densities.Select(d=>(d.id,d.signature)).ToArray());return null;}
            string outputs=ReloadOutputKey(w,current,out reason);if(outputs==null)return null;
            return new ReloadWorld{world=w.GetInstanceID(),proof=new LTEditorReloadProof{code=code,owner=ReloadOwner(w),
                inputs=ReloadInputKey(w,current),outputs=outputs},densities=densities.Select(d=>new ReloadDensity
                    {id=d.id,signature=d.signature,bounds=d.bounds,cellSize=d.cellSize}).ToArray()};
        }
        static void BeforeTerrainAssemblyReload()
        {
            DiscardReloadSession();var session=new ReloadSession{diagnosticId=Guid.NewGuid().ToString("N").Substring(0,8)};
            var timer=System.Diagnostics.Stopwatch.StartNew();
            void Skip(string reason,LTWorld world=null)
            {
                string item=(world?$"'{world.name}' ({world.GetInstanceID()}): ":"global: ")+reason;
                session.skipped.Add(item);Debug.Log($"[Terrain Reload] Skip [{session.diagnosticId}] {item}",world);
            }
            try
            {
                Debug.Log($"[Terrain Reload] Before [{session.diagnosticId}]: playOrTransition={EditorApplication.isPlayingOrWillChangePlaymode}; hotControl={GUIUtility.hotControl}; states={states.Count}.");
                if(!EditorApplication.isPlayingOrWillChangePlaymode&&GUIUtility.hotControl==0)
                {
                    var worlds=Worlds();
                    if(worlds.Length==0)Skip("no eligible loaded world candidates from Worlds()");
                    foreach(var w in worlds)
                    {
                        if(!OwnsPainting(w)){Skip(ReloadWorldEligibility(w),w);continue;}
                        if(EditorSceneManager.IsPreviewScene(w.gameObject.scene)){Skip("preview-scene",w);continue;}
                        if(LTStampSizeHandles.IsEditing(w)){Skip("stamp-size-handle-editing",w);continue;}
                        if(!states.TryGetValue(w.GetInstanceID(),out var s)){Skip("world-state-missing",w);continue;}
                        try
                        {
                            var checkpoint=CaptureReloadWorld(w,s,reloadCodeKey,out string reason);
                            if(checkpoint==null){Skip(reason??"capture-returned-null",w);continue;}
                            session.worlds.Add(checkpoint);
                            var detail=w.GetComponent<LTDetailRenderer>();
                            if(detail&&detail.CanSavePlacement)
                            {
                                var asset=detail.CaptureReloadPlacement();
                                if(asset){checkpoint.detail=detail.GetInstanceID();checkpoint.placement=asset.GetInstanceID();checkpoint.placementName=asset.name;}
                                else Debug.Log($"[Terrain Reload] Detail cache '{w.name}': no ready cells or snapshot size limit.",w);
                            }
                            else Debug.Log($"[Terrain Reload] Detail cache '{w.name}': renderer={(detail?"present":"absent")}, enabled={(detail&&detail.isActiveAndEnabled)}, generating={(detail&&detail.IsGenerating)}, canSave={(detail&&detail.CanSavePlacement)}.",w);
                        }
                        catch(Exception e)
                        {
                            Skip("capture-exception: "+e.GetType().Name+": "+e.Message,w);
                            Debug.LogWarning("[Terrain Reload] Cold recovery for "+w.name+": "+e.Message,w);
                        }
                    }
                }
                else Skip(EditorApplication.isPlayingOrWillChangePlaymode?"play-mode-or-transition":"active-GUI-hotControl="+GUIUtility.hotControl);
                SessionState.SetString(ReloadSessionKey,JsonUtility.ToJson(session));
                Debug.Log($"[Terrain Reload] Captured {session.worlds.Count} settled world(s), {session.worlds.Count(i=>i.placement!=0)} detail snapshot(s), skipped={session.skipped.Count}, handoff={session.diagnosticId}, {timer.Elapsed.TotalMilliseconds:F1} ms. Session memory only; no scene/assets saved.");
            }
            catch(Exception e)
            {
                foreach(var item in session.worlds)DestroyReloadPlacement(item);
                SessionState.EraseString(ReloadSessionKey);Debug.LogWarning("[Terrain Reload] Checkpoint failed: "+e.Message);
            }
            finally{StopLODJobs();}
        }
        static string ReloadWorldEligibility(LTWorld w)
        {
            // Log all cheap facts for the existing OwnsPainting guard. Do not
            // capture stamps, initialize State or mutate serialized scene data.
            return $"world-not-eligible: activeEnabled={w.isActiveAndEnabled}, autoUpdate={w.autoUpdate}, source={(bool)w.source}, root={(bool)w.generatedRoot}, transformValid={ValidTransform(w)}, sceneValid={w.gameObject.scene.IsValid()}, sceneLoaded={w.gameObject.scene.isLoaded}, persistent={EditorUtility.IsPersistent(w)}, prefabAsset={PrefabUtility.IsPartOfPrefabAsset(w)}, prefabStage={PrefabStageUtility.GetPrefabStage(w.gameObject)!=null}";
        }
        [MenuItem("Tools/Local Terrain/Report Editor Reload Readiness (no scene changes)")]
        public static void ReportEditorReloadReadiness()
        {
            // Passive readiness only, NOT a checkpoint or proof of fresh inputs.
            // No GetState, Capture, Detect, rebuild, save or SessionState writes.
            var worlds=Worlds();
            Debug.Log($"[Terrain Reload] Readiness: worlds={worlds.Length}; states={states.Count}; playOrTransition={EditorApplication.isPlayingOrWillChangePlaymode}; hotControl={GUIUtility.hotControl}; codeKeyReady={!string.IsNullOrEmpty(reloadCodeKey)}.");
            foreach(var w in worlds)
            {
                string reason=!OwnsPainting(w)?ReloadWorldEligibility(w):
                    EditorSceneManager.IsPreviewScene(w.gameObject.scene)?"preview-scene":
                    LTStampSizeHandles.IsEditing(w)?"stamp-size-handle-editing":
                    !states.TryGetValue(w.GetInstanceID(),out var s)?"world-state-missing":
                    ReloadPendingReason(w,s)??"settled; fresh input/output checks run only during capture";
                Debug.Log($"[Terrain Reload] Readiness '{w.name}' ({w.GetInstanceID()}): {reason}",w);
            }
        }
        static void DestroyReloadPlacement(ReloadWorld item)
        {
            var asset=EditorUtility.EntityIdToObject(item.placement) as LTDetailPlacementAsset;
            if(asset&&asset.name==item.placementName&&!EditorUtility.IsPersistent(asset)&&(asset.hideFlags&HideFlags.DontSave)==HideFlags.DontSave)Object.DestroyImmediate(asset);
            item.placement=0;
        }
        static void RestoreReloadPlacements()
        {
            if(reloadPlacementsRestored||reloadSession==null)return;reloadPlacementsRestored=true;
            var live=Worlds().ToDictionary(w=>w.GetInstanceID());
            foreach(var item in reloadSession.worlds.ToArray())
            {
                if(EditorApplication.isPlayingOrWillChangePlaymode||!live.TryGetValue(item.world,out var world))
                {
                    Debug.Log($"[Terrain Reload] Discard handoff world={item.world}: play/transition or world no longer loaded.");
                    DestroyReloadPlacement(item);reloadSession.worlds.Remove(item);continue;
                }
                if(item.proof==null||item.proof.code!=reloadCodeKey||item.proof.owner!=ReloadOwner(world))
                {DestroyReloadPlacement(item);continue;} // geometry path logs the cold-recovery reason
                var renderer=EditorUtility.EntityIdToObject(item.detail) as LTDetailRenderer;
                var asset=EditorUtility.EntityIdToObject(item.placement) as LTDetailPlacementAsset;
                if(renderer&&renderer.isActiveAndEnabled&&renderer.GetComponent<LTWorld>()==world&&asset&&asset.name==item.placementName&&!EditorUtility.IsPersistent(asset))
                {
                    try{renderer.AcceptReloadPlacement(asset);item.placement=0;}
                    catch(Exception e){DestroyReloadPlacement(item);Debug.LogWarning("[Terrain Reload] Detail cache skipped: "+e.Message,renderer);}
                }
                else DestroyReloadPlacement(item);
            }
        }
        static void DiscardReloadSession()
        {
            if(reloadSession!=null)foreach(var item in reloadSession.worlds)DestroyReloadPlacement(item);
            reloadSession=new ReloadSession();SessionState.EraseString(ReloadSessionKey);
        }
        static bool ApplyReloadWorld(LTWorld w,State state,List<Stamp> current,ReloadWorld item,string code,out string reason)
        {
            if(item.proof==null||item.densities==null){reason="нет контрольных данных";return false;}
            // Reject cheap mismatches before reading generated vertex buffers.
            if(!item.proof.Matches(code,ReloadOwner(w),ReloadInputKey(w,current),item.proof.outputs,out reason))return false;
            string outputs=ReloadOutputKey(w,current,out string outputReason);
            if(outputs==null){reason=outputReason;return false;}
            if(!item.proof.Matches(code,ReloadOwner(w),ReloadInputKey(w,current),outputs,out reason))return false;
            DetectChunkLayout(w,state);
            state.config=Config(w);state.previous=current;state.initialized=true;
            state.densities=item.densities.Select(d=>new Density{id=d.id,signature=d.signature,bounds=d.bounds,cellSize=d.cellSize}).ToList();
            state.reloadDensityPending=true;
            // No synthetic BeginCycle: geometry and rock-seam assets are intact.
            state.lastPaint=double.NegativeInfinity;
            return true;
        }
        static void TryResumeAfterReload(LTWorld w,State state,List<Stamp> current)
        {
            if(state.reloadRestoreAttempted)return;state.reloadRestoreAttempted=true;
            RestoreReloadPlacements(); // update may precede delayCall after reload
            var item=reloadSession?.worlds.FirstOrDefault(i=>i.world==w.GetInstanceID());
            if(item==null)
            {
                Debug.Log($"[Terrain Reload] No checkpoint for '{w.name}' ({w.GetInstanceID()}), handoff={reloadSession?.diagnosticId??"none"}; previous skip reasons={reloadSession?.skipped?.Count??0}. Normal update path.",w);
                return;
            }
            reloadSession.worlds.Remove(item);DestroyReloadPlacement(item);
            try
            {
                if(ApplyReloadWorld(w,state,current,item,reloadCodeKey,out string reason))
                    Debug.Log("[Terrain Reload] "+w.name+": unchanged geometry restored without rebuild; restoring paint and detail buffers.",w);
                else Debug.Log("[Terrain Reload] "+w.name+": обычное обновление — "+reason+".",w);
            }
            catch(Exception e)
            {
                // Optional recovery must never disable the normal terrain engine.
                state.initialized=false;state.reloadDensityPending=false;state.densities.Clear();
                Debug.LogWarning("[Terrain Reload] Cold recovery for "+w.name+": "+e.Message,w);
            }
        }
    }
}
