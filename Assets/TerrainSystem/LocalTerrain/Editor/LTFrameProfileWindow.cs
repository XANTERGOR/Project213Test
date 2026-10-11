using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    // User-operated diagnostic only. No scene writes, camera motion, rendering-quality changes,
    // automatic Play Mode, clearing/loading old Profiler data or standalone-player launch.
    public sealed class LTFrameProfileWindow : EditorWindow
    {
        enum Phase { Idle, Countdown, Recording, Draining, Analyzing }
        enum Target { SceneView, GameView }
        const double PreparationSeconds = 3, CaptureSeconds = 5;
        static readonly ProfilerArea[] Areas = { ProfilerArea.CPU, ProfilerArea.GPU, ProfilerArea.Rendering, ProfilerArea.Memory };
        readonly bool[] savedAreas = new bool[Areas.Length];
        readonly List<int> received = new List<int>(2048);
        readonly HashSet<int> receivedSet = new HashSet<int>();
        [SerializeField] Target target;
        [SerializeField] Camera gameCamera;
        [SerializeField] bool gpu = true;
        [SerializeField] string reportPath;
        string message = "Нажмите запись, вернитесь в Scene/Game View и летайте камерой.", prefix;
        Phase phase;
        bool savedEditor, leased, recordingOwned, playingAtStart, invalidated, nativeSaveAttempted;
        int connection, analyzeAt, analyzedSamples;
        double deadline, began, nextRepaint;
        LTFrameProfileReport report;
        Camera observedCamera;
        Vector3 lastPosition;
        Quaternion lastRotation;
        double movedDistance, rotatedDegrees;
        int cameraObservations, cameraWidth, cameraHeight;

        [MenuItem("Tools/Local Terrain/Frame Profiler (5 seconds)")]
        public static void Open() => GetWindow<LTFrameProfileWindow>("Frame Profiler");
        void OnEnable()
        {
            minSize = new Vector2(420, 320);
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += BeforeQuit;
            ProfilerDriver.NewProfilerFrameRecorded += NewFrame;
            ProfilerDriver.profileCleared += HistoryChanged;
            ProfilerDriver.profileLoaded += HistoryChanged;
        }
        void OnDisable()
        {
            Abort("Окно закрыто; запись прервана.");
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            EditorApplication.quitting -= BeforeQuit;
            ProfilerDriver.NewProfilerFrameRecorded -= NewFrame;
            ProfilerDriver.profileCleared -= HistoryChanged;
            ProfilerDriver.profileLoaded -= HistoryChanged;
        }
        void BeforeReload() => Abort("Перекомпиляция; запись прервана.");
        void BeforeQuit() => Abort("Закрытие Unity; запись прервана.");
        void PlayModeChanged(PlayModeStateChange _) => Abort("Смена Play Mode; запись прервана.");
        void HistoryChanged()
        { if (phase == Phase.Recording || phase == Phase.Draining || phase == Phase.Analyzing) invalidated = true; }
        void OnGUI()
        {
            EditorGUILayout.HelpBox("3 секунды подготовки + 5 секунд полёта. Сохраняет .txt, CSV и профиль Unity в Logs/FrameProfiles. Сцену не сохраняет. Отчёт разбирается после остановки, не во время полёта.", MessageType.Info);
            using (new EditorGUI.DisabledScope(phase != Phase.Idle))
            {
                target = (Target)EditorGUILayout.EnumPopup("Где летать", target);
                if (target == Target.GameView)
                    gameCamera = (Camera)EditorGUILayout.ObjectField("Камера (или MainCamera)", gameCamera, typeof(Camera), true);
                gpu = EditorGUILayout.Toggle("Записывать GPU", gpu);
                EditorGUILayout.HelpBox("GPU-запись зависит от API/драйвера и добавляет нагрузку; может влиять на Graphics Jobs. Для сравнения CPU можно повторить без GPU. Deep Profile и запись стэков должны быть выключены.", MessageType.None);
                if (GUILayout.Button("Начать — 5 секунд", GUILayout.Height(32))) StartCapture();
            }
            if (phase != Phase.Idle && GUILayout.Button("Остановить и сохранить частичный результат")) StopRequested();
            EditorGUILayout.HelpBox(message, MessageType.Info);
            if (!string.IsNullOrEmpty(reportPath) && File.Exists(reportPath))
            {
                EditorGUILayout.SelectableLabel(reportPath, EditorStyles.wordWrappedLabel, GUILayout.Height(45));
                if (GUILayout.Button("Показать отчёт в папке")) EditorUtility.RevealInFinder(reportPath);
                if (GUILayout.Button("Скопировать путь отчёта")) EditorGUIUtility.systemCopyBuffer = reportPath;
            }
        }
        void StartCapture()
        {
            if (phase != Phase.Idle) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPaused ||
                EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
            { message = "Дождитесь окончания импорта/компиляции и снимите паузу."; return; }
            if (ProfilerDriver.enabled || Profiler.enabled || Profiler.enableBinaryLog || ProfilerDriver.deepProfiling ||
                ProfilerDriver.memoryRecordMode != ProfilerMemoryRecordMode.None)
            { message = "Остановите текущую запись Profiler, выключите Deep Profile, Call Stacks и binary logging. Чужую запись не прерываем."; return; }
            connection = ProfilerDriver.connectedProfiler;
            string identifier = ProfilerDriver.GetConnectionIdentifier(connection);
            if (string.IsNullOrEmpty(identifier) || !identifier.StartsWith("Editor", StringComparison.OrdinalIgnoreCase))
            { message = "В Profiler выберите текущий Editor, не внешний Player. Подключение автоматически не переключается."; return; }
            observedCamera = target == Target.SceneView ? (SceneView.lastActiveSceneView ? SceneView.lastActiveSceneView.camera : null) :
                (gameCamera ? gameCamera : Camera.main);
            if (!observedCamera || (target == Target.GameView && (!EditorApplication.isPlaying || !observedCamera.isActiveAndEnabled)))
            { message = target == Target.SceneView ? "Сначала откройте Scene View." : "Для Game View нужен Play Mode и активная камера."; return; }
            received.Clear(); receivedSet.Clear(); invalidated = nativeSaveAttempted = false; analyzeAt = 1; analyzedSamples = 0;
            movedDistance = rotatedDegrees = 0; cameraObservations = 0;
            playingAtStart = EditorApplication.isPlaying;
            report = new LTFrameProfileReport();
            reportPath = null; prefix = null;
            phase = Phase.Countdown; deadline = EditorApplication.timeSinceStartup + PreparationSeconds; nextRepaint = 0;
            message = "Через 3 секунды начнётся запись. Вернитесь в " + target + " и летайте камерой.";
            Repaint();
        }
        void BeginRecording()
        {
            // Check again after countdown: another window could have started profiling meanwhile.
            if (ProfilerDriver.enabled || Profiler.enabled || Profiler.enableBinaryLog)
            { Abort("Во время подготовки началась другая запись Profiler."); return; }
            var folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/FrameProfiles"));
            Directory.CreateDirectory(folder);
            prefix = Path.Combine(folder, "flight-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            report.context = Context();
            lastPosition = observedCamera.transform.position; lastRotation = observedCamera.transform.rotation;
            cameraWidth = observedCamera.pixelWidth; cameraHeight = observedCamera.pixelHeight;
            savedEditor = ProfilerDriver.profileEditor;
            for (int i = 0; i < Areas.Length; i++) savedAreas[i] = ProfilerDriver.IsAreaEnabled(Areas[i]);
            leased = true;
            ProfilerDriver.profileEditor = target == Target.SceneView;
            for (int i = 0; i < Areas.Length; i++) ProfilerDriver.SetAreaEnabled(Areas[i], Areas[i] != ProfilerArea.GPU || gpu);
            began = EditorApplication.timeSinceStartup; deadline = began + CaptureSeconds;
            phase = Phase.Recording; recordingOwned = true;
            ProfilerDriver.enabled = true;
        }
        void NewFrame(int source, int frame)
        {
            if ((phase != Phase.Recording && phase != Phase.Draining) || source != connection) return;
            if (received.Count >= 10000) { invalidated = true; return; }
            if (receivedSet.Add(frame)) received.Add(frame);
        }
        void Tick()
        {
            if (phase == Phase.Idle) return;
            try
            {
                if (invalidated || ProfilerDriver.connectedProfiler != connection)
                { Abort("История/подключение Profiler изменились; результат нельзя считать полным."); return; }
                if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPaused || playingAtStart != EditorApplication.isPlaying)
                { Abort("Импорт, компиляция, пауза или смена Play Mode; запись прервана."); return; }
                if ((phase == Phase.Draining || phase == Phase.Analyzing) &&
                    (ProfilerDriver.enabled || Profiler.enabled || Profiler.enableBinaryLog))
                { Abort("Началась другая запись Profiler; сохранена только доступная часть разбора."); return; }
                double now = EditorApplication.timeSinceStartup;
                if (phase == Phase.Countdown || phase == Phase.Recording)
                {
                    if (!observedCamera) { Abort("Камера закрыта/удалена; запись прервана."); return; }
                    if (ProfilerDriver.deepProfiling || ProfilerDriver.memoryRecordMode != ProfilerMemoryRecordMode.None)
                    { Abort("Включены Deep Profile или Call Stacks; повторите без них."); return; }
                }
                if (phase == Phase.Countdown && now >= deadline) BeginRecording();
                else if (phase == Phase.Recording)
                {
                    if (!ProfilerDriver.enabled || Profiler.enableBinaryLog)
                    { Abort("Настройки записи Profiler изменены во время теста."); return; }
                    ObserveCamera();
                    if (now >= deadline) EndRecording("Завершено", now);
                }
                else if (phase == Phase.Draining && now >= deadline) BeginAnalysis();
                else if (phase == Phase.Analyzing) AnalyzeSome();
                if (phase != Phase.Idle && now >= nextRepaint)
                {
                    nextRepaint = now + .5;
                    message = phase == Phase.Countdown ? "Подготовка… " + Math.Max(0, deadline - now).ToString("F0") + " с" :
                        phase == Phase.Recording ? "Запись… летайте! Осталось " + Math.Max(0, deadline - now).ToString("F1") + " с" :
                        phase == Phase.Draining ? "Запись остановлена. Получаю последние кадры…" : "Разбор кадров: " + Math.Max(0, analyzeAt - 1) + "/" + Math.Max(0, received.Count - 2);
                    Repaint();
                }
            }
            catch (Exception e) { Abort("Ошибка захвата: " + e.Message); }
        }
        void ObserveCamera()
        {
            movedDistance += Vector3.Distance(lastPosition, observedCamera.transform.position);
            rotatedDegrees += Quaternion.Angle(lastRotation, observedCamera.transform.rotation);
            lastPosition = observedCamera.transform.position; lastRotation = observedCamera.transform.rotation; cameraObservations++;
            if (cameraWidth != observedCamera.pixelWidth || cameraHeight != observedCamera.pixelHeight)
                LTFrameProfileReader.Note(report.notes, "Разрешение камеры менялось во время записи.");
        }
        void EndRecording(string reason, double now)
        {
            report.reason = reason; report.wallSeconds = now - began;
            phase = Phase.Draining;
            StopOwnedRecording();
            deadline = EditorApplication.timeSinceStartup + .5; // Let queued profiler frames arrive. No blocking wait.
        }
        void StopOwnedRecording()
        {
            if (!recordingOwned) return;
            recordingOwned = false;
            // Never switch or stop a newly selected remote connection.
            try { if (ProfilerDriver.connectedProfiler == connection) ProfilerDriver.enabled = false; }
            finally { Profiler.enabled = false; } // Only this local Editor's owned recording.
        }
        void RestoreSettings()
        {
            if (!leased) return;
            leased = false;
            if (ProfilerDriver.connectedProfiler != connection)
            { report.notes.Add("Подключение изменилось: настройки нового Target не тронуты."); return; }
            if (ProfilerDriver.enabled || Profiler.enabled || Profiler.enableBinaryLog)
            { report.notes.Add("Началась другая запись: её настройки не восстановлены поверх новых. При необходимости восстановите модули Profiler вручную."); return; }
            TryRestore(() => ProfilerDriver.profileEditor = savedEditor);
            for (int i = 0; i < Areas.Length; i++)
            { int slot = i; TryRestore(() => ProfilerDriver.SetAreaEnabled(Areas[slot], savedAreas[slot])); }
        }
        void TryRestore(Action restore)
        { try { restore(); } catch (Exception e) { report.notes.Add("Не удалось восстановить настройку Profiler: " + e.Message); } }
        void BeginAnalysis()
        {
            received.Sort(); report.receivedFrames = received.Count;
            SaveNativeProfile(); // Save before history can be changed by another recording.
            report.notes.Add("Движение наблюдаемой камеры: " + LTFrameProfileReport.Number(movedDistance) + " м / " +
                LTFrameProfileReport.Number(rotatedDegrees) + " градусов; наблюдений: " + cameraObservations + ". Это не замер FPS.");
            if (movedDistance < .01 && rotatedDegrees < .1) report.notes.Add("Движение выбранной камеры не обнаружено — возможно, вы летали в другом окне.");
            phase = Phase.Analyzing; analyzeAt = 1;
        }
        void SaveNativeProfile()
        {
            if (nativeSaveAttempted || prefix == null || received.Count == 0 || ProfilerDriver.connectedProfiler != connection || invalidated ||
                ProfilerDriver.enabled || Profiler.enabled || Profiler.enableBinaryLog) return;
            nativeSaveAttempted = true;
            try
            {
                bool saved = ProfilerDriver.SaveProfile(prefix + ".data");
                report.notes.Add(saved ? "Unity profile: " + prefix + ".data. Может содержать более раннюю историю; диапазон этого теста указан в frames.csv." : "Unity не сохранил исходный .data; текстовый разбор всё ещё возможен.");
            }
            catch (Exception e) { report.notes.Add("Не удалось сохранить .data: " + e.Message); }
        }
        void AnalyzeSome()
        {
            if (ProfilerDriver.enabled || Profiler.enabled)
            { Abort("Началась новая запись Profiler; разбор остановлен, сохранена доступная часть."); return; }
            double until = EditorApplication.timeSinceStartup + .012;
            while (analyzeAt < received.Count - 1 && EditorApplication.timeSinceStartup < until)
            {
                int id = received[analyzeAt++];
                try
                {
                    var frame = LTFrameProfileReader.Read(id, report.notes);
                    if (frame == null) report.missingFrames++;
                    else { report.frames.Add(frame); analyzedSamples += frame.samples.Count; }
                }
                catch (Exception e)
                { report.missingFrames++; LTFrameProfileReader.Note(report.notes, "Ошибка чтения кадра " + id + ": " + e.Message); }
                if (analyzedSamples >= 500000)
                {
                    report.missingFrames += Math.Max(0, received.Count - 1 - analyzeAt);
                    report.notes.Add("Разбор остановлен на лимите 500000 маркеров в отчёте; полные сохранённые данные доступны в .data.");
                    Publish(); return;
                }
            }
            if (analyzeAt >= received.Count - 1) Publish();
        }
        void StopRequested()
        {
            if (phase == Phase.Recording) EndRecording("Остановлено пользователем (частичный захват)", EditorApplication.timeSinceStartup);
            else Abort("Остановлено пользователем (частичный результат).");
        }
        void Abort(string reason)
        {
            if (phase == Phase.Idle) return;
            var previous = phase; phase = Phase.Idle;
            try { StopOwnedRecording(); }
            finally { RestoreSettings(); }
            report.reason = reason;
            if (previous == Phase.Recording) report.wallSeconds = EditorApplication.timeSinceStartup - began;
            report.receivedFrames = received.Count;
            if (prefix != null)
            {
                if (previous != Phase.Analyzing) SaveNativeProfile();
                report.notes.Add("Захват/анализ прерван. Отсутствие тяжёлых маркеров в частичном отчёте не означает отсутствие нагрузки.");
                Publish();
            }
            else { message = reason; Repaint(); }
        }
        void Publish()
        {
            phase = Phase.Idle;
            RestoreSettings(); // Some Unity versions invalidate views on a target/mode switch.
            report.receivedFrames = received.Count;
            report.missingFrames = Math.Max(0, received.Count - 2 - report.frames.Count);
            try
            {
                File.WriteAllText(prefix + "-frames.csv", report.FramesCsv(), Encoding.UTF8);
                File.WriteAllText(prefix + "-markers.csv", report.MarkersCsv(), Encoding.UTF8);
                reportPath = prefix + ".txt";
                File.WriteAllText(reportPath, report.Summary(), Encoding.UTF8);
                message = report.frames.Count == 0 ? "Пригодные кадры не получены. Сохранён диагностический отчёт; проверьте Target в Profiler." :
                    report.reason + ". Разобрано кадров: " + report.frames.Count + (report.missingFrames > 0 ? " (неполный захват)" : "") + ". Напишите «читай» — отчёт находится в Logs/FrameProfiles.";
                Debug.Log("[Frame Flight Profile] " + message + "\nReport: " + reportPath);
            }
            catch (Exception e) { message = "Не удалось сохранить отчёт: " + e.Message; Debug.LogWarning("[Frame Flight Profile] " + message); }
            Repaint();
        }
        string Context()
        {
            var text = new StringBuilder().AppendLine("UTC: " + DateTime.UtcNow.ToString("O"))
                .AppendLine("Unity: " + Application.unityVersion + "; mode: " + target + "; Play Mode: " + EditorApplication.isPlaying)
                .AppendLine("CPU: " + SystemInfo.processorType + "; logical cores: " + SystemInfo.processorCount)
                .AppendLine("GPU: " + SystemInfo.graphicsDeviceName + "; API: " + SystemInfo.graphicsDeviceType)
                .AppendLine("GPU requested: " + gpu + "; VSync: " + QualitySettings.vSyncCount + "; FPS cap: " + Application.targetFrameRate)
                .AppendLine("Camera: " + observedCamera.name + "; " + observedCamera.pixelWidth + "x" + observedCamera.pixelHeight)
                .AppendLine("Render pipeline: " + (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.name : "Built-in"));
            for (int i = 0; i < SceneManager.sceneCount; i++)
            { var scene = SceneManager.GetSceneAt(i); text.AppendLine("Loaded scene: " + scene.path + "; dirty: " + scene.isDirty); }
            text.AppendLine("Scene View windows: " + SceneView.sceneViews.Count + "; active Game cameras: " + Camera.allCamerasCount);
            text.AppendLine("No scene, quality, LOD, camera or FPS settings modified. Profiler history size unchanged; no ClearAllFrames/LoadProfile.");
            return text.ToString();
        }
    }
}
