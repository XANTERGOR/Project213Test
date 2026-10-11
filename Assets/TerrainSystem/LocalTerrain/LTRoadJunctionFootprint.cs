using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Source LOD0 only, including always-visible parts. Independent of current
    // display LOD, generated mesh depth, materials and source renderer visibility.
    public static class LTRoadJunctionFootprint
    {
        sealed class Cache {public string key;public LTRoadFootprint shape;}
        static readonly ConditionalWeakTable<LTRoadJunction,Cache> caches=new ConditionalWeakTable<LTRoadJunction,Cache>();
        public static LTRoadFootprint Capture(LTRoadJunction node)
        {
            var root=node.SourceRoot;
            if(!root||!node.World)throw new ArgumentException("Для основания по мешу нужны модель перекрёстка и LTWorld.");
            var renderers=root.GetComponentsInChildren<MeshRenderer>(true).Where(r=>node.SourceMember(r.transform)).Cast<Renderer>().ToArray();
            var groups=root.GetComponentsInChildren<LODGroup>(true).Where(g=>node.SourceMember(g.transform)).ToArray();
            if(groups.Length>1)throw new ArgumentException("Основание перекрёстка: нужен один общий LODGroup.");
            if(groups.Length==1)
            {
                var levels=groups[0].GetLODs();if(levels.Length==0||levels[0].renderers==null||levels[0].renderers.Length==0)
                    throw new ArgumentException("Основание перекрёстка: LOD0 пуст.");
                var grouped=new HashSet<Renderer>(levels.SelectMany(l=>l.renderers??Array.Empty<Renderer>()));
                renderers=levels[0].renderers.Concat(renderers.Where(r=>!grouped.Contains(r))).Distinct().ToArray();
            }
            if(renderers.Length==0)throw new ArgumentException("Основание перекрёстка: нет мешей LOD0.");
            var parts=new List<(Mesh mesh,Matrix4x4 matrix)>();var key=new StringBuilder("junction-footprint-v1|");
            var matrix=node.World.transform.worldToLocalMatrix*node.transform.localToWorldMatrix*root.transform.worldToLocalMatrix;
            long vertices=0;
            foreach(var renderer in renderers)
            {
                if(!renderer||!(renderer is MeshRenderer)||!node.SourceMember(renderer.transform))throw new ArgumentException("LOD0 содержит чужой или неподдерживаемый Renderer.");
                var filter=renderer.GetComponent<MeshFilter>();var mesh=filter?filter.sharedMesh:null;
                if(!mesh||!mesh.isReadable)throw new ArgumentException("Основание перекрёстка: включите Read/Write у модели LOD0.");
                vertices+=mesh.vertexCount;if(vertices>2000000)throw new ArgumentException("Основание перекрёстка: лимит 2 млн вершин LOD0.");
                var transform=matrix*renderer.transform.localToWorldMatrix;
                key.Append(mesh.GetInstanceID()).Append(':').Append(mesh.vertexCount).Append(':').Append(transform.ToString("R")).Append('|');
#if UNITY_EDITOR
                key.Append(UnityEditor.EditorUtility.GetDirtyCount(mesh)).Append(':');
                string path=UnityEditor.AssetDatabase.GetAssetPath(mesh);
                if(!string.IsNullOrEmpty(path))key.Append(UnityEditor.AssetDatabase.GetAssetDependencyHash(path));
#endif
                parts.Add((mesh,transform));
            }
            var cache=caches.GetValue(node,n=>new Cache());string signature=key.ToString();
            if(cache.key==signature)return cache.shape;
            var points=new List<Vector3>();var indices=new List<int>();
            foreach(var part in parts)
            {
                for(int s=0;s<part.mesh.subMeshCount;s++)if(part.mesh.GetTopology(s)!=MeshTopology.Triangles)throw new ArgumentException("Основание перекрёстка: нужны треугольники.");
                int start=points.Count;points.AddRange(part.mesh.vertices.Select(part.matrix.MultiplyPoint3x4));
                var triangles=part.mesh.triangles;
                if((long)indices.Count+triangles.Length>6000000)throw new ArgumentException("Основание перекрёстка: лимит 2 млн треугольников LOD0.");
                foreach(int index in triangles)indices.Add(start+index);
            }
            var shape=new LTRoadFootprint(points.ToArray(),indices.ToArray(),0);
            if(shape.Empty)throw new ArgumentException("Основание перекрёстка: проекция LOD0 на XZ пуста.");
            cache.shape=shape;cache.key=signature;return shape;
        }
    }
}
