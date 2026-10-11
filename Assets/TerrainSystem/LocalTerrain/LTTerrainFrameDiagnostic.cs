using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace LocalTerrainPrototype
{
    // Opt-in, runtime-only tool. No scene setup, asset writes, forced camera,
    // shader globals, property blocks or terrain regeneration. F8 temporarily
    // removes VSync/FPS caps; closing restores them. Five layer-rendering modes.
    [AddComponentMenu("")]
    public sealed class LTTerrainFrameDiagnostic : MonoBehaviour
    {
        struct CaptureSettings : IEquatable<CaptureSettings>
        {
            public int width,height,cameraWidth,cameraHeight,scaledWidth,scaledHeight,quality,vsync,targetFPS;
            public double refreshHz;
            public float scaleX,scaleY,hdrpScale;
            public bool dynamicResolution;
            public bool Equals(CaptureSettings other)=>width==other.width&&height==other.height&&
                cameraWidth==other.cameraWidth&&cameraHeight==other.cameraHeight&&quality==other.quality&&
                vsync==other.vsync&&targetFPS==other.targetFPS&&scaledWidth==other.scaledWidth&&scaledHeight==other.scaledHeight&&refreshHz==other.refreshHz&&
                scaleX==other.scaleX&&scaleY==other.scaleY&&hdrpScale==other.hdrpScale&&dynamicResolution==other.dynamicResolution;
            public override string ToString()=>string.Format(Inv,
                "screen={0}x{1}; cameraPixels={2}x{3}; quality={4}; VSync={5}; targetFPS={6}; bufferScale={7:R},{8:R}; dynamicResolutionAllowed={9}; cameraScaledPixels={10}x{11}; displayRefreshHz={12:R}; hdrpRequestedScale={13:R} (-1=unavailable)",
                width,height,cameraWidth,cameraHeight,quality,vsync,targetFPS,scaleX,scaleY,dynamicResolution,scaledWidth,scaledHeight,refreshHz,hdrpScale);
        }
        sealed class Result
        {
            public double[] frames;
            public double mean,median,p95;
            public string context;
            public LTTerrainFrameMetrics.Snapshot metrics;
            public LTTerrainFrameGeometry.Snapshot geometry;
        }
        static LTTerrainFrameDiagnostic instance;
        internal static bool Active {get;private set;}
        internal static LTTerrainDiagnosticMode Mode {get;private set;}
        static readonly LTTerrainDiagnosticMode[] Modes={LTTerrainDiagnosticMode.LiveLayers,LTTerrainDiagnosticMode.FastLayers,
            LTTerrainDiagnosticMode.TopTwoAfterHeight,LTTerrainDiagnosticMode.TopTwoBeforeHeight,LTTerrainDiagnosticMode.TopTwoColorRemainder};
        static readonly string[] Names={"Обычные слои","Быстрые слои","Два слоя — после высот","Два слоя — до высот","Два слоя + цвет остальных"};
        static int ModeIndex=>Math.Max(0,Array.IndexOf(Modes,Mode));
        static string ModeSettings(int index)=>$"fast={(index==0?0:1)}; topTwo={(index<2?0:1)}; beforeHeight={(index>=3?1:0)}; colorRemainder={(index==4?1:0)}";
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        readonly LTTerrainFrameSamples samples=new LTTerrainFrameSamples();
        readonly Result[] results=new Result[5];
        readonly LTTerrainFramePacing pacing=new LTTerrainFramePacing();
        readonly LTTerrainFrameMetrics metrics=new LTTerrainFrameMetrics();
        readonly LTTerrainFrameGeometry geometry=new LTTerrainFrameGeometry();
        LTTerrainFrameGeometry.Snapshot captureGeometry;
        string resultGeometryPolicy;
        Camera captureCamera;
        Camera resolutionCamera;
        float hdrpScale=-1;
        Vector3 anchorPosition;
        Quaternion anchorRotation;
        Matrix4x4 anchorProjection;
        CaptureSettings anchorSettings;
        string anchorContext;
        int anchorRevision,observedFrame=-1,nearChunks,farChunks,skippedChunks;
        int resultContentRevision=-1;
        int pairedChunks,unpairedChunks;
        double nextTextUpdate;
        string message="",panel="";
        bool hasAnchor;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            if(instance)instance.Close();
            Active=false;Mode=LTTerrainDiagnosticMode.Normal;
            LTTerrainDiagnosticMaterials.RestoreAll();instance=null;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if(instance||SystemInfo.graphicsDeviceType==UnityEngine.Rendering.GraphicsDeviceType.Null)return;
            var go=new GameObject("Terrain frame diagnostic (F8)"){hideFlags=HideFlags.HideAndDontSave};
            DontDestroyOnLoad(go);instance=go.AddComponent<LTTerrainFrameDiagnostic>();
        }
        void OnEnable(){if(Application.isPlaying)instance=this;}
        void OnDisable(){Close();if(instance==this)instance=null;}
        void OnApplicationFocus(bool focused)
        {if(!focused&&samples.Running)Cancel("Замер отменён: окно потеряло фокус. F9 — повторить.");}

        void ObserveResolution(ScriptableRenderContext context,Camera camera)
        {
            // HDRP leaves this camera's DRS handler selected until after its
            // EndCameraRendering callback. Read only; do not update the handler
            // or create an HDCamera. This also detects SOFTWARE dynamic scaling,
            // which Camera.scaledPixelWidth/ScalableBufferManager alone miss.
            if(!Active||camera!=captureCamera||!(RenderPipelineManager.currentPipeline is HDRenderPipeline))return;
            resolutionCamera=camera;hdrpScale=DynamicResolutionHandler.instance.GetCurrentScale();
        }

        internal static void Observe(Camera camera,int near,int far,int skipped,int forced=0,int unavailable=0,int paired=0,int unpaired=0)
        {
            if(!Active||!instance||camera!=instance.captureCamera)return;
            if(instance.observedFrame!=Time.frameCount)
            {
                instance.observedFrame=Time.frameCount;
                instance.nearChunks=instance.farChunks=instance.skippedChunks=0;
                instance.pairedChunks=instance.unpairedChunks=0;
            }
            instance.nearChunks+=near;instance.farChunks+=far;instance.skippedChunks+=skipped;
            instance.pairedChunks+=paired;instance.unpairedChunks+=unpaired;
        }

        void Close()
        {
            Active=false;Mode=LTTerrainDiagnosticMode.Normal;samples.Cancel();
            geometry.Dispose();
            pacing.Dispose();metrics.Dispose();
            RenderPipelineManager.endCameraRendering-=ObserveResolution;resolutionCamera=null;hdrpScale=-1;
            LTTerrainDiagnosticMaterials.RestoreAll();
        }
        void SetMode(LTTerrainDiagnosticMode mode)
        {
            if(!LTTerrainDiagnosticMaterials.IsLayerComparison(mode))throw new ArgumentOutOfRangeException(nameof(mode));
            samples.Cancel();metrics.Dispose();Mode=mode;
            message="Режим переключён. Дождитесь компиляции; F9 — замер (2 с прогрев + 5 с запись без панели). Камеру не двигать.";
            nextTextUpdate=0;
        }
        static Camera FindCamera()
        {
            var main=Camera.main;
            if(main&&main.isActiveAndEnabled&&main.cameraType==CameraType.Game)return main;
            foreach(var camera in Camera.allCameras)
                if(camera&&camera.isActiveAndEnabled&&camera.cameraType==CameraType.Game&&!camera.targetTexture)return camera;
            return null;
        }
        CaptureSettings Settings()
        {
            var camera=captureCamera;
            return new CaptureSettings{width=Screen.width,height=Screen.height,cameraWidth=camera?camera.pixelWidth:0,cameraHeight=camera?camera.pixelHeight:0,
                quality=QualitySettings.GetQualityLevel(),vsync=QualitySettings.vSyncCount,targetFPS=Application.targetFrameRate,
                scaledWidth=camera?camera.scaledPixelWidth:0,scaledHeight=camera?camera.scaledPixelHeight:0,refreshHz=Screen.currentResolution.refreshRateRatio.value,
                hdrpScale=resolutionCamera==camera?hdrpScale:-1,
                scaleX=ScalableBufferManager.widthScaleFactor,scaleY=ScalableBufferManager.heightScaleFactor,dynamicResolution=camera&&camera.allowDynamicResolution};
        }
        bool SamePose(Camera camera)
        {
            if(!camera||camera!=captureCamera||!camera.isActiveAndEnabled)return false;
            if(Vector3.Distance(anchorPosition,camera.transform.position)>.01f||Quaternion.Angle(anchorRotation,camera.transform.rotation)>.05f)return false;
            var projection=camera.nonJitteredProjectionMatrix;
            for(int i=0;i<16;i++)if(Mathf.Abs(projection[i]-anchorProjection[i])>.00001f)return false;
            return true;
        }
        void BeginCapture()
        {
            var camera=FindCamera();
            if(!camera){Cancel("Не найдена активная игровая камера.");return;}
            try{captureGeometry=geometry.Read(camera,true);}
            catch(Exception error){Cancel("Не удалось проверить меши terrain: "+error.Message);return;}
            if(captureGeometry.error!=null){Cancel("LOD/геометрия: "+captureGeometry.error);return;}
            bool same=hasAnchor&&SamePose(camera)&&resultContentRevision==LTTerrainDiagnosticMaterials.ContentRevision&&resultGeometryPolicy==captureGeometry.policy;
            captureCamera=camera;
            var settings=Settings();
            same&=anchorSettings.Equals(settings);
            if(!same)
            {
                Array.Clear(results,0,results.Length);
                anchorPosition=camera.transform.position;anchorRotation=camera.transform.rotation;
                anchorProjection=camera.nonJitteredProjectionMatrix;anchorSettings=settings;hasAnchor=true;
                anchorContext=string.Format(Inv,"camera={0}; position=({1:R},{2:R},{3:R}); rotation=({4:R},{5:R},{6:R},{7:R}); {8}",
                    camera.name,anchorPosition.x,anchorPosition.y,anchorPosition.z,
                    anchorRotation.x,anchorRotation.y,anchorRotation.z,anchorRotation.w,settings);
            }
            // Repeating a mode replaces its result only after a fresh successful recording.
            observedFrame=-1;nearChunks=farChunks=skippedChunks=0;
            pairedChunks=unpairedChunks=0;metrics.Begin();
            samples.Start(Time.realtimeSinceStartupAsDouble);
            message=same?"Прогрев. Камеру не двигать.":"Новая точка сравнения; старые результаты очищены. Прогрев.";
            nextTextUpdate=0;
        }
        void Cancel(string reason){samples.Cancel();metrics.Dispose();message=reason;nextTextUpdate=0;}

        void TogglePacing()
        {
            Cancel("Лимиты FPS изменены: результаты очищены. F9 — новый замер.");
            if(pacing.Enabled)pacing.Dispose();else pacing.Enable();
            Array.Clear(results,0,results.Length);hasAnchor=false;
        }

        void Update()
        {
            bool toggle=false,one=false,two=false,three=false,four=false,five=false,uncap=false,record=false,save=false;
#if ENABLE_INPUT_SYSTEM
            var keyboard=Keyboard.current;
            if(keyboard!=null)
            {
                toggle=keyboard.f8Key.wasPressedThisFrame;
                if(Active)
                {
                    one=keyboard.digit1Key.wasPressedThisFrame;two=keyboard.digit2Key.wasPressedThisFrame;uncap=keyboard.f7Key.wasPressedThisFrame;
                    three=keyboard.digit3Key.wasPressedThisFrame;four=keyboard.digit4Key.wasPressedThisFrame;five=keyboard.digit5Key.wasPressedThisFrame;
                    record=keyboard.f9Key.wasPressedThisFrame;save=keyboard.f10Key.wasPressedThisFrame;
                }
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            toggle=Input.GetKeyDown(KeyCode.F8);
            if(Active)
            {
                one=Input.GetKeyDown(KeyCode.Alpha1);two=Input.GetKeyDown(KeyCode.Alpha2);uncap=Input.GetKeyDown(KeyCode.F7);
                three=Input.GetKeyDown(KeyCode.Alpha3);four=Input.GetKeyDown(KeyCode.Alpha4);five=Input.GetKeyDown(KeyCode.Alpha5);
                record=Input.GetKeyDown(KeyCode.F9);save=Input.GetKeyDown(KeyCode.F10);
            }
#endif
            if(toggle)
            {
                if(Active){Close();return;}
                Active=true;pacing.Enable();captureCamera=FindCamera();SetMode(Modes[0]);
                RenderPipelineManager.endCameraRendering+=ObserveResolution;
            }
            if(!Active)return;
            if(one)SetMode(Modes[0]);
            if(two)SetMode(Modes[1]);
            if(three)SetMode(Modes[2]);
            if(four)SetMode(Modes[3]);
            if(five)SetMode(Modes[4]);
            if(uncap)TogglePacing();
            if(record)BeginCapture();
            if(save&&!samples.Running)SaveReport();
            double now=Time.realtimeSinceStartupAsDouble;
            // BeginCapture ran in this Update: its first interval belongs to the
            // previous mode/frame, so start collecting only on the next Update.
            if(samples.Running&&!record)
            {
                if(!SamePose(captureCamera))Cancel("Замер отменён: камера сдвинулась, сменилась или изменилась проекция. F9 — повторить.");
                else if(!anchorSettings.Equals(Settings()))Cancel("Замер отменён: изменилось разрешение/качество/ограничение FPS.");
                else if(observedFrame<Time.frameCount-2)Cancel("Замер отменён: нет отрисовки terrain этой камерой.");
                else if(samples.Sampling&&anchorRevision!=LTTerrainDiagnosticMaterials.Revision)
                    Cancel("Замер отменён: обновились материалы terrain. Дождитесь готовности и нажмите F9.");
                else if(samples.Sampling&&(pairedChunks==0||unpairedChunks>0))Cancel(LayersUnavailable());
                else
                {
                    bool wasSampling=samples.Sampling;
                    metrics.Poll(wasSampling);
                    if(samples.Tick(now))FinishCapture();
                    else if(!samples.Running)Cancel("Замер отменён: некорректный интервал времени.");
                    else if(!wasSampling&&samples.Sampling)
                    {
                        anchorRevision=LTTerrainDiagnosticMaterials.Revision;
                        if(resultContentRevision!=LTTerrainDiagnosticMaterials.ContentRevision)Array.Clear(results,0,results.Length);
                        if(pairedChunks==0||unpairedChunks>0)Cancel(LayersUnavailable());
                    }
                }
            }
            // Neither draw the overlay nor rebuild its variable-length result
            // text during warm-up/recording. Its cost must not grow from mode 1 to 5.
            if(!samples.Running&&now>=nextTextUpdate){nextTextUpdate=now+.25;UpdatePanel(now);}
        }

        void FinishCapture()
        {
            // These queries run AFTER the last timed interval, not every frame.
            LTTerrainFrameGeometry.Snapshot meshState;
            try{meshState=geometry.Read(captureCamera);}
            catch(Exception error){Cancel("Не удалось проверить меши terrain: "+error.Message);return;}
            if(meshState.error!=null){Cancel("LOD/геометрия: "+meshState.error);return;}
            if(meshState.fingerprint!=captureGeometry.fingerprint||meshState.policy!=captureGeometry.policy)
            {Cancel("Замер отменён: меши/настройки terrain изменились во время записи. Дождитесь готовности и нажмите F9.");return;}
            if(!LTTerrainFrameSamples.TrySummarize(samples.milliseconds,out double mean,out double median,out double p95))
            {Cancel("Нет корректных кадров для отчёта.");return;}
            results[ModeIndex]=new Result{frames=samples.milliseconds.ToArray(),mean=mean,median=median,p95=p95,
                metrics=metrics.Finish(),geometry=meshState,
                context=anchorContext+$"; mode={ModeIndex+1}; {ModeSettings(ModeIndex)}; verifiedMultilayerChunks={pairedChunks}; unsupportedMultilayerChunks={unpairedChunks}; naturalChunksNear={nearChunks}; naturalChunksFar={farChunks}; skipped={skippedChunks}; uncappedScope={pacing.Enabled}; originalVSync={pacing.OriginalVSync}; originalTargetFPS={pacing.OriginalTargetFPS}"};
            resultContentRevision=LTTerrainDiagnosticMaterials.ContentRevision;
            resultGeometryPolicy=meshState.policy;
            message="Замер готов. Не двигайте камеру: выберите следующий режим и нажмите F9. F10 — сохранить отчёт.";
            if(!results[ModeIndex].metrics.hasCpuGpu)message="Записан только FPS: CPU/GPU-таймингов нет. F10 — сохраните отчёт с диагностикой счётчиков.";
            nextTextUpdate=0;
        }
        string ResultLine(int index)
        {
            var result=results[index];
            return result==null?$"{index+1}. {Names[index]}: нет замера":
                string.Format(Inv,"{0}. {1}: {2:F2} ms / {3:F1} FPS; median {4:F2}, p95 {5:F2} ms ({6} кадров)",
                    index+1,Names[index],result.mean,1000/result.mean,result.median,result.p95,result.frames.Length)+"\n    "+result.metrics.brief+" | "+result.geometry.Brief;
        }
        static string LayersUnavailable()=>"Сравнение неприменимо: подойдите к многослойной земле. Нет подходящих ближних чанков либо часть материалов не готова. Исходный режим LT World может быть любым.";
        void UpdatePanel(double now)
        {
            string progress=samples.Running?(samples.Sampling?$"Запись: {samples.elapsed:F1} / 5 секунд":$"Прогрев: {Math.Max(0,2-(now-samples.start)):F1} с"):message;
            panel="TERRAIN A/B — "+Names[ModeIndex]+"\n"+
                "1 — обычные   |   2 — быстрые   |   3 — два после высот   |   4 — два до высот   |   5 — два + цвет\n"+
                "F8 — закрыть и восстановить   |   F9 — замер: 2 с прогрева + 5 с записи   |   F10 — сохранить TXT + CSV\n"+
                "Во время прогрева и записи панель скрыта. Не двигайте камеру.\n\n"+
                $"F7 лимиты: {(pacing.Enabled?"временно сняты":"исходные")}; сейчас VSync={QualitySettings.vSyncCount}, cap={Application.targetFrameRate}.\n"+
                progress+"\n\n"+ResultLine(0)+"\n"+ResultLine(1)+"\n"+ResultLine(2)+"\n"+ResultLine(3)+"\n"+ResultLine(4)+"\n\n"+
                $"Многослойные чанки: проверено {pairedChunks}, не готовы/несовместимы {unpairedChunks}. Ближние {nearChunks}, дальние {farChunks}.\n"+
                "В режиме 5 остальные слои добавляют только цвет. Допуски быстрых режимов — из LT World.\n"+
                "Исходный режим LT World может быть любым. Галочки и исходные материалы не перезаписываются.\n"+
                "FPS — wall time; ниже — CPU/GPU тайминги. N/A = данных нет, не нулевая нагрузка.\n"+
                "Меняется окраска terrain, включая его дорожные слои. Скалы/отдельные дороги/LitBlend/трава не меняются.\n"+
                "Тени, геометрия/displacement и дальний фон одинаковы. Число чанков — НЕ покрытие экрана.\n"+
                "Держите одинаковый ракурс. Динамическое разрешение/внешний лимит FPS могут скрыть разницу.";
        }
        void OnGUI()
        {
            if(!Active||samples.Running)return;
            float width=Mathf.Min(1280,Screen.width-20),height=Mathf.Min(520,Screen.height-20);
            GUI.Box(new Rect(10,10,width,height),GUIContent.none);
            GUI.Label(new Rect(20,20,width-20,height-20),panel);
        }
        void SaveReport()
        {
            if(Array.TrueForAll(results,result=>result==null)){message="Сначала запишите хотя бы один режим через F9.";nextTextUpdate=0;return;}
            try
            {
                string directory=Path.Combine(Application.persistentDataPath,"TerrainDiagnostics");
                Directory.CreateDirectory(directory);
                string prefix=Path.Combine(directory,"terrain-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff",Inv));
                var report=new StringBuilder("Local Terrain runtime A/B v6 - five layer modes\nUTC: ").Append(DateTime.UtcNow.ToString("O",Inv)).AppendLine();
                report.AppendLine($"Unity: {Application.unityVersion}; editor={Application.isEditor}; development={Debug.isDebugBuild}");
                report.AppendLine($"GPU: {SystemInfo.graphicsDeviceName}; API: {SystemInfo.graphicsDeviceType}; CPU: {SystemInfo.processorType}");
                report.AppendLine("Wall intervals include waits, NOT GPU/CPU work. CPU/GPU counters below are whole-player measurements, not terrain-only; overlapping threads must NOT be added.");
                report.AppendLine("This diagnostic attaches FrameTiming recorders only during captures; collection has overhead. The F8 overlay and its text refresh are disabled for BOTH warm-up and recording (all five modes). Frame Timing Stats/Development build/other profilers may independently keep timing collection active. No persistent Player/Quality setting edits.");
                report.AppendLine("Native/counter timings arrive delayed (normally 4 frames). Recorded after 2s + at least 8 Updates of settling; may contain the last warmup frames and omit final recording frames. Counter rows are observations, not timestamp-deduplicated native frames; DO NOT join these CSVs row-for-row.");
                report.AppendLine("Zero/invalid GPU or CPU work readings are N/A. Native timestamps are deduplicated; zero Present wait is valid only when CPU timing is available. Partial samples are reported as valid/total, not silently treated as complete.");
                report.AppendLine("Render counters cover all cameras/passes and may be unavailable in release builds. They do not count unique visible triangles. Camera.scaledPixelWidth/height and native scale are recorded, but do not prove HDRP software-upscaled target size.");
                report.AppendLine("F8 temporarily removes Unity VSync and targetFrameRate caps, F7 toggles this. Closing/disable restores previous values. Driver/compositor/VRR/external caps may remain.");
                for(int i=0;i<Modes.Length;i++)report.AppendLine($"Mode {i+1}: {Names[i]}; {ModeSettings(i)}.");
                report.AppendLine("1 = original full layers; 2 = fast soft-pruned layers; 3 = top two AFTER height; 4 = top two BEFORE height; 5 = same pre-height pair + albedo-only remainder. _LTFastLayerParams.w: 0/0/0/1/2. Fast keyword OFF only in mode 1; pre-height keyword ON only in 4/5; color-remainder keyword ON only in 5. Authored LT World mode may be ordinary or fast; its checkboxes are never written.");
                report.AppendLine("Terrain material changes use temporary copies. Texture bindings and authored blend/projection tolerances stay identical; fast projection cutoff applies only in modes 2-5, soft pruning only in mode 2. Road requirements are supplied from actual chunk data, including when the authored source is ordinary. Natural far atlases unchanged; rocks/separate road meshes/LitBlend/vegetation untouched. Terrain road layers participate. Tessellation/displacement geometry continues using its full-layer path; only fragment shading changes.");
                report.AppendLine("No shadow or LOD override. Material distance selection uses fixed reference bounds. verifiedMultilayerChunks counts actual compatible bound materials, NOT visible pixels; base-only chunks do not validate the experiment. Zero supported chunks or any unsupported near multilayer material cancels capture.");
                report.AppendLine("Geometry counts read the actually assigned MeshFilter.sharedMesh index buffers (all triangle submeshes) at capture boundaries; NOT currentLOD (spatial LOD reports 0). Frustum counts are whole-chunk AABB candidates for this camera, NOT occlusion-tested/visible GPU triangles, and exclude shadow/reflection passes. Vertex bank count is not unique referenced vertices. LOD changes triangle size/rasterization and depth too: a gain supports geometry-path cost, not exclusively vertex shader cost.");
                report.AppendLine("Near/far counts are world chunk assignments, NOT visible pixels. Test a nearby blended surface. Missing modes are not zero.");
                report.AppendLine("Only the latest completed capture of each mode is retained. Save before repeating mode 1 if comparing drift. This report alone does not identify individual GPU passes or prove a bottleneck.");
                var csv=new StringBuilder("mode,frame,wall_ms\n");
                var counters=new StringBuilder("mode,observation,observed_unity_frame");
                foreach(var metric in LTTerrainFrameMetrics.Metrics)counters.Append(',').Append(metric.name).Append(" (").Append(metric.unit).Append(')');
                counters.Append('\n');
                var native=new StringBuilder("mode,frame_start_timestamp,cpu_total_ms,cpu_main_ms,cpu_render_ms,gpu_ms,present_wait_ms,sync_interval,width_scale,height_scale\n");
                for(int mode=0;mode<results.Length;mode++)
                {
                    report.AppendLine(ResultLine(mode));var result=results[mode];if(result==null)continue;
                    report.AppendLine(result.context);
                    report.AppendLine(result.geometry.ToString());
                    report.AppendLine(result.metrics.summary);
                    result.metrics.AppendCounters(counters,mode+1);result.metrics.AppendNative(native,mode+1);
                    for(int i=0;i<result.frames.Length;i++)csv.Append(mode+1).Append(',').Append(i).Append(',').Append(result.frames[i].ToString("R",Inv)).Append('\n');
                }
                File.WriteAllText(prefix+".txt",report.ToString(),new UTF8Encoding(false));
                File.WriteAllText(prefix+".csv",csv.ToString(),new UTF8Encoding(false));
                File.WriteAllText(prefix+"-counters.csv",counters.ToString(),new UTF8Encoding(false));
                File.WriteAllText(prefix+"-timings.csv",native.ToString(),new UTF8Encoding(false));
                message="Сохранено: "+prefix+".txt";Debug.Log("[Local Terrain A/B] "+message);
            }
            catch(Exception error){message="Не удалось сохранить отчёт: "+error.Message;Debug.LogWarning("[Local Terrain A/B] "+message);}
            nextTextUpdate=0;
        }
    }
}
