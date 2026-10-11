using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Reflection;
using LocalTerrainPrototype;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine.Profiling;

static class Program
{
    static int assertions;
    static void Require(bool ok, string why) { assertions++; if (!ok) throw new Exception(why); }
    static object Call(object o, string name) => o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(o, null);
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
    static void Main()
    {
        MathChecks(); StageReportChecks(); LifecycleChecks(); SourceChecks();
        Console.WriteLine("PASS Frame Profiler: " + assertions + " managed assertions; statistics, lifecycle, restoration, partial/missing frames and source safety. NOT native Unity/GPU or live performance.");
    }
    static void MathChecks()
    {
        Require(double.IsNaN(LTFrameProfileReport.Percentile(Array.Empty<double>(), .95)), "empty percentile unavailable");
        Require(LTFrameProfileReport.Percentile(new[] { 1d, 2, 10, double.NaN, -1, double.PositiveInfinity }, .95) == 10, "p95 ignores invalid");
        Require(LTFrameProfileReport.Percentile(new[] { 1d, 2, 10 }, .5) == 2, "nearest rank median");
        var report = new LTFrameProfileReport { receivedFrames = 5, context = "fixture", reason = "done" };
        var a = new LTFrameProfileReport.Frame { index = 10, cpuMs = 20, gpuMs = 8, gcBytes = 0 };
        var b = new LTFrameProfileReport.Frame { index = 11, cpuMs = 30 };
        var c = new LTFrameProfileReport.Frame { index = 12, cpuMs = 10, gpuMs = 6 };
        report.frames.AddRange(new[] { a, b, c });
        void Add(LTFrameProfileReport.Frame f, string path, double total, double self, bool gpu = false, string thread = "Main Thread [; 1]") =>
            f.samples.Add(new LTFrameProfileReport.Sample { name = path.Split('/').Last(), path = path, total = total, self = self, calls = 1, gpu = gpu, thread = thread });
        Add(a, "Loop", 12, 2); Add(a, "Loop/Work", 4, 4); Add(a, "Loop/Work", 6, 6);
        Add(c, "Loop/Work", 2, 2); Add(a, "Loop/Work", 7, 7, thread: "Worker [; 2]");
        Add(a, "Render/Shadows", 4, 4, true); Add(c, "Render/Shadows", 6, 6, true);
        Add(a, "Loop/Gfx.WaitForPresentOnGfxThread", 8, 8);
        var rows = report.Rows(); var work = rows.Single(r => r.path == "Loop/Work" && LTFrameProfileReport.IsMain(r.thread));
        Require(work.self == 12 && work.total == 12 && work.calls == 3 && work.present == 2, "same path frame sums, zeros implicit");
        Require(work.maxSelf == 10 && work.peakFrame == 10, "max frame not max call");
        Require(rows.Count(r => r.path == "Loop/Work") == 2, "parallel threads separate");
        Require(rows.Single(r => r.path == "Loop").self == 2, "self does not include child total");
        Require(LTFrameProfileReport.IsWait("Gfx.WaitForPresentOnGfxThread") && !LTFrameProfileReport.IsWait("RenderShadows"), "wait labels not active work");
        var csv = report.MarkersCsv();
        Require(csv.Contains("\"Loop/Work\",4,4,10,10"), "mean over all CPU frames incl absent");
        Require(csv.Contains("\"Render/Shadows\",5,5,6,12"), "GPU denominator only measured frames");
        Require(report.FramesCsv().Contains("11,30,N/A,N/A,N/A,N/A,N/A"), "unavailable is not zero");
        Require(report.FramesCsv().Contains("10,20,8,0"), "real zero counter preserved");
        Require(LTFrameProfileReport.Csv("a,\"b\"\nc") == "\"a,\"\"b\"\"\nc\"", "CSV escape");
        var culture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = new CultureInfo("ru-RU"); Require(LTFrameProfileReport.Number(1.25) == "1.25", "invariant number"); }
        finally { CultureInfo.CurrentCulture = culture; }
        var summary = report.Summary();
        Require(summary.Contains("Вложенные Total нельзя складывать") && summary.Contains("Ожидания НЕ доказывают GPU") && summary.Contains("не производительности standalone"), "interpretation cautions");
        report.frames.Clear(); report.missingFrames = 3;
        Require(report.Summary().Contains("GPU НЕДОСТУПЕН") && report.Summary().Contains("неполный захват"), "empty/truncated report honest");
    }
    static void StageReportChecks()
    {
        var report=new LTFrameProfileReport();
        var first=new LTFrameProfileReport.Frame{index=1,cpuMs=10,gpuMs=1};
        var second=new LTFrameProfileReport.Frame{index=2,cpuMs=10};report.frames.Add(first);report.frames.Add(second);
        void Add(string name,string path,double self,string thread="Main Thread [; 1]",bool gpu=false)
            =>first.samples.Add(new LTFrameProfileReport.Sample{name=name,path=path,total=self,self=self,calls=1,thread=thread,gpu=gpu});
        for(int i=0;i<30;i++)Add("LT.Noise"+i,"Loop/LT.Noise"+i,100+i);
        // More than 20 paths: every stage in both Scene View repaint paths must
        // survive the dedicated table, even when the common LT top-20 is full.
        for(int i=0;i<28;i++)
        {
            string name="LT.Details.GPU.Stage.Stage"+i+"CPU";
            Add(name,"Loop/"+name,.01);
        }
        const string stage="LT.Details.GPU.Stage.DrawSubmitCPU";
        Add(stage,"First/"+stage,2);Add(stage,"Second/"+stage,3);
        Add(stage,"First/"+stage,1); // same path repeated in a frame must still sum
        Add(stage,"Worker/"+stage,100,"Worker [; 2]");
        Add(stage,"GPU/"+stage,100,"Main Thread [; 1]",true);
        var summary=report.Summary();int start=summary.IndexOf("GPU vegetation —",StringComparison.Ordinal);
        int end=summary.IndexOf("\nGPU —",start,StringComparison.Ordinal);var table=summary.Substring(start,end-start);
        Require(table.Contains("НЕ время GPU")&&table.Contains("вложенные Total не складывать"),"CPU-only and nesting labels");
        for(int i=0;i<28;i++)Require(table.Contains("Loop/LT.Details.GPU.Stage.Stage"+i+"CPU"),"stage hidden by general top-20: "+i);
        Require(table.Contains("1.5 | 1.5 | 3 (#1) | 1 | Main Thread [; 1] | First/"+stage),"stage mean uses all frames and retains path");
        Require(table.Contains("1.5 | 1.5 | 3 (#1) | 0.5 | Main Thread [; 1] | Second/"+stage),"other repaint path kept separate");
        Require(!table.Contains("Worker/")&&!table.Contains("GPU/"),"CPU main-stage table excludes GPU/parallel workers");
        report.frames.Clear();var empty=report.Summary();start=empty.IndexOf("GPU vegetation —",StringComparison.Ordinal);
        end=empty.IndexOf("\nGPU —",start,StringComparison.Ordinal);
        Require(empty.Substring(start,end-start).Contains("Нет зарегистрированных замеров."),"absent stages are unavailable, not zero");
    }
    static void Reset()
    {
        ProfilerDriver.enabled = Profiler.enabled = Profiler.enableBinaryLog = ProfilerDriver.deepProfiling = false;
        ProfilerDriver.profileEditor = false; ProfilerDriver.memoryRecordMode = ProfilerMemoryRecordMode.None;
        ProfilerDriver.connectedProfiler = 7; ProfilerDriver.identifier = "Editor"; ProfilerDriver.saves = 0;
        ProfilerDriver.throwGpuEnable = ProfilerDriver.throwSave = false;
        ProfilerDriver.areas.Clear(); ProfilerDriver.areas[ProfilerArea.Rendering] = true;
        EditorApplication.timeSinceStartup = 0;
        EditorApplication.isCompiling = EditorApplication.isUpdating = EditorApplication.isPaused = EditorApplication.isPlaying = EditorApplication.isPlayingOrWillChangePlaymode = false;
        LTFrameProfileReader.missing.Clear();
    }
    static LTFrameProfileWindow Window() { var w = new LTFrameProfileWindow(); Call(w, "OnEnable"); return w; }
    static void Restored()
    {
        Require(!ProfilerDriver.enabled && !Profiler.enabled && !ProfilerDriver.profileEditor, "profiler recording/editor restored");
        Require(!ProfilerDriver.IsAreaEnabled(ProfilerArea.CPU) && !ProfilerDriver.IsAreaEnabled(ProfilerArea.GPU) &&
            ProfilerDriver.IsAreaEnabled(ProfilerArea.Rendering) && !ProfilerDriver.IsAreaEnabled(ProfilerArea.Memory), "all profiler areas restored");
    }
    static void LifecycleChecks()
    {
        Reset(); var w = Window(); Call(w, "StartCapture");
        Require(Field<object>(w, "phase").ToString() == "Countdown" && !ProfilerDriver.enabled, "countdown no profiler mutation");
        EditorApplication.Tick(2.99); Require(!ProfilerDriver.enabled, "no premature recording");
        EditorApplication.Tick(3); Require(ProfilerDriver.enabled && ProfilerDriver.profileEditor, "Scene View records Editor");
        for (int i = 100; i <= 112; i++) { ProfilerDriver.Frame(i); ProfilerDriver.Frame(i); }
        ProfilerDriver.Frame(300, 99); LTFrameProfileReader.missing.Add(102);
        EditorApplication.Tick(7.99); Require(ProfilerDriver.enabled, "record until full five seconds");
        EditorApplication.Tick(8); Require(!ProfilerDriver.enabled, "five second automatic stop");
        EditorApplication.Tick(8.6); EditorApplication.Tick(8.7); Restored();
        var report = Field<LTFrameProfileReport>(w, "report");
        Require(report.receivedFrames == 13 && report.frames.Count == 10 && report.missingFrames == 1, "duplicate/foreign events excluded and missing counted");
        Require(report.frames.All(f => f.index > 100 && f.index < 112), "partial first/last excluded");
        Require(report.wallSeconds == 5 && ProfilerDriver.saves == 1, "duration exact and native save once");
        Require(File.Exists(Field<string>(w, "reportPath")), "readable report saved");
        Call(w, "OnDisable"); Restored();

        Action[] aborts = { () => AssemblyReloadEvents.Reload(), () => EditorApplication.ChangePlayMode(), () => EditorApplication.Quit(),
            () => { EditorApplication.isCompiling = true; EditorApplication.Tick(4); }, () => { ProfilerDriver.Clear(); EditorApplication.Tick(4); } };
        foreach (var abort in aborts)
        {
            Reset(); w = Window(); Call(w, "StartCapture"); EditorApplication.Tick(3); ProfilerDriver.Frame(1); abort();
            Restored(); Require(Field<object>(w, "phase").ToString() == "Idle", "abort ends capture"); Call(w, "OnDisable");
        }
        Reset(); w = Window(); Call(w, "StartCapture"); Call(w, "OnDisable"); Restored(); Require(ProfilerDriver.saves == 0, "countdown close no empty profile");
        Reset(); w = Window(); Call(w, "StartCapture"); ProfilerDriver.enabled = true; EditorApplication.Tick(3);
        Require(ProfilerDriver.enabled && Field<object>(w, "phase").ToString() == "Idle", "foreign recording during countdown untouched"); Call(w, "OnDisable");
        foreach (string guard in new[] { "recording", "binary", "deep", "stacks", "remote" })
        {
            Reset(); w = Window();
            if (guard == "recording") ProfilerDriver.enabled = true;
            if (guard == "binary") Profiler.enableBinaryLog = true;
            if (guard == "deep") ProfilerDriver.deepProfiling = true;
            if (guard == "stacks") ProfilerDriver.memoryRecordMode = ProfilerMemoryRecordMode.GCAlloc;
            if (guard == "remote") ProfilerDriver.identifier = "WindowsPlayer";
            Call(w, "StartCapture"); Require(Field<object>(w, "phase").ToString() == "Idle", "guard " + guard); Call(w, "OnDisable");
        }
        Reset(); w = Window(); Call(w, "StartCapture"); ProfilerDriver.throwGpuEnable = true; EditorApplication.Tick(3); Restored(); Call(w, "OnDisable");
        Reset(); w = Window(); Call(w, "StartCapture"); EditorApplication.Tick(3); ProfilerDriver.Frame(1);
        ProfilerDriver.throwSave = true; EditorApplication.Tick(8); EditorApplication.Tick(8.6); EditorApplication.Tick(8.7);
        Restored(); Require(Field<LTFrameProfileReport>(w, "report").notes.Any(n => n.Contains("fixture file failure")), "save failure retained"); Call(w, "OnDisable");
        Reset(); w = Window(); Call(w, "StartCapture"); EditorApplication.Tick(3); ProfilerDriver.connectedProfiler = 99;
        EditorApplication.Tick(4); Require(ProfilerDriver.enabled && ProfilerDriver.connectedProfiler == 99, "new remote target not stopped/changed");
        Require(!Profiler.enabled, "owned local recording stopped on remote switch"); Call(w, "OnDisable");
        foreach (bool duringAnalysis in new[] { false, true })
        {
            Reset(); w = Window(); Call(w, "StartCapture"); EditorApplication.Tick(3);
            for (int i = 0; i < 5; i++) ProfilerDriver.Frame(i);
            EditorApplication.Tick(8); if (duringAnalysis) EditorApplication.Tick(8.6);
            int priorSaves = ProfilerDriver.saves;
            ProfilerDriver.enabled = true; ProfilerDriver.profileEditor = true;
            ProfilerDriver.areas[ProfilerArea.GPU] = true;
            EditorApplication.Tick(8.7);
            Require(ProfilerDriver.enabled && ProfilerDriver.profileEditor && ProfilerDriver.IsAreaEnabled(ProfilerArea.GPU), "new same-target recording/settings untouched during drain/analysis");
            Require(ProfilerDriver.saves == priorSaves, "no export of another recording"); Call(w, "OnDisable");
        }
    }
    static void SourceChecks()
    {
        var window = File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTFrameProfileWindow.cs");
        foreach (string forbidden in new[] { "SaveScene(", "SaveAssets(", "ClearAllFrames(", "LoadProfile(", "EnterPlaymode(", "RepaintAll(", "QueuePlayerLoopUpdate(" })
            Require(!window.Contains(forbidden), "no scene/settings/history mutation: " + forbidden);
        var reader = File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTFrameProfileReader.cs");
        Require(reader.Contains("using (var view") && reader.Contains("using (var raw") && reader.Contains("using (var iterator"), "native profiler views disposed");
        Require(reader.Contains("GpuProfilingStatisticsAvailabilityStates.Gathered") && reader.Contains("raw.HasCounterValue"), "availability gates");
        var world = File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTWorld.cs");
        Require(world.Contains("using var lodProfile = LodUpdateMarker.Auto()") && world.Contains("using(LodBalanceMarker.Auto())"), "balanced LOD scopes");
    }
}
