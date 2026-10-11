using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Persistent native buffers are immutable until the job has finished. Dispose
    // retires immediately; only completed jobs are collected during normal editing.
    public sealed class LTPaintWeightWork:IDisposable
    {
        struct Buffer<T>:ILTHeightBuffer<T> where T:struct
        {public NativeArray<T> values;public T this[int i]=>values[i];}
        struct TerrainAccess:ILTPaintTerrainData
        {
            [ReadOnly] public NativeArray<LTPaintWeightMath.Tile> tiles;
            [ReadOnly] public NativeArray<Vector3> vertices,normals;
            [ReadOnly] public NativeArray<int> indices,triangles;
            [ReadOnly] public NativeArray<Vector2Int> bins;
            public LTPaintWeightMath.Tile Tile(int i)=>tiles[i];
            public Vector3 Vertex(int i)=>vertices[i];public Vector3 Normal(int i)=>normals[i];
            public int Index(int i)=>indices[i];public Vector2Int Bin(int i)=>bins[i];public int Triangle(int i)=>triangles[i];
        }
        [BurstCompile(FloatMode=FloatMode.Strict,FloatPrecision=FloatPrecision.High)]
        struct PaintJob:IJobParallelFor
        {
            [ReadOnly] public NativeArray<LTPaintWeightMath.Stamp> stamps;
            [ReadOnly] public NativeArray<LTPaintWeightMath.RoadHit> hits;
            [ReadOnly] public NativeArray<float> masks;
            [ReadOnly] public NativeArray<Vector2> coordinates;
            public TerrainAccess data;
            public LTPaintWeightMath.Terrain terrain;
            [WriteOnly] public NativeArray<LTPaintWeightMath.Pixel> pixels;
            [WriteOnly] public NativeArray<byte> backend;
            public Rect rect;public int size;
            [BurstDiscard] static void Managed(ref byte b){b=2;}
            public void Execute(int i)
            {
                byte b=1;Managed(ref b);backend[i]=b;
                pixels[i]=LTPaintWeightMath.Evaluate(i,stamps.Length,new Buffer<LTPaintWeightMath.Stamp>{values=stamps},
                    new Buffer<LTPaintWeightMath.RoadHit>{values=hits},new Buffer<float>{values=masks},new Buffer<Vector2>{values=coordinates},data,terrain);
            }
        }
        static readonly List<LTPaintWeightWork> live=new List<LTPaintWeightWork>();
        public const int Capacity=2;
        public static bool CanSchedule=>live.Count<Capacity;
        NativeArray<LTPaintWeightMath.Stamp> stamps;
        NativeArray<LTPaintWeightMath.RoadHit> hits;
        NativeArray<float> masks;
        NativeArray<Vector2> coordinates;
        NativeArray<LTPaintWeightMath.Pixel> pixels;
        NativeArray<byte> backend;
        TerrainAccess data;
        JobHandle handle;bool scheduled,completed,retired,released;
        public bool IsValid=>!retired&&!released;
        public bool IsCompleted=>completed||handle.IsCompleted;
        public LTPaintWeightWork(LTPaintWeightInput input,LTPaintCpuCapture capture=null)
        {
            if(!CanSchedule)throw new InvalidOperationException("Paint weight job capacity exceeded.");
            if(input.size<2||input.size>257||input.stamps.Length>64||
                (long)input.hits.Length*16+(long)input.masks.Length*4+(long)input.coordinates.Length*8>16L*1024*1024)
                throw new ArgumentException("Paint weight input exceeds its bounded layout.");
            var terrainInput=input.terrain;
            long terrainBytes=(long)terrainInput.tiles.Length*64+(long)terrainInput.vertices.Length*12+(long)terrainInput.normals.Length*12+
                (long)terrainInput.indices.Length*4+(long)terrainInput.triangles.Length*4+(long)terrainInput.bins.Length*8;
            if(terrainBytes>16L*1024*1024)throw new ArgumentException("Paint terrain snapshot exceeds its bounded layout.");
            try
            {
                var t=input.terrain;
                using(capture?.Measure(LTPaintCpuCapture.Stage.WeightNativeCopy)??default)
                {
                    stamps=new NativeArray<LTPaintWeightMath.Stamp>(input.stamps,Allocator.Persistent);
                    hits=new NativeArray<LTPaintWeightMath.RoadHit>(input.hits,Allocator.Persistent);
                    masks=new NativeArray<float>(input.masks,Allocator.Persistent);
                    coordinates=new NativeArray<Vector2>(input.coordinates,Allocator.Persistent);
                    data.tiles=new NativeArray<LTPaintWeightMath.Tile>(t.tiles,Allocator.Persistent);
                    data.vertices=new NativeArray<Vector3>(t.vertices,Allocator.Persistent);
                    data.normals=new NativeArray<Vector3>(t.normals,Allocator.Persistent);
                    data.indices=new NativeArray<int>(t.indices,Allocator.Persistent);
                    data.triangles=new NativeArray<int>(t.triangles,Allocator.Persistent);
                    data.bins=new NativeArray<Vector2Int>(t.bins,Allocator.Persistent);
                    pixels=new NativeArray<LTPaintWeightMath.Pixel>(input.size*input.size,Allocator.Persistent);
                    backend=new NativeArray<byte>(pixels.Length,Allocator.Persistent);
                }
                using(capture?.Measure(LTPaintCpuCapture.Stage.WeightSchedule)??default)
                {
                    handle=new PaintJob{stamps=stamps,hits=hits,masks=masks,coordinates=coordinates,pixels=pixels,backend=backend,data=data,terrain=t.terrain,rect=input.rect,size=input.size}.Schedule(pixels.Length,64);
                    scheduled=true;live.Add(this);JobHandle.ScheduleBatchedJobs();
                }
            }
            catch{Release();throw;}
        }
        public LTPaintWeightMath.Pixel[] Result(out bool burst)
        {
            if(retired||released)throw new ObjectDisposedException(nameof(LTPaintWeightWork));
            if(!IsCompleted)throw new InvalidOperationException("Paint weight job is still running.");
            Complete();burst=backend.Length>0&&backend[0]==1;
            for(int i=0;i<backend.Length;i++)if(backend[i]!=1&&backend[i]!=2)throw new InvalidOperationException("Invalid paint weight job result.");
            return pixels.ToArray();
        }
        void Complete(){if(scheduled&&!completed){handle.Complete();completed=true;}}
        void Release()
        {
            if(released)return;Complete();
            if(stamps.IsCreated)stamps.Dispose();if(hits.IsCreated)hits.Dispose();if(masks.IsCreated)masks.Dispose();
            if(coordinates.IsCreated)coordinates.Dispose();
            if(pixels.IsCreated)pixels.Dispose();if(backend.IsCreated)backend.Dispose();
            if(data.tiles.IsCreated)data.tiles.Dispose();if(data.vertices.IsCreated)data.vertices.Dispose();if(data.normals.IsCreated)data.normals.Dispose();
            if(data.indices.IsCreated)data.indices.Dispose();if(data.triangles.IsCreated)data.triangles.Dispose();if(data.bins.IsCreated)data.bins.Dispose();
            released=true;live.Remove(this);
        }
        public void Dispose(){if(retired||released)return;retired=true;if(IsCompleted)Release();}
        public static void CollectRetired(){for(int i=live.Count-1;i>=0;i--)if(live[i].retired&&live[i].IsCompleted)live[i].Release();}
        public static void Drain(){while(live.Count>0){live[0].retired=true;live[0].Release();}}
    }
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
    static class LTPaintWeightLifetime
    {
        static LTPaintWeightLifetime()
        {
            UnityEditor.EditorApplication.update+=LTPaintWeightWork.CollectRetired;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload+=LTPaintWeightWork.Drain;
            UnityEditor.EditorApplication.quitting+=LTPaintWeightWork.Drain;
            UnityEditor.EditorApplication.playModeStateChanged+=s=>{if(s==UnityEditor.PlayModeStateChange.ExitingEditMode)LTPaintWeightWork.Drain();};
        }
    }
#endif
}
