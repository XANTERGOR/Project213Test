using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // One connection workflow for Scene handles and inspector. Preparation is read-only:
    // invalid topology must never leave a half-connected scene behind.
    public static class LTRoadJunctionConnection
    {
        public static void CheckBasics(LTRoad road,bool start,LTRoadJunction node,LTRoadJunctionSocket socket)
        {
            if(!road||!node||!socket||!node.Authored||!node.isActiveAndEnabled||!road.isActiveAndEnabled)
                throw new ArgumentException("Выберите активную дорогу и въезд активного перекрёстка.");
            if(road.mode!=LTRoadMode.Asphalt)throw new ArgumentException("К этому въезду подключается дорога Asphalt.");
            if(!road.World||road.World!=node.World||road.gameObject.scene!=node.gameObject.scene)
                throw new ArgumentException("Дорога и перекрёсток должны быть в одной сцене и одном LTWorld.");
            var current=start?road.startJunction:road.endJunction;
            if(current&&current!=node)throw new ArgumentException("Этот конец уже занят. Сначала отключите его от прежнего перекрёстка.");
            if(current==node&&(start?road.startJunctionSocket:road.endJunctionSocket)!=socket)
                throw new ArgumentException("Для смены въезда сначала отключите этот конец дороги.");
            if(!node.OwnHierarchy&&Mathf.Abs(road.surfaceOffset-node.surfaceOffset)>.0001f)
                throw new ArgumentException("Старый режим префаба требует одинаковый Surface Offset дороги и узла.");
            if(node.groundLayer&&(road.groundLayer==node.groundLayer&&road.GroundProjection==LTRoadProjection.Spline||
                road.ActiveWheelLayer==node.groundLayer&&road.WheelProjection==LTRoadProjection.Spline))
                throw new ArgumentException("Слой грунта площадки не должен быть направленным Spline-слоем этой дороги.");
            if(road.points==null||road.points.Count<2)throw new ArgumentException("Дороге нужны минимум две точки.");
            node.Socket(road,start,socket);
            if(Mathf.Abs(socket.width-road.width)>node.socketTolerance)
                throw new ArgumentException($"Ширина дороги {road.width:G4} м, въезд {socket.width:G4} м. Подготовьте совпадающие профили.");
            node.Port(road,start,socket);
        }
        public static void Prepare(LTRoad road,bool start,LTRoadJunction node,LTRoadJunctionSocket socket,bool preview=false)
        {
            CheckBasics(road,start,node,socket);
            if(preview&&!EditorSceneManager.IsPreviewScene(road.gameObject.scene))throw new ArgumentException("Preview is only allowed in an owned preview scene.");
            var snapshot=road.PreviewConnection(node,socket,start);
            var geometry=preview?LTRoadModuleSource.BuildPreview(road,snapshot):LTRoadModuleSource.Build(road,snapshot);
            if(!preview)LTRoadJunctionMesh.ValidateRoadEnds(road,geometry); // Existing opposite end too.
            LTRoadJunctionPrefab.MatchRoad(node,road,start,geometry,null,preview,socket);
        }
        public static void Connect(LTRoad road,bool start,LTRoadJunction node,LTRoadJunctionSocket socket,bool preview=false)
        {
            Prepare(road,start,node,socket,preview);
            if(!preview)Undo.RecordObject(road,"Подключить дорогу к въезду");
            if(start){road.startJunction=node;road.startJunctionSocket=socket;}
            else{road.endJunction=node;road.endJunctionSocket=socket;}
            if(!preview)Changed(road,node);
        }
        public static void Disconnect(LTRoad road,bool start,bool preview=false)
        {
            if(!road)return;
            if(preview&&!EditorSceneManager.IsPreviewScene(road.gameObject.scene))throw new ArgumentException("Preview is only allowed in an owned preview scene.");
            var node=start?road.startJunction:road.endJunction;if(!node)return;
            // Materialize the current endpoint, not its old position before the node moved.
            LTRoadPoint? point=null;
            if(node.Authored)try{node.Endpoint(road,start,out var end,out _);point=end;}catch(ArgumentException){}
            if(!preview)Undo.RecordObject(road,"Отключить дорогу от въезда");
            if(point.HasValue&&road.points!=null&&road.points.Count>0)road.points[start?0:road.points.Count-1]=point.Value;
            if(start){road.startJunction=null;road.startJunctionSocket=null;}else{road.endJunction=null;road.endJunctionSocket=null;}
            if(!preview)Changed(road,node);
        }
        static void Changed(LTRoad road,LTRoadJunction node)
        {
            EditorUtility.SetDirty(road);PrefabUtility.RecordPrefabInstancePropertyModifications(road);EditorSceneManager.MarkSceneDirty(road.gameObject.scene);
            LTRoadBakeQueue.Queue(road);LTRoadJunctionBakeQueue.Queue(node);SceneView.RepaintAll();
        }
    }
}
