#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTDetailRenderer
    {
        bool placementWorldOperation,placementValidationOnly,placementSelectionOnly;
        bool placementIncremental;
        LTDetailPlacementAsset.Cell placementReusedCell;
        public int PlacementReusedCells {get;private set;}
        public int PlacementGeneratedCells {get;private set;}
        readonly List<Vector2Int> placementSelectedCells=new List<Vector2Int>();
        int placementValidatedCells;

        // Same planner coordinates, surface snapshots, fingerprints and generator as runtime.
        // Only one cell is resident; maxInstances remains a per-resident-cell safety bound.
        // Progress returns false to cancel. Never publishes a partial asset or saves a scene.
        public LTDetailPlacementAsset.Cell[] ProcessWorldPlacement(bool validateOnly,Func<int,int,bool> progress=null)
        {
            if(placementWorldOperation||Application.isPlaying)throw new InvalidOperationException("Операция доступна только вне Play Mode.");
            world=GetComponent<LTWorld>();
            if(!world||!world.isActiveAndEnabled||!world.source||!world.generatedRoot||!world.enableLayerPainting)
                throw new InvalidOperationException("Нужен активный LTWorld с готовой геометрией и покраской слоёв.");
            if(cellSize<1||maxCells<1||maxInstances<1)throw new InvalidOperationException("Некорректные лимиты детализации.");
            if(validateOnly&&(!useSavedPlacement||!savedPlacement||!savedPlacement.Compatible||!UnityEditor.EditorUtility.IsPersistent(savedPlacement)))
                throw new InvalidOperationException("Назначьте сохранённую расстановку и включите её использование.");
            var rect=world.TerrainRect;
            int x0=Mathf.FloorToInt(rect.xMin/cellSize),z0=Mathf.FloorToInt(rect.yMin/cellSize);
            int x1=Mathf.CeilToInt(rect.xMax/cellSize),z1=Mathf.CeilToInt(rect.yMax/cellSize);
            long total=(long)(x1-x0)*(z1-z0);
            if(total<=0||total>int.MaxValue)throw new InvalidOperationException("Некорректный размер мира.");
            var chunks=new HashSet<Vector2Int>();
            foreach(var chunk in world.generatedRoot.GetComponentsInChildren<LTChunk>())
            {
                if(!chunk.mesh||chunk.lodsPending||!chunks.Add(new Vector2Int(chunk.x,chunk.z)))
                    throw new InvalidOperationException("Геометрия чанков не готова: "+chunk.name);
            }
            for(int z=world.chunkOrigin.y;z<world.chunkOrigin.y+world.chunksZ;z++)
            for(int x=world.chunkOrigin.x;x<world.chunkOrigin.x+world.chunksX;x++)
                if(!chunks.Contains(new Vector2Int(x,z)))throw new InvalidOperationException($"Отсутствует готовый чанк ({x}, {z}).");
            var result=new List<LTDetailPlacementAsset.Cell>();
            bool previousBypass=bypassSavedPlacement;
            // Session reload cache must never satisfy a build's on-disk requirement.
            var previousReload=reloadPlacement;reloadPlacement=null;
            placementWorldOperation=true;placementValidationOnly=validateOnly;placementValidatedCells=0;
            placementIncremental=!validateOnly&&savedPlacement&&UnityEditor.EditorUtility.IsPersistent(savedPlacement);
            PlacementReusedCells=PlacementGeneratedCells=0;
            try
            {
                Clear();bypassSavedPlacement=!validateOnly&&!placementIncremental;
                int paintRevision=world.DetailPaintRuntime?.DetailSurfaceRevision??0;
                world.UpdatePainting(true);
                if(world.DetailPaintRuntime==null||!world.DetailPaintRuntime.DetailSurfaceReady||world.DetailPaintRuntime.DetailSurfaceRevision==paintRevision)
                    throw new InvalidOperationException("Покраска ещё не готова. Дождитесь её завершения и повторите операцию.");
                // Cheap coverage pass: no candidate generation, terrain sampling or cell hashes.
                // Recomputed during validation so a newly painted zone cannot escape the guard.
                placementSelectedCells.Clear();planned.AddRange(LTDetailStreaming.WorldCells(rect,cellSize));
                placementSelectionOnly=true;
                try
                {
                    Prepare();
                    if(generation==null)throw new InvalidOperationException("Не удалось определить зоны детализации: "+status);
                    double nextSelectionProgress=0;
                    while(generation.MoveNext())
                    {
                        double now=UnityEditor.EditorApplication.timeSinceStartup;
                        if(progress!=null&&now>=nextSelectionProgress)
                        {
                            nextSelectionProgress=now+.1;
                            if(!progress(0,0))throw new OperationCanceledException();
                        }
                    }
                }
                finally{(generation as IDisposable)?.Dispose();generation=null;placementSelectionOnly=false;}
                total=placementSelectedCells.Count;
                int completed=0;double nextProgress=0;
                foreach(var key in placementSelectedCells)
                {
                    void Poll()
                    {
                        double now=UnityEditor.EditorApplication.timeSinceStartup;
                        if(progress==null||now<nextProgress)return;nextProgress=now+.1;
                        if(!progress(completed,(int)total))throw new OperationCanceledException();
                    }
                    Poll();
                    planned.Clear();planned.Add(key);wanted.Clear();wanted.Add(key);
                    placementReusedCell=null;
                    Prepare();
                    if(generation==null)throw new InvalidOperationException("Не удалось подготовить ячейку "+key+": "+status);
                    try{while(generation.MoveNext())Poll();}
                    finally{(generation as IDisposable)?.Dispose();generation=null;}
                    if(!validateOnly)
                    {
                        if(placementReusedCell!=null)
                        {
                            // Serialized records are immutable here. No instance/matrix restoration.
                            result.Add(placementReusedCell);PlacementReusedCells++;
                        }
                        else
                        {
                            var ready=CaptureReadyPlacement();
                            if(ready.Length!=1||ready[0].coordinate!=key)throw new InvalidOperationException("Ячейка не завершена: "+key);
                            result.Add(ready[0]);RemoveCell(key);PlacementGeneratedCells++;
                        }
                    }
                    completed++;
                }
                if(validateOnly&&placementValidatedCells!=total)throw new InvalidOperationException("Проверены не все зоны детализации.");
                return result.ToArray();
            }
            finally
            {
                // The build's scene copy must not serialize temporary paint materials.
                if(validateOnly)world.DetailPaintRuntime?.RestoreMaterialsForSave();
                Clear();reloadPlacement=previousReload;bypassSavedPlacement=previousBypass;
                placementSelectedCells.Clear();placementWorldOperation=placementValidationOnly=placementSelectionOnly=false;requested=true;blocked=false;
                placementIncremental=false;placementReusedCell=null;
            }
        }

        static bool PlacementAreaMayContainDetails(Rect area,LTDetailSurface surface,List<Source> sources)
        {
            foreach(var source in sources)
            {
                if(source.density<=0||source.entry.probability<=0||!LTDetailMath.DensityMaskMayAffect(source.entry,area,source.seed))continue;
                var stamp=source.stamp;
                if(stamp!=null&&!Touches(area,stamp.rect))continue;
                foreach(var tile in surface.tiles)
                {
                    if(!Touches(area,tile.rect))continue;
                    if(stamp!=null&&stamp.allLayers)return true;
                    for(int slot=0;slot<tile.layers.Length;slot++)
                    {
                        bool allowed=stamp==null?tile.layers[slot]==source.layer:
                            stamp.layers!=null&&Array.IndexOf(stamp.layers,tile.layers[slot])>=0;
                        if(!allowed)continue;
                        if(tile.layers.Length==1)return true;
                        var image=slot<4?tile.weights0:slot<8?tile.weights1:tile.weights2;
                        // Missing coverage is uncertain: retain, never erase possible grass.
                        if(image==null||LTDetailMath.HasLayerWeight(image.pixels,image.width,image.height,tile.rect,area,slot%4))return true;
                    }
                }
            }
            return false;
        }
    }
}
#endif
