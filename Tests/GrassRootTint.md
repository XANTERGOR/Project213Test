# Grass root tint

The existing `DA_Grass_WIND` Shader Graph has a **Grass Roots** section.
No material is enabled or reassigned automatically; strength defaults to zero.
The existing GrassWind indirect adapter derives from the same graph.

Open **Tools → Local Terrain → Grass Root Tint** (or **Grass Color**). Drag compatible
materials or prefabs into the list, or use the optional prefab field and **Добавить из
префаба**. Check the materials to edit together; uncheck others to retain their values.
The window changes only checked materials (including all of their other users), with
Undo. Mixed fields show a dash; editing one field does not overwrite other fields.
It does not call SaveAssets or save scenes. For independent settings, duplicate the
material and assign the copy to the intended prefab first.

- **Root Tint** / **Оттенок корней**: multiplicative LDR color, not a sampled ground
  albedo. White is neutral; dark warm colors darken/tint roots while retaining texture.
- **Root Strength** / **Сила эффекта**: 0 disables, 1 applies full root tint.
- **Root Blend Height**: distance above the base over which tint fades smoothly to
  the original albedo, in mesh-local units (before instance scale).
- **Root Base Height**: root-plane local Y; accommodates meshes with offset pivots.
- **Попробовать**: warm tint (.6, .48, .33), strength .65. With one checked material
  and a matching optional prefab, estimates base and 30% blend height from LOD0
  mesh-local bounds. With multiple checked materials it preserves individual heights
  and bases. Verify single-material estimates visually, especially when one material
  is shared by differently sized meshes. Variation and terrain pickup are unaffected.

The implementation carries one interpolated scalar: original object-space Y before
wind. Smoothstep is evaluated per fragment, so even a low-poly grass card has a smooth
gradient. Neither vertex positions nor alpha, normals, AO, transmission settings,
geometry density, LOD, distances or culling are modified. No added texture fetches or
render passes; there is scalar-varying and fragment-math overhead, not a measured FPS claim.
Custom interpolators target the graph's existing raster path (ray tracing is disabled).
Optional [automatic terrain color](GrassTerrain.md) adds a separate vertex texture
fetch before manual root tint; its strength defaults to zero.

## Checks

```powershell
node Tests/GrassRootTintChecks.mjs Logs/GrassRootTint-before.json
```

Omit the second argument when the per-edit backup is unavailable. It adds preservation
checks for every original graph object and non-albedo edge. Scalar tests execute the
expressions extracted from HLSL, but do not replace a shader compiler/GPU test.

In Unity, run **Tools → Local Terrain → Validate Grass Root Tint (no scene changes)**.
This compiles source/indirect raster passes and checks the shared HLSL function on a
temporary 1×1 floating-point render target. Source materials/scenes are not saved or
modified. The combined check now reports `[Grass Color v3] PASS` / `FAIL`; see
[GrassVariation.md](GrassVariation.md) for the added color checks and menu alias.
Version 2 uses `material.enableInstancing` without trying to enable the reserved
`INSTANCING_ON` keyword directly. Version 1 passed 22 raster pass compilations and
19 HLSL pixel checks in the user's Editor, but emitted a keyword API error; that
run is not considered a clean validation or proof of live instanced rendering.
It does not prove actual instanced rendering, live wind appearance or player performance.

After native validation, try strength 0/.65 in the same view, orbit the sun-facing and
backlit sides, compare LODs and differently scaled instances. Tops and silhouettes
should be unchanged; check roots remain stable while wind bends the blades.
