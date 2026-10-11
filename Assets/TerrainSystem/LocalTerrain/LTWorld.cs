using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // Source data and generated meshes are separate assets. Editor logic never touches the source Terrain.
    [System.Serializable]
    public sealed class LTLODSettings
    {
        [Range(0,12)] public int simplificationSteps = 1;
        [Min(0)] public float maxHeightError = .15f;
        [Min(0)] public float startDistance = 80;
        public LTLODSettings(int steps,float error,float distance)
        {simplificationSteps=steps;maxHeightError=error;startDistance=distance;}
    }
    [ExecuteAlways]
    public sealed partial class LTWorld : MonoBehaviour
    {
        static readonly Unity.Profiling.ProfilerMarker LodUpdateMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.Update");
        static readonly Unity.Profiling.ProfilerMarker LodCacheMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.Cache");
        static readonly Unity.Profiling.ProfilerMarker LodTerrainMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.TerrainApply");
        static readonly Unity.Profiling.ProfilerMarker LodRoadMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.Roads");
        static readonly Unity.Profiling.ProfilerMarker LodSetupMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.TopologySetup");
        static readonly Unity.Profiling.ProfilerMarker LodSelectionMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.Selection");
        static readonly Unity.Profiling.ProfilerMarker LodBalanceMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.Balance");
        static readonly Unity.Profiling.ProfilerMarker LodRockSeamMarker = new Unity.Profiling.ProfilerMarker("LT.LOD.RockSeams");
        [HideInInspector] public LTRockSeamAsset rockSeams;
        [System.NonSerialized] public bool rockSeamBakePending;
        readonly LTRockSeamRuntime rockSeamRuntime=new LTRockSeamRuntime();
        System.Func<Mesh,LTRockSeamIndices.Plan> rockSeamLookup;
        bool rockSeamFailed;
        public void InvalidateRockSeams(){rockSeamRuntime.Dispose();rockSeamFailed=false;}
        public int ResolveRockSeamChunkLOD(LTChunk chunk)
        {
            if(!enableLODs||(!Application.isPlaying&&!previewLODs&&forceLOD<0)||IsRegularMaskGridChunk(chunk.x,chunk.z))return 0;
            var camera=ResolveLODCamera();return camera?SelectLOD(Mathf.Sqrt(chunk.mesh.bounds.SqrDistance(chunk.transform.InverseTransformPoint(camera.transform.position))),chunk.currentLOD,lods,lodHysteresis):0;
        }
        public LTSource source;
        public Material material;
        public LTSurfaceLayer baseLayer;
        public bool lightweightBackground=true;
        [Tooltip("Use three texture projections on terrain and rocks using terrain layers. More near texture samples; rebake global maps after switching projection. Rocks remain detailed.")]
        public bool triplanarTexturing;
        public bool enableLayerDisplacement=true;
        [Tooltip("Compute-симуляция глубины. На неподдерживаемом GPU используется CPU. Смена режима сбрасывает следы.")]
        public bool gpuMudSimulation=true;
        [Min(1), Tooltip("Дистанция от камеры, после которой восстановление обновляется реже.")]
        public float mudNearDistance=30;
        [Min(1)] public float mudFarDistance=100;
        [Range(1,64)] public float layerTessellationFactor=8;
        public bool displacementTargetEdgeEnabled=true;
        [Min(.02f)] public float displacementTargetEdgeLength=.25f;
        [Min(0)] public float displacementFadeStart=15;
        [Min(1)] public float displacementFadeEnd=50;
        [Min(.1f)] public float displacementSeamFade=2;
        [Min(0)] public float displacementMaskEdgeFade=.5f;
        [Min(0)] public float tessellationMaskTransition=.5f;
        public bool refineDisplacementFootprints=true;
        public bool regularMaskGridPreview;
        public bool regularMaskGridDisplacement;
        public Vector2Int regularMaskGridChunk=Vector2Int.zero;
        [Min(.125f)] public float regularMaskGridStep=.5f;
        public bool RegularMaskGridActive => regularMaskGridPreview&&enableLayerPainting&&enableLayerDisplacement;
        public bool IsRegularMaskGridChunk(int x,int z) => RegularMaskGridActive&&regularMaskGridChunk.x==x&&regularMaskGridChunk.y==z;
        public bool displacementFineBase;
        public bool FineDisplacementBaseActive => displacementFineBase && refineDisplacementFootprints;
        public float EffectiveDisplacementCellSize => LTPaintMath.DisplacementBaseStep(displacementBaseCellSize,FineDisplacementBaseActive);
        public float EffectiveTessellationFactor => LTPaintMath.DisplacementGpuFactor(layerTessellationFactor,FineDisplacementBaseActive);
        [Min(.5f)] public float displacementBaseCellSize=2;
        public bool displacementBoundaryPrototype;
        public bool displacementTransitionDiagonals=true;
        public Vector2Int displacementBoundaryChunk=Vector2Int.zero;
        [Min(.5f)] public float displacementBoundaryCellSize=.5f;
        public bool enableLayerPainting;
        public LTArrayResolution arrayColorResolution=LTArrayResolution.R1024;
        public LTArrayResolution arrayNormalResolution=LTArrayResolution.R1024;
        public LTArrayResolution arrayMaskResolution=LTArrayResolution.R1024;
        [Tooltip("Storage of the next saved bake. BC7 color/mask + BC5 normals; transient preview remains RGBA32. Source textures are unchanged.")]
        public LTLayerArrayStorage arrayStorage=LTLayerArrayStorage.RGBA32;
        public bool arrayTrilinear=true;
        [Range(1,16)] public int arrayAnisotropy=4;
        [Min(16)] public int arrayMemoryBudgetMiB=512;
        public LTLayerArrayBakeAsset savedLayerArrays;
        [System.NonSerialized] public int arrayPackRevision;
        [System.NonSerialized] public string arrayPackStatus="Массивы создаются при включённой покраске.";
        [System.NonSerialized] public Texture2DArray arrayColorPreview,arrayNormalPreview,arrayMaskPreview;
        public void RepackLayerArrays(){arrayPackRevision++;UpdatePainting();}
        [Range(0,1)] public float layerHeightBlend;
        public bool enableGlobalLayerMaps;
        [Range(256,8192)] public int globalLayerResolution=2048;
        [Min(0)] public float globalLayerStart=150;
        [Min(1)] public float globalLayerEnd=300;
        [Range(0,4), Tooltip("Яркость запечённого дальнего цвета: 1 — исходная, 0 — чёрный. Не требует повторного запекания; ближние слои не меняются.")]
        public float farLayerBrightness=1;
        [Range(0,1)] public float farLayerSmoothness=.3f;
        [Range(0,1)] public float farLayerMetallic;
        public LTGlobalBakeAsset savedGlobalBake;
        [System.NonSerialized] public int globalBakeRevision;
        [System.NonSerialized] public string paintTerrainSignature="";
        [System.NonSerialized] public string globalLayerStatus;
        [System.NonSerialized] public Texture globalAlbedoPreview,globalNormalPreview;
        [System.NonSerialized] public string paintStatus;
        [System.NonSerialized] public bool paintBenchmarkRunning;
        [System.NonSerialized] public LTPaintCpuCapture paintCpu=new LTPaintCpuCapture();
        LTPaintRuntime paintRuntime;
        internal LTPaintRuntime DetailPaintRuntime=>paintRuntime;
        public string GrassTerrainStatus=>paintRuntime?.GrassTerrainStatus??"Ожидание покраски террейна.";
        public string RoadTerrainBlendStatus=>paintRuntime?.RoadTerrain.Status??"Ожидание покраски terrain и перестройки дороги.";
        [System.NonSerialized] public bool showDisplacementCoverage;
        [System.NonSerialized] public bool forceHullOneDiagnostic;
        [System.NonSerialized] public bool uniformHullDiagnostic;
        [System.NonSerialized] public float uniformHullFactor=16;
        [System.NonSerialized] public Vector2Int uniformHullChunk=Vector2Int.zero;
        public void ApplyUniformHullDiagnostic()
        {
            if(uniformHullDiagnostic)SetHullOneDiagnostic(false);
            paintRuntime?.SetUniformHullDiagnostic(this);
        }
        public void SetHullOneDiagnostic(bool enabled)
        {
            forceHullOneDiagnostic=enabled;paintRuntime?.SetHullOneDiagnostic(enabled);
        }
        public System.Collections.Generic.IEnumerable<string> HullOneDiagnosticStatus =>
            paintRuntime!=null?paintRuntime.HullOneDiagnosticStatus:System.Array.Empty<string>();
        [System.NonSerialized] public bool displacementCoverageControlColor;
        [System.NonSerialized] public bool displacementCoverageFixedProjection;
        [System.NonSerialized] public bool displacementCoverageCoordinateProbe;
        [System.NonSerialized] public bool displacementHullReasons;
        [System.NonSerialized] public int displacementSurfaceProbe;
        public System.Collections.Generic.IEnumerable<LTPaintRuntime.CoverageDiagnostic> CoverageDiagnostics =>
            paintRuntime!=null?paintRuntime.CoverageDiagnostics:System.Array.Empty<LTPaintRuntime.CoverageDiagnostic>();
        public void SetDisplacementCoverageDebug(bool enabled)
        {
            showDisplacementCoverage=enabled;paintRuntime?.SetCoverageDebug(enabled,displacementCoverageControlColor,displacementCoverageFixedProjection,displacementCoverageCoordinateProbe,displacementHullReasons,displacementSurfaceProbe);
        }
        [System.NonSerialized] public readonly LTPaintMath.TerrainAuthoringRevisions displacementGeometry=new LTPaintMath.TerrainAuthoringRevisions();
        [System.NonSerialized] public string lastEditorUpdateCycle;
        [System.NonSerialized] public string lastEditorPaintStages;
        [System.NonSerialized] public string lastEditorPaintCycleStages;
#if UNITY_EDITOR
        // Installed by the editor engine. Automatic worlds have one update owner:
        // geometry first, paint second. Runtime/manual worlds keep their usual path.
        public static System.Func<LTWorld,bool> EditorOwnsPainting;
#endif
        public System.Collections.Generic.IEnumerable<LTPaintRuntime.DensityCoverage> DisplacementCoverage =>
            paintRuntime!=null?paintRuntime.DensityCoverages:System.Array.Empty<LTPaintRuntime.DensityCoverage>();
        public void ReleasePainting(){paintRuntime?.Dispose();paintRuntime=null;}
        public void RestorePaintingMaterialsForSave(){paintRuntime?.RestoreMaterialsForSave();}
        public void BakeGlobalLayerMaps()
        {
            if(!isActiveAndEnabled||paintBenchmarkRunning||!enableLayerPainting||!enableGlobalLayerMaps)
            {globalLayerStatus="Для запекания включите покраску и глобальные карты; дождитесь завершения GPU-теста.";return;}
            if(paintRuntime==null)paintRuntime=new LTPaintRuntime();
            paintRuntime.RequestGlobalBake();paintRuntime.Tick(this);
        }
        public void UpdatePainting()=>UpdatePainting(false);
        public bool PaintingPending=>paintRuntime!=null&&paintRuntime.PendingRoadSampling;
        public void UpdatePainting(bool afterGeometryUpdate,bool backgroundRoadSampling=false)
        {
            if(!isActiveAndEnabled||paintBenchmarkRunning)return;
#if UNITY_EDITOR
            // Scene handles and inspector drags retain hotControl until release/cancel.
            // Gate both ExecuteAlways.Update and the editor polling path here, before
            // hashes, terrain sampling or weight baking. Runtime updates are unaffected.
            if(!Application.isPlaying&&GUIUtility.hotControl!=0)return;
            if(!Application.isPlaying&&!afterGeometryUpdate&&EditorOwnsPainting!=null&&EditorOwnsPainting(this))return;
#endif
            if(paintRuntime==null)paintRuntime=new LTPaintRuntime();
            paintRuntime.Tick(this,afterGeometryUpdate,backgroundRoadSampling&&!Application.isPlaying);
        }
        [ContextMenu("Сбросить визуальное продавливание")]
        public void ClearDeformation(){paintRuntime?.ClearDeformation();}
        // Computed, not serialized: removing/reparenting stamps cannot leave stale
        // palette entries. Disabled stamps remain registered. Nested worlds own theirs.
        public System.Collections.Generic.List<LTPaintStamp> CollectPaintStamps()
        {
            var result=new System.Collections.Generic.List<LTPaintStamp>();
            void Walk(Transform parent)
            {
                for(int childIndex=0;childIndex<parent.childCount;childIndex++)
                {
                    var child=parent.GetChild(childIndex);
                    if(child==generatedRoot||child.TryGetComponent<LTWorld>(out _)||child.TryGetComponent<LTRoadGenerated>(out _)||child.TryGetComponent<LTRoadJunctionGenerated>(out _))continue;
                    if(child.TryGetComponent<LTPaintStamp>(out var stamp))result.Add(stamp);
                    Walk(child);
                }
            }
            Walk(transform);
            // Neutral junction coverage blends over the directed branch ends; keep
            // the relative order of ordinary stamps unchanged.
            var nodes=result.FindAll(s=>s.Junction);result.RemoveAll(s=>s.Junction);result.AddRange(nodes);
            return result;
        }
        [Range(1, LTChunkGrid.MaxAxis)] public int chunksX = 3, chunksZ = 3;
        [Range(8, 128)] public int cellsPerChunk = 32;
        public bool adaptive = true;
        [HideInInspector]
        public int localRefinement = 2; // legacy, ignored in v0.4
        [Min(10000), Tooltip("Resource budget only. Generation reports an error instead of lowering requested density.")]
        public int maxVerticesPerChunk = 300000;
        [Min(0.0001f)] public float maxHeightError = 0.05f;
        public bool enableLODs = true;
        public LTTerrainLODMode terrainLODMode;
        [Range(1,8)] public int lodPatchDivisions=4;
        public bool UseSpatialLODs=>enableLODs&&terrainLODMode==LTTerrainLODMode.WithinChunk;
        public int SpatialLODDivisions=>UseSpatialLODs?LTSpatialLODMath.Divisions(lodPatchDivisions):1;
        public LTLODSettings[] lods = {new LTLODSettings(1,.15f,80),new LTLODSettings(2,.5f,180),new LTLODSettings(3,1.5f,400)};
        public Camera lodCamera;
        [Min(0)] public float lodHysteresis = 5;
        [Tooltip("Disable terrain colliders farther than this distance at runtime. Set to 0 to keep all colliders enabled.")]
        [Min(0)] public float colliderMaxDistance = 250;
        [Range(-1,4), Tooltip("-1 = automatic; 0..4 = force a level for inspection.")]
        public int forceLOD = -1;
        public bool previewLODs;
        public bool autoUpdate = true;
        [Tooltip("Wait until a Scene/Inspector drag ends before rebuilding meshes. Greatly reduces editor lag for large stamps.")]
        [HideInInspector] public bool rebuildAfterEdit = true; // legacy scene data; staged editor updates always wait for release
        public bool showDebug = true;
        [HideInInspector] public Transform generatedRoot;
        [HideInInspector] public string outputFolder;
        [HideInInspector] public int lastUpdatedChunks;
        [HideInInspector] public float lastUpdateMilliseconds;
        [HideInInspector] public string lastUpdatedIds;
        [System.NonSerialized] public string lastBuildTimings;
        [System.NonSerialized] public string benchmarkTimings;
        [System.NonSerialized] public float lastDetectionMilliseconds, lastColliderMilliseconds;
        LTChunk[] cachedChunks;
        LTRoadLOD[] cachedRoadLODs;
        LTSpatialLODTopology spatialTopology;
        LTChunk[] spatialChunks;
        LTSpatialLODAsset[] spatialAssets;
        int[] spatialRevisions;
        int spatialColumns,spatialRows,spatialDivisions;
        bool spatialFailed;
        [System.NonSerialized] public string spatialLODStatus;
        [System.NonSerialized] public readonly LTLODCpuCapture lodCpu=new LTLODCpuCapture();
        [System.NonSerialized] public string lodCpuReportPath;
        readonly LTLODCameraSchedule lodCameraSchedule=new LTLODCameraSchedule();
        double nextColliderLODUpdate;
        public void RefreshLODCache(){InvalidateRockSeams();cachedChunks=null;cachedRoadLODs=null;spatialTopology=null;spatialChunks=null;spatialFailed=false;spatialLODStatus=null;lodCameraSchedule.Invalidate();nextColliderLODUpdate=0;}
        void OnEnable()
        {
            RefreshLODCache();
            RenderPipelineManager.beginCameraRendering-=BeforeCameraRendering;
            RenderPipelineManager.beginCameraRendering+=BeforeCameraRendering;
        }
        // OnValidate may run off the main thread: only invalidate managed state.
        void OnValidate(){lodCameraSchedule.Invalidate();}
        void OnDisable()
        {
            InvalidateRockSeams();
            if(lodCpu.Stop("world disabled"))PublishLODCpuReport();
            RenderPipelineManager.beginCameraRendering-=BeforeCameraRendering;
            lodCameraSchedule.Invalidate();
            ReleasePainting();
            if(generatedRoot)foreach(var c in generatedRoot.GetComponentsInChildren<LTChunk>())
            {c.SetRockSeamMaps(null);c.ReleaseLODPreview();c.ShowLOD(0);}
            foreach(var road in GetComponentsInChildren<LTRoadLOD>())road.Show(0);
        }
        void Update(){UpdateLOD();UpdatePainting();paintCpu.Poll();PollLODCpuCapture();}
        public void StartLODCpuCapture()
        {
            if(lodCpu.Running)return;
            lodCpuReportPath=null;
            var camera=ResolveLODCamera();
            lodCpu.Start(name,Application.isPlaying?"Play":"Edit",
                $"mode={terrainLODMode}; enabled={enableLODs}; preview={previewLODs}; Force LOD={forceLOD}; patches={SpatialLODDivisions}; chunks={chunksX}x{chunksZ}; camera={(camera?camera.name:"none")}; Unity={Application.unityVersion}");
        }
        public void PollLODCpuCapture(){if(lodCpu.Poll())PublishLODCpuReport();}
        public void StopLODCpuCapture(){if(lodCpu.Stop())PublishLODCpuReport();}
        void PublishLODCpuReport()
        {
            // Never serialize/log from the rendering callback: finishing a capture
            // must not create the hitch that we are trying to diagnose.
#if UNITY_EDITOR
            try
            {
                string folder=System.IO.Path.GetFullPath("Logs/LocalTerrainLODCaptures");
                System.IO.Directory.CreateDirectory(folder);
                lodCpuReportPath=System.IO.Path.Combine(folder,System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+System.Guid.NewGuid().ToString("N")+".json");
                System.IO.File.WriteAllText(lodCpuReportPath,JsonUtility.ToJson(lodCpu.LastReport,true));
            }
            catch(System.Exception error){lodCpuReportPath="Не удалось сохранить отчёт: "+error.Message;}
#endif
            Debug.Log(lodCpu.Status+"\n"+lodCpuReportPath,this);
        }
        public void UpdateLOD()
        {
            if(!isActiveAndEnabled||paintBenchmarkRunning||!lodCameraSchedule.PollDue(Time.realtimeSinceStartupAsDouble))return;
            UpdateLOD(ResolveLODCamera(),false);
        }
        void BeforeCameraRendering(ScriptableRenderContext context,Camera renderingCamera)
        {
            if(!isActiveAndEnabled||paintBenchmarkRunning||!renderingCamera||!LTLODCameraSchedule.IsRenderCamera(renderingCamera.cameraType))return;
            // Keep one LOD driver for this world, even if HDRP renders multiple
            // cameras. Reflection/preview cameras must never change its topology.
            UpdateLOD(ResolveLODCamera(),true);
        }
        Camera ResolveLODCamera()
        {
            Camera camera=lodCamera;
#if UNITY_EDITOR
            if(!Application.isPlaying && previewLODs && UnityEditor.SceneView.lastActiveSceneView)
                camera=UnityEditor.SceneView.lastActiveSceneView.camera;
#endif
            if(!camera)camera=Camera.main;
            return camera;
        }
        void UpdateLOD(Camera camera,bool beforeRender)
        {
            if(!generatedRoot)return;
            double now=Time.realtimeSinceStartupAsDouble;
            if(!lodCameraSchedule.TryBegin(now,camera?camera.GetInstanceID():0,camera?camera.transform.position:Vector3.zero,beforeRender))return;
            using var lodProfile = LodUpdateMarker.Auto();
            var capturePosition=lodCpu.Running&&camera?camera.transform.position:Vector3.zero;
            using var capture=lodCpu.BeginUpdate(lodCpu.Running&&camera?camera.GetInstanceID():0,capturePosition.x,capturePosition.y,capturePosition.z,beforeRender,forceLOD);
            // Camera-rate visual selection must not increase collider maintenance.
            bool updateColliders=Application.isPlaying&&now>=nextColliderLODUpdate;
            if(updateColliders)nextColliderLODUpdate=now+.1;
            using(LodCacheMarker.Auto())
            using(lodCpu.Measure(LTLODCpuCapture.Stage.Cache))
                if(cachedChunks==null)cachedChunks=generatedRoot.GetComponentsInChildren<LTChunk>();
            int terrainForce=TerrainForcedLOD;
            bool spatialRequested=UseSpatialLODs&&(Application.isPlaying||previewLODs||terrainForce>=0)&&(camera||terrainForce>=0);
            bool spatialReady=spatialRequested&&UpdateSpatialTopology(camera);
            using(LodRockSeamMarker.Auto())
            if(!rockSeamFailed)
            {
                try{rockSeamRuntime.Prepare(this,cachedChunks,spatialTopology,spatialReady);}
                catch(System.Exception error)
                {
                    rockSeamRuntime.Dispose();rockSeamFailed=true;
                    Debug.LogWarning("Joint rock/terrain LOD disabled for this bake; original matching contours retained: "+error.Message,this);
                }
            }
            rockSeamLookup??=rockSeamRuntime.Map;
            foreach(var c in cachedChunks)if(c)c.SetRockSeamMaps(rockSeamLookup);
            lodCpu.TerrainState(spatialRequested,spatialReady,cachedChunks.Length);
            using(LodTerrainMarker.Auto())
            using(lodCpu.Measure(LTLODCpuCapture.Stage.TerrainApply))
            {
                foreach(var c in cachedChunks)
                {
                    if(!c||!c.mesh)continue;
                    if(updateColliders && camera && colliderMaxDistance > 0)
                    {
                        Vector3 colliderLocal=c.transform.InverseTransformPoint(camera.transform.position);
                        float colliderDistance=Mathf.Sqrt(c.mesh.bounds.SqrDistance(colliderLocal));
                        var collider=c.GetComponent<MeshCollider>();
                        if(collider) collider.enabled=colliderDistance <= colliderMaxDistance;
                    }
                    int level=0;
                    if(enableLODs && (Application.isPlaying||previewLODs||terrainForce>=0))
                    {
                        if(terrainForce>=0)level=terrainForce;
                        else if(camera && lods!=null)
                        {
                            Vector3 local=c.transform.InverseTransformPoint(camera.transform.position);
                            float distance=Mathf.Sqrt(c.mesh.bounds.SqrDistance(local));
                            level=SelectLOD(distance,c.currentLOD,lods,lodHysteresis);
                        }
                    }
                    if(spatialReady)
                    {int id=ChunkId(c.x,c.z);c.ShowSpatialLOD(spatialTopology,id,spatialTopology.DirtyChunks.Contains(id),lodCpu);}
                    else c.ShowLOD(UseSpatialLODs||IsRegularMaskGridChunk(c.x,c.z)?0:level);
                }
            }
            if(spatialReady)lodCpu.IndexCacheResult(spatialTopology.IndexCacheHits,spatialTopology.IndexCacheBuilds,
                spatialTopology.IndexCacheCells,spatialTopology.IndexCacheFallbacks,spatialTopology.IndexCacheBytes);
            using(LodRoadMarker.Auto())
            using(lodCpu.Measure(LTLODCpuCapture.Stage.Roads))
            {
                if(cachedRoadLODs==null)cachedRoadLODs=GetComponentsInChildren<LTRoadLOD>(true);
                foreach(var segment in cachedRoadLODs)
                {
                    if(!segment||!segment.gameObject.activeInHierarchy)continue;
                    var node=segment.junction;
                    if(node?node.World!=this||!node.isActiveAndEnabled:!segment.owner||segment.owner.World!=this)continue;
                    var settings=node?node.lods:segment.owner.asphaltLODs;
                    int level=0;
                    if((node||segment.owner.asphaltLODMode!=LTRoadLODMode.Disabled)&&(Application.isPlaying||previewLODs||forceLOD>=0))
                    {
                        if(forceLOD>=0)level=forceLOD;
                        else if(camera)
                        {
                            float distance=Mathf.Sqrt(segment.bounds.SqrDistance(segment.transform.InverseTransformPoint(camera.transform.position)));
                            level=node&&!node.Authored?SelectLOD(distance,segment.current,settings,lodHysteresis):
                                LTRoadLODPolicy.Select(distance,segment.current,settings,segment.meshes?.Length??0,lodHysteresis);
                        }
                    }
                    segment.Show(level);
                }
            }
            lodCpu.Completed();
        }
        bool UpdateSpatialTopology(Camera camera)
        {
            if(spatialFailed)return false;
            try
            {
                using(LodSetupMarker.Auto())
                using(lodCpu.Measure(LTLODCpuCapture.Stage.TopologySetup))
                {
                    bool reset=spatialTopology==null||spatialColumns!=chunksX||spatialRows!=chunksZ||spatialDivisions!=SpatialLODDivisions;
                    if(!reset)foreach(var c in cachedChunks)
                    {
                        int id=c?ChunkId(c.x,c.z):-1;
                        if(id<0||id>=spatialChunks.Length||spatialChunks[id]!=c||spatialAssets[id]!=c.ActiveSpatialLOD||!c.ActiveSpatialLOD||spatialRevisions[id]!=c.ActiveSpatialLOD.revision){reset=true;break;}
                    }
                    if(reset)
                    {
                        if(!Grid.Valid)throw new System.InvalidOperationException("Некорректный размер сетки чанков.");
                        int count=chunksX*chunksZ;
                        var ordered=new LTChunk[count];var input=new LTSpatialLODTopology.Chunk[count];
                        var assets=new LTSpatialLODAsset[count];var revisions=new int[count];
                        foreach(var c in cachedChunks)
                        {
                            if(!c||!Grid.Contains(c.x,c.z))throw new System.InvalidOperationException("Некорректная сетка чанков.");
                            int id=ChunkId(c.x,c.z);var asset=c.ActiveSpatialLOD;
                            if(ordered[id]||!asset||!asset.vertexBank||asset.formatVersion!=LTSpatialLODMath.Version||asset.divisions!=SpatialLODDivisions)
                                throw new System.InvalidOperationException("Нужна однократная перестройка геометрии: данные адаптивных стыков LOD отсутствуют или устарели.");
                            ordered[id]=c;assets[id]=asset;revisions[id]=asset.revision;
                            input[id]=new LTSpatialLODTopology.Chunk{cells=asset.cells,patches=asset.patches};
                        }
                        spatialTopology=new LTSpatialLODTopology(chunksX,chunksZ,SpatialLODDivisions,input);
                        spatialChunks=ordered;spatialAssets=assets;spatialRevisions=revisions;
                        spatialColumns=chunksX;spatialRows=chunksZ;spatialDivisions=SpatialLODDivisions;
                        lodCpu.TopologyBuilt();
                    }
                }
                using(LodSelectionMarker.Auto())
                using(lodCpu.Measure(LTLODCpuCapture.Stage.Selection))
                {
                    for(int id=0;id<spatialChunks.Length;id++)
                    {
                        var c=spatialChunks[id];if(!c)continue;var patches=spatialAssets[id].patches;
                        var local=camera?c.transform.InverseTransformPoint(camera.transform.position):Vector3.zero;
                        bool fixedLOD=IsRegularMaskGridChunk(c.x,c.z);
                        for(int p=0;p<patches.Length;p++)
                        {
                            int level=fixedLOD?0:TerrainForcedLOD>=0?TerrainForcedLOD:SelectLOD(Mathf.Sqrt(patches[p].bounds.SqrDistance(local)),spatialTopology.Requested(id,p),lods,lodHysteresis);
                            spatialTopology.SetLevel(id,p,level);
                        }
                    }
                }
                using(LodBalanceMarker.Auto())
                using(lodCpu.Measure(LTLODCpuCapture.Stage.Balance))spatialTopology.Update();
                lodCpu.TopologyResult(spatialTopology.DirtyChunks.Count,spatialTopology.ResetPatches,spatialTopology.ProcessedCells);
                spatialLODStatus=null;return true;
            }
            catch(System.Exception e)
            {
                lodCpu.TopologyFailed();
                // Never mix incompatible old/new seam data across neighbouring chunks.
                // A successful bake calls RefreshLODCache and permits a fresh attempt.
                spatialFailed=true;spatialTopology=null;spatialLODStatus=e.Message+" Пока отображается LOD0.";
                return false;
            }
        }
        public static int SelectLOD(float distance,int current,LTLODSettings[] settings,float hysteresis)
        {
            int level=0;
            if(settings==null)return level;
            for(int i=0;i<Mathf.Min(4,settings.Length);i++)
            {
                if(settings[i]==null||float.IsNaN(settings[i].startDistance)||float.IsInfinity(settings[i].startDistance)||settings[i].startDistance<0 ||
                    (i>0&&settings[i].startDistance<=settings[i-1].startDistance))return 0;
                float threshold=settings[i].startDistance+(current>i?-Mathf.Max(0,hysteresis):Mathf.Max(0,hysteresis));
                if(distance>=threshold)level=i+1;else break;
            }
            return level;
        }
    }
}
