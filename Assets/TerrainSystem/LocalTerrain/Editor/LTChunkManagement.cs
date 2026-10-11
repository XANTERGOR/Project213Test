using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public static partial class LTEditorEngine
    {
        static void DetectChunkLayout(LTWorld world,State state)
        {
            using var layoutScope=chunkLayoutMarker.Auto();
            if(!world.Grid.Valid)throw new InvalidOperationException("Некорректный размер сетки чанков.");
            var chunks=state.chunkScan;
            // Still inspect live children each poll: no hierarchy/Undo event may
            // hide deletion, activation, replacement or direct coordinate edits.
            world.generatedRoot.GetComponentsInChildren(false,chunks);
            var probe=state.chunkProbe;probe.Begin(world.chunkOrigin.x,world.chunkOrigin.y,world.chunksX,world.chunksZ);
            foreach(var chunk in chunks)
            {
                int id=world.ChunkId(chunk.x,chunk.z);
                if(id<0)throw new InvalidOperationException($"Чанк ({chunk.x},{chunk.z}) вне области. Добавляйте чанки через управление сеткой.");
                if(!probe.Add(id,chunk.GetInstanceID()))throw new InvalidOperationException($"Повтор координат чанка ({chunk.x},{chunk.z}).");
            }
            if(state.chunkLayout!=null&&probe.Unchanged)return;
            string layout=world.chunkOrigin+":"+world.chunksX+":"+world.chunksZ+":"+
                string.Join(";",chunks.OrderBy(c=>world.ChunkId(c.x,c.z)).Select(c=>$"{c.x},{c.z}:{c.GetInstanceID()}"));
            if(state.chunkLayout==layout){probe.Accept();return;}
            CancelBuilds(state);state.revision++;state.initialized=false;state.chunkLayout=layout;
            state.liveChunks=new HashSet<int>(probe.ObservedIds);state.dirty.Clear();state.colliders.Clear();
            state.topologyInputs.Clear();state.topologyCache.Clear();
            state.balanceCache=new LTBalancedForest.InternalPlanCache();state.seamCache.Clear();
            state.boundaryCache=new BoundaryCache();state.rockOutputs.Clear();state.heightSamples.Clear();
            world.NotifyChunkLayoutChanged();
            probe.Accept();
        }
        public static void InvalidateChunkLayout(LTWorld world)
        {
            if(states.TryGetValue(world.GetInstanceID(),out var state))
            {CancelBuilds(state);state.chunkLayout=null;state.initialized=false;state.dirty.Clear();state.colliders.Clear();}
            world.NotifyChunkLayoutChanged();world.rockSeamBakePending=true;
        }
        internal static void ValidateChunkGridGeometry(LTWorld world)
        {
            if(!EditorSceneManager.IsPreviewScene(world.gameObject.scene))throw new InvalidOperationException("Preview scene required.");
            var state=new State();
            try{Detect(world,state,Capture(world));Rebuild(world,state,true);}
            finally{CancelBuilds(state);}
        }
    }

    public static class LTChunkManagement
    {
        public static Dictionary<Vector2Int,LTChunk> Read(LTWorld world)
        {
            if(!world||!world.source||!world.generatedRoot||!world.Grid.Valid)
                throw new InvalidOperationException("Нужен LTWorld с исходными данными и корнем чанков.");
            var result=new Dictionary<Vector2Int,LTChunk>();
            foreach(var c in world.generatedRoot.GetComponentsInChildren<LTChunk>(true))
            {
                if(!world.Grid.Contains(c.x,c.z))throw new InvalidOperationException($"Чанк ({c.x},{c.z}) вне сетки.");
                if(!result.TryAdd(new Vector2Int(c.x,c.z),c))throw new InvalidOperationException($"Повтор координат ({c.x},{c.z}).");
            }
            return result;
        }
        public static int Add(LTWorld world,IEnumerable<Vector2Int> coordinates)=>AddCore(world,coordinates,true);
        // Validation may use exactly the same operation, but only in an isolated preview scene.
        internal static int AddPreview(LTWorld world,IEnumerable<Vector2Int> coordinates)
        {
            if(!EditorSceneManager.IsPreviewScene(world.gameObject.scene))throw new InvalidOperationException("Preview scene required.");
            return AddCore(world,coordinates,false);
        }
        static int AddCore(LTWorld world,IEnumerable<Vector2Int> coordinates,bool persistent)
        {
            if(Application.isPlaying)throw new InvalidOperationException("Управление чанками доступно вне Play Mode.");
            var present=Read(world);var wanted=coordinates.Distinct().Where(p=>!present.ContainsKey(p)).ToArray();
            if(wanted.Length==0)return 0;
            var grid=world.Grid;foreach(var p in wanted)grid=grid.Include(p.x,p.y);
            // Validate storage BEFORE changing scene/layout. Existing assets are never overwritten.
            if(persistent&&(string.IsNullOrEmpty(world.outputFolder)||!world.outputFolder.StartsWith("Assets/",StringComparison.Ordinal)||!AssetDatabase.IsValidFolder(world.outputFolder)))
                throw new InvalidOperationException("Папка generated-ассетов отсутствует. Сначала восстановите Output Folder.");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Add / restore terrain chunks");
            Undo.RecordObject(world,"Extend terrain grid");
            world.fixedChunkSize=grid.cell;world.chunkOrigin=new Vector2Int(grid.minX,grid.minZ);world.chunksX=grid.columns;world.chunksZ=grid.rows;
            try
            {
              foreach(var p in wanted)
              {
                var go=new GameObject($"Chunk_{p.x}_{p.y}");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go,world.gameObject.scene);
                go.transform.SetParent(world.generatedRoot,false);
                Undo.RegisterCreatedObjectUndo(go,"Add terrain chunk");
                go.transform.localPosition=new Vector3(p.x*grid.cell.x,0,p.y*grid.cell.y);
                go.AddComponent<MeshFilter>();go.AddComponent<MeshRenderer>().sharedMaterial=world.material;go.AddComponent<MeshCollider>();
                var chunk=go.AddComponent<LTChunk>();chunk.x=p.x;chunk.z=p.y;
                chunk.mesh=new Mesh{name=go.name};
                // A removed chunk's old asset remains recoverable; Undo never deletes files.
                if(persistent)
                {
                    string path=AssetDatabase.GenerateUniqueAssetPath(world.outputFolder+"/"+go.name+".asset");
                    AssetDatabase.CreateAsset(chunk.mesh,path);
                }
                EditorUtility.SetDirty(chunk);
              }
              Changed(world);Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);return wanted.Length;
            }
            catch
            {
                // Files already created remain available for recovery; never remove assets in rollback.
                Undo.RevertAllDownToGroup(group);LTEditorEngine.InvalidateChunkLayout(world);throw;
            }
        }
        public static int RestoreMissing(LTWorld world)
        {
            var grid=world.Grid;var positions=new List<Vector2Int>();
            for(int i=0;i<grid.columns*grid.rows;i++)positions.Add(grid.Coordinate(i));
            return Add(world,positions);
        }
        public static bool Remove(LTWorld world,Vector2Int coordinate)
        {
            if(Application.isPlaying)throw new InvalidOperationException("Управление чанками доступно вне Play Mode.");
            var present=Read(world);if(!present.TryGetValue(coordinate,out var chunk))return false;
            // Generated chunks must not own authoring data or manually parented objects.
            if(chunk.transform.childCount!=0)throw new InvalidOperationException("В чанке есть дочерние объекты. Перенесите их перед удалением чанка.");
            var tile=world.ChunkRect(coordinate.x,coordinate.y);
            foreach(var stamp in world.GetComponentsInChildren<LTMeshStamp>())
            {
                if(!stamp.isActiveAndEnabled||!stamp.affectHeight||!stamp.cutTerrain||stamp.GetComponentInParent<LTWorld>()!=world)continue;
                // Closed rock bridges are a separate topology contract. Do not silently
                // tear one open, delete a stamp, or keep a stale floating transition.
                var mesh=stamp.renderAsset&&stamp.renderAsset.transitions!=null&&stamp.renderAsset.transitions.Length>0?
                    stamp.renderAsset.transitions[0]:stamp.generatedTrimmedMesh;
                if(!mesh)continue;
                var matrix=world.transform.worldToLocalMatrix*stamp.transform.localToWorldMatrix;
                var box=mesh.bounds;var min=new Vector2(float.PositiveInfinity,float.PositiveInfinity);var max=-min;
                for(int i=0;i<8;i++)
                {
                    var p=matrix.MultiplyPoint3x4(box.center+Vector3.Scale(box.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1)));
                    min=Vector2.Min(min,new Vector2(p.x,p.z));max=Vector2.Max(max,new Vector2(p.x,p.z));
                }
                if(tile.Overlaps(Rect.MinMaxRect(min.x,min.y,max.x,max.y)))
                    throw new InvalidOperationException($"Чанк несёт замкнутый стык меш-штампа «{stamp.name}». Перенесите или отключите этот штамп и примените изменения перед удалением чанка.");
            }
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Remove terrain chunk");
            Undo.DestroyObjectImmediate(chunk.gameObject);Changed(world);Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);return true;
        }
        static void Changed(LTWorld world)
        {
            LTEditorEngine.InvalidateChunkLayout(world);EditorUtility.SetDirty(world);
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);SceneView.RepaintAll();
        }
    }

    public sealed class LTChunkManagerWindow : EditorWindow
    {
        LTWorld world;Vector2 scroll;Vector2Int selected;string status;
        [MenuItem("Tools/Local Terrain/Manage Chunks")]
        static void Menu()=>Open(Selection.activeGameObject?Selection.activeGameObject.GetComponentInParent<LTWorld>():null);
        public static void Open(LTWorld world)
        {var window=GetWindow<LTChunkManagerWindow>("Чанки террейна");window.world=world;window.Show();}
        void OnEnable(){Undo.undoRedoPerformed+=Repaint;}
        void OnDisable(){Undo.undoRedoPerformed-=Repaint;}
        void Run(Action action)
        {try{action();status="Готово. Нажмите «Применить геометрию», если Auto Update выключен. Сцена не сохранена.";}catch(Exception e){status=e.Message;Debug.LogException(e,world);}}
        void OnGUI()
        {
            world=(LTWorld)EditorGUILayout.ObjectField("Мир",world,typeof(LTWorld),true);
            if(!world)return;
            Dictionary<Vector2Int,LTChunk> present;
            try{present=LTChunkManagement.Read(world);}catch(Exception e){EditorGUILayout.HelpBox(e.Message,MessageType.Error);return;}
            var grid=world.Grid;int missing=grid.columns*grid.rows-present.Count;
            EditorGUILayout.LabelField($"Чанки: {present.Count}; пустые ячейки: {missing}; область: {grid.columns}×{grid.rows}");
            EditorGUILayout.HelpBox("Пустая ячейка — допустимый край террейна. Добавление за границами расширяет область, не сдвигая старые чанки. Вне исходной карты высот продолжается её край; рельеф можно менять штампами. Размер области — до 64×64.",MessageType.Info);
            EditorGUILayout.LabelField("Чанки под замкнутым стыком скалы защищены: сначала перенесите/отключите меш-штамп.",EditorStyles.wordWrappedMiniLabel);
            using(new EditorGUI.DisabledScope(Application.isPlaying))
            {
                selected=EditorGUILayout.Vector2IntField("Координаты X / Z",selected);
                bool exists=present.ContainsKey(selected);
                using(new EditorGUI.DisabledScope(exists))
                    if(GUILayout.Button(grid.Contains(selected.x,selected.y)?"Добавить чанк в ячейку":"Расширить область и добавить чанк"))Run(()=>LTChunkManagement.Add(world,new[]{selected}));
                using(new EditorGUI.DisabledScope(!exists))
                    if(GUILayout.Button("Удалить выбранный чанк…")&&EditorUtility.DisplayDialog("Удалить чанк?",$"Удалить ({selected.x},{selected.y})? Штампы и файлы мешей сохранятся. Действие можно отменить через Undo.","Удалить","Отмена"))
                        Run(()=>LTChunkManagement.Remove(world,selected));
                using(new EditorGUI.DisabledScope(missing==0))
                    if(GUILayout.Button($"Восстановить все пустые ячейки ({missing})…")&&EditorUtility.DisplayDialog("Восстановление сетки",$"Добавить {missing} чанков? Будут заполнены ВСЕ пустые ячейки, включая удалённые намеренно. Существующие чанки и штампы не удаляются.","Восстановить","Отмена"))
                        Run(()=>LTChunkManagement.RestoreMissing(world));
                if(GUILayout.Button("Применить геометрию (без сохранения сцены)"))Run(()=>LTEditorEngine.Refresh(world,false,false));
            }
            if(!string.IsNullOrEmpty(status))EditorGUILayout.HelpBox(status,MessageType.None);
            EditorGUILayout.LabelField("Синий — существует; серый — пусто. Нажмите ячейку для выбора.");
            scroll=EditorGUILayout.BeginScrollView(scroll);
            for(int row=grid.rows-1;row>=0;row--)
            {
                EditorGUILayout.BeginHorizontal();GUILayout.Label((grid.minZ+row).ToString(),GUILayout.Width(35));
                for(int col=0;col<grid.columns;col++)
                {
                    var p=new Vector2Int(grid.minX+col,grid.minZ+row);var old=GUI.backgroundColor;
                    GUI.backgroundColor=p==selected?Color.yellow:present.ContainsKey(p)?new Color(.35f,.65f,1):Color.gray;
                    if(GUILayout.Button(new GUIContent(p.x.ToString(),$"({p.x},{p.y})"),GUILayout.Width(28),GUILayout.Height(22)))selected=p;
                    GUI.backgroundColor=old;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
