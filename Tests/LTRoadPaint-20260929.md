# Road paint locality and split CPU timings — 2026-09-29

## Scope

User diagnostic: completed edit cycle, geometry 304.3 ms, painting 1572.7 ms;
WeightBake 1537.3 ms and 9 weight bakes. These are CPU measurements, not GPU
time. WeightBake previously included spline projection, weight calculation,
texture upload and rock masks. The lower last-build report is a separate
operation record and must not be summed into that cycle.

This step improves the synchronous painter and instruments its actual costs.
It does **not** add painting Jobs, change resolution or change visual formulas.
The existing height/LOD Jobs and scheduling remain intact.

## Changes

- Road ground/wheel weights share one bounded nearest-segment query. The two
  individual query entry points remain available for reference/other consumers.
- Terrain chunk weights have separate dependencies from full road projection.
  Palette slots, stamp order, ordinary stamp masks/settings and local terrain
  filter dependencies still invalidate weights. Each changed chunk compares
  immutable old/new road snapshots before reusing its weight textures.
- Regional road comparison uses exact ordered candidate-segment inputs,
  independent of BVH grouping. No quantization, sparse output probes or new
  tolerance is used in the production reuse decision. Both old and new paths
  participate. The explicit bounds gate is compared conservatively; changes
  of bounds/settings may still cause extra safe bakes.
- Arc length remains a weight dependency for variation and endpoint fades.
  A changed total length is irrelevant only if the compared region is outside
  both old/new end-fade zones. Independent ground/wheel tiling is not a weight
  dependency. UVs deliberately retain their original whole-road invalidation,
  including downstream V shifts and shared-layer ownership at intersections.
- Dependency snapshots are committed only after a successful bake/reuse.
  Deletion/removal/reordering also changes the palette/stamp inputs. Rock paint
  keeps its existing full-bake policy; its count now increments inside Bake.
- Diagnostics add RoadProjection, RoadUVCompute, RoadUVUpload, WeightCompute,
  WeightUpload, weight reuse count and projection update count. Existing enum
  indices are preserved. WeightBake contains the new substages; they must not
  be added to it. UV counters/timings also include asphalt suppression maps.
  Upload reports CPU API time, not GPU completion time. UV-only/reuse-only
  updates now refresh the retained editor diagnostic too.

## Verification

- Full managed suite: PASS, `Logs/LTRoadPaint-20260929-managed-final.log`.
  New checks: 1731 region pairs, 571 reusable regions, 909819 exact pixel/pair/
  composite comparisons. Includes genuine painted-prefix reuse after a tail
  edit, downstream weight reuse with a required UV change after a head edit,
  variation, caps, wheel views, asphalt, disabled/unassigned wheel paint,
  independent tiling, move-out/deletion math, Undo comparison symmetry and
  ordered composites. Hot combined queries allocate 0 B in the managed test.
- Existing geometry, height/LOD, paint, road network, wheel/tiling and other
  managed/source-contract checks pass. These do not execute native texture
  uploads or actual scene Undo; production wiring checks are source contracts.
- C# project build: PASS, 0 errors, 6 warnings,
  `Logs/LTRoadPaint-20260929-csharp-final.log`. Warnings were not classified as
  pre-existing. This is not proof of native Unity import/rendering success.
- Earlier logs retained: managed-1 failed to compile an ambiguous Random name
  in the new test; managed-2 passed the new math checks but failed an outdated
  source-call literal assertion. Both were corrected; managed-3 and final pass.
- No new live-scene performance or graphics result yet. After script reload,
  move one road point and wait for completion; compare WeightCompute,
  RoadUVCompute, both Upload fields and reused/rebuilt counts. Also visually
  check Undo, independent tiling and road removal. Initial domain-reload cache
  warm-up is not a comparable edit benchmark. Select a future Jobs target from
  these measurements, not from the old aggregate WeightBake alone.

## Safety

No Unity process was started/stopped, project opened, scene saved, package
changed or validation directory created by the agent. No cleanup or links.
Logs are under Logs, not Temp. The working Unity installation and package
caches were read-only. Existing dirty code/generated assets were preserved.

Before/after hashes matched:

- TerrainTest.unity: 35C84DB5E423FF841AD28381E73591E700F97BE0E279868C814D921C91C2D3C1
- manifest.json: 3730D99B48B91296ADE8BDC1D4F47136EC341762D7351BDE6597D78610B82865
- packages-lock.json: B2294EA6BA2BEAE339614F9719CF6AD41A1D4AD03C7593790CA95558B584DADA
- GraphicsSettings.asset: 5AE8C26EA9E00803676E4B1065E2550769A125986DE123B58610478D1CB08C33
- QualitySettings.asset: 7BCBD98B0E668762CC8BF52D30FBFEB35A43690D6FFE173E4C478C14FB638031
