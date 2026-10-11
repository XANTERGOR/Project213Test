// Validation only: samples exactly the production mask and material functions.
Shader "Hidden/Local Terrain/Road Ground Probe"
{
    Properties
    {
        _LTRoadBlendDistances("Distances",2D)="white"{}
        _LTRoadBlendSource("Ground source",Float)=0
        [HideInInspector] _LTRoadTerrainReady("Road terrain ready", Float) = 0
        [HideInInspector] _LTRoadTerrainGrid("Road terrain chunk grid", Vector) = (0,0,0,0)
        [HideInInspector] _LTRoadTerrainCell("Road terrain chunk size", Vector) = (1,1,0,0)
        _LTRoadBlendColor("Ground color",2D)="white"{}
        _LTRoadBlendNormal("Ground normal",2D)="bump"{}
        _LTRoadBlendMask("Ground mask",2D)="white"{}
        _LTRoadBlendRect("Rect",Vector)=(0,0,1,1)
        _LTRoadBlendParams("Params",Vector)=(.6,.2,1,0)
        _LTRoadBlendNoise("Noise",Vector)=(1,0,0,0)
        _LTRoadBlendTiling("Tiling",Vector)=(4,4,0,0)
        _LTRoadBlendTint("Tint",Vector)=(1,1,1,1)
        _LTRoadBlendSettings("Settings",Vector)=(1,0,.4,0)
        _LTRoadBlendFlags("Flags",Vector)=(0,1,0,0)
        _LTRockColorMap("Asphalt color",2D)="white"{}
        _LTRockNormalMap("Asphalt normal",2D)="bump"{}
        _LTRockMaskMap("Asphalt mask",2D)="white"{}
        _LTRockTint("Asphalt tint",Vector)=(1,1,1,1)
        _LTRockUV("Asphalt UV",Vector)=(1,1,0,0)
        _LTRockSettings("Asphalt settings",Vector)=(1,.4,0,0)
        _LTRockFlags("Asphalt flags",Vector)=(1,0,0,0)
        _LTRockRemap("Remap",Vector)=(0,1,0,1)
        _LTProbePosition("Position",Vector)=(0,0,0,0)
        _LTProbeUV("Source UV",Vector)=(.5,.5,0,0)
        _LTProbeNormal("Surface normal",Vector)=(0,1,0,0)
        _LTGraphNormal("Graph input normal TS",Vector)=(0,0,1,0)
    }
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ _LT_ROAD_TERRAIN
            #pragma multi_compile_local _ _LT_GRAPH_PROBE
            #pragma vertex Vert
            #pragma fragment Frag
            #define _LT_ROAD_BLEND 1
            #define LT_ROAD_BLEND_PROBE 1
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"
            CBUFFER_START(UnityPerMaterial)
            #include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadGroundProperties.hlsl"
            #include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRockMaterialProperties.hlsl"
            float4x4 _LTSurfaceWorldToLocal;
            float4 _LTProbePosition,_LTProbeUV,_LTProbeNormal;
            float4 _LTGraphNormal;
            CBUFFER_END
            #if defined(_LT_GRAPH_PROBE)
            TEXTURE2D(_LTRoadBlendDistances);
            TEXTURE2D(_LTRoadBlendColor); SAMPLER(sampler_LTRoadBlendColor);
            TEXTURE2D(_LTRoadBlendNormal); SAMPLER(sampler_LTRoadBlendNormal);
            TEXTURE2D(_LTRoadBlendMask); SAMPLER(sampler_LTRoadBlendMask);
            #include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadGraphGround.hlsl"
            #else
            float3 LTProjectionGradient(float3 n,int axis)
            {float2 slope=-n.xy/max(n.z,.0001);return axis==0?float3(0,slope.y,slope.x):axis==1?float3(slope.x,0,slope.y):float3(slope.x,slope.y,0);}
            #include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadGroundSurface.hlsl"
            #endif
            float4 Vert(uint id:SV_VertexID):SV_POSITION {return GetFullScreenTriangleVertexPosition(id);}
            float4 Frag():SV_Target
            {
                float3 p=mul(_LTSurfaceWorldToLocal,float4(_LTProbePosition.xyz,1)).xyz;
                float w=LTRoadGroundWeight(_LTProbePosition.xyz,p),ao,smoothness,metallic;float3 color,n;
                #if defined(_LT_GRAPH_PROBE)
                LTRoadGraphGround_float(_LTRockTint.rgb,_LTGraphNormal.xyz,_LTRockSettings.y,1,_LTRockSettings.z,
                    _LTProbePosition.xyz,normalize(_LTProbeNormal.xyz),float3(1,0,0),-normalize(cross(_LTProbeNormal.xyz,float3(1,0,0))),color,n,smoothness,ao,metallic);
                #else
                LTRockNear(p,normalize(_LTProbeNormal.xyz),float3(1,0,0),-1,_LTProbeUV.xy,w,color,n,ao,smoothness,metallic);
                #endif
                if(_LTProbePosition.w>1.5)return float4(metallic,w,0,1);
                if(_LTProbePosition.w>.5)return float4(n,smoothness);
                return float4(color,ao);
            }
            ENDHLSL
        }
    }
}
