using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace LocalTerrainPrototype
{
    // Build-scene copies must not retain an editor preview's non-persistent mesh.
    public sealed class LTSpatialLODBuildGuard : UnityEditor.Build.IProcessSceneWithReport
    {
        public int callbackOrder=>0;
        public void OnProcessScene(UnityEngine.SceneManagement.Scene scene,UnityEditor.Build.Reporting.BuildReport report)
        {
            foreach(var root in scene.GetRootGameObjects())
            {
                foreach(var chunk in root.GetComponentsInChildren<LTChunk>(true))chunk.ShowLOD(0);
                foreach(var road in root.GetComponentsInChildren<LTRoadLOD>(true))road.Show(0);
            }
        }
    }
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
            {foreach(var chunk in UnityEngine.Object.FindObjectsByType<LTChunk>(FindObjectsInactive.Include,FindObjectsSortMode.None))chunk.ReleaseSpatialLOD();};
        }
        public static void Apply(LTChunk chunk,LTSpatialLODMath.Output data)
        {
            chunk.ReleaseSpatialLOD();
            string path=AssetDatabase.GetAssetPath(chunk.mesh);
            path=path.Substring(0,path.Length-6)+"_SpatialLOD.asset";
            var asset=AssetDatabase.LoadAssetAtPath<LTSpatialLODAsset>(path);
            if(!asset){asset=ScriptableObject.CreateInstance<LTSpatialLODAsset>();asset.name=chunk.name+" Spatial LOD";AssetDatabase.CreateAsset(asset,path);}
            if(!asset.vertexBank){asset.vertexBank=new Mesh{name="Vertex bank"};AssetDatabase.AddObjectToAsset(asset.vertexBank,asset);}
            var mesh=asset.vertexBank;mesh.Clear();mesh.indexFormat=data.vertices.Length>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices=data.vertices;mesh.normals=data.normals;mesh.uv=data.uv;mesh.triangles=data.baseIndices;mesh.RecalculateBounds();
            // LOD0 bounds must not cull a coarser level's raised vertices.
            if(data.vertices.Length>0){var bounds=new Bounds(data.vertices[0],Vector3.zero);foreach(var v in data.vertices)bounds.Encapsulate(v);mesh.bounds=bounds;}
            asset.patches=data.patches;asset.cells=data.cells;asset.divisions=data.divisions;asset.formatVersion=LTSpatialLODMath.Version;chunk.spatialLOD=asset;
            UpdateNormals(chunk);EditorUtility.SetDirty(chunk);
        }
        public static void UpdateNormals(LTChunk chunk)
        {
            var asset=chunk.spatialLOD;if(!asset||!asset.vertexBank||asset.formatVersion!=LTSpatialLODMath.Version)return;
            chunk.ReleaseSpatialLOD();
            var normals=asset.vertexBank.normals;var vertices=asset.vertexBank.vertices;
            var fine=new Dictionary<Vector3,Vector3>();var baseVertices=chunk.mesh.vertices;var baseNormals=chunk.mesh.normals;
            for(int i=0;i<baseVertices.Length;i++)fine[baseVertices[i]]=baseNormals[i];
            for(int i=0;i<vertices.Length;i++)if(fine.TryGetValue(vertices[i],out var shared))normals[i]=shared;
            asset.vertexBank.normals=normals;asset.vertexBank.tangents=LTStampMesh.TerrainTangents(normals);asset.revision++;
            EditorUtility.SetDirty(asset.vertexBank);EditorUtility.SetDirty(asset);
        }
    }
}
