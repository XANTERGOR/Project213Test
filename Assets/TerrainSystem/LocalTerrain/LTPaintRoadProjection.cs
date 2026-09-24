using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTPaintRuntime
    {
        // V1: one exclusive spline owner per layer/slot within a chunk. In particular,
        // the base layer and ordinary stamps must not share that layer with a spline.
        // Different slots retain the usual ordered coverage and smooth height blend.
        // Float UVs are intentionally unwrapped; half precision loses long-road detail.
        const int RoadProjectionSize = 257;
        static readonly ConditionalWeakTable<ChunkState, RoadProjection> roadProjections =
            new ConditionalWeakTable<ChunkState, RoadProjection>();

        sealed class RoadProjection
        {
            public RoadProjectionSnapshot cpu;
            public Texture2DArray texture;
            public Texture2D suppression;
            public LTRoadMath.Snapshot[] asphalt = Array.Empty<LTRoadMath.Snapshot>();
            public Vector4 slots0, slots1, slots2; // 0 = world, otherwise slice + 1
            public readonly LTRoadMath.Snapshot[] sources = new LTRoadMath.Snapshot[LayerCapacity];
            public readonly LTSurfaceLayer[] layers = new LTSurfaceLayer[LayerCapacity];
        }

        // Immutable managed data: detail generators can retain this snapshot across
        // replacement/release of GPU resources without referring to Unity objects.
        internal sealed class RoadProjectionSnapshot
        {
            readonly Rect rect;
            readonly Color[][] maps;
            readonly Color32[] suppression;
            public readonly int hash;
            internal Rect Rect => rect;
            internal RoadProjectionSnapshot(Rect rect, Color[][] maps, Color32[] suppression, int hash)
            { this.rect = rect; this.maps = maps; this.suppression = suppression; this.hash = hash; }

            internal float DisplacementSuppression(float x, float z)
            {
                if (suppression == null) return 0;
                float u = ((x - rect.xMin) / rect.width * (RoadProjectionSize - 1) + .5f) / RoadProjectionSize;
                float v = ((z - rect.yMin) / rect.height * (RoadProjectionSize - 1) + .5f) / RoadProjectionSize;
                return LTPaintMath.SampleGpuBilinear(suppression, RoadProjectionSize, RoadProjectionSize, u, v, false).r;
            }

            // uv is FINAL texture UV, independent of layer tile size/offset and of
            // the triplanar axis. right is a unit vector in terrain-local XZ.
            // Uses the exact endpoint-grid bilinear arithmetic of LTRoadProjection.hlsl.
            internal bool TrySample(int slot, float x, float z, out Vector2 uv, out Vector2 right)
            {
                uv = default; right = Vector2.right;
                if (slot < 0 || slot >= maps.Length || maps[slot] == null) return false;
                float px = Mathf.Clamp01((x - rect.xMin) / rect.width) * (RoadProjectionSize - 1);
                float pz = Mathf.Clamp01((z - rect.yMin) / rect.height) * (RoadProjectionSize - 1);
                int ix = Mathf.Min(Mathf.FloorToInt(px), RoadProjectionSize - 2);
                int iz = Mathf.Min(Mathf.FloorToInt(pz), RoadProjectionSize - 2);
                int at = iz * RoadProjectionSize + ix;
                var map = maps[slot];
                var value = Color.LerpUnclamped(
                    Color.LerpUnclamped(map[at], map[at + 1], px - ix),
                    Color.LerpUnclamped(map[at + RoadProjectionSize], map[at + RoadProjectionSize + 1], px - ix), pz - iz);
                uv = new Vector2(value.r, value.g);
                right = new Vector2(value.b, value.a);
                right = right.sqrMagnitude > 1e-12f ? right.normalized : Vector2.right;
                return true;
            }
        }

        static RoadProjectionSnapshot CaptureRoadProjection(ChunkState state)
            => roadProjections.TryGetValue(state, out var value) ? value.cpu : null;

        // CPU displacement/coverage hook: if false retain the existing world UV.
        static bool TryRoadProjectionUV(ChunkState state, int slot, float x, float z, out Vector2 uv)
        {
            uv = default;
            return roadProjections.TryGetValue(state, out var value) && value.cpu.TrySample(slot, x, z, out uv, out _);
        }

        // Optional CPU displacement preview hook; multiply the final geometric
        // displacement/deformation by this factor, not the layer paint weights.
        static float RoadDisplacementMultiplier(ChunkState state, float x, float z)
            => roadProjections.TryGetValue(state, out var value) ? 1 - value.cpu.DisplacementSuppression(x, z) : 1;

        // Direct mathematical mapping for tools; CPU material consumers should use
        // the baked snapshot above so their samples agree with the shader approximation.
        internal static bool TryRoadCoordinates(LTRoadMath.Snapshot road, float x, float z,
            out Vector2 uv, out Vector2 right)
        {
            uv = default; right = Vector2.right;
            return road != null && road.projection == LTRoadProjection.Spline &&
                road.TryTextureCoordinates(x, z, out uv, out right);
        }

        // Call after the ordered local palette is final and BEFORE weight/coverage
        // baking. Returns true when mapping changed, including removal. Validation
        // precedes replacement so invalid authoring never publishes a partial map.
        static bool BakeRoadProjection(LTWorld world, Rect rect, List<LTPaintStamp> stamps,
            List<LTSurfaceLayer> layers, ChunkState state)
        {
            var sources = new LTRoadMath.Snapshot[LayerCapacity];
            var contributors = new int[LayerCapacity];
            int count = 0;
            foreach (var stamp in stamps)
            {
                if (!stamp || !stamp.ActiveForPaint || (!stamp.Road && stamp.strength <= 0)) continue;
                int slot = layers.IndexOf(stamp.EffectiveLayer);
                if (slot < 0 || slot >= LayerCapacity) continue;
                contributors[slot]++;
                var road = stamp.Road;
                if (!road || road.projection != LTRoadProjection.Spline) continue;
                if (sources[slot] != null) throw ProjectionConflict(layers[slot]);
                sources[slot] = road.Capture(world); count++;
            }
            for (int slot = 0; slot < LayerCapacity; slot++)
                if (sources[slot] != null && (slot == 0 || contributors[slot] != 1))
                    throw ProjectionConflict(layers[slot]);
            // Asphalt exists independently of paint strength/layer assignment.
            // Do not derive this from only the active local paint contributors.
            var asphalt = new List<LTRoadMath.Snapshot>();
            foreach (var road in world.GetComponentsInChildren<LTRoad>())
            {
                if (!road.isActiveAndEnabled || road.World != world || road.mode != LTRoadMode.Asphalt) continue;
                LTRoadMath.Snapshot snapshot;
                try { snapshot = road.Capture(world); }
                catch (ArgumentException) { continue; } // Main authoring validation reports invalid paths.
                if (Touches(rect, snapshot.bounds)) asphalt.Add(snapshot);
            }
            if (count == 0 && asphalt.Count == 0)
            {
                bool existed = roadProjections.TryGetValue(state, out _);
                ReleaseRoadProjection(state); return existed;
            }
            if (rect.width <= 0 || rect.height <= 0 || layers.Count > LayerCapacity)
                throw new InvalidOperationException("Invalid terrain rectangle/palette for spline projection.");
            roadProjections.TryGetValue(state, out var old);
            bool same = old != null && old.cpu.Rect == rect;
            same = same && old.asphalt.Length == asphalt.Count;
            for (int i = 0; same && i < asphalt.Count; i++) same = SameRoadProjection(old.asphalt[i], asphalt[i]);
            for (int slot = 0; same && slot < LayerCapacity; slot++)
                same = SameRoadProjection(old.sources[slot], sources[slot]) &&
                    old.layers[slot] == (slot < layers.Count ? layers[slot] : null);
            if (same) return false; // No texture allocation, upload or material mutation.
            if (count > 0 && (!SystemInfo.supports2DArrayTextures || !SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat)))
                throw new InvalidOperationException("Spline terrain projection requires RGBAFloat texture arrays.");

            var next = new RoadProjection { asphalt = asphalt.ToArray() };
            var maps = new Color[LayerCapacity][];
            Color32[] suppression = null;
            int hash = Mix(rect.GetHashCode(), RoadProjectionSize);
            try
            {
                if (count > 0) next.texture = new Texture2DArray(RoadProjectionSize, RoadProjectionSize, count,
                    TextureFormat.RGBAFloat, false, true)
                { name = "Terrain spline UV + orientation", hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                int slice = 0;
                for (int slot = 0; slot < LayerCapacity; slot++)
                {
                    next.sources[slot] = sources[slot];
                    next.layers[slot] = slot < layers.Count ? layers[slot] : null;
                    hash = Mix(hash, sources[slot]?.paintHash ?? 0);
                    hash = Mix(hash, Id(next.layers[slot]));
                    if (sources[slot] == null) continue;
                    var pixels = maps[slot] = new Color[RoadProjectionSize * RoadProjectionSize];
                    // No road coverage can read the zero-filled remainder. Keep a
                    // two-texel guard so bilinear UVs remain exact along mask edges.
                    var region = sources[slot].TextureBakeRegion(rect, RoadProjectionSize);
                    for (int z = region.yMin; z < region.yMax; z++)
                    for (int x = region.xMin; x < region.xMax; x++)
                    {
                        float px = rect.xMin + rect.width * x / (RoadProjectionSize - 1);
                        float pz = rect.yMin + rect.height * z / (RoadProjectionSize - 1);
                        if (!TryRoadCoordinates(sources[slot], px, pz, out var uv, out var right))
                            throw new InvalidOperationException("Invalid spline coordinate while baking road projection.");
                        pixels[z * RoadProjectionSize + x] = new Color(uv.x, uv.y, right.x, right.y);
                    }
                    next.texture.SetPixels(pixels, slice, 0);
                    if (slot < 4) next.slots0[slot] = slice + 1;
                    else if (slot < 8) next.slots1[slot - 4] = slice + 1;
                    else next.slots2[slot - 8] = slice + 1;
                    slice++;
                }
                if (next.texture) next.texture.Apply(false, true); // Retain only the explicit managed CPU snapshot.
                if (asphalt.Count > 0)
                {
                    suppression = new Color32[RoadProjectionSize * RoadProjectionSize];
                    foreach (var road in asphalt) hash = Mix(hash, road.paintHash);
                    // Pad the full-strength core by one cell diagonal. Every corner
                    // of a cell intersecting asphalt is then fully suppressed, so
                    // bilinear interpolation cannot leak displacement under its edge.
                    float guard = new Vector2(rect.width, rect.height).magnitude / (RoadProjectionSize - 1);
                    for (int z = 0; z < RoadProjectionSize; z++)
                    for (int x = 0; x < RoadProjectionSize; x++)
                    {
                        float px = rect.xMin + rect.width * x / (RoadProjectionSize - 1);
                        float pz = rect.yMin + rect.height * z / (RoadProjectionSize - 1);
                        float weight = 0;
                        foreach (var road in asphalt)
                            if (road.TrySample(px, pz, out var hit))
                            {
                                float distance = hit.radialDistance - road.width * .5f - guard;
                                float fade = 1 - Mathf.SmoothStep(0, 1, distance / Mathf.Max(.0001f, road.shoulderWidth));
                                weight = Mathf.Max(weight, fade);
                            }
                        byte value = (byte)Mathf.RoundToInt(Mathf.Clamp01(weight) * 255);
                        suppression[z * RoadProjectionSize + x] = new Color32(value, 0, 0, 255);
                    }
                    next.suppression = new Texture2D(RoadProjectionSize, RoadProjectionSize, TextureFormat.RGBA32, false, true)
                    { name = "Asphalt displacement suppression", hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                    next.suppression.SetPixels32(suppression); next.suppression.Apply(false, true);
                }
                next.cpu = new RoadProjectionSnapshot(rect, maps, suppression, hash);
            }
            catch { DestroyOwned(next.texture); DestroyOwned(next.suppression); throw; }
            ReleaseRoadProjection(state);
            roadProjections.Add(state, next);
            return true;
        }

        static InvalidOperationException ProjectionConflict(LTSurfaceLayer layer)
            => new InvalidOperationException("Spline UV layer '" + (layer ? layer.name : "null") +
                "' must belong to one road per chunk and cannot also be the base layer or an ordinary/world-projected stamp. Use a separate layer asset.");

        static bool SameRoadProjection(LTRoadMath.Snapshot a, LTRoadMath.Snapshot b)
            => a == null ? b == null : b != null && a.geometryHash == b.geometryHash && a.paintHash == b.paintHash;

        // Call for live/flat/far/bake materials after binding/copying their other
        // properties. Explicit zero binding is necessary after deletion and Undo.
        static void BindRoadProjection(Material material, ChunkState state)
        {
            if (!material) return;
            bool active = roadProjections.TryGetValue(state, out var value);
            material.SetFloat("_LTRoadProjectionEnabled", active && value.texture ? 1 : 0);
            material.SetVector("_LTRoadProjectionSlots0", active ? value.slots0 : Vector4.zero);
            material.SetVector("_LTRoadProjectionSlots1", active ? value.slots1 : Vector4.zero);
            material.SetVector("_LTRoadProjectionSlots2", active ? value.slots2 : Vector4.zero);
            material.SetTexture("_LTRoadProjectionMap", active ? value.texture : null);
            material.SetFloat("_LTRoadSuppressionEnabled", active && value.suppression ? 1 : 0);
            material.SetTexture("_LTRoadSuppressionMap", active ? value.suppression : null);
        }

        static void ReleaseRoadProjection(ChunkState state)
        {
            if (!roadProjections.TryGetValue(state, out var value)) return;
            roadProjections.Remove(state); DestroyOwned(value.texture); DestroyOwned(value.suppression);
        }
    }
}
