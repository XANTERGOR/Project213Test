using System;
using System.IO;
using LocalTerrainPrototype;

partial class Checks
{
    static void LODCaptureChecks()
    {
        long now=0;int clockReads=0;
        var capture=new LTLODCpuCapture(()=>{clockReads++;return now;},1000);
        using(capture.BeginUpdate(1,0,0,0,true,-1))using(capture.Measure(LTLODCpuCapture.Stage.Upload))capture.Uploaded(12);
        Require(clockReads==0&&!capture.Poll()&&capture.LastReport==null,"disabled capture neither reads clock nor creates samples");
        capture.Start("test","Edit","automatic");
        capture.Start("ignored","Play","fixed");
        now=100;
        using(capture.BeginUpdate(7,1,2,3,true,-1))
        {
            capture.TerrainState(true,true,256);capture.TopologyResult(2,4,160);
            using(capture.Measure(LTLODCpuCapture.Stage.Selection))now+=2;
            using(capture.Measure(LTLODCpuCapture.Stage.Balance))now+=3;
            using(capture.Measure(LTLODCpuCapture.Stage.TerrainApply))
            {
                using(capture.Measure(LTLODCpuCapture.Stage.Clone)){now+=5;capture.MeshCloned();}
                using(capture.Measure(LTLODCpuCapture.Stage.Indices))now+=7;
                using(capture.Measure(LTLODCpuCapture.Stage.Upload))now+=11;
                capture.Uploaded(600);capture.Uploaded(900);
            }
            capture.TopologyBuilt();capture.Completed();
            capture.IndexCacheResult(20,3,120,1,4096);
            Require(!capture.Stop()&&!capture.Poll(),"no report formatting/stop inside a timed update");
        }
        now=200;
        using(capture.BeginUpdate(7,4,5,6,false,2)){now+=1;capture.Completed();}
        // A failed callback is retained as incomplete; using/finally closes the sample.
        now=300;
        try
        {
            using(capture.BeginUpdate(7,0,0,0,true,-1))
            {capture.TopologyFailed();now+=4;throw new InvalidOperationException();}
        }
        catch(InvalidOperationException){}
        now=15000;
        using(capture.BeginUpdate(7,0,0,0,true,-1))capture.Uploaded(999);
        Require(capture.Running&&capture.LastReport==null,"deadline does not format a report from render callback");
        Require(capture.Poll()&&!capture.Poll(),"one report published outside callback");
        var report=capture.LastReport;var first=report.samples[0];
        Require(report.samples.Length==3&&report.world=="test"&&report.mode=="Edit","restart ignored; no expired sample");
        Require(first.total==28&&first.selection==2&&first.balance==3&&first.terrainApply==23&&first.clone==5&&first.indices==7&&first.upload==11,"exact exclusive/inclusive timing semantics");
        Require(first.cameraId==7&&first.cameraX==1&&first.cameraY==2&&first.cameraZ==3&&first.beforeRender&&first.forceLOD==-1,"camera/callback context retained");
        Require(first.uploadedChunks==2&&first.uploadedIndices==1500&&first.dirtyChunks==2&&first.resetPatches==4&&first.processedCells==160&&first.clonedMeshes==1&&first.topologyBuilds==1,"per-call workload counters");
        Require(first.indexCacheHits==20&&first.indexCacheBuilds==3&&first.indexCacheCells==120&&first.indexCacheFallbacks==1&&first.indexCacheBytes==4096,"per-call index cache counters and retained capacity");
        var idle=report.samples[1];
        Require(idle.total==1&&idle.uploadedChunks==0&&idle.uploadedIndices==0&&idle.clonedMeshes==0&&idle.balance==0,"idle sample resets every counter and stage");
        Require(idle.indexCacheHits==0&&idle.indexCacheBuilds==0&&idle.indexCacheCells==0&&idle.indexCacheFallbacks==0&&idle.indexCacheBytes==0,"index cache counters do not leak into a later invocation");
        Require(!report.samples[2].completed&&report.samples[2].topologyFailed,"partial/fallback results identified");
        Require(capture.Status.Contains("Total mean / p95 / max: 11.000 / 28.000 / 28.000")&&capture.Status.Contains("неполных/ошибок: 1")&&capture.Status.Contains("Самый тяжёлый вызов #0"),"summary and peak correlation");
        Require(capture.Status.Contains("НЕ на кадр/GPU")&&report.note.Contains("Stage maxima can belong to different calls"),"no CPU/frame/GPU or stage-max ambiguity");

        capture.Start("empty","Edit","no root");now+=15000;
        Require(capture.Poll()&&capture.LastReport.samples.Length==0&&capture.Status.Contains("Нет замеров"),"empty capture explains absent data");
        capture.Start("bounded","Edit","flight");
        for(int i=0;i<LTLODCpuCapture.Capacity+100;i++)using(capture.BeginUpdate(1,0,0,0,true,-1))capture.Completed();
        Require(capture.Running&&capture.LastReport==null,"full buffer stops sampling without formatting in render");
        Require(capture.Poll()&&capture.LastReport.samples.Length==LTLODCpuCapture.Capacity&&capture.LastReport.stopReason=="buffer full","bounded buffer reports truncation explicitly");

        // Warm both paths before testing allocations; includes Scope disposal and counters.
        capture.Start("allocation","Edit","flight");
        void Tick()
        {
            using(capture.BeginUpdate(1,0,0,0,true,-1))
            {
                using(capture.Measure(LTLODCpuCapture.Stage.Balance))capture.TopologyResult(2,4,16);
                using(capture.Measure(LTLODCpuCapture.Stage.Indices))capture.Uploaded(600);
                capture.Completed();
            }
        }
        for(int i=0;i<100;i++)Tick();
        long allocated=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++)Tick();
        Require(GC.GetAllocatedBytesForCurrentThread()==allocated,"active capture has zero managed per-call allocation");
        Require(capture.Stop("test")&&capture.LastReport.samples.Length==1100,"manual stop/reset yields only current capture");
        allocated=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)Tick();
        Require(GC.GetAllocatedBytesForCurrentThread()==allocated,"inactive instrumentation has zero managed allocation");

        var world=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTWorld.cs");
        int gate=world.IndexOf("if(!lodCameraSchedule.TryBegin(",StringComparison.Ordinal);
        Require(world.IndexOf("lodCpu.BeginUpdate(",gate,StringComparison.Ordinal)>gate,"only accepted LOD invocations recorded");
        var body=world.Substring(gate,world.IndexOf("bool UpdateSpatialTopology(",gate,StringComparison.Ordinal)-gate);
        // Rock seam fallback predates the frame profiler: a single warning after permanently
        // disabling the failed seam bake is allowed. Normal timing/export must stay out of render.
        const string seamWarning="Debug.LogWarning(\"Joint rock/terrain LOD disabled for this bake; original matching contours retained: \"+error.Message,this);";
        Require(body.Contains("if(!rockSeamFailed)")&&body.Contains("rockSeamFailed=true;"),"seam failure diagnostic remains one-shot");
        var normalBody=body.Replace(seamWarning,"");
        Require(!normalBody.Contains("PublishLODCpuReport")&&!normalBody.Contains("File.Write")&&!normalBody.Contains("Debug.Log"),"no log/export from normal render hot path");
        var editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        Require(editor.Contains("world.PollLODCpuCapture();")&&editor.Contains("if(w.lodCpu.Running)return;"),"editor finalizes without selection and avoids diagnostic mesh scans during capture");
        Console.WriteLine("PASS LOD CPU capture: deterministic stage/peak accounting, dirty/upload/clone counters, idle reset, exceptions, deadlines, bounded buffer, empty/manual captures, active/inactive zero per-call allocations, and hot-path source contracts. Not native render timing.");
    }
}
