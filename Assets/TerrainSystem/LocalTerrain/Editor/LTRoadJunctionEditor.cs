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
        LTRoad connectRoad;bool connectStart=true;
        LTRoadJunction Node=>(LTRoadJunction)target;
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using(new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUILayout.HelpBox("Явный узел: подключите 2–8 концов дорог. Положение узла задаёт общую высоту; вторая/предпоследняя точка дороги — направление въезда. Простое пересечение сплайнов ничего не соединяет.",MessageType.Info);
                void Field(string key,string label)=>EditorGUILayout.PropertyField(serializedObject.FindProperty(key),new GUIContent(label),true);
                EditorGUI.BeginChangeCheck();
                Field("surface","Покрытие площадки");Field("radius","Радиус въездов, м");Field("neckLength","Прямой въезд, м");
                Field("blendWidth","Переход к рельефу, м");Field("terrainCellSize","Ячейка terrain, м");
                Field("groundLayer","Слой центрального грунта");
                EditorGUILayout.HelpBox("Центру нужен отдельный слой с ненаправленной текстурой. Ветки грунтовок могут использовать один общий Spline-слой. Максимум остаётся 12 слоёв на чанк, включая центр и фон.",MessageType.None);
                if(serializedObject.FindProperty("surface").enumValueIndex==(int)LTRoadMode.Asphalt)
                {
                    Field("asphaltMaterial","Материал площадки");Field("surfaceOffset","Над terrain, м");
                    Field("textureRepeatMetres","Повтор текстуры, м");Field("lods","LOD площадки");
                    EditorGUILayout.HelpBox("Ровная асфальтовая площадка без разметки. Все асфальтовые въезды должны иметь тот же Surface Offset и плоские торцы. Модуль с бордюром/толщиной подключайте через отдельный плоский переходник.",MessageType.Info);
                }
                Field("clearVegetation","Убирать растительность");Field("clearStones","Убирать камни");
                if(EditorGUI.EndChangeCheck()){serializedObject.ApplyModifiedProperties();LTRoadJunctionBakeQueue.Queue(Node);}
                EditorGUILayout.Space();EditorGUILayout.LabelField("Подключения",EditorStyles.boldLabel);
                foreach(var road in Node.Roads().ToArray())
                {
                    if(road.startJunction==Node)Connection(road,true);
                    if(road.endJunction==Node)Connection(road,false);
                }
                connectRoad=(LTRoad)EditorGUILayout.ObjectField("Дорога",connectRoad,typeof(LTRoad),true);
                connectStart=EditorGUILayout.Popup("Подключаемый конец",connectStart?0:1,new[]{"Начало","Конец"})==0;
                using(new EditorGUI.DisabledScope(!connectRoad))if(GUILayout.Button("Подключить к узлу"))
                {
                    if(connectRoad.World!=Node.World)Node.status="Дорога и узел должны быть в одном LTWorld.";
                    else if(connectStart?connectRoad.startJunction&&connectRoad.startJunction!=Node:connectRoad.endJunction&&connectRoad.endJunction!=Node)
                        Node.status="Этот конец уже подключён. Сначала отключите его от прежнего узла.";
                    else{SetConnection(connectRoad,connectStart,Node);connectRoad=null;}
                }
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
            if(!string.IsNullOrEmpty(Node.status))EditorGUILayout.HelpBox(Node.status,Node.status.StartsWith("Ошибка:")?MessageType.Error:MessageType.Info);
        }
        void Connection(LTRoad road,bool start)
        {
            using(new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.ObjectField(start?"Начало":"Конец",road,typeof(LTRoad),true);
                if(GUILayout.Button("Отключить",GUILayout.Width(90)))SetConnection(road,start,null);
            }
        }
        void SetConnection(LTRoad road,bool start,LTRoadJunction node)
        {
            Undo.RecordObject(road,"Подключение перекрёстка");if(start)road.startJunction=node;else road.endJunction=node;
            EditorUtility.SetDirty(road);PrefabUtility.RecordPrefabInstancePropertyModifications(road);
            LTRoadBakeQueue.Queue(road);LTRoadJunctionBakeQueue.Queue(Node);SceneView.RepaintAll();
        }
        [DrawGizmo(GizmoType.Selected|GizmoType.NonSelected|GizmoType.Pickable)]
        static void Draw(LTRoadJunction node,GizmoType type)
        {
            if(!node.isActiveAndEnabled)return;
            var old=Gizmos.color;Gizmos.color=new Color(.1f,.9f,1);Gizmos.DrawWireSphere(node.transform.position,1);
            try
            {
                var data=node.Capture();var matrix=node.World.transform.localToWorldMatrix;
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
            p.x=Mathf.Clamp(p.x,0,world.source.size.x);p.z=Mathf.Clamp(p.z,0,world.source.size.z);p.y=LTEditorEngine.CreateHeightSamplerForRoad(world)(p.x,p.z);
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
            .Where(n=>n.gameObject.scene.IsValid()&&n.gameObject.scene.isLoaded&&!EditorUtility.IsPersistent(n)&&PrefabStageUtility.GetPrefabStage(n.gameObject)==null).ToArray();
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
                .Append('|').Append(n.groundLayer?n.groundLayer.GetInstanceID():0).Append('|').Append(n.asphaltMaterial?n.asphaltMaterial.GetInstanceID():0)
                .Append('|').Append(n.textureRepeatMetres).Append('|').Append(n.clearVegetation).Append('|').Append(n.clearStones)
                .Append('|').Append(n.generatedRoot?n.generatedRoot.GetInstanceID():0);
            if(n.lods!=null)foreach(var lod in n.lods)text.Append('|').Append(JsonUtility.ToJson(lod));
            foreach(var road in n.Roads())
            {
                text.Append('|').Append(road.GetInstanceID()).Append(':');
                try{text.Append(road.Capture(n.World).geometryHash);}catch(ArgumentException e){text.Append(e.Message);}
                text.Append(':').Append(LTRoadModuleSource.DependencyKey(road)).Append(':').Append((int)road.asphaltSource);
                text.Append(':').Append((int)road.moduleAxis).Append(':').Append(road.moduleFitWidth).Append(':').Append(road.moduleBaseY)
                    .Append(':').Append(road.moduleLength).Append(':').Append((int)road.asphaltLODMode)
                    .Append(':').Append(road.asphaltMaterial?road.asphaltMaterial.GetInstanceID():0);
                if(road.moduleMaterials!=null)foreach(var material in road.moduleMaterials)text.Append(':').Append(material?material.GetInstanceID():0);
                if(road.asphaltLODs!=null)foreach(var lod in road.asphaltLODs)text.Append(':').Append(JsonUtility.ToJson(lod));
            }
            return text.ToString();
        }
        static State Get(LTRoadJunction n)
        {if(!states.TryGetValue(n,out var s)){s=new State{input=Input(n)};states.Add(n,s);}return s;}
        static bool NeedsBake(LTRoadJunction n){var s=Get(n);return s.pending||s.input!=Input(n);}
        public static void Queue(LTRoadJunction n){if(!n)return;var s=Get(n);s.input=Input(n);s.pending=true;s.due=EditorApplication.timeSinceStartup+.45;}
        static void Tick()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating||GUIUtility.hotControl!=0||EditorApplication.timeSinceStartup<next)return;
            next=EditorApplication.timeSinceStartup+.25;
            foreach(var dead in states.Keys.Where(n=>!n).ToArray())states.Remove(dead);
            foreach(var n in Nodes()){var s=Get(n);if(s.input!=Input(n))Queue(n);if(s.pending&&EditorApplication.timeSinceStartup>=s.due)BakeNow(n,false);}
        }
        public static void BakeNow(LTRoadJunction n,bool explicitBake,bool enteringPlay=false)
        {
            if(!n||Application.isPlaying||(!enteringPlay&&EditorApplication.isPlayingOrWillChangePlaymode))return;
            var state=Get(n);
            try
            {
                if(!n.isActiveAndEnabled){if(n.OwnsOutput)n.generatedRoot.gameObject.SetActive(false);return;}
                var data=n.Capture();var world=n.World;
                if(!world.source)throw new ArgumentException("Нужен исходный terrain LTWorld.");
                if(n.surface==LTRoadMode.Offroad&&!n.groundLayer)throw new ArgumentException("Назначьте слой центрального грунта.");
                var connected=n.Roads().ToArray();
                var boundary=new List<Vector3>();
                foreach(var road in connected)if(road.mode==LTRoadMode.Asphalt)
                {
                    if(road.asphaltSource==LTRoadMeshSource.ProceduralRibbon&&!road.asphaltMaterial)
                        throw new ArgumentException(road.name+": назначьте материал дороги или сначала явно запеките её для создания материала.");
                    LTRoadJunctionMesh.ValidateRoadEnds(road,LTRoadModuleSource.Build(road,road.Capture(world)),n,boundary);
                }
                data=LTRoadJunctionMath.WithBoundaryPoints(data,boundary);
                if(n.surface==LTRoadMode.Asphalt)
                {
                    if(!n.asphaltMaterial)throw new ArgumentException("Назначьте материал асфальтовой площадки.");
                    if(n.generatedRoot&&!n.OwnsOutput)throw new ArgumentException("Чужой generatedRoot не изменён: ссылка не принадлежит этому узлу.");
                    var matrix=n.transform.worldToLocalMatrix*world.transform.localToWorldMatrix;
                    int levels=1+Math.Min(3,n.lods?.Length??0),rings=Mathf.Clamp(Mathf.CeilToInt(data.coreRadius/3),1,8);
                    var prepared=new LTRoadMesh.Chunk[levels];
                    for(int level=0;level<levels;level++)prepared[level]=LTRoadJunctionMesh.Build(data,Math.Max(1,rings>>level),n.textureRepeatMetres,n.surfaceOffset,matrix);
                    if(explicitBake)Undo.RecordObject(n,"Запечь перекрёсток");
                    const string parent="Assets/LocalTerrainRoads";
                    if(!AssetDatabase.IsValidFolder(parent))AssetDatabase.CreateFolder("Assets","LocalTerrainRoads");
                    if(!Guid.TryParseExact(n.bakeId,"N",out _)||Nodes().Any(other=>other!=n&&other.bakeId==n.bakeId))n.bakeId=Guid.NewGuid().ToString("N");
                    string folder=parent+"/Junction_"+n.bakeId;if(!AssetDatabase.IsValidFolder(folder))AssetDatabase.CreateFolder(parent,"Junction_"+n.bakeId);
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
                    filter.sharedMesh=meshes[0];renderer.sharedMaterial=n.asphaltMaterial;renderer.enabled=true;
                    if(collider.sharedMesh!=meshes[0]){collider.sharedMesh=null;collider.sharedMesh=meshes[0];}collider.enabled=true;
                    lod.junction=n;lod.owner=null;lod.meshes=meshes;lod.bounds=meshes[0].bounds;lod.current=0;
                    root.gameObject.SetActive(true);foreach(var c in new Component[]{filter,renderer,collider,lod}){EditorUtility.SetDirty(c);PrefabUtility.RecordPrefabInstancePropertyModifications(c);}
                    world.RefreshLODCache();EditorUtility.SetDirty(n);PrefabUtility.RecordPrefabInstancePropertyModifications(n);
                    EditorSceneManager.MarkSceneDirty(n.gameObject.scene);
                }
                else if(n.OwnsOutput)n.generatedRoot.gameObject.SetActive(false);
                if(explicitBake)
                {
                    foreach(var road in connected)
                    {LTRoadBakeQueue.BakeNow(road,false);var error=LTRoadBakeQueue.Error(road);if(!string.IsNullOrEmpty(error))throw new InvalidOperationException(road.name+": "+error);}
                    LTEditorEngine.Refresh(world,false,false);
                }
                world.GetComponent<LTDetailRenderer>()?.RequestSurfaceRefresh();
                n.status=$"Готово: {connected.Length} дорог. Меши и LOD сохраняются в Assets/LocalTerrainRoads; сцену сохраняйте обычным способом.";
            }
            catch(Exception error){n.status="Ошибка: "+error.Message;if(n.OwnsOutput)n.generatedRoot.gameObject.SetActive(false);}
            finally{state.input=Input(n);state.pending=false;SceneView.RepaintAll();}
        }
    }
}
