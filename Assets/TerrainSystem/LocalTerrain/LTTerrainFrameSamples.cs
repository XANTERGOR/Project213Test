using System;
using System.Collections.Generic;

namespace LocalTerrainPrototype
{
    // Wall-clock frame intervals, NOT CPU work or GPU timings. Independent of
    // Unity for deterministic tests; no per-frame allocations after construction.
    internal sealed class LTTerrainFrameSamples
    {
        public const double WarmupSeconds=2,SampleSeconds=5;
        public readonly List<double> milliseconds=new List<double>(8192);
        public double start,previous,elapsed;
        public bool Running {get;private set;}
        public bool Sampling {get;private set;}
        public void Start(double now)
        {milliseconds.Clear();start=previous=now;elapsed=0;Sampling=false;Running=true;}
        public void Cancel(){Running=false;Sampling=false;}
        public bool Tick(double now)
        {
            if(!Running)return false;
            if(!IsFinite(now)||now<=previous){Cancel();return false;}
            double interval=now-previous;previous=now;
            if(!Sampling)
            {
                // First interval after the warm-up still straddles its boundary; skip it.
                if(now-start>=WarmupSeconds)Sampling=true;
                return false;
            }
            milliseconds.Add(interval*1000);elapsed+=interval;
            if(elapsed<SampleSeconds)return false;
            Running=false;return true;
        }
        public static bool IsFinite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
        public static bool TrySummarize(IList<double> values,out double mean,out double median,out double p95)
        {
            mean=median=p95=double.NaN;
            if(values==null||values.Count==0)return false;
            var sorted=new double[values.Count];double sum=0;
            for(int i=0;i<values.Count;i++)
            {
                double value=values[i];if(!IsFinite(value)||value<=0)return false;
                sorted[i]=value;sum+=value;
            }
            Array.Sort(sorted);mean=sum/sorted.Length;
            median=sorted[(int)Math.Ceiling(sorted.Length*.5)-1];
            p95=sorted[(int)Math.Ceiling(sorted.Length*.95)-1];return true;
        }
    }
}
