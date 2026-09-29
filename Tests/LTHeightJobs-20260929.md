# Chunk height Jobs — 2026-09-29

## Scope

Road-affected chunks in the staged editor rebuild now prefetch numerical heights
with `IJobParallelFor`. LOD0 planning requests the base lattice first; LOD0
emission requests the balanced fine lattice. Deferred LOD preparation reuses the
same chunk/version cache. This does not move every terrain stage to a worker.

- Jobs own copied source-height patches, road samples/BVH nodes and ordered numeric
  stamp operands. They do not access scene objects, textures, meshes or the cache.
- Mask `GetPixelBilinear` and rock underside queries remain on the editor thread;
  their numeric results are passed to the worker without changing interpolation.
- Tiny batches, arbitrary cut-intersection coordinates, non-road-only chunks and
  explicit synchronous save/rebuild paths retain the previous height evaluator.
- LOD0 remains higher priority than deferred coarse LODs. A waiting job yields
  the editor tick instead of spinning until the soft time budget expires.
- Superseded work is retired, not forcibly stopped. Input-version/epoch checks
  reject late cache acceptance; native arrays remain owned until completion.
  Assembly reload/quit drains owned jobs. At most two height jobs are retained;
  each request batch contains at most 32768 points.
- The composed cache still retains at most 262144 points in total. A dense chunk
  may now borrow that whole budget instead of stopping at 131072 while shared
  capacity is unused. This changes the per-chunk allocation, not the global cap.

## Managed checks

`dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj /p:UseAppHost=false`

Successful full-suite runs are retained in
`Logs/LTHeightJobs-20260929-managed-final-2.log`,
`Logs/LTHeightJobs-20260929-repro.log` and
`Logs/LTHeightJobs-20260929-managed-high.log`.

New coverage:

- 180008 exact road/kernel comparisons: bank, ruts, width/strength variation,
  straight ends, junction fade, negative coordinates and multiple shared BVHs.
- 4356 exact cropped-source interpolation comparisons; every height blend mode;
  mixed road/stamp/rock/junction operation order.
- Exact uncut LOD0 mesh streams and coarse plans, with zero synchronous height
  fallback queries in the managed prefetch fixture.
- Stale version/global epoch rejection, preservation across unrelated edits,
  dense cache capacity, and cancellation while waiting before LOD0 publication.
- An additional 50700 queries reproduce the twelve native-check fixtures,
  including the user's first failing point (fixture 0, sample 147).

`Tests/LTHeightJobsMonoRepro.cs` was also compiled as a standalone console program
against the built runtime assembly and Unity's CoreModule, then run with the
installed Unity Mono runtime. All 50700 kernel/reference queries matched exactly.
Log: `Logs/LTHeightJobs-20260929-mono-repro.log`.
This is still managed arithmetic, not native Burst execution.

C# build with `Tests/LocalTerrainCompile.targets`: zero errors, three warnings
for both the diagnostic and Strict/High revisions. Logs are retained, including
earlier runs (`Logs/LTHeightJobs-20260929-csharp-high.log` is the last build).

## Native Unity validation — failure retained, investigation in progress

The user ran **Tools / Local Terrain / Validate Height Jobs (no scene changes)**.
This opt-in checker creates numeric fixtures only; it does not find/save scenes
or modify assets/settings. It compares all four operation prefixes, records
per-fixture maximum errors, and verifies retired-result rejection. It does not
stop on the first tolerance mismatch, but still fails at the end if any remain.

The initial Strict/Standard Burst run failed at fixture 0, sample 147 with
`4.929304E-05 m`. The expanded run confirmed:

- Burst executed all twelve jobs.
- 29 / 202800 stage samples exceeded the unchanged `2E-05 m` tolerance.
- Maximum job/reference error: `0.000147104263 m` (0.147 mm).
- Maximum scalar kernel/reference error inside Unity: exactly zero.
- Source/stamp-stage differences were much smaller; the significant divergence
  occurred at the road stage.

Failure evidence: `Logs/LTHeightJobs-20260929-native-standard-failure.log`.
The working job was changed to Strict/High precision for a follow-up comparison.
The user repeated the native check at 04:56:11: the same 29 / 202800 failures and
`0.000147104263 m` maximum remained. Higher math precision alone did not fix it.
The tolerance was NOT relaxed. A further opt-in trace records nearest segment,
projection fraction, squared distance, interpolated hit and blending operands.
At that point native parity still failed pending diagnosis; the managed suite
did not establish native parity (see the guarded-projection follow-up below).
Rendering, complete live-scene latency and player performance are separate and
have not been validated by this checker.

### Follow-up: guarded projection v1

Native intermediate tracing identified adjacent-segment selection, not the
height-source interpolation: at fixture 9, `(31,25)`, Mono selected segment 63
with `t=0.00182985677`, squared distance `19.5774651`; Burst selected the shared
endpoint of segment 62 with `t=1`, squared distance `19.577467`. Both segments
belong to the same BVH leaf, so this case is not a branch-pruning difference.
The chosen spline height and bank then amplify that selection difference.
Evidence is retained in `Logs/LTHeightJobs-20260929-native-high-trace.log`.

The production worker now flags near-equal candidate distances in a conservative
coordinate/radius-scaled floating-point band. Identical shared endpoints are
excluded. A clearly better candidate clears earlier ambiguity; BVH pruning uses
the same conservative band so near competitors are inspected. This does NOT
loosen the final height tolerance or silently choose a new tie-breaking rule.

`PrefetchHeights` accepts unambiguous job heights as before. For flagged points,
it calls the original version-guarded composed-height evaluator on the editor
thread. That evaluator uses the complete original stamp order, not the expected
test values or just a replacement road height. Those evaluations are counted
separately as CPU reference fallbacks, not accepted job heights.

The native checker applies this same flagged-only resolution policy, retains
and reports raw job differences, and still fails on any remaining resolved
error above `2E-05 m`. Passing this check means parity of the hybrid production
path, not bitwise equality of raw Burst arithmetic. The user's subsequent native
menu run passed, as read from Editor.log: 202800 stage samples, maximum resolved
error 2.861023E-06 m, Burst executed 12/12 jobs, retired result rejected;
1650/202800 reference fallbacks. The 29 raw differences remain reported, with
raw maximum 0.000147104263 m. Tolerance remained 2E-05 m. This is correctness of
guarded projection v1, not a live-scene performance benchmark.
The initial guarded managed suite and C# build pass; logs:
`Logs/LTHeightJobs-20260929-managed-guarded.log`,
`Logs/LTHeightJobs-20260929-csharp-guarded.log` (zero errors, six warnings).
Additional endpoint/coverage regressions now pass in
`Logs/LTHeightJobs-20260929-managed-guarded-final-2.log`: 825 / 50700 managed
fixture queries were flagged (about 1.6%, not a live-scene measurement).
The shared-endpoint fixture uses the actual stored spline sample coordinate,
not a nominal integer that need not be an exact sampled vertex. Its earlier
failure log (`managed-guarded-final.log`) is retained rather than overwritten.

## User's in-progress scene diagnostic

The screenshot received during implementation already showed `Burst Job` for
height generation, 131072 accepted job heights, and one edited chunk. Geometry
CPU time was 757.5 ms; accumulated painting checks were 1769.2 ms over nine polls.
The cycle was still waiting for coarse LODs: this is not a complete before/after
benchmark. A paint poll is not necessarily a mask rebake; the last reported
poll was 1.9 ms with no weight/displacement/global bakes.

The old per-chunk cache cap had been reached (12046 bypassed queries); that
motivated sharing the unchanged global budget with a dense chunk. Do not claim
a measured speedup for this adjustment without another comparable scene run.

## Safety

No Unity validation project was created, linked or opened. No package cache,
Unity installation, package manifest/lockfile or graphics/quality settings were
changed. No editor was stopped, no scene was saved by the agent, and no cleanup
was performed. Test outputs are under `Logs`, not the working project's `Temp`.

Manifest, lockfile, graphics and quality hashes matched the start-of-turn
baseline. The on-disk TerrainTest scene hash changed during the user's live
editing session; it was left intact, not restored or rewritten by the agent.
