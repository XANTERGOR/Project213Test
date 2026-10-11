Shader "Hidden/Local Terrain/Compressed Array Probe"
{
    Properties { _CheckArray("Array",2DArray)="" {} }
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
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
            #include "Assets/TerrainSystem/LocalTerrain/Shaders/LTLayerNormal.hlsl"
            TEXTURE2D_ARRAY(_CheckArray);SAMPLER(sampler_CheckArray);
            float4 _ProbeUV;
            float _NormalFlag,_Strength;
            float4 Vert(uint id:SV_VertexID):SV_POSITION {return GetFullScreenTriangleVertexPosition(id);}
            float4 Frag():SV_Target
            {
                float4 value=SAMPLE_TEXTURE2D_ARRAY_LOD(_CheckArray,sampler_CheckArray,_ProbeUV.xy,_ProbeUV.z,_ProbeUV.w);
                if(_NormalFlag>.5)return float4(normalize(LTUnpackLayerNormal(value,_Strength,_NormalFlag)),1);
                return value;
            }
            ENDHLSL
        }
    }
}
