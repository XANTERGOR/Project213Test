using UnityEngine;

namespace LocalTerrainPrototype
{
    // Local footprint sizes; neither transform scale nor height is changed.
    internal static class LTStampSizeMath
    {
        internal const float Minimum = .01f;
        internal static bool Valid(Vector2 size) => Finite(size.x) && Finite(size.y) && size.x >= Minimum && size.y >= Minimum;
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static Vector2 Edge(Vector2 original, Vector3 localPoint, int axis, int sign)
        {
            if (!Valid(original) || (axis != 0 && axis != 1) || (sign != -1 && sign != 1)) return original;
            float span = 2 * sign * (axis == 0 ? localPoint.x : localPoint.z);
            if (!Finite(span)) return original;
            span = Mathf.Max(Minimum, span); // Crossing the centre never mirrors the stamp.
            return axis == 0 ? new Vector2(span, original.y) : new Vector2(original.x, span);
        }

        internal static Vector2 Corner(Vector2 original, Vector3 localPoint, int xSign, int zSign)
        {
            if (!Valid(original) || (xSign != -1 && xSign != 1) || (zSign != -1 && zSign != 1) || !Finite(localPoint.x) || !Finite(localPoint.z)) return original;
            // Project onto the original half-diagonal. Double intermediates avoid
            // overflow for large valid footprints; use the mouse-down ratio, not
            // the previous frame, so repeated drags cannot accumulate aspect drift.
            double x = xSign * (double)original.x * .5, z = zSign * (double)original.y * .5;
            double factor = (localPoint.x * x + localPoint.z * z) / (x * x + z * z);
            factor = System.Math.Max(factor, System.Math.Max(Minimum / (double)original.x, Minimum / (double)original.y));
            var result = new Vector2((float)(original.x * factor), (float)(original.y * factor));
            return Valid(result) ? result : original;
        }
    }
}
