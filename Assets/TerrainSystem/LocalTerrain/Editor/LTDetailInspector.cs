using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    [CustomPropertyDrawer(typeof(LTDetailEntry))]
    public sealed class LTDetailEntryDrawer : PropertyDrawer
    {
        static readonly string[] Fields={"enabled","prefab","category","density","probability",
            "densityMaskMode","patchSize","patchCoverage","patchSoftness","patchMinimumDensity","patchSeed",
            "patchScaleEnabled","patchScaleEdge","patchScaleInside","patchScaleSoftness",
            "scaleRange","yawRange","slopeRange","alignToNormal","heightOffsetRange",
            "fadeStart","cullDistance","limitShadowDistance","shadowDistance","shaderFade","thinInDistance","farDensity","deformationBoundsPadding"};
        public override float GetPropertyHeight(SerializedProperty property,GUIContent label)
        {
            float height=EditorGUIUtility.singleLineHeight;
            if(property.isExpanded)foreach(string name in Fields)
                if(Visible(property,name))
                height+=EditorGUIUtility.standardVerticalSpacing+EditorGUI.GetPropertyHeight(property.FindPropertyRelative(name),true);
            return height;
        }
        static bool Visible(SerializedProperty property,string name)
        {
            if(name=="shadowDistance")return property.FindPropertyRelative("limitShadowDistance").boolValue;
            if(!name.StartsWith("patch"))return true;
            if(property.FindPropertyRelative("densityMaskMode").enumValueIndex!=(int)LTDetailMaskMode.Own)return false;
            return !name.StartsWith("patchScale")||name=="patchScaleEnabled"||property.FindPropertyRelative("patchScaleEnabled").boolValue;
        }
        public override void OnGUI(Rect position,SerializedProperty property,GUIContent label)
        {
            EditorGUI.BeginProperty(position,label,property);
            var prefab=property.FindPropertyRelative("prefab").objectReferenceValue;
            var line=new Rect(position.x,position.y,position.width,EditorGUIUtility.singleLineHeight);
            property.isExpanded=EditorGUI.Foldout(line,property.isExpanded,
                new GUIContent(prefab?prefab.name:"Объект — префаб не назначен",
                    "Настройки одного типа детализации. Наведите курсор на название параметра для подсказки."),true);
            if(property.isExpanded)
            {
                EditorGUI.indentLevel++;
                foreach(string name in Fields)
                {
                    if(!Visible(property,name))continue;
                    var child=property.FindPropertyRelative(name);
                    line.y+=line.height+EditorGUIUtility.standardVerticalSpacing;
                    line.height=EditorGUI.GetPropertyHeight(child,true);
                    if(name=="densityMaskMode")
                    {
                        EditorGUI.BeginChangeCheck();
                        EditorGUI.PropertyField(line,child,new GUIContent("Маска плотности",child.tooltip));
                        if(EditorGUI.EndChangeCheck())property.FindPropertyRelative("maskModeConfigured").boolValue=true;
                    }
                    else EditorGUI.PropertyField(line,child,new GUIContent(
                        name=="limitShadowDistance"?"Ограничить дальность теней":
                        name=="shadowDistance"?"Дальность теней, м":LTDetailInspector.ScaleLabel(name)??child.displayName,child.tooltip),true);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }
    }
    public static class LTDetailInspector
    {
        sealed class MaskPreview
        {
            public Object owner;
            public Texture2D texture;
            public readonly Color32[] pixels=new Color32[128*128];
            public string key;
            public float metres=32;
            public int worldSeed=12345;
            public int mode;
            public double lastDraw;
        }
        // Bounded editor-only cache; textures never become project assets.
        static readonly Dictionary<int,MaskPreview> previews=new Dictionary<int,MaskPreview>();
        static double nextPreviewCleanup;
        static LTDetailInspector()
        {
            AssemblyReloadEvents.beforeAssemblyReload+=ClearPreviews;
            EditorApplication.quitting+=ClearPreviews;
            EditorApplication.update+=CleanupPreviews;
        }
        static void ClearPreviews()
        {
            foreach(var preview in previews.Values)if(preview.texture)Object.DestroyImmediate(preview.texture);
            previews.Clear();
        }
        static void CleanupPreviews()
        {
            double now=EditorApplication.timeSinceStartup;
            if(now<nextPreviewCleanup)return;
            nextPreviewCleanup=now+5;
            var expired=new List<int>();
            foreach(var pair in previews)
                if(!pair.Value.owner||now-pair.Value.lastDraw>60)expired.Add(pair.Key);
            foreach(int id in expired)
            {
                if(previews[id].texture)Object.DestroyImmediate(previews[id].texture);
                previews.Remove(id);
            }
        }
        static void DrawMaskPreview(SerializedObject owner,SerializedProperty mask)
        {
            int id=owner.targetObject.GetInstanceID();
            if(!previews.TryGetValue(id,out var preview))
            {
                if(previews.Count>=8)
                {
                    int oldest=0;double time=double.PositiveInfinity;
                    foreach(var pair in previews)if(pair.Value.lastDraw<time){oldest=pair.Key;time=pair.Value.lastDraw;}
                    if(previews[oldest].texture)Object.DestroyImmediate(previews[oldest].texture);
                    previews.Remove(oldest);
                }
                preview=new MaskPreview{owner=owner.targetObject};
                previews.Add(id,preview);
            }
            preview.lastDraw=EditorApplication.timeSinceStartup;
            EditorGUILayout.LabelField("Предпросмотр маски",EditorStyles.miniBoldLabel);
            preview.mode=GUILayout.Toolbar(preview.mode,new[]{"Плотность","Размер"});
            preview.metres=Mathf.Clamp(EditorGUILayout.FloatField(new GUIContent("Область превью, м",
                "Размер квадратного образца XZ от (0,0). Только масштаб предпросмотра, не меняет расстановку."),preview.metres),1,1024);
            int contextSeed;
            if(owner.targetObject is LTDetailStamp stamp&&stamp.world&&stamp.world.GetComponent<LTDetailRenderer>())
            {
                int worldSeed=stamp.world.GetComponent<LTDetailRenderer>().seed;
                contextSeed=worldSeed^owner.FindProperty("seed").intValue;
                EditorGUILayout.LabelField("Seed мира / штампа",worldSeed+" / "+owner.FindProperty("seed").intValue,EditorStyles.miniLabel);
            }
            else
            {
                preview.worldSeed=EditorGUILayout.IntField(new GUIContent("Seed мира (только превью)",
                    "Слой может использоваться в разных мирах. Для совпадения рисунка укажите Seed из Detail Renderer; реальный мир это поле не меняет."),preview.worldSeed);
                contextSeed=preview.worldSeed;
                if(owner.targetObject is LTDetailStamp)contextSeed^=owner.FindProperty("seed").intValue;
            }
            var entry=new LTDetailEntry
            {
                densityMask=mask.FindPropertyRelative("enabled").boolValue,
                patchSize=mask.FindPropertyRelative("patchSize").floatValue,
                patchCoverage=mask.FindPropertyRelative("patchCoverage").floatValue,
                patchSoftness=mask.FindPropertyRelative("patchSoftness").floatValue,
                patchMinimumDensity=mask.FindPropertyRelative("patchMinimumDensity").floatValue,
                patchSeed=mask.FindPropertyRelative("patchSeed").intValue,
                patchScaleEnabled=mask.FindPropertyRelative("patchScaleEnabled").boolValue,
                patchScaleEdge=mask.FindPropertyRelative("patchScaleEdge").floatValue,
                patchScaleInside=mask.FindPropertyRelative("patchScaleInside").floatValue,
                patchScaleSoftness=mask.FindPropertyRelative("patchScaleSoftness").floatValue
            };
            float maxScale=entry.patchScaleEnabled?Mathf.Max(.01f,Mathf.Max(entry.patchScaleEdge,entry.patchScaleInside)):1;
            string key=JsonUtility.ToJson(entry)+"|"+contextSeed+"|"+preview.mode+"|"+preview.metres.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
            var row=GUILayoutUtility.GetRect(0,160,GUILayout.ExpandWidth(true));
            float side=Mathf.Min(160,row.width);
            var rect=new Rect(row.x+(row.width-side)*.5f,row.y,side,side);
            // GUI.Box reserves a control ID even though it is passive. Call it on EVERY
            // event so subsequent text fields keep the same IDs during input/repaint.
            GUI.Box(rect,GUIContent.none);
            if(Event.current.type==EventType.Repaint)
            {
                if(!preview.texture)
                {
                    preview.texture=new Texture2D(128,128,TextureFormat.RGBA32,false,true)
                    {name="Detail density mask preview",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
                    preview.key=null;
                }
                if(preview.key!=key)
                {
                    for(int z=0;z<128;z++)for(int x=0;x<128;x++)
                    {
                        // Same final density multiplier used by generation, including coverage/falloff/minimum.
                        float value=LTDetailMath.DensityMask(entry,(x+.5f)/128*preview.metres,(z+.5f)/128*preview.metres,contextSeed,out float scale);
                        if(preview.mode==1)value=scale/maxScale;
                        byte gray=(byte)Mathf.RoundToInt(Mathf.Clamp01(value)*255);
                        preview.pixels[z*128+x]=new Color32(gray,gray,gray,255);
                    }
                    preview.texture.SetPixels32(preview.pixels);preview.texture.Apply(false,false);preview.key=key;
                }
                GUI.DrawTexture(new Rect(rect.x+1,rect.y+1,rect.width-2,rect.height-2),preview.texture,ScaleMode.ScaleToFit,false);
            }
            EditorGUILayout.LabelField(preview.mode==0?"Белое — густо · чёрное — пусто · серое — реже":
                "Белое — ×"+maxScale.ToString("0.##")+" · чёрное — ×0 · множитель Scale Range",EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Образец XZ от (0,0), без весов слоёв и границ штампа.",EditorStyles.miniLabel);
        }
        public static string ScaleLabel(string name)
        {
            switch(name)
            {
                case "patchScaleEnabled":return "Размер по маске";
                case "patchScaleEdge":return "Размер у края, ×";
                case "patchScaleInside":return "Размер внутри, ×";
                case "patchScaleSoftness":return "Мягкость размера";
                default:return null;
            }
        }
        static void DrawCommonMask(SerializedObject owner)
        {
            var mask=owner.FindProperty("detailDensityMask");
            if(mask==null)return;
            EditorGUILayout.LabelField("Маска плотности детализации",EditorStyles.boldLabel);
            var enabled=mask.FindPropertyRelative("enabled");
            EditorGUILayout.PropertyField(enabled,new GUIContent("Включить общую маску",enabled.tooltip));
            if(enabled.boolValue)
            {
                string[] fields={"patchSize","patchCoverage","patchSoftness","patchMinimumDensity","patchSeed"};
                string[] labels={"Размер пятен, м","Заполненность","Мягкость края","Плотность между пятнами","Seed пятен"};
                for(int i=0;i<fields.Length;i++)
                {
                    var field=mask.FindPropertyRelative(fields[i]);
                    EditorGUILayout.PropertyField(field,new GUIContent(labels[i],field.tooltip));
                }
                var scaleEnabled=mask.FindPropertyRelative("patchScaleEnabled");
                EditorGUILayout.PropertyField(scaleEnabled,new GUIContent(ScaleLabel("patchScaleEnabled"),scaleEnabled.tooltip));
                if(scaleEnabled.boolValue)
                {
                    foreach(string name in new[]{"patchScaleEdge","patchScaleInside","patchScaleSoftness"})
                    {
                        var field=mask.FindPropertyRelative(name);
                        EditorGUILayout.PropertyField(field,new GUIContent(ScaleLabel(name),field.tooltip));
                    }
                    EditorGUILayout.HelpBox("Размер растёт от края внутрь пятна по тому же шуму. Мягкость размера не меняет плотность. Множители действуют на все объекты с общей маской, поверх их Scale Range.",MessageType.Info);
                }
                DrawMaskPreview(owner,mask);
            }
            EditorGUILayout.HelpBox("Объекты в режиме «Общая» используют эти настройки. «Своя» и «Без маски» — исключения. Старые включённые индивидуальные маски сохранены как «Свои».",MessageType.Info);
            if(GUILayout.Button(new GUIContent("Все объекты → общая маска",
                "Перевести весь список на наследование. Свои параметры сохраняются. Изменение можно отменить Ctrl+Z.")))
            {
                // SerializedProperty changes participate in the owner's normal Undo/prefab override flow.
                var entries=owner.FindProperty("details");
                for(int i=0;i<entries.arraySize;i++)
                {
                    var entry=entries.GetArrayElementAtIndex(i);
                    entry.FindPropertyRelative("densityMaskMode").enumValueIndex=(int)LTDetailMaskMode.Common;
                    entry.FindPropertyRelative("maskModeConfigured").boolValue=true;
                }
            }
        }
        static GameObject PrefabAsset(Object value)
        {
            if(!(value is GameObject go)||!EditorUtility.IsPersistent(go))return null;
            // Normalize child selections to their prefab/model asset root.
            var root=AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(go));
            return root&&PrefabUtility.IsPartOfPrefabAsset(root)?root:null;
        }
        public static int AddPrefabs(SerializedObject owner,Object[] selection)
        {
            owner.ApplyModifiedProperties();
            var layer=owner.targetObject as LTSurfaceLayer;
            var stamp=owner.targetObject as LTDetailStamp;
            if(!layer&&!stamp)return 0;
            var entries=layer?layer.details:stamp.details;
            var existing=new HashSet<GameObject>();
            if(entries!=null)foreach(var entry in entries)
                if(entry!=null&&entry.prefab)existing.Add(entry.prefab);
            var additions=new List<GameObject>();
            foreach(var value in selection)
            {
                var prefab=PrefabAsset(value);
                if(prefab&&existing.Add(prefab))additions.Add(prefab);
            }
            if(additions.Count==0)return 0;
            Undo.RecordObject(owner.targetObject,"Add detail prefabs");
            if(entries==null)
            {
                entries=new List<LTDetailEntry>();
                if(layer)layer.details=entries;else stamp.details=entries;
            }
            // Always construct defaults, never duplicate the last array element.
            foreach(var prefab in additions)entries.Add(new LTDetailEntry{prefab=prefab});
            LTDetailEntry.ValidateAll(entries);
            if(stamp&&PrefabUtility.IsPartOfPrefabInstance(stamp))
                PrefabUtility.RecordPrefabInstancePropertyModifications(stamp);
            EditorUtility.SetDirty(owner.targetObject);
            owner.Update();
            return additions.Count;
        }
        static void DrawDropZone(SerializedObject owner)
        {
            var rect=GUILayoutUtility.GetRect(0,54,GUILayout.ExpandWidth(true));
            GUI.Box(rect,new GUIContent("Перетащите сюда несколько префабов из Project",
                "Каждый префаб добавится отдельной записью с исходными настройками. Уже добавленные префабы, объекты сцены и неподходящие файлы пропускаются. Отмена — Ctrl+Z."),EditorStyles.helpBox);
            var ev=Event.current;
            if(!rect.Contains(ev.mousePosition)||(ev.type!=EventType.DragUpdated&&ev.type!=EventType.DragPerform))return;
            bool supported=false;
            foreach(var value in DragAndDrop.objectReferences)if(PrefabAsset(value)){supported=true;break;}
            DragAndDrop.visualMode=supported?DragAndDropVisualMode.Copy:DragAndDropVisualMode.Rejected;
            if(ev.type==EventType.DragPerform&&supported)
            {
                DragAndDrop.AcceptDrag();
                int count=AddPrefabs(owner,DragAndDrop.objectReferences);
                Debug.Log("Детализация: добавлено префабов — "+count+". Дубликаты и неподходящие объекты пропущены.",owner.targetObject);
            }
            ev.Use();
        }
        public static void DrawList(SerializedObject owner,string property="details")
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Детализация",EditorStyles.boldLabel);
            DrawCommonMask(owner);
            DrawDropZone(owner);
            EditorGUILayout.PropertyField(owner.FindProperty(property),new GUIContent("Объекты детализации",
                "Набор статических объектов. Можно добавлять записи вручную или перетаскивать несколько префабов в область выше."),true);
            EditorGUILayout.HelpBox("Для расстановки добавьте Local Terrain / Detail Renderer на объект LTWorld. В материалах нужен Enable GPU Instancing. Тени наследуются из Renderer каждого LOD.",MessageType.Info);
            if(GUILayout.Button("Проверить префабы детализации"))
            {
                owner.ApplyModifiedProperties();
                var entries=owner.targetObject is LTSurfaceLayer layer?layer.details:((LTDetailStamp)owner.targetObject).details;
                foreach(var entry in entries)
                {
                    if(entry==null||!entry.enabled)continue;
                    var errors=new List<string>();var warnings=new List<string>();
                    if(entry.prefab&&!PrefabUtility.IsPartOfPrefabAsset(entry.prefab))
                        errors.Add("Нужен prefab asset из Project, не объект сцены.");
                    bool valid=LTDetailPrefab.TryBuild(entry.prefab,out var recipe,errors,warnings)&&errors.Count==0;
                    string label=entry.prefab?entry.prefab.name:"Без префаба";
                    foreach(string message in errors)Debug.LogError(label+": "+message,owner.targetObject);
                    foreach(string message in warnings)Debug.LogWarning(label+": "+message,owner.targetObject);
                    if(valid)Debug.Log(label+": структура поддерживается; LOD: "+recipe.levels.Count+
                        ". Совместимость шейдера и GPU ещё требует проверки.",owner.targetObject);
                }
            }
        }
    }
    [CustomEditor(typeof(LTDetailRenderer))]
    public sealed class LTDetailRendererInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var renderer=(LTDetailRenderer)target;
            EditorGUILayout.HelpBox(renderer.Status,MessageType.Info);
            EditorGUILayout.LabelField("Ячейки: загружено / зона камер",renderer.CellCount+" / "+renderer.WantedCellCount);
            EditorGUILayout.LabelField("Последняя камера",renderer.LastCamera??"—");
            EditorGUILayout.LabelField("Draw calls / инстансы частей",renderer.LastDrawCalls+" / "+renderer.LastSubmittedInstances);
            if(renderer.gpuDetails)
            {
                EditorGUILayout.LabelField("GPU: indirect calls / кандидаты",renderer.GpuDrawCalls+" / "+renderer.GpuCandidates);
                EditorGUILayout.LabelField("GPU: общих проходов отсечения",renderer.GpuCullDispatches.ToString());
                EditorGUILayout.LabelField("GPU: буферы / CPU fallback",(renderer.GpuBytes/(1024.0*1024)).ToString("F2")+" МиБ / "+renderer.GpuFallbackGroups+" групп");
                EditorGUILayout.LabelField("GPU: резерв / пик буферов",(renderer.GpuReservedBytes/(1024.0*1024)).ToString("F2")+" / "+(renderer.GpuPeakBytes/(1024.0*1024)).ToString("F2")+" МиБ");
                EditorGUILayout.LabelField("GPU: буферы / группы / камеры-группы",renderer.GpuBufferCount+" / "+renderer.GpuGroupCount+" / "+renderer.GpuViewCount);
                EditorGUILayout.LabelField("GPU: материалы / освобождено",renderer.GpuMaterialCount+" / "+(renderer.GpuReleasedBytes/(1024.0*1024)).ToString("F2")+" МиБ суммарно");
                EditorGUILayout.LabelField("GPU: ждут загрузки (эта камера)",renderer.GpuPendingGroups.ToString());
                EditorGUILayout.LabelField("GPU upload: МиБ / CPU мс / пик",(renderer.GpuUploadBytes/(1024.0*1024)).ToString("F2")+" / "+renderer.GpuUploadMilliseconds.ToString("F2")+" / "+renderer.GpuPeakUploadMilliseconds.ToString("F2"));
                EditorGUILayout.HelpBox("Upload-бюджет общий для камер этого мира: игровой кадр или обновление редактора. Пока группа не готова целиком, рисуется CPU. Резерв включает ещё не выделенные буферы; пик и освобождённые байты — с создания GPU backend. Это не полная VRAM. Одна операция драйвера может превысить мягкий бюджет.",MessageType.Info);
                if(GUILayout.Button("Проверить учёт GPU-памяти"))renderer.CheckGpuMemory();
                EditorGUILayout.HelpBox("GPU-режим: счётчики CPU ниже не включают GPU-экземпляры. Число кандидатов — до GPU-отсечения, не число видимых. Нет синхронного readback. Поддержаны DA_Grass_WIND, BaseShaderProps и HDRP/Lit; прочие шейдеры остаются на CPU. Индивидуальные классические Light Probes не передаются — проверьте освещение. Подготовка матриц и загрузка порций выполняются на CPU.",MessageType.Info);
                if(GUILayout.Button("Почему объекты остались на CPU? → Console"))renderer.ReportGpuFallback();
                if(!string.IsNullOrEmpty(renderer.GpuStatus))EditorGUILayout.HelpBox(renderer.GpuStatus,MessageType.Warning);
                if(GUILayout.Button("Проверить GPU-шейдер и отсечение…"))LTDetailGpuCheck.Run();
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Диагностика детализации — CPU",EditorStyles.boldLabel);
            void Metric(string label,string tooltip,string value)
                =>EditorGUILayout.LabelField(new GUIContent(label,tooltip),new GUIContent(value));
            string Ms(double value)=>value.ToString("F2")+" мс";
            string Pair(double last,double peak)=>Ms(last)+" / "+Ms(peak);
            Metric("Партии: без объединения / факт","Число вызовов RenderMeshInstanced для того же отбора: расчёт без объединения и фактически отправлено. Не включает дополнительные GPU-проходы HDRP. Это не сравнение FPS.",
                renderer.LastUnmergedDrawCalls+" / "+renderer.LastDrawCalls);
            Metric("CPU-кэш матриц, МиБ","Матрицы загруженных ячеек. Предел — Matrix Cache MiB; это RAM, не VRAM. При перестройке временно существует ещё строящаяся ячейка.",
                (renderer.CachedMatrixBytes/(1024.0*1024)).ToString("F2"));
            Metric("Буферы партий: число / МиБ","Переиспользуемые CPU-массивы по 511 матриц, максимум 128 буферов (около 4 МиБ). Это дополнительная память, не входит в лимит кэша матриц; служебные объекты не учтены.",
                renderer.BatchBufferCount+" / "+(renderer.BatchBufferCount*511*64/(1024.0*1024)).ToString("F2"));
            Metric("Обновление: сейчас / пик","Весь последний Tick: подгрузка, подготовка и порция генерации. Пик с момента сброса, не весь кадр Unity.",
                Pair(renderer.LastTickMilliseconds,renderer.PeakTickMilliseconds));
            Metric("Планирование: сейчас","Часть Tick: план ячеек, приоритет, выгрузка. Не каждый Tick выполняет полный пересчёт.",Ms(renderer.LastStreamingMilliseconds));
            Metric("Подготовка: последняя / пик","Последняя подготовка карт, мешей и источников, включая UpdatePainting. Значение сохраняется до следующей подготовки; это не расход каждого кадра. Вне бюджета генерации.",
                Pair(renderer.LastPreparationMilliseconds,renderer.PeakPreparationMilliseconds));
            Metric("Генерация: сейчас / пик","Порция генерации в последнем Tick. 0 — генерация не выполнялась. Пик с момента сброса.",
                Pair(renderer.LastGenerationMilliseconds,renderer.PeakGenerationMilliseconds));
            Metric("Отрисовка CPU: сейчас / пик","Последний проход последней камеры: отсечение, LOD, формирование и отправка партий. НЕ время GPU; пик может относиться к другой камере.",
                Pair(renderer.LastRenderMilliseconds,renderer.PeakRenderMilliseconds));
            Metric("Отбор / отправка CPU","Части времени отрисовки: отбор экземпляров и формирование/отправка партий. Уже включены в строку выше, не складывать повторно.",
                Pair(System.Math.Max(0,renderer.LastRenderMilliseconds-renderer.LastSubmissionMilliseconds),renderer.LastSubmissionMilliseconds));
            Metric("Ячейки: проверено / отсечено","В последнем проходе камеры; отсечённые ячейки не перебирают экземпляры.",renderer.LastTestedCells+" / "+renderer.LastCulledCells);
            Metric("Экземпляры: проверено / выбрано","Проверено поштучно; выбрано уникальных экземпляров для хотя бы одной части меша или тени. Не количество submesh.",
                renderer.LastTestedInstances+" / "+renderer.LastSelectedInstances);
            Metric("Отсечено / из них заранее","Всего экземпляров не отправлено. Из них заранее — целыми ячейками/группами без поштучной проверки. Проверено + заранее = загружено.",
                renderer.LastCulledInstances+" / "+renderer.LastEarlySkippedInstances);
            Metric("Частей с обычными тенями","Отправленные экземпляры частей с Cast Shadows; не число проходов/каскадов теней и не GPU-время. Контактные тени не учитываются.",
                renderer.LastShadowSubmittedInstances.ToString());
            EditorGUILayout.HelpBox("Показатели отрисовки — для последней камеры, не сумма Scene + Game. CPU не показывает стоимость GPU: её смотрите в Unity Profiler. Пики включают прогрев и перестройку; после загрузки сбросьте статистику. Маркеры Profiler: LT.Details.*.",MessageType.Info);
            if(GUILayout.Button("Сбросить статистику CPU"))renderer.ResetStatistics();
            if(GUILayout.Button("Записать профиль CPU/GPU…"))LTDetailProfileCapture.OpenFor(renderer);
            if(GUILayout.Button("Проверить Motion Vectors…"))LTDetailMotionDebug.Open();
            EditorGUILayout.HelpBox("Поверхность — исходная геометрия без GPU displacement и следов грязи. Нет физических объектов и реакции на игрока. LOD/fade дискретные; HDRP-освещение и тени проверьте в сцене.",MessageType.Info);
            if(GUILayout.Button("Перестроить детализацию"))renderer.Rebuild();
        }
        public override bool RequiresConstantRepaint()=>true;
    }
    [CustomEditor(typeof(LTDetailStamp))]
    public sealed class LTDetailStampInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject,"m_Script","id","details","detailDensityMask");
            var stamp=(LTDetailStamp)target;
            if(!stamp.world)EditorGUILayout.HelpBox("Назначьте LTWorld.",MessageType.Warning);
            if(!stamp.allLayers&&stamp.allowedLayers.Count==0)
                EditorGUILayout.HelpBox("Не выбраны разрешённые слои: штамп не будет действовать.",MessageType.Warning);
            var others=Object.FindObjectsByType<LTDetailStamp>(FindObjectsInactive.Include,FindObjectsSortMode.None);
            foreach(var other in others)if(other!=stamp&&other.Id==stamp.Id&&other.world==stamp.world)
            {
                EditorGUILayout.HelpBox("Обнаружен одинаковый ID после копирования штампа. Выдайте копии новый ID для независимой расстановки.",MessageType.Warning);
                if(GUILayout.Button("Новый ID этой копии"))
                {
                    Undo.RecordObject(stamp,"New detail stamp identity");stamp.RegenerateIdentity();EditorUtility.SetDirty(stamp);
                }
                break;
            }
            if(stamp.mode!=LTDetailStampMode.Remove)LTDetailInspector.DrawList(serializedObject);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
