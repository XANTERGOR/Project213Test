# Regular displacement-mask source grid — stage 1

In LT World → Тесселяция enable «Регулярная сетка по маске».
Select an existing chunk (initially 0,0) and start with a 0.5 m requested cell step.
Painting and Displacement слоёв must remain enabled so displacement-layer coverage exists.
Allow the automatic geometry rebuild to finish. Inspect the selected chunk in Wireframe.

This stage uses the production quadtree planner and balanced mesh emitter. Occupied
mask cells request a constant source-grid step; empty areas retain ordinary source
density except where balancing needs transition cells. The step is rounded down to a
dyadic subdivision of the chunk. Finer requirements from other stamps take priority.
Ordinary cells use consistent diagonals (two triangles per rectangular cell), while
transition polygons use boundary-preserving triangulation without a central vertex.
This is NOT an implementation of hardware quad patches.

The selected chunk uses LOD0. With «Displacement без дробления» off it uses the
non-tessellated material to isolate source topology. With that toggle on it uses the
existing displacement shader, forcing every visible patch's edge and inside factors
to 1. The GPU hull/domain pipeline still runs, but adds no subdivision; this requires
tessellation shader support. The regular-grid factor takes priority over Hull debug
settings, maximum tessellation and target edge length. The displacement height,
normal calculation, camera/seam/mask fades and expanded renderer bounds remain in use.
The collider remains the un-displaced CPU mesh. Coverage generation remains active
in both modes. The toggle is off by default and does not request a CPU topology rebuild.
Other chunks keep their rendering settings; source balancing can affect neighbours.
Existing cut clipping, vertex limits, collider rebuild and cancellation paths remain
in use. Curved cut contours remain polygonal approximations, and the existing cutter
does not guarantee detection of holes smaller than a cell with no cut vertices.

Disable the toggle and wait for rebuilding to restore the ordinary generation and
material paths. This setting does not alter layer assets or their displacement values.

Automated validation: TerrainBridgeChecks links the actual LTStampMesh and
LTBalancedForest sources (only editor progress UI is stubbed). It exercises fine
interior/coarse exterior, balanced transitions, winding, covered area and a circular
cut with no unintended open interior edges. This does not replace Unity visual QA,
multi-chunk scene validation or performance measurements.

Unity A/B check: keep the camera and grid step unchanged, toggle displacement, inspect
Wireframe for unchanged connectivity and Shaded for height/normals. Check mask edges,
real cuts, chunk seams and distance fade. Disable the entire regular-grid mode to
confirm ordinary tessellation settings take control again.
