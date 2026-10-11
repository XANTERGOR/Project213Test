using System;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    // Sample the uncut heightfield so an already cut hole can still be previewed.
    // The red patch is an editor overlay, never a scene renderer/collider/asset.
    [InitializeOnLoad]
    internal static class LTHoleStampPreview
    {
        const string Prefix="LocalTerrain.HolePreview.";
        const int Cells=64, Side=Cells+1;
        internal const string ShaderPath="Assets/TerrainSystem/LocalTerrain/Editor/LTHoleStampPreview.shader";
        static LTHoleStamp selected;
        static LTWorld world;
        static LTHoleStampSnapshot snapshot;
        static Func<float,float,float> height;
        static Mesh mesh;
        static Material material;
        static Vector3[] vertices;
        static Color[] colors;
        static int[] indices;
        static string key,error;
        static Rect bounds;
        static int row;
        static bool ready,building;
        static double nextPoll;
        static readonly System.Diagnostics.Stopwatch Watch=new System.Diagnostics.Stopwatch();
        static LTHoleStampPreview()
        {
            Selection.selectionChanged+=Reset;
            Undo.undoRedoPerformed+=Invalidate;
            EditorApplication.projectChanged+=Invalidate;
            EditorApplication.hierarchyChanged+=Invalidate;
            AssemblyReloadEvents.beforeAssemblyReload+=Reset;
            EditorApplication.quitting+=Reset;
            EditorApplication.playModeStateChanged+=_=>Reset();
            Subscribe();
        }
        static void Subscribe()
        {
            EditorApplication.update-=Tick;SceneView.duringSceneGui-=Draw;
            if(SessionState.GetBool(Prefix+"Enabled",false)){EditorApplication.update+=Tick;SceneView.duringSceneGui+=Draw;}
        }
        internal static void DrawInspector(LTHoleStamp stamp)
        {
            bool changed=GUI.changed;
            EditorGUILayout.Space();EditorGUILayout.LabelField("Предпросмотр дырки — Scene View",EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            bool enabled=EditorGUILayout.Toggle("Показать вырезание",SessionState.GetBool(Prefix+"Enabled",false));
            if(EditorGUI.EndChangeCheck()){SessionState.SetBool(Prefix+"Enabled",enabled);Reset();Subscribe();SceneView.RepaintAll();}
            if(enabled)
            {
                EditorGUI.BeginChangeCheck();float opacity=EditorGUILayout.Slider("Непрозрачность",SessionState.GetFloat(Prefix+"Opacity",.55f),.05f,.9f);
                if(EditorGUI.EndChangeCheck()){SessionState.SetFloat(Prefix+"Opacity",opacity);SceneView.RepaintAll();}
                EditorGUILayout.HelpBox("Красное — область вырезания; жёлтое — примерный контур. Показывается и над уже вырезанной дыркой: это только редакторская подсветка, не восстановленная поверхность. Предпросмотр 64×64 приблизительный; тонкие детали могут быть не видны. Высота учитывает штампы/дороги, но не shader displacement. Геометрия, материалы, коллайдеры и Game View не меняются.",MessageType.Info);
                if(selected==stamp&&!string.IsNullOrEmpty(error))EditorGUILayout.HelpBox(error,MessageType.Warning);
                else if(selected==stamp&&!ready)EditorGUILayout.LabelField("Подготовка предпросмотра…");
                if(GUILayout.Button("Обновить предпросмотр дырки"))Invalidate();
            }
            GUI.changed=changed;
        }
        static void Invalidate(){key=null;height=null;ready=building=false;error=null;nextPoll=0;SceneView.RepaintAll();}
        static void Reset()
        {
            if(mesh)Object.DestroyImmediate(mesh);if(material)Object.DestroyImmediate(material);
            mesh=null;material=null;vertices=null;colors=null;indices=null;selected=null;world=null;snapshot=null;Invalidate();
        }
        static void Tick()
        {
            var active=Selection.activeGameObject?Selection.activeGameObject.GetComponent<LTHoleStamp>():null;
            if(active!=selected){Reset();selected=active;}
            if(!selected)return;
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorUtility.IsPersistent(selected)||!selected.isActiveAndEnabled||
                !selected.gameObject.scene.IsValid()||!selected.gameObject.scene.isLoaded)
            {ready=building=false;key=null;error="Нужен включённый штамп в открытой сцене, Edit Mode.";return;}
            double now=EditorApplication.timeSinceStartup;
            if(now>=nextPoll)
            {
                nextPoll=now+.12;
                try
                {
                    var parent=selected.GetComponentInParent<LTWorld>();
                    if(parent!=world){world=parent;height=null;key=null;}
                    if(!world||!world.source)throw new ArgumentException("Нужен родительский LT World с Source.");
                    var next=LTEditorEngine.CaptureHoleSnapshot(selected,world,out var nextKey);
                    if(nextKey!=key)
                    {
                        key=nextKey;snapshot=next;ready=false;building=true;row=0;error=null;
                        bounds=next.Bounds;
                        float pad=Mathf.Max(bounds.width,bounds.height)/Cells;
                        var extent=world.TerrainRect;
                        bounds=Rect.MinMaxRect(Mathf.Max(extent.xMin,bounds.xMin-pad),Mathf.Max(extent.yMin,bounds.yMin-pad),
                            Mathf.Min(extent.xMax,bounds.xMax+pad),Mathf.Min(extent.yMax,bounds.yMax+pad));
                        if(bounds.width<=0||bounds.height<=0)throw new ArgumentException("Штамп находится вне границ террейна.");
                        if(height==null)height=LTEditorEngine.CreateHolePreviewHeightSampler(world);
                        if(vertices==null)
                        {
                            vertices=new Vector3[Side*Side];colors=new Color[Side*Side];indices=new int[Cells*Cells*6];
                            for(int y=0,k=0;y<Cells;y++)for(int x=0;x<Cells;x++)
                            {int a=y*Side+x,b=a+Side;indices[k++]=a;indices[k++]=b;indices[k++]=a+1;indices[k++]=a+1;indices[k++]=b;indices[k++]=b+1;}
                        }
                        SceneView.RepaintAll();
                    }
                }
                catch(Exception ex){error=ex.Message;ready=building=false;key=null;return;}
            }
            if(!building)return;
            try
            {
                Watch.Restart();
                do
                {
                    float z=Mathf.Lerp(bounds.yMin,bounds.yMax,row/(float)Cells);
                    for(int x=0;x<Side;x++)
                    {
                        float px=Mathf.Lerp(bounds.xMin,bounds.xMax,x/(float)Cells),y=height(px,z);
                        if(!LTHoleMeshData.Finite(y))throw new ArgumentException("Некорректная высота предпросмотра.");
                        int i=row*Side+x;vertices[i]=new Vector3(px,y,z);colors[i]=new Color(snapshot.Contains(px,y,z)?1:0,0,0,1);
                    }
                    row++;
                }while(row<Side&&Watch.Elapsed.TotalMilliseconds<2);
                if(row<Side)return;
                LTEditorEngine.CaptureHoleSnapshot(selected,world,out string latest);
                if(latest!=key){key=null;building=false;nextPoll=0;return;}
                if(!mesh)mesh=new Mesh{name="Hole preview (editor only)",hideFlags=HideFlags.HideAndDontSave};
                mesh.Clear();mesh.vertices=vertices;mesh.colors=colors;mesh.triangles=indices;mesh.RecalculateBounds();
                ready=true;building=false;SceneView.RepaintAll();
            }
            catch(Exception ex){error=ex.Message;ready=building=false;}
        }
        static void Draw(SceneView view)
        {
            if(Event.current.type!=EventType.Repaint||!view.drawGizmos||!ready||!mesh||!world||!selected||Selection.activeGameObject!=selected.gameObject||
                !view.camera||!selected.isActiveAndEnabled||!world.isActiveAndEnabled||SceneVisibilityManager.instance.IsHidden(world.gameObject)||
                SceneVisibilityManager.instance.IsHidden(selected.gameObject)||(view.camera.cullingMask&(1<<world.gameObject.layer))==0||
                EditorApplication.isPlayingOrWillChangePlaymode)return;
            if(!material)
            {
                var shader=AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
                if(!shader||!shader.isSupported||ShaderUtil.ShaderHasError(shader)){error="Shader предпросмотра дырки не готов; проверь Console.";return;}
                material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
            }
            bool wire=GL.wireframe;
            try{GL.wireframe=false;material.SetFloat("_Opacity",SessionState.GetFloat(Prefix+"Opacity",.55f));if(material.SetPass(0))Graphics.DrawMeshNow(mesh,world.transform.localToWorldMatrix,0);}
            finally{GL.wireframe=wire;}
        }
    }
}
