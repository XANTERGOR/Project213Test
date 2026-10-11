#if defined(_LT_ROAD_BLEND)
#if !defined(LT_ROAD_GRAPH_PROPERTIES)
TEXTURE2D(_LTRoadBlendDistances);
#endif
#if defined(_LT_ROAD_TERRAIN)
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadTerrainSampling.hlsl"
#else
#if !defined(LT_ROAD_GRAPH_PROPERTIES)
TEXTURE2D(_LTRoadBlendColor); SAMPLER(sampler_LTRoadBlendColor);
TEXTURE2D(_LTRoadBlendNormal); SAMPLER(sampler_LTRoadBlendNormal);
TEXTURE2D(_LTRoadBlendMask); SAMPLER(sampler_LTRoadBlendMask);
#endif
#endif

// Match terrain coordinates, tint, RGB mask convention and normal projection.
// Single-layer mode uses its own textures; the optional terrain variant reads
// shared layer arrays and copied paint masks. Neither adds a draw pass.
void LTSampleRoadGround(float3 p,float3 baseN,float3 dx,float3 dy,
    out float3 color,out float3 gradient,out float ao,out float smoothness,out float metallic)
{
    color=0;gradient=0;ao=1;smoothness=0;metallic=0;
#if defined(_LT_ROAD_TERRAIN)
    // The keyword selects the source at compile time. A runtime branch retained
    // three unused single-layer samplers and exceeded the authored HDRP budget.
    LTSampleRoadTerrain(p,baseN,dx,dy,color,gradient,ao,smoothness,metallic);
#else
    float3 localN=normalize(mul((float3x3)_LTSurfaceWorldToLocal,baseN));
    float3 weights=_LTRoadBlendFlags.z>.5?pow(abs(localN),4):float3(0,1,0);
    weights/=max(dot(weights,float3(1,1,1)),1e-5);
    color=0;gradient=0;ao=0;smoothness=0;metallic=_LTRoadBlendSettings.y;
    [unroll] for(int axis=0;axis<3;axis++)
    {
        if(weights[axis]<=.00001)continue;
        float2 pos=axis==0?p.zy:axis==1?p.xz:p.xy;
        float2 ux=(axis==0?dx.zy:axis==1?dx.xz:dx.xy)/_LTRoadBlendTiling.xy;
        float2 uy=(axis==0?dy.zy:axis==1?dy.xz:dy.xy)/_LTRoadBlendTiling.xy;
        float2 uv=pos/_LTRoadBlendTiling.xy+_LTRoadBlendTiling.zw;
        float3 c,mask=float3(1,.5,_LTRoadBlendSettings.z),n=float3(0,0,1);
#if defined(SHADER_STAGE_RAY_TRACING)
        c=SAMPLE_TEXTURE2D_LOD(_LTRoadBlendColor,sampler_LTRoadBlendColor,uv,0).rgb;
        if(_LTRoadBlendFlags.x>.5)n=UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_LTRoadBlendNormal,sampler_LTRoadBlendNormal,uv,0),_LTRoadBlendSettings.x);
        if(_LTRoadBlendSettings.w>.5)mask=SAMPLE_TEXTURE2D_LOD(_LTRoadBlendMask,sampler_LTRoadBlendMask,uv,0).rgb;
#else
        c=SAMPLE_TEXTURE2D_GRAD(_LTRoadBlendColor,sampler_LTRoadBlendColor,uv,ux,uy).rgb;
        if(_LTRoadBlendFlags.x>.5)n=UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_LTRoadBlendNormal,sampler_LTRoadBlendNormal,uv,ux,uy),_LTRoadBlendSettings.x);
        if(_LTRoadBlendSettings.w>.5)mask=SAMPLE_TEXTURE2D_GRAD(_LTRoadBlendMask,sampler_LTRoadBlendMask,uv,ux,uy).rgb;
#endif
        color+=weights[axis]*c*_LTRoadBlendTint.rgb;
        ao+=weights[axis]*saturate(1+(mask.r-1)*_LTRoadBlendFlags.y);
        smoothness+=weights[axis]*mask.b;
        gradient+=weights[axis]*LTProjectionGradient(n,axis);
    }
    gradient=mul(gradient,(float3x3)_LTSurfaceWorldToLocal);
    gradient-=baseN*dot(baseN,gradient);
#endif
}
#if !defined(LT_ROAD_BLEND_AUTHORED)
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRockMaterialSampling.hlsl"
#endif
float LTRoadBlendHash(float2 p)
{
    uint2 v=asuint(int2(p));uint h=v.x*1597334677u^v.y*3812015801u^(uint)_LTRoadBlendNoise.y;
    h^=h>>16;h*=2246822519u;h^=h>>13;
    return (h&0x00ffffffu)/16777215.0;
}
float LTRoadBlendNoiseValue(float2 p)
{
    float2 i=floor(p),f=frac(p);f=f*f*(3-2*f);
    return lerp(lerp(LTRoadBlendHash(i),LTRoadBlendHash(i+float2(1,0)),f.x),
        lerp(LTRoadBlendHash(i+float2(0,1)),LTRoadBlendHash(i+1),f.x),f.y);
}
float LTRoadGroundWeight(float3 absoluteWS,float3 groundPosition)
{
    float2 p=mul(_LTRoadBlendWorldToMask,float4(absoluteWS,1)).xz;
    float2 uv=(p-_LTRoadBlendRect.xy)/_LTRoadBlendRect.zw;
    // Rect describes endpoint sample positions, not outer texture texel edges.
    uv=(uv*(_LTRoadBlendDistances_TexelSize.zw-1)+.5)*_LTRoadBlendDistances_TexelSize.xy;
    float2 d=SAMPLE_TEXTURE2D_LOD(_LTRoadBlendDistances,sampler_LinearClamp,uv,0).rg;
    float noise=lerp(1,.25+.75*LTRoadBlendNoiseValue(groundPosition.xz/max(_LTRoadBlendNoise.x,.05)),_LTRoadBlendParams.w);
    float2 width=_LTRoadBlendParams.xy*noise;
    float2 w=(1-smoothstep(0,max(width,.00001),d))*step(.00001,width);
    float result=max(w.x,w.y)*_LTRoadBlendParams.z;
#if defined(_LT_ROAD_TERRAIN)
    [branch] if(result>0)
    {
        result=LTRoadTerrainTile(groundPosition.xz)>=0?result:0;
    }
#endif
    return result;
}
#if !defined(LT_ROAD_BLEND_PROBE) && !defined(LT_ROAD_BLEND_AUTHORED)
void LTApplyRoadGroundSurface(FragInputs input,inout SurfaceData surfaceData,out float3 normalTS,out float3 sampledNormalTS)
{
    float3 absoluteWS=GetAbsolutePositionWS(input.positionRWS);
    float3 p=mul(_LTSurfaceWorldToLocal,float4(absoluteWS,1)).xyz;
    float3 baseN=normalize(input.tangentToWorld[2]);
    float3 t=normalize(input.tangentToWorld[0]-baseN*dot(baseN,input.tangentToWorld[0]));
    float3 b=normalize(input.tangentToWorld[1]);
    float handedness=dot(cross(baseN,t),b)>=0?1:-1;
    float3 color,n;float ao,smoothness,metallic;
    LTRockNear(p,baseN,t,handedness,input.texCoord0.xy,LTRoadGroundWeight(absoluteWS,p),color,n,ao,smoothness,metallic);
    surfaceData.baseColor=color;surfaceData.ambientOcclusion=saturate(ao);
    surfaceData.perceptualSmoothness=saturate(smoothness);surfaceData.metallic=saturate(metallic);
    sampledNormalTS=n;
#ifdef SURFACE_GRADIENT
    normalTS=SurfaceGradientFromTangentSpaceNormalAndFromTBN(n,t,b);
#else
    normalTS=n;
#endif
}
#endif
#endif
