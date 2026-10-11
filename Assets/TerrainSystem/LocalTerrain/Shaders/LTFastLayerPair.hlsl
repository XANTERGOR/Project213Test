// Pre-height specialization: rank coverage once, then keep only two layer records.
// No 12-slot texture/mask/normal scratch arrays. Coverage is shared by all axes.
struct LTLayerPairData
{
    int slot;
    float slice,raw;
    float4 tiling,tint,settings,flags;
};
struct LTLayerPairSample
{
    float3 mask;
    float2 uv,dx,dy,right;
};
void LTChooseLayerPair(float4 w0,float4 w1,float4 w2,out int2 slots,out float2 raw)
{
    slots=0;raw=0;float2 slices=1e20;
    [unroll] for(int rank=0;rank<12;rank++)
    {
        float4 group=rank<4?w0:rank<8?w1:w2;
        float w=group[rank&3],slice=LTLayerSlice(rank);
        if(w>0 && (w>raw.x || (w==raw.x && slice<slices.x)))
        {
            slots.y=slots.x;raw.y=raw.x;slices.y=slices.x;
            slots.x=rank;raw.x=w;slices.x=slice;
        }
        else if(w>0 && (w>raw.y || (w==raw.y && slice<slices.y)))
        {slots.y=rank;raw.y=w;slices.y=slice;}
    }
    if(slots.x==slots.y)raw.y=0;
    // Preserve ascending-slot accumulation order from the original sampler.
    if(slots.x>slots.y){slots=slots.yx;raw=raw.yx;}
}
void LTLoadPairLayer(int slot,float raw,out LTLayerPairData layer)
{
    layer.slot=slot;layer.raw=raw;layer.slice=LTLayerSlice(slot);
    // Read uniforms for the selected slot only, without dynamically indexed
    // temporary arrays. The generated road adapter uses a direct buffer lookup.
    if(slot==0){layer.tiling=_LTTiling0;layer.tint=_LTTint0;layer.settings=_LTSettings0;layer.flags=_LTFlags0;}
    else if(slot==1){layer.tiling=_LTTiling1;layer.tint=_LTTint1;layer.settings=_LTSettings1;layer.flags=_LTFlags1;}
    else if(slot==2){layer.tiling=_LTTiling2;layer.tint=_LTTint2;layer.settings=_LTSettings2;layer.flags=_LTFlags2;}
    else if(slot==3){layer.tiling=_LTTiling3;layer.tint=_LTTint3;layer.settings=_LTSettings3;layer.flags=_LTFlags3;}
    else if(slot==4){layer.tiling=_LTTiling4;layer.tint=_LTTint4;layer.settings=_LTSettings4;layer.flags=_LTFlags4;}
    else if(slot==5){layer.tiling=_LTTiling5;layer.tint=_LTTint5;layer.settings=_LTSettings5;layer.flags=_LTFlags5;}
    else if(slot==6){layer.tiling=_LTTiling6;layer.tint=_LTTint6;layer.settings=_LTSettings6;layer.flags=_LTFlags6;}
    else if(slot==7){layer.tiling=_LTTiling7;layer.tint=_LTTint7;layer.settings=_LTSettings7;layer.flags=_LTFlags7;}
    else if(slot==8){layer.tiling=_LTTiling8;layer.tint=_LTTint8;layer.settings=_LTSettings8;layer.flags=_LTFlags8;}
    else if(slot==9){layer.tiling=_LTTiling9;layer.tint=_LTTint9;layer.settings=_LTSettings9;layer.flags=_LTFlags9;}
    else if(slot==10){layer.tiling=_LTTiling10;layer.tint=_LTTint10;layer.settings=_LTSettings10;layer.flags=_LTFlags10;}
    else {layer.tiling=_LTTiling11;layer.tint=_LTTint11;layer.settings=_LTSettings11;layer.flags=_LTFlags11;}
}
void LTPrepareLayerPair(float4 w0,float4 w1,float4 w2,out LTLayerPairData first,out LTLayerPairData second)
{
    int2 slots;float2 raw;LTChooseLayerPair(w0,w1,w2,slots,raw);
    LTLoadPairLayer(slots.x,raw.x,first);LTLoadPairLayer(slots.y,raw.y,second);
}
#if defined(_LT_FAST_COLOR_REMAINDER)
// Optional albedo-only remainder. PBR properties and gradients stay EXACTLY those
// of the height-blended pair. No remaining layer's mask/normal/cracks are sampled.
void LTBlendPairRemainderColor(LTLayerPairData first,LTLayerPairData second,float4 w0,float4 w1,float4 w2,
    float2 coveragePosition,float2 coverageDx,float2 coverageDy,float2 position,float2 uvDx,float2 uvDy,inout float3 color)
{
    // Also guards the generated road adapter, which selects this mode per tile.
    [branch] if(_LTFastLayerParams.w<1.5)return;
    float pairMass=first.raw+second.raw,remainderMass=0;
    float3 remainderColor=0;
    [loop] for(int slot=0;slot<12;slot++)
    {
        if(slot==first.slot||slot==second.slot)continue;
        float4 group=slot<4?w0:slot<8?w1:w2;
        float raw=group[slot&3];
        if(raw<=.00001)continue;
        LTLayerPairData layer;LTLoadPairLayer(slot,raw,layer);
        float2 scale=max(layer.tiling.xy,.001);
        float2 uv=position/scale+layer.tiling.zw,dx=uvDx/scale,dy=uvDy/scale;
#if defined(_LT_FAST_ROADS)
        float2 right;
        LTRoadMappedUV(slot,coveragePosition,coverageDx,coverageDy,uv,dx,dy,right);
#endif
#if defined(SHADER_STAGE_RAY_TRACING)
        float3 c=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,uv,layer.slice,0).rgb;
#else
        float3 c=SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,uv,layer.slice,dx,dy).rgb;
#endif
        remainderColor+=c*layer.tint.rgb*raw;remainderMass+=raw;
    }
    // Exact no-op for empty/one/two layers, including tiny-coverage cases.
    if(remainderMass>0)color=(color*pairMass+remainderColor)/max(pairMass+remainderMass,.00001);
}
#endif // _LT_FAST_COLOR_REMAINDER
void LTReadPairMask(LTLayerPairData layer,float2 coveragePosition,float2 coverageDx,float2 coverageDy,
    float2 position,float2 uvDx,float2 uvDy,out LTLayerPairSample sample)
{
    sample.mask=float3(1,.5,layer.settings.z);
    sample.uv=0;sample.dx=0;sample.dy=0;sample.right=float2(1,0);
    [branch] if(layer.raw>.00001)
    {
        float2 scale=max(layer.tiling.xy,.001);
        sample.uv=position/scale+layer.tiling.zw;sample.dx=uvDx/scale;sample.dy=uvDy/scale;
#if defined(_LT_FAST_ROADS)
        LTRoadMappedUV(layer.slot,coveragePosition,coverageDx,coverageDy,sample.uv,sample.dx,sample.dy,sample.right);
#endif
        [branch] if(layer.settings.w>.5)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            sample.mask=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,sample.uv,layer.slice,0).rgb;
#else
            sample.mask=SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTMaskArray,sampler_LTMaskArray,sample.uv,layer.slice,sample.dx,sample.dy).rgb;
#endif
        }
    }
    sample.mask.y=saturate((sample.mask.y-.5)*layer.flags.z+.5+layer.flags.w);
}
void LTAccumulatePairLayer(LTLayerPairData layer,LTLayerPairSample sample,float weight,
    float2 coveragePosition,float2 coverageDx,float2 coverageDy,int projectionAxis,
    inout float3 color,inout float3 normal,inout float ao,inout float smoothness,inout float metallic)
{
    [branch] if(weight<=0)return;
    float3 c=0,n=layer.slot<8?float3(0,0,1):float3(0,0,0),mask=sample.mask;
    [branch] if(layer.raw>.00001)
    {
        n=float3(0,0,1);
#if defined(SHADER_STAGE_RAY_TRACING)
        c=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTColorArray,sampler_LTColorArray,sample.uv,layer.slice,0).rgb*layer.tint.rgb;
        if(layer.flags.x>.5)n=LTUnpackLayerNormal(SAMPLE_TEXTURE2D_ARRAY_LOD(_LTNormalArray,sampler_LTNormalArray,sample.uv,layer.slice,0),layer.settings.x,layer.flags.x);
#else
        c=SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTColorArray,sampler_LTColorArray,sample.uv,layer.slice,sample.dx,sample.dy).rgb*layer.tint.rgb;
        if(layer.flags.x>.5)n=LTUnpackLayerNormal(SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTNormalArray,sampler_LTNormalArray,sample.uv,layer.slice,sample.dx,sample.dy),layer.settings.x,layer.flags.x);
#endif
#if defined(_LT_FAST_ROADS)
        LTApplyAsphaltCracks(layer.slot,coveragePosition,coverageDx,coverageDy,sample.right,c,n,mask);
#endif
    }
#if defined(_LT_FAST_ROADS)
    if(_LTRoadProjectionEnabled>.5)n=LTRoadLayerGradient(layer.slot,n,sample.right,projectionAxis);
#endif
    color+=c*weight;normal+=n*weight;
    ao+=saturate(1+(mask.x-1)*layer.flags.y)*weight;
    smoothness+=mask.z*weight;metallic+=layer.settings.y*weight;
}
void LTSampleLayerPair(LTLayerPairData first,LTLayerPairData second,
    float2 coveragePosition,float2 coverageDx,float2 coverageDy,float2 position,float2 uvDx,float2 uvDy,int projectionAxis,
    out float3 color,out float3 normalTS,out float ao,out float smoothness,out float metallic,out float3 gradientLocal)
{
    LTLayerPairSample a,b;
    LTReadPairMask(first,coveragePosition,coverageDx,coverageDy,position,uvDx,uvDy,a);
    LTReadPairMask(second,coveragePosition,coverageDx,coverageDy,position,uvDx,uvDy,b);
    float highest=-1;
    if(first.raw>.00001)highest=max(highest,a.mask.y+first.raw);
    if(second.raw>.00001)highest=max(highest,b.mask.y+second.raw);
    float2 weights=float2(first.raw,second.raw);
    if(_LTHeightBlend>.0001)
        weights*=lerp(1,saturate((float2(a.mask.y,b.mask.y)+weights-highest+.2)/.2),_LTHeightBlend);
    weights/=max(weights.x+weights.y,.00001);
    color=0;float3 n=0;ao=0;smoothness=0;metallic=0;
    LTAccumulatePairLayer(first,a,weights.x,coveragePosition,coverageDx,coverageDy,projectionAxis,color,n,ao,smoothness,metallic);
    LTAccumulatePairLayer(second,b,weights.y,coveragePosition,coverageDx,coverageDy,projectionAxis,color,n,ao,smoothness,metallic);
#if defined(_LT_FAST_ROADS)
    if(_LTRoadProjectionEnabled>.5)
    {gradientLocal=n;normalTS=normalize(float3(-n.x,-n.z,1));return;}
#endif
    normalTS=normalize(n+float3(0,0,.00001));gradientLocal=LTProjectionGradient(normalTS,projectionAxis);
}
