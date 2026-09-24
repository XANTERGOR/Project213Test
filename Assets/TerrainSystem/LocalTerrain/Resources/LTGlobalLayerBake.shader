Shader "Hidden/Local Terrain/Global Layer Bake"
{
    Properties
    {
_LTGlobalAlbedo("Global Albedo", 2D) = "white" {}
_LTGlobalNormal("Global Normal", 2D) = "bump" {}
[HideInInspector] _LTGlobalParams("Global params", Vector) = (0,150,300,0)
[HideInInspector] _LTFarSurface("Far surface", Vector) = (.3,0,0,0)
[HideInInspector] _LTRect("Chunk rectangle", Vector) = (0,0,1,1)
[HideInInspector] _LTWorldSize("World size", Vector) = (1,1,0,0)
[HideInInspector] _LTHeightBlend("Height blend", Float) = 0
[HideInInspector] _LTTriplanar("Triplanar", Float) = 0
 [HideInInspector] _LTBaseOnly("Background only", Float) = 0
[HideInInspector] _LTRoadProjectionEnabled("Spline projection", Float) = 0
[HideInInspector] _LTRoadProjectionSlots0("Spline slots 0-3", Vector) = (0,0,0,0)
[HideInInspector] _LTRoadProjectionSlots1("Spline slots 4-7", Vector) = (0,0,0,0)
[HideInInspector] _LTRoadProjectionSlots2("Spline slots 8-11", Vector) = (0,0,0,0)
[HideInInspector] _LTRoadProjectionMap("Spline coordinates", 2DArray) = "" {}
[HideInInspector] _LTRoadSuppressionEnabled("Asphalt suppression", Float) = 0
[HideInInspector] _LTRoadSuppressionMap("Asphalt coverage", 2D) = "black" {}
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
    }
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always
        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
        CBUFFER_START(UnityPerMaterial)
float4 _LTLayerSlices0, _LTLayerSlices1, _LTLayerSlices2;
        float4 _LTRect;
float4 _LTWorldSize;
float _LTHeightBlend;
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
        CBUFFER_END
        TEXTURE2D(_LTProbe);
        #include "Assets/TerrainSystem/LocalTerrain/Shaders/LTLayerBlendCore.hlsl"
        #include "Assets/TerrainSystem/LocalTerrain/Shaders/LTProjectedLayers.hlsl"
        struct Varyings {float4 positionCS:SV_POSITION;float2 uv:TEXCOORD0;};
        Varyings Vert(uint id:SV_VertexID)
        {
            const float2 corners[6]={float2(0,0),float2(0,1),float2(1,1),float2(0,0),float2(1,1),float2(1,0)};
            float2 uv=_LTBakeRect.xy+corners[id]*_LTBakeRect.zw;
            Varyings o;
            o.positionCS=float4(uv*2-1,UNITY_NEAR_CLIP_VALUE,1);
#if UNITY_UV_STARTS_AT_TOP
            o.positionCS.y=-o.positionCS.y;
#endif
            o.uv=uv;return o;
        }
        float4 Bake(Varyings input,bool normal)
        {
            float2 globalUV=input.uv;
            float3 color,n;float ao,smoothness,metallic;
            float2 position=globalUV*_LTWorldSize.xy;
            LTSampleLayers(position,ddx(position),ddy(position),color,n,ao,smoothness,metallic);
            return float4(normal?n*.5+.5:color,1);
        }
        float4 ColorFrag(Varyings input):SV_Target {return Bake(input,false);}
        float4 NormalFrag(Varyings input):SV_Target {return Bake(input,true);}
        struct MeshInput {float3 position:POSITION;float3 normal:NORMAL;float4 tangent:TANGENT;};
        struct MeshVaryings
        {
            float4 positionCS:SV_POSITION;
            float3 terrainPosition:TEXCOORD0;
            float3 normalWS:TEXCOORD1;
            float4 tangentWS:TEXCOORD2;
        };
        MeshVaryings MeshVert(MeshInput input)
        {
            MeshVaryings o;
            o.terrainPosition=mul(_LTBakeLocalToTerrain,float4(input.position,1)).xyz;
            float2 uv=o.terrainPosition.xz/_LTWorldSize.xy;
            o.positionCS=float4(uv*2-1,UNITY_NEAR_CLIP_VALUE,1);
#if UNITY_UV_STARTS_AT_TOP
            o.positionCS.y=-o.positionCS.y;
#endif
            o.normalWS=normalize(mul((float3x3)_LTBakeNormalToWorld,input.normal));
            o.tangentWS=float4(normalize(mul((float3x3)_LTBakeLocalToWorld,input.tangent.xyz)),input.tangent.w);
            return o;
        }
        float4 BakeMesh(MeshVaryings input,bool normal)
        {
            float3 baseN=normalize(input.normalWS);
            float3 color,g;float ao,smoothness,metallic;
            LTSampleProjectedLayers(input.terrainPosition,baseN,true,color,g,ao,smoothness,metallic);
            // Store perturbation in the terrain's ORIGINAL tangent frame. The
            // far shader applies it to its current frame, never to displaced faces.
            float3 t=normalize(input.tangentWS.xyz-baseN*dot(baseN,input.tangentWS.xyz));
            float3 b=cross(baseN,t)*(input.tangentWS.w>0?1:-1);
            float3 tn=normalize(float3(-dot(g,t),-dot(g,b),1));
            return float4(normal?tn*.5+.5:color,1);
        }
        float4 MeshColorFrag(MeshVaryings input):SV_Target {return BakeMesh(input,false);}
        float4 MeshNormalFrag(MeshVaryings input):SV_Target {return BakeMesh(input,true);}
        float4 ProbeFrag(Varyings input):SV_Target {return SAMPLE_TEXTURE2D_LOD(_LTProbe,sampler_PointClamp,_LTProbeUV.xy,0);}
        ENDHLSL
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment ColorFrag
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment NormalFrag
            ENDHLSL
        }
        // Editor verification samples the atlas through the GPU, avoiding readback Y conventions.
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment ProbeFrag
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex MeshVert
            #pragma fragment MeshColorFrag
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex MeshVert
            #pragma fragment MeshNormalFrag
            ENDHLSL
        }
    }
}
