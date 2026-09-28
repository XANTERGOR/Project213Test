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
        LTRoadMath.Snapshot previewSnapshot;
        Vector3[] previewCenter, previewLeft, previewRight;
        LTRoad Road => (LTRoad)target;
        void OnEnable()
        { Selection.selectionChanged += OnSelectionChanged; if (target) LTRoadBakeQueue.Queue(Road); }
        void OnDisable() { Selection.selectionChanged -= OnSelectionChanged; RestoreTools(); }
        void OnSelectionChanged() { RestoreTools(); }
        void RestoreTools()
        {
            if (!ownsToolVisibility) return;
            Tools.hidden = previousToolsHidden; ownsToolVisibility = false;
        }
        GUIContent Label(string text, string tip) => new GUIContent(text, tip);
        void Field(string name, string label, string tip)
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
                Field("mode", "Тип дороги", "Offroad изменяет terrain. Asphalt дополнительно создаёт отдельную ленту с коллайдером.");
                Field("startJunction", "Перекрёсток в начале", "Явный узел управляет первым торцом; исходная точка сохраняется для отключения.");
                Field("endJunction", "Перекрёсток в конце", "Явный узел управляет последним торцом. Вторая/предпоследняя точка задаёт направление въезда.");
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
                }
                var mode = (LTRoadMode)serializedObject.FindProperty("mode").enumValueIndex;
                surfaceOpen = EditorGUILayout.Foldout(surfaceOpen, "Покрытие", true);
                if (surfaceOpen)
                {
                    Field("groundLayer", "Слой terrain", "Исходный слой покрытия. Редактор дороги не изменяет его текстуры или материал.");
                    if (mode == LTRoadMode.Offroad)
                    {
                        Field("pattern", "Рисунок покрытия", "Solid — сплошная полоса; Tracks — две колеи.");
                        Field("projection", "Проекция текстуры", "World — мировая проекция; Spline — текстура следует вдоль дороги на поверхности terrain.");
                        if (serializedObject.FindProperty("projection").enumValueIndex == (int)LTRoadProjection.Spline)
                        {
                            EditorGUILayout.HelpBox("Несколько дорог Spline могут использовать один слой в чанке. Этот слой нельзя одновременно использовать как фоновый или обычную кисть. Площадке перекрёстка назначьте отдельный слой без направленных колей.", MessageType.Info);
                            EditorGUILayout.HelpBox("Карта проекции: около 1 МиБ GPU + 1 МиБ CPU на слой/чанк для одной дороги; 2 + 2 МиБ для нескольких. Дополнительные дороги не занимают отдельные слоты слоёв.", MessageType.Info);
                            Field("textureRepeatMetres", "Повтор вдоль пути, м", "Расстояние вдоль сплайна на один повтор текстуры.");
                            Field("textureAcrossMetres", "Повтор поперёк, м", "0 — один повтор на всю ширину дороги, как раньше. Положительное значение — метры на один повтор поперёк. Меньше значение — мельче рисунок.");
                            Field("textureOffset", "Смещение текстуры", "Смещение в повторах текстуры: X — поперёк дороги, Y — вдоль.");
                        }
                        else EditorGUILayout.HelpBox("World: тайлинг покрытия и следов настраивается независимо в их Surface Layer → Tile Size Metres / Tile Offset Metres. Настройки Spline ниже при этом не используются.",MessageType.Info);
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
                    else
                    {
                        Field("asphaltMaterial", "Материал асфальта", "Назначается рендереру без изменения исходного материала. При явном запекании можно создать собственный HDRP/Lit.");
                        Field("textureRepeatMetres", "Повтор вдоль пути, м", "UV: U от 0 до 1 поперёк; V — расстояние вдоль пути / повтор. Между чанками V непрерывен.");
                    }
                    EditorGUILayout.HelpBox("Компонент кисти покрытия управляется дорогой. Настраивайте покрытие здесь.", MessageType.Info);
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
                            Field("moduleAxis","Продольная ось","Z или X исходного модуля; Y — вверх.");
                            Field("moduleLength","Длина повтора, м","0 — исходная длина. Все повторы равномерно подгоняются к общей длине сплайна, без обрезанного хвоста.");
                            Field("moduleFitWidth","Подогнать ширину","Масштабировать поперёк до ширины дороги. UV и цвета сохраняются.");
                            Field("moduleBaseY","Уровень покрытия в модуле","Локальный Y, совпадающий с поверхностью дороги. Обычно 0; толщина/бордюры сохраняются.");
                        }
                        Field("asphaltLODMode","Подготовка LOD","Automatic — упрощение; Authored — готовые меши/LODGroup; Disabled — только LOD0. Для процедурной ленты уровни всегда генерируются.");
                        if(module&&serializedObject.FindProperty("asphaltLODMode").enumValueIndex==2)
                            Field("moduleLODMeshes","Меши LOD1–LOD3","Для отдельного Mesh; при назначенном префабе используются уровни его LODGroup. Торцы всех уровней должны совпадать.");
                        Field("asphaltLODs","Уровни и дистанции","Start Distance — метры; Max Height Error — допуск к исходным плоскостям при автоупрощении. Цели LOD1/2/3 — 50/25/12.5% треугольников, если позволяют защищённые швы. Simplification Steps для асфальта не используется.");
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
            }
            string error = localError ?? LTRoadBakeQueue.Error(Road) ?? previewError;
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (!Road.World) EditorGUILayout.HelpBox("Поместите дорогу внутрь нужного LTWorld.", MessageType.Warning);
            if (Road.generatedRoot)
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField("Созданные меши", Road.generatedRoot, typeof(Transform), true);
            if(!string.IsNullOrEmpty(Road.meshLODStatus))EditorGUILayout.HelpBox(Road.meshLODStatus,MessageType.Info);
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
                if(serializedObject.FindProperty("projection").enumValueIndex==(int)LTRoadProjection.Spline)
                {
                    var own=serializedObject.FindProperty("wheelTracks.independentTiling");
                    bool wasOwn=own.boolValue;
                    Field("wheelTracks.independentTiling","Свой тайлинг следов","Выключено — наследовать тайлинг покрытия. Включено — отдельный размер повтора и смещение; ширина окрашенных полос не меняется.");
                    if(!wasOwn&&own.boolValue&&!serializedObject.FindProperty("wheelTilingInitialized").boolValue)
                    {
                        float across=serializedObject.FindProperty("textureAcrossMetres").floatValue;
                        if(across<=0)across=serializedObject.FindProperty("width").floatValue;
                        serializedObject.FindProperty("wheelTracks.tileSizeMetres").vector2Value=new Vector2(Mathf.Max(.01f,across),serializedObject.FindProperty("textureRepeatMetres").floatValue);
                        serializedObject.FindProperty("wheelTracks.textureOffset").vector2Value=serializedObject.FindProperty("textureOffset").vector2Value;
                        serializedObject.FindProperty("wheelTilingInitialized").boolValue=true;
                    }
                    using(new EditorGUI.DisabledScope(!own.boolValue))
                    {
                        Field("wheelTracks.tileSizeMetres","Повтор следов, м","X — поперёк, Y — вдоль сплайна. Меньше число — чаще повтор. Общая непрерывная UV-развёртка дороги, не отдельная развёртка каждой шины.");
                        Field("wheelTracks.textureOffset","Смещение следов","В повторах текстуры: X — поперёк, Y — вдоль. Не перемещает сами полосы.");
                    }
                }
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
            EditorGUILayout.HelpBox("Обе полосы занимают один дополнительный слот из 12 и используют проекцию покрытия. Копия будет темнее, с менее выраженными нормалями, без displacement и правил спавна. Рельеф колей настраивается отдельно. После добавления нового слоя обновите сохранённые массивы в LTWorld → «Массивы». У подключённых перекрёстков следы плавно затухают.",MessageType.Info);
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
            if(Road.startJunction||Road.endJunction)EditorGUILayout.HelpBox("Подключённый торец управляется узлом. Его исходная точка сохранена, но не используется до отключения. Направление въезда меняется второй/предпоследней точкой.",MessageType.Info);
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
                        if(Road.mode==LTRoadMode.Offroad&&Road.variation.enabled)
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
                if (p.x < 0 || p.z < 0 || p.x > world.source.size.x || p.z > world.source.size.z)
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
            if (EditorApplication.isPlayingOrWillChangePlaymode || !editPath || Selection.activeGameObject != Road.gameObject)
            { RestoreTools(); return; }
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
                }
            }
            finally { Handles.zTest = previousDepth; }
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
                var snapshot = Road.Capture(Road.World);
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
                var size = world.source.size;
                if (!LTRoadMesh.Finite(size) || size.x <= .1f || size.z <= .1f) throw new InvalidOperationException("Некорректный размер исходного terrain.");
                var view = SceneView.lastActiveSceneView;
                Vector3 center = view ? world.transform.InverseTransformPoint(view.pivot) : new Vector3(size.x * .5f, 0, size.z * .5f);
                float half = Mathf.Min(10, size.z * .2f);
                float margin = Mathf.Min(5, size.x * .2f);
                center.x = Mathf.Clamp(center.x, margin, size.x - margin); center.z = Mathf.Clamp(center.z, half, size.z - half);
                var path = new[] { center - Vector3.forward * half, center, center + Vector3.forward * half };
                var sampleHeight = LTEditorEngine.CreateHeightSamplerForRoad(world);
                for (int i = 0; i < path.Length; i++)
                { path[i].y = sampleHeight(path[i].x, path[i].z); if (!LTRoadMesh.Finite(path[i])) throw new InvalidOperationException("Не удалось определить высоту terrain."); }
                Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Создать дорогу");
                var go = new GameObject(mode == LTRoadMode.Offroad ? "Road (Offroad)" : "Road (Asphalt)");
                Undo.RegisterCreatedObjectUndo(go, "Создать дорогу"); Undo.SetTransformParent(go.transform, world.transform, "Создать дорогу");
                go.transform.localPosition = path[1]; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;
                var road = Undo.AddComponent<LTRoad>(go); road.mode = mode;
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
            && !EditorUtility.IsPersistent(road) && PrefabStageUtility.GetPrefabStage(road.gameObject) == null;
        static string Input(LTRoad road) => EditorJsonUtility.ToJson(road) + "|" + road.transform.localToWorldMatrix.ToString("R")
            + "|" + (road.World ? road.World.transform.localToWorldMatrix.ToString("R") : "no-world")
            + "|" + LTRoadModuleSource.DependencyKey(road)
            + "|" + (road.startJunction?road.startJunction.EndpointHash:0)+"|"+(road.endJunction?road.endJunction.EndpointHash:0);
        static State Get(LTRoad road)
        { if (!states.TryGetValue(road, out var state)) states.Add(road, state = new State { input = Input(road) }); return state; }
        static bool NeedsBake(LTRoad road)
        { var state = Get(road); return state.pending || state.input != Input(road); }
        public static string Error(LTRoad road) => road && states.TryGetValue(road, out var state) ? state.error : null;
        public static void Queue(LTRoad road)
        {
            if (!Editable(road)) return;
            var state = Get(road); state.input = Input(road); state.pending = true; state.error = null; state.due = EditorApplication.timeSinceStartup + .4;
        }
        static void Tick()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating
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
            catch (Exception exception) { state.error = exception.Message; SetVisible(road, false); }
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
                renderer.sharedMaterials=prepared.materials;
                renderer.enabled = true; collider.enabled = true; segment.gameObject.SetActive(true);
                EditorUtility.SetDirty(filter); EditorUtility.SetDirty(renderer); EditorUtility.SetDirty(collider);
                EditorUtility.SetDirty(lod);PrefabUtility.RecordPrefabInstancePropertyModifications(lod);
                PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
            }
            // Retain obsolete owned segments for Undo/reuse. Never delete user children or asset files.
            foreach (Transform child in root)
                if (Owned(child, road) && !used.Contains(child) && child.gameObject.activeSelf)
                { Record(child.gameObject, allowDefaultMaterial); child.gameObject.SetActive(false); }
            road.bakedGeometryHash = snapshot.geometryHash; SetVisible(road, true, allowDefaultMaterial);
            road.meshLODStatus=prepared.status;world.RefreshLODCache();
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
