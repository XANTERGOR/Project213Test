using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Per Detail Renderer cache. Mesh assets are immutable road bake outputs;
    // changing seed/module/bake/transform invalidates placement without touching terrain paint.
    public sealed class LTRoadVegetationMask
    {
        public sealed class Entry
        {
            internal string key;
            public int hash;
            public LTRoadFootprint footprint;
        }
        readonly Dictionary<int,Entry> cache=new Dictionary<int,Entry>();
        public void Clear()=>cache.Clear();
        public List<Entry> Capture(LTRoad[] roads,LTWorld world)
        {
            var result=new List<Entry>();var used=new HashSet<int>();
            foreach(var road in roads)
            {
                if(!road.isActiveAndEnabled||road.World!=world||road.mode!=LTRoadMode.Asphalt||!road.clearVegetation||!road.asphaltClearMesh||!road.generatedRoot)continue;
                var marker=road.generatedRoot.GetComponent<LTRoadGenerated>();
                if(!marker||marker.owner!=road||!road.generatedRoot.IsChildOf(road.transform)||!road.generatedRoot.gameObject.activeInHierarchy)continue;
                var parts=new List<(Mesh mesh,Matrix4x4 matrix)>();var key=new StringBuilder("mesh-clear-v1|");
                key.Append(road.asphaltGrassPadding.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                foreach(var lod in road.generatedRoot.GetComponentsInChildren<LTRoadLOD>())
                {
                    if(lod.owner!=road||!lod.gameObject.activeInHierarchy||lod.meshes==null||lod.meshes.Length==0||!lod.meshes[0])continue;
                    var mesh=lod.meshes[0];var matrix=world.transform.worldToLocalMatrix*lod.transform.localToWorldMatrix;
                    parts.Add((mesh,matrix));key.Append('|').Append(mesh.GetInstanceID()).Append(':').Append(matrix.ToString("R"));
#if UNITY_EDITOR
                    key.Append(':').Append(UnityEditor.EditorUtility.GetDirtyCount(mesh));
#endif
                }
                if(parts.Count==0)continue;
                int id=road.GetInstanceID();used.Add(id);string signature=key.ToString();
                if(!cache.TryGetValue(id,out var entry)||entry.key!=signature)
                {
                    var vertices=new List<Vector3>();var triangles=new List<int>();
                    foreach(var part in parts)
                    {
                        int start=vertices.Count;
                        foreach(var p in part.mesh.vertices)vertices.Add(part.matrix.MultiplyPoint3x4(p));
                        foreach(int index in part.mesh.triangles)triangles.Add(start+index);
                    }
                    entry=new Entry{key=signature,hash=signature.GetHashCode(),footprint=new LTRoadFootprint(vertices.ToArray(),triangles.ToArray(),road.asphaltGrassPadding)};
                    cache[id]=entry;
                }
                result.Add(entry);
            }
            foreach(int id in cache.Keys.Where(id=>!used.Contains(id)).ToArray())cache.Remove(id);
            return result;
        }
    }
}
