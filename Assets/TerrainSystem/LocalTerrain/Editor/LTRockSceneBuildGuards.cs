using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    // These callbacks also run for Play-mode scene copies (report == null).
    // Do not mistake that for a failed player build. Keep the asset requirement
    // strict for actual builds, including a build context without a report.
    // https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Build.IProcessSceneWithReport.OnProcessScene.html
    internal static class LTRockSceneBuildDiagnostics
    {
        internal static bool IsBuild(BuildReport report)=>report!=null||BuildPipeline.isBuildingPlayer;
        internal static void Missing(Component component,bool building,string problem,string playConsequence)
        {
            string path=component.name;
            for(var parent=component.transform.parent;parent;parent=parent.parent)path=parent.name+"/"+path;
            var scene=component.gameObject.scene;
            string location=(string.IsNullOrEmpty(scene.path)?scene.name:scene.path)+" :: "+path;
            string repair="Выйдите из Play Mode, в LT World нажмите Rebuild All + Save Meshes, дождитесь завершения и сохраните сцену.";
            string message="Local Terrain: "+location+" — "+problem+". ";
            if(building)throw new BuildFailedException(message+"Сборка остановлена: необходимы сохранённые LOD. "+repair);
            Debug.LogWarning(message+"Play Mode не заблокирован. "+playConsequence+" "+repair,component);
        }
    }

    public sealed class LTRockRenderBuildGuard : IProcessSceneWithReport
    {
        public int callbackOrder=>1;
        public void OnProcessScene(Scene scene,BuildReport report)
        {
            bool building=LTRockSceneBuildDiagnostics.IsBuild(report);
            foreach(var root in scene.GetRootGameObjects())foreach(var stamp in root.GetComponentsInChildren<LTMeshStamp>(true))
            {
                if(!stamp.enabled||!stamp.affectHeight||(!stamp.trimRockInsideTerrain&&stamp.mode!=LTMeshConformMode.Cave))continue;
                if(!stamp.sourceMesh)continue;
                if(!stamp.renderAsset||!stamp.renderAsset.Valid)
                {
                    LTRockSceneBuildDiagnostics.Missing(stamp,building,
                        !stamp.renderAsset?"не назначен ресурс LOD скалы и перехода":"ресурс LOD скалы и перехода неполный (отсутствуют необходимые меши)",
                        "Текущая геометрия и коллайдер оставлены как есть; восстановление скалы/перехода из сохранённых LOD недоступно.");
                    continue;
                }
                // Only the processed scene copy is touched. Runtime recreates the
                // hidden transition, just as before this diagnostic split.
                stamp.GetComponent<MeshFilter>().sharedMesh=stamp.renderAsset.rocks[0];
                var collider=stamp.GetComponent<MeshCollider>();if(collider)collider.sharedMesh=stamp.renderAsset.combined;
            }
        }
    }

    // Scene copies must not retain an editor preview's non-persistent mesh.
    public sealed class LTSpatialLODBuildGuard : IProcessSceneWithReport
    {
        public int callbackOrder=>0;
        public void OnProcessScene(Scene scene,BuildReport report)
        {
            bool building=LTRockSceneBuildDiagnostics.IsBuild(report);
            foreach(var root in scene.GetRootGameObjects())
            {
                foreach(var chunk in root.GetComponentsInChildren<LTChunk>(true))
                {
                    if(chunk.lodsPending)LTRockSceneBuildDiagnostics.Missing(chunk,building,
                        "построение LOD чанка не завершено","Для чанка используется LOD0; дальние LOD пока недоступны.");
                    chunk.ShowLOD(0);
                }
                foreach(var road in root.GetComponentsInChildren<LTRoadLOD>(true))road.Show(0);
            }
        }
    }
}
