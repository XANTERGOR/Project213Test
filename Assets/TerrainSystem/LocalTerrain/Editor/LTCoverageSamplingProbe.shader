Shader "Hidden/Local Terrain/Coverage Sampling Check"
{
Properties
    {
_LTGlobalAlbedo("Global Albedo", 2D) = "white" {}
_LTGlobalNormal("Global Normal", 2D) = "bump" {}
[HideInInspector] _LTGlobalParams("Global params", Vector) = (0,150,300,0)
[HideInInspector] _LTFarSurface("Far surface", Vector) = (.3,0,0,0)
[HideInInspector] _LTRect("Chunk rectangle", Vector) = (0,0,1,1)
[HideInInspector] _LTWorldSize("World size", Vector) = (1,1,0,0)
[HideInInspector] _LTGlobalRect("Global atlas extent", Vector) = (0,0,1,1)
[HideInInspector] _LTHeightBlend("Height blend", Float) = 0
[HideInInspector] _LTFastLayerParams("Fast tolerances", Vector) = (0,0,0,0)
[HideInInspector] _LTTriplanar("Triplanar", Float) = 0
 [HideInInspector] _LTBaseOnly("Background only", Float) = 0
[HideInInspector] _LTRoadProjectionEnabled("Spline projection", Float) = 0
[HideInInspector] _LTRoadProjectionSlots0("Spline slots 0-3", Vector) = (0,0,0,0)
[HideInInspector] _LTRoadProjectionSlots1("Spline slots 4-7", Vector) = (0,0,0,0)
[HideInInspector] _LTRoadProjectionSlots2("Spline slots 8-11", Vector) = (0,0,0,0)
[HideInInspector] _LTRoadProjectionMap("Spline coordinates", 2DArray) = "" {}
[HideInInspector] _LTRoadSuppressionEnabled("Asphalt suppression", Float) = 0
[HideInInspector] _LTRoadSuppressionMap("Asphalt coverage", 2D) = "black" {}
        [HideInInspector] _LTAsphaltCracks("Asphalt crack detail",2DArray)="" {}
        [HideInInspector] _LTAsphaltDamage("Asphalt crack influence",2DArray)="" {}
        [HideInInspector] _LTAsphaltStyle0("Asphalt style 0",Vector)=(0,0,0,0)
        [HideInInspector] _LTAsphaltStyle1("Asphalt style 1",Vector)=(0,0,0,0)
        [HideInInspector] _LTAsphaltStyle2("Asphalt style 2",Vector)=(0,0,0,0)
        [HideInInspector] _LTAsphaltStyle3("Asphalt style 3",Vector)=(0,0,0,0)
        [HideInInspector] _LTAsphaltStyle4("Asphalt style 4",Vector)=(0,0,0,0)
        [HideInInspector] _LTAsphaltStyle5("Asphalt style 5",Vector)=(0,0,0,0)
        [HideInInspector] _LTAsphaltStyle6("Asphalt style 6",Vector)=(0,0,0,0)
        [HideInInspector] _LTAsphaltStyle7("Asphalt style 7",Vector)=(0,0,0,0)
        [HideInInspector] _LTAsphaltStyle8("Asphalt style 8",Vector)=(0,0,0,0)
        [HideInInspector] _LTAsphaltStyle9("Asphalt style 9",Vector)=(0,0,0,0)
        [HideInInspector] _LTAsphaltStyle10("Asphalt style 10",Vector)=(0,0,0,0)
        [HideInInspector] _LTAsphaltStyle11("Asphalt style 11",Vector)=(0,0,0,0)
[HideInInspector] _LTTiling0("Tiling 0", Vector) = (0,0,0,0)
[HideInInspector] _LTTiling1("Tiling 1", Vector) = (0,0,0,0)
[HideInInspector] _LTTiling2("Tiling 2", Vector) = (0,0,0,0)
[HideInInspector] _LTTiling3("Tiling 3", Vector) = (0,0,0,0)
[HideInInspector] _LTTiling4("Tiling 4", Vector) = (0,0,0,0)
[HideInInspector] _LTTiling5("Tiling 5", Vector) = (0,0,0,0)
[HideInInspector] _LTTiling6("Tiling 6", Vector) = (0,0,0,0)
[HideInInspector] _LTTiling7("Tiling 7", Vector) = (0,0,0,0)
[HideInInspector] _LTTiling8("Tiling 8", Vector) = (0,0,0,0)
[HideInInspector] _LTTiling9("Tiling 9", Vector) = (0,0,0,0)
[HideInInspector] _LTTiling10("Tiling 10", Vector) = (0,0,0,0)
[HideInInspector] _LTTiling11("Tiling 11", Vector) = (0,0,0,0)
[HideInInspector] _LTTint0("Tint 0", Vector) = (0,0,0,0)
[HideInInspector] _LTTint1("Tint 1", Vector) = (0,0,0,0)
[HideInInspector] _LTTint2("Tint 2", Vector) = (0,0,0,0)
[HideInInspector] _LTTint3("Tint 3", Vector) = (0,0,0,0)
[HideInInspector] _LTTint4("Tint 4", Vector) = (0,0,0,0)
[HideInInspector] _LTTint5("Tint 5", Vector) = (0,0,0,0)
[HideInInspector] _LTTint6("Tint 6", Vector) = (0,0,0,0)
[HideInInspector] _LTTint7("Tint 7", Vector) = (0,0,0,0)
[HideInInspector] _LTTint8("Tint 8", Vector) = (0,0,0,0)
[HideInInspector] _LTTint9("Tint 9", Vector) = (0,0,0,0)
[HideInInspector] _LTTint10("Tint 10", Vector) = (0,0,0,0)
[HideInInspector] _LTTint11("Tint 11", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings0("Settings 0", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings1("Settings 1", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings2("Settings 2", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings3("Settings 3", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings4("Settings 4", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings5("Settings 5", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings6("Settings 6", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings7("Settings 7", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings8("Settings 8", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings9("Settings 9", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings10("Settings 10", Vector) = (0,0,0,0)
[HideInInspector] _LTSettings11("Settings 11", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags0("Flags 0", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags1("Flags 1", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags2("Flags 2", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags3("Flags 3", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags4("Flags 4", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags5("Flags 5", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags6("Flags 6", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags7("Flags 7", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags8("Flags 8", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags9("Flags 9", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags10("Flags 10", Vector) = (0,0,0,0)
[HideInInspector] _LTFlags11("Flags 11", Vector) = (0,0,0,0)
        _LTColorArray("Layer colors", 2DArray) = "" {}
        _LTNormalArray("Layer normals RGB", 2DArray) = "" {}
        _LTMaskArray("Layer masks", 2DArray) = "" {}
        _LTLayerSlices0("Slices 0-3", Vector) = (0,0,0,0)
        _LTLayerSlices1("Slices 4-7", Vector) = (0,0,0,0)
        _LTLayerSlices2("Slices 8-11", Vector) = (0,0,0,0)
_LTWeights0("Weights 0-3", 2D) = "black" {}
_LTWeights1("Weights 4-7", 2D) = "black" {}
_LTWeights2("Weights 8-11", 2D) = "black" {}
        _LTBakeRect("Bake rectangle", Vector) = (0,0,1,1)
        _LTProbe("Validation texture", 2D) = "white" {}
        _LTProbeUV("Validation UV", Vector) = (0,0,0,0)
        _LTProbeNormal("Validation surface normal", Vector) = (0,1,0,0)
        _CheckPoint("Validation position", Vector) = (0,0,0,0)
        _CheckDx("Validation derivative X", Vector) = (0,0,0,0)
        _CheckDy("Validation derivative Y", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always
        HLSLINCLUDE
        #pragma target 4.5
        #pragma multi_compile_local _ _LT_FAST_LAYERS
        #pragma multi_compile_local _ _LT_FAST_PREHEIGHT
        #pragma multi_compile_local _ _LT_FAST_COLOR_REMAINDER
        #pragma multi_compile_local _ _LT_FAST_ROADS
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
CBUFFER_START(UnityPerMaterial)
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadAsphaltProperties.hlsl"
float4 _LTLayerSlices0, _LTLayerSlices1, _LTLayerSlices2;
        float4 _LTRect;
float4 _LTWorldSize;
float4 _LTGlobalRect;
float _LTHeightBlend;
float4 _LTFastLayerParams;
float _LTBaseOnly;
float _LTRoadProjectionEnabled;
float4 _LTRoadProjectionSlots0, _LTRoadProjectionSlots1, _LTRoadProjectionSlots2;
float _LTRoadSuppressionEnabled;
float _LTTriplanar;
float4x4 _LTSurfaceWorldToLocal;
float4x4 _LTBakeLocalToTerrain;
float4x4 _LTBakeLocalToWorld;
float4x4 _LTBakeNormalToWorld;
float4 _LTTiling0;
float4 _LTTiling1;
float4 _LTTiling2;
float4 _LTTiling3;
float4 _LTTiling4;
float4 _LTTiling5;
float4 _LTTiling6;
float4 _LTTiling7;
float4 _LTTiling8;
float4 _LTTiling9;
float4 _LTTiling10;
float4 _LTTiling11;
float4 _LTTint0;
float4 _LTTint1;
float4 _LTTint2;
float4 _LTTint3;
float4 _LTTint4;
float4 _LTTint5;
float4 _LTTint6;
float4 _LTTint7;
float4 _LTTint8;
float4 _LTTint9;
float4 _LTTint10;
float4 _LTTint11;
float4 _LTSettings0;
float4 _LTSettings1;
float4 _LTSettings2;
float4 _LTSettings3;
float4 _LTSettings4;
float4 _LTSettings5;
float4 _LTSettings6;
float4 _LTSettings7;
float4 _LTSettings8;
float4 _LTSettings9;
float4 _LTSettings10;
float4 _LTSettings11;
float4 _LTFlags0;
float4 _LTFlags1;
float4 _LTFlags2;
float4 _LTFlags3;
float4 _LTFlags4;
float4 _LTFlags5;
float4 _LTFlags6;
float4 _LTFlags7;
float4 _LTFlags8;
float4 _LTFlags9;
float4 _LTFlags10;
float4 _LTFlags11;



        float4 _LTBakeRect;
        float4 _LTProbeUV;
        float4 _LTProbeNormal;
        float4 _CheckPoint, _CheckDx, _CheckDy;
        CBUFFER_END
        #include "Assets/TerrainSystem/LocalTerrain/Shaders/LTLayerBlendCore.hlsl"
        #include "Assets/TerrainSystem/LocalTerrain/Shaders/LTProjectedLayers.hlsl"
        #include "Assets/TerrainSystem/LocalTerrain/Editor/LTCoverageSamplingReference.hlsl"
        float4 Vert(uint id:SV_VertexID):SV_POSITION { return GetFullScreenTriangleVertexPosition(id); }
        float4 Result(float3 c,float3 g,float a,float s,float m)
        {
            if(_LTProbeUV.z>1.5)return float4(m,a,s,1);
            if(_LTProbeUV.z>.5)return float4(g,s);
            return float4(c,a);
        }
        float4 ReferenceFrag():SV_Target
        {
            float3 c,g;float a,s,m;
            LTReferenceProjectedLayers(_CheckPoint.xyz,normalize(_LTProbeNormal.xyz),_LTTriplanar>.5,
                _CheckDx.xyz,_CheckDy.xyz,c,g,a,s,m);
            return Result(c,g,a,s,m);
        }
        float4 OptimizedFrag():SV_Target
        {
            float3 c,g;float a,s,m;
            LTSampleProjectedLayersGrad(_CheckPoint.xyz,normalize(_LTProbeNormal.xyz),_LTTriplanar>.5,
                _CheckDx.xyz,_CheckDy.xyz,c,g,a,s,m);
            return Result(c,g,a,s,m);
        }
        ENDHLSL
        Pass
        {
            Name "Reference"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment ReferenceFrag
            ENDHLSL
        }
        Pass
        {
            Name "Optimized"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment OptimizedFrag
            ENDHLSL
        }
    }
}

