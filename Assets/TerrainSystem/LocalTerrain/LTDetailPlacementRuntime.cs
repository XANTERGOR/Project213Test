using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public sealed partial class LTDetailRenderer
    {
        [InspectorName("Требовать запекание для билда")]
        [Tooltip("Выключите, чтобы собирать проект без запекания: отсутствующие и устаревшие ячейки будут генерироваться во время игры.")]
        public bool requireBakedPlacementForBuild=true;
        [Tooltip("Загружать готовые ячейки из сохранения, только если поверхность, дороги и настройки совпадают. Остальные генерируются как обычно.")]
        [InspectorName("Использовать сохранённую расстановку")]
        public bool useSavedPlacement=true;
        [Tooltip("Сохранённая расстановка, а не меши травинок. Содержит только сохранённые готовые ячейки, не обязательно весь мир.")]
        [InspectorName("Сохранённая расстановка")]
        public LTDetailPlacementAsset savedPlacement;
#if UNITY_EDITOR
        // Editor-session ownership only. Never serialized into the scene, never
        // assigned to savedPlacement and never written to an asset on disk.
        [NonSerialized] LTDetailPlacementAsset reloadPlacement;
        public int ReloadPlacementCellCount=>reloadPlacement?reloadPlacement.CellCount:0;
        public LTDetailPlacementAsset CaptureReloadPlacement()
        {
            // A reload optimization must not duplicate an arbitrarily large
            // resident population. 500k items ~= 22 MB of numeric payload;
            // managed/object overhead is additional. Larger populations regenerate.
            if(instanceCount>500000)return null;
            var ready=CapturePlacement();if(ready.Length==0)return null;
            var asset=ScriptableObject.CreateInstance<LTDetailPlacementAsset>();
            asset.name="LT owned reload placement "+Guid.NewGuid().ToString("N");asset.hideFlags=HideFlags.HideAndDontSave;
            asset.Initialize(ready);return asset;
        }
        public void AcceptReloadPlacement(LTDetailPlacementAsset asset)
        {
            if(!asset||UnityEditor.EditorUtility.IsPersistent(asset)||(asset.hideFlags&HideFlags.DontSave)!=HideFlags.DontSave)
                throw new ArgumentException("Only an owned, transient reload placement is accepted.");
            ReleaseReloadPlacement();reloadPlacement=asset;requested=true;
        }
        void ReleaseReloadPlacement()
        {
            if(reloadPlacement)Object.DestroyImmediate(reloadPlacement);reloadPlacement=null;
        }
#endif
        bool bypassSavedPlacement;
        public int LoadedPlacementCellCount=>cells.Values.Count(c=>c.fromSaved);
        public bool CanSavePlacement=>isActiveAndEnabled&&!Application.isPlaying&&!IsGenerating&&!requested&&!blocked&&cells.Count>0;
        public string SavedPlacementStatus=>!savedPlacement?"Сохранение не назначено; работает обычная генерация.":
            !savedPlacement.Compatible?"Формат сохранения устарел; работает обычная генерация.":
            $"В сохранении: {savedPlacement.CellCount} ячеек. Сейчас загружено из него: {LoadedPlacementCellCount}. "+
            (requireBakedPlacementForBuild?"Зоны детализации проверяются перед билдом.":"Сборка без запекания разрешена.");

        sealed class ImageKey {public string value;}
        // Snapshots are immutable and already cached by the surface provider.
        // Weak keys do not retain old texture copies after cells are unloaded.
        readonly ConditionalWeakTable<LTDetailSurface.Image,ImageKey> placementImageKeys=new ConditionalWeakTable<LTDetailSurface.Image,ImageKey>();
        string ImageContent(LTDetailSurface.Image image)
        {
            if(image==null)return null;
            return placementImageKeys.GetValue(image,i=>
            {
                var h=new LTDetailPlacementFingerprint();h.Add(i.width);h.Add(i.height);h.Add(i.pixels);
                return new ImageKey{value=h.ToString()};
            }).value;
        }
        sealed class PlacementKeyContext
        {
            readonly LTDetailRenderer renderer;
            readonly LTDetailSurface surface;
            readonly Matrix4x4 worldMatrix;
            readonly Dictionary<Source,string> sources=new Dictionary<Source,string>();
            readonly Dictionary<LTRoadMath.Snapshot,string> roads=new Dictionary<LTRoadMath.Snapshot,string>();
            public PlacementKeyContext(LTDetailRenderer renderer,LTDetailSurface surface,Matrix4x4 matrix)
            {this.renderer=renderer;this.surface=surface;worldMatrix=matrix;}
            string Road(LTRoadMath.Snapshot road)
            {
                if(roads.TryGetValue(road,out var result))return result;
                var h=new LTDetailPlacementFingerprint();h.Add(JsonUtility.ToJson(road.DetailPlacementSettings));h.Add(road.samples.Count);
                if(road.HasShoulderJoins)h.Add(road.FoundationKey);
                foreach(var p in road.samples){h.Add(p.position);h.Add(p.right);h.Add(p.distance);h.Add(p.bank);h.Add(p.variationStrength);}
                return roads[road]=h.ToString();
            }
            public string Key(Vector2Int coordinate,Rect rect,List<LTDetailSurface.Tile> tiles,List<Source> localSources,
                List<Stamp> stamps,List<LTRoadMath.Snapshot> localRoads,List<LTRoadVegetationMask.Entry> masks,out Object[] references)
            {
                var h=new LTDetailPlacementFingerprint();var refs=new List<Object>();
                void Reference(Object obj){h.Add(refs.Count);refs.Add(obj);}
                h.Add(LTDetailPlacementAsset.CurrentVersion);h.Add(coordinate.x);h.Add(coordinate.y);h.Add(rect);
                h.Add(worldMatrix);h.Add(renderer.world.TerrainRect);h.Add(renderer.cellSize);h.Add(renderer.seed);h.Add(renderer.densityMultiplier);
                h.Add(renderer.maxCandidatesPerCell);h.Add(surface.triplanar);h.Add(surface.lightweight);h.Add(surface.blend);
                h.Add(renderer.terrain.DetailCellGeometryKey(rect));
                h.Add(tiles.Count);
                foreach(var tile in tiles)
                {
                    h.Add(tile.rect);h.Add(renderer.ImageContent(tile.weights0));h.Add(renderer.ImageContent(tile.weights1));h.Add(renderer.ImageContent(tile.weights2));
                    h.Add(tile.roadProjection?.ContentKey);h.Add(tile.layers.Length);
                    foreach(var layer in tile.layers)
                    {
                        Reference(layer);var settings=surface.layers[layer];
                        h.Add(settings.size);h.Add(settings.offset);h.Add(settings.strength);h.Add(settings.heightOffset);h.Add(renderer.ImageContent(settings.mask));
                    }
                }
                var localStamps=stamps.Where(s=>Touches(rect,s.rect)).ToArray();h.Add(localStamps.Length);
                foreach(var stamp in localStamps)
                {
                    // Junction stamp IDs include a transient InstanceID. Only the
                    // numeric suppression rule and its layer references matter here.
                    h.Add(stamp.inverse);h.Add(stamp.rect);h.Add(stamp.size);h.Add(stamp.ellipse);h.Add(stamp.allLayers);
                    h.Add(stamp.falloff);h.Add((int)stamp.categories);h.Add((int)stamp.mode);
                    if(stamp.junction!=null)
                    {h.Add("junction-mesh-v1");h.Add(stamp.junction.footprint.ContentKey);h.Add(stamp.junction.shoulder);h.Add(stamp.junction.blend);}
                    h.Add(stamp.layers==null?-1:stamp.layers.Length);if(stamp.layers!=null)foreach(var layer in stamp.layers)Reference(layer);
                }
                h.Add(localRoads.Count);foreach(var road in localRoads)h.Add(Road(road));
                h.Add(masks.Count);foreach(var mask in masks)h.Add(mask.footprint.ContentKey);
                h.Add(localSources.Count);
                foreach(var source in localSources)
                {
                    Reference(source.layer);Reference(source.entry.prefab);
                    h.Add(source.owner);h.Add(source.seed);h.Add(source.order);h.Add(source.density);h.Add(source.recipe.rootScale);
                    if(!sources.TryGetValue(source,out var settings))sources[source]=settings=JsonUtility.ToJson(source.entry.SavedPlacementSettings());
                    h.Add(settings);
                }
                references=refs.ToArray();return h.ToString();
            }
        }

        LTDetailPlacementAsset.Cell MatchingPlacement(Vector2Int key,Rect rect,string inputKey,Object[] references,int sources)
        {
            if(bypassSavedPlacement)return null;
#if UNITY_EDITOR
            var restored=reloadPlacement?reloadPlacement.Find(key):null;
            if(restored!=null&&restored.inputKey==inputKey&&LTDetailPlacementAsset.ReferencesMatch(restored.references,references)
                &&LTDetailPlacementAsset.ValidPayload(restored,sources,maxInstances,rect))return restored;
#endif
            bool allowSaved=useSavedPlacement;
#if UNITY_EDITOR
            // Authoring reuse is independent of the runtime saved-placement toggle.
            allowSaved|=placementIncremental;
#endif
            if(!allowSaved||!savedPlacement)return null;
            var cell=savedPlacement.Find(key);
            if(cell==null||cell.inputKey!=inputKey||!LTDetailPlacementAsset.ReferencesMatch(cell.references,references))return null;
            if(LTDetailPlacementAsset.ValidPayload(cell,sources,maxInstances,rect))return cell;
            string warning="Сохранённая ячейка "+key+" повреждена или превышает лимит; используется обычная генерация.";
            if(!messages.Contains(warning))messages.Add(warning);
            return null;
        }
        static void AddSavedItem(Group group,LTDetailPlacementAsset.Item item)
        {
            var recipe=group.source.recipe;var entry=group.source.entry;
            var matrix=Matrix4x4.TRS(item.position,item.rotation,recipe.rootScale*item.scale);
            var bounds=TransformBounds(recipe.bounds,matrix);bounds.Expand(2*entry.deformationBoundsPadding);
            float rootScale=Mathf.Max(Mathf.Abs(recipe.rootScale.x),Mathf.Max(Mathf.Abs(recipe.rootScale.y),Mathf.Abs(recipe.rootScale.z)));
            if(group.instances.Count==0){group.bounds=bounds;group.originBounds=new Bounds(item.position,Vector3.zero);}
            else{group.bounds.Encapsulate(bounds);group.originBounds.Encapsulate(item.position);}
            group.instances.Add(new Instance{matrix=matrix,bounds=bounds,position=item.position,rotation=item.rotation,scale=item.scale,id=item.id,
                maskPosition=item.maskPosition,lodPosition=matrix.MultiplyPoint3x4(recipe.lodReferencePoint),lodSize=recipe.lodSize*item.scale*rootScale});
        }
        // Editor saving calls this only after generation finishes. No pending or
        // half-restored cell is ever inserted into the resident cell dictionary.
        public LTDetailPlacementAsset.Cell[] CapturePlacement()
        {
            if(!CanSavePlacement)throw new InvalidOperationException("Дождитесь завершения генерации детализации вне Play Mode.");
            return CaptureReadyPlacement();
        }
        LTDetailPlacementAsset.Cell[] CaptureReadyPlacement()
        {
            var result=new List<LTDetailPlacementAsset.Cell>();
            foreach(var pair in cells.OrderBy(p=>p.Key.y).ThenBy(p=>p.Key.x))
            {
                var cell=pair.Value;if(string.IsNullOrEmpty(cell.savedInputKey)||cell.sources==null)continue;
                var groups=new List<LTDetailPlacementAsset.Group>();
                foreach(var group in cell.groups)
                {
                    int source=cell.sources.FindIndex(s=>s.layer==group.source.layer&&s.owner==group.source.owner&&s.entry.Id==group.source.entry.Id&&s.entry.prefab==group.source.entry.prefab);
                    if(source<0)throw new InvalidOperationException("Источник сохранённой группы не найден.");
                    groups.Add(new LTDetailPlacementAsset.Group{source=source,items=group.instances.Select(i=>new LTDetailPlacementAsset.Item
                        {position=i.position,rotation=i.rotation,scale=i.scale,maskPosition=i.maskPosition,id=i.id}).ToArray()});
                }
                var saved=new LTDetailPlacementAsset.Cell{coordinate=pair.Key,inputKey=cell.savedInputKey,references=(Object[])cell.savedReferences.Clone(),sourceCount=cell.sources.Count,groups=groups.ToArray()};
                saved.payloadKey=LTDetailPlacementAsset.PayloadKey(saved);result.Add(saved);
            }
            return result.ToArray();
        }
    }
}
