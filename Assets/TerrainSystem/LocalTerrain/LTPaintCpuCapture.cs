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
            RoadProjection, RoadUVCompute, RoadUVUpload, WeightCompute, WeightUpload }
        static readonly int StageCount=Enum.GetValues(typeof(Stage)).Length;
        [Serializable] public sealed class Sample
        {
            public double[] milliseconds=new double[StageCount];
            public int weightBakes,layerBinds,globalMaterialUpdates,farMaterialCopies;
            public int weightReuses,roadProjectionBakes;
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
        public string LastTickStatus
        {
            get
            {
                if(LastTick==null)return "";
                var text=new System.Text.StringBuilder("Последний рабочий проход покраски (CPU):\n");
                for(int i=0;i<StageCount;i++)text.AppendLine($"{(Stage)i}: {LastTick.milliseconds[i]:F1} мс");
                text.AppendLine($"Масок пересчитано: {LastTick.weightBakes}; сохранено при изменении дороги: {LastTick.weightReuses}; карт дорожных UV/подавления обновлено: {LastTick.roadProjectionBakes}.");
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
                if(stage==0){owner.LastTick=sample;owner.current=null;}
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
