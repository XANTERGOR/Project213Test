using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Unity.Profiling;

namespace LocalTerrainPrototype
{
    public static partial class LTEditorEngine
    {
        static readonly ProfilerMarker captureMarker=new ProfilerMarker("LT.Editor.Capture");
        static readonly ProfilerMarker detectMarker=new ProfilerMarker("LT.Editor.Detect");
        static readonly ProfilerMarker meshGeometryMarker=new ProfilerMarker("LT.Editor.MeshStamp.Geometry");
        static readonly ProfilerMarker meshHitMarker=new ProfilerMarker("LT.Editor.MeshStamp.CacheHit");
        static readonly ProfilerMarker meshBuildMarker=new ProfilerMarker("LT.Editor.MeshStamp.CacheMissBuild");
        static readonly ProfilerMarker meshAuditMarker=new ProfilerMarker("LT.Editor.MeshStamp.ContentAudit");
        // Reused for the infrequent guard against mesh edits that did not mark the
        // object dirty. Only the editor main thread calls Capture; no nested reads.
        static readonly List<Vector3> meshAuditVertices=new List<Vector3>();
        static readonly List<int> meshAuditIndices=new List<int>(),meshAuditPart=new List<int>();
        static LTMeshStampGeometry CaptureMeshGeometry(int owner,Mesh mesh,Matrix4x4 matrix,LTMeshStampGeometryCache cache)
        {
            using(meshGeometryMarker.Auto())
            {
                long indices=0;for(int s=0;s<mesh.subMeshCount;s++)indices+=mesh.GetIndexCount(s);
                var key=new LTMeshStampGeometryKey(mesh.GetInstanceID(),EditorUtility.GetDirtyCount(mesh),mesh.vertexCount,
                    mesh.subMeshCount,indices,matrix,AssetInputHash(mesh));
                double now=EditorApplication.timeSinceStartup;
                LTMeshStampGeometry cached=null;
                var lookup=cache==null?LTMeshStampGeometryLookup.Miss:cache.Lookup(owner,key,now,out cached);
                // A previously readable mesh can become unreadable without its ID
                // changing. Never let a cache hit hide that invalid input.
                if(!mesh.isReadable){cache?.Remove(owner);throw new System.InvalidOperationException("Mesh Stamp: source mesh must be readable: "+mesh.name);}
                if(lookup==LTMeshStampGeometryLookup.Hit)
                {using(meshHitMarker.Auto())return cached;}
                if(lookup==LTMeshStampGeometryLookup.Audit)
                {
                    using(meshAuditMarker.Auto())
                    {
                        mesh.GetVertices(meshAuditVertices);meshAuditIndices.Clear();
                        for(int s=0;s<mesh.subMeshCount;s++){mesh.GetTriangles(meshAuditPart,s);meshAuditIndices.AddRange(meshAuditPart);}
                        if(cached.SameSource(meshAuditVertices,meshAuditIndices))
                        {cache.ConfirmAudit(owner,now);return cached;}
                    }
                }
                using(meshBuildMarker.Auto())
                {
                    var data=LTMeshStampGeometry.Build(mesh.vertices,mesh.triangles,matrix);
                    cache?.Store(owner,key,data,now);return data;
                }
            }
        }
        static void ClearMeshCaptureCaches()
        {
            foreach(var state in states.Values)state.meshCapture.Clear();
            // These buffers can have grown to the largest source mesh encountered.
            meshAuditVertices.Clear();meshAuditVertices.TrimExcess();
            meshAuditIndices.Clear();meshAuditIndices.TrimExcess();meshAuditPart.Clear();meshAuditPart.TrimExcess();
        }
    }
}
