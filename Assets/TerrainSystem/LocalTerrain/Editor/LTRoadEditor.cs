using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalTerrainPrototype
{
    [CustomEditor(typeof(LTRoad))]
    public sealed class LTRoadEditor : UnityEditor.Editor
    {
        bool shapeOpen = true, surfaceOpen = true, detailOpen, meshOpen, pointsOpen = true;
        int selectedPoint;
        bool editPath = true, ownsToolVisibility, previousToolsHidden;
        string localError, previewError;
        LTRoadMath.Snapshot previewSnapshot;
        Vector3[] previewCenter, previewLeft, previewRight;
        LTRoad Road => (LTRoad)target;
        void OnEnable()
        { Selection.selectionChanged += OnSelectionChanged; if (target) LTRoadBakeQueue.Queue(Road); }
        void OnDisable() { Selection.selectionChanged -= OnSelectionChanged; RestoreTools(); }
        void OnSelectionChanged() { RestoreTools(); }
        void RestoreTools()
        {
            if (!ownsToolVisibility) return;
            Tools.hidden = previousToolsHidden; ownsToolVisibility = false;
        }
        GUIContent Label(string text, string tip) => new GUIContent(text, tip);
        void Field(string name, string label, string tip)
        {
            var property = serializedObject.FindProperty(name);
            if (property != null) EditorGUILayout.PropertyField(property, Label(label, tip), true);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                DrawPathToolbar();
                EditorGUI.BeginChangeCheck();
                Field("mode", "Тип дороги", "Offroad изменяет terrain. Asphalt дополнительно создаёт отдельную ленту с коллайдером.");
                shapeOpen = EditorGUILayout.Foldout(shapeOpen, "Форма и рельеф", true);
                if (shapeOpen)
                {
                    Field("width", "Ширина, м", "Полная ширина проезжей части.");
                    Field("shoulderWidth", "Обочина, м", "Ширина обочины с каждой стороны дороги.");
                    Field("blendWidth", "Плавный переход, м", "Ширина перехода от обочины к исходному рельефу.");
                    if(serializedObject.FindProperty("mode").enumValueIndex==(int)LTRoadMode.Offroad)
                        Field("flatten", "Выравнивание", "Сила изменения рельефа вдоль высоты и крена сплайна, от 0 до 1.");
                    Field("sampleSpacing", "Шаг вдоль пути, м", "Меньший шаг точнее описывает повороты, но увеличивает число образцов.");
                    Field("terrainCellSize", "Размер ячейки terrain, м", "Желаемая плотность terrain в области дороги.");
                }
                var mode = (LTRoadMode)serializedObject.FindProperty("mode").enumValueIndex;
                surfaceOpen = EditorGUILayout.Foldout(surfaceOpen, "Покрытие", true);
                if (surfaceOpen)
                {
                    Field("groundLayer", "Слой terrain", "Исходный слой покрытия. Редактор дороги не изменяет его текстуры или материал.");
                    if (mode == LTRoadMode.Offroad)
                    {
                        Field("pattern", "Рисунок покрытия", "Solid — сплошная полоса; Tracks — две колеи.");
                        Field("projection", "Проекция текстуры", "World — мировая проекция; Spline — текстура следует вдоль дороги на поверхности terrain.");
                        if (serializedObject.FindProperty("projection").enumValueIndex == (int)LTRoadProjection.Spline)
                        {
                            EditorGUILayout.HelpBox("Ограничение первой версии Spline: в каждом чанке слой LTSurfaceLayer должен принадлежать только одной дороге. Нельзя использовать baseLayer, слой обычной кисти или другой дороги в том же чанке. Создайте отдельную копию ассета LTSurfaceLayer; ссылки на те же текстуры допустимы. При конфликте проверка потребует отдельный ассет.", MessageType.Warning);
                            EditorGUILayout.HelpBox("Карта проекции 257 × 257 Float расходует примерно 1 МиБ GPU и 1 МиБ CPU на каждый слой дороги в каждом затронутом чанке. Расход памяти растёт с числом слоёв и чанков.", MessageType.Info);
                            Field("textureRepeatMetres", "Повтор вдоль пути, м", "Расстояние вдоль сплайна на один повтор текстуры.");
                            Field("textureOffset", "Смещение текстуры", "Смещение UV текстуры вдоль и поперёк дороги.");
                        }
                        if(serializedObject.FindProperty("pattern").enumValueIndex==(int)LTRoadPattern.Tracks)
                        {
                            Field("rutWidth", "Ширина колеи, м", "Ширина каждой полосы колеи.");
                            Field("rutSeparation", "Между колеями, м", "Расстояние между центрами колей.");
                            Field("rutDepth", "Глубина колеи, м", "Углубление колеи относительно поверхности дороги.");
                            Field("edgeNoise", "Неровность края", "Сила нерегулярности края колей.");
                            Field("noiseSize", "Размер шума, м", "Масштаб неровностей края.");
                            Field("seed", "Сид шума", "Фиксированный сид даёт воспроизводимый край.");
                        }
                    }
                    else
                    {
                        Field("asphaltMaterial", "Материал асфальта", "Назначается рендереру без изменения исходного материала. При явном запекании можно создать собственный HDRP/Lit.");
                        Field("textureRepeatMetres", "Повтор вдоль пути, м", "UV: U от 0 до 1 поперёк; V — расстояние вдоль пути / повтор. Между чанками V непрерывен.");
                    }
                    EditorGUILayout.HelpBox("Компонент кисти покрытия управляется дорогой. Настраивайте покрытие здесь.", MessageType.Info);
                }
                detailOpen = EditorGUILayout.Foldout(detailOpen, "Растительность и камни", true);
                if (detailOpen)
                {
                    Field("clearVegetation", "Убирать растительность", "Удалять растительность в области дороги.");
                    Field("clearStones", "Убирать камни", "Удалять камни в области дороги.");
                    Field("vegetationFade", "Сила удаления растительности", "Доля удаляемой растительности внутри маски, от 0 до 1. Плавный край определяется маской дороги.");
                }
                if (mode == LTRoadMode.Asphalt)
                {
                    meshOpen = EditorGUILayout.Foldout(meshOpen, "Меш асфальта", true);
                    if (meshOpen)
                    {
                        Field("meshChunkLength", "Длина чанка, м", "Приблизительная длина меша; границы проходят по образцам сплайна.");
                        Field("surfaceOffset", "Над поверхностью, м", "Лента располагается на SurfaceHeight плюс это смещение, с учётом крена.");
                        EditorGUILayout.HelpBox("Меши создаются только в редакторе. Изменённые чанки сохраняются отдельными ассетами; старые версии сохраняются для Undo. Перекрёстки и мосты не поддерживаются.", MessageType.None);
                    }
                }
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties(); localError = null;
                    LTRoadBakeQueue.Queue(Road); SceneView.RepaintAll();
                }
                pointsOpen = EditorGUILayout.Foldout(pointsOpen, "Точки пути и крен", true);
                if (pointsOpen) DrawPoints();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(Label("Прижать к terrain", "Проекция всех точек на исходный terrain со штампами, исключая все дороги.")))
                        TryAction(ProjectPoints);
                    if (GUILayout.Button(Label("Перестроить / запечь", "Обновить дорогу сейчас и сохранить её меши. Без материала создаётся собственный HDRP/Lit.")))
                        LTRoadBakeQueue.BakeNow(Road, true);
                }
            }
            string error = localError ?? LTRoadBakeQueue.Error(Road) ?? previewError;
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (!Road.World) EditorGUILayout.HelpBox("Поместите дорогу внутрь нужного LTWorld.", MessageType.Warning);
            if (Road.generatedRoot)
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField("Созданные меши", Road.generatedRoot, typeof(Transform), true);
        }

        void DrawPathToolbar()
        {
            EditorGUILayout.LabelField("Сплайн дороги", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Показать сплайн")) FramePath(false);
                using (new EditorGUI.DisabledScope(Road.points == null || Road.points.Count == 0))
                    if (GUILayout.Button("Показать выбранную точку")) FramePath(true);
            }
            bool editing = GUILayout.Toggle(editPath, "Редактировать точки в Scene", "Button");
            if (editing != editPath)
            { editPath = editing; if (!editPath) RestoreTools(); SceneView.RepaintAll(); }
            EditorGUILayout.HelpBox("Перетаскивайте пронумерованные точки. Shift + клик по земле — добавить точку в конец. Alt — навигация. Чтобы двигать весь объект Road, выключите редактирование точек.", MessageType.Info);
            if (GUILayout.Button(Label("Перенести путь к объекту Road…", "Перенести начало пути к объекту и прижать точки к terrain. Форма в плане и крен сохраняются. Можно отменить через Ctrl+Z."))
                && EditorUtility.DisplayDialog("Перенести путь?", "Начало пути будет перенесено к объекту Road, высоты точек будут подогнаны к terrain. Текущее расположение дороги изменится. Действие можно отменить через Ctrl+Z.", "Перенести", "Отмена"))
                TryAction(() => {
                    var points = serializedObject.FindProperty("points");
                    if (points.arraySize == 0) throw new InvalidOperationException("Сначала добавьте точку пути.");
                    ProjectPoints(-points.GetArrayElementAtIndex(0).FindPropertyRelative("position").vector3Value);
                    selectedPoint = 0; FramePath(false);
                });
        }

        public bool HasFrameBounds() => Road && Road.points != null && Road.points.Count > 0;
        public Bounds OnGetFrameBounds() => PathBounds(Road, -1);
        internal static Bounds PathBounds(LTRoad road, int pointIndex)
        {
            var bounds = new Bounds(road.transform.position, Vector3.zero);
            if (road.points != null && road.points.Count > 0)
            {
                int index = Mathf.Clamp(pointIndex, 0, road.points.Count - 1);
                bounds = new Bounds(road.transform.TransformPoint(road.points[index].position), Vector3.zero);
                if (pointIndex < 0)
                    foreach (var point in road.points) bounds.Encapsulate(road.transform.TransformPoint(point.position));
            }
            bounds.Expand(Mathf.Max(8, road.width + 2 * road.shoulderWidth));
            return bounds;
        }
        void FramePath(bool selected)
        {
            var view = SceneView.lastActiveSceneView;
            if (!view) view = EditorWindow.GetWindow<SceneView>();
            view.drawGizmos = true;
            view.Frame(PathBounds(Road, selected ? selectedPoint : -1), false);
            view.Repaint();
        }

        void DrawPoints()
        {
            var points = serializedObject.FindProperty("points");
            if (points.arraySize > LTRoadMesh.MaxPoints)
                EditorGUILayout.HelpBox($"Лимит точек: {LTRoadMesh.MaxPoints}. Удалите лишние точки перед запеканием.", MessageType.Error);
            // Keep an oversized externally supplied array editable without drawing thousands of controls.
            int start = Mathf.Clamp(selectedPoint / 32 * 32, 0, Mathf.Max(0, points.arraySize - 1));
            if (points.arraySize > 32)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("◀")) selectedPoint = Mathf.Max(0, start - 32);
                    EditorGUILayout.LabelField($"{start + 1}–{Math.Min(start + 32, points.arraySize)} / {points.arraySize}");
                    if (GUILayout.Button("▶")) selectedPoint = Mathf.Min(points.arraySize - 1, start + 32);
                }
            }
            for (int i = start; i < Math.Min(start + 32, points.arraySize); i++)
            {
                var point = points.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Toggle(selectedPoint == i, $"Точка {i + 1}", "Button") && selectedPoint != i)
                        { selectedPoint = i; SceneView.RepaintAll(); }
                        using (new EditorGUI.DisabledScope(points.arraySize >= LTRoadMesh.MaxPoints))
                            if (GUILayout.Button("Вставить после")) { InsertPoint(i + 1); return; }
                        if (GUILayout.Button("Удалить"))
                        {
                            points.DeleteArrayElementAtIndex(i); serializedObject.ApplyModifiedProperties();
                            selectedPoint = Mathf.Clamp(i, 0, Mathf.Max(0, points.arraySize - 1));
                            LTRoadBakeQueue.Queue(Road); SceneView.RepaintAll(); return;
                        }
                    }
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(point.FindPropertyRelative("position"), Label("Позиция", "Координаты относительно объекта дороги."));
                    EditorGUILayout.PropertyField(point.FindPropertyRelative("bank"), Label("Крен, °", "Поперечный наклон дороги в этой точке; интерполируется вдоль пути."));
                    if (EditorGUI.EndChangeCheck())
                    { serializedObject.ApplyModifiedProperties(); localError = null; LTRoadBakeQueue.Queue(Road); SceneView.RepaintAll(); }
                }
            }
            using (new EditorGUI.DisabledScope(points.arraySize >= LTRoadMesh.MaxPoints))
                if (GUILayout.Button("Добавить точку в конец")) { InsertPoint(points.arraySize); FramePath(true); }
        }

        void InsertPoint(int index, Vector3? placedPosition = null)
        {
            var points = serializedObject.FindProperty("points");
            if (points.arraySize >= LTRoadMesh.MaxPoints) return;
            Vector3 p = Vector3.zero; float bank = 0;
            if (points.arraySize > 0)
            {
                var before = points.GetArrayElementAtIndex(Math.Max(0, index - 1));
                p = before.FindPropertyRelative("position").vector3Value;
                bank = before.FindPropertyRelative("bank").floatValue;
                if (index < points.arraySize)
                {
                    var after = points.GetArrayElementAtIndex(index);
                    p = (p + after.FindPropertyRelative("position").vector3Value) * .5f;
                    bank = (bank + after.FindPropertyRelative("bank").floatValue) * .5f;
                }
                else if (points.arraySize > 1)
                {
                    Vector3 delta = p - points.GetArrayElementAtIndex(points.arraySize - 2).FindPropertyRelative("position").vector3Value;
                    p += delta.sqrMagnitude > .0001f ? delta.normalized * Mathf.Max(2, Road.sampleSpacing * 2) : Vector3.forward * 5;
                }
                else p += Vector3.forward * 5;
            }
            points.InsertArrayElementAtIndex(index);
            var added = points.GetArrayElementAtIndex(index);
            added.FindPropertyRelative("position").vector3Value = placedPosition ?? p;
            added.FindPropertyRelative("bank").floatValue = bank;
            serializedObject.ApplyModifiedProperties(); selectedPoint = index;
            LTRoadBakeQueue.Queue(Road); SceneView.RepaintAll();
        }

        void TryAction(Action action)
        {
            try { action(); localError = null; }
            catch (Exception exception) { localError = exception.Message; }
        }
        void ProjectPoints() => ProjectPoints(Vector3.zero);
        void ProjectPoints(Vector3 localOffset)
        {
            var world = Road.World;
            if (!world || !world.source) throw new InvalidOperationException("Для проекции нужен родительский LTWorld с исходным terrain.");
            var points = serializedObject.FindProperty("points");
            if (points.arraySize > LTRoadMesh.MaxPoints) throw new InvalidOperationException("Превышен лимит точек.");
            var projected = new Vector3[points.arraySize];
            var sampleHeight = LTEditorEngine.CreateHeightSamplerForRoad(world);
            // Evaluate everything before committing, so an error cannot partially project a path.
            for (int i = 0; i < projected.Length; i++)
            {
                Vector3 p = world.transform.InverseTransformPoint(Road.transform.TransformPoint(points.GetArrayElementAtIndex(i).FindPropertyRelative("position").vector3Value + localOffset));
                if (p.x < 0 || p.z < 0 || p.x > world.source.size.x || p.z > world.source.size.z)
                    throw new InvalidOperationException($"Точка {i + 1} находится вне исходного terrain.");
                p.y = sampleHeight(p.x, p.z);
                if (!LTRoadMesh.Finite(p)) throw new InvalidOperationException($"Некорректная высота у точки {i + 1}.");
                projected[i] = Road.transform.InverseTransformPoint(world.transform.TransformPoint(p));
            }
            for (int i = 0; i < projected.Length; i++) points.GetArrayElementAtIndex(i).FindPropertyRelative("position").vector3Value = projected[i];
            serializedObject.ApplyModifiedProperties(); LTRoadBakeQueue.Queue(Road); SceneView.RepaintAll();
        }

        void OnSceneGUI()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !editPath || Selection.activeGameObject != Road.gameObject)
            { RestoreTools(); return; }
            if (!ownsToolVisibility)
            { previousToolsHidden = Tools.hidden; ownsToolVisibility = true; }
            Tools.hidden = true;
            serializedObject.Update();
            PlacePointOnTerrain();
            var points = serializedObject.FindProperty("points");
            int count = points.arraySize;
            if (count == 0 || count > LTRoadMesh.MaxPoints) return;
            selectedPoint = Mathf.Clamp(selectedPoint, 0, count - 1);
            var positions = new Vector3[count];
            for (int i = 0; i < count; i++) positions[i] = Road.transform.TransformPoint(points.GetArrayElementAtIndex(i).FindPropertyRelative("position").vector3Value);
            var previousDepth = Handles.zTest;
            try
            {
                Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
                using (new Handles.DrawingScope(new Color(1, .65f, .12f)))
                {
                    DrawCurve();
                    for (int i = 0; i < count; i++)
                    {
                        float size = HandleUtility.GetHandleSize(positions[i]) * .065f;
                        using (new Handles.DrawingScope(i == selectedPoint ? Color.green : Handles.color))
                            if (Handles.Button(positions[i], Quaternion.identity, size, size, Handles.SphereHandleCap))
                            { selectedPoint = i; Repaint(); }
                        Handles.Label(positions[i] + Vector3.up * size * 2, (i + 1).ToString());
                    }
                    EditorGUI.BeginChangeCheck();
                    Vector3 moved = Handles.PositionHandle(positions[selectedPoint], Tools.pivotRotation == PivotRotation.Local ? Road.transform.rotation : Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        points.GetArrayElementAtIndex(selectedPoint).FindPropertyRelative("position").vector3Value = Road.transform.InverseTransformPoint(moved);
                        serializedObject.ApplyModifiedProperties(); localError = null; LTRoadBakeQueue.Queue(Road); Repaint();
                    }
                }
            }
            finally { Handles.zTest = previousDepth; }
        }

        void PlacePointOnTerrain()
        {
            var e = Event.current;
            if (!e.shift || e.alt || e.control || e.command) return;
            int control = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(control);
            if (e.type != EventType.MouseDown || e.button != 0 || GUIUtility.hotControl != 0) return;
            // Only this world's generated terrain colliders; never place on vegetation or another world.
            var hits = Physics.RaycastAll(HandleUtility.GUIPointToWorldRay(e.mousePosition), Mathf.Infinity, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            bool found = false;
            foreach (var hit in hits)
            {
                if (!hit.collider.GetComponent<LTChunk>() || hit.collider.GetComponentInParent<LTWorld>() != Road.World) continue;
                TryAction(() => {
                    if (Road.points.Count >= LTRoadMesh.MaxPoints) throw new InvalidOperationException("Достигнут лимит точек дороги.");
                    InsertPoint(Road.points.Count, Road.transform.InverseTransformPoint(hit.point));
                });
                found = true; break;
            }
            if (!found) localError = "Кликните по поверхности этого LTWorld. Для размещения нужен коллайдер terrain.";
            e.Use(); Repaint(); SceneView.RepaintAll();
        }

        void DrawCurve()
        {
            if (Event.current.type != EventType.Repaint || !Road.World) return;
            try
            {
                var snapshot = Road.Capture(Road.World);
                if (!ReferenceEquals(snapshot, previewSnapshot))
                {
                    int count = snapshot.samples.Count;
                    if (count > LTRoadMesh.MaxSamples) throw new InvalidOperationException("Превышен лимит образцов для предпросмотра.");
                    previewCenter = new Vector3[count]; previewLeft = new Vector3[count]; previewRight = new Vector3[count];
                    for (int i = 0; i < count; i++)
                    {
                        var sample = snapshot.samples[i]; var hit = new LTRoadMath.Hit { position = sample.position, bank = sample.bank };
                        float half = snapshot.width * .5f;
                        Vector3 left = sample.position - sample.right * half, right = sample.position + sample.right * half;
                        left.y = snapshot.SurfaceHeight(hit, -half); right.y = snapshot.SurfaceHeight(hit, half);
                        previewCenter[i] = Road.World.transform.TransformPoint(sample.position);
                        previewLeft[i] = Road.World.transform.TransformPoint(left); previewRight[i] = Road.World.transform.TransformPoint(right);
                    }
                    previewSnapshot = snapshot;
                }
                Handles.DrawAAPolyLine(3, previewCenter);
                using (new Handles.DrawingScope(new Color(1, .85f, .4f, .7f)))
                { Handles.DrawAAPolyLine(2, previewLeft); Handles.DrawAAPolyLine(2, previewRight); }
                previewError = null;
            }
            catch (Exception exception) { previewError = exception.Message; }
        }

        [MenuItem("GameObject/Local Terrain/Road (Offroad)", false, 15)]
        static void CreateOffroad(MenuCommand command) => Create(command, LTRoadMode.Offroad);
        [MenuItem("GameObject/Local Terrain/Road (Asphalt)", false, 16)]
        static void CreateAsphalt(MenuCommand command) => Create(command, LTRoadMode.Asphalt);
        static void Create(MenuCommand command, LTRoadMode mode)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var selected = Selection.gameObjects;
            GameObject context = command.context as GameObject;
            var candidates = (context ? new[] { context } : selected).Select(go => go.GetComponentInParent<LTWorld>()).Distinct().ToArray();
            if (candidates.Length != 1 || !candidates[0] || !candidates[0].source)
            { EditorUtility.DisplayDialog("Создание дороги", "Выберите один LTWorld с исходным terrain или его дочерний объект.", "OK"); return; }
            var world = candidates[0];
            if (EditorUtility.IsPersistent(world) || PrefabStageUtility.GetPrefabStage(world.gameObject) != null)
            { EditorUtility.DisplayDialog("Создание дороги", "Создавайте дорогу внутри LTWorld в обычной сцене.", "OK"); return; }
            try
            {
                var size = world.source.size;
                if (!LTRoadMesh.Finite(size) || size.x <= .1f || size.z <= .1f) throw new InvalidOperationException("Некорректный размер исходного terrain.");
                var view = SceneView.lastActiveSceneView;
                Vector3 center = view ? world.transform.InverseTransformPoint(view.pivot) : new Vector3(size.x * .5f, 0, size.z * .5f);
                float half = Mathf.Min(10, size.z * .2f);
                float margin = Mathf.Min(5, size.x * .2f);
                center.x = Mathf.Clamp(center.x, margin, size.x - margin); center.z = Mathf.Clamp(center.z, half, size.z - half);
                var path = new[] { center - Vector3.forward * half, center, center + Vector3.forward * half };
                var sampleHeight = LTEditorEngine.CreateHeightSamplerForRoad(world);
                for (int i = 0; i < path.Length; i++)
                { path[i].y = sampleHeight(path[i].x, path[i].z); if (!LTRoadMesh.Finite(path[i])) throw new InvalidOperationException("Не удалось определить высоту terrain."); }
                Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Создать дорогу");
                var go = new GameObject(mode == LTRoadMode.Offroad ? "Road (Offroad)" : "Road (Asphalt)");
                Undo.RegisterCreatedObjectUndo(go, "Создать дорогу"); Undo.SetTransformParent(go.transform, world.transform, "Создать дорогу");
                go.transform.localPosition = path[1]; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;
                var road = Undo.AddComponent<LTRoad>(go); road.mode = mode;
                var data = new SerializedObject(road); var points = data.FindProperty("points"); points.arraySize = path.Length;
                for (int i = 0; i < path.Length; i++)
                { var point = points.GetArrayElementAtIndex(i); point.FindPropertyRelative("position").vector3Value = path[i] - path[1]; point.FindPropertyRelative("bank").floatValue = 0; }
                data.ApplyModifiedProperties(); Selection.activeGameObject = go;
                LTRoadBakeQueue.BakeNow(road, true); Undo.CollapseUndoOperations(group);
                if (view) { view.drawGizmos = true; view.Frame(PathBounds(road, -1), false); }
            }
            catch (Exception exception) { EditorUtility.DisplayDialog("Создание дороги", exception.Message, "OK"); }
        }
    }

    // Detection is editor-only and cheap. Capture, mesh preparation and asset work run once
    // after the edit debounce, never while a Scene/Inspector control is being dragged.
    [InitializeOnLoad]
    public static class LTRoadBakeQueue
    {
        sealed class State { public string input, error; public bool pending; public double due; }
        static readonly Dictionary<LTRoad, State> states = new Dictionary<LTRoad, State>();
        static double nextPoll;
        static LTRoadBakeQueue()
        {
            EditorApplication.update += Tick;
            Undo.undoRedoPerformed += () => { foreach (var pair in states) if (pair.Key) Queue(pair.Key); SceneView.RepaintAll(); };
            EditorSceneManager.sceneSaving += (scene, path) => {
                foreach (var road in Roads())
                    if (road.gameObject.scene == scene && NeedsBake(road)) BakeNow(road, false);
            };
            EditorApplication.playModeStateChanged += state => {
                if (state == PlayModeStateChange.ExitingEditMode)
                    foreach (var road in Roads()) if (NeedsBake(road)) BakeNow(road, false, true);
                if (state == PlayModeStateChange.EnteredEditMode) states.Clear();
            };
        }
        static LTRoad[] Roads() => UnityEngine.Object.FindObjectsByType<LTRoad>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(Editable).ToArray();
        static bool Editable(LTRoad road) => road && road.gameObject.scene.IsValid() && road.gameObject.scene.isLoaded
            && !EditorUtility.IsPersistent(road) && PrefabStageUtility.GetPrefabStage(road.gameObject) == null;
        static string Input(LTRoad road) => EditorJsonUtility.ToJson(road) + "|" + road.transform.localToWorldMatrix.ToString("R")
            + "|" + (road.World ? road.World.transform.localToWorldMatrix.ToString("R") : "no-world");
        static State Get(LTRoad road)
        { if (!states.TryGetValue(road, out var state)) states.Add(road, state = new State { input = Input(road) }); return state; }
        static bool NeedsBake(LTRoad road)
        { var state = Get(road); return state.pending || state.input != Input(road); }
        public static string Error(LTRoad road) => road && states.TryGetValue(road, out var state) ? state.error : null;
        public static void Queue(LTRoad road)
        {
            if (!Editable(road)) return;
            var state = Get(road); state.input = Input(road); state.pending = true; state.error = null; state.due = EditorApplication.timeSinceStartup + .4;
        }
        static void Tick()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating
                || GUIUtility.hotControl != 0 || EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + .25;
            foreach (var dead in states.Keys.Where(road => !road).ToArray()) states.Remove(dead);
            foreach (var road in Roads())
            {
                var state = Get(road); string input = Input(road);
                if (input != state.input) Queue(road);
                if (state.pending && EditorApplication.timeSinceStartup >= state.due) BakeNow(road, false);
            }
        }
        public static void BakeNow(LTRoad road, bool allowDefaultMaterial, bool enteringPlay = false)
        {
            if (!Editable(road) || Application.isPlaying || (!enteringPlay && EditorApplication.isPlayingOrWillChangePlaymode)) return;
            var state = Get(road);
            int undoGroup = Undo.GetCurrentGroup();
            try
            {
                if (allowDefaultMaterial) Record(road, true);
                Bake(road, allowDefaultMaterial);
                // Explicit bake must also apply terrain with LTWorld.autoUpdate disabled.
                // Automatic UV/material edits only update this road's ribbon.
                // Updating one road must not synchronously save every dirty project asset.
                // Terrain meshes stay dirty for the user's normal Save; ribbon assets
                // are saved individually by the road baker.
                if (allowDefaultMaterial && road.World)
                {
                    LTEditorEngine.Refresh(road.World, false, false);
                    road.World.GetComponent<LTDetailRenderer>()?.RequestSurfaceRefresh();
                }
                state.error = null;
            }
            catch (Exception exception) { state.error = exception.Message; SetVisible(road, false); }
            finally
            {
                if (allowDefaultMaterial) Undo.CollapseUndoOperations(undoGroup);
                state.input = Input(road); state.pending = false; SceneView.RepaintAll();
            }
        }
        static bool Owned(Transform root, LTRoad road)
        { var marker = root ? root.GetComponent<LTRoadGenerated>() : null; return marker && marker.owner == road && root.IsChildOf(road.transform); }
        static void Record(UnityEngine.Object value, bool undo)
        { if (undo && value) Undo.RecordObject(value, "Запечь дорогу"); }
        static void SetVisible(LTRoad road, bool visible, bool undo = false)
        {
            if (Owned(road.generatedRoot, road) && road.generatedRoot.gameObject.activeSelf != visible)
            { Record(road.generatedRoot.gameObject, undo); road.generatedRoot.gameObject.SetActive(visible); EditorUtility.SetDirty(road.generatedRoot.gameObject); }
        }
        static void Bake(LTRoad road, bool allowDefaultMaterial)
        {
            if (!road.isActiveAndEnabled) { SetVisible(road, false, allowDefaultMaterial); return; }
            var world = road.World;
            if (!world || !world.source) throw new InvalidOperationException("Для запекания нужен родительский LTWorld с исходным terrain.");
            if (road.points == null || road.points.Count < 2 || road.points.Count > LTRoadMesh.MaxPoints)
                throw new InvalidOperationException($"Для дороги требуется от 2 до {LTRoadMesh.MaxPoints} точек.");
            var snapshot = road.Capture(world);
            if (road.mode != LTRoadMode.Asphalt) { SetVisible(road, false, allowDefaultMaterial); return; }
            if (road.generatedRoot && !Owned(road.generatedRoot, road))
                throw new InvalidOperationException("Ссылка generatedRoot не принадлежит этой дороге. Чужой объект не изменён.");
            if (!road.asphaltMaterial && !allowDefaultMaterial)
                throw new InvalidOperationException("Назначьте материал асфальта или нажмите «Перестроить / запечь» для создания собственного HDRP/Lit.");
            var chunks = LTRoadMesh.Build(snapshot, road.meshChunkLength, road.textureRepeatMetres, road.surfaceOffset,
                road.transform.worldToLocalMatrix * world.transform.localToWorldMatrix);
            // Fully validate and prepare the geometry before creating any assets or scene objects.
            string folder = EnsureFolder(road);
            if (!road.asphaltMaterial)
            {
                var shader = Shader.Find("HDRP/Lit");
                if (!shader) throw new InvalidOperationException("HDRP/Lit недоступен. Назначьте подходящий материал вручную.");
                var material = new Material(shader) { name = "Road Asphalt" };
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", new Color(.08f, .08f, .08f, 1));
                if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", .25f);
                AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath(folder + "/Asphalt.mat"));
                road.asphaltMaterial = material; AssetDatabase.SaveAssetIfDirty(material);
            }
            if (!road.generatedRoot)
            {
                // Undo may have restored the serialized reference before generated children existed.
                foreach (Transform child in road.transform)
                    if (Owned(child, road) && child.name == "Road Generated") { road.generatedRoot = child; break; }
                if (!road.generatedRoot) road.generatedRoot = NewOwned("Road Generated", road.transform, road, allowDefaultMaterial);
            }
            var root = road.generatedRoot;
            Record(root, allowDefaultMaterial);
            root.localPosition = Vector3.zero; root.localRotation = Quaternion.identity; root.localScale = Vector3.one;
            var used = new HashSet<Transform>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var data = chunks[i]; string name = $"Segment_{i:D4}"; Transform segment = null;
                foreach (Transform child in root) if (child.name == name && Owned(child, road)) { segment = child; break; }
                if (!segment) segment = NewOwned(name, root, road, allowDefaultMaterial);
                Record(segment, allowDefaultMaterial); Record(segment.gameObject, allowDefaultMaterial);
                used.Add(segment); segment.localPosition = Vector3.zero; segment.localRotation = Quaternion.identity; segment.localScale = Vector3.one;
                var filter = Component<MeshFilter>(segment, allowDefaultMaterial);
                var renderer = Component<MeshRenderer>(segment, allowDefaultMaterial);
                var collider = Component<MeshCollider>(segment, allowDefaultMaterial);
                Record(filter, allowDefaultMaterial); Record(renderer, allowDefaultMaterial); Record(collider, allowDefaultMaterial);
                string meshName = "RoadChunk_" + data.hash;
                Mesh mesh = filter.sharedMesh;
                if (!mesh || mesh.name != meshName || !AssetDatabase.GetAssetPath(mesh).StartsWith(folder + "/", StringComparison.Ordinal))
                {
                    string path = folder + "/" + meshName + ".asset";
                    mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                    if (!mesh || mesh.name != meshName)
                    {
                        mesh = new Mesh { name = meshName, indexFormat = data.vertices.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                        mesh.vertices = data.vertices; mesh.normals = data.normals; mesh.tangents = data.tangents;
                        mesh.uv = data.uv; mesh.triangles = data.triangles; mesh.RecalculateBounds();
                        AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath(path)); AssetDatabase.SaveAssetIfDirty(mesh);
                    }
                    filter.sharedMesh = mesh;
                }
                if (collider.sharedMesh != mesh) { collider.sharedMesh = null; collider.sharedMesh = mesh; }
                if (renderer.sharedMaterial != road.asphaltMaterial) renderer.sharedMaterial = road.asphaltMaterial;
                renderer.enabled = true; collider.enabled = true; segment.gameObject.SetActive(true);
                EditorUtility.SetDirty(filter); EditorUtility.SetDirty(renderer); EditorUtility.SetDirty(collider);
                PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
            }
            // Retain obsolete owned segments for Undo/reuse. Never delete user children or asset files.
            foreach (Transform child in root)
                if (Owned(child, road) && !used.Contains(child) && child.gameObject.activeSelf)
                { Record(child.gameObject, allowDefaultMaterial); child.gameObject.SetActive(false); }
            road.bakedGeometryHash = snapshot.geometryHash; SetVisible(road, true, allowDefaultMaterial);
            EditorUtility.SetDirty(road); PrefabUtility.RecordPrefabInstancePropertyModifications(road);
            EditorSceneManager.MarkSceneDirty(road.gameObject.scene);
        }
        static T Component<T>(Transform transform, bool undo) where T : UnityEngine.Component
        {
            var component = transform.GetComponent<T>();
            return component ? component : undo ? Undo.AddComponent<T>(transform.gameObject) : transform.gameObject.AddComponent<T>();
        }
        static Transform NewOwned(string name, Transform parent, LTRoad road, bool undo)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var marker = go.AddComponent<LTRoadGenerated>(); marker.owner = road; marker.bakeId = road.bakeId;
            if (undo) Undo.RegisterCreatedObjectUndo(go, "Запечь дорогу");
            return go.transform;
        }
        static string EnsureFolder(LTRoad road)
        {
            const string parent = "Assets/LocalTerrainRoads";
            if (!AssetDatabase.IsValidFolder(parent) && string.IsNullOrEmpty(AssetDatabase.CreateFolder("Assets", "LocalTerrainRoads")))
                throw new InvalidOperationException("Не удалось создать Assets/LocalTerrainRoads. Проверьте доступ к папке.");
            bool valid = Guid.TryParseExact(road.bakeId, "N", out _);
            bool duplicate = valid && Roads().Any(other => other != road && other.bakeId == road.bakeId);
            if (!valid || duplicate)
            { road.bakeId = Guid.NewGuid().ToString("N"); EditorUtility.SetDirty(road); }
            string folder = parent + "/" + road.bakeId;
            if (!AssetDatabase.IsValidFolder(folder) && string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, road.bakeId)))
                throw new InvalidOperationException("Не удалось создать собственную папку ассетов дороги.");
            return folder;
        }
    }
}
