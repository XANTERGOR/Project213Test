// Road support is compiled only for chunks that need spline UVs or asphalt damage.
// Explicit gradients are supplied BEFORE the weight-dependent texture branches.
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTFastLayerPair.hlsl"
#if !defined(_LT_FAST_PREHEIGHT)
void LTStrongestLayerPair(float weights[12],out int firstSlot,out int secondSlot)
{
    firstSlot=0;secondSlot=0;
    float firstWeight=0,secondWeight=0,firstSlice=1e20,secondSlice=1e20;
    [unroll] for(int rank=0;rank<12;rank++)
    {
        float w=weights[rank],slice=LTLayerSlice(rank);
        // Global palette ids keep equal-weight choices stable across chunk slots.
        if(w>0 && (w>firstWeight || (w==firstWeight && slice<firstSlice)))
        {
            secondSlot=firstSlot;secondWeight=firstWeight;secondSlice=firstSlice;
            firstSlot=rank;firstWeight=w;firstSlice=slice;
        }
        else if(w>0 && (w>secondWeight || (w==secondWeight && slice<secondSlice)))
        {secondSlot=rank;secondWeight=w;secondSlice=slice;}
    }
}
#endif
void LTSampleLayersMappedFrameWithCoverage(float2 coveragePosition,float2 coverageDx,float2 coverageDy,
    float2 position,float2 uvDx,float2 uvDy,int projectionAxis,float4 w0,float4 w1,float4 w2,
    out float3 color,out float3 normalTS,out float ao,out float smoothness,out float metallic,
    out float3 gradientLocal)
{
    color=0;normalTS=float3(0,0,1);ao=0;smoothness=0;metallic=0;gradientLocal=0;
    // Keep the genuinely cheap uniform path on background-only chunks.
    [branch] if(_LTBaseOnly>.5)
    {
        float2 scale=max(_LTTiling0.xy,.001),uv=position/scale+_LTTiling0.zw;
        float3 mask=float3(1,.5,_LTSettings0.z),n=float3(0,0,1);
#if defined(SHADER_STAGE_RAY_TRACING)
        color=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(0),0).rgb*_LTTint0.rgb;
        if(_LTSettings0.w>.5)mask=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(0),0).rgb;
        if(_LTFlags0.x>.5)n=LTUnpackLayerNormal(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(0),0),_LTSettings0.x,_LTFlags0.x);
#else
        float2 dx=uvDx/scale,dy=uvDy/scale;
        color=SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(0),dx,dy).rgb*_LTTint0.rgb;
        if(_LTSettings0.w>.5)mask=SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(0),dx,dy).rgb;
        if(_LTFlags0.x>.5)n=LTUnpackLayerNormal(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(0),dx,dy),_LTSettings0.x,_LTFlags0.x);
#endif
        normalTS=normalize(n+float3(0,0,.00001));
        ao=saturate(1+(mask.x-1)*_LTFlags0.y);smoothness=mask.z;metallic=_LTSettings0.y;
        gradientLocal=LTProjectionGradient(normalTS,projectionAxis);return;
    }
#if defined(_LT_FAST_PREHEIGHT)
    // Planar callers also use the specialization; projected callers prepare the
    // pair once outside their axis loop and call LTSampleLayerPair directly.
    LTLayerPairData first,second;LTPrepareLayerPair(w0,w1,w2,first,second);
    LTSampleLayerPair(first,second,coveragePosition,coverageDx,coverageDy,position,uvDx,uvDy,projectionAxis,
        color,normalTS,ao,smoothness,metallic,gradientLocal);
#if defined(_LT_FAST_COLOR_REMAINDER)
    LTBlendPairRemainderColor(first,second,w0,w1,w2,coveragePosition,coverageDx,coverageDy,position,uvDx,uvDy,color);
#endif // _LT_FAST_COLOR_REMAINDER
#else
    float4 tiling[12]={_LTTiling0,_LTTiling1,_LTTiling2,_LTTiling3,_LTTiling4,_LTTiling5,
        _LTTiling6,_LTTiling7,_LTTiling8,_LTTiling9,_LTTiling10,_LTTiling11};
    float4 tint[12]={_LTTint0,_LTTint1,_LTTint2,_LTTint3,_LTTint4,_LTTint5,
        _LTTint6,_LTTint7,_LTTint8,_LTTint9,_LTTint10,_LTTint11};
    float4 settings[12]={_LTSettings0,_LTSettings1,_LTSettings2,_LTSettings3,_LTSettings4,_LTSettings5,
        _LTSettings6,_LTSettings7,_LTSettings8,_LTSettings9,_LTSettings10,_LTSettings11};
    float4 flags[12]={_LTFlags0,_LTFlags1,_LTFlags2,_LTFlags3,_LTFlags4,_LTFlags5,
        _LTFlags6,_LTFlags7,_LTFlags8,_LTFlags9,_LTFlags10,_LTFlags11};
    float raw[12]={w0.x,w0.y,w0.z,w0.w,w1.x,w1.y,w1.z,w1.w,w2.x,w2.y,w2.z,w2.w};
    bool topTwo=_LTFastLayerParams.z>.5;
    float weights[12];float3 masks[12];
#if defined(_LT_FAST_ROADS)
    float2 mappedUV[12],mappedDx[12],mappedDy[12],mappedRight[12];
#endif
    float maxHeight=-1;
    // Post-height/general mode retains every candidate.
    [unroll] for(int i=0;i<12;i++)
    {
        float3 mask=float3(1,.5,settings[i].z);
        [branch] if(raw[i]>.00001)
        {
            float2 scale=max(tiling[i].xy,.001);
            float2 uv=position/scale+tiling[i].zw;
            float2 dx=uvDx/scale,dy=uvDy/scale;
#if defined(_LT_FAST_ROADS)
            float2 roadRight;
            LTRoadMappedUV(i,coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);
            mappedUV[i]=uv;mappedDx[i]=dx;mappedDy[i]=dy;mappedRight[i]=roadRight;
#endif
            [branch] if(settings[i].w>.5)
            {
#if defined(SHADER_STAGE_RAY_TRACING)
            mask=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(i),0).rgb;
#else
            mask=SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,uv,LTLayerSlice(i),dx,dy).rgb;
#endif
            }
        }
        // Cracks change AO/smoothness but never height. Apply them only to survivors,
        // before AO strength, exactly as in the full sampler.
        mask.y=saturate((mask.y-.5)*flags[i].z+.5+flags[i].w);
        masks[i]=mask;
        if(raw[i]>.00001)maxHeight=max(maxHeight,mask.y+raw[i]);
    }
    float total=0;
    [unroll] for(int j=0;j<12;j++)
    {
        float w=raw[j];
        if(_LTHeightBlend>.0001)
            w*=lerp(1,saturate((masks[j].y+w-maxHeight+.2)/.2),_LTHeightBlend);
        weights[j]=w;total+=w;
    }
    [unroll] for(int q=0;q<12;q++)weights[q]/=max(total,.00001);

    // Pre-height selection lives in a separate compiled implementation.
    [branch] if(topTwo && total>.00001)
    {
        int firstSlot,secondSlot;LTStrongestLayerPair(weights,firstSlot,secondSlot);
        // Do not double-count a single winning slot.
        float kept=max(weights[firstSlot]+(secondSlot!=firstSlot?weights[secondSlot]:0),.00001);
        [unroll] for(int keep=0;keep<12;keep++)
            weights[keep]=(keep==firstSlot || keep==secondSlot)?weights[keep]/kept:0;
    }
    else [branch] if(!topTwo && _LTFastLayerParams.x>0 && total>.00001)
    {
        float first=0,second=0,third=0;
        [unroll] for(int rank=0;rank<12;rank++)
        {
            float w=weights[rank];
            third=max(third,min(second,w));
            second=max(second,min(first,w));first=max(first,w);
        }
        // Continuous soft threshold, not hard top-two selection. A fixed divisor
        // avoids discontinuities when the number of nonzero layers changes.
        // At most 'budget' total normalized weight is removed, before renormalizing.
        float cutoff=min(third,clamp(_LTFastLayerParams.x,0,.15)/12);
        float kept=0;
        [unroll] for(int r=0;r<12;r++){weights[r]=max(weights[r]-cutoff,0);kept+=weights[r];}
        [unroll] for(int s=0;s<12;s++)weights[s]/=max(kept,.00001);
    }
    color=0;float3 n=0;ao=0;smoothness=0;metallic=0;
    [unroll] for(int k=0;k<12;k++)
    {
        float w=weights[k];
        // Height-rejected/sparsified layers never fetch their albedo or normal.
        [branch] if(w>0)
        {
            float3 c=0;
            float3 mask=masks[k];
#if defined(_LT_FAST_ROADS)
            float2 roadRight=float2(1,0);
#endif
            // Match legacy defaults for tiny, unsampled coverage slots as well.
            float3 normal=k<8?float3(0,0,1):float3(0,0,0);
            [branch] if(raw[k]>.00001)
            {
                normal=float3(0,0,1);
                float2 scale=max(tiling[k].xy,.001);
                float2 uv=position/scale+tiling[k].zw;
                float2 dx=uvDx/scale,dy=uvDy/scale;
#if defined(_LT_FAST_ROADS)
                uv=mappedUV[k];dx=mappedDx[k];dy=mappedDy[k];roadRight=mappedRight[k];
#endif
#if defined(SHADER_STAGE_RAY_TRACING)
                c=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(k),0).rgb*tint[k].rgb;
                if(flags[k].x>.5)normal=LTUnpackLayerNormal(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(k),0),settings[k].x,flags[k].x);
#else
                c=SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,LTLayerSlice(k),dx,dy).rgb*tint[k].rgb;
                if(flags[k].x>.5)normal=LTUnpackLayerNormal(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,uv,LTLayerSlice(k),dx,dy),settings[k].x,flags[k].x);
#endif
#if defined(_LT_FAST_ROADS)
                LTApplyAsphaltCracks(k,coveragePosition,coverageDx,coverageDy,roadRight,c,normal,mask);
#endif
            }
#if defined(_LT_FAST_ROADS)
            if(_LTRoadProjectionEnabled>.5)normal=LTRoadLayerGradient(k,normal,roadRight,projectionAxis);
#endif
            color+=c*w;n+=normal*w;ao+=saturate(1+(mask.x-1)*flags[k].y)*w;
            smoothness+=mask.z*w;metallic+=settings[k].y*w;
        }
    }
#if defined(_LT_FAST_ROADS)
    if(_LTRoadProjectionEnabled>.5)
    {
        gradientLocal=n;normalTS=normalize(float3(-n.x,-n.z,1));return;
    }
#endif
    normalTS=normalize(n+float3(0,0,.00001));
    gradientLocal=LTProjectionGradient(normalTS,projectionAxis);
#endif // _LT_FAST_PREHEIGHT
}
