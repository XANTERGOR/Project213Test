#ifndef LT_GRASS_TERRAIN_INCLUDED
#define LT_GRASS_TERRAIN_INCLUDED

void LTGrassTerrainApply_float(float3 BaseColor, float4 Ground, float RestHeight,
    float RootBase, float BlendHeight, float Strength, out float3 Color)
{
    float t = saturate((RestHeight - RootBase) / max(BlendHeight, 0.0001));
    float weight = saturate(Strength) * saturate(Ground.a) * (1.0 - t * t * (3.0 - 2.0 * t));
    Color = BaseColor;
    if (weight <= 0.0) return;
    // Transfer ground hue and half its luminance, keeping grass luminance detail
    // (including the existing variation). This is albedo, never lit scene color.
    float grassY = dot(max(BaseColor, 0.0), float3(0.2126, 0.7152, 0.0722));
    float groundY = dot(max(Ground.rgb, 0.0), float3(0.2126, 0.7152, 0.0722));
    float3 target = max(Ground.rgb, 0.0) * (0.5 + 0.5 * grassY / max(groundY, 0.02));
    Color = lerp(BaseColor, saturate(target), weight);
}
void LTGrassTerrainApply_half(half3 BaseColor, half4 Ground, half RestHeight,
    half RootBase, half BlendHeight, half Strength, out half3 Color)
{
    float3 result; LTGrassTerrainApply_float(BaseColor, Ground, RestHeight, RootBase, BlendHeight, Strength, result); Color = (half3)result;
}
#ifndef LT_GRASS_TERRAIN_MATH_ONLY
void LTGrassTerrainSample_float(float3 RootPosition, UnityTexture2D TerrainMap,
    float4 U, float4 V, float Ready, float Strength, out float4 Ground)
{
    Ground = 0.0;
    if (Ready < 0.5 || Strength <= 0.0) return;
    float2 uv = float2(dot(float4(RootPosition, 1.0), U), dot(float4(RootPosition, 1.0), V));
    if (any(uv < 0.0) || any(uv > 1.0)) return;
    Ground = SAMPLE_TEXTURE2D_LOD(TerrainMap.tex, TerrainMap.samplerstate, uv, 0);
    // Cache texels are premultiplied by availability near sparse-cell boundaries.
    if (Ground.a > 0.0001) Ground.rgb /= Ground.a;
}
void LTGrassTerrainSample_half(half3 RootPosition, UnityTexture2D TerrainMap,
    half4 U, half4 V, half Ready, half Strength, out half4 Ground)
{
    float4 result; LTGrassTerrainSample_float(RootPosition, TerrainMap, U, V, Ready, Strength, result); Ground = (half4)result;
}
#endif
#endif
