using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public static class LTRoadJunctionScene
    {
        static LTRoad messageRoad;
        static string message;
        static bool failed;
        public static void Draw(LTRoad road,bool start,Vector3 endpoint)
        {
            if(!road||road.mode!=LTRoadMode.Asphalt||!road.World)return;
            var linked=start?road.startJunction:road.endJunction;
            var current=Event.current;LTRoadJunction nearest=null;LTRoadJunctionSocket target=null;float best=float.PositiveInfinity;
            if(!linked)
            {
                foreach(var node in road.World.GetComponentsInChildren<LTRoadJunction>())
                {
                    if(!node.isActiveAndEnabled||!node.Authored||node.World!=road.World||node.gameObject.scene!=road.gameObject.scene||
                        EditorSceneManager.IsPreviewScene(node.gameObject.scene))continue;
                    foreach(var socket in node.Sockets)
                    {
                        Vector3 p;
                        try{p=road.World.transform.TransformPoint(node.SocketMatrix(socket).MultiplyPoint3x4(Vector3.zero))+Vector3.up*node.MeshSurfaceOffset;}
                        catch(ArgumentException){continue;}
                        bool occupied=node.Roads().Any(r=>r.startJunction==node&&r.startJunctionSocket==socket||r.endJunction==node&&r.endJunctionSocket==socket);
                        float distance=Vector3.Distance(endpoint,p);bool close=distance<=Math.Max(1,socket.width*.5f);
                        if(!occupied&&close&&distance<best){best=distance;nearest=node;target=socket;}
                        using(new Handles.DrawingScope(occupied?Color.gray:close?Color.yellow:Color.cyan))
                        {
                            float size=HandleUtility.GetHandleSize(p)*.075f;
                            bool clicked=Handles.Button(p,Quaternion.identity,size,size,Handles.SphereHandleCap);
                            if(clicked&&!occupied){Connect(road,start,node,socket);return;}
                            Handles.Label(p+Vector3.up*size*2,node.name+" / "+socket.name+(occupied?" — занят":""));
                            if(close&&!occupied)Handles.DrawDottedLine(endpoint,p,4);
                        }
                    }
                }
            }
            string problem=null;
            if(nearest)try{LTRoadJunctionConnection.CheckBasics(road,start,nearest,target);}catch(Exception e){problem=e.Message;}
            Handles.BeginGUI();
            try
            {
                GUILayout.BeginArea(new Rect(12,72,390,245),EditorStyles.helpBox);
                GUILayout.Label(start?"Подключение начала дороги":"Подключение конца дороги",EditorStyles.boldLabel);
                if(linked)
                {
                    GUILayout.Label("Подключено: "+linked.name);
                    if(GUILayout.Button("Отключить — освободить конец"))
                    {LTRoadJunctionConnection.Disconnect(road,start);message=null;}
                }
                else if(nearest)
                {
                    GUILayout.Label("Въезд: "+nearest.name+" / "+target.name);
                    if(problem!=null)EditorGUILayout.HelpBox(problem,MessageType.Warning);
                    else if(GUILayout.Button("Проверить стык и подключить (Enter)"))Connect(road,start,nearest,target);
                }
                else GUILayout.Label("Подведите конец к въезду или нажмите на его голубую точку.",EditorStyles.wordWrappedLabel);
                if(messageRoad==road&&!string.IsNullOrEmpty(message))EditorGUILayout.HelpBox(message,failed?MessageType.Error:MessageType.Info);
                GUILayout.EndArea();
            }
            finally{Handles.EndGUI();}
            if(nearest&&problem==null&&GUIUtility.hotControl==0&&!EditorGUIUtility.editingTextField&&current.type==EventType.KeyDown&&
                (current.keyCode==KeyCode.Return||current.keyCode==KeyCode.KeypadEnter))
            {Connect(road,start,nearest,target);current.Use();}
        }
        static void Connect(LTRoad road,bool start,LTRoadJunction node,LTRoadJunctionSocket socket)
        {
            messageRoad=road;
            try{LTRoadJunctionConnection.Connect(road,start,node,socket);failed=false;message="Подключено. Undo отменяет привязку.";}
            catch(Exception e){failed=true;message=e.Message;}
            SceneView.RepaintAll();
        }
    }
}
