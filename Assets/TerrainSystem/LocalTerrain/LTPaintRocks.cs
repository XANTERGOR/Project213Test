using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTPaintRuntime
    {
        sealed partial class RockState
        {
            public LTMeshStamp stamp;
            public MeshRenderer renderer;
            public Material[] restore;
            public ChunkState paint;
        }
        readonly Dictionary<LTMeshStamp,RockState> rocks=new Dictionary<LTMeshStamp,RockState>();
        static bool HasRockMaterial(RockState state)
        {
            if(!state.renderer||!state.paint.material)return false;
            foreach(var m in state.renderer.sharedMaterials)if(m==state.paint.material)return true;
            return false;
        }
        static void RestoreRock(RockState state)
        {
            RestoreSplitRock(state);
            if(HasRockMaterial(state))
            {
                var restored=state.restore??Array.Empty<Material>();
                // Material-only toggles also work before the editor has rebuilt
                // the bridge, and in play mode where no editor rebuild occurs.
                var stamp=state.stamp;
                if(stamp && (!stamp.useTerrainMaterial||!stamp.isActiveAndEnabled) && stamp.objectMaterials!=null && stamp.objectMaterials.Length>0)
                {
                    var filter=stamp.GetComponent<MeshFilter>();
                    int slots=filter&&filter.sharedMesh?filter.sharedMesh.subMeshCount:stamp.objectMaterials.Length;
                    var original=new Material[Mathf.Max(1,slots)];
                    int sourceSlots=stamp.sourceMesh?stamp.sourceMesh.subMeshCount:original.Length;
                    for(int i=0;i<original.Length;i++)
                        original[i]=i<sourceSlots?stamp.objectMaterials[Mathf.Min(i,stamp.objectMaterials.Length-1)]:
                            restored.Length>0?restored[restored.Length-1]:stamp.objectMaterials[0];
                    restored=original;
                }
                state.renderer.sharedMaterials=restored;
            }
            if(state.stamp)state.stamp.terrainLayersMaterialApplied=false;
        }
        void RestoreRockMaterialsForSave(){foreach(var state in rocks.Values)RestoreRock(state);}
        void ReleaseRockMaterials()
        {
            foreach(var state in rocks.Values){RestoreRock(state);ReleaseSplitRock(state);Release(state.paint);}
            rocks.Clear();
        }
        // Rocks use the same numeric weight kernel and bounded worker pool as
        // chunks, but intentionally exclude road stamps from their palette.
        static readonly List<LTRoadMath.Snapshot> noRockRoads=new List<LTRoadMath.Snapshot>();
        bool TryBakeRockWeights(LTWorld world,Rect rect,List<LTPaintStamp> local,List<LTSurfaceLayer> layers,
            ChunkState state,int coverage,List<LTRoadMath.Snapshot> asphaltInputs,bool backgroundWeights,ref int resultsApplied)
        {
            bool needsBake=layers.Count>1&&(!state.ready||state.coverageHash!=coverage);
            bool workerWeights=backgroundWeights&&state.ready&&needsBake;
            if(workerWeights&&!WeightInputFits(local)){workerWeights=false;world.paintCpu.WeightJobBudgetFallback();}
            // Check even when edits return to the already-published state or to
            // base-only. A retired job may finish, but can never publish its map.
            if(state.weightWork!=null&&(!workerWeights||!state.weightWork.Matches(rect,coverage,coverage,noRockRoads)))
            {
                state.weightWork.Dispose();state.weightWork=null;world.paintCpu.RockWeightDiscarded();
            }
            if(!needsBake)return true;
            LTPaintWeightMath.Pixel[] bakedPixels=null;
            if(workerWeights)
            {
                if(state.weightWork!=null)
                {
                    if(!state.weightWork.work.IsCompleted||resultsApplied>=1){PendingRoadSampling=true;return false;}
                    using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightResolve))
                    {
                        try
                        {
                            bakedPixels=state.weightWork.work.Result(out bool burst);
                            world.paintCpu.WeightJobResolved(burst,bakedPixels.Length);world.paintCpu.RockWeightResolved(burst);
                        }
                        finally{state.weightWork.Dispose();state.weightWork=null;}
                    }
                    resultsApplied++;
                }
                else
                {
                    // Queue pressure is not a reason to run the expensive CPU
                    // fallback. Keep old maps/bindings and retry a later poll.
                    if(!LTPaintWeightWork.CanSchedule){PendingRoadSampling=true;return false;}
                    using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightPrepare))
                        if(TryPrepareWeights(world,rect,local,layers,null,out var input))
                        {
                            using(input)
                                state.weightWork=new PendingWeights{work=new LTPaintWeightWork(input,world.paintCpu),rect=rect,
                                    coverage=coverage,weight=coverage,roads=Array.Empty<LTRoadMath.Snapshot>()};
                            world.paintCpu.WeightJobScheduled();world.paintCpu.RockWeightScheduled();
                            PendingRoadSampling=true;return false;
                        }
                    world.paintCpu.WeightJobBudgetFallback();
                }
            }
            // First/import/manual/Play builds and oversized snapshots preserve
            // the reference path. Guarded pixels also use that path inside Bake.
            if(bakedPixels==null)world.paintCpu.RockWeightCpuBaked();
            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightBake))
                Bake(world,rect,local,layers,state,asphaltInputs,true,null,bakedPixels);
            return true;
        }
        void TickRockMaterials(LTWorld world,Shader shader,List<LTPaintStamp> active,List<Rect> bounds,List<LTRoadMath.Snapshot> asphaltInputs,
            Dictionary<LTSurfaceLayer,LayerChangeInput> layerInputs,Dictionary<LTPaintStamp,int[]> coverageInputs,
            bool backgroundWeights,ref int resultsApplied)
        {
            var live=new HashSet<LTMeshStamp>();int painted=0;bool bakedRock=false;
            var warnings=new List<string>();
            foreach(var stamp in world.GetComponentsInChildren<LTMeshStamp>())
            {
                if(!stamp.isActiveAndEnabled||(!stamp.useTerrainMaterial&&stamp.renderOutput==null)||stamp.GetComponentInParent<LTWorld>()!=world)continue;
                var renderer=stamp.GetComponent<MeshRenderer>();var filter=stamp.GetComponent<MeshFilter>();
                if(!renderer||!filter||!filter.sharedMesh)continue;
                var box=renderer.bounds;var inverse=world.transform.worldToLocalMatrix;
                if(stamp.renderOutput!=null&&stamp.renderOutput.bridgeRenderer)box.Encapsulate(stamp.renderOutput.bridgeRenderer.bounds);
                var min=new Vector3(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity);var max=-min;
                for(int i=0;i<8;i++)
                {
                    var p=inverse.MultiplyPoint3x4(box.center+Vector3.Scale(box.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1)));
                    min=Vector3.Min(min,p);max=Vector3.Max(max,p);
                }
                var rect=Rect.MinMaxRect(min.x,min.z,Mathf.Max(min.x+.01f,max.x),Mathf.Max(min.z+.01f,max.z));
                var local=new List<LTPaintStamp>();var layers=new List<LTSurfaceLayer>{world.baseLayer};
                for(int i=0;i<active.Count;i++)if(!active[i].Road&&Touches(rect,bounds[i]))
                {local.Add(active[i]);if(!layers.Contains(active[i].EffectiveLayer))layers.Add(active[i].EffectiveLayer);}
                if(layers.Count>LayerCapacity){warnings.Add(stamp.name+$": больше {LayerCapacity} слоёв, материал слоёв не назначен");continue;}
                live.Add(stamp);
                if(!rocks.TryGetValue(stamp,out var rock))
                {
                    rock=new RockState{stamp=stamp,renderer=renderer,paint=new ChunkState()};
                    rock.paint.material=new Material(shader){name="Terrain layers / "+stamp.name,hideFlags=HideFlags.HideAndDontSave};
                    rocks.Add(stamp,rock);
                }
                if(stamp.renderOutput==null&&!HasRockMaterial(rock))
                {
                    rock.restore=stamp.terrainLayersMaterialApplied&&stamp.objectMaterials!=null?
                        (Material[])stamp.objectMaterials.Clone():renderer.sharedMaterials;
                    if(!stamp.terrainMaterialApplied && !stamp.unionGroupOutputApplied && !stamp.terrainLayersMaterialApplied)
                    {
                        stamp.objectMaterials=(Material[])rock.restore.Clone();
#if UNITY_EDITOR
                        UnityEditor.EditorUtility.SetDirty(stamp);
#endif
                    }
                }
                int coverage=Mix(rect.GetHashCode(),inverse.GetHashCode());coverage=Mix(coverage,Id(world.baseLayer));
                foreach(var junction in asphaltJunctions)if(junction.Intersects(rect))coverage=Mix(coverage,junction.hash);
                foreach(var s in local)
                    foreach(int value in coverageInputs[s])coverage=Mix(coverage,value);
                foreach(var layer in layers)coverage=Mix(coverage,Id(layer));
                if(local.Exists(s=>s.HasTerrainFilters))coverage=Mix(coverage,TerrainFilterSignature(rect,local));
                int surface=Mix(world.triplanarTexturing?1:0,world.lightweightBackground?1:0);
                surface=Mix(surface,world.layerHeightBlend.GetHashCode());
                foreach(var layer in layers)surface=layerInputs[layer].AppendSurface(surface);
                var state=rock.paint;
                if(!TryBakeRockWeights(world,rect,local,layers,state,coverage,asphaltInputs,backgroundWeights,ref resultsApplied))
                {
                    // Do not bind a new palette/rectangle over the previous map.
                    // Dependencies become published only after a successful bake.
                    painted++;continue;
                }
                bool rebound=!state.ready||state.coverageHash!=coverage||state.surfaceHash!=surface;
                if(rebound)
                {
                    Bind(state.material,world,rect,layers,state);
                    state.material.SetFloat("_LTRockProjection",1);
                    state.material.SetVector("_LTGlobalParams",Vector4.zero);
                    state.material.SetFloat("_LTDebugCoverage",0);
                    state.coverageHash=coverage;state.surfaceHash=surface;state.ready=true;
                }
                BindFastLayers(world,state);
                if(stamp.renderOutput!=null)
                {
                    UpdateSplitRock(world,rock,rebound,ref bakedRock);
                    painted++;continue;
                }
                int slots=Mathf.Max(1,filter.sharedMesh.subMeshCount);
                var assigned=renderer.sharedMaterials;bool needsAssign=assigned.Length!=slots;
                for(int i=0;!needsAssign&&i<assigned.Length;i++)needsAssign=assigned[i]!=state.material;
                if(needsAssign)
                {
                    assigned=new Material[slots];for(int i=0;i<slots;i++)assigned[i]=state.material;
                    renderer.sharedMaterials=assigned;
                }
                stamp.terrainLayersMaterialApplied=true;
                painted++;
            }
            foreach(var key in new List<LTMeshStamp>(rocks.Keys))if(!key||!live.Contains(key))
            {
                var rock=rocks[key];RestoreRock(rock);ReleaseSplitRock(rock);Release(rock.paint);rocks.Remove(key);
            }
            if(painted>0)world.paintStatus+=$"\nМатериал террейна на скалах: {painted}. Собственные маски {Resolution}×{Resolution} по X/Z; без displacement.";
            if(warnings.Count>0)world.paintStatus+="\n"+string.Join("; ",warnings);
        }
    }
}
