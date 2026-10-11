using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public static class LTLayerRenderEditor
    {
        static void MarkChanged(LTWorld world)
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(world);
            EditorUtility.SetDirty(world);EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            world.RefreshLayerRenderMode();SceneView.RepaintAll();
        }
        public static void Draw(LTWorld world)
        {
            EditorGUILayout.LabelField("Отрисовка слоёв",EditorStyles.boldLabel);
            using var settings=new SerializedObject(world);settings.Update();
            var mode=settings.FindProperty("surfaceRenderMode");
            int current=mode.intValue==(int)LTSurfaceRenderMode.FastLiveLayers?1:0;
            EditorGUI.BeginChangeCheck();
            int selected=EditorGUILayout.Popup("Режим",current,new[]{"Обычные слои","Обычные слои — быстрые"});
            if(selected!=current)mode.intValue=(int)(selected==1?LTSurfaceRenderMode.FastLiveLayers:LTSurfaceRenderMode.LiveLayers);
            void Field(string name,string label)=>EditorGUILayout.PropertyField(settings.FindProperty(name),new GUIContent(label));
            if(selected==1)
            {
                EditorGUILayout.HelpBox("Живые слои без кэша: цвет и нормали читаются только для оставшихся вкладов. Разрешение и тайлинг текстур не меняются.",MessageType.Info);
                Field("fastLayerTopTwo","Только 2 слоя");
                bool topTwo=settings.FindProperty("fastLayerTopTwo").boolValue;
                using(new EditorGUI.DisabledScope(!topTwo))
                    Field("fastLayerTopTwoBeforeHeight","Выбирать 2 слоя до высот");
                bool beforeHeight=topTwo&&settings.FindProperty("fastLayerTopTwoBeforeHeight").boolValue;
                using(new EditorGUI.DisabledScope(!beforeHeight))
                    Field("fastLayerColorRemainder","Остальные слои — только цвет");
                bool colorRemainder=beforeHeight&&settings.FindProperty("fastLayerColorRemainder").boolValue;
                using(new EditorGUI.DisabledScope(topTwo))
                    Field("fastLayerBlendBudget","Допуск упрощения смешивания");
                Field("fastLayerProjectionCutoff","Отсечение слабых проекций");
                EditorGUILayout.HelpBox(topTwo
                    ?beforeHeight
                        ?"До высот: пара выбирается один раз по весам покраски до проекций. Высоты, нормали и свойства поверхности рассчитываются только для неё, без промежуточных массивов на 12 слоёв. Исходные веса сохраняются до Height Blend. Допуск упрощения не используется."
                        :"После высот: маски читаются для всех присутствующих слоёв, затем выбираются два наибольших вклада. Цвет и нормали читаются только для пары. При трипланаре пара выбирается отдельно для каждой проекции. Допуск упрощения не используется."
                    :"Флажок выключен: прежнее плавное отсечение слабых примесей, значимые тройные стыки сохраняются. Старт: 0,03 / 0,025. При нулевых допусках остаются исходные веса и трипланар.",MessageType.None);
                if(topTwo)EditorGUILayout.HelpBox("На стыках трёх и более покрытий смена пары может быть заметна. Автоматический A/B сравнивает выбор после высот и до высот. Дальний атлас не меняется.",MessageType.None);
                if(colorRemainder)EditorGUILayout.HelpBox("Два основных слоя сохраняют Height Blend и нормали. Остальные добавляют только цвет с исходным тайлингом и весами покраски; без собственных высот, нормалей, AO, гладкости и трещин асфальта. Это сохранение цветовых примесей, не ускорение относительно строгой пары. В F8 это режим 5. Отдельный автоматический A/B после/до высот временно выключает примесь в обоих вариантах.",MessageType.Info);
                EditorGUILayout.HelpBox("Быстрые слои работают на terrain, spline-дорогах, асфальте и скалах нашей системы. При displacement ускоряется окраска; смещение геометрии рассчитывается по всем слоям. Дальний атлас и LitBlend не меняются.",MessageType.None);
                EditorGUILayout.LabelField(world.FastLiveLayersStatus,EditorStyles.wordWrappedLabel);
            }
            bool changed=EditorGUI.EndChangeCheck();settings.ApplyModifiedProperties();
            if(changed)MarkChanged(world);
            using(new EditorGUI.DisabledScope(EditorApplication.isCompiling||Application.isPlaying||world.paintBenchmarkRunning))
                if(GUILayout.Button("A/B: 2 слоя после высот ↔ до высот…"))LTFastLayerBenchmark.Open(world);
            if(world.FastLiveLayersActive)
                using(new EditorGUI.DisabledScope(EditorApplication.isCompiling||Application.isPlaying))
                    if(GUILayout.Button("Проверить быстрые слои (без изменений сцены)"))LTCoverageSamplingCheck.RunFast();
            EditorGUILayout.Space(8);
        }
    }
}
