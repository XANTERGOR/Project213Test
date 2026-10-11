#ifndef LT_GRASS_ROOT_TINT_INCLUDED
#define LT_GRASS_ROOT_TINT_INCLUDED

// RestHeight is the mesh-local Y BEFORE wind, interpolated from the vertex stage.
// RootBase/RootHeight use the same object-space units: instance scale/rotation and
// terrain elevation cannot move the gradient through a blade. No terrain sampling.
void LTGrassRootTint_float(float3 BaseColor, float RestHeight, float3 RootTint,
    float Strength, float RootBase, float RootHeight, out float3 Color)
{
    float t = saturate((RestHeight - RootBase) / max(RootHeight, 0.0001));
    float rootMask = 1.0 - t * t * (3.0 - 2.0 * t);
    float weight = saturate(Strength) * rootMask;
    // Multiplicative tint retains the albedo's fine detail. Strength=0 and a
    // white tint both preserve the original material; alpha is never changed.
    Color = BaseColor * lerp(float3(1.0, 1.0, 1.0), saturate(RootTint), weight);
}

void LTGrassRootTint_half(half3 BaseColor, half RestHeight, half3 RootTint,
    half Strength, half RootBase, half RootHeight, out half3 Color)
{
    float3 result;
    LTGrassRootTint_float(BaseColor, RestHeight, RootTint, Strength, RootBase, RootHeight, result);
    Color = (half3)result;
}
#endif
