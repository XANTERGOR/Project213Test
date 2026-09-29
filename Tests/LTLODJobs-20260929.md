# LT background LOD coarsening — first Burst / Jobs stage

## Scope

Automatic per-chunk coarse LOD updates now schedule a real `IJob`, marked with
`BurstCompile` and strict floating point mode. Locked Burst 1.8.30 and Collections
2.6.8 are used without changing the manifest, lockfile or package sources.

- The existing mouse-release gate, LOD0-first publication and per-chunk input
  revisions remain. Only queued dirty/topologically changed chunks get jobs.
- Snapshot preparation reads heights and protected road/rock regions on the editor
  thread, yielding at 256 items or a soft 2 ms budget. Sorting, individual height
  queries, allocation/copy and mesh emission are still indivisible operations.
- Jobs receive only owned blittable NativeArrays: no scene objects, Unity textures,
  managed callbacks or shared editor caches. Allocator.Persistent supports more
  than four frames and waiting during dragging.
- One bottom-up error hierarchy is evaluated and shared across the requested LOD
  levels. A parent's conservative error and generation dominate its children's;
  maximal eligible nodes reproduce the old iterative merge result. Protected or
  incomplete branches cannot be merged. This is coarsening, not a different
  height-error policy or loss of seam/contact protection.
- The editor yields after Schedule, polls IsCompleted and only then calls Complete
  and reads results. Waiting does not spin through the 4 ms editor budget. Every
  continuation, including result reads/publication, retains the queue version guard.
- Cancelling an iterator retires its native job without waiting. Its result cannot
  be read/applied; owned buffers are freed after completion. The global maximum is
  two native jobs, including retired work. Rapid changes cannot grow an unbounded
  backlog. LOD0 is not gated by this limit. Reload/quit joins owned workers before
  unloading code; collection also runs before the play/compile Tick early returns.
- A job completion marker prevents publishing incomplete output. A BurstDiscard
  marker reports actual Burst execution versus the managed Jobs fallback in
  LTWorld Diagnostics. A cold/disabled Burst compiler still uses scheduled Jobs.

## Remaining main-thread work

This does **not** move the entire terrain pipeline into jobs. LOD0 generation,
Unity-dependent height sampling, coarse balancing, spatial layout, vertex/index
emission, mesh upload, painting and colliders retain their existing paths.
Manual rebuild/save uses the previous synchronous implementation. Coarse meshes
still publish together per chunk, after LOD0. No claim of measured live-scene or
end-to-end speedup is made.

## Verification

- C# editor build passed, zero errors; final incremental build has three existing
  dependency warnings. Initial full build had six warnings.
- Full managed TerrainBridgeChecks suite passed. New checks: 420 exact reference
  level comparisons with mixed trees, protection, pinned/free boundaries,
  rectangular chunks, unordered inputs and non-monotonic level settings; lazy
  cancellation, immutable snapshots, invalid input, empty chunks, zero-step levels,
  waiting/version rejection and production lifetime source contracts.
- The first managed synthetic 16,384-leaf / four-level sample reported 29.87 ms
  snapshot + 4.76 ms scalar hierarchy/selection versus 27.24 ms for the existing
  shared managed coarsener. This is a single cold-ish managed sample, NOT a Burst
  timing or a total speedup. The primary change is scheduling/responsiveness;
  snapshot cost remains on the editor thread in slices.
- No Unity project/process was launched, closed or modified for validation; no
  temporary validation project, cache links, package repair or scene save.
- TerrainTest scene, manifest/lockfile and Graphics/Quality settings SHA-256 hashes
  match the pre-edit baseline; existing dirty assets and scene changes preserved.
- Native Unity import, actual Burst compilation/execution, graphics, worker memory
  under real editor reload, and live-scene latency have NOT been verified here.

Logs: `Logs/LTLODJobs-20260929-managed-final.log` and
`Logs/LTLODJobs-20260929-csharp-final.log`. Earlier logs are retained (initial test
build failed on an ambiguous test-only Random type, then was corrected).

## Native / scene acceptance

1. After Unity imports the scripts, run **Tools → Local Terrain → Validate LOD Jobs
   (no scene changes)**. It schedules native jobs and compares 60 plans against
   the managed production coarsener; it also rejects retired scheduled results.
   It does not access scene objects or save/edit assets/settings. A warning with
   `Burst executed 0/12` means the native Burst backend is not yet validated:
   enable Burst or allow async compilation to finish, then rerun. The check is
   intentionally not auto-run when scripts import.
2. Move a road point and release. LOD0 should show first, without a progress modal;
   Diagnostics should eventually report `Burst Job` (or explicitly the managed
   Jobs fallback). Check both whole-chunk and within-chunk LOD modes.
3. While a chunk is queued, change it again and then edit a distant chunk. No stale
   coarse geometry should replace the latest LOD0. Inspect chunk borders, road
   junctions, rock cuts and mixed near/far LODs.
4. Test Undo/Redo, world disable, scene close, save and script reload with work
   pending. No native-array leak warnings or pending jobs after domain teardown.
