using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    // No asset deletion and no inactive-in-hierarchy heuristic. A successful bake
    // explicitly marks obsolete generated output; manual deactivation is NOT enough.
    internal static class LTRoadSegmentCleanup
    {
        internal static void Mark(LTRoad road,Transform segment,bool obsolete,bool undo)
        {
            var marker=segment?segment.GetComponent<LTRoadGenerated>():null;
            if(!marker||marker.owner!=road||marker.obsolete==obsolete)return;
            if(undo)Undo.RecordObject(marker,"Запечь дорогу");
            marker.obsolete=obsolete;EditorUtility.SetDirty(marker);PrefabUtility.RecordPrefabInstancePropertyModifications(marker);
        }
        static bool Editable(LTRoad road)=>road&&road.gameObject.scene.IsValid()&&road.gameObject.scene.isLoaded&&
            !EditorUtility.IsPersistent(road)&&PrefabStageUtility.GetPrefabStage(road.gameObject)==null;
        internal static Transform[] Candidates(LTRoad road)
        {
            if(!Editable(road)||!road.generatedRoot||string.IsNullOrEmpty(road.bakeId))return Array.Empty<Transform>();
            var root=road.generatedRoot;var owner=root.GetComponent<LTRoadGenerated>();
            if(!owner||owner.owner!=road||owner.bakeId!=road.bakeId||!root.IsChildOf(road.transform))return Array.Empty<Transform>();
            return root.Cast<Transform>().Where(t=>Eligible(road,t)).ToArray();
        }
        static bool Eligible(LTRoad road,Transform segment)
        {
            if(!segment||segment.parent!=road.generatedRoot||segment.gameObject.activeSelf||segment.childCount!=0||
                EditorUtility.IsPersistent(segment)||PrefabUtility.IsPartOfPrefabInstance(segment.gameObject))return false;
            var marker=segment.GetComponent<LTRoadGenerated>();var lod=segment.GetComponent<LTRoadLOD>();
            if(!marker||!marker.obsolete||marker.owner!=road||marker.bakeId!=road.bakeId||!lod||lod.owner!=road)return false;
            // Preserve custom scripts, missing scripts, extra components and user children.
            return segment.GetComponents<Component>().All(c=>c&&(
                c.GetType()==typeof(Transform)||c.GetType()==typeof(MeshFilter)||c.GetType()==typeof(MeshRenderer)||
                c.GetType()==typeof(MeshCollider)||c.GetType()==typeof(LTRoadLOD)||c.GetType()==typeof(LTRoadGenerated)||c.GetType()==typeof(LTRoadGroundBlend)));
        }
        internal static int Remove(LTRoad road,bool useUndo=true)
        {
            if(!Editable(road))return 0;
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating)
                throw new InvalidOperationException("Дождитесь завершения компиляции/импорта и выйдите из Play Mode.");
            if(!useUndo&&!EditorSceneManager.IsPreviewScene(road.gameObject.scene))
                throw new InvalidOperationException("Очистка без Undo разрешена только в изолированной preview-проверке.");
            var candidates=Candidates(road);if(candidates.Length==0)return 0;
            int group=-1,count=0;
            if(useUndo){Undo.IncrementCurrentGroup();group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Удалить неиспользуемые сегменты дороги");}
            try
            {
                foreach(var segment in candidates)
                {
                    if(!Eligible(road,segment))continue;
                    if(useUndo)Undo.DestroyObjectImmediate(segment.gameObject);else Object.DestroyImmediate(segment.gameObject);
                    count++;
                }
                road.World?.RefreshLODCache();
                if(useUndo&&count>0)EditorSceneManager.MarkSceneDirty(road.gameObject.scene);
            }
            finally{if(useUndo)Undo.CollapseUndoOperations(group);}
            return count;
        }
    }
}
