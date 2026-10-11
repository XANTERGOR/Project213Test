using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    public static class LTDetailBatchCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Detail Batch Editing (no scene changes)
        public static void Run()
        {
            // Only transient ScriptableObjects, no user assets, global Undo/Redo or rendering.
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogWarning("Detail Batch: stop Play Mode before validation."); return; }
            var scenes = new List<(Scene scene, bool dirty, int roots)>();
            for (int i = 0; i < SceneManager.sceneCount; i++) { var s = SceneManager.GetSceneAt(i); scenes.Add((s, s.isDirty, s.rootCount)); }
            var active = SceneManager.GetActiveScene(); var selected = Selection.activeObject; int undo = Undo.GetCurrentGroup();
            var layer = ScriptableObject.CreateInstance<LTSurfaceLayer>(); layer.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                layer.details = new List<LTDetailEntry>{new LTDetailEntry(),new LTDetailEntry(),new LTDetailEntry()};
                LTDetailEntry.ValidateAll(layer.details);
                string first = layer.details[0].Id, last = layer.details[2].Id;
                using (var owner = new SerializedObject(layer))
                {
                    foreach (var field in LTDetailBatchPatch.Fields)
                    {
                        owner.Update();
                        var before = new string[3]; for (int i = 0; i < 3; i++) before[i] = JsonUtility.ToJson(layer.details[i]);
                        var expected = JsonUtility.FromJson<LTDetailEntry>(before[0]);
                        object value = Different(field, field.info.GetValue(expected)); field.info.SetValue(expected, value);
                        if (field.name == "densityMaskMode") typeof(LTDetailEntry).GetField("maskModeConfigured", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(expected, true);
                        var patch = new Dictionary<string, object>{{field.name, value}};
                        Check(LTDetailBatchEditor.TryStage(owner, new HashSet<string>{first}, patch, out int count, out string error) && count == 1, field.name + ": " + error);
                        owner.ApplyModifiedPropertiesWithoutUndo();
                        Check(JsonUtility.ToJson(layer.details[0]) == JsonUtility.ToJson(expected), "only selected field changes: " + field.name);
                        Check(JsonUtility.ToJson(layer.details[1]) == before[1] && JsonUtility.ToJson(layer.details[2]) == before[2], "unselected entries unchanged: " + field.name);
                    }
                    owner.Update();
                    var list = owner.FindProperty("details"); list.MoveArrayElement(0, 2); owner.ApplyModifiedPropertiesWithoutUndo(); owner.Update();
                    Check(layer.details[2].Id == first, "fixture reorder");
                    Check(LTDetailBatchEditor.TryStage(owner, new HashSet<string>{first,last}, new Dictionary<string,object>{{"density",.321f}}, out int changed, out _) && changed == 2, "two stable selections after reorder");
                    owner.ApplyModifiedPropertiesWithoutUndo();
                    Check(layer.details[0].density == 2 && layer.details[1].density == .321f && layer.details[2].density == .321f, "only selected IDs edited");
                    owner.Update();
                    Check(LTDetailBatchEditor.TryStage(owner,new HashSet<string>{first,last},new Dictionary<string,object>{{"density",.321f}},out int same,out _) && same == 0 && !owner.hasModifiedProperties,"identical values produce no pending writes");
                    owner.Update(); string snapshot = JsonUtility.ToJson(layer);
                    Check(!LTDetailBatchEditor.TryStage(owner, new HashSet<string>{first,"deleted"}, new Dictionary<string,object>{{"density",5f}}, out _, out _), "stale target rejects entire batch");
                    Check(!owner.hasModifiedProperties && JsonUtility.ToJson(layer) == snapshot, "failed preflight writes nothing");
                    Check(!LTDetailBatchEditor.TryStage(owner, new HashSet<string>{first}, new Dictionary<string,object>{{"prefab",null}}, out _, out _), "no prefab overwrite");
                    Check(!LTDetailBatchEditor.TryStage(owner, new HashSet<string>{first}, new Dictionary<string,object>{{"cullDistance",0f}}, out _, out _), "no implicit fade clamping");
                    layer.details[0].fadeStart = 1; owner.Update();
                    Check(!LTDetailBatchEditor.TryStage(owner,new HashSet<string>{layer.details[0].Id,last},new Dictionary<string,object>{{"cullDistance",10f}},out _,out _) && !owner.hasModifiedProperties,"later invalid row cannot partially change an earlier valid row");
                    owner.FindProperty("details").GetArrayElementAtIndex(0).FindPropertyRelative("id").stringValue = first;
                    Check(!LTDetailBatchEditor.TryStage(owner,new HashSet<string>{first},new Dictionary<string,object>{{"density",5f}},out _,out _),"duplicate IDs reject selection");
                    owner.Update(); // Discard fixture's intentionally staged duplicate ID.
                    layer.hideFlags |= HideFlags.NotEditable;
                    Check(!LTDetailBatchEditor.TryStage(owner,new HashSet<string>{first},new Dictionary<string,object>{{"density",5f}},out _,out _),"read-only owner rejected");
                    layer.hideFlags = HideFlags.HideAndDontSave;
                    // Regression: legacy Own flag must not defeat an explicit switch to Common.
                    layer.details[0].densityMask = true;
                    typeof(LTDetailEntry).GetField("maskModeConfigured", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(layer.details[0], false);
                    owner.Update(); string legacy = layer.details[0].Id;
                    Check(LTDetailBatchEditor.TryStage(owner,new HashSet<string>{legacy},new Dictionary<string,object>{{"densityMaskMode",LTDetailMaskMode.Common}},out _,out _),"legacy mode switch");
                    owner.ApplyModifiedPropertiesWithoutUndo();
                    Check(layer.details[0].EffectiveMaskMode == LTDetailMaskMode.Common, "explicit mode wins over legacy flag");
                    Check(layer.details[0].densityMask && layer.details[0].prefab == null, "dormant own-mask values and prefab preserved");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(layer);
                Check(Undo.GetCurrentGroup() == undo && Selection.activeObject == selected, "user Undo/selection unchanged");
                Check(SceneManager.GetActiveScene() == active && SceneManager.sceneCount == scenes.Count, "user scene selection/count unchanged");
                foreach (var s in scenes) Check(s.scene.IsValid() && s.scene.isDirty == s.dirty && s.scene.rootCount == s.roots, "user scene unchanged");
            }
            Debug.Log("Detail Batch v1 PASS: 27 isolated property edits, untouched entries/fields, multi-selection after reorder, stale-ID and range rejection, legacy mask-mode switch. Temporary objects only; no user scene/assets/Undo changes. Interactive UI and Ctrl+Z remain a separate manual check.");
        }
        static object Different(LTDetailBatchPatch.Field field, object value)
        {
            if (value is bool b) return !b;
            if (value is Vector2) return field.name == "heightOffsetRange" ? new Vector2(-.2f,.1f) : new Vector2(.2f,.8f);
            if (value is float) return field.name == "cullDistance" ? 100f : field.name == "fadeStart" ? 12f : .4f;
            if (value is LTDetailMaskMode) return LTDetailMaskMode.Own;
            if (value is LTDetailCategory) return LTDetailCategory.Stones;
            return 17;
        }
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Detail Batch FAIL: " + message); }
    }
}
