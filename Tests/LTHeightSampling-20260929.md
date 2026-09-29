# LT composed-height reuse — 2026-09-29

## Scope

First step of the next update-pipeline optimisation, not a claim that all height,
painting or mesh work now runs in Burst. Existing LOD Jobs and per-chunk scheduling
remain in place.

- `LTHeightSampling.Source` owns a copied numeric source heightfield. Its bilinear
  interpolation and floating-point operation order match `LTSource.Sample`.
- LOD0 planning/emission, manual LOD preparation, deferred Job-input preparation
  and spatial/legacy LOD emission share exact-coordinate composed heights.
- The cache holds values only, not evaluator delegates or rebuild closures. It is
  main-thread-owned; workers still receive the existing numeric `LTLODJobInput`.
- Dirty chunks invalidate their samples and pending LOD version together. Pure
  topology publication does not invalidate accepted LOD0 heights. Source layout,
  array replacement, dirty count and imported asset hash participate in refresh.
- Limits per world: 262,144 samples total, 16 resident chunks, 131,072 samples per
  chunk. Whole old chunks are evicted LRU; overflow evaluations are not retained.
  Raw key/value payload is at most 3 MiB, with additional dictionary overhead and
  the source snapshot (4 bytes per source sample). This is not a total memory cap.
- Unmodified terrain with no height-affecting stamps bypasses the composed cache.
- Native mask `GetPixelBilinear` reads and road/rock evaluation formulas remain
  unchanged. Rock-excluded contour queries do not share full-terrain values.
- LTWorld diagnostics now report reuse, evaluations, retained samples, evictions
  and bypasses. Counters describe composed-cache queries, not all height queries.

## Verification

Managed suite command:

```powershell
dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj /p:UseAppHost=false
```

Final log: `Logs/LTHeightSampling-20260929-managed-final.log`, exit 0.

- 6,048 exact source-height comparisons, including rectangular extents and bounds;
  copied-source isolation, exact float keys, local/global stale rejection, LRU and
  sample caps, full-world eviction, errors, nonfinite values and reentrant guards.
- Production mesh/LOD math: 5 plans, LOD0 positions/normals/UV/indices, cut contours,
  spatial vertex streams and every renderable stitch variant remain identical.
- Road fixture composed evaluations: **32,238 -> 12,770**, with 19,468 cache hits.
- Full pre-existing managed suite passes. Editor scheduling integration is covered
  by source contracts; this is not native Editor integration execution.

Sampling-only microbenchmark (16,384 road points, four repeated stages, warmed
alternating runs): the separate successful run in
`Logs/LTHeightSampling-20260929-managed-4.log` measured median **44.86 -> 16.31 ms**.
This excludes mesh upload, native masks, worker execution, rendering and the live
scene. It is not the expected total road-edit speedup. The final regression run
overlapped a C# build; use evaluation counts/parity, not that run's wall time, for
the stable comparison.

Production C# build with `Tests/LocalTerrainCompile.targets`, output/intermediates
under `Logs/LTStagedBuild-Compile`: exit 0, 0 errors, 3 warnings on final build.
Log: `Logs/LTHeightSampling-20260929-csharp-final.log`. Initial full build reported
6 warnings. C# compilation does not prove Unity import, Burst compilation or GPU
correctness; no native Unity/render/live-scene performance check was run here.

Earlier harness attempts and logs are retained: the first had incorrect test API
usage, the second lacked handling of search-only spatial cells, and the third
encountered a locked test executable from the second failed run. Only that owned
test process was stopped after checking its exact path, PID and start time; the
user's Unity editor and other processes were not stopped. Harness issues were
corrected before both successful full-suite runs.

## Safety / remaining work

No Unity project was opened for tests, no scene was saved, no validation Unity
project or links were created, no package files/cache were changed or cleaned.
Before/after SHA-256 values match:

| File | SHA-256 |
| --- | --- |
| Assets/TerrainTest.unity | 2D15B4E77E0CABB9102E474C14AA9D39E5FD8EE4500CE057ECD03F6292B4B2A7 |
| Packages/manifest.json | 3730D99B48B91296ADE8BDC1D4F47136EC341762D7351BDE6597D78610B82865 |
| Packages/packages-lock.json | B2294EA6BA2BEAE339614F9719CF6AD41A1D4AD03C7593790CA95558B584DADA |
| ProjectSettings/GraphicsSettings.asset | 5AE8C26EA9E00803676E4B1065E2550769A125986DE123B58610478D1CB08C33 |
| ProjectSettings/QualitySettings.asset | 7BCBD98B0E668762CC8BF52D30FBFEB35A43690D6FFE173E4C478C14FB638031 |

Live acceptance: move one road point, release the mouse, check LOD0-first and
eventual LOD completion; move it again while coarse work is pending and confirm no
old surface appears. Inspect LTWorld Diagnostics' height counters and CPU stage
times, then Undo/Redo. Full-world rebuilding may evict early chunks before their
coarse work starts; the cache deliberately prioritises bounded memory and local
edits rather than retaining all world samples.

Still separate future work: numeric snapshots of texture masks and a Burst-ready
height evaluator, then mesh emission after measured prioritisation. Source values
edited in-place by external code must still be marked dirty (or force a full
rebuild); the editor does not checksum a multi-million-element array every poll.
