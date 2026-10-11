#if UNITY_EDITOR
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Transient, camera-specific draw suppression. Never disables components or
    // releases vegetation placement/GPU resources. No player code or scene data.
    public static class LTDetailBenchmarkIsolation
    {
        public static Camera Camera { get; private set; }
        public static int SuppressedCallbacks { get; private set; }
        public static bool OtherDiagnosticActive=>LTTerrainFrameDiagnostic.Active;
        static object owner;
        public static bool Acquire(object token,Camera camera)
        {
            if(owner!=null||token==null||!camera)return false;
            owner=token;Camera=camera;SuppressedCallbacks=0;return true;
        }
        public static bool IsOwner(object token)=>ReferenceEquals(owner,token);
        public static void Release(object token)
        {if(!IsOwner(token))return;Camera=null;owner=null;}
        internal static bool Skip(Camera camera)
        {
            if(owner==null||!Camera||camera!=Camera)return false;
            SuppressedCallbacks++;return true;
        }
    }
}
#endif
