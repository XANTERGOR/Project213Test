// Command-recording substitutes, NOT a Unity renderer, GPU or timing simulation.
using System.Globalization;
using UnityEngine.Rendering;
namespace UnityEngine
{
    public class Object
    {
        public string name="";public HideFlags hideFlags;bool destroyed;
        public static implicit operator bool(Object o)=>o is not null&&!o.destroyed;
        public static void Destroy(Object o){if(o is not null)o.destroyed=true;}
        public static void DestroyImmediate(Object o)=>Destroy(o);
        public static T Instantiate<T>(T o) where T:Object,new()=>new T{name=o.name};
    }
    public enum HideFlags{HideAndDontSave}
    public struct Vector3
    {
        public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public override string ToString()=>$"{x:R},{y:R},{z:R}";
    }
    public struct Vector4
    {
        public static Vector4 zero=>default;
        public float x,y,z,w;public Vector4(float x,float y,float z,float w){this.x=x;this.y=y;this.z=z;this.w=w;}
        public static implicit operator Vector4(Vector3 v)=>new(v.x,v.y,v.z,0);
        public override string ToString()=>$"{x:R},{y:R},{z:R},{w:R}";
    }
    public struct Matrix4x4{public int token;public static Matrix4x4 identity=>new(){token=1};public Matrix4x4 inverse=>this;public override string ToString()=>token.ToString();}
    public struct Bounds{public Vector3 center,size;public Bounds(Vector3 c,Vector3 s){center=c;size=s;}public override string ToString()=>center+"/"+size;}
    public struct Plane{public Vector3 normal;public float distance;public Plane(Vector3 n,float d){normal=n;distance=d;}}
    public class Transform{public Vector3 position,forward=new(0,0,1);}
    public class GameObject:Object{}
    public class Texture:Object{public int pixelRevision;public override string ToString()=>name;}
    public class Camera:Object{public Transform transform=new();public bool stereoEnabled,orthographic;public float orthographicSize=5,fieldOfView=60;public int cullingMask=-1;}
    public static class QualitySettings{public static float lodBias=1;public static int maximumLODLevel;}
    public static class Mathf{public const float Deg2Rad=(float)(Math.PI/180);public static float Tan(float f)=>(float)Math.Tan(f);public static float Min(float a,float b)=>Math.Min(a,b);}
    public static class Application{public static bool isPlaying;}
    public static class Time
    {
        static double now;public static int Reads;
        public static double realtimeSinceStartupAsDouble{get{Reads++;return now;}set{now=value;}}
    }
    public static class SystemInfo{public static bool supportsComputeShaders=true,supportsInstancing=true,supportsIndirectArgumentsBuffer=true;public static GraphicsDeviceType graphicsDeviceType=GraphicsDeviceType.Direct3D12;}
    public class Shader:Object
    {
        public bool isSupported=true;static readonly Dictionary<string,int> ids=new();static readonly List<string> names=new();
        public static int PropertyToID(string s){if(!ids.TryGetValue(s,out int id)){id=names.Count;names.Add(s);ids.Add(s,id);}return id;}
        public static string Name(int id)=>names[id];
    }
    public class Material:Object
    {
        Shader shaderValue;bool instancing;int queue=2000;
        public static int NativeReads,Copies,PassWrites,InstancingWrites;
        public static bool CopyPassState,CopyInstancingState,FailNextCopy;
        public static Action AfterCopy;
        public Shader shader{get{NativeReads++;return shaderValue;}set{shaderValue=value;}}
        public bool enableInstancing{get{NativeReads++;return instancing;}set{instancing=value;InstancingWrites++;}}
        public int renderQueue{get{NativeReads++;return queue;}set{queue=value;}}
        public readonly Dictionary<int,float> values=new();readonly Dictionary<string,bool> passes=new();
        public Material(Shader s){shaderValue=s;}
        public bool HasProperty(string s)=>HasProperty(Shader.PropertyToID(s));
        public bool HasProperty(int id){NativeReads++;return values.ContainsKey(id);}
        public float GetFloat(string s)=>GetFloat(Shader.PropertyToID(s));
        public float GetFloat(int id){NativeReads++;return values.TryGetValue(id,out var x)?x:0;}
        public void SetFloat(string s,float x)=>values[Shader.PropertyToID(s)]=x;
        public void CopyPropertiesFromMaterial(Material m)
        {
            Copies++;if(FailNextCopy){FailNextCopy=false;throw new InvalidOperationException("injected material copy failure");}
            values.Clear();foreach(var p in m.values)values[p.Key]=p.Value;queue=m.queue;
            if(CopyPassState){passes.Clear();foreach(var p in m.passes)passes[p.Key]=p.Value;}
            if(CopyInstancingState)instancing=m.instancing;
            AfterCopy?.Invoke();
        }
        bool PassValue(string s)=>!passes.TryGetValue(s,out bool x)||x;
        public bool GetShaderPassEnabled(string s){NativeReads++;return PassValue(s);}
        public void SetShaderPassEnabled(string s,bool x){passes[s]=x;PassWrites++;}
        public string Payload()=>shaderValue.name+":"+queue+":"+instancing+":"+Trace.Values(values.ToDictionary(p=>p.Key,p=>(object)p.Value))+":"+
            string.Join(";",new[]{"MotionVectors","MOTIONVECTORS","ShadowCaster","DepthOnly","GBuffer","Forward"}.Select(p=>p+"="+PassValue(p)));
    }
    public class Mesh:Object{public uint GetIndexCount(int sub)=>(uint)(3*(sub+1));public uint GetIndexStart(int sub)=>(uint)(3*sub);public uint GetBaseVertex(int sub)=>(uint)sub;}
    public class MaterialPropertyBlock
    {
        public static int TerrainWrites;
        void Write(int n,object value){values[n]=value;if(Shader.Name(n).StartsWith("_LTGrassTerrain",StringComparison.Ordinal))TerrainWrites++;}
        public readonly Dictionary<int,object> values=new();
        public void SetBuffer(string n,GraphicsBuffer b)=>SetBuffer(Shader.PropertyToID(n),b);public void SetBuffer(int n,GraphicsBuffer b)=>values[n]=b;
        public void SetMatrix(string n,Matrix4x4 b)=>SetMatrix(Shader.PropertyToID(n),b);public void SetMatrix(int n,Matrix4x4 b)=>values[n]=b;
        public void SetVector(string n,Vector4 b)=>SetVector(Shader.PropertyToID(n),b);public void SetVector(int n,Vector4 b)=>Write(n,b);
        public void SetFloat(string n,float b)=>SetFloat(Shader.PropertyToID(n),b);public void SetFloat(int n,float b)=>Write(n,b);
        public void SetTexture(string n,Texture b)=>SetTexture(Shader.PropertyToID(n),b);public void SetTexture(int n,Texture b)=>Write(n,b);
    }
    public class GraphicsBuffer:IDisposable
    {
        public static bool FailNextAllocation;
        public enum Target{Structured,Append,IndirectArguments}
        public struct IndirectDrawIndexedArgs{public uint indexCountPerInstance,instanceCount,startIndex,baseVertexIndex,startInstance;public static int size=>20;public override string ToString()=>$"{indexCountPerInstance},{instanceCount},{startIndex},{baseVertexIndex},{startInstance}";}
        public readonly Target target;public readonly int count,stride;public object[] data;public uint counter;public bool disposed;
        public GraphicsBuffer(Target t,int c,int s){if(FailNextAllocation){FailNextAllocation=false;throw new InvalidOperationException("injected allocation failure");}target=t;count=c;stride=s;data=new object[c];}
        public void SetData<T>(T[] a)=>SetData(a,0,0,a.Length);
        public void SetData<T>(T[] a,int src,int dst,int n){for(int i=0;i<n;i++)data[dst+i]=a[src+i];}
        public void SetCounterValue(uint x)=>counter=x;
        public static void CopyCount(GraphicsBuffer from,GraphicsBuffer to,int offset){if(offset!=4)throw new Exception("wrong count offset");var arg=(IndirectDrawIndexedArgs)to.data[0];arg.instanceCount=from.counter;to.data[0]=arg;}
        public void Dispose()=>disposed=true;
        public string Payload()=>target+":"+count+":"+stride+":"+(target==Target.Append?"visible":string.Join(";",data.Select(Trace.Value)));
    }
    public class ComputeShader:Object
    {
        public static bool MultiSupported=true;
        readonly Dictionary<int,object> values=new();public int FindKernel(string s)=>s=="CullMany"?1:0;public bool IsSupported(int i)=>i==0||MultiSupported;
        public bool HasKernel(string name)=>name=="Cull"||name=="CullMany";
        void Set(int n,object x){values[n]=x;Trace.UniformWrites++;}
        public void SetVectorArray(string n,Vector4[] x)=>SetVectorArray(Shader.PropertyToID(n),x);public void SetVectorArray(int n,Vector4[] x)=>Set(n,(Vector4[])x.Clone());
        public void SetVector(string n,Vector4 x)=>SetVector(Shader.PropertyToID(n),x);public void SetVector(int n,Vector4 x)=>Set(n,x);
        public void SetInt(string n,int x)=>SetInt(Shader.PropertyToID(n),x);public void SetInt(int n,int x)=>Set(n,x);
        public void SetBuffer(int k,string n,GraphicsBuffer x)=>SetBuffer(k,Shader.PropertyToID(n),x);public void SetBuffer(int k,int n,GraphicsBuffer x)=>Set(n,x);
        public void Dispatch(int k,int x,int y,int z)
        {
            Trace.Events.Add("dispatch:"+x+","+y+","+z+":"+Trace.Values(values));
            // Only model the count write so command traces have deterministic args.
            // Do not pretend to simulate HLSL culling, floating point or GPU timing.
            if(k==0)((GraphicsBuffer)values[Shader.PropertyToID("_LTVisible")]).counter=(uint)(int)values[Shader.PropertyToID("_Count")];
            else
            {
                var selectors=(Vector4[])values[Shader.PropertyToID("_Selections")];var distinct=new HashSet<GraphicsBuffer>();
                for(int i=0;i<8;i++)
                {
                    var buffer=(GraphicsBuffer)values[Shader.PropertyToID("_LTVisible"+i)];
                    if(!distinct.Add(buffer))throw new Exception("Aliased append UAVs");
                    if(selectors[i].w!=0)buffer.counter=(uint)(int)values[Shader.PropertyToID("_Count")];
                }
            }
        }
    }
    public static class Resources
    {
        public static readonly Shader Source=new(){name="source"},Indirect=new(){name="indirect"};
        public static T Load<T>(string path) where T:Object=>new ComputeShader{name=path} as T;
        public static T[] LoadAll<T>(string path) where T:Object=>new[]{new LocalTerrainPrototype.LTDetailGpuShader{source=Source,indirect=Indirect} as T};
    }
    public static class Trace
    {
        public static readonly List<string> Events=new();public static int UniformWrites;
        public static void Reset(){Events.Clear();UniformWrites=0;Material.NativeReads=Material.Copies=Material.PassWrites=Material.InstancingWrites=Time.Reads=MaterialPropertyBlock.TerrainWrites=0;}
        public static string Values(Dictionary<int,object> values)=>string.Join("|",values.OrderBy(p=>Shader.Name(p.Key),StringComparer.Ordinal).Select(p=>Shader.Name(p.Key)+"="+Value(p.Value)));
        public static string Value(object o)=>o switch{
            null=>"null",GraphicsBuffer b=>b.Payload(),Vector4[] a=>string.Join(";",a.Select(v=>v.ToString())),
            LocalTerrainPrototype.LTDetailGpuData d=>d.objectToWorld+"/"+d.worldToObject+"/"+d.positionSize+"/"+d.lodPositionRandom+"/"+d.center+"/"+d.extents,
            _=>Convert.ToString(o,CultureInfo.InvariantCulture)};
    }
    public static class Graphics
    {
        public static void RenderMeshIndirect(RenderParams p,Mesh mesh,GraphicsBuffer args)
        {Trace.Events.Add("draw:"+p.camera.name+":"+mesh.name+":"+p.material.Payload()+":"+p.worldBounds+":"+p.layer+":"+p.renderingLayerMask+":"+p.shadowCastingMode+":"+p.receiveShadows+":"+p.motionVectorMode+":"+p.lightProbeUsage+":"+p.reflectionProbeUsage+":"+args.Payload()+":"+Trace.Values(p.matProps.values));}
    }
}
namespace UnityEngine.Rendering
{
    public enum ShadowCastingMode{Off,On,TwoSided,ShadowsOnly}
    public enum LightProbeUsage{Off,BlendProbes,UseProxyVolume,CustomProvided}
    public enum ReflectionProbeUsage{Off,BlendProbes}
    public enum MotionVectorGenerationMode{Camera,Object,ForceNoMotion}
    public enum RenderQueue{Transparent=3000}
    public enum GraphicsDeviceType{Direct3D11,Direct3D12,Vulkan}
    public struct RenderParams
    {
        public UnityEngine.Material material;public UnityEngine.Camera camera;public UnityEngine.Bounds worldBounds;
        public UnityEngine.MaterialPropertyBlock matProps;public int layer;public uint renderingLayerMask;
        public ShadowCastingMode shadowCastingMode;public bool receiveShadows;public MotionVectorGenerationMode motionVectorMode;public LightProbeUsage lightProbeUsage;public ReflectionProbeUsage reflectionProbeUsage;
        public RenderParams(UnityEngine.Material m){this=default;material=m;}
    }
}
namespace LocalTerrainPrototype
{
    public class LTDetailGpuShader:UnityEngine.Object{public UnityEngine.Shader source,indirect;public string error;}
    public class LTDetailEntry{public UnityEngine.GameObject prefab=new(){name="prefab"};public float cullDistance=150,fadeStart=100,farDensity=.3f,shadowDistance=60;public bool thinInDistance,limitShadowDistance;}
    public class LTDetailPrefab
    {
        public class Part
        {
            public const string GrassRootProperty="_LTGrassRootInObject";public UnityEngine.Material material;public UnityEngine.Mesh mesh;
            public int submesh,layer;public uint renderingLayerMask;public bool receiveShadows;public UnityEngine.Matrix4x4 localMatrix;public UnityEngine.Vector3 grassRootInPart;
            public ShadowCastingMode castShadows;public LightProbeUsage lightProbes;public ReflectionProbeUsage reflectionProbes;public MotionVectorGenerationMode motionVectors;
            public bool SupportsGrassColor=>material&&material.HasProperty(GrassRootProperty);
        }
        public class Level{public float screenRelativeHeight;public readonly List<Part> parts=new();}
        public readonly List<Level> levels=new();public bool hasLODGroup;
    }
    public class LTGrassTerrainMap
    {
        static readonly UnityEngine.Texture black=new(){name="black"};
        public UnityEngine.Texture texture=new(){name="terrain"};public bool ready=true;
        public UnityEngine.Vector4 u=new(1,0,0,0),v=new(0,0,1,0);
        public static LTGrassTerrainBinding CaptureBinding(LTGrassTerrainMap map)
        {
            bool ready=map!=null&&map.ready&&map.texture;
            return new(ready?map.texture:black,ready,ready?map.u:default,ready?map.v:default);
        }
        public static void Bind(UnityEngine.MaterialPropertyBlock b,LTGrassTerrainMap map)
        {
            bool ready=map!=null&&map.ready&&map.texture;
            b.SetFloat("_LTGrassTerrainReady",ready?1:0);b.SetTexture("_LTGrassTerrainMap",ready?map.texture:black);
            b.SetVector("_LTGrassTerrainU",ready?map.u:default);b.SetVector("_LTGrassTerrainV",ready?map.v:default);
        }
    }
}
