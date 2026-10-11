Shader "Hidden/Local Terrain/Compressed Global Probe"
{
    Properties { _CheckMap("Map",2D)="white" {} }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Assets/TerrainSystem/LocalTerrain/Shaders/LTGlobalNormalEncoding.hlsl"
            TEXTURE2D(_CheckMap);SAMPLER(sampler_CheckMap);
            float4 _ProbeUV;
            float _NormalFlag;
            float4 Vert(uint id:SV_VertexID):SV_POSITION {return GetFullScreenTriangleVertexPosition(id);}
            float4 Frag():SV_Target
            {
                float4 value=SAMPLE_TEXTURE2D_LOD(_CheckMap,sampler_CheckMap,_ProbeUV.xy,_ProbeUV.z);
                if(_NormalFlag>.5)return float4(normalize(LTGlobalNormalRGB(value.xyz,_NormalFlag-1)*2-1+float3(0,0,.00001)),1);
                return value;
            }
            ENDHLSL
        }
    }
}
