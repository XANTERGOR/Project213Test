using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Editor-owned numeric snapshots. No resampling / quantisation is introduced:
    // the source interpolation is identical to LTSource.Sample, and composed
    // heights retain the exact float coordinates of each caller.
    internal static class LTHeightSampling
    {
        internal sealed class Source
        {
            readonly float[] heights;
            public readonly int resolution;
            public readonly Vector3 size;
            public Source(float[] values,int resolution,Vector3 size)
            {
                if(values==null||resolution<2||(long)resolution*resolution>values.Length||
                    !(size.x>0)||!(size.z>0)||float.IsInfinity(size.x)||float.IsInfinity(size.z))
                    throw new InvalidOperationException("Invalid terrain height source.");
                this.resolution=resolution;this.size=size;
                heights=new float[checked(resolution*resolution)];Array.Copy(values,heights,heights.Length);
            }
            public float Sample(float x,float z)
            {
                float fx=Mathf.Clamp01(x/size.x)*(resolution-1),fz=Mathf.Clamp01(z/size.z)*(resolution-1);
                int ix=Mathf.Min(Mathf.FloorToInt(fx),resolution-2),iz=Mathf.Min(Mathf.FloorToInt(fz),resolution-2);
                float tx=fx-ix,tz=fz-iz;
                return Mathf.Lerp(Mathf.Lerp(heights[iz*resolution+ix],heights[iz*resolution+ix+1],tx),
                    Mathf.Lerp(heights[(iz+1)*resolution+ix],heights[(iz+1)*resolution+ix+1],tx),tz);
            }
            public float[] CopyPatch(Rect rect,out LTHeightJobMath.Source layout)
            {
                int Index(float p,float extent)=>Mathf.Min(Mathf.FloorToInt(Mathf.Clamp01(p/extent)*(resolution-1)),resolution-2);
                int x0=Index(rect.xMin,size.x),z0=Index(rect.yMin,size.z),x1=Index(rect.xMax,size.x)+1,z1=Index(rect.yMax,size.z)+1;
                int width=x1-x0+1,depth=z1-z0+1;var copy=new float[checked(width*depth)];
                for(int z=0;z<depth;z++)Array.Copy(heights,(z0+z)*resolution+x0,copy,z*width,width);
                layout=new LTHeightJobMath.Source{resolution=resolution,size=size,x=x0,z=z0,width=width};return copy;
            }
        }

        internal sealed class Cache
        {
            // Main-thread cache only; workers receive independent numeric arrays.
            // Call Invalidate before binding changed chunk inputs.
            // 3 MiB of raw key/value payload at the default limit, plus bounded
            // Dictionary/entry overhead. This is not an unbounded world grid.
            // Let one dense edited chunk borrow the shared budget instead of
            // falling back at half capacity while other chunks have free space.
            public const int DefaultSamples=262144,DefaultChunks=16,DefaultChunkSamples=DefaultSamples;
            readonly int maxSamples,maxChunks,maxChunkSamples;
            readonly Dictionary<int,long> versions=new Dictionary<int,long>();
            readonly Dictionary<int,Entry> entries=new Dictionary<int,Entry>();
            readonly LinkedList<int> recent=new LinkedList<int>();
            long epoch;
            public int Count {get;private set;}
            public int ChunkCount=>entries.Count;
            public int Limit=>maxSamples;
            public long Hits {get;private set;}
            public long Evaluations {get;private set;}
            public long Bypassed {get;private set;}
            public long Evictions {get;private set;}
            public long FromJobs {get;private set;}
            sealed class Entry
            {
                public readonly Dictionary<Point,float> samples=new Dictionary<Point,float>();
                public LinkedListNode<int> recent;
            }
            internal readonly struct Point : IEquatable<Point>
            {
                readonly int x,z;
                public Point(float x,float z){this.x=BitConverter.SingleToInt32Bits(x);this.z=BitConverter.SingleToInt32Bits(z);}
                public bool Equals(Point p)=>x==p.x&&z==p.z;
                public override bool Equals(object obj)=>obj is Point p&&Equals(p);
                public override int GetHashCode()
                {
                    // Float grid coordinates share many bits. Mix both axes instead
                    // of using an XOR hash that collapses aligned terrain grids.
                    unchecked
                    {
                        uint h=(uint)x*0x9e3779b1u+(uint)z*0x85ebca77u;
                        h^=h>>16;h*=0x7feb352du;h^=h>>15;h*=0x846ca68bu;return (int)(h^(h>>16));
                    }
                }
            }
            // A binding owns an evaluator, not the cache. Eviction cannot retain
            // the owning rebuild's forest/meshes or leave orphan sample dictionaries.
            internal sealed class Sampler
            {
                readonly Cache owner;readonly int id;readonly long epoch,version;
                readonly Func<float,float,float> evaluate;
                public Sampler(Cache owner,int id,Func<float,float,float> evaluate)
                {this.owner=owner;this.id=id;this.evaluate=evaluate;epoch=owner.epoch;version=owner.Version(id);}
                public float Sample(float x,float z)=>owner.Sample(id,epoch,version,x,z,evaluate);
                public bool Contains(float x,float z)
                {owner.RequireCurrent(id,epoch,version);return owner.entries.TryGetValue(id,out var e)&&e.samples.ContainsKey(new Point(x,z));}
                public int Remaining
                {get{owner.RequireCurrent(id,epoch,version);return owner.maxChunkSamples-(owner.entries.TryGetValue(id,out var e)?e.samples.Count:0);}}
                public void Accept(float x,float z,float value)
                {owner.RequireCurrent(id,epoch,version);owner.Retain(id,new Point(x,z),x,z,value);owner.FromJobs++;}
            }
            public Cache(int maxSamples=DefaultSamples,int maxChunks=DefaultChunks,int maxChunkSamples=DefaultChunkSamples)
            {
                if(maxSamples<1||maxChunks<1||maxChunkSamples<1)throw new ArgumentOutOfRangeException("Height cache limits must be positive.");
                this.maxSamples=maxSamples;this.maxChunks=maxChunks;this.maxChunkSamples=Math.Min(maxSamples,maxChunkSamples);
            }
            long Version(int id)=>versions.TryGetValue(id,out long value)?value:0;
            public Sampler Bind(int id,Func<float,float,float> evaluate)
            {if(evaluate==null)throw new ArgumentNullException(nameof(evaluate));return new Sampler(this,id,evaluate);}
            public void ResetStatistics(){Hits=Evaluations=Bypassed=Evictions=FromJobs=0;}
            public void Invalidate(int id)
            {versions[id]=Version(id)+1;Remove(id);}
            public void Clear()
            {epoch++;entries.Clear();recent.Clear();versions.Clear();Count=0;}
            void RequireCurrent(int id,long inputEpoch,long version)
            {
                if(inputEpoch!=epoch||version!=Version(id))
                    throw new InvalidOperationException("Stale terrain height snapshot. Discard the superseded build before resuming it.");
            }
            void Remove(int id)
            {
                if(!entries.TryGetValue(id,out var entry))return;
                Count-=entry.samples.Count;recent.Remove(entry.recent);entries.Remove(id);
            }
            void Evict(int id){Remove(id);Evictions++;}
            float Sample(int id,long inputEpoch,long version,float x,float z,Func<float,float,float> evaluate)
            {
                RequireCurrent(id,inputEpoch,version);
                var key=new Point(x,z);
                if(entries.TryGetValue(id,out var entry))
                {
                    if(recent.First!=entry.recent){recent.Remove(entry.recent);recent.AddFirst(entry.recent);}
                    if(entry.samples.TryGetValue(key,out float cached)){Hits++;return cached;}
                }
                Evaluations++;float value=evaluate(x,z);
                RequireCurrent(id,inputEpoch,version);
                return Retain(id,key,x,z,value);
            }
            float Retain(int id,Point key,float x,float z,float value)
            {
                if(float.IsNaN(value)||float.IsInfinity(value)||float.IsNaN(x)||float.IsNaN(z)||float.IsInfinity(x)||float.IsInfinity(z))
                {Bypassed++;return value;} // retain the caller's original validation/error path
                // A callback may evict another binding's entry. Re-resolve it
                // after evaluation rather than writing into an orphan dictionary.
                if(!entries.TryGetValue(id,out var entry))
                {
                    while(entries.Count>=maxChunks)Evict(recent.Last.Value);
                    entry=new Entry{recent=recent.AddFirst(id)};entries.Add(id,entry);
                }
                else
                {
                    if(recent.First!=entry.recent){recent.Remove(entry.recent);recent.AddFirst(entry.recent);}
                    if(entry.samples.ContainsKey(key))return value;
                }
                if(entry.samples.Count>=maxChunkSamples){Bypassed++;return value;}
                while(Count>=maxSamples)
                {
                    if(recent.Last.Value==id){Bypassed++;return value;}
                    Evict(recent.Last.Value);
                }
                entry.samples.Add(key,value);Count++;return value;
            }
        }
    }
}
