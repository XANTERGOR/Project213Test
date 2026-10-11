using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace LocalTerrainPrototype
{
    [CustomEditor(typeof(LTRoadJunction))]
    public sealed class LTRoadJunctionEditor : Editor
    {
        LTRoad connectRoad;bool connectStart=true;LTRoadJunctionSocket connectSocket;bool advanced,manual;
        LTRoadJunction Node=>(LTRoadJunction)target;
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using(new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if(Node.transform.parent&&!Node.GetComponentsInChildren<MeshRenderer>(true).Any()&&LTRoadJunctionSetup.HasModel(Node.transform.parent.gameObject)&&
                    !Node.transform.parent.GetComponent<LTRoadJunction>())
                {
                    if(GUILayout.Button("Собрать один объект на родительской модели (Undo)"))
                    {
                        try{LTRoadJunctionSetup.Configure(Node.transform.parent.gameObject);GUIUtility.ExitGUI();}
                        catch(ExitGUIException){throw;}
                        catch(Exception e){Node.status="Ошибка: "+e.Message;}
                    }
                }
                EditorGUILayout.HelpBox("Один объект: Road Junction на корне модели, меши/LODGroup и Port внутри. Выберите дорогу → редактирование точек в Scene → подведите её конец к въезду → Enter. Стык проверяется перед подключением.",MessageType.Info);
                void Field(string key,string label)=>EditorGUILayout.PropertyField(serializedObject.FindProperty(key),new GUIContent(label),true);
                EditorGUI.BeginChangeCheck();
                var source=serializedObject.FindProperty("meshSource");
                source.intValue=EditorGUILayout.IntPopup("Геометрия",source.intValue,new[]{"Меши этого объекта (один объект)","Отдельный префаб (старый режим)","Процедурная площадка"},new[]{2,1,0});
                bool authored=source.intValue!=0,own=source.intValue==2;
                var surface=serializedObject.FindProperty("surface");
                if(authored)surface.intValue=(int)LTRoadMode.Asphalt;
                else surface.intValue=EditorGUILayout.IntPopup("Покрытие площадки",surface.intValue,new[]{"Offroad","Asphalt"},new[]{0,1});
                if(authored&&!own)Field("junctionPrefab","Префаб с въездами и LOD");
                if(authored)
                {
                    var fit=serializedObject.FindProperty("connectionFit");
                    fit.intValue=EditorGUILayout.IntPopup("Стык дороги",fit.intValue,new[]{"Строгий Fuse","Подгонка профиля (без смены сетки)"},new[]{0,1});
                    if(fit.intValue==1)
                    {
                        Field("profileBlendLength","Длина подгонки, м");Field("profileMaxShift","Максимальный сдвиг, м");
                        Field("profileSearchBand","Полоса поиска торца, м");
                        EditorGUILayout.HelpBox("Подгоняет верх и боковины, плавно деформирует концевой сегмент. Нижние заглушки могут отличаться. Число опор верха/боковин должно совпадать; треугольники и UV не меняются. Все авторские LOD проверяются, перевёрнутые грани запрещены. Переход должен помещаться в прямой въезд и концевой сегмент дороги.",MessageType.Info);
                    }
                }
                Field("surfaceOffset","Высота посадки, м");
                if(authored)
                {
                    Field("meshDepth","Заглубление меша, м");
                    EditorGUILayout.HelpBox("Больше заглубление — ниже асфальт; основание terrain не опускается. Начните с 0.05 м. В режиме подгонки стык согласуется в пределах максимального сдвига. Строгому Fuse нужны совпадающие высоты торцов — учитывайте «Уровень покрытия в модуле» у дороги.",MessageType.Info);
                }
                Field("neckLength","Прямой въезд, м");
                bool meshFoundation=false;
                if(authored)
                {
                    var foundation=serializedObject.FindProperty("foundationMode");
                    foundation.intValue=EditorGUILayout.IntPopup("Форма основания",foundation.intValue,new[]{"Круг (старый режим)","По мешу LOD0"},new[]{0,1});
                    meshFoundation=foundation.intValue==1;
                }
                if(meshFoundation)Field("foundationShoulder","Обочина от края меша, м");
                else Field("radius",authored?"Зона основания, радиус, м":"Радиус въездов, м");
                Field("blendWidth","Переход к рельефу, м");Field("clearVegetation","Убирать растительность");
                if(surface.intValue==(int)LTRoadMode.Asphalt)
                {
                    Field("simplifyTerrainUnderAsphalt","Упрощать землю под асфальтом");
                    if(serializedObject.FindProperty("simplifyTerrainUnderAsphalt").boolValue)
                    {
                        Field("terrainLODEdgeError","Ошибка земли у края LOD, м");
                        EditorGUILayout.HelpBox("Проверяются все запечённые LOD перекрёстка. Ровная земля под покрытием, у краёв и отверстий упрощается; неровная ограничена ошибкой у края. LOD0 и коллайдер terrain не меняются. Без готовых читаемых мешей область остаётся защищённой. После перестройки — Apply Pending Changes у LT World.",MessageType.Info);
                    }
                }
                if(meshFoundation)
                {
                    Field("coordinateShoulders","Согласовать обочины дорог");
                    if(serializedObject.FindProperty("coordinateShoulders").boolValue)
                    {
                        Field("shoulderJoinLength","Длина согласования, м");
                        EditorGUILayout.HelpBox("Обочина и переход дороги подстраиваются к основанию перекрёстка только у подключённых въездов. Длина ограничена прямым въездом. Меши асфальта, UV и LOD не растягиваются. Для покраски без цветового шва используйте один и тот же слой грунта. После перестройки — Apply Pending Changes у LT World.",MessageType.Info);
                    }
                }
                if(meshFoundation)EditorGUILayout.HelpBox("Основание, грунт и удаление травы повторяют проекцию LOD0, включая выемки и сквозные отверстия. Обочина расширяет контур, переход плавно возвращает исходный рельеф. После перестройки примените Apply Pending Changes у LT World. Высота основания остаётся общей для всех въездов.",MessageType.Info);
                if(authored)
                {
                    Field("groundBlend","Бленд с грунтом");
                    if(serializedObject.FindProperty("groundBlend").boolValue)
                    {
                        var ground=serializedObject.FindProperty("groundBlendSource");
                        ground.intValue=EditorGUILayout.IntPopup("Источник грунта",ground.intValue,new[]{"Один слой","Слои terrain под перекрёстком"},new[]{0,1});
                        if(ground.intValue==0)Field("groundBlendLayer","Слой для бленда");
                        Field("groundBlendWidth","Бленд внешнего края, м");Field("groundBlendHoleWidth","Бленд вокруг дырок, м");
                        Field("groundBlendStrength","Сила бленда");Field("groundBlendNoise","Неровность бленда");
                        Field("groundBlendNoiseSize","Размер пятен, м");Field("groundBlendSeed","Seed бленда");Field("groundBlendMaskCell","Шаг маски бленда, м");
                        EditorGUILayout.HelpBox("Грунт подмешивается на самом асфальте, не перекрашивая terrain. Маска общая для всех LOD. Подключённые дороги продолжают контур: въезд не получает поперечную полосу грунта. Для одинакового перехода задайте одинаковые параметры бленда у дороги и перекрёстка.",MessageType.Info);
                    }
                }
                advanced=EditorGUILayout.Foldout(advanced,"Расширенные настройки",true);
                if(advanced)
                {
                    if(authored)Field("socketTolerance","Допуск Fuse, м");
                    Field("terrainCellSize","Ячейка terrain, м");Field("groundLayer","Слой центрального грунта");
                    Field("asphaltMaterial",authored?"Замена материалов (необязательно)":"Материал площадки");
                    if(!authored)Field("textureRepeatMetres","Повтор текстуры, м");
                    Field("lods","Дистанции LOD, м");Field("clearStones","Убирать камни");
                    EditorGUILayout.HelpBox("Готовые LOD сохраняются, переключаются по метрам, не процентам LODGroup. Строгий Fuse не меняет треугольники. Основание не меняется при переключении LOD.",MessageType.Info);
                }
                if(EditorGUI.EndChangeCheck()){serializedObject.ApplyModifiedProperties();LTRoadJunctionBakeQueue.Queue(Node);}
                if(Node.Authored&&Node.groundBlend)
                using(new EditorGUI.DisabledScope(LTRoadGraphAdapter.Busy||EditorApplication.isCompiling||EditorApplication.isUpdating))
                if(GUILayout.Button("Обновить адаптер бленда"))
                    EditorApplication.delayCall+=()=>
                    {
                        if(!this||!target)return;
                        try{Node.status=LTRoadGraphAdapter.UpdateAdapter();}
                        catch(Exception e){Node.status="Ошибка: "+e.Message;Debug.LogError("[Road Graph Adapter] "+e);}
                        Repaint();SceneView.RepaintAll();
                    };
                if(Node.Authored&&!Node.OwnHierarchy&&Node.junctionPrefab&&GUILayout.Button("Открыть префаб для настройки въездов"))AssetDatabase.OpenAsset(Node.junctionPrefab);
                if(Node.OwnHierarchy&&!Node.World)EditorGUILayout.HelpBox("В префабе настраиваются меши, LOD и Port. Для подключения дорог поместите этот объект внутрь LTWorld в сцене.",MessageType.Info);
                EditorGUILayout.Space();EditorGUILayout.LabelField("Въезды и подключения",EditorStyles.boldLabel);
                if(Node.Authored)foreach(var socket in Node.Sockets)
                {
                    var roads=Node.Roads().ToArray();bool occupied=roads.Any(r=>r.startJunction==Node&&r.startJunctionSocket==socket||r.endJunction==Node&&r.endJunctionSocket==socket);
                    using(new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(socket.name+$" · {socket.width:G4} м · "+(occupied?"занят":"свободен"));
                        if(Node.OwnHierarchy&&GUILayout.Button("Показать",GUILayout.Width(75)))Selection.activeGameObject=socket.gameObject;
                    }
                }
                foreach(var road in Node.Roads().ToArray())
                {
                    if(road.startJunction==Node)Connection(road,true);
                    if(road.endJunction==Node)Connection(road,false);
                }
                manual=EditorGUILayout.Foldout(manual,"Подключить через список",true);
                if(manual)
                {
                connectRoad=(LTRoad)EditorGUILayout.ObjectField("Дорога",connectRoad,typeof(LTRoad),true);
                connectStart=EditorGUILayout.Popup("Подключаемый конец",connectStart?0:1,new[]{"Начало","Конец"})==0;
                if(Node.Authored)connectSocket=SelectSocket(Node,connectSocket,"Въезд префаба");
                using(new EditorGUI.DisabledScope(!connectRoad))if(GUILayout.Button("Подключить к узлу"))
                {
                    if(LTRoadMath.IsTerrainAsphalt(connectRoad.mode))Node.status="Terrain Asphalt (experimental): перекрёстки пока не поддерживаются.";
                    else if(connectRoad.World!=Node.World)Node.status="Дорога и узел должны быть в одном LTWorld.";
                    else if(connectStart?connectRoad.startJunction&&connectRoad.startJunction!=Node:connectRoad.endJunction&&connectRoad.endJunction!=Node)
                        Node.status="Этот конец уже подключён. Сначала отключите его от прежнего узла.";
                    else if(Node.Authored&&(!connectSocket||Node.Roads().Any(r=>r.startJunction==Node&&r.startJunctionSocket==connectSocket||r.endJunction==Node&&r.endJunctionSocket==connectSocket)))
                        Node.status="Выберите свободный въезд префаба.";
                    else
                    {
                        try{if(Node.Authored)LTRoadJunctionConnection.Connect(connectRoad,connectStart,Node,connectSocket);else SetConnection(connectRoad,connectStart,Node);connectRoad=null;}
                        catch(Exception e){Node.status="Ошибка: "+e.Message;}
                    }
                }
                }
                using(new EditorGUI.DisabledScope(!Node.World||EditorUtility.IsPersistent(Node)||PrefabStageUtility.GetPrefabStage(Node.gameObject)!=null))
                {
                if(GUILayout.Button("Прижать центр к исходному terrain"))
                {
                    if(Node.World&&Node.World.source)
                    {
                        var world=Node.World;var p=world.transform.InverseTransformPoint(Node.transform.position);
                        p.y=LTEditorEngine.CreateHeightSamplerForRoad(world)(p.x,p.z);Undo.RecordObject(Node.transform,"Высота перекрёстка");
                        Node.transform.position=world.transform.TransformPoint(p);LTRoadJunctionBakeQueue.Queue(Node);
                    }
                }
                if(GUILayout.Button("Перестроить / запечь перекрёсток"))LTRoadJunctionBakeQueue.BakeNow(Node,true);
                }
            }
            if(!string.IsNullOrEmpty(Node.status))EditorGUILayout.HelpBox(Node.status,Node.status.StartsWith("Ошибка:")?MessageType.Error:MessageType.Info);
        }
        void Connection(LTRoad road,bool start)
        {
            using(new EditorGUILayout.HorizontalScope())
            {
                var socket=start?road.startJunctionSocket:road.endJunctionSocket;
                EditorGUILayout.ObjectField((start?"Начало":"Конец")+(Node.Authored?" → "+(socket?socket.name:"не выбран въезд"):""),road,typeof(LTRoad),true);
                if(GUILayout.Button("Отключить",GUILayout.Width(90)))LTRoadJunctionConnection.Disconnect(road,start);
            }
        }
        void SetConnection(LTRoad road,bool start,LTRoadJunction node)
        {
            Undo.RecordObject(road,"Подключение перекрёстка");if(start)road.startJunction=node;else road.endJunction=node;
            if(start)road.startJunctionSocket=node&&node.Authored?connectSocket:null;else road.endJunctionSocket=node&&node.Authored?connectSocket:null;
            EditorUtility.SetDirty(road);PrefabUtility.RecordPrefabInstancePropertyModifications(road);
            LTRoadBakeQueue.Queue(road);LTRoadJunctionBakeQueue.Queue(Node);SceneView.RepaintAll();
        }
        static LTRoadJunctionSocket SelectSocket(LTRoadJunction node,LTRoadJunctionSocket selected,string label)
        {
            var sockets=node.Sockets;int current=Array.IndexOf(sockets,selected)+1;
            var names=new[]{"Не выбран"}.Concat(sockets.Select(s=>AnimationUtility.CalculateTransformPath(s.transform,node.SourceRoot.transform)+$" ({s.width:G4} м)")).ToArray();
            int next=EditorGUILayout.Popup(label,current,names);return next==0?null:sockets[next-1];
        }
        internal static void SocketField(SerializedProperty property,LTRoadJunction node,string label)
        {
            EditorGUI.BeginChangeCheck();var value=SelectSocket(node,property.objectReferenceValue as LTRoadJunctionSocket,label);
            if(EditorGUI.EndChangeCheck())property.objectReferenceValue=value;
        }
        [DrawGizmo(GizmoType.Selected|GizmoType.NonSelected|GizmoType.Pickable)]
        static void Draw(LTRoadJunction node,GizmoType type)
        {
            if(!node.isActiveAndEnabled||!node.World)return;
            var old=Gizmos.color;Gizmos.color=new Color(.1f,.9f,1);Gizmos.DrawWireSphere(node.transform.position,1);
            try
            {
                if(node.Authored)
                    foreach(var socket in node.Sockets)
                    {
                        var m=node.World.transform.localToWorldMatrix*node.SocketMatrix(socket);var p=m.MultiplyPoint3x4(Vector3.zero)+Vector3.up*node.MeshSurfaceOffset;
                        Gizmos.DrawLine(p-m.MultiplyVector(Vector3.right)*socket.width*.5f,p+m.MultiplyVector(Vector3.right)*socket.width*.5f);
                        Gizmos.DrawRay(p,m.MultiplyVector(Vector3.forward)*2);Handles.Label(p,socket.name);
                    }
                var data=node.Capture();var matrix=node.World.transform.localToWorldMatrix;
                if(data.footprint!=null)LTRoadJunctionFoundationGizmos.Draw(node,data,matrix);
                for(int i=0;i<data.outline.Length;i++)
                {var a=data.outline[i];var b=data.outline[(i+1)%data.outline.Length];Gizmos.DrawLine(matrix.MultiplyPoint3x4(new Vector3(a.x,data.centre.y,a.y)),matrix.MultiplyPoint3x4(new Vector3(b.x,data.centre.y,b.y)));}
            }
            catch(ArgumentException){}
            finally{Gizmos.color=old;}
        }
        [MenuItem("GameObject/Local Terrain/Road Junction",false,17)]
        static void Create(MenuCommand command)
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var selected=command.context as GameObject;if(!selected)selected=Selection.activeGameObject;
            var world=selected?selected.GetComponentInParent<LTWorld>():null;
            if(!world||!world.source||EditorUtility.IsPersistent(world)||PrefabStageUtility.GetPrefabStage(world.gameObject)!=null)
            {EditorUtility.DisplayDialog("Перекрёсток","Выберите LTWorld в обычной сцене или его дочерний объект.","OK");return;}
            var view=SceneView.lastActiveSceneView;var p=world.transform.InverseTransformPoint(view?view.pivot:world.transform.position+world.source.size*.5f);
            var extent=world.TerrainRect;p.x=Mathf.Clamp(p.x,extent.xMin,extent.xMax);p.z=Mathf.Clamp(p.z,extent.yMin,extent.yMax);p.y=LTEditorEngine.CreateHeightSamplerForRoad(world)(p.x,p.z);
            var go=new GameObject("Road Junction");Undo.RegisterCreatedObjectUndo(go,"Создать перекрёсток");Undo.SetTransformParent(go.transform,world.transform,"Создать перекрёсток");
            go.transform.localPosition=p;go.transform.localRotation=Quaternion.identity;go.transform.localScale=Vector3.one;
            Undo.AddComponent<LTRoadJunction>(go);Selection.activeGameObject=go;if(view)view.drawGizmos=true;
        }
    }
    [InitializeOnLoad]
    public static class LTRoadJunctionBakeQueue
    {
        sealed class State{public string input;public bool pending;public double due;}
        static readonly Dictionary<LTRoadJunction,State> states=new Dictionary<LTRoadJunction,State>();
        static double next;
        static LTRoadJunction[] Nodes()=>UnityEngine.Object.FindObjectsByType<LTRoadJunction>(FindObjectsInactive.Include,FindObjectsSortMode.None)
            .Where(n=>n.gameObject.scene.IsValid()&&n.gameObject.scene.isLoaded&&!EditorUtility.IsPersistent(n)&&!EditorSceneManager.IsPreviewScene(n.gameObject.scene)&&PrefabStageUtility.GetPrefabStage(n.gameObject)==null).ToArray();
        static LTRoadJunctionBakeQueue()
        {
            EditorApplication.update+=Tick;Undo.undoRedoPerformed+=()=>{foreach(var n in Nodes())Queue(n);};
            EditorSceneManager.sceneSaving+=(scene,path)=>{foreach(var n in Nodes())if(n.gameObject.scene==scene&&NeedsBake(n))BakeNow(n,false);};
            EditorApplication.playModeStateChanged+=s=>{if(s==PlayModeStateChange.ExitingEditMode)foreach(var n in Nodes())if(NeedsBake(n))BakeNow(n,false,true);if(s==PlayModeStateChange.EnteredEditMode)states.Clear();};
        }
        static string Input(LTRoadJunction n)
        {
            // Exclude generated ownership/status so publishing cannot queue another bake.
            var text=new StringBuilder();text.Append(n.EndpointHash).Append('|').Append(n.blendWidth).Append('|').Append(n.terrainCellSize)
                .Append('|').Append(n.coordinateShoulders).Append('|').Append(n.shoulderJoinLength)
                .Append('|').Append(n.groundLayer?n.groundLayer.GetInstanceID():0).Append('|').Append(n.asphaltMaterial?n.asphaltMaterial.GetInstanceID():0)
                .Append('|').Append(n.textureRepeatMetres).Append('|').Append(n.clearVegetation).Append('|').Append(n.clearStones)
                .Append('|').Append(n.generatedRoot?n.generatedRoot.GetInstanceID():0).Append('|').Append(LTRoadJunctionPrefab.DependencyKey(n))
                .Append(LTRoadGroundBlendBuild.DependencyKey(n));
            if(n.lods!=null)foreach(var lod in n.lods)text.Append('|').Append(JsonUtility.ToJson(lod));
            foreach(var road in n.Roads())
            {
                text.Append('|').Append(road.GetInstanceID()).Append(':');
                try{text.Append(road.Capture(n.World).geometryHash);}catch(ArgumentException e){text.Append(e.Message);}
                text.Append(':').Append(LTRoadModuleSource.DependencyKey(road)).Append(':').Append((int)road.asphaltSource);
                text.Append(':').Append((int)road.moduleAxis).Append(':').Append(road.moduleFitWidth).Append(':').Append(road.moduleBaseY)
                    .Append(':').Append(road.moduleLength).Append(':').Append((int)road.asphaltLODMode)
                    .Append(':').Append(road.asphaltMaterial?road.asphaltMaterial.GetInstanceID():0);
                text.Append(':').Append(road.moduleRandomSeed).Append(':').Append(road.moduleAvoidRepeat).Append(':').Append(road.meshChunkLength)
                    .Append(':').Append(road.moduleFuseEnds).Append(':').Append(road.moduleFuseTolerance).Append(':').Append(road.moduleFusePlaneTolerance)
                    .Append(':').Append(road.moduleAutomaticJoints).Append(':').Append(road.moduleJointWidth).Append(':').Append(road.moduleJointMaxChange).Append(':').Append(road.moduleJointPrecision)
                    .Append(':').Append(road.moduleFitEnds).Append(':').Append(road.moduleEndBlendLength).Append(':').Append(road.moduleEndMaxMismatch)
                    .Append(':').Append(road.moduleRestoreLODEnds).Append(':').Append(road.moduleLODEndWidth);
                if(road.moduleVariants!=null)foreach(var variant in road.moduleVariants)text.Append(':').Append(JsonUtility.ToJson(variant));
                if(road.moduleMaterials!=null)foreach(var material in road.moduleMaterials)text.Append(':').Append(material?material.GetInstanceID():0);
                if(road.asphaltLODs!=null)foreach(var lod in road.asphaltLODs)text.Append(':').Append(JsonUtility.ToJson(lod));
            }
            return text.ToString();
        }
        static State Get(LTRoadJunction n)
        {if(!states.TryGetValue(n,out var s)){s=new State{input=Input(n),pending=n.OwnHierarchy&&!n.OwnsOutput&&n.isActiveAndEnabled,due=EditorApplication.timeSinceStartup+.45};states.Add(n,s);}return s;}
        static bool NeedsBake(LTRoadJunction n){var s=Get(n);return s.pending||s.input!=Input(n);}
        static bool Editable(LTRoadJunction n)=>n&&n.World&&n.gameObject.scene.IsValid()&&!EditorUtility.IsPersistent(n)&&
            !EditorSceneManager.IsPreviewScene(n.gameObject.scene)&&PrefabStageUtility.GetPrefabStage(n.gameObject)==null;
        public static void Queue(LTRoadJunction n){if(!Editable(n))return;var s=Get(n);s.input=Input(n);s.pending=true;s.due=EditorApplication.timeSinceStartup+.45;}
        static void Tick()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating||GUIUtility.hotControl!=0||EditorApplication.timeSinceStartup<next)return;
            next=EditorApplication.timeSinceStartup+.25;
            foreach(var dead in states.Keys.Where(n=>!n).ToArray())states.Remove(dead);
            foreach(var n in Nodes()){if(!Editable(n))continue;var s=Get(n);if(s.input!=Input(n))Queue(n);if(s.pending&&EditorApplication.timeSinceStartup>=s.due)BakeNow(n,false);}
        }
        public static void BakeNow(LTRoadJunction n,bool explicitBake,bool enteringPlay=false)
        {
            if(!Editable(n)||Application.isPlaying||(!enteringPlay&&EditorApplication.isPlayingOrWillChangePlaymode))return;
            var state=Get(n);
            try
            {
                if(!n.isActiveAndEnabled){if(n.OwnsOutput)n.generatedRoot.gameObject.SetActive(false);SetSourceVisibility(n,false,explicitBake);return;}
                var data=n.Capture();var world=n.World;
                if(!world.source)throw new ArgumentException("Нужен исходный terrain LTWorld.");
                if(n.surface==LTRoadMode.Offroad&&!n.groundLayer)throw new ArgumentException("Назначьте слой центрального грунта.");
                var connected=n.Roads().ToArray();
                var authored=n.Authored?LTRoadJunctionPrefab.Build(n):null;
                var roadSources=new Dictionary<LTRoad,LTRoadModuleSource.Prepared>();
                var boundary=new List<Vector3>();
                foreach(var road in connected)if(road.mode==LTRoadMode.Asphalt)
                {
                    if(road.asphaltSource==LTRoadMeshSource.ProceduralRibbon&&!road.asphaltMaterial)
                        throw new ArgumentException(road.name+": назначьте материал дороги или сначала явно запеките её для создания материала.");
                    var roadSource=LTRoadModuleSource.Build(road,road.Capture(world));
                    LTRoadJunctionMesh.ValidateRoadEnds(road,roadSource,n,boundary);
                    roadSources.Add(road,roadSource);
                    // Catch unsupported/stale blend adapters before replacing the junction.
                    if(n.Authored){using(var blend=LTRoadGroundBlendBuild.Prepare(road,roadSource)){} }
                }
                if(!n.Authored)data=LTRoadJunctionMath.WithBoundaryPoints(data,boundary);
                using var groundBlend=n.Authored?LTRoadGroundBlendBuild.Prepare(n,authored,roadSources):null;
                if(n.surface==LTRoadMode.Asphalt)
                {
                    if(!n.Authored&&!n.asphaltMaterial)throw new ArgumentException("Назначьте материал асфальтовой площадки.");
                    if(n.generatedRoot&&!n.OwnsOutput)throw new ArgumentException("Чужой generatedRoot не изменён: ссылка не принадлежит этому узлу.");
                    var matrix=n.transform.worldToLocalMatrix*world.transform.localToWorldMatrix;
                    int levels=1+Math.Min(3,n.lods?.Length??0),rings=Mathf.Clamp(Mathf.CeilToInt(data.coreRadius/3),1,8);
                    var prepared=authored?.levels??new LTRoadMesh.Chunk[levels];
                    if(authored==null)for(int level=0;level<levels;level++)prepared[level]=LTRoadJunctionMesh.Build(data,Math.Max(1,rings>>level),n.textureRepeatMetres,n.surfaceOffset,matrix);
                    if(explicitBake)Undo.RecordObject(n,"Запечь перекрёсток");
                    const string parent="Assets/LocalTerrainRoads";
                    if(!AssetDatabase.IsValidFolder(parent))AssetDatabase.CreateFolder("Assets","LocalTerrainRoads");
                    if(!Guid.TryParseExact(n.bakeId,"N",out _)||Nodes().Any(other=>other!=n&&other.bakeId==n.bakeId))n.bakeId=Guid.NewGuid().ToString("N");
                    string folder=parent+"/Junction_"+n.bakeId;if(!AssetDatabase.IsValidFolder(folder))AssetDatabase.CreateFolder(parent,"Junction_"+n.bakeId);
                    if(groundBlend!=null)LTRoadGroundBlendBuild.Save(n,groundBlend,folder);
                    var meshes=prepared.Select(p=>LTRoadBakeQueue.SaveChunk(p,folder)).ToArray();
                    if(!n.generatedRoot)
                    {
                        var go=new GameObject("Junction Generated");go.transform.SetParent(n.transform,false);go.AddComponent<LTRoadJunctionGenerated>().owner=n;
                        if(explicitBake)Undo.RegisterCreatedObjectUndo(go,"Запечь перекрёсток");n.generatedRoot=go.transform;
                    }
                    var root=n.generatedRoot;
                    if(explicitBake){Undo.RecordObject(root,"Запечь перекрёсток");Undo.RecordObject(root.gameObject,"Запечь перекрёсток");}
                    root.localPosition=Vector3.zero;root.localRotation=Quaternion.identity;root.localScale=Vector3.one;
                    T Component<T>() where T:Component
                    {var c=root.GetComponent<T>();if(!c)c=explicitBake?Undo.AddComponent<T>(root.gameObject):root.gameObject.AddComponent<T>();if(explicitBake)Undo.RecordObject(c,"Запечь перекрёсток");return c;}
                    var filter=Component<MeshFilter>();var renderer=Component<MeshRenderer>();var collider=Component<MeshCollider>();var lod=Component<LTRoadLOD>();
                    filter.sharedMesh=meshes[0];renderer.sharedMaterials=groundBlend?.materials??authored?.materials??new[]{n.asphaltMaterial};renderer.enabled=true;
                    if(collider.sharedMesh!=meshes[0]){collider.sharedMesh=null;collider.sharedMesh=meshes[0];}collider.enabled=true;
                    lod.junction=n;lod.owner=null;lod.meshes=meshes;lod.bounds=meshes[0].bounds;lod.current=0;
                    root.gameObject.SetActive(true);SetSourceVisibility(n,n.OwnHierarchy,explicitBake);
                    var binding=root.GetComponent<LTRoadGroundBlend>();
                    if(groundBlend!=null)
                    {
                        binding=Component<LTRoadGroundBlend>();binding.owner=null;binding.junction=n;
                        binding.roadToMask=groundBlend.roadToMask;binding.enabled=true;binding.Refresh();
                    }
                    else if(binding){if(explicitBake)Undo.RecordObject(binding,"Бленд перекрёстка");binding.enabled=false;}
                    if(binding){EditorUtility.SetDirty(binding);PrefabUtility.RecordPrefabInstancePropertyModifications(binding);}
                    foreach(var c in new Component[]{filter,renderer,collider,lod}){EditorUtility.SetDirty(c);PrefabUtility.RecordPrefabInstancePropertyModifications(c);}
                    world.RefreshLODCache();EditorUtility.SetDirty(n);PrefabUtility.RecordPrefabInstancePropertyModifications(n);
                    EditorSceneManager.MarkSceneDirty(n.gameObject.scene);
                }
                else{if(n.OwnsOutput)n.generatedRoot.gameObject.SetActive(false);SetSourceVisibility(n,false,explicitBake);}
                if(explicitBake)
                {
                    foreach(var road in connected)
                    {LTRoadBakeQueue.BakeNow(road,false);var error=LTRoadBakeQueue.Error(road);if(!string.IsNullOrEmpty(error))throw new InvalidOperationException(road.name+": "+error);}
                    LTEditorEngine.Refresh(world,false,false);
                }
                world.GetComponent<LTDetailRenderer>()?.RequestSurfaceRefresh();
                n.status=$"Готово: {connected.Length} дорог. "+(authored!=null?$"Префаб: {authored.levels.Length} LOD, {n.Sockets.Length} въездов; "+string.Join(" / ",authored.levels.Select((m,l)=>$"LOD{l}: {m.triangles.Length/3:N0} tris"))+". ":"")+
                    (groundBlend!=null?$"Бленд: {groundBlend.mask.width}×{groundBlend.mask.height}, одна маска на все LOD. ":"")+"Меши сохраняются в Assets/LocalTerrainRoads; сцену сохраняйте обычным способом.";
            }
            catch(Exception error){n.status="Ошибка: "+error.Message;if(!n.Authored&&n.OwnsOutput)n.generatedRoot.gameObject.SetActive(false);}
            finally{state.input=Input(n);state.pending=false;SceneView.RepaintAll();}
        }
        static void SetSourceVisibility(LTRoadJunction n,bool suppress,bool undo)
        {
            var objects=new List<UnityEngine.Object>{n};
            objects.AddRange(n.sourceRenderers.Select(s=>(UnityEngine.Object)s.target));objects.AddRange(n.sourceLODGroups.Select(s=>(UnityEngine.Object)s.target));objects.AddRange(n.sourceColliders.Select(s=>(UnityEngine.Object)s.target));
            if(n.OwnHierarchy)objects.AddRange(n.GetComponentsInChildren<Component>(true).Where(c=>(c is Renderer||c is Collider||c is LODGroup)&&n.SourceMember(c.transform)));
            var live=objects.Where(o=>o).Distinct().ToArray();if(undo)Undo.RecordObjects(live,"Отображение перекрёстка");
            n.SetSourceSuppressed(suppress);
            foreach(var o in live){EditorUtility.SetDirty(o);PrefabUtility.RecordPrefabInstancePropertyModifications(o);}
        }
    }
}
