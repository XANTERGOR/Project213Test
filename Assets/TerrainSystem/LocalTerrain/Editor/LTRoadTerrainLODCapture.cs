using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTRoadTerrainLODCapture
    {
        sealed class Cache
        {
            internal string key;internal LTRoadTerrainSurface surface;
            internal readonly LTRoadCaptureSignature signature=new LTRoadCaptureSignature();
            internal readonly List<LTRoadLOD> lods=new List<LTRoadLOD>();
            internal readonly List<(Mesh mesh,Matrix4x4 matrix,int level)> inputs=new List<(Mesh,Matrix4x4,int)>();
        }
        static readonly ConditionalWeakTable<LTRoad,Cache> caches=new ConditionalWeakTable<LTRoad,Cache>();
        static readonly ProfilerMarker markerCapture=new ProfilerMarker("LT.Editor.Capture.RoadFoundation");
        internal static LTRoadTerrainSurface Capture(LTRoad road,LTWorld world,LTRoadMath.Snapshot snapshot,out string key)
        {
            using var scope=markerCapture.Auto();
            key="fixed";
            if(!road.simplifyTerrainUnderAsphalt)return null;
            var root=road.generatedRoot;LTRoadGenerated marker=null;if(root)root.TryGetComponent(out marker);
            if(!root||!marker||marker.owner!=road||!root.IsChildOf(road.transform)||!root.gameObject.activeInHierarchy||road.bakedGeometryHash!=snapshot.geometryHash)
            {key="pending-road-bake";return null;}
            var cache=caches.GetValue(road,r=>new Cache());var lods=cache.lods;var inputs=cache.inputs;
            try
            {
                root.GetComponentsInChildren(false,lods);
                cache.signature.Begin(road.terrainLODEdgeError);int count=0;
                foreach(var lod in lods)
                {
                    if(lod.owner!=road||!lod.gameObject.activeInHierarchy)continue;
                    count++;
                    if(lod.meshes==null||lod.meshes.Length==0)
                    {key="unreadable-road-lods";return null;}
                    var matrix=world.transform.worldToLocalMatrix*lod.transform.localToWorldMatrix;
                    cache.signature.AddLOD(lod.GetInstanceID(),matrix,lod.meshes.Length);
                    for(int level=0;level<lod.meshes.Length;level++)
                    {
                        var mesh=lod.meshes[level];
                        if(!mesh||!mesh.isReadable){key="unreadable-road-lods";return null;}
                        cache.signature.AddMesh(mesh.GetInstanceID(),EditorUtility.GetDirtyCount(mesh),LTEditorEngine.AssetInputHash(mesh),mesh.vertexCount,mesh.subMeshCount);
                        inputs.Add((mesh,matrix,level));
                    }
                }
                if(count==0){key="missing-road-lods";return null;}
                key=cache.signature.Finish();
                if(cache.key==key)return cache.surface;
                try
                {
                    var parts=new List<LTRoadTerrainSurface.Part>();
                    foreach(var input in inputs)
                    {
                        var p=input.mesh.vertices;for(int i=0;i<p.Length;i++)p[i]=input.matrix.MultiplyPoint3x4(p[i]);
                        for(int s=0;s<input.mesh.subMeshCount;s++)if(input.mesh.GetTopology(s)!=UnityEngine.MeshTopology.Triangles)throw new InvalidOperationException("Non-triangle road mesh.");
                        parts.Add(new LTRoadTerrainSurface.Part{vertices=p,indices=input.mesh.triangles,level=input.level});
                    }
                    cache.surface=new LTRoadTerrainSurface(parts,road.terrainLODEdgeError);
                }
                catch(ArgumentException){cache.surface=null;}
                catch(InvalidOperationException){cache.surface=null;}
                catch(UnityException){cache.surface=null;}
                cache.key=key;return cache.surface; // Unavailable geometry keeps the old conservative lock.
            }
            finally {lods.Clear();inputs.Clear();} // Do not retain removed Unity objects in scratch storage.
        }
    }
}
