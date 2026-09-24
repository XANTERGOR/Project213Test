using System.Runtime.InteropServices;
using UnityEngine;

namespace LocalTerrainPrototype
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LTDetailGpuData
    {
        public Matrix4x4 objectToWorld,worldToObject;
        public Vector4 positionSize,lodPositionRandom,center,extents;
        public const int Stride=192;
    }
}
