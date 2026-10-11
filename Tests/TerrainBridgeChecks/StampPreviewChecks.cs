using System;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void StampPreviewChecks()
    {
        void Near(float a, float b, string label) => Require(Math.Abs(a - b) < 2e-6, $"Preview {label}: {a} != {b}");
        int samples = 0;
        var random = new System.Random(9041);
        foreach (var dims in new[] { (1, 1), (1, 9), (13, 1), (3, 5), (32, 24), (127, 65) })
        {
            int w = dims.Item1, h = dims.Item2;
            var reds = Enumerable.Range(0, w * h).Select(_ => (float)random.NextDouble() * 1.4f - .2f).ToArray();
            float Read(int x, int y) => reds[y * w + x];
            foreach (bool invert in new[] { false, true })
            foreach (float power in new[] { .01f, .5f, 1, 2, 8 })
            foreach (var range in new[] { new Vector2(0, 1), new Vector2(.2f, .7f) })
            {
                var reference = new LTStampDensityMask(w, h, y => Enumerable.Range(0, w).Select(x => new Color(Read(x, y), 0, 0)).ToArray(), invert, range, power);
                LTStampPreviewMath.ValidateRemap(range, power);
                for (int n = 0; n < 100; n++)
                {
                    var uv = new Vector2((float)random.NextDouble() * 1.4f - .2f, (float)random.NextDouble() * 1.4f - .2f);
                    Near(LTStampPreviewMath.DensitySample(w, h, Read, uv, invert, range, power), reference.Sample(uv.x, uv.y), "density vs production pyramid sample");
                    samples++;
                }
            }
        }
        Near(LTStampPreviewMath.DensitySample(2, 1, (x, y) => x, new Vector2(.5f, .5f), false, new Vector2(0, 1), 2), .5f,
            "power before interpolation, not pow(.5,2)");
        foreach (var shape in new[] { LTStampShape.Ellipse, LTStampShape.Rectangle })
        {
            var zone = new LTStampMesh.Zone { inverse = Matrix4x4.identity, size = new Vector2(8, 4), shape = shape, cellSize = .2f, edgeCellSize = 3 };
            for (int n = 0; n < 500; n++)
            {
                var uv = new Vector2((float)random.NextDouble(), (float)random.NextDouble());
                float density = LTStampPreviewMath.ShapeDensity(uv, shape);
                Near(Mathf.Lerp(3, .2f, density), zone.CellSizeAt((uv.x - .5f) * 8, (uv.y - .5f) * 4), "shape density vs production zone");
                foreach (bool maskIsHeight in new[] { true, false })
                {
                    float edge = .001f + (float)random.NextDouble(), strength = (float)random.NextDouble(), mask = (float)random.NextDouble();
                    float nx = Mathf.Abs(uv.x * 2 - 1), nz = Mathf.Abs(uv.y * 2 - 1);
                    float d = shape == LTStampShape.Ellipse ? Mathf.Sqrt(nx * nx + nz * nz) : Mathf.Max(nx, nz);
                    float t = Mathf.Clamp01((1 - d) / edge);
                    float expected = d >= 1 ? 0 : t * t * (3 - 2 * t) * strength * (maskIsHeight ? 1 : mask);
                    Near(LTStampPreviewMath.Influence(uv, shape, edge, strength, mask, maskIsHeight), expected, "height influence vs CPU reference");
                }
            }
            Near(LTStampPreviewMath.ShapeDensity(new Vector2(.91f, .5f), shape), 0, "outer 20% is coarse");
            Near(LTStampPreviewMath.Influence(new Vector2(.5f, .5f), shape, .5f, .7f, 0, true), .7f, "black height mask retains influence");
            Near(LTStampPreviewMath.Influence(new Vector2(.5f, .5f), shape, .5f, .7f, 0, false), 0, "black influence mask removes influence");
            Near(LTStampPreviewMath.Influence(new Vector2(0, .5f), shape, .5f, 1, 1, true), 0, "edge has zero influence");
        }
        foreach (var range in new[] { new Vector2(1, 0), new Vector2(0, 0), new Vector2(float.NaN, 1), new Vector2(-.1f, 1) })
        {
            bool rejected = false;
            try { LTStampPreviewMath.ValidateRemap(range, 1); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "preview invalid range rejected");
        }
        foreach (float power in new[] { 0, float.NaN, float.PositiveInfinity })
        {
            bool rejected = false;
            try { LTStampPreviewMath.ValidateRemap(new Vector2(0, 1), power); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "preview invalid power rejected");
        }
        Func<int, int, float> readConstant = (x, y) => .5f;
        LTStampPreviewMath.DensitySample(7, 5, readConstant, new Vector2(.2f, .7f), true, new Vector2(0, 1), 2);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 10000; n++) LTStampPreviewMath.DensitySample(7, 5, readConstant, new Vector2(.2f, .7f), true, new Vector2(0, 1), 2);
        Require(GC.GetAllocatedBytesForCurrentThread() == start, "preview sampler allocation-free");
        Console.WriteLine($"STAMP PREVIEW PASS: {samples} density comparisons; shape density, height mask/influence semantics, boundaries, remap guards, zero per-sample allocation.");
    }
}
