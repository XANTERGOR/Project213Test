# Shared LOD preparation — 2026-09-28

## Change

- Each changed terrain chunk prepares its height samples and immutable LOD0 plane/error data once for all requested LODs. Each level still starts from LOD0 with its own merge state and the existing error budget, ordering, protection regions and boundary policy.
- Both WholeChunk and WithinChunk paths use the shared preparation. The cache is local to one synchronous rebuild, never a persistent or static cache across road edits. WholeChunk prepares coarse plans per chunk before the existing per-level neighbour balancing; only plans, not height caches, are retained across chunks.
- LOD progress waits one second from the rebuild start, then repaints at most every 100 ms, checking every 1024 work items. A single clock spans chunks/levels. Cancellation still throws before mesh publication and the existing rebuild `finally` clears the progress bar. Other stages' progress bars are unchanged.
- Build diagnostics now include the number of prepared chunk snapshots and unique height samples used for LOD coarsening. This excludes height queries in planning, mesh emission, normal generation and painting.

## Verification

`dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj` — PASS.

- 120 exact ordered-cell comparisons against the frozen pre-change algorithm: regular/irregular grids, rectangular offset chunks, boundary locking, protected strips/regions, patch divisions 1/4/8, forward/reverse/repeated requests, independent next-edit height sources and input preservation.
- Invalid settings and LOD0-only requests do not sample heights. Returned plans and caller input lists do not alias internal preparation state.
- Injected-clock checks verify the one-second delay, repaint throttling, cancellation, retry after partial preparation, and progress across multiple individually small chunks.
- Existing production-emitter tests now use shared preparation: 8 multi-chunk layouts, 224 mixed/incremental LOD comparisons, preserved holes and watertight edges; 526241 road surface-height comparisons, asphalt protection, and exact return-near LOD0.
- The rest of the managed regression suite also passed.

Synthetic managed benchmark, 16384 cells and four LOD levels; five alternating measured runs after warm-up:

| Metric | Previous | Shared preparation |
| --- | ---: | ---: |
| Height evaluations | 264196 | 66049 |
| Managed allocation | 28336568 B | 13107736 B |
| Median preparation time | 52.33 ms | 27.81 ms |

These are isolated CPU measurements, not full Unity scene-update timings or a GPU/FPS benchmark.

Editor C# project build with `Tests/LocalTerrainCompile.targets` — PASS, 0 errors, 3 warnings in this incremental build. Logs: `Logs/LODPreparation-managed.log` and `Logs/LODPreparation-build.log`.

No new Unity editor or validation project was launched. Native rendering, live spline-edit timings and the native progress dialog were not exercised by the automated checks. No shader, scene, graphics/quality settings, package version, normal calculation or contact-shadow setting was changed. Scene and package/graphics/quality hashes remained unchanged during this task.
