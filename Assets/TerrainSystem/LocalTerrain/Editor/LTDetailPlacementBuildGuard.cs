using System;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    // Validate the actual build scene copy, including builds made through scripts.
    // Before source stamps are stripped, after terrain LOD0 is restored.
    public sealed class LTDetailPlacementBuildGuard : IProcessSceneWithReport
    {
        public int callbackOrder=>50;
        public void OnProcessScene(Scene scene,BuildReport report)
        {
            if(!LTRockSceneBuildDiagnostics.IsBuild(report))return;
            foreach(var root in scene.GetRootGameObjects())
            foreach(var renderer in root.GetComponentsInChildren<LTDetailRenderer>(true))
            {
                if(!renderer.enabled||!renderer.requireBakedPlacementForBuild)continue;
                try{renderer.ProcessWorldPlacement(true);}
                catch(Exception error)
                {
                    string path=renderer.name;
                    for(var parent=renderer.transform.parent;parent;parent=parent.parent)path=parent.name+"/"+path;
                    throw new BuildFailedException($"Local Terrain: {scene.path} :: {path} — сборка остановлена. {error.Message} " +
                        "В LTDetailRenderer нажмите «Запечь зоны детализации», дождитесь завершения и сохраните сцену.");
                }
            }
        }
    }
}
