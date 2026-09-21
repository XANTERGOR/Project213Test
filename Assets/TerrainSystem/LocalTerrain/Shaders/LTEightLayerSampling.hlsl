// Shared blending code is also used by the GPU atlas baker.
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTLayerBlendCore.hlsl"
TEXTURE2D(_LTGlobalAlbedo);
TEXTURE2D(_LTGlobalNormal);
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTCoverageCoordinates.hlsl"
#ifndef LT_OCCUPANCY_TEXTURE_DECLARED
#define LT_OCCUPANCY_TEXTURE_DECLARED
TEXTURE2D(_LTDisplacementOccupancy);
#endif
float LTDebugCoverageValue(float2 position)
{
    if(_LTDebugCoverage<0 || any(_LTDisplacementRect.zw<=0))return 0;
    float2 uv=(position-_LTDisplacementRect.xy)/_LTDisplacementRect.zw;
    if(any(uv<0)||any(uv>1))return 0;
    return SAMPLE_TEXTURE2D_LOD(_LTDisplacementOccupancy,sampler_PointClamp,uv,0).r;
}
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTProjectedLayers.hlsl"
void LTApplyLayers(FragInputs input, inout SurfaceData surfaceData, out float3 normalTS, out float3 sampledNormalTS)
{
    sampledNormalTS=float3(0,0,1);
    if(abs(_LTDebugCoverage)>.5 && _LTDebugCoverage<6.5)
    {
        surfaceData.baseColor=0;surfaceData.metallic=0;
        surfaceData.ambientOcclusion=1;surfaceData.perceptualSmoothness=0;
#ifdef SURFACE_GRADIENT
        normalTS=0;
#else
        normalTS=float3(0,0,1);
#endif
        return;
    }
    float3 color=0,n=float3(0,0,1); float ao=1,smoothness=0,metallic=0;
    float3 projectedGradient=0;
    bool projected=_LTTriplanar>.5 || _LTRockProjection>.5;
    float2 position=input.texCoord0.xy*_LTWorldSize.xy;
#if defined(SHADER_STAGE_RAY_TRACING)
    float2 dx=0,dy=0;
#else
    float2 dx=ddx(position),dy=ddy(position);
#endif
    float fade=0;
    if(_LTGlobalParams.x > .5 && _LTRockProjection < .5)
        fade=smoothstep(_LTGlobalParams.y,_LTGlobalParams.z,length(GetCurrentViewPosition()-input.positionRWS));
#if defined(_LT_FAR_ONLY)
    fade=1;
#else
    // The full-far branch never samples detailed layers, even on straddling chunks.
    [branch] if(projected && fade < 1)
    {
        float3 sourcePositionRWS=input.positionRWS;
#if defined(LT_TESSELLATION) && defined(TESSELLATION_ON) && !defined(SHADER_STAGE_RAY_TRACING)
        sourcePositionRWS=input.color.xyz;
#endif
        float3 p=mul(_LTSurfaceWorldToLocal,float4(GetAbsolutePositionWS(sourcePositionRWS),1)).xyz;
        LTSampleProjectedLayers(p,input.tangentToWorld[2],_LTTriplanar>.5,color,projectedGradient,ao,smoothness,metallic);
        float3 ws=normalize(input.tangentToWorld[2]-projectedGradient);
        n=float3(dot(ws,normalize(input.tangentToWorld[0])),dot(ws,normalize(input.tangentToWorld[1])),dot(ws,input.tangentToWorld[2]));
    }
    else if(fade < 1)
        LTSampleLayers(position,dx,dy,color,n,ao,smoothness,metallic);
#endif
    if(fade > 0)
    {
#if defined(SHADER_STAGE_RAY_TRACING)
        float3 farColor=SAMPLE_TEXTURE2D_LOD(_LTGlobalAlbedo,sampler_LinearClamp,input.texCoord0.xy,0).rgb;
        float3 farNormal=SAMPLE_TEXTURE2D_LOD(_LTGlobalNormal,sampler_LinearClamp,input.texCoord0.xy,0).xyz*2-1;
#else
        float3 farColor=SAMPLE_TEXTURE2D_GRAD(_LTGlobalAlbedo,sampler_TrilinearClamp,input.texCoord0.xy,dx/_LTWorldSize.xy,dy/_LTWorldSize.xy).rgb;
        float3 farNormal=SAMPLE_TEXTURE2D_GRAD(_LTGlobalNormal,sampler_TrilinearClamp,input.texCoord0.xy,dx/_LTWorldSize.xy,dy/_LTWorldSize.xy).xyz*2-1;
#endif
        farNormal=normalize(farNormal+float3(0,0,.00001));
        if(projected)
        {
            // Atlas stores a tangent-space perturbation, not a baked geometric
            // normal. Resolve on the current terrain frame (including its LOD).
            float2 farSlope=-farNormal.xy/max(farNormal.z,.0001);
            // HDRP keeps MikkTS interpolation lengths. This atlas is encoded in
            // an orthonormal frame; use the same frame as BakeMesh when decoding.
            float3 baseN=input.tangentToWorld[2];
            float3 farT=normalize(input.tangentToWorld[0]-baseN*dot(baseN,input.tangentToWorld[0]));
            float3 farB=normalize(input.tangentToWorld[1]);
            float3 farGradient=farSlope.x*farT+farSlope.y*farB;
            projectedGradient=lerp(projectedGradient,farGradient,fade);
        }
        if(fade>=1){color=farColor;n=farNormal;ao=1;smoothness=_LTFarSurface.x;metallic=_LTFarSurface.y;}
        else
        {
            color=lerp(color,farColor,fade);n=normalize(lerp(n,farNormal,fade));
            ao=lerp(ao,1,fade);smoothness=lerp(smoothness,_LTFarSurface.x,fade);metallic=lerp(metallic,_LTFarSurface.y,fade);
        }
    }
    // Preserve the blended texture vector before surface-gradient conversion.
    // Diagnostic output reuses this sample; no extra normal-map reads.
    sampledNormalTS=n;
    // Only the three grey probes override the material/normal.
    if(_LTDebugCoverage>6.5 && _LTDebugCoverage<9.5)
    {
        color=float3(.4,.4,.4);ao=1;smoothness=0;metallic=0;
        if(_LTDebugCoverage<7.5 || _LTDebugCoverage>8.5){n=float3(0,0,1);projectedGradient=0;}
    }
    surfaceData.baseColor=color;
    surfaceData.ambientOcclusion=saturate(ao);
    surfaceData.perceptualSmoothness=saturate(smoothness);
    surfaceData.metallic=saturate(metallic);
    // Tessellation only changes positions. Retain the interpolated terrain
    // normal/tangent frame and the material normal map, with no displacement correction.
#ifdef SURFACE_GRADIENT
    normalTS=projected?projectedGradient:SurfaceGradientFromTangentSpaceNormalAndFromTBN(n,input.tangentToWorld[0],input.tangentToWorld[1]);
#else
    normalTS=n;
#endif
}
