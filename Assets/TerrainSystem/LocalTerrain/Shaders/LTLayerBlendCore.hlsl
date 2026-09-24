// Three world-shared arrays; local layer slots map to world palette slices.
// Reuse HDRP's guarded global sampler declarations; do not redeclare them.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
TEXTURE2D_ARRAY(_LTColorArray);
TEXTURE2D_ARRAY(_LTNormalArray);
TEXTURE2D_ARRAY(_LTMaskArray);
// Each sampler follows its own texture: mask-only domain/depth programs strip color.
SAMPLER(sampler_LTColorArray);
SAMPLER(sampler_LTNormalArray);
SAMPLER(sampler_LTMaskArray);
float LTLayerSlice(int slot)
{
    // Keep every expression in bounds, including branches folded for constant slots.
    int component=slot&3;
    return slot<4?_LTLayerSlices0[component]:slot<8?_LTLayerSlices1[component]:_LTLayerSlices2[component];
}
float4 LTSampleLayerMask(int slot,float2 uv,float mip)
{
    return SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(slot),mip);
}
TEXTURE2D(_LTWeights0);
TEXTURE2D(_LTWeights1);
TEXTURE2D(_LTWeights2);

#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadProjection.hlsl"

void LTSampleLayersMappedFrame(float2 coveragePosition, float2 coverageDx, float2 coverageDy,
    float2 position, float2 uvDx, float2 uvDy, int projectionAxis,
    out float3 color, out float3 normalTS, out float ao, out float smoothness, out float metallic,
    out float3 gradientLocal)
{
    gradientLocal=0;
    color=0;normalTS=float3(0,0,1);ao=1;smoothness=0;metallic=0;
    // Uniform per-chunk fast path. No coverage reads, layer arrays or height
    // blending on chunks containing only the background. The baker shares this path.
    [branch] if(_LTBaseOnly > .5)
    {
        float2 uv=position/max(_LTTiling0.xy,.001)+_LTTiling0.zw;
        float3 mask=float3(1,.5,_LTSettings0.z);
        float3 n=float3(0,0,1);
#if defined(SHADER_STAGE_RAY_TRACING)
        color=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(0),0).rgb*_LTTint0.rgb;
        if(_LTSettings0.w > .5) mask=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(0),0).rgb;
        if(_LTFlags0.x > .5) n=UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(0),0),_LTSettings0.x);
#else
        float2 dx=uvDx/max(_LTTiling0.xy,.001),dy=uvDy/max(_LTTiling0.xy,.001);
        color=SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(0),dx,dy).rgb*_LTTint0.rgb;
        if(_LTSettings0.w > .5) mask=SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(0),dx,dy).rgb;
        if(_LTFlags0.x > .5) n=UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(0),dx,dy),_LTSettings0.x);
#endif
        normalTS=normalize(n+float3(0,0,.00001));
        ao=saturate(1+(mask.x-1)*_LTFlags0.y);smoothness=mask.z;metallic=_LTSettings0.y;
        gradientLocal=LTProjectionGradient(normalTS,projectionAxis);
        return;
    }
    float2 coverageUV = (coveragePosition - _LTRect.xy) / _LTRect.zw;
    // Coverage texels include both endpoints, so adjacent chunks agree at their border.
    coverageUV = (coverageUV * 256.0 + .5) / 257.0;
    float4 w0 = SAMPLE_TEXTURE2D_LOD(_LTWeights0, sampler_LinearClamp, coverageUV, 0);
    float4 w1 = SAMPLE_TEXTURE2D_LOD(_LTWeights1, sampler_LinearClamp, coverageUV, 0);
    float4 w2 = SAMPLE_TEXTURE2D_LOD(_LTWeights2, sampler_LinearClamp, coverageUV, 0);
    float weights[12] = {w0.x,w0.y,w0.z,w0.w,w1.x,w1.y,w1.z,w1.w,w2.x,w2.y,w2.z,w2.w};
    float3 colors[12], normals[12], masks[12];

    {
        float2 uv = position / max(_LTTiling0.xy, .001) + _LTTiling0.zw;
        float2 dx=uvDx/max(_LTTiling0.xy,.001),dy=uvDy/max(_LTTiling0.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(0,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[0] = 0; normals[0] = float3(0,0,1); masks[0] = float3(1,.5,_LTSettings0.z);
        if(weights[0] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[0] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(0), 0).rgb * _LTTint0.rgb;
            if(_LTSettings0.w > .5) masks[0] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(0), 0).rgb;
            if(_LTFlags0.x > .5) normals[0] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(0), 0),_LTSettings0.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[0] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(0), dx, dy).rgb * _LTTint0.rgb;
            if(_LTSettings0.w > .5) masks[0] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(0), dx, dy).rgb;
            if(_LTFlags0.x > .5) normals[0] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(0), dx, dy),_LTSettings0.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[0]=LTRoadLayerGradient(0,normals[0],roadRight,projectionAxis);
    }

    {
        float2 uv = position / max(_LTTiling1.xy, .001) + _LTTiling1.zw;
        float2 dx=uvDx/max(_LTTiling1.xy,.001),dy=uvDy/max(_LTTiling1.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(1,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[1] = 0; normals[1] = float3(0,0,1); masks[1] = float3(1,.5,_LTSettings1.z);
        if(weights[1] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[1] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(1), 0).rgb * _LTTint1.rgb;
            if(_LTSettings1.w > .5) masks[1] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(1), 0).rgb;
            if(_LTFlags1.x > .5) normals[1] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(1), 0),_LTSettings1.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[1] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(1), dx, dy).rgb * _LTTint1.rgb;
            if(_LTSettings1.w > .5) masks[1] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(1), dx, dy).rgb;
            if(_LTFlags1.x > .5) normals[1] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(1), dx, dy),_LTSettings1.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[1]=LTRoadLayerGradient(1,normals[1],roadRight,projectionAxis);
    }

    {
        float2 uv = position / max(_LTTiling2.xy, .001) + _LTTiling2.zw;
        float2 dx=uvDx/max(_LTTiling2.xy,.001),dy=uvDy/max(_LTTiling2.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(2,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[2] = 0; normals[2] = float3(0,0,1); masks[2] = float3(1,.5,_LTSettings2.z);
        if(weights[2] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[2] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(2), 0).rgb * _LTTint2.rgb;
            if(_LTSettings2.w > .5) masks[2] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(2), 0).rgb;
            if(_LTFlags2.x > .5) normals[2] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(2), 0),_LTSettings2.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[2] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(2), dx, dy).rgb * _LTTint2.rgb;
            if(_LTSettings2.w > .5) masks[2] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(2), dx, dy).rgb;
            if(_LTFlags2.x > .5) normals[2] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(2), dx, dy),_LTSettings2.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[2]=LTRoadLayerGradient(2,normals[2],roadRight,projectionAxis);
    }

    {
        float2 uv = position / max(_LTTiling3.xy, .001) + _LTTiling3.zw;
        float2 dx=uvDx/max(_LTTiling3.xy,.001),dy=uvDy/max(_LTTiling3.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(3,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[3] = 0; normals[3] = float3(0,0,1); masks[3] = float3(1,.5,_LTSettings3.z);
        if(weights[3] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[3] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(3), 0).rgb * _LTTint3.rgb;
            if(_LTSettings3.w > .5) masks[3] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(3), 0).rgb;
            if(_LTFlags3.x > .5) normals[3] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(3), 0),_LTSettings3.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[3] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(3), dx, dy).rgb * _LTTint3.rgb;
            if(_LTSettings3.w > .5) masks[3] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(3), dx, dy).rgb;
            if(_LTFlags3.x > .5) normals[3] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(3), dx, dy),_LTSettings3.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[3]=LTRoadLayerGradient(3,normals[3],roadRight,projectionAxis);
    }

    {
        float2 uv = position / max(_LTTiling4.xy, .001) + _LTTiling4.zw;
        float2 dx=uvDx/max(_LTTiling4.xy,.001),dy=uvDy/max(_LTTiling4.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(4,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[4] = 0; normals[4] = float3(0,0,1); masks[4] = float3(1,.5,_LTSettings4.z);
        if(weights[4] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[4] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(4), 0).rgb * _LTTint4.rgb;
            if(_LTSettings4.w > .5) masks[4] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(4), 0).rgb;
            if(_LTFlags4.x > .5) normals[4] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(4), 0),_LTSettings4.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[4] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(4), dx, dy).rgb * _LTTint4.rgb;
            if(_LTSettings4.w > .5) masks[4] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(4), dx, dy).rgb;
            if(_LTFlags4.x > .5) normals[4] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(4), dx, dy),_LTSettings4.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[4]=LTRoadLayerGradient(4,normals[4],roadRight,projectionAxis);
    }

    {
        float2 uv = position / max(_LTTiling5.xy, .001) + _LTTiling5.zw;
        float2 dx=uvDx/max(_LTTiling5.xy,.001),dy=uvDy/max(_LTTiling5.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(5,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[5] = 0; normals[5] = float3(0,0,1); masks[5] = float3(1,.5,_LTSettings5.z);
        if(weights[5] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[5] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(5), 0).rgb * _LTTint5.rgb;
            if(_LTSettings5.w > .5) masks[5] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(5), 0).rgb;
            if(_LTFlags5.x > .5) normals[5] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(5), 0),_LTSettings5.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[5] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(5), dx, dy).rgb * _LTTint5.rgb;
            if(_LTSettings5.w > .5) masks[5] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(5), dx, dy).rgb;
            if(_LTFlags5.x > .5) normals[5] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(5), dx, dy),_LTSettings5.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[5]=LTRoadLayerGradient(5,normals[5],roadRight,projectionAxis);
    }

    {
        float2 uv = position / max(_LTTiling6.xy, .001) + _LTTiling6.zw;
        float2 dx=uvDx/max(_LTTiling6.xy,.001),dy=uvDy/max(_LTTiling6.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(6,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[6] = 0; normals[6] = float3(0,0,1); masks[6] = float3(1,.5,_LTSettings6.z);
        if(weights[6] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[6] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(6), 0).rgb * _LTTint6.rgb;
            if(_LTSettings6.w > .5) masks[6] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(6), 0).rgb;
            if(_LTFlags6.x > .5) normals[6] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(6), 0),_LTSettings6.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[6] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(6), dx, dy).rgb * _LTTint6.rgb;
            if(_LTSettings6.w > .5) masks[6] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(6), dx, dy).rgb;
            if(_LTFlags6.x > .5) normals[6] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(6), dx, dy),_LTSettings6.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[6]=LTRoadLayerGradient(6,normals[6],roadRight,projectionAxis);
    }

    {
        float2 uv = position / max(_LTTiling7.xy, .001) + _LTTiling7.zw;
        float2 dx=uvDx/max(_LTTiling7.xy,.001),dy=uvDy/max(_LTTiling7.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(7,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[7] = 0; normals[7] = float3(0,0,1); masks[7] = float3(1,.5,_LTSettings7.z);
        if(weights[7] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[7] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(7), 0).rgb * _LTTint7.rgb;
            if(_LTSettings7.w > .5) masks[7] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(7), 0).rgb;
            if(_LTFlags7.x > .5) normals[7] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(7), 0),_LTSettings7.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[7] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(7), dx, dy).rgb * _LTTint7.rgb;
            if(_LTSettings7.w > .5) masks[7] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(7), dx, dy).rgb;
            if(_LTFlags7.x > .5) normals[7] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(7), dx, dy),_LTSettings7.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[7]=LTRoadLayerGradient(7,normals[7],roadRight,projectionAxis);
    }
    colors[8]=0; normals[8]=0; masks[8]=float3(1,.5,_LTSettings8.z);
    [branch] if(weights[8] > .00001)
    {
        float2 uv = position / max(_LTTiling8.xy, .001) + _LTTiling8.zw;
        float2 dx=uvDx/max(_LTTiling8.xy,.001),dy=uvDy/max(_LTTiling8.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(8,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[8] = 0; normals[8] = float3(0,0,1); masks[8] = float3(1,.5,_LTSettings8.z);
        if(weights[8] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[8] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(8), 0).rgb * _LTTint8.rgb;
            if(_LTSettings8.w > .5) masks[8] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(8), 0).rgb;
            if(_LTFlags8.x > .5) normals[8] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(8), 0),_LTSettings8.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[8] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(8), dx, dy).rgb * _LTTint8.rgb;
            if(_LTSettings8.w > .5) masks[8] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(8), dx, dy).rgb;
            if(_LTFlags8.x > .5) normals[8] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(8), dx, dy),_LTSettings8.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[8]=LTRoadLayerGradient(8,normals[8],roadRight,projectionAxis);
    }
    colors[9]=0; normals[9]=0; masks[9]=float3(1,.5,_LTSettings9.z);
    [branch] if(weights[9] > .00001)
    {
        float2 uv = position / max(_LTTiling9.xy, .001) + _LTTiling9.zw;
        float2 dx=uvDx/max(_LTTiling9.xy,.001),dy=uvDy/max(_LTTiling9.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(9,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[9] = 0; normals[9] = float3(0,0,1); masks[9] = float3(1,.5,_LTSettings9.z);
        if(weights[9] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[9] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(9), 0).rgb * _LTTint9.rgb;
            if(_LTSettings9.w > .5) masks[9] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(9), 0).rgb;
            if(_LTFlags9.x > .5) normals[9] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(9), 0),_LTSettings9.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[9] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(9), dx, dy).rgb * _LTTint9.rgb;
            if(_LTSettings9.w > .5) masks[9] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(9), dx, dy).rgb;
            if(_LTFlags9.x > .5) normals[9] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(9), dx, dy),_LTSettings9.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[9]=LTRoadLayerGradient(9,normals[9],roadRight,projectionAxis);
    }
    colors[10]=0; normals[10]=0; masks[10]=float3(1,.5,_LTSettings10.z);
    [branch] if(weights[10] > .00001)
    {
        float2 uv = position / max(_LTTiling10.xy, .001) + _LTTiling10.zw;
        float2 dx=uvDx/max(_LTTiling10.xy,.001),dy=uvDy/max(_LTTiling10.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(10,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[10] = 0; normals[10] = float3(0,0,1); masks[10] = float3(1,.5,_LTSettings10.z);
        if(weights[10] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[10] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(10), 0).rgb * _LTTint10.rgb;
            if(_LTSettings10.w > .5) masks[10] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(10), 0).rgb;
            if(_LTFlags10.x > .5) normals[10] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(10), 0),_LTSettings10.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[10] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(10), dx, dy).rgb * _LTTint10.rgb;
            if(_LTSettings10.w > .5) masks[10] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(10), dx, dy).rgb;
            if(_LTFlags10.x > .5) normals[10] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(10), dx, dy),_LTSettings10.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[10]=LTRoadLayerGradient(10,normals[10],roadRight,projectionAxis);
    }
    colors[11]=0; normals[11]=0; masks[11]=float3(1,.5,_LTSettings11.z);
    [branch] if(weights[11] > .00001)
    {
        float2 uv = position / max(_LTTiling11.xy, .001) + _LTTiling11.zw;
        float2 dx=uvDx/max(_LTTiling11.xy,.001),dy=uvDy/max(_LTTiling11.xy,.001);
        float2 roadRight;
        LTRoadMappedUV(11,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
        colors[11] = 0; normals[11] = float3(0,0,1); masks[11] = float3(1,.5,_LTSettings11.z);
        if(weights[11] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[11] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(11), 0).rgb * _LTTint11.rgb;
            if(_LTSettings11.w > .5) masks[11] = SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(11), 0).rgb;
            if(_LTFlags11.x > .5) normals[11] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(11), 0),_LTSettings11.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[11] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(11), dx, dy).rgb * _LTTint11.rgb;
            if(_LTSettings11.w > .5) masks[11] = SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(11), dx, dy).rgb;
            if(_LTFlags11.x > .5) normals[11] = UnpackNormalRGB(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(11), dx, dy),_LTSettings11.x);
#endif
        }
        [branch] if(_LTRoadProjectionEnabled>.5)
            normals[11]=LTRoadLayerGradient(11,normals[11],roadRight,projectionAxis);
    }

    float layerMetallic[12] = {_LTSettings0.y,_LTSettings1.y,_LTSettings2.y,_LTSettings3.y,_LTSettings4.y,_LTSettings5.y,_LTSettings6.y,_LTSettings7.y,_LTSettings8.y,_LTSettings9.y,_LTSettings10.y,_LTSettings11.y};
    // Per-layer RGB mask controls: flags = normal present, AO strength, height contrast, height offset.
    masks[0].x = saturate(1.0 + (masks[0].x - 1.0) * _LTFlags0.y);
    masks[0].y = saturate((masks[0].y - .5) * _LTFlags0.z + .5 + _LTFlags0.w);
    masks[1].x = saturate(1.0 + (masks[1].x - 1.0) * _LTFlags1.y);
    masks[1].y = saturate((masks[1].y - .5) * _LTFlags1.z + .5 + _LTFlags1.w);
    masks[2].x = saturate(1.0 + (masks[2].x - 1.0) * _LTFlags2.y);
    masks[2].y = saturate((masks[2].y - .5) * _LTFlags2.z + .5 + _LTFlags2.w);
    masks[3].x = saturate(1.0 + (masks[3].x - 1.0) * _LTFlags3.y);
    masks[3].y = saturate((masks[3].y - .5) * _LTFlags3.z + .5 + _LTFlags3.w);
    masks[4].x = saturate(1.0 + (masks[4].x - 1.0) * _LTFlags4.y);
    masks[4].y = saturate((masks[4].y - .5) * _LTFlags4.z + .5 + _LTFlags4.w);
    masks[5].x = saturate(1.0 + (masks[5].x - 1.0) * _LTFlags5.y);
    masks[5].y = saturate((masks[5].y - .5) * _LTFlags5.z + .5 + _LTFlags5.w);
    masks[6].x = saturate(1.0 + (masks[6].x - 1.0) * _LTFlags6.y);
    masks[6].y = saturate((masks[6].y - .5) * _LTFlags6.z + .5 + _LTFlags6.w);
    masks[7].x = saturate(1.0 + (masks[7].x - 1.0) * _LTFlags7.y);
    masks[7].y = saturate((masks[7].y - .5) * _LTFlags7.z + .5 + _LTFlags7.w);
    masks[8].x = saturate(1.0 + (masks[8].x - 1.0) * _LTFlags8.y);
    masks[8].y = saturate((masks[8].y - .5) * _LTFlags8.z + .5 + _LTFlags8.w);
    masks[9].x = saturate(1.0 + (masks[9].x - 1.0) * _LTFlags9.y);
    masks[9].y = saturate((masks[9].y - .5) * _LTFlags9.z + .5 + _LTFlags9.w);
    masks[10].x = saturate(1.0 + (masks[10].x - 1.0) * _LTFlags10.y);
    masks[10].y = saturate((masks[10].y - .5) * _LTFlags10.z + .5 + _LTFlags10.w);
    masks[11].x = saturate(1.0 + (masks[11].x - 1.0) * _LTFlags11.y);
    masks[11].y = saturate((masks[11].y - .5) * _LTFlags11.z + .5 + _LTFlags11.w);
    float maxHeight = -1;
    [unroll] for(int i=0;i<12;i++) if(weights[i] > .00001) maxHeight=max(maxHeight,masks[i].y+weights[i]);
    float total=0;
    [unroll] for(int j=0;j<12;j++)
    {
        if(_LTHeightBlend > .0001)
            weights[j] *= lerp(1,saturate((masks[j].y+weights[j]-maxHeight+.2)/.2),_LTHeightBlend);
        total+=weights[j];
    }
    color=0; float3 n=0; ao=0; smoothness=0; metallic=0;
    [unroll] for(int k=0;k<12;k++)
    {
        float w=weights[k]/max(total,.00001);
        color+=colors[k]*w; n+=normals[k]*w;
        ao+=masks[k].x*w; smoothness+=masks[k].z*w; metallic+=layerMetallic[k]*w;
    }
    if(_LTRoadProjectionEnabled>.5)
    {
        // In spline-enabled chunks each layer contributes a gradient in the SAME
        // terrain frame, before the smooth layer blend. The planar tangent frame
        // is +X/+Z; projected callers consume gradientLocal directly.
        gradientLocal=n;
        normalTS=normalize(float3(-n.x,-n.z,1));
    }
    else
    {
        normalTS=normalize(n+float3(0,0,.00001));
        gradientLocal=LTProjectionGradient(normalTS,projectionAxis);
    }
}

void LTSampleLayersMapped(float2 coveragePosition,float2 position,float2 uvDx,float2 uvDy,
    out float3 color,out float3 normalTS,out float ao,out float smoothness,out float metallic)
{
    // Legacy planar caller: its coverage and texture projection are both XZ.
    float3 unused;
    LTSampleLayersMappedFrame(coveragePosition,uvDx,uvDy,position,uvDx,uvDy,1,
        color,normalTS,ao,smoothness,metallic,unused);
}

// Existing terrain and atlas callers keep their XZ mapping unchanged.
void LTSampleLayers(float2 position,float2 uvDx,float2 uvDy,out float3 color,out float3 normalTS,out float ao,out float smoothness,out float metallic)
{
    LTSampleLayersMapped(position,position,uvDx,uvDy,color,normalTS,ao,smoothness,metallic);
}
