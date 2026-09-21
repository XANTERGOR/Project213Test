using UnityEngine;

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
    public sealed class LTWorld : MonoBehaviour
    {
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
        [Range(0,1)] public float layerHeightBlend;
        public bool enableGlobalLayerMaps;
        [Range(256,8192)] public int globalLayerResolution=2048;
        [Min(0)] public float globalLayerStart=150;
        [Min(1)] public float globalLayerEnd=300;
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
        [System.NonSerialized] public string displacementGeometryKey="";
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
        public void UpdatePainting()
        {
            if(!isActiveAndEnabled||paintBenchmarkRunning)return;
#if UNITY_EDITOR
            // Scene handles and inspector drags retain hotControl until release/cancel.
            // Gate both ExecuteAlways.Update and the editor polling path here, before
            // hashes, terrain sampling or weight baking. Runtime updates are unaffected.
            if(!Application.isPlaying&&GUIUtility.hotControl!=0)return;
#endif
            if(paintRuntime==null)paintRuntime=new LTPaintRuntime();
            paintRuntime.Tick(this);
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
                foreach(Transform child in parent)
                {
                    if(child==generatedRoot||child.GetComponent<LTWorld>())continue;
                    var stamp=child.GetComponent<LTPaintStamp>();if(stamp)result.Add(stamp);
                    Walk(child);
                }
            }
            Walk(transform);return result;
        }
        [Range(1, 16)] public int chunksX = 3, chunksZ = 3;
        [Range(8, 128)] public int cellsPerChunk = 32;
        public bool adaptive = true;
        [HideInInspector]
        public int localRefinement = 2; // legacy, ignored in v0.4
        [Min(10000), Tooltip("Resource budget only. Generation reports an error instead of lowering requested density.")]
        public int maxVerticesPerChunk = 300000;
        [Min(0.0001f)] public float maxHeightError = 0.05f;
        public bool enableLODs = true;
        public LTLODSettings[] lods = {new LTLODSettings(1,.15f,80),new LTLODSettings(2,.5f,180),new LTLODSettings(3,1.5f,400)};
        public Camera lodCamera;
        [Min(0)] public float lodHysteresis = 5;
        [Tooltip("Disable terrain colliders farther than this distance at runtime. Set to 0 to keep all colliders enabled.")]
        [Min(0)] public float colliderMaxDistance = 250;
        [Range(-1,3), Tooltip("-1 = automatic; 0..3 = force a level for inspection.")]
        public int forceLOD = -1;
        public bool previewLODs;
        public bool autoUpdate = true;
        [Tooltip("Wait until a Scene/Inspector drag ends before rebuilding meshes. Greatly reduces editor lag for large stamps.")]
        public bool rebuildAfterEdit = true;
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
        double nextLODUpdate;
        public void RefreshLODCache(){cachedChunks=null;nextLODUpdate=0;}
        void OnEnable(){RefreshLODCache();}
        void OnDisable()
        {
            ReleasePainting();
            if(generatedRoot)foreach(var c in generatedRoot.GetComponentsInChildren<LTChunk>())c.ShowLOD(0);
        }
        void Update(){UpdateLOD();UpdatePainting();paintCpu.Poll();}
        public void UpdateLOD()
        {
            if(paintBenchmarkRunning)return;
            double now=Time.realtimeSinceStartupAsDouble;
            if(now<nextLODUpdate)return;
            nextLODUpdate=now+.1;
            if(!generatedRoot)return;
            if(cachedChunks==null)cachedChunks=generatedRoot.GetComponentsInChildren<LTChunk>();
            Camera camera=lodCamera;
#if UNITY_EDITOR
            if(!Application.isPlaying && previewLODs && UnityEditor.SceneView.lastActiveSceneView)
                camera=UnityEditor.SceneView.lastActiveSceneView.camera;
#endif
            if(!camera)camera=Camera.main;
            foreach(var c in cachedChunks)
            {
                if(!c||!c.mesh)continue;
                if(Application.isPlaying && camera && colliderMaxDistance > 0)
                {
                    Vector3 colliderLocal=c.transform.InverseTransformPoint(camera.transform.position);
                    float colliderDistance=Mathf.Sqrt(c.mesh.bounds.SqrDistance(colliderLocal));
                    var collider=c.GetComponent<MeshCollider>();
                    if(collider) collider.enabled=colliderDistance <= colliderMaxDistance;
                }
                int level=0;
                if(enableLODs && (Application.isPlaying||previewLODs||forceLOD>=0))
                {
                    if(forceLOD>=0)level=forceLOD;
                    else if(camera && lods!=null)
                    {
                        Vector3 local=c.transform.InverseTransformPoint(camera.transform.position);
                        float distance=Mathf.Sqrt(c.mesh.bounds.SqrDistance(local));
                        level=SelectLOD(distance,c.currentLOD,lods,lodHysteresis);
                    }
                }
                c.ShowLOD(IsRegularMaskGridChunk(c.x,c.z)?0:level);
            }
        }
        public static int SelectLOD(float distance,int current,LTLODSettings[] settings,float hysteresis)
        {
            int level=0;
            if(settings==null)return level;
            for(int i=0;i<Mathf.Min(3,settings.Length);i++)
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
