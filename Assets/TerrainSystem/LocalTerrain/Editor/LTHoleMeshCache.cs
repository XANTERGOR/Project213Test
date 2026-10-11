using System;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    [InitializeOnLoad]
    internal static class LTHoleMeshCache
    {
        sealed class Entry
        {
            internal string sourceKey, error;
            internal LTHoleMeshData data;
            internal LTHoleMeshVolume volume;
            internal Matrix4x4 matrix;
            internal int projectRevision=-1;
            internal string assetHash;
        }
        static readonly ConditionalWeakTable<LTHoleStamp,Entry> Entries=new ConditionalWeakTable<LTHoleStamp,Entry>();
        static int revision;
        static LTHoleMeshCache(){EditorApplication.projectChanged+=()=>revision++;}
        internal static void Invalidate(LTHoleStamp stamp){Entries.Remove(stamp);revision++;SceneView.RepaintAll();}
        internal static LTHoleStampSnapshot Capture(LTHoleStamp stamp,LTWorld world,out string signature)
        {
            var mesh=stamp.cutMesh;
            if(!mesh)throw new ArgumentException("Hole Mesh: назначь Cut Mesh.");
            if(!mesh.isReadable)throw new ArgumentException("Hole Mesh: включи Read/Write у меша. Настройки импорта автоматически не меняются.");
            var scale=stamp.transform.lossyScale;
            if(scale.x<=0||scale.y<=0||scale.z<=0)throw new ArgumentException("Hole Mesh: нужен положительный Scale по всем осям.");
            var entry=Entries.GetValue(stamp,_=>new Entry());
            string path=AssetDatabase.GetAssetPath(mesh);
            if(entry.projectRevision!=revision)
            {entry.assetHash=string.IsNullOrEmpty(path)?"memory":AssetDatabase.GetAssetDependencyHash(path).ToString();entry.projectRevision=revision;}
            string sourceKey=mesh.GetInstanceID()+":"+EditorUtility.GetDirtyCount(mesh)+":"+mesh.vertexCount+":"+mesh.subMeshCount+":"+entry.assetHash;
            if(entry.sourceKey!=sourceKey)
            {
                entry.sourceKey=sourceKey;entry.data=null;entry.volume=null;entry.error=null;
                try
                {
                    if(mesh.vertexCount>LTHoleMeshData.MaxVertices)throw new ArgumentException("Hole Mesh: превышен лимит 150 000 вершин; используй более простой cutter.");
                    long count=0;
                    for(int i=0;i<mesh.subMeshCount;i++)
                    {
                        if(mesh.GetTopology(i)!=MeshTopology.Triangles)throw new ArgumentException("Hole Mesh: все submesh должны быть треугольными.");
                        count+=mesh.GetIndexCount(i);
                    }
                    if(count>3*LTHoleMeshData.MaxTriangles)throw new ArgumentException("Hole Mesh: превышен лимит 50 000 треугольников; используй более простой cutter.");
                    entry.data=new LTHoleMeshData(mesh.vertices,mesh.triangles);
                }
                catch(ArgumentException ex){entry.error=ex.Message;}
                catch(UnityException ex){entry.error="Hole Mesh: не удалось прочитать меш: "+ex.Message;}
            }
            if(entry.error!=null)throw new ArgumentException(entry.error);
            var matrix=world.transform.worldToLocalMatrix*stamp.transform.localToWorldMatrix;
            if(entry.volume==null||!matrix.Equals(entry.matrix))
            {entry.volume=new LTHoleMeshVolume(entry.data,matrix);entry.matrix=matrix;}
            // Input dependency hash is stable across unrelated generated imports;
            // cache revisions themselves must not continually dirty the terrain.
            signature="mesh-volume-contour-lod-v3:"+mesh.GetInstanceID()+":"+entry.data.ContentHash+":"+matrix.ToString("R")+":"+stamp.edgeCellSize.ToString("R")+
                ":"+stamp.lodEdgeCellSize.ToString("R")+":"+stamp.meshLodContourError.ToString("R");
            return new LTHoleStampSnapshot(entry.volume,stamp.edgeCellSize,stamp.lodEdgeCellSize,stamp.meshLodContourError);
        }
    }
}
