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
        public string Status=>status;
        public int InstanceCount=>instanceCount;
        public int CellCount=>cells.Count;
        public int LastDrawCalls{get;private set;}
        public int LastSubmittedInstances{get;private set;}
        public string LastCamera{get;private set;}
        string status="Ожидание поверхности.";
        LTWorld world;
        readonly LTPaintTerrain terrain=new LTPaintTerrain();
        readonly Dictionary<Vector2Int,Cell> cells=new Dictionary<Vector2Int,Cell>();
        readonly Dictionary<GameObject,RecipeCache> recipes=new Dictionary<GameObject,RecipeCache>();
        readonly Plane[] planes=new Plane[6];
        // Conservative Unity limit including object-to-world and inverse matrices.
        readonly Matrix4x4[] drawMatrices=new Matrix4x4[511];
        readonly List<string> messages=new List<string>();
        readonly List<Vector2> cameraPositions=new List<Vector2>();
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
            public Stamp stamp;public string owner;public int seed,order,hash;public float density;
        }
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
            public Matrix4x4 matrix;public Bounds bounds;public Vector3 lodPosition;public float lodSize;public uint id;
        }
        sealed class Group
        {
            public Source source;
            public readonly List<Instance> instances=new List<Instance>();
            public List<int>[] selected;
        }
        sealed class Cell
        {
            public int hash,count;public double lastWanted;public bool shadows;public Bounds bounds;public readonly List<Group> groups=new List<Group>();
        }
        static int Mix(int a,int b)=>unchecked(a*397^b);
        static bool Touches(Rect a,Rect b)=>a.xMin<b.xMax&&a.xMax>b.xMin&&a.yMin<b.yMax&&a.yMax>b.yMin;
        void OnEnable()
        {
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
            (generation as IDisposable)?.Dispose();generation=null;cells.Clear();recipes.Clear();terrain.Clear();instanceCount=0;
            planned.Clear();wanted.Clear();nextPlan=0;streamDirty=true;
            if(world)world.DetailPaintRuntime?.ReleaseDetailSnapshots();
            LastDrawCalls=0;LastSubmittedInstances=0;
        }
        void OnValidate()
        {
            cellSize=Mathf.Max(1,cellSize);maxCells=Mathf.Max(1,maxCells);maxInstances=Mathf.Max(1,maxInstances);
            maxCandidatesPerCell=Mathf.Max(1,maxCandidatesPerCell);maxDistance=Mathf.Max(1,maxDistance);
            densityMultiplier=Mathf.Max(0,densityMultiplier);generationMilliseconds=Mathf.Clamp(generationMilliseconds,.25f,20);requested=true;
            preloadDistance=Mathf.Max(0,preloadDistance);unloadDelay=Mathf.Max(0,unloadDelay);nextPlan=0;
        }
        [ContextMenu("Перестроить детализацию")]
        public void Rebuild(){requested=true;blocked=false;recipes.Clear();rebuildRevision++;}
        void Update(){if(Application.isPlaying)Tick();}
#if UNITY_EDITOR
        void EditorTick()
        {
            if(!this||!isActiveAndEnabled||Application.isPlaying||UnityEditor.EditorApplication.isCompiling||GUIUtility.hotControl!=0)return;
            Tick();
        }
#endif
        void Tick()
        {
            if(!world||!world.isActiveAndEnabled||!world.source||!world.generatedRoot||!world.enableLayerPainting)
            {if(cells.Count>0||generation!=null)Clear();status="Нужен активный LTWorld с геометрией и покраской слоёв.";requested=true;return;}
            if(!SystemInfo.supportsInstancing){status="GPU Instancing не поддерживается.";return;}
            try
            {
                UpdateStreaming();
                if(!planValid)return;
                if(requested&&generation!=null){(generation as IDisposable)?.Dispose();generation=null;}
                if(generation==null&&(requested||streamDirty||(autoRefresh&&Time.realtimeSinceStartupAsDouble>=nextRefresh)))
                {
                    nextRefresh=Time.realtimeSinceStartupAsDouble+1;
                    world.UpdatePainting();
                    if(world.DetailPaintRuntime==null)return;
                    bool force=requested||streamDirty;requested=false;streamDirty=false;
                    if(!blocked||force)Prepare();
                }
                if(generation!=null)
                {
                    var timer=System.Diagnostics.Stopwatch.StartNew();
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
                    }while(timer.Elapsed.TotalMilliseconds<generationMilliseconds);
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
                (generation as IDisposable)?.Dispose();generation=null;
                cells.Clear();instanceCount=0;wanted.Clear();residentCellSize=cellSize;streamDirty=true;
            }
            cameraPositions.Clear();
            int count=Camera.allCamerasCount;
            if(cameraBuffer.Length<count)cameraBuffer=new Camera[count];
            count=Camera.GetAllCameras(cameraBuffer);
            for(int i=0;i<count;i++)
            {
                var camera=cameraBuffer[i];
                if(camera&&camera.isActiveAndEnabled&&camera.cameraType==CameraType.Game)
                {var p=world.transform.InverseTransformPoint(camera.transform.position);cameraPositions.Add(new Vector2(p.x,p.z));}
            }
#if UNITY_EDITOR
            if(sceneView)foreach(UnityEditor.SceneView view in UnityEditor.SceneView.sceneViews)
                if(view&&view.camera){var p=world.transform.InverseTransformPoint(view.camera.transform.position);cameraPositions.Add(new Vector2(p.x,p.z));}
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
            var sources=new List<Source>();var stamps=new List<Stamp>();var ids=new HashSet<string>();
            void AddEntries(List<LTDetailEntry> entries,LTSurfaceLayer layer,Stamp stamp,int stampSeed,float multiplier,int order,LTDetailDensityMask commonMask)
            {
                if(entries==null)return;
                foreach(var entry in entries)
                {
                    if(entry==null||!entry.enabled||!entry.prefab||entry.density<=0||entry.probability<=0)continue;
                    if(stamp!=null&&(entry.category&stamp.categories)==0)continue;
                    string owner=stamp==null?"layer":stamp.id;
                    if(string.IsNullOrEmpty(entry.Id)||!ids.Add(owner+":"+entry.Id))
                        throw new InvalidOperationException("Пустой/повторяющийся ID записи. Удалите и добавьте скопированную запись заново.");
                    var recipe=Recipe(entry.prefab);if(recipe==null)continue;
                    var copy=JsonUtility.FromJson<LTDetailEntry>(JsonUtility.ToJson(entry));
                    copy.ResolveDensityMask(commonMask);
                    if(copy.shaderFade)messages.Add(entry.prefab.name+": Shader Fade пока заменён дискретным отсечением.");
                    sources.Add(new Source{entry=copy,recipe=recipe,layer=layer,stamp=stamp,owner=owner,seed=seed^stampSeed,order=order,
                        density=entry.density*densityMultiplier*multiplier,
                        hash=Mix(JsonUtility.ToJson(copy).GetHashCode(),recipes[entry.prefab].key.GetHashCode())});
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
                    layers=s.allowedLayers.ToArray(),categories=s.categories,mode=s.mode,
                    hash=Mix(JsonUtility.ToJson(s).GetHashCode(),matrix.GetHashCode())};
                stamps.Add(stamp);
                if(s.mode!=LTDetailStampMode.Remove)AddEntries(s.details,null,stamp,s.seed,s.densityMultiplier,stamps.Count-1,s.detailDensityMask);
            }
            sources.Sort((a,b)=>string.CompareOrdinal(a.owner+":"+a.entry.Id,b.owner+":"+b.entry.Id));
            var worldMatrix=world.transform.localToWorldMatrix;
            int common=Mix(Mix(seed,densityMultiplier.GetHashCode()),cellSize);common=Mix(common,worldMatrix.GetHashCode());
            common=Mix(common,world.layerHeightBlend.GetHashCode());common=Mix(common,world.triplanarTexturing?1:0);
            common=Mix(common,maxInstances);common=Mix(common,maxCandidatesPerCell);common=Mix(common,rebuildRevision);blocked=false;
            generation=Generate(surface,chunks,sources,stamps,common,worldMatrix);
            status="Генерация детализации…";
        }
        IEnumerator Generate(LTDetailSurface surface,LTChunk[] chunks,List<Source> sources,List<Stamp> stamps,int common,Matrix4x4 worldMatrix)
        {
            var weights=new float[8];int visits=0;
            var normalMatrix=worldMatrix.inverse.transpose;
            var attempted=new HashSet<Vector2Int>();
            while(true)
            {
                Vector2Int key=default;bool found=false;
                foreach(var candidate in planned)if(!attempted.Contains(candidate)){key=candidate;found=true;break;}
                if(!found)yield break;
                attempted.Add(key);
                var rect=LTDetailStreaming.CellRect(key,cellSize,new Vector2(world.source.size.x,world.source.size.z));
                var localTiles=surface.tiles.FindAll(t=>Touches(rect,t.rect));
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
                foreach(var s in localSources)hash=Mix(hash,s.hash);
                foreach(var s in stamps)if(Touches(rect,s.rect))hash=Mix(hash,s.hash);
                cells.TryGetValue(key,out var old);
                if(old!=null&&old.hash==hash){yield return null;continue;}
                var cell=new Cell{hash=hash};int candidates=0;
                foreach(var source in localSources)
                {
                    if(float.IsNaN(source.density)||float.IsInfinity(source.density)||source.density>maxCandidatesPerCell)
                        throw new InvalidOperationException("Плотность превышает бюджет кандидатов.");
                    var entry=source.entry;var recipe=source.recipe;
                    var group=new Group{source=source,selected=new List<int>[recipe.levels.Count]};
                    for(int i=0;i<group.selected.Length;i++)group.selected[i]=new List<int>();
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
                        weight*=LTDetailMath.DensityMask(entry,px,pz,source.seed);
                        if(!LTDetailMath.Accept(id,weight*entry.probability))continue;
                        var normalWS=normalMatrix.MultiplyVector(normal).normalized;
                        float scale=Mathf.Lerp(entry.scaleRange.x,entry.scaleRange.y,LTDetailMath.Unit(id,3));
                        float yaw=Mathf.Lerp(entry.yawRange.x,entry.yawRange.y,LTDetailMath.Unit(id,4));
                        float offset=Mathf.Lerp(entry.heightOffsetRange.x,entry.heightOffsetRange.y,LTDetailMath.Unit(id,5));
                        var rotation=Quaternion.Slerp(Quaternion.identity,Quaternion.FromToRotation(Vector3.up,normalWS),entry.alignToNormal)*Quaternion.Euler(0,yaw,0);
                        var matrix=Matrix4x4.TRS(position+normalWS*offset,rotation,recipe.rootScale*scale);
                        var bounds=TransformBounds(recipe.bounds,matrix);bounds.Expand(2*entry.deformationBoundsPadding);
                        float rootScale=Mathf.Max(Mathf.Abs(recipe.rootScale.x),Mathf.Max(Mathf.Abs(recipe.rootScale.y),Mathf.Abs(recipe.rootScale.z)));
                        group.instances.Add(new Instance{matrix=matrix,bounds=bounds,id=id,lodPosition=matrix.MultiplyPoint3x4(recipe.lodReferencePoint),lodSize=recipe.lodSize*scale*rootScale});
                        if(cell.count==0)cell.bounds=bounds;else cell.bounds.Encapsulate(bounds);
                        cell.count++;
                        cells.TryGetValue(key,out var resident);
                        if(instanceCount-(resident?.count??0)+cell.count>maxInstances)TrimCache(true);
                        if(instanceCount-(resident?.count??0)+cell.count>maxInstances)throw new InvalidOperationException("Превышен Max Instances в зоне камер. Уменьшите плотность/радиус или увеличьте лимит.");
                    }
                    if(group.instances.Count>0)
                    {
                        cell.groups.Add(group);
                        foreach(var level in recipe.levels)foreach(var part in level.parts)
                            if(part.castShadows!=ShadowCastingMode.Off)cell.shadows=true;
                    }
                }
                if(!wanted.Contains(key))continue;
                cells.TryGetValue(key,out old);
                if(old==null&&cells.Count>=maxCells)TrimCache(true);
                if(old==null&&cells.Count>=maxCells)throw new InvalidOperationException("Превышен лимит загруженных ячеек.");
                cell.lastWanted=Time.realtimeSinceStartupAsDouble;
                instanceCount+=cell.count-(old?.count??0);cells[key]=cell;
                yield return null;
            }
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
            LastDrawCalls=0;LastSubmittedInstances=0;LastCamera=camera.name;
            GeometryUtility.CalculateFrustumPlanes(camera,planes);
            var position=camera.transform.position;var forward=camera.transform.forward;
            foreach(var cell in cells.Values)
            {
                if(cell.count==0||cell.bounds.SqrDistance(position)>maxDistance*maxDistance)continue;
                if(!cell.shadows&&!GeometryUtility.TestPlanesAABB(planes,cell.bounds))continue;
                foreach(var group in cell.groups)
                {
                    var entry=group.source.entry;var recipe=group.source.recipe;
                    foreach(var selected in group.selected)selected.Clear();
                    float end=Mathf.Min(maxDistance,entry.cullDistance);
                    for(int i=0;i<group.instances.Count;i++)
                    {
                        var instance=group.instances[i];float distance=Vector3.Distance(position,instance.matrix.GetColumn(3));
                        if(distance>=end)continue;
                        if(entry.thinInDistance&&LTDetailMath.Unit(instance.id,27)>=Mathf.Lerp(1,entry.farDensity,Mathf.InverseLerp(Mathf.Min(entry.fadeStart,end),end,distance)))continue;
                        float screen=LTDetailMath.ScreenHeight(instance.lodSize,Vector3.Dot(instance.lodPosition-position,forward),camera.orthographic,camera.orthographicSize,camera.fieldOfView,QualitySettings.lodBias);
                        int lod=0;
                        if(recipe.hasLODGroup)
                        {
                            for(;lod<recipe.levels.Count;lod++)if(screen>=recipe.levels[lod].screenRelativeHeight)break;
                            if(lod==recipe.levels.Count)continue;
                            lod=Mathf.Min(recipe.levels.Count-1,Mathf.Max(lod,QualitySettings.maximumLODLevel));
                        }
                        group.selected[lod].Add(i);
                    }
                    for(int lod=0;lod<recipe.levels.Count;lod++)foreach(var part in recipe.levels[lod].parts)
                    {
                        if(!part.mesh||!part.material||!part.material.enableInstancing||(camera.cullingMask&(1<<part.layer))==0)continue;
                        var rp=new RenderParams(part.material){camera=camera,worldBounds=cell.bounds,layer=part.layer,
                            renderingLayerMask=part.renderingLayerMask,shadowCastingMode=part.castShadows,receiveShadows=part.receiveShadows,
                            lightProbeUsage=part.lightProbes,reflectionProbeUsage=part.reflectionProbes,motionVectorMode=part.motionVectors};
                        int count=0;
                        foreach(int index in group.selected[lod])
                        {
                            var instance=group.instances[index];
                            // Offscreen casters must remain submitted for potentially visible shadows.
                            if(part.castShadows==ShadowCastingMode.Off&&!GeometryUtility.TestPlanesAABB(planes,instance.bounds))continue;
                            drawMatrices[count++]=instance.matrix*part.localMatrix;
                            if(count==drawMatrices.Length){Graphics.RenderMeshInstanced(rp,part.mesh,part.submesh,drawMatrices,count);LastDrawCalls++;LastSubmittedInstances+=count;count=0;}
                        }
                        if(count>0){Graphics.RenderMeshInstanced(rp,part.mesh,part.submesh,drawMatrices,count);LastDrawCalls++;LastSubmittedInstances+=count;}
                    }
                }
            }
        }
    }
}
