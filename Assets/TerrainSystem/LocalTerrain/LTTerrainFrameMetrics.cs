using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Recorders activate FrameTiming counters even in Release Players without
    // persisting PlayerSettings.enableFrameTimingStats. No full/deep Profiler.
    // GPU/CPU timings are delayed; do NOT pair their rows with wall-clock rows.
    internal sealed class LTTerrainFrameMetrics : IDisposable
    {
        internal sealed class Metric
        {
            public readonly string name,unit;
            public readonly ProfilerCategory category;
            public readonly double multiplier;
            public readonly bool positive;
            public Metric(string name,ProfilerCategory category,string unit="ms",bool positive=true)
            {this.name=name;this.category=category;this.unit=unit;this.positive=positive;multiplier=unit=="ms"?1e-6:1;}
        }
        internal static readonly Metric[] Metrics={
            new Metric("CPU Total Frame Time",ProfilerCategory.Internal),
            new Metric("CPU Main Thread Frame Time",ProfilerCategory.Internal),
            new Metric("CPU Render Thread Frame Time",ProfilerCategory.Internal),
            new Metric("GPU Frame Time",ProfilerCategory.Internal),
            new Metric("Draw Calls Count",ProfilerCategory.Render,"count",false),
            new Metric("Batches Count",ProfilerCategory.Render,"count",false),
            new Metric("SetPass Calls Count",ProfilerCategory.Render,"count",false),
            new Metric("Triangles Count",ProfilerCategory.Render,"count",false),
            new Metric("Vertices Count",ProfilerCategory.Render,"count",false),
            new Metric("GC Allocated In Frame",ProfilerCategory.Memory,"bytes",false)
        };
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        sealed class TimingOrder : IComparer<FrameTiming>
        {public int Compare(FrameTiming a,FrameTiming b)=>a.frameStartTimestamp.CompareTo(b.frameStartTimestamp);}
        static readonly TimingOrder Order=new TimingOrder();
        readonly ProfilerRecorder[] recorders=new ProfilerRecorder[Metrics.Length];
        readonly string[] availability=new string[Metrics.Length];
        readonly FrameTiming[] buffer=new FrameTiming[32];
        readonly List<FrameTiming> native=new List<FrameTiming>(8192);
        readonly List<double> values=new List<double>(8192*Metrics.Length);
        readonly List<int> observedFrames=new List<int>(8192);
        ulong lastTimestamp;
        int ticks;
        bool active,nativeFailed;
        string nativeStatus;

        public void Begin()
        {
            Dispose();native.Clear();values.Clear();observedFrames.Clear();lastTimestamp=0;ticks=0;nativeFailed=false;
            nativeStatus="No native timings returned yet.";active=true;
            for(int i=0;i<recorders.Length;i++)
            {
                try
                {
                    recorders[i]=ProfilerRecorder.StartNew(Metrics[i].category,Metrics[i].name,1);
                    availability[i]=recorders[i].Valid?"created":"unavailable in this build/API";
                }
                catch(Exception error){availability[i]="unavailable: "+error.GetType().Name;}
            }
        }

        public void Poll(bool sampling)
        {
            if(!active)return;
            ticks++;
            // At least eight rendered Updates plus the controller's two-second
            // warmup precede retained timings (Unity normally delays four frames).
            bool retain=sampling&&ticks>8;
            if(sampling)
            {
                observedFrames.Add(Time.frameCount);
                for(int i=0;i<recorders.Length;i++)
                {
                    var recorder=recorders[i];double value=double.NaN;
                    if(retain&&recorder.Valid&&recorder.IsRunning&&recorder.Count>0)
                    {
                        long raw=recorder.LastValue;
                        if(raw>=0&&(!Metrics[i].positive||raw>0))value=raw*Metrics[i].multiplier;
                    }
                    values.Add(value);
                }
            }
            // Keep collection running across warmup and recording. Reset() STOPS
            // the native recorder; resetting every Update also prevents frame-end
            // publication. LastValue is an observation of the latest completed
            // sample, not proof of a new timestamp (native timings are separate).
            if(nativeFailed)return;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                int count=(int)FrameTimingManager.GetLatestTimings((uint)buffer.Length,buffer);
                if(count==0)return;
                nativeStatus="Timestamped FrameTiming data received (delayed).";
                Array.Sort(buffer,0,count,Order);
                for(int i=0;i<count;i++)
                {
                    var timing=buffer[i];
                    if(timing.frameStartTimestamp==0||timing.frameStartTimestamp<=lastTimestamp)continue;
                    lastTimestamp=timing.frameStartTimestamp;
                    if(retain)native.Add(timing);
                }
            }
            catch(Exception error){nativeFailed=true;nativeStatus="Native timing unavailable: "+error.GetType().Name;}
        }

        public Snapshot Finish()
        {
            for(int i=0;i<recorders.Length;i++)if(recorders[i].Valid)
                availability[i]=$"{(recorders[i].IsRunning?"running":"STOPPED")}; buffered={recorders[i].Count}";
            var result=new Snapshot(values.ToArray(),observedFrames.ToArray(),native.ToArray(),(string[])availability.Clone(),nativeStatus);
            Dispose();return result;
        }
        public void Dispose()
        {
            active=false;
            for(int i=0;i<recorders.Length;i++)
            {
                if(recorders[i].Valid)recorders[i].Dispose();
                recorders[i]=default;
            }
        }

        internal sealed class Snapshot
        {
            public readonly double[] values;
            public readonly int[] observedFrames;
            public readonly FrameTiming[] native;
            public readonly string summary,brief;
            public readonly bool hasCpuGpu;
            public Snapshot(double[] values,int[] frames,FrameTiming[] native,string[] availability,string status)
            {
                this.values=values;observedFrames=frames;this.native=native;
                var report=new StringBuilder();var briefText=new StringBuilder();
                for(int metric=0;metric<Metrics.Length;metric++)
                {
                    var list=new List<double>(frames.Length);
                    for(int row=0;row<frames.Length;row++)list.Add(values[row*Metrics.Length+metric]);
                    if(metric<4)foreach(double value in list)if(Positive(value)){hasCpuGpu=true;break;}
                    report.Append(Metrics[metric].name).Append(": ").Append(Statistics(list,Metrics[metric].unit))
                        .Append("; recorder=").AppendLine(availability[metric]);
                    if(metric==1||metric==2||metric==3)
                    {
                        if(briefText.Length>0)briefText.Append(" | ");
                        briefText.Append(metric==1?"CPU main ":metric==2?"render ":"GPU ").Append(MeanText(list)).Append(" ms");
                    }
                }
                var wait=new List<double>(native.Length);
                foreach(var timing in native)
                {
                    if(Positive(timing.cpuFrameTime)||Positive(timing.gpuFrameTime))hasCpuGpu=true;
                    wait.Add(Positive(timing.cpuFrameTime)&&Finite(timing.cpuMainThreadPresentWaitTime)&&timing.cpuMainThreadPresentWaitTime>=0?
                        timing.cpuMainThreadPresentWaitTime:double.NaN);
                }
                report.Append("CPU main Present/target-FPS wait (native): ").AppendLine(Statistics(wait,"ms"));
                report.Append("Native unique timestamps: ").Append(native.Length).Append("; ").AppendLine(status);
                if(!hasCpuGpu)report.AppendLine("WARNING: no valid CPU/GPU timings. Wall FPS only; this capture cannot distinguish a CPU from a GPU bottleneck.");
                brief=briefText.ToString();summary=report.ToString();
            }
            static string MeanText(List<double> values)
            {
                double total=0;int count=0;
                foreach(double value in values)if(Finite(value)&&value>=0){total+=value;count++;}
                return count==0?"N/A":(total/count).ToString("F2",Inv);
            }
            internal static string Statistics(IList<double> values,string unit)
            {
                var valid=new List<double>(values.Count);double total=0;
                foreach(double value in values)if(Finite(value)&&value>=0){valid.Add(value);total+=value;}
                if(valid.Count==0)return $"N/A (valid 0/{values.Count})";
                valid.Sort();
                return string.Format(Inv,"mean={0:F3}, median={1:F3}, p95={2:F3}, max={3:F3} {4}; valid={5}/{6}",
                    total/valid.Count,valid[(int)Math.Ceiling(valid.Count*.5)-1],valid[(int)Math.Ceiling(valid.Count*.95)-1],valid[valid.Count-1],unit,valid.Count,values.Count);
            }
            public void AppendCounters(StringBuilder csv,int mode)
            {
                for(int row=0;row<observedFrames.Length;row++)
                {
                    csv.Append(mode).Append(',').Append(row).Append(',').Append(observedFrames[row]);
                    for(int metric=0;metric<Metrics.Length;metric++)csv.Append(',').Append(Number(values[row*Metrics.Length+metric]));
                    csv.Append('\n');
                }
            }
            public void AppendNative(StringBuilder csv,int mode)
            {
                foreach(var timing in native)
                    csv.Append(mode).Append(',').Append(timing.frameStartTimestamp).Append(',')
                        .Append(Number(ValidTime(timing.cpuFrameTime))).Append(',').Append(Number(ValidTime(timing.cpuMainThreadFrameTime))).Append(',')
                        .Append(Number(ValidTime(timing.cpuRenderThreadFrameTime))).Append(',').Append(Number(ValidTime(timing.gpuFrameTime))).Append(',')
                        .Append(Number(Positive(timing.cpuFrameTime)?timing.cpuMainThreadPresentWaitTime:double.NaN)).Append(',')
                        .Append(timing.syncInterval).Append(',').Append(Number(timing.widthScale)).Append(',').Append(Number(timing.heightScale)).Append('\n');
            }
        }
        static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
        static bool Positive(double value)=>Finite(value)&&value>0;
        static double ValidTime(double value)=>Positive(value)?value:double.NaN;
        static string Number(double value)=>Finite(value)&&value>=0?value.ToString("R",Inv):"";
    }
}
