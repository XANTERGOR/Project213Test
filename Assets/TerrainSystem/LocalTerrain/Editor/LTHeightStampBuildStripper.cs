using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    // Runs on the build's scene copy, never the author's scene or Play Mode.
    public sealed class LTHeightStampBuildStripper : IProcessSceneWithReport
    {
        public int callbackOrder => 100;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!LTRockSceneBuildDiagnostics.IsBuild(report)) return;
            var stamps = new List<LTHeightStamp>();
            var worlds = new HashSet<LTWorld>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var stamp in root.GetComponentsInChildren<LTHeightStamp>(true))
                {
                    var world = stamp.GetComponentInParent<LTWorld>(true);
                    if (!world) continue; // No owning terrain: no evidence of a baked result.
                    stamps.Add(stamp);
                    worlds.Add(world);
                }

            // Validate every owner before removing any component.
            foreach (var world in worlds) ValidateBakedTerrain(world);
            foreach (var stamp in stamps)
                UnityEngine.Object.DestroyImmediate(stamp); // Preserve children and other components.
        }

        static void ValidateBakedTerrain(LTWorld world)
        {
            string repair = $"LTWorld '{world.name}' ({world.gameObject.scene.path}): " +
                "нельзя исключить высотные штампы без сохранённого рельефа. " +
                "Выполните Rebuild All + Save Meshes, дождитесь завершения и сохраните сцену.";
            if (!world.generatedRoot || world.chunksX < 1 || world.chunksZ < 1)
                throw new BuildFailedException(repair);
            var cells = new HashSet<Vector2Int>();
            foreach (var chunk in world.generatedRoot.GetComponentsInChildren<LTChunk>(true))
            {
                if (chunk.GetComponentInParent<LTWorld>(true) != world) continue;
                if (!chunk.mesh || !AssetDatabase.Contains(chunk.mesh) || chunk.lodsPending ||
                    chunk.x < 0 || chunk.x >= world.chunksX || chunk.z < 0 || chunk.z >= world.chunksZ ||
                    !cells.Add(new Vector2Int(chunk.x, chunk.z)))
                    throw new BuildFailedException(repair + " Чанк: " + chunk.name);
            }
            if (cells.Count != (long)world.chunksX * world.chunksZ)
                throw new BuildFailedException(repair + " Сохранены не все чанки.");
        }
    }
}
