using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalTerrainPrototype
{
    [CustomEditor(typeof(LTRoad))]
    public sealed class LTRoadEditor : UnityEditor.Editor
    {
        bool shapeOpen = true, surfaceOpen = true, wheelOpen=true, variationOpen=true, detailOpen, meshOpen, pointsOpen = true;
        int selectedPoint;
        bool editPath = true, ownsToolVisibility, previousToolsHidden;
        string localError, previewError;
        string adapterStatus;
        string cleanupStatus;
        LTRoadMath.Snapshot previewSnapshot;
        Vector3[] previewCenter, previewLeft, previewRight;
        readonly LTRoadDimensionHandles dimensionHandles=new LTRoadDimensionHandles();
        LTRoad Road => (LTRoad)target;
        void OnEnable()
        { Selection.selectionChanged += OnSelectionChanged; if (target) LTRoadBakeQueue.Queue(Road); }
        void OnDisable() { Selection.selectionChanged -= OnSelectionChanged; dimensionHandles.Dispose(); RestoreTools(); }
        void OnSelectionChanged() { dimensionHandles.Cancel(); RestoreTools(); }
        void RestoreTools()
        {
            if (!ownsToolVisibility) return;
            Tools.hidden = previousToolsHidden; ownsToolVisibility = false;
        }
        GUIContent Label(string text, string tip) => new GUIContent(text, tip);
        void Field(string name, string label, string tip="")
        {
            var property = serializedObject.FindProperty(name);
            if (property != null) EditorGUILayout.PropertyField(property, Label(label, tip), true);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                DrawPathToolbar();
                EditorGUI.BeginChangeCheck();
                Field("mode", "Тип дороги", "Offroad — грунтовка. Asphalt — отдельный меш. Terrain Asphalt — слой на terrain. Geometric Asphalt — полотно по сплайну, врезанное в меш чанка, со сколами и ямами.");
                Field("startJunction", "Перекрёсток в начале", "Явный узел управляет первым торцом; исходная точка сохраняется для отключения.");
                if(serializedObject.FindProperty("startJunction").objectReferenceValue is LTRoadJunction startNode&&startNode.Authored)
                    LTRoadJunctionEditor.SocketField(serializedObject.FindProperty("startJunctionSocket"),startNode,"Въезд начала");
                Field("endJunction", "Перекрёсток в конце", "Явный узел управляет последним торцом. Вторая/предпоследняя точка задаёт направление въезда.");
                if(serializedObject.FindProperty("endJunction").objectReferenceValue is LTRoadJunction endNode&&endNode.Authored)
                    LTRoadJunctionEditor.SocketField(serializedObject.FindProperty("endJunctionSocket"),endNode,"Въезд конца");
                shapeOpen = EditorGUILayout.Foldout(shapeOpen, "Форма и рельеф", true);
                if (shapeOpen)
                {
                    Field("width", "Ширина, м", "Полная ширина проезжей части.");
                    Field("shoulderWidth", "Обочина, м", "Ширина обочины с каждой стороны дороги.");
                    Field("blendWidth", "Плавный переход, м", "Ширина перехода от обочины к исходному рельефу.");
                    if(serializedObject.FindProperty("mode").enumValueIndex==(int)LTRoadMode.Offroad)
                        Field("flatten", "Выравнивание", "Сила изменения рельефа вдоль высоты и крена сплайна, от 0 до 1.");
                    Field("sampleSpacing", "Шаг вдоль пути, м", "Меньший шаг точнее описывает повороты, но увеличивает число образцов.");
                    Field("terrainCellSize", "Размер ячейки terrain, м", "Желаемая плотность terrain в области дороги.");
                    if(serializedObject.FindProperty("mode").enumValueIndex==(int)LTRoadMode.Asphalt)
                    {
                        Field("simplifyTerrainUnderAsphalt","Упрощать землю под асфальтом","Проверяет реальные треугольники всех вариантов стыковки terrain против всех запечённых LOD полотна. Без готового полотна действует прежняя защита всей зоны.");
                        if(serializedObject.FindProperty("simplifyTerrainUnderAsphalt").boolValue)
                            Field("terrainLODEdgeError","Ошибка земли у края LOD, м","Допустимое изменение высоты земли относительно LOD0 у внешнего края и отверстий асфальта. 0.03 = 3 см. Больше — свободнее упрощение; проверка выхода земли над полотном остаётся включена. 0 разрешает только практически неизменную поверхность. Исходные меши дороги и коллайдер не меняются.");
                        EditorGUILayout.HelpBox("Сначала перестройте дорогу, затем Apply Pending Changes у LT World. Края и отверстия больше не фиксируют плотную полосу навсегда: ровная земля упрощается, неровная ограничена ошибкой у края. Проверяются все LOD дороги. Без актуального читаемого полотна сохраняется подробная земля.",MessageType.Info);
                    }
                    if(serializedObject.FindProperty("mode").enumValueIndex==(int)LTRoadMode.GeometricAsphalt)
                    {
                        Field("insetCellSize", "Шаг сетки полотна, м", "Максимальный шаг собственной сетки дороги в LOD0. Меньше — плотнее. Повороты дополнительно уточняет «Шаг вдоль пути». Не изменяет плотность окружающей земли.");
                        Field("insetLodReduction", "Упрощение полотна LOD", "0 — без дополнительного упрощения полотна; 1 — обычное; 2 — сильнее. Полотно и земля упрощаются совместно с укрупнением ячеек terrain; границы соседних LOD согласованы. Коллайдер остаётся LOD0.");
                        EditorGUILayout.HelpBox("Полотно строится по сплайну, земля под ним удаляется. Полотно и земля входят в один меш чанка. Самопересечения и перекрёстки в этом режиме пока не поддерживаются; при ошибке предыдущий меш сохраняется.",MessageType.Info);
                    }
                }
                var mode = (LTRoadMode)serializedObject.FindProperty("mode").enumValueIndex;
                surfaceOpen = EditorGUILayout.Foldout(surfaceOpen, "Покрытие", true);
                if (surfaceOpen)
                {
                    bool autoGround=mode==LTRoadMode.Asphalt&&serializedObject.FindProperty("groundBlend").boolValue&&serializedObject.FindProperty("groundBlendSource").enumValueIndex==1;
                    using(new EditorGUI.DisabledScope(autoGround))
                        Field("groundLayer", "Слой terrain", "Однослойный режим красит грунт этим слоем. В автоматическом режиме назначение сохранено, но покраска дорогой отключена: читается существующая смесь terrain.");
                    if (mode == LTRoadMode.Offroad)
                    {
                        Field("pattern", "Рисунок покрытия", "Solid — сплошная полоса; Tracks — две колеи.");
                        Field("projection", "Проекция текстуры", "Проекция основного покрытия: World — общая с землёй; Spline — вдоль дороги. У следов колёс ниже есть отдельный выбор проекции.");
                        if (serializedObject.FindProperty("projection").enumValueIndex == (int)LTRoadProjection.Spline)
                        {
                            EditorGUILayout.HelpBox("Несколько дорог Spline могут использовать один слой в чанке. Этот слой нельзя одновременно использовать как фоновый или обычную кисть. Площадке перекрёстка назначьте отдельный слой без направленных колей.", MessageType.Info);
                            EditorGUILayout.HelpBox("Карта проекции: около 1 МиБ GPU + 1 МиБ CPU на слой/чанк для одной дороги; 2 + 2 МиБ для нескольких. Дополнительные дороги не занимают отдельные слоты слоёв.", MessageType.Info);
                            Field("textureRepeatMetres", "Повтор вдоль пути, м", "Расстояние вдоль сплайна на один повтор текстуры.");
                            Field("textureAcrossMetres", "Повтор поперёк, м", "0 — один повтор на всю ширину дороги, как раньше. Положительное значение — метры на один повтор поперёк. Меньше значение — мельче рисунок.");
                            Field("textureOffset", "Смещение текстуры", "Смещение в повторах текстуры: X — поперёк дороги, Y — вдоль.");
                        }
                        else EditorGUILayout.HelpBox("World: тайлинг основного покрытия берётся из Surface Layer → Tile Size Metres / Tile Offset Metres. Здесь можно использовать тот же asset, что и у земли. Проекцию и тайлинг колей выберите отдельно в «Следы колёс».",MessageType.Info);
                        if(serializedObject.FindProperty("pattern").enumValueIndex==(int)LTRoadPattern.Tracks||
                            serializedObject.FindProperty("variation.enabled").boolValue&&serializedObject.FindProperty("variation.solidRuts").boolValue)
                        {
                            Field("rutWidth", "Ширина колеи, м", "Ширина каждой полосы колеи.");
                            Field("rutSeparation", "Между колеями, м", "Расстояние между центрами колей.");
                            Field("rutDepth", "Глубина колеи, м", "Углубление колеи относительно поверхности дороги.");
                            Field("edgeNoise", "Неровность края", "Сила нерегулярности края колей.");
                            Field("noiseSize", "Размер шума, м", "Масштаб неровностей края.");
                            Field("seed", "Сид шума", "Фиксированный сид даёт воспроизводимый край.");
                        }
                    }
                    else if(mode==LTRoadMode.Asphalt)
                    {
                        Field("asphaltMaterial", "Материал асфальта", "Назначается рендереру без изменения исходного материала. При явном запекании можно создать собственный HDRP/Lit.");
                        Field("groundBlend", "Бленд с грунтом", "Подмешивает выбранный «Слой terrain» у внешнего края и вокруг сквозных отверстий. Исходные материалы и меши не изменяются.");
                        if(serializedObject.FindProperty("groundBlend").boolValue)
                        {
                            var groundSource=serializedObject.FindProperty("groundBlendSource");
                            groundSource.enumValueIndex=EditorGUILayout.Popup("Источник грунта",groundSource.enumValueIndex,new[]{"Один выбранный слой","Автоматически от terrain"});
                            EditorGUILayout.HelpBox(groundSource.enumValueIndex==1?
                                "Читает существующие слои и маски terrain, включая height blend и Spline UV. Сама дорога не красит грунт. Текстуры общие; маски пересекаемых чанков копируются на GPU без запекания цвета. LOD и UV асфальта не меняются.":
                                "Грунт под дорогой и бленд используют проекцию World с тайлингом Surface Layer. Можно назначить тот же слой, что у земли; UV самого асфальта не меняются. Настройка Spline от режима Offroad здесь не применяется.", MessageType.Info);
                            if(groundSource.enumValueIndex==1&&Road.World)
                                EditorGUILayout.HelpBox(Road.World.RoadTerrainBlendStatus,MessageType.Info);
                            Field("groundBlendWidth", "Бленд у обочины, м", "Ширина грунта внутрь асфальта. 0 — выключить внешний край.");
                            Field("groundBlendHoleWidth", "Бленд вокруг дырок, м", "Отдельная ширина вокруг сквозных отверстий LOD0. 0 — выключить.");
                            Field("groundBlendStrength", "Сила бленда", "0 — исходный асфальт; 1 — грунт полностью покрывает край.");
                            Field("groundBlendNoise", "Неровность бленда", "Неровный наплыв грунта; не меняет геометрию.");
                            Field("groundBlendNoiseSize", "Размер пятен бленда, м", "Единый рисунок в координатах земли, без повторения на стыках модулей.");
                            Field("groundBlendSeed", "Seed бленда", "Воспроизводимый рисунок пятен.");
                            Field("groundBlendMaskCell", "Шаг маски бленда, м", "Меньше — точнее мелкие отверстия, больше память. Отверстия уже двух ячеек могут не определиться. Общая маска LOD0 для всех LOD, не запекание текстур покрытия.");
                            EditorGUILayout.HelpBox("Непрозрачный бленд цвета/нормалей/AO/гладкости. Поддерживает RoadShader, новый Road_Shader.shadergraph этого проекта и обычный HDRP/Lit (UV0). Смешивание асфальта по vertex color сохраняется. Внутри настоящих дырок видна земля; LOD, закрывающий дыру, сохраняет грунтовое пятно. Высоту стыка этот режим не меняет.", MessageType.Info);
                        }
                        Field("textureRepeatMetres", "Повтор вдоль пути, м", "UV: U от 0 до 1 поперёк; V — расстояние вдоль пути / повтор. Между чанками V непрерывен.");
                    }
                    EditorGUILayout.HelpBox("Компонент кисти покрытия управляется дорогой. Настраивайте покрытие здесь.", MessageType.Info);
                }
                if(LTRoadMath.IsTerrainAsphalt(mode))
                {
                    EditorGUILayout.HelpBox(mode==LTRoadMode.GeometricAsphalt?
                        "Эксперимент: собственная сетка полотна по сплайну заменяет землю под дорогой. Один меш и материал чанка, общие LOD; коллайдер — LOD0. Покрытие использует один Surface Layer асфальта.":
                        "Эксперимент: один слой асфальта на сетке terrain. Без отдельного полотна, LOD и коллайдер общие с землёй. Назначьте отдельный Surface Layer асфальта; его можно повторно использовать на таких дорогах. Перекрёстки пока не поддерживаются.",MessageType.Info);
                    if(mode==LTRoadMode.GeometricAsphalt)
                        EditorGUILayout.HelpBox("Геометрический тест: отдельные ямы или связанные зоны износа. Ямы разрезают даже крупные треугольники. LOD упрощает полотно и разбиение стенок; общий контур пока сохраняется. Пересекающиеся повреждения разных дорог не поддерживаются. Цветовые края и латки ограничены разрешением карты весов terrain.",MessageType.Info);
                    Field("textureRepeatMetres","Повтор покрытия вдоль, м");
                    Field("textureAcrossMetres","Повтор покрытия поперёк, м","0 — один повтор на ширину дороги. Проекция всегда вдоль сплайна.");
                    Field("textureOffset","Смещение покрытия");
                    EditorGUILayout.LabelField("Повреждения асфальта",EditorStyles.boldLabel);
                    Field("asphaltDamage.strength","Общая сила повреждений");
                    Field("asphaltDamage.potholes","Количество ям","Вероятность ямы в каждой ячейке распределения; не число ям на метр.");
                    Field("asphaltDamage.spacing","Шаг распределения, м");
                    Field("asphaltDamage.size","Размер ям, м","Не более 80% шага. В Terrain Asphalt нужна мелкая сетка; Geometric Asphalt сам разрезает поверхность по контуру.");
                    Field("asphaltDamage.depth","Глубина ям, м","Меняет геометрию LOD0 и коллайдер, не shader displacement.");
                    Field("asphaltDamage.brokenEdges","Разрушение краёв");
                    if(mode==LTRoadMode.GeometricAsphalt)
                    {
                        Field("connectedDamage","Связанные зоны износа (тест)","Единые вогнутые зоны с пережимами вдоль колеи и обочин. Без широкой фаски. Выключение возвращает генератор отдельных ям; числовые настройки остаются.");
                        bool connected=serializedObject.FindProperty("connectedDamage").boolValue;
                        if(connected)
                        {
                            Field("connectedFloorAsphalt","Асфальт на дне","0 — открытый грунт. 1 — больше остатков асфальта на дне; доля различается между зонами. Не новый слой.");
                            Field("repairPatchStrength","Выраженность латок","Более тёмные участки ремонта с ослабленными трещинами. Меняет цвет и гладкость, не геометрию. 0 отключает.");
                        }
                        if(GUILayout.Button("Тест: изношенная дорога"))
                        {
                            serializedObject.FindProperty("connectedDamage").boolValue=true;
                            serializedObject.FindProperty("connectedFloorAsphalt").floatValue=.7f;
                            serializedObject.FindProperty("repairPatchStrength").floatValue=.75f;
                            serializedObject.FindProperty("asphaltDamage.strength").floatValue=1;
                            serializedObject.FindProperty("asphaltDamage.potholes").floatValue=.8f;
                            serializedObject.FindProperty("asphaltDamage.spacing").floatValue=7;
                            serializedObject.FindProperty("asphaltDamage.size").floatValue=4.5f;
                            serializedObject.FindProperty("asphaltDamage.depth").floatValue=.06f;
                            serializedObject.FindProperty("asphaltDamage.brokenEdges").floatValue=.3f;
                            serializedObject.FindProperty("asphaltDamage.cracks").floatValue=.45f;
                            connected=true;GUI.changed=true;
                        }
                        EditorGUILayout.HelpBox("Кнопка задаёт тестовые параметры повреждений (в том числе глубину 6 см), не меняет материал и плотность полотна. Undo отменяет пресет. Для сравнения используйте короткую тестовую дорогу.",MessageType.None);
                        using(new EditorGUI.DisabledScope(connected))
                        {
                        Field("pitShapeVariation","Разнообразие форм","0 — прежние контуры. Больше — сильнее различаются размер, вытянутость и поворот ям и сколов обочины; Seed сохраняет результат.");
                        Field("pitBevelWidth","Ширина скола, м","0 — прежняя отвесная кромка. Ширина немного меняется между ямами и ограничивается размером ямы. Без нового слоя или отдельного объекта.");
                        Field("pitBevelDepth","Глубина скола, м","Понижение фаски до начала стенки, не глубина всей ямы. Не больше половины глубины ямы. 0 отключает фаску.");
                        if(GUILayout.Button("Попробовать сколы и разные формы"))
                        {
                            serializedObject.FindProperty("pitBevelWidth").floatValue=.12f;
                            serializedObject.FindProperty("pitBevelDepth").floatValue=.05f;
                            serializedObject.FindProperty("pitShapeVariation").floatValue=.7f;
                            GUI.changed=true;
                        }
                        }
                    }
                    Field("asphaltDamage.cracks","Сила трещин");
                    Field("asphaltDamage.seed","Seed повреждений");
                    Field("asphaltCrackMask","Рисунок трещин","Необязательная линейная grayscale-текстура: белое = трещина. Пусто — встроенный тестовый рисунок.");
                    Field("asphaltCrackRepeat","Повтор трещин, м");
                    EditorGUILayout.HelpBox("Трещины и латки не занимают второй слот слоя. Повреждения можно ослабить до 0 у отдельных точек пути через «Своя сила вариативности». Все дороги с одним слоем должны использовать одинаковую текстуру и повтор трещин. Параметры геометрии и силы могут различаться. Дно показывает грунт или смесь с асфальтом в связанном режиме, не сквозную дыру.",MessageType.None);
                }
                if(mode==LTRoadMode.Offroad)
                {
                    DrawWheelTracks();
                    variationOpen=EditorGUILayout.Foldout(variationOpen,"Вариативность дороги",true);
                    if(variationOpen)
                    {
                        Field("variation.enabled","Включить вариативность","Выключено — прежняя форма, маски и колеи без изменений.");
                        using(new EditorGUI.DisabledScope(!serializedObject.FindProperty("variation.enabled").boolValue))
                        {
                            Field("variation.strength","Общая сила","0 — исходная дорога; 1 — полное действие настроек. Множитель также можно задать в точках пути.");
                            Field("variation.seed","Seed вариативности","Воспроизводимые пятна и края; не зависит от камеры или времени.");
                            Field("variation.widthAmount","Изменение полуширины","Независимые левые/правые края. 0.15 — до ±15% полуширины.");
                            Field("variation.widthLength","Длина изменений ширины, м","Плавные сужения и расширения вдоль пути.");
                            Field("variation.patchStrength","Подмешивание грунта","Крупные пятна открывают исходные слои под дорогой. Колеи сохраняют больше дорожного покрытия.");
                            Field("variation.patchSize","Размер пятен, м","Масштаб вдоль/поперёк дороги, независимо от тайлинга текстуры.");
                            Field("variation.solidRuts","Колеи на сплошной дороге","Добавить рельеф колей в режиме Solid. Ширина, расстояние и максимальная глубина — в Покрытии.");
                            Field("variation.rutVariation","Ослабление колей","Колеи местами становятся мельче; 1 позволяет им почти исчезать. В Tracks меняется и маска колей.");
                            Field("variation.rutLength","Длина изменений колей, м","Масштаб чередования выраженных и слабых колей.");
                        }
                        EditorGUILayout.HelpBox("Пятна смешивают дорогу с уже покрашенным грунтом, без новых слоёв. Если под дорогой такой же материал, цветовые пятна незаметны. Они не меняют форму сплайна и маску удаления травы; displacement слоёв по-прежнему зависит от их смешивания. Ширина/колеи обновляют геометрию при редактировании, не при движении камеры. У въездов в перекрёстки вариативность затухает.",MessageType.Info);
                    }
                }
                detailOpen = EditorGUILayout.Foldout(detailOpen, "Растительность и камни", true);
                if (detailOpen)
                {
                    DrawVegetationPlacement(mode);
                    if(mode==LTRoadMode.Asphalt&&serializedObject.FindProperty("clearVegetation").boolValue)
                    {
                        Field("asphaltClearMesh","Без травы под мешем","Полностью исключает точки посадки под фактическим мешем LOD0, независимо от силы удаления по сплайну. Работает с Local Terrain Detail Renderer.");
                        if(serializedObject.FindProperty("asphaltClearMesh").boolValue)
                            Field("asphaltGrassPadding","Отступ травы от меша, м","Дополнительный отступ по земле для травы/кустов, которые нависают с края. Проверяется корень растения; для больших кустов увеличьте отступ.");
                    }
                    Field("clearStones", "Убирать камни", "Удалять камни в области дороги.");
                    if(mode!=LTRoadMode.Offroad||!serializedObject.FindProperty("vegetationOnlyWheelTracks").boolValue)
                        Field("vegetationFade", "Сила удаления растительности", "Доля удаляемой растительности внутри маски, от 0 до 1. Плавный край определяется маской дороги.");
                }
                if (mode == LTRoadMode.Asphalt)
                {
                    meshOpen = EditorGUILayout.Foldout(meshOpen, "Меш асфальта", true);
                    if (meshOpen)
                    {
                        Field("meshChunkLength", "Длина чанка, м", "Приблизительная длина меша; границы проходят по образцам сплайна.");
                        Field("surfaceOffset", "Над поверхностью, м", "Лента располагается на SurfaceHeight плюс это смещение, с учётом крена.");
                        Field("asphaltSource","Источник меша","ProceduralRibbon — прежняя лента; Module — повтор и изгиб своего модуля.");
                        bool module=serializedObject.FindProperty("asphaltSource").enumValueIndex==1;
                        if(module)
                        {
                            Field("asphaltModulePrefab","Префаб модуля","Статические MeshRenderer. Если назначен, используется вместо отдельного Mesh. Масштаб корня префаба не применяется.");
                            Field("asphaltModule","Меш модуля","Используется, когда префаб не назначен. Исходный ассет не изменяется.");
                            Field("moduleMaterials","Материалы модуля","Для отдельного Mesh — по submesh. Для префаба берутся его материалы. Материал асфальта выше переопределяет все слоты, если задан.");
                            Field("moduleVariants","Набор случайных модулей","Пусто — повторять один модуль выше. Непустой список полностью заменяет его. В каждой записи назначьте префаб/FBX либо Mesh и его материалы; вес 0 исключает вариант.");
                            DrawModuleDrop();
                            if(GUILayout.Button("Добавить текущий модуль в набор"))
                            {
                                serializedObject.ApplyModifiedProperties();Undo.RecordObject(Road,"Добавить модуль дороги");
                                var variants=new List<LTRoadModuleVariant>(Road.moduleVariants??Array.Empty<LTRoadModuleVariant>());
                                variants.Add(new LTRoadModuleVariant{prefab=Road.asphaltModulePrefab,mesh=Road.asphaltModule,
                                    materials=(Material[])(Road.moduleMaterials??Array.Empty<Material>()).Clone(),lodMeshes=(Mesh[])(Road.moduleLODMeshes??Array.Empty<Mesh>()).Clone()});
                                Road.moduleVariants=variants.ToArray();EditorUtility.SetDirty(Road);serializedObject.Update();GUI.changed=true;
                            }
                            if(serializedObject.FindProperty("moduleVariants").arraySize>0)
                            {
                                Field("moduleRandomSeed","Seed порядка модулей","Один воспроизводимый порядок для всех LOD и чанков. Камера не меняет порядок.");
                                Field("moduleAvoidRepeat","Без одинаковых соседей","Не выбирать тот же вариант подряд, если доступны хотя бы два. При двух вариантах получится чередование.");
                                EditorGUILayout.HelpBox("Все модули приводятся к длине первого активного (либо заданной длине повтора). Ширина/центр и уровень Y берутся в общей системе первого модуля. Для разных сеток торцов включите «Автоматические стыки». Разные UV/материалы могут дать видимый текстурный шов.",MessageType.Info);
                            }
                            Field("moduleAxis","Продольная ось","Z или X исходного модуля; Y — вверх.");
                            Field("moduleLength","Длина повтора, м","0 — исходная длина. Все повторы равномерно подгоняются к общей длине сплайна, без обрезанного хвоста.");
                            Field("moduleFitWidth","Подогнать ширину","Масштабировать поперёк до ширины дороги. UV и цвета сохраняются.");
                            Field("moduleBaseY","Уровень покрытия в модуле","Локальный Y, совпадающий с поверхностью дороги. Обычно 0; толщина/бордюры сохраняются.");
                            Field("moduleFuseEnds","Fuse торцов","Быстрая привязка совместимых краёв ВСЕХ модулей и LOD к первому LOD0. Только координаты в пределах малого допуска, без срезов/переходных полос и без изменения UV, нормалей или треугольников. Имеет приоритет над прежними режимами.");
                            if(serializedObject.FindProperty("moduleFuseEnds").boolValue)
                            {
                                Field("moduleFuseTolerance","Допуск Fuse, м","0.0001 м = 0.1 мм. Максимальный полный сдвиг вершины в общей системе первого модуля до изгиба. Не средство исправления разной формы или разного числа точек.");
                                Field("moduleFusePlaneTolerance","Полоса поиска торца, м","Обычно 0.0001 м (0.1 мм). Только вершины настолько близко к крайней плоскости могут двигаться. Держите полосу узкой, даже если допуск поперечного сдвига больше, чтобы не захватывать соседний ряд сетки.");
                                EditorGUILayout.HelpBox("Сохраняет всю авторскую сетку, включая LOD и сколы. Нужен одинаковый контур края на всех модулях/LOD; триангуляция внутри может отличаться. UV и жёсткие нормали не свариваются. При несовместимости — понятная ошибка, без скрытой перестройки. Выключите Fuse, чтобы вернуться к прежним режимам.",MessageType.Info);
                            }
                            else
                            {
                            Field("moduleAutomaticJoints","Автоматические стыки","Отдельный режим для разной топологии торцов. Перестраивает концы сгенерированных копий ВСЕХ модулей и LOD, включая LOD0, к одному профилю. Исходные меши не меняются.");
                            if(serializedObject.FindProperty("moduleAutomaticJoints").boolValue)
                            {
                                Field("moduleJointWidth","Зона перехода, м","С каждого конца, в масштабе первого импортированного модуля; до 45% длины. Вне зоны сохраняются авторские треугольники и атрибуты. Не ширина дороги.");
                                Field("moduleJointMaxChange","Допуск формы стыка, м","Максимальное отличие поперечного сечения от общего профиля. Разрыв или другое число контуров не скрываются увеличением допуска.");
                                Field("moduleJointPrecision","Точность общего профиля, м","Допуск упрощения профиля первого LOD0. Меньше — точнее и больше вершин на всех LOD; больше — проще. Плотные полосы LOD0 целиком не копируются.");
                                EditorGUILayout.HelpBox("Один общий профиль берётся у начала сохраняемой середины первого активного модуля. LOD0 и готовые LOD сохраняются вне зоны перехода; UV/цвета на новых концах переносятся со своего модуля. Отверстия/разрывы на сечении должны быть совместимы. Текстурный шов разных материалов не устраняется автоматически.",MessageType.Info);
                            }
                            else
                            {
                            Field("moduleFitEnds","Автоподгонка торцов","Выравнивает близкие совместимые профили только в генерируемой копии. FBX, UV, материалы и количество треугольников не меняются.");
                            if(serializedObject.FindProperty("moduleFitEnds").boolValue)
                            {
                                Field("moduleEndBlendLength","Длина сглаживания торца, м","В масштабе импортированного модуля, до растяжения вдоль сплайна. Не более 45% длины с каждой стороны; середина остаётся исходной.");
                                Field("moduleEndMaxMismatch","Макс. разница торцов, м","Допуск между соответствующими точками. Каждый конец сдвигается к общему среднему профилю. Разную топологию или перевёрнутые грани подгонка не исправляет.");
                                EditorGUILayout.HelpBox("Для Road_1_Line начните с 0.5 м сглаживания и 0.05 м допуска. LOD строятся после подгонки. Текстуры на торцах должны стыковаться в самом материале/UV; подгонка исправляет геометрию.",MessageType.Info);
                            }
                            Field("moduleRestoreLODEnds","Восстанавливать торцы готовых LOD","Для несовместимых готовых LOD сохраняет узкие полосы своего LOD0, соединяя их с авторской серединой. Совместимые LOD не перетриангулирует. Только генерируемые копии — исходные ассеты не изменяются.");
                            if(serializedObject.FindProperty("moduleRestoreLODEnds").boolValue)
                                Field("moduleLODEndWidth","Торцевая зона LOD, м","На каждом конце: первая половина зоны — геометрия LOD0, вторая — соединение с готовым LOD. Вне зоны авторская геометрия сохраняется. В масштабе импортированного модуля; не больше 45% его длины.");
                            }
                            }
                        }
                        Field("asphaltLODMode","Подготовка LOD","Automatic — готовые уровни LODGroup, а без него автоупрощение. Authored — ручное количество готовых уровней. Disabled — только LOD0. Для процедурной ленты уровни генерируются.");
                        if(module&&serializedObject.FindProperty("asphaltLODMode").enumValueIndex==2)
                            Field("moduleLODMeshes","Готовые меши LOD1+","Для отдельного Mesh; при назначенном префабе используются уровни его LODGroup. Торцы всех уровней должны совпадать.");
                        Field("asphaltLODs","Дистанции / автоупрощение","Start Distance — метры. Число записей задаёт количество уровней ТОЛЬКО для автоупрощения без LODGroup и ручного Authored. В Automatic готовые уровни префаба берутся все (до LOD7). Max Height Error — допуск при автоупрощении; Simplification Steps не используется.");
                        if(module)DrawDetectedModuleLODs();
                        EditorGUILayout.HelpBox("Авто-LOD закрепляет торцы, открытые края, UV- и материальные швы. Простые/полностью разрезанные меши могут не упрощаться — фактическое число треугольников показано ниже. В меше нужны продольные сегменты для плавного изгиба; генератор не добавляет их автоматически. Коллайдер всегда LOD0.",MessageType.Info);
                        EditorGUILayout.HelpBox("Меши создаются в редакторе; старые версии сохраняются для Undo. Для перекрёстков используйте Road Junction. Его въезды пока требуют плоского торца без бордюров. Мосты не создаются автоматически.", MessageType.None);
                    }
                }
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties(); localError = null;
                    LTRoadBakeQueue.Queue(Road); SceneView.RepaintAll();
                }
                pointsOpen = EditorGUILayout.Foldout(pointsOpen, "Точки пути и крен", true);
                if (pointsOpen) DrawPoints();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(Label("Прижать к terrain", "Проекция всех точек на исходный terrain со штампами, исключая все дороги.")))
                        TryAction(ProjectPoints);
                    if (GUILayout.Button(Label("Перестроить / запечь", "Обновить дорогу сейчас и сохранить её меши. Без материала создаётся собственный HDRP/Lit.")))
                        LTRoadBakeQueue.BakeNow(Road, true);
                }
                if(Road.generatedRoot)
                {
                    int obsolete=LTRoadSegmentCleanup.Candidates(Road).Length;
                    using(new EditorGUI.DisabledScope(obsolete==0||EditorApplication.isCompiling||EditorApplication.isUpdating))
                        if(GUILayout.Button(Label($"Удалить неиспользуемые сегменты ({obsolete})","С Undo, только явно устаревшие выключенные сегменты после перестройки. Меш-ассеты, исходные модули и объекты с пользовательскими дочерними объектами/компонентами не удаляются.")))
                            TryAction(()=>
                            {
                                int removed=LTRoadSegmentCleanup.Remove(Road);
                                cleanupStatus=$"Удалено сегментов: {removed}. Можно восстановить через Undo (Ctrl+Z). Меш-ассеты не удалялись.";
                                Debug.Log("[Road Segment Cleanup] "+cleanupStatus,Road);SceneView.RepaintAll();
                            });
                    if(!string.IsNullOrEmpty(cleanupStatus))EditorGUILayout.HelpBox(cleanupStatus,MessageType.Info);
                }
                if(Road.mode==LTRoadMode.Asphalt&&Road.groundBlend)
                {
                    using(new EditorGUI.DisabledScope(LTRoadGraphAdapter.Busy||EditorApplication.isCompiling||EditorApplication.isUpdating))
                    if(GUILayout.Button(Label("Обновить адаптер бленда", "Пересоздать общий адаптер из сохранённого Road_Shader.shadergraph. Сохранит резервную копию и проверит компиляцию; при ошибке восстановит прежний адаптер. Исходный граф и материал не меняет. Затем перестройте дорогу.")))
                    {
                        // Import outside IMGUI layout; do not apply serialized
                        // settings, queue a bake or save the user's scene here.
                        EditorApplication.delayCall+=()=>
                        {
                            if(!this||!target)return;
                            try{adapterStatus=LTRoadGraphAdapter.UpdateAdapter();localError=null;LTRoadBakeQueue.ClearGroundBlendError(Road);}
                            catch(Exception e){localError=e.Message;adapterStatus=null;Debug.LogError("[Road Graph Adapter] "+e);}
                            Repaint();SceneView.RepaintAll();
                        };
                    }
                    if(!string.IsNullOrEmpty(adapterStatus))EditorGUILayout.HelpBox(adapterStatus,MessageType.Info);
                }
            }
            string error = localError ?? LTRoadBakeQueue.Error(Road) ?? previewError;
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (!Road.World) EditorGUILayout.HelpBox("Поместите дорогу внутрь нужного LTWorld.", MessageType.Warning);
            if (Road.generatedRoot)
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField("Созданные меши", Road.generatedRoot, typeof(Transform), true);
            if(!string.IsNullOrEmpty(Road.meshLODStatus))EditorGUILayout.HelpBox(Road.meshLODStatus,MessageType.Info);
        }

        void DrawDetectedModuleLODs()
        {
            // Do not apply SerializedObject just to inspect it: the normal change block owns Undo/baking.
            // The report refreshes on the next repaint after assigning a prefab.
            try
            {
                var plan=LTRoadModuleLODPlan.Create(Road);
                EditorGUILayout.HelpBox($"Результат: LOD0–LOD{plan.count}.\n"+plan.status+
                    "\nДистанции общие для дороги; проценты и Cull из LODGroup не переносятся. Автоматические стыки согласуют разную сетку торцов всех модулей и LOD. В прежнем режиме базовые торцы модулей должны быть совместимы.",MessageType.Info);
                var settings=serializedObject.FindProperty("asphaltLODs");
                for(int i=settings.arraySize;i<plan.count;i++)
                {
                    float distance=LTRoadLODPolicy.Distance(Road.asphaltLODs,i+1);
                    EditorGUI.BeginChangeCheck();
                    float edited=EditorGUILayout.FloatField(Label($"LOD{i+1}: дистанция, м (авто)","Для отсутствующей записи: стандартная дистанция или удвоенная предыдущая. Изменение сохраняет значение в настройках дороги."),distance);
                    if(!EditorGUI.EndChangeCheck())continue;
                    int oldSize=settings.arraySize;settings.arraySize=i+1;
                    for(int j=oldSize;j<=i;j++)
                    {
                        var entry=settings.GetArrayElementAtIndex(j);
                        entry.FindPropertyRelative("simplificationSteps").intValue=j+1;
                        entry.FindPropertyRelative("maxHeightError").floatValue=LTRoadLODPolicy.DefaultError(j+1);
                        entry.FindPropertyRelative("startDistance").floatValue=j==i?edited:LTRoadLODPolicy.Distance(Road.asphaltLODs,j+1);
                    }
                }
            }
            catch(InvalidOperationException e){EditorGUILayout.HelpBox(e.Message,MessageType.Warning);}
        }

        void DrawModuleDrop()
        {
            var rect=GUILayoutUtility.GetRect(0,42,GUILayout.ExpandWidth(true));GUI.Box(rect,"Перетащите несколько FBX, префабов или Mesh из Project");
            var e=Event.current;if(!rect.Contains(e.mousePosition)||(e.type!=EventType.DragUpdated&&e.type!=EventType.DragPerform))return;
            var objects=DragAndDrop.objectReferences.Where(o=>EditorUtility.IsPersistent(o)&&(o is GameObject||o is Mesh)).ToArray();
            if(objects.Length==0)return;
            DragAndDrop.visualMode=DragAndDropVisualMode.Copy;
            if(e.type==EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();serializedObject.ApplyModifiedProperties();Undo.RecordObject(Road,"Добавить модули дороги");
                var variants=new List<LTRoadModuleVariant>(Road.moduleVariants??Array.Empty<LTRoadModuleVariant>());
                foreach(var o in objects)variants.Add(new LTRoadModuleVariant{prefab=o as GameObject,mesh=o as Mesh});
                Road.moduleVariants=variants.ToArray();EditorUtility.SetDirty(Road);serializedObject.Update();GUI.changed=true;
            }
            e.Use();
        }

        void DrawVegetationPlacement(LTRoadMode mode)
        {
            if(mode!=LTRoadMode.Offroad)
            {Field("clearVegetation","Убирать растительность","Удалять растительность в области дороги.");return;}
            var clear=serializedObject.FindProperty("clearVegetation");
            var only=serializedObject.FindProperty("vegetationOnlyWheelTracks");
            int current=clear.boolValue?(only.boolValue?2:1):0;
            int next=EditorGUILayout.Popup("Растительность",current,new[]{"Не удалять","Убирать по всей дороге","Убирать только в следах"});
            if(next!=current)
            {
                clear.boolValue=next!=0;only.boolValue=next==2;
                if(next==2)serializedObject.FindProperty("wheelTracks.enabled").boolValue=true;
            }
            if(clear.boolValue&&only.boolValue)
            {
                EditorGUILayout.HelpBox("Внутри полной ширины следов места посадки исключаются, независимо от силы слоя, шума и ослабления колей. Между полосами и снаружи сохраняются правила плотности слоёв/штампов; новые растения этот режим не добавляет. Крона большого куста может нависать над следом — проверяется точка посадки.",MessageType.Info);
                if(!serializedObject.FindProperty("wheelTracks.enabled").boolValue)
                    EditorGUILayout.HelpBox("Следы колёс выключены: удаление только в следах сейчас не действует.",MessageType.Warning);
            }
        }

        void DrawWheelTracks()
        {
            wheelOpen=EditorGUILayout.Foldout(wheelOpen,"Следы колёс",true);
            if(!wheelOpen)return;
            Field("wheelTracks.enabled","Выделять следы колёс","Две полосы отдельного terrain-слоя поверх покрытия дороги. По умолчанию выключено.");
            using(new EditorGUI.DisabledScope(!serializedObject.FindProperty("wheelTracks.enabled").boolValue))
            {
                Field("wheelLayer","Слой следов","Отдельный Surface Layer: цвет, нормали и шероховатость уплотнённого грунта. Не назначайте сюда слой самого покрытия.");
                using(new EditorGUI.DisabledScope(!serializedObject.FindProperty("groundLayer").objectReferenceValue))
                    if(GUILayout.Button("Создать слой следов из покрытия…"))
                    {
                        serializedObject.ApplyModifiedProperties();
                        TryAction(CreateWheelLayer);
                        serializedObject.Update();
                    }
                Field("wheelTracks.strength","Сила смешивания","Доля слоя следов внутри полос. Не влияет на глубину колей или очистку камней.");
                Field("wheelTracks.width","Ширина одного следа, м","Ширина окрашенной полосы; независима от геометрической колеи.");
                Field("wheelTracks.separation","Между центрами следов, м","Расстояние между центрами левой и правой полосы.");
                Field("wheelTracks.softness","Мягкость краёв","Доля полуширины, занимаемая плавным переходом от полного покрытия к нулю.");
                var wheelProjection=serializedObject.FindProperty("wheelTracks.projection");
                wheelProjection.enumValueIndex=EditorGUILayout.Popup(Label("Проекция следов","Как покрытие — прежнее поведение. Spline позволяет колеям идти вдоль сплайна, даже если основной грунт использует World."),
                    wheelProjection.enumValueIndex,new[]{"Как покрытие","World","Spline"});
                var groundProjection=(LTRoadProjection)serializedObject.FindProperty("projection").enumValueIndex;
                var wheelChoice=(LTRoadWheelProjection)wheelProjection.enumValueIndex;
                bool wheelSpline=wheelChoice==LTRoadWheelProjection.Spline||wheelChoice==LTRoadWheelProjection.Inherit&&groundProjection==LTRoadProjection.Spline;
                if(wheelSpline)
                {
                    var own=serializedObject.FindProperty("wheelTracks.independentTiling");
                    bool separate=groundProjection!=LTRoadProjection.Spline;
                    bool wasOwn=own.boolValue;
                    if(separate)
                        EditorGUILayout.HelpBox("Грунт остаётся в World, колеи используют Spline и параметры ниже. Общий слой земли не копируется. Слой следов должен отличаться от грунта и не использоваться как обычная World-покраска в том же чанке.",MessageType.Info);
                    else Field("wheelTracks.independentTiling","Свой тайлинг следов","Выключено — наследовать тайлинг покрытия. Включено — отдельный размер повтора и смещение; ширина окрашенных полос не меняется.");
                    if(!wasOwn&&own.boolValue&&!serializedObject.FindProperty("wheelTilingInitialized").boolValue)
                    {
                        float across=serializedObject.FindProperty("textureAcrossMetres").floatValue;
                        if(across<=0)across=serializedObject.FindProperty("width").floatValue;
                        serializedObject.FindProperty("wheelTracks.tileSizeMetres").vector2Value=new Vector2(Mathf.Max(.01f,across),serializedObject.FindProperty("textureRepeatMetres").floatValue);
                        serializedObject.FindProperty("wheelTracks.textureOffset").vector2Value=serializedObject.FindProperty("textureOffset").vector2Value;
                        serializedObject.FindProperty("wheelTilingInitialized").boolValue=true;
                    }
                    using(new EditorGUI.DisabledScope(!separate&&!own.boolValue))
                    {
                        Field("wheelTracks.tileSizeMetres","Повтор следов, м","X — поперёк, Y — вдоль сплайна. Меньше число — чаще повтор. Общая непрерывная UV-развёртка дороги, не отдельная развёртка каждой шины.");
                        Field("wheelTracks.textureOffset","Смещение следов","В повторах текстуры: X — поперёк, Y — вдоль. Не перемещает сами полосы.");
                    }
                }
                else EditorGUILayout.HelpBox("World для следов: размер и смещение текстуры берутся из Surface Layer следов. Полосы следуют дороге, рисунок текстуры остаётся в мировых координатах.",MessageType.Info);
                Field("wheelTracks.clearStones","Убирать камни в следах","Удаляет камни внутри полос, независимо от цвета/силы слоя. Не убирает растительность.");
                if(GUILayout.Button("Растительность: оставить только вне следов"))
                {
                    serializedObject.FindProperty("clearVegetation").boolValue=true;
                    serializedObject.FindProperty("vegetationOnlyWheelTracks").boolValue=true;
                    detailOpen=true;GUI.changed=true;
                }
                if(serializedObject.FindProperty("wheelTracks.enabled").boolValue)
                {
                    var layer=serializedObject.FindProperty("wheelLayer").objectReferenceValue;
                    if(!layer||layer==serializedObject.FindProperty("groundLayer").objectReferenceValue)
                        EditorGUILayout.HelpBox("Назначьте отдельный слой следов или создайте копию кнопкой выше. Без него полосы не окрашиваются; очистка камней всё равно работает.",MessageType.Warning);
                    if(serializedObject.FindProperty("clearStones").boolValue)
                        EditorGUILayout.HelpBox("В «Растительность и камни» включено удаление камней по всей дороге. Выключите его, чтобы очищались только следы.",MessageType.Info);
                }
            }
            EditorGUILayout.HelpBox("Обе полосы используют один слой следов, с собственной проекцией или «Как покрытие». Общий World-слой земли нового слота не требует, если уже есть в чанке. Кнопка копии создаёт отдельный asset: темнее, без displacement и спавна. Рельеф колей настраивается отдельно. После изменения состава слоёв обновите сохранённые массивы в LTWorld → «Массивы». У перекрёстков следы плавно затухают.",MessageType.Info);
        }

        void CreateWheelLayer()
        {
            if(!Road.groundLayer)throw new InvalidOperationException("Сначала назначьте слой покрытия дороги.");
            string path=EditorUtility.SaveFilePanelInProject("Сохранить слой следов колёс",Road.groundLayer.name+"_WheelTracks","asset","Выберите место для отдельной копии слоя. Исходный слой и текстуры не изменятся.");
            if(string.IsNullOrEmpty(path))return;
            // Never overwrite an existing asset, even if its name was chosen in the dialog.
            path=AssetDatabase.GenerateUniqueAssetPath(path);
            var copy=Instantiate(Road.groundLayer);
            copy.name=System.IO.Path.GetFileNameWithoutExtension(path);
            var tint=copy.tint;copy.tint=new Color(tint.r*.75f,tint.g*.75f,tint.b*.75f,tint.a);
            copy.normalStrength*=.5f;copy.smoothness=Mathf.Min(copy.smoothness,.25f);copy.metallic=0;
            copy.displacement=false;copy.deformation=false;copy.details=new List<LTDetailEntry>();
            AssetDatabase.CreateAsset(copy,path);AssetDatabase.SaveAssetIfDirty(copy);
            Undo.RecordObject(Road,"Назначить слой следов колёс");
            Road.wheelLayer=copy;Road.wheelTracks.enabled=true;
            EditorUtility.SetDirty(Road);PrefabUtility.RecordPrefabInstancePropertyModifications(Road);
            LTRoadBakeQueue.Queue(Road);SceneView.RepaintAll();EditorGUIUtility.PingObject(copy);
        }

        void DrawPathToolbar()
        {
            EditorGUILayout.LabelField("Сплайн дороги", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Показать сплайн")) FramePath(false);
                using (new EditorGUI.DisabledScope(Road.points == null || Road.points.Count == 0))
                    if (GUILayout.Button("Показать выбранную точку")) FramePath(true);
            }
            bool editing = GUILayout.Toggle(editPath, "Редактировать точки в Scene", "Button");
            if (editing != editPath)
            { editPath = editing; if (!editPath) RestoreTools(); SceneView.RepaintAll(); }
            bool zones=EditorGUILayout.ToggleLeft(Label("Показывать зоны основания", "Для выбранной дороги: бирюзовая линия — край обочины, бледная — внешняя граница перехода. По сплайну, независимо от LOD и редактирования точек; Gizmos в Scene должны быть включены."),LTRoadFoundationGizmos.Visible);
            if(zones!=LTRoadFoundationGizmos.Visible)LTRoadFoundationGizmos.Visible=zones;
            dimensionHandles.DrawInspector(Road);
            EditorGUILayout.HelpBox("Перетаскивайте пронумерованные точки. Shift + клик по земле — добавить точку в конец. Alt — навигация. Чтобы двигать весь объект Road, выключите редактирование точек.", MessageType.Info);
            if (GUILayout.Button(Label("Перенести путь к объекту Road…", "Перенести начало пути к объекту и прижать точки к terrain. Форма в плане и крен сохраняются. Можно отменить через Ctrl+Z."))
                && EditorUtility.DisplayDialog("Перенести путь?", "Начало пути будет перенесено к объекту Road, высоты точек будут подогнаны к terrain. Текущее расположение дороги изменится. Действие можно отменить через Ctrl+Z.", "Перенести", "Отмена"))
                TryAction(() => {
                    var points = serializedObject.FindProperty("points");
                    if (points.arraySize == 0) throw new InvalidOperationException("Сначала добавьте точку пути.");
                    ProjectPoints(-points.GetArrayElementAtIndex(0).FindPropertyRelative("position").vector3Value);
                    selectedPoint = 0; FramePath(false);
                });
        }

        public bool HasFrameBounds() => Road && Road.points != null && Road.points.Count > 0;
        public Bounds OnGetFrameBounds() => PathBounds(Road, -1);
        internal static Bounds PathBounds(LTRoad road, int pointIndex)
        {
            var bounds = new Bounds(road.transform.position, Vector3.zero);
            if (road.points != null && road.points.Count > 0)
            {
                int index = Mathf.Clamp(pointIndex, 0, road.points.Count - 1);
                bounds = new Bounds(road.transform.TransformPoint(road.points[index].position), Vector3.zero);
                if (pointIndex < 0)
                    foreach (var point in road.points) bounds.Encapsulate(road.transform.TransformPoint(point.position));
            }
            bounds.Expand(Mathf.Max(8, road.width + 2 * road.shoulderWidth));
            return bounds;
        }
        void FramePath(bool selected)
        {
            var view = SceneView.lastActiveSceneView;
            if (!view) view = EditorWindow.GetWindow<SceneView>();
            view.drawGizmos = true;
            view.Frame(PathBounds(Road, selected ? selectedPoint : -1), false);
            view.Repaint();
        }

        void DrawPoints()
        {
            if(Road.startJunction||Road.endJunction)EditorGUILayout.HelpBox("Подключённый торец управляется узлом. Исходная точка сохранена до отключения. У префаба направление задаёт маркер въезда; вторую/предпоследнюю точку держите снаружи за прямым участком. У процедурного узла эта точка задаёт направление въезда.",MessageType.Info);
            var points = serializedObject.FindProperty("points");
            if (points.arraySize > LTRoadMesh.MaxPoints)
                EditorGUILayout.HelpBox($"Лимит точек: {LTRoadMesh.MaxPoints}. Удалите лишние точки перед запеканием.", MessageType.Error);
            // Keep an oversized externally supplied array editable without drawing thousands of controls.
            int start = Mathf.Clamp(selectedPoint / 32 * 32, 0, Mathf.Max(0, points.arraySize - 1));
            if (points.arraySize > 32)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("◀")) selectedPoint = Mathf.Max(0, start - 32);
                    EditorGUILayout.LabelField($"{start + 1}–{Math.Min(start + 32, points.arraySize)} / {points.arraySize}");
                    if (GUILayout.Button("▶")) selectedPoint = Mathf.Min(points.arraySize - 1, start + 32);
                }
            }
            for (int i = start; i < Math.Min(start + 32, points.arraySize); i++)
            {
                var point = points.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Toggle(selectedPoint == i, $"Точка {i + 1}", "Button") && selectedPoint != i)
                        { selectedPoint = i; SceneView.RepaintAll(); }
                        using (new EditorGUI.DisabledScope(points.arraySize >= LTRoadMesh.MaxPoints))
                            if (GUILayout.Button("Вставить после")) { InsertPoint(i + 1); return; }
                        if (GUILayout.Button("Удалить"))
                        {
                            points.DeleteArrayElementAtIndex(i); serializedObject.ApplyModifiedProperties();
                            selectedPoint = Mathf.Clamp(i, 0, Mathf.Max(0, points.arraySize - 1));
                            LTRoadBakeQueue.Queue(Road); SceneView.RepaintAll(); return;
                        }
                    }
                    EditorGUI.BeginChangeCheck();
                    using(new EditorGUI.DisabledScope(ConnectedNode(i)))
                    {
                        EditorGUILayout.PropertyField(point.FindPropertyRelative("position"), Label("Позиция", "Координаты относительно объекта дороги."));
                        EditorGUILayout.PropertyField(point.FindPropertyRelative("bank"), Label("Крен, °", "Поперечный наклон дороги в этой точке; интерполируется вдоль пути."));
                        if(Road.mode==LTRoadMode.Offroad&&Road.variation.enabled||LTRoadMath.IsTerrainAsphalt(Road.mode))
                        {
                            EditorGUILayout.PropertyField(point.FindPropertyRelative("overrideVariation"),Label("Своя сила вариативности","Без переопределения множитель равен 1. Между точками плавно интерполируется."));
                            if(point.FindPropertyRelative("overrideVariation").boolValue)
                                EditorGUILayout.PropertyField(point.FindPropertyRelative("variationStrength"),Label("Множитель вариативности","0 — исходная дорога в этой точке, 1 — общие настройки."));
                        }
                    }
                    if (EditorGUI.EndChangeCheck())
                    { serializedObject.ApplyModifiedProperties(); localError = null; LTRoadBakeQueue.Queue(Road); SceneView.RepaintAll(); }
                }
            }
            using (new EditorGUI.DisabledScope(points.arraySize >= LTRoadMesh.MaxPoints))
                if (GUILayout.Button("Добавить точку в конец")) { InsertPoint(points.arraySize); FramePath(true); }
        }

        void InsertPoint(int index, Vector3? placedPosition = null)
        {
            var points = serializedObject.FindProperty("points");
            if (points.arraySize >= LTRoadMesh.MaxPoints) return;
            Vector3 p = Vector3.zero; float bank = 0,variation=1;
            if (points.arraySize > 0)
            {
                var before = points.GetArrayElementAtIndex(Math.Max(0, index - 1));
                p = before.FindPropertyRelative("position").vector3Value;
                bank = before.FindPropertyRelative("bank").floatValue;
                variation=before.FindPropertyRelative("overrideVariation").boolValue?before.FindPropertyRelative("variationStrength").floatValue:1;
                if (index < points.arraySize)
                {
                    var after = points.GetArrayElementAtIndex(index);
                    p = (p + after.FindPropertyRelative("position").vector3Value) * .5f;
                    bank = (bank + after.FindPropertyRelative("bank").floatValue) * .5f;
                    variation=(variation+(after.FindPropertyRelative("overrideVariation").boolValue?after.FindPropertyRelative("variationStrength").floatValue:1))*.5f;
                }
                else if (points.arraySize > 1)
                {
                    Vector3 delta = p - points.GetArrayElementAtIndex(points.arraySize - 2).FindPropertyRelative("position").vector3Value;
                    p += delta.sqrMagnitude > .0001f ? delta.normalized * Mathf.Max(2, Road.sampleSpacing * 2) : Vector3.forward * 5;
                }
                else p += Vector3.forward * 5;
            }
            points.InsertArrayElementAtIndex(index);
            var added = points.GetArrayElementAtIndex(index);
            added.FindPropertyRelative("position").vector3Value = placedPosition ?? p;
            added.FindPropertyRelative("bank").floatValue = bank;
            added.FindPropertyRelative("overrideVariation").boolValue=variation!=1;
            added.FindPropertyRelative("variationStrength").floatValue=variation;
            serializedObject.ApplyModifiedProperties(); selectedPoint = index;
            LTRoadBakeQueue.Queue(Road); SceneView.RepaintAll();
        }

        void TryAction(Action action)
        {
            try { action(); localError = null; }
            catch (Exception exception) { localError = exception.Message; }
        }
        void ProjectPoints() => ProjectPoints(Vector3.zero);
        void ProjectPoints(Vector3 localOffset)
        {
            var world = Road.World;
            if (!world || !world.source) throw new InvalidOperationException("Для проекции нужен родительский LTWorld с исходным terrain.");
            var points = serializedObject.FindProperty("points");
            if (points.arraySize > LTRoadMesh.MaxPoints) throw new InvalidOperationException("Превышен лимит точек.");
            var projected = new Vector3[points.arraySize];
            var sampleHeight = LTEditorEngine.CreateHeightSamplerForRoad(world);
            // Evaluate everything before committing, so an error cannot partially project a path.
            for (int i = 0; i < projected.Length; i++)
            {
                Vector3 p = world.transform.InverseTransformPoint(Road.transform.TransformPoint(points.GetArrayElementAtIndex(i).FindPropertyRelative("position").vector3Value + localOffset));
                var extent=world.TerrainRect;
                if (p.x < extent.xMin || p.z < extent.yMin || p.x > extent.xMax || p.z > extent.yMax)
                    throw new InvalidOperationException($"Точка {i + 1} находится вне исходного terrain.");
                p.y = sampleHeight(p.x, p.z);
                if (!LTRoadMesh.Finite(p)) throw new InvalidOperationException($"Некорректная высота у точки {i + 1}.");
                projected[i] = Road.transform.InverseTransformPoint(world.transform.TransformPoint(p));
            }
            for (int i = 0; i < projected.Length; i++) points.GetArrayElementAtIndex(i).FindPropertyRelative("position").vector3Value = projected[i];
            serializedObject.ApplyModifiedProperties(); LTRoadBakeQueue.Queue(Road); SceneView.RepaintAll();
        }

        LTRoadJunction ConnectedNode(int index)
        {
            var node=index==0?Road.startJunction:index==Road.points.Count-1?Road.endJunction:null;
            return node&&node.isActiveAndEnabled?node:null;
        }
        void OnSceneGUI()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || Selection.activeGameObject != Road.gameObject)
            { dimensionHandles.Cancel(); RestoreTools(); return; }
            if(!editPath){RestoreTools();dimensionHandles.Draw(Road,selectedPoint);return;}
            if (!ownsToolVisibility)
            { previousToolsHidden = Tools.hidden; ownsToolVisibility = true; }
            Tools.hidden = true;
            serializedObject.Update();
            PlacePointOnTerrain();
            var points = serializedObject.FindProperty("points");
            int count = points.arraySize;
            if (count == 0 || count > LTRoadMesh.MaxPoints) return;
            selectedPoint = Mathf.Clamp(selectedPoint, 0, count - 1);
            var positions = new Vector3[count];
            for (int i = 0; i < count; i++) positions[i] = Road.transform.TransformPoint(points.GetArrayElementAtIndex(i).FindPropertyRelative("position").vector3Value);
            for(int i=0;i<count;i++)
            {
                var node=ConnectedNode(i);if(!node)continue;
                try{node.Endpoint(Road,i==0,out var point,out _);positions[i]=Road.transform.TransformPoint(point.position);}
                catch(ArgumentException){} // The inspector displays invalid connection diagnostics.
            }
            var previousDepth = Handles.zTest;
            try
            {
                Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
                using (new Handles.DrawingScope(new Color(1, .65f, .12f)))
                {
                    DrawCurve();
                    for (int i = 0; i < count; i++)
                    {
                        float size = HandleUtility.GetHandleSize(positions[i]) * .065f;
                        using (new Handles.DrawingScope(i == selectedPoint ? Color.green : Handles.color))
                            if (Handles.Button(positions[i], Quaternion.identity, size, size, Handles.SphereHandleCap))
                            { selectedPoint = i; Repaint(); }
                        Handles.Label(positions[i] + Vector3.up * size * 2, (i + 1).ToString());
                    }
                    EditorGUI.BeginChangeCheck();
                    var linked=ConnectedNode(selectedPoint);
                    if(linked)Handles.Label(positions[selectedPoint]+Vector3.up,"Торец → "+linked.name+" (перемещайте узел)");
                    Vector3 moved = linked?positions[selectedPoint]:Handles.PositionHandle(positions[selectedPoint], Tools.pivotRotation == PivotRotation.Local ? Road.transform.rotation : Quaternion.identity);
                    if (EditorGUI.EndChangeCheck())
                    {
                        points.GetArrayElementAtIndex(selectedPoint).FindPropertyRelative("position").vector3Value = Road.transform.InverseTransformPoint(moved);
                        serializedObject.ApplyModifiedProperties(); localError = null; LTRoadBakeQueue.Queue(Road); Repaint();
                    }
                    if(selectedPoint==0||selectedPoint==count-1)LTRoadJunctionScene.Draw(Road,selectedPoint==0,moved);
                }
            }
            finally { Handles.zTest = previousDepth; }
            dimensionHandles.Draw(Road,selectedPoint);
        }

        void PlacePointOnTerrain()
        {
            var e = Event.current;
            if (!e.shift || e.alt || e.control || e.command) return;
            int control = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(control);
            if (e.type != EventType.MouseDown || e.button != 0 || GUIUtility.hotControl != 0) return;
            // Only this world's generated terrain colliders; never place on vegetation or another world.
            var hits = Physics.RaycastAll(HandleUtility.GUIPointToWorldRay(e.mousePosition), Mathf.Infinity, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            bool found = false;
            foreach (var hit in hits)
            {
                if (!hit.collider.GetComponent<LTChunk>() || hit.collider.GetComponentInParent<LTWorld>() != Road.World) continue;
                TryAction(() => {
                    if (Road.points.Count >= LTRoadMesh.MaxPoints) throw new InvalidOperationException("Достигнут лимит точек дороги.");
                    InsertPoint(Road.points.Count, Road.transform.InverseTransformPoint(hit.point));
                });
                found = true; break;
            }
            if (!found) localError = "Кликните по поверхности этого LTWorld. Для размещения нужен коллайдер terrain.";
            e.Use(); Repaint(); SceneView.RepaintAll();
        }

        void DrawCurve()
        {
            if (Event.current.type != EventType.Repaint || !Road.World) return;
            try
            {
                var snapshot = LTRoadDimensionHandles.DisplaySnapshot(Road);
                if (!ReferenceEquals(snapshot, previewSnapshot))
                {
                    int count = snapshot.samples.Count;
                    if (count > LTRoadMesh.MaxSamples) throw new InvalidOperationException("Превышен лимит образцов для предпросмотра.");
                    previewCenter = new Vector3[count]; previewLeft = new Vector3[count]; previewRight = new Vector3[count];
                    for (int i = 0; i < count; i++)
                    {
                        var sample = snapshot.samples[i]; var hit = new LTRoadMath.Hit { position = sample.position, bank = sample.bank,
                            distance=sample.distance,variationStrength=sample.variationStrength,lateral=-snapshot.width*.5f };
                        float leftHalf=snapshot.HalfWidth(hit);hit.lateral=snapshot.width*.5f;float rightHalf=snapshot.HalfWidth(hit);
                        Vector3 left = sample.position - sample.right * leftHalf, right = sample.position + sample.right * rightHalf;
                        left.y = snapshot.SurfaceHeight(hit, -leftHalf); right.y = snapshot.SurfaceHeight(hit, rightHalf);
                        previewCenter[i] = Road.World.transform.TransformPoint(sample.position);
                        previewLeft[i] = Road.World.transform.TransformPoint(left); previewRight[i] = Road.World.transform.TransformPoint(right);
                    }
                    previewSnapshot = snapshot;
                }
                Handles.DrawAAPolyLine(3, previewCenter);
                using (new Handles.DrawingScope(new Color(1, .85f, .4f, .7f)))
                { Handles.DrawAAPolyLine(2, previewLeft); Handles.DrawAAPolyLine(2, previewRight); }
                previewError = null;
            }
            catch (Exception exception) { previewError = exception.Message; }
        }

        [MenuItem("GameObject/Local Terrain/Road (Offroad)", false, 15)]
        static void CreateOffroad(MenuCommand command) => Create(command, LTRoadMode.Offroad);
        [MenuItem("GameObject/Local Terrain/Road (Asphalt)", false, 16)]
        static void CreateAsphalt(MenuCommand command) => Create(command, LTRoadMode.Asphalt);
        [MenuItem("GameObject/Local Terrain/Road (Terrain Asphalt - experimental)", false, 17)]
        static void CreateTerrainAsphalt(MenuCommand command) => Create(command, LTRoadMode.TerrainAsphalt);

        [MenuItem("GameObject/Local Terrain/Road (Geometric Asphalt - experimental)",false,13)]
        static void CreateGeometricAsphalt(MenuCommand command)=>Create(command,LTRoadMode.GeometricAsphalt);
        static void Create(MenuCommand command, LTRoadMode mode)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var selected = Selection.gameObjects;
            GameObject context = command.context as GameObject;
            var candidates = (context ? new[] { context } : selected).Select(go => go.GetComponentInParent<LTWorld>()).Distinct().ToArray();
            if (candidates.Length != 1 || !candidates[0] || !candidates[0].source)
            { EditorUtility.DisplayDialog("Создание дороги", "Выберите один LTWorld с исходным terrain или его дочерний объект.", "OK"); return; }
            var world = candidates[0];
            if (EditorUtility.IsPersistent(world) || PrefabStageUtility.GetPrefabStage(world.gameObject) != null)
            { EditorUtility.DisplayDialog("Создание дороги", "Создавайте дорогу внутри LTWorld в обычной сцене.", "OK"); return; }
            try
            {
                var extent=world.TerrainRect;var size = new Vector3(extent.width,world.source.size.y,extent.height);
                if (!LTRoadMesh.Finite(size) || size.x <= .1f || size.z <= .1f) throw new InvalidOperationException("Некорректный размер исходного terrain.");
                var view = SceneView.lastActiveSceneView;
                Vector3 center = view ? world.transform.InverseTransformPoint(view.pivot) : new Vector3(extent.center.x, 0, extent.center.y);
                float half = Mathf.Min(10, size.z * .2f);
                float margin = Mathf.Min(5, size.x * .2f);
                center.x = Mathf.Clamp(center.x, extent.xMin+margin, extent.xMax-margin); center.z = Mathf.Clamp(center.z, extent.yMin+half, extent.yMax-half);
                var path = new[] { center - Vector3.forward * half, center, center + Vector3.forward * half };
                var sampleHeight = LTEditorEngine.CreateHeightSamplerForRoad(world);
                for (int i = 0; i < path.Length; i++)
                { path[i].y = sampleHeight(path[i].x, path[i].z); if (!LTRoadMesh.Finite(path[i])) throw new InvalidOperationException("Не удалось определить высоту terrain."); }
                Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Создать дорогу");
                var go = new GameObject(mode == LTRoadMode.Offroad ? "Road (Offroad)" : mode==LTRoadMode.GeometricAsphalt?"Road (Geometric Asphalt)":mode==LTRoadMode.TerrainAsphalt?"Road (Terrain Asphalt)":"Road (Asphalt)");
                Undo.RegisterCreatedObjectUndo(go, "Создать дорогу"); Undo.SetTransformParent(go.transform, world.transform, "Создать дорогу");
                go.transform.localPosition = path[1]; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;
                var road = Undo.AddComponent<LTRoad>(go); road.mode = mode;
                if(mode==LTRoadMode.GeometricAsphalt)road.terrainCellSize=2;
                var data = new SerializedObject(road); var points = data.FindProperty("points"); points.arraySize = path.Length;
                for (int i = 0; i < path.Length; i++)
                { var point = points.GetArrayElementAtIndex(i); point.FindPropertyRelative("position").vector3Value = path[i] - path[1]; point.FindPropertyRelative("bank").floatValue = 0; }
                data.ApplyModifiedProperties(); Selection.activeGameObject = go;
                LTRoadBakeQueue.BakeNow(road, true); Undo.CollapseUndoOperations(group);
                if (view) { view.drawGizmos = true; view.Frame(PathBounds(road, -1), false); }
            }
            catch (Exception exception) { EditorUtility.DisplayDialog("Создание дороги", exception.Message, "OK"); }
        }
    }

    // Detection is editor-only and cheap. Capture, mesh preparation and asset work run once
    // after the edit debounce, never while a Scene/Inspector control is being dragged.
    [InitializeOnLoad]
    public static class LTRoadBakeQueue
    {
        sealed class State { public string input, error; public bool pending; public double due; }
        static readonly Dictionary<LTRoad, State> states = new Dictionary<LTRoad, State>();
        static double nextPoll;
        static LTRoadBakeQueue()
        {
            EditorApplication.update += Tick;
            Undo.undoRedoPerformed += () => { foreach (var pair in states) if (pair.Key) Queue(pair.Key); SceneView.RepaintAll(); };
            EditorSceneManager.sceneSaving += (scene, path) => {
                foreach (var road in Roads())
                    if (road.gameObject.scene == scene && NeedsBake(road)) BakeNow(road, false);
            };
            EditorApplication.playModeStateChanged += state => {
                if (state == PlayModeStateChange.ExitingEditMode)
                    foreach (var road in Roads()) if (NeedsBake(road)) BakeNow(road, false, true);
                if (state == PlayModeStateChange.EnteredEditMode) states.Clear();
            };
        }
        static LTRoad[] Roads() => UnityEngine.Object.FindObjectsByType<LTRoad>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(Editable).ToArray();
        static bool Editable(LTRoad road) => road && road.gameObject.scene.IsValid() && road.gameObject.scene.isLoaded
            && !EditorUtility.IsPersistent(road) && !EditorSceneManager.IsPreviewScene(road.gameObject.scene) && PrefabStageUtility.GetPrefabStage(road.gameObject) == null;
        static string Input(LTRoad road) => EditorJsonUtility.ToJson(road) + "|" + road.transform.localToWorldMatrix.ToString("R")
            + "|" + (road.World ? road.World.transform.localToWorldMatrix.ToString("R") : "no-world")
            + "|" + LTRoadModuleSource.DependencyKey(road)
            + LTRoadGroundBlendBuild.DependencyKey(road)
            + "|" + (road.startJunction?road.startJunction.EndpointHash:0)+"|"+(road.endJunction?road.endJunction.EndpointHash:0)
            + "|" + LTRoadJunctionPrefab.DependencyKey(road.startJunction)+"|"+LTRoadJunctionPrefab.DependencyKey(road.endJunction)
            + "|" + ShoulderInput(road);
        static string ShoulderInput(LTRoad road)
        {
            if(!(road.startJunction&&road.startJunction.CoordinateShoulders)&&!(road.endJunction&&road.endJunction.CoordinateShoulders))return "";
            try{return road.Capture(road.World).FoundationKey;}catch(ArgumentException e){return e.Message;}
        }
        static State Get(LTRoad road)
        { if (!states.TryGetValue(road, out var state)) states.Add(road, state = new State { input = Input(road) }); return state; }
        static bool NeedsBake(LTRoad road)
        { var state = Get(road); return state.pending || state.input != Input(road); }
        public static string Error(LTRoad road) => road && states.TryGetValue(road, out var state) ? state.error : null;
        internal static void ClearGroundBlendError(LTRoad road)
        {if(road&&states.TryGetValue(road,out var state)&&state.error!=null&&state.error.StartsWith("Бленд грунта:",StringComparison.Ordinal))state.error=null;}
        public static void Queue(LTRoad road)
        {
            if (!Editable(road)) return;
            var state = Get(road); state.input = Input(road); state.pending = true; state.error = null; state.due = EditorApplication.timeSinceStartup + .4;
        }
        static void Tick()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating || LTRoadGraphAdapter.Busy
                || GUIUtility.hotControl != 0 || EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + .25;
            foreach (var dead in states.Keys.Where(road => !road).ToArray()) states.Remove(dead);
            foreach (var road in Roads())
            {
                var state = Get(road); string input = Input(road);
                if (input != state.input) Queue(road);
                if (state.pending && EditorApplication.timeSinceStartup >= state.due) BakeNow(road, false);
            }
        }
        public static void BakeNow(LTRoad road, bool allowDefaultMaterial, bool enteringPlay = false)
        {
            if (!Editable(road) || Application.isPlaying || (!enteringPlay && EditorApplication.isPlayingOrWillChangePlaymode)) return;
            var state = Get(road);
            int undoGroup = Undo.GetCurrentGroup();
            try
            {
                if (allowDefaultMaterial) Record(road, true);
                Bake(road, allowDefaultMaterial);
                // Explicit bake must also apply terrain with LTWorld.autoUpdate disabled.
                // Automatic UV/material edits only update this road's ribbon.
                // Updating one road must not synchronously save every dirty project asset.
                // Terrain meshes stay dirty for the user's normal Save; ribbon assets
                // are saved individually by the road baker.
                if (allowDefaultMaterial && road.World)
                {
                    LTEditorEngine.Refresh(road.World, false, false);
                    road.World.GetComponent<LTDetailRenderer>()?.RequestSurfaceRefresh();
                }
                state.error = null;
            }
            catch (Exception exception) { state.error = exception.Message; if(!(exception is LTRoadGroundBlendBuild.Failure)&&
                !(road.startJunction&&road.startJunction.Authored)&&!(road.endJunction&&road.endJunction.Authored))SetVisible(road, false); }
            finally
            {
                if (allowDefaultMaterial) Undo.CollapseUndoOperations(undoGroup);
                state.input = Input(road); state.pending = false; SceneView.RepaintAll();
            }
        }
        static bool Owned(Transform root, LTRoad road)
        { var marker = root ? root.GetComponent<LTRoadGenerated>() : null; return marker && marker.owner == road && root.IsChildOf(road.transform); }
        static void Record(UnityEngine.Object value, bool undo)
        { if (undo && value) Undo.RecordObject(value, "Запечь дорогу"); }
        static void SetVisible(LTRoad road, bool visible, bool undo = false)
        {
            if (Owned(road.generatedRoot, road) && road.generatedRoot.gameObject.activeSelf != visible)
            { Record(road.generatedRoot.gameObject, undo); road.generatedRoot.gameObject.SetActive(visible); EditorUtility.SetDirty(road.generatedRoot.gameObject); }
        }
        static void Bake(LTRoad road, bool allowDefaultMaterial)
        {
            if (!road.isActiveAndEnabled) { SetVisible(road, false, allowDefaultMaterial); return; }
            var world = road.World;
            if (!world || !world.source) throw new InvalidOperationException("Для запекания нужен родительский LTWorld с исходным terrain.");
            if (road.points == null || road.points.Count < 2 || road.points.Count > LTRoadMesh.MaxPoints)
                throw new InvalidOperationException($"Для дороги требуется от 2 до {LTRoadMesh.MaxPoints} точек.");
            var snapshot = road.Capture(world);
            if (road.mode != LTRoadMode.Asphalt) { SetVisible(road, false, allowDefaultMaterial); return; }
            if (road.generatedRoot && !Owned(road.generatedRoot, road))
                throw new InvalidOperationException("Ссылка generatedRoot не принадлежит этой дороге. Чужой объект не изменён.");
            if (road.asphaltSource==LTRoadMeshSource.ProceduralRibbon && !road.asphaltMaterial && !allowDefaultMaterial)
                throw new InvalidOperationException("Назначьте материал асфальта или нажмите «Перестроить / запечь» для создания собственного HDRP/Lit.");
            var prepared=LTRoadModuleSource.Build(road,snapshot);
            LTRoadJunctionMesh.ValidateRoadEnds(road,prepared);
            var chunks=prepared.levels[0];
            using var groundBlend=LTRoadGroundBlendBuild.Prepare(road,prepared);
            // Fully validate and prepare the geometry before creating any assets or scene objects.
            string folder = EnsureFolder(road);
            if (road.asphaltSource==LTRoadMeshSource.ProceduralRibbon && !road.asphaltMaterial)
            {
                var shader = Shader.Find("HDRP/Lit");
                if (!shader) throw new InvalidOperationException("HDRP/Lit недоступен. Назначьте подходящий материал вручную.");
                var material = new Material(shader) { name = "Road Asphalt" };
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", new Color(.08f, .08f, .08f, 1));
                if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", .25f);
                AssetDatabase.CreateAsset(material, AssetDatabase.GenerateUniqueAssetPath(folder + "/Asphalt.mat"));
                road.asphaltMaterial = material; AssetDatabase.SaveAssetIfDirty(material);
                prepared.materials[0]=material;
            }
            if(groundBlend!=null)LTRoadGroundBlendBuild.Save(road,groundBlend,folder);
            if (!road.generatedRoot)
            {
                // Undo may have restored the serialized reference before generated children existed.
                foreach (Transform child in road.transform)
                    if (Owned(child, road) && child.name == "Road Generated") { road.generatedRoot = child; break; }
                if (!road.generatedRoot) road.generatedRoot = NewOwned("Road Generated", road.transform, road, allowDefaultMaterial);
            }
            var root = road.generatedRoot;
            Record(root, allowDefaultMaterial);
            root.localPosition = Vector3.zero; root.localRotation = Quaternion.identity; root.localScale = Vector3.one;
            var used = new HashSet<Transform>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var data = chunks[i]; string name = $"Segment_{i:D4}"; Transform segment = null;
                foreach (Transform child in root) if (child.name == name && Owned(child, road)) { segment = child; break; }
                if (!segment) segment = NewOwned(name, root, road, allowDefaultMaterial);
                Record(segment, allowDefaultMaterial); Record(segment.gameObject, allowDefaultMaterial);
                used.Add(segment); segment.localPosition = Vector3.zero; segment.localRotation = Quaternion.identity; segment.localScale = Vector3.one;
                var filter = Component<MeshFilter>(segment, allowDefaultMaterial);
                var renderer = Component<MeshRenderer>(segment, allowDefaultMaterial);
                var collider = Component<MeshCollider>(segment, allowDefaultMaterial);
                var lod=Component<LTRoadLOD>(segment,allowDefaultMaterial);Record(lod,allowDefaultMaterial);
                Record(filter, allowDefaultMaterial); Record(renderer, allowDefaultMaterial); Record(collider, allowDefaultMaterial);
                var meshes=new Mesh[prepared.levels.Length];
                for(int level=0;level<meshes.Length;level++)meshes[level]=SaveChunk(prepared.levels[level][i],folder);
                Mesh mesh=meshes[0];filter.sharedMesh=mesh;lod.owner=road;lod.meshes=meshes;lod.bounds=mesh.bounds;lod.current=0;
                foreach(var other in meshes)lod.bounds.Encapsulate(other.bounds);
                if (collider.sharedMesh != mesh) { collider.sharedMesh = null; collider.sharedMesh = mesh; }
                renderer.sharedMaterials=groundBlend!=null?groundBlend.materials:prepared.materials;
                var binding=segment.GetComponent<LTRoadGroundBlend>();
                if(groundBlend!=null)
                {
                    binding=Component<LTRoadGroundBlend>(segment,allowDefaultMaterial);Record(binding,allowDefaultMaterial);
                    binding.owner=road;binding.junction=null;binding.roadToMask=groundBlend.roadToMask;binding.enabled=true;binding.Refresh();
                    EditorUtility.SetDirty(binding);PrefabUtility.RecordPrefabInstancePropertyModifications(binding);
                }
                else if(binding){Record(binding,allowDefaultMaterial);binding.enabled=false;EditorUtility.SetDirty(binding);}
                renderer.enabled = true; collider.enabled = true; segment.gameObject.SetActive(true);
                EditorUtility.SetDirty(filter); EditorUtility.SetDirty(renderer); EditorUtility.SetDirty(collider);
                EditorUtility.SetDirty(lod);PrefabUtility.RecordPrefabInstancePropertyModifications(lod);
                PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
            }
            // Publish cleanup eligibility only after all segments were updated successfully.
            foreach(var segment in used)LTRoadSegmentCleanup.Mark(road,segment,false,allowDefaultMaterial);
            // Retain obsolete owned segments for Undo/reuse. The explicit cleanup button
            // removes only marked inactive output, never arbitrary user children or assets.
            foreach (Transform child in root)
                if (Owned(child, road) && !used.Contains(child))
                {
                    if(child.gameObject.activeSelf){Record(child.gameObject, allowDefaultMaterial); child.gameObject.SetActive(false);}
                    LTRoadSegmentCleanup.Mark(road,child,true,allowDefaultMaterial);
                }
            road.bakedGeometryHash = snapshot.geometryHash; SetVisible(road, true, allowDefaultMaterial);
            road.meshLODStatus=prepared.status;world.RefreshLODCache();
            if(groundBlend!=null)road.meshLODStatus+=$"\nБленд грунта: маска LOD0 {groundBlend.mask.width} × {groundBlend.mask.height}, шаг {groundBlend.mask.cell:G3} м; {(groundBlend.mask.holePixels>0?"отверстия обнаружены":"сквозных отверстий не обнаружено")}. Общая для всех LOD.";
            world.GetComponent<LTDetailRenderer>()?.RequestSurfaceRefresh();
            EditorUtility.SetDirty(road); PrefabUtility.RecordPrefabInstancePropertyModifications(road);
            EditorSceneManager.MarkSceneDirty(road.gameObject.scene);
        }
        internal static Mesh SaveChunk(LTRoadMesh.Chunk data,string folder)
        {
            string meshName="RoadChunk_"+data.hash,path=folder+"/"+meshName+".asset";
            var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);if(mesh&&mesh.name==meshName)return mesh;
            mesh=new Mesh{name=meshName,indexFormat=data.vertices.Length>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16};
            mesh.vertices=data.vertices;mesh.normals=data.normals;mesh.tangents=data.tangents;mesh.uv=data.uv;
            if(data.uv2!=null)mesh.uv2=data.uv2;if(data.uv3!=null)mesh.uv3=data.uv3;if(data.uv4!=null)mesh.uv4=data.uv4;if(data.colors!=null)mesh.colors=data.colors;
            if(data.submeshes==null)mesh.triangles=data.triangles;
            else{mesh.subMeshCount=data.submeshes.Length;for(int s=0;s<data.submeshes.Length;s++)mesh.SetTriangles(data.submeshes[s],s,false);}
            mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,AssetDatabase.GenerateUniqueAssetPath(path));AssetDatabase.SaveAssetIfDirty(mesh);return mesh;
        }
        static T Component<T>(Transform transform, bool undo) where T : UnityEngine.Component
        {
            var component = transform.GetComponent<T>();
            return component ? component : undo ? Undo.AddComponent<T>(transform.gameObject) : transform.gameObject.AddComponent<T>();
        }
        static Transform NewOwned(string name, Transform parent, LTRoad road, bool undo)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var marker = go.AddComponent<LTRoadGenerated>(); marker.owner = road; marker.bakeId = road.bakeId;
            if (undo) Undo.RegisterCreatedObjectUndo(go, "Запечь дорогу");
            return go.transform;
        }
        static string EnsureFolder(LTRoad road)
        {
            const string parent = "Assets/LocalTerrainRoads";
            if (!AssetDatabase.IsValidFolder(parent) && string.IsNullOrEmpty(AssetDatabase.CreateFolder("Assets", "LocalTerrainRoads")))
                throw new InvalidOperationException("Не удалось создать Assets/LocalTerrainRoads. Проверьте доступ к папке.");
            bool valid = Guid.TryParseExact(road.bakeId, "N", out _);
            bool duplicate = valid && Roads().Any(other => other != road && other.bakeId == road.bakeId);
            if (!valid || duplicate)
            { road.bakeId = Guid.NewGuid().ToString("N"); EditorUtility.SetDirty(road); }
            string folder = parent + "/" + road.bakeId;
            if (!AssetDatabase.IsValidFolder(folder) && string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, road.bakeId)))
                throw new InvalidOperationException("Не удалось создать собственную папку ассетов дороги.");
            return folder;
        }
    }
}
