# Detail-zone placement bake

In LTDetailRenderer, use **Запечь зоны детализации**, wait for completion, then save
the scene manually. Output is automatically saved to `Assets/TerrainSystem/GrassGen`
with a unique filename; no file picker. The operation covers all detail categories
owned by this renderer (including grass), independently of cameras.
It never overwrites an existing asset. Cancel/failure preserves the previous assignment.
The ordinary **Сохранить расстановку** button still captures resident cells only.

Repeated baking compares selected cells against the assigned saved asset. Compatible
records with matching input fingerprints, references and valid payloads are copied
directly, without regenerating candidates or restoring instance matrices. Only new,
changed or corrupt cells are generated. The runtime saved-placement toggle does not
disable this authoring reuse. Session reload caches are excluded. Cells no longer in
the selected zones are omitted from the new asset. The progress display and completion
log report reused/generated counts. Selection, painting preparation and fingerprint
checks still run; this does not make a no-change bake instantaneous. Fingerprints
are conservative (for example, paint maps are tracked per overlapping terrain tile).

**Требовать запекание для билда** is enabled by default. Turn it off to permit
builds without baking; missing/stale placement then uses normal runtime generation.
The separate saved-placement toggle still controls reuse of any valid saved cells.

Before generating candidates, the baker selects potential detail zones from layer
coverage, Add/Replace stamp bounds/allowed layers, source density and conservative
noise-mask bounds. Proven-empty cells are skipped. Bilinear neighbours and thin paint
islands are retained. This is conservative: boundary cells or cells later cleared by
roads/slope/removal filters can still produce empty records. A nonzero density floor
means grass remains possible outside the visible dense patches. Progress counts only
selected zones after the coverage pass. Build validation recomputes this selection,
so adding grass in a previously empty area requires a new bake.

**Проверить расстановку для билда** checks current scene state without generating
instances. Actual player builds also validate every enabled LTDetailRenderer requiring a bake in each
processed scene, before height stamps are stripped. Missing/old-format/stale/corrupt
cells, disabled saved-placement use, invalid prefabs, incomplete terrain or failed
paint preparation stop the build with the scene, object and repair instructions.
Play Mode is not blocked. There is no automatic rebake or scene save during a build.
Worlds must be active and fully prepared; inactive hierarchies are not a validation
bypass. Dynamically loaded scenes/prefabs outside BuildPipeline scene processing are
outside this callback's scope.

The baker keeps one generated cell resident at a time and accumulates compact saved
records. Max Cells is not a whole-world bake limit. Max Instances still bounds each
cell; the total serialized asset can be much larger. Wind, LOD and rendering remain
dynamic. The complete asset is loaded as before; this is not a disk streaming change.
Validation uses the production cell fingerprint and payload checksum, including
terrain, paint, road masks, detail settings, seed and referenced sources. Editor-session
reload caches cannot satisfy build validation. Existing fully covering assets can pass
without a special "baked" flag. Transient paint materials are restored after validation.

## Verification

- `dotnet run --project Tests/SceneBuildGuardChecks --no-restore`: production build
  callback with API doubles; ordinary/scripted builds, diagnostics, Play bypass,
  inactive hierarchy, disabled renderers and optional baking.
- `dotnet run --project Tests/TerrainBridgeChecks --no-restore -- --detail-placement`:
  production grid traversal and payload checks, negative/partial cells, exact coverage,
  corruption, content keys, empty payloads, sparse coverage, bilinear borders and
  conservative noise-mask bounds against sampled density.
- Compile `Assembly-CSharp-Editor.csproj` with `Tests/LocalTerrainCompile.targets`.
- The existing native `LTDetailPlacementCheckMenu.Run` fixture now also checks strict
  rejection of stale/corrupt/missing/disabled saved placement without generating
  instances. This native fixture and an actual Unity player build were not run as
  part of the managed checks. No user scene is saved by these commands.

Paint preparation uses the existing Unity rendering/texture paths. A graphics-less
batch run is not a substitute for the native editor/build verification.
