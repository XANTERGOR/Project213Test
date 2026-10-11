using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTRoadJunctionTerrainLODCapture
    {
        sealed class Cache
        {
            internal string key;
            internal LTRoadTerrainSurface surface;
            internal readonly LTRoadCaptureSignature signature=new LTRoadCaptureSignature();
        }
        static readonly ConditionalWeakTable<LTRoadJunction,Cache> caches=new ConditionalWeakTable<LTRoadJunction,Cache>();
        static readonly ProfilerMarker markerCapture=new ProfilerMarker("LT.Editor.Capture.JunctionFoundation");

        internal static LTRoadTerrainSurface Capture(LTRoadJunction node,LTWorld world,out string key)
        {
            using var scope=markerCapture.Auto();
            key="fixed-junction";
            if(node.surface!=LTRoadMode.Asphalt||!node.simplifyTerrainUnderAsphalt)return null;
            var root=node.generatedRoot;
            if(!root||!root.IsChildOf(node.transform)||!root.gameObject.activeInHierarchy||
                !root.TryGetComponent<LTRoadJunctionGenerated>(out var marker)||marker.owner!=node)
            {key="missing-junction-bake";return null;}
            // The baker publishes one owned LTRoadLOD on the generated root. Use
            // its complete array, never the current MeshFilter or source LODGroup.
            // Even while source edits await a bake this is the geometry actually
            // displayed. Publishing replacements changes the live key and dirties
            // both old/new bounds. No transient instance-ID hash is saved to scenes.
            if(!root.TryGetComponent<LTRoadLOD>(out var lod)||lod.junction!=node||lod.owner||lod.meshes==null||lod.meshes.Length==0)
            {key="missing-junction-lods";return null;}
            var cache=caches.GetValue(node,n=>new Cache());
            var matrix=world.transform.worldToLocalMatrix*root.localToWorldMatrix;
            cache.signature.Begin(node.terrainLODEdgeError);
            cache.signature.AddLOD(lod.GetInstanceID(),matrix,lod.meshes.Length);
            foreach(var mesh in lod.meshes)
            {
                if(!mesh||!mesh.isReadable||mesh.vertexCount<3||mesh.subMeshCount==0)
                {key="unreadable-junction-lods";return null;}
                cache.signature.AddMesh(mesh.GetInstanceID(),EditorUtility.GetDirtyCount(mesh),LTEditorEngine.AssetInputHash(mesh),mesh.vertexCount,mesh.subMeshCount);
            }
            key=cache.signature.Finish();
            if(cache.key==key)return cache.surface;
            try
            {
                var parts=new List<LTRoadTerrainSurface.Part>();
                for(int level=0;level<lod.meshes.Length;level++)
                {
                    var mesh=lod.meshes[level];
                    for(int s=0;s<mesh.subMeshCount;s++)
                        if(mesh.GetTopology(s)!=MeshTopology.Triangles)throw new InvalidOperationException("Non-triangle junction LOD.");
                    var vertices=mesh.vertices;var indices=mesh.triangles;
                    if(indices.Length==0)throw new InvalidOperationException("Empty junction LOD.");
                    for(int i=0;i<vertices.Length;i++)vertices[i]=matrix.MultiplyPoint3x4(vertices[i]);
                    parts.Add(new LTRoadTerrainSurface.Part{vertices=vertices,indices=indices,level=level});
                }
                cache.surface=new LTRoadTerrainSurface(parts,node.terrainLODEdgeError);
            }
            catch(ArgumentException){cache.surface=null;}
            catch(InvalidOperationException){cache.surface=null;}
            catch(UnityException){cache.surface=null;}
            cache.key=key;return cache.surface;
        }
    }
}
