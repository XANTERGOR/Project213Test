using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Owns all native data. The job never reads a scene, a Mesh, a delegate or an
    // editor cache. Allocator.Persistent is intentional: edit jobs may outlive
    // four frames, pause while dragging, or retire after an input revision.
    internal sealed class LTLODJobWork : IDisposable
    {
        [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.Standard)]
        internal struct BuildJob : IJob
        {
            [ReadOnly] public NativeArray<LTLODJobMath.Cell> cells;
            [ReadOnly] public NativeArray<LTLODJobMath.Level> levels;
            public NativeArray<LTLODJobMath.Metric> metrics;
            [WriteOnly] public NativeArray<byte> selected;
            [WriteOnly] public NativeArray<int> execution;
            [BurstDiscard] static void MarkManaged(ref int value){value=1;}
            public void Execute()
            {
                int managed=0;MarkManaged(ref managed);execution[0]=managed;
                for(int i=0;i<cells.Length;i++)
                {
                    var p=cells[i];
                    metrics[i]=p.leaf!=0?LTLODJobMath.Leaf(p):p.merge==0?default:
                        LTLODJobMath.Parent(p,cells[p.c0],metrics[p.c0],cells[p.c1],metrics[p.c1],
                            cells[p.c2],metrics[p.c2],cells[p.c3],metrics[p.c3]);
                }
                for(int level=0;level<levels.Length;level++)for(int i=0;i<cells.Length;i++)
                {
                    var p=cells[i];var setting=levels[level];
                    selected[level*cells.Length+i]=(byte)(LTLODJobMath.Eligible(p,metrics[i],setting)&&
                        (p.parent<0||!LTLODJobMath.Eligible(cells[p.parent],metrics[p.parent],setting))?1:0);
                }
                execution[1]=1;
            }
        }

        // Globally bounded across all worlds: one current + one retiring job can
        // coexist. Repeated edits cannot accumulate unbounded native snapshots.
        static readonly List<LTLODJobWork> live=new List<LTLODJobWork>();
        internal const int Capacity=2;
        public static int LiveCount=>live.Count;
        public static bool CanSchedule=>live.Count<Capacity;
        NativeArray<LTLODJobMath.Cell> cells;
        NativeArray<LTLODJobMath.Level> levels;
        NativeArray<LTLODJobMath.Metric> metrics;
        NativeArray<byte> selected;
        NativeArray<int> execution;
        JobHandle handle;
        bool scheduled,completed,retired,released;
        public bool IsCompleted=>completed||handle.IsCompleted;
        public bool UsedBurst
        {
            get
            {
                if(retired||released)throw new ObjectDisposedException(nameof(LTLODJobWork));
                if(!IsCompleted)throw new InvalidOperationException("LOD job is still running.");
                RequireResult();return execution[0]==0;
            }
        }

        public LTLODJobWork(LTLODJobInput input)
        {
            if(!CanSchedule)throw new InvalidOperationException("LOD job capacity exceeded.");
            if(input.Cells==null)throw new InvalidOperationException("LOD snapshot not ready.");
            try
            {
                cells=new NativeArray<LTLODJobMath.Cell>(input.Cells,Allocator.Persistent);
                levels=new NativeArray<LTLODJobMath.Level>(input.Levels,Allocator.Persistent);
                metrics=new NativeArray<LTLODJobMath.Metric>(cells.Length,Allocator.Persistent);
                selected=new NativeArray<byte>(checked(cells.Length*levels.Length),Allocator.Persistent);
                execution=new NativeArray<int>(2,Allocator.Persistent);
                handle=new BuildJob{cells=cells,levels=levels,metrics=metrics,selected=selected,execution=execution}.Schedule();
                scheduled=true;live.Add(this);JobHandle.ScheduleBatchedJobs();
            }
            catch{Release();throw;}
        }
        // Only called after IsCompleted. Completion must not pull expensive work
        // back onto the editor thread when it is still running on a worker.
        public List<Vector3Int> ReadLevel(int level)
        {
            if(retired||released)throw new ObjectDisposedException(nameof(LTLODJobWork));
            if(!IsCompleted)throw new InvalidOperationException("LOD job is still running.");
            RequireResult();
            if(level<0||level>=levels.Length)throw new ArgumentOutOfRangeException(nameof(level));
            var result=new List<Vector3Int>();int offset=level*cells.Length;
            for(int i=0;i<cells.Length;i++)if(selected[offset+i]!=0)
            {var p=cells[i];result.Add(new Vector3Int(p.x,p.z,p.size));}
            LTLODJobInput.SortPlan(result);return result;
        }
        void Complete(){if(scheduled&&!completed){handle.Complete();completed=true;}}
        void RequireResult()
        {
            Complete();
            if(execution[1]!=1)throw new InvalidOperationException("LOD job did not finish successfully. Previous meshes preserved; check the Console.");
        }
        void Release()
        {
            if(released)return;
            Complete();
            if(execution.IsCreated)execution.Dispose();
            if(selected.IsCreated)selected.Dispose();if(metrics.IsCreated)metrics.Dispose();
            if(levels.IsCreated)levels.Dispose();if(cells.IsCreated)cells.Dispose();
            released=true;live.Remove(this);
        }
        public void Dispose()
        {
            if(released||retired)return;
            retired=true;
            // Never wait on an obsolete job during a mouse edit or an Undo.
            if(IsCompleted)Release();
        }
        public static void CollectRetired()
        {
            for(int i=live.Count-1;i>=0;i--)
                if(live[i].retired&&live[i].IsCompleted)live[i].Release();
        }
        // The only blocking barrier: unloading the domain / quitting must not
        // leave native allocations or worker code referring to the old assembly.
        public static void Drain()
        {while(live.Count>0)live[live.Count-1].Release();}
    }
}
