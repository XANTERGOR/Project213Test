using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LocalTerrainPrototype
{
    // Opt-in, bounded capture. No strings, lists or per-call allocations while
    // recording. Summaries and disk writes belong outside the render callback.
    public sealed class LTLODCpuCapture
    {
        public const int Capacity=8192;
        public enum Stage { Total, Cache, TopologySetup, Selection, Balance, TerrainApply, Clone, Indices, Upload, Roads }
        [Serializable] public struct Sample
        {
            public double offsetSeconds,total,cache,topologySetup,selection,balance,terrainApply,clone,indices,upload,roads;
            public int cameraId,forceLOD,chunksVisited,dirtyChunks,resetPatches,processedCells,uploadedChunks,clonedMeshes,topologyBuilds;
            public long uploadedIndices;
            public int indexCacheHits,indexCacheBuilds,indexCacheCells,indexCacheFallbacks;
            public long indexCacheBytes;
            public float cameraX,cameraY,cameraZ;
            public bool beforeRender,spatialRequested,spatialReady,completed,topologyFailed;
            public double Get(Stage stage)
            {
                switch(stage)
                {
                    case Stage.Total:return total;
                    case Stage.Cache:return cache;
                    case Stage.TopologySetup:return topologySetup;
                    case Stage.Selection:return selection;
                    case Stage.Balance:return balance;
                    case Stage.TerrainApply:return terrainApply;
                    case Stage.Clone:return clone;
                    case Stage.Indices:return indices;
                    case Stage.Upload:return upload;
                    default:return roads;
                }
            }
            internal void Add(Stage stage,double ms)
            {
                switch(stage)
                {
                    case Stage.Total:total+=ms;break;
                    case Stage.Cache:cache+=ms;break;
                    case Stage.TopologySetup:topologySetup+=ms;break;
                    case Stage.Selection:selection+=ms;break;
                    case Stage.Balance:balance+=ms;break;
                    case Stage.TerrainApply:terrainApply+=ms;break;
                    case Stage.Clone:clone+=ms;break;
                    case Stage.Indices:indices+=ms;break;
                    case Stage.Upload:upload+=ms;break;
                    case Stage.Roads:roads+=ms;break;
                }
            }
        }
        [Serializable] public sealed class Report
        {
            public string utc,world,mode,settings,stopReason;
            public string note="CPU elapsed time per accepted UpdateLOD invocation, NOT frame/GPU time. Total includes all stages; TerrainApply includes Clone/Indices/Upload. Stage maxima can belong to different calls. Skipped camera callbacks and other systems are not measured. Diagnostic overhead is included.";
            public double requestedSeconds,elapsedSeconds;
            public Sample[] samples;
        }
        readonly Func<long> clock;
        readonly double frequency;
        Sample[] buffer;
        Sample current;
        long start;
        int count,epoch;
        bool recording;
        Report report;
        public bool Running {get;private set;}
        public Report LastReport {get;private set;}
        public string Status {get;private set;}="Замер переключения LOD ещё не запускался.";
        public LTLODCpuCapture():this(Stopwatch.GetTimestamp,Stopwatch.Frequency){}
        // Deterministic clock for managed tests; production always uses Stopwatch.
        internal LTLODCpuCapture(Func<long> clock,double frequency){this.clock=clock;this.frequency=frequency;}
        public void Start(string world,string mode,string settings,double seconds=15)
        {
            if(Running)return;
            if(double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<=0)throw new ArgumentOutOfRangeException(nameof(seconds));
            buffer=new Sample[Capacity];count=0;recording=false;
            report=new Report{utc=DateTime.UtcNow.ToString("O"),world=world,mode=mode,settings=settings,requestedSeconds=seconds};
            LastReport=null;
            Status=$"Идёт CPU-замер LOD ({seconds:0.#} секунд). Летайте по проблемному маршруту; настройки и штампы не меняйте. Отчёт появится автоматически после записи.";
            start=clock();Running=true;
        }
        public Scope BeginUpdate(int cameraId,float x,float y,float z,bool beforeRender,int forceLOD)
        {
            if(!Running||recording||count==Capacity)return default;
            long now=clock();
            if((now-start)/frequency>=report.requestedSeconds)return default;
            current=new Sample{offsetSeconds=(now-start)/frequency,cameraId=cameraId,cameraX=x,cameraY=y,cameraZ=z,beforeRender=beforeRender,forceLOD=forceLOD};
            recording=true;epoch++;
            return new Scope(this,Stage.Total,now,epoch);
        }
        public Scope Measure(Stage stage)=>recording?new Scope(this,stage,clock(),epoch):default;
        public void TerrainState(bool requested,bool ready,int visited)
        {if(recording){current.spatialRequested=requested;current.spatialReady=ready;current.chunksVisited=visited;}}
        public void TopologyResult(int dirty,int reset,int cells)
        {if(recording){current.dirtyChunks=dirty;current.resetPatches=reset;current.processedCells=cells;}}
        public void TopologyBuilt(){if(recording)current.topologyBuilds++;}
        public void IndexCacheResult(int hits,int builds,int cells,int fallbacks,long bytes)
        {if(recording){current.indexCacheHits=hits;current.indexCacheBuilds=builds;current.indexCacheCells=cells;current.indexCacheFallbacks=fallbacks;current.indexCacheBytes=bytes;}}
        public void TopologyFailed(){if(recording)current.topologyFailed=true;}
        public void MeshCloned(){if(recording)current.clonedMeshes++;}
        public void Uploaded(int indices){if(recording){current.uploadedChunks++;current.uploadedIndices+=indices;}}
        public void Completed(){if(recording)current.completed=true;}
        public readonly struct Scope:IDisposable
        {
            readonly LTLODCpuCapture owner;
            readonly Stage stage;
            readonly long start;
            readonly int epoch;
            internal Scope(LTLODCpuCapture owner,Stage stage,long start,int epoch)
            {this.owner=owner;this.stage=stage;this.start=start;this.epoch=epoch;}
            public void Dispose()
            {
                if(owner==null||!owner.recording||owner.epoch!=epoch)return;
                owner.current.Add(stage,(owner.clock()-start)*1000.0/owner.frequency);
                if(stage==Stage.Total)
                {
                    owner.buffer[owner.count++]=owner.current;
                    owner.recording=false;
                }
            }
        }
        // Called by Update/editor polling, never from BeginUpdate or Scope.Dispose.
        public bool Poll()
        {
            if(!Running||recording)return false;
            if(count==Capacity)return Stop("buffer full");
            if((clock()-start)/frequency>=report.requestedSeconds)return Stop("duration reached");
            return false;
        }
        public bool Stop(string reason="manual")
        {
            if(!Running||recording)return false;
            Running=false;report.stopReason=reason;report.elapsedSeconds=(clock()-start)/frequency;
            report.samples=new Sample[count];Array.Copy(buffer,report.samples,count);buffer=null;
            LastReport=report;Status=Summarize(report);return true;
        }
        public static string Summarize(Report report)
        {
            var samples=report.samples;int n=samples.Length,changed=0,cold=0,failed=0,worst=0;
            var totals=new double[n];double sum=0,changedSum=0,changedMax=0;
            for(int i=0;i<n;i++)
            {
                var s=samples[i];totals[i]=s.total;sum+=s.total;
                if(s.uploadedChunks>0){changed++;changedSum+=s.total;changedMax=Math.Max(changedMax,s.total);}
                if(s.topologyBuilds>0||s.clonedMeshes>0)cold++;
                if(!s.completed||s.topologyFailed)failed++;
                if(s.total>samples[worst].total)worst=i;
            }
            Array.Sort(totals);
            string F(double value)=>value.ToString("F3",CultureInfo.InvariantCulture);
            var text=new StringBuilder("LOD flight CPU — время на вызов UpdateLOD, НЕ на кадр/GPU.\n");
            text.AppendLine($"{report.mode}; {report.settings}");
            text.AppendLine($"Вызовов: {n}; с загрузкой индексов: {changed}; с созданием топологии/мешей: {cold}; неполных/ошибок: {failed}. Остановка: {report.stopReason}.");
            if(n==0){text.Append("Нет замеров: проверьте активность мира, generatedRoot и обновление камеры.");return text.ToString();}
            text.AppendLine($"Total mean / p95 / max: {F(sum/n)} / {F(totals[Math.Max(0,(int)Math.Ceiling(n*.95)-1)])} / {F(totals[n-1])} ms.");
            text.AppendLine($"Вызовы с загрузкой — mean / max: {F(changed>0?changedSum/changed:0)} / {F(changedMax)} ms.");
            text.AppendLine("Этапы: mean / max за вызов (максимумы могут быть из разных вызовов):");
            for(int stage=1;stage<= (int)Stage.Roads;stage++)
            {
                double stageSum=0,max=0;
                foreach(var s in samples){double v=s.Get((Stage)stage);stageSum+=v;max=Math.Max(max,v);}
                text.AppendLine($"{(Stage)stage}: {F(stageSum/n)} / {F(max)} ms");
            }
            var peak=samples[worst];
            text.AppendLine($"Самый тяжёлый вызов #{worst} (+{F(peak.offsetSeconds)} s), Total {F(peak.total)} ms; beforeRender={peak.beforeRender}; Force LOD={peak.forceLOD}; spatial={peak.spatialRequested}/{peak.spatialReady}.");
            text.AppendLine($"В нём: Balance {F(peak.balance)}; Indices {F(peak.indices)}; Upload {F(peak.upload)}; Clone {F(peak.clone)} ms.");
            text.AppendLine($"Чанков в обходе {peak.chunksVisited}; dirty {peak.dirtyChunks}; загружено {peak.uploadedChunks}; индексов {peak.uploadedIndices}; новых мешей {peak.clonedMeshes}; топологий {peak.topologyBuilds}; reset patches {peak.resetPatches}; обработано ячеек {peak.processedCells}.");
            text.AppendLine($"В нём кэш индексов: использовано участков {peak.indexCacheHits}; собрано {peak.indexCacheBuilds} ({peak.indexCacheCells} ячеек); без сохранения из-за бюджета {peak.indexCacheFallbacks}; ёмкость {F(peak.indexCacheBytes/1048576.0)} MiB, лимит 64 MiB на топологию (без дескрипторов).");
            text.Append("Total включает все этапы; TerrainApply включает Clone + Indices + Upload. Upload — CPU-время SetIndices, не время GPU. Паузы между вызовами и другие системы не измеряются; накладные расходы замера включены.");
            return text.ToString();
        }
    }
}
