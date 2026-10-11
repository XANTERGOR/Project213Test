# Spline roads and explicit junctions

## Shared ground with spline wheel textures

In **LT Road → Покрытие**, set **«Проекция текстуры» = World** and assign the
**same Surface Layer asset** already used by the surrounding terrain. In
**«Следы колёс»**, enable wheel tracks, keep a distinct wheel Surface Layer and set
**«Проекция следов» = Spline**. Configure **«Повтор следов, м»** and
**«Смещение следов»** there. X is across the whole road frame, Y follows its length;
these controls do not move or widen the two paint strips.

- **«Как покрытие»** is the default: old roads preserve their original mapping.
- Both ground and wheels may independently use World or Spline. World uses each
  Surface Layer's tile size/offset. Spline wheels over World ground always use the
  wheel repeat/offset controls; hidden ground spline settings are not inherited.
- The existing biome asset is reused, not copied. If it already occurs in this
  chunk, only the wheel layer adds a slot (unless that too is already in use).
  Both wheel strips share one layer. Different assets using the same textures
  still count as different layers.
- The spline wheel layer cannot simultaneously serve as a World-painted/base layer
  in the same chunk. Shared World ground is allowed, including Road Junction's
  ground; the junction cannot reuse a branch's spline wheel layer as its ground.
- Only wheel UVs and orientation change. Road geometry, paint/clearing masks and
  colliders do not change. Normal orientation and displacement sampling follow the
  existing per-slot spline path. Sparse paint-job sampling also retains guard
  pixels when only the wheel layer uses Spline.
- Update saved arrays in LTWorld → «Массивы» after changing their layer palette.
  No texture baking, reduced-resolution road strip, duplicated ground material,
  additional shader variant or new shader pass is introduced.

Managed: `dotnet run --project Tests/TerrainBridgeChecks -- --road-projection`.
Native component/palette validation:
**Tools → Local Terrain → Validate Road Layer Projections (no scene changes)**.
The existing **Validate Road Paint Jobs (no scene changes)** also includes mixed
World-ground/Spline-wheel fixtures. Shader pixel checks remain the separate
**Validate Road Rendering** command. Build/math passes do not prove native
graphics, Burst execution or visual quality in the user's scene.

The experimental baked road surface was removed at the user's request. Its UI,
baker, shader and dedicated tests are gone. `LTRoadSurfaceBake` remains a data-only
compatibility class (original GUID) so previously saved assets do not lose their
script. Roads do not use those assets anymore. User scenes and generated assets
were not rewritten/deleted. Retired source is recoverable from
`Logs/RetiredRoadSurface-source-backup.json`.

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
- The overlay defaults to inheriting World/Spline projection; **Проекция следов**
  can override it independently. Spline uses a continuous full-road UV frame,
  not a separate repeated UV frame per tyre. Its repeat size/offset can
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
- When both layers use Spline, **Следы колёс → Свой тайлинг следов** enables a separate **Повтор следов, м**
  (`X` across / `Y` along) and UV offset. On first enabling it copies the current
  ground settings, avoiding a jump; subsequent off/on toggles preserve the authored
  values. With the toggle off, wheel UVs exactly inherit the road settings.
  The origin stays at `U = 0.5` on the centreline; this is one continuous UV frame,
  not a separate tread frame around each tyre. With World ground and Spline wheels,
  the wheel controls are always independent (there is no inheritance toggle).
  Any layer using World instead uses its Surface Layer's Tile Size Metres / Tile Offset Metres.
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

## Experimental terrain asphalt — 2026-10-07

Select `Terrain Asphalt` on an `LTRoad`, or create it under
`GameObject → Local Terrain → Road (Terrain Asphalt - experimental)` with an LTWorld
selected. Existing `Offroad=0` and separate-mesh `Asphalt=1` serialization is unchanged;
the new value is appended as 2. Existing scene roads are not migrated automatically.

- Assign a dedicated asphalt Surface Layer in `Слой terrain`. The road uses one
  local terrain-layer slot; wheel-layer fields from other modes are ignored. The
  same asphalt layer can be shared by these roads, but not by the world background
  or ordinary biome stamps: base albedo/normal/mask are projected along the spline.
- The road is part of the terrain mesh. Height and bank follow the spline, with
  procedural pothole depressions in terrain LOD0 and its collider. Terrain LODs
  simplify by their normal height-error budget; no separate ribbon or fixed LOD0
  foundation. This does not create bridges, overhangs, loose rubble or through-holes.
- `Общая сила повреждений` gates all damage. `Количество ям` is a probability per
  road-local distribution cell; `Шаг распределения` controls spacing, `Размер ям`
  their diameter (up to 80% of spacing), `Глубина ям` their maximum depth, and
  `Разрушение краёв` erodes pavement edges. Missing pavement reveals existing
  underlying terrain layers without adding a substrate slot. Geometry and coverage
  remain deterministic across chunks; Seed changes the pattern. Point override
  `Своя сила вариативности` controls local damage and interpolates between points.
- `Сила трещин` changes appearance only. Optional `Рисунок трещин` is a linear
  grayscale mask (white = crack, Read/Write not needed); leave empty for a built-in
  tileable test pattern. Color, shading normal, AO and smoothness are modified in
  the existing layer evaluator. This is not an extra terrain layer or a real fissure.
  The asphalt base follows the spline; isotropic crack detail uses terrain-local XZ
  metres and `Повтор трещин`. Roads sharing a layer need the same mask and repeat;
  their damage strengths and pit settings may differ. Use a custom mask for art
  quality: the built-in cellular pattern is deliberately a prototype.
- Choose terrain cell size substantially smaller than a pothole (e.g. 0.25–0.5 m
  for 2 m pits). Paint coverage remains the existing 257×257 endpoint grid per chunk;
  very small missing-pavement shapes may be softer than geometry. Distant LODs can
  flatten small pits within their height tolerance; the texture marks remain.
- Crack detail is opt-in, not free: two extra array samples on enabled painted
  layers, about 0.25 MiB per affected layer/chunk for influence and 0.33 MiB per
  distinct 256² mipmapped crack pattern per world. Existing spline projection maps
  retain their usual cost. No per-camera crack generation or extra road draw call.
  Source texture streaming may temporarily limit the packed detail resolution;
  the atlas is rebuilt on source texture revision. No source textures are modified.
- Experimental mode does not support Road Junctions yet; disconnect any assigned
  endpoints first. Existing ground/mesh roads and junctions keep their old behavior.
  Vegetation clearing follows the whole road footprint even inside missing pavement.
  Scene/assets are never automatically saved by this feature's validation.

Validation:

- `dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj -- --terrain-asphalt`
  checks 16,000 reference/worker height and coverage queries, damage controls,
  cache invalidation and road LOD fixtures (including mixed-LOD seams and collider
  index preservation). These are managed, not native Burst/GPU/live-scene checks.
- `node Tests/TerrainAsphaltChecks.mjs` checks integration/source contracts.
- `Tests/CompileTerrainArrays.ps1` compiles six reduced Direct3D HLSL cases including
  the new shared evaluator. It is not the full Unity/HDRP shader compiler.
- In Unity: `Tools → Local Terrain → Validate Terrain Asphalt (no scene changes)`.
  Uses an owned temporary preview scene/resources: real terrain mesh/collider,
  one-slot and spline routing, shader pass compilation, texture-array lifecycle and
  shared-baker GPU pixels (slots 1, 8, 11). Logs geometry and GPU results separately.
  Native results and actual scene appearance/performance must be verified in Unity.
- Then create a short experimental road, assign asphalt, Apply Pending Changes,
  compare LOD 0/4, and inspect a chunk boundary plus damage strength 0/1. Existing
  scene/material assets were not changed to set up this test automatically.

## Geometric Asphalt prototype — 2026-10-07

Select `Geometric Asphalt` (appended enum value 3) on the same road to compare
with `Terrain Asphalt` (2). Existing roads are not migrated. New roads can also
be created with `GameObject → Local Terrain → Road (Geometric Asphalt - experimental)`.

- Real, convex eight-corner potholes and edge chips replace pieces of terrain
  triangles. Vertical sidewalls and a lowered bottom are emitted into the **same
  chunk mesh, submesh and collider**, with the existing terrain material. There
  is no separate overlay renderer or double surface over the pit.
- Geometry cuts the actual triangles, including a pit entirely inside a very
  coarse triangle. It does not need a dense terrain grid to find the pit. New
  roads start with a 2 m terrain cell; switching an existing road preserves its
  authored cell size. The spline still sets the top surface and bank.
- Whole-chunk and Within Chunk LODs use their normal terrain simplification.
  The eight contour corners remain; terrain subdivisions along the rim/walls
  reduce with LOD. This is not a far-distance pit-removal/impostor system and
  cannot make every tiny pit completely free. No fixed dense LOD0 ring.
- Top intersections interpolate each original terrain triangle's plane. Floors
  use that same plane minus a constant pit depth; walls are hard-normal splits.
  Adjacent chunks use the same immutable world-local contours. Existing terrain
  holes are cut first, so the new generator does not fill them back in.
- The existing damage controls now drive actual cuts. Strength changes radius
  and depth; probability/spacing control distribution, broken edges add bites at
  the paved edge, seed is deterministic. Zero depth/strength restores the uncut
  top surface. Sharp cuts are not subsurface tunnels, undercuts or loose rubble.
- Paint and geometry use the same pit contours, exposing previously painted
  ground at the bottom. Paint jobs receive a bounded precomputed contour mask
  and retain ordered composition; no additional terrain layer is allocated.
  The usual 257×257 weight map still limits **color** edge sharpness; small pits
  may have a soft color rim. Sidewalls use the existing terrain projection, not
  a separately authored vertical asphalt-edge texture. This first mode tests
  geometry, not a final close-up material treatment.
- Reuse the existing road displacement-suppression map for this mode, including
  its bilinear guard and the full extent of edge chips. Terrain layer displacement
  and visual vehicle deformation are suppressed there to avoid tearing cut rims.
- Damage from one road avoids overlapping contours deterministically. Overlapping
  pits from different roads fail before publishing replacement meshes. Junctions
  remain unsupported. Max 8192 accepted pits/100000 candidate positions per road,
  pit diameter at most 128 m, with the existing per-chunk vertex/index budgets.
  Generation occurs during the terrain build, not during camera movement.

Validation for this mode:

- `dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj -- --geometric-asphalt`
  tests geometry, paint exposure, neutral state, exact LOD0/collider topology,
  coarse-contained holes, mixed spatial LODs, translated chunks, projected area,
  wall coverage, winding and budget/overlap failures. Managed checks only.
- `Tools → Local Terrain → Validate Geometric Asphalt (no scene changes)` uses
  an owned preview scene and native MeshCollider floor/wall raycasts, checks
  shared LOD and displacement guard, and never saves user scenes/assets. Native
  graphics/material appearance, Burst paint preparation and live performance
  are separate checks; a C# build does not prove them.
- For the scene comparison: choose the new road mode, Apply Pending Changes,
  inspect both shaded and wireframe views at Force LOD 0/4 and a chunk boundary.
  Compare against the old mode or damage strength zero using the same road.

### Geometric asphalt boundary accounting fix

The v1 native check passed (9 floor probes, 9 wall probes, LOD 3270 → 440), but
did not exercise the global terrain boundary collector used by rock transitions.
Convex pit clipping can put multiple short edges opposite one longer edge.
Counting only identical vertex pairs mistook these T-junctions for open contours.
Also skip triangles whose corners collapse to duplicate keys in the collector's
existing 1 mm grid, so they cannot contribute phantom duplicate edges.

The collector now subdivides its open-edge candidates at collinear endpoints
before counting them, across changed and cached chunks together. This is boundary
accounting, not a render-mesh weld, a contour repair or a change to author settings.
Positions are retained. A bounded float round-off guard handles kilometre offsets.
Genuine holes/open endpoints remain; the strict degree-two check still rejects
invalid contours. No shader, collider, pit silhouette or render LOD change.

- `dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj -- --asphalt-boundary`
  reproduces the old branches on 12 four-chunk fixtures (8/32 cells; offsets
  0, 879.375 and 4096 m), with and without a separate rock cut. Checks exact loop
  counts (world border + optional rock hole), closed pit geometry, preserved real
  gaps/open endpoints, and rejection of a non-manifold overlap. Managed only.
- The same Unity menu is now **v2**. Besides the native v1 physics checks, it runs
  those fixtures through production `BuildChunkBoundary`, the incremental cache
  (including a single-chunk replacement) and strict `OrderedBoundaryLoops`.
  A managed pass is not a native v2 result or proof of the user's scene rebuild.

### Dense-corner boundary regression (v3)

The user ran v2 successfully, but Apply Pending Changes subsequently failed with
`overlapping/non-manifold edge after subdividing collinear uses`. On the next
rebuild, after adding diagnostic messages only, the user reported that the error
disappeared. Therefore that successful rebuild alone is **not** evidence that the
remaining numerical issue was fixed or that its exact scene edge was captured.

A denser managed fixture reproduced the same four-use error: a nearby corner
(9.937443, 2.8296936, 122.13885) was treated as lying on multiple differently
oriented edges next to (9.9375, 2.8296897, 122.138725). A separate 1 mm-key collision
left a false narrow closed loop. Fixes:

- Use 0.1 mm **topology** keys, matching the strict contour walker's scale, with
  double multiplication before rounding. Keep the 1 mm seam-normal lookup separate
  and explicitly map back to it. This changes boundary accounting, not vertices,
  mesh/collider geometry, normals or author-facing Weld Distance.
- A nearby point can subdivide an edge only if it has a collinear incident use.
  Check the supporting lines in both directions. Allow length-scaled floating-point
  error when extrapolating an incident edge; very short edges within the rounding
  scale may not infer an arbitrary direction. Still reject more than two uses and
  open/branched contours, and preserve actual holes/gaps.
- Failure messages now include the atomic edge, count and up to 24 source edge
  contributions with invariant round-trip coordinates; no scene export/save.
- `--asphalt-boundary` now tests 24 fixtures, including 64/128-cell dense meshes.
  `--asphalt-boundary-stress` tests 20 seeds × 24 fixtures with the managed cache
  model and strict contour-walker model, including replacement of one chunk.
  Both use production clipping and boundary normalization. Negative controls retain
  genuine open edges and 2 mm/1 cm gaps; true non-manifold counts still fail.
- Unity's same validation menu is now **v3**, running the 24 fixtures through the
  real production cache/walker and the existing native floor/wall collider tests.
  Native v3 and the final live-scene rebuild are separate from managed results.

Verified in this iteration: C# build 0 errors (3 existing warnings on the final
incremental build); managed stress 480 fixtures passed, as did geometry/paint/LOD
regressions, 4935 cache checks and static asphalt integration contracts. The user's
native v3 run in Editor.log passed 24 fixtures, 9 floor and 9 wall raycasts,
LOD 3270 → 440, and unchanged LOD0 collider. The final algorithm's appearance and
performance in the authored scene are not measured by those tests.

### Spline-generated inset road (Geometric Asphalt v4)

This replaces the implementation of the existing serialized mode 3; it is not a
new mode or a conversion of the user's scene. Other road modes are unchanged.
Instead of relying on the terrain's triangles to describe the carriageway,
`LTRoadInsetMesh` generates a longitudinal/transverse spline grid, subtracts its
footprint from the land, and stitches the two surfaces before simplification.
The result stays in the chunk mesh with the existing terrain material and spline
texture projection. It does not place another overlapping renderer on the land.
Existing pit floors, vertical walls, edge chips and paint masks remain supported.

Controls in **Форма и рельеф**:

- **Шаг сетки полотна, м** (`insetCellSize`, default 1): maximum source-grid step
  along/across each spline span. Smaller values give a denser carriageway. Cell
  cuts and curve samples can add shorter edges; this is not an exact triangle
  size/count guarantee.
- **Размер ячейки terrain, м**: density around the road. Deep inside the removed
  footprint, a coarser carriageway can release redundant terrain subdivision.
- **Упрощение полотна LOD** (`insetLodReduction`, 0–2, default 1): additional
  connected-surface reduction as terrain cells coarsen. Zero retains the ribbon
  grid without this additional reduction; ordinary terrain LOD still operates.
  Two is stronger. The same edge is shared by land and road; LOD cell boundaries
  retain their piecewise profile for neighbouring/mixed levels.
- **Шаг вдоль пути, м** still controls curve sampling. A finer curve sampling
  interval can make the longitudinal grid finer than the carriageway step.

All geometry/LOD variants are produced during rebuilding, including staged,
whole-chunk, spatial and collider paths. Camera movement selects baked indices.
The collider retains LOD0. Density/reduction changes invalidate geometry; texture
phase alone does not. No new shader/material/bake mode was introduced.

Robustness checks/fixes:

- Canonical cell-edge height profiles keep banked road/land joins consistent.
  Double precision is used for clipping and spline profile offsets before
  conversion to chunk-local vertices.
- Conform generated T-junctions before simplifying; remove exact duplicate index
  triplets introduced by near-collinear clipping, not unique faces or contours.
  Preserve upward winding and protect exterior cell edges during reduction.
- Boundary caching now chooses representatives/normals only from vertices used
  by the supplied indices. Inactive vertices from another LOD in the shared bank
  cannot replace an active boundary position through a 0.1 mm-key collision.
  Strict contour validation and genuine-gap negative controls remain enabled.
- Folded profiles, intersecting ribbons/self-intersections and budgets produce a
  clear build failure rather than publishing that replacement mesh. Junctions
  are not supported by this mode. Source grid budget: 300000 faces per chunk;
  conforming work is capped at 12000 unique vertices per LOD cell, in addition to
  the existing chunk vertex/index limits.

Managed verification:

- `--inset-road`: independent density, zero/partial authored cuts, overlapping
  ribbon rejection, budget/hash checks, damaged floors/walls, projected area,
  no duplicate/inverted faces, exact spatial/whole LOD0 parity and closed
  boundaries. Basic fixture: **6608 / 1362 / 435 / 173 / 77** triangles.
- The shared numerical checker exercises straight and curved/banked roads across
  two chunks at origins 0 and 4096 m, five uniform levels and 48 mixed layouts per
  configuration. Default, stronger reduction and damage configurations pass.
  `--inset-road-unreduced` adds the zero-reduction case: **192 mixed layouts** in
  total. Monotone counts are checked for the reduction-enabled fixtures; zero
  reduction intentionally does not promise reduction of the independent grid.
- Legacy geometry/damage, road/UV/projection, terrain asphalt, spatial LOD,
  emitter parity, rock simplification and boundary/cache regressions pass.
  The boundary invalidation regression now has 4938 checks including inactive
  shared-bank vertices. Static asphalt integration contracts also pass.
- C# build passes. These are managed/source checks, **not** a native import,
  rendering, Burst or live-scene performance result.

Unity handoff: run **Tools → Local Terrain → Validate Geometric Asphalt (no scene
changes)** (v4 / Inset) after compilation. It uses an owned preview scene, the real
production boundary cache/walker and native collider floor/wall raycasts; it does
not save the user scene. Then Apply Pending Changes with carriageway step 1 and
reduction 1, and compare Force LOD 0/4 plus automatic mixed LOD at the road edge
and a chunk border. Native v4 and the authored-scene appearance remain pending.

### Captured sub-weld corner follow-up (v5)

The user's native v4 check passed: 9 floor and 9 wall raycasts, LOD 4088 → 628,
the shared inset fixtures and 24 boundary fixtures. Their subsequent scene rebuild
failed in the boundary collector, not in that validation: three source edges
around `(884.375, 7.25532055, 226.5)`, `(884.375, 7.255383, 226.5)` and
`(884.375, 7.255379, 226.500229)` were counted as a three-use atomic edge.

`--asphalt-micro-corner` reproduces the exact logged exception before the fix.
The first two points are only about 62 micrometres apart, but fall on opposite
sides of the 0.1 mm quantization bucket boundary. The existing strict contour
walker welds within a 0.1 mm radius; the preceding edge counter previously did
not, and its absolute collinearity tolerance could fold this tiny triangle into
a false triple use.

The boundary resolver now canonicalizes endpoints at that **same fixed 0.1 mm
radius before counting**, deterministically and without transitive chain growth.
Render/collider vertices are not changed. A short-edge angular guard prevents
resolvable small triangles from being classified as collinear; its uncertainty
accounts for float rounding on varying axes, so kilometre-offset dense fixtures
remain valid. Actual multi-use edges and the strict closed-loop check still fail.

Verified: exact captured case in six edge orders, through the managed cache and
strict-walker model; retained 0.15 mm-scale contour and genuine 2 mm/1 cm gaps;
rejected real triple-use edges; 480 dense four-chunk stress fixtures; 192 mixed
inset layouts; 4938 cache checks; C# build. No live-scene success is inferred from
these results. The Unity menu is now **v5 / Inset** and includes the captured
corner through the actual production cache/walker. Native v5 and another
Apply Pending Changes in the user's scene are pending.

### Opt-in chipped edges and varied pit outlines (v6)

In **Geometric Asphalt** only, the road inspector now offers **Попробовать сколы
и разные формы**. This sets bevel width to 0.12 m, bevel depth to 0.05 m and shape
variation to 0.7. Existing roads default to zero for these three controls; their
appearance is not changed just by compiling. Zero width or depth removes the
bevel, and zero variation restores the old deterministic outlines.

- Eight convex, unevenly spaced outline corners remain the shared paint/geometry
  footprint. Variation changes size, aspect ratio and rotation without adding
  contour vertices. This is faceted chipped damage, not arbitrary concave holes
  or debris generation.
- A homothetic inner contour creates a sloping lip above the existing lower wall
  and floor. Width varies between pits and is capped at 35% of the centre-to-edge
  clearance; lip depth is capped at half the full pit depth. Double-precision
  clipping preserves the original triangle plane and the same profile across
  mixed LODs and chunk borders.
- No shader, texture, extra layer, renderer or material was added. The bevel and
  floor expose the existing underlying terrain paint. Small colour edges remain
  limited by the terrain weight-map resolution.
- Only the minimum contour/facet shape stays fixed. Additional splits follow
  the road/terrain tessellation. Coplanar convex outside fragments are merged
  only when their combined hull preserves area, with bounded bake work; no
  concavity or hole is bridged. Collider geometry remains LOD0.
- Reject double-precision collinear fan triangles before float conversion, so
  rounding cannot turn a zero-area face into a third edge use. Internal boundary
  accounting now runs in a translation-free chunk frame first; global keys add
  translation in double precision. This avoids interior edges hiding a nearby
  chunk border at kilometre offsets. The existing 0.1 mm endpoint-weld radius is
  unchanged. Newly coincident pairs caused by that weld cancel before collinear
  subdivision; original duplicate/multi-use edges remain detectable. The precise
  local frame uses one weld radius plus float rounding as its line tolerance.

Managed checks passed: 4612 deterministic pits across sizes 0.1/1/3 m and origins
0/4096 m; 120 mixed direct-damage layouts; 144 mixed inset-road layouts with
default/strong/disabled reduction; 480 beveled and 480 legacy dense four-chunk
boundary fixtures (8/32/64/128); the existing road, paint, height, spatial LOD,
emitter and cache regressions. The five-level curved beveled fixture has
5187 / 2886 / 1780 / 1722 / 1722 triangles; monotone reduction is asserted for
enabled reduction. The direct single-pit fixture reduces 8748 to 60 triangles.
These counts are test fixtures, not measurements of the user's scene.

The Unity menu is now **Validate Geometric Asphalt (no scene changes), v6 / Bevel**.
It adds the beveled boundary/inset fixtures and native raycasts on the lip as
well as the wall and floor, all in an owned preview scene. C# and managed checks
do not prove native import, physics cooking or appearance: the v6 Unity run and
visual inspection in the user's scene are still pending. No user scene was saved.

### Opt-in connected wear prototype (v7)

In **Geometric Asphalt**, **Тест: изношенная дорога** enables
**Связанные зоны износа (тест)** and sets strength 1, pit probability 0.8,
spacing 7 m, size 4.5 m, maximum depth 0.06 m, broken edges 0.3, cracks 0.45,
retained floor asphalt 0.7 and repair patch strength 0.75. It does not change
road density, LOD settings, materials or scene objects. The preset is an undoable
inspector edit; existing roads remain unchanged until opted in. Turning off the
toggle restores the legacy generator at the current numeric settings, not a
saved copy of the pre-preset settings. Undo the preset for that full restoration.

- Worn stretches contain one concave 12-corner footprint with three connected
  lobes and narrow necks. Widths, positions, depths and retained asphalt vary by
  seed. This first prototype does **not** perform arbitrary unions between roads
  or zones, create islands/debris, or synthesize photoreal fracture textures.
  Intersecting candidates within one road are skipped; cross-road overlap still
  fails before mesh publication. Convex bevel/shape controls are disabled here.
- The immutable footprint is partitioned once into disjoint convex pieces by
  ear clipping and convex merging. Subtraction/floors cover the same area as the
  concave silhouette. Walls are emitted only on the true exterior, never on
  partition diagonals. Re-entrant bays retain intact top asphalt.
- Coplanar top fragments conform exact double-precision T vertices before
  emission. A centre fan is used only when collinear edge splits need retaining;
  ordinary polygons keep a corner fan. Adding centres to very thin fragments
  caused sub-weld residuals in development, so that failed approach is not used.
  No boundary tolerance or strict closed-loop rule was relaxed for this feature.
- Floor asphalt is a mix with existing substrate paint, not another layer.
  Repair patches use the previously unused G channel of the existing RGBA damage
  influence map: the existing fetch supplies both cracks and repairs. Patches
  darken the original asphalt, adjust smoothness and attenuate cracks; no new
  texture-array slice per patch, renderer, terrain slot or fragment noise loop.
  The R-only legacy path stays neutral when G is zero. Repair-only roads retain
  their influence binding even when crack strength is zero.
- Patch ownership follows the same ordered compositing as layer weights,
  including later clean roads, biome overlays and wheel layers. Floor/patch
  parameters invalidate paint, not geometry. Connected shape settings invalidate
  both. Other road modes ignore all three new controls.
- Road/terrain tessellation still simplifies at build time; camera movement only
  switches saved indices. The minimal 12-corner silhouette currently remains
  fixed across LODs. This is not full distance-based contour collapse. Colour
  edges and small patches remain limited by terrain influence-map resolution.

Managed checks: `--asphalt-connected` covers 6032 contours (24 seeds, 0.1/1/4.5 m
sizes, origins 0/4096 m), exact convex partitions, deterministic geometry, retained
floors, neutral/hash contracts and ordered patch ownership; 120 direct mixed LOD
layouts and 25155 worker paint pixels. The direct fixture simplifies 8440 to 86
triangles. `--asphalt-connected-inset` and `--asphalt-connected-strong` cover 96
mixed straight/curved/banked layouts; the ordinary example reduces
6980 / 4309 / 2980 / 2644 / 2644. `--asphalt-connected-stress` covers another 384
mixed layouts with the actual worn preset and 192 four-chunk boundary fixtures
at resolutions 8/32/64/128, over eight seeds. All assert monotone LOD counts,
closed boundaries and exact LOD0 render/collider agreement. These are synthetic
fixture counts, not live-scene performance measurements.

Native handoff: **Tools → Local Terrain → Validate Worn Asphalt (no scene changes)**
runs **Geometric Asphalt v7 / Connected** and **Terrain Asphalt v2 / Patches**.
The first checks the production boundary collector plus owned preview colliders
(floors, walls, intact concave bays and legacy bevels). The second compiles the
shared raster shaders and checks actual baker pixels for cracks-only,
repairs-only, mixed and neutral masks at slots 1/8/11, plus atlas lifetime.
Neither saves scenes/assets. C# and managed/static tests passed; these native
runs, native paint jobs, HDRP appearance and live performance remain unverified
until run in Unity. For the visual trial, use a short test road, click the preset,
then Apply Pending Changes; compare near/far and Force LOD 0/4 in shaded view.

### Optional module-end fitting (Road_1_Line)

In **Asphalt > Module**, enable **Автоподгонка торцов**. Start with
**Длина сглаживания торца = 0.5 m**, **Макс. разница торцов = 0.05 m**.
Both distances use imported source-space metres, before width/length fitting to
the spline. Existing roads default to OFF. The original strict endpoint check
still applies when OFF. The output status reports measured mismatch and the
effective blend band (limited to 45% of module length per side).

Preparation pairs close end-profile positions bijectively and checks matching
longitudinal rim connectivity (not cap triangulation). Import UV/normal splits
are grouped by position within 0.01 mm only for this geometric correspondence;
the actual mesh keeps all split vertices, submeshes, UV0–UV3 and colours.
Both ends use the same midpoint profile. An interpolating offset field fades
with smoothstep into the module, leaving vertices outside the end bands intact.
Normals/tangents follow the deformation; similar end shading frames match while
hard edges and opposite cap normals stay separate. Flipped/collapsed triangles,
ambiguous matches, incompatible rim topology and excessive mismatch fail before
publication. This is not a general mesh repair/remeshing/self-intersection solver.
Sparse source triangles cannot acquire extra curvature without extra vertices.

Correction runs on owned arrays **before** automatic LOD simplification. Authored
LODs are fitted against the same LOD0 reference and must retain compatible rim
connectivity. All levels retain the exact shared profile. The source cache key
includes the toggle, band length and tolerance; serialized inspector changes
already invalidate the road bake queue. FBX, prefab, materials and user scenes
are never edited by this correction. Terrain protection under separate asphalt,
collider-at-LOD0 policy and other road modes are unchanged. No terrain-layer blend
shader is added here. Geometry alignment does not make arbitrary UVs/textures tile.

`dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj -- --road-module`
checks the existing module path, exact mixed-LOD curved/banked seams, authored
profiles, split attributes, unchanged interior/source, deterministic fitting and
invalid-input guards. A read-only binary FBX probe also exercises Road_1_Line's
actual control points/polygons (1367 points, 2390 triangles, 10 points per rim,
27.91 mm maximum mismatch). Its normals/UVs are synthetic, so these are **not**
Unity importer or visual tests. The raw-point simplifier gives 2390/1194/596/428
triangles; imported UV/hard-edge splits can limit native simplification more.

Native follow-up: **Tools > Local Terrain > Validate Road Module Ends (no scene changes)**,
log tag **[Road Module Ends v1]**. It reads the actual imported prefab/mesh in an
owned preview scene, checks opt-out and cache limits, all 16 mixed-LOD joins,
native mesh channels and render switching with the collider retained at LOD0.
It verifies source bytes/mesh hashes and user scene dirty/root state afterward.
It does not save scenes/assets, run physics raycasts or establish rendered quality.

### Module pool + asphalt vegetation exclusion (v2)

**Asphalt > Module > Набор случайных модулей** replaces the single-module fields
when nonempty. **Добавить текущий модуль в набор** copies the legacy assignment;
the drop area accepts multiple persistent prefab/FBX/Mesh assets from Project.
Each entry supports its own prefab or Mesh, material slots, authored LOD meshes
and nonnegative weight. Weight zero disables that entry. An empty pool retains
the old single-module path. At most 32 entries are supported.

The separate **Seed порядка модулей** drives a deterministic integer hash,
not UnityEngine.Random or camera/time. All LODs/chunks share the same sequence;
changing chunk size does not reshuffle. **Без одинаковых соседей** excludes the
previous entry when at least two are enabled (two entries therefore alternate).
Weights apply among the eligible entries, so exclusion affects their final ratios.
All sources are normalized longitudinally to the first active module's length;
the configured repeat length then fits all slots equally to the spline. This
first version does not concatenate modules at independently authored lengths.
The first module defines the common X centre/width and Y reference. Prepare
matching cross sections; close profiles can use shared end fitting. Every rim
position/connectivity is checked across every module and LOD. UVs/colours and
submesh materials survive packing; an Asphalt Material override still overrides
all slots. Unassigned optional channels use zero UV / white colour. Imported
models and materials are not edited. No arbitrary 180-degree flips are applied.

**Растительность и камни > Без травы под мешем** is enabled by default and gated
by **Убирать растительность** and Asphalt mode. It adds a full root exclusion
inside the XZ projection of the real owned LOD0 mesh, plus **Отступ травы от меша**
(default 0.35 m, terrain-local units). It is independent of paint opacity,
vegetationFade and render LOD. The legacy spline removal mask remains additive;
holes in the new mesh mask do not restore vegetation removed by that old mask.
Larger bushes rooted outside the exclusion can still overhang; increase padding
if necessary. This affects Local Terrain Detail Renderer Vegetation entries,
not hand-placed objects, Unity Terrain details or entries categorized as Stones.

`LTRoadFootprint` builds an immutable projected-triangle BVH. The per-renderer
cache reads only LOD0 of active, explicitly owned generated sections, never the
current MeshFilter LOD; Undo-retained inactive sections are ignored. Mesh identity,
transform, editor dirty count and padding invalidate the footprint and affected
cell placement hashes. A successful road bake requests a surface refresh even
when no terrain paint/height data changed. Offroad and other modes keep their
existing removal rules. No shader or extra draw call is introduced by this mask.

Managed `--road-module-mix` checks the previous module/end tests plus a weighted
10000-slot sequence, disabled weights, repeat exclusion, prefix stability,
three different module lengths/densities across four LODs, exact mixed seams,
two material slots, missing optional channels and chunk-partition invariance.
The footprint check compares 11921 points with a rectangle-ring reference and
checks real holes, corner/edge padding, broadphase and translated coordinates.
`--terrain-asphalt` and `--road-projection` are regression checks for unrelated
road geometry/paint/root removal behaviour. These are managed tests, not native
renderer or live-scene performance measurements.

Read-only probes of the two supplied raw FBX files found that **Road_2_Line is
not currently compatible with Road_1_Line at the default settings**: their rim
point counts differ. Increasing the positional tolerance does not fix this.
The assets were left unchanged; prepare a common end topology in the source
models before putting those two together. The native pool validator below uses
an owned compatible variant of Road_1_Line, not a claim that Road_2_Line passes.

Native menu: **Tools > Local Terrain > Validate Road Modules and Grass (no scene changes)**.
The older Module Ends menu aliases the same **[Road Modules v2 / Grass]** check.
It adds an owned second variant derived from the imported Road_1_Line (different
interior geometry, length and material), checks pool/cache/seed/weight changes,
LOD switching with collider retention, pavement-root probes, footprint reuse
across LODs and invalidation on transform, disable and opt-out. No scenes/assets
are saved. Native run and visible grass/module joins require the user's Unity
check; C# compilation and managed tests alone do not establish those results.

### Automatic module joints (opt-in)

In **Road > Меш асфальта**, enable **Автоматические стыки** to replace only the
end zones of generated module copies, including LOD0. The previous strict fitting
and authored-LOD collar mode remain the default; enabling this mode does not edit
the FBX/prefab assets or save the scene. Joint generation is editor-time, not a
runtime mesh operation. Runtime LOD distances and the LOD0 collider policy are unchanged.

One simplified cross-section from the first active module's retained centre is
shared by every module and LOD. Different vertex counts and triangulations connect
to that common boundary; dense LOD0 strips are not copied into each far LOD.
Central triangles and their UV0–3/normals/tangents/colors remain exact after the
existing module-length normalization. New end attributes are sampled from the
same module/LOD/material. Existing planar caps/end walls are rebuilt where supported.
Different authored materials/UVs can still produce visible texture seams.

Controls: **Зона перехода** (source-space metres on each end, maximum 45% length),
**Допуск формы стыка** (allowed section/surface deviation), and **Точность общего
профиля** (outline simplification error; smaller means more shared boundary vertices).
Defaults are 2 m / 0.5 m / 0.01 m. The current updated Road_1/2 plus older Road_3
fixture uses **2 m / 1 m / 0.01 m**, producing a 9-point common profile. The 1 m
tolerance is a deliberate fixture/user choice, not an automatic relaxation of limits.

This is not arbitrary-mesh repair. Non-manifold edges, mismatched/disconnected cut
contours, excessive shape deviations, collapsed/flipped bridges, and isolated holes
wholly inside a replaced band are rejected without replacing existing road output.
Nested cap contours (holes in end caps) are currently rejected rather than filled.
Choose a band that avoids such damage, or prepare a compatible source end region.
Meshes still need longitudinal segments for bending; this mode does not resample
the authored central mesh along the spline.

Managed `--road-auto-joints` covers synthetic open/closed profiles, caps, tiny cuts,
unequal densities, split UV/normal/material channels, centre holes and safety guards;
it also checks the current 9 raw FBX LODs and 567 bent mixed module/LOD seams.
The native **Validate Automatic Road Joints (no scene changes)** menu uses owned
preview objects and verifies source/settings/scene hashes, the actual imported
Road_1/2/3 mixed pool, cache invalidation, output mesh channels, seams and LOD0 collider.
On the current assets it passed: 9 profile points, 10,928 preserved centre triangles,
567 mixed seams; no source assets or scene saved. Visible shading/texture seams and
physics raycasts remain separate checks, not implied by these results.

### Authored LOD collar precision regression (Roads_1)

`LTRoadModuleCollar` now computes geometric cut-face normals with double-precision
cross products and explicit normalization. Unity's `Vector3.normalized` used to
return zero for a valid tiny clipped face, falsely triggering the inverted-bridge
guard on the supplied `Roads_1/Road_1` LOD1 at a 2 m collar width. Triangle winding,
the inversion guard, source assets and authored central LOD geometry are unchanged.
The failure message also includes the connecting triangle's coordinates.

Managed checks: `--road-collars` adds a near-row cut with nonzero faces below
Unity's normalization threshold and confirms that a genuinely folded bridge is
still rejected. `--road1-collars` reads Road_1/2/3 raw FBX geometry, tests all six
LOD1/2 collars at the saved 2 m width, exact central channels, source hashes,
paired cut edges, determinism and 189 curved/banked mixed-LOD joins. FBX probe
channels are synthetic: this is not native import, rendering or physics proof.

The **Validate Authored Road LOD Ends (no scene changes)** menu is now v2. It
checks the three imported current prefabs, the Road_1+Road_2 pool and the legacy
Road_1_Line fixture in owned preview scenes. It verifies original dependency,
scene/settings hashes, native mesh channels, shared end positions and LOD0
collider retention; it never saves scenes or source assets.

Separate limitation: the current raw Road_3 LOD0 has a different rim point count
from Road_1. Individual collar success does not make those two modules compatible
in one pool. This change does not rewrite Road_3's LOD0 ends.

### Strict Fuse road ends (2026-10-09)

Road > Mesh asphalt > **Fuse торцов** is opt-in and takes precedence over both
legacy end fitting and automatic transition strips. Existing scene settings and
source FBX/prefabs are not migrated or saved. The user explicitly chose strict
position-only Fuse: incompatible LODs fail; no topology repair, level removal or
fallback surgery is performed.

`moduleFuseTolerance` defaults to 0.0001 m (0.1 mm) and bounds the full 3D
displacement in the first module's source coordinate system, after the existing
length normalization and before spline bending. `moduleFusePlaneTolerance`
defaults to 0.0001 m and identifies the narrow end band; it must not exceed the
displacement tolerance. Keep this band narrow when increasing lateral tolerance,
so that nearby interior rows do not become end vertices. Both controls, the mode,
source dependencies and an algorithm version participate in cache invalidation.

One immutable target rim is taken from the first active LOD0 front. Every module
and LOD must have a bijective, tolerance-bounded mapping with identical rim edge
connectivity. Cap interiors are not rim anchors, so cap triangulation may differ.
Coincident UV/hard-normal/material splits remain separate vertex indices. Fuse
copies all arrays, only snaps end positions, checks for newly collapsed/flipped
faces with double-precision cross products, and verifies exact resulting rims.
Vertex counts, triangle/submesh indices, normals, tangents, UV0-3, colors, holes
and positions outside the search band are preserved. It does not repair bad
source winding or remove existing internal cap faces between repeats. Source
attributes remain authored, so geometric continuity does not guarantee a seamless
normal map or material transition.

Managed check: `dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj -- --road-fuse`.
Synthetic checks cover open/closed and capped rims, cap fans, split vertices,
reordered indices, tiny offsets, center holes, idempotence, all channels, retained
auto-LOD boundaries, invalid settings/nonfinite data, duplicates, missing rim
points and collapsed-face rejection. The read-only current Road_1/2 raw-FBX
audit checks LOD0/1/2 individually and all combinations of successfully snapped
meshes after curved/banked bending. FBX normals/UVs are synthetic in this managed
reader; this is not a native Unity import or visual test.

Current asset result: Road_1/2 LOD0/1 are compatible without moving vertices;
112 mixed bent seam combinations pass. Full LOD chains are NOT ready: even at
1 mm displacement / 0.1 mm search, Road_1 LOD2 has 6 versus 7 rim points at the
back; Road_2 LOD2 reports a collapsed/overlapping rim edge. At the default
0.1 mm displacement Road_1 LOD2 first exceeds the lateral tolerance (~0.834 mm).
These asset incompatibilities are printed separately, not counted as successful
LOD2 builds. Preserve a common end contour when authoring those LODs; increasing
the tolerance is not a topology repair.

Native menu: **Tools > Local Terrain > Validate Road Fuse (no scene changes)**.
It hashes source dependencies, scripts, settings and open scenes, uses only an
owned preview scene, audits the imported Road_1/2 LODs, checks all accepted mixed
seams, and builds a transient LOD0/1 pool for native output/collider/cache checks.
It also attempts the entire authored chain and confirms that incompatible LOD2
is rejected, not silently removed. A PASS for compatible levels is accompanied
by an explicit ASSET INCOMPATIBLE warning while the full chain remains blocked.
No source meshes, user scenes or materials are saved. Native execution remains
a separate user-run step; C# compilation and managed checks do not prove render
quality, physics raycasts, or full-road construction speed. No speedup measured.

### New Road_Shader Shader Graph ground blend

The `Roads_1/Road.mat` material can keep its original `Road_Shader.shadergraph`.
The road baker selects the owned `LTRoadGraphGround.shadergraph` derivative when
ground blending is enabled; original materials and imported meshes are untouched.
Both single-ground-layer and automatic terrain-layer modes use the existing rim
distance mask, shared by all road LODs, including rims around holes.

The derivative retains every original graph node, slot, property and HDRP setting.
Only the final base colour, tangent normal, smoothness, AO and metallic pass through
a ground-blend custom function. Red vertex colour still mixes the two authored
asphalt texture/normal sets with their own tint and tiling. The original graph's
unconnected mask properties remain unconnected. Zero blend preserves the exact
original surface values. World position and tangent-frame inputs are explicit
graph nodes; no generated shader text or package source is patched.

After saving edits to the source Shader Graph, use **Обновить адаптер бленда** in
the Road inspector, below **Перестроить / запечь** (Asphalt + ground blend enabled).
This updates the project's shared road graph adapter, not the source graph or
material. Then rebuild the affected roads to copy their current material settings.
The button does not invoke a road bake or save scenes. It keeps the adapter GUID
and backs up the previous bytes under `Logs/RoadGraphAdapterUpdates/<unique-id>`.

The in-Editor generator requires the existing opaque HDRP Lit target settings,
tangent-space normals, all five surface blocks, no vertex displacement and no
reserved ground-blend property names. It copies arbitrary authored nodes and
properties without interpreting their surface math. Unsupported target changes
fail before writing. Both single-layer and automatic-terrain variants of six
native passes must compile before the new fingerprint state is accepted. Failure
restores the previous adapter/state; backups and diagnostic logs are retained.
Shader Graph changes must be saved first; unsaved graph-window edits are not read.

`Tests/RoadGraphAdapterChecks` runs the actual C# JSON transformer without Unity.
`Tests/RoadGraphBlendChecks.mjs` independently checks the saved adapter against
the source/template/state fingerprints and verifies unchanged authored objects.
`--generation-only` tests the reference transformation without requiring an update;
`--emit` prints a reference derivative for comparison, never writing source files.

Native menu: **Tools > Local Terrain > Validate Road Shader Graph Blend (no scene changes)**.
It checks the imported `Road.mat`, copied properties/render state, both native graph
keyword variants and GPU blend-hook values at exterior/hole rims. It then invokes
the automatic terrain validator (v5), which compares the graph hook against the
terrain reference and inspects SRP Batcher compatibility for all three adapters.
Both validators use temporary objects and verify source/settings/scene hashes;
they never save user scenes or materials. Static checks and C# compilation alone
do not establish native shader compilation, GPU correctness or visible quality.

## Asphalt foundation terrain LOD

`Road > Форма и рельеф > Упрощать землю под асфальтом` permits ordinary terrain
LOD height-error simplification on shoulders and safe cells under a separate
asphalt mesh. Enabled by default; disable to retain the previous blanket lock.
Rebuild the road first, then use LTWorld **Apply Pending Changes**. Road authored
LODs, Fuse topology, terrain LOD0 and colliders are not modified by this policy.

Version 2 snapshots all generated road LOD surfaces in terrain-local coordinates,
not the currently displayed MeshFilter. The exterior/hole band plus a fine-cell
halo is now an LOD0 height reference, NOT a fixed ring. `Ошибка земли у края LOD, м`
defaults to 0.03 m: larger values allow more simplification while asphalt clearance
remains mandatory. Overlapping roads use the strictest captured edge tolerance in
that chunk. Zero permits only essentially unchanged edge-band surfaces.

An affine upper bound over the emitter's 3x3 stencil is an accept-only fast path.
If it cannot prove clearance, exact projected triangle intersections test all
reachable midpoint/diagonal variants against every overlapping road LOD. Polygon
vertices bound the affine height difference over each overlap. The same exact test
limits positive/negative change against the LOD0 edge reference. Flat edges and
holes can coarsen fully; visible bumps retain detail. This covers both diagonals,
midpoint fans and regular transition triangulation, independent road and terrain
LOD selection, WholeChunk and WithinChunk modes. CPU bake work only; there is no
new shader or per-frame road-intersection calculation.

Missing, stale, hidden, unreadable or invalid baked roads fall back to the previous
whole-influence protection. Asphalt junctions retain their old protection too.
Areas already intersecting at LOD0 are not repaired; GPU displacement is not
evaluated. This is conservative: some cells with small clearance cannot simplify.
Road contact with separate Hole Stamps, mesh cutters or inset geometric roads
retains local protection: those emitters can insert fresh height samples outside
the tested 3x3 stencil. Holes in the authored asphalt mesh itself use the normal
edge-error policy and do not activate this fallback.
Road geometry changes invalidate cached terrain plans even if the spline's height
field is unchanged. Immutable surface snapshots are cached by mesh identity,
dirty count, asset dependency hash, generated transforms and edge error.

Managed: `dotnet run --project Tests/TerrainBridgeChecks -- --road-foundation-lod`.
Native menu: **Tools > Local Terrain > Validate Road Foundation LOD (no scene changes)**.
The native check uses owned preview-scene objects and verifies source/settings/scene
hashes. It checks all-LOD capture, cache invalidation, transforms and conservative
fallbacks in addition to the shared geometry tests. It does not save user scenes
or assets, test the visual result in the user's scene, or establish performance.

## Unused road segments

The road baker keeps obsolete generated segments inactive for reuse and Undo.
After a successful rebuild it explicitly marks them `LTRoadGenerated.obsolete`;
reused segments have the marker cleared. Existing inactive objects need one rebuild
before they can be recognized as obsolete. Deactivation alone is never sufficient.

Road inspector **Удалить неиспользуемые сегменты (N)** removes only marked inactive
direct children with matching road ownership/bake ID, standard generated components,
no children and no prefab-instance connection. Custom/missing components, user
children, foreign output and manually hidden current segments are preserved. The
button uses one Undo group; Ctrl+Z restores removed objects. No mesh/material/source
assets are deleted and the scene is not automatically saved. It never runs on its own.

Native menu: **Validate Road Segment Cleanup (no scene changes)**. Tests selection,
reuse, shared-mesh preservation and removal only on owned preview objects. It does
not exercise or clear global Undo (callbacks can schedule user road rebuilds);
Undo round-trip should be checked with the actual button separately.
