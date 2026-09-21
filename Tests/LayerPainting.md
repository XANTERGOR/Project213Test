# Eight-layer painting prototype

In LTWorld / Текстуры, assign a base Surface Layer and enable painting. Add child
Layer Stamps with Surface Layers. Later stamps in depth-first hierarchy order
paint over earlier stamps. Disabled stamps stay in the registry but do not paint.
Eight distinct layers per chunk includes the base. Duplicate assets share a slot.
Overflow restores the original material on that chunk and reports its coordinates.

LTWorld's `Облегчённый фон` defaults on: slot 0 uses Albedo and optional Normal,
ignores Mask Map, fixes AO=1 / blend height=.5, and uses scalar layer Smoothness
and Metallic. Normal strength zero skips normal sampling. Since duplicate layers
share a slot, stamps using the same asset as the background also use this mode.
Turn it off to restore full background mask controls; source assets are not edited.
Chunks with only the background use `_LTBaseOnly`: no two coverage texture reads,
eight-layer mixing or height blending. With light mode this needs at most two
background texture samples (one without normal), outside the far transition.
Shared bake code uses identical semantics; re-bake stale distant maps manually.
The final atlas is NOT reused as an editable background, so moved stamps do not
leave baked ghosts. Eight slots including the background remain unchanged.
Validate Eight Layer Shader checks atlas coordinates in both normal and base-only
paths. Verify normals, stamp edges, switching light mode, and manual bake in Unity;
C# tests do not compile or measure the shader. The GPU 1/4/8 stress benchmark keeps
the general full-layer path for comparable results, not this background-only path.

Color, normal and RGB (R AO / G height / B smoothness) maps are bound directly;
no texture arrays or source texture conversions. Normal maps need Unity's normal
map import type; RGB masks and stamp masks should have sRGB disabled. Stamp masks
use R and do not need Read/Write. Metallic and missing-map smoothness are scalar.

Coverage is currently 257x257 per chunk (two linear RGBA8 textures). Endpoint
texels and sampling coordinates match across chunk borders. Only coverage-affecting
changes regenerate those maps; layer appearance updates just rebind material data.
The editor polls at 150 ms intervals. Painting does not rebuild terrain meshes.
Automatic painting updates in Edit Mode wait while a scene handle or inspector
control owns GUIUtility.hotControl. Moving/rotating/scaling a Layer Stamp keeps
the previous weights during drag, then the next poll updates on mouse release
or cancellation. Inspector slider drags are also deferred; Play Mode and explicit
manual global baking are unchanged. Check drag across a chunk boundary, release,
Escape cancellation and Undo/Redo in Unity.
CPU copies of stamp masks are cached and invalidated by imageContentsHash.

Layer Stamp / Noise optionally multiplies coverage by deterministic two-octave
value noise. Size is stamp-local metres (pattern follows translation/rotation/scale),
Strength blends between original and broken coverage, Threshold removes increasing
areas, Softness smooths the threshold, Seed changes the pattern. Disabled by default.
It reveals underlying layers throughout the interior, not just the outer falloff.
Noise uses the same stamp-local coordinates across chunks, combines with texture
mask and terrain filters, and does not sample terrain unless those filters are on.
CPU noise runs only when coverage is rebuilt, after editor mouse release; no new
shader samples or per-frame noise. Parameter edits invalidate weights and saved
global signatures. The existing 257x257 per-chunk coverage limits fine detail.
Check interior breakup, crossing chunk seams, move/rotate/scale, Undo/Redo, zero
strength, disabled mode, and manual global bake in Unity. Reusing the same layer
underneath cannot reveal a different material, even when the noise removes coverage.

This first implementation targets mesh terrain chunks, including their LODs,
not trimmed rock meshes. Projection is world-relative XZ, not triplanar. Height
blending affects material weights, not displacement. Flow filters are not implemented.
Generated resources are transient and restored on
disable; original materials are restored before scene save. Runtime reconstructs
the painting from serialized layer/stamp data. This is not a baked release pipeline.

Verification:
- `dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj --no-restore`
  tests production coverage/compositing math plus existing geometry regressions.
- If Unity has not regenerated csproj files, use
  `dotnet build Assembly-CSharp-Editor.csproj --no-restore /p:CustomAfterMicrosoftCommonTargets=G:/UnityProjects/Project213-Testing/Tests/LocalTerrainCompile.targets`.
- In Unity, `Tools / Local Terrain / Validate Eight Layer Shader` synchronously
  compiles default raster passes and reports compiler errors. This is separate
  from the C# build and does not validate every HDRP keyword combination.
- Visually check base-only, eight overlapping different tints, duplicate layers,
  disabled/reordered stamps, ninth-layer overflow, border crossing, normal maps,
  texture replacement, save/reload and toggling painting off.

Shader compilation and visual checks require Unity; console math checks alone
cannot verify the final HDRP rendering.

## Global distant maps (GPU-baked prototype)

### Stamp terrain filters

Layer Stamp has Height / Slope / Curve toolbar tabs, each with an independent enable
checkbox, Min–Max slider and feather outside the selected full-strength range.
All filters default off; enabled filters multiply stamp coverage and mask R.
Height uses LTWorld-local metres, slope uses interpolated LOD0 terrain normals
(0–90 degrees). Curve is signed, radius-normalized height Laplacian clamped to
[-1,1]: negative hollows, positive ridges, zero planes. Radius is in terrain metres.
The sampler reads generated LOD0 triangles, including height-stamp deformation;
it does not depend on collider updates, camera LOD, or material normal maps.
Triangle bins are cached by chunk mesh/rebuild revision and transform. Terrain
changes invalidate filtered coverage; disabled filters do not incur terrain sampling.
Curvature samples cross chunk boundaries. At the outer world edge the radius shrinks,
with zero curvature at the edge itself. Missing/cut terrain samples reject coverage.
Read/Write must remain enabled on generated LOD0 meshes used by the sampler.
Filtered weights are also used by manual global baking. Saved snapshot signatures
include filters and sampled geometry. Changing terrain or filters marks maps stale.

Unity checks: plane/slope/hill/hollow, stamp spanning four chunks, move height stamp,
Undo/Redo filter edits, switch camera LOD, disable all filters (old result unchanged),
and manually bake/reload with active filters. CPU tests cover range feathers,
curvature sign/planar invariance, and triangle interpolation; not native rendering.

LTWorld / Текстуры / Глобальные карты вдали enables a shared pair of linear RGBA8
RenderTextures, default 2048x2048, Clamp + mipmaps. The old base slot is now labeled
Фоновый слой. Near and baker shaders share LTLayerBlendCore.hlsl, including layer
tiling, tint, mask controls, stamp weights and height blending. Baking is manual:
LTWorld / Текстуры / Запечь в память (без сохранения) requests one full bake. Editing stamps
or textures updates near rendering but only marks the atlas stale; the last baked
result remains visible far away. Without a bake, rendering stays detailed.
Texture content hashes mark the snapshot stale after source texture reimports.

Normals are encoded XYZ normals in the common terrain UV tangent frame, NOT
Unity-imported BC5 normals and NOT baked world-space geometric normals. The shader
decodes/normalizes them and applies them through the terrain tangent basis, just
like detailed layer normals. The underlying mesh supplies the geometric normal.
Global Albedo is unlit linear color; AO, metallic and smoothness are not baked.

Default transition is 150–300 metres from each rendering camera. The transition
blends material attributes; completely distant pixels skip detailed sampling.
Fully distant chunk bounds select the `_LT_FAR_ONLY` shader variant per camera,
which compiles out the detailed blend call. Near/far use the same HDRP passes and
geometry. Far AO is 1, with user-selected scalar smoothness and metallic.
If any chunk exceeds the layer limit or the atlas is incomplete, far mode is
disabled to avoid showing stale/uninitialized tiles. Painting benchmarks bypass
global maps and freeze per-camera material selection.

Use `Запечь и сохранить глобальные карты…` outside Play Mode to bake and save one
LTGlobalBakeAsset containing linear Albedo and XYZ Normal texture sub-assets with
mipmaps. A unique asset path is used; earlier snapshots are never overwritten.
The asset is assigned to LTWorld; save the scene separately to persist that reference.
On scene/domain reload the saved pair is bound without an automatic bake. In-memory
bakes take precedence until painting is reset; saving switches to the persistent pair.
Editor signatures detect layer settings, texture reimports, stamp order/transforms,
and baker shader changes. Stale maps remain visible until a manual bake; incompatible
world layouts disable far maps. Player builds load snapshots but do not evaluate the
editor asset signature. Original materials are restored for scene serialization.
At 2048 the GPU pair
with mipmaps takes approximately 43 MiB; 4096 takes approximately 171 MiB.

Validate Eight Layer Shader now checks default near, far and both bake passes.
Also visually check an asymmetric layout (different corner colors), non-square
worlds, chunk borders, mip transitions, changed/deleted stamps, replaced textures,
two cameras at different distances, toggling globals and scene save/reload.
Actual GPU bake orientation and rendering still require Unity verification.
For persistence, save an asymmetric bake, save/reopen the scene, and compare near/far
colors and normals. Edit a stamp: near must update, far must retain the saved result
and report stale. Save a new bake: the old asset must remain intact. Cancel the save
dialog: neither the bake nor the assigned snapshot should change.

## GPU comparison tool

### Experimental per-layer geometric displacement

Surface Layer / Displacement enables raw Mask Map G displacement:
`(G - center) * amplitudeMetres`, multiplied by the same normalized height-blended
coverage as color. Disabled/missing-mask layers contribute zero offset. It is
independent of height contrast/offset used to choose blend weights. Light background
never displaces; turn off lightweight background if displacement of slot 0 is needed.
Existing assets default to displacement off. LTWorld has a master switch, factor
(default 8, capped 32), fade start/end (15/50 m), and seam fade (2 m).

LTEightLayersTessellation is based on the installed HDRP LitTessellation shader.
Domain samples original mask textures at mip 0, with shared clamp-weight/repeat-mask
samplers. Raster depth, shadows, motion, forward and GBuffer use the same domain
modification. Primary-camera distance is used for consistent shadow displacement.
Pre-displacement domain position is carried in otherwise-unused vertex color for
differential geometric normal correction; authored vertex colors are not supported.
The mesh and MeshCollider are NOT changed. Ray tracing/path tracing and baked GI
see original geometry; the first implementation targets raster rendering only.

Only chunks with enabled displacement layers use this shader; outside the fade
distance the per-camera renderer switches to a normal non-tessellated material.
Far atlas always uses the normal shader. Displacement ends by the global map start
distance. Conservative renderer bounds and shader frustum epsilon include amplitude.
Bindings/copies refresh on changes, not every idle poll. Terrain shader changes do
not require altering source texture assets or editing/saving the user's scene.

Important limitation: different adaptive chunk edges/palettes are protected by
smoothly reducing displacement to zero at every chunk boundary. This avoids differing
offsets at the join but creates a strip with reduced detail. It is not cross-chunk
continuous displacement. Tessellation subdivides existing triangles only, so very
large terrain triangles need denser base geometry for small stones. Factors can
grow triangle counts roughly quadratically; start with 0.1–0.2 m and factor 4–8.

Adaptive refinement has two stages. Editor source-mesh density zones now query the
composited displacement coverage, including Noise, stamp masks, Height/Slope/Curve
and upper-layer occlusion (default cell size 2 m, minimum 0.5 m). Empty parts retain
the ordinary terrain adaptation; existing forest balancing/stitching and budgets
remain in charge. The inspector toggle is `Уплотнять по покрытию слоёв`.
An immutable summed-area grid answers region queries in constant time. A one-cell
coverage halo and balanced neighboring cells intentionally retain extra geometry.
Coverage samples the normalized texture-height blend, using the same interpolated
257-endpoint paint weights and mip-0 mask G as the domain shader. It now measures
the summed visible weight of enabled displacement layers, NOT signed offset or
albedo brightness. Refinement starts above 50% final weight: other-layer-dominant
dark islands stay unrefined. Domain displacement uses the same mask with a smooth
50–65% transition. Even zero-height points inside the visible layer retain density.
The previous 2–4 mm amplitude cutoff has been removed. Layer colors and their
blending are unchanged; this threshold controls extra geometry/displacement only.
The coverage rectangle is cropped to displaced stamp bounds plus weight-filter
padding, clamped to the chunk. Its power-of-two grid targets 0.125 m cells, capped
at 1024 cells per axis (large footprints therefore have coarser precision).
This is sampled coverage, not an analytic guarantee for sub-cell features; tiny
islands can still be missed. Original paint/noise maps remain 257 endpoints: this
change does not restore noise already lost when those maps were baked.
A non-lightweight displaced background can
cover a whole chunk. Lightweight background never contributes.

Slope/Curve may change slightly when the source mesh is refined. Within one
authoring revision the CPU density grid unions subsequent coverage instead of
alternating refinement/coarsening. Paint input, active displacement slots or source
geometry edits reset that union. Density is keyed by grid content, not generated
mesh revision. Snapshot queries never read back the GPU or scan entire grids.
After painting updates, detection dirties affected chunks and topology follows
Auto Update / Rebuild After Edit. Manual worlds may need Apply Pending Changes
after the updated coverage is available. Refinement changes topology, NOT the
terrain height function or collider displacement.

Validate in Unity: use one displacement stamp with Noise at full strength and
large holes; inspect wireframe inside/outside the holes. Cover half with an opaque
non-displaced layer, then move/delete it, disable displacement, Undo/Redo, edit
Slope/Curve and test across a chunk corner. Old areas should coarsen, narrow islands
remain and idle mesh updates settle. Console tests cover rectangle queries, tiny
islands, empty grids and immutable monotonic filter-feedback coverage. Native mesh
appearance and FPS still need a Unity check. Sharp facets can also arise because
domain displacement reads mip 0 at finite vertex spacing; coverage optimization
alone does not fix height aliasing and is not claimed to do so.

GPU hull factors use the same cropped, variable-resolution displacement occupancy
pyramid. Each cell unions four visibility samples. Mip R stores maximum occupancy,
G stores minimum (fully occupied). Hull queries now traverse the mip hierarchy,
rejecting nodes with a separating-axis triangle/rectangle intersection test, and
skip empty nodes. Fully occupied nodes terminate immediately; mixed nodes descend
to intersecting leaves. Edges use degenerate triangles with canonically ordered
endpoints, never the opposite vertex. A white cell merely inside the AABB no longer
activates a triangle/edge outside that cell. Empty regions get factor 1, not 0.
The one-base-cell halo remains; it is not expanded by coarse mip size.
DFS uses 40 stack entries (depth <=10) and at most 256 visited nodes per query.
If the budget is exhausted the unresolved query conservatively refines; this can
still cause extra geometry on highly complex masks. Queries are skipped when HDRP
already requests factor 1. This is more GPU work than the old four-sample query;
native GPU performance must be measured before treating it as an optimization.
A triangle genuinely crossing both layers still subdivides as one hardware patch;
this does not split its base topology exactly along the mask contour.
CPU reference tests cover black holes, off-edge/off-triangle white islands, winding
and 2000 comparisons against a full leaf scan (zero fallbacks in that fixture).
They do not execute HLSL. The pyramid rebuilds
on coverage, layer settings, mask texture or Height-blend changes; CPU-readable mask
copies are cached, with GPU readback only when a source texture changes. Camera distance
and screen-size factors are still HDRP's. This does not promise fine detail smaller
than the coverage/mesh resolution. Cross-chunk displacement still fades at borders.
The tessellation shader explicitly requests ATTRIBUTES_NEED_TEXCOORD0 before pass
varying declarations: hull coverage needs UV0 even in passes which do not use color
textures. Validate all default raster passes, including depth/shadow/debug, after
changes to shader attributes (missing DS UVs cause invalid texCoord0 subscripts).

The separate `Тест: Hull возвращает 1` toggle is a transient execution probe.
Keep the normal tessellation factor at 8, mask visualization off, and compare the
same near-camera Wireframe view with this probe on/off. `_LTForceHullOne` returns
edge and inside factors 1 ONLY from LTAdaptiveHullConstant, before coverage queries;
HDRP-culled patches remain culled. It does not change `_TessellationFactor`, source
geometry, displacement settings, saved assets or atlas bake content. Flat/far
materials do not run the hull stage; the inspector prints the current shader,
actual material probe value and ordinary factor for each displacement chunk.
The material flag alone is not proof that Wireframe executes this hull stage.
Disable after testing; domain reload also resets the nonserialized toggle.

For coverage diagnostics use LTWorld / Текстуры / `Показать маску уплотнения`
in Shaded mode (not Wireframe). This displays the actual GPU occupancy texture at
mip 0: white occupied, black empty/outside the cropped bounds, also black on chunks
without active displacement. It deliberately excludes conservative rectangle/mip
expansion, distance fade and CPU filter-feedback unions: it is the input mask, not
a visualization of final triangle density. Near/flat/far materials share the mode.
The temporary nonserialized toggle only changes debug material bindings; it does
not dirty density or global bake state. The atlas baker uses the blend core, not
this debug surface path, so baked textures remain unchanged. Disable the toggle
to return to normal materials. Validate black/white mask vs the two-layer color
view from the same camera; then compare factors 1 and 8 in Wireframe separately.
`Контрольный цвет (без маски)` sets debug mode 2 on near/flat/far painted materials,
including non-displaced chunks. It returns constant cyan base color and emission
from the same diagnostic output branch, without sampling coverage or using its
coordinates. Cyan is deliberately distinct from Unity's magenta error material.
The toggle is transient, does not change geometry or bake data, and returns to
mask mode when unchecked. Test in Shaded; native shader errors must be checked
separately. A constant color not appearing does NOT establish that the mask is
empty; it indicates that the diagnostic render path still needs investigation.
The inspector now selects these modes using `Режим диагностики`. The third mode,
`Текстура — экранные плитки`, sets mode 3 on chunks with an active coverage map.
It samples that same texture at explicit mip 0 using frac(screen pixel / 256),
without mesh UVs, crop bounds, terrain transforms or camera-relative conversion.
White samples use base color 1 and emission 10, comparable to the cyan control.
Other chunks stay in the empty-mask mode. Repeated silhouettes confirm texture
readback through this render path, NOT correct world mapping or tessellation.
Screen-space mode is a raster diagnostic; evaluate it in Shaded Scene View.
The diagnostic toggle also displays each existing GPU occupancy map directly in
the inspector, converting its R channel to grayscale, plus occupied-cell count,
crop bounds, active shader, debug value and whether the renderer's current material
references that map. Missing maps show a separate warning, not a fabricated black
preview. Previews are transient cached copies, invalidated on occupancy rebuild
and destroyed with the runtime. This view bypasses scene exposure/fog/lighting and
does not prove that the scene shader samples the correct coordinates.
Coverage rebuilds now force material rebinding, including derived far/flat copies,
even when the ordinary paint/surface hashes are unchanged. Debug toggles also rebind
the coverage texture, crop, dimensions and transform. Runtime bindings now select
canonical UV0 multiplied by LTWorld size for hull coverage; diagnostic pixels use
the same UV mapping directly, like the layer sampler. The optional world-position
helper remains available, but runtime disables it. Synthetic benchmark materials
also default to UV mapping. The inspector additionally reports
the LIVE material crop and world-mapping switch (not just expected CPU bounds).
After import, validate the native shaders, compare the scene mask with inspector
preview at factor 1, then factor 8. Native rendering remains a required check;
dotnet compilation cannot validate HLSL or guarantee contour-tight tessellation.
World-mask debug now uses the same base color and emission intensity as screen
tiles, removing an intensity difference from the comparison. `Проверка координат
маски` (mode 4) reads no texture: red outside the crop, green/blue gradient inside,
yellow invalid dimensions/non-finite coordinates. Chunks without coverage stay
in empty-mask mode. The live material's LTWorld size is printed in the inspector.
Read-only validation of the saved Chunk_0_0 mesh found 13005 vertices with exactly
zero XZ error for UV0 * 4096, including 244 vertices within the observed crop.
This checks the saved asset only, not live GPU interpolators or unsaved geometry.

Required Unity validation: run Tools / Local Terrain / Validate Eight Layer Shader,
then enable one textured rocky layer; inspect silhouette/normals/shadows, fade, a
four-chunk corner, neighboring non-displaced chunks, layer removal/master off,
Undo/Redo, scene save and two cameras. Native shader compilation/visual/performance
checks are not performed by dotnet. GPU Layer Benchmark has a displacement toggle
for two separate runs with the same stationary camera; it deliberately tessellates
all test chunks, so this is stress-test data rather than actual scene cost. Put an
enabled displacement layer first to exercise the 1-layer phase. Bounds/materials
are restored at the end. JSON includes mode, factor, fade and seam settings.

CPU investigation: LTWorld / Текстуры / `CPU-замер покраски — 10 секунд` records
painting polls without forcing rebuilds or changing scene/material settings.
Keep the camera and stamps still; start after import/initial paint has settled.
The report appears in the inspector/Console and is saved with a unique name to
`Temp/LocalTerrainCpuBenchmarks/*.json`. It contains raw per-poll stage timings,
mean/p95/max, mask rebuild/bind counts, global material update/copy counts, and
separate CPU camera-selection totals. Main-thread allocations include ALL systems
and measurement overhead, not just painting. Total is inclusive; stages are its
subsets. Missing stage time is other polling/setup work. These are CPU wall times,
not FPS, GPU duration, full frame cost, or actual Scene View redraw cost. The
capture works in Edit/Play; domain reload discards an unfinished capture. Inactive
capture skips clock reads. Do not run alongside the GPU benchmark.

Global material bindings are cached per chunk. Near layer rebinds dirty the far
copy; global readiness, texture references, fade distances and far surface values
invalidate bindings. Disabled globals do not repeatedly copy dirty far materials.
Rebaking into the same RenderTextures needs no rebind. Camera callbacks assign a
renderer material only when the desired near/far material differs from its current
one (not from a per-camera cache, so multiple cameras/save restoration still work).
After initial setup, repeat the idle CPU capture: global updates/copies should be
zero. Then check layer edits, fade/surface settings, global off/on, saved-map swap,
manual bake, scene save and cameras on opposite sides of the fade distance in Unity.

Open `Tools / Local Terrain / GPU Layer Benchmark` in Play Mode.

The default `Сравнить 3 режима displacement` mode uses snapshots of currently
bound chunk materials, their actual weight/coverage textures, and identical source
meshes. It compares: ordinary non-tessellated shader; tessellated displacement with
face-corrected normals; tessellated displacement with smooth normals at the world's
configured radius. Non-displaced chunks stay unchanged. Masks, target edge length,
height smoothing and all other snapshot settings remain fixed. GPU elapsed values
are full-camera measurements, including effects on shadows/contact shadows, not
isolated tessellation-stage timings. Baseline retains boundary-refined source mesh
cost. No eight-layer selection is required in this mode. Turn the mode off for the
existing synthetic layer-count test. Reports record mode labels, raw samples,
world/material settings and per-round medians/p95. No native GPU run has been
performed by the C# build; verify start/cancel/completion restoration in Unity.

For the synthetic mode select LTWorld,
an active Game Camera and eight distinct layer assets (the collect button fills
them from the world). Keep Game View open with a stationary camera viewing terrain.
The tool clones chunk materials, binds the same first N layers across chunks and
uses equal constant weights for N=1,4,8. It freezes only this world's painting and
LOD updates, not the rest of the game. Original renderer materials are restored on
completion, cancel, window close, domain reload, save or leaving Play Mode.

Three rounds in different orders each discard 90 camera frames and collect 180
valid GPU samples. The CustomSampler is GPU-enabled and bracketed by SRP camera
callbacks using command buffers. Recorder GPU data has a three-frame delay; the
warmup discards old-case measurements. CPU time/FPS is never substituted. Missing
GPU counters stop the run with an explicit unavailable result. Camera/resolution,
render scale, mesh or material changes abort the test rather than mixing conditions.

JSON reports, including raw samples, median and p95, are written to
`Temp/LocalTerrainGpuBenchmarks/` with unique names. Reports are temporary project
diagnostics, not imported assets. This measures full-camera GPU time in Editor,
not an isolated shader/pass; other scene content and GPU work remain confounders.
### Target-edge tessellation

Smooth displacement normals: LTWorld's `Сглаженные нормали displacement` is
enabled by default (radius 0.15 world metres). Domain evaluates the complete
displacement field at four neighbouring tangent-plane points, including layer
weights, smoothing mip, visibility gate, distance fade and seam envelope. World
offsets are transformed into LTWorld XZ using its inverse matrix. The central
gradient perturbs the original smooth normal and the tangent is re-orthogonalized.
Position displacement remains unchanged and uses the original normal. The old
fragment face correction is skipped in this mode to avoid double-counting slope.
Normal maps still use the resulting tangent frame. Diagnostic views skip extra
normal probes; outside the displacement envelope the base normal is retained.

This is a local tangent-plane approximation, not exact curvature reconstruction.
It smooths shading, not silhouette, mesh cracks or sharp height transitions. Cost
is up to four extra full layer-displacement evaluations per domain vertex; check
GPU timing on/off before using widely. Compare fixed-camera lighting with radius
0.15 and toggle off to restore the old face-corrected shading. Validate shadows,
normal maps, non-identity LTWorld transforms, seams, slopes, zero displacement and
distance fades in Unity. CPU analytic-normal tests do not compile/run HLSL.

Geometry height smoothing is independently controlled per Surface Layer by
`Сглаживание высоты (Mip)` (default 0 preserves the previous path). Runtime and
benchmark bindings clamp it to the mask's available mip levels. LTDisplacement.z
stores this value. Each active displaced layer reads an additional filtered G
sample only when mip > 0; original mip-0 h still feeds height blending and
visibility. CPU occupancy construction does not apply smoothing. Normal maps,
albedo, tessellation factors and source topology are unchanged. This adds a texture
read; it is a quality control, not a guaranteed speedup or a fix for topology cracks.
Fixed per-layer mip selection avoids patch-dependent displacement differences
along shared edges. The UI warns if the mask has no mipmaps; imports are not changed.

Native checks: compare mip 0/3/4 at fixed camera and amplitude; verify the mask
remains aligned, spikes decrease without erasing desired relief, shadows and seams
remain consistent, and mip 0 restores the old result. Mip filtering only smooths
height values, not high-frequency visibility gates, so remaining artifacts require
separate diagnosis. C# builds/console tests do not execute the GPU shader.

Coverage bake sampling uses explicit GPU-convention bilinear sampling of RGBA8
arrays: pixel coordinate = UV * size - 0.5. Weight textures clamp; tiled height
textures repeat, including interpolation across the repeat seam. Arrays and
dimensions are read once per coverage bake. The endpoint-weight UV correction
remains unchanged; it must not be passed directly to CPU GetPixelBilinear.
Stamp authoring sampling is unchanged (both display and coverage consume its
resulting weights). After script reload, compare normal material and mask from
the same camera. No atlas rebake or manual mask offset is required. CPU tests
cover texel centres, a 257-endpoint grid, transitions, clamp and repeat; native
Unity alignment still needs visual verification. Mip/filter differences and
conservative coverage dilation can still cause local contour differences.

`Тесселяция по длине ребра` enables a world-space edge-length cap (default on,
target 0.25 m). Hull computes min(maximumFactor, max(1, edgeLength / target))
before HDRP distance/screen attenuation and existing occupancy checks. Shared
edges use only their two endpoints; world-position differences also account for
object scale. The interior factor is derived by the existing HDRP calculation.
Source mesh generation, mask construction and displacement amplitudes are unchanged.
The target is approximate, not a strict output edge bound: fractional-odd hardware
partitioning, maximum factor, distance and screen attenuation affect the result.
An already short source edge cannot be simplified by tessellation. Turn the option
off for the old fixed-factor behaviour; changing target must rebind materials.

In Unity compare on/off in the same near-camera Wireframe view with maximum 8,
source step 4 m and boundary step 0.5 m. Start with target 0.25 m, then try 0.5 m
for lower density. Check shared edges, shadows, camera movement/distance fade,
normal rendering, mask diagnostics and Hull=1 probe. Run native shader validation.
CPU reference tests check the cap and endpoint symmetry, not native GPU execution
or GPU performance. Measure GPU time separately before claiming a speedup.

### Boundary prototype usage

In Textures, enable `Прототип: сетка у границ маски` under coverage refinement.
It is disabled by default. Select chunk X/Z (default 0/0), keep the existing
interior source step at 4 m and start with a boundary step of 0.5 m. The boundary
step is clamped to [0.5 m, interior step]. Mixed occupied/empty rectangles are
queried using an integral grid and refined by the existing quadtree planner.
Empty and solid interiors do not receive the extra boundary zone. This is a
bounded approximation, not pixel-exact contour triangulation. Fine noise can
still produce many mixed cells; the existing vertex budget/cancel safeguards apply.
The balanced forest and common-edge stitching remain responsible for transitions;
neighbour chunks can need stitching updates. Coverage includes the existing
conservative union across geometry/filter feedback until authored inputs change.

Compare prototype OFF/ON with an unchanged camera: source vertex/triangle counts
in Diagnostics (`Boundary test chunk (source)`), last build timings, mask view,
Hull reasons and Wireframe. Use a native GPU capture for GPU time; source counts
are not tessellated counts. Toggle OFF to restore baseline construction rules.
Do not rebake the distant atlas just for this experiment. Validate release-only
updates, budget cancellation, boundary-crossing stamps, and cross-chunk seams.
`Tools / Local Terrain / Run Refinement Tests` now includes mixed-mask planning,
coarse-cell retention, emitted topology and disable/restore checks; these require
Unity. Console tests cover 2000 integral boundary queries against brute force.

### Hull decision diagnostic usage

In LTWorld enable coverage diagnostics and select `Причины дробления (Hull)`.
Disable `Тест: Hull возвращает 1`, keep factor 8 and inspect the same close view in
Shaded. Blue = empty; green = confirmed occupancy intersection including the
one-cell halo; red = conservative traversal-budget fallback. Gray means the query
was skipped (factors already <= 1, or a non-tessellated material); yellow means
the force-one probe is still enabled. Red takes precedence over green if any
edge/interior query affecting a factor > 1 exhausted the budget.

The patch constant carries the actual query outcome through LTAdaptiveDomain.
The wrapper calls HDRP Domain, then writes only color.w; color.rgb still preserves
the pre-displacement position used for normals. No fragment-stage re-query is
used. Every child triangle has the same parent decision. Green across a dark
subregion can therefore be legitimate when its source triangle also touches the
light layer. This diagnostic does not fix topology or change coverage decisions.
Check native Unity shader compilation, all existing debug modes, normal rendering,
motion vectors/shadows, and the force-one probe after import. Console CPU tests
exercise confirmed hits, empty queries and bounded-work fallback, not GPU execution.

For a worst-case 24-texture test all eight layers need color, normal and mask maps.
Visual frustum overlap is checked, but actual terrain pixel coverage is not measured.

## UV and normal-map isolation probes

Enable the temporary coverage display and select one of the three new modes:
6: UV checker, 0.25 m cells in texCoord0 * world size, emission-only output;
7: lit grey without texture normal; 8: identical lit grey with texture normal.
Modes 7/8 force albedo 0.4, AO 1, metallic/smoothness 0, but retain the normal
blend, displacement geometry and production geometric-normal correction. Mode 7
replaces the blended tangent-space normal before geometric correction. Domain
smooth normals remain enabled in all three probes; Hull reason payload is only
written in mode 5. Probes 6-8 also work with displacement disabled/no occupancy.
Settings are nonserialized and do not change layer assets or baked maps.

Native verification (not covered by dotnet tests): compile both shaders, use one
fixed Scene camera in Shaded with Hull=1 disabled, compare checker with global
displacement on/off, then compare grey modes 7/8 with displacement on. Keep light,
exposure, postprocessing and decals unchanged (prefer no decals). Checker is not
immune to exposure/tonemapping. Return to ordinary material by disabling coverage
display. Verify legacy modes 1-5, shadows and smooth-normal on/off still work.
Successful C# build and CPU reference suite do not validate these GPU probes.

## Displacement mask edge fade

### Uniform hull isolation test

Nonserialized `uniformHullDiagnostic`, `uniformHullFactor` (default 16, 1-63),
and `uniformHullChunk` (default 0,0) are under the paint diagnostics. Applies only
to a matching displacement-active chunk; other chunks keep production factors.
After HDRP culling, override every visible patch's three outer and inner factors
with the same value. Bypasses coverage, length, distance and screen-size density
attenuation, but leaves displacement/mask/seam/distance HEIGHT envelopes intact.
Hull=1 and uniform probes are mutually exclusive in the UI. Shader also prioritises
Hull=1. Coverage reason 5 is pink in the Hull reasons view. Real-material benchmark
rejects this diagnostic. Material clones receive the override and reset to zero
on disable/tick; no source mesh or authored settings are changed by the probe.

Native check: select a displacement-active chunk, fixed camera inside that chunk,
toggle 16 then (if safe) 32 in Wireframe. GPU fractional_odd rounds segment count to
odd (63 maximum); this is not a world-edge-length mode. Whole chunk including empty
mask subdivides, so monitor GPU responsiveness. Ignore outer chunk boundary for this
test: neighbouring chunks may have mismatched factors. Compare normal view too.
Pink under Hull reasons confirms probe branch execution, not topology correctness.

### Source transition diagonals experiment

Under boundary prototype, `displacementTransitionDiagonals` replaces centre fans
only in balanced source cells with hanging edge midpoints AND mixed mask coverage,
on the selected test chunk. Convex-polygon dynamic programming maximises the worst
XZ triangle area/sum-of-squared-edges score among triangulations without interior
vertices. It does not compare quality against the old fan; this is an alternative
topology, not a guarantee of improved aspect ratio or appearance. No boundary vertex
is removed, so all stitched edge segments remain identical. Standard four-corner
cells, unrelated cells/chunks, and hardware tessellator partitioning are unchanged.
The same selector is passed to LOD emitters. Config changes trigger rebuilds.

New topology uses n-2 triangles instead of n and omits the centre sample. This can
change approximation of the underlying heightfield and colliders after regeneration;
source surface error is not independently revalidated by this experiment. Rock-cut
clipping still runs through Tri and shared intersection caches. CPU tests directly
exercise all 16 midpoint patterns at three aspect ratios, total area, winding,
nondegeneracy and boundary/internal incidence. Native test still required for
curved heightfields, cut boundaries, LOD changes and displaced shared edges.

A/B: keep GPU mask transition at zero, enable boundary prototype, use same test
chunk and density, toggle source diagonals. Compare Hull=1 wireframe to isolate
source topology, then ordinary hull for final tessellation. A GPU fan in a normal
four-corner cell will not be fixed by this change. Disable toggle to restore fans.

### Tessellation transition band (separate from height fade)

`tessellationMaskTransition` defaults to 0.5 world metres. Zero reproduces the
previous hull support logic. Empty edges with factors above one receive one extra
bounded occupancy query with expanded boxes; a hit/fallback enables half of their
additional subdivision. Padding uses local X/Z axis scales. It is a conservative
box band, not Euclidean distance or a continuously graded field. Within-patch
inside factor is at least the mean of the final edge factors; enclosed-island
inside subdivision is never reduced. No height, colour or source-mesh changes.
Canonical endpoints and the same map ensure matching shared-edge support within
a chunk. Separate chunk maps and coarse/fine source T-junctions remain limitations
to verify, not a claim of globally seam-free topology. Existing seam fade remains.
Hull green/red now includes transition queries. Each empty edge can add up to 256
node visits; triangle count can increase. Benchmark after native visual validation.

Native A/B: same camera, width 0 vs 0.5, Wireframe and normal material; check edge
fans, small islands, empty background, chunk boundaries, and CPU/GPU timings. This
is the first transition-support change, not a source-mesh quality remesher.

LT World / Textures has `displacementMaskEdgeFade` (metres, default 0.5, zero
disables). The CPU builds an RFloat endpoint-grid distance texture from the current
visible displacement union BEFORE the conservative occupancy pyramid/density union.
Two-pass 8-neighbour chamfer distances approximate distance to empty samples,
including holes. X/Z spacing includes transform axis lengths. Internal chunk bounds
are not seeded as empty; cross-chunk distances are not exchanged. Existing seam fade
still handles chunk edges; very wide mask fade can differ across a chunk boundary.
Distances have mask-grid accuracy, not exact vector contours. Small islands narrower
than the fade band may be suppressed throughout. A full mask gets a finite far value.

Distance map is rebuilt with coverage, not every frame or on fade-width changes.
Endpoint-centred bilinear sampling matches the paint convention. The shared height
function multiplies displacement by smoothstep(0,width,distance), so normal probes
and raster passes use the same envelope. Colour, layer blending and tessellation
occupancy rules are unchanged. Extra cost: up to ~4 MiB per 1025² distance map and
one texture sample per height evaluation (also each of four smooth-normal probes).
Width zero skips that GPU sample; the cached map still exists.

CPU production tests cover empty/full, holes, anisotropic distances, diagonal and
straight boundaries. Native check: compare width 0 / 0.5 / 1 at a painted edge and
hole; confirm colour stays put, normals follow the fade, chunk seams stay closed,
and moving/removing stamps refreshes the distance field. C# tests do not prove GPU
shader compilation or rendered results.

### Regular source topology follow-up

Inspector organisation: LTWorld now has Geometry / Textures / Tessellation /
Diagnostics tabs. Tessellation contains the displacement enable switch, all mesh
refinement/fade/normal controls, per-layer displacement amplitude/centre/mip and
coverage/Hull probes. Texture-layer inline editors omit those four displacement
fields; texture assets and height blending remain in Textures. Serialized field
names/defaults are unchanged; moving tabs must not reset values or diagnostics.
The regular HDRP Domain and LTAdaptiveHull still take three control points per patch.

`displacementFineBase` is a separate, default-off A/B mode, effective only when
footprint refinement is enabled. It divides the requested base and prototype
boundary steps by four (after the old 0.5 m minimum), and the clamped GPU maximum
by four (minimum one). Example: 2 m / 32 becomes 0.5 m / 8. Stored settings remain
unchanged. Covered chunks use regular source diagonals irrespective of the older
single-chunk diagonal prototype. Refinement still uses coverage queries and forest
balancing, not uniform world refinement. Geometry config and density signatures
invalidate generated meshes; material hash tracks the effective factor. Benchmark
binding/reporting also uses the effective factor.

Source triangle counts can grow roughly sixteenfold in refined areas; vertex
budgets, cancellation and previous-mesh preservation remain in effect. Collider
and LOD source meshes are affected. Far-field cost can increase even when GPU
tessellation fades out. Equal final density/cost is not guaranteed because of
quadtree rounding, screen/distance factors, and fractional-odd partitioning.
Disable both Hull diagnostic overrides before visual/performance A/B. Check source
rebuild completion/errors, chunk seams, LOD transitions, silhouette and GPU time.
CPU tests cover parameter conversion only; visual improvement requires Unity.

The existing `displacementTransitionDiagonals` prototype now covers the entire
selected boundary-prototype chunk, including all LOD meshes, rather than only mixed
mask-boundary cells. Regular quads use a consistent diagonal: six incident source
triangles per interior grid vertex instead of checkerboard four/eight-way hubs.
Stitched cells still use the tested boundary-preserving triangulator without a
central vertex. Disabled mode retains the previous checkerboard and centre fans.
No cell refinement, mask, Hull factors, or displacement values are changed.
On nonplanar cells, changing diagonals changes the piecewise-linear surface and
its collider; the prototype is not guaranteed to meet a new geometric error bound.
Hardware triangle-domain patterns and adaptive density transitions can still exist.

CPU tests reproduce old 4/8 incidence, verify new six-way incidence, shared edges,
and all 16 stitched-cell patterns. Native validation remains required: on chunk
(0,0), compare the existing topology checkbox OFF/ON with Hull-one first to inspect
the source, then uniform Hull 16 to inspect subdivision, then normal adaptive mode.
Keep the view fixed; check rendered relief, cuts, LOD/chunk seams and GPU timing.
This is not a claim that all visible GPU fans have been eliminated.

### Probe follow-up: daylight exposure and real faces

Mode 6 now cancels HDRP pre-exposure on its emission and sets black diffuse and
zero specular-color response/coat. Previously emission 0.05-0.8 was in physical
scene units and could disappear under daylight exposure while dielectric lighting
remained visible. Tonemapping, fog and screen-space effects are still applicable;
this is not a separate unlit rendering pass. Check recognisable near-field cells
before interpreting any UV result, and compare with displacement off at fixed view.

Mode 9 (grey real faces) replaces the final shading/geometric normal with the
oriented cross product of screen derivatives of the displaced position. It bypasses
texture normals and domain smoothing for shading only, keeping the exact geometry.
Degenerate derivatives and ray tracing fall back to the input normal; use raster
Scene view. Compare modes 7 and 9 with otherwise identical camera/light/settings:
differences isolate smooth-normal shading versus actual triangle orientation.
