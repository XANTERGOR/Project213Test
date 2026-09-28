using System;
using System.Diagnostics;
using LocalTerrainPrototype;
using UnityEngine;

// Managed production math vs the pre-optimization unbounded-query formula.
// These timings are not Unity Editor, GPU, streaming or packaged-player benchmarks.
static class RoadOptimizationChecks
{
    static int checks;
    static void Require(bool ok, string message)
    { checks++; if (!ok) throw new Exception("Road optimization: " + message); }
    static float Smooth(float v) => v * v * (3 - 2 * v);
    static float Fade(float d, float inner, float feather) => d <= inner ? 1 : feather <= 0 ? 0 :
        1 - Smooth(Math.Max(0, Math.Min(1, (d - inner) / feather)));
    static float Corner(int x, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)(((seed * 397) ^ x) * 397 ^ z);
            h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
            return (h >> 8) * (1f / 16777216);
        }
    }
    static float Noise(float x, float z, int seed)
    {
        int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
        float u = Smooth(x - ix), v = Smooth(z - iz);
        float a = Corner(ix, iz, seed), b = Corner(ix + 1, iz, seed);
        float c = Corner(ix, iz + 1, seed), d = Corner(ix + 1, iz + 1, seed);
        return (a + (b - a) * u) * (1 - v) + (c + (d - c) * u) * v;
    }
    static float Tracks(LTRoadMath.Snapshot r, LTRoadMath.Hit h, float x, float z)
    {
        float extent = r.rutWidth * .5f * (1 - r.edgeNoise * Noise(x / r.noiseSize, z / r.noiseSize, r.seed) * .8f);
        float across = Math.Abs(Math.Abs(h.lateral) - r.rutSeparation * .5f);
        float beyond = Math.Max(0, h.radialDistance * h.radialDistance - h.lateral * h.lateral);
        return Fade((float)Math.Sqrt(across * across + beyond), extent * .65f, extent * .35f);
    }
    static bool Sample(LTRoadMath.Snapshot r, float x, float z, out LTRoadMath.Hit h)
    {
        h = default;
        return x >= r.bounds.xMin && x <= r.bounds.xMax && z >= r.bounds.yMin && z <= r.bounds.yMax && r.TrySample(x, z, out h);
    }
    static float ReferenceHeight(LTRoadMath.Snapshot r, float x, float z, float original)
    {
        if (!Sample(r, x, z, out var h)) return original;
        float weight = Fade(h.radialDistance, r.width * .5f, r.shoulderWidth + r.blendWidth);
        weight *= r.mode == LTRoadMode.Asphalt ? 1 : r.flatten;
        if (weight <= 0) return original;
        float target = r.SurfaceHeight(h, h.lateral);
        if (r.mode == LTRoadMode.Offroad && r.pattern == LTRoadPattern.Tracks) target -= r.rutDepth * Tracks(r, h, x, z);
        return original + (target - original) * weight;
    }
    static float ReferencePaint(LTRoadMath.Snapshot r, float x, float z)
    {
        if (!Sample(r, x, z, out var h)) return 0;
        return r.mode == LTRoadMode.Offroad && r.pattern == LTRoadPattern.Tracks ? Tracks(r, h, x, z) :
            Fade(h.radialDistance, r.width * .5f, r.shoulderWidth);
    }
    static LTRoadPoint[] Path(float offset = 0) => new[] {
        new LTRoadPoint(new Vector3(16 + offset, 3, 16), 10),
        new LTRoadPoint(new Vector3(250 + offset, 5, 260), -12),
        new LTRoadPoint(new Vector3(490 + offset, 2, 420), 7),
        new LTRoadPoint(new Vector3(740 + offset, 7, 800), -5),
        new LTRoadPoint(new Vector3(990 + offset, 4, 990), 0) };

    public static void Run()
    {
        checks = 0;
        var random = new System.Random(71903);
        for (int fixture = 0; fixture < 24; fixture++)
        {
            var s = LTRoadMath.Settings.Default;
            s.mode = fixture % 3 == 0 ? LTRoadMode.Asphalt : LTRoadMode.Offroad;
            s.pattern = fixture % 2 == 0 ? LTRoadPattern.Tracks : LTRoadPattern.Solid;
            s.width = 2.5f + fixture; s.shoulderWidth = fixture % 4; s.blendWidth = fixture % 5;
            s.flatten = fixture % 4 / 3f; s.edgeNoise = fixture % 3 / 2f;
            s.sampleSpacing = fixture % 2 == 0 ? .5f : 4;
            s.seed += fixture; s.clearStones = true; s.vegetationFade = .7f;
            var r = LTRoadMath.Build(Path(fixture % 2 == 0 ? 0 : 4000), Matrix4x4.identity, s);
            void Compare(float x, float z)
            {
                float height = ReferenceHeight(r, x, z, -3.75f), paint = ReferencePaint(r, x, z);
                Require(r.ApplyHeight(x, z, -3.75f) == height, "bounded height differs from unbounded reference");
                Require(r.PaintWeight(x, z) == paint, "bounded paint differs from unbounded reference");
                Require(r.ClearWeight(x, z, LTDetailCategory.Vegetation) == paint * .7f, "vegetation mask changed");
                Require(r.ClearWeight(x, z, LTDetailCategory.Stones) == paint, "stone mask changed");
            }
            for (int i = 0; i < 4000; i++)
            {
                Compare(r.bounds.xMin + (float)random.NextDouble() * r.bounds.width,
                    r.bounds.yMin + (float)random.NextDouble() * r.bounds.height);
                var p = r.samples[i % r.samples.Count];
                float side = ((float)random.NextDouble() * 2 - 1) * (s.width + s.shoulderWidth + s.blendWidth);
                Compare(p.position.x + p.right.x * side, p.position.z + p.right.z * side);
            }
            foreach (int index in new[] { 0, r.samples.Count / 2, r.samples.Count - 1 })
            foreach (float delta in new[] { -.001f, 0f, .001f })
            foreach (float sign in new[] { -1f, 1f })
            {
                var p = r.samples[index];
                float side = sign * (s.width * .5f + s.shoulderWidth + s.blendWidth + delta);
                Compare(p.position.x + p.right.x * side, p.position.z + p.right.z * side);
            }
        }
        ScopedChunks();
        Benchmark();
        Console.WriteLine($"PASS road optimizations: {checks} exact-reference / dirty-region checks; managed CPU only.");
    }

    static Rect Expand(Rect r, float halo) => Rect.MinMaxRect(r.xMin - halo, r.yMin - halo, r.xMax + halo, r.yMax + halo);
    static bool Overlap(Rect a, Rect b) => a.xMin <= b.xMax && a.xMax >= b.xMin && a.yMin <= b.yMax && a.yMax >= b.yMin;
    static void ScopedChunks()
    {
        var s = LTRoadMath.Settings.Default;
        var before = LTRoadMath.Build(Path(), Matrix4x4.identity, s);
        var after = LTRoadMath.Build(Path(80), Matrix4x4.identity, s);
        const float halo = .5f;
        int broad = 0, scoped = 0;
        for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
        {
            var chunk = new Rect(x * 64, z * 64, 64, 64);
            bool oldBroad = Overlap(Expand(before.bounds, halo), chunk), newBroad = Overlap(Expand(after.bounds, halo), chunk);
            bool dirty = (oldBroad && before.Intersects(Expand(chunk, halo))) || (newBroad && after.Intersects(Expand(chunk, halo)));
            if (oldBroad || newBroad) broad++;
            if (dirty) scoped++;
            for (int iz = 0; iz <= 8; iz++) for (int ix = 0; ix <= 8; ix++)
            foreach (var r in new[] { before, after })
            {
                float px = chunk.xMin + ix * 8, pz = chunk.yMin + iz * 8;
                bool affected = r.ApplyHeight(px, pz, -10) != -10 ||
                    r.ApplyHeight(px - halo, pz, -10) != -10 || r.ApplyHeight(px + halo, pz, -10) != -10 ||
                    r.ApplyHeight(px, pz - halo, -10) != -10 || r.ApplyHeight(px, pz + halo, -10) != -10;
                Require(!affected || dirty, "move/remove must rebuild affected heights and normal halo on both paths");
            }
            // Removal uses only the old path; insertion only the new path. Density is scoped independently.
            foreach (var r in new[] { before, after })
                Require(!r.Intersects(chunk) || (Overlap(r.bounds, chunk) && r.Intersects(Expand(chunk, halo))),
                    "density refinement remains covered by dirty geometry");
        }
        Require(scoped < broad / 2, "diagonal fixture must skip most empty bounding-box chunks");
        Console.WriteLine($"Road dirty-region fixture (moved diagonal, normal halo): {broad} bounding-box chunks -> {scoped} path chunks before seam propagation.");
    }

    static void Benchmark()
    {
        var s = LTRoadMath.Settings.Default; s.sampleSpacing = .5f;
        var r = LTRoadMath.Build(Path(), Matrix4x4.identity, s);
        var points = new Vector2[256 * 256];
        for (int i = 0; i < points.Length; i++) points[i] = new Vector2(i % 256 * 4, i / 256 * 4);
        double Run(bool optimized)
        {
            double sum = 0;
            foreach (var p in points) sum += optimized ? r.ApplyHeight(p.x, p.y, -3.75f) : ReferenceHeight(r, p.x, p.y, -3.75f);
            return sum;
        }
        for (int warmup = 0; warmup < 4; warmup++) Require(Run(false) == Run(true), "benchmark output checksum");
        var oldTimes = new double[5]; var newTimes = new double[5];
        for (int i = 0; i < 5; i++)
        {
            for (int order = 0; order < 2; order++)
            {
                bool optimized = (i + order) % 2 == 0;
                var clock = Stopwatch.StartNew(); double sum = Run(optimized); clock.Stop();
                GC.KeepAlive(sum);
                (optimized ? newTimes : oldTimes)[i] = clock.Elapsed.TotalMilliseconds;
            }
        }
        Array.Sort(oldTimes); Array.Sort(newTimes);
        Console.WriteLine($"Road query synthetic CPU median, {points.Length} heights/{r.samples.Count} path samples: unbounded {oldTimes[2]:F2} ms -> bounded {newTimes[2]:F2} ms. Not a Unity scene timing.");
    }
}
