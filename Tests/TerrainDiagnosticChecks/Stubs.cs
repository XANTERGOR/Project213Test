// Managed test doubles only. Not Unity shader, SRP Batcher or graphics validation.
using System;
using System.Collections.Generic;
using System.Linq;
namespace UnityEngine
{
    public class Object
    {
        public static readonly List<Object> All=new();
        public static int findCalls;
        int id;
        public Object(){All.Add(this);id=All.Count;}
        public int GetInstanceID()=>id;
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive,FindObjectsSortMode sort) where T:Object
        {findCalls++;return All.OfType<T>().Where(obj=>!obj.destroyed).ToArray();}
        public string name;
        public HideFlags hideFlags;
        public bool destroyed;
        public static implicit operator bool(Object obj)=>obj!=null&&!obj.destroyed;
        public static void Destroy(Object obj){obj.destroyed=true;}
        public static void DestroyImmediate(Object obj){obj.destroyed=true;}
        public static void DontDestroyOnLoad(Object obj){}
    }
    public enum HideFlags {HideAndDontSave}
    public enum FindObjectsInactive {Include,Exclude}
    public enum FindObjectsSortMode {None}
    public enum RuntimeInitializeLoadType {SubsystemRegistration,AfterSceneLoad}
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute {public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type){}}
    public sealed class AddComponentMenu : Attribute {public AddComponentMenu(string text){}}
    public class Component : Object
    {
        public GameObject gameObject;
        public Component(){gameObject=new GameObject("fixture");gameObject.Attach(this);}
        public T GetComponent<T>() where T:Component=>gameObject.components.OfType<T>().FirstOrDefault(c=>!c.destroyed);
    }
    public class MonoBehaviour : Component {}
    public class GameObject : Object
    {
        public int layer;
        public static bool failAddComponent;
        public readonly List<Component> components=new();
        public GameObject(string name){this.name=name;}
        public void Attach(Component component){component.gameObject=this;components.Add(component);}
        public T AddComponent<T>() where T:Component,new(){if(failAddComponent)throw new InvalidOperationException("fixture failure");var value=new T();Attach(value);return value;}
    }
    public enum LightShadows {None,Hard,Soft}
    public class Light : Component {public LightShadows shadows;public bool enabled=true;public float intensity=1;}
    public class Shader : Object
    {
        static readonly Dictionary<string,int> ids=new();
        public static int PropertyToID(string name){if(!ids.ContainsKey(name))ids[name]=ids.Count+1;return ids[name];}
    }
    public class Material : Object
    {
        public Shader shader;
        public int copies;
        Dictionary<int,float> values=new();
        Dictionary<int,Vector4> vectors=new();
        HashSet<string> keywords=new();
        public Material(Shader shader){this.shader=shader;}
        public Material(Material source){shader=source.shader;CopyPropertiesFromMaterial(source);}
        public void CopyPropertiesFromMaterial(Material source){values=new(source.values);vectors=new(source.vectors);keywords=new(source.keywords);copies++;}
        public bool HasProperty(int id)=>values.ContainsKey(id)||vectors.ContainsKey(id);
        public void SetVector(int id,Vector4 value){vectors[id]=value;}
        public Vector4 GetVector(int id)=>vectors.GetValueOrDefault(id);
        public void SetFloat(int id,float value){values[id]=value;}
        public float GetFloat(int id)=>values.GetValueOrDefault(id);
        public void EnableKeyword(string key){keywords.Add(key);}
        public void DisableKeyword(string key){keywords.Remove(key);}
        public bool IsKeywordEnabled(string key)=>keywords.Contains(key);
    }
    public class MeshRenderer : Component {public Material sharedMaterial;public Bounds bounds;public bool enabled=true,forceRenderingOff;}
    public class MeshFilter : Component {public Mesh sharedMesh;}
    public enum MeshTopology {Triangles,Lines}
    public class Mesh : Object
    {
        public uint[] indices={300};public MeshTopology[] topology={MeshTopology.Triangles};public int vertexCount=100;
        public int subMeshCount=>indices.Length;
        public uint GetIndexCount(int sub)=>indices[sub];
        public MeshTopology GetTopology(int sub)=>topology[sub];
    }
    public struct Bounds {public bool outside;public float distance;public float SqrDistance(Vector3 p)=>distance;}
    public struct Plane {}
    public static class GeometryUtility
    {
        public static Plane[] CalculateFrustumPlanes(Camera camera)=>Array.Empty<Plane>();
        public static bool TestPlanesAABB(Plane[] planes,Bounds bounds)=>!bounds.outside;
    }
    public struct Vector4 {public float x,y,z,w;public Vector4(float x,float y,float z,float w){this.x=x;this.y=y;this.z=z;this.w=w;}}
    public struct Vector3
    {
        public float x,y,z;
        public static float Distance(Vector3 a,Vector3 b)=>(float)Math.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y)+(a.z-b.z)*(a.z-b.z));
    }
    public struct Quaternion {public float x,y,z,w;public static float Angle(Quaternion a,Quaternion b)=>Math.Abs(a.y-b.y);}
    public struct Matrix4x4 {public float first;public float this[int i]=>i==0?first:0;}
    public class Transform : Object
    {
        public Vector3 position;public Quaternion rotation;
        public readonly List<Component> children=new();
        public T[] GetComponentsInChildren<T>() where T:Component=>children.OfType<T>().Where(c=>c).ToArray();
    }
    public enum CameraType {Game,Preview}
    public class Camera : Component
    {
        public static Camera main=new(){name="GameCamera"};
        public static Camera[] allCameras=>new[]{main};
        public bool isActiveAndEnabled=true,allowDynamicResolution;
        public CameraType cameraType;
        public Object targetTexture;
        public int pixelWidth=1280,pixelHeight=720;
        public int cullingMask=-1;
        public int scaledPixelWidth=>pixelWidth;public int scaledPixelHeight=>pixelHeight;
        public Transform transform=new();
        public Matrix4x4 nonJitteredProjectionMatrix;
    }
    public static class Application
    {
        public static bool isPlaying=true,isEditor=false;
        public static int targetFrameRate=-1;
        public static string persistentDataPath="",unityVersion="managed-double";
    }
    public struct RefreshRate {public double value;}
    public struct Resolution {public RefreshRate refreshRateRatio;}
    public static class Screen {public static int width=1280,height=720;public static Resolution currentResolution=new(){refreshRateRatio=new(){value=180}};}
    public static class QualitySettings {public static int vSyncCount=0;public static int GetQualityLevel()=>1;}
    public static class ScalableBufferManager {public static float widthScaleFactor=1,heightScaleFactor=1;}
    public static class Time {public static int frameCount;public static double realtimeSinceStartupAsDouble;}
    public static class SystemInfo
    {
        public static Rendering.GraphicsDeviceType graphicsDeviceType=Rendering.GraphicsDeviceType.Direct3D12;
        public static string graphicsDeviceName="double",processorType="double";
    }
    public static class Debug {public static bool isDebugBuild=true;public static void Log(object value){}public static void LogWarning(object value){}}
    public static class Mathf {public static float Abs(float value)=>Math.Abs(value);public static float Min(float a,float b)=>Math.Min(a,b);}
    public struct Rect {public Rect(float x,float y,float w,float h){}}
    public class GUIContent {public static GUIContent none=new();}
    public static class GUI {public static int calls;public static void Box(Rect r,GUIContent content){calls++;}public static void Label(Rect r,string text){calls++;}}
    public struct FrameTiming
    {
        public ulong frameStartTimestamp;
        public double cpuFrameTime,cpuMainThreadFrameTime,cpuRenderThreadFrameTime,cpuMainThreadPresentWaitTime,gpuFrameTime;
        public uint syncInterval;public float widthScale,heightScale;
    }
    public static class FrameTimingManager
    {
        public static FrameTiming[] data=Array.Empty<FrameTiming>();
        public static bool fail;
        public static void CaptureFrameTimings(){if(fail)throw new InvalidOperationException();}
        public static uint GetLatestTimings(uint count,FrameTiming[] target){int size=Math.Min((int)count,data.Length);Array.Copy(data,target,size);return (uint)size;}
    }
}
namespace Unity.Profiling
{
    public struct ProfilerCategory {public static ProfilerCategory Internal,Render,Memory;}
    public struct ProfilerRecorder : IDisposable
    {
        public static readonly Dictionary<string,long> Values=new();
        public static readonly HashSet<string> Missing=new();
        public static int live;
        sealed class State {public string name;public bool running=true,disposed;public int count;public long last;}
        static readonly List<State> states=new();
        State state;
        public bool Valid=>state!=null&&!state.disposed;
        public bool IsRunning=>Valid&&state.running;
        public int Count=>Valid?state.count:0;
        public long LastValue=>Valid?state.last:0;
        // Samples become readable only at frame end, and only for RUNNING
        // handles. Copies of the struct share the same native-like state.
        public static void EndFrame()
        {foreach(var s in states)if(!s.disposed&&s.running&&Values.TryGetValue(s.name,out var value)){s.count=1;s.last=value;}}
        public static ProfilerRecorder StartNew(ProfilerCategory category,string name,int capacity)
        {if(Missing.Contains(name))return default;live++;var s=new State{name=name};states.Add(s);return new(){state=s};}
        public void Reset(){if(Valid){state.running=false;state.count=0;state.last=0;}}
        public void Dispose(){if(Valid){live--;state.disposed=true;state.running=false;}}
    }
}
namespace UnityEngine.Rendering
{
    public enum GraphicsDeviceType {Null,Direct3D12}
    public struct ScriptableRenderContext {}
    public static class RenderPipelineManager
    {
        public static object currentPipeline=new HighDefinition.HDRenderPipeline();
        public static event Action<ScriptableRenderContext,UnityEngine.Camera> endCameraRendering;
        public static void End(UnityEngine.Camera camera)=>endCameraRendering?.Invoke(default,camera);
        public static event Action<ScriptableRenderContext,List<UnityEngine.Camera>> beginContextRendering;
        public static void Render(params UnityEngine.Camera[] cameras)=>beginContextRendering?.Invoke(default,new(cameras));
    }
    public class DynamicResolutionHandler
    {
        public static DynamicResolutionHandler instance=new();public float scale=1;
        public float GetCurrentScale()=>scale;
    }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene {}
    public enum LoadSceneMode {Single,Additive}
    public static class SceneManager
    {
        public static event Action<Scene,LoadSceneMode> sceneLoaded;
        public static void Load()=>sceneLoaded?.Invoke(default,LoadSceneMode.Additive);
    }
}
namespace UnityEngine.Rendering.HighDefinition
{
    public class HDRenderPipeline {}
    public enum FrameSettingsField {ShadowMaps=20,ContactShadows=21,ScreenSpaceShadows=34,Shadowmask=22,SSAO=23}
    public struct Bits
    {
        ulong data;
        public bool this[uint index] {get=>(data&(1UL<<(int)index))!=0;set {if(value)data|=1UL<<(int)index;else data&=~(1UL<<(int)index);}}
    }
    public struct FrameSettingsOverrideMask {public Bits mask;}
    public struct FrameSettings
    {
        Bits bits;
        public bool IsEnabled(FrameSettingsField field)=>bits[(uint)field];
        public void SetEnabled(FrameSettingsField field,bool value){bits[(uint)field]=value;}
    }
    public class HDAdditionalCameraData : UnityEngine.Component
    {
        public bool customRenderingSettings;
        FrameSettings settings;
        public ref FrameSettings renderingPathCustomFrameSettings=>ref settings;
        public FrameSettingsOverrideMask renderingPathCustomFrameSettingsOverrideMask;
    }
    public class BoolScalableSettingValue {public bool useOverride,@override;public int level;}
    public class HDAdditionalLightData : UnityEngine.Component {public readonly BoolScalableSettingValue useContactShadow=new();}
}
namespace UnityEngine.InputSystem
{
    public sealed class Key {public bool wasPressedThisFrame;}
    public class Keyboard
    {
        public static Keyboard current=new();
        public Key f7Key=new(),f8Key=new(),f9Key=new(),f10Key=new(),digit1Key=new(),digit2Key=new(),digit3Key=new(),digit4Key=new(),digit5Key=new(),digit6Key=new(),digit7Key=new();
    }
}
namespace LocalTerrainPrototype
{
    public class LTChunk : UnityEngine.Component
    {
        public int x,z,currentLOD;public bool lodsPending,pinned;
        public UnityEngine.Mesh[] lodMeshes=Array.Empty<UnityEngine.Mesh>();
        public UnityEngine.Mesh mesh;
        public LTSpatialLODAsset ActiveSpatialLOD;
    }
    public class LTSpatialLODAsset : UnityEngine.Object {public UnityEngine.Mesh vertexBank;public LTSpatialLODPatch[] patches;}
    public class LTSpatialLODPatch {public object[] levels=new object[5];}
    public class FixtureTopology {public int level;public int Requested(int id,int p)=>level;}
    public class FixtureSchedule {public int invalidations;public void Invalidate(){invalidations++;}}
    public sealed partial class LTWorld : UnityEngine.MonoBehaviour
    {
        public bool isActiveAndEnabled=true,enableLODs=true,UseSpatialLODs,spatialFailed;
        public int forceLOD=-1,updates;
        public string spatialLODStatus;
        public UnityEngine.Transform generatedRoot=new();
        public FixtureTopology spatialTopology=new();
        readonly FixtureSchedule lodCameraSchedule=new();
        UnityEngine.Camera ResolveLODCamera()=>UnityEngine.Camera.main;
        int ChunkId(int x,int z)=>x+z;
        public bool IsRegularMaskGridChunk(int x,int z)=>generatedRoot.GetComponentsInChildren<LTChunk>().Any(c=>c.x==x&&c.z==z&&c.pinned);
        // Fixture only: NOT an implementation test of real spatial balancing.
        void UpdateLOD(UnityEngine.Camera camera,bool beforeRender)
        {
            updates++;int level=enableLODs?Math.Max(0,TerrainForcedLOD):0;spatialTopology.level=level;
            foreach(var c in generatedRoot.GetComponentsInChildren<LTChunk>())
            {
                int selected=c.pinned||c.lodsPending?0:Math.Min(level,c.lodMeshes.Length);
                c.currentLOD=UseSpatialLODs?0:selected;
                var f=c.GetComponent<UnityEngine.MeshFilter>();if(f)f.sharedMesh=selected==0?c.mesh:c.lodMeshes[selected-1];
            }
        }
    }
}
