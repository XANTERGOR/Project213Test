using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;

partial class Checks
{
    static void PaintCycleCaptureChecks()
    {
        var cycle=new LTPaintCpuCapture.Cycle();cycle.Reset(10);
        var prepare=new LTPaintCpuCapture.Sample{weightJobs=1};
        prepare.milliseconds[(int)LTPaintCpuCapture.Stage.Total]=210;
        prepare.milliseconds[(int)LTPaintCpuCapture.Stage.WeightPrepare]=200;
        var wait=new LTPaintCpuCapture.Sample{globalMaterialUpdates=13};
        wait.milliseconds[(int)LTPaintCpuCapture.Stage.Total]=15;
        wait.milliseconds[(int)LTPaintCpuCapture.Stage.GlobalMaterials]=13;
        var apply=new LTPaintCpuCapture.Sample{weightJobResults=1,weightBurstResults=1,weightJobPixels=66049,weightBakes=1,weightPixelFallbacks=4};
        apply.milliseconds[(int)LTPaintCpuCapture.Stage.Total]=20;
        apply.milliseconds[(int)LTPaintCpuCapture.Stage.WeightResolve]=.2;
        apply.milliseconds[(int)LTPaintCpuCapture.Stage.WeightBake]=.4;
        apply.milliseconds[(int)LTPaintCpuCapture.Stage.WeightCompute]=.3;
        apply.milliseconds[(int)LTPaintCpuCapture.Stage.WeightUpload]=.1;
        cycle.Record(11,prepare,211,true);cycle.Record(12,wait,16,true);cycle.Record(13,apply,21,false);
        Require(cycle.Polls==3&&cycle.MeasuredPolls==3&&cycle.WorkPolls==2&&cycle.WaitingOnlyPolls==1,"cycle distinguishes prepare, wait and apply, not three mask rebuilds");
        Require(cycle.Sum.weightBakes==1&&cycle.Sum.weightJobs==1&&cycle.Sum.weightJobResults==1&&cycle.Sum.weightBurstResults==1&&cycle.Sum.weightPixelFallbacks==4,"cycle aggregates job/map counters across polls");
        Require(cycle.Sum.milliseconds[(int)LTPaintCpuCapture.Stage.Total]==245&&cycle.CallbackMilliseconds==248,"parent Total is not a sum of nested stages or the outer callback timer");
        Require(cycle.Sum.milliseconds[(int)LTPaintCpuCapture.Stage.WeightPrepare]==200&&apply.milliseconds[(int)LTPaintCpuCapture.Stage.WeightPrepare]==0,"earlier preparation remains visible while last apply has zero preparation");
        Require(cycle.MaxPerPoll[(int)LTPaintCpuCapture.Stage.Total]==210,"peak is max per measured paint invocation");
        cycle.Record(13,apply,1,false);cycle.Record(12,prepare,1,false);cycle.Record(13,null,1,false);
        Require(cycle.Polls==6&&cycle.MeasuredPolls==3&&cycle.Sum.weightBakes==1,"skipped, duplicate and out-of-order ticks never reuse stale LastTick");
        prepare.milliseconds[(int)LTPaintCpuCapture.Stage.WeightPrepare]=999;
        Require(cycle.Sum.milliseconds[(int)LTPaintCpuCapture.Stage.WeightPrepare]==200,"sum owns its values, not a mutable LastTick alias");
        string frozen=cycle.Status(true);
        Require(frozen.Contains("завершён")&&frozen.Contains("WeightPrepare")&&frozen.Contains("максимум")&&frozen.Contains("Этапы вложенные, не складывайте"),"cycle labels lifecycle, sum/max, and inclusive timing semantics");
        Require(cycle.Status(true)==frozen,"reading completed summary never mutates accumulated results");
        var otherWorld=new LTPaintCpuCapture.Cycle();otherWorld.Reset(0);otherWorld.Record(1,apply,21,false);
        Require(cycle.MeasuredPolls==3&&otherWorld.MeasuredPolls==1,"world cycles are isolated");
        cycle.Reset(13);
        Require(cycle.Polls==0&&cycle.Sum.weightJobs==0&&cycle.MaxPerPoll.All(v=>v==0)&&cycle.CallbackMilliseconds==0,"new authoring cycle resets totals, peaks and counters");
        cycle.Record(13,apply,1,false);Require(cycle.MeasuredPolls==0,"reset baseline rejects previous cycle sample");
        cycle.Record(14,wait,16,true);Require(cycle.Sum.weightBakes==0&&cycle.WaitingOnlyPolls==1,"fresh cycle does not inherit previous apply");

        // Every integer counter must be covered by Sample.Add, including future ones.
        var a=new LTPaintCpuCapture.Sample();var b=new LTPaintCpuCapture.Sample();int n=1;
        var fields=typeof(LTPaintCpuCapture.Sample).GetFields().Where(f=>f.FieldType==typeof(int)).ToArray();
        foreach(var field in fields){field.SetValue(a,n);field.SetValue(b,n*3);n++;}
        for(int i=0;i<a.milliseconds.Length;i++){a.milliseconds[i]=i*.5;b.milliseconds[i]=i*1.5;}
        var sum=new LTPaintCpuCapture.Sample();sum.Add(a);sum.Add(b);n=1;
        foreach(var field in fields){Require((int)field.GetValue(sum)==n*4,"all sample counters included: "+field.Name);n++;}
        sum.Add(new LTPaintCpuCapture.Sample{weightInputAllocatedBytes=3L*1024*1024*1024});
        Require(sum.weightInputAllocatedBytes==3L*1024*1024*1024,"allocation totals do not overflow across large cycles");
        for(int i=0;i<a.milliseconds.Length;i++)Require(sum.milliseconds[i]==i*2,"all stage totals included");

        var capture=new LTPaintCpuCapture();
        using(capture.BeginTick(true))
        {
            using(capture.Measure(LTPaintCpuCapture.Stage.WeightPrepare)){capture.WeightJobScheduled();}
            Require(capture.CompletedTicks==0&&capture.LastTick==null,"only completed ticks can be recorded");
        }
        Require(capture.CompletedTicks==1&&capture.LastTick.weightJobs==1&&capture.LastTick.HasMaskWork,"weight preparation alone updates the last working poll");
        var last=capture.LastTick;using(capture.BeginTick()){}
        Require(capture.CompletedTicks==1&&ReferenceEquals(last,capture.LastTick),"uncaptured idle call keeps sequence and prior LastTick unchanged");
        using(capture.BeginTick(true)){capture.WeightJobResolved(true,66049);}
        Require(capture.CompletedTicks==2&&capture.LastTick.weightJobResults==1&&last.weightJobResults==0,"new sample remains independent from old sample");
        const string root="Assets/TerrainSystem/LocalTerrain/";var editor=File.ReadAllText(root+"Editor/LTEditor.cs");
        int call=editor.IndexOf("long previousPaintTick=w.paintCpu.CompletedTicks;");
        Require(call>=0&&editor.IndexOf("w.UpdatePainting(true,true)",call)>call&&editor.Contains("freshPaintTick?w.paintCpu.LastTick:null"),"coordinator checks completion token around the actual call");
        Require(editor.Contains("s.cyclePaint.Reset(w.paintCpu.CompletedTicks)")&&editor.Contains("BeginCycle(w,s)")&&editor.Contains("BeginCycle(w,state)"),"authoring and density-only cycle starts reset diagnostics");
        Require(editor.Contains("w.lastEditorPaintCycleStages=state.cyclePaint.Status(settled)")&&editor.Contains("if(freshPaintTick&&(rebuilt||w.paintCpu.LastTick.HasMaskWork))"),"cycle sum and last working poll are separate, fresh reports");
        Require(editor.Contains("отдельный отчёт, не сумма текущего цикла"),"legacy build timings are explicitly not current cycle totals");
        Console.WriteLine("PASS paint cycle diagnostics: ordered prepare/wait/apply, all counters/stages, inclusive totals, per-poll peaks, stale/duplicate rejection, reset, world isolation and actual capture scopes. Managed tests; coordinator wiring checked by source contracts.");
    }
}
