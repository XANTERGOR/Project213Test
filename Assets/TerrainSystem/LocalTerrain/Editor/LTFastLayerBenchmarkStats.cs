using System;
using System.Collections.Generic;

namespace LocalTerrainPrototype
{
    // Pure statistics shared with the standalone managed regression checks.
    internal static class LTFastLayerBenchmarkStats
    {
        public const int Phases=4, Samples=120, WarmupFrames=60;
        public static double Quantile(IList<double> values,double percentile)
        {
            if(values.Count==0)return double.NaN;
            var sorted=new double[values.Count];values.CopyTo(sorted,0);Array.Sort(sorted);
            double at=Math.Max(0,Math.Min(1,percentile))*(sorted.Length-1);
            int lower=(int)at,upper=Math.Min(lower+1,sorted.Length-1);
            return sorted[lower]+(sorted[upper]-sorted[lower])*(at-lower);
        }
        public static double Reduction(double baseline,double candidate)=>baseline>0?(baseline-candidate)/baseline*100:double.NaN;
        // A zero, repeated or multi-block observation is not a usable GPU timing.
        public static bool Accept(int frame,int previousFrame,int blocks,long nanoseconds)=>
            frame!=previousFrame&&blocks==1&&nanoseconds>0;
    }
}
