Frame flight profiler (Editor diagnostic, not a standalone benchmark)

Use Tools > Local Terrain > Frame Profiler (5 seconds).
Select SceneView (works outside Play Mode) or GameView (requires Play Mode).
Stop any existing Profiler recording. Disable Deep Profile and Call Stacks.
Click Start; return to your view during the 3-second countdown and fly for 5 seconds.
Keep the scene/render settings unchanged during the capture.
Wait for the report-complete message. Files: Logs/FrameProfiles/flight-<UTC>-<unique>.*

Outputs:
  .txt          CPU/GPU frame mean, median, p95, max; waits vs work;
                per-thread Self tables, inclusive Local Terrain timings, GPU passes.
  -frames.csv   Per-frame timings and available rendering/GC counters.
  -markers.csv  Full collected path/thread aggregates (Self and Total separate).
  .data         ProfilerDriver.SaveProfile; inspect using Load in Unity Profiler.
                This native file can contain older history. Use frames.csv frame IDs.

Safety/measurement limits:
  No scene save, camera motion, LOD/quality/FPS change, automatic Play Mode, history
  clear, player launch, package change or changes to the existing detail capture tool.
  Only the stopped local Editor target can be used. Profiler CPU/GPU/Rendering/Memory
  area flags and Profile Editor mode are temporarily set and restored after analysis.
  Remote/new recording changes abort the capture; new target/settings are not overwritten.
  First/last received frames are excluded (partial boundaries).
  History capacity is unchanged: overwritten frames produce an explicit partial report.
  GPU availability comes from Unity's Gathered flag AND a positive finite frame time.
  Missing data is N/A, not zero. A missing GPU-pass hierarchy is reported separately.
  GPU internal column constants are resolved from the installed Unity assembly; no
  guessed numeric constants. Unsupported versions still export CPU and native profile.
  Shader/object-specific GPU cost is NOT inferred from draw counts or CPU submission.
  Profiler overhead is real, especially GPU profiling (may affect Graphics Jobs).
  All visible views/cameras can affect results. SceneView CPU timing is the Editor
  cycle, including UI/idle; not game FPS. VSync/Present waits do not prove a GPU bottleneck.
  CPU/GPU/worker thread times overlap; do not add them. Inclusive parent/child times
  also overlap. Mean marker costs include zero for absent frames, and GPU averages
  exclude frames without measurements. Self can still contain uninstrumented waits.
  Analysis happens after recording. Boundaries: <=10000 frame events, <=256 threads,
  <=250000 hierarchy nodes per thread/frame, <=128 depth, stop analysis after 500000
  stored markers. Limits and missing frames are reported rather than silently guessed.
  Raw .data is saved before analysis. Closing/reload/Play Mode changes save only a
  partial diagnostic where possible; no claim of complete performance data.

Verification:
  dotnet run --project Tests/FrameProfilerChecks/FrameProfilerChecks.csproj
  The managed harness links actual report/window code against test doubles, and writes
  fixture reports ONLY below Logs/FrameProfilerChecks. It is NOT a native Unity project.
  Covers clock boundaries, duplicate/foreign frame events, missing/boundary frames,
  GC zero vs missing, per-frame sums, exclusive/inclusive timing, GPU denominators,
  invariant/escaped CSV, all shutdown paths and foreign-recording protection.
  Actual Unity API compilation uses Tests/LocalTerrainCompile.targets.
  Native import, frame callbacks, GPU driver data and runtime cost need a user flight.

Primary API references consulted (installed Unity 6000.3 API checked by C# build):
  https://docs.unity.com/en-us/engine/6000.3/script-reference/unityeditor/profiling/hierarchyframedataview
  https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ProfilerEditor/Public/ProfilerAPI.bindings.cs
  https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/ProfilerEditor/Public/HierarchyFrameDataView.bindings.cs
