// Minimal test doubles. Production callbacks and asset.Valid are linked intact.
// No Unity editor, scene file, or asset database is opened by this executable.
namespace UnityEngine
{
    public class Object
    {
        public virtual string name { get; set; }="";
        public bool destroyed;
        public bool persistent;
        public static void DestroyImmediate(Object value) { value.destroyed=true; if(value is Component c)c.gameObject.components.Remove(c); }
        public static implicit operator bool(Object value)=>value!=null&&!value.destroyed;
    }
    public class ScriptableObject:Object { }
    public class Mesh:Object { }
    public record struct Vector2Int(int x,int y);
    public class Component:Object
    {
        public GameObject gameObject;
        public Transform transform=>gameObject.transform;
        public override string name { get=>gameObject.name; set=>gameObject.name=value; }
        public T GetComponent<T>()where T:Component=>gameObject.components.OfType<T>().FirstOrDefault();
        public T GetComponentInParent<T>(bool includeInactive)where T:Component
        { for(var t=transform;t!=null;t=t.parent){var found=t.GetComponent<T>();if(found!=null)return found;}return null; }
        public T[] GetComponentsInChildren<T>(bool includeInactive)where T:Component=>gameObject.GetComponentsInChildren<T>(includeInactive).ToArray();
    }
    public class Transform:Component { public Transform parent; }
    public class GameObject:Object
    {
        public readonly Transform transform;
        public readonly List<Component> components=new();
        public readonly List<GameObject> children=new();
        public SceneManagement.Scene scene;
        public bool active=true;
        public GameObject(string name){this.name=name;transform=new Transform{gameObject=this};}
        public T Add<T>(T component)where T:Component{component.gameObject=this;components.Add(component);return component;}
        public GameObject Child(string name)
        {
            var child=new GameObject(name){scene=scene};child.transform.parent=transform;children.Add(child);return child;
        }
        public IEnumerable<T> GetComponentsInChildren<T>(bool includeInactive)where T:Component
        {
            if(!includeInactive&&!active)yield break;
            foreach(var c in components.OfType<T>())yield return c;
            foreach(var child in children)foreach(var c in child.GetComponentsInChildren<T>(includeInactive))yield return c;
        }
    }
    public class MeshFilter:Component { public Mesh sharedMesh; }
    public class MeshCollider:Component { public Mesh sharedMesh; }
    public static class Debug
    {
        public static readonly List<(string text,Object context)> warnings=new();
        public static void LogWarning(object message,Object context)=>warnings.Add((message.ToString(),context));
    }
}
namespace UnityEngine.SceneManagement
{
    public class Scene
    {
        public string name="TerrainTest",path="Assets/TerrainTest.unity";
        public readonly List<UnityEngine.GameObject> roots=new();
        public UnityEngine.GameObject[] GetRootGameObjects()=>roots.ToArray();
        public UnityEngine.GameObject Root(string name)
        {var root=new UnityEngine.GameObject(name){scene=this};roots.Add(root);return root;}
    }
}
namespace UnityEditor
{
    public static class BuildPipeline { public static bool isBuildingPlayer; }
    public static class AssetDatabase { public static bool Contains(UnityEngine.Object value)=>value&&value.persistent; }
}
namespace UnityEditor.Build.Reporting { public class BuildReport { } }
namespace UnityEditor.Build
{
    public class BuildFailedException:Exception { public BuildFailedException(string message):base(message){ } }
    public interface IProcessSceneWithReport
    {
        int callbackOrder { get; }
        void OnProcessScene(UnityEngine.SceneManagement.Scene scene,Reporting.BuildReport report);
    }
}
namespace LocalTerrainPrototype
{
    public class LTDetailRenderer:UnityEngine.Component
    {
        public bool enabled=true;
        public bool requireBakedPlacementForBuild=true;
        public int validations;
        public Exception failure;
        public void ProcessWorldPlacement(bool validateOnly)
        {
            if(!validateOnly)throw new Exception("Build must only validate, never silently rebake.");
            validations++;if(failure!=null)throw failure;
        }
    }
    public enum LTMeshConformMode { Surface, Cave }
    public class LTHeightStamp:UnityEngine.Component { }
    public class LTWorld:UnityEngine.Component { public UnityEngine.Transform generatedRoot; public int chunksX=1,chunksZ=1; }
    public class LTMeshStamp:UnityEngine.Component
    {
        public bool enabled=true,affectHeight=true,trimRockInsideTerrain=true;
        public LTMeshConformMode mode;
        public UnityEngine.Mesh sourceMesh;
        public LTRockRenderAsset renderAsset;
    }
    public class LTChunk:UnityEngine.Component
    {
        public UnityEngine.Mesh mesh;
        public int x,z;
        public bool lodsPending;
        public int shown=-1,showCalls;
        public void ShowLOD(int level){shown=level;showCalls++;}
    }
    public class LTRoadLOD:UnityEngine.Component
    {
        public int shown=-1,showCalls;
        public void Show(int level){shown=level;showCalls++;}
    }
}
