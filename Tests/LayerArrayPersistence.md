# Persistent terrain layer arrays

LTWorld → Массивы → **Запечь и сохранить**, outside Play Mode, creates a fresh
`Assets/LocalTerrainGenerated/LayerArrays/<version-id>/` containing Color.asset,
Normal.asset, Mask.asset and Palette.asset. The LTWorld reference is Undoable;
save the scene/prefab normally to persist that reference. No scene is auto-saved,
no existing bake is overwritten/deleted, and no unrelated dirty assets are saved.

The editor waits for streamed source mips without holding a blocking sleep;
cancel, compilation/domain reload, quit and timeout release the job's requests.
The actual GPU packing/readback and asset serialization are synchronous editor
work with a cancelable progress bar between mip levels. Large saves can pause
the editor; this does not occur during normal rendering or saved-array loading.

GPU-only preview arrays are NOT serialized directly. Readback copies every mip
and slice to readable RGBA32 arrays. After writing and importing the four files,
the saved CPU data hashes must match the pre-save hashes before the LTWorld
reference changes. Partial files remain recoverable on failure.

Persistent arrays retain CPU data for serialization (approximately the GPU array
size again in system RAM). The saving GPU budget includes the resident preview,
packing arrays, output arrays and staging texture; source textures, other loaded
bakes and the rest of the scene are additional. No texture compression/export
pipeline was added. Color is sRGB, normal/mask linear; all mip levels are retained.

Valid saved arrays are borrowed directly; runtime cache disposal never destroys
them or mutates their filtering. Missing/stale snapshots use transient preview in
the editor. Staleness checks include palette order, source references, texture
content/import dependencies, resolutions, filtering, color space and pack shader.
Tint/tiling/strength edits do not invalidate packed pixels. Player scene processing
rejects missing/stale snapshots rather than silently shipping runtime repacking.
This scene guard does not validate dynamically spawned/addressable-only worlds.

## Verification

- C# compiler: use `Tests/LocalTerrainCompile.targets` as documented in TerrainRoads.md.
- CPU/source suite: `dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj --no-restore`.
- In Unity, save a small scene's arrays. Check the status says loaded from asset,
  save the scene, then reopen it (or restart the editor). Select LTWorld and run
  **Tools → Local Terrain → Validate Saved Layer Arrays**. This compares every
  stored mip/slice's bytes with its recorded checksum, not rendered HDRP output.
- Check normal terrain/road appearance, then change source texture or resolution:
  expect a stale warning. Change tint only: expect the saved bake to remain valid.
- Bake a second version; old files should remain. Undo/redo the world assignment.
- Cancel during preparation/readback: the previous saved reference must remain.
- Test the build guard with an intentionally stale world, then restore/rebake it.

Native save/reopen and player-build checks require the running Unity editor and
are not established by the C# build or source-contract checks.
