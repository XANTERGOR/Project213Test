using System;

namespace LocalTerrainPrototype
{
    // Pure policy shared by the editor window and managed regression checks.
    internal static class LTStampManagerRules
    {
        [Flags] internal enum Kind { None = 0, Height = 1, Density = 2, Hole = 4 }
        internal static Kind Classify(bool height, bool density) => (height ? Kind.Height : Kind.None) | (density ? Kind.Density : Kind.None);
        internal static string Label(Kind kind) => kind == (Kind.Height | Kind.Density) ? "Высота + плотность" :
            kind == Kind.Height ? "Высота" : kind == Kind.Density ? "Плотность" : kind == Kind.Hole ? "Дырка" : "Без влияния";
        internal static bool Matches(Kind kind, bool active, bool warning, string path, string type, string query, int typeFilter, int stateFilter)
        {
            Kind filter = typeFilter == 1 ? Kind.Height : typeFilter == 2 ? Kind.Density : typeFilter == 3 ? Kind.Hole : Kind.None;
            if (filter != Kind.None && (kind & filter) == 0) return false;
            if (stateFilter == 1 && !active || stateFilter == 2 && active || stateFilter == 3 && !warning) return false;
            query = (query ?? "").Trim();
            return query.Length == 0 || (path ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (type ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Positive(float value) => Finite(value) && value > 0;
        internal static bool NonNegative(float value) => Finite(value) && value >= 0;
        internal static bool ValidRemap(float black, float white, float power) => Finite(black) && Finite(white) && Finite(power) && black >= 0 && white <= 1 && white > black && power >= .01f;
    }
}
