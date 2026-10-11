using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalTerrainPrototype
{
    internal static class LTDetailBatchEditor
    {
        // UI-only state: never serialized into layers/stamps or candidate identities.
        sealed class State
        {
            internal bool open;
            internal readonly bool[] groups = { true, false, false, false };
            internal readonly HashSet<string> selected = new HashSet<string>();
            internal readonly HashSet<string> fields = new HashSet<string>();
            internal HashSet<string> unique = new HashSet<string>();
            internal readonly LTDetailEntry values = new LTDetailEntry();
            internal string message;
            internal bool error;
        }
        static readonly ConditionalWeakTable<Object, State> States = new ConditionalWeakTable<Object, State>();
        static bool Supported(SerializedObject owner) => owner != null && owner.targetObject && !owner.isEditingMultipleObjects &&
            (owner.targetObject is LTSurfaceLayer || owner.targetObject is LTDetailStamp);
        static bool CanApply(SerializedObject owner) => Supported(owner) && !EditorApplication.isPlayingOrWillChangePlaymode &&
            !EditorApplication.isCompiling && (owner.targetObject.hideFlags & HideFlags.NotEditable) == 0 &&
            (!(owner.targetObject is Component component) || (component.gameObject.hideFlags & HideFlags.NotEditable) == 0);
        static State Get(SerializedObject owner) => States.GetValue(owner.targetObject, _ => new State());
        static string Id(SerializedProperty entry) => entry.FindPropertyRelative("id")?.stringValue;
        static void Sync(SerializedObject owner, State state)
        {
            var list = owner.FindProperty("details"); var ids = new List<string>();
            for (int i = 0; i < list.arraySize; i++) ids.Add(Id(list.GetArrayElementAtIndex(i)));
            state.unique = LTDetailBatchPatch.UniqueIds(ids); state.selected.IntersectWith(state.unique);
        }
        internal static void DrawHeader(SerializedObject owner)
        {
            if (!Supported(owner)) return;
            var state = Get(owner); Sync(owner, state);
            bool changed = GUI.changed;
            state.open = EditorGUILayout.Foldout(state.open, "Групповое редактирование объектов", true);
            if (state.open)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Выбрано: " + state.selected.Count, EditorStyles.miniLabel);
                    if (GUILayout.Button("Выбрать все", EditorStyles.miniButton)) { state.selected.UnionWith(state.unique); state.message = null; }
                    if (GUILayout.Button("Снять выбор", EditorStyles.miniButton)) { state.selected.Clear(); state.message = null; }
                }
                EditorGUILayout.HelpBox("Отметь записи галочкой слева от имени, затем ниже списка отметь параметры для применения. Галочка выбора не включает и не выключает объект.", MessageType.Info);
                if (state.unique.Count != owner.FindProperty("details").arraySize)
                    EditorGUILayout.HelpBox("Записи с пустым или повторяющимся ID недоступны для группового выбора. Сначала исправьте список в Inspector.", MessageType.Warning);
            }
            GUI.changed = changed;
        }
        internal static Rect DrawSelection(Rect line, SerializedProperty entry)
        {
            var owner = entry.serializedObject;
            if (!Supported(owner) || !entry.propertyPath.StartsWith("details.Array.data[", StringComparison.Ordinal)) return line;
            var state = Get(owner); if (!state.open) return line;
            bool changed = GUI.changed; string id = Id(entry);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(id) || !state.unique.Contains(id)))
            {
                bool selected = state.selected.Contains(id);
                bool next = GUI.Toggle(new Rect(EditorGUI.IndentedRect(line).x, line.y, 18, line.height), selected, new GUIContent("", "Выбрать для группового применения"));
                if (next != selected) { if (next) state.selected.Add(id); else state.selected.Remove(id); state.message = null; }
            }
            GUI.changed = changed; line.x += 22; line.width -= 22; return line;
        }
        static object Read(SerializedProperty property, LTDetailBatchPatch.Field field)
        {
            var p = property.FindPropertyRelative(field.name); var type = field.info.FieldType;
            if (type == typeof(float)) return p.floatValue;
            if (type == typeof(bool)) return p.boolValue;
            if (type == typeof(Vector2)) return p.vector2Value;
            if (type.IsEnum) return Enum.ToObject(type, p.intValue);
            return p.intValue;
        }
        static void Write(SerializedProperty property, LTDetailBatchPatch.Field field, object value)
        {
            var p = property.FindPropertyRelative(field.name); var type = field.info.FieldType;
            if (type == typeof(float)) p.floatValue = (float)value;
            else if (type == typeof(bool)) p.boolValue = (bool)value;
            else if (type == typeof(Vector2)) p.vector2Value = (Vector2)value;
            else p.intValue = Convert.ToInt32(value);
            if (field.name == "densityMaskMode") property.FindPropertyRelative("maskModeConfigured").boolValue = true;
        }
        internal static bool TryStage(SerializedObject owner, ISet<string> selected, IDictionary<string, object> changes, out int count, out string error)
        {
            count = 0; error = null;
            if (!CanApply(owner)) { error = "Групповое применение недоступно для этого объекта или режима редактора."; return false; }
            if (changes.Count == 0 || selected.Count == 0) { error = "Выбери объекты и хотя бы один параметр."; return false; }
            var list = owner.FindProperty("details"); var ids = new List<string>();
            for (int i = 0; i < list.arraySize; i++) ids.Add(Id(list.GetArrayElementAtIndex(i)));
            var unique = LTDetailBatchPatch.UniqueIds(ids);
            foreach (var id in selected) if (string.IsNullOrEmpty(id) || !unique.Contains(id))
            { error = "Список изменился или содержит повторяющиеся ID. Повтори выбор объектов."; return false; }
            var targets = new List<SerializedProperty>();
            for (int i = 0; i < list.arraySize; i++)
            {
                var entry = list.GetArrayElementAtIndex(i); if (!selected.Contains(Id(entry))) continue;
                var current = new LTDetailEntry { fadeStart = entry.FindPropertyRelative("fadeStart").floatValue, cullDistance = entry.FindPropertyRelative("cullDistance").floatValue };
                if (!LTDetailBatchPatch.Validate(current, changes, out error)) { error = "Запись " + (i + 1) + ": " + error; return false; }
                targets.Add(entry);
            }
            // All targets validated first. Only marked properties are written.
            foreach (var entry in targets)
            {
                bool changed = false;
                foreach (var change in changes)
                {
                    var field = LTDetailBatchPatch.Find(change.Key);
                    if (Equals(Read(entry, field), change.Value) && (field.name != "densityMaskMode" || entry.FindPropertyRelative("maskModeConfigured").boolValue)) continue;
                    Write(entry, field, change.Value); changed = true;
                }
                if (changed) count++;
            }
            return true;
        }
        static object DrawValue(LTDetailBatchPatch.Field field, object value)
        {
            var type = field.info.FieldType;
            if (type == typeof(float)) return EditorGUILayout.FloatField((float)value);
            if (type == typeof(bool)) return EditorGUILayout.Toggle((bool)value);
            if (type == typeof(Vector2)) return EditorGUILayout.Vector2Field(GUIContent.none, (Vector2)value);
            if (type == typeof(LTDetailCategory)) return (LTDetailCategory)((int)(LTDetailCategory)EditorGUILayout.EnumFlagsField((LTDetailCategory)value) & (int)LTDetailCategory.All);
            if (type.IsEnum) return EditorGUILayout.EnumPopup((Enum)value);
            return EditorGUILayout.IntField((int)value);
        }
        internal static void DrawPanel(SerializedObject owner)
        {
            if (!Supported(owner)) return;
            var state = Get(owner); if (!state.open) return;
            Sync(owner, state); bool changed = GUI.changed;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Применить к выбранным: " + state.selected.Count, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(state.selected.Count == 0))
                    if (GUILayout.Button("Взять значения первого выбранного"))
                    {
                        var list = owner.FindProperty("details");
                        for (int i = 0; i < list.arraySize; i++)
                        {
                            var entry = list.GetArrayElementAtIndex(i); if (!state.selected.Contains(Id(entry))) continue;
                            foreach (var field in LTDetailBatchPatch.Fields) field.info.SetValue(state.values, Read(entry, field));
                            state.message = "Значения взяты из записи " + (i + 1) + ". Отметь нужные параметры и нажми «Применить»."; state.error = false; break;
                        }
                    }
                for (int group = 0; group < LTDetailBatchPatch.Groups.Length; group++)
                {
                    state.groups[group] = EditorGUILayout.Foldout(state.groups[group], LTDetailBatchPatch.Groups[group], true);
                    if (!state.groups[group]) continue;
                    foreach (var field in LTDetailBatchPatch.Fields)
                    {
                        if (field.group != group) continue;
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            bool use = EditorGUILayout.ToggleLeft(new GUIContent(field.label, "Менять только если этот параметр отмечен"), state.fields.Contains(field.name), GUILayout.Width(Mathf.Min(220, EditorGUIUtility.currentViewWidth * .48f)));
                            if (use) state.fields.Add(field.name); else state.fields.Remove(field.name);
                            using (new EditorGUI.DisabledScope(!use)) field.info.SetValue(state.values, DrawValue(field, field.info.GetValue(state.values)));
                        }
                    }
                }
                EditorGUILayout.HelpBox("Изменятся только отмеченные поля, включая поля в свёрнутых разделах. Своя маска действует только в режиме «Своя»; общая маска слоя не изменяется. Префабы, ID и порядок сохраняются.", MessageType.None);
                if (owner.targetObject is LTSurfaceLayer) EditorGUILayout.LabelField("Изменения слоя действуют во всех использующих его штампах.", EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Снять отметки параметров", EditorStyles.miniButton)) state.fields.Clear();
                    using (new EditorGUI.DisabledScope(!CanApply(owner) || state.selected.Count == 0 || state.fields.Count == 0))
                        if (GUILayout.Button("Применить (" + state.fields.Count + " полей)", EditorStyles.miniButton))
                        {
                            var patch = new Dictionary<string, object>();
                            foreach (var name in state.fields) patch.Add(name, LTDetailBatchPatch.Find(name).info.GetValue(state.values));
                            // Flush unrelated Inspector edits into their normal Undo record first.
                            owner.ApplyModifiedProperties(); owner.Update();
                            if (TryStage(owner, state.selected, patch, out int count, out string error))
                            {
                                if (count > 0)
                                {
                                    Undo.IncrementCurrentGroup(); int undo = Undo.GetCurrentGroup();
                                    Undo.SetCurrentGroupName("Параметры выбранных объектов детализации");
                                    owner.ApplyModifiedProperties(); Undo.CollapseUndoOperations(undo); Undo.IncrementCurrentGroup();
                                }
                                state.message = count > 0 ? "Изменено записей: " + count + ". Отмена — Ctrl+Z." : "Значения уже совпадают. Изменений нет."; state.error = false;
                            }
                            else { state.message = error; state.error = true; }
                        }
                }
                if (!string.IsNullOrEmpty(state.message)) EditorGUILayout.HelpBox(state.message, state.error ? MessageType.Warning : MessageType.Info);
            }
            GUI.changed = changed;
        }
    }
}
