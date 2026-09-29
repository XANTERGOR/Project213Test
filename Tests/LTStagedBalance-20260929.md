# LT staged balance: remaining modal progress callbacks

## Cause and correction

The stage-1 pipeline still called the old balancer's independent progress window.
It displayed after every 16,384 live cells without an elapsed-time threshold.
Planner and emitter also retained independent modal callbacks. Suppressing only
the LOD coarsener's progress did not suppress these nested operations.

- Automatic staged builds now pass one explicitly silent progress policy through
  planning, balancing, emission and spatial LOD preparation.
- Base/internal-cache/cross-chunk and legacy coarse balancing use resumable queue
  slices (1,024 visits, including stale queue entries). Queue order and split/stitch
  results remain unchanged. Partially balanced cache entries are not published.
- CPU diagnostics exclude waits between editor updates.
- Manual/save builds drain the same algorithm synchronously and retain cancelable
  progress, delayed by one second and throttled to 100 ms between reports.

This is cooperative main-thread work, not Burst/Jobs or a background thread.
Planning, mesh emission/upload and other indivisible stages can still exceed the
soft editor budget. This change does not prove reduced total computation time or
eliminate Unity's own busy dialog. The old cell-count trigger explains this popup,
but not whether the user's scene recently became slower.

## Verification

- Full `TerrainBridgeChecks` managed suite: PASS. Reproduced the old modal on a
  uniform 128x128 grid; the new path completes in 16 silent slices. Includes 96
  exact topology/stitch comparisons against the frozen pre-change balancer,
  abandoned-cache/reuse checks, nested progress policies and manual cancellation.
- C# editor project build with `LocalTerrainCompile.targets`: PASS, zero errors,
  three dependency warnings. Outputs remain under `Logs/LTStagedBuild-Compile`.
- `git diff --check`: PASS.
- TerrainTest scene, package manifest/lock and Graphics/Quality settings hashes
  match this turn's baseline. No scene saved or Unity process launched/stopped.
- No native graphics or live-scene timing test was performed. Check one road-point
  drag/release in the user's scene: automatic rebuilding should not open an LT
  modal; diagnostics and staged LOD0/coarse publication remain active.

Logs: `Logs/LTStagedBalance-20260929-managed.log`,
`Logs/LTStagedBalance-20260929-csharp.log`.
