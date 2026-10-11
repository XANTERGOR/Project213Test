// Executable tests of the linked production rock coordinator, not Unity rendering
// or Burst. Workers deliberately stay pending until the test finishes them.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace UnityEngine
{
    public class Object { static int next;public readonly int id=++next;public bool alive=true;public string name="test";public static implicit operator bool(Object o)=>o!=null&&o.alive; }
    public enum HideFlags { HideAndDontSave }
    public class Shader:Object {}
    public class Material:Object {public HideFlags hideFlags;public Material(Shader s){}public void SetFloat(string k,float v){}public void SetVector(string k,Vector4 v){} }
    public class Mesh:Object {public int subMeshCount=1;}
    public class MeshFilter:Object {public Mesh sharedMesh=new Mesh();}
    public class MeshRenderer:Object {public Material[] sharedMaterials={new Material(new Shader())};public Bounds bounds=new Bounds{center=new Vector3(5,0,5),extents=new Vector3(5,1,5)};}
    public struct Bounds {public Vector3 center,extents;}
    public record struct Vector3(float x,float y,float z)
    {
        public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a)=>new(-a.x,-a.y,-a.z);
        public static Vector3 Scale(Vector3 a,Vector3 b)=>new(a.x*b.x,a.y*b.y,a.z*b.z);
        public static Vector3 Min(Vector3 a,Vector3 b)=>new(Math.Min(a.x,b.x),Math.Min(a.y,b.y),Math.Min(a.z,b.z));
        public static Vector3 Max(Vector3 a,Vector3 b)=>new(Math.Max(a.x,b.x),Math.Max(a.y,b.y),Math.Max(a.z,b.z));
    }
    public struct Vector4 {public static Vector4 zero=>default;}
    public record struct Rect(float x,float y,float width,float height) {public static Rect MinMaxRect(float x,float y,float xx,float yy)=>new(x,y,xx-x,yy-y);}
    public struct Matrix4x4 {public Vector3 MultiplyPoint3x4(Vector3 p)=>p;}
    public class Transform {public Matrix4x4 worldToLocalMatrix;}
    public static class Mathf {public static int Max(int a,int b)=>Math.Max(a,b);public static int Min(int a,int b)=>Math.Min(a,b);public static float Max(float a,float b)=>Math.Max(a,b);}
    public static class Application {public static bool isPlaying;public static string unityVersion=>"test-double";}
    public static class Debug {public static void Log(object o){} }
}
namespace LocalTerrainPrototype
{
    public class LTWorld:UnityEngine.Object
    {
        public LTPaintCpuCapture paintCpu=new();public Transform transform=new();public LTSurfaceLayer baseLayer=new();
        public bool triplanarTexturing,lightweightBackground;public float layerHeightBlend;public string paintStatus="";
        public List<LTMeshStamp> rocks=new();public int terrainVersion;
        public T[] GetComponentsInChildren<T>()=>rocks.OfType<T>().ToArray();
    }
    public class LTSurfaceLayer:UnityEngine.Object {}
    public class LTMeshStamp:UnityEngine.Object
    {
        public bool isActiveAndEnabled=true,useTerrainMaterial=true,terrainLayersMaterialApplied,terrainMaterialApplied,unionGroupOutputApplied;
        public Material[] objectMaterials;public Mesh sourceMesh=new();public LTWorld world;
        public MeshRenderer renderer=new();public MeshFilter filter=new();
        public T GetComponent<T>() where T:class=>typeof(T)==typeof(MeshRenderer)?renderer as T:filter as T;
        public T GetComponentInParent<T>() where T:class=>world as T;
    }
    public class LTPaintStamp:UnityEngine.Object {public UnityEngine.Object Road;public LTSurfaceLayer EffectiveLayer=new();public bool HasTerrainFilters;public int revision;}
    public static class LTRoadMath {public class Snapshot {}}
    public class LTRoadRasterSet {}
    public static class LTPaintWeightMath {public struct Pixel {public int value;}}
    public class LTPaintWeightInput:IDisposable {public static int returns;public int value;bool disposed;public void Dispose(){if(disposed)return;disposed=true;returns++;value=-999;}}
    public sealed class LTPaintWeightWork:IDisposable
    {
        public static List<LTPaintWeightWork> live=new();public static bool CanSchedule=>live.Count<2;
        public static bool FailResult,FailConstructor;public bool IsCompleted,retired,released;public int reads;readonly int value;
        public bool IsValid=>!retired&&!released;
        public LTPaintWeightWork(LTPaintWeightInput i,LTPaintCpuCapture capture=null){if(!CanSchedule)throw new Exception("capacity");if(FailConstructor)throw new InvalidOperationException("injected copy failure");value=i.value;live.Add(this);}
        public LTPaintWeightMath.Pixel[] Result(out bool burst)
        {
            if(!IsValid||!IsCompleted)throw new Exception("unsafe result read");reads++;burst=true;
            if(FailResult)throw new InvalidOperationException("injected result failure");
            return new[]{new LTPaintWeightMath.Pixel{value=value}};
        }
        public void Dispose(){retired=true;if(IsCompleted){released=true;live.Remove(this);}}
        public static void Finish(){foreach(var w in live.ToArray()){w.IsCompleted=true;if(w.retired)w.Dispose();}}
        public static void Drain(){Finish();foreach(var w in live.ToArray())w.Dispose();FailResult=FailConstructor=false;}
    }
    public sealed partial class LTPaintRuntime
    {
        sealed class ChunkState {public bool ready;public int coverageHash,surfaceHash,map;public Material material;public PendingWeights weightWork;}
        readonly struct LayerChangeInput {public int AppendSurface(int hash)=>hash;}
        const int LayerCapacity=12,Resolution=257;
        public bool PendingRoadSampling {get;private set;}
        public bool fits=true,prepareFits=true,failBake;public int prepares,bakes,binds,releases,lastPixel=-1;
        static int Mix(int a,int b)=>unchecked(a*397^b);
        static int Id(UnityEngine.Object o)=>o?o.id:0;
        static bool Touches(Rect a,Rect b)=>a.x<=b.x+b.width&&b.x<=a.x+a.width&&a.y<=b.y+b.height&&b.y<=a.y+a.height;
        int TerrainFilterSignature(Rect r,List<LTPaintStamp> s)=>testWorld.terrainVersion;
        bool WeightInputFits(List<LTPaintStamp> s)=>fits;
        bool TryPrepareWeights(LTWorld w,Rect r,List<LTPaintStamp> s,List<LTSurfaceLayer> l,LTRoadRasterSet raster,out LTPaintWeightInput input)
        {prepares++;input=new LTPaintWeightInput{value=s.Sum(v=>v.revision)};return prepareFits;}
        void Bake(LTWorld w,Rect r,List<LTPaintStamp> s,List<LTSurfaceLayer> l,ChunkState state,List<LTRoadMath.Snapshot> a,bool bakeWeights=true,LTRoadRasterSet raster=null,LTPaintWeightMath.Pixel[] pixels=null)
        {if(failBake)throw new InvalidOperationException("injected bake failure");bakes++;lastPixel=pixels==null?-1:pixels[0].value;state.map=lastPixel;w.paintCpu.WeightBaked();}
        void Bind(Material m,LTWorld w,Rect r,List<LTSurfaceLayer> l,ChunkState s){binds++;}
        void Release(ChunkState s){s.weightWork?.Dispose();s.weightWork=null;releases++;s.material.alive=false;}
        LTWorld testWorld;
        public void TickTest(LTWorld w,List<LTPaintStamp> stamps,bool background=true,int usedBudget=0)
        {
            testWorld=w;PendingRoadSampling=false;
            var layers=new Dictionary<LTSurfaceLayer,LayerChangeInput>{{w.baseLayer,new()}};
            foreach(var s in stamps)layers[s.EffectiveLayer]=new();
            var inputs=stamps.ToDictionary(s=>s,s=>new[]{s.id,s.revision,s.EffectiveLayer.id});
            using(w.paintCpu.BeginTick(true))TickRockMaterials(w,new Shader(),stamps,stamps.Select(s=>new Rect(-100,-100,200,200)).ToList(),new(),layers,inputs,background,ref usedBudget);
        }
        public int Hash(LTMeshStamp s)=>rocks[s].paint.coverageHash;
        public int Count=>rocks.Count;
        public void Invalidate(){foreach(var s in rocks.Values)s.paint.ready=false;}
        public void DisposeTest()=>ReleaseRockMaterials();
    }
}
