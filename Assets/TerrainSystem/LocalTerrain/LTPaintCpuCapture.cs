using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Opt-in reports plus a bounded last-tick sample for coordinated editor updates.
    // CPU wall time, not GPU time or FPS. Runtime remains opt-in.
    public sealed class LTPaintCpuCapture
    {
        public enum Stage { Total, TerrainCache, ChangeChecks, WeightBake, LayerBind, SavedSignature, GlobalMaterials, GlobalBake, DisplacementBake, RockMaterials,
            RoadProjection, RoadUVCompute, RoadUVUpload, WeightCompute, WeightUpload, RoadSamplePrepare, RoadSampleResolve, WeightPrepare, WeightResolve,
            WeightTerrainSnapshot, WeightInputSetup, WeightCoordinates, WeightRoadHits, WeightMasks, WeightNativeCopy, WeightSchedule,
            TerrainMeshRead, TerrainTransform, TerrainSpatialIndex, TerrainFingerprint }
        static readonly int StageCount=Enum.GetValues(typeof(Stage)).Length;
        [Serializable] public sealed class Sample
        {
            public double[] milliseconds=new double[StageCount];
            public int weightBakes,layerBinds,globalMaterialUpdates,farMaterialCopies;
            public int weightReuses,roadProjectionBakes;
            public int roadSampleJobs,roadSampleResults,roadSampleBurstResults,roadSamplePixels,roadSampleFallbacks;
            public int weightJobs,weightJobResults,weightBurstResults,weightJobPixels,weightPixelFallbacks,weightBudgetFallbacks;
            public int rockWeightJobs,rockWeightResults,rockWeightBurstResults,rockWeightCpuBakes,rockWeightDiscards;
            public int terrainSnapshotBuilds,terrainSnapshotReuses,terrainSnapshotRejects;
            public int terrainTileReuses,terrainTileBuilds,terrainIndexReuses,terrainSurfaceReuses;
            public int weightInputAllocations,weightInputReuses;
            public long weightInputAllocatedBytes;
            // "Work" here means mask/job activity, not zero CPU: waiting polls may
            // still bind materials, inspect changes or update global parameters.
            public bool HasMaskWork=>weightBakes>0||weightReuses>0||roadProjectionBakes>0||roadSampleJobs>0||roadSampleResults>0||
                weightJobs>0||weightJobResults>0||weightBudgetFallbacks>0||rockWeightDiscards>0;
            public void Add(Sample s)
            {
                for(int i=0;i<StageCount;i++)milliseconds[i]+=s.milliseconds[i];
                weightBakes+=s.weightBakes;layerBinds+=s.layerBinds;globalMaterialUpdates+=s.globalMaterialUpdates;farMaterialCopies+=s.farMaterialCopies;
                weightReuses+=s.weightReuses;roadProjectionBakes+=s.roadProjectionBakes;
                roadSampleJobs+=s.roadSampleJobs;roadSampleResults+=s.roadSampleResults;roadSampleBurstResults+=s.roadSampleBurstResults;
                roadSamplePixels+=s.roadSamplePixels;roadSampleFallbacks+=s.roadSampleFallbacks;
                weightJobs+=s.weightJobs;weightJobResults+=s.weightJobResults;weightBurstResults+=s.weightBurstResults;weightJobPixels+=s.weightJobPixels;
                weightPixelFallbacks+=s.weightPixelFallbacks;weightBudgetFallbacks+=s.weightBudgetFallbacks;
                rockWeightJobs+=s.rockWeightJobs;rockWeightResults+=s.rockWeightResults;rockWeightBurstResults+=s.rockWeightBurstResults;
                rockWeightCpuBakes+=s.rockWeightCpuBakes;rockWeightDiscards+=s.rockWeightDiscards;
                terrainSnapshotBuilds+=s.terrainSnapshotBuilds;terrainSnapshotReuses+=s.terrainSnapshotReuses;terrainSnapshotRejects+=s.terrainSnapshotRejects;
                terrainTileReuses+=s.terrainTileReuses;terrainTileBuilds+=s.terrainTileBuilds;terrainIndexReuses+=s.terrainIndexReuses;terrainSurfaceReuses+=s.terrainSurfaceReuses;
                weightInputAllocations+=s.weightInputAllocations;weightInputReuses+=s.weightInputReuses;weightInputAllocatedBytes+=s.weightInputAllocatedBytes;
            }
        }
        // A coordinator-owned, bounded accumulator. No list of polls and no Jobs
        // wall-time estimate. Reset only on a new authoring/refinement cycle.
        public sealed class Cycle
        {
            public Sample Sum {get;private set;}=new Sample();
            public double[] MaxPerPoll {get;private set;}=new double[StageCount];
            public int Polls {get;private set;}
            public int MeasuredPolls {get;private set;}
            public int WorkPolls {get;private set;}
            public int WaitingOnlyPolls {get;private set;}
            public double CallbackMilliseconds {get;private set;}
            long lastCompletedTick;
            public void Reset(long completedTick)
            {
                Sum=new Sample();Array.Clear(MaxPerPoll,0,MaxPerPoll.Length);
                Polls=MeasuredPolls=WorkPolls=WaitingOnlyPolls=0;CallbackMilliseconds=0;lastCompletedTick=completedTick;
            }
            public void Record(long completedTick,Sample sample,double callbackMilliseconds,bool pending)
            {
                Polls++;CallbackMilliseconds+=callbackMilliseconds;
                // UpdatePainting may return before Tick (benchmark/disabled/ownership
                // gates). LastTick then still belongs to an older invocation.
                if(sample==null||completedTick<=lastCompletedTick)return;
                lastCompletedTick=completedTick;MeasuredPolls++;Sum.Add(sample);
                for(int i=0;i<StageCount;i++)MaxPerPoll[i]=Math.Max(MaxPerPoll[i],sample.milliseconds[i]);
                if(sample.HasMaskWork)WorkPolls++;else if(pending)WaitingOnlyPolls++;
            }
            public string Status(bool settled)
            {
                var text=new System.Text.StringBuilder($"Покраска — сумма за цикл ({(settled?"завершён":"накапливается")}, CPU основного потока):\n");
                text.AppendLine($"Вызовов: {Polls}; с работой масок/Jobs: {WorkPolls}; ожидание без новой работы масок/Jobs: {WaitingOnlyPolls}; прочие измеренные: {MeasuredPolls-WorkPolls-WaitingOnlyPolls}; без нового замера: {Polls-MeasuredPolls}.");
                text.AppendLine($"UpdatePainting целиком: {CallbackMilliseconds:F1} мс. Ниже: сумма / максимум за один измеренный вызов, не за кадр.");
                for(int i=0;i<StageCount;i++)text.AppendLine($"{(Stage)i}: {Sum.milliseconds[i]:F1} / {MaxPerPoll[i]:F1} мс");
                text.AppendLine($"Масок пересчитано: {Sum.weightBakes}; сохранено: {Sum.weightReuses}; дорожных UV/подавления: {Sum.roadProjectionBakes}. Это счётчики карт/чанков, не число вызовов.");
                text.AppendLine($"Поиск сплайна: Jobs запущено {Sum.roadSampleJobs}; принято {Sum.roadSampleResults} (Burst {Sum.roadSampleBurstResults}); ячеек {Sum.roadSamplePixels}; CPU-проверок {Sum.roadSampleFallbacks}.");
                text.AppendLine($"Веса: Jobs запущено {Sum.weightJobs}; принято {Sum.weightJobResults} (Burst {Sum.weightBurstResults}); пикселей {Sum.weightJobPixels}; CPU-округление {Sum.weightPixelFallbacks}; CPU из-за бюджета снимка {Sum.weightBudgetFallbacks}.");
                text.AppendLine(RockStatus(Sum));
                text.AppendLine(SnapshotStatus(Sum));
                text.AppendLine(PreparationReuseStatus(Sum));
                text.AppendLine($"Привязки слоёв: {Sum.layerBinds}; глобальные параметры материалов: {Sum.globalMaterialUpdates}; копии дальних материалов: {Sum.farMaterialCopies}.");
                text.Append("Этапы вложенные, не складывайте: Total включает остальные; WeightBake включает RoadProjection + WeightCompute + WeightUpload; RoadUVCompute/Upload входят в RoadProjection. Маски скал входят также в RockMaterials. Prepare/Resolve и Upload — CPU, не время выполнения Jobs или GPU. Паузы между вызовами не включены. Только вызовы автоматического координатора в этом цикле; ручные и внецикловые обновления не включены.");
                return text.ToString();
            }
        }
        [Serializable] public sealed class Report
        {
            public string utc,unityVersion,worldName,mode;
            public string note="CPU wall time of painting polls only; not GPU/frame time. Stage times are included in Total. Allocations include diagnostic overhead. Camera selection measured separately.";
            public string[] stages=Enum.GetNames(typeof(Stage));
            public double elapsedSeconds,cameraSelectionMs;
            public int cameraCalls;
            public long allocatedBytes;
            public List<Sample> samples=new List<Sample>();
        }
        public bool Running {get;private set;}
        public string Status="CPU-замер ещё не запускался.";
        Report report;
        Sample current;
        public Sample LastTick {get;private set;}
        public long CompletedTicks {get;private set;}
        public string LastTickStatus
        {
            get
            {
                if(LastTick==null)return "";
                var text=new System.Text.StringBuilder("Последний рабочий проход покраски (CPU):\n");
                for(int i=0;i<StageCount;i++)text.AppendLine($"{(Stage)i}: {LastTick.milliseconds[i]:F1} мс");
                text.AppendLine($"Масок пересчитано: {LastTick.weightBakes}; сохранено при изменении дороги: {LastTick.weightReuses}; карт дорожных UV/подавления обновлено: {LastTick.roadProjectionBakes}.");
                text.AppendLine($"Поиск сплайна: Jobs запущено {LastTick.roadSampleJobs}; принято {LastTick.roadSampleResults} (Burst {LastTick.roadSampleBurstResults}); ячеек {LastTick.roadSamplePixels}; неоднозначных CPU {LastTick.roadSampleFallbacks}. Prepare/Resolve — основной поток, не время Job.");
                text.AppendLine($"Веса покрытия: Jobs запущено {LastTick.weightJobs}; принято {LastTick.weightJobResults} (Burst {LastTick.weightBurstResults}); пикселей {LastTick.weightJobPixels}; CPU-проверки округления {LastTick.weightPixelFallbacks}; CPU из-за бюджета снимка {LastTick.weightBudgetFallbacks}. WeightPrepare/Resolve — основной поток, не время Job. Начальные/ручные/Play-пересчёты — CPU.");
                text.AppendLine(RockStatus(LastTick));
                text.AppendLine(SnapshotStatus(LastTick));
                text.AppendLine(PreparationReuseStatus(LastTick));
                text.Append("Этапы вложенные, не складывайте: WeightBake включает RoadProjection + WeightCompute + WeightUpload; RoadUVCompute/Upload входят в RoadProjection. Маски скал входят также в RockMaterials. Upload — время CPU, не GPU.");
                return text.ToString();
            }
        }
        long start,allocatedStart;
        public void Start(string worldName)
        {
            if(Running)return;
            report=new Report{utc=DateTime.UtcNow.ToString("O"),unityVersion=Application.unityVersion,worldName=worldName,mode=Application.isPlaying?"Play":"Edit"};
            current=null;start=Stopwatch.GetTimestamp();allocatedStart=GC.GetAllocatedBytesForCurrentThread();
            Running=true;Status="CPU-замер: 10 секунд. Не двигайте камеру и не меняйте штампы.";
        }
        public Scope BeginTick(bool captureLast=false)
        {
            if(!Running&&!captureLast)return default;
            current=new Sample();if(Running)report.samples.Add(current);return Measure(Stage.Total);
        }
        public Scope Measure(Stage stage)=>current!=null?new Scope(this,current,(int)stage):default;
        public Scope CameraScope()=>Running?new Scope(this,null,-1):default;
        public void WeightBaked(){if(current!=null)current.weightBakes++;}
        public void WeightReused(){if(current!=null)current.weightReuses++;}
        public void RoadProjectionBaked(){if(current!=null)current.roadProjectionBakes++;}
        public void RoadSampleScheduled(){if(current!=null)current.roadSampleJobs++;}
        public void WeightJobScheduled(){if(current!=null)current.weightJobs++;}
        public void WeightPixelFallback(){if(current!=null)current.weightPixelFallbacks++;}
        public void WeightJobBudgetFallback(){if(current!=null)current.weightBudgetFallbacks++;}
        static string RockStatus(Sample s)=>$"Из них скалы: Jobs запущено {s.rockWeightJobs}; принято {s.rockWeightResults} (Burst {s.rockWeightBurstResults}); полных CPU-пересчётов {s.rockWeightCpuBakes}; устаревших задач отброшено {s.rockWeightDiscards}. Включены в общие счётчики весов, не добавляйте повторно.";
        static string SnapshotStatus(Sample s)=>$"Снимки terrain для весов: собрано {s.terrainSnapshotBuilds}; из кэша {s.terrainSnapshotReuses}; вне бюджета {s.terrainSnapshotRejects}. Кэш до 4 снимков / 16 МиБ числовых данных и ключей на мир; native-копии Jobs отдельные. WeightTerrainSnapshot/InputSetup/Coordinates/RoadHits/Masks/NativeCopy/Schedule вложены в WeightPrepare; это CPU, не время Job.";
        public void TerrainSnapshotPrepared(bool made,bool reused)
        {if(current!=null){if(!made)current.terrainSnapshotRejects++;else if(reused)current.terrainSnapshotReuses++;else current.terrainSnapshotBuilds++;}}
        static string PreparationReuseStatus(Sample s)=>$"TerrainCache: без чтения меша {s.terrainTileReuses}; прочитано {s.terrainTileBuilds}; из прочитанных сохранён индекс XZ {s.terrainIndexReuses}, поверхность целиком {s.terrainSurfaceReuses}. Счётчики за вызовы, не уникальные чанки. TerrainMeshRead/Transform/SpatialIndex/Fingerprint вложены в TerrainCache.\n"+
            $"Входные массивы весов: новых {s.weightInputAllocations}; из пула {s.weightInputReuses}; выделено {s.weightInputAllocatedBytes/1048576.0:F2} МиБ (с запасом на дескрипторы). Пул до 16 МиБ на мир, возврат после native-копирования; это не кэш координат и не буферы работающих Jobs.";
        public void TerrainTileReused(){if(current!=null)current.terrainTileReuses++;}
        public void TerrainTilePrepared(bool index,bool surface)
        {if(current!=null){current.terrainTileBuilds++;if(index)current.terrainIndexReuses++;if(surface)current.terrainSurfaceReuses++;}}
        public void WeightInputRented(int created,int reused,int allocated)
        {if(current!=null){current.weightInputAllocations+=created;current.weightInputReuses+=reused;current.weightInputAllocatedBytes+=allocated;}}
        public void RockWeightScheduled(){if(current!=null)current.rockWeightJobs++;}
        public void RockWeightResolved(bool burst){if(current!=null){current.rockWeightResults++;if(burst)current.rockWeightBurstResults++;}}
        public void RockWeightCpuBaked(){if(current!=null)current.rockWeightCpuBakes++;}
        public void RockWeightDiscarded(){if(current!=null)current.rockWeightDiscards++;}
        public void WeightJobResolved(bool burst,int pixels)
        {if(current!=null){current.weightJobResults++;if(burst)current.weightBurstResults++;current.weightJobPixels+=pixels;}}
        public void RoadSampleResolved(bool burst,int pixels,int fallbacks)
        {if(current!=null){current.roadSampleResults++;if(burst)current.roadSampleBurstResults++;current.roadSamplePixels+=pixels;current.roadSampleFallbacks+=fallbacks;}}
        public void LayerBound(){if(current!=null)current.layerBinds++;}
        public void GlobalUpdated(bool copied)
        {if(current!=null){current.globalMaterialUpdates++;if(copied)current.farMaterialCopies++;}}
        public readonly struct Scope : IDisposable
        {
            readonly LTPaintCpuCapture owner;
            readonly Sample sample;
            readonly int stage;
            readonly long time;
            internal Scope(LTPaintCpuCapture owner,Sample sample,int stage)
            {this.owner=owner;this.sample=sample;this.stage=stage;time=Stopwatch.GetTimestamp();}
            public void Dispose()
            {
                if(owner==null)return;
                double ms=(Stopwatch.GetTimestamp()-time)*1000.0/Stopwatch.Frequency;
                if(stage<0){owner.report.cameraSelectionMs+=ms;owner.report.cameraCalls++;}
                else sample.milliseconds[stage]+=ms;
                if(stage==0){owner.LastTick=sample;owner.CompletedTicks++;owner.current=null;}
            }
        }
        public void Poll()
        {
            if(!Running||(Stopwatch.GetTimestamp()-start)/(double)Stopwatch.Frequency<10)return;
            Running=false;
            report.elapsedSeconds=(Stopwatch.GetTimestamp()-start)/(double)Stopwatch.Frequency;
            report.allocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocatedStart;
            var text=new System.Text.StringBuilder();
            text.AppendLine($"CPU: {report.samples.Count} проходов за {report.elapsedSeconds:F1} с. Время на проход, не на кадр:");
            for(int stage=0;stage<report.stages.Length;stage++)
            {
                var values=new List<double>();double sum=0;
                foreach(var sample in report.samples){double ms=sample.milliseconds[stage];sum+=ms;values.Add(ms);}
                values.Sort();int n=values.Count;
                text.AppendLine($"{(Stage)stage}: mean {(n>0?sum/n:0):F3}, p95 {(n>0?values[Math.Min(n-1,(int)Math.Ceiling(n*.95)-1)]:0):F3}, max {(n>0?values[n-1]:0):F3} ms");
            }
            int bakes=0,binds=0;foreach(var sample in report.samples){bakes+=sample.weightBakes;binds+=sample.layerBinds;}
            text.AppendLine($"Пересчёты масок: {bakes}; привязки слоёв: {binds}. Camera selection: {report.cameraCalls} вызовов / {report.cameraSelectionMs:F3} ms суммарно.");
            int reuses=0,projections=0;foreach(var sample in report.samples){reuses+=sample.weightReuses;projections+=sample.roadProjectionBakes;}
            text.AppendLine($"Сохранено масок при изменении дороги: {reuses}; обновлено карт дорожных UV/подавления: {projections}. RoadUVCompute/Upload вложены в RoadProjection, а он вместе с WeightCompute/Upload — в WeightBake. Upload — CPU, не GPU.");
            int globals=0,copies=0;foreach(var sample in report.samples){globals+=sample.globalMaterialUpdates;copies+=sample.farMaterialCopies;}
            text.AppendLine($"Обновления глобальных параметров материалов: {globals}; копирования дальних материалов: {copies}.");
            text.AppendLine($"Аллокации всего main thread за окно: {report.allocatedBytes/1048576.0:F2} MiB (включая другие системы и диагностику).");
#if UNITY_EDITOR
            try
            {
                string folder=System.IO.Path.GetFullPath("Logs/LocalTerrainCpuBenchmarks");
                System.IO.Directory.CreateDirectory(folder);
                string path=System.IO.Path.Combine(folder,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")+".json");
                System.IO.File.WriteAllText(path,JsonUtility.ToJson(report,true));text.AppendLine(path);
            }
            catch(Exception error){text.AppendLine("Не удалось сохранить отчёт: "+error.Message);}
#endif
            Status=text.ToString();UnityEngine.Debug.Log(Status);
        }
    }
}
