using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    [CustomEditor(typeof(LTMeshStamp)), CanEditMultipleObjects]
    internal sealed class LTRockStampInspector : Editor
    {
        static readonly string[] Modes={"Свой материал / UV меша","Слои террейна"};
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject,"m_Script","useTerrainMaterial","materialBlendWidth","materialBlendSoftness",
                "simplifyDistantMaterial","materialFarStart","materialFarEnd","materialFarResolution","enableMeshLODs","meshLodError","terrainSeamLodError");
            EditorGUILayout.Space();EditorGUILayout.LabelField("Скала → переход → террейн",EditorStyles.boldLabel);
            var mode=serializedObject.FindProperty("useTerrainMaterial");
            EditorGUI.showMixedValue=mode.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();int selected=EditorGUILayout.Popup("Материал скалы",mode.boolValue?1:0,Modes);
            if(EditorGUI.EndChangeCheck())mode.boolValue=selected==1;
            EditorGUI.showMixedValue=false;
            using(new EditorGUI.DisabledScope(mode.boolValue&&!mode.hasMultipleDifferentValues))
            {
                Field("materialBlendWidth","Ширина бленда (м)");Field("materialBlendSoftness","Мягкость бленда");
            }
            EditorGUILayout.HelpBox("Скала и переход — отдельные меши. Переход забирает полосу исходных треугольников скалы: без наложения и с сохранением UV0. Свой материал: непрозрачный односторонний HDRP/Lit Standard (цвет, normal, mask). Для других шейдеров сохраняется исходный материал, но бленд отключается.",MessageType.Info);
            EditorGUILayout.Space();Field("enableMeshLODs","LOD скалы и перехода");
            using(new EditorGUI.DisabledScope(!serializedObject.FindProperty("enableMeshLODs").boolValue))Field("meshLodError","Ошибка LOD1 (м)");
            EditorGUILayout.HelpBox("Скала упрощается до создания служебного атласа: его швы больше не блокируют LOD. LOD3–4 продолжают упрощение сильнее. Край у перехода, границы материалов и исходные UV-швы закреплены; форма LOD0 и коллайдер не меняются.",MessageType.None);
            Field("terrainSeamLodError","Ошибка стыка LOD1 (м)");
            EditorGUILayout.HelpBox("Стык земли и перехода упрощается общим планом; 0 сохраняет контур и полосу. Допуск стыка удваивается на каждом LOD. Внутри полосы действует Max Height Error соответствующего LOD земли. Проверяются все варианты соседних LOD; при растущих допусках дальний уровень продолжает сокращение общей сетки. Близкие скалы переключают стык вместе по ближайшему участку земли. Границы участков и UV-швов сохраняются. LOD0 и коллайдер не меняются.",MessageType.None);
            EditorGUILayout.Space();Field("simplifyDistantMaterial","Облегчённый дальний материал");
            using(new EditorGUI.DisabledScope(!serializedObject.FindProperty("simplifyDistantMaterial").boolValue))
            {
                Field("materialFarStart","Начало перехода (м)");Field("materialFarEnd","Полный дальний (м)");
                Field("materialFarResolution","Размер атласа");
            }
            EditorGUILayout.HelpBox("Дальний материал: UV-атлас цвет/AO + normal/smoothness/metallic. Свой материал скалы остаётся исходным; атлас нужен только переходу. Со слоями на скале каждый её LOD получает свой атлас, размер уменьшается вдвое до 128². При размере 512² и пяти LOD: ≈7,7 МиБ на скалу + ≈5,3 МиБ на переход. Атласы временные, готовятся по очереди после открытия/изменений, не при движении камеры. Пока нужный атлас не готов, используются живые слои.",MessageType.None);
            serializedObject.ApplyModifiedProperties();
            if(targets.Length==1)
            {
                var stamp=(LTMeshStamp)target;
                if(!stamp.trimRockInsideTerrain)EditorGUILayout.HelpBox("Раздельный переход требует Trim Rock Inside Terrain.",MessageType.Warning);
                if(!string.IsNullOrEmpty(stamp.renderStatus))EditorGUILayout.HelpBox(stamp.renderStatus,MessageType.Info);
                if(stamp.renderOutput!=null)EditorGUILayout.LabelField("Текущий LOD",stamp.renderOutput.CurrentLOD.ToString());
            }
            EditorGUILayout.HelpBox("После изменения ширины, атласа или геометрических LOD: Apply Pending Changes в LT World (либо Auto Update). Сцена автоматически не сохраняется.",MessageType.None);
        }
        void Field(string property,string label)=>EditorGUILayout.PropertyField(serializedObject.FindProperty(property),new GUIContent(label),true);
    }
}
