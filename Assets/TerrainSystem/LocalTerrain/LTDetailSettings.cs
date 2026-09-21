using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    [Flags] public enum LTDetailCategory { Vegetation=1, Stones=2, Other=4, All=7 }
    public enum LTDetailStampMode { Add, Replace, Remove }
    public enum LTDetailMaskMode
    {
        [InspectorName("Общая")] Common,
        [InspectorName("Своя")] Own,
        [InspectorName("Без маски")] None
    }

    [Serializable]
    public sealed class LTDetailDensityMask
    {
        [Tooltip("Общая маска для объектов с режимом «Общая». Не меняет покраску террейна.")]
        public bool enabled;
        [Min(.1f), Tooltip("Характерный размер пятен в метрах, не точный диаметр.")]
        public float patchSize=6;
        [Range(0,1), Tooltip("Заполненность через порог шума: 0 — минимальная плотность, 1 — полная. Не точный процент площади.")]
        public float patchCoverage=.6f;
        [Range(0,1), Tooltip("Мягкость границ пятен. 0 — резкий переход.")]
        public float patchSoftness=.2f;
        [Range(0,1), Tooltip("Доля исходной плотности между пятнами. 0 — пустоты, 0.05 — около 5%.")]
        public float patchMinimumDensity=.05f;
        [Tooltip("Seed общей маски: объекты с наследованием растут в общих пятнах.")]
        public int patchSeed;
        public void Validate()
        {
            patchSize=Mathf.Max(.1f,patchSize);patchCoverage=Mathf.Clamp01(patchCoverage);
            patchSoftness=Mathf.Clamp01(patchSoftness);patchMinimumDensity=Mathf.Clamp01(patchMinimumDensity);
        }
    }

    [Serializable]
    public sealed class LTDetailEntry : ISerializationCallbackReceiver
    {
        [SerializeField, HideInInspector] string id;
        public string Id=>id;
        [Tooltip("Включить этот тип детализации. Отключение сохраняет префаб и все настройки записи.")]
        public bool enabled=true;
        [Tooltip("Префаб из Project. Источник мешей, материалов, LOD и настроек теней. Скрипты и коллайдеры не копируются.")]
        public GameObject prefab;
        [Tooltip("Категория для фильтра штампов: Vegetation — растительность, Stones — камни, Other — другое. Не меняет материал или поведение автоматически.")]
        public LTDetailCategory category=LTDetailCategory.Vegetation;
        [Min(0), Tooltip("Базовое количество экземпляров на квадратный метр проекции XZ. Итог уменьшат вероятность, вес слоя, фильтры и штампы. 0 — не создавать.")]
        public float density=2;
        [Range(0,1), Tooltip("Доля принимаемых кандидатов: 1 — все, 0.5 — примерно половина, 0 — ни одного. Выбор стабилен по seed.")]
        public float probability=1;
        [Tooltip("Общая — наследовать маску слоя/штампа. Своя — отдельные параметры. Без маски — не прореживать пятнами.")]
        public LTDetailMaskMode densityMaskMode;
        [SerializeField, HideInInspector] bool maskModeConfigured;
        public LTDetailMaskMode EffectiveMaskMode=>!maskModeConfigured&&densityMask?LTDetailMaskMode.Own:densityMaskMode;
        [Tooltip("Разбить распределение на густые пятна и проплешины. Выключено — прежняя расстановка. Маска работает в локальных XZ-координатах террейна.")]
        [HideInInspector] public bool densityMask;
        [Min(.1f), Tooltip("Характерный размер пятен шума в метрах; не точный диаметр каждого островка. Большие значения создают более крупные заросли.")]
        public float patchSize=6;
        [Range(0,1), Tooltip("Заполненность пятнами: 0 — везде минимальная плотность, 1 — полная. Промежуточные значения меняют порог шума, а не гарантируют точный процент площади.")]
        public float patchCoverage=.6f;
        [Range(0,1), Tooltip("Мягкость перехода между густыми пятнами и проплешинами. 0 — резкая граница; больше — шире переход плотности.")]
        public float patchSoftness=.2f;
        [Range(0,1), Tooltip("Доля исходной плотности между пятнами: 0 — пустоты, 0.05 — около 5% исходной плотности, 1 — маска ничего не прореживает.")]
        public float patchMinimumDensity=.05f;
        [Tooltip("Seed рисунка пятен. Меняет маску, но не позиции кандидатов. Одинаковые параметры и seed позволяют нескольким префабам расти в общих пятнах.")]
        public int patchSeed;
        [Tooltip("X — минимальный, Y — максимальный равномерный множитель масштаба. 1 — исходный размер префаба.")]
        public Vector2 scaleRange=new Vector2(.8f,1.2f);
        [Tooltip("X/Y — минимальный/максимальный угол случайного поворота вокруг оси вверх объекта, в градусах. 0–360 — полный оборот.")]
        public Vector2 yawRange=new Vector2(0,360);
        [Tooltip("X/Y — минимальный/максимальный уклон поверхности в градусах: 0 — горизонтальная поверхность, 90 — вертикальная.")]
        public Vector2 slopeRange=new Vector2(0,45);
        [Range(0,1), Tooltip("Выравнивание по нормали поверхности: 0 — вертикально, 1 — полностью по склону, промежуточные значения — частичный наклон.")]
        public float alignToNormal=1;
        [Tooltip("X/Y — диапазон смещения от поверхности вдоль её нормали, в метрах. Отрицательное значение заглубляет основание.")]
        public Vector2 heightOffsetRange=Vector2.zero;
        [Min(0), Tooltip("Расстояние от камеры в метрах, с которого начинается исчезновение/снижение плотности. Без поддерживаемого Shader Fade и Thin In Distance объект остаётся до Cull Distance.")]
        public float fadeStart=35;
        [Min(0), Tooltip("Расстояние от камеры в метрах, после которого объект не рисуется, даже если нет LODGroup. 0 — не рисовать.")]
        public float cullDistance=50;
        [Tooltip("Зарезервировано для совместимого шейдера. Текущий рендерер пока использует дискретное отсечение и выводит предупреждение.")]
        public bool shaderFade;
        [Tooltip("Стабильное уменьшение числа экземпляров вдали. Для заметных камней обычно выключено.")]
        public bool thinInDistance;
        [Range(0,1), Tooltip("Доля экземпляров к концу диапазона Fade Start–Cull Distance при Thin In Distance: 0.25 — четверть, 1 — без прореживания. После Cull Distance не остаётся ничего.")]
        public float farDensity=.25f;
        [Min(0), Tooltip("Запас bounds для ветра и будущего изгиба растительности, м.")]
        public float deformationBoundsPadding=.5f;
        void UpgradeMaskMode()
        {
            if(maskModeConfigured)return;
            if(densityMask)densityMaskMode=LTDetailMaskMode.Own;
            maskModeConfigured=true;
        }
        public void OnBeforeSerialize(){}
        public void OnAfterDeserialize(){UpgradeMaskMode();}
        // Only call on the generation snapshot, never on the authored entry.
        public void ResolveDensityMask(LTDetailDensityMask common)
        {
            UpgradeMaskMode();
            var mode=EffectiveMaskMode;
            densityMask=mode==LTDetailMaskMode.Own||(mode==LTDetailMaskMode.Common&&common!=null&&common.enabled);
            if(mode!=LTDetailMaskMode.Common||common==null)return;
            patchSize=common.patchSize;patchCoverage=common.patchCoverage;patchSoftness=common.patchSoftness;
            patchMinimumDensity=common.patchMinimumDensity;patchSeed=common.patchSeed;
        }
        public void Validate(HashSet<string> used)
        {
            UpgradeMaskMode();
            if(string.IsNullOrEmpty(id)||!used.Add(id))
            {id=Guid.NewGuid().ToString("N");used.Add(id);}
            density=Mathf.Max(0,density);probability=Mathf.Clamp01(probability);
            patchSize=Mathf.Max(.1f,patchSize);patchCoverage=Mathf.Clamp01(patchCoverage);
            patchSoftness=Mathf.Clamp01(patchSoftness);patchMinimumDensity=Mathf.Clamp01(patchMinimumDensity);
            scaleRange=Ordered(scaleRange,.001f,float.MaxValue);
            slopeRange=Ordered(slopeRange,0,90);yawRange=Ordered(yawRange,-360,360);
            heightOffsetRange=Ordered(heightOffsetRange,-1000,1000);
            cullDistance=Mathf.Max(0,cullDistance);fadeStart=Mathf.Clamp(fadeStart,0,cullDistance);
            alignToNormal=Mathf.Clamp01(alignToNormal);farDensity=Mathf.Clamp01(farDensity);
            deformationBoundsPadding=Mathf.Max(0,deformationBoundsPadding);
        }
        static Vector2 Ordered(Vector2 value,float low,float high)
            =>new Vector2(Mathf.Clamp(Mathf.Min(value.x,value.y),low,high),Mathf.Clamp(Mathf.Max(value.x,value.y),low,high));
        public static void ValidateAll(List<LTDetailEntry> entries)
        {
            if(entries==null)return;
            var used=new HashSet<string>();
            foreach(var entry in entries)entry?.Validate(used);
        }
    }
}
