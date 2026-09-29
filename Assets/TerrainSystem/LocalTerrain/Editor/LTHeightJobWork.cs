using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
namespace LocalTerrainPrototype
{
    internal sealed class LTHeightJobWork:IDisposable
    {
        internal struct NativeBuffer<T>:ILTHeightBuffer<T> where T:struct
        {public NativeArray<T> values;public T this[int index]=>values[index];}
        // Closest-segment selection is sensitive to arithmetic rounding.
        [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High)]
        internal struct SampleJob:IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> source;
            public LTHeightJobMath.Source layout;
            [ReadOnly] public NativeArray<LTRoadHeightKernel.Road> roads;
            [ReadOnly] public NativeArray<LTRoadMath.Sample> samples;
            [ReadOnly] public NativeArray<LTRoadHeightKernel.Node> nodes;
            [ReadOnly] public NativeArray<LTHeightJobMath.Point> points;
            [ReadOnly] public NativeArray<LTHeightJobMath.Command> commands;
            [WriteOnly] public NativeArray<float> heights;
            [WriteOnly] public NativeArray<int> status;
            [BurstDiscard] static void MarkManaged(ref int value){value=2;}
            public void Execute(int index)
            {
                var p=points[index];var pos=p.position;
                var r=new NativeBuffer<LTRoadHeightKernel.Road>{values=roads};
                var s=new NativeBuffer<LTRoadMath.Sample>{values=samples};var n=new NativeBuffer<LTRoadHeightKernel.Node>{values=nodes};
                float h=layout.Sample(new NativeBuffer<float>{values=source},pos.x,pos.y);
                bool needsReference=false;
                for(int c=p.first;c<p.first+p.count;c++)
                {h=LTHeightJobMath.Apply(h,pos,commands[c],r,s,n,out bool ambiguous);needsReference|=ambiguous;}
                heights[index]=h;int result=1;MarkManaged(ref result);
                status[index]=float.IsNaN(h)||float.IsInfinity(h)?-1:result|(needsReference?4:0);
            }
        }
        internal sealed class Input
        {
            public float[] source;public LTHeightJobMath.Source layout;
            public LTRoadHeightKernel.Road[] roads;public LTRoadMath.Sample[] samples;public LTRoadHeightKernel.Node[] nodes;
        }
        static readonly List<LTHeightJobWork> live=new List<LTHeightJobWork>();
        internal const int Capacity=2,MaxBatch=32768;
        public static bool CanSchedule=>live.Count<Capacity;
        NativeArray<float> source,heights;
        NativeArray<LTRoadHeightKernel.Road> roads;NativeArray<LTRoadMath.Sample> samples;NativeArray<LTRoadHeightKernel.Node> nodes;
        NativeArray<LTHeightJobMath.Point> points;NativeArray<LTHeightJobMath.Command> commands;NativeArray<int> status;
        JobHandle handle;bool scheduled,completed,retired,released,validated;
        public bool IsCompleted=>completed||handle.IsCompleted;
        public bool UsedBurst{get{RequireResult();return status.Length>0&&(status[0]&3)==1;}}
        public LTHeightJobWork(Input input,List<LTHeightJobMath.Point> requests,List<LTHeightJobMath.Command> operations)
        {
            if(!CanSchedule||requests.Count>MaxBatch)throw new InvalidOperationException("Height job capacity exceeded.");
            try
            {
                source=new NativeArray<float>(input.source,Allocator.Persistent);roads=new NativeArray<LTRoadHeightKernel.Road>(input.roads,Allocator.Persistent);
                samples=new NativeArray<LTRoadMath.Sample>(input.samples,Allocator.Persistent);nodes=new NativeArray<LTRoadHeightKernel.Node>(input.nodes,Allocator.Persistent);
                points=new NativeArray<LTHeightJobMath.Point>(requests.ToArray(),Allocator.Persistent);
                commands=new NativeArray<LTHeightJobMath.Command>(operations.ToArray(),Allocator.Persistent);
                heights=new NativeArray<float>(requests.Count,Allocator.Persistent);status=new NativeArray<int>(requests.Count,Allocator.Persistent);
                handle=new SampleJob{source=source,layout=input.layout,roads=roads,samples=samples,nodes=nodes,points=points,commands=commands,heights=heights,status=status}.Schedule(requests.Count,64);
                scheduled=true;live.Add(this);JobHandle.ScheduleBatchedJobs();
            }
            catch{Release();throw;}
        }
        void Complete(){if(scheduled&&!completed){handle.Complete();completed=true;}}
        void RequireResult()
        {
            if(retired||released)throw new ObjectDisposedException(nameof(LTHeightJobWork));
            if(!IsCompleted)throw new InvalidOperationException("Height job is still running.");
            Complete();if(validated)return;
            for(int i=0;i<status.Length;i++)if((status[i]&~7)!=0||((status[i]&3)!=1&&(status[i]&3)!=2))
                throw new InvalidOperationException("Height job failed or returned a non-finite height. Previous meshes preserved; check Console.");
            validated=true;
        }
        public float Read(int index){RequireResult();return heights[index];}
        public bool NeedsReference(int index){RequireResult();return (status[index]&4)!=0;}
        void Release()
        {
            if(released)return;Complete();
            if(source.IsCreated)source.Dispose();if(roads.IsCreated)roads.Dispose();if(samples.IsCreated)samples.Dispose();if(nodes.IsCreated)nodes.Dispose();
            if(points.IsCreated)points.Dispose();if(commands.IsCreated)commands.Dispose();if(heights.IsCreated)heights.Dispose();if(status.IsCreated)status.Dispose();
            released=true;live.Remove(this);
        }
        public void Dispose(){if(retired||released)return;retired=true;if(IsCompleted)Release();}
        public static void CollectRetired(){for(int i=live.Count-1;i>=0;i--)if(live[i].retired&&live[i].IsCompleted)live[i].Release();}
        public static void Drain(){while(live.Count>0)live[live.Count-1].Release();}
    }
}
