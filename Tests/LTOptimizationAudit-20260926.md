# LT code/performance audit — 2026-09-26

Scope: road authoring/math and terrain planning, paint/cache invalidation, texture
array ownership/packing, vegetation preparation/GPU upload and deformation update
paths. This is a code audit plus managed regression tests, **not** a complete
native Unity profile, graphics check, packaged-player test or GTX 1660 benchmark.
Scene settings, density, texture resolutions and tessellation quality were not reduced.

## Implemented

1. **Bounded road influence queries.** Height, paint and detail-clearing queries
   start nearest-segment traversal with a conservative influence radius, not
   infinity. Empty space within a curved road's large bounding rectangle no longer
   requires finding an irrelevant distant segment. Offroad flatten=0 skips height
   queries. Public nearest/UV queries remain unbounded (rounded endpoint texture
   continuation must not change).
2. **Path-scoped terrain invalidation.** Changed/removed old roads and changed/new
   roads mark intersecting path chunks, not every chunk in their rectangular bounds.
   Height invalidation includes the existing normal-sampling halo; density zones
   carry their immutable road snapshot. Reordering remains conservative. Existing
   forest balancing may still propagate seam topology into additional neighbours.
3. **Asphalt inputs captured once per paint tick.** Terrain and rock projection
   bakes reuse validated snapshots rather than re-enumerating the scene hierarchy
   for every chunk. Per-chunk filtering and invalid-road handling are retained.
4. **Tick-local paint change inputs.** Serialize each active stamp's filters once;
   compute each palette layer's scalar/texture hashes once. Append the same values
   in the same order as before for surface/coverage/density fingerprints. Snapshot
   lifetime is one poll, so Undo, transform edits and texture imports are not hidden
   behind a long-lived cache. Rocks share layer fingerprints too.
5. **Local terrain dependencies for filtered paint.** Height/slope/curvature paint
   uses nearby tile content hashes, including maximum enabled curvature radius,
   world clamping and a conservative one-tile guard. Distant terrain changes alone
   no longer invalidate all filtered weight maps. Global signatures remain for
   consumers that need them. Displacement density's conservative union keeps its
   independent global authoring invalidation (see remaining costs).
6. **Seam-normal cache correctness.** A normal-only update on an otherwise unchanged
   neighbour now advances its CPU sampling revision. Cached slope filters and detail
   placement must see the new normals even when topology was reused.

## Verification

- `dotnet build Assembly-CSharp-Editor.csproj --no-restore -v:q /clp:ErrorsOnly
  /p:CustomAfterMicrosoftCommonTargets=G:/UnityProjects/Project213-Testing/Tests/LocalTerrainCompile.targets`
  — passed, zero errors (six warnings on the full build; three on the final incremental build).
- `dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj --no-restore`
  — full managed/source-contract suite passed. New road checks compare exact output
  against pre-optimization unbounded query formulas: 811,717 assertions including
  solid/tracks/asphalt, noisy edges, banks, endpoints, offsets, old/new dirty regions
  and normal halos. Existing road allocation test still expects zero query allocations.
- New paint checks cover 364,500 centre/curvature dependency probes across rectangular
  and single-tile worlds, world clamping, chunk edges and oversized radius, plus
  source contracts for local signature wiring and per-tick cache refresh.
- Synthetic managed CPU example: 65,536 height queries / 4,097 road samples,
  alternating-run medians across repeated runs approximately **141–160 ms -> 8–9 ms**, identical checksums.
  This deliberately measures queries across a large sparse road bounding rectangle;
  it is **not** a prediction for every road or total editor rebuild time.
- Moved diagonal fixture: **256 -> 57** directly dirty chunks, before balancing/seam
  propagation. Local curvature dependency fixture: **25 of 256** tile hashes.
- Tests caught and corrected an ambiguous test `Random` reference and updated a
  source contract for the expanded rock-paint call. A synthetic road at ~800 km
  hit the existing spline precision/spacing guard; equivalence fixtures use valid
  0/4 km offsets. Large-coordinate spline robustness was not changed in this patch.

## Remaining costs / cautions from the audit

This section records the first pass. The follow-up below supersedes its textual
fingerprinting and full rectangular UV-work items; other cautions remain applicable.

- Geometry rebuild still completes synchronously: planning, balancing, mesh emission,
  seam/rock validation, native mesh uploads and collider cooking. Narrower dirty regions
  reduce the work but do not create a frame-time budget. Incremental publication would
  require a transactional seam/collider strategy; do not publish partially compatible
  chunks or weaken validation just to shorten a frame.
- `LTPaintTerrain.Update` constructs textual geometry fingerprints when tile content
  changes; large vertex counts can allocate substantial temporary strings. Converting
  this to a binary content hash is a separate measurable optimization; timestamps alone
  would incorrectly hide content changes or trigger endless refinement/rebake cycles.
- Displacement occupancy/density uses a global authoring revision to reset conservative
  coverage unions. It can still rebuild beyond the road area. A future scoped revision
  must include all affected geometry/filter/neighbour dependencies; simply removing
  that revision can retain stale refinement after deletion or coarsening.
- Weight-map generation still uses CPU texel loops and native `GetPixelBilinear` mask
  reads. A managed/GPU replacement needs sampling-convention equivalence, masks,
  height/slope/curvature, ordering, readback and lifetime tests. Do not change resolution
  or sampling convention silently.
- Spline UV maps retain their guarded rectangular bake region. Curved sparse paths
  still have empty interior texels; tighter regions need to preserve the bilinear UV
  footprint at caps and bends. Asphalt suppression also queries the full map grid.
- Array packing already fingerprints inputs and reuses owned/borrowed arrays; scalar
  layer changes must not force repacking. Native saved-asset/GPU validation is separate.
- GPU detail upload already uses budgeted allocation/staging and retained buffers.
  CPU surface/preparation and prefab dependency checks remain, so “GPU drawing” does
  not mean all generation work is GPU-only. No new player streaming/memory tests or
  vegetation quality presets were added (deferred by the user).
- Deformation already tracks active/dirty regions. Vehicle-contact behavior and player
  performance remain outside this editor-road optimization pass.

## Live verification still required

After Unity recompiles, move one point on an existing curved road and inspect
**LTWorld → Диагностика → Last mesh update / build timings / collider update**.
Compare a repeated edit of similar size, not the first post-domain-reload rebuild.
Check Undo, removal, chunk-edge normals, slope/curvature paint and vegetation contact.
The painting CPU capture on the Textures tab distinguishes change checks, terrain
cache and weight bakes from mesh generation; its idle capture is not total road-edit
latency. No user scene was saved, Unity process launched/killed, package version
changed or validation directory created for this audit. No shader source changed.

## Follow-up: second optimization pass

Implemented without changing authored settings, topology or texture resolution:

- Replaced the per-tile formatted-float geometry string with a streaming, versioned
  two-64-bit content fingerprint. It still incorporates transformed vertices,
  available normals, triangle order and counts; normal-only edits remain observable.
  Hashing does not allocate per word. This is a non-cryptographic cache key, not an
  integrity/security primitive. The small aggregate world signature remains unchanged
  in structure. The new tile-key format invalidates old cache signatures once; saved
  global color/normal maps can show a stale-bake warning until rebaked. Saved layer
  texture arrays do **not** need repacking for this change.
- Displacement bakes share CPU mask readbacks within one paint tick, keyed by the
  actual immutable `ReadMask` copy. Cache retention is capped at **64 MiB**; oversized
  or overflow masks use the normal uncached path, with no downsampling. The cap is
  for cached reuse, not total displacement-bake memory. All references are dropped
  after the tick. Imports/replacements cannot reuse an earlier tick's cached pixels.
- Displacement height sampling now avoids road-UV work for texture-free and
  zero/negligible-weight slots. It uses the same existing sample threshold/default
  height and keeps all blend, deformation-control and occupancy calculations.
- Road UV generation visits conservative 8x8 blocks near the path instead of every
  texel inside the large road rectangle. Two-texel padding retains interpolated paint
  coverage, all four UV/Jacobian corners, caps and chunk borders. Unused texels remain
  zero as in the previous guarded rectangular bake. GPU storage/resolution unchanged.
- Asphalt displacement suppression uses the original formula and cell-diagonal
  guard with a bounded nearest search. It is not clipped to the height/paint bounds:
  padding may exceed those bounds. The full output map is still written.
- Added opt-in `DisplacementBake` timing to the existing painting CPU report. Stage
  arrays/report loops now use the stage count rather than a hardcoded eight. New
  captures go to `Logs/LocalTerrainCpuBenchmarks`, not Unity's `Temp`; old logs remain.

Validation:

- C# editor build passes with zero errors (six warnings); full managed/source-contract
  suite passes, including **907,446** new checks. No native Unity import/render test
  or live-scene timing was performed.
- Sparse UV fixtures cover solid/tracks, 33/65/257 grids, curved/banked path, unequal
  chunk dimensions and both endpoints. They assert that every corner of any cell
  with nonzero interpolated paint coverage retains exactly the full-map UV/right;
  this also preserves the shader's four-corner Jacobian.
- Bounded suppression is compared exactly to the old unbounded formula for random
  points, points near the path, shoulder=0, small shoulders, different padding,
  footprint edges and endpoints; query allocation check remains zero.
- Geometry keys are checked for determinism, culture independence, position/normal/
  index changes, order/length sensitivity, exact float bits and zero streaming
  allocations. A 20,000-vertex synthetic fixture measured **9.31 ms / 7,667,912 B**
  for old string serialization alone versus **0.60 ms / 208 B** for the new hash.
  The old native Hash128 call, mesh extraction and total road build are excluded.
- The long-road UV fixture retained **12,832 of 132,098** full-grid queries across
  two patterns. This is a work-count comparison, not an editor FPS or road-time claim.
- Mask-readback lifetime/budget and timing integration have source-contract checks;
  actual Unity readback reuse, peak memory and timings remain native validation work.

Still intentionally unchanged: synchronous mesh/collider publication, global
authoring invalidation for conservative density unions, and the existing terrain
mask sampling convention. Do not remove those safeguards or move Unity object work
to background threads without targeted correctness/lifetime validation. Deferred
player/vehicle vegetation tests and quality presets remain deferred.

## Follow-up: repeated stalls after moving a road point

The user reports three approximately 17-second editor stalls after moving a point.
Source inspection found a world-wide authoring key in every chunk's displacement
coverage key, plus independent geometry and painting update callbacks. The exact
allocation of the reported 17 seconds has not been measured in the live scene.

Changes superseding the global-authoring limitation above:

- Authoring revisions are now per terrain tile. The existing conservative old/new
  footprint detection (road BVH plus normal halo) touches revisions on move, removal,
  addition, reorder and Undo. Curvature consumers include their probe radius and
  neighbour guard. Configuration changes still invalidate the entire world.
- Density-only/generated-mesh updates do NOT touch authoring revisions. Conservative
  coverage union remains enabled until a relevant authoring/paint input changes;
  refinement is not simply frozen after its first pass. This preserves coarsening
  after deletion and avoids self-resetting refinement loops.
- Automatic editor worlds have one paint owner, after geometry rebuild. ExecuteAlways,
  detail preparation and the independent paint callback defer to that owner. Idle
  paint polling remains throttled; a coordinated update bypasses the runtime throttle
  so a quick mesh rebuild cannot accidentally consume old coverage.
- Coverage is checked immediately after painting, before automatic collider cooking.
  Intermediate refinement meshes no longer cause a collider cook on every pass.
  Manual Refresh/save semantics, explicit global bakes and runtime updates are not
  converted to an asynchronous transaction. No scene/settings/assets were edited.
- LTWorld Diagnostics now retains automatic update-cycle mesh/paint pass counts,
  accumulated CPU stage times and pending chunk count. It excludes waits, detection,
  explicit manual refreshes and rendering. Follow-up refinement is still possible.

Validation: C# editor build and full managed regression suite; added regional revision
tests (old/new/deletion-equivalent touches, repeated edits/Undo, configuration reset,
curvature, clamping and world resize) and coordinator source-contract checks.
The one-tile synthetic fixture resets 16/256 consumers including conservative halos,
not all 256. This is neither an actual road footprint count nor a scene timing.
Native import/rendering and the user's three-stall interaction remain unverified;
compare the new Diagnostics cycle after moving the same point. Do not promise a
single pass or a particular speedup. No quality/resolution reductions were made.

## Follow-up: measured painting bottleneck (user screenshot)

The supplied live Inspector capture reports one geometry pass (525.7 ms), one
painting/mask pass (9135.6 ms) and colliders (19.9 ms). Bridge/weld work (167.1 ms)
and shared-normal stages (9.7 + 8.7 ms) are included in geometry, not additional.
The following changes target that measured split, without claiming new scene times:

- A bake-local, pixel-lifetime terrain cache shares centre height/slope and curvature
  across layers with the same effective radius. World-edge radius shrinking, holes,
  failed probes, lazy filter evaluation and exact formulas are preserved. Four equal
  curvature filters use five surface queries instead of seventeen. No persistent
  per-layer image cache or texture downsampling is added.
- Terrain lookup bins adapt from 16 to at most 128 per axis with triangle density.
  Triangle order, legacy coarse-bin footprint and barycentric tolerance at new bin
  edges are preserved. The dense fixture compares exact winner/height/weights,
  including duplicate faces and holes: 942,983 candidate checks become 166,191.
  This counts query work; index build/native mesh read time is not included.
- Road paint selection excludes empty parts of a curved road's bounding rectangle
  using its BVH influence and a two-texel guard. Old slots still invalidate correctly
  when the road leaves. Masks within a selected dirty chunk are still rebuilt in
  full: per-pixel incremental mask updates have NOT been introduced here.
- Seam contributions are cached from geometry per chunk, not smoothed normals.
  Per-face accumulation order is retained exactly, with full global contributions
  still available for rock validation. Mesh/dirty-version/transform/rect changes
  invalidate entries. Normal writes visit changed chunks and their one-ring halo.
  Proposed cache data is not committed on validation-only/failed preparation.
- Owned rock outputs can be reused when authoring/config/source/output versions
  match and neither proposed nor externally changed geometry reaches their neighbour
  region. Explicit validation bypasses reuse. Restores are not cached. Contact-contour
  preparation and initial stamp capture remain conservative full scans.
- Diagnostics now retains the last working painting pass by stage, including rock
  materials. Detailed CPU sampling is automatic only for coordinated editor updates;
  runtime remains opt-in. WeightBake includes rock masks inside RockMaterials, so
  those stage values must not be added together as independent costs.

Validation: C# build passes (0 errors / 6 warnings); managed regression/source-contract
checks pass, including cached curvature, adaptive lookup, incremental seam contributions
versus exact full accumulation over 40 edits/cuts and discarded-proposal isolation.
Native renderer/asset lifetime and the user's new road timings are not measured by
these checks. User scene and pre-existing generated mesh changes were preserved;
no save, editor restart, package change or standalone validation project was requested.
