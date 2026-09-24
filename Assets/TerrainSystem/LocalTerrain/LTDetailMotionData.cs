using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // RenderMeshInstanced recognizes these exact field names. This is transform
    // history for STATIC instances, not precomputed vertex velocity (TEXCOORD5).
    [StructLayout(LayoutKind.Sequential)]
    public struct LTDetailMotionData
    {
        public Matrix4x4 objectToWorld;
        public Matrix4x4 prevObjectToWorld;

        public static void FillStatic(Matrix4x4[] source,LTDetailMotionData[] destination,int count)
        {
            if(source==null)throw new ArgumentNullException(nameof(source));
            if(destination==null)throw new ArgumentNullException(nameof(destination));
            if(count<0||count>source.Length||count>destination.Length)throw new ArgumentOutOfRangeException(nameof(count));
            // Never use the previous contents of a batch slot: selection/LOD/cameras
            // can reorder slots. New or rebuilt instances start with zero transform motion.
            for(int i=0;i<count;i++)
                destination[i]=new LTDetailMotionData{objectToWorld=source[i],prevObjectToWorld=source[i]};
        }
    }
}
