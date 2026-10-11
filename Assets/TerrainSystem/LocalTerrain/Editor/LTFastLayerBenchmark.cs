using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    // Real HDRP camera, real masks/textures, fixed meshes. Temporary materials and
    // hiding are scoped to one camera callback, never serialized into scene/assets.
    public sealed class LTFastLayerBenchmark : EditorWindow
    {
        [Serializable] sealed class Phase
        {
            public string mode;
            public int index,invalidObservations,duplicateFrames,verifiedBindings,renders;
            public bool topTwo=true,beforeHeight;
            public double medianMs,p95Ms;
            public List<double> gpuMs=new List<double>();
            public List<int> engineFrames=new List<int>();
        }
        [Serializable] sealed class ChunkInfo
        {
            public string name,reason,shader,keywords;
            public int instanceId,vertices;
            public Vector3 center,size;
            public float distance;
        }
        [Serializable] sealed class Report
        {
            public string utc,status,unity,gpu,api,scene,worldSettings,authoredMode,notes,siteChunk,searchExclusions;
            public int width,height,vSync,targetFps,selectedChunks,hiddenRenderers,detailCallbacks,probeHits,probeTotal;
            public float blendBudget,projectionCutoff,siteViewSize;
            public int overlapHits,minimumLayers,maximumLayers,searchMaximumLayers,siteAttempts;
            public Vector3 siteFocus;
            public Vector2 renderScale;
            public Vector3 cameraPosition;
            public Quaternion cameraRotation;
            public bool authoredTopTwo,authoredBeforeHeight,authoredColorRemainder,completed,protectedFilesUnchanged,sceneStateUnchanged,restored=true,cameraRestored=true;
            public List<ChunkInfo> chunks=new List<ChunkInfo>();
            public List<Phase> phases=new List<Phase>();
            public List<string> changes=new List<string>();
        }
        sealed class Draw
        {
            public LTChunk chunk;
            public MeshRenderer renderer;
            public MeshFilter filter;
            public Mesh mesh;
            public Matrix4x4 matrix;
            public double updated;
            public Material a,b,assigned;
            public Material[] originals;
        }
        struct Hidden { public Renderer renderer; public bool before; }
        static LTFastLayerBenchmark active;
        [SerializeField] LTWorld world;
        [SerializeField] string reportPath;
        string message="Тест сам найдёт смешивание 4–5 слоёв и наведёт Scene View на землю.";
        SceneView view;
        Camera target;
        Matrix4x4 pose,projection;
        readonly List<Draw> draws=new List<Draw>();
        readonly List<Hidden> hidden=new List<Hidden>();
        Renderer[] others;
        Report report;
        Dictionary<string,string> hashes;
        (Scene scene,bool dirty,int roots)[] scenes;
        CustomSampler[] samplers;
        Recorder[] recorders;
        CommandBuffer commands;
        bool running,scopeOpen,renderOverride,ownedFreeze,pending,advance,preparing;
        LTFastLayerBenchmarkView cameraLease;
        List<LTFastLayerBenchmarkSite> sites;
        LTFastLayerBenchmarkSite site;
        int siteIndex,framingAttempt,settledRenders;
        double preparationStarted;
        int phase,scopePhase,rendered,lastFrame,observedFrame;
        double phaseStarted,lastRender,nextUi,warmStarted;
        static readonly string[] Passes={"GBuffer","Forward","DepthOnly","ShadowCaster"};
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        bool BeforeHeight=>LTFastLayerBenchmarkPolicy.BeforeHeight(phase);

        [MenuItem("Tools/Local Terrain/Fast Layers A-B (isolated Scene View)")]
        public static void Open()=>Open(null);
        public static void Open(LTWorld selected)
        {
            var window=GetWindow<LTFastLayerBenchmark>("Live Layers A/B");
            if(!window.running&&!window.pending&&selected)window.world=selected;
            window.Show();
        }
        void OnEnable()
        {
            minSize=new Vector2(440,320);
            EditorApplication.update+=Tick;
            EditorApplication.hierarchyChanged+=HierarchyChanged;
            AssemblyReloadEvents.beforeAssemblyReload+=Reload;
            EditorApplication.quitting+=Reload;
            EditorApplication.playModeStateChanged+=PlayMode;
            EditorSceneManager.sceneSaving+=Saving;
            Undo.undoRedoPerformed+=Edited;
            Undo.postprocessModifications+=Modified;
        }
        void OnDisable()
        {
            Finish("Отменено: окно закрыто.",false);Publish();
            EditorApplication.update-=Tick;
            EditorApplication.hierarchyChanged-=HierarchyChanged;
            AssemblyReloadEvents.beforeAssemblyReload-=Reload;
            EditorApplication.quitting-=Reload;
            EditorApplication.playModeStateChanged-=PlayMode;
            EditorSceneManager.sceneSaving-=Saving;
            Undo.undoRedoPerformed-=Edited;
            Undo.postprocessModifications-=Modified;
        }
        void Reload(){Finish("Отменено: перезагрузка редактора/скриптов.",false);Publish();}
        void Saving(Scene _,string __){Finish("Отменено: пользователь сохраняет сцену.",false);Publish();}
        void PlayMode(PlayModeStateChange _)=>Finish("Отменено: смена Play Mode.",false);
        void Edited()=>Finish("Отменено: Undo/Redo.",false);
        void HierarchyChanged()=>Finish("Отменено: изменилась иерархия сцены.",false);
        UndoPropertyModification[] Modified(UndoPropertyModification[] changes)
        {if(changes.Length>0)Finish("Отменено: редактирование во время теста.",false);return changes;}
        void OnGUI()
        {
            EditorGUILayout.HelpBox("Автопоиск смешивания 4–5 слоёв. Быстрый режим и Top Two включены в обеих фазах. Выбор пары: ПОСЛЕ высот → ДО высот → ДО высот → ПОСЛЕ высот. ≥2 с / 60 кадров прогрева и 120 GPU-наблюдений на этап. Не двигайте камеру и не меняйте размер Scene View.",MessageType.Info);
            using(new EditorGUI.DisabledScope(running||pending))
            {
                world=(LTWorld)EditorGUILayout.ObjectField("LT World",world,typeof(LTWorld),true);
                if(GUILayout.Button("Взять выбранный LT World"))world=Selection.activeGameObject?Selection.activeGameObject.GetComponentInParent<LTWorld>():null;
                if(GUILayout.Button("Найти участок и начать A/B",GUILayout.Height(30)))StartTest();
            }
            if(running&&GUILayout.Button("Отменить и восстановить"))Finish("Отменено пользователем.",false);
            EditorGUILayout.HelpBox(message,MessageType.Info);
            EditorGUILayout.HelpBox("Поиск по картам весов: 4–5 слоёв ≥3% до Height Blend. Проверка сетки 21×21: земля ≥98% кадра, смешивание ≥80%. Камера смотрит сверху; после теста её положение восстановится. Дороги учитываются; displacement исключён из автоподбора камеры. Остальные Renderer и LT-растительность скрываются для этой камеры, дальний фон выключен. Это GPU-время камеры с HDRP, не FPS билда. Сцена не сохраняется.",MessageType.None);
            if(!string.IsNullOrEmpty(reportPath)&&File.Exists(reportPath)&&GUILayout.Button("Показать отчёт"))EditorUtility.RevealInFinder(reportPath);
        }
        static void Require(bool value,string why){if(!value)throw new InvalidOperationException(why);}
        static string Hash(string path)
        {using var stream=File.OpenRead(path);using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(stream));}
        static bool Profiling()=>ProfilerDriver.enabled||Profiler.enabled||Profiler.enableBinaryLog||ProfilerDriver.deepProfiling||ProfilerDriver.memoryRecordMode!=ProfilerMemoryRecordMode.None;
        void StartTest()
        {
            if(running||pending)return;
            report=null;hashes=null;scenes=null;reportPath=null;
            try
            {
                Require(!active,"Другой тест быстрых слоёв уже запущен.");
                Require(!EditorApplication.isPlayingOrWillChangePlaymode&&!EditorApplication.isCompiling&&!EditorApplication.isUpdating,"Нужен Edit Mode без импорта/компиляции.");
                Require(!Profiling()&&!LTDetailBenchmarkIsolation.OtherDiagnosticActive,"Остановите Profiler, запись стэков и F8.");
                Require(SystemInfo.supportsGpuRecorder,"GPU Recorder не поддерживается. FPS вместо GPU-времени не подставляется.");
                Require(world&&world.isActiveAndEnabled&&world.generatedRoot&&world.enableLayerPainting&&!world.paintBenchmarkRunning,"Выберите готовый LT World без другого теста.");
                Require(!world.PaintingPending,"Дождитесь завершения покраски.");
                Require(!world.showDisplacementCoverage&&!world.forceHullOneDiagnostic&&!world.uniformHullDiagnostic,"Выключите отладку displacement.");
                Require(!ShaderUtil.anythingCompiling,"Дождитесь компиляции шейдеров.");
                view=SceneView.lastActiveSceneView;target=view?view.camera:null;
                Require(view&&target&&view.cameraMode.drawMode==DrawCameraMode.Textured,"Нужна открытая Scene View в Shaded-режиме.");
                report=new Report{utc=DateTime.UtcNow.ToString("O"),unity=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),scene=world.gameObject.scene.path,
                    width=target.pixelWidth,height=target.pixelHeight,vSync=QualitySettings.vSyncCount,targetFps=Application.targetFrameRate,
                    cameraPosition=target.transform.position,cameraRotation=target.transform.rotation,
                    renderScale=new Vector2(ScalableBufferManager.widthScaleFactor,ScalableBufferManager.heightScaleFactor),
                    blendBudget=Mathf.Clamp(world.fastLayerBlendBudget,0,.15f),projectionCutoff=Mathf.Clamp(world.fastLayerProjectionCutoff,0,.15f),authoredTopTwo=world.fastLayerTopTwo,authoredBeforeHeight=world.fastLayerTopTwoBeforeHeight,authoredColorRemainder=world.fastLayerColorRemainder,
                    authoredMode=world.EffectiveSurfaceRenderMode.ToString(),worldSettings=EditorJsonUtility.ToJson(world),
                    notes="Real HDRP Scene View camera GPU interval, NOT isolated shader time or player FPS. Fast mode and Top Two enabled in BOTH arms (_LTFastLayerParams.z=1); w and matching _LT_FAST_PREHEIGHT keyword change (A=0: AFTER height general sampler, B=1: BEFORE height specialized two-record sampler), ABBA. Pre-height pair chosen once before projections by coverage, retains authored weights until height blend; only two layer records are evaluated without 12-slot scratch arrays. Same real masks, textures and projection cutoff; soft pruning unused in both arms. Auto site: 4-5 coverage weights >=3% BEFORE height blend; surviving post-height layers may be fewer. 21x21 probes of actual rendered mesh and bilinear base-mip weights (not an exact per-pixel GPU coverage measurement); ground >=98%, overlap >=80%. Orthographic overhead camera restored on completion/cancel/error. Road/asphalt chunks supported in both arms. Base-only, displacement and other Renderers excluded. Global background disabled only in temporary material copies. LT vegetation suppressed only for this camera; generation is NOT measured/optimized. Sky, lighting, cached shadows, post effects and third-party procedural draws may remain. Observations deduplicated by Time.renderedFrameCount, NOT native GPU timestamps. Only one positive GPU block accepted. No authored mode/quality/scene/asset saves."};
                scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Select(s=>(s,s.isDirty,s.rootCount)).ToArray();
                hashes=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*",SearchOption.AllDirectories).Concat(Directory.GetFiles("ProjectSettings"))
                    .Concat(scenes.Select(s=>s.scene.path)).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"}).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
                Require(LTDetailBenchmarkIsolation.Acquire(this,target),"Другая диагностика владеет изоляцией растительности.");
                ownedFreeze=true;world.paintBenchmarkRunning=true;active=this;running=true;
                preparationStarted=lastRender=EditorApplication.timeSinceStartup;preparing=true;
                sites=LTFastLayerBenchmarkSite.Find(world.CaptureFastLayerBenchmarkChunks(),target,out report.searchMaximumLayers,out report.searchExclusions);
                Require(sites.Count>0,$"Нет достаточно широкого участка смешивания 4–5 слоёв ≥3%. Максимум в проверенных точках: {report.searchMaximumLayers}.\nИсключения: {report.searchExclusions}");
                cameraLease=new LTFastLayerBenchmarkView(view);
                siteIndex=-1;NextSite();
                RenderPipelineManager.beginCameraRendering+=BeginCamera;
                RenderPipelineManager.endCameraRendering+=EndCamera;
            }
            catch(Exception error)
            {
                if(running)Finish("Ошибка запуска: "+error.Message,false);
                else {RestoreRendering();Release();message=error.Message;}
            }
        }
        void NextSite()
        {
            do
            {Require(++siteIndex<sites.Count,"Найдено смешивание, но не удалось заполнить кадр землёй ≥98% и смешиванием ≥80%. Проверьте отчёт.");site=sites[siteIndex];}
            while(!site.Locate());
            framingAttempt=0;FrameSite();
        }
        void FrameSite()
        {
            report.siteAttempts++;report.siteChunk=site.surface.chunk.chunk.name;report.siteFocus=site.focus;
            report.siteViewSize=site.size*Mathf.Pow(.65f,framingAttempt);
            cameraLease.Frame(site.focus,site.Rotation,report.siteViewSize);
            settledRenders=0;view.Repaint();
            message=$"Автонаведение: {report.siteChunk}; кандидат {siteIndex+1}/{sites.Count}.";Repaint();
        }
        void PrepareCamera()
        {
            Require(site.surface.GeometryCurrent,"Геометрия найденного участка изменилась во время автонаведения. Повторите тест после завершения построения.");
            // SceneView applies its pivot/zoom to the Camera during repaint.
            // Wait for two identical camera poses before validating the frame.
            if(Close(pose,target.transform.localToWorldMatrix)&&Close(projection,target.nonJitteredProjectionMatrix))settledRenders++;
            else settledRenders=0;
            pose=target.transform.localToWorldMatrix;projection=target.nonJitteredProjectionMatrix;
            if(settledRenders<2)return;
            site.Probe(target,out report.probeHits,out report.overlapHits,out report.minimumLayers,out report.maximumLayers);report.probeTotal=441;
            if(!LTFastLayerBenchmarkPolicy.FrameSuitable(report.probeTotal,report.probeHits,report.overlapHits))
            {if(++framingAttempt<3)FrameSite();else NextSite();return;}
            report.cameraPosition=target.transform.position;report.cameraRotation=target.transform.rotation;
            SelectChunks();Require(draws.Count>0,"Найденный чанк больше не доступен для теста.");
            others=Resources.FindObjectsOfTypeAll<Renderer>().Where(r=>r&&r.gameObject.scene.IsValid()&&r.gameObject.scene.isLoaded&&!draws.Any(d=>d.renderer==r)).ToArray();
            report.selectedChunks=draws.Count;report.hiddenRenderers=others.Count(r=>r.enabled&&r.gameObject.activeInHierarchy&&!r.forceRenderingOff);
            samplers=new CustomSampler[4];recorders=new Recorder[4];
            for(int i=0;i<4;i++)
            {
                samplers[i]=CustomSampler.Create("LocalTerrain.FastTopTwoHeightAB.Camera.Phase"+i,true);
                Require(samplers[i]!=null&&samplers[i].isValid,"GPU sampler недоступен.");
                recorders[i]=samplers[i].GetRecorder();Require(recorders[i].isValid,"GPU recorder недоступен.");recorders[i].enabled=true;
            }
            commands=new CommandBuffer{name="LT isolated fast top two AB"};
            preparing=false;phase=0;SetPhase();
        }
        void SelectChunks()
        {
            var planes=GeometryUtility.CalculateFrustumPlanes(target);
            foreach(var item in world.CaptureFastLayerBenchmarkChunks())
            {
                var r=item.renderer;var filter=r?r.GetComponent<MeshFilter>():null;var mesh=filter?filter.sharedMesh:null;
                string reason=LTFastLayerBenchmarkSite.Exclusion(item,target);
                if(reason==null&&item.chunk!=site.surface.chunk.chunk)reason="outside auto site";
                if(reason==null&&!GeometryUtility.TestPlanesAABB(planes,r.bounds))reason="outside frustum";
                report.chunks.Add(new ChunkInfo{name=item.chunk?item.chunk.name:"missing",instanceId=item.chunk?item.chunk.GetInstanceID():0,reason=reason??"selected",
                    shader=item.liveMaterial?item.liveMaterial.shader.name:"missing",keywords=item.liveMaterial?string.Join(" ",item.liveMaterial.shaderKeywords):"",
                    vertices=mesh?mesh.vertexCount:0,center=r?r.bounds.center:Vector3.zero,size=r?r.bounds.size:Vector3.zero,distance=r?Mathf.Sqrt(r.bounds.SqrDistance(target.transform.position)):-1});
                if(reason!=null)continue;
                var d=new Draw{chunk=item.chunk,renderer=r,filter=filter,mesh=mesh,matrix=r.localToWorldMatrix,updated=item.chunk.updatedAt};draws.Add(d);
                d.a=Clone(item.liveMaterial,false,item.roadSampling);d.b=Clone(item.liveMaterial,true,item.roadSampling);
            }
            // Queue both variants before measurements. Actual camera warmup also
            // waits for shader compilation; never time a compiling/pink variant.
            foreach(var material in draws.SelectMany(d=>new[]{d.a,d.b}).GroupBy(m=>string.Join(" ",m.shaderKeywords.OrderBy(k=>k))).Select(g=>g.First()))
                foreach(string name in Passes)
                {int pass=material.FindPass(name);Require(pass>=0,"Нет HDRP-прохода: "+name);ShaderUtil.CompilePass(material,pass,false);}
        }
        Material Clone(Material source,bool beforeHeight,bool roads)
        {
            var m=new Material(source){hideFlags=HideFlags.HideAndDontSave,name=source.name+(beforeHeight?" [AB top two BEFORE height]":" [AB top two AFTER height]")};
            m.DisableKeyword("_LT_FAR_ONLY");m.SetVector("_LTGlobalParams",Vector4.zero);
            m.SetVector("_LTFastLayerParams",new Vector4(report.blendBudget,report.projectionCutoff,1,beforeHeight?1:0));
            m.EnableKeyword("_LT_FAST_LAYERS");
            m.DisableKeyword("_LT_FAST_COLOR_REMAINDER");
            if(beforeHeight)m.EnableKeyword("_LT_FAST_PREHEIGHT");else m.DisableKeyword("_LT_FAST_PREHEIGHT");
            if(roads)m.EnableKeyword("_LT_FAST_ROADS");else m.DisableKeyword("_LT_FAST_ROADS");
            return m;
        }
        void SetPhase()
        {
            report.phases.Add(new Phase{index=phase,beforeHeight=BeforeHeight,mode=LTFastLayerBenchmarkPolicy.Mode(BeforeHeight)});
            rendered=0;lastFrame=observedFrame=int.MinValue;advance=false;phaseStarted=warmStarted=EditorApplication.timeSinceStartup;
        }
        static bool Close(Matrix4x4 a,Matrix4x4 b)
        {for(int i=0;i<16;i++)if(Mathf.Abs(a[i]-b[i])>.00001f)return false;return true;}
        void CheckStable()
        {
            Require(world&&world.isActiveAndEnabled&&view&&target&&view.camera==target,"Мир/Scene View закрыты.");
            Require(!EditorApplication.isPlayingOrWillChangePlaymode&&!EditorApplication.isCompiling&&!EditorApplication.isUpdating,"Начался импорт, компиляция C# или Play Mode.");
            Require(!Profiling()&&!LTDetailBenchmarkIsolation.OtherDiagnosticActive,"Включена другая диагностика.");
            Require(world.paintBenchmarkRunning&&LTDetailBenchmarkIsolation.IsOwner(this)&&LTDetailBenchmarkIsolation.Camera==target,"Потерян владелец временных настроек.");
            Require(world.EffectiveSurfaceRenderMode.ToString()==report.authoredMode&&!world.PaintingPending,"Режим/покраска изменились.");
            if(!preparing)Require(Close(pose,target.transform.localToWorldMatrix)&&Close(projection,target.nonJitteredProjectionMatrix),"Камера переместилась или изменилась проекция.");
            Require(target.pixelWidth==report.width&&target.pixelHeight==report.height&&new Vector2(ScalableBufferManager.widthScaleFactor,ScalableBufferManager.heightScaleFactor)==report.renderScale,"Изменилось разрешение.");
            Require(QualitySettings.vSyncCount==report.vSync&&Application.targetFrameRate==report.targetFps&&view.cameraMode.drawMode==DrawCameraMode.Textured,"Изменились настройки отображения.");
            foreach(var d in draws)
                Require(d.chunk&&d.filter&&d.renderer&&d.renderer.enabled&&d.renderer.gameObject.activeInHierarchy&&!d.renderer.forceRenderingOff&&d.filter.sharedMesh==d.mesh&&d.chunk.updatedAt==d.updated&&Close(d.matrix,d.renderer.localToWorldMatrix),"Геометрия/LOD/видимость изменились.");
        }
        void ApplyRendering()
        {
            renderOverride=true;
            foreach(var r in others)if(r)
            {hidden.Add(new Hidden{renderer=r,before=r.forceRenderingOff});r.forceRenderingOff=true;}
            foreach(var d in draws)
            {
                d.originals=d.renderer.sharedMaterials;d.assigned=BeforeHeight?d.b:d.a;d.renderer.sharedMaterial=d.assigned;
                Require(d.renderer.sharedMaterial==d.assigned&&d.assigned.IsKeywordEnabled("_LT_FAST_LAYERS")&&
                    d.assigned.IsKeywordEnabled("_LT_FAST_PREHEIGHT")==BeforeHeight&&
                    !d.assigned.IsKeywordEnabled("_LT_FAST_COLOR_REMAINDER")&&
                    d.assigned.GetVector("_LTFastLayerParams")==new Vector4(report.blendBudget,report.projectionCutoff,1,BeforeHeight?1:0)&&
                    !d.assigned.IsKeywordEnabled("_LT_FAR_ONLY")&&d.assigned.GetVector("_LTGlobalParams")==Vector4.zero,"Неверная фактическая привязка материала.");
            }
            report.phases[phase].verifiedBindings=draws.Count;
        }
        void RestoreRendering()
        {
            if(!renderOverride)return;
            renderOverride=false;
            foreach(var d in draws)if(d.assigned)
            {
                try
                {
                    if(d.renderer)
                    {
                        var current=d.renderer.sharedMaterials;
                        bool ours=current.Length==1&&current[0]==d.assigned;
                        if(!ours){report.restored=false;report.changes.Add("External material change: "+d.renderer.name);}
                        var restored=ours?d.originals:LTFastLayerBenchmarkPolicy.Restore(current,d.originals,d.assigned);
                        d.renderer.sharedMaterials=restored;
                        if(!d.renderer.sharedMaterials.SequenceEqual(restored))throw new InvalidOperationException("Material restoration did not persist.");
                    }
                    else {report.restored=false;report.changes.Add("Selected renderer removed.");}
                }
                catch(Exception e){report.restored=false;report.changes.Add("Restore material failed: "+e.Message);}
                finally{d.assigned=null;d.originals=null;}
            }
            foreach(var h in hidden)if(h.renderer)
            {
                try
                {
                    if(h.renderer.forceRenderingOff)h.renderer.forceRenderingOff=h.before;
                    else if(h.before){report.restored=false;report.changes.Add("External visibility change: "+h.renderer.name);}
                }
                catch(Exception e){report.restored=false;report.changes.Add("Restore visibility failed: "+e.Message);}
            }
            hidden.Clear();
        }
        void BeginCamera(ScriptableRenderContext context,Camera camera)
        {
            if(!running)return;
            if(scopeOpen||renderOverride){Finish("Отменено: вложенный рендер камеры.",false);return;}
            if(camera!=target)return;
            try
            {
                CheckStable();
                lastRender=EditorApplication.timeSinceStartup;
                if(preparing){PrepareCamera();return;}
                var result=report.phases[phase];int frame=Time.renderedFrameCount;
                bool compiling=ShaderUtil.anythingCompiling;
                Require(!ShaderUtil.ShaderHasError(draws[0].a.shader),"Ошибка компиляции terrain-шейдера.");
                if(compiling)
                {
                    Require(result.gpuMs.Count==0,"Шейдер начал компилироваться во время измерений.");
                    warmStarted=lastRender;rendered=0;
                }
                else if(frame!=lastFrame){rendered++;lastFrame=frame;}
                else result.duplicateFrames++;
                if(!advance&&!compiling&&LTFastLayerBenchmarkPolicy.Warm(lastRender-warmStarted,rendered)&&frame!=observedFrame)
                {
                    var r=recorders[phase];long ns=r.gpuElapsedNanoseconds;int blocks=r.gpuSampleBlockCount;
                    if(LTFastLayerBenchmarkStats.Accept(frame,observedFrame,blocks,ns)){result.gpuMs.Add(ns/1000000.0);result.engineFrames.Add(frame);}
                    else result.invalidObservations++;
                    observedFrame=frame;advance=result.gpuMs.Count>=LTFastLayerBenchmarkStats.Samples;
                }
                ApplyRendering();result.renders++;
                scopePhase=phase;commands.Clear();commands.BeginSample(samplers[scopePhase]);context.ExecuteCommandBuffer(commands);scopeOpen=true;
            }
            catch(Exception e){Finish("Отменено: "+e.Message,false);}
        }
        void EndCamera(ScriptableRenderContext context,Camera camera)
        {
            if(camera!=target)return;
            try
            {
                if(scopeOpen){commands.Clear();commands.EndSample(samplers[scopePhase]);context.ExecuteCommandBuffer(commands);}
            }
            catch(Exception e){Finish("Ошибка закрытия GPU-интервала: "+e.Message,false);}
            finally
            {
                scopeOpen=false;RestoreRendering();
                if(pending)EditorApplication.delayCall+=Publish;
            }
        }
        void Tick()
        {
            if(pending&&!scopeOpen){Publish();return;}
            if(!running)return;
            try
            {
                CheckStable();double now=EditorApplication.timeSinceStartup;
                Require(now-lastRender<15,"Нет отрисовки выбранной Scene View за 15 секунд.");
                if(preparing)
                {
                    Require(now-preparationStarted<60,"Автонаведение не завершилось за 60 секунд. GPU-замер не выполнялся.");
                    view.Repaint();return;
                }
                Require(now-phaseStarted<120,"Недостаточно GPU-данных/прогрев не завершился за 120 секунд. GPU = N/A.");
                if(advance&&!scopeOpen)
                {
                    if(++phase==4){Finish("Завершено",true);return;}
                    SetPhase();
                }
                view.Repaint();
                if(now>=nextUi)
                {
                    nextUi=now+.5;
                    Require(EditorJsonUtility.ToJson(world)==report.worldSettings,"Изменились настройки LT World.");
                    Require(report.restored,"Обнаружено стороннее изменение материалов/видимости.");
                    message=$"Этап {phase+1}/4: {report.phases[phase].mode}\nПрогрев {Math.Min(rendered,60)}/60; GPU {report.phases[phase].gpuMs.Count}/120.\nЗемля {report.probeHits*100.0/report.probeTotal:0}%; смешивание 4–5 слоёв {report.overlapHits*100.0/report.probeTotal:0}%.";
                    Repaint();
                }
            }
            catch(Exception e){Finish("Отменено: "+e.Message,false);}
        }
        void Finish(string status,bool completed)
        {
            if(!running)return;
            running=false;report.status=status;report.completed=completed;pending=true;message=status;
            EditorApplication.update+=Drain;
            RenderPipelineManager.beginCameraRendering-=BeginCamera;
            // Restore immediately, including when the user saves/undoes mid-test.
            // Keep the GPU end callback alive until the outstanding marker closes.
            RestoreRendering();
            RestoreCamera();preparing=false;
            report.detailCallbacks=LTDetailBenchmarkIsolation.SuppressedCallbacks;
            LTDetailBenchmarkIsolation.Release(this);
            if(ownedFreeze&&world)world.paintBenchmarkRunning=false;
            ownedFreeze=false;
            if(view)view.Repaint();
        }
        void Drain()
        {
            // HDRP normally closes the marker synchronously before the next
            // editor update. A pipeline exception may omit endCameraRendering.
            // Never retain hidden renderers, material copies or a stuck window
            // indefinitely; discard that broken interval instead of timing it.
            if(scopeOpen&&EditorApplication.timeSinceStartup-lastRender>2)
            {scopeOpen=false;report.completed=false;report.status+="; отсутствует endCameraRendering, GPU-интервал отброшен.";}
            Publish();
        }
        void Release()
        {
            if(scopeOpen)return;
            RenderPipelineManager.beginCameraRendering-=BeginCamera;
            RenderPipelineManager.endCameraRendering-=EndCamera;
            if(recorders!=null)foreach(var r in recorders)if(r!=null)r.enabled=false;
            recorders=null;samplers=null;commands?.Release();commands=null;
            LTDetailBenchmarkIsolation.Release(this);
            RestoreCamera();preparing=false;
            if(ownedFreeze&&world)world.paintBenchmarkRunning=false;
            ownedFreeze=false;
            foreach(var d in draws){if(d.a)DestroyImmediate(d.a);if(d.b)DestroyImmediate(d.b);}
            draws.Clear();others=null;sites=null;site=null;
            if(active==this)active=null;
        }
        void RestoreCamera()
        {
            if(cameraLease==null)return;
            var lease=cameraLease;cameraLease=null;
            try
            {
                report.cameraRestored=lease.Restore();
                if(!report.cameraRestored)throw new InvalidOperationException("Параметры Scene View не восстановлены.");
            }
            catch(Exception e){report.cameraRestored=false;report.restored=false;report.changes.Add("Restore camera failed: "+e.Message);}
        }
        void Publish()
        {
            if(!pending||scopeOpen)return;
            EditorApplication.update-=Drain;
            pending=false;Release();
            try
            {
                report.protectedFilesUnchanged=hashes.All(p=>File.Exists(p.Key)&&Hash(p.Key)==p.Value);
                report.sceneStateUnchanged=SceneManager.sceneCount==scenes.Length&&scenes.All(s=>s.scene.IsValid()&&s.scene.isLoaded&&s.scene.isDirty==s.dirty&&s.scene.rootCount==s.roots)&&world&&EditorJsonUtility.ToJson(world)==report.worldSettings;
                if(!report.protectedFilesUnchanged||!report.sceneStateUnchanged||!report.restored)
                {report.completed=false;report.status+="; файлы/сцена/восстановление отличаются — результат недействителен.";}
                var text=new StringBuilder("[LT Fast Top Two A/B v3 — AFTER vs BEFORE height, automatic site]\n"+report.status+"\n");
                text.AppendLine($"{report.gpu} / {report.api}; {report.width}x{report.height}; authored mode {report.authoredMode}.");
                text.AppendLine($"Auto site {report.siteChunk}, focus {report.siteFocus}, view size {N(report.siteViewSize)}; framing attempts {report.siteAttempts}; maximum coverage layers found {report.searchMaximumLayers}.");
                text.AppendLine($"Rendered-mesh probes: ground {report.probeHits}/{report.probeTotal}, overlap 4-5 layers {report.overlapHits}/{report.probeTotal}, observed layer range {report.minimumLayers}-{report.maximumLayers}. Weight >=3%, BEFORE height blend; post-height layer count not measured.");
                text.AppendLine($"Selected {report.selectedChunks} multilayer chunks; hidden Renderers {report.hiddenRenderers}; LT detail callbacks suppressed {report.detailCallbacks}. Search exclusions: {report.searchExclusions}.");
                text.AppendLine($"Both variants FAST, Top Two ON. A: select AFTER height; B: select BEFORE height. Blend budget {N(report.blendBudget)} unused in both; projection {N(report.projectionCutoff)} identical. Authored top two {report.authoredTopTwo}, before height {report.authoredBeforeHeight}, color remainder {report.authoredColorRemainder} (forced OFF in both copies). Real textures/masks; global background OFF in both copies; geometry fixed.");
                foreach(var p in report.phases)
                {
                    p.medianMs=p.gpuMs.Count>0?LTFastLayerBenchmarkStats.Quantile(p.gpuMs,.5):-1;p.p95Ms=p.gpuMs.Count>0?LTFastLayerBenchmarkStats.Quantile(p.gpuMs,.95):-1;
                    text.AppendLine($"{p.index+1}. {p.mode}: median {(p.gpuMs.Count>0?N(p.medianMs):"N/A")} ms; p95 {(p.gpuMs.Count>0?N(p.p95Ms):"N/A")} ms; {p.gpuMs.Count}/120; invalid {p.invalidObservations}; duplicates {p.duplicateFrames}; verified materials {p.verifiedBindings}; renders {p.renders}.");
                }
                if(report.completed&&report.phases.Count==4&&report.phases.All(p=>p.gpuMs.Count==120))
                {
                    double a=(report.phases[0].medianMs+report.phases[3].medianMs)*.5,b=(report.phases[1].medianMs+report.phases[2].medianMs)*.5;
                    text.AppendLine($"Mean phase medians: AFTER height {N(a)} ms; BEFORE height {N(b)} ms; difference {N(a-b)} ms; reduction {N(LTFastLayerBenchmarkStats.Reduction(a,b))}% (positive = BEFORE height is faster).");
                    text.AppendLine($"AFTER height repeat drift {N(Math.Abs(report.phases[0].medianMs-report.phases[3].medianMs))} ms; BEFORE height repeat drift {N(Math.Abs(report.phases[1].medianMs-report.phases[2].medianMs))} ms. Repeat if drift is comparable to the difference; no automatic significance/FPS claim.");
                }
                else text.AppendLine("INCOMPLETE / INVALID: no speedup conclusion.");
                foreach(var group in report.chunks.GroupBy(c=>c.reason))text.AppendLine($"Chunks {group.Key}: {group.Count()}");
                text.AppendLine($"Restored: {report.restored}; camera restored: {report.cameraRestored}; protected files unchanged: {report.protectedFilesUnchanged}; scene state unchanged: {report.sceneStateUnchanged}.");
                foreach(string change in report.changes)text.AppendLine(change);
                text.AppendLine(report.notes);
                var folder=Path.GetFullPath("Logs/FastLayerBenchmarks");Directory.CreateDirectory(folder);
                var prefix=Path.Combine(folder,"ab-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
                reportPath=prefix+".txt";File.WriteAllText(reportPath,text.ToString());File.WriteAllText(prefix+".json",JsonUtility.ToJson(report,true));
                message=report.status+".\nОтчёт: "+reportPath;Debug.Log(text+"\n"+reportPath);
            }
            catch(Exception e){message="Отрисовка восстановлена, но отчёт не записан: "+e.Message;Debug.LogWarning("[LT Fast Layers A/B] "+message);}
            if(this)Repaint();
        }
        static string N(double value)=>double.IsNaN(value)?"N/A":value.ToString("F3",Inv);
    }
}
