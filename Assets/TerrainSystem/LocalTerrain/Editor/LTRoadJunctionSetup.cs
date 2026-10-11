using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public static class LTRoadJunctionSetup
    {
        public static bool HasModel(GameObject root)=>root&&root.GetComponentsInChildren<MeshRenderer>(true).Any(r=>!r.GetComponentInParent<LTRoadGenerated>()&&!r.GetComponentInParent<LTRoadJunctionGenerated>());

        [MenuItem("GameObject/Local Terrain/Make Junction From Selected",false,16)]
        static void FromSelected(MenuCommand command)
        {
            var root=command.context as GameObject;if(!root)root=Selection.activeGameObject;
            try{Configure(root);}
            catch(Exception e){EditorUtility.DisplayDialog("Один объект перекрёстка",e.Message,"OK");}
        }
        public static LTRoadJunction Configure(GameObject root)
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode||!root||root.GetComponent<LTWorld>()||root.GetComponent<LTRoad>()||EditorUtility.IsPersistent(root)||!root.scene.IsValid()||!HasModel(root))
                throw new ArgumentException("Выберите корень модели с мешами/LOD и Port в сцене или Prefab Mode.");
            var node=root.GetComponent<LTRoadJunction>();
            var children=root.GetComponentsInChildren<LTRoadJunction>(true).Where(n=>n!=node&&n.isActiveAndEnabled).ToArray();
            if(children.Length>1)throw new ArgumentException("Внутри несколько активных узлов. Выберите корень одной модели перекрёстка.");
            var old=children.FirstOrDefault();
            if(old&&(old.generatedRoot||old.Roads().Any()||old.GetComponentsInChildren<MeshRenderer>(true).Length>0||old.GetComponentsInChildren<LTRoadJunctionSocket>(true).Length>0))
                throw new ArgumentException("Дочерний узел уже содержит геометрию/въезды или подключённые дороги. Автоматический перенос не выполнен; сначала отключите его подключения и выберите корень его модели.");
            if(node&&node.meshSource!=LTRoadJunctionSource.OwnHierarchy&&(node.generatedRoot||node.Roads().Any()))
                throw new ArgumentException("Этот узел уже построен или подключён. Смена источника выполняется вручную в инспекторе, чтобы не потерять подключения.");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Собрать перекрёсток на одном объекте");
            if(!node)node=Undo.AddComponent<LTRoadJunction>(root);else Undo.RecordObject(node,"Источник перекрёстка");
            if(old)
            {
                // Preserve the original controller as a disabled child, not a destructive
                // component move. Only the new controller owns future output and painting.
                node.radius=old.radius;node.neckLength=old.neckLength;node.blendWidth=old.blendWidth;node.terrainCellSize=old.terrainCellSize;
                node.foundationMode=old.foundationMode;node.foundationShoulder=old.foundationShoulder;
                node.groundLayer=old.groundLayer;node.asphaltMaterial=old.asphaltMaterial;node.surfaceOffset=old.surfaceOffset;
                node.meshDepth=old.meshDepth;
                node.textureRepeatMetres=old.textureRepeatMetres;node.socketTolerance=old.socketTolerance;
                node.connectionFit=old.connectionFit;node.profileBlendLength=old.profileBlendLength;
                node.profileMaxShift=old.profileMaxShift;node.profileSearchBand=old.profileSearchBand;
                node.groundBlend=old.groundBlend;node.groundBlendSource=old.groundBlendSource;node.groundBlendLayer=old.groundBlendLayer;
                node.groundBlendWidth=old.groundBlendWidth;node.groundBlendHoleWidth=old.groundBlendHoleWidth;
                node.groundBlendStrength=old.groundBlendStrength;node.groundBlendNoise=old.groundBlendNoise;
                node.groundBlendNoiseSize=old.groundBlendNoiseSize;node.groundBlendSeed=old.groundBlendSeed;node.groundBlendMaskCell=old.groundBlendMaskCell;
                node.clearVegetation=old.clearVegetation;node.clearStones=old.clearStones;
                node.lods=old.lods?.Select(l=>l==null?null:JsonUtility.FromJson<LTLODSettings>(JsonUtility.ToJson(l))).ToArray();
                Undo.RecordObject(old,"Отключить прежний контроллер");old.enabled=false;
                EditorUtility.SetDirty(old);PrefabUtility.RecordPrefabInstancePropertyModifications(old);
            }
            node.meshSource=LTRoadJunctionSource.OwnHierarchy;node.surface=LTRoadMode.Asphalt;
            node.status="Один объект настроен. Въезды берутся из его дочерних Port. "+(old?"Прежний дочерний контроллер отключён, не удалён. ":"")+"Выберите дорогу и её конец в Scene.";
            EditorUtility.SetDirty(node);PrefabUtility.RecordPrefabInstancePropertyModifications(node);EditorSceneManager.MarkSceneDirty(root.scene);
            Undo.CollapseUndoOperations(group);Selection.activeGameObject=root;LTRoadJunctionBakeQueue.Queue(node);return node;
        }
    }
}
