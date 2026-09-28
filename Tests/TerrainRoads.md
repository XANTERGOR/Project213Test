# Spline roads and explicit junctions

## Offroad variation

In Road, expand **«Вариативность дороги»** and enable **«Включить вариативность»**.
It is opt-in; existing roads and asphalt remain unchanged when disabled.

- `Общая сила` blends back to the original road at zero. `Seed вариативности`
  is deterministic, independent of camera/time and separate from the existing rut-edge seed.
- `Изменение полуширины` gives independent left/right narrowing and widening
  (default up to ±15% of half-width); `Длина изменений ширины` controls its scale.
  Height, paint, removal masks and scene gizmos use the same side widths. Density,
  dirty bounds and sparse UV bakes conservatively include the widest possible road.
- `Подмешивание грунта` / `Размер пятен` reveal the previously painted terrain
  beneath the road in large patches. They do not create new materials, textures or
  palette slots. The road needs a different substrate to show material contrast;
  later paint stamps can still cover it. This is not a dedicated sand/gravel layer selector.
- `Колеи на сплошной дороге` adds ruts while retaining Solid's full-width paint mask.
  Configure rut width/separation/depth under `Покрытие`. `Ослабление колей` and
  `Длина изменений колей` vary their strength along the road. Tracks mode retains
  its central unpainted strip. Patches preserve more coating in the wheel tracks.
- Each authored point has optional **«Своя сила вариативности»** (0–1 multiplier).
  Unset means 1; values interpolate along the spline. Generated junction endpoints
  remain node-controlled. Variation fades out over the whole straight entrance neck,
  and at free rounded caps, to retain the existing connection geometry.

Patch edits leave the road's geometry and removal signatures unchanged; solitary
spline-layer UV maps are reused. Shared-layer road networks still rebake ownership
when paint weights change. Layer-dependent vegetation/displacement may need their
normal paint refresh: this is not a guarantee of zero downstream work. Width/rut
edits use the existing scoped terrain rebuild. No extra per-frame generator or
shader pass is added. Camera LOD still uses baked geometry; colliders remain LOD0.

Managed checks cover 23,463 finite/bounded/deterministic probes, disabled/zero/asphalt
parity, local profiles, fixed junction entrances, patch-only invalidation, fresh-vs-
cached single-road UV maps and 0 B allocation for warmed math queries. A variable-
width rutted multi-chunk fixture also checks height budgets and watertight mixed LODs.
Native Unity import/render, appearance and live editor timings are separate checks.

## Authoring

Select an LTWorld (or its child), then use **GameObject → Local Terrain → Road (Offroad)**
or **Road (Asphalt)**. No scene objects are created until this command is used.

- New roads put their Transform at the middle authored point, with small local
  point coordinates, and frame the path in Scene view. Existing roads are not migrated.
- At the top of Road, `Показать сплайн` frames the whole path without changing it;
  `Показать выбранную точку` frames one point. Both enable Scene gizmos. F uses path bounds.
- `Редактировать точки в Scene` hides the ordinary Transform handle while active.
  Turn it off to move the entire Road. Tool visibility is restored on deselection,
  disabling editing and editor teardown. Point handles remain visible through terrain.
- Shift + left click on this world's generated terrain collider appends a point
  at the hit position. Alt-navigation is preserved; other worlds, vegetation and
  asphalt colliders are ignored. Missing collider/hit is reported, with no insertion.
  `Добавить точку в конец` also selects and frames the new point.
- `Перенести путь к объекту Road…` is an explicit, confirmed repair for a distant
  path: translates the first point to the origin and projects all heights to source
  terrain. It preserves plan shape/banks, validates every point before committing,
  and supports Undo. It does not run automatically on old roads.

- Points are road-local. Select a numbered handle and move it; insert/delete points
  in the inspector. Banking is in degrees. `Прижать к terrain` samples source terrain
  plus height/mesh stamps, excluding all roads; it captures those stamps once.
- `Ширина`, `Обочина`, `Плавный переход` control the footprint and height transition.
  The CPU terrain mesh and its collider receive the road shape. `Размер ячейки terrain`
  controls LOD0 density; tiny values can exceed the existing terrain vertex budget.
  Offroad roads and offroad junctions simplify with the terrain, using each LTWorld
  LOD's simplification steps and maximum height error (including rut geometry).
  This applies to both WholeChunk and WithinChunk. LOD0 and colliders are unchanged.
  Asphalt road/junction foundations still retain fine topology: the separate asphalt
  meshes have independent LODs, so independently coarsening their foundations could
  make terrain intersect the visible ribbon. Rock/cut contact contours stay protected.
  Existing baked LODs need one terrain rebuild to adopt this policy. In Scene view,
  enable `Preview LODs` and leave `Force LOD = -1` to use camera distance.
- Offroad is a **terrain layer**, not a separate ribbon. Assign `Слой terrain`.
  `Solid` paints the full width; `Tracks` paints two strips and can lower their
  terrain height. The centre of two tracks remains unpainted/un-cleared.
- **Проекция текстуры → World / Spline** is per road. World keeps layer tiling.
  Spline uses U = lateral distance / road width + 0.5 and V = accumulated path
  length / `Повтор вдоль пути, м`. `Смещение текстуры` is UV offset (X across,
  Y along), not metres. Colour, normal, height mask, displacement, vegetation
  height-blend sampling and the far-map baker use the same coordinates.
  Offroad projection/repeat/offset do not change the terrain geometry hash.
- Asphalt uses a separately saved mesh ribbon and MeshCollider; continuous UVs
  cross chunk boundaries. Assign an asphalt material, or explicitly bake to
  create a private HDRP/Lit material. Existing material assets are never edited.
  Terrain visual displacement/deformation is suppressed beneath asphalt with a
  conservative feathered mask. `surfaceOffset` is added once to the ribbon.
- Clearing vegetation and stones are separate choices. Clearing is part of
  generation for both CPU and GPU rendering; no per-frame plant edits.
- Editing is debounced; releasing a handle updates generated data. Explicit
  `Перестроить / запечь` also applies terrain changes when auto-update is off.
  Save the scene/assets normally to persist authored roads and generated meshes.

## Deliberate first-version limits

- An affected chunk supports twelve terrain layer slots including the base layer.
  Multiple spline roads may share a layer; it cannot also be the base layer or
  an ordinary/world-projected paint stamp in that chunk. The neutral junction
  centre uses a separate layer asset (which may reuse the same texture assets).
  Conflicts are shown in LTWorld paint status, without repeated Console exceptions.
- Junctions are explicit nodes, not automatically inferred spline crossings.
  No bridge, tunnel, closed-loop or self-intersection solver.
  Use gentle turns: wide asphalt ribbons that fold are rejected. Road transform
  supports translation/yaw and unit scale; point heights and banks remain editable.
- Coverage and coordinate maps are 257×257 per affected terrain chunk. Very narrow
  ruts and tight curves are limited by that spacing. Spline UV maps use float32
  and cost about 1 MiB GPU + 1 MiB managed CPU per single-road layer/chunk;
  multiple roads share two slices (about 2 + 2 MiB), not one slot per road.
  They are rebuilt only on relevant edits. Asphalt suppression uses an additional RGBA8 map.
- Runtime consumes editor-baked road geometry; it is not a runtime spline editor.
- Generated mesh assets live in `Assets/LocalTerrainRoads/<bakeId>`, separate from
  terrain mesh cleanup. Old immutable mesh versions remain for Undo/reuse; there
  is no automatic deletion of authored files or old mesh assets.

## Explicit junction authoring — 2026-09-26

1. Select LTWorld or a child, then **GameObject → Local Terrain → Road Junction**.
   Position the node at the desired centre; its Y is the common entrance height.
2. In its inspector assign a road and **Начало / Конец**, then **Подключить к узлу**.
   T junctions need three road ends, X junctions four. A crossing of two unbroken
   roads is deliberately not cut automatically. Supported: 2–8 ends, non-overlapping
   mouths whose convex outline contains the node centre.
3. The second/penultimate authored point determines the entrance direction and
   must lie beyond **Радиус въездов + Прямой въезд**. Endpoints and flat necks are
   derived without changing the saved authored points. Disconnect/Undo restores
   the ordinary road path. Connected endpoint handles show the actual derived port.
4. Assign **Слой центрального грунта**: a separate, non-directed layer without
   wheel tracks. All directed branches can share one Spline layer. Central paint
   overlays branch caps and feathers out; it consumes one of the existing 12 slots.
5. If any entrance is asphalt, choose **Asphalt** on the node, assign its material,
   and match **Surface Offset** with the asphalt branches. Offroad endpoints rise
   to the asphalt surface and their rut depth fades out at the entrance. No lane
   markings are generated or matched automatically; use an unmarked centre material.
6. **Перестроить / запечь перекрёсток** updates connected asphalt, terrain and details
   explicitly, including when terrain auto-update is off. Ordinary edits are debounced
   after releasing a handle and use existing scoped terrain dirty-region detection.
   Save the scene normally; the tool never calls SaveScene or global SaveAssets.

Asphalt centres are flat convex meshes with fixed entrance boundaries at all LODs;
only interior rings simplify. Distance selection follows LTWorld's camera/preview/
force-LOD settings; the collider stays at LOD0. Immutable meshes are saved under
`Assets/LocalTerrainRoads/Junction_<bakeId>` and retained for Undo.

Current module compatibility: a planar end at the configured surface height and
exact road width at every supplied/generated LOD. Raised kerbs, a thick end cap,
or differing LOD end widths are rejected with an inspector message. Use a separate
flat-ended transition road; automatic kerb/cross-section junction construction is
not implemented. The centre does not yet round corners or create turn lanes.

The shared UV map stores a dominant branch per texel and a local UV Jacobian at
ownership seams. CPU coverage/displacement sampling and HLSL use matching selection;
they never bilinearly blend unrelated longitudinal UV phases. Single-owner cells
keep the previous bilinear path. Outside a neutral junction centre, overlapping
directed branches can still have a hard ownership seam; this is not a texture
cross-fade or an automatic intersection. Very narrow features remain grid-limited.

Validation for this change: managed regression suite passed (8,708 ownership samples,
shared chunk boundaries, 4 node layouts × 4 LODs, flat entries, bounded influence),
and six reduced HLSL compiler cases passed, with existing displacement X4000 warnings.
These do not establish live appearance, collision driving or scene performance.
Native follow-up: T/X/Y and asphalt/offroad joins, module rejection, moving/disable/
delete/Undo, saved-scene reopen, camera LOD switching and shared-layer chunk borders.

### Existing verification commands

- Compiler-only: `dotnet build Assembly-CSharp-Editor.csproj --no-restore -v:q
  /clp:ErrorsOnly /p:CustomAfterMicrosoftCommonTargets=G:/UnityProjects/Project213-Testing/Tests/LocalTerrainCompile.targets`.
- CPU: `dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj --no-restore`.
  Includes endpoint UV and guarded-region/full-grid bilinear equivalence checks,
  twelve-slot painting/displacement/deformation and copied-layer ID regressions,
  production terrain mesh integration,
  and the existing terrain/detail regression suite.
- Road-terrain LOD regression (2026-09-28): five managed 2x2-chunk fixtures cover
  flat offroad, curved/banked ruts, varied offroad, an offroad junction and an asphalt foundation.
  526,241 emitted-surface comparisons stay within the configured height budgets;
  mixed patch/chunk boundaries are watertight and returning near restores exact LOD0.
  LOD0/collider geometry is unchanged, and asphalt contact cells and stitch masks
  remain fixed. WholeChunk border preservation and offroad reduction are also checked.
  Synthetic LOD0 -> distant whole-fixture triangle counts: flat 16,491 -> 400;
  rutted 16,550 -> 3,345; junction 5,679 -> 508. These are not live-scene statistics
  or FPS measurements. C# editor build passed (0 errors, 3 warnings) and the full
  managed suite passed; native import/render and the user's scene remain unverified.
- Texture-array migration source contracts: three shared color/normal/mask arrays,
  all twelve local-to-world slice mappings in raster/ray sampling and tessellation,
  canonical RGB normal decoding, and unique material mapping uniforms. Cache checks
  cover per-world ownership, the full palette (including disabled stamps),
  texture-only fingerprints, generation invalidation, disposal, GPU-only packing,
  Apply-before-copy ordering, complete mip copies and peak memory/capability guards.
  Additional contracts cover the world-independent packing overload, streaming
  readiness/request restoration, abandoned waits on cache hits, captured native
  failures before publication, and cleanup when Apply/staging allocation fails.
  These inspect source; they do not instantiate arrays or prove native GPU behavior.
- Reduced HLSL harness: Windows `D3DCompile` (`d3dcompiler_47.dll`, default
  optimization) passed projected sampling and geometry-helper pixel/vertex
  shader builds. Geometry helpers emitted possible-initialization warnings.
  This harness replaced texture macros/uniform declarations and stubbed normal
  decoding: it did **not** compile full HDRP/ShaderLab, a real domain shader or DXR,
  and did not render anything. Full native validation remains separate below.
- Explicit native check: **Tools → Local Terrain → Validate Road Rendering**.
  Compiles live/bake shader passes and renders/readbacks spline UV, restored world
  projection and rotated normals using transient resources; no scene or asset saves.
  Added but **not executed** in the user's running editor during implementation.
- Still to check visually: curved textured road, triplanar on/off, height blend,
  chunk boundary, global map rebake, Undo/delete/disable, asphalt contact and collider.
  CPU/build results do not establish native shader success, appearance or GTX 1660 FPS.

No validation Unity project, package changes, linked caches or working-scene saves
were made for this implementation.

## Wheel-wear surface strips — 2026-09-28

- Offroad → **Следы колёс** → **Выделять следы колёс** adds an optional
  secondary `LTSurfaceLayer`. Both strips use one additional local slot out of 12;
  their weights composite after this road's ground coverage, before subsequent
  stamps. Existing roads default to disabled. Asphalt is unaffected.
- Assign a separate layer or press **Создать слой следов из покрытия…** and choose
  its asset path. This explicitly creates a unique copy, not an overwrite: tint
  RGB × 0.75, normal strength × 0.5, smoothness capped at 0.25, no metallic,
  displacement, deformation or copied detail recipes. Texture assets are shared
  read-only. Undo restores the road's assignment; the created layer asset remains.
- Starting settings: width 0.65 m, centre spacing 1.8 m, strength 0.85, edge softness
  0.4. Colour-strip dimensions are independent of geometric rut dimensions/depth.
  Width must be less than centre spacing; their sum must fit the road width.
  A tread pattern is not generated; a custom surface layer can supply textures.
- The overlay inherits World/Spline projection and the continuous full-road UV
  frame, not a separate repeated UV frame per tyre. Its repeat size/offset can now
  be independent of the ground layer (see below). Shared spline material ownership
  uses the wheel footprints, not the road's entire footprint. Explicit junction
  ports fade the strips; existing centre/road layers and LOD topology are retained.
- **Убирать камни в следах** uses the soft wheel footprint, independent of layer
  assignment and paint opacity. Disable the older whole-road **Убирать камни** to
  keep stones in the centre and shoulders. This clearing toggle does not directly
  remove vegetation; as with any terrain painting, changed layer weights can affect
  layer-based detail recipes. Edge noise and existing rut variation also modulate
  the visual strips; underlying colour patches do not.
- Assigned disabled wheel layers stay in the full packed palette, while the live
  chunk palette uses only enabled contributors. After adding/replacing the asset,
  refresh saved arrays in LTWorld → **Массивы**. Re-bake saved global maps if used.
  Terrain painting, array palettes, displacement coverage and saved signatures
  include the secondary layer. Road painting remains excluded from rock-stamp
  overlays. Masks retain their current resolution: very narrow tracks
  can be softened by the terrain weight-map texel size.
- Wheel colour/width settings do not change the road geometry hash or its height
  function. A chosen layer's own displacement/deformation can still affect the
  rendered surface if explicitly enabled; the convenience copy disables both.
- `RoadWheelTracksChecks` exercises production managed math: two bounded strips,
  centre gap/soft edges/caps, normalized compositing, geometry and legacy/asphalt
  parity, selective clearing, scoped hashes, shared-road UV ownership, junction
  fading, curved/banked paths, invalid inputs and zero-allocation hot queries.
  Additional source contracts cover palettes, saving and copy safety. These are
  not native Unity import, saved-asset roundtrip or live-render/performance tests.
- Verification: C# editor build passed with 0 errors / 6 warnings; the managed
  suite passed, including 20,377 wheel-mask grid probes and 0 B allocated by
  warmed wheel-mask/stone-clearing queries. Existing terrain LOD regressions also
  pass. No Unity/package restart, working-scene save or generated asset rewrite
  was used for these checks.

### Independent tiling and vegetation outside wheel strips

- In Spline mode, **Покрытие → Повтор поперёк, м** sets metres per transverse
  repeat; zero preserves the old one-repeat-over-road-width mapping.
  **Повтор вдоль пути, м** retains its existing meaning. Smaller values repeat
  the texture more frequently; neither changes the road or painted strip width.
- **Следы колёс → Свой тайлинг следов** enables a separate **Повтор следов, м**
  (`X` across / `Y` along) and UV offset. On first enabling it copies the current
  ground settings, avoiding a jump; subsequent off/on toggles preserve the authored
  values. With the toggle off, wheel UVs exactly inherit the road settings.
  The origin stays at `U = 0.5` on the centreline; this is one continuous UV frame,
  not a separate tread frame around each tyre. World mode still uses the two
  separate Surface Layer assets' Tile Size Metres / Tile Offset Metres.
- **Растительность: оставить только вне следов** is a shortcut to
  **Растительность и камни → Растительность → Убирать только в следах**.
  Alternatives are no removal and whole-road removal. Existing scenes keep their
  previous setting until the user selects the new mode. Selecting the mode also
  enables wheel tracks; subsequently disabling tracks disables this removal too,
  never silently changing it to whole-road removal.
- Strip-only vegetation exclusion is complete inside the configured wheel width,
  including soft paint edges, and independent of paint opacity, edge noise and rut
  weakening. It uses rounded finite caps and is applied through the existing common
  detail acceptance path before CPU/GPU rendering. It excludes roots/placement
  points, not entire prefab bounding boxes: large canopies can overhang the strip.
  The central gap and outside probabilities are preserved, but this is removal,
  not a new spawn source: configure plant recipes on the ground layer/detail stamps
  as usual. Stone-clearing controls keep their previous behavior.
- UV changes update projection caches and saved-global-map signatures, not road
  geometry/LOD hashes. Independent wheel UV cache keys do not depend on ground
  repeat/offset changes. Vegetation mode changes update detail placement keys,
  not paint/geometry. Texture arrays contain the same pixels: changing only tiling
  does not require repacking them. Saved global maps, if used, need a new bake.
- `RoadTilingVegetationChecks` covers independent/inherited UVs, cache invalidation,
  shared-material roads, chunk seams/cap extension, deterministic strip-only removal
  including curves/banking, unchanged centre/shoulders, legacy/asphalt behavior,
  invalid input and allocation-free hot queries. C# and managed checks are separate
  from live Unity visual/CPU/GPU verification. If Auto Refresh is disabled on the
  detail renderer, explicitly rebuild details after changing removal settings.
- Verification for this extension: editor C# build passed (0 errors / 6 warnings);
  full managed suite passed, including 21,535 straight-strip exclusion probes plus
  curved/banked and shared-UV fixtures. Warmed UV/exclusion queries allocated 0 B.
  No working scene, generated mesh, package or material asset was edited by tests.

### Native visual follow-up (not performed automatically)

Enable the feature on an offroad, create/assign the wheel layer, adjust its tint,
then check a turn, chunk seam, distant LOD and a shared-material crossing. Toggle
whole-road versus strip-only stone clearing and verify Undo/disable restore the
previous coverage. Save/reopen arrays through the existing explicit tools. Do not
save or overwrite the user's scene as part of an automated check.

### Array sampler ownership regression

Each layer array now has its own texture-associated sampler. Height/domain and
mask-only depth programs must not borrow the color sampler: Unity strips the
unused color texture and rejects its orphaned sampler even when raw D3DCompile
succeeds. Source checks enforce matching texture/sampler pairs for every sample;
`CompileTerrainArrays.ps1` also checks the optimized resource binding table and
rejects orphaned layer samplers. Saved packed pixels are unchanged. Six reduced
HLSL cases pass, with the existing displacement X4000 warnings; full Unity/HDRP
compilation remains a separate verification step.

## Road corrections — 2026-09-25

- Detail IDs are unique within a layer/stamp list, not globally across layers.
  Copied layer assets retain their existing entries and candidate seeds; scale-only
  cache lookup also checks the layer object so copies cannot reuse another layer's group.
  The previous global check aborted generation while leaving old GPU vegetation visible.
- Texture UVs extend along the endpoint tangent; geometry, finite paint footprint,
  height and clearing queries still use clamped segments. Rounded caps no longer
  stretch a constant texture row. CPU material samples and shaders use the same baked map.
- UV search runs only over the road bounds plus a two-texel guard, not the entire
  chunk grid. Texture resolution/storage is unchanged; tests compare against a full bake.
- Explicit road bake no longer saves every dirty project asset synchronously. Generated
  terrain meshes remain dirty for normal user Save; asphalt mesh assets still save individually.
  It requests a detail surface refresh after the terrain update, without discarding prefab
  recipes or bumping the global placement revision. Unchanged cells retain their caches.
- Native `Validate Road Rendering` now additionally checks slots 8–11. Full HDRP
  rendering, live road timings and the user's screenshot must still be verified in Unity.
- Twelve-layer reduced Windows `D3DCompile` checks passed in six pixel/vertex cases,
  including combined sampling and geometry helpers. They substitute the Unity macro
  preamble/normal decoding and are not full HDRP or GPU rendering. Geometry cases retain
  three X4000 possible-uninitialized warnings at existing early returns. C# build and
  the full CPU/source-contract suite pass; native Unity validation remains pending.
