using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalTerrainPrototype
{
    // One editor-owned overlay for the active selection, even with locked inspectors.
    // No scene components, material assignments, mesh copies or generation calls.
    [InitializeOnLoad]
    internal static class LTStampPreview
    {
        enum Mode { Off, Mask, Influence, Density }
        const string Prefix = "LocalTerrain.StampPreview.";
        internal const string ShaderPath = "Assets/TerrainSystem/LocalTerrain/Editor/LTStampPreview.shader";
        static readonly string[] ModeNames = { "Выключен", "Маска высоты (R)", "Влияние на высоту", "Плотность сетки" };
        static readonly List<LTChunk> Chunks = new List<LTChunk>();
        static readonly List<DrawChunk> DrawChunks = new List<DrawChunk>();
        static readonly Plane[] Planes = new Plane[6];
        static Mode mode;
        static float opacity;
        static LTHeightStamp selected;
        static Transform cachedRoot;
        static Material material;
        static Texture2D image;
        static Color[] pixels;
        static TextureKey key;
        static bool hasKey, ready, chunksDirty = true;
        static int row;
        static double nextPoll;
        static string error;
        static Func<int, int, float> readDensity;
        static readonly System.Diagnostics.Stopwatch BuildWatch = new System.Diagnostics.Stopwatch();
        struct DrawChunk { internal LTChunk chunk; internal MeshFilter filter; internal MeshRenderer renderer; }

        struct TextureKey : IEquatable<TextureKey>
        {
            internal Texture2D texture;
            internal int width, height;
            internal uint update;
            internal Hash128 hash;
            internal bool remap, invert;
            internal Vector2 range;
            internal float power;
            public bool Equals(TextureKey other) => texture == other.texture && width == other.width && height == other.height &&
                update == other.update && hash == other.hash && remap == other.remap && invert == other.invert && range.Equals(other.range) && power == other.power;
        }

        static LTStampPreview()
        {
            opacity = Mathf.Clamp(SessionState.GetFloat(Prefix + "Opacity", .55f), .05f, .9f);
            Selection.selectionChanged += SelectionChanged;
            EditorApplication.hierarchyChanged += HierarchyChanged;
            EditorApplication.projectChanged += Invalidate;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.quitting += Release;
            EditorApplication.playModeStateChanged += _ => SelectionChanged();
            SetMode((Mode)Mathf.Clamp(SessionState.GetInt(Prefix + "Mode", 0), 0, 3));
        }

        internal static void DrawInspector(LTHeightStamp stamp)
        {
            bool wasChanged = GUI.changed;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Предпросмотр штампа — Scene View", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var choice = (Mode)EditorGUILayout.Popup("Показывать", (int)mode, ModeNames);
            if (EditorGUI.EndChangeCheck()) SetMode(choice);
            if (mode != Mode.Off)
            {
                EditorGUI.BeginChangeCheck();
                opacity = EditorGUILayout.Slider("Непрозрачность", opacity, .05f, .9f);
                if (EditorGUI.EndChangeCheck()) { SessionState.SetFloat(Prefix + "Opacity", opacity); SceneView.RepaintAll(); }
                if (Selection.activeGameObject != stamp.gameObject)
                    EditorGUILayout.HelpBox("Показан только активный выбранный Height Stamp. Закреплённый инспектор не меняет выбор.", MessageType.Info);
                else
                {
                    string unavailable = Unavailable(stamp);
                    if (!string.IsNullOrEmpty(unavailable)) EditorGUILayout.HelpBox(unavailable, MessageType.Warning);
                    else if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Warning);
                    else if (!ready) EditorGUILayout.LabelField("Готовится карта предпросмотра…");
                }
                string legend = mode == Mode.Density
                    ? $"Синий: крупная ячейка {stamp.densityCellSizeMax:g4} м → красный: мелкая {stamp.densityCellSizeMin:g4} м. " +
                      "Это точечная карта запроса плотности выбранного штампа, не готовые ячейки. Поздние штампы, сохранение мелких деталей маски и стыковка могут изменить сетку."
                    : mode == Mode.Influence
                        ? "Синий: вес 0 → красный: вес 1. Учитывает Strength и Edge Falloff; маску — только при выключенном Mask Is Height. Не показывает итоговую разницу высот после Operation и других штампов."
                        : "Синий: R=0 → красный: R=1. Только красный канал Mask, без Strength и Edge Falloff. При Mask Is Height это множитель целевой высоты; иначе — множитель влияния. Без маски R=1.";
                EditorGUILayout.HelpBox(legend + "\nКарта маски 256×256, мелкие детали могут быть не видны. Только Scene View и существующая сетка (без шейдерного displacement); Game View, геометрия и материалы не меняются.", MessageType.Info);
                if (GUILayout.Button("Обновить предпросмотр")) Invalidate();
            }
            // Preview controls are session state, never serialized stamp edits.
            GUI.changed = wasChanged;
        }

        static void SetMode(Mode value)
        {
            mode = value;
            SessionState.SetInt(Prefix + "Mode", (int)mode);
            EditorApplication.update -= Tick;
            SceneView.duringSceneGui -= Draw;
            Release();
            if (mode != Mode.Off) { EditorApplication.update += Tick; SceneView.duringSceneGui += Draw; }
            SceneView.RepaintAll();
        }

        static void SelectionChanged() { Release(); SceneView.RepaintAll(); }
        static void HierarchyChanged() { chunksDirty = true; nextPoll = 0; }
        internal static void Invalidate() { hasKey = ready = false; error = null; nextPoll = 0; SceneView.RepaintAll(); }
        static void ReleaseImage()
        {
            if (image) Object.DestroyImmediate(image);
            image = null; ready = false; row = 0; readDensity = null;
        }
        static void Release()
        {
            ReleaseImage();
            if (material) Object.DestroyImmediate(material);
            material = null; pixels = null; selected = null; cachedRoot = null;
            Chunks.Clear(); DrawChunks.Clear(); chunksDirty = true;
            hasKey = false; key = default; error = null; nextPoll = 0;
        }

        static string Unavailable(LTHeightStamp stamp)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Предпросмотр доступен в Edit Mode.";
            if (!stamp || !stamp.isActiveAndEnabled) return "Выбранный штамп выключен.";
            if (EditorUtility.IsPersistent(stamp) || !stamp.gameObject.scene.IsValid() || !stamp.gameObject.scene.isLoaded)
                return "Выбери штамп в открытой сцене, не prefab-ассет.";
            var world = stamp.GetComponentInParent<LTWorld>();
            if (!world || !world.generatedRoot) return "Нет готовой сетки родительского LT World. Предпросмотр сам её не строит.";
            var tr = stamp.transform; var scale = tr.lossyScale;
            if (Vector3.Dot(tr.up, Vector3.up) < .9999f || scale.x <= 0 || scale.y <= .00001f || scale.z <= 0)
                return "Для штампа нужны только Y rotation и положительный Scale.";
            if (!LTStampPreviewMath.Finite(stamp.size.x) || !LTStampPreviewMath.Finite(stamp.size.y) || stamp.size.x <= 0 || stamp.size.y <= 0)
                return "Size должен быть положительным и конечным.";
            if (mode == Mode.Density && !stamp.densityZone) return "Включи Density Zone для просмотра плотности.";
            if (mode != Mode.Density && !stamp.affectHeight) return "Affect Height выключен: высота не изменяется.";
            bool densityMask = mode == Mode.Density && stamp.densityMode == LTStampDensityMode.Mask;
            Texture2D texture = densityMask ? (stamp.densityMask ? stamp.densityMask : stamp.mask) : mode == Mode.Density ? null : stamp.mask;
            if (densityMask && !texture) return "Mask density неактивна: назначь Density Mask или Mask.";
            if (texture && !texture.isReadable) return "Для маски требуется Read/Write. Настройки импорта автоматически не меняются.";
            if (densityMask)
            {
                try { LTStampPreviewMath.ValidateRemap(stamp.densityMaskRange, stamp.densityMaskPower); }
                catch (ArgumentException ex) { return ex.Message; }
            }
            return null;
        }

        static TextureKey CurrentKey(LTHeightStamp stamp)
        {
            bool remap = mode == Mode.Density && stamp.densityMode == LTStampDensityMode.Mask;
            var texture = remap ? (stamp.densityMask ? stamp.densityMask : stamp.mask) : mode == Mode.Density ? null : stamp.mask;
            return new TextureKey { texture = texture, width = texture ? texture.width : 1, height = texture ? texture.height : 1,
                hash = texture ? texture.imageContentsHash : default, update = texture ? texture.updateCount : 0,
                remap = remap, invert = remap && stamp.densityMaskInvert, range = remap ? stamp.densityMaskRange : new Vector2(0, 1), power = remap ? stamp.densityMaskPower : 1 };
        }

        static void Tick()
        {
            var active = Selection.activeGameObject ? Selection.activeGameObject.GetComponent<LTHeightStamp>() : null;
            if (active != selected) { Release(); selected = active; }
            if (!selected) return;
            double now = EditorApplication.timeSinceStartup;
            if (now >= nextPoll)
            {
                nextPoll = now + .1;
                string unavailable = Unavailable(selected);
                if (unavailable != null)
                {
                    if (ready || hasKey) { ReleaseImage(); hasKey = false; SceneView.RepaintAll(); }
                    error = unavailable; return;
                }
                var next = CurrentKey(selected);
                if (!hasKey || !key.Equals(next))
                {
                    ReleaseImage(); key = next; hasKey = true; error = null;
                    if (!key.texture) ready = true;
                    else
                    {
                        if (pixels == null) pixels = new Color[LTStampPreviewMath.Resolution * LTStampPreviewMath.Resolution];
                        var source = key.texture;
                        readDensity = (x, y) => source.GetPixel(x, y).r;
                    }
                    SceneView.RepaintAll();
                }
            }
            if (ready || !hasKey || error != null) return;
            // A bounded editor task, not a full image read inside a render callback.
            // Moving the stamp/camera changes only matrices, not this texture key.
            try
            {
                const int n = LTStampPreviewMath.Resolution;
                BuildWatch.Restart();
                do
                {
                    for (int x = 0; x < n; x++)
                    {
                        var uv = new Vector2((x + .5f) / n, (row + .5f) / n);
                        float v = key.remap ? LTStampPreviewMath.DensitySample(key.width, key.height, readDensity, uv, key.invert, key.range, key.power)
                            : key.texture.GetPixelBilinear(uv.x, uv.y).r;
                        if (!LTStampPreviewMath.Finite(v)) throw new ArgumentException("Маска содержит NaN/Infinity.");
                        pixels[row * n + x] = new Color(v, 0, 0, 1);
                    }
                    row++;
                } while (row < n && BuildWatch.Elapsed.TotalMilliseconds < 2);
                if (row < n) return;
                // Reject stale work before publication (including a reimport mid-build).
                if (!key.Equals(CurrentKey(selected))) { Invalidate(); return; }
                image = new Texture2D(n, n, TextureFormat.RGBAFloat, false, true) { name = "LT Stamp preview (editor only)",
                    hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                image.SetPixels(pixels); image.Apply(false, false);
                ready = true; readDensity = null; SceneView.RepaintAll();
            }
            catch (Exception ex) { ReleaseImage(); error = "Предпросмотр недоступен: " + ex.Message; }
        }

        static void Draw(SceneView view)
        {
            if (Event.current.type != EventType.Repaint || !view.drawGizmos || !ready || !selected ||
                Selection.activeGameObject != selected.gameObject || !view.camera || Unavailable(selected) != null) return;
            var world = selected.GetComponentInParent<LTWorld>();
            if (!world || !world.generatedRoot) return;
            if (chunksDirty || cachedRoot != world.generatedRoot)
            {
                cachedRoot = world.generatedRoot; chunksDirty = false;
                Chunks.Clear(); DrawChunks.Clear(); cachedRoot.GetComponentsInChildren(false, Chunks);
                foreach (var chunk in Chunks)
                    if (chunk.GetComponentInParent<LTWorld>() == world)
                        DrawChunks.Add(new DrawChunk { chunk = chunk, filter = chunk.GetComponent<MeshFilter>(), renderer = chunk.GetComponent<MeshRenderer>() });
            }
            if (!material)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
                if (!shader || !shader.isSupported || ShaderUtil.ShaderHasError(shader))
                { error = "Shader предпросмотра недоступен; проверь Console после импорта."; ready = false; return; }
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            var stampToWorld = selected.transform.localToWorldMatrix;
            var min = new Vector2(float.MaxValue, float.MaxValue); var max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 4; i++)
            {
                var p = stampToWorld.MultiplyPoint3x4(new Vector3(((i & 1) == 0 ? -.5f : .5f) * selected.size.x, 0, ((i & 2) == 0 ? -.5f : .5f) * selected.size.y));
                var xz = new Vector2(p.x, p.z); min = Vector2.Min(min, xz); max = Vector2.Max(max, xz);
            }
            GeometryUtility.CalculateFrustumPlanes(view.camera, Planes);
            material.SetTexture("_PreviewMask", image ? image : Texture2D.whiteTexture);
            material.SetVector("_StampSize", new Vector4(mode == Mode.Density ? selected.size.x : Mathf.Max(.01f, selected.size.x),
                mode == Mode.Density ? selected.size.y : Mathf.Max(.01f, selected.size.y), 0, 0));
            material.SetFloat("_Mode", (int)mode); material.SetFloat("_Shape", (int)selected.shape); material.SetFloat("_Opacity", opacity);
            material.SetFloat("_Edge", selected.edgeFalloff); material.SetFloat("_Strength", selected.strength);
            material.SetFloat("_MaskIsHeight", selected.maskIsHeight ? 1 : 0);
            material.SetFloat("_MaskDensity", selected.densityMode == LTStampDensityMode.Mask ? 1 : 0);
            material.SetFloat("_UniformDensity", selected.densityCellSizeMax <= selected.densityCellSizeMin ? 1 : 0);
            // Match the engine's inverse evaluated on terrain-local (x, 0, z).
            var terrainToStamp = (world.transform.worldToLocalMatrix * stampToWorld).inverse;
            var flatToStamp = terrainToStamp * Matrix4x4.Scale(new Vector3(1, 0, 1)) * world.transform.worldToLocalMatrix;
            bool wireframe = GL.wireframe;
            try
            {
                GL.wireframe = false;
                foreach (var draw in DrawChunks)
                {
                    if (!draw.chunk || !draw.filter || !draw.renderer || !draw.renderer.enabled || draw.renderer.forceRenderingOff ||
                        !draw.chunk.gameObject.activeInHierarchy || SceneVisibilityManager.instance.IsHidden(draw.chunk.gameObject) ||
                        (view.camera.cullingMask & (1 << draw.chunk.gameObject.layer)) == 0) continue;
                    var bounds = draw.renderer.bounds;
                    if (bounds.max.x < min.x || bounds.min.x > max.x || bounds.max.z < min.y || bounds.min.z > max.y ||
                        !GeometryUtility.TestPlanesAABB(Planes, bounds)) continue;
                    // Current displayed mesh: follows Spatial/whole-chunk LOD and real
                    // holes, without reading vertex arrays or touching mesh/material refs.
                    var mesh = draw.filter.sharedMesh;
                    if (!mesh) continue;
                    var matrix = draw.filter.transform.localToWorldMatrix;
                    material.SetMatrix("_MeshToStamp", flatToStamp * matrix);
                    if (material.SetPass(0)) Graphics.DrawMeshNow(mesh, matrix, 0);
                }
            }
            finally { GL.wireframe = wireframe; }
        }
    }
}
