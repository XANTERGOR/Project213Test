# Automatic grass terrain color (experimental)

Open **Tools → Local Terrain → Grass Color**, drag materials or prefabs into the
list, check the desired materials, then **Попробовать оттенок земли: сила 0.5**.
You can also add the Project selection or use **Добавить из префаба** (all child
MeshRenderer material slots, including inactive children/LODs). Duplicates and
incompatible materials are skipped. Adding/removing/checking list entries does not
change materials, shader assignments or scene objects. The list persists with the
editor window; the previous single-material reference is migrated automatically.

Field edits affect only that property on the checked, compatible shared materials,
with a single Undo record. A dash means differing values; untouched fields retain
each material's value. Unchecked materials are not edited. Presets change their
section only. The window does not call SaveAssets or save a scene. Defaults are off.

- **Сила подхвата / Terrain Color Strength**: 0–1, zero disables.
- **Высота перехода (лок.) / Terrain Blend Height**: mesh-local height above the
  existing Root Base Height. Independent from the manual root-tint height; the test
  button estimates 60% of LOD0 height only with one checked material and a matching
  optional prefab. With multiple checked materials it preserves individual heights.
  Editing the height field explicitly assigns one height to all applicable checked
  materials. Neither path changes the base, manual root tint or variation settings.
- Manual root tint is applied afterward and can darken roots additionally. For a
  clear A/B test temporarily set manual Root Strength to zero (restore with Undo).

This works on compatible DA_Grass_WIND parts drawn by Local Terrain Detail Renderer,
both CPU instancing/merged batches and the indirect adapter. No material per clump.
External MeshRenderers are unchanged because they have no per-world map binding.
Other vegetation sharing this shader/material also receives the effect.

## Rendering and cache

The existing stable planting-root transform samples one texture in the vertex stage,
then carries RGBA to the fragment stage. Sampling skips when strength or map readiness
is zero. Wind/camera position/LOD do not define the sample point. Mesh-local pre-wind
height provides a smooth fade; alpha, wind and normals are unchanged. Existing color
variation runs first, manual root tint last. The target transfers ground hue and
half its luminance, retaining grass luminance variation/detail; very dark colors use
a guarded denominator, and the target stays in the albedo 0–1 range.

`LTGrassTerrainMap` owns a separate, transient **linear** albedo RenderTexture and a
temporary baking material per world. It uses LTGlobalLayerBake's existing unlit near
layer evaluator (textures, tints, height blend, spline UVs) and LOD0 geometry for
triplanar projection. No screenshot, lighting, shadows, CPU readback or manual bake.
Existing saved far maps, their settings and their explicit bake workflow are untouched.
Ground roads painted into terrain are included; separate asphalt/rock meshes, decals,
shader wetness/deformation appearance and light-dependent transmission are not captured.

The cache is requested only by compatible cached detail recipes with nonzero material
strength. Material layer rebinding increments a revision; geometry revisions/matrices
are tracked for triplanar tiles. Unchanged tiles are not redrawn. Up to four dirty tiles
are submitted per Advance call, with a soft 2ms CPU submission budget. This is **not**
a GPU-time cap: a single tile render/allocation cannot be interrupted. The detail tick
advances the queue; painting polls synchronize it. Temporary old colors remain until
an updated tile is submitted. Topology/layout/transform changes clear the cache and
refill it to avoid old deleted-chunk colors. Alpha zero marks unavailable/sparse tiles,
which leave grass unchanged. Shared materials are bound through property blocks, never
global shader state. Renderer/world disable releases the cache; no serialized assets.

Nominal texel spacing is 0.5 terrain-local units, capped at 4096 pixels per dimension
and the GPU texture limit (at most 64 MiB RGBA32, no mips, per world). Large worlds lose
small color details; current spacing and progress appear in the Grass Color window.
Texture allocation and very large/many-layer tiles can cost time. No FPS, player-memory
or streaming claim is made; the user's deferred player checks remain deferred.

## Verification

```powershell
node Tests/GrassRootTintChecks.mjs Logs/GrassRootTint-before.json
node Tests/GrassVariationChecks.mjs Logs/GrassVariation-before.json
node Tests/GrassTerrainChecks.mjs Logs/GrassTerrain-before.json
dotnet run --project Tests/GrassMaterialBatchChecks/GrassMaterialBatchChecks.csproj
dotnet build Assembly-CSharp-Editor.csproj --no-restore /clp:ErrorsOnly /p:CustomAfterMicrosoftCommonTargets=G:/UnityProjects/Project213-Testing/Tests/LocalTerrainCompile.targets /p:OutputPath=Logs/GrassTerrain-build/bin/ /p:IntermediateOutputPath=Logs/GrassTerrain-build/obj/
```

Managed tests check graph references, all original objects/non-insertion edges, neutral
defaults, mask math, shader/binding/invalidation source wiring. They are not GPU tests.
The batch-material test links the production editor helper with small API test doubles:
prefab gathering, deduplication, supported/checked selection, mixed values, independent
property edits, no-op handling and Undo/dirty call routing. It does not exercise native
Unity Undo or IMGUI. In Unity, check two materials with different heights/variation,
edit only terrain strength, verify other values remain distinct, and test Undo/Redo.

After native import run **Tools → Local Terrain → Validate Grass Terrain Color
(no scene changes)**. Expected log prefix: `[Grass Terrain Color v1]`. The check uses
only temporary materials, textures and a mesh (no scene objects). It compiles source
and indirect passes and compares real layer-bake/vertex-sampling pixels: transform,
height, sparse/deleted areas, incremental edits, separate maps, triplanar geometry,
zero strength and alpha preservation. It restores graphics state and does not save
scenes/assets. Read its actual PASS/FAIL; a C# build is not a substitute.

Then visually compare strength 0/0.5 near a ground road and across a layer boundary,
including near/far grass LODs and CPU/GPU Details. Wait for the map status to be ready.
This native validation does not prove live instanced rendering or performance.
