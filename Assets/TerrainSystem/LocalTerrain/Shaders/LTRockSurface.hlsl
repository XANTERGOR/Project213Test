#if defined(_LT_ROCK_SURFACE)
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRockMaterialSampling.hlsl"
TEXTURE2D(_LTRockFarColor);
TEXTURE2D(_LTRockFarNormal);
void LTApplyRockSurface(FragInputs input,inout SurfaceData surfaceData,out float3 normalTS,out float3 sampledNormalTS)
{
    float3 baseN=normalize(input.tangentToWorld[2]);
    float3 t=normalize(input.tangentToWorld[0]-baseN*dot(baseN,input.tangentToWorld[0]));
    float3 b=normalize(input.tangentToWorld[1]);
    float handedness=dot(cross(baseN,t),b)>=0?1:-1;
    float fade=_LTRockFarParams.x>.5?smoothstep(_LTRockFarParams.y,_LTRockFarParams.z,
        length(GetCurrentViewPosition()-input.positionRWS)):0;
    float3 color=0,n=float3(0,0,1);float ao=1,smoothness=0,metallic=0;
    [branch] if(fade<1)
    {
        float3 p=mul(_LTSurfaceWorldToLocal,float4(GetAbsolutePositionWS(input.positionRWS),1)).xyz;
        LTRockNear(p,baseN,t,handedness,input.texCoord0.xy,input.color.r,color,n,ao,smoothness,metallic);
    }
    [branch] if(fade>0)
    {
        // Bounded mip footprint fits the padded UV charts. Never use the terrain XZ atlas.
        float4 c=SAMPLE_TEXTURE2D_LOD(_LTRockFarColor,sampler_LinearClamp,input.texCoord2.xy,1);
        float4 s=SAMPLE_TEXTURE2D_LOD(_LTRockFarNormal,sampler_LinearClamp,input.texCoord2.xy,1);
        color=lerp(color,c.rgb,fade);n=normalize(lerp(n,LTRockOctDecode(s.rg),fade));
        ao=lerp(ao,c.a,fade);smoothness=lerp(smoothness,s.b,fade);metallic=lerp(metallic,s.a,fade);
    }
    sampledNormalTS=n;surfaceData.baseColor=color;surfaceData.ambientOcclusion=saturate(ao);
    surfaceData.perceptualSmoothness=saturate(smoothness);surfaceData.metallic=saturate(metallic);
#ifdef SURFACE_GRADIENT
    normalTS=SurfaceGradientFromTangentSpaceNormalAndFromTBN(n,t,b);
#else
    normalTS=n;
#endif
}
#endif
