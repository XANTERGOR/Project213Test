#ifndef LT_ROAD_ASPHALT_INCLUDED
#define LT_ROAD_ASPHALT_INCLUDED
TEXTURE2D_ARRAY(_LTAsphaltCracks);
TEXTURE2D_ARRAY(_LTAsphaltDamage);
float4 LTAsphaltStyle(int slot)
{
    if(slot==0)return _LTAsphaltStyle0;
    if(slot==1)return _LTAsphaltStyle1;
    if(slot==2)return _LTAsphaltStyle2;
    if(slot==3)return _LTAsphaltStyle3;
    if(slot==4)return _LTAsphaltStyle4;
    if(slot==5)return _LTAsphaltStyle5;
    if(slot==6)return _LTAsphaltStyle6;
    if(slot==7)return _LTAsphaltStyle7;
    if(slot==8)return _LTAsphaltStyle8;
    if(slot==9)return _LTAsphaltStyle9;
    if(slot==10)return _LTAsphaltStyle10;
    return _LTAsphaltStyle11;
}
void LTApplyAsphaltCracks(int slot,float2 position,float2 dx,float2 dy,float2 right,
    inout float3 color,inout float3 normalTS,inout float3 mask)
{
    float4 style=LTAsphaltStyle(slot);
    [branch] if(style.x<.5)return;
    float2 coverage=((position-_LTRect.xy)/_LTRect.zw*256+.5)/257;
    float2 influence=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTAsphaltDamage,sampler_LinearClamp,coverage,style.x-1,0).rg;
    // G was unused. Broad repair patches reuse this fetch, asphalt slot and UVs;
    // they do not add a renderer, terrain layer or per-fragment noise loop.
    float repair=saturate(influence.g);
    color*=1-.32*repair;
    mask.z=lerp(mask.z,.28,repair*.65);
    float strength=influence.r*(1-.85*repair);
    [branch] if(strength<=.0001)return;
    // Isotropic crack pattern in terrain-local metres; the asphalt base remains spline-projected.
    float2 uv=position/max(style.z,.1);float4 detail;
#if defined(SHADER_STAGE_RAY_TRACING)
    detail=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTAsphaltCracks,sampler_LinearRepeat,uv,style.y,0);
#else
    detail=SAMPLE_TEXTURE2D_ARRAY_GRAD(_LTAsphaltCracks,sampler_LinearRepeat,uv,style.y,dx/max(style.z,.1),dy/max(style.z,.1));
#endif
    float amount=saturate(detail.r*strength);
    color*=1-.7*amount;
    mask.x*=1-.35*amount;mask.z=lerp(mask.z,.12,amount);
    float2 worldPerturbation=(detail.gb*2-1)*strength*.65;
    float2 forward=float2(-right.y,right.x);
    normalTS=normalize(float3(normalTS.xy+float2(dot(worldPerturbation,right),dot(worldPerturbation,forward)),normalTS.z));
}
#endif
