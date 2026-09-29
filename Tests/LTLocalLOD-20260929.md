# LT per-chunk LOD ownership and local input invalidation

## Implemented

- Automatic updates still wait for mouse release. LOD0 is published as a coherent
  seam/rock-contact transaction, followed by independent coarse-LOD continuations.
- Each chunk has its own input revision. A changed road/density region cancels
  only matching coarse work. Unrelated work retains progress while the higher-
  priority LOD0 transaction runs, then resumes (not concurrent worker execution).
- Base transactions own a frozen requested-ID set. New unrelated dirty IDs do not
  enter that snapshot. Read dependencies include normal halos and whole affected
  rock contact regions. A superseded transaction requeues its output IDs, not all
  read-only neighbours. Global configuration changes still invalidate globally.
- A chunk with an owned pending coarse job is not treated as missing LOD data by
  subsequent base planning. Actual changed topology/stitches still replace it.
- Road diffs compare exact candidate BVH leaf samples and settings within each
  chunk, on both old/new footprints with the normal halo. This conservatively
  skips unchanged portions of one road. Arc-length-driven variations/end fades
  remain dependencies; a change can legitimately affect downstream chunks.
- WholeChunk coarse meshes balance in a one-chunk forest. LOD0's external border
  midpoint bits are preserved; internal transitions use the local coarse plan.
  WithinChunk uses the same spatial layout/emitter, with locally reconstructed
  base masks. Unchanged neighbours do not get new LOD geometry.
- Coarse publication uses current accepted seam normals. It does not rewrite
  LOD0 positions, update its timestamp or invalidate paint/detail geometry.
- Queued jobs are lazy. Only one chunk's height cache/coarse output is active;
  pending snapshots retain their base plan, boundary bits and local stamps. Their
  validators are created outside the base iterator to avoid retaining its world-
  sized forest/pending mesh arrays through a compiler-generated closure.
- Generated LOD imports no longer cancel all jobs via `projectChanged`. Cached
  asset dependency hashes refresh on that event; actual source/height-mask/mesh
  changes enter input signatures. Replaced generated objects/assets are rejected.
- Save/manual rebuild, disable, Undo, script reload and failures retain safe
  barriers. They may conservatively discard unfinished work. No scene save is
  performed automatically by this feature.
- Diagnostics expose pending chunk IDs and completed/discarded counts per session.

## Limits

This is the agreed local scheduling stage, **not Burst, Jobs or background work**.
Coarsening and emission still have indivisible main-thread stages. The 4 ms budget
is soft. LOD0 planning/border consistency still traverses the cached world topology;
only dirty output and propagated changed neighbours get regenerated geometry.
Neighbour-dependent LOD0 preparation can restart as a group. Coarse levels publish
together per chunk, not one level at a time. Manual/save barriers remain synchronous.
No live-scene speedup, native import correctness or save/reload visual result is
inferred from managed tests.

## Verification

- Full managed TerrainBridgeChecks suite: PASS. New checks include independent A/B
  progress and versions, 100 superseding edits, failures/lifecycle barriers, base
  read dependencies, 256 lazy jobs with one live working cache, 144 exact legacy
  mesh/normal/UV/index comparisons (irregular neighbours, three levels and cuts),
  local spatial base-mask parity, and 15,028 road height/density comparisons in
  reused regions. The existing mixed spatial LOD/watertight tests also pass.
- C# editor project build: PASS, zero errors. Final incremental build reports
  three dependency warnings; first full build reported six existing warnings.
- `git diff --check`: PASS.
- TerrainTest scene, locked packages and Graphics/Quality settings hashes match
  this turn's baseline. Existing dirty scene/generated assets were preserved.
- No Unity process launched/closed, native graphics test, scene save or live-scene
  performance benchmark was performed.

Logs: `Logs/LTLocalLOD-20260929-managed-final.log`,
`Logs/LTLocalLOD-20260929-csharp-final.log`. Earlier failed managed runs stopped on
old source-shape assertions, which were updated for the new production paths;
their logs are retained.

## Manual acceptance

1. Move a road point and release: LOD0 appears first; Diagnostics lists queued LOD
   chunk coordinates. Unchanged areas should not acquire fresh mesh output.
2. While coarse LODs remain, edit a distant chunk. Its old request is discarded;
   other queued work remains. Local geometry/normal seam dependencies are allowed.
3. Repeat near a chunk corner, a rock cut and a junction in both LOD modes. Check
   mixed near/far levels for cracks and check that normal-only neighbours retain
   their LOD geometry.
4. Test Undo/Redo, world disable/enable and save with outstanding work. These are
   conservative lifecycle barriers, not ordinary local point edits.
