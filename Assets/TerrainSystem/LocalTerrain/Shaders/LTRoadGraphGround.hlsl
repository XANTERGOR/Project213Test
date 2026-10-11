#ifndef LT_ROAD_GRAPH_GROUND_INCLUDED
#define LT_ROAD_GRAPH_GROUND_INCLUDED
// Owned Shader Graph derivative. Blackboard properties generate UnityPerMaterial
// and texture declarations before this include; the original graph is untouched.
#define _LT_ROAD_BLEND 1
#define LT_ROAD_BLEND_AUTHORED 1
#define LT_ROAD_GRAPH_PROPERTIES 1
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
float3 LTProjectionGradient(float3 n,int axis)
{float2 slope=-n.xy/max(n.z,.0001);return axis==0?float3(0,slope.y,slope.x):axis==1?float3(slope.x,0,slope.y):float3(slope.x,slope.y,0);}
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadGroundSurface.hlsl"
void LTRoadGraphGround_float(float3 BaseColor,float3 NormalTS,float Smoothness,float Occlusion,float Metallic,
    float3 AbsoluteWS,float3 NormalWS,float3 TangentWS,float3 BitangentWS,
    out float3 OutBaseColor,out float3 OutNormalTS,out float OutSmoothness,out float OutOcclusion,out float OutMetallic)
{
    OutBaseColor=BaseColor;OutNormalTS=NormalTS;OutSmoothness=Smoothness;OutOcclusion=Occlusion;OutMetallic=Metallic;
#if !defined(SHADERGRAPH_PREVIEW)
    float3 p=mul(_LTSurfaceWorldToLocal,float4(AbsoluteWS,1)).xyz;
    float3 dx=0,dy=0;
#if !defined(SHADER_STAGE_RAY_TRACING)
    dx=ddx(p);dy=ddy(p);
#endif
    float w=LTRoadGroundWeight(AbsoluteWS,p);
    [branch] if(w>0)
    {
        float3 baseN=normalize(NormalWS);
        float3 t=normalize(TangentWS-baseN*dot(baseN,TangentWS));
        float3 b=normalize(BitangentWS);
        float3 color,gradient;float ao,smoothness,metallic;
        LTSampleRoadGround(p,baseN,dx,dy,color,gradient,ao,smoothness,metallic);
        float3 n=normalize(float3(-dot(gradient,t),-dot(gradient,b),1));
        OutBaseColor=lerp(BaseColor,color,w);
        OutNormalTS=normalize(lerp(NormalTS,n,w));
        OutSmoothness=lerp(Smoothness,smoothness,w);
        OutOcclusion=lerp(Occlusion,ao,w);
        OutMetallic=lerp(Metallic,metallic,w);
    }
#endif
}
#endif
