// Frozen pre-optimization implementation, used only by the owned preview validation.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTRoadCaptureReference
    {
        sealed class Cache {internal string key;internal LTRoadTerrainSurface surface;}
        static readonly ConditionalWeakTable<LTRoad,Cache> caches=new ConditionalWeakTable<LTRoad,Cache>();
        internal static LTRoadTerrainSurface Capture(LTRoad road,LTWorld world,LTRoadMath.Snapshot snapshot,out string key)
        {
            key="fixed";
            if(!road.simplifyTerrainUnderAsphalt)return null;
            var root=road.generatedRoot;var marker=root?root.GetComponent<LTRoadGenerated>():null;
            if(!root||!marker||marker.owner!=road||!root.IsChildOf(road.transform)||!root.gameObject.activeInHierarchy||road.bakedGeometryHash!=snapshot.geometryHash)
            {key="pending-road-bake";return null;}
            var lods=root.GetComponentsInChildren<LTRoadLOD>().Where(l=>l.owner==road&&l.gameObject.activeInHierarchy).ToArray();
            if(lods.Length==0){key="missing-road-lods";return null;}
            var inputs=new List<(Mesh mesh,Matrix4x4 matrix,int level)>();var text=new StringBuilder("road-foundation-v2:");
            text.Append(road.terrainLODEdgeError.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).Append(':');
            foreach(var lod in lods)
            {
                if(lod.meshes==null||lod.meshes.Length==0||lod.meshes.Any(m=>!m||!m.isReadable))
                {key="unreadable-road-lods";return null;}
                var matrix=world.transform.worldToLocalMatrix*lod.transform.localToWorldMatrix;
                text.Append(lod.GetInstanceID()).Append(':').Append(matrix.ToString("R")).Append(':');
                for(int level=0;level<lod.meshes.Length;level++)
                {
                    var mesh=lod.meshes[level];
                    text.Append(mesh.GetInstanceID()).Append(',').Append(EditorUtility.GetDirtyCount(mesh)).Append(',').Append(LTEditorEngine.AssetInputHash(mesh)).Append(',').Append(mesh.vertexCount).Append(',').Append(mesh.subMeshCount).Append(';');
                    inputs.Add((mesh,matrix,level));
                }
            }
            key=text.ToString();var cache=caches.GetValue(road,r=>new Cache());
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
    }
}
