# LT stage 1: cooperative LOD0-first editor rebuilding

## Implemented

- Automatic rebuilding waits for mouse release and a 150 ms authoring debounce.
  The old `rebuildAfterEdit` field remains for scene compatibility, but is hidden;
  automatic updates now always wait for release.
- One revisioned continuation per world, with a coalesced dirty set rather than
  a queue of obsolete snapshots. Geometry/settings, density, Undo and asset-import
  invalidation reject old work before the next stage. Undo retains local diffing.
- Capture owns its stamp/contour snapshot for the duration of the continuation.
  Generated object replacement/deletion invalidates an active continuation.
- Plan/emit changed chunks, validate shared normals and rock bridges, then publish
  LOD0 for the affected seam group. There is a mandatory editor-tick boundary before
  coarse work. Independent adjacent chunks retain their existing LOD data.
- WithinChunk receives a transient LOD0-only spatial asset, including edge variants.
  Its neighbours can still refine/coarsen through the shared spatial topology.
  It uses `HideAndDontSave` and is destroyed on replacement, disable, destruction
  or assembly reload. It is never saved as a generated asset.
- `lodsPending` prevents old coarse data from rendering with a new base, including
  after a reload that lost transient data. Until a new complete set is ready, the
  last accepted LOD0 remains visible.
- Coarsening and emission yield between chunk/level stages. The shared per-chunk
  height cache from the previous optimization remains enabled.
- Paint/density refinement and LOD0 collider updates run after base publication.
  Refinement can invalidate the unfinished coarse set. Final coarse publication
  does not rewrite LOD0 or its `updatedAt`, avoiding another paint/detail rebake.
- Save/manual rebuild drains the same pipeline synchronously. A player build
  rejects unfinished LOD data. No automatic scene saving was introduced.
- Diagnostics distinguish preparation of LOD0 and deferred LOD work, with CPU time
  excluding intervals between editor updates.

## Deliberate stage-1 limits

This is **not Burst, Jobs, Task.Run or a background worker**. Unity API calls and
calculation still execute on the main thread. The coordinator has a soft 4 ms
budget, checked only between indivisible stages, and a 100 ms polling interval.
A single heavy planner, coarsener, spatial emitter, bridge validation, upload or
collider cook can still block longer. No live-scene speedup is claimed.

Publication is atomic with respect to editor frames for a dirty seam group, not
individual arbitrarily ordered chunks. An edit that invalidates the world snapshot
restarts its unfinished group; per-chunk dependency/version isolation is future work.
Coarse levels are published together after their set is ready. A temporary spatial
LOD0 incurs extra topology/variant preparation and transient memory until completion.

## Checks

- `dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj`:
  complete managed suite PASS. Includes LOD0-before-coarse, 100 rapid superseding
  edits, stale-result rejection, seam-neighbour dirty union, cancellation before/
  after base publication, failure disposal and production source contracts.
- Spatial checks: 8 layouts, 4 preview-chunk combinations each, both uniform and
  irregular meshes with cut contours, patch divisions 1/2/4/8. LOD0-only preview
  plus existing coarse neighbours is watertight and preserves hole boundaries.
  Preview LOD0 geometry matches the complete spatial bake.
- C# project compilation with `Tests/LocalTerrainCompile.targets`: PASS, 0 errors.
  The full build reported six unrelated warnings (assembly-reference conflicts
  and unused road projection fields); the final incremental build reported three
  dependency warnings. Output is under `Logs/LTStagedBuild-Compile`, not Unity Temp.
- An earlier repeat build failed with CS0006 when its DLL in Unity Temp disappeared
  during the user's Editor startup. Its log is retained; no caches/packages were
  modified. The separate-output rerun succeeded.
- `git diff --check`: PASS.
- No scene opened/saved by the checks. TerrainTest scene, package manifest/lock,
  GraphicsSettings and QualitySettings hashes match the pre-edit baseline.
- Native scene rendering, save/reload behavior and interactive performance are
  **not** proven by managed compilation/tests. No automated graphics test was run.

Logs: `Logs/LTStagedBuild-managed-final.log`,
`Logs/LTStagedBuild-csharp-isolated-output.log`, and the retained failed repeat
`Logs/LTStagedBuild-csharp.log`.

## Manual acceptance in the user's scene

1. With Auto Update and LODs enabled, drag a road point and release. Old geometry
   should stay visible until a coherent LOD0/rock-edge group appears. Diagnostics
   should then say `LOD0 показан; расчёт остальных LOD`.
2. Edit again before completion; only the latest shape should complete. Check a
   chunk corner, a rock cut, a road intersection, both LOD modes and force LOD.
3. Move the camera after completion; verify coarse LOD switching returns, with no
   stale road shape, edge cracks or extra terrain paint/detail regeneration.
4. During deferred work, try Undo/Redo, Auto Update off/on, world disable/enable,
   save and script reload. No stale coarse mesh should appear. Saving may block
   while the synchronous barrier finishes the set.
5. Compare cycle CPU times and time to first visible LOD0. Stage 2 should target
   the measured indivisible heavy stages for Burst, rather than assume stage 1
   has already moved computation off the main thread.
