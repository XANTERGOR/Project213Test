using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Owned numeric buffers. Ordinary polling NEVER completes unfinished work.
    // Retirement invalidates access immediately; collection waits for completion.
    public sealed class LTRoadRasterWork:IDisposable
    {
        struct Buffer<T>:ILTHeightBuffer<T> where T:struct
        {public NativeArray<T> values;public T this[int index]=>values[index];}
        [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High)]
        struct SampleJob:IJobParallelFor
        {
            [ReadOnly] public NativeArray<LTRoadHeightKernel.Road> roads;
            [ReadOnly] public NativeArray<LTRoadMath.Sample> samples;
            [ReadOnly] public NativeArray<LTRoadHeightKernel.Node> nodes;
            [ReadOnly] public NativeArray<byte> needed;
            [WriteOnly] public NativeArray<int> segments;
            [WriteOnly] public NativeArray<byte> backend;
            public Rect rect;public int size;
            [BurstDiscard] static void Managed(ref byte value){value=2;}
            public void Execute(int index)
            {
                byte mode=1;Managed(ref mode);backend[index]=mode;
                if(needed[index]==0){segments[index]=-1;return;}
                int pixel=index%(size*size),road=index/(size*size);
                float x=rect.xMin+rect.width*(pixel%size)/(size-1),z=rect.yMin+rect.height*(pixel/size)/(size-1);
                segments[index]=LTRoadRasterKernel.Segment(roads[road],new Buffer<LTRoadMath.Sample>{values=samples},
                    new Buffer<LTRoadHeightKernel.Node>{values=nodes},x,z);
            }
        }
        static readonly List<LTRoadRasterWork> live=new List<LTRoadRasterWork>();
        public const int Capacity=2;
        public static bool CanSchedule=>live.Count<Capacity;
        readonly LTRoadMath.Snapshot[] sources;
        readonly Rect rect;readonly int size;
        NativeArray<LTRoadHeightKernel.Road> roads;
        NativeArray<LTRoadMath.Sample> samples;
        NativeArray<LTRoadHeightKernel.Node> nodes;
        NativeArray<byte> needed,backend;
        NativeArray<int> segments;
        JobHandle handle;bool scheduled,completed,retired,released;
        public bool IsCompleted=>completed||handle.IsCompleted;
        public bool Matches(Rect area,IReadOnlyList<LTRoadMath.Snapshot> inputs)
        {
            if(retired||released||rect!=area||sources.Length!=inputs.Count)return false;
            for(int i=0;i<inputs.Count;i++)if(!ReferenceEquals(sources[i],inputs[i]))return false;
            return true;
        }
        public LTRoadRasterWork(LTRoadRasterInput input)
        {
            if(!CanSchedule)throw new InvalidOperationException("Road sampling job capacity exceeded.");
            sources=input.sources;rect=input.rect;size=input.size;
            try
            {
                roads=new NativeArray<LTRoadHeightKernel.Road>(input.roads,Allocator.Persistent);
                samples=new NativeArray<LTRoadMath.Sample>(input.samples,Allocator.Persistent);
                nodes=new NativeArray<LTRoadHeightKernel.Node>(input.nodes,Allocator.Persistent);
                needed=new NativeArray<byte>(input.needed,Allocator.Persistent);
                segments=new NativeArray<int>(input.needed.Length,Allocator.Persistent);
                backend=new NativeArray<byte>(input.needed.Length,Allocator.Persistent);
                handle=new SampleJob{roads=roads,samples=samples,nodes=nodes,needed=needed,segments=segments,backend=backend,rect=rect,size=size}
                    .Schedule(segments.Length,64);
                scheduled=true;live.Add(this);JobHandle.ScheduleBatchedJobs();
            }
            catch{Release();throw;}
        }
        public LTRoadRasterSet Result(out bool usedBurst)
        {
            if(retired||released)throw new ObjectDisposedException(nameof(LTRoadRasterWork));
            if(!IsCompleted)throw new InvalidOperationException("Road sampling is still running.");
            Complete();usedBurst=backend.Length>0&&backend[0]==1;
            for(int i=0;i<segments.Length;i++)
                if(backend[i]!=1&&backend[i]!=2||segments[i]<-2||segments[i]>=sources[i/(size*size)].samples.Count-1)
                    throw new InvalidOperationException("Invalid road sampling result; no worker output was accepted.");
            return new LTRoadRasterSet(sources,rect,size,segments.ToArray());
        }
        void Complete(){if(scheduled&&!completed){handle.Complete();completed=true;}}
        void Release()
        {
            if(released)return;Complete();
            if(roads.IsCreated)roads.Dispose();if(samples.IsCreated)samples.Dispose();if(nodes.IsCreated)nodes.Dispose();
            if(needed.IsCreated)needed.Dispose();if(segments.IsCreated)segments.Dispose();if(backend.IsCreated)backend.Dispose();
            released=true;live.Remove(this);
        }
        public void Dispose(){if(retired||released)return;retired=true;if(IsCompleted)Release();}
        public static void CollectRetired(){for(int i=live.Count-1;i>=0;i--)if(live[i].retired&&live[i].IsCompleted)live[i].Release();}
        // Only reload/shutdown drains may wait for jobs; never a scene edit.
        public static void Drain(){while(live.Count>0){live[0].retired=true;live[0].Release();}}
    }
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
    static class LTRoadRasterLifetime
    {
        static LTRoadRasterLifetime()
        {
            UnityEditor.EditorApplication.update+=LTRoadRasterWork.CollectRetired;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload+=LTRoadRasterWork.Drain;
            UnityEditor.EditorApplication.quitting+=LTRoadRasterWork.Drain;
            UnityEditor.EditorApplication.playModeStateChanged+=state=>
            {if(state==UnityEditor.PlayModeStateChange.ExitingEditMode)LTRoadRasterWork.Drain();};
        }
    }
#endif
}
