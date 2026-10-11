#ifndef LT_GRASS_VARIATION_INCLUDED
#define LT_GRASS_VARIATION_INCLUDED

// Integer lattice hashing avoids time, draw order and trigonometric hashes.
float LTGrassHash(int2 cell, uint seed)
{
    uint h = (uint)cell.x * 1597334677u ^ (uint)cell.y * 3812015801u ^ seed * 2798796415u;
    h = (h ^ (h >> 16)) * 2246822519u;
    h = (h ^ (h >> 13)) * 3266489917u;
    h ^= h >> 16;
    return (h & 16777215u) / 16777215.0;
}

float LTGrassNoise(float2 p, uint seed)
{
    int2 cell = (int2)floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(LTGrassHash(cell, seed), LTGrassHash(cell + int2(1, 0), seed), f.x),
        lerp(LTGrassHash(cell + int2(0, 1), seed), LTGrassHash(cell + int2(1, 1), seed), f.x), f.y);
}

// Evaluated in the VERTEX stage, once per vertex, constant across each clump.
// Smooth micro-noise avoids hash jumps from tiny transform roundoff between LODs.
void LTGrassVariation_float(float3 RootPosition, float3 TintA, float3 TintB,
    float ClumpStrength, float PatchStrength, float PatchSize, float Seed, out float3 Multiplier)
{
    float clump = saturate(ClumpStrength);
    float patch = saturate(PatchStrength);
    float sum = clump + patch;
    // Uniform early-out for all existing materials (both strengths default to 0).
    if (sum <= 0.0) { Multiplier = float3(1.0, 1.0, 1.0); return; }
    uint seed = (uint)clamp(floor(Seed + 0.5), 0.0, 65535.0);
    float micro = LTGrassNoise(RootPosition.xz * 8.0, seed);
    float macro = LTGrassNoise(RootPosition.xz / max(PatchSize, 0.1), seed ^ 1867u);
    float blend = (micro * clump + macro * patch) / max(sum, 0.0001);
    Multiplier = lerp(float3(1.0, 1.0, 1.0), lerp(saturate(TintA), saturate(TintB), blend), saturate(sum));
}

void LTGrassVariation_half(half3 RootPosition, half3 TintA, half3 TintB,
    half ClumpStrength, half PatchStrength, half PatchSize, half Seed, out half3 Multiplier)
{
    float3 result;
    LTGrassVariation_float(RootPosition, TintA, TintB, ClumpStrength, PatchStrength, PatchSize, Seed, result);
    Multiplier = (half3)result;
}

void LTGrassVariationApply_float(float3 BaseColor, float3 Multiplier, out float3 Color)
{
    Color = BaseColor * Multiplier;
}
void LTGrassVariationApply_half(half3 BaseColor, half3 Multiplier, out half3 Color)
{
    Color = BaseColor * Multiplier;
}

// Probe shaders test the pure math without an SRP transform environment.
#ifndef LT_GRASS_VARIATION_MATH_ONLY
void LTGrassVariationRoot_float(float3 ObjectOrigin, float3 RootInObject, out float3 RootPosition)
{
    // ObjectOrigin is Shader Graph Object.Position (absolute WS in HDRP).
    // LocalTerrain supplies the same prefab root for every LOD/child renderer.
    RootPosition = ObjectOrigin + mul((float3x3)UNITY_MATRIX_M, RootInObject);
}
void LTGrassVariationRoot_half(half3 ObjectOrigin, half3 RootInObject, out half3 RootPosition)
{
    float3 result;
    LTGrassVariationRoot_float(ObjectOrigin, RootInObject, result);
    RootPosition = (half3)result;
}
#endif
#endif
