using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(LTWorld))]
    [AddComponentMenu("Local Terrain/Detail Renderer")]
    public sealed class LTDetailRenderer : MonoBehaviour
    {
        [Tooltip("Общий seed. Расстановка не зависит от камеры и порядка списка.")] public int seed=12345;
        [Min(0),Tooltip("Множитель плотности. Увеличение добавляет объекты, сохраняя прежние позиции.")] public float densityMultiplier=1;
        [Min(1),Tooltip("Размер ячейки отсечения, м. Не меняет позиции кандидатов на фиксированной метровой решётке.")] public int cellSize=16;
        [Min(1),Tooltip("Общий предел видимости; дополнительно применяется дистанция записи.")] public float maxDistance=150;
        [Min(0),Tooltip("Запас подгрузки за Max Distance, м. Не увеличивает дистанцию отрисовки.")] public float preloadDistance=32;
        [Min(0),Tooltip("Сколько секунд хранить ячейку после выхода из зоны подгрузки. При нехватке бюджета дальний кэш освобождается раньше.")] public float unloadDelay=5;
        [Range(.25f,20),Tooltip("Мягкий бюджет генерации за обновление, мс. Проверка каждые 32 кандидата; подготовка карт/мешей вне бюджета.")] public float generationMilliseconds=2;
        [Min(1),Tooltip("Лимит экземпляров мира. При превышении остановка с сообщением, без скрытого снижения плотности.")] public int maxInstances=200000;
        [Min(1),Tooltip("Лимит загруженных ячеек всех камер, а не всей карты. При превышении уменьшите радиус или увеличьте размер ячейки/лимит.")] public int maxCells=4096;
        [Min(1),Tooltip("Защита от чрезмерной плотности: максимум кандидатов на одну ячейку.")] public int maxCandidatesPerCell=1000000;
        [Tooltip("Отображать в Scene View, независимо от игровых камер.")] public bool sceneView=true;
        [Tooltip("Проверять изменения раз в секунду. Иначе перестройка вручную через меню компонента.")] public bool autoRefresh=true;
        [Tooltip("GPU indirect: постоянные буферы, GPU culling/LOD. Адаптированы DA_Grass_WIND, BaseShaderProps и HDRP/Lit; остальные шейдеры используют CPU. Windows D3D11/12, без XR. Индивидуальные классические Light Probes не поддерживаются; сравните освещение. Исходные материалы не меняются.")] public bool gpuDetails;
        [Range(16,512),Tooltip("Лимит буферов GPU-детализации на этот мир, МиБ. При нехватке группа остаётся на CPU. Не включает меши, текстуры, материалы и накладные расходы драйвера.")] public int gpuMemoryMiB=128;
        [Range(.25f,10),Tooltip("Мягкий CPU-бюджет подготовки/загрузки GPU-буферов за игровой кадр или обновление редактора, общий для камер этого мира. Отдельный вызов драйвера может превысить бюджет. Ожидающие группы временно рисуются CPU.")] public float gpuUploadMilliseconds=1;
        [Range(1,16),Tooltip("Максимум данных матриц, загружаемых на GPU за кадр/обновление редактора, МиБ. Не включает выделение пустых выходных буферов. Порция — до 2048 экземпляров.")] public int gpuUploadMiB=2;
        [Tooltip("Объединять совместимые непрозрачные партии соседних ячеек (2×2). Выключите для сравнения с прежней отправкой. Количество растений, LOD и дальности не меняются. Это CPU batching, не GPU indirect.")] public bool combineDrawBatches=true;
        [InspectorName("Matrix Cache MiB"),Range(0,64),Tooltip("Предел постоянного CPU-кэша матриц частей мешей, МиБ. Заполняется в бюджете генерации, освобождается с ячейками. 0 — считать матрицы при отправке. Не VRAM; при перестройке старый и новый набор могут временно сосуществовать.")] public int matrixCacheMiB=16;
        public string Status=>status;
        public bool IsGenerating=>generation!=null;
        public int InstanceCount=>instanceCount;
        public int CellCount=>cells.Count;
        public int LastDrawCalls{get;private set;}
        public int LastSubmittedInstances{get;private set;}
        public string LastCamera{get;private set;}
        public double LastTickMilliseconds{get;private set;}
        public double PeakTickMilliseconds{get;private set;}
        public double LastStreamingMilliseconds{get;private set;}
        public double LastPreparationMilliseconds{get;private set;}
        public double PeakPreparationMilliseconds{get;private set;}
        public double LastGenerationMilliseconds{get;private set;}
        public double PeakGenerationMilliseconds{get;private set;}
        public double LastRenderMilliseconds{get;private set;}
        public double PeakRenderMilliseconds{get;private set;}
        public double LastSubmissionMilliseconds{get;private set;}
        public int LastTestedCells{get;private set;}
        public int LastCulledCells{get;private set;}
        public int LastEarlySkippedInstances{get;private set;}
        public int LastTestedInstances{get;private set;}
        public int LastCulledInstances{get;private set;}
        public int LastSelectedInstances{get;private set;}
        public int LastShadowSubmittedInstances{get;private set;}
        public int LastUnmergedDrawCalls{get;private set;}
        public long CachedMatrixBytes=>cachedMatrixBytes;
        public int BatchBufferCount=>batchQueue.BufferCount;
        public long GpuBytes=>gpuRenderer?.ResidentBytes??0;
        public int GpuDrawCalls=>gpuRenderer?.DrawCalls??0;
        public int GpuCullDispatches=>gpuRenderer?.CullDispatches??0;
        public int GpuCandidates=>gpuRenderer?.Candidates??0;
        public int GpuFallbackGroups=>gpuRenderer?.FallbackGroups??0;
        public string GpuStatus=>gpuRenderer?.Status;
        public long GpuReservedBytes=>gpuRenderer?.ReservedBytes??0;
        public long GpuPeakBytes=>gpuRenderer?.PeakBytes??0;
        public long GpuReleasedBytes=>gpuRenderer?.ReleasedBytes??0;
        public int GpuBufferCount=>gpuRenderer?.BufferCount??0;
        public int GpuGroupCount=>gpuRenderer?.GroupCount??0;
        public int GpuViewCount=>gpuRenderer?.ViewCount??0;
        public int GpuMaterialCount=>gpuRenderer?.MaterialCount??0;
        public int GpuPendingGroups=>gpuRenderer?.PendingGroups??0;
        public long GpuUploadBytes=>gpuRenderer?.UploadBytes??0;
        public double GpuUploadMilliseconds=>gpuRenderer?.UploadMilliseconds??0;
        public double GpuPeakUploadMilliseconds=>gpuRenderer?.PeakUploadMilliseconds??0;
        public void ReportGpuFallback()
        {
            Debug.Log("Камера: "+LastCamera+"\n"+(gpuRenderer?.GetFallbackReport()??"GPU backend не создан."),this);
        }
        int gpuEditorFrame;
        public void CheckGpuMemory()
        {
            if(gpuRenderer==null){Debug.Log("GPU backend не создан; принадлежащих ему буферов нет.",this);return;}
            bool ok=gpuRenderer.CheckMemoryAccounting(out var report);
            if(ok)Debug.Log(report,this);else Debug.LogError(report,this);
        }
        LTDetailGpuRenderer gpuRenderer;
        long cachedMatrixBytes;
        readonly LTDetailBatchQueue<DrawKey> batchQueue=new LTDetailBatchQueue<DrawKey>();
        Action<LTDetailBatchQueue<DrawKey>.Batch> emitMerged;
        Camera submissionCamera;
        struct DrawKey:IEquatable<DrawKey>
        {
            public LTDetailDrawState state;
            public LTDetailPrefab.Part part;
            public bool Equals(DrawKey other)=>state.Equals(other.state);
            public override bool Equals(object other)=>other is DrawKey key&&Equals(key);
            public override int GetHashCode()=>state.GetHashCode();
        }
        static readonly Unity.Profiling.ProfilerMarker TickMarker=new Unity.Profiling.ProfilerMarker("LT.Details.Tick");
        static readonly Unity.Profiling.ProfilerMarker StreamingMarker=new Unity.Profiling.ProfilerMarker("LT.Details.Streaming");
        static readonly Unity.Profiling.ProfilerMarker PrepareMarker=new Unity.Profiling.ProfilerMarker("LT.Details.Prepare");
        static readonly Unity.Profiling.ProfilerMarker GenerateMarker=new Unity.Profiling.ProfilerMarker("LT.Details.Generate");
        static readonly Unity.Profiling.ProfilerMarker RenderMarker=new Unity.Profiling.ProfilerMarker("LT.Details.RenderCPU");
        static readonly Unity.Profiling.ProfilerMarker SubmitMarker=new Unity.Profiling.ProfilerMarker("LT.Details.Submit");
        static readonly Unity.Profiling.ProfilerMarker GraphicsMarker=new Unity.Profiling.ProfilerMarker("LT.Details.GraphicsSubmit");
        static long Timestamp()=>System.Diagnostics.Stopwatch.GetTimestamp();
        static double Milliseconds(long start)=>(Timestamp()-start)*(1000.0/System.Diagnostics.Stopwatch.Frequency);
        string status="Ожидание поверхности.";
        LTWorld world;
        readonly LTPaintTerrain terrain=new LTPaintTerrain();
        readonly Dictionary<Vector2Int,Cell> cells=new Dictionary<Vector2Int,Cell>();
        readonly Dictionary<GameObject,RecipeCache> recipes=new Dictionary<GameObject,RecipeCache>();
        readonly Plane[] planes=new Plane[6];
        // Conservative Unity limit including object-to-world and inverse matrices.
        readonly Matrix4x4[] drawMatrices=new Matrix4x4[511];
        LTDetailMotionData[] motionData;
        readonly List<string> messages=new List<string>();
        readonly List<Vector2> cameraPositions=new List<Vector2>();
        readonly List<LTDetailStreaming.View> streamingViews=new List<LTDetailStreaming.View>();
        readonly List<Plane[]> streamingPlaneBuffers=new List<Plane[]>();
        readonly List<Vector2Int> planned=new List<Vector2Int>();
        readonly HashSet<Vector2Int> wanted=new HashSet<Vector2Int>();
        Camera[] cameraBuffer=new Camera[8];
        double nextPlan;
        bool streamDirty,planValid=true;
        int residentCellSize;
        public int WantedCellCount=>wanted.Count;
        IEnumerator generation;
        double nextRefresh;
        int instanceCount,rebuildRevision;
        bool requested=true,blocked;
        sealed class RecipeCache { public string key;public LTDetailPrefab recipe;public string message; }
        sealed class Source
        {
            public LTDetailEntry entry;public LTDetailPrefab recipe;public LTSurfaceLayer layer;
            public Stamp stamp;public string owner;public int seed,order,hash,placementHash;public float density;
        }
        [Serializable]
        sealed class Stamp
        {
            public string id;public int hash;public Matrix4x4 inverse;public Rect rect;public Vector2 size;
            public bool ellipse,allLayers;public float falloff;public LTDetailCategory categories;
            public LTDetailStampMode mode;public LTSurfaceLayer[] layers;
            public float Weight(Vector3 p,LTDetailSurface.Tile tile,float[] weights)
            {
                var q=inverse.MultiplyPoint3x4(p);float allowed=allLayers?1:0;
                if(!allLayers)for(int i=0;i<tile.layers.Length;i++)if(Array.IndexOf(layers,tile.layers[i])>=0)allowed+=weights[i];
                return LTDetailMath.AreaWeight(q.x,q.z,size.x,size.y,ellipse,falloff)*Mathf.Clamp01(allowed);
            }
        }
        struct Instance
        {
            public Matrix4x4 matrix;public Bounds bounds;public Vector3 position,lodPosition;public float lodSize;public uint id;
            // Preserve the original orientation and sampling point, avoiding ratio drift
            // and offset-along-normal errors when repeatedly changing size.
            public Quaternion rotation;public Vector2 maskPosition;
        }
        struct Selection { public int index;public bool inView,shadowsInRange; }
        struct LevelFlags { public bool color,caster; }
        sealed class Group : ILTDetailGpuSource
        {
            public LTDetailPrefab Recipe=>source.recipe;
            public LTDetailEntry Entry=>source.entry;
            public Bounds Bounds=>bounds;
            public int Count=>instances.Count;
            public void CopyGpuData(int start,int count,LTDetailGpuData[] data)
            {
                for(int i=0;i<count;i++)
                {
                    var item=instances[start+i];
                    data[i]=new LTDetailGpuData{objectToWorld=item.matrix,worldToObject=item.matrix.inverse,
                        positionSize=new Vector4(item.position.x,item.position.y,item.position.z,item.lodSize),
                        lodPositionRandom=new Vector4(item.lodPosition.x,item.lodPosition.y,item.lodPosition.z,LTDetailMath.Unit(item.id,27)),
                        center=item.bounds.center,extents=item.bounds.extents};
                }
            }
            public Source source;
            public readonly List<Instance> instances=new List<Instance>();
            public List<Selection>[] selected;
            public LevelFlags[] levelFlags;
            public Bounds bounds,originBounds;
            public float shadowRange;
            public readonly Dictionary<Matrix4x4,Matrix4x4[]> partMatrices=new Dictionary<Matrix4x4,Matrix4x4[]>();
        }
        sealed class Cell
        {
            public int hash,placementHash,count;public long cachedMatrixBytes;public double lastWanted;public float shadowRange;public Bounds bounds,originBounds;public readonly List<Group> groups=new List<Group>();
        }
        static int Mix(int a,int b)=>unchecked(a*397^b);
        static bool Touches(Rect a,Rect b)=>a.xMin<b.xMax&&a.xMax>b.xMin&&a.yMin<b.yMax&&a.yMax>b.yMin;
        void OnEnable()
        {
            emitMerged=SubmitMerged;
            world=GetComponent<LTWorld>();requested=true;
            RenderPipelineManager.beginCameraRendering+=Render;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update+=EditorTick;
#endif
        }
        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering-=Render;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update-=EditorTick;
#endif
            Clear();
        }
        void Clear()
        {
            gpuRenderer?.Dispose();gpuRenderer=null;
            (generation as IDisposable)?.Dispose();generation=null;cells.Clear();recipes.Clear();terrain.Clear();instanceCount=0;
            cachedMatrixBytes=0;batchQueue.Clear();submissionCamera=null;
            motionData=null;
            planned.Clear();wanted.Clear();nextPlan=0;streamDirty=true;
            if(world)world.DetailPaintRuntime?.ReleaseDetailSnapshots();
            ResetStatistics();
        }
        public void ResetStatistics()
        {
            ResetRenderStatistics();LastCamera=null;
            LastTickMilliseconds=PeakTickMilliseconds=LastStreamingMilliseconds=0;
            LastPreparationMilliseconds=PeakPreparationMilliseconds=0;
            LastGenerationMilliseconds=PeakGenerationMilliseconds=0;
            LastRenderMilliseconds=PeakRenderMilliseconds=LastSubmissionMilliseconds=0;
        }
        void ResetRenderStatistics()
        {
            LastDrawCalls=LastSubmittedInstances=LastShadowSubmittedInstances=0;
            LastUnmergedDrawCalls=0;
            LastTestedCells=LastCulledCells=LastEarlySkippedInstances=0;
            LastTestedInstances=LastCulledInstances=LastSelectedInstances=0;
            LastSubmissionMilliseconds=0;
        }
        void OnValidate()
        {
            cellSize=Mathf.Max(1,cellSize);maxCells=Mathf.Max(1,maxCells);maxInstances=Mathf.Max(1,maxInstances);
            maxCandidatesPerCell=Mathf.Max(1,maxCandidatesPerCell);maxDistance=Mathf.Max(1,maxDistance);
            densityMultiplier=Mathf.Max(0,densityMultiplier);generationMilliseconds=Mathf.Clamp(generationMilliseconds,.25f,20);requested=true;
            preloadDistance=Mathf.Max(0,preloadDistance);unloadDelay=Mathf.Max(0,unloadDelay);nextPlan=0;
            matrixCacheMiB=Mathf.Clamp(matrixCacheMiB,0,64);
            gpuMemoryMiB=Mathf.Clamp(gpuMemoryMiB,16,512);
            gpuUploadMilliseconds=Mathf.Clamp(gpuUploadMilliseconds,.25f,10);gpuUploadMiB=Mathf.Clamp(gpuUploadMiB,1,16);
        }
        [ContextMenu("Перестроить детализацию")]
        public void Rebuild(){requested=true;blocked=false;recipes.Clear();rebuildRevision++;}
        // Re-capture updated terrain/road masks; per-cell hashes retain unaffected
        // placements, prefab recipes and GPU groups. Unlike Rebuild, no global revision bump.
        public void RequestSurfaceRefresh(){requested=true;blocked=false;}
        void Update(){if(Application.isPlaying)Tick();}
#if UNITY_EDITOR
        void EditorTick()
        {
            unchecked{gpuEditorFrame++;}
            if(!this||!isActiveAndEnabled||Application.isPlaying||UnityEditor.EditorApplication.isCompiling||GUIUtility.hotControl!=0)return;
            Tick();
        }
#endif
        void Tick()
        {
            long start=Timestamp();LastGenerationMilliseconds=LastStreamingMilliseconds=0;
            using(TickMarker.Auto())
            try{TickCore();}
            finally{LastTickMilliseconds=Milliseconds(start);PeakTickMilliseconds=Math.Max(PeakTickMilliseconds,LastTickMilliseconds);}
        }
        void TickCore()
        {
            if(!world||!world.isActiveAndEnabled||!world.source||!world.generatedRoot||!world.enableLayerPainting)
            {if(cells.Count>0||generation!=null)Clear();status="Нужен активный LTWorld с геометрией и покраской слоёв.";requested=true;return;}
            if(!SystemInfo.supportsInstancing){status="GPU Instancing не поддерживается.";return;}
            try
            {
                long streamingStart=Timestamp();
                using(StreamingMarker.Auto())
                try{UpdateStreaming();}
                finally{LastStreamingMilliseconds=Milliseconds(streamingStart);}
                if(!planValid)return;
                if(requested&&generation!=null){(generation as IDisposable)?.Dispose();generation=null;}
                if(generation==null&&(requested||streamDirty||(autoRefresh&&Time.realtimeSinceStartupAsDouble>=nextRefresh)))
                {
                    nextRefresh=Time.realtimeSinceStartupAsDouble+1;
                    long prepareStart=Timestamp();
                    using(PrepareMarker.Auto())
                    try
                    {
                        world.UpdatePainting();
                        if(world.DetailPaintRuntime==null)return;
                        bool force=requested||streamDirty;requested=false;streamDirty=false;
                        if(!blocked||force)Prepare();
                    }
                    finally
                    {
                        LastPreparationMilliseconds=Milliseconds(prepareStart);
                        PeakPreparationMilliseconds=Math.Max(PeakPreparationMilliseconds,LastPreparationMilliseconds);
                    }
                }
                if(generation!=null)
                {
                    long generationStart=Timestamp();
                    using(GenerateMarker.Auto())
                    try
                    {
                        do
                        {
                            if(!generation.MoveNext())
                            {
                                (generation as IDisposable)?.Dispose();generation=null;
                                status=$"Готово: {instanceCount:N0} экземпляров; загружено {cells.Count}, нужно {wanted.Count} ячеек.\n"+string.Join("\n",messages);
#if UNITY_EDITOR
                                UnityEditor.SceneView.RepaintAll();
#endif
                                break;
                            }
                        }while(Milliseconds(generationStart)<generationMilliseconds);
                    }
                    finally
                    {
                        LastGenerationMilliseconds=Milliseconds(generationStart);
                        PeakGenerationMilliseconds=Math.Max(PeakGenerationMilliseconds,LastGenerationMilliseconds);
                    }
                }
            }
            catch(Exception error)
            {
                (generation as IDisposable)?.Dispose();generation=null;blocked=true;
                status="Генерация остановлена: "+error.Message+"\nИсправьте настройки и нажмите «Перестроить детализацию».";
                Debug.LogWarning(status,this);
            }
        }
        void RemoveCell(Vector2Int key)
        {
            if(!cells.TryGetValue(key,out var cell))return;
            foreach(var group in cell.groups)gpuRenderer?.Remove(group);
            cachedMatrixBytes-=cell.cachedMatrixBytes;
            instanceCount-=cell.count;cells.Remove(key);
        }
        void TrimCache(bool pressure)
        {
            double now=Time.realtimeSinceStartupAsDouble;
            foreach(var key in new List<Vector2Int>(cells.Keys))
            {
                var cell=cells[key];bool keep=wanted.Contains(key);
                if(keep)cell.lastWanted=now;
                else if(pressure||LTDetailStreaming.Expired(false,now,cell.lastWanted,unloadDelay))RemoveCell(key);
            }
        }
        void UpdateStreaming()
        {
            double now=Time.realtimeSinceStartupAsDouble;
            if(now<nextPlan)return;nextPlan=now+.25;
            if(residentCellSize!=cellSize)
            {
                gpuRenderer?.Dispose();gpuRenderer=null;
                (generation as IDisposable)?.Dispose();generation=null;
                cells.Clear();instanceCount=0;wanted.Clear();residentCellSize=cellSize;streamDirty=true;
                cachedMatrixBytes=0;
            }
            cameraPositions.Clear();streamingViews.Clear();
            void AddCamera(Camera camera)
            {
                var p=world.transform.InverseTransformPoint(camera.transform.position);
                var position=new Vector2(p.x,p.z);cameraPositions.Add(position);
                int index=streamingViews.Count;
                if(index==streamingPlaneBuffers.Count)streamingPlaneBuffers.Add(new Plane[6]);
                var frustum=streamingPlaneBuffers[index];GeometryUtility.CalculateFrustumPlanes(camera,frustum);
                streamingViews.Add(new LTDetailStreaming.View{position=position,planes=frustum});
            }
            int count=Camera.allCamerasCount;
            if(cameraBuffer.Length<count)cameraBuffer=new Camera[count];
            count=Camera.GetAllCameras(cameraBuffer);
            for(int i=0;i<count;i++)
            {
                var camera=cameraBuffer[i];
                if(camera&&camera.isActiveAndEnabled&&camera.cameraType==CameraType.Game)
                    AddCamera(camera);
            }
#if UNITY_EDITOR
            if(sceneView)foreach(UnityEditor.SceneView view in UnityEditor.SceneView.sceneViews)
                if(view&&view.camera)AddCamera(view.camera);
#endif
            bool wasValid=planValid;
            planValid=LTDetailStreaming.Plan(cameraPositions,new Vector2(world.source.size.x,world.source.size.z),
                cellSize,maxDistance+preloadDistance,maxCells,planned);
            if(!planValid)
            {
                (generation as IDisposable)?.Dispose();generation=null;
                status="Зона камер превышает Max Cells. Уменьшите Max Distance/Preload Distance или увеличьте Cell Size/Max Cells.";
                return;
            }
            if(!wasValid||!wanted.SetEquals(planned)){streamDirty=true;blocked=false;}
            wanted.Clear();wanted.UnionWith(planned);
            TrimCache(false);
            // Use actual terrain height bounds, not a flat Y=0 rectangle. This is queue
            // priority only, never visibility culling; conservative bounds cannot hide grass.
            float minY=float.PositiveInfinity,maxY=float.NegativeInfinity;
            var toLocal=world.transform.worldToLocalMatrix;
            foreach(var chunk in world.generatedRoot.GetComponentsInChildren<LTChunk>())
            {
                if(!chunk||!chunk.mesh)continue;
                var bounds=TransformBounds(chunk.mesh.bounds,toLocal*chunk.transform.localToWorldMatrix);
                minY=Mathf.Min(minY,bounds.min.y);maxY=Mathf.Max(maxY,bounds.max.y);
            }
            if(float.IsPositiveInfinity(minY)){minY=0;maxY=world.source.size.y;}
            var worldMatrix=world.transform.localToWorldMatrix;
            Bounds PriorityBounds(Vector2Int key)
            {
                if(cells.TryGetValue(key,out var cell)&&cell.count>0)return cell.bounds;
                var rect=LTDetailStreaming.CellRect(key,cellSize,new Vector2(world.source.size.x,world.source.size.z));
                return TransformBounds(new Bounds(new Vector3(rect.center.x,(minY+maxY)*.5f,rect.center.y),
                    new Vector3(rect.width,maxY-minY,rect.height)),worldMatrix);
            }
            LTDetailStreaming.Prioritize(planned,streamingViews,new Vector2(world.source.size.x,world.source.size.z),
                cellSize,maxDistance,PriorityBounds,cells.ContainsKey);
        }
        LTDetailPrefab Recipe(GameObject prefab)
        {
            if(!prefab)return null;
            string key=prefab.GetInstanceID().ToString();
#if UNITY_EDITOR
            string path=UnityEditor.AssetDatabase.GetAssetPath(prefab);
            if(string.IsNullOrEmpty(path)){messages.Add(prefab.name+": нужен префаб из Project, не объект сцены.");return null;}
            key+=UnityEditor.AssetDatabase.GetAssetDependencyHash(path).ToString();
#endif
            if(!recipes.TryGetValue(prefab,out var cache)||cache.key!=key)
            {
                var errors=new List<string>();var warnings=new List<string>();
                LTDetailPrefab.TryBuild(prefab,out var recipe,errors,warnings);
                if(recipe!=null)foreach(var level in recipe.levels)foreach(var part in level.parts)
                {
                    if(!part.material.enableInstancing)errors.Add(part.material.name+": включите Enable GPU Instancing.");
                    if(!part.material.shader||!part.material.shader.isSupported)errors.Add(part.material.name+": шейдер не поддерживается.");
                    if(part.lightProbes==LightProbeUsage.UseProxyVolume||part.lightProbes==LightProbeUsage.CustomProvided)
                        errors.Add("Proxy Volume/Custom Provided probes пока не поддерживаются.");
                }
                cache=new RecipeCache{key=key,recipe=errors.Count==0?recipe:null,
                    message=errors.Count>0?prefab.name+": "+string.Join(" ",errors):warnings.Count>0?prefab.name+": "+string.Join(" ",warnings):null};
                recipes[prefab]=cache;
            }
            if(cache.message!=null&&!messages.Contains(cache.message))messages.Add(cache.message);
            return cache.recipe;
        }
        void Prepare()
        {
            messages.Clear();
            var nearChunks=new List<LTChunk>();
            var chunkKeys=new HashSet<Vector2Int>();
            float chunkWidth=world.source.size.x/world.chunksX,chunkDepth=world.source.size.z/world.chunksZ;
            foreach(var chunk in world.generatedRoot.GetComponentsInChildren<LTChunk>())
            {
                var rect=new Rect(chunk.x*chunkWidth,chunk.z*chunkDepth,chunkWidth,chunkDepth);
                foreach(var key in planned)
                    if(Touches(rect,LTDetailStreaming.CellRect(key,cellSize,new Vector2(world.source.size.x,world.source.size.z))))
                    {nearChunks.Add(chunk);chunkKeys.Add(new Vector2Int(chunk.x,chunk.z));break;}
            }
            var surface=world.DetailPaintRuntime.CaptureDetailSurface(world,chunkKeys);
            if(planned.Count==0)
            {
                terrain.Clear();status="Нет камер рядом с террейном; дальний кэш освобождается с задержкой.";return;
            }
            if(surface.tiles.Count==0){Clear();status="Карты слоёв ещё не готовы.";requested=true;return;}
            var chunks=nearChunks.ToArray();terrain.Update(world,chunks);
            if(Vector3.Distance(world.transform.lossyScale,Vector3.one)>.001f||Quaternion.Angle(world.transform.rotation,Quaternion.identity)>.01f)
                throw new InvalidOperationException("LTWorld должен иметь Rotation=0 и Scale=1. Перенос мира поддерживается.");
            var sources=new List<Source>();var stamps=new List<Stamp>();
            void AddEntries(List<LTDetailEntry> entries,LTSurfaceLayer layer,Stamp stamp,int stampSeed,float multiplier,int order,LTDetailDensityMask commonMask)
            {
                if(entries==null)return;
                // IDs belong to a layer/stamp, not to the whole world. Duplicating a
                // layer legitimately copies its entries; keep the candidate seed stable.
                var ids=new HashSet<string>();
                foreach(var entry in entries)
                {
                    if(entry==null||!entry.enabled||!entry.prefab||entry.density<=0||entry.probability<=0)continue;
                    if(stamp!=null&&(entry.category&stamp.categories)==0)continue;
                    string owner=stamp==null?"layer":stamp.id;
                    if(string.IsNullOrEmpty(entry.Id)||!ids.Add(owner+":"+entry.Id))
                        throw new InvalidOperationException($"Пустой/повторяющийся ID записи внутри {(layer?layer.name:owner)}. Исправьте записи этого слоя/штампа.");
                    var recipe=Recipe(entry.prefab);if(recipe==null)continue;
                    var copy=JsonUtility.FromJson<LTDetailEntry>(JsonUtility.ToJson(entry));
                    copy.ResolveDensityMask(commonMask);
                    if(copy.shaderFade)messages.Add(entry.prefab.name+": Shader Fade пока заменён дискретным отсечением.");
                    float density=entry.density*densityMultiplier*multiplier;
                    int sourceKey=Mix(Mix(recipes[entry.prefab].key.GetHashCode(),seed^stampSeed),Mix(order,density.GetHashCode()));
                    sources.Add(new Source{entry=copy,recipe=recipe,layer=layer,stamp=stamp,owner=owner,seed=seed^stampSeed,order=order,
                        density=density,hash=Mix(JsonUtility.ToJson(copy).GetHashCode(),sourceKey),
                        placementHash=Mix(JsonUtility.ToJson(copy.PlacementSettings()).GetHashCode(),sourceKey)});
                }
            }
            foreach(var layer in surface.layers.Keys)AddEntries(layer.details,layer,null,0,1,-1,layer.detailDensityMask);
            var active=new List<LTDetailStamp>();
            foreach(var s in FindObjectsByType<LTDetailStamp>(FindObjectsInactive.Exclude,FindObjectsSortMode.None))
                if(s.isActiveAndEnabled&&s.world==world)active.Add(s);
            active.Sort((a,b)=>a.priority!=b.priority?a.priority.CompareTo(b.priority):string.CompareOrdinal(a.Id,b.Id));
            var stampIDs=new HashSet<string>();
            foreach(var s in active)
            {
                if(string.IsNullOrEmpty(s.Id)||!stampIDs.Add(s.Id))throw new InvalidOperationException("Повторяющийся ID Detail Stamp. В его инспекторе нажмите «Новый ID этой копии».");
                if(Mathf.Abs(Vector3.Dot(s.transform.up,world.transform.up))<.999f)
                    throw new InvalidOperationException(s.name+": штамп поддерживает поворот вокруг Y, без наклона X/Z.");
                var matrix=world.transform.worldToLocalMatrix*s.transform.localToWorldMatrix;
                Vector2 min=new Vector2(float.PositiveInfinity,float.PositiveInfinity),max=-min;
                for(int x=-1;x<=1;x+=2)for(int z=-1;z<=1;z+=2)
                {var p=matrix.MultiplyPoint3x4(new Vector3(x*s.size.x*.5f,0,z*s.size.y*.5f));var q=new Vector2(p.x,p.z);min=Vector2.Min(min,q);max=Vector2.Max(max,q);}
                var stamp=new Stamp{id=s.Id,rect=Rect.MinMaxRect(min.x,min.y,max.x,max.y),inverse=s.transform.worldToLocalMatrix,
                    size=s.size,ellipse=s.shape==LTStampShape.Ellipse,falloff=s.edgeFalloff,allLayers=s.allLayers,
                    layers=s.allowedLayers.ToArray(),categories=s.categories,mode=s.mode};
                // Area/suppression key only. Entries, seed and density are keyed by Source.
                // Hash before assigning hash itself (zero in this fresh snapshot).
                stamp.hash=Mix(JsonUtility.ToJson(stamp).GetHashCode(),s.priority);
                stamps.Add(stamp);
                if(s.mode!=LTDetailStampMode.Remove)AddEntries(s.details,null,stamp,s.seed,s.densityMultiplier,stamps.Count-1,s.detailDensityMask);
            }
            sources.Sort((a,b)=>string.CompareOrdinal(a.owner+":"+a.entry.Id,b.owner+":"+b.entry.Id));
            var worldMatrix=world.transform.localToWorldMatrix;
            int common=Mix(Mix(seed,densityMultiplier.GetHashCode()),cellSize);common=Mix(common,worldMatrix.GetHashCode());
            common=Mix(common,world.layerHeightBlend.GetHashCode());common=Mix(common,world.triplanarTexturing?1:0);
            common=Mix(common,maxInstances);common=Mix(common,maxCandidatesPerCell);common=Mix(common,rebuildRevision);blocked=false;
            common=Mix(common,matrixCacheMiB);
            generation=Generate(surface,chunks,sources,stamps,common,worldMatrix,new HashSet<Vector2Int>(planned));
            status="Генерация детализации…";
        }
        IEnumerator Generate(LTDetailSurface surface,LTChunk[] chunks,List<Source> sources,List<Stamp> stamps,int common,Matrix4x4 worldMatrix,HashSet<Vector2Int> preparedKeys)
        {
            var roads=new List<LTRoadMath.Snapshot>();
            foreach(var road in world.GetComponentsInChildren<LTRoad>())
                if(road.isActiveAndEnabled&&road.World==world)
                    roads.Add(road.Capture(world));
            var weights=new float[LTPaintRuntime.LayerCapacity];int visits=0;
            var normalMatrix=worldMatrix.inverse.transpose;
            var attempted=new HashSet<Vector2Int>();
            while(true)
            {
                Vector2Int key=default;bool found=false;
                // Re-read current priorities between cells. Finish the current cell rather than
                // restarting it on every turn of the camera. Newly entered territory needs a fresh
                // surface snapshot next pass; never publish empty cells from an old snapshot.
                foreach(var candidate in planned)if(preparedKeys.Contains(candidate)&&!attempted.Contains(candidate)){key=candidate;found=true;break;}
                if(!found)yield break;
                attempted.Add(key);
                var rect=LTDetailStreaming.CellRect(key,cellSize,new Vector2(world.source.size.x,world.source.size.z));
                var localTiles=surface.tiles.FindAll(t=>Touches(rect,t.rect));
                var localRoads=roads.FindAll(r=>r.Intersects(rect));
                var localSources=sources.FindAll(s=>s.stamp!=null?Touches(rect,s.stamp.rect):localTiles.Exists(t=>Array.IndexOf(t.layers,s.layer)>=0));
                int hash=common;
                foreach(var t in localTiles){hash=Mix(hash,t.coverage);hash=Mix(hash,t.surface);}
                foreach(var chunk in chunks)
                {
                    if(!chunk||!chunk.mesh)continue;
                    float w=world.source.size.x/world.chunksX,d=world.source.size.z/world.chunksZ;
                    if(Touches(rect,new Rect(chunk.x*w,chunk.z*d,w,d)))
                    {hash=Mix(hash,chunk.mesh.GetInstanceID());hash=Mix(hash,chunk.updatedAt.GetHashCode());hash=Mix(hash,chunk.transform.localToWorldMatrix.GetHashCode());}
                }
                foreach(var s in stamps)if(Touches(rect,s.rect))hash=Mix(hash,s.hash);
                foreach(var road in localRoads)hash=Mix(hash,road.detailHash);
                int placementHash=hash;
                foreach(var s in localSources){hash=Mix(hash,s.hash);placementHash=Mix(placementHash,s.placementHash);}
                cells.TryGetValue(key,out var old);
                if(old!=null&&old.hash==hash){yield return null;continue;}
                var cell=new Cell{hash=hash,placementHash=placementHash};int candidates=0;
                bool scaleOnly=old!=null&&old.placementHash==placementHash;
                foreach(var source in localSources)
                {
                    if(float.IsNaN(source.density)||float.IsInfinity(source.density)||source.density>maxCandidatesPerCell)
                        throw new InvalidOperationException("Плотность превышает бюджет кандидатов.");
                    var entry=source.entry;var recipe=source.recipe;
                    var previous=scaleOnly?old.groups.Find(g=>g.source.layer==source.layer&&g.source.owner==source.owner&&g.source.entry.Id==entry.Id):null;
                    if(scaleOnly&&previous==null)continue; // Previously empty groups stay empty.
                    if(previous!=null&&previous.source.hash==source.hash)
                    {
                        IncludeGroup(cell,previous);cell.groups.Add(previous);
                        cell.cachedMatrixBytes+=(long)previous.instances.Count*64*previous.partMatrices.Count;
                        continue; // Keep unchanged CPU caches and GPU resources alive.
                    }
                    var group=new Group{source=source,selected=new List<Selection>[recipe.levels.Count],levelFlags=new LevelFlags[recipe.levels.Count]};
                    for(int i=0;i<group.selected.Length;i++)group.selected[i]=new List<Selection>();
                    if(previous!=null)
                    {
                        foreach(var item in previous.instances)
                        {
                            if(++visits%32==0){yield return null;if(!wanted.Contains(key))yield break;}
                            LTDetailMath.DensityMask(entry,item.maskPosition.x,item.maskPosition.y,source.seed,out float maskScale);
                            float scale=Mathf.Lerp(entry.scaleRange.x,entry.scaleRange.y,LTDetailMath.Unit(item.id,3))*maskScale;
                            var matrix=Matrix4x4.TRS(item.position,item.rotation,recipe.rootScale*scale);
                            var bounds=TransformBounds(recipe.bounds,matrix);bounds.Expand(2*entry.deformationBoundsPadding);
                            float rootScale=Mathf.Max(Mathf.Abs(recipe.rootScale.x),Mathf.Max(Mathf.Abs(recipe.rootScale.y),Mathf.Abs(recipe.rootScale.z)));
                            var updated=item;updated.matrix=matrix;updated.bounds=bounds;
                            updated.lodPosition=matrix.MultiplyPoint3x4(recipe.lodReferencePoint);updated.lodSize=recipe.lodSize*scale*rootScale;
                            if(group.instances.Count==0){group.bounds=bounds;group.originBounds=new Bounds(item.position,Vector3.zero);}
                            else{group.bounds.Encapsulate(bounds);group.originBounds.Encapsulate(item.position);}
                            group.instances.Add(updated);
                        }
                        IncludeGroup(cell,group);
                    }
                    else
                    {
                        int count=Mathf.CeilToInt(source.density);
                        for(int z=Mathf.FloorToInt(rect.yMin);z<Mathf.CeilToInt(rect.yMax);z++)
                        for(int x=Mathf.FloorToInt(rect.xMin);x<Mathf.CeilToInt(rect.xMax);x++)for(int i=0;i<count;i++)
                        {
                            if(++visits%32==0)
                            {
                                yield return null;
                                if(!wanted.Contains(key))yield break; // Discard unfinished work after leaving/teleporting.
                            }
                            if(++candidates>maxCandidatesPerCell)throw new InvalidOperationException($"Ячейка {key}: превышен Max Candidates Per Cell.");
                            uint id=LTDetailMath.Candidate(source.seed,source.owner,entry.Id,x,z,i);
                            if(!LTDetailMath.DensityAccept(id,i,source.density))continue;
                            float px=x+LTDetailMath.Unit(id,1),pz=z+LTDetailMath.Unit(id,2);
                            if(!rect.Contains(new Vector2(px,pz)))continue;
                            LTDetailSurface.Tile tile=null;
                            foreach(var t in localTiles)if(t.rect.Contains(new Vector2(px,pz))){tile=t;break;}
                            if(tile==null||!terrain.Sample(px,pz,out float height,out float slope,out var normal))continue;
                            if(slope<entry.slopeRange.x||slope>entry.slopeRange.y)continue;
                            var p=new Vector3(px,height,pz);var position=worldMatrix.MultiplyPoint3x4(p);
                            surface.Weights(tile,p,normal,weights);
                            int slot=source.layer?Array.IndexOf(tile.layers,source.layer):-1;
                            float weight=source.stamp!=null?source.stamp.Weight(position,tile,weights):slot<0?0:weights[slot];
                            for(int s=source.order+1;s<stamps.Count&&weight>0;s++)
                            {
                                var stamp=stamps[s];if((stamp.categories&entry.category)==0)continue;
                                weight=LTDetailMath.Suppress(weight,stamp.Weight(position,tile,weights),stamp.mode==LTDetailStampMode.Add);
                            }
                            weight*=LTDetailMath.DensityMask(entry,px,pz,source.seed,out float maskScale);
                            foreach(var road in localRoads)weight*=1-road.ClearWeight(px,pz,entry.category);
                            if(!LTDetailMath.Accept(id,weight*entry.probability))continue;
                            var normalWS=normalMatrix.MultiplyVector(normal).normalized;
                            float scale=Mathf.Lerp(entry.scaleRange.x,entry.scaleRange.y,LTDetailMath.Unit(id,3));
                            scale*=maskScale; // Shared CPU/GPU transform, bounds and LOD size; generation only.
                            float yaw=Mathf.Lerp(entry.yawRange.x,entry.yawRange.y,LTDetailMath.Unit(id,4));
                            float offset=Mathf.Lerp(entry.heightOffsetRange.x,entry.heightOffsetRange.y,LTDetailMath.Unit(id,5));
                            var rotation=Quaternion.Slerp(Quaternion.identity,Quaternion.FromToRotation(Vector3.up,normalWS),entry.alignToNormal)*Quaternion.Euler(0,yaw,0);
                            var matrix=Matrix4x4.TRS(position+normalWS*offset,rotation,recipe.rootScale*scale);
                            var bounds=TransformBounds(recipe.bounds,matrix);bounds.Expand(2*entry.deformationBoundsPadding);
                            float rootScale=Mathf.Max(Mathf.Abs(recipe.rootScale.x),Mathf.Max(Mathf.Abs(recipe.rootScale.y),Mathf.Abs(recipe.rootScale.z)));
                            var origin=(Vector3)matrix.GetColumn(3);
                            if(group.instances.Count==0){group.bounds=bounds;group.originBounds=new Bounds(origin,Vector3.zero);}
                            else{group.bounds.Encapsulate(bounds);group.originBounds.Encapsulate(origin);}
                            group.instances.Add(new Instance{matrix=matrix,position=origin,bounds=bounds,id=id,rotation=rotation,maskPosition=new Vector2(px,pz),lodPosition=matrix.MultiplyPoint3x4(recipe.lodReferencePoint),lodSize=recipe.lodSize*scale*rootScale});
                            if(cell.count==0){cell.bounds=bounds;cell.originBounds=new Bounds(origin,Vector3.zero);}
                            else{cell.bounds.Encapsulate(bounds);cell.originBounds.Encapsulate(origin);}
                            cell.count++;
                            cells.TryGetValue(key,out var resident);
                            if(instanceCount-(resident?.count??0)+cell.count>maxInstances)TrimCache(true);
                            if(instanceCount-(resident?.count??0)+cell.count>maxInstances)throw new InvalidOperationException("Превышен Max Instances в зоне камер. Уменьшите плотность/радиус или увеличьте лимит.");
                        }
                    }
                    if(group.instances.Count>0)
                    {
                        // Cache unique local transforms across submeshes/LODs while the cell is
                        // still private. Interrupted generation cannot leak cache accounting.
                        long bytes=(long)group.instances.Count*64;
                        foreach(var level in recipe.levels)foreach(var part in level.parts)
                        {
                            cells.TryGetValue(key,out var cacheResident);
                            if(group.partMatrices.ContainsKey(part.localMatrix)||
                                cachedMatrixBytes-(cacheResident?.cachedMatrixBytes??0)+cell.cachedMatrixBytes+bytes>(long)matrixCacheMiB*1024*1024)continue;
                            var matrices=new Matrix4x4[group.instances.Count];
                            for(int i=0;i<matrices.Length;i++)
                            {
                                matrices[i]=group.instances[i].matrix*part.localMatrix;
                                if(++visits%32==0){yield return null;if(!wanted.Contains(key))yield break;}
                            }
                            group.partMatrices.Add(part.localMatrix,matrices);cell.cachedMatrixBytes+=bytes;
                        }
                        cell.groups.Add(group);
                        foreach(var level in recipe.levels)foreach(var part in level.parts)
                            if(part.castShadows!=ShadowCastingMode.Off)
                                group.shadowRange=entry.limitShadowDistance?Mathf.Min(entry.shadowDistance,entry.cullDistance):entry.cullDistance;
                        cell.shadowRange=Mathf.Max(cell.shadowRange,group.shadowRange);
                    }
                }
                if(!wanted.Contains(key))continue;
                cells.TryGetValue(key,out old);
                if(old==null&&cells.Count>=maxCells)TrimCache(true);
                if(old==null&&cells.Count>=maxCells)throw new InvalidOperationException("Превышен лимит загруженных ячеек.");
                cell.lastWanted=Time.realtimeSinceStartupAsDouble;
                cachedMatrixBytes+=cell.cachedMatrixBytes-(old?.cachedMatrixBytes??0);
                if(old!=null)foreach(var group in old.groups)if(!cell.groups.Contains(group))gpuRenderer?.Remove(group);
                instanceCount+=cell.count-(old?.count??0);cells[key]=cell;
                yield return null;
            }
        }
        static void IncludeGroup(Cell cell,Group group)
        {
            if(group.instances.Count==0)return;
            if(cell.count==0){cell.bounds=group.bounds;cell.originBounds=group.originBounds;}
            else{cell.bounds.Encapsulate(group.bounds);cell.originBounds.Encapsulate(group.originBounds);}
            cell.count+=group.instances.Count;cell.shadowRange=Mathf.Max(cell.shadowRange,group.shadowRange);
        }
        static Bounds TransformBounds(Bounds bounds,Matrix4x4 matrix)
        {
            var e=bounds.extents;
            var x=matrix.MultiplyVector(new Vector3(e.x,0,0));var y=matrix.MultiplyVector(new Vector3(0,e.y,0));var z=matrix.MultiplyVector(new Vector3(0,0,e.z));
            return new Bounds(matrix.MultiplyPoint3x4(bounds.center),2*new Vector3(Mathf.Abs(x.x)+Mathf.Abs(y.x)+Mathf.Abs(z.x),
                Mathf.Abs(x.y)+Mathf.Abs(y.y)+Mathf.Abs(z.y),Mathf.Abs(x.z)+Mathf.Abs(y.z)+Mathf.Abs(z.z)));
        }
        void Render(ScriptableRenderContext context,Camera camera)
        {
            if(!this||!isActiveAndEnabled||!world||!world.isActiveAndEnabled||!world.enableLayerPainting||!camera||camera.cameraType==CameraType.Preview)return;
            if(camera.cameraType==CameraType.SceneView&&!sceneView)return;
            ResetRenderStatistics();LastCamera=camera.name;
            long start=Timestamp();
            if(gpuDetails)
            {
                if(gpuRenderer==null)gpuRenderer=new LTDetailGpuRenderer();
                gpuRenderer.Begin(gpuMemoryMiB,Application.isPlaying?Time.frameCount:gpuEditorFrame,gpuUploadMilliseconds,gpuUploadMiB);
            }
            else if(gpuRenderer!=null){gpuRenderer.Dispose();gpuRenderer=null;}
            batchQueue.Begin();submissionCamera=camera;
            using(RenderMarker.Auto())
            try{RenderCamera(camera);}
            finally
            {
                batchQueue.Begin();submissionCamera=null;
                LastRenderMilliseconds=Milliseconds(start);
                PeakRenderMilliseconds=Math.Max(PeakRenderMilliseconds,LastRenderMilliseconds);
            }
        }
        static bool CanDrawPart(LTDetailPrefab.Part part,int cameraMask)
            =>part.mesh&&part.material&&part.material.enableInstancing&&(cameraMask&(1<<part.layer))!=0;
        void SkipInstances(int count)
        {LastEarlySkippedInstances+=count;LastCulledInstances+=count;}
        void RenderCamera(Camera camera)
        {
            GeometryUtility.CalculateFrustumPlanes(camera,planes);
            var position=camera.transform.position;var forward=camera.transform.forward;
            int cameraMask=camera.cullingMask;bool orthographic=camera.orthographic;
            float orthoSize=camera.orthographicSize,fov=camera.fieldOfView,lodBias=QualitySettings.lodBias;
            int maximumLOD=QualitySettings.maximumLODLevel;
            foreach(var pair in cells)
            {
                var cell=pair.Value;
                LastTestedCells++;
                if(cell.count==0||LTDetailMath.BoundsDistanceSquared(cell.bounds,position)>maxDistance*maxDistance||
                    LTDetailMath.BoundsDistanceSquared(cell.originBounds,position)>=maxDistance*maxDistance)
                {LastCulledCells++;SkipInstances(cell.count);continue;}
                bool cellInView=GeometryUtility.TestPlanesAABB(planes,cell.bounds);
                if(LTDetailMath.CanSkipOffscreen(cellInView,cell.originBounds,position,cell.shadowRange,maxDistance))
                {LastCulledCells++;SkipInstances(cell.count);continue;}
                foreach(var group in cell.groups)
                {
                    var entry=group.source.entry;var recipe=group.source.recipe;
                    float end=Mathf.Min(maxDistance,entry.cullDistance);
                    if(end<=0||LTDetailMath.BoundsDistanceSquared(group.originBounds,position)>=end*end)
                    {SkipInstances(group.instances.Count);continue;}
                    bool groupInView=cellInView&&GeometryUtility.TestPlanesAABB(planes,group.bounds);
                    if(LTDetailMath.CanSkipOffscreen(groupInView,group.originBounds,position,group.shadowRange,maxDistance))
                    {SkipInstances(group.instances.Count);continue;}
                    bool anyColor=false,anyCaster=false;
                    if(gpuDetails&&gpuRenderer!=null&&gpuRenderer.TryRender(group,camera,planes,maxDistance))continue;
                    for(int lod=0;lod<recipe.levels.Count;lod++)
                    {
                        group.selected[lod].Clear();var flags=new LevelFlags();
                        foreach(var part in recipe.levels[lod].parts)
                            if(CanDrawPart(part,cameraMask))
                            {
                                flags.color|=part.castShadows!=ShadowCastingMode.ShadowsOnly;
                                flags.caster|=part.castShadows!=ShadowCastingMode.Off;
                            }
                        group.levelFlags[lod]=flags;anyColor|=flags.color;anyCaster|=flags.caster;
                    }
                    if((!anyColor&&!anyCaster)||(!groupInView&&!anyCaster))
                    {SkipInstances(group.instances.Count);continue;}
                    for(int i=0;i<group.instances.Count;i++)
                    {
                        LastTestedInstances++;
                        var instance=group.instances[i];float distanceSquared=(position-instance.position).sqrMagnitude;
                        if(distanceSquared>=end*end){LastCulledInstances++;continue;}
                        // Distance, frustum and shadow range are computed ONCE per instance/camera.
                        if(entry.thinInDistance&&LTDetailMath.Unit(instance.id,27)>=Mathf.Lerp(1,entry.farDensity,
                            Mathf.InverseLerp(Mathf.Min(entry.fadeStart,end),end,Mathf.Sqrt(distanceSquared))))
                        {LastCulledInstances++;continue;}
                        bool shadowsInRange=LTDetailMath.ShadowsInRange(entry,distanceSquared);
                        bool inView=groupInView&&GeometryUtility.TestPlanesAABB(planes,instance.bounds);
                        if(!inView&&!(anyCaster&&shadowsInRange)){LastCulledInstances++;continue;}
                        int lod=0;
                        if(recipe.hasLODGroup)
                        {
                            float screen=LTDetailMath.ScreenHeight(instance.lodSize,Vector3.Dot(instance.lodPosition-position,forward),orthographic,orthoSize,fov,lodBias);
                            for(;lod<recipe.levels.Count;lod++)if(screen>=recipe.levels[lod].screenRelativeHeight)break;
                            if(lod==recipe.levels.Count){LastCulledInstances++;continue;}
                            lod=Mathf.Min(recipe.levels.Count-1,Mathf.Max(lod,maximumLOD));
                        }
                        var flags=group.levelFlags[lod];
                        if(!(inView&&flags.color)&&!(shadowsInRange&&flags.caster))
                        {LastCulledInstances++;continue;}
                        group.selected[lod].Add(new Selection{index=i,inView=inView,shadowsInRange=shadowsInRange});
                        LastSelectedInstances++;
                    }
                    long submitStart=Timestamp();
                    using(SubmitMarker.Auto())
                    try{SubmitGroup(camera,cameraMask,group,pair.Key);}
                    finally{LastSubmissionMilliseconds+=Milliseconds(submitStart);}
                }
            }
            long flushStart=Timestamp();
            using(SubmitMarker.Auto())
            try{batchQueue.Flush(emitMerged);}
            finally{LastSubmissionMilliseconds+=Milliseconds(flushStart);}
        }
        void SubmitGroup(Camera camera,int cameraMask,Group group,Vector2Int cellKey)
        {
            var entry=group.source.entry;var recipe=group.source.recipe;
            for(int lod=0;lod<recipe.levels.Count;lod++)foreach(var part in recipe.levels[lod].parts)
            {
                if(group.selected[lod].Count==0||!CanDrawPart(part,cameraMask))continue;
                group.partMatrices.TryGetValue(part.localMatrix,out var matrices);
                // Transparent sorting is sensitive to grouping: retain the original path.
                bool merge=combineDrawBatches&&part.material.renderQueue<(int)RenderQueue.Transparent;
                var rp=new RenderParams(part.material){camera=camera,worldBounds=group.bounds,layer=part.layer,
                    renderingLayerMask=part.renderingLayerMask,shadowCastingMode=part.castShadows,receiveShadows=part.receiveShadows,
                    lightProbeUsage=part.lightProbes,reflectionProbeUsage=part.reflectionProbes,motionVectorMode=part.motionVectors};
                // Shadow casting is a draw-level flag: partition instances, never the whole cell.
                int batches=entry.limitShadowDistance&&part.castShadows!=ShadowCastingMode.Off?2:1;
                for(int batch=0;batch<batches;batch++)
                {
                    var mode=batch==0?part.castShadows:ShadowCastingMode.Off;
                    // A ShadowsOnly part must not suddenly become visible after its shadows expire.
                    if(part.castShadows==ShadowCastingMode.ShadowsOnly&&mode==ShadowCastingMode.Off)continue;
                    rp.shadowCastingMode=mode;
                    int count=0,accepted=0;
                    var key=new DrawKey{part=part,state=new LTDetailDrawState{
                        mesh=part.mesh.GetInstanceID(),material=part.material.GetInstanceID(),submesh=part.submesh,
                        layer=part.layer,shadow=(int)mode,receiveShadows=part.receiveShadows,renderingLayerMask=part.renderingLayerMask,
                        lightProbes=(int)part.lightProbes,reflectionProbes=(int)part.reflectionProbes,motionVectors=(int)part.motionVectors,
                        regionX=cellKey.x>>1,regionZ=cellKey.y>>1}};
                    foreach(var selected in group.selected[lod])
                    {
                        if(LTDetailMath.ShadowMode(part.castShadows,selected.shadowsInRange)!=mode||
                            !LTDetailMath.SubmitPart(part.castShadows,selected.inView,selected.shadowsInRange))continue;
                        var instance=group.instances[selected.index];
                        var matrix=matrices!=null?matrices[selected.index]:instance.matrix*part.localMatrix;
                        accepted++;
                        if(merge){batchQueue.Add(key,matrix,instance.bounds,emitMerged);continue;}
                        drawMatrices[count++]=matrix;
                        if(count==drawMatrices.Length){SubmitBatch(rp,part,count);count=0;}
                    }
                    if(count>0)SubmitBatch(rp,part,count);
                    LastUnmergedDrawCalls+=(accepted+drawMatrices.Length-1)/drawMatrices.Length;
                }
            }
        }
        void SubmitBatch(RenderParams parameters,LTDetailPrefab.Part part,int count)
        {
            DrawInstances(parameters,part,drawMatrices,count);
            LastDrawCalls++;LastSubmittedInstances+=count;
            if(parameters.shadowCastingMode!=ShadowCastingMode.Off)LastShadowSubmittedInstances+=count;
        }
        void SubmitMerged(LTDetailBatchQueue<DrawKey>.Batch batch)
        {
            var part=batch.key.part;var state=batch.key.state;
            var parameters=new RenderParams(part.material){camera=submissionCamera,worldBounds=batch.bounds,layer=state.layer,
                renderingLayerMask=state.renderingLayerMask,shadowCastingMode=(ShadowCastingMode)state.shadow,receiveShadows=state.receiveShadows,
                lightProbeUsage=(LightProbeUsage)state.lightProbes,reflectionProbeUsage=(ReflectionProbeUsage)state.reflectionProbes,
                motionVectorMode=(MotionVectorGenerationMode)state.motionVectors};
            DrawInstances(parameters,part,batch.matrices,batch.count);
            LastDrawCalls++;LastSubmittedInstances+=batch.count;
            if(parameters.shadowCastingMode!=ShadowCastingMode.Off)LastShadowSubmittedInstances+=batch.count;
        }
        void DrawInstances(RenderParams parameters,LTDetailPrefab.Part part,Matrix4x4[] matrices,int count)
        {
            // Static instances must provide explicit transform history. The matrix-only
            // path produced false motion and flicker in HDRP (confirmed by scene A/B).
            // Vertex animation remains shader-owned; do not enable Precomputed Velocity.
            if(motionData==null)motionData=new LTDetailMotionData[drawMatrices.Length];
            LTDetailMotionData.FillStatic(matrices,motionData,count);
            using(GraphicsMarker.Auto())
                Graphics.RenderMeshInstanced(parameters,part.mesh,part.submesh,motionData,count);
        }
    }
}
