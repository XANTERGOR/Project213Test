using UnityEngine;

namespace LocalTerrainPrototype
{
    [CreateAssetMenu(menuName="Local Terrain/Surface Layer",fileName="Surface Layer")]
    public sealed class LTSurfaceLayer : ScriptableObject
    {
        public Texture2D baseColorMap;
        public Color tint=Color.white;
        public Texture2D normalMap;
        [Range(0,2)] public float normalStrength=1;
        [Header("Mask Map: R = AO, G = Height, B = Smoothness")]
        [Tooltip("RGB: R — Ambient Occlusion, G — Height Map, B — Smoothness. Альфа-канал не используется. Импортировать как linear (sRGB выключен).")]
        public Texture2D maskMap;
        [Range(0,2), Tooltip("Сила AO (R): 0 — выключено, 1 — исходная карта, 2 — усиление.")]
        public float aoStrength=1;
        [Range(0,4), Tooltip("Контраст Height (G) вокруг 0.5. Работает при включённом смешивании по Height в LTWorld; геометрию не меняет.")]
        public float heightStrength=1;
        [Range(-1,1), Tooltip("Смещение Height: положительное значение усиливает приоритет слоя при смешивании по высоте.")]
        public float heightOffset;
        public bool displacement;
        [Range(0,2), Tooltip("Полный диапазон смещения в метрах. Коллайдер не изменяется.")]
        public float displacementAmplitude=.15f;
        [Range(0,1)] public float displacementCenter=.5f;
        [Range(0,10), Tooltip("Mip heightmap только для геометрии: 0 — исходная детализация, больше — сглаженнее. Не меняет маску покрытия и Height-смешивание. Требуются mipmaps у Mask Map.")]
        public int displacementSmoothingMip;
        [Header("Визуальное продавливание")]
        public bool deformation;
        [Range(.01f,2), Tooltip("Максимальная глубина колеи, м. Коллайдер и нормали не меняются.")]
        public float deformationDepth=.15f;
        [Min(0), Tooltip("Восстановление полной глубины, секунд. 0 — след не восстанавливается до сброса.")]
        public float deformationRecovery=20;
        [Range(1,63), Tooltip("Максимальный фактор тесселяции в покрытии этого слоя; затухает с расстоянием.")]
        public float deformationTessellation=32;
        [Range(0,1)] public float metallic;
        [Range(0,1)] public float smoothness=.4f;
        [Tooltip("Texture repetition size in world metres; independent of mesh density.")]
        public Vector2 tileSizeMetres=new Vector2(4,4);
        public Vector2 tileOffsetMetres;
        [Tooltip("Общая маска плотности для наследующих её объектов детализации.")]
        public LTDetailDensityMask detailDensityMask=new LTDetailDensityMask();
        [Tooltip("Статическая детализация: настройки для будущего инстанс-спавнера, не GameObject-копий.")]
        public System.Collections.Generic.List<LTDetailEntry> details=new System.Collections.Generic.List<LTDetailEntry>();
        // Painting/displacement must not depend on vegetation settings or editor object names.
        public int SurfaceHash()
        {
            int hash=17;
            void Add(int value){hash=unchecked(hash*397^value);}
            Add(baseColorMap?baseColorMap.GetInstanceID():0);Add(tint.GetHashCode());
            Add(normalMap?normalMap.GetInstanceID():0);Add(normalStrength.GetHashCode());
            Add(maskMap?maskMap.GetInstanceID():0);Add(aoStrength.GetHashCode());
            Add(heightStrength.GetHashCode());Add(heightOffset.GetHashCode());
            Add(displacement.GetHashCode());Add(displacementAmplitude.GetHashCode());
            Add(displacementCenter.GetHashCode());Add(displacementSmoothingMip);
            Add(deformation.GetHashCode());Add(deformationDepth.GetHashCode());
            Add(deformationRecovery.GetHashCode());Add(deformationTessellation.GetHashCode());
            Add(metallic.GetHashCode());Add(smoothness.GetHashCode());
            Add(tileSizeMetres.GetHashCode());Add(tileOffsetMetres.GetHashCode());
            return hash;
        }
        void OnValidate()
        {
            if(detailDensityMask==null)detailDensityMask=new LTDetailDensityMask();
            detailDensityMask.Validate();
            LTDetailEntry.ValidateAll(details);
            tileSizeMetres.x=Mathf.Max(.001f,tileSizeMetres.x);
            tileSizeMetres.y=Mathf.Max(.001f,tileSizeMetres.y);
        }
    }
}
