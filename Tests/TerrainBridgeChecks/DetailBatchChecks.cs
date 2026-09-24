using System;
using System.Collections.Generic;
using System.IO;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void DetailBatchChecks()
    {
        var queue=new LTDetailBatchQueue<int>();
        var seen=new HashSet<int>();int calls=0,total=0;
        void Emit(LTDetailBatchQueue<int>.Batch batch)
        {
            Require(batch.count>0&&batch.count<=511,"valid instanced submission count");
            calls++;
            for(int i=0;i<batch.count;i++)
            {
                int id=(int)batch.matrices[i].m03;
                Require(seen.Add(id),"merged instance submitted exactly once");
                Require(id%4==batch.key,"instance stays in its compatible draw partition");
                var min=batch.bounds.min;var max=batch.bounds.max;
                Require(min.x<=id-.5f&&max.x>=id+.5f,"batch bounds cover every instance including extents");
                total++;
            }
        }
        // 80 former groups with 100 instances each; 4 compatible states. No density reduction.
        for(int group=0;group<80;group++)for(int i=0;i<100;i++)
        {
            int id=group*100+i;
            queue.Add(id%4,new Matrix4x4{m03=id},new Bounds(new Vector3(id,0,0),Vector3.one),Emit);
        }
        queue.Flush(Emit);
        Require(total==8000&&calls==16,"compatible synthetic batches reduced without dropping instances");
        Require(queue.BufferCount==4,"reuse one buffer per active draw state");
        queue.Flush(Emit);
        Require(calls==16,"flushing an empty camera never resubmits old instances");
        int priorBuffers=queue.BufferCount;
        queue.Add(0,new Matrix4x4{m03=9000},new Bounds(new Vector3(9000,0,0),Vector3.one),Emit);
        queue.Begin(); // Aborted camera: no pending contents may reach the next one.
        queue.Add(1,new Matrix4x4{m03=9001},new Bounds(new Vector3(9001,0,0),Vector3.one),batch=>
        {Require(false,"one pending instance is not emitted early");});
        queue.Flush(batch=>
        {
            Require(batch.count==1&&batch.key==1&&batch.matrices[0].m03==9001,"camera reset drops previous pending data");
            Require(batch.bounds.size.x==1&&batch.bounds.center.x==9001,"reused buffer resets bounds");
        });
        Require(queue.BufferCount==priorBuffers,"camera switches reuse buffers");
        queue.Clear();Require(queue.BufferCount==0,"disable releases staging buffers");

        // Memory pressure: more distinct keys than available buffers forces a safe partial flush.
        var tiny=new LTDetailBatchQueue<int>(7,3);
        var random=new System.Random(1729);seen.Clear();total=0;
        for(int id=0;id<3000;id++)
        {
            int key=random.Next(12);
            tiny.Add(key,new Matrix4x4{m03=id,m13=key},new Bounds(new Vector3(id,0,0),Vector3.one),CheckTiny);
            Require(tiny.BufferCount<=3,"draw state churn stays within staging memory limit");
        }
        tiny.Flush(CheckTiny);
        Require(total==3000,"buffer pressure does not lose instances");
        void CheckTiny(LTDetailBatchQueue<int>.Batch batch)
        {
            Require(batch.count>0&&batch.count<=7,"partial flush honours configured capacity");
            for(int i=0;i<batch.count;i++)
            {
                Require(seen.Add((int)batch.matrices[i].m03),"pressure flush never duplicates instances");
                Require(batch.matrices[i].m13==batch.key,"pressure flush never mixes draw state");total++;
            }
        }

        var state=new LTDetailDrawState();
        Require(state.Equals(default(LTDetailDrawState))&&state.GetHashCode()==default(LTDetailDrawState).GetHashCode(),"equal batch keys hash equally");
        foreach(var field in typeof(LTDetailDrawState).GetFields())
        {
            object different=state;
            if(field.FieldType==typeof(bool))field.SetValue(different,true);
            else if(field.FieldType==typeof(uint))field.SetValue(different,1u);
            else field.SetValue(different,1);
            Require(!state.Equals((LTDetailDrawState)different),"batch key isolates "+field.Name);
        }
        // A pooled key must not keep an unloaded mesh/material recipe alive after flushing.
        var referenceQueue=new LTDetailBatchQueue<ReferenceKey>(3,2);
        LTDetailBatchQueue<ReferenceKey>.Batch retained=null;
        referenceQueue.Add(new ReferenceKey{value=new object()},default,default,b=>retained=b);
        referenceQueue.Flush(b=>retained=b);
        Require(retained!=null&&retained.key.value==null&&retained.count==0,"pooled buffers release key object references");

        var renderer=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTDetailRenderer.cs");
        Require(System.Runtime.InteropServices.Marshal.SizeOf<LTDetailMotionData>()==128&&
            System.Runtime.InteropServices.Marshal.OffsetOf<LTDetailMotionData>("prevObjectToWorld").ToInt32()==64,
            "motion instance ABI: two contiguous matrices with Unity field names");
        var sourceMatrices=new Matrix4x4[511];
        var motionData=new LTDetailMotionData[511];
        foreach(int count in new[]{511,1,64,0,237,511})
        {
            for(int i=0;i<sourceMatrices.Length;i++)
                for(int element=0;element<16;element++)sourceMatrices[i][element]=(float)(random.NextDouble()*200-100);
            // Fill a reused scratch buffer in a different order (other camera/LOD/batch).
            LTDetailMotionData.FillStatic(sourceMatrices,motionData,count);
            for(int i=0;i<count;i++)for(int element=0;element<16;element++)
                Require(motionData[i].objectToWorld[element]==sourceMatrices[i][element]&&
                    motionData[i].prevObjectToWorld[element]==sourceMatrices[i][element],
                    "static history matches current instance, never previous batch slot");
        }
        bool rejected=false;
        try{LTDetailMotionData.FillStatic(sourceMatrices,new LTDetailMotionData[1],2);}
        catch(ArgumentOutOfRangeException){rejected=true;}
        Require(rejected,"motion conversion refuses undersized buffer");
        Require(renderer.Contains("DrawInstances(parameters,part,drawMatrices,count)")&&
            renderer.Contains("DrawInstances(parameters,part,batch.matrices,batch.count)")&&
            !renderer.Contains("diagnosticStaticMotionMatrices")&&
            renderer.Contains("LTDetailMotionData.FillStatic(matrices,motionData,count)"),
            "both render paths always supply explicit static motion history");
        string drawMethod=renderer.Substring(renderer.IndexOf("void DrawInstances("));
        Require(!drawMethod.Contains("SetShaderPassEnabled")&&!drawMethod.Contains("EnableKeyword")&&
            !drawMethod.Contains("motionVectorMode="),"static motion history does not change material passes or force motion mode");
        Require(drawMethod.Contains("Graphics.RenderMeshInstanced(parameters,part.mesh,part.submesh,motionData,count)")&&
            !drawMethod.Contains("part.submesh,matrices,count"),"no matrix-only fallback can reintroduce false motion");
        Console.WriteLine("PASS static motion data: exact matrix layout, 511/partial/reordered batches, mandatory shared draw path. CPU/source checks; not a GPU test.");
        Require(renderer.Contains("combineDrawBatches&&part.material.renderQueue<(int)RenderQueue.Transparent"),"transparent materials retain reference path");
        Require(renderer.Contains("regionX=cellKey.x>>1,regionZ=cellKey.y>>1"),"merge regions are 2x2 even for negative coordinates");
        Require(renderer.Contains("cachedMatrixBytes-(cacheResident?.cachedMatrixBytes??0)+cell.cachedMatrixBytes+bytes"),"replacement cache respects resident byte budget");
        Require(renderer.Contains("cachedMatrixBytes-=cell.cachedMatrixBytes"),"unloading returns matrix cache budget");
        Require(renderer.Contains("cachedMatrixBytes+=cell.cachedMatrixBytes-(old?.cachedMatrixBytes??0)"),"atomic cell replacement accounts cache once");
        Require(renderer.Contains("matrices!=null?matrices[selected.index]:instance.matrix*part.localMatrix"),"uncached groups keep compatible fallback");
        Console.WriteLine("PASS detail batch queue: synthetic 80 -> 16 submissions for identical 8000 instances; all draw-state partitions, capacity overflow, bounds, camera reset, pooled reference cleanup and cache budget source contracts. No Unity/GPU/FPS measurement.");
    }
    struct ReferenceKey:IEquatable<ReferenceKey>
    {
        public object value;
        public bool Equals(ReferenceKey b)=>ReferenceEquals(value,b.value);
        public override int GetHashCode()=>value?.GetHashCode()??0;
    }
}
