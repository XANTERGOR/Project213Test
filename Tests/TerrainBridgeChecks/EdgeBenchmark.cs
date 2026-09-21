using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
partial class Checks
{
    sealed class EdgeKeyComparer : IEqualityComparer<long>
    {
        public static readonly EdgeKeyComparer Instance=new EdgeKeyComparer();
        public bool Equals(long a,long b)=>a==b;
        public int GetHashCode(long key)
        {
            unchecked
            {
                ulong value=(ulong)key;
                value^=value>>33;value*=0xff51afd7ed558ccdUL;
                value^=value>>33;value*=0xc4ceb9fe1a85ec53UL;
                value^=value>>33;
                return (int)(value^(value>>32));
            }
        }
    }
    static void EdgeBenchmark()
    {
        var edges=new List<(int a,int b)>();
        void Tri(int a,int b,int c){edges.Add((a,b));edges.Add((b,c));edges.Add((c,a));}
        const int width=70;
        for(int z=0;z<width;z++)for(int x=0;x<width;x++)
        {
            if(x>25&&x<40&&z>25&&z<40)continue;
            int a=z*(width+1)+x,b=a+1,c=a+width+1,d=c+1;
            Tri(a,c,b);Tri(b,c,d);
        }
        double elapsed=0;
        Dictionary<long,(int count,int a,int b)> Run(bool single,bool mixed=false)
        {
            var timer=Stopwatch.StartNew();
            var counts=new Dictionary<long,int>();var ends=new Dictionary<long,(int a,int b)>();
            var combined=new Dictionary<long,(int count,int a,int b)>(mixed?EdgeKeyComparer.Instance:EqualityComparer<long>.Default);
            foreach(var e in edges)
            {
                long key=((long)Math.Min(e.a,e.b)<<32)|(uint)Math.Max(e.a,e.b);
                if(single){combined.TryGetValue(key,out var value);combined[key]=(value.count+1,e.a,e.b);}
                else{counts[key]=counts.TryGetValue(key,out int n)?n+1:1;ends[key]=e;}
            }
            timer.Stop();elapsed=timer.Elapsed.TotalMilliseconds;
            if(!single)foreach(var p in counts)combined[p.Key]=(p.Value,ends[p.Key].a,ends[p.Key].b);
            return combined;
        }
        var oldResult=Run(false);var newResult=Run(true);
        Require(oldResult.Count==newResult.Count&&oldResult.All(p=>newResult[p.Key]==p.Value),"edge counter equivalence");
        for(int i=0;i<8;i++){Run(false);Run(true);}
        var oldTimes=new List<double>();var newTimes=new List<double>();
        for(int i=0;i<20;i++)for(int j=0;j<2;j++)
        {
            bool single=((i+j)&1)==0;var result=Run(single);
            GC.KeepAlive(result);(single?newTimes:oldTimes).Add(elapsed);
        }
        oldTimes.Sort();newTimes.Sort();
        Console.WriteLine($"EDGE BENCH synthetic {edges.Count/3} triangles, 20 alternating runs: old median {oldTimes[10]:F2} ms, single median {newTimes[10]:F2} ms; exact counts/endpoints PASS. Result conversion excluded; not Unity timings.");
        var mixedResult=Run(true,true);
        Require(newResult.Count==mixedResult.Count&&newResult.All(p=>mixedResult[p.Key]==p.Value),"mixed hash changed edge counts/endpoints");
        for(int i=0;i<8;i++){Run(true);Run(true,true);}
        var defaultTimes=new List<double>();var mixedTimes=new List<double>();
        for(int i=0;i<20;i++)for(int j=0;j<2;j++)
        {
            bool mixed=((i+j)&1)==0;var result=Run(true,mixed);GC.KeepAlive(result);
            (mixed?mixedTimes:defaultTimes).Add(elapsed);
        }
        defaultTimes.Sort();mixedTimes.Sort();
        var keys=newResult.Keys.ToArray();
        int oldHashes=keys.Select(k=>EqualityComparer<long>.Default.GetHashCode(k)).Distinct().Count();
        int mixedHashes=keys.Select(k=>EdgeKeyComparer.Instance.GetHashCode(k)).Distinct().Count();
        Console.WriteLine($"HASH BENCH: {keys.Length} keys; default unique hashes {oldHashes}, mixed {mixedHashes}; default median {defaultTimes[10]:F2} ms [{defaultTimes[0]:F2}–{defaultTimes[19]:F2}], mixed {mixedTimes[10]:F2} ms [{mixedTimes[0]:F2}–{mixedTimes[19]:F2}]. Exact edge equality PASS; outside Unity.");
    }
}
