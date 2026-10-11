using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace LocalTerrainPrototype
{
    [InitializeOnLoad]
    public static class LTSpatialLODBake
    {
        static LTSpatialLODBake()
        {
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving+=(scene,path)=>
            {
                foreach(var chunk in UnityEngine.Object.FindObjectsByType<LTChunk>(FindObjectsInactive.Include,FindObjectsSortMode.None))
                    if(chunk.gameObject.scene==scene)chunk.ReleaseSpatialLOD();
            };
            AssemblyReloadEvents.beforeAssemblyReload+=()=>
            {foreach(var chunk in UnityEngine.Object.FindObjectsByType<LTChunk>(FindObjectsInactive.Include,FindObjectsSortMode.None))chunk.ReleaseLODPreview();};
        }
        public static void Apply(LTChunk chunk,LTSpatialLODMath.Output data)
        {
            string path=AssetDatabase.GetAssetPath(chunk.mesh);
            path=path.Substring(0,path.Length-6)+"_SpatialLOD.asset";
            var asset=AssetDatabase.LoadAssetAtPath<LTSpatialLODAsset>(path);
            if(!asset){asset=ScriptableObject.CreateInstance<LTSpatialLODAsset>();asset.name=chunk.name+" Spatial LOD";AssetDatabase.CreateAsset(asset,path);}
            if(!asset.vertexBank){asset.vertexBank=new Mesh{name="Vertex bank"};AssetDatabase.AddObjectToAsset(asset.vertexBank,asset);}
            Fill(asset,data);
            chunk.ReleaseLODPreview();chunk.spatialLOD=asset;chunk.lodsPending=false;
            UpdateNormals(chunk);EditorUtility.SetDirty(chunk);
        }
        public static void ApplyPreview(LTChunk chunk,LTSpatialLODMath.Output data)
        {
            var asset=ScriptableObject.CreateInstance<LTSpatialLODAsset>();
            asset.name=chunk.name+" LOD0 staging";asset.hideFlags=HideFlags.HideAndDontSave;
            try
            {
                asset.vertexBank=new Mesh{name=asset.name,hideFlags=HideFlags.HideAndDontSave};
                Fill(asset,data);chunk.SetLODPreview(asset);UpdateNormals(chunk);
            }
            catch
            {
                if(chunk.ActiveSpatialLOD==asset)chunk.ReleaseLODPreview();
                else{if(asset.vertexBank)UnityEngine.Object.DestroyImmediate(asset.vertexBank);UnityEngine.Object.DestroyImmediate(asset);}
                throw;
            }
        }
        static void Fill(LTSpatialLODAsset asset,LTSpatialLODMath.Output data)
        {
            var mesh=asset.vertexBank;mesh.Clear();mesh.indexFormat=data.vertices.Length>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices=data.vertices;mesh.normals=data.normals;mesh.uv=data.uv;mesh.triangles=data.baseIndices;mesh.RecalculateBounds();
            // LOD0 bounds must not cull a coarser level's raised vertices.
            if(data.vertices.Length>0){var bounds=new Bounds(data.vertices[0],Vector3.zero);foreach(var v in data.vertices)bounds.Encapsulate(v);mesh.bounds=bounds;}
            asset.patches=data.patches;asset.cells=data.cells;asset.divisions=data.divisions;asset.formatVersion=LTSpatialLODMath.Version;
        }
        public static void UpdateNormals(LTChunk chunk)
        {
            var asset=chunk.ActiveSpatialLOD;if(!asset||!asset.vertexBank||asset.formatVersion!=LTSpatialLODMath.Version)return;
            chunk.ReleaseSpatialLOD();
            var normals=asset.vertexBank.normals;var vertices=asset.vertexBank.vertices;
            var fine=new Dictionary<Vector3,Vector3>();var baseVertices=chunk.mesh.vertices;var baseNormals=chunk.mesh.normals;
            // A pit rim has coincident top/wall vertices with distinct normals.
            for(int i=0;i<baseVertices.Length;i++)if(Math.Abs(baseNormals[i].y)>=.000001f)fine[baseVertices[i]]=baseNormals[i];
            for(int i=0;i<vertices.Length;i++)if(Math.Abs(normals[i].y)>=.000001f&&fine.TryGetValue(vertices[i],out var shared))normals[i]=shared;
            asset.vertexBank.normals=normals;asset.vertexBank.tangents=LTStampMesh.TerrainTangents(normals);asset.revision++;
            EditorUtility.SetDirty(asset.vertexBank);EditorUtility.SetDirty(asset);
        }
    }
}
