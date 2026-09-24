using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace LocalTerrainPrototype
{
    // User-initiated, bounded capture. Never enters Play Mode or changes scene/render settings.
    public sealed class LTDetailProfileCapture : EditorWindow
    {
        LTDetailRenderer targetRenderer;
        Camera gameCamera;
        bool running,recording,savedCpu,savedGpu,savedEditor;
        double started,nextRepaint;
        string savedLog,message="Включите Play Mode, дождитесь загрузки детализации и зафиксируйте камеру.",rawPath;
        bool savedBinary,moved,changed,generationObserved;
        int startFrame,connection,initialInstances,initialCells;
        Matrix4x4 cameraPose,cameraProjection;
        string initialSettings;
        StringBuilder notes;

        [MenuItem("Tools/Local Terrain/Detail Profile Capture")]
        public static void Open()=>GetWindow<LTDetailProfileCapture>("Detail Profile");
        public static void OpenFor(LTDetailRenderer renderer)
        {
            var window=GetWindow<LTDetailProfileCapture>("Detail Profile");
            if(!window.running){window.targetRenderer=renderer;window.gameCamera=Camera.main;}
        }
        void OnEnable()
        {
            EditorApplication.update+=Tick;
            EditorApplication.playModeStateChanged+=PlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload+=BeforeReload;
        }
        void OnDisable()
        {
            Finish("Остановлено: окно закрыто.");
            EditorApplication.update-=Tick;
            EditorApplication.playModeStateChanged-=PlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload-=BeforeReload;
        }
        void BeforeReload()=>Finish("Остановлено: перекомпиляция.");
        void PlayModeChanged(PlayModeStateChange state)
        {if(state!=PlayModeStateChange.EnteredPlayMode)Finish("Остановлено: смена Play Mode.");}
        void OnGUI()
        {
            EditorGUILayout.HelpBox("2 секунды подготовки + 10 секунд записи CPU/GPU в .raw. Настройки сцены не меняются. Deep Profile должен быть выключен, текущая запись Profiler — остановлена, Target — Play Mode текущего Editor. GPU-данные могут быть недоступны на выбранном API: это не нулевая нагрузка.",MessageType.Info);
            using(new EditorGUI.DisabledScope(running))
            {
                targetRenderer=(LTDetailRenderer)EditorGUILayout.ObjectField("Detail Renderer",targetRenderer,typeof(LTDetailRenderer),true);
                gameCamera=(Camera)EditorGUILayout.ObjectField("Game Camera",gameCamera,typeof(Camera),true);
                if(GUILayout.Button("Найти в сцене"))
                {targetRenderer=UnityEngine.Object.FindFirstObjectByType<LTDetailRenderer>();gameCamera=Camera.main;}
                if(GUILayout.Button("Записать профиль — 10 секунд"))StartCapture();
            }
            if(running&&GUILayout.Button("Остановить запись"))Finish("Остановлено пользователем.");
            EditorGUILayout.HelpBox(message,MessageType.Info);
            if(!running&&!string.IsNullOrEmpty(rawPath)&&File.Exists(rawPath))
            {
                EditorGUILayout.SelectableLabel(rawPath,EditorStyles.wordWrappedLabel,GUILayout.Height(48));
                if(GUILayout.Button("Показать файлы записи"))EditorUtility.RevealInFinder(rawPath);
                EditorGUILayout.LabelField("В Profiler: Load → этот .raw → CPU Timeline.",EditorStyles.wordWrappedLabel);
            }
        }
        void StartCapture()
        {
            if(running)return;
            if(!EditorApplication.isPlaying||EditorApplication.isPaused||EditorApplication.isCompiling)
            {message="Нужен работающий Play Mode, без паузы и компиляции.";return;}
            if(!targetRenderer||!targetRenderer.isActiveAndEnabled||!gameCamera||!gameCamera.isActiveAndEnabled||gameCamera.cameraType!=CameraType.Game)
            {message="Назначьте активные Detail Renderer и Game Camera.";return;}
            if(targetRenderer.IsGenerating||targetRenderer.CellCount==0||targetRenderer.CellCount<targetRenderer.WantedCellCount)
            {message="Сначала дождитесь окончания загрузки ячеек.";return;}
            if(Profiler.enabled||ProfilerDriver.enabled||Profiler.enableBinaryLog||ProfilerDriver.deepProfiling)
            {message="Остановите текущую запись в Profiler и выключите Deep Profile / binary logging. Чужую запись не перезаписываем.";return;}
            connection=ProfilerDriver.connectedProfiler;
            var identifier=ProfilerDriver.GetConnectionIdentifier(connection);
            if(string.IsNullOrEmpty(identifier)||!identifier.StartsWith("Editor",StringComparison.OrdinalIgnoreCase))
            {message="Выберите текущий Editor / Play Mode в Target окна Profiler, не подключённый Player.";return;}
            savedCpu=ProfilerDriver.IsAreaEnabled(ProfilerArea.CPU);
            savedGpu=ProfilerDriver.IsAreaEnabled(ProfilerArea.GPU);
            savedEditor=ProfilerDriver.profileEditor;
            savedLog=Profiler.logFile;savedBinary=Profiler.enableBinaryLog;
            running=true;recording=false;moved=false;changed=false;generationObserved=false;notes=null;rawPath=null;
            try
            {
                var folder=Path.GetFullPath(Path.Combine(Application.dataPath,"../Logs/DetailProfiles"));
                Directory.CreateDirectory(folder);
                var id=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,8);
                rawPath=Path.Combine(folder,"details-"+id+".raw");
                initialSettings=JsonUtility.ToJson(targetRenderer);
                initialInstances=targetRenderer.InstanceCount;initialCells=targetRenderer.CellCount;
                cameraPose=gameCamera.transform.localToWorldMatrix;cameraProjection=gameCamera.nonJitteredProjectionMatrix;
                notes=new StringBuilder().AppendLine("Unity detail profile — Editor Play Mode; not standalone or GPU benchmark")
                    .AppendLine("UTC: "+DateTime.UtcNow.ToString("O"))
                    .AppendLine("Unity: "+Application.unityVersion).AppendLine("GPU: "+SystemInfo.graphicsDeviceName)
                    .AppendLine("API: "+SystemInfo.graphicsDeviceType).AppendLine("CPU: "+SystemInfo.processorType)
                    .AppendLine("Camera: "+gameCamera.name+"; size: "+gameCamera.pixelWidth+"x"+gameCamera.pixelHeight)
                    .AppendLine("VSync: "+QualitySettings.vSyncCount+"; FPS cap: "+Application.targetFrameRate)
                    .AppendLine("Scene View detail rendering: "+targetRenderer.sceneView)
                    .AppendLine("Initial detail settings: "+initialSettings)
                    .AppendLine("Start instances/cells: "+initialInstances+" / "+initialCells)
                    .AppendLine("CPU: LT.Details.Submit includes preparation + LT.Details.GraphicsSubmit (native calls/waits, NOT GPU time).")
                    .AppendLine("GPU module requested; unavailable samples are NOT 0 ms. Profile includes all active cameras.");
                ProfilerDriver.profileEditor=false;
                ProfilerDriver.SetAreaEnabled(ProfilerArea.CPU,true);
                ProfilerDriver.SetAreaEnabled(ProfilerArea.GPU,true);
                started=EditorApplication.timeSinceStartup;nextRepaint=0;
                message="Подготовка: вернитесь в Game View, не двигайте камеру. Запись начнётся через 2 секунды.";
            }
            catch(Exception e){Finish("Ошибка подготовки: "+e.Message);}
        }
        void Tick()
        {
            if(!running)return;
            try
            {
                if(!EditorApplication.isPlaying||EditorApplication.isPaused||!targetRenderer||!gameCamera||
                    !targetRenderer.isActiveAndEnabled||!gameCamera.isActiveAndEnabled)
                {Finish("Остановлено: пауза, камера или рендерер недоступны.");return;}
                if(ProfilerDriver.connectedProfiler!=connection)
                {Finish("Остановлено: смена подключения Profiler.");return;}
                // Auto Refresh also scans unchanged cells via the generation iterator.
                // Keep recording that real cost; do not abort every one-second refresh.
                generationObserved|=targetRenderer.IsGenerating;
                if(!recording&&(Profiler.enabled||ProfilerDriver.enabled||Profiler.enableBinaryLog))
                {Finish("Остановлено: во время подготовки начата другая запись Profiler.");return;}
                if(ProfilerDriver.deepProfiling)
                {Finish("Остановлено: включён Deep Profile, результаты будут несопоставимы.");return;}
                double elapsed=EditorApplication.timeSinceStartup-started;
                if(!recording&&elapsed>=2)
                {
                    Profiler.logFile=rawPath;Profiler.enableBinaryLog=true;
                    recording=true;startFrame=Time.frameCount;
                    Profiler.enabled=true;
                }
                if(recording)
                {
                    moved|=cameraPose!=gameCamera.transform.localToWorldMatrix||cameraProjection!=gameCamera.nonJitteredProjectionMatrix;
                    changed|=initialCells!=targetRenderer.CellCount||initialInstances!=targetRenderer.InstanceCount;
                    if(elapsed>=12){Finish("Запись завершена.");return;}
                }
                if(EditorApplication.timeSinceStartup>=nextRepaint)
                {
                    nextRepaint=EditorApplication.timeSinceStartup+.5;
                    if(recording)message="Запись… осталось "+Math.Max(0,12-elapsed).ToString("F0")+" с. Камеру не двигать.";
                    Repaint();
                }
            }
            catch(Exception e){Finish("Ошибка записи: "+e.Message);}
        }
        void Finish(string reason)
        {
            if(!running)return;
            running=false;
            try
            {
                if(recording){Profiler.enabled=false;Profiler.enableBinaryLog=false;Profiler.logFile="";}
            }
            finally
            {
                if(recording){Profiler.logFile=savedLog;Profiler.enableBinaryLog=savedBinary;}
                // Do not change a newly selected remote Player's profiler configuration.
                if(ProfilerDriver.connectedProfiler==connection)
                {
                    ProfilerDriver.profileEditor=savedEditor;
                    ProfilerDriver.SetAreaEnabled(ProfilerArea.CPU,savedCpu);
                    ProfilerDriver.SetAreaEnabled(ProfilerArea.GPU,savedGpu);
                }
            }
            try
            {
                bool settingsChanged=targetRenderer&&initialSettings!=JsonUtility.ToJson(targetRenderer);
                if(notes!=null)
                {
                    notes.AppendLine(reason).AppendLine("Recorded game frames: "+(recording?Time.frameCount-startFrame:0))
                        .AppendLine("Camera moved: "+moved+"; population changed: "+changed+"; renderer settings changed: "+settingsChanged)
                        .AppendLine("Background generation/unchanged-cell scan observed: "+generationObserved)
                        .AppendLine("Raw file exists: "+File.Exists(rawPath));
                    File.WriteAllText(Path.ChangeExtension(rawPath,"txt"),notes.ToString());
                }
                message=reason+(File.Exists(rawPath)?" Файлы сохранены в Logs/DetailProfiles.":" Файл профиля не создан.");
                if(moved||changed||settingsChanged)message+=" Условия менялись: запись не подходит для строгого A/B.";
            }
            catch(Exception e){message=reason+" Не удалось сохранить описание: "+e.Message;}
            recording=false;Repaint();
        }
    }
}
