using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public sealed class LTGrassRootTintWindow : EditorWindow
    {
        internal const string Tint="_LTGrassRootTint",Strength="_LTGrassRootStrength",
            Height="_LTGrassRootHeight",Base="_LTGrassRootBase";
        internal const string TintA="_LTGrassTintA",TintB="_LTGrassTintB",Clump="_LTGrassClumpStrength",
            Patch="_LTGrassPatchStrength",PatchSize="_LTGrassPatchSize",Seed="_LTGrassVariationSeed";
        [SerializeField] Material material; // Migrate the previous single-material window.
        [SerializeField] GameObject prefab;
        [SerializeField] List<LTGrassMaterialEntry> entries=new List<LTGrassMaterialEntry>();
        Vector2 scroll;
        string notice;
        [MenuItem("Tools/Local Terrain/Grass Color")]
        [MenuItem("Tools/Local Terrain/Grass Root Tint")]
        static void Open()
        {
            var window=GetWindow<LTGrassRootTintWindow>("Цвет травы");
            if(Selection.objects.Length==1&&Selection.activeObject is GameObject go)window.prefab=go;
            window.AddObjects(Selection.objects);window.Show();
        }
        void OnEnable()
        {
            if(entries==null)entries=new List<LTGrassMaterialEntry>();
            if(material)
            {
                // Preserve the old reference even while the shader is reimporting.
                if(!entries.Any(e=>e!=null&&e.material==material))entries.Add(new LTGrassMaterialEntry{material=material});
                material=null;
            }
            entries.RemoveAll(e=>e==null);
            Undo.undoRedoPerformed+=Repaint;
        }
        void OnDisable(){Undo.undoRedoPerformed-=Repaint;}
        internal static bool Supported(Material m)=>m&&m.HasProperty(Tint)&&m.HasProperty(Strength)&&m.HasProperty(Height)&&m.HasProperty(Base);
        internal static bool SupportsVariation(Material m)=>m&&new[]{TintA,TintB,Clump,Patch,PatchSize,Seed}.All(m.HasProperty);
        void AddObjects(IEnumerable<Object> objects)
        {
            var found=LTGrassMaterialBatch.Gather(objects);
            int added=LTGrassMaterialBatch.Add(entries,found,Supported),unsupported=found.Count(m=>!Supported(m));
            notice=$"Добавлено: {added}. Уже в списке: {found.Length-added-unsupported}. Несовместимых: {unsupported}.";
            Repaint();
        }
        void Changed(bool changed){if(changed){SceneView.RepaintAll();Repaint();}}
        void Change(Material[] targets,Action<Material> change)=>Changed(LTGrassMaterialBatch.Change(targets,change));
        bool TryHeight(Material target,out float bottom,out float height)
        {
            bottom=0;height=0;if(!prefab||!target)return false;
            var group=prefab.GetComponent<LODGroup>();var lods=group?group.GetLODs():null;
            var renderers=lods!=null&&lods.Length>0?lods[0].renderers:prefab.GetComponentsInChildren<MeshRenderer>(true);
            float min=float.PositiveInfinity,max=float.NegativeInfinity;
            foreach(var renderer in renderers)
            {
                if(!renderer||!renderer.sharedMaterials.Contains(target))continue;
                var filter=renderer.GetComponent<MeshFilter>();if(!filter||!filter.sharedMesh)continue;
                var bounds=filter.sharedMesh.bounds;min=Mathf.Min(min,bounds.min.y);max=Mathf.Max(max,bounds.max.y);
            }
            if(float.IsInfinity(min)||max-min<=0.0001f)return false;
            bottom=min;height=max-min;return true;
        }
        void OnGUI()
        {
            using(var view=new EditorGUILayout.ScrollViewScope(scroll)){scroll=view.scrollPosition;DrawControls();}
        }
        void DrawMaterials()
        {
            EditorGUILayout.LabelField("Материалы травы",EditorStyles.boldLabel);
            var drop=GUILayoutUtility.GetRect(0,46,GUILayout.ExpandWidth(true));
            GUI.Box(drop,"Перетащите сюда несколько материалов или префабов из Project",EditorStyles.helpBox);
            var evt=Event.current;
            if(drop.Contains(evt.mousePosition)&&(evt.type==EventType.DragUpdated||evt.type==EventType.DragPerform)&&
                DragAndDrop.objectReferences.Any(o=>o is Material||o is GameObject))
            {
                DragAndDrop.visualMode=DragAndDropVisualMode.Copy;
                if(evt.type==EventType.DragPerform){DragAndDrop.AcceptDrag();AddObjects(DragAndDrop.objectReferences);}
                evt.Use();
            }
            var add=(Material)EditorGUILayout.ObjectField("Добавить материал",null,typeof(Material),false);
            if(add)AddObjects(new Object[]{add});
            using(new EditorGUILayout.HorizontalScope())
            {
                prefab=(GameObject)EditorGUILayout.ObjectField("Префаб (необяз.)",prefab,typeof(GameObject),true);
                using(new EditorGUI.DisabledScope(!prefab))
                    if(GUILayout.Button("Добавить из префаба",GUILayout.Width(160)))AddObjects(new Object[]{prefab});
            }
            if(GUILayout.Button("Добавить выделенные в Project"))AddObjects(Selection.objects);
            if(!string.IsNullOrEmpty(notice))EditorGUILayout.LabelField(notice,EditorStyles.wordWrappedMiniLabel);
            for(int i=0;i<entries.Count;i++)
            {
                var entry=entries[i];
                using(new EditorGUILayout.HorizontalScope())
                {
                    using(new EditorGUI.DisabledScope(!Supported(entry.material)))
                        entry.included=EditorGUILayout.Toggle(entry.included,GUILayout.Width(18));
                    var replacement=(Material)EditorGUILayout.ObjectField(entry.material,typeof(Material),false);
                    if(replacement!=entry.material)
                    {
                        if(replacement&&entries.Any(e=>e!=entry&&e.material==replacement))notice="Этот материал уже есть в списке.";
                        else entry.material=replacement;
                    }
                    if(GUILayout.Button("×",GUILayout.Width(24))){entries.RemoveAt(i);break;}
                }
                if(entry.material&&!Supported(entry.material))
                    EditorGUILayout.LabelField("Шейдер не поддерживает настройки травы; материал пропущен.",EditorStyles.wordWrappedMiniLabel);
            }
            if(entries.Count>0)using(new EditorGUILayout.HorizontalScope())
            {
                if(GUILayout.Button("Отметить все"))foreach(var entry in entries)entry.included=Supported(entry.material);
                if(GUILayout.Button("Снять отметки"))foreach(var entry in entries)entry.included=false;
                if(GUILayout.Button("Очистить список"))entries.Clear();
            }
            EditorGUILayout.LabelField("Добавление, снятие галочки и × меняют только список окна — не материалы и не объекты сцены.",EditorStyles.wordWrappedMiniLabel);
        }
        void DrawControls()
        {
            EditorGUILayout.HelpBox("Разнотон, автоматический оттенок от земли и ручное затемнение корней. Подхват земли работает у травы, которую рисует Local Terrain / Detail Renderer.",MessageType.Info);
            DrawMaterials();
            var targets=LTGrassMaterialBatch.Selected(entries,Supported);
            if(targets.Length==0)
            {
                EditorGUILayout.HelpBox("Добавьте совместимые материалы DA_Grass_WIND и отметьте нужные галочками. После импорта шейдера они станут доступны.",MessageType.Info);
                return;
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Редактируются отмеченные материалы: {targets.Length}",EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("«—» означает разные значения. Правка одного поля меняет только его у отмеченных материалов; остальные параметры сохраняются. Пресеты меняют настройки своего раздела. Есть Undo. Материалы общие для всех использующих их объектов; окно не сохраняет сцену и не вызывает SaveAssets.",MessageType.Info);
            DrawTerrainColor(targets);
            DrawVariation(targets);
            DrawRoots(targets);
        }
        void FloatControl(Material[] targets,string property,string label,float min,float max,bool slider=false,bool integer=false)
        {
            bool previous=EditorGUI.showMixedValue;
            try
            {
                EditorGUI.showMixedValue=LTGrassMaterialBatch.FloatMixed(targets,property);
                EditorGUI.BeginChangeCheck();
                float old=targets[0].GetFloat(property);
                float value=integer?EditorGUILayout.IntField(label,Mathf.RoundToInt(old)):
                    slider?EditorGUILayout.Slider(label,old,min,max):EditorGUILayout.FloatField(label,old);
                if(EditorGUI.EndChangeCheck()&&!float.IsNaN(value)&&!float.IsInfinity(value))
                    Changed(LTGrassMaterialBatch.SetFloat(targets,property,Mathf.Clamp(value,min,max)));
            }
            finally{EditorGUI.showMixedValue=previous;}
        }
        void ColorControl(Material[] targets,string property,string label)
        {
            bool previous=EditorGUI.showMixedValue;
            try
            {
                EditorGUI.showMixedValue=LTGrassMaterialBatch.ColorMixed(targets,property);
                EditorGUI.BeginChangeCheck();
                var value=EditorGUILayout.ColorField(new GUIContent(label),targets[0].GetColor(property),true,false,false);
                if(EditorGUI.EndChangeCheck())Changed(LTGrassMaterialBatch.SetColor(targets,property,value));
            }
            finally{EditorGUI.showMixedValue=previous;}
        }
        void DrawRoots(Material[] targets)
        {
            EditorGUILayout.Space();EditorGUILayout.LabelField("Корни — ручное затемнение",EditorStyles.boldLabel);
            ColorControl(targets,Tint,"Оттенок корней");
            FloatControl(targets,Strength,"Сила эффекта",0,1,true);
            FloatControl(targets,Height,"Высота перехода (лок.)",.0001f,float.MaxValue);
            FloatControl(targets,Base,"Высота основания (лок.)",float.MinValue,float.MaxValue);
            EditorGUILayout.LabelField("Высоты — в координатах меша, до масштаба экземпляра. Выше перехода цвет не меняется ручным затемнением.",EditorStyles.wordWrappedMiniLabel);
            float meshBase=0,meshHeight=0;
            bool measured=targets.Length==1&&TryHeight(targets[0],out meshBase,out meshHeight);
            if(measured)EditorGUILayout.LabelField($"LOD0 префаба: основание {meshBase:0.###}, высота {meshHeight:0.###}.",EditorStyles.wordWrappedMiniLabel);
            if(GUILayout.Button(measured?"Попробовать: низ 30%, сила 0.65":"Попробовать: сила 0.65 (сохранить высоты)"))
                Change(targets,m=>
                {
                    m.SetColor(Tint,new Color(.6f,.48f,.33f,1));m.SetFloat(Strength,.65f);
                    if(measured){m.SetFloat(Base,meshBase);m.SetFloat(Height,meshHeight*.3f);}
                });
            if(GUILayout.Button("Выключить окраску корней у отмеченных"))Changed(LTGrassMaterialBatch.SetFloat(targets,Strength,0));
        }
        void DrawTerrainColor(Material[] all)
        {
            var targets=all.Where(m=>m.HasProperty(LTGrassTerrainMap.Strength)&&m.HasProperty(LTGrassTerrainMap.Height)).ToArray();
            EditorGUILayout.Space();EditorGUILayout.LabelField("Оттенок от земли (пробный)",EditorStyles.boldLabel);
            if(targets.Length!=all.Length)EditorGUILayout.HelpBox($"Поддерживают подхват земли: {targets.Length}/{all.Length}. Остальные пропускаются — проверьте импорт шейдера.",MessageType.Warning);
            if(targets.Length==0)return;
            FloatControl(targets,LTGrassTerrainMap.Strength,"Сила подхвата",0,1,true);
            FloatControl(targets,LTGrassTerrainMap.Height,"Высота перехода (лок.)",.0001f,float.MaxValue);
            EditorGUILayout.LabelField("Цвет под основанием пучка, без освещения и теней. Сильнее у корней, выше перехода — прежняя трава. 0 выключает эффект. Низ берётся из «Высоты основания» в разделе корней.",EditorStyles.wordWrappedMiniLabel);
            float meshHeight=0;
            bool measured=all.Length==1&&TryHeight(targets[0],out _,out meshHeight);
            if(GUILayout.Button("Попробовать оттенок земли: сила 0.5"))Change(targets,m=>
            {
                m.SetFloat(LTGrassTerrainMap.Strength,.5f);
                if(measured)m.SetFloat(LTGrassTerrainMap.Height,Mathf.Max(.0001f,meshHeight*.6f));
            });
            if(all.Length>1)EditorGUILayout.LabelField("Групповая проба сохраняет индивидуальные высоты перехода. Изменение поля высоты выше явно задаёт одну высоту всем отмеченным.",EditorStyles.wordWrappedMiniLabel);
            if(GUILayout.Button("Выключить оттенок от земли у отмеченных"))Changed(LTGrassMaterialBatch.SetFloat(targets,LTGrassTerrainMap.Strength,0));
            foreach(var world in Object.FindObjectsByType<LTWorld>(FindObjectsSortMode.None))
                if(world.isActiveAndEnabled)EditorGUILayout.HelpBox(world.name+": "+world.GrassTerrainStatus,MessageType.None);
            EditorGUILayout.LabelField("Карта обновляется автоматически. Отдельные меши скал/асфальта не входят. До готовности участка цвет прежний. Ручное затемнение корней применяется дополнительно.",EditorStyles.wordWrappedMiniLabel);
        }
        void DrawVariation(Material[] all)
        {
            var targets=all.Where(SupportsVariation).ToArray();
            EditorGUILayout.Space();EditorGUILayout.LabelField("Разнотон",EditorStyles.boldLabel);
            if(targets.Length!=all.Length)EditorGUILayout.HelpBox($"Поддерживают разнотон: {targets.Length}/{all.Length}. Остальные пропускаются.",MessageType.Warning);
            if(targets.Length==0)return;
            ColorControl(targets,TintA,"Оттенок A");
            ColorControl(targets,TintB,"Оттенок B");
            FloatControl(targets,Clump,"Между кустиками",0,1,true);
            FloatControl(targets,Patch,"Крупные пятна",0,1,true);
            FloatControl(targets,PatchSize,"Размер пятен (м)",.1f,float.MaxValue);
            FloatControl(targets,Seed,"Seed",0,65535,integer:true);
            EditorGUILayout.LabelField("Оба эффекта выбирают оттенок между A и B; суммарная сила ограничена 1. Нули выключают разнотон, не затрагивая корни или подхват земли.",EditorStyles.wordWrappedMiniLabel);
            if(GUILayout.Button("Попробовать мягкий разнотон"))Change(targets,m=>
            {
                m.SetColor(TintA,new Color(.97f,.95f,.87f,1));m.SetColor(TintB,new Color(.79f,.83f,.75f,1));
                m.SetFloat(Clump,.2f);m.SetFloat(Patch,.35f);m.SetFloat(PatchSize,12);m.SetFloat(Seed,1);
            });
            if(GUILayout.Button("Выключить разнотон у отмеченных"))Change(targets,m=>{m.SetFloat(Clump,0);m.SetFloat(Patch,0);});
        }
    }
}
