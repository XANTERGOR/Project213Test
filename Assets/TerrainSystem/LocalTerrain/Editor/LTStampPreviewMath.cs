using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Editor visualization only. Point samples are not the conservative region
    // maxima used by mesh subdivision, nor the final result of layered stamps.
    internal static class LTStampPreviewMath
    {
        internal const int Resolution = 256;

        internal static float Radius(Vector2 uv, LTStampShape shape)
        {
            float x = Mathf.Abs(uv.x * 2 - 1), z = Mathf.Abs(uv.y * 2 - 1);
            return shape == LTStampShape.Ellipse ? Mathf.Sqrt(x * x + z * z) : Mathf.Max(x, z);
        }

        internal static float Influence(Vector2 uv, LTStampShape shape, float edge, float strength, float mask, bool maskIsHeight)
        {
            float d = Radius(uv, shape);
            if (d >= 1) return 0;
            float t = Mathf.Clamp01((1 - d) / Mathf.Max(.001f, edge));
            return Mathf.Clamp01(t * t * (3 - 2 * t) * strength * (maskIsHeight ? 1 : mask));
        }

        internal static float ShapeDensity(Vector2 uv, LTStampShape shape)
        {
            float t = Mathf.Clamp01(Radius(uv, shape) / .8f);
            return 1 - t * t * (3 - 2 * t);
        }

        internal static void ValidateRemap(Vector2 range, float power)
        {
            if (!Finite(range.x) || !Finite(range.y) || range.x < 0 || range.y > 1 || range.y <= range.x || !Finite(power) || power < .01f)
                throw new ArgumentException("Density Mask: нужны 0 ≤ black < white ≤ 1 и Power ≥ 0.01.");
        }

        internal static float Remap(float red, bool invert, Vector2 range, float power)
        {
            if (!Finite(red)) throw new ArgumentException("Маска содержит NaN/Infinity.");
            float v = Mathf.Clamp01(red);
            if (invert) v = 1 - v;
            v = Mathf.Clamp01((v - range.x) / (range.y - range.x));
            return power == 1 ? v : Mathf.Pow(v, power);
        }

        // Remap BEFORE bilinear interpolation, exactly like LTStampDensityMask.
        // No full-resolution duplicate or max pyramid is needed for a preview.
        internal static float DensitySample(int width, int height, Func<int, int, float> read, Vector2 uv, bool invert, Vector2 range, float power)
        {
            float x = Mathf.Clamp(Mathf.Clamp01(uv.x) * width - .5f, 0, width - 1);
            float y = Mathf.Clamp(Mathf.Clamp01(uv.y) * height - .5f, 0, height - 1);
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
            float a = Remap(read(x0, y0), invert, range, power), b = Remap(read(x1, y0), invert, range, power);
            float c = Remap(read(x0, y1), invert, range, power), d = Remap(read(x1, y1), invert, range, power);
            return Mathf.Lerp(Mathf.Lerp(a, b, x - x0), Mathf.Lerp(c, d, x - x0), y - y0);
        }

        internal static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
