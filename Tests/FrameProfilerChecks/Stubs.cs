// Managed lifecycle test doubles only. These tests are NOT a Unity/native/GPU test.
using System;
using System.Collections.Generic;
using System.IO;
namespace UnityEngine
{
    public class Object { public string name = "fixture"; public static implicit operator bool(Object o) => o != null; }
    public sealed class SerializeField : Attribute { }
    public struct Vector2 { public Vector2(float x, float y) { } }
    public struct Vector3
    { public float x; public static float Distance(Vector3 a, Vector3 b) => Math.Abs(a.x-b.x); }
    public struct Quaternion { public static float Angle(Quaternion a, Quaternion b) => 0; }
    public class Transform { public Vector3 position; public Quaternion rotation; }
    public class Camera : Object
    {
        public static Camera main = new Camera(); public static int allCamerasCount => 1;
        public bool isActiveAndEnabled = true; public int pixelWidth = 1280, pixelHeight = 720;
        public Transform transform = new Transform();
    }
    public static class Application
    { public static string dataPath = Path.GetFullPath("Logs/FrameProfilerChecks/FakeAssets"); public static string unityVersion = "managed-test"; public static int targetFrameRate = -1; }
    public static class SystemInfo
    { public static string processorType = "test", graphicsDeviceName = "none", graphicsDeviceType = "none"; public static int processorCount = 4; }
    public static class QualitySettings { public static int vSyncCount = 1; }
    public static class Debug { public static void Log(string _) { } public static void LogWarning(string _) { } }
    public static class GUILayout
    { public static object Height(float _) => null; public static bool Button(string _, params object[] options) => false; }
}
namespace UnityEngine.Rendering
{ public static class GraphicsSettings { public static UnityEngine.Object currentRenderPipeline; } }
namespace UnityEngine.SceneManagement
{
    public struct Scene { public string path; public bool isDirty; }
    public static class SceneManager { public static int sceneCount => 0; public static Scene GetSceneAt(int i) => default; }
}
namespace UnityEngine.Profiling
{
    public enum ProfilerArea { CPU, GPU, Rendering, Memory }
    public static class Profiler { public static bool enabled, enableBinaryLog; }
}
namespace UnityEditor
{
    public sealed class MenuItem : Attribute { public MenuItem(string _) { } }
    public enum MessageType { Info, None }
    public enum PlayModeStateChange { ExitingEditMode, EnteredPlayMode }
    public class EditorWindow : UnityEngine.Object
    { public UnityEngine.Vector2 minSize; public static T GetWindow<T>(string _) where T: new() => new T(); public void Repaint() { } }
    public class SceneView : UnityEngine.Object
    { public static SceneView lastActiveSceneView = new SceneView(); public UnityEngine.Camera camera = new UnityEngine.Camera(); public static List<SceneView> sceneViews = new List<SceneView>(); }
    public static class EditorApplication
    {
        public static bool isCompiling, isUpdating, isPaused, isPlaying, isPlayingOrWillChangePlaymode;
        public static double timeSinceStartup;
        public static event Action update, quitting;
        public static event Action<PlayModeStateChange> playModeStateChanged;
        public static void Tick(double t) { timeSinceStartup = t; update?.Invoke(); }
        public static void Quit() => quitting?.Invoke();
        public static void ChangePlayMode() => playModeStateChanged?.Invoke(PlayModeStateChange.ExitingEditMode);
    }
    public static class AssemblyReloadEvents
    { public static event Action beforeAssemblyReload; public static void Reload() => beforeAssemblyReload?.Invoke(); }
    public static class EditorUtility { public static void RevealInFinder(string _) { } }
    public static class EditorGUIUtility { public static string systemCopyBuffer; }
    public static class EditorStyles { public static object wordWrappedLabel; }
    public static class EditorGUI
    { public struct DisabledScope : IDisposable { public DisabledScope(bool _) { } public void Dispose() { } } }
    public static class EditorGUILayout
    {
        public static void HelpBox(string a, MessageType b) { }
        public static Enum EnumPopup(string _, Enum v) => v;
        public static UnityEngine.Object ObjectField(string a, UnityEngine.Object v, Type t, bool b) => v;
        public static bool Toggle(string _, bool v) => v;
        public static void SelectableLabel(string a, object b, params object[] c) { }
    }
}
namespace UnityEditorInternal
{
    using UnityEngine.Profiling;
    public enum ProfilerMemoryRecordMode { None, GCAlloc }
    public static class ProfilerDriver
    {
        public static bool enabled, deepProfiling, profileEditor;
        public static ProfilerMemoryRecordMode memoryRecordMode;
        public static int connectedProfiler = 7, saves;
        public static string identifier = "Editor";
        public static bool throwGpuEnable, throwSave;
        public static readonly Dictionary<ProfilerArea, bool> areas = new Dictionary<ProfilerArea, bool>();
        public static event Action<int, int> NewProfilerFrameRecorded;
        public static event Action profileCleared, profileLoaded;
        public static void Frame(int i, int connection = 7) => NewProfilerFrameRecorded?.Invoke(connection, i);
        public static void Clear() => profileCleared?.Invoke();
        public static void Load() => profileLoaded?.Invoke();
        public static string GetConnectionIdentifier(int _) => identifier;
        public static bool IsAreaEnabled(ProfilerArea a) => areas.TryGetValue(a, out var b) && b;
        public static void SetAreaEnabled(ProfilerArea a, bool value)
        { if (a == ProfilerArea.GPU && value && throwGpuEnable) throw new Exception("fixture GPU failure"); areas[a] = value; }
        public static bool SaveProfile(string path)
        { saves++; if (throwSave) throw new IOException("fixture file failure"); File.WriteAllText(path, "fixture only - not a Unity profile"); return true; }
    }
}
namespace LocalTerrainPrototype
{
    internal static class LTFrameProfileReader
    {
        internal static readonly HashSet<int> missing = new HashSet<int>();
        internal static LTFrameProfileReport.Frame Read(int i, List<string> notes) => missing.Contains(i) ? null : new LTFrameProfileReport.Frame { index = i, cpuMs = 20, gpuStatus = "fixture unavailable" };
        internal static void Note(List<string> notes, string text) { if (!notes.Contains(text)) notes.Add(text); }
    }
}
