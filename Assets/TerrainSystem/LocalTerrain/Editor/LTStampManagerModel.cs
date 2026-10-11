using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Read-only listing/diagnostics. Never capture masks, evaluate terrain or
    // validate mesh topology here: a browser refresh must not trigger a build.
    internal static class LTStampManagerModel
    {
        internal sealed class Row
        {
            internal Behaviour stamp;
            internal int order;
            internal LTStampManagerRules.Kind kind;
            internal string path, type, state, warning;
            internal bool active;
        }

        internal static bool SceneWorld(LTWorld world) => world && !EditorUtility.IsPersistent(world) &&
            world.gameObject.scene.IsValid() && world.gameObject.scene.isLoaded &&
            PrefabStageUtility.GetPrefabStage(world.gameObject) == null;

        static bool Excluded(Transform node, LTWorld world) => node == world.generatedRoot ||
            node.GetComponent<LTWorld>() || node.GetComponent<LTChunk>() ||
            node.GetComponent<LTRoadGenerated>() || node.GetComponent<LTRoadJunctionGenerated>();

        internal static bool Belongs(LTWorld world, Behaviour stamp)
        {
            if (!SceneWorld(world) || !stamp || !(stamp is LTHeightStamp || stamp is LTHoleStamp) || EditorUtility.IsPersistent(stamp)) return false;
            for (var node = stamp.transform; node && node != world.transform; node = node.parent)
            {
                if (Excluded(node, world)) return false;
                if (node.parent == world.transform) return true;
            }
            return false;
        }

        internal static bool Editable(LTWorld world, Behaviour stamp) => Belongs(world, stamp) &&
            !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling &&
            !EditorSceneManager.IsPreviewScene(world.gameObject.scene) &&
            (stamp.hideFlags & HideFlags.NotEditable) == 0 && (stamp.gameObject.hideFlags & HideFlags.NotEditable) == 0;

        internal static void Collect(LTWorld world, List<Row> rows)
        {
            rows.Clear(); if (!SceneWorld(world)) return;
            // Deliberately include inactive subtrees for re-enabling/diagnosis.
            // Within each object the engine captures a hole before height/density.
            void Walk(Transform parent, string prefix)
            {
                foreach (Transform child in parent)
                {
                    if (Excluded(child, world)) continue;
                    string path = prefix.Length == 0 ? child.name : prefix + "/" + child.name;
                    void Add(Behaviour stamp)
                    {
                        if (!stamp) return;
                        var height = stamp as LTHeightStamp;
                        var kind = height ? LTStampManagerRules.Classify(height.affectHeight, height.densityZone) : LTStampManagerRules.Kind.Hole;
                        var hole = stamp as LTHoleStamp;
                        string type = hole ? hole.cutMode == LTHoleStampMode.MeshVolume ? "Дырка · Mesh" : "Дырка · Форма/маска" : LTStampManagerRules.Label(kind);
                        string state = !stamp.enabled ? "Компонент выключен" : !stamp.gameObject.activeInHierarchy ? "Объект/родитель неактивен" :
                            !world.isActiveAndEnabled ? "LTWorld выключен" : "Включён";
                        rows.Add(new Row { stamp = stamp, order = rows.Count + 1, kind = kind, path = path, type = type,
                            active = stamp.isActiveAndEnabled && world.isActiveAndEnabled, state = state, warning = Warning(stamp) });
                    }
                    Add(child.GetComponent<LTHoleStamp>()); Add(child.GetComponent<LTHeightStamp>());
                    Walk(child, path);
                }
            }
            Walk(world.transform, "");
        }

        internal static string Warning(Behaviour stamp)
        {
            var warnings = new List<string>();
            var scale = stamp.transform.lossyScale;
            if (!LTStampManagerRules.Positive(scale.x) || !LTStampManagerRules.Positive(scale.y) || !LTStampManagerRules.Positive(scale.z))
                warnings.Add("Нужен положительный конечный Scale.");
            var height = stamp as LTHeightStamp;
            var hole = stamp as LTHoleStamp;
            bool volume = hole && hole.cutMode == LTHoleStampMode.MeshVolume;
            if (!volume && (Vector3.Dot(stamp.transform.up, Vector3.up) < .9999f || scale.y <= .00001f))
                warnings.Add("Для этого штампа допустим только поворот по Y; Scale Y должен быть > 0.00001.");
            if (height)
            {
                if (!height.affectHeight && !height.densityZone) warnings.Add("И высота, и плотность выключены: штамп не влияет на поверхность.");
                if (!LTStampManagerRules.Positive(height.size.x) || !LTStampManagerRules.Positive(height.size.y)) warnings.Add("Некорректный Size.");
                if (height.affectHeight && height.mask && !height.mask.isReadable) warnings.Add("Mask: выключен Read/Write; высота не применяется.");
                if (height.densityZone)
                {
                    if (!LTStampManagerRules.Positive(height.densityCellSizeMin) || !LTStampManagerRules.Positive(height.densityCellSizeMax) || height.densityCellSizeMax < height.densityCellSizeMin)
                        warnings.Add("Проверь Min/Max Cell Size плотности.");
                    if (height.densityMode == LTStampDensityMode.Mask)
                    {
                        var mask = height.densityMask ? height.densityMask : height.mask;
                        if (!mask) warnings.Add("Не назначена маска плотности (Density Mask или Mask).");
                        else if (!mask.isReadable) warnings.Add("Маска плотности: выключен Read/Write.");
                        if (!LTStampManagerRules.ValidRemap(height.densityMaskRange.x, height.densityMaskRange.y, height.densityMaskPower))
                            warnings.Add("Проверь диапазон маски плотности и Power.");
                    }
                }
            }
            if (hole)
            {
                if (!LTStampManagerRules.Positive(hole.edgeCellSize) || !LTStampManagerRules.NonNegative(hole.lodEdgeCellSize))
                    warnings.Add("Проверь Edge Cell Size / LOD Edge Cell Size.");
                if (volume)
                {
                    if (!hole.cutMesh) warnings.Add("Не назначен Cut Mesh.");
                    else if (!hole.cutMesh.isReadable) warnings.Add("Cut Mesh: выключен Read/Write.");
                    if (!LTStampManagerRules.NonNegative(hole.meshLodContourError)) warnings.Add("Некорректный LOD Contour Error.");
                }
                else
                {
                    if (!LTStampManagerRules.Positive(hole.size.x) || !LTStampManagerRules.Positive(hole.size.y)) warnings.Add("Некорректный Size.");
                    if (hole.mask && !hole.mask.isReadable) warnings.Add("Mask: выключен Read/Write.");
                    if (!LTStampManagerRules.Finite(hole.maskThreshold) || hole.maskThreshold < .001f || hole.maskThreshold > 1) warnings.Add("Порог маски должен быть от 0.001 до 1.");
                    if (!LTStampManagerRules.NonNegative(hole.lodCornerError)) warnings.Add("Некорректный LOD Corner Error.");
                }
                // Non-serialized result of the normal build, not a new capture.
                if (!string.IsNullOrEmpty(hole.status)) warnings.Add("Последняя сборка: " + hole.status);
            }
            return string.Join("\n", warnings);
        }

        internal static Bounds FrameBounds(Behaviour stamp)
        {
            var hole = stamp as LTHoleStamp; var height = stamp as LTHeightStamp;
            Bounds local;
            if (hole && hole.cutMode == LTHoleStampMode.MeshVolume && hole.cutMesh) local = hole.cutMesh.bounds;
            else
            {
                var size = height ? height.size : hole ? hole.size : Vector2.one;
                local = new Bounds(Vector3.zero, new Vector3(size.x, 1, size.y));
            }
            var matrix = stamp.transform.localToWorldMatrix;
            var result = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var point = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                result.Encapsulate(matrix.MultiplyPoint3x4(point));
            }
            if (!Finite(result.center) || !Finite(result.size)) return new Bounds(Finite(stamp.transform.position) ? stamp.transform.position : Vector3.zero, Vector3.one * 5);
            result.Expand(1); return result;
        }
        static bool Finite(Vector3 p) => LTStampManagerRules.Finite(p.x) && LTStampManagerRules.Finite(p.y) && LTStampManagerRules.Finite(p.z);

        internal static void SetEnabled(LTWorld world, Behaviour stamp, bool enabled)
        {
            if (!Editable(world, stamp) || stamp.enabled == enabled) return;
            Undo.RecordObject(stamp, enabled ? "Включить штамп" : "Выключить штамп");
            stamp.enabled = enabled; Changed(stamp);
        }
        internal static void Rename(LTWorld world, Behaviour stamp, string name)
        {
            if (!Editable(world, stamp)) return;
            name = (name ?? "").Trim(); if (name.Length == 0 || stamp.name == name) return;
            Undo.RecordObject(stamp.gameObject, "Переименовать штамп");
            stamp.gameObject.name = name; Changed(stamp.gameObject);
        }
        internal static bool CanDuplicate(LTWorld world, Behaviour stamp)
        {
            if (!Editable(world, stamp)) return false;
            // A prefab child may duplicate a larger root. Do not expand the action
            // beyond the row's object; let the user duplicate its root in Hierarchy.
            if (PrefabUtility.IsPartOfPrefabInstance(stamp) && PrefabUtility.GetNearestPrefabInstanceRoot(stamp.gameObject) != stamp.gameObject) return false;
            foreach (var node in stamp.GetComponentsInChildren<Transform>(true)) if (Excluded(node, world)) return false;
            return true;
        }
        internal static GameObject Duplicate(LTWorld world, Behaviour stamp)
        {
            if (!CanDuplicate(world, stamp)) return null;
            // Unity's native duplication preserves prefab overrides/references
            // and records creation Undo. No copied generated output is permitted.
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Дублировать штамп");
            try { return GameObjectUtility.DuplicateGameObject(stamp.gameObject); }
            finally { Undo.CollapseUndoOperations(group); }
        }
        static void Changed(UnityEngine.Object target)
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            EditorUtility.SetDirty(target); SceneView.RepaintAll();
            // Normal Detect/Undo callbacks own terrain invalidation. No forced
            // Refresh, SaveAssets or scene save from the browser.
        }
    }
}
