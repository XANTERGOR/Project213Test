using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
namespace LocalTerrainPrototype
{
    public static partial class LTEditorEngine
    {
        [DrawGizmo(GizmoType.Selected|GizmoType.NonSelected|GizmoType.Pickable)]
        static void DrawHoleStampIcon(LTHoleStamp stamp,GizmoType type)
        {
            if(!stamp.enabled)return;
            var view=SceneView.currentDrawingSceneView;if(!view||!view.camera)return;
            var camera=view.camera.transform;var position=stamp.transform.position;
            float size=HandleUtility.GetHandleSize(position)*.14f;var old=Gizmos.color;
            Gizmos.color=new Color(1,.3f,.2f);
            for(int i=0;i<24;i++)
            {
                float a=i*Mathf.PI/12,b=(i+1)*Mathf.PI/12;
                Gizmos.DrawLine(position+size*(camera.right*Mathf.Cos(a)+camera.up*Mathf.Sin(a)*.55f),position+size*(camera.right*Mathf.Cos(b)+camera.up*Mathf.Sin(b)*.55f));
            }
            Gizmos.DrawLine(position+camera.up*size*.35f,position-camera.up*size*.35f);
            Gizmos.color=old;
        }
        sealed class HoleMaskCache {public string key;public LTStampDensityMask data;}
        static readonly ConditionalWeakTable<LTHoleStamp,HoleMaskCache> holeMasks=new ConditionalWeakTable<LTHoleStamp,HoleMaskCache>();
        static void CaptureHole(Transform child,LTWorld world,List<Stamp> result)
        {
            if(!child.TryGetComponent<LTHoleStamp>(out var stamp)||!stamp.enabled)return;
            stamp.status=null;
            try
            {
                var snapshot=CaptureHoleSnapshot(stamp,world,out string signature);
                result.Add(new Stamp{id=stamp.GetInstanceID(),signature=signature,bounds=snapshot.ProtectedBounds,hole=snapshot});
            }
            catch(ArgumentException e){stamp.status=e.Message;}
            catch(UnityException e){stamp.status="Hole input could not be read: "+e.Message;}
        }
        internal static LTHoleStampSnapshot CaptureHoleSnapshot(LTHoleStamp stamp,LTWorld world,out string signature)
        {
                if(stamp.cutMode==LTHoleStampMode.MeshVolume)return LTHoleMeshCache.Capture(stamp,world,out signature);
                var child=stamp.transform;
                var scale=child.lossyScale;
                if(Vector3.Dot(child.up,Vector3.up)<.9999f||scale.x<=0||scale.y<=.00001f||scale.z<=0)
                    throw new ArgumentException("Hole Stamp skipped: use only Y rotation and positive scale.");
                var matrix=world.transform.worldToLocalMatrix*child.localToWorldMatrix;
                string maskKey="";LTStampDensityMask mask=null;
                if(stamp.mask)
                {
                    if(!stamp.mask.isReadable)throw new ArgumentException("Hole inactive: enable Read/Write on Mask. Import settings were not changed.");
                    maskKey=stamp.mask.GetInstanceID()+":"+stamp.mask.imageContentsHash+":"+stamp.mask.updateCount+":"+AssetInputHash(stamp.mask)+":"+stamp.invertMask;
                    var cache=holeMasks.GetValue(stamp,key=>new HoleMaskCache());
                    if(cache.data==null||cache.key!=maskKey)
                    {
                        var data=LTStampDensityMask.Capture(stamp.mask,stamp.invertMask,new Vector2(0,1),1);
                        cache.data=data;cache.key=maskKey;
                    }
                    mask=cache.data;
                }
                else holeMasks.Remove(stamp);
                var snapshot=new LTHoleStampSnapshot(matrix,matrix.inverse,stamp.size,stamp.shape,stamp.edgeCellSize,mask,stamp.maskThreshold,stamp.lodEdgeCellSize,stamp.lodCornerError);
                signature="hole-edge-lod-v5:"+EditorJsonUtility.ToJson(stamp)+matrix.ToString("R")+maskKey;
                return snapshot;
        }
        static bool HasTerrainCuts(List<Stamp> stamps)=>stamps.Any(s=>s.hole!=null||s.meshStamp&&s.meshCutTerrain);
        static IEnumerable<LTBuildStep> PrepareLODProtectedArea(List<Stamp> stamps,List<Vector3Int> fine,Rect rect,
            Func<float,float,float> height,Action<Func<Rect,bool>> ready,Func<Vector3Int,int> baseMask,Func<Rect,bool> regularTopology,
            Func<float,float,bool> cut,Func<float,float,float> terrainHeight=null)
        {
            var holes=stamps.Where(s=>s.hole!=null&&!s.hole.RequiresHeight).Select(s=>s.hole).ToArray();
            var volumes=stamps.Where(s=>s.hole!=null&&s.hole.RequiresHeight).Select(s=>s.hole).ToArray();
            var roads=stamps.Where(s=>s.fixedRoadSurface&&s.road!=null&&s.roadTerrainSurface==null).Select(s=>s.road).ToArray();
            var surfaces=stamps.Where(s=>s.fixedRoadSurface&&s.roadTerrainSurface!=null).Select(s=>s.roadTerrainSurface).ToArray();
            var rockBounds=stamps.Where(s=>s.meshStamp&&s.meshCutTerrain).Select(s=>s.bounds).ToArray();
            if(holes.Length==0&&volumes.Length==0&&roads.Length==0&&surfaces.Length==0&&rockBounds.Length==0){ready(null);yield break;}
            LTRoadTerrainLODProtection roadProtection=null;
            if(surfaces.Length>0)
            {
                roadProtection=new LTRoadTerrainLODProtection();
                var remeshed=stamps.Where(s=>s.hole!=null||s.meshStamp&&s.meshCutTerrain||s.road!=null&&s.road.mode==LTRoadMode.GeometricAsphalt).Select(s=>s.bounds).ToArray();
                foreach(var step in roadProtection.Prepare(fine,rect,surfaces,terrainHeight??height,baseMask,regularTopology,remeshed))yield return step;
            }
            LTHoleVolumeLODProtection volumeEdges=null;
            if(volumes.Length>0)
            {
                volumeEdges=new LTHoleVolumeLODProtection();
                foreach(var step in volumeEdges.Prepare(volumes,fine,rect,height,baseMask,regularTopology,holes))yield return step;
            }
            LTRockTerrainLODProtection rockEdges=null;
            if(rockBounds.Length>0)
            {
                rockEdges=new LTRockTerrainLODProtection();
                foreach(var step in rockEdges.Prepare(fine,rect,rockBounds,cut))yield return step;
            }
            ready(area=>LTHoleStampSnapshot.ProtectsAnyLOD(holes,area)||(volumeEdges?.Protects(area)??false)||
                (rockEdges?.Protects(area)??false)||(roadProtection?.Protects(area)??false)||roads.Any(road=>road.Intersects(area)));
        }
        internal static void AddHoleStamp(LTWorld world)
        {
            var go=new GameObject("Hole Stamp");Undo.RegisterCreatedObjectUndo(go,"Add Hole Stamp");
            go.transform.SetParent(world.transform,false);
            go.transform.localPosition=new Vector3(world.source.size.x*.5f,0,world.source.size.z*.5f);
            go.AddComponent<LTHoleStamp>();Selection.activeGameObject=go;
        }
    }
    [CustomEditor(typeof(LTHoleStamp))]
    public sealed class LTHoleStampInspector:Editor
    {
        readonly LTStampSizeHandles sizeHandles = new LTStampSizeHandles();
        void OnSceneGUI() => sizeHandles.Draw((LTHoleStamp)target);
        void OnDisable() => sizeHandles.Dispose();
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("cutMode"),new GUIContent("Cut Mode"));
            bool meshMode=serializedObject.FindProperty("cutMode").enumValueIndex==(int)LTHoleStampMode.MeshVolume;
            if(meshMode)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("cutMesh"),new GUIContent("Cut Mesh"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("lodEdgeCellSize"),new GUIContent("LOD Edge Cell Size (m)"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("meshLodContourError"),new GUIContent("LOD Contour Error (m)"));
                var filter=((LTHoleStamp)target).GetComponent<MeshFilter>();
                if(filter&&filter.sharedMesh&&GUILayout.Button("Взять меш из MeshFilter этого объекта"))
                    serializedObject.FindProperty("cutMesh").objectReferenceValue=filter.sharedMesh;
            }
            else
            {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("shape"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("size"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("lodEdgeCellSize"),new GUIContent("LOD Edge Cell Size (m)"));
            if(serializedObject.FindProperty("shape").enumValueIndex==(int)LTStampShape.Rectangle&&!serializedObject.FindProperty("mask").objectReferenceValue)
                EditorGUILayout.PropertyField(serializedObject.FindProperty("lodCornerError"),new GUIContent("LOD Corner Error (m)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("mask"));
            if(serializedObject.FindProperty("mask").objectReferenceValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("invertMask"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("maskThreshold"));
            }
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("edgeCellSize"),new GUIContent("Edge Cell Size (m)"));
            serializedObject.ApplyModifiedProperties();var stamp=(LTHoleStamp)target;
            if(meshMode)
            {
                EditorGUILayout.HelpBox("Вырезает только точки поверхности внутри объёма Cut Mesh. Положение Y, вращение XYZ и Scale влияют на результат. Используются локальные координаты/пивот меша и Transform штампа; Shape, Size и Mask в этом режиме не используются. Стенки/дно/renderer не создаются; коллайдер вырезается вместе с террейном.",MessageType.Info);
                EditorGUILayout.HelpBox("Нужен замкнутый треугольный меш без самопересечений, до 50 000 треугольников. Проверяются рёбра, winding и ненулевой объём, но не все самопересечения. Вложенные оболочки используют правило чётности (полости). Edge Cell Size задаёт точность LOD0; тонкие детали меньше ячейки могут потеряться.",MessageType.Info);
                EditorGUILayout.HelpBox("LOD Edge Cell Size — предельный размер ячейки контура вдали. LOD Contour Error — допустимое отклонение края от LOD0 в плоскости XZ. Ноль в любом из этих полей сохраняет точный контур. Малые отверстия, сложные участки, перемычки и соседство с другими типами вырезов могут сохранять более мелкую сетку. Учитываются все варианты стыков; LOD0 и коллайдер не меняются.",MessageType.Info);
                var meshWorld=stamp.GetComponentInParent<LTWorld>();
                if(meshWorld)
                {
                    try{LTHoleMeshCache.Capture(stamp,meshWorld,out _);}
                    catch(ArgumentException ex){EditorGUILayout.HelpBox(ex.Message,MessageType.Error);}
                    catch(UnityException ex){EditorGUILayout.HelpBox(ex.Message,MessageType.Error);}
                }
                else EditorGUILayout.HelpBox("Place this stamp under an LTWorld.",MessageType.Warning);
                if(GUILayout.Button("Перечитать Cut Mesh"))LTHoleMeshCache.Invalidate(stamp);
                if(meshWorld&&!meshWorld.autoUpdate)EditorGUILayout.HelpBox("Auto Update выключен: после изменений нажми Apply Pending Changes.",MessageType.Warning);
                LTHoleStampPreview.DrawInspector(stamp);return;
            }
            EditorGUILayout.HelpBox("Through-hole in terrain AND its collider; no walls or floor. Empty Mask cuts the whole Shape; white cuts and black keeps terrain (red channel, after inversion). Overlapping hole stamps combine. Separate roads and rocks are not cut.",MessageType.Info);
            EditorGUILayout.HelpBox("Only Y rotation and positive scale. Y position does not set depth. Edge Cell Size controls LOD0/collider refinement. LOD Edge Cell Size permits coarser rims (0 = lock). Straight sides, rounded shapes and corners have separate safety limits. Mask contours retain fine cells; the surrounding ring can simplify. Sub-cell mask details may be lost.",MessageType.Info);
            var world=stamp.GetComponentInParent<LTWorld>();
            if(world)
            {
                try
                {
                    var matrix=world.transform.worldToLocalMatrix*stamp.transform.localToWorldMatrix;
                    var limits=new LTHoleStampSnapshot(matrix,matrix.inverse,stamp.size,stamp.shape,stamp.edgeCellSize,null,stamp.maskThreshold,stamp.lodEdgeCellSize,stamp.lodCornerError);
                    string far=stamp.mask?"mask contour locked":limits.LODEdgeCellSize==0?"locked":$"≤ {limits.LODEdgeCellSize:F3} m";
                    string corners=stamp.shape==LTStampShape.Rectangle&&!stamp.mask&&limits.LODEdgeCellSize>0
                        ?limits.LODCornerError==0?"; original corner cells locked":$"; corner merge XZ error ≤ {limits.LODCornerError:F3} m":"";
                    EditorGUILayout.HelpBox($"Effective limits: LOD0 ≤ {limits.CellSize:F3} m; far edge {far}{corners}. Actual grid uses power-of-two steps; other density/height constraints may make cells smaller.",MessageType.None);
                }
                catch(ArgumentException e){EditorGUILayout.HelpBox(e.Message,MessageType.Error);}
            }
            if(!world)EditorGUILayout.HelpBox("Place this stamp under an LTWorld.",MessageType.Warning);
            else if(!world.autoUpdate)EditorGUILayout.HelpBox("Auto Update is paused: use LTWorld → Apply Pending Changes to rebuild the hole and collider.",MessageType.Warning);
            if(stamp.mask&&!stamp.mask.isReadable)EditorGUILayout.HelpBox("Hole inactive: Mask needs Read/Write enabled.",MessageType.Error);
            if(!string.IsNullOrEmpty(stamp.status))EditorGUILayout.HelpBox(stamp.status,MessageType.Error);
            sizeHandles.DrawInspector(stamp);
            LTHoleStampPreview.DrawInspector(stamp);
        }
    }
}
