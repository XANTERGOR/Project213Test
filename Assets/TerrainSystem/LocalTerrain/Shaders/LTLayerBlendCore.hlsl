// Eight independent texture bindings, not Texture2DArray.
// Reuse HDRP's guarded global sampler declarations; do not redeclare them.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
TEXTURE2D(_LTWeights0);
TEXTURE2D(_LTWeights1);
TEXTURE2D(_LTColor0); TEXTURE2D(_LTNormal0); TEXTURE2D(_LTMask0);
TEXTURE2D(_LTColor1); TEXTURE2D(_LTNormal1); TEXTURE2D(_LTMask1);
TEXTURE2D(_LTColor2); TEXTURE2D(_LTNormal2); TEXTURE2D(_LTMask2);
TEXTURE2D(_LTColor3); TEXTURE2D(_LTNormal3); TEXTURE2D(_LTMask3);
TEXTURE2D(_LTColor4); TEXTURE2D(_LTNormal4); TEXTURE2D(_LTMask4);
TEXTURE2D(_LTColor5); TEXTURE2D(_LTNormal5); TEXTURE2D(_LTMask5);
TEXTURE2D(_LTColor6); TEXTURE2D(_LTNormal6); TEXTURE2D(_LTMask6);
TEXTURE2D(_LTColor7); TEXTURE2D(_LTNormal7); TEXTURE2D(_LTMask7);

void LTSampleLayersMapped(float2 coveragePosition, float2 position, float2 uvDx, float2 uvDy, out float3 color, out float3 normalTS, out float ao, out float smoothness, out float metallic)
{
    // Uniform per-chunk fast path. No coverage reads, eight-layer arrays or height
    // blending on chunks containing only the background. The baker shares this path.
    [branch] if(_LTBaseOnly > .5)
    {
        float2 uv=position/max(_LTTiling0.xy,.001)+_LTTiling0.zw;
        float3 mask=float3(1,.5,_LTSettings0.z);
        float3 n=float3(0,0,1);
#if defined(SHADER_STAGE_RAY_TRACING)
        color=SAMPLE_TEXTURE2D_LOD(_LTColor0,sampler_LinearRepeat,uv,0).rgb*_LTTint0.rgb;
        if(_LTSettings0.w > .5) mask=SAMPLE_TEXTURE2D_LOD(_LTMask0,sampler_LinearRepeat,uv,0).rgb;
        if(_LTFlags0.x > .5) n=UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_LTNormal0,sampler_LinearRepeat,uv,0),_LTSettings0.x);
#else
        float2 dx=uvDx/max(_LTTiling0.xy,.001),dy=uvDy/max(_LTTiling0.xy,.001);
        color=SAMPLE_TEXTURE2D_GRAD(_LTColor0,sampler_LinearRepeat,uv,dx,dy).rgb*_LTTint0.rgb;
        if(_LTSettings0.w > .5) mask=SAMPLE_TEXTURE2D_GRAD(_LTMask0,sampler_LinearRepeat,uv,dx,dy).rgb;
        if(_LTFlags0.x > .5) n=UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_LTNormal0,sampler_LinearRepeat,uv,dx,dy),_LTSettings0.x);
#endif
        normalTS=normalize(n+float3(0,0,.00001));
        ao=saturate(1+(mask.x-1)*_LTFlags0.y);smoothness=mask.z;metallic=_LTSettings0.y;
        return;
    }
    float2 coverageUV = (coveragePosition - _LTRect.xy) / _LTRect.zw;
    // Coverage texels include both endpoints, so adjacent chunks agree at their border.
    coverageUV = (coverageUV * 256.0 + .5) / 257.0;
    float4 w0 = SAMPLE_TEXTURE2D_LOD(_LTWeights0, sampler_LinearClamp, coverageUV, 0);
    float4 w1 = SAMPLE_TEXTURE2D_LOD(_LTWeights1, sampler_LinearClamp, coverageUV, 0);
    float weights[8] = {w0.x,w0.y,w0.z,w0.w,w1.x,w1.y,w1.z,w1.w};
    float3 colors[8], normals[8], masks[8];

    {
        float2 uv = position / max(_LTTiling0.xy, .001) + _LTTiling0.zw;
        colors[0] = 0; normals[0] = float3(0,0,1); masks[0] = float3(1,.5,_LTSettings0.z);
        if(weights[0] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[0] = SAMPLE_TEXTURE2D_LOD(_LTColor0, sampler_LinearRepeat, uv, 0).rgb * _LTTint0.rgb;
            if(_LTSettings0.w > .5) masks[0] = SAMPLE_TEXTURE2D_LOD(_LTMask0, sampler_LinearRepeat, uv, 0).rgb;
            if(_LTFlags0.x > .5) normals[0] = UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_LTNormal0, sampler_LinearRepeat, uv, 0),_LTSettings0.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[0] = SAMPLE_TEXTURE2D_GRAD(_LTColor0, sampler_LinearRepeat, uv, uvDx/max(_LTTiling0.xy,.001), uvDy/max(_LTTiling0.xy,.001)).rgb * _LTTint0.rgb;
            if(_LTSettings0.w > .5) masks[0] = SAMPLE_TEXTURE2D_GRAD(_LTMask0, sampler_LinearRepeat, uv, uvDx/max(_LTTiling0.xy,.001), uvDy/max(_LTTiling0.xy,.001)).rgb;
            if(_LTFlags0.x > .5) normals[0] = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_LTNormal0, sampler_LinearRepeat, uv, uvDx/max(_LTTiling0.xy,.001), uvDy/max(_LTTiling0.xy,.001)),_LTSettings0.x);
#endif
        }
    }

    {
        float2 uv = position / max(_LTTiling1.xy, .001) + _LTTiling1.zw;
        colors[1] = 0; normals[1] = float3(0,0,1); masks[1] = float3(1,.5,_LTSettings1.z);
        if(weights[1] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[1] = SAMPLE_TEXTURE2D_LOD(_LTColor1, sampler_LinearRepeat, uv, 0).rgb * _LTTint1.rgb;
            if(_LTSettings1.w > .5) masks[1] = SAMPLE_TEXTURE2D_LOD(_LTMask1, sampler_LinearRepeat, uv, 0).rgb;
            if(_LTFlags1.x > .5) normals[1] = UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_LTNormal1, sampler_LinearRepeat, uv, 0),_LTSettings1.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[1] = SAMPLE_TEXTURE2D_GRAD(_LTColor1, sampler_LinearRepeat, uv, uvDx/max(_LTTiling1.xy,.001), uvDy/max(_LTTiling1.xy,.001)).rgb * _LTTint1.rgb;
            if(_LTSettings1.w > .5) masks[1] = SAMPLE_TEXTURE2D_GRAD(_LTMask1, sampler_LinearRepeat, uv, uvDx/max(_LTTiling1.xy,.001), uvDy/max(_LTTiling1.xy,.001)).rgb;
            if(_LTFlags1.x > .5) normals[1] = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_LTNormal1, sampler_LinearRepeat, uv, uvDx/max(_LTTiling1.xy,.001), uvDy/max(_LTTiling1.xy,.001)),_LTSettings1.x);
#endif
        }
    }

    {
        float2 uv = position / max(_LTTiling2.xy, .001) + _LTTiling2.zw;
        colors[2] = 0; normals[2] = float3(0,0,1); masks[2] = float3(1,.5,_LTSettings2.z);
        if(weights[2] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[2] = SAMPLE_TEXTURE2D_LOD(_LTColor2, sampler_LinearRepeat, uv, 0).rgb * _LTTint2.rgb;
            if(_LTSettings2.w > .5) masks[2] = SAMPLE_TEXTURE2D_LOD(_LTMask2, sampler_LinearRepeat, uv, 0).rgb;
            if(_LTFlags2.x > .5) normals[2] = UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_LTNormal2, sampler_LinearRepeat, uv, 0),_LTSettings2.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[2] = SAMPLE_TEXTURE2D_GRAD(_LTColor2, sampler_LinearRepeat, uv, uvDx/max(_LTTiling2.xy,.001), uvDy/max(_LTTiling2.xy,.001)).rgb * _LTTint2.rgb;
            if(_LTSettings2.w > .5) masks[2] = SAMPLE_TEXTURE2D_GRAD(_LTMask2, sampler_LinearRepeat, uv, uvDx/max(_LTTiling2.xy,.001), uvDy/max(_LTTiling2.xy,.001)).rgb;
            if(_LTFlags2.x > .5) normals[2] = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_LTNormal2, sampler_LinearRepeat, uv, uvDx/max(_LTTiling2.xy,.001), uvDy/max(_LTTiling2.xy,.001)),_LTSettings2.x);
#endif
        }
    }

    {
        float2 uv = position / max(_LTTiling3.xy, .001) + _LTTiling3.zw;
        colors[3] = 0; normals[3] = float3(0,0,1); masks[3] = float3(1,.5,_LTSettings3.z);
        if(weights[3] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[3] = SAMPLE_TEXTURE2D_LOD(_LTColor3, sampler_LinearRepeat, uv, 0).rgb * _LTTint3.rgb;
            if(_LTSettings3.w > .5) masks[3] = SAMPLE_TEXTURE2D_LOD(_LTMask3, sampler_LinearRepeat, uv, 0).rgb;
            if(_LTFlags3.x > .5) normals[3] = UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_LTNormal3, sampler_LinearRepeat, uv, 0),_LTSettings3.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[3] = SAMPLE_TEXTURE2D_GRAD(_LTColor3, sampler_LinearRepeat, uv, uvDx/max(_LTTiling3.xy,.001), uvDy/max(_LTTiling3.xy,.001)).rgb * _LTTint3.rgb;
            if(_LTSettings3.w > .5) masks[3] = SAMPLE_TEXTURE2D_GRAD(_LTMask3, sampler_LinearRepeat, uv, uvDx/max(_LTTiling3.xy,.001), uvDy/max(_LTTiling3.xy,.001)).rgb;
            if(_LTFlags3.x > .5) normals[3] = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_LTNormal3, sampler_LinearRepeat, uv, uvDx/max(_LTTiling3.xy,.001), uvDy/max(_LTTiling3.xy,.001)),_LTSettings3.x);
#endif
        }
    }

    {
        float2 uv = position / max(_LTTiling4.xy, .001) + _LTTiling4.zw;
        colors[4] = 0; normals[4] = float3(0,0,1); masks[4] = float3(1,.5,_LTSettings4.z);
        if(weights[4] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[4] = SAMPLE_TEXTURE2D_LOD(_LTColor4, sampler_LinearRepeat, uv, 0).rgb * _LTTint4.rgb;
            if(_LTSettings4.w > .5) masks[4] = SAMPLE_TEXTURE2D_LOD(_LTMask4, sampler_LinearRepeat, uv, 0).rgb;
            if(_LTFlags4.x > .5) normals[4] = UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_LTNormal4, sampler_LinearRepeat, uv, 0),_LTSettings4.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[4] = SAMPLE_TEXTURE2D_GRAD(_LTColor4, sampler_LinearRepeat, uv, uvDx/max(_LTTiling4.xy,.001), uvDy/max(_LTTiling4.xy,.001)).rgb * _LTTint4.rgb;
            if(_LTSettings4.w > .5) masks[4] = SAMPLE_TEXTURE2D_GRAD(_LTMask4, sampler_LinearRepeat, uv, uvDx/max(_LTTiling4.xy,.001), uvDy/max(_LTTiling4.xy,.001)).rgb;
            if(_LTFlags4.x > .5) normals[4] = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_LTNormal4, sampler_LinearRepeat, uv, uvDx/max(_LTTiling4.xy,.001), uvDy/max(_LTTiling4.xy,.001)),_LTSettings4.x);
#endif
        }
    }

    {
        float2 uv = position / max(_LTTiling5.xy, .001) + _LTTiling5.zw;
        colors[5] = 0; normals[5] = float3(0,0,1); masks[5] = float3(1,.5,_LTSettings5.z);
        if(weights[5] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[5] = SAMPLE_TEXTURE2D_LOD(_LTColor5, sampler_LinearRepeat, uv, 0).rgb * _LTTint5.rgb;
            if(_LTSettings5.w > .5) masks[5] = SAMPLE_TEXTURE2D_LOD(_LTMask5, sampler_LinearRepeat, uv, 0).rgb;
            if(_LTFlags5.x > .5) normals[5] = UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_LTNormal5, sampler_LinearRepeat, uv, 0),_LTSettings5.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[5] = SAMPLE_TEXTURE2D_GRAD(_LTColor5, sampler_LinearRepeat, uv, uvDx/max(_LTTiling5.xy,.001), uvDy/max(_LTTiling5.xy,.001)).rgb * _LTTint5.rgb;
            if(_LTSettings5.w > .5) masks[5] = SAMPLE_TEXTURE2D_GRAD(_LTMask5, sampler_LinearRepeat, uv, uvDx/max(_LTTiling5.xy,.001), uvDy/max(_LTTiling5.xy,.001)).rgb;
            if(_LTFlags5.x > .5) normals[5] = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_LTNormal5, sampler_LinearRepeat, uv, uvDx/max(_LTTiling5.xy,.001), uvDy/max(_LTTiling5.xy,.001)),_LTSettings5.x);
#endif
        }
    }

    {
        float2 uv = position / max(_LTTiling6.xy, .001) + _LTTiling6.zw;
        colors[6] = 0; normals[6] = float3(0,0,1); masks[6] = float3(1,.5,_LTSettings6.z);
        if(weights[6] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[6] = SAMPLE_TEXTURE2D_LOD(_LTColor6, sampler_LinearRepeat, uv, 0).rgb * _LTTint6.rgb;
            if(_LTSettings6.w > .5) masks[6] = SAMPLE_TEXTURE2D_LOD(_LTMask6, sampler_LinearRepeat, uv, 0).rgb;
            if(_LTFlags6.x > .5) normals[6] = UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_LTNormal6, sampler_LinearRepeat, uv, 0),_LTSettings6.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[6] = SAMPLE_TEXTURE2D_GRAD(_LTColor6, sampler_LinearRepeat, uv, uvDx/max(_LTTiling6.xy,.001), uvDy/max(_LTTiling6.xy,.001)).rgb * _LTTint6.rgb;
            if(_LTSettings6.w > .5) masks[6] = SAMPLE_TEXTURE2D_GRAD(_LTMask6, sampler_LinearRepeat, uv, uvDx/max(_LTTiling6.xy,.001), uvDy/max(_LTTiling6.xy,.001)).rgb;
            if(_LTFlags6.x > .5) normals[6] = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_LTNormal6, sampler_LinearRepeat, uv, uvDx/max(_LTTiling6.xy,.001), uvDy/max(_LTTiling6.xy,.001)),_LTSettings6.x);
#endif
        }
    }

    {
        float2 uv = position / max(_LTTiling7.xy, .001) + _LTTiling7.zw;
        colors[7] = 0; normals[7] = float3(0,0,1); masks[7] = float3(1,.5,_LTSettings7.z);
        if(weights[7] > 0.00001)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            colors[7] = SAMPLE_TEXTURE2D_LOD(_LTColor7, sampler_LinearRepeat, uv, 0).rgb * _LTTint7.rgb;
            if(_LTSettings7.w > .5) masks[7] = SAMPLE_TEXTURE2D_LOD(_LTMask7, sampler_LinearRepeat, uv, 0).rgb;
            if(_LTFlags7.x > .5) normals[7] = UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_LTNormal7, sampler_LinearRepeat, uv, 0),_LTSettings7.x);
#else
            // Explicit gradients remain valid in weight-dependent branches.
            colors[7] = SAMPLE_TEXTURE2D_GRAD(_LTColor7, sampler_LinearRepeat, uv, uvDx/max(_LTTiling7.xy,.001), uvDy/max(_LTTiling7.xy,.001)).rgb * _LTTint7.rgb;
            if(_LTSettings7.w > .5) masks[7] = SAMPLE_TEXTURE2D_GRAD(_LTMask7, sampler_LinearRepeat, uv, uvDx/max(_LTTiling7.xy,.001), uvDy/max(_LTTiling7.xy,.001)).rgb;
            if(_LTFlags7.x > .5) normals[7] = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_LTNormal7, sampler_LinearRepeat, uv, uvDx/max(_LTTiling7.xy,.001), uvDy/max(_LTTiling7.xy,.001)),_LTSettings7.x);
#endif
        }
    }

    float layerMetallic[8] = {_LTSettings0.y,_LTSettings1.y,_LTSettings2.y,_LTSettings3.y,_LTSettings4.y,_LTSettings5.y,_LTSettings6.y,_LTSettings7.y};
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
    float maxHeight = -1;
    [unroll] for(int i=0;i<8;i++) if(weights[i] > .00001) maxHeight=max(maxHeight,masks[i].y+weights[i]);
    float total=0;
    [unroll] for(int j=0;j<8;j++)
    {
        if(_LTHeightBlend > .0001)
            weights[j] *= lerp(1,saturate((masks[j].y+weights[j]-maxHeight+.2)/.2),_LTHeightBlend);
        total+=weights[j];
    }
    color=0; float3 n=0; ao=0; smoothness=0; metallic=0;
    [unroll] for(int k=0;k<8;k++)
    {
        float w=weights[k]/max(total,.00001);
        color+=colors[k]*w; n+=normals[k]*w;
        ao+=masks[k].x*w; smoothness+=masks[k].z*w; metallic+=layerMetallic[k]*w;
    }
    normalTS=normalize(n+float3(0,0,.00001));
}

// Existing terrain and atlas callers keep their XZ mapping unchanged.
void LTSampleLayers(float2 position,float2 uvDx,float2 uvDy,out float3 color,out float3 normalTS,out float ao,out float smoothness,out float metallic)
{
    LTSampleLayersMapped(position,position,uvDx,uvDy,color,normalTS,ao,smoothness,metallic);
}
