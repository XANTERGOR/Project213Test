using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    internal sealed class LTRoadDimensionHandles:IDisposable
    {
        const string Setting="LocalTerrain.Road.DimensionHandles";
        static readonly HashSet<LTRoadDimensionHandles> active=new HashSet<LTRoadDimensionHandles>();
        static readonly string[] Properties={"width","shoulderWidth","blendWidth"};
        static readonly string[] Labels={"Ширина","Обочина","Переход"};
        readonly LTRoadMath.Hit[] frames=new LTRoadMath.Hit[3];
        LTRoad owner;SceneView dragView;LTRoadMath.Snapshot source,preview;
        Vector3 original,values;Matrix4x4 inverse;
        int control,parameter;bool dragging;
        string error;
        internal static LTRoadMath.Snapshot DisplaySnapshot(LTRoad road)
        {
            var committed=road.Capture(road.World);
            foreach(var edit in active)if(edit.owner==road&&ReferenceEquals(edit.source,committed))return edit.preview;
            return committed;
        }
        static Vector3 Values(LTRoad road)=>new Vector3(road.width,road.shoulderWidth,road.blendWidth);
        static bool WidthLocked(LTRoad road)=>road.startJunction&&road.startJunction.isActiveAndEnabled&&road.startJunction.Authored||
            road.endJunction&&road.endJunction.isActiveAndEnabled&&road.endJunction.Authored;

        internal void DrawInspector(LTRoad road)
        {
            bool changed=GUI.changed;
            bool enabled=EditorGUILayout.ToggleLeft("Ручки ширины и обочины в Scene",SessionState.GetBool(Setting,true));
            if(enabled!=SessionState.GetBool(Setting,true))
            {SessionState.SetBool(Setting,enabled);if(!enabled)Cancel();SceneView.RepaintAll();}
            if(enabled)
            {
                EditorGUILayout.HelpBox("Ручки возле выбранной точки пути: оранжевая — полная ширина, бирюзовая — обочина, голубая — переход. Меняются параметры всей дороги. Во время жеста — только предпросмотр; отпускание применяет, Esc отменяет, Undo возвращает весь жест.",MessageType.None);
                if(WidthLocked(road))EditorGUILayout.HelpBox("Ширина зафиксирована въездом готового перекрёстка. Обочину и переход можно менять.",MessageType.Info);
                else if(road.mode==LTRoadMode.Asphalt&&road.asphaltSource==LTRoadMeshSource.Module&&!road.moduleFitWidth)
                    EditorGUILayout.HelpBox("«Подогнать ширину» выключено: ширина меняет основание, исходная ширина модуля сохраняется.",MessageType.Info);
            }
            if(!string.IsNullOrEmpty(error))EditorGUILayout.HelpBox(error,MessageType.Warning);
            GUI.changed=changed; // Session toggles must not queue an inspector bake.
        }

        internal void Draw(LTRoad road,int selectedPoint)
        {
            var view=SceneView.currentDrawingSceneView;
            if(!road||!road.isActiveAndEnabled||!road.World||!SessionState.GetBool(Setting,true)||
                EditorApplication.isPlayingOrWillChangePlaymode||EditorUtility.IsPersistent(road)||
                !road.gameObject.scene.IsValid()||!road.gameObject.scene.isLoaded||EditorSceneManager.IsPreviewScene(road.gameObject.scene)||
                PrefabStageUtility.GetPrefabStage(road.gameObject)!=null||Selection.activeGameObject!=road.gameObject||Selection.gameObjects.Length!=1||!view||!view.drawGizmos)
            {Cancel();return;}
            if(dragging&&view!=dragView)return;
            var e=Event.current;
            if(dragging&&e.type==EventType.KeyDown&&e.keyCode==KeyCode.Escape){Cancel();e.Use();return;}
            var previousDepth=Handles.zTest;
            try
            {
                var committed=road.Capture(road.World);
                if(dragging&&(!ReferenceEquals(source,committed)||GUIUtility.hotControl!=control)){Cancel();return;}
                var snapshot=dragging?preview:committed;var dimensions=dragging?values:Values(road);
                var matrix=road.World.transform.localToWorldMatrix;
                if(!dragging)
                {
                    var point=road.transform.TransformPoint(road.points[Mathf.Clamp(selectedPoint,0,road.points.Count-1)].position);
                    if(selectedPoint==0&&road.startJunction&&road.startJunction.isActiveAndEnabled)point=matrix.MultiplyPoint3x4(snapshot.samples[0].position);
                    if(selectedPoint==road.points.Count-1&&road.endJunction&&road.endJunction.isActiveAndEnabled)point=matrix.MultiplyPoint3x4(snapshot.samples[snapshot.samples.Count-1].position);
                    var local=road.World.transform.InverseTransformPoint(point);snapshot.TrySample(local.x,local.z,out var anchor);
                    float spacing=Math.Min(snapshot.length/3,Math.Max(.5f,HandleUtility.GetHandleSize(point)*.22f));
                    float start=Mathf.Clamp(anchor.distance-spacing,0,Math.Max(0,snapshot.length-2*spacing));
                    for(int i=0;i<3;i++)frames[i]=LTRoadDimensionMath.Frame(snapshot,start+i*spacing);
                }
                Handles.zTest=CompareFunction.Always;
                for(int part=0;part<3;part++)for(int sign=-1;sign<=1;sign+=2)
                {
                    var hit=frames[part];hit.lateral=sign*snapshot.width*.5f;
                    float factor=snapshot.HalfWidth(hit)/(snapshot.width*.5f);
                    float radius=LTRoadDimensionMath.Radius(dimensions,part,factor);
                    var position=hit.position+hit.right*(sign*radius);position.y=snapshot.SurfaceHeight(hit,sign*radius);
                    position=matrix.MultiplyPoint3x4(position);
                    var direction=matrix.MultiplyVector(new Vector3(hit.right.x,(float)Math.Tan(hit.bank*Math.PI/180),hit.right.z)*sign).normalized;
                    int id=GUIUtility.GetControlID(unchecked(road.GetInstanceID()*397+part*2+(sign+1)/2+90153),FocusType.Passive);
                    bool locked=part==0&&WidthLocked(road);
                    var color=locked?Color.gray:part==0?new Color(1,.65f,.12f):part==1?new Color(.1f,.9f,1):new Color(.55f,.8f,1);
                    using(new Handles.DrawingScope(color,Matrix4x4.identity))
                    {
                        float size=HandleUtility.GetHandleSize(position)*.065f;
                        if(e.type==EventType.Repaint)Handles.DrawDottedLine(matrix.MultiplyPoint3x4(hit.position),position,4);
                        if(sign>0)Handles.Label(position+Vector3.up*size*1.7f,Labels[part]+": "+dimensions[part].ToString("0.00")+" м"+(locked?" (Port)":""));
                        if(locked||((e.alt||e.shift)&&!dragging))continue;
                        bool ownedBefore=dragging&&control==id;
                        bool mouseReleased=e.rawType==EventType.MouseUp&&e.button==0;
                        EditorGUI.BeginChangeCheck();
                        var moved=Handles.Slider(id,position,direction,size,Handles.CubeHandleCap,0);
                        bool changed=EditorGUI.EndChangeCheck();
                        if(!dragging&&GUIUtility.hotControl==id)Begin(road,view,id,part,committed);
                        if(changed&&dragging&&control==id)
                        {
                            var p=inverse.MultiplyPoint3x4(moved)-hit.position;
                            float distance=(p.x*hit.right.x+p.z*hit.right.z)*sign;
                            var next=LTRoadDimensionMath.Drag(original,part,distance,factor);
                            if(next!=values)
                            {
                                try{var candidate=owner.PreviewDimensions(next.x,next.y,next.z);values=next;preview=candidate;error=null;}
                                catch(ArgumentException ex){error=ex.Message;}
                                SceneView.RepaintAll();
                            }
                        }
                        if(ownedBefore&&GUIUtility.hotControl!=id)
                        {if(mouseReleased)Commit();else Cancel();return;}
                    }
                }
                if(dragging)Handles.Label(matrix.MultiplyPoint3x4(frames[parameter].position),"Предпросмотр • отпусти для применения • Esc: отмена");
            }
            catch(ArgumentException ex){Cancel();error=ex.Message;}
            finally{Handles.zTest=previousDepth;}
        }
        void Begin(LTRoad road,SceneView view,int id,int part,LTRoadMath.Snapshot snapshot)
        {
            owner=road;dragView=view;control=id;parameter=part;source=preview=snapshot;original=values=Values(road);
            inverse=road.World.transform.worldToLocalMatrix;dragging=true;error=null;active.Add(this);
            EditorApplication.update+=Watch;AssemblyReloadEvents.beforeAssemblyReload+=Cancel;Undo.undoRedoPerformed+=Cancel;
        }
        void Watch()
        {
            if(!owner||!dragView||EditorApplication.isPlayingOrWillChangePlaymode||EditorWindow.focusedWindow!=dragView||
                Selection.activeGameObject!=owner.gameObject||!SessionState.GetBool(Setting,true))Cancel();
        }
        void Commit()
        {
            var road=owner;var next=values;int field=parameter;
            bool valid=road&&road.World&&ReferenceEquals(source,road.Capture(road.World))&&!(field==0&&WidthLocked(road));
            Cancel();
            if(!valid||Values(road)==next)return;
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Изменить "+Labels[field].ToLowerInvariant()+" дороги");
            using(var serialized=new SerializedObject(road))
            {serialized.FindProperty(Properties[field]).floatValue=next[field];serialized.ApplyModifiedProperties();}
            Undo.CollapseUndoOperations(group);Undo.IncrementCurrentGroup();LTRoadBakeQueue.Queue(road);SceneView.RepaintAll();
        }
        internal void Cancel()
        {
            if(!dragging)return;
            if(GUIUtility.hotControl==control)
            {GUIUtility.hotControl=0;EditorGUIUtility.SetWantsMouseJumping(0);}
            EditorApplication.update-=Watch;AssemblyReloadEvents.beforeAssemblyReload-=Cancel;Undo.undoRedoPerformed-=Cancel;
            active.Remove(this);dragging=false;owner=null;dragView=null;source=preview=null;control=0;SceneView.RepaintAll();
        }
        public void Dispose()=>Cancel();
    }
}
