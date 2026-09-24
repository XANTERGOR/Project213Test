using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace LocalTerrainPrototype
{
    // Editor-only viewer: does not enable motion passes, alter assets or force camera settings.
    public sealed class LTDetailMotionDebug : EditorWindow
    {
        DebugDisplaySettings ownedSettings;
        FullScreenDebugMode previousMode, appliedMode;
        int view;
        string message;

        [MenuItem("Tools/Local Terrain/Detail Motion Vectors")]
        public static void Open()
        {
            var window=GetWindow<LTDetailMotionDebug>("Detail Motion Vectors");
            window.minSize=new Vector2(440,330);
        }
        static DebugDisplaySettings CurrentSettings =>
            (RenderPipelineManager.currentPipeline as HDRenderPipeline)?.debugDisplaySettings;

        void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload+=Restore;
            EditorApplication.playModeStateChanged+=PlayModeChanged;
        }
        void OnDisable()
        {
            Restore();
            AssemblyReloadEvents.beforeAssemblyReload-=Restore;
            EditorApplication.playModeStateChanged-=PlayModeChanged;
        }
        void PlayModeChanged(PlayModeStateChange state)=>Restore();
        void OnInspectorUpdate()
        {
            // Do not fight another Debugger window or reapply settings after a pipeline change.
            if(ownedSettings!=null && (CurrentSettings!=ownedSettings ||
                ownedSettings.data.fullScreenDebugMode!=appliedMode))Restore();
            Repaint();
        }
        void OnGUI()
        {
            EditorGUILayout.HelpBox("Просмотр буфера HDRP, общий для камер. Не меняет velocity, материалы, сглаживание или настройки сцены. Закрытие окна возвращает прежний режим просмотра.",MessageType.Info);
            EditorGUILayout.LabelField("Проверка мерцания",EditorStyles.boldLabel);
            EditorGUILayout.LabelField("1. Включите Play Mode и оставьте рядом обычный префаб и нашу траву с тем же материалом.\n2. Остановите камеру и выключите ветер. Не ставьте игру на паузу.\n3. Включите просмотр и сравните обе области несколько секунд.",EditorStyles.wordWrappedLabel);
            view=EditorGUILayout.Popup(new GUIContent("Просмотр","Направление — цвет и стрелки HDRP; сила — интенсивность вектора. Это визуализация, не изменение скорости."),view,
                new[]{"Направление — Motion Vectors","Сила — Motion Vectors Intensity"});
            using(new EditorGUI.DisabledScope(CurrentSettings==null))
                if(GUILayout.Button("Показать Motion Vectors в Game"))ShowVectors();
            using(new EditorGUI.DisabledScope(ownedSettings==null))
                if(GUILayout.Button("Вернуть обычную картинку"))Restore();
            if(CurrentSettings==null)EditorGUILayout.HelpBox("Активный HDRP ещё не найден. Откройте Game/Scene и дождитесь инициализации рендеринга.",MessageType.Warning);
            if(!string.IsNullOrEmpty(message))EditorGUILayout.HelpBox(message,MessageType.Info);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("На неподвижной геометрии без ветра ожидаются близкие к нулю векторы. Меняющиеся цвета/стрелки только на инстансах — повод проверять их motion-проход. Пустой буфер НЕ доказывает исправность: проверьте, включены ли Motion Vectors в HDRP, камере и материале. Этот инструмент их принудительно не включает.",MessageType.Warning);
            EditorGUILayout.LabelField("Снимите короткое видео Game: обычная картинка → направление → сила. По одному скриншоту нельзя подтвердить мерцание.",EditorStyles.wordWrappedLabel);
        }
        void ShowVectors()
        {
            var settings=CurrentSettings;
            if(settings==null)return;
            if(ownedSettings!=null && (settings!=ownedSettings || settings.data.fullScreenDebugMode!=appliedMode))Restore();
            if(ownedSettings==null)
            {
                // SetFullScreenDebugMode also resets material/lighting/history debug states.
                // Refuse conflicting modes rather than silently destroying the user's setup.
                if(settings.IsDebugDisplayEnabled() || settings.data.historyBuffersView!=-1)
                {
                    message="Сначала отключите другие режимы в Rendering Debugger. Их настройки оставлены без изменений.";
                    return;
                }
                ownedSettings=settings;
                previousMode=settings.data.fullScreenDebugMode;
            }
            appliedMode=view==0?FullScreenDebugMode.MotionVectors:FullScreenDebugMode.MotionVectorsIntensity;
            settings.SetFullScreenDebugMode(appliedMode);
            message="Просмотр включён. Смотрите Game: окно инструмента само буфер не захватывает. При смене Play Mode или перекомпиляции просмотр отключится.";
            RefreshViews();
        }
        void Restore()
        {
            if(ownedSettings==null)return;
            // Preserve any subsequent change made by the user in Rendering Debugger.
            if(ownedSettings.data.fullScreenDebugMode==appliedMode)
                ownedSettings.data.fullScreenDebugMode=previousMode;
            ownedSettings=null;
            message="Просмотр завершён. Настройки сцены и материалов не менялись.";
            RefreshViews();
        }
        static void RefreshViews()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }
    }
}
