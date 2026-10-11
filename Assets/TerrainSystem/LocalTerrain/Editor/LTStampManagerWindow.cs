using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed class LTStampManagerWindow : EditorWindow
    {
        const float RowHeight = 62;
        static readonly string[] Types = { "Все типы", "Высота", "Плотность", "Дырки" };
        static readonly string[] States = { "Все состояния", "Включённые", "Выключенные", "С предупреждениями" };
        [SerializeField] LTWorld world;
        [SerializeField] bool followSelection = true;
        [SerializeField] string query = "";
        [SerializeField] int typeFilter, stateFilter;
        Vector2 scroll;
        readonly List<LTStampManagerModel.Row> rows = new List<LTStampManagerModel.Row>();
        readonly List<LTStampManagerModel.Row> visible = new List<LTStampManagerModel.Row>();
        bool dirty = true;
        double nextPoll;
        Behaviour renameTarget;
        string renameText, actionError;

        [MenuItem("Tools/Local Terrain/Stamp Manager")]
        public static void Open() => Open(null);
        internal static void Open(LTWorld target)
        {
            var window = GetWindow<LTStampManagerWindow>("Штампы");
            window.minSize = new Vector2(500, 310);
            if (target) window.SetWorld(target); else window.SelectionChanged();
            window.Show();
        }
        void OnEnable()
        {
            minSize = new Vector2(500, 310);
            Selection.selectionChanged += SelectionChanged;
            EditorApplication.hierarchyChanged += Invalidate;
            EditorApplication.projectChanged += Invalidate;
            Undo.undoRedoPerformed += Invalidate;
            EditorApplication.playModeStateChanged += PlayChanged;
            EditorApplication.update += Tick;
            SelectionChanged(); Invalidate();
        }
        void OnDisable()
        {
            Selection.selectionChanged -= SelectionChanged;
            EditorApplication.hierarchyChanged -= Invalidate;
            EditorApplication.projectChanged -= Invalidate;
            Undo.undoRedoPerformed -= Invalidate;
            EditorApplication.playModeStateChanged -= PlayChanged;
            EditorApplication.update -= Tick;
            rows.Clear(); visible.Clear(); renameTarget = null;
        }
        void PlayChanged(PlayModeStateChange state) { renameTarget = null; Invalidate(); }
        void SetWorld(LTWorld target)
        {
            if (world == target) return;
            world = target; scroll = Vector2.zero; renameTarget = null; actionError = null; Invalidate();
        }
        void SelectionChanged()
        {
            if (followSelection && Selection.activeGameObject)
            {
                var owner = Selection.activeGameObject.GetComponentInParent<LTWorld>(true);
                if (LTStampManagerModel.SceneWorld(owner)) SetWorld(owner);
            }
            Repaint();
        }
        void Invalidate() { dirty = true; Repaint(); }
        void Tick()
        {
            // No project-wide search. Poll while interacting with the window;
            // captures no textures, vertices, terrain heights or materials.
            if (!hasFocus && mouseOverWindow != this) return;
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + .5; Invalidate();
        }
        void Filter()
        {
            visible.Clear();
            foreach (var row in rows)
                if (row.stamp && LTStampManagerRules.Matches(row.kind, row.active, !string.IsNullOrEmpty(row.warning), row.path, row.type, query, typeFilter, stateFilter)) visible.Add(row);
        }
        void OnGUI()
        {
            EditorGUILayout.Space(3);
            using (new EditorGUILayout.HorizontalScope())
            {
                var target = (LTWorld)EditorGUILayout.ObjectField(new GUIContent("LTWorld"), world, typeof(LTWorld), true);
                if (target != world) { followSelection = false; SetWorld(target); }
                bool follow = GUILayout.Toggle(followSelection, new GUIContent("За выделением", "Переключать LTWorld по выбранному объекту"), EditorStyles.miniButton, GUILayout.Width(114));
                if (follow != followSelection) { followSelection = follow; if (follow) SelectionChanged(); }
            }
            if (!LTStampManagerModel.SceneWorld(world))
            {
                rows.Clear(); visible.Clear();
                EditorGUILayout.HelpBox("Выбери LTWorld или его штамп в обычной открытой сцене. Prefab Mode и ассеты не редактируются этой панелью.", MessageType.Info); return;
            }
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                query = GUILayout.TextField(query, GUI.skin.FindStyle("ToolbarSearchTextField") ?? EditorStyles.toolbarTextField);
                typeFilter = EditorGUILayout.Popup(typeFilter, Types, EditorStyles.toolbarPopup, GUILayout.Width(112));
                stateFilter = EditorGUILayout.Popup(stateFilter, States, EditorStyles.toolbarPopup, GUILayout.Width(148));
                if (EditorGUI.EndChangeCheck()) { scroll = Vector2.zero; Filter(); }
                if (GUILayout.Button(new GUIContent("×", "Сбросить поиск и фильтры"), EditorStyles.toolbarButton, GUILayout.Width(24)))
                { query = ""; typeFilter = stateFilter = 0; scroll = Vector2.zero; Filter(); GUI.FocusControl(null); }
            }
            if (dirty && Event.current.type == EventType.Layout)
            { LTStampManagerModel.Collect(world, rows); Filter(); dirty = false; }
            EditorGUILayout.LabelField($"Показано {visible.Count} из {rows.Count}  ·  Порядок Hierarchy", EditorStyles.miniLabel);
            if (EditorApplication.isPlayingOrWillChangePlaymode) EditorGUILayout.HelpBox("Play Mode: только просмотр и выделение.", MessageType.Info);
            else if (!world.autoUpdate) EditorGUILayout.HelpBox("Auto Update выключен. Изменения штампов применяются через LTWorld → Apply Pending Changes.", MessageType.Info);
            if (!world.source) EditorGUILayout.HelpBox("У LTWorld не назначен Source. Панель не перестраивает террейн самостоятельно.", MessageType.Warning);
            if (!string.IsNullOrEmpty(actionError)) EditorGUILayout.HelpBox(actionError, MessageType.Warning);
            DrawRename();
            var viewport = GUILayoutUtility.GetRect(0, 100000, 0, 100000, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            float width = Mathf.Max(460, viewport.width - 18);
            scroll.y = Mathf.Clamp(scroll.y, 0, Mathf.Max(0, visible.Count * RowHeight - viewport.height));
            scroll = GUI.BeginScrollView(viewport, scroll, new Rect(0, 0, width, visible.Count * RowHeight));
            int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowHeight));
            int last = Mathf.Min(visible.Count, Mathf.CeilToInt((scroll.y + viewport.height) / RowHeight) + 1);
            for (int i = first; i < last; i++) DrawRow(new Rect(0, i * RowHeight, width, RowHeight - 2), visible[i]);
            if (visible.Count == 0) GUI.Label(new Rect(10, 8, width - 20, 36), rows.Count == 0 ? "В этом LTWorld пока нет штампов высоты, плотности или дырок." : "По этим фильтрам штампы не найдены.", EditorStyles.wordWrappedMiniLabel);
            GUI.EndScrollView();
            EditorGUILayout.LabelField("Галочка — компонент. Имя — выделить. «К объекту» — камера. «⋮» — действия.", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Быстрые предупреждения не заменяют проверку топологии Cut Mesh в Inspector.", EditorStyles.miniLabel);
        }
        void DrawRow(Rect rect, LTStampManagerModel.Row row)
        {
            if (!row.stamp) { dirty = true; return; }
            var stamp = row.stamp; bool selected = Selection.activeGameObject == stamp.gameObject;
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(rect, selected ? new Color(.2f, .4f, .6f, .4f) : new Color(0, 0, 0, .10f));
            using (new EditorGUI.DisabledScope(!LTStampManagerModel.Editable(world, stamp)))
            {
                bool enabled = GUI.Toggle(new Rect(7, rect.y + 6, 18, 18), stamp.enabled, new GUIContent("", "Включить/выключить только этот компонент. Не активирует родителей."));
                if (enabled != stamp.enabled) Run(() => LTStampManagerModel.SetEnabled(world, stamp, enabled));
            }
            GUI.Label(new Rect(28, rect.y + 6, 36, 18), row.order.ToString(), EditorStyles.miniLabel);
            if (GUI.Button(new Rect(64, rect.y + 4, rect.width - 192, 20), new GUIContent(stamp.name, row.path + "\n" + row.type), EditorStyles.label))
            { Selection.activeGameObject = stamp.gameObject; EditorGUIUtility.PingObject(stamp.gameObject); }
            if (GUI.Button(new Rect(rect.width - 125, rect.y + 4, 94, 20), "К объекту", EditorStyles.miniButton)) Frame(stamp);
            if (GUI.Button(new Rect(rect.width - 28, rect.y + 4, 24, 20), "⋮", EditorStyles.miniButton)) Menu(stamp);
            GUI.Label(new Rect(28, rect.y + 25, rect.width - 36, 15), new GUIContent(row.type + "  ·  " + row.state + "  ·  " + row.path, row.path), EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(row.warning))
            {
                GUI.Label(new Rect(7, rect.y + 41, 16, 16), EditorGUIUtility.IconContent("console.warnicon.sml"));
                GUI.Label(new Rect(28, rect.y + 41, rect.width - 36, 16), new GUIContent(row.warning.Replace('\n', ' '), row.warning), EditorStyles.miniLabel);
            }
        }
        void Frame(Behaviour stamp)
        {
            if (!LTStampManagerModel.Belongs(world, stamp)) return;
            Selection.activeGameObject = stamp.gameObject;
            var view = SceneView.lastActiveSceneView;
            if (!view) { actionError = "Открой Scene View для перехода камеры."; return; }
            view.Frame(LTStampManagerModel.FrameBounds(stamp), false); view.Repaint();
        }
        void Menu(Behaviour stamp)
        {
            var owner = world; var menu = new GenericMenu();
            if (LTStampManagerModel.Editable(owner, stamp))
                menu.AddItem(new GUIContent("Переименовать объект…"), false, () => { if (!stamp || owner != world) return; renameTarget = stamp; renameText = stamp.name; Repaint(); });
            else menu.AddDisabledItem(new GUIContent("Переименовать объект…"));
            if (LTStampManagerModel.CanDuplicate(owner, stamp)) menu.AddItem(new GUIContent("Дублировать объект с дочерними"), false, () => Run(() =>
            {
                if (!stamp || !LTStampManagerModel.CanDuplicate(owner, stamp)) return;
                if (stamp.transform.childCount > 0 && !EditorUtility.DisplayDialog("Дублировать штамп", "Будут скопированы объект «" + stamp.name + "» и все его дочерние объекты.", "Дублировать", "Отмена")) return;
                var copy = LTStampManagerModel.Duplicate(owner, stamp); if (copy) Selection.activeGameObject = copy;
            }));
            else menu.AddDisabledItem(new GUIContent("Дублирование недоступно (режим, prefab или вложенный мир)"));
            menu.ShowAsContext();
        }
        void DrawRename()
        {
            if (!renameTarget) return;
            if (!LTStampManagerModel.Editable(world, renameTarget)) { renameTarget = null; return; }
            using (new EditorGUILayout.HorizontalScope())
            {
                renameText = EditorGUILayout.TextField("Новое имя", renameText);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(renameText)))
                    if (GUILayout.Button("OK", GUILayout.Width(42))) { Run(() => LTStampManagerModel.Rename(world, renameTarget, renameText)); renameTarget = null; }
                if (GUILayout.Button("Отмена", GUILayout.Width(62))) renameTarget = null;
            }
        }
        void Run(Action action)
        {
            try { action(); actionError = null; }
            catch (Exception e) { actionError = e.Message; Debug.LogException(e); }
            Invalidate();
        }
    }
}
