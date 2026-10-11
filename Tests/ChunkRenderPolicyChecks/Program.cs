using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using LocalTerrainPrototype;
using UnityEngine;

// Real LTChunk methods, small Unity doubles. This is NOT native rendering.
// The separate Unity C# project build checks the public API surface.
static class Check
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Event(LTChunk c,string name)=>typeof(LTChunk).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(c,null);
    static LTChunk Chunk(MeshRenderer renderer=null)
    {
        var c=new LTChunk{mesh=new Mesh()};c.Attach(new MeshFilter());
        if(renderer!=null)c.Attach(renderer);
        return c;
    }
    static void Main()
    {
        CheckRealAssembly();
        SpatialProfilingChecks();
        Require(Attribute.IsDefined(typeof(LTChunk),typeof(ExecuteAlways)),"edit-mode and player lifecycle");
        foreach(bool playing in new[]{false,true})
        {
            Application.isPlaying=playing;
            foreach(bool existing in new[]{false,true})foreach(bool initiallyEnabled in new[]{false,true})
            {
                var renderer=new MeshRenderer();var unrelated=new MeshRenderer();var child=new MeshRenderer();var chunk=Chunk(renderer);
                UnityEngine.Rendering.DisallowGPUDrivenRendering original=null;
                if(existing)
                {
                    original=(UnityEngine.Rendering.DisallowGPUDrivenRendering)chunk.gameObject.AddComponent(typeof(UnityEngine.Rendering.DisallowGPUDrivenRendering));
                    original.enabled=initiallyEnabled;
                }
                Event(chunk,"OnEnable");
                Require(!renderer.allowGPUDrivenRendering,"chunk excluded");
                var guard=chunk.GetComponent<UnityEngine.Rendering.DisallowGPUDrivenRendering>();
                Require(guard&&!guard.applyToChildrenRecursively,"new policy is not recursive");
                Require(!existing||guard==original,"reuse user's existing component");
                Require(unrelated.allowGPUDrivenRendering&&child.allowGPUDrivenRendering,"other objects untouched");
                int writes=renderer.writes,adds=chunk.gameObject.adds;
                for(int i=0;i<100;i++)chunk.ShowLOD(0);
                Require(renderer.writes==writes&&chunk.gameObject.adds==adds,"no repeated flag writes or added components");
                Require(chunk.GetComponent<MeshFilter>().sharedMesh==chunk.mesh,"LOD0 published");
                var coarse=new Mesh();chunk.lodMeshes=new[]{coarse};chunk.ShowLOD(1);
                Require(chunk.GetComponent<MeshFilter>().sharedMesh==coarse,"coarse mesh published");
                Event(chunk,"OnDisable");Require(!renderer.allowGPUDrivenRendering,"disabling LTChunk does not remove the renderer exclusion");
                Event(chunk,"OnEnable");Require(chunk.gameObject.adds==adds,"re-enable does not duplicate guard");
                guard.enabled=false;chunk.ShowLOD(0);Require(!renderer.allowGPUDrivenRendering,"disabled guard re-enabled before publication");
                guard.enabled=false;guard.alive=false;chunk.ShowLOD(0);
                Require(!renderer.allowGPUDrivenRendering&&chunk.gameObject.adds==adds+1,"removed guard recreated");
            }
            var late=Chunk();Event(late,"OnEnable");
            Require(late.gameObject.adds==0,"no renderer means no component created");
            var added=new MeshRenderer();late.Attach(added);late.ShowLOD(0);
            Require(!added.allowGPUDrivenRendering,"late renderer guarded");
            int count=late.gameObject.adds;added.alive=false;
            var replacement=new MeshRenderer();late.Attach(replacement);late.ShowLOD(0);
            Require(!replacement.allowGPUDrivenRendering&&late.gameObject.adds==count,"replacement renderer reuses and reapplies guard");
            var spatialRenderer=new MeshRenderer();var spatial=Chunk(spatialRenderer);var bank=new Mesh();
            spatial.spatialLOD=new LTSpatialLODAsset{vertexBank=bank,formatVersion=LTSpatialLODMath.Version};
            Mesh.beforeSetIndices=()=>Require(!spatialRenderer.allowGPUDrivenRendering,"excluded before index mutation");
            var topology=new LTSpatialLODTopology();spatial.ShowSpatialLOD(topology,0,true);
            var clone=spatial.GetComponent<MeshFilter>().sharedMesh;
            Require(clone!=bank&&clone!=spatial.mesh&&clone.indexWrites==1,"private spatial clone published");
            spatial.ShowSpatialLOD(topology,0,false);Require(clone.indexWrites==1,"idle does not upload again");
            spatial.ShowSpatialLOD(topology,0,true);Require(clone.indexWrites==2,"dirty upload preserved");
            var capture=new LTLODCpuCapture();capture.Start("chunk","test","automatic");
            using(capture.BeginUpdate(1,0,0,0,true,-1)){spatial.ShowSpatialLOD(topology,0,false,capture);capture.Completed();}
            using(capture.BeginUpdate(1,0,0,0,true,-1)){spatial.ShowSpatialLOD(topology,0,true,capture);capture.Completed();}
            spatial.spatialLOD.revision++;
            using(capture.BeginUpdate(1,0,0,0,true,-1)){spatial.ShowSpatialLOD(topology,0,false,capture);capture.Completed();}
            capture.Stop();var samples=capture.LastReport.samples;
            Require(samples.Length==3&&samples[0].uploadedChunks==0&&samples[0].clonedMeshes==0,"capture does not force idle work");
            Require(samples[1].uploadedChunks==1&&samples[1].uploadedIndices==6&&samples[1].clonedMeshes==0,"ordinary dirty upload counted without clone");
            Require(samples[2].uploadedChunks==1&&samples[2].uploadedIndices==6&&samples[2].clonedMeshes==1,"revision rebuild reports one clone and upload");
            Require(clone.indexWrites==3&&spatial.GetComponent<MeshFilter>().sharedMesh!=clone,"instrumentation preserves revision and dirty publication policy");
            var current=spatial.GetComponent<MeshFilter>().sharedMesh;int writesBefore=current.indexWrites;
            var seamMap=new LTRockSeamIndices.Plan(new[]{0,1,2,2});spatial.SetRockSeamMaps(_=>seamMap);spatial.ShowSpatialLOD(topology,0,false);
            Require(current.triangles.SequenceEqual(new[]{0,1,2})&&current.indexWrites==writesBefore+1,"seam change updates clean spatial chunk");
            spatial.ShowSpatialLOD(topology,0,false);Require(current.indexWrites==writesBefore+1,"unchanged seam does not upload again");
            spatial.SetRockSeamMaps(null);spatial.ShowSpatialLOD(topology,0,false);
            Require(current.triangles.SequenceEqual(new[]{0,1,2,2,1,3}),"removing seam restores RAW topology, not already remapped indices");
            var bandPlan=new LTRockSeamIndices.Plan(new[]{0,1,2,3},new LTRockSeamIndices.Band{source=new[]{0,1,2,2,1,3},replacement=new[]{0,1,3,0,3,2}});
            spatial.SetRockSeamMaps(_=>bandPlan);spatial.ShowSpatialLOD(topology,0,false);
            Require(current.triangles.SequenceEqual(new[]{0,1,3,0,3,2}),"spatial band replacement published");
            int bandWrites=current.indexWrites;spatial.ShowSpatialLOD(topology,0,false);Require(current.indexWrites==bandWrites,"idle band does not upload");
            spatial.SetRockSeamMaps(null);spatial.ShowSpatialLOD(topology,0,false);
            Require(current.triangles.SequenceEqual(new[]{0,1,2,2,1,3}),"clearing band restores original topology");
            spatial.ReleaseLODPreview();spatial.ShowLOD(0);Require(!spatialRenderer.allowGPUDrivenRendering,"preview cleanup retains guard");
            var legacy=new Mesh();spatial.lodMeshes=new[]{legacy};spatial.SetRockSeamMaps(m=>m==legacy?seamMap:null);spatial.ShowLOD(1);
            Require(spatial.GetComponent<MeshFilter>().sharedMesh!=legacy&&spatial.GetComponent<MeshFilter>().sharedMesh.triangles.Length==3&&legacy.triangles.Length==6,"legacy uses owned remapped clone without changing source");
            spatial.ShowLOD(0);Require(spatial.GetComponent<MeshFilter>().sharedMesh==spatial.mesh,"legacy seam release restores LOD0");
            Mesh.beforeSetIndices=null;Event(spatial,"OnDestroy");
        }
        // A failed engine call must not escape through beginCameraRendering.
        var failed=Chunk(new MeshRenderer());GameObject.failAdd=true;
        Event(failed,"OnEnable");
        for(int i=0;i<100;i++)failed.ShowLOD(0);
        Require(failed.GetComponent<MeshFilter>().sharedMesh==failed.mesh,"failed exclusion does not block mesh publication");
        Require(failed.gameObject.addAttempts==1&&Debug.errors==1,"failed policy is not retried or logged each frame");
        var alsoFailed=Chunk(new MeshRenderer());Event(alsoFailed,"OnEnable");
        Require(Debug.errors==1,"warning limited across chunks");
        GameObject.failAdd=false;Event(failed,"OnEnable");
        Require(!failed.GetComponent<MeshRenderer>().allowGPUDrivenRendering,"explicit re-enable retries failed policy");
        var editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        int create=editor.IndexOf("var g=new GameObject($\"Chunk_{x}_{z}\")",StringComparison.Ordinal);
        Require(create>=0&&editor.IndexOf("g.AddComponent<MeshRenderer>()",create,StringComparison.Ordinal)<editor.IndexOf("g.AddComponent<LTChunk>()",create,StringComparison.Ordinal),"renderer exists before creation OnEnable");
        var link=XDocument.Load("Assets/TerrainSystem/LocalTerrain/link.xml");
        var assembly=link.Root!.Element("assembly")!;var type=assembly.Element("type")!;
        Require((string)assembly.Attribute("fullname")=="Unity.RenderPipelines.GPUDriven.Runtime"&&
            (string)type.Attribute("fullname")=="UnityEngine.Rendering.DisallowGPUDrivenRendering"&&
            (string)type.Attribute("preserve")=="all","official component lifecycle preserved for player stripping");
        Console.WriteLine("PASS real LTChunk + test doubles: local exclusion, existing/disabled/removed guards, late/replaced renderer, enable/disable, no duplicate components or idle flag writes, LOD0/coarse/spatial publication; engine-call failure preserves rendering, bounded logging/retry. Creation-order and linker contracts passed. Not native rendering or an IL2CPP build.");
    }
    static void SpatialProfilingChecks()
    {
        var c=Chunk(new MeshRenderer());
        var bank=new Mesh();c.spatialLOD=new LTSpatialLODAsset{vertexBank=bank,formatVersion=LTSpatialLODMath.Version};
        var topology=new LTSpatialLODTopology();
        void Scopes(params string[] names)
        {
            Require(Unity.Profiling.ProfilerMarker.ActiveCount==0,"all operation scopes close");
            Require(Unity.Profiling.ProfilerMarker.Entered.SequenceEqual(names.Select(n=>"LT.LOD.Spatial"+n)),"only actual spatial operations measured");
            Unity.Profiling.ProfilerMarker.Entered.Clear();
        }
        c.ShowSpatialLOD(topology,0,false);Scopes("Clone","Indices","Upload","Bind");
        var clone=c.GetComponent<MeshFilter>().sharedMesh;
        Require(clone!=bank&&clone.triangles.SequenceEqual(bank.triangles),"cold output unchanged");
        for(int i=0;i<100;i++)c.ShowSpatialLOD(topology,0,false);
        Scopes();Require(clone.indexWrites==1,"idle capture emits no scopes and performs no upload");
        c.ShowSpatialLOD(topology,0,true);Scopes("Indices","Upload");
        Require(clone.indexWrites==2,"dirty publication still synchronous");
        c.spatialLOD.revision++;c.ShowSpatialLOD(topology,0,false);Scopes("Clone","Indices","Upload","Bind");
        Require(!clone,"old revision clone released");
        var seam=new LTRockSeamIndices.Plan(new[]{0,1,2,2});c.SetRockSeamMaps(_=>seam);
        c.ShowSpatialLOD(topology,0,false);Scopes("Indices","Seam","Upload");
        Require(c.GetComponent<MeshFilter>().sharedMesh.triangles.SequenceEqual(new[]{0,1,2}),"seam still applied within index assembly");
        c.SetRockSeamMaps(null);c.ShowSpatialLOD(topology,0,false);Scopes("Indices","Upload");
        Require(c.GetComponent<MeshFilter>().sharedMesh.triangles.SequenceEqual(bank.triangles),"seam removal restores original topology");
        c.GetComponent<MeshFilter>().sharedMesh=bank;c.ShowSpatialLOD(topology,0,false);Scopes("Bind");
        // Failing native uploads must close their scopes without hiding the error.
        Mesh.beforeSetIndices=()=>throw new InvalidOperationException("test upload failure");
        bool failed=false;
        try{c.ShowSpatialLOD(topology,0,true);}catch(InvalidOperationException){failed=true;}
        finally{Mesh.beforeSetIndices=null;}
        Require(failed,"upload exception propagated");Scopes("Indices","Upload");
        c.ShowSpatialLOD(topology,0,true);Scopes("Indices","Upload");
        c.ReleaseSpatialLOD();c.ShowSpatialLOD(topology,0,false);Scopes("Clone","Indices","Upload","Bind");
        c.ShowLOD(0);Scopes();Require(c.GetComponent<MeshFilter>().sharedMesh==c.mesh,"legacy fallback unchanged");
        Event(c,"OnDestroy");
        Console.WriteLine("PASS spatial profiler scope contracts: cold/revision/dirty/seam/rebind/release/fallback/exception paths; 100 idle updates emit no operation markers. Test doubles, NOT native Unity timing.");
    }
    static void CheckRealAssembly()
    {
        bool Contains(string path,params string[] names)
        {
            using var file=File.OpenRead(path);using var pe=new PEReader(file);
            var metadata=pe.GetMetadataReader();var found=new HashSet<string>();
            foreach(var handle in metadata.TypeDefinitions)
            {
                var type=metadata.GetTypeDefinition(handle);
                found.Add(metadata.GetString(type.Namespace)+"."+metadata.GetString(type.Name));
            }
            return names.All(found.Contains);
        }
        const string component="UnityEngine.Rendering.DisallowGPUDrivenRendering";
        Require(Contains("Library/ScriptAssemblies/Unity.RenderPipelines.GPUDriven.Runtime.dll",component,
            "UnityEngine.Rendering.GPUResidentDrawer"),"actual compiled Unity DLL contains both exclusion component and assembly anchor");
        Require(!Contains("Library/ScriptAssemblies/Unity.RenderPipelines.Core.Runtime.dll",component),"regression: Volume's assembly does not own the component");
        string chunk=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTChunk.cs");
        Require(chunk.Contains("typeof(UnityEngine.Rendering.GPUResidentDrawer).Assembly.GetType(")&&
            chunk.Contains("\"UnityEngine.Rendering.DisallowGPUDrivenRendering\",false)"),"production uses verified assembly and non-throwing type resolution");
        Console.WriteLine("PASS actual Unity DLL metadata: GPUResidentDrawer and DisallowGPUDrivenRendering are in GPUDriven.Runtime, not Core.Runtime. Read-only metadata inspection, no Unity instance launched.");
    }
}
namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class ExecuteAlways:Attribute{}
    [AttributeUsage(AttributeTargets.Field)] public sealed class HideInInspector:Attribute{}
    public class Object
    {
        public bool alive=true;public string name;
        public static implicit operator bool(Object value)=>value!=null&&value.alive;
        public static Mesh Instantiate(Mesh value)=>new Mesh{triangles=(int[])value.triangles.Clone()};
        public static void Destroy(Object value){if(value!=null)value.alive=false;}
        public static void DestroyImmediate(Object value)=>Destroy(value);
    }
    public sealed class GameObject
    {
        readonly Dictionary<Type,Component> components=new();public int adds,addAttempts;public static bool failAdd;
        public void Attach(Component c){c.gameObject=this;components[c.GetType()]=c;}
        public Component GetComponent(Type type)=>components.TryGetValue(type,out var value)&&value?value:null;
        public Component AddComponent(Type type)
        {
            addAttempts++;if(failAdd)throw new InvalidOperationException("simulated Unity AddComponent failure");
            var c=(Component)Activator.CreateInstance(type)!;Attach(c);adds++;
            if(c is Behaviour b)b.ApplyEnabled();return c;
        }
    }
    public class Component:Object
    {
        public GameObject gameObject=new();
        public void Attach(Component c)=>gameObject.Attach(c);
        public Component GetComponent(Type type)=>gameObject.GetComponent(type);
        public T GetComponent<T>() where T:Component=>(T)GetComponent(typeof(T));
        public T GetComponentInParent<T>() where T:Component=>null;
    }
    public class Behaviour:Component
    {
        bool on=true;
        public bool enabled{get=>on;set{if(on==value)return;on=value;ApplyEnabled();}}
        public virtual void ApplyEnabled(){}
    }
    public class MonoBehaviour:Behaviour{}
    public sealed class MeshRenderer:Component
    {
        bool allow=true;public int writes;
        public bool allowGPUDrivenRendering{get=>allow;set{allow=value;writes++;}}
    }
    public sealed class MeshFilter:Component{public Mesh sharedMesh;}
    public sealed class Mesh:Object
    {
        public HideFlags hideFlags;public int indexWrites;public static Action beforeSetIndices;
        public int[] triangles=new[]{0,1,2,2,1,3};
        public void MarkDynamic(){}public uint GetIndexCount(int submesh)=>6;
        public void SetIndices(List<int> indices,MeshTopology topology,int submesh,bool bounds){beforeSetIndices?.Invoke();indexWrites++;triangles=indices.ToArray();}
        public void SetTriangles(List<int> indices,int submesh,bool bounds){triangles=indices.ToArray();}
    }
    public enum HideFlags{HideAndDontSave} public enum MeshTopology{Triangles}
    public struct Vector3Int{}
    public static class Application{public static bool isPlaying;}
    public static class Debug{public static int errors;public static void LogError(string message,Object context){errors++;}}
    public static class Mathf{public static int Clamp(int value,int min,int max)=>Math.Clamp(value,min,max);}
}
namespace UnityEngine.Rendering
{
    public sealed class GPUResidentDrawer{}
    internal sealed class DisallowGPUDrivenRendering:Behaviour
    {
        public bool applyToChildrenRecursively=>false;
        public override void ApplyEnabled(){var r=GetComponent<MeshRenderer>();if(r)r.allowGPUDrivenRendering=!enabled;}
    }
}
namespace LocalTerrainPrototype
{
    public sealed class LTWorld:UnityEngine.MonoBehaviour{public void NotifyChunkLayoutChanged(){}}
    public sealed class LTSpatialLODAsset:UnityEngine.Object{public Mesh vertexBank;public int formatVersion,revision;}
    public static class LTSpatialLODMath{public const int Version=2;}
    public sealed class LTSpatialLODTopology
    {
        public void WriteIndices(int chunk,List<int> output,LTRockSeamIndices.Plan seam=null)
        {output.Clear();output.AddRange(new[]{0,1,2,2,1,3});seam?.Apply(output);}
    }
}
