# Bridge geometry checks

Current status: automatic intersecting-Union grouping, its BSP kernel and Union Seam Width were removed from production at the user's request. Stamps are generated independently again; intersecting cut regions remain unsupported. The historical UnionGeometry.cs / UnionChecks.cs files below are retained only as experimental test references and are no longer invoked by the console harness. The hidden unionGroupOutputApplied flag remains solely to restore original material slots from previous generated groups.

Run `dotnet run --project Tests/TerrainBridgeChecks/TerrainBridgeChecks.csproj` from the Unity project directory.

The console harness runs a snapshot of the pure geometry methods from `LTEditor.cs`, using Unity's vector math assembly without launching the Editor. Refresh `Geometry.cs` from those methods when editing the implementation; it is a test snapshot, not another implementation used by Unity.

The 73 cases exercise clipped closed rock geometry, cut offsets 0.05–6 m, retopo heights 0.05–6 m, translated/sloped geometry, irregular rim sampling, density changes, and Blend Distance changes. Eighteen cases retain the underground cutter surface with reversed winding (Cave). Checks cover interior edge incidence, consistent shared-edge winding, exact outer-rim retention and finite coordinates. Density independence is checked with distance welding disabled: final weld counts can legitimately change when the shape changes. A separate regression verifies that post-density welding removes connected vertices without opening the rim.

Contour checks cover reversed/duplicate segments, explicit open/branch diagnostics and intersections with tiny signed height differences. These tests do not exercise Unity scene rendering, collider cooking, serialization migration or full terrain-cut generation.

Multi-stamp ownership checks exercise whole-loop selection, rejection of foreign rims, loop-order independence, offset distance and rejection of a merged cut before applying geometry. Union groups are assembled before ownership selection, so their shared contour belongs to one generated group. Merged cuts between separate groups (including Cave groups) remain explicitly rejected.

## Archived experiment: Shared Union surface (removed from production)

Overlapping 3D bounds connect active Union stamps with Cut Terrain and Trim Rock Inside Terrain enabled into a generation group. Source assets and individual transforms remain unchanged. One deterministic group member renders the combined mesh; other members get empty instance outputs until they separate and regenerate individually. This is an editor-generation workflow, not a runtime boolean.

The polygon union removes internal faces, repairs split-edge T-junctions and verifies closed, consistently oriented topology. Only the shared seam band is locally retessellated and relaxed, with displacement bounded to one quarter of the band width and inversion checks. Union Seam Width is independent of density; the group currently uses the mean width. Grid cell size, terrain cut offset and retopo height are spatially interpolated across the group; the Bridge uses a common row layout and variable circumferential sampling. Outside-band vertex positions are preserved. Source meshes must be closed; complex or degenerate inputs may be rejected by validation/time/size budgets before terrain assets change.

UnionGeometry.cs mirrors the production kernel. UnionChecks.cs covers two- and three-solid volume, input order, containment, disjoint solids, translation, outside-band preservation, rotated intersections and four union/remesh/clip/Bridge/weld cases using a variable cell-size field. These remain synthetic checks: actual scene rendering, material restoration, grouping lifecycle and collision cooking require Unity validation. The current implementation conservatively rebuilds all terrain chunks when multiple Union stamps are present.

BSP build, clipping, inversion and collection use explicit work stacks rather than recursive traversal or a fixed 256-plane limit. Convex inputs can legitimately have deeper plane chains. Regression tests include identical and intersecting 300-sided closed prisms, checking manifold output and volume. Existing time, work and output-size limits remain enforced.

Clipped fragments retain their original plane rather than recomputing it from thin slivers. Polygon vertex reconciliation uses four times the BSP classification epsilon (0.00008 terrain units) to reconcile independently computed junctions. This numerical tolerance is independent of the user-facing Weld Distance and Grid Density.

Edge splitting uses that same reconciliation tolerance: a point within 0.00008 units of an edge must be inserted on both incident faces even if it is more than the 0.00002 plane-classification tolerance away. A regression with a 0.00006-unit displaced edge midpoint failed before this fix and passes afterwards, both at the origin and translated to scene-scale coordinates. Group errors now distinguish source validation from result/remesh validation and identify the affected stamps.

Mesh stamp modes are now Union (0) and Cave (3). Legacy mode values 1/2 migrate to Union. Cave is an initial closed-cutter workflow, not a general mesh boolean: the cutter must cross the terrain, and its lower trimmed contour must match the terrain opening loop count. Rock Retopo Height is the depth of the cave transition below terrain in Cave mode. Cave forces terrain cutting and model trimming. Source mesh assets are preserved.

For the actual open Unity scene, use **Tools > Local Terrain > Validate Mesh Bridges**. This builds and validates candidate terrain and bridge geometry without applying it or saving mesh assets. Invalid or disappearing contours are reported before changing the existing geometry.

**Tools > Local Terrain > Validate Mesh Instance Ownership** runs a Unity-native regression with temporary objects: duplicates a stamp, verifies independent output meshes and unchanged source geometry, restores one object and simulates loss of ownership on domain reload. This is not run by the .NET console harness. The check cleans up its temporary objects and meshes.
