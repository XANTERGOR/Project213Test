using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void EmitDiagnosticsChecks()
    {
        var rect=new Rect(-13.375f,23.125f,31.75f,47.875f);int comparisons=0;
        void Same(EmittedStamp expected,EmittedStamp actual)
        {
            IEnumerable<int> Bits3(Vector3[] values)=>values.SelectMany(v=>new[]{BitConverter.SingleToInt32Bits(v.x),BitConverter.SingleToInt32Bits(v.y),BitConverter.SingleToInt32Bits(v.z)});
            IEnumerable<int> Bits2(Vector2[] values)=>values.SelectMany(v=>new[]{BitConverter.SingleToInt32Bits(v.x),BitConverter.SingleToInt32Bits(v.y)});
            Require(Bits3(expected.positions).SequenceEqual(Bits3(actual.positions))&&Bits3(expected.normals).SequenceEqual(Bits3(actual.normals)),"diagnostics exact positions/normals");
            Require(Bits2(expected.uv).SequenceEqual(Bits2(actual.uv))&&expected.indices.SequenceEqual(actual.indices),"diagnostics exact UV/indices");
            Require(expected.heightCalls.SequenceEqual(actual.heightCalls)&&expected.cutCalls.SequenceEqual(actual.cutCalls),"diagnostics do not change callback coordinates/order");comparisons++;
        }
        void Consistent(LTStampMesh.EmitDiagnostics d,EmittedStamp mesh,int cells)
        {
            Require(d.Completed&&d.Cells==cells&&d.SampledCells>0&&d.SampledCells<=cells,"diagnostics completed cell counts");
            Require(d.OutputVertices==mesh.positions.Length&&d.OutputIndices==mesh.indices.Length&&d.VerticesBeforeCompact>=d.OutputVertices,"diagnostics mesh counts");
            Require(d.VertexRequests>=d.VertexHits&&d.HeightRequests>=d.HeightHits&&d.IntersectionRequests>=d.IntersectionHits,"diagnostics cache hit bounds");
            long sum=0;foreach(LTStampMesh.EmitDiagnostics.Part part in Enum.GetValues(typeof(LTStampMesh.EmitDiagnostics.Part)))
            {long ticks=d.SampledTicks(part);Require(ticks>=0,"diagnostics nonnegative sampled stage");sum+=ticks;}
            Require(sum==d.SampleTicks&&sum<=d.CellTicks,"exclusive sampled scopes account once, including nested intersections/heights");
            Require(d.SetupTicks+d.CellTicks+d.CompactTicks+d.ArrayTicks<=d.TotalTicks,"exact phases are inside the total emitter time");
            Require(d.AllocatedBytes>0&&d.Gen0>=0&&d.Gen1>=0&&d.Gen2>=0,"allocation/collection counters");
        }
        var single=LODFixture(1);
        foreach(int stride in new[]{1,64})foreach(bool regular in new[]{false,true})for(int mask=0;mask<16;mask++)
        {
            int m=mask;bool Mid(Vector3Int p,int side)=>(m&(1<<side))!=0;
            bool Cut(float x,float z)=>x>2.331f&&z<60.787f;
            var d=new LTStampMesh.EmitDiagnostics(stride);
            var expected=EmitStamp(true,rect,single,null,0,Cut,r=>regular,Mid);
            var actual=EmitStamp(false,rect,single,null,0,Cut,r=>regular,Mid,diagnostics:d);
            Same(expected,actual);Consistent(d,actual,1);Require(d.SampledCells==1,"small mesh is fully sampled");
        }
        bool NoMid(Vector3Int p,int side)=>false;
        var shared=new LTStampMesh.EmitDiagnostics();var grid=LODFixture(64);
        var large=EmitStamp(false,rect,grid,null,0,(x,z)=>false,null,NoMid,diagnostics:shared);
        Consistent(shared,large,grid.Count);Require(shared.SampledCells==grid.Count/64,"one deterministic sample per complete block");
        Same(EmitStamp(false,rect,grid,null,0,(x,z)=>false,null,NoMid),large);
        var small=EmitStamp(false,rect,single,null,0,null,null,NoMid,diagnostics:shared);
        Consistent(shared,small,1);
        Require(shared.VertexRequests==4&&shared.VertexHits==0&&shared.HeightRequests==4&&shared.HeightHits==0&&shared.TriangleRequests==2&&shared.IntersectionRequests==0,"reused collector resets all operation counters");
        var report=shared.Report("(3,1)");
        Require(report.Contains("Emit exact:")&&report.Contains("Emit samples:")&&report.Contains("NOT whole-chunk estimates")&&report.Contains("process-wide counts, not pause time"),"report distinguishes exact/sample/GC data");
        Require(shared.AllocationAvailable&&shared.OutputPayloadBytes==small.positions.Length*32L+small.indices.Length*4L,"valid allocation and separate payload lower bound");
        long measured=shared.AllocatedBytes;
        foreach(long invalid in new[]{0L,-1L,shared.OutputPayloadBytes-1})
        {
            shared.AllocatedBytes=invalid;
            Require(!shared.AllocationAvailable&&shared.Report("invalid").Contains("unavailable")&&shared.Report("invalid").Contains("not total allocation"),"invalid allocation counter is never presented as measured zero or replaced by payload size");
        }
        shared.AllocatedBytes=measured;
        bool rejected=false;
        try{EmitStamp(false,rect,single,null,0,null,null,NoMid,budget:1,diagnostics:shared);}catch(InvalidOperationException){rejected=true;}
        Require(rejected&&!shared.Completed,"failed build cannot expose a completed measurement");
        EmitStamp(false,rect,single,null,0,null,null,NoMid,diagnostics:shared);Require(shared.Completed,"collector reusable after failure");
        double seconds=0;var cancel=new LTLODMesh.BuildProgress(()=>seconds+=2,()=>true);
        rejected=false;try{EmitStamp(false,rect,single,null,0,null,null,NoMid,progress:cancel,diagnostics:shared);}catch(OperationCanceledException){rejected=true;}
        Require(rejected&&!shared.Completed,"cancellation preserved with diagnostics");
        rejected=false;
        try
        {
            bool Nested(float x,float z){EmitStamp(false,rect,single,null,0,null,null,NoMid,diagnostics:shared);return false;}
            EmitStamp(false,rect,single,null,0,Nested,null,NoMid,diagnostics:shared);
        }
        catch(InvalidOperationException e){rejected=e.Message.Contains("simultaneous");}
        Require(rejected&&!shared.Completed,"reject recursive ownership of the same collector");
        var other=new LTStampMesh.EmitDiagnostics();bool ran=false;
        bool Independent(float x,float z)
        {if(!ran){ran=true;EmitStamp(false,rect,single,null,0,null,null,NoMid,diagnostics:other);}return false;}
        var nested=EmitStamp(false,rect,single,null,0,Independent,null,NoMid,diagnostics:shared);
        Require(other.Completed&&shared.Completed,"independent recursive diagnostics stay isolated");
        Same(EmitStamp(false,rect,single,null,0,(x,z)=>false,null,NoMid),nested);
        var empty=EmitStamp(false,rect,new List<Vector3Int>(),null,0,null,null,NoMid,diagnostics:shared);
        Require(shared.Completed&&shared.Cells==0&&shared.SampledCells==0&&shared.SampleTicks==0&&empty.indices.Length==0,"empty mesh resets sample data without division by zero");
        Require(!shared.Report("empty").Contains("NaN"),"empty diagnostic report is finite");
        var editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        Require(editor.Contains("diagnostics:emitDiagnostics")&&editor.Contains("terrainTimings.Append(emitDiagnostics.Report"),"LOD0 publishes detail for each rebuilt chunk");
        Console.WriteLine($"PASS emitter diagnostics: {comparisons} exact on/off/legacy meshes and callback sequences; nested exclusive timings, counts, sampling, reset, failure/cancel and ownership.");
        // Alternating warm runs; no threshold assertion on noisy wall time.
        var dense=LODFixture(128);var off=new List<double>();var on=new List<double>();
        EmittedStamp Run(bool enabled)=>EmitStamp(false,rect,dense,null,0,(x,z)=>false,null,NoMid,trace:false,diagnostics:enabled?new LTStampMesh.EmitDiagnostics():null);
        for(int i=0;i<4;i++){Run(false);Run(true);}
        for(int i=0;i<12;i++)for(int j=0;j<2;j++)
        {bool enabled=((i+j)&1)==0;var timer=Stopwatch.StartNew();Run(enabled);timer.Stop();(enabled?on:off).Add(timer.Elapsed.TotalMilliseconds);}
        off.Sort();on.Sort();Console.WriteLine($"Emitter diagnostics synthetic overhead: off {off[6]:F2} ms, sampled on {on[6]:F2} ms; {(on[6]-off[6]):F2} ms difference, no scene/native performance claim.");
    }
}
