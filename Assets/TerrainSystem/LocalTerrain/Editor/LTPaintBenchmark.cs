using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // Controlled full-camera comparison. Does not claim to isolate terrain shader time.
    public sealed class LTPaintBenchmark : EditorWindow
    {
        [Serializable] sealed class Result
        {
            public int layers,round;
            public string mode;
            public double medianGpuMs,p95GpuMs;
            public List<double> samples=new List<double>();
        }
        [Serializable] sealed class Report
        {
            public string status,utc,unity,gpu,api,camera,scene,notes;
            public int width,height,chunkCount,visibleChunks,warmupFrames,sampleCount,vSync,targetFps;
            public float heightBlend;
            public bool tessellation;
            public bool displacementComparison;
            public string worldSettings;
            public string[] materialSettings;
            public float tessellationFactor,displacementStart,displacementEnd,seamFade;
            public string[] layers,layerSettings;
            public List<Result> results=new List<Result>();
        }
        sealed class SavedRenderer
        {
            public MeshRenderer renderer;
            public Material[] original;
            public Material test;
            public Material snapshot;
            public MeshFilter filter;
            public Mesh mesh;
            public Bounds bounds;
        }
        LTWorld world;
        LTLayerTextureArrays benchmarkArrays;
        Camera cameraToMeasure;
        bool tessellation;
        bool displacementComparison=true;
        readonly List<LTSurfaceLayer> layers=new List<LTSurfaceLayer>();
        readonly List<SavedRenderer> renderers=new List<SavedRenderer>();
        static readonly int[] layerCases={1,4,8,LTPaintRuntime.LayerCapacity};
        readonly int[] layerSchedule={1,4,8,LTPaintRuntime.LayerCapacity,LTPaintRuntime.LayerCapacity,8,4,1,4,LTPaintRuntime.LayerCapacity,1,8};
        const int WeightMapCount=(LTPaintRuntime.LayerCapacity+3)/4;
        readonly int[] displacementSchedule={1,4,4,1,1,4};
        int[] schedule=>displacementComparison?displacementSchedule:layerSchedule;
        readonly List<Texture2D> weights=new List<Texture2D>();
        CustomSampler sampler;
        Recorder recorder;
        CommandBuffer commands;
        Report report;
        Matrix4x4 cameraPose,cameraProjection;
        Vector2 renderScale;
        bool running,scopeOpen;
        int phase,frames,lastFrame=-1,missing;
        const int Warmup=90,Samples=180;
        double lastRender;
        string message="Выбери LTWorld и камеру Game View. Для запуска нужен Play Mode.",reportPath;
        Vector2 scroll;

        [MenuItem("Tools/Local Terrain/GPU Layer Benchmark")]
        static void Open()=>GetWindow<LTPaintBenchmark>("GPU Layer Benchmark");
        void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload+=BeforeReload;
            EditorApplication.playModeStateChanged+=OnPlayMode;
            EditorApplication.update+=Watchdog;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving+=OnSaving;
        }
        void OnDisable()
        {
            if(running)Finish("Отменено: окно закрыто.");
            AssemblyReloadEvents.beforeAssemblyReload-=BeforeReload;
            EditorApplication.playModeStateChanged-=OnPlayMode;
            EditorApplication.update-=Watchdog;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving-=OnSaving;
        }
        void BeforeReload(){if(running)Finish("Отменено: перекомпиляция скриптов.");}
        void OnPlayMode(PlayModeStateChange state){if(running&&state!=PlayModeStateChange.EnteredPlayMode)Finish("Отменено: смена Play Mode.");}
        void OnSaving(UnityEngine.SceneManagement.Scene scene,string path)
        {
            if(!running)return;
            Finish("Отменено: сохранение сцены.");
            if(world&&world.gameObject.scene==scene)world.RestorePaintingMaterialsForSave();
        }
        void OnGUI()
        {
            EditorGUILayout.HelpBox("GPU-время всей камеры. Режим displacement сравнивает текущие материалы и маски: без displacement / с displacement. В обоих случаях нормали исходного террейна и normal map материала; исходная сетка одинакова. Это не отдельное время шейдера.",MessageType.Info);
            using(new EditorGUI.DisabledScope(running))
            {
                world=(LTWorld)EditorGUILayout.ObjectField("LTWorld",world,typeof(LTWorld),true);
                cameraToMeasure=(Camera)EditorGUILayout.ObjectField("Game Camera",cameraToMeasure,typeof(Camera),true);
                displacementComparison=EditorGUILayout.Toggle("Сравнить 3 режима displacement",displacementComparison);
                if(!displacementComparison)
                {
                tessellation=EditorGUILayout.Toggle("Тестировать displacement",tessellation);
                if(tessellation)EditorGUILayout.HelpBox("Тест с тесселяцией на всех чанках; включите Displacement хотя бы у первого слоя. Для сравнения запустите тот же тест без галочки, не меняя камеру. Это синтетическая нагрузка, не точная стоимость текущей расстановки.",MessageType.Info);
                }
                if(GUILayout.Button("Взять мир и слои из сцены"))
                {
                    if(!world)world=Selection.activeGameObject?Selection.activeGameObject.GetComponentInParent<LTWorld>():null;
                    if(!world)world=UnityEngine.Object.FindFirstObjectByType<LTWorld>();
                    if(!cameraToMeasure)cameraToMeasure=Camera.main;
                    layers.Clear();
                    if(world)
                    {
                        if(world.baseLayer)layers.Add(world.baseLayer);
                        foreach(var stamp in world.CollectPaintStamps())stamp.AppendLayers(layers,true);
                        if(layers.Count>LTPaintRuntime.LayerCapacity)layers.RemoveRange(LTPaintRuntime.LayerCapacity,layers.Count-LTPaintRuntime.LayerCapacity);
                    }
                }
                if(!displacementComparison)
                {
                while(layers.Count<LTPaintRuntime.LayerCapacity)layers.Add(null);
                for(int i=0;i<LTPaintRuntime.LayerCapacity;i++)layers[i]=(LTSurfaceLayer)EditorGUILayout.ObjectField("Слой "+(i+1),layers[i],typeof(LTSurfaceLayer),false);
                if(layers.Any(l=>l&&(!l.baseColorMap||!l.normalMap||!l.maskMap)))
                    EditorGUILayout.HelpBox($"У некоторых слоёв не назначены все три карты. Такой тест не измеряет максимальную нагрузку от {LTPaintRuntime.LayerCapacity*3} текстур слоёв.",MessageType.Warning);
                }
                EditorGUILayout.LabelField($"3 раунда; прогрев {Warmup}, замеры {Samples} кадров на этап.");
                if(GUILayout.Button("Начать GPU Benchmark"))StartTest();
            }
            if(running&&GUILayout.Button("Отменить и восстановить материалы"))Finish("Отменено пользователем.");
            scroll=EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.SelectableLabel(message,EditorStyles.wordWrappedLabel,GUILayout.MinHeight(150));
            EditorGUILayout.EndScrollView();
            if(!string.IsNullOrEmpty(reportPath)&&GUILayout.Button("Показать JSON-отчёт"))EditorUtility.RevealInFinder(reportPath);
        }
        void StartTest()
        {
            if(world&&world.RegularMaskGridActive){message="Выключи предварительный просмотр регулярной CPU-сетки перед GPU benchmark.";return;}
            if(!Application.isPlaying||EditorApplication.isPaused){message="Войди в Play Mode и сними паузу. Оставь Game View открытым.";return;}
            if(!world||!world.isActiveAndEnabled||!world.generatedRoot||!world.source||world.paintBenchmarkRunning)
            {message="Нужен активный LTWorld с готовыми чанками, без другого запущенного теста.";return;}
            if(!cameraToMeasure||!cameraToMeasure.isActiveAndEnabled||cameraToMeasure.cameraType!=CameraType.Game)
            {message="Назначь активную Game Camera.";return;}
            if(!displacementComparison&&(layers.Count!=LTPaintRuntime.LayerCapacity||layers.Any(l=>!l)||layers.Distinct().Count()!=LTPaintRuntime.LayerCapacity))
            {message=$"Назначь {LTPaintRuntime.LayerCapacity} разных Surface Layer. Тест {string.Join("/",layerCases)} использует первые N слоёв списка.";return;}
            if((tessellation||displacementComparison)&&!SystemInfo.supportsTessellationShaders){message="GPU не поддерживает тесселяцию.";return;}
            if(displacementComparison&&(world.showDisplacementCoverage||world.forceHullOneDiagnostic||world.uniformHullDiagnostic))
            {message="Выключи диагностику маски и тест Hull=1 перед замером.";return;}
            var shader=Resources.Load<Shader>(tessellation?"LTEightLayersTessellation":"LTEightLayers");
            if(!shader||!shader.isSupported||ShaderUtil.ShaderHasError(shader)){message="Сначала исправь ошибки Eight Layer Shader.";return;}
            if(displacementComparison)
            {
                var tessShader=Resources.Load<Shader>("LTEightLayersTessellation");
                var baseShader=Resources.Load<Shader>("LTEightLayers");
                if(!tessShader||!baseShader||!tessShader.isSupported||!baseShader.isSupported||ShaderUtil.ShaderHasError(tessShader)||ShaderUtil.ShaderHasError(baseShader))
                {message="Сначала исправь ошибки обоих Eight Layer Shader.";return;}
            }
            report=new Report{utc=DateTime.UtcNow.ToString("O"),unity=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName,
                api=SystemInfo.graphicsDeviceType.ToString(),camera=cameraToMeasure.name,scene=world.gameObject.scene.path,
                width=cameraToMeasure.pixelWidth,height=cameraToMeasure.pixelHeight,heightBlend=world.layerHeightBlend,
                tessellation=tessellation,tessellationFactor=world.EffectiveTessellationFactor,displacementStart=world.displacementFadeStart,displacementEnd=world.displacementFadeEnd,seamFade=world.displacementSeamFade,
                warmupFrames=Warmup,sampleCount=Samples,vSync=QualitySettings.vSyncCount,targetFps=Application.targetFrameRate,
                displacementComparison=displacementComparison,
                worldSettings=JsonUtility.ToJson(world),
                layers=layers.Where(l=>l).Select(l=>AssetDatabase.GetAssetPath(l)).ToArray(),
                layerSettings=layers.Where(l=>l).Select(l=>JsonUtility.ToJson(l)).ToArray(),
                notes="GPU CustomSampler around selected SRP camera. Three-frame GPU delay discarded during warmup. Equal positive weights across all terrain chunks; normal/mask sampled only if assigned. Full camera time, not isolated shader time. Editor/other cameras, animation and dynamic scene content can affect results. No automatic GPU profiling fallback to FPS."};
            cameraPose=cameraToMeasure.transform.localToWorldMatrix;cameraProjection=cameraToMeasure.nonJitteredProjectionMatrix;
            if(displacementComparison)report.notes="Current bound chunk materials and masks, source meshes held fixed. Cases: no tessellation/displacement; displacement preserving source terrain normals and material normal maps. Full camera GPU time, not isolated terrain cost. Geometry changes also affect shadows/contact shadows. Scene animation and other cameras remain confounders.";
            renderScale=new Vector2(ScalableBufferManager.widthScaleFactor,ScalableBufferManager.heightScaleFactor);
            reportPath=null;phase=0;lastFrame=-1;running=true;world.paintBenchmarkRunning=true;
            try
            {
                if(!displacementComparison)
                {
                    benchmarkArrays=new LTLayerTextureArrays();
                    if(!benchmarkArrays.Ensure(world,layers))throw new InvalidOperationException(benchmarkArrays.Status);
                }
                foreach(var chunk in world.generatedRoot.GetComponentsInChildren<LTChunk>())
                {
                    var r=chunk.GetComponent<MeshRenderer>();var filter=chunk.GetComponent<MeshFilter>();
                    if(!r||!filter||!filter.sharedMesh)continue;
                    if(displacementComparison)
                    {
                        var source=r.sharedMaterial;
                        if(!source||r.sharedMaterials.Length!=1)throw new InvalidOperationException("Для сравнения нужен один текущий материал на чанк.");
                        var clone=new Material(source){name="Temporary displacement comparison",hideFlags=HideFlags.HideAndDontSave};
                        var snapshot=new Material(source){name="Displacement benchmark snapshot",hideFlags=HideFlags.HideAndDontSave};
                        renderers.Add(new SavedRenderer{renderer=r,original=r.sharedMaterials,test=clone,snapshot=snapshot,filter=filter,mesh=filter.sharedMesh,bounds=r.localBounds});
                        r.sharedMaterial=clone;
                        continue;
                    }
                    var material=new Material(shader){name="Temporary GPU layer benchmark",hideFlags=HideFlags.HideAndDontSave};
                    var state=new SavedRenderer{renderer=r,original=r.sharedMaterials,test=material,filter=filter,mesh=filter.sharedMesh,bounds=r.localBounds};
                    renderers.Add(state);
                    float w=world.source.size.x/world.chunksX,d=world.source.size.z/world.chunksZ;
                    material.SetVector("_LTRect",new Vector4(chunk.x*w,chunk.z*d,w,d));
                    material.SetVector("_LTWorldSize",new Vector4(world.source.size.x,world.source.size.z,0,0));
                    material.SetFloat("_LTHeightBlend",world.layerHeightBlend);
                    for(int i=0;i<LTPaintRuntime.LayerCapacity;i++)BindLayer(material,layers[i],i,benchmarkArrays.mask);
                    benchmarkArrays.Bind(material,layers);
                    if(tessellation)
                    {
                        float end=Mathf.Max(.02f,world.displacementFadeEnd),start=Mathf.Clamp(world.displacementFadeStart,0,end-.01f);
                        material.SetVector("_LTDisplacementParams",new Vector4(start,end,Mathf.Max(.1f,world.displacementSeamFade),2));
                        material.SetFloat("_TessellationFactor",world.EffectiveTessellationFactor);
                        material.SetFloat("_TessellationFactorMinDistance",start);material.SetFloat("_TessellationFactorMaxDistance",end);
                        material.SetFloat("_TessellationFactorTriangleSize",16);material.SetFloat("_TessellationBackFaceCullEpsilon",-1);
                        material.SetFloat("_TessellationShapeFactor",0);
                        var expanded=state.bounds;var scale=r.transform.lossyScale;
                        expanded.Expand(new Vector3(4/Mathf.Max(.0001f,Mathf.Abs(scale.x)),4/Mathf.Max(.0001f,Mathf.Abs(scale.y)),4/Mathf.Max(.0001f,Mathf.Abs(scale.z))));r.localBounds=expanded;
                    }
                    r.sharedMaterial=material;
                }
                report.chunkCount=renderers.Count;
                if(displacementComparison)report.materialSettings=renderers.Select(r=>EditorJsonUtility.ToJson(r.snapshot)).ToArray();
                if(renderers.Count==0)throw new InvalidOperationException("Нет мешей чанков.");
                var planes=GeometryUtility.CalculateFrustumPlanes(cameraToMeasure);
                report.visibleChunks=renderers.Count(r=>r.renderer.enabled&&r.renderer.gameObject.activeInHierarchy&&
                    (cameraToMeasure.cullingMask&(1<<r.renderer.gameObject.layer))!=0&&GeometryUtility.TestPlanesAABB(planes,r.renderer.bounds));
                if(report.visibleChunks==0)throw new InvalidOperationException("Камера не видит ни одного чанка террейна.");
                if(displacementComparison&&!renderers.Any(r=>r.original[0].shader.name=="Local Terrain/Eight Layers Tessellation HDRP"&&
                    r.renderer.enabled&&r.renderer.gameObject.activeInHierarchy&&
                    (cameraToMeasure.cullingMask&(1<<r.renderer.gameObject.layer))!=0&&GeometryUtility.TestPlanesAABB(planes,r.renderer.bounds)))
                    throw new InvalidOperationException("Камера не видит чанков с активным tessellation-материалом. Подойди к displacement-слою и дождись обновления.");
                // Keep all cases alive until the run ends; no texture allocation during measurement.
                if(!displacementComparison)foreach(int count in layerCases)for(int group=0;group<WeightMapCount;group++)
                {
                    var texture=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true){name="Benchmark weights",hideFlags=HideFlags.HideAndDontSave,wrapMode=TextureWrapMode.Clamp};
                    var color=Color.clear;for(int c=0;c<4;c++)if(group*4+c<count)color[c]=1f/count;
                    texture.SetPixel(0,0,color);texture.Apply(false,true);weights.Add(texture);
                }
                sampler=CustomSampler.Create("LocalTerrain.PaintBenchmark.Camera",true);
                if(sampler==null||!sampler.isValid)throw new InvalidOperationException("GPU sampler недоступен.");
                recorder=sampler.GetRecorder();recorder.enabled=true;
                commands=new CommandBuffer{name="Local Terrain GPU timestamps"};
                RenderPipelineManager.beginCameraRendering+=BeginCamera;
                RenderPipelineManager.endCameraRendering+=EndCamera;
                SetPhase();lastRender=EditorApplication.timeSinceStartup;
            }
            catch(Exception e){Finish("Ошибка подготовки: "+e.Message);Debug.LogException(e);}
        }
        static void BindLayer(Material m,LTSurfaceLayer l,int i,Texture2DArray maskArray)
        {
            m.SetVector("_LTTiling"+i,new Vector4(l.tileSizeMetres.x,l.tileSizeMetres.y,
                l.tileOffsetMetres.x/Mathf.Max(.001f,l.tileSizeMetres.x),l.tileOffsetMetres.y/Mathf.Max(.001f,l.tileSizeMetres.y)));
            m.SetVector("_LTTint"+i,l.tint.linear);
            m.SetVector("_LTSettings"+i,new Vector4(l.normalStrength,l.metallic,l.smoothness,l.maskMap?1:0));
            m.SetVector("_LTFlags"+i,new Vector4(l.normalMap?1:0,l.aoStrength,l.heightStrength,l.heightOffset));
            float smoothing=l.maskMap?Mathf.Clamp(l.displacementSmoothingMip,0,Mathf.Min(10,l.maskMap.mipmapCount-1)):0;
            if(l.maskMap&&smoothing>0)
                smoothing=Mathf.Clamp(smoothing+Mathf.Log((float)maskArray.width/Mathf.Max(l.maskMap.width,l.maskMap.height),2),0,maskArray.mipmapCount-1);
            m.SetVector("_LTDisplacement"+i,new Vector4(l.displacement&&l.maskMap?Mathf.Clamp(l.displacementAmplitude,0,2):0,Mathf.Clamp01(l.displacementCenter),smoothing,0));
        }
        void SetPhase()
        {
            frames=0;missing=0;int count=schedule[phase];
            if(displacementComparison)
            {
                foreach(var r in renderers)
                {
                    var original=r.snapshot;
                    r.test.shader=original.shader;r.test.CopyPropertiesFromMaterial(original);
                    if(original.shader.name!="Local Terrain/Eight Layers Tessellation HDRP")continue;
                    if(count==1)r.test.shader=Resources.Load<Shader>("LTEightLayers");
                }
            }
            else
            {
                int firstMap=Array.IndexOf(layerCases,count)*WeightMapCount;
                foreach(var r in renderers)for(int group=0;group<WeightMapCount;group++)
                    r.test.SetTexture("_LTWeights"+group,weights[firstMap+group]);
            }
            report.results.Add(new Result{layers=displacementComparison?0:count,mode=PhaseLabel(count),round=phase/(displacementComparison?2:layerCases.Length)+1});
        }
        string PhaseLabel(int count)=>!displacementComparison?count+" слоёв":count==1?"Без displacement":"Displacement, нормали террейна";
        bool Stable()
        {
            if(!world||!world.isActiveAndEnabled||!cameraToMeasure||!cameraToMeasure.isActiveAndEnabled)return false;
            if(cameraToMeasure.transform.localToWorldMatrix!=cameraPose||cameraToMeasure.nonJitteredProjectionMatrix!=cameraProjection)return false;
            if(cameraToMeasure.pixelWidth!=report.width||cameraToMeasure.pixelHeight!=report.height)return false;
            if(new Vector2(ScalableBufferManager.widthScaleFactor,ScalableBufferManager.heightScaleFactor)!=renderScale)return false;
            return renderers.All(r=>r.renderer&&r.filter&&r.filter.sharedMesh==r.mesh&&r.renderer.sharedMaterial==r.test);
        }
        void BeginCamera(ScriptableRenderContext context,Camera camera)
        {
            if(!running||camera!=cameraToMeasure)return;
            try
            {
                if(!Stable()){Finish("Отменено: камера, разрешение, меш или материал изменились. Отключи движение камеры и dynamic resolution.");return;}
                if(lastFrame==Time.frameCount){Finish("Камера отрисовалась повторно в одном кадре; замер неоднозначен.");return;}
                lastFrame=Time.frameCount;lastRender=EditorApplication.timeSinceStartup;
                // Recorder GPU counters describe a frame three frames in the past.
                // Read once per rendered frame, after a long per-case warmup.
                if(frames++>=Warmup)
                {
                    long ns=recorder.gpuElapsedNanoseconds;int blocks=recorder.gpuSampleBlockCount;
                    if(blocks==1&&ns>0)
                    {
                        report.results[phase].samples.Add(ns/1000000.0);missing=0;
                        if(report.results[phase].samples.Count>=Samples)
                        {
                            var result=report.results[phase];var sorted=result.samples.OrderBy(v=>v).ToArray();
                            result.medianGpuMs=(sorted[sorted.Length/2-1]+sorted[sorted.Length/2])*.5;
                            result.p95GpuMs=sorted[(int)Math.Ceiling(sorted.Length*.95)-1];
                            phase++;
                            if(phase==schedule.Length){Finish("Завершено");return;}
                            SetPhase();
                        }
                    }
                    else if(++missing>180){Finish("GPU-таймер не вернул однозначные данные. Включи GPU Usage в Unity Profiler и повтори. GPU-время не заменяется CPU/FPS.");return;}
                }
                commands.Clear();commands.BeginSample(sampler);context.ExecuteCommandBuffer(commands);scopeOpen=true;
                message=$"Этап {phase+1}/{schedule.Length}: {PhaseLabel(schedule[phase])}.\nПрогрев: {Math.Min(frames,Warmup)}/{Warmup}; GPU-замеры: {report.results[phase].samples.Count}/{Samples}.\nНе двигай камеру и не меняй слои. Оставь Game View открытым.";
            }
            catch(Exception e){Finish("Ошибка замера: "+e.Message);Debug.LogException(e);}
        }
        void EndCamera(ScriptableRenderContext context,Camera camera)
        {
            if(!running||camera!=cameraToMeasure||!scopeOpen)return;
            commands.Clear();commands.EndSample(sampler);context.ExecuteCommandBuffer(commands);scopeOpen=false;
        }
        void Watchdog()
        {
            if(!running)return;
            if(EditorApplication.timeSinceStartup-lastRender>15)Finish("Камера не отрисовывается. Открой Game View, сними паузу и повтори.");
            Repaint();
        }
        void Finish(string status)
        {
            if(!running)return;running=false;
            RenderPipelineManager.beginCameraRendering-=BeginCamera;RenderPipelineManager.endCameraRendering-=EndCamera;
            if(recorder!=null)recorder.enabled=false;
            commands?.Release();commands=null;scopeOpen=false;
            foreach(var r in renderers)
            {
                // Do not overwrite an external material edit made during the test.
                if(r.renderer&&r.renderer.sharedMaterial==r.test){r.renderer.sharedMaterials=r.original;r.renderer.localBounds=r.bounds;}
                if(r.test)DestroyImmediate(r.test);
                if(r.snapshot)DestroyImmediate(r.snapshot);
            }
            renderers.Clear();foreach(var t in weights)if(t)DestroyImmediate(t);weights.Clear();
            benchmarkArrays?.Dispose();benchmarkArrays=null;
            if(world)world.paintBenchmarkRunning=false;
            message=status;
            if(report!=null)
            {
                report.status=status;
                foreach(int count in displacementComparison?new[]{1,4}:layerCases)
                {
                    var complete=report.results.Where(r=>r.mode==PhaseLabel(count)&&r.samples.Count==Samples).ToArray();
                    if(complete.Length==0)continue;
                    message+=$"\n{PhaseLabel(count)} — GPU медианы раундов: "+string.Join(" / ",complete.Select(r=>r.medianGpuMs.ToString("F3")))+" ms";
                }
                message+="\nЭто время всей камеры, не только террейна.";
                try
                {
                    string folder=Path.GetFullPath(Path.Combine(Application.dataPath,"../Logs/LocalTerrainGpuBenchmarks"));Directory.CreateDirectory(folder);
                    reportPath=Path.Combine(folder,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")+".json");
                    File.WriteAllText(reportPath,JsonUtility.ToJson(report,true));message+="\nОтчёт: "+reportPath;
                }
                catch(Exception e){message+="\nНе удалось сохранить отчёт: "+e.Message;}
                Debug.Log("Local Terrain GPU benchmark: "+message);
            }
            Repaint();
        }
    }
}
