using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // Per-inspector interaction; session-only visibility. Only Size is serialized.
    // The explicit drag lease also works between IMGUI events, when hotControl
    // need not describe the Scene view in the terrain's EditorApplication tick.
    internal sealed class LTStampSizeHandles : IDisposable
    {
        const string Setting = "LocalTerrain.StampSizeHandles";
        static readonly HashSet<LTStampSizeHandles> Active = new HashSet<LTStampSizeHandles>();
        static readonly Vector2Int[] Signs = { new Vector2Int(-1,0), new Vector2Int(1,0), new Vector2Int(0,-1), new Vector2Int(0,1),
            new Vector2Int(-1,-1), new Vector2Int(-1,1), new Vector2Int(1,1), new Vector2Int(1,-1) };
        readonly Vector3[] outline = new Vector3[5];
        Component owner;
        LTWorld world;
        SceneView dragView;
        int control, undoGroup = -1;
        Vector2 startSize;
        Matrix4x4 startMatrix, startInverse;
        bool dragging;

        internal static bool IsEditing(LTWorld target)
        {
            foreach (var edit in Active) if (edit.dragging && edit.owner && edit.world == target) return true;
            return false;
        }

        internal void DrawInspector(Component stamp)
        {
            bool wasChanged = GUI.changed;
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            bool enabled = EditorGUILayout.Toggle("Ручки размера в сцене", SessionState.GetBool(Setting, true));
            if (EditorGUI.EndChangeCheck())
            {
                SessionState.SetBool(Setting, enabled);
                if (!enabled) Finish();
                SceneView.RepaintAll();
            }
            if (enabled)
            {
                EditorGUILayout.HelpBox("Круглые ручки по краям — ширина/длина; квадратные по углам — размер с сохранением пропорций. Центр и Transform не меняются. Esc — отмена текущего перетаскивания; Undo/Redo — весь жест. Нужны один выбранный штамп и включённые Gizmos.", MessageType.None);
                var parent = stamp.GetComponentInParent<LTWorld>();
                if (parent && !parent.autoUpdate)
                    EditorGUILayout.HelpBox("Auto Update выключен: Size и предпросмотр меняются сразу, геометрия — после Apply Pending Changes.", MessageType.Info);
            }
            GUI.changed = wasChanged;
        }

        internal void Draw(Component stamp)
        {
            if(stamp is LTHoleStamp meshHole&&meshHole.cutMode==LTHoleStampMode.MeshVolume){Finish();return;}
            var view = SceneView.currentDrawingSceneView;
            if (!stamp || !SessionState.GetBool(Setting, true) || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorUtility.IsPersistent(stamp) || !stamp.gameObject.scene.IsValid() || !stamp.gameObject.scene.isLoaded ||
                Selection.activeGameObject != stamp.gameObject || Selection.gameObjects.Length != 1 || !view || !view.drawGizmos)
            { Finish(); return; }
            // Another Scene view can repaint while the first owns a drag. Do not
            // release that view's control or feed its event stream to the sliders.
            if (dragging && view != dragView) return;
            var e = Event.current;
            if (dragging && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                ApplySize(owner, startSize);
                Finish(); e.Use(); return;
            }
            if (dragging && GUIUtility.hotControl != control) Finish();
            Vector2 size = GetSize(stamp);
            var scale = stamp.transform.lossyScale;
            if (!LTStampSizeMath.Valid(size) || Vector3.Dot(stamp.transform.up, Vector3.up) < .9999f || scale.x <= 0 || scale.y <= .00001f || scale.z <= 0)
            { Finish(); return; }
            var matrix = stamp.transform.localToWorldMatrix;
            // An unrelated transform edit while dragging must not be overwritten.
            if (dragging && !matrix.Equals(startMatrix)) Finish();
            var previousDepth = Handles.zTest;
            Color color = stamp is LTHoleStamp ? new Color(1, .4f, .2f) : new Color(.2f, 1, .8f);
            try
            {
                Handles.zTest = CompareFunction.Always;
                using (new Handles.DrawingScope(color, Matrix4x4.identity))
                {
                    for (int i = 0; i < 4; i++)
                        outline[i] = matrix.MultiplyPoint3x4(new Vector3(Signs[i+4].x * size.x * .5f, 0, Signs[i+4].y * size.y * .5f));
                    outline[4] = outline[0];
                    // Bounding box is intentional for ellipses: corners scale both axes.
                    for (int i = 0; i < 4; i++) Handles.DrawDottedLine(outline[i], outline[i+1], 4);
                    for (int i = 0; i < Signs.Length; i++)
                    {
                        var sign = Signs[i]; bool corner = i >= 4;
                        var local = new Vector3(sign.x * size.x * .5f, 0, sign.y * size.y * .5f);
                        var position = matrix.MultiplyPoint3x4(local);
                        // Use the original diagonal throughout a proportional drag.
                        var basis = dragging ? startSize : size;
                        var direction = corner ? new Vector3(sign.x * basis.x, 0, sign.y * basis.y) : new Vector3(sign.x, 0, sign.y);
                        direction = matrix.MultiplyVector(direction).normalized;
                        int id = GUIUtility.GetControlID(unchecked(stamp.GetInstanceID() * 397 + i + 71083), FocusType.Passive);
                        // Alt is camera orbit, never a resize gesture. Preserve IDs.
                        if (e.alt && !dragging) continue;
                        bool ownedBefore = dragging && control == id;
                        EditorGUI.BeginChangeCheck();
                        Vector3 moved = Handles.Slider(id, position, direction, HandleUtility.GetHandleSize(position) * .065f,
                            corner ? Handles.CubeHandleCap : Handles.SphereHandleCap, 0);
                        bool changed = EditorGUI.EndChangeCheck();
                        if (!dragging && GUIUtility.hotControl == id) Begin(stamp, view, id);
                        if (changed && dragging && control == id)
                        {
                            var point = startInverse.MultiplyPoint3x4(moved);
                            var next = corner ? LTStampSizeMath.Corner(startSize, point, sign.x, sign.y)
                                : LTStampSizeMath.Edge(startSize, point, sign.x == 0 ? 1 : 0, sign.x == 0 ? sign.y : sign.x);
                            if (!next.Equals(GetSize(stamp)))
                            {
                                if (undoGroup < 0)
                                {
                                    Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup();
                                    Undo.SetCurrentGroupName(stamp is LTHoleStamp ? "Resize Hole Stamp" : "Resize Height Stamp");
                                }
                                ApplySize(stamp, next);
                            }
                        }
                        if (ownedBefore && GUIUtility.hotControl != id) Finish();
                    }
                    if (dragging)
                        Handles.Label(matrix.MultiplyPoint3x4(new Vector3(0, 0, size.y * .5f)), $"Size {size.x:g4} × {size.y:g4}  •  Esc: отмена");
                }
            }
            finally { Handles.zTest = previousDepth; }
        }

        void Begin(Component stamp, SceneView view, int id)
        {
            owner = stamp; world = stamp.GetComponentInParent<LTWorld>(); dragView = view; control = id;
            startSize = GetSize(stamp); startMatrix = stamp.transform.localToWorldMatrix; startInverse = startMatrix.inverse;
            dragging = true; undoGroup = -1; Active.Add(this);
            EditorApplication.update += WatchDrag;
            AssemblyReloadEvents.beforeAssemblyReload += Finish;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        void WatchDrag()
        {
            // MouseUp outside the view, lost focus, deletion and selection changes
            // must never leave the terrain coordinator permanently suspended.
            if (!owner || !dragView || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorWindow.focusedWindow != dragView || Selection.activeGameObject != owner.gameObject ||
                !SessionState.GetBool(Setting, true)) Finish();
        }

        void OnUndoRedo() { Release(); SceneView.RepaintAll(); }
        void Finish()
        {
            if (!dragging) return;
            try
            {
                if (undoGroup >= 0)
                {
                    Undo.FlushUndoRecordObjects();
                    Undo.CollapseUndoOperations(undoGroup);
                    Undo.IncrementCurrentGroup();
                }
            }
            finally { Release(); SceneView.RepaintAll(); }
        }

        void Release()
        {
            if (dragging && GUIUtility.hotControl == control) GUIUtility.hotControl = 0;
            EditorApplication.update -= WatchDrag;
            AssemblyReloadEvents.beforeAssemblyReload -= Finish;
            Undo.undoRedoPerformed -= OnUndoRedo;
            Active.Remove(this); dragging = false; owner = null; world = null; dragView = null; control = 0; undoGroup = -1;
        }

        internal static Vector2 GetSize(Component stamp) => stamp is LTHeightStamp height ? height.size : ((LTHoleStamp)stamp).size;

        // SerializedProperty provides Undo, prefab overrides and scene dirtying
        // for this field only. No forced refresh/save, Transform or material edits.
        internal static void ApplySize(Component stamp, Vector2 value)
        {
            if (!stamp || !LTStampSizeMath.Valid(value) || GetSize(stamp).Equals(value)) return;
            using (var serialized = new SerializedObject(stamp))
            {
                serialized.FindProperty("size").vector2Value = value;
                serialized.ApplyModifiedProperties();
            }
            SceneView.RepaintAll();
        }

        public void Dispose() => Finish();
    }
}
