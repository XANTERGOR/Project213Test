# Grass color variation

Open **Tools → Local Terrain → Grass Color** (the old **Grass Root Tint** menu is
an alias). Add grass materials/prefabs to the list, check the desired materials,
then **Попробовать мягкий разнотон**. Only checked compatible materials are edited.
The opt-in preset changes only variation settings, not root tint or terrain pickup:

- Multipliers A = (.97, .95, .87), B = (.79, .83, .75), in the material color UI.
- Clump strength .2; patch strength .35; patch scale 12 world metres; seed 1.
- Both strengths default to zero in the shader, preserving existing materials.
- **Выключить разнотон** resets both strengths, retaining root tint settings.
- The two strengths weight micro/macro noise; their sum (clamped to one) is the
  total tint amount. Both use the same palette. White/white is neutral.
- These are multiplicative LDR tints, not replacement albedos. They preserve texture
  detail and can darken/tint but do not brighten above the source texture.
- Seed is an integer 0–65535. Patch size is the smooth noise scale, not a circle radius.

Material edits are shared by every object using the material and support Undo. No
material is changed or duplicated automatically. Neither tool calls SaveAssets nor
saves a user scene. Mixed values appear as a dash: editing one field changes only
that property, preserving each material's other settings. The preset deliberately
sets all variation fields listed above. See [GrassTerrain.md](GrassTerrain.md) for
the shared multi-material window and its managed batch checks. Variation itself
does not sample/bake terrain albedo.

## Rendering

`LTGrassVariation.hlsl` computes smooth lattice noise in the vertex stage, at the
absolute world XZ of each planted root. A high-frequency 0.125m noise produces
per-clump differences; the large-scale noise produces broad color patches. Smooth
noise avoids hard cell-hash discontinuities due to tiny transform roundoff. It is
spatial variation, not random draw/instance-buffer indexing. Moving the plant to
another XZ position intentionally changes its color. Root elevation does not.

The three-channel multiplier is interpolated and multiplied with the original
base texture **before** the existing root-tint function. Alpha, wind positions,
normals, AO/transmission, placement density, distances and LOD selection are unchanged.
No additional texture fetches, keywords, render passes or per-clump materials.
The separate opt-in [terrain color](GrassTerrain.md) adds one vertex texture fetch
between variation and manual root tint. The variation itself is unchanged.
There is vertex arithmetic and a float3 varying; zero strengths have a uniform
early-out. This is not an FPS/standalone-player performance claim.

LocalTerrain's prefab recipe stores each part's inverse-transformed root, exposed
as a hidden `_LTGrassRootInObject` vector. Both CPU and GPU draws supply that offset.
The CPU merged-draw key includes the offset: incompatible child pivots cannot be
merged using the wrong anchor. Compatible draws still batch. The property block
is cached per part, not allocated per instance/frame. Parts with different roots
may require separate draws. No indirect buffer layout is changed.

Outside LocalTerrain, the zero-offset fallback uses the MeshRenderer's own origin.
Independent children with different pivots do not automatically share one root in
that fallback. Static-batched external renderers are not validated here.

## Verification

```powershell
node Tests/GrassRootTintChecks.mjs Logs/GrassRootTint-before.json
node Tests/GrassVariationChecks.mjs Logs/GrassVariation-before.json
dotnet build Assembly-CSharp-Editor.csproj --no-restore /clp:ErrorsOnly /p:CustomAfterMicrosoftCommonTargets=G:/UnityProjects/Project213-Testing/Tests/LocalTerrainCompile.targets /p:OutputPath=Logs/GrassVariation-build/bin/ /p:IntermediateOutputPath=Logs/GrassVariation-build/obj/
```

The optional second arguments compare original graph objects/edges against per-edit
backups. Managed checks cover graph references and preservation, opt-in defaults,
vertex-stage color path, absolute root wiring, CPU/GPU/merge offset code, 16,968 root
mask cases and 19,089 noise/color cases. The hash test executes HLSL uint expressions
translated to JS wrap semantics; this is not an HLSL compiler or a live render.

In Unity run **Tools → Local Terrain → Validate Grass Color (no scene changes)**.
The old root-validation menu runs the same combined check. Expected version:
`[Grass Color v3] PASS` / `FAIL`. It uses temporary materials and a 1×1 floating-point
RT, compiles source/indirect raster passes, compares 19 root and 81 variation HLSL
pixels against independent C# expectations, and checks rotated/mirrored child-root
transform math. It does not instantiate/edit the user scene or save assets.

Native import, GPU pixel checks and live instanced rendering are separate evidence.
The menu does not prove actual CPU/GPU draws, LOD stability or live appearance. For
visual acceptance, enable the soft preset, compare it with zero strengths, then
move the camera while wind runs and compare near/far LODs and CPU/GPU Details modes.
The planted color must remain stable. Check shifted child pivots as well as a plain
single-mesh prefab. The deferred standalone-player/performance work remains deferred.
