using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    public static class LTStampManagerCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Stamp Manager (no scene changes)
        public static void Run()
        {
            // No Undo operations/selection changes: the user's Undo stack and
            // terrain callbacks must not be exercised by an isolated fixture.
            var scenes = new List<(Scene scene, bool dirty, int roots)>();
            for (int i = 0; i < SceneManager.sceneCount; i++) { var s = SceneManager.GetSceneAt(i); scenes.Add((s, s.isDirty, s.rootCount)); }
            var active = SceneManager.GetActiveScene(); var selection = Selection.activeObject;
            int undo = Undo.GetCurrentGroup(); Scene preview = default; GameObject root = null;
            try
            {
                preview = EditorSceneManager.NewPreviewScene();
                root = new GameObject("Stamp Manager check"); root.hideFlags = HideFlags.HideAndDontSave; root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root, preview);
                var world = root.AddComponent<LTWorld>(); world.autoUpdate = false;
                GameObject Child(Transform parent, string name)
                {
                    var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
                    SceneManager.MoveGameObjectToScene(go, preview); go.transform.SetParent(parent, false); return go;
                }
                var a = Child(root.transform, "Дюна").AddComponent<LTHeightStamp>();
                var group = Child(root.transform, "Группа"); group.SetActive(false);
                var b = Child(group.transform, "Плотность").AddComponent<LTHeightStamp>(); b.affectHeight = false; b.densityZone = true; b.enabled = false;
                var c = Child(root.transform, "Смешанный").AddComponent<LTHeightStamp>(); c.densityZone = true;
                var hole = c.gameObject.AddComponent<LTHoleStamp>(); hole.cutMode = LTHoleStampMode.MeshVolume;
                var nested = Child(root.transform, "Вложенный мир").AddComponent<LTWorld>();
                Child(nested.transform, "Чужой штамп").AddComponent<LTHeightStamp>();
                var generated = Child(root.transform, "Generated"); world.generatedRoot = generated.transform;
                Child(generated.transform, "Не штамп мира").AddComponent<LTHoleStamp>();
                var road = Child(root.transform, "Road generated"); road.AddComponent<LTRoadGenerated>();
                Child(road.transform, "Не пользовательский штамп").AddComponent<LTHeightStamp>();
                var rows = new List<LTStampManagerModel.Row>();
                LTStampManagerModel.Collect(world, rows);
                Require(rows.Count == 4 && rows[0].stamp == a && rows[1].stamp == b && rows[2].stamp == hole && rows[3].stamp == c, "preorder/hole-before-height/excluded subtrees");
                Require(rows[1].path == "Группа/Плотность" && !rows[1].active && rows[1].kind == LTStampManagerRules.Kind.Density, "inactive parent and disabled stamp still listed");
                Require(rows[3].kind == (LTStampManagerRules.Kind.Height | LTStampManagerRules.Kind.Density), "combined stamp has both filters");
                Require(rows[2].warning.Contains("Cut Mesh"), "missing cutter warning without mesh capture");
                b.densityMode = LTStampDensityMode.Mask;
                Require(LTStampManagerModel.Warning(b).Contains("маска плотности"), "missing density mask warning");
                a.transform.localPosition = new Vector3(10, 2, -4); a.transform.localRotation = Quaternion.Euler(0, 37, 0); a.transform.localScale = new Vector3(2, 3, .5f);
                var bounds = LTStampManagerModel.FrameBounds(a);
                for (int z = -1; z <= 1; z += 2) for (int x = -1; x <= 1; x += 2)
                    Require(bounds.Contains(a.transform.TransformPoint(new Vector3(x * a.size.x * .5f, 0, z * a.size.y * .5f))), "frame bounds contain transformed footprint");
                Require(!LTStampManagerModel.Editable(world, a) && !LTStampManagerModel.CanDuplicate(world, a), "preview fixtures cannot create user Undo records");
                LTStampManagerModel.SetEnabled(world, a, false); LTStampManagerModel.Rename(world, a, "Forbidden");
                Require(a.enabled && a.name == "Дюна" && LTStampManagerModel.Duplicate(world, a) == null, "guarded mutations leave fixture unchanged");
                a.transform.SetParent(nested.transform, false);
                Require(!LTStampManagerModel.Belongs(world, a) && LTStampManagerModel.Belongs(nested, a), "moved stamp changes owner");
                a.transform.SetParent(root.transform, false); a.transform.SetSiblingIndex(0);
                UnityEngine.Object.DestroyImmediate(b.gameObject); LTStampManagerModel.Collect(world, rows);
                Require(rows.Count == 3 && rows[1].stamp == hole && rows[2].stamp == c, "deleted entries/order refresh");
                Require(!LTStampManagerModel.Belongs(world, b) && !LTStampManagerModel.Editable(world, b) && !LTStampManagerModel.CanDuplicate(world, b), "destroyed Unity references reject stale actions");
                LTStampManagerModel.SetEnabled(world, b, false); LTStampManagerModel.Rename(world, b, "Deleted");
                Require(LTStampManagerModel.Duplicate(world, b) == null, "deleted entry actions safely do nothing");
                Require(Undo.GetCurrentGroup() == undo && Selection.activeObject == selection, "no selection/Undo changes");
            }
            finally
            {
                if (root) UnityEngine.Object.DestroyImmediate(root);
                if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
                Require(SceneManager.GetActiveScene() == active && SceneManager.sceneCount == scenes.Count, "scene selection/count changed");
                foreach (var s in scenes) Require(s.scene.IsValid() && s.scene.isDirty == s.dirty && s.scene.rootCount == s.roots, "user scene changed");
            }
            Debug.Log("Stamp Manager v1 PASS: native hierarchy order, disabled/inactive stamps, nested-world/generated exclusions, mixed roles, cheap warnings, framing bounds, deletion and edit guards. No user scene/assets/Undo/selection changed. Interactive UI and Undo/Redo still require a manual check.");
        }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Stamp Manager FAIL: " + message); }
    }
}
