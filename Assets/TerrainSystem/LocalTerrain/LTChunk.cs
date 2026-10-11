using UnityEngine;
namespace LocalTerrainPrototype
{
    [System.Serializable]
    public sealed class LTLODPlan
    {
        public System.Collections.Generic.List<Vector3Int> leaves = new System.Collections.Generic.List<Vector3Int>();
    }
    [ExecuteAlways]
    public sealed class LTChunk : MonoBehaviour
    {
        // Coarse operation scopes for the five-second Frame Profiler. They do
        // not start a capture or change LOD policy; idle chunks emit none.
        static readonly Unity.Profiling.ProfilerMarker SpatialCloneMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.SpatialClone");
        static readonly Unity.Profiling.ProfilerMarker SpatialIndicesMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.SpatialIndices");
        static readonly Unity.Profiling.ProfilerMarker SpatialSeamMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.SpatialSeam");
        static readonly Unity.Profiling.ProfilerMarker SpatialUploadMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.SpatialUpload");
        static readonly Unity.Profiling.ProfilerMarker SpatialBindMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.SpatialBind");
        public int x, z;
        public Mesh mesh;
        [HideInInspector] public Mesh[] lodMeshes = new Mesh[0];
        [HideInInspector] public LTLODPlan[] lodPlans = new LTLODPlan[0];
        [System.NonSerialized] public int currentLOD;
        [HideInInspector] public LTSpatialLODAsset spatialLOD;
        // Persist the guard, not the temporary meshes: reload must never expose
        // old coarse geometry after an early LOD0 publication.
        [HideInInspector] public bool lodsPending;
        [System.NonSerialized] LTSpatialLODAsset previewSpatialLOD;
        public LTSpatialLODAsset ActiveSpatialLOD => previewSpatialLOD ? previewSpatialLOD : lodsPending ? null : spatialLOD;
        public void SetLODPreview(LTSpatialLODAsset preview)
        {
            ReleaseLODPreview();previewSpatialLOD=preview;lodsPending=true;
        }
        public void ReleaseLODPreview()
        {
            ReleaseSpatialLOD();
            if(!previewSpatialLOD)return;
            var bank=previewSpatialLOD.vertexBank;
            if(Application.isPlaying){if(bank)Destroy(bank);Destroy(previewSpatialLOD);}
            else{if(bank)DestroyImmediate(bank);DestroyImmediate(previewSpatialLOD);}
            previewSpatialLOD=null;
        }
        Mesh spatialMesh;
        LTSpatialLODAsset activeSpatial;
        int spatialRevision;
        readonly System.Collections.Generic.List<int> spatialIndices=new System.Collections.Generic.List<int>();
        System.Func<Mesh,LTRockSeamIndices.Plan> rockSeamMaps;
        LTRockSeamIndices.Plan spatialSeamMap,legacySeamMap;
        Mesh seamLegacy,seamLegacySource;
        public void SetRockSeamMaps(System.Func<Mesh,LTRockSeamIndices.Plan> maps)=>rockSeamMaps=maps;
        void ReleaseSeamLegacy()
        {
            if(cachedFilter&&cachedFilter.sharedMesh==seamLegacy)cachedFilter.sharedMesh=seamLegacySource;
            if(seamLegacy){if(Application.isPlaying)Destroy(seamLegacy);else DestroyImmediate(seamLegacy);}
            seamLegacy=null;seamLegacySource=null;legacySeamMap=null;
        }
        MeshFilter cachedFilter;
        MeshRenderer cachedRenderer;
        Behaviour rendererExclusion;
        // GPUDriven has a nested asmdef: this component is NOT in Volume's
        // Core.Runtime assembly. Missing optional policy must not poison LTChunk's
        // static initializer and abort HDRP's beginCameraRendering callback.
        static readonly System.Type RendererExclusionType=typeof(UnityEngine.Rendering.GPUResidentDrawer).Assembly.GetType(
            "UnityEngine.Rendering.DisallowGPUDrivenRendering",false);
        static bool rendererPolicyWarningLogged;
        bool rendererPolicyFailed;
        void OnEnable(){rendererPolicyFailed=false;EnsureRendererPolicy();GetComponentInParent<LTWorld>()?.NotifyChunkLayoutChanged();}
        void EnsureRendererPolicy()
        {
            if(rendererPolicyFailed)return;
            try{ApplyRendererPolicy();}
            catch(System.Exception e)
            {
                // Keep LOD/mesh publication and the camera render alive. Do not
                // retry a failed AddComponent every frame or flood the console.
                rendererPolicyFailed=true;
                if(!rendererPolicyWarningLogged)
                {
                    rendererPolicyWarningLogged=true;
                    Debug.LogError("Local Terrain: could not exclude chunks from GPU Resident Drawer. "+
                        "LOD rendering remains active, but the GPU-driven flicker workaround was not applied. "+e.Message,this);
                }
            }
        }
        void ApplyRendererPolicy()
        {
            if(RendererExclusionType==null)
                throw new System.InvalidOperationException("DisallowGPUDrivenRendering is absent from the GPUDriven runtime assembly.");
            // Spatial LOD rewrites this mesh's index buffer. Keep only this chunk
            // off GPU Resident Drawer; the rest of the scene remains eligible.
            bool rendererChanged=false;
            if(!cachedRenderer)
            {
                cachedRenderer=GetComponent<MeshRenderer>();rendererChanged=true;
            }
            if(!cachedRenderer)return;
            if(!rendererExclusion)
            {
                rendererExclusion=GetComponent(RendererExclusionType) as Behaviour;
                if(!rendererExclusion)
                {
                    rendererExclusion=(Behaviour)gameObject.AddComponent(RendererExclusionType);
                    if(!rendererExclusion)throw new System.InvalidOperationException("Unity could not add DisallowGPUDrivenRendering.");
                    return; // Its OnEnable has already applied the local exclusion.
                }
            }
            // Reapply if a renderer was replaced without replacing its GameObject.
            if(rendererChanged&&rendererExclusion.enabled)rendererExclusion.enabled=false;
            if(!rendererExclusion.enabled)rendererExclusion.enabled=true;
            // Retain the component with the chunk: scene reloads, builds and a
            // disabled LTChunk must not silently re-enable GPU-driven rendering.
        }
        public void ShowSpatialLOD(LTSpatialLODTopology topology,int chunk,bool dirty,LTLODCpuCapture capture=null)
        {
            ReleaseSeamLegacy();
            EnsureRendererPolicy();
            var spatialLOD=ActiveSpatialLOD;
            if(!spatialLOD||!spatialLOD.vertexBank||spatialLOD.formatVersion!=LTSpatialLODMath.Version){ShowLOD(0);return;}
            if(!cachedFilter)cachedFilter=GetComponent<MeshFilter>();
            if(!cachedFilter)return;
            bool changed=!spatialMesh||activeSpatial!=spatialLOD||spatialRevision!=spatialLOD.revision;
            var seamMap=rockSeamMaps?.Invoke(spatialLOD.vertexBank);
            dirty|=!ReferenceEquals(spatialSeamMap,seamMap);
            if(changed)
            {
                using var cloneProfile=SpatialCloneMarker.Auto();
                using var cloneCapture=capture?.Measure(LTLODCpuCapture.Stage.Clone)??default;
                ReleaseSpatialLOD();activeSpatial=spatialLOD;spatialRevision=spatialLOD.revision;
                spatialMesh=Instantiate(spatialLOD.vertexBank);spatialMesh.name=name+" Spatial LOD (runtime)";
                spatialMesh.hideFlags=HideFlags.HideAndDontSave;spatialMesh.MarkDynamic();
                int capacity=(int)spatialMesh.GetIndexCount(0);
                if(spatialIndices.Capacity<capacity)spatialIndices.Capacity=capacity;
                capture?.MeshCloned();
            }
            // Refill a preview clone after scene save, even with an unchanged camera.
            if(changed||dirty)
            {
                using(SpatialIndicesMarker.Auto())
                using(capture?.Measure(LTLODCpuCapture.Stage.Indices)??default)
                {
                    topology.WriteIndices(chunk,spatialIndices);
                    if(seamMap!=null)using(SpatialSeamMarker.Auto())seamMap.Apply(spatialIndices);
                }
                spatialSeamMap=seamMap;
                using(SpatialUploadMarker.Auto())
                using(capture?.Measure(LTLODCpuCapture.Stage.Upload)??default)spatialMesh.SetIndices(spatialIndices,MeshTopology.Triangles,0,false);
                capture?.Uploaded(spatialIndices.Count);
            }
            if(cachedFilter.sharedMesh!=spatialMesh)
            {
                using(SpatialBindMarker.Auto())cachedFilter.sharedMesh=spatialMesh;
            }
            currentLOD=0;
        }
        public void ReleaseSpatialLOD()
        {
            if(cachedFilter&&cachedFilter.sharedMesh==spatialMesh)cachedFilter.sharedMesh=mesh;
            if(spatialMesh){if(Application.isPlaying)Destroy(spatialMesh);else DestroyImmediate(spatialMesh);}
            spatialMesh=null;activeSpatial=null;spatialSeamMap=null;
        }
        void OnDisable(){ReleaseLODPreview();ReleaseSeamLegacy();GetComponentInParent<LTWorld>()?.NotifyChunkLayoutChanged();}
        void OnDestroy(){ReleaseLODPreview();ReleaseSeamLegacy();}
        public void ShowLOD(int level)
        {
            EnsureRendererPolicy();
            if(spatialMesh)ReleaseSpatialLOD();
            if(!cachedFilter)cachedFilter=GetComponent<MeshFilter>();
            if(!cachedFilter)return;
            level=lodsPending?0:Mathf.Clamp(level,0,lodMeshes==null?0:lodMeshes.Length);
            Mesh selected=level==0?mesh:lodMeshes[level-1];
            if(!selected){level=0;selected=mesh;}
            var map=rockSeamMaps?.Invoke(selected);
            if(seamLegacySource!=selected||!ReferenceEquals(map,legacySeamMap))
            {
                ReleaseSeamLegacy();
                if(map!=null)
                {
                    seamLegacySource=selected;legacySeamMap=map;seamLegacy=Instantiate(selected);seamLegacy.name=selected.name+" / Joint seam";seamLegacy.hideFlags=HideFlags.HideAndDontSave;
                    var indices=new System.Collections.Generic.List<int>(selected.triangles);map.Apply(indices);seamLegacy.SetTriangles(indices,0,false);
                }
            }
            if(seamLegacy)selected=seamLegacy;
            if(cachedFilter.sharedMesh!=selected)cachedFilter.sharedMesh=selected;
            currentLOD=level;
        }
        [HideInInspector] public System.Collections.Generic.List<Vector3Int> sourceLeaves = new System.Collections.Generic.List<Vector3Int>();
        [HideInInspector] public System.Collections.Generic.List<Vector3Int> renderLeaves = new System.Collections.Generic.List<Vector3Int>();
        [HideInInspector] public System.Collections.Generic.List<int> borderStitches = new System.Collections.Generic.List<int>();
        [System.NonSerialized] public double updatedAt;
        void OnDrawGizmos()
        {
#if UNITY_EDITOR
            var world = GetComponentInParent<LTWorld>();
            if (!world || !world.showDebug || !mesh) return;
            Gizmos.color = UnityEditor.EditorApplication.timeSinceStartup - updatedAt < 1.5
                ? Color.green : new Color(0.3f, 0.6f, 1, 0.3f);
            var old = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(mesh.bounds.center, mesh.bounds.size);
            Gizmos.matrix = old;
#endif
        }
    }
}
