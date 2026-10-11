GPU detail CPU preparation optimization - 2026-10-10

Scope: CPU servicing of the existing Editor GPU-detail renderer. Placement,
quality settings, source LODs, density, shadow ranges and compute shader unchanged.
Deferred player streaming/performance/memory and quality-preset work is NOT resumed.

Implementation:
- Begin clears material validation and recipe/shadow-partition plans every camera
  pass, retaining dictionary capacity. No cross-frame material decisions.
- LTDetailRenderer opts into PrepareCameraPass directly before its synchronous
  group loop. Camera/projection/frustum/quality inputs remain immutable within it.
- Camera uniforms upload once per prepared pass; LOD uniforms when recipe changes.
  Group-specific distance, density, shadow range, counts and buffers still bind
  per group. Compute dispatch and draw order/count are unchanged.
- Compute/MPB property names use cached Shader.PropertyToID values.
- Callers using Begin + TryRender without PrepareCameraPass keep live per-call
  material, camera and recipe reads. A foreign camera invalidates prepared state.
- Production renderer still has no readback. Fallback counts remain per source.

Managed differential harness:
  dotnet run --project Tests/DetailGpuCpuChecks

Links actual production renderer/data/upload-budget, plus a frozen pre-change
renderer and command-recording Unity substitutes. Compares complete dispatch
inputs and draw payloads; it does NOT execute HLSL or measure native CPU/GPU time.
LegacyDetailGpuRenderer.cs is a test fixture outside Assets, not a second backend.
6680 assertions / 4842 command comparisons passed. Cases include 1..8 LODs,
all shadow modes, duplicated parts, mixed recipes/materials, camera changes,
frustum/projection/quality inputs, material edits, fallback, XR, upload failure,
foreign cameras, and direct callers that do not opt into a prepared pass.
Warm fixture: 24 groups, one shared material, two recipe/shadow plans.
Compute uniform writes: 1248 -> 1087; instrumented material API reads: 2498 -> 8.
These fixture counts are NOT milliseconds, a speedup percentage or FPS.

C# build against installed Unity references passed: 0 errors, 6 existing warnings
(2 assembly-version conflicts, 4 unused-field warnings). Logs:
  Logs/DetailGpuCPU-optimization-native-check-build.log
  Logs/DetailGpuCPU-managed-parity.log

Existing managed regressions passed:
  TerrainBridgeChecks --detail-gpu       layout / readback-free / source contracts
  TerrainBridgeChecks --detail-placement 30 data/payload checks
  TerrainBridgeChecks --detail-batch     batch editing + 3600 mask-scale samples
  TerrainBridgeChecks --editor-reload    297 policy checks
  TerrainBridgeChecks --mesh-stamp-cache 2034 assertions, 200 legacy fixtures

Native validation (PENDING user run; managed success does not imply native pass):
  Tools > Local Terrain > Validate GPU Detail CPU Preparation (no scene changes)
Small owned preview fixture, disabled cameras, read-only adapter load, no forced
import, no user camera render, no scene/asset save, no quality-setting changes.
Compares cached and uncached production paths: native selected instance IDs and
indirect arguments, LOD/shadow partitions, thinning, camera/mask/material edits.
Editor-only readback sorts append IDs, since GPU append order is unspecified.
Hashes scene/source/settings files before and after, checks scene dirty/root state.
This verifies native commands/selections, not rendered HDRP appearance or speed.

Live performance baseline (CPU recording, SceneView, GPU Details ON):
  Logs/FrameProfiles/flight-20261009-220422-181-3e649f8c.txt
Partial report: 351 analyzed / 501 received, 148 missing/unanalysed frames,
report marker limit reached. Sum only separate SceneView root paths, not nested
inclusive times: RenderCPU ~3.017 ms; TryRenderCPU ~2.020 ms (self ~1.882 ms);
BeginCPU ~0.111 ms; Tick ~0.336 ms. These are CPU markers, NOT GPU timings.
Mesh stamp capture: 145 hits, 20 audits, 0 cache-miss builds.

After native validation and terrain/vegetation settling, repeat the same 5-second
SceneView flight, Record GPU OFF, scene GPU Details ON, quality/LOD unchanged.
Compare RenderCPU + BeginCPU as well as TryRenderCPU: shared camera upload moved
to RenderCamera before the group loop (outside TryRenderCPU, inside RenderCPU).
Do not count that scope movement alone as a speedup. Compare frame counts, GC,
camera count, draw/dispatch counts and partial-report flags; a different route or
population is not a controlled A/B benchmark. No after-change timings yet.

Safety audit: 308 baseline scene/source/settings/package files checked. Only the
two intended render source files differ; zero missing files. Added a new native
validator and its unique GUID. No package cache, installed Unity or scene writes.
