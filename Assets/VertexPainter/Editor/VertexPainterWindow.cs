using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Project213.VertexPainting
{
    public sealed class VertexPainterWindow : EditorWindow
    {
        [SerializeField] MeshFilter target;
        [SerializeField] Mesh editableMesh;
        [SerializeField] Color paintColor = Color.red;
        [SerializeField] float radius = 0.5f, strength = 0.5f, softness = 0.7f;
        [SerializeField] bool red = true, green = true, blue = true, alpha;
        [SerializeField] bool frontOnly = true;
        [SerializeField] int preview = 1;
        bool painting, stroke;
        int undoGroup = -1, control;
        Mesh cachedMesh;
        Vector3[] vertices, normals;
        int[] triangles;
        Color[] colors, strokeColors;
        float[] strokeWeights;
        Vector3 previousHit;
        bool hasPreviousHit, eraseStroke;
        Material previewMaterial;
        string error;
        Vector2 scroll;

        [MenuItem("Tools/Vertex Painter")]
        static void Open() => GetWindow<VertexPainterWindow>("Vertex Painter");

        void OnEnable()
        {
            SceneView.duringSceneGui += DuringSceneGUI;
            Undo.undoRedoPerformed += OnUndo;
            Selection.selectionChanged += Repaint;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        void OnDisable()
        {
            EndStroke();
            SceneView.duringSceneGui -= DuringSceneGUI;
            Undo.undoRedoPerformed -= OnUndo;
            Selection.selectionChanged -= Repaint;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            if (previewMaterial) DestroyImmediate(previewMaterial);
            SceneView.RepaintAll();
        }

        void OnPlayMode(PlayModeStateChange state) { EndStroke(); painting = false; }
        void OnUndo() { EndStroke(); cachedMesh = null; Repaint(); SceneView.RepaintAll(); }

        void SetTarget(MeshFilter value)
        {
            if (target == value) return;
            EndStroke(); painting = false; target = value; editableMesh = null;
            cachedMesh = null; error = null; SceneView.RepaintAll();
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Vertex Painter", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("MeshFilter: выберите объект модели. Создайте копию меша, затем включите кисть. Исходный FBX не изменяется.", MessageType.Info);
            using (new EditorGUI.DisabledScope(stroke))
            {
                SetTarget((MeshFilter)EditorGUILayout.ObjectField("Объект", target, typeof(MeshFilter), true));
                if (GUILayout.Button("Взять выбранный объект"))
                    SetTarget(Selection.activeGameObject ? Selection.activeGameObject.GetComponent<MeshFilter>() : null);
            }
            bool valid = target && target.sharedMesh && target.GetComponent<MeshRenderer>() &&
                         !EditorUtility.IsPersistent(target) && !EditorApplication.isPlayingOrWillChangePlaymode;
            if (!valid)
                EditorGUILayout.HelpBox("Нужен объект сцены с MeshFilter и MeshRenderer, вне Play Mode. Для модели из нескольких частей выберите нужный дочерний объект.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!valid || stroke))
                if (GUILayout.Button("Создать отдельную копию меша…")) PrepareCopy();
            bool ready = valid && editableMesh && target.sharedMesh == editableMesh;
            if (ready)
            {
                EditorGUILayout.ObjectField("Меш для покраски", editableMesh, typeof(Mesh), false);
                EditorGUILayout.LabelField("Вершин", editableMesh.vertexCount.ToString("N0"));
            }
            paintColor = EditorGUILayout.ColorField("Цвет", paintColor);
            radius = Mathf.Max(0.001f, EditorGUILayout.FloatField("Радиус (м)", radius));
            strength = EditorGUILayout.Slider("Сила", strength, 0f, 1f);
            softness = EditorGUILayout.Slider("Мягкость края", softness, 0f, 1f);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Каналы записи");
            red = GUILayout.Toggle(red, "R"); green = GUILayout.Toggle(green, "G");
            blue = GUILayout.Toggle(blue, "B"); alpha = GUILayout.Toggle(alpha, "A");
            EditorGUILayout.EndHorizontal();
            frontOnly = EditorGUILayout.Toggle("Только лицевая сторона", frontOnly);
            preview = EditorGUILayout.Popup("Предпросмотр", preview, new[] { "Материал", "RGB", "R", "G", "B", "A" });
            using (new EditorGUI.DisabledScope(!ready || !(red || green || blue || alpha)))
            {
                bool next = GUILayout.Toggle(painting, painting ? "Кисть включена" : "Включить кисть", "Button");
                if (next != painting) { EndStroke(); painting = next; SceneView.RepaintAll(); }
                using (new EditorGUI.DisabledScope(stroke))
                {
                    if (GUILayout.Button("Залить выбранные каналы цветом")) Fill(paintColor);
                    if (GUILayout.Button("Сбросить выбранные каналы в белый (1)")) Fill(Color.white);
                    if (GUILayout.Button("Сохранить меш")) AssetDatabase.SaveAssetIfDirty(editableMesh);
                }
            }
            if (!ready) painting = false;
            EditorGUILayout.HelpBox("ЛКМ — красить, Shift + ЛКМ — стирать в белый. [ / ] — радиус. Alt — навигация. Ctrl+Z — Undo. Один мазок применяет силу один раз; повторный мазок усиливает цвет. Заливка применяет цвет полностью.", MessageType.None);
            EditorGUILayout.HelpBox("Предпросмотр виден только в Scene View. Для цвета в игре материал должен читать Vertex Color. Кисть работает в объёме радиуса на выбранном меше; близкие поверхности могут попадать в один мазок. Другие объекты не блокируют кисть. Сцену сохраните самостоятельно.", MessageType.None);
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            EditorGUILayout.EndScrollView();
            if (GUI.changed) SceneView.RepaintAll();
        }

        bool ReadMesh()
        {
            if (!target || !target.sharedMesh) return false;
            Mesh mesh = target.sharedMesh;
            if (cachedMesh == mesh) return true;
            try
            {
                if (!mesh.isReadable) throw new InvalidOperationException("В Import Settings модели включите Read/Write и нажмите Apply.");
                vertices = mesh.vertices; normals = mesh.normals;
                var indices = new List<int>();
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    if (mesh.GetTopology(i) != MeshTopology.Triangles)
                        throw new InvalidOperationException("Поддерживаются только треугольные меши.");
                    indices.AddRange(mesh.GetTriangles(i));
                }
                triangles = indices.ToArray();
                colors = mesh.colors;
                if (colors.Length != vertices.Length)
                {
                    colors = new Color[vertices.Length];
                    for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
                }
                cachedMesh = mesh; error = null; return true;
            }
            catch (Exception ex) { error = ex.Message; painting = false; Repaint(); return false; }
        }

        void PrepareCopy()
        {
            if (!ReadMesh()) return;
            string path = EditorUtility.SaveFilePanelInProject("Копия меша для Vertex Color", target.sharedMesh.name + "_Painted", "asset", "Выберите место для нового меша");
            if (string.IsNullOrEmpty(path)) return;
            // Never replace an existing asset, even if the save dialog confirmed overwrite.
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            Mesh copy = Instantiate(target.sharedMesh);
            copy.name = System.IO.Path.GetFileNameWithoutExtension(path);
            copy.colors = colors;
            AssetDatabase.CreateAsset(copy, path);
            Undo.RecordObject(target, "Assign vertex paint mesh");
            target.sharedMesh = copy;
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            EditorUtility.SetDirty(target);
            editableMesh = copy; cachedMesh = null;
            AssetDatabase.SaveAssetIfDirty(copy);
            SceneView.RepaintAll();
        }

        bool CanEdit()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !target || !editableMesh || target.sharedMesh != editableMesh || !ReadMesh()) return false;
            // A duplicated scene object can start sharing the previously private copy.
            foreach (MeshFilter other in Resources.FindObjectsOfTypeAll<MeshFilter>())
                if (other != target && other.sharedMesh == editableMesh)
                { error = "Этот меш используется другим объектом. Создайте новую отдельную копию."; Repaint(); return false; }
            foreach (SkinnedMeshRenderer other in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>())
                if (other.sharedMesh == editableMesh)
                { error = "Этот меш используется SkinnedMeshRenderer. Создайте новую отдельную копию."; Repaint(); return false; }
            error = null;
            return true;
        }

        Color BlendChannels(Color from, Color to, float weight)
        {
            Color result = Color.Lerp(from, to, weight);
            if (!red) result.r = from.r;
            if (!green) result.g = from.g;
            if (!blue) result.b = from.b;
            if (!alpha) result.a = from.a;
            return result;
        }

        void Fill(Color color)
        {
            if (!CanEdit()) return;
            Undo.RegisterCompleteObjectUndo(editableMesh, "Fill vertex colors");
            for (int i = 0; i < colors.Length; i++) colors[i] = BlendChannels(colors[i], color, 1f);
            ApplyColors(); AssetDatabase.SaveAssetIfDirty(editableMesh);
        }

        void ApplyColors()
        {
            editableMesh.colors = colors;
            EditorUtility.SetDirty(editableMesh);
            SceneView.RepaintAll();
        }

        void EndStroke()
        {
            if (!stroke) return;
            stroke = false;
            if (GUIUtility.hotControl == control) GUIUtility.hotControl = 0;
            if (undoGroup >= 0) Undo.CollapseUndoOperations(undoGroup);
            undoGroup = -1;
            if (editableMesh) AssetDatabase.SaveAssetIfDirty(editableMesh);
            strokeColors = null; strokeWeights = null;
            hasPreviousHit = false;
        }

        void PaintSegment(Vector3 hit, Vector3 direction)
        {
            if (!hasPreviousHit) Stamp(hit, direction, eraseStroke);
            else
            {
                float distance = Vector3.Distance(previousHit, hit);
                // Do not bridge distant disconnected surfaces with paint.
                int steps = distance > radius * 8f ? 1 : Mathf.Clamp(Mathf.CeilToInt(distance / (radius * 0.15f)), 1, 64);
                for (int i = 1; i <= steps; i++)
                    Stamp(Vector3.Lerp(previousHit, hit, i / (float)steps), direction, eraseStroke);
            }
            previousHit = hit; hasPreviousHit = true;
        }

        void Stamp(Vector3 center, Vector3 direction, bool erase)
        {
            Matrix4x4 matrix = target.transform.localToWorldMatrix;
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            Color destination = erase ? Color.white : paintColor;
            for (int i = 0; i < vertices.Length; i++)
            {
                float distance = Vector3.Distance(matrix.MultiplyPoint3x4(vertices[i]), center) / radius;
                if (distance > 1f) continue;
                if (frontOnly && normals.Length == vertices.Length && Vector3.Dot(normalMatrix.MultiplyVector(normals[i]), direction) >= 0f) continue;
                float falloff = softness <= 0f ? 1f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - distance) / softness));
                float weight = strength * falloff;
                if (weight <= strokeWeights[i]) continue;
                strokeWeights[i] = weight;
                colors[i] = BlendChannels(strokeColors[i], destination, weight);
            }
            ApplyColors();
        }

        bool Pick(Ray worldRay, out Vector3 point, out Vector3 normal)
        {
            point = normal = default;
            Matrix4x4 inverse = target.transform.worldToLocalMatrix;
            Vector3 origin = inverse.MultiplyPoint3x4(worldRay.origin);
            Vector3 direction = inverse.MultiplyVector(worldRay.direction);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
                Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(direction, e2);
                float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-10f) continue;
                Vector3 s = origin - a;
                float u = Vector3.Dot(s, p) / det;
                if (u < 0f || u > 1f) continue;
                Vector3 q = Vector3.Cross(s, e1);
                float v = Vector3.Dot(direction, q) / det;
                if (v < 0f || u + v > 1f) continue;
                float t = Vector3.Dot(e2, q) / det;
                if (t < 0f || t >= nearest) continue;
                nearest = t;
                normal = inverse.transpose.MultiplyVector(Vector3.Cross(e1, e2)).normalized;
            }
            if (float.IsPositiveInfinity(nearest)) return false;
            point = worldRay.GetPoint(nearest);
            if (Vector3.Dot(normal, worldRay.direction) > 0f) normal = -normal;
            return true;
        }

        void DrawPreview()
        {
            if (preview == 0 || !editableMesh || target.sharedMesh != editableMesh) return;
            if (!previewMaterial)
            {
                Shader shader = Shader.Find("Hidden/Project213/VertexColorPreview");
                if (!shader) return;
                previewMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            previewMaterial.SetFloat("_Channel", preview - 2);
            if (!previewMaterial.SetPass(0)) return;
            for (int i = 0; i < editableMesh.subMeshCount; i++)
                Graphics.DrawMeshNow(editableMesh, target.transform.localToWorldMatrix, i);
        }

        void DuringSceneGUI(SceneView view)
        {
            if (!target || !target.sharedMesh || EditorApplication.isPlayingOrWillChangePlaymode)
            { EndStroke(); return; }
            Event e = Event.current;
            if (e.type == EventType.Repaint) DrawPreview();
            if (!painting || !editableMesh || target.sharedMesh != editableMesh || !ReadMesh())
            { EndStroke(); return; }
            control = GUIUtility.GetControlID("Project213VertexPainter".GetHashCode(), FocusType.Passive);
            if (stroke && (e.rawType == EventType.MouseUp || e.type == EventType.Ignore || (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)))
            { EndStroke(); e.Use(); return; }
            if (e.alt || e.control || e.command || e.button > 0) return;
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(control);
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.LeftBracket || e.keyCode == KeyCode.RightBracket))
            {
                radius = Mathf.Max(0.001f, radius * (e.keyCode == KeyCode.LeftBracket ? 0.8f : 1.25f));
                e.Use(); Repaint(); view.Repaint();
            }
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (!Pick(ray, out Vector3 hit, out Vector3 normal)) { hasPreviousHit = false; return; }
            if (e.type == EventType.Repaint)
            {
                using (new Handles.DrawingScope(e.shift ? Color.white : new Color(1f, 0.6f, 0.05f, 1f)))
                {
                    Handles.DrawWireDisc(hit, normal, radius);
                    Handles.DrawWireDisc(hit, normal, radius * (1f - softness));
                }
            }
            if (e.type == EventType.MouseMove) view.Repaint();
            if (e.type == EventType.MouseDown && e.button == 0 && HandleUtility.nearestControl == control && CanEdit())
            {
                Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Paint vertex colors");
                Undo.RegisterCompleteObjectUndo(editableMesh, "Paint vertex colors");
                strokeColors = (Color[])colors.Clone(); strokeWeights = new float[colors.Length];
                eraseStroke = e.shift;
                stroke = true; GUIUtility.hotControl = control;
                PaintSegment(hit, ray.direction); e.Use();
            }
            else if (stroke && e.type == EventType.MouseDrag && GUIUtility.hotControl == control)
            { PaintSegment(hit, ray.direction); e.Use(); }
        }
    }
}
