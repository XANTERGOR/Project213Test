using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    [CustomEditor(typeof(LTRoadJunctionSocket))]
    public sealed class LTRoadJunctionSocketEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("Маркер размещается в центре сечения въезда, Y=0 относительно корня модели с Road Junction. Синяя Z смотрит наружу, зелёная Y — вверх. Width — полная ширина дороги. Scale=1. Маркеры общие для всех LOD: держите их вне веток LOD0/1/2. На дороге включите редактирование точек в Scene, выберите её начало/конец и нажмите на маркер либо подведите конец и подтвердите Enter.",MessageType.Info);
        }
        [DrawGizmo(GizmoType.Selected|GizmoType.NonSelected|GizmoType.Pickable)]
        static void Draw(LTRoadJunctionSocket socket,GizmoType type)
        {
            var old=Gizmos.color;var p=socket.transform.position;Gizmos.color=Color.cyan;
            Gizmos.DrawLine(p-socket.transform.right*socket.width*.5f,p+socket.transform.right*socket.width*.5f);
            Gizmos.DrawWireSphere(p,.15f);Gizmos.color=Color.blue;Gizmos.DrawRay(p,socket.transform.forward*2);
            Gizmos.color=Color.green;Gizmos.DrawRay(p,socket.transform.up);Gizmos.color=old;Handles.Label(p,socket.name);
        }
        [MenuItem("GameObject/Local Terrain/Junction Socket",false,18)]
        static void Create(MenuCommand command)
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var parent=command.context as GameObject;if(!parent)parent=Selection.activeGameObject;
            if(!parent||EditorUtility.IsPersistent(parent))
            {EditorUtility.DisplayDialog("Въезд","Откройте обычный префаб перекрёстка в Prefab Mode и выберите его корень. Для FBX нужна префаб-обёртка.","OK");return;}
            var go=new GameObject("Port");Undo.RegisterCreatedObjectUndo(go,"Создать въезд");
            if(go.scene!=parent.scene)SceneManager.MoveGameObjectToScene(go,parent.scene);
            Undo.SetTransformParent(go.transform,parent.transform,"Создать въезд");go.transform.localPosition=Vector3.zero;
            go.transform.localRotation=Quaternion.identity;go.transform.localScale=Vector3.one;Undo.AddComponent<LTRoadJunctionSocket>(go);
            Selection.activeGameObject=go;
        }
    }
}
