using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Editor-only, explicit allowlist. No identity, prefab, list order or shared mask writes.
    internal static class LTDetailBatchPatch
    {
        internal sealed class Field
        {
            internal readonly string name, label;
            internal readonly int group;
            internal readonly FieldInfo info;
            internal Field(string name, string label, int group)
            { this.name = name; this.label = label; this.group = group; info = typeof(LTDetailEntry).GetField(name); }
        }
        internal static readonly string[] Groups = { "Размещение", "Своя маска и размер по маске", "Отображение", "Состояние" };
        internal static readonly Field[] Fields = {
            new Field("density", "Плотность, шт/м²", 0), new Field("probability", "Вероятность", 0),
            new Field("scaleRange", "Масштаб, min/max", 0), new Field("yawRange", "Поворот, min/max °", 0),
            new Field("slopeRange", "Уклон, min/max °", 0), new Field("alignToNormal", "Наклон по поверхности", 0),
            new Field("heightOffsetRange", "Смещение, min/max м", 0), new Field("densityMaskMode", "Режим маски", 0),
            new Field("patchSize", "Размер пятен, м", 1), new Field("patchCoverage", "Заполненность", 1),
            new Field("patchSoftness", "Мягкость края", 1), new Field("patchMinimumDensity", "Плотность между пятнами", 1),
            new Field("patchSeed", "Seed пятен", 1), new Field("patchScaleEnabled", "Размер по маске", 1),
            new Field("patchScaleEdge", "Размер у края, ×", 1), new Field("patchScaleInside", "Размер внутри, ×", 1),
            new Field("patchScaleSoftness", "Мягкость размера", 1),
            new Field("fadeStart", "Начало исчезновения, м", 2), new Field("cullDistance", "Дальность, м", 2),
            new Field("limitShadowDistance", "Ограничить дальность теней", 2), new Field("shadowDistance", "Дальность теней, м", 2),
            new Field("shaderFade", "Shader Fade", 2), new Field("thinInDistance", "Прореживать вдали", 2),
            new Field("farDensity", "Доля вдали", 2), new Field("deformationBoundsPadding", "Запас bounds, м", 2),
            new Field("enabled", "Объект включён", 3), new Field("category", "Категория", 3)
        };
        internal static Field Find(string name)
        { foreach (var field in Fields) if (field.name == name) return field; return null; }
        internal static HashSet<string> UniqueIds(IEnumerable<string> ids)
        {
            var unique = new HashSet<string>(); var duplicates = new HashSet<string>();
            foreach (string id in ids) if (!string.IsNullOrEmpty(id) && !unique.Add(id)) duplicates.Add(id);
            unique.ExceptWith(duplicates); return unique;
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Range(float value, float min, float max) => Finite(value) && value >= min && value <= max;
        static bool Range(Vector2 value, float min, float max) => Range(value.x, min, max) && Range(value.y, min, max) && value.x <= value.y;

        internal static bool ValidValue(Field field, object value)
        {
            if (value == null || value.GetType() != field.info.FieldType) return false;
            switch (field.name)
            {
                case "density": case "fadeStart": case "cullDistance": case "shadowDistance": case "deformationBoundsPadding":
                    return Range((float)value, 0, float.MaxValue);
                case "probability": case "alignToNormal": case "patchCoverage": case "patchSoftness":
                case "patchMinimumDensity": case "patchScaleSoftness": case "farDensity": return Range((float)value, 0, 1);
                case "patchSize": return Range((float)value, .1f, float.MaxValue);
                case "patchScaleEdge": case "patchScaleInside": return Range((float)value, .01f, float.MaxValue);
                case "scaleRange": return Range((Vector2)value, .001f, float.MaxValue);
                case "yawRange": return Range((Vector2)value, -360, 360);
                case "slopeRange": return Range((Vector2)value, 0, 90);
                case "heightOffsetRange": return Range((Vector2)value, -1000, 1000);
                case "densityMaskMode": return Enum.IsDefined(typeof(LTDetailMaskMode), value);
                case "category": return ((int)(LTDetailCategory)value & ~(int)LTDetailCategory.All) == 0;
                default: return field.info.FieldType == typeof(bool) || field.info.FieldType == typeof(int);
            }
        }
        // Validate all selected values before any write; reject coupled-range conflicts
        // instead of silently changing an unselected setting via OnValidate.
        internal static bool Validate(LTDetailEntry current, IDictionary<string, object> changes, out string error)
        {
            error = null;
            foreach (var change in changes)
            {
                var field = Find(change.Key);
                if (field == null || !ValidValue(field, change.Value))
                { error = "Некорректное значение: " + (field == null ? change.Key : field.label) + ". Диапазоны задаются от min к max."; return false; }
            }
            if (changes.ContainsKey("fadeStart") || changes.ContainsKey("cullDistance"))
            {
                float start = changes.TryGetValue("fadeStart", out var a) ? (float)a : current.fadeStart;
                float end = changes.TryGetValue("cullDistance", out var b) ? (float)b : current.cullDistance;
                if (!Finite(start) || !Finite(end) || start > end)
                { error = "Начало исчезновения больше дальности. Отметь оба поля и задай согласованный диапазон."; return false; }
            }
            return true;
        }
    }
}
