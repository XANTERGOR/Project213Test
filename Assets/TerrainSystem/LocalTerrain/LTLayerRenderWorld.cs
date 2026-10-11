using UnityEngine;

namespace LocalTerrainPrototype
{
    // Keep serialized ids: 1..5 belonged to removed cache modes, 6 is fast layers.
    public enum LTSurfaceRenderMode { LiveLayers=0, FastLiveLayers=6 }

    public sealed partial class LTWorld
    {
        public LTSurfaceRenderMode surfaceRenderMode;
        [Tooltip("В быстрых слоях ограничить смешивание двумя слоями. Момент выбора задаёт флажок ниже. Выключено — прежнее смешивание.")]
        public bool fastLayerTopTwo;
        [Tooltip("При включённом ограничении: отдельный двухслойный вариант. Выбрать пару один раз ДО проекций и высот; дальше считать только её. Выключено — выбрать пару ПОСЛЕ высот всех слоёв.")]
        public bool fastLayerTopTwoBeforeHeight;
        [Tooltip("Только при выборе пары до высот: остальные слои добавляют цвет по весам покраски. Их высоты, нормали, AO и гладкость не читаются. Дороже строгой пары; дальний атлас не меняется.")]
        public bool fastLayerColorRemainder;
        [Range(0,.15f)] public float fastLayerBlendBudget=.03f;
        [Range(0,.15f)] public float fastLayerProjectionCutoff=.025f;
        // Old/unknown ids render ordinary layers without rewriting user scenes.
        public LTSurfaceRenderMode EffectiveSurfaceRenderMode=>surfaceRenderMode==LTSurfaceRenderMode.FastLiveLayers
            ?LTSurfaceRenderMode.FastLiveLayers:LTSurfaceRenderMode.LiveLayers;
        public bool FastLiveLayersActive=>EffectiveSurfaceRenderMode==LTSurfaceRenderMode.FastLiveLayers;
        public string FastLiveLayersStatus=>paintRuntime?.FastLiveLayersStatus??"Материалы ещё не готовы.";
        public void RefreshLayerRenderMode()=>UpdatePainting(true);
    }
}
