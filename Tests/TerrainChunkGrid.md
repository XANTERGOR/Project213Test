# Editable terrain chunk grid

Open `LTWorld → Геометрия → Управление чанками / восстановление`, or
`Tools → Local Terrain → Manage Chunks` with the world selected.

- Blue cells exist, grey cells are empty; click a cell or enter signed X/Z coordinates.
- **Восстановить все пустые ячейки** fills every absent cell in the current envelope.
  This includes intentionally removed cells; the dialog shows the count before applying.
- **Добавить чанк** fills one cell. Coordinates outside the envelope expand it in any
  direction, up to 64 cells per axis. Old chunk positions, dimensions, source data,
  stamps and source-normalized UVs are unchanged.
- **Удалить выбранный чанк** removes only that GameObject. Mesh files and stamps are
  retained. Undo restores the object; adding/restoring does not overwrite old files.
  Existing child objects block removal. A chunk carrying a closed rock transition is
  also protected: move/disable the mesh stamp and apply it first. Cutting a rock bridge
  into an open rim is not implemented by this grid tool; do not bypass this guard with
  Hierarchy Delete under a rock transition.
- Apply geometry after editing when Auto Update is off. The operation does not save
  the user scene. New mesh files are created in the world's generated output folder;
  Undo does not erase those files. Restore reconstructs terrain from the current source
  and stamps; it is not a backup of arbitrary manual mesh edits.

The source height map stays fixed. Outside it, its clamped edge height continues;
use stamps to shape new land. Holes and disconnected islands are supported by both
the balancing forest and runtime spatial LOD. LOD0 remains the collider source.

Dense topology IDs are derived from stable signed chunk coordinates and the current
envelope. Geometry/paint/rock caches are invalidated when membership changes. Paint
sampling, detail residency, deformation and global atlas mapping use the expanded
extent. Existing distant global maps do not match an expanded envelope and fall back
to detailed materials until explicitly rebaked. Road experimental baking remains removed.

## Verification

Managed checks:

```powershell
dotnet run --project Tests/TerrainBridgeChecks -- --chunk-grid
dotnet run --project Tests/TerrainBridgeChecks -- --spatial-lod
dotnet run --project Tests/TerrainBridgeChecks -- --grid-regressions
```

`--chunk-grid` checks stable positive/negative expansion, UVs, sparse and empty grids,
deletion of neighbours without rebaking surviving LOD assets, required contour midpoints,
manifold/matching live edges, return to coarse LOD, negative paint sampling, vegetation
bounds and cache invalidation. Regression checks are managed CPU/source checks, not
native Unity/Burst, graphics, or player performance results.

In Unity, run `Tools → Local Terrain → Validate Chunk Grid (no scene changes)`.
It uses transient meshes in a temporary preview scene to exercise the management API,
Undo, signed expansion, runtime LOD selection, collider ownership and the production
geometry preparation path. It does not save scenes or create persistent assets. Log:
`[Chunk Grid v1] PASS` / `FAIL`. It does not prove GPU shader correctness.

For the real world: restore the missing cells, apply geometry, compare Force LOD 0/4,
check seams and colliders, then test one empty cell and an exterior cell in each direction.
Check paint/road UVs and vegetation, and validate global-map graphics separately if used.
Save the scene only after reviewing the result.
