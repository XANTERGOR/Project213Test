// Hook for the owned derivative of the project's RoadShader. Original surface
// evaluation (two asphalt maps, masks, line colour and global noise) stays intact.
#define _LT_ROAD_BLEND 1
#define LT_ROAD_BLEND_AUTHORED 1
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
float3 LTProjectionGradient(float3 n,int axis)
{float2 slope=-n.xy/max(n.z,.0001);return axis==0?float3(0,slope.y,slope.x):axis==1?float3(slope.x,0,slope.y):float3(slope.x,slope.y,0);}
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadGroundSurface.hlsl"
void LTBlendAuthoredRoad(FragInputs input,inout GlobalSurfaceDescription surface)
{
    float3 absoluteWS=GetAbsolutePositionWS(input.positionRWS);
    float3 p=mul(_LTSurfaceWorldToLocal,float4(absoluteWS,1)).xyz;
    float3 dx=0,dy=0;
#if !defined(SHADER_STAGE_RAY_TRACING)
    dx=ddx(p);dy=ddy(p);
#endif
    float w=LTRoadGroundWeight(absoluteWS,p);
    [branch] if(w>0)
    {
        float3 baseN=normalize(input.tangentToWorld[2]);
        float3 t=normalize(input.tangentToWorld[0]-baseN*dot(baseN,input.tangentToWorld[0]));
        float3 b=normalize(input.tangentToWorld[1]);
        float3 color,gradient;float ao,smoothness,metallic;
        LTSampleRoadGround(p,baseN,dx,dy,color,gradient,ao,smoothness,metallic);
        float3 n=normalize(float3(-dot(gradient,t),-dot(gradient,b),1));
        surface.BaseColor=lerp(surface.BaseColor,color,w);
        surface.Normal=normalize(lerp(surface.Normal,n,w));
        surface.Smoothness=lerp(surface.Smoothness,smoothness,w);
        surface.Occlusion=lerp(surface.Occlusion,ao,w);
        surface.Metallic=lerp(surface.Metallic,metallic,w);
    }
}
