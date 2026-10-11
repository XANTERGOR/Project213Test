// Only camera identity/liveness for managed lease tests. This does not emulate
// Unity rendering, callbacks, component lifetime or native object destruction.
namespace UnityEngine
{
    public sealed class TooltipAttribute:System.Attribute {public TooltipAttribute(string text){}}
    public sealed class RangeAttribute:System.Attribute {public RangeAttribute(float minimum,float maximum){}}
    public sealed class Camera
    {
        public bool alive=true;
        public static implicit operator bool(Camera value)=>value!=null&&value.alive;
    }
}
namespace LocalTerrainPrototype
{
    public static class LTTerrainFrameDiagnostic { internal static bool Active {get;set;} }
    // Compile the actual rendering-mode partial without emulating Unity scenes.
    public sealed partial class LTWorld
    {
        internal LTPaintRuntime paintRuntime;
        void UpdatePainting(bool force){}
    }
    public sealed class LTPaintRuntime {public string FastLiveLayersStatus=>"stub";}
}
