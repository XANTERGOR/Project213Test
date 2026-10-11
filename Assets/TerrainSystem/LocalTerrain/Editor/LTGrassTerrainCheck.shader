Shader "Hidden/LocalTerrain/GrassTerrainCheck"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            struct UnityTexture2D { Texture2D tex; SamplerState samplerstate; };
            #define SAMPLE_TEXTURE2D_LOD(tex,samp,uv,lod) tex.SampleLevel(samp,uv,lod)
            #include "../Shaders/LTGrassTerrain.hlsl"
            Texture2D _Map; SamplerState sampler_Map;
            float4 _Root, _U, _V, _Albedo;
            float _Ready, _Strength, _Height, _Base, _Rest, _ShowGround;
            struct Varyings {float4 position:SV_POSITION;float4 ground:TEXCOORD0;};
            Varyings Vert(appdata_img input)
            {
                Varyings o;o.position=UnityObjectToClipPos(input.vertex);
                UnityTexture2D map;map.tex=_Map;map.samplerstate=sampler_Map;
                LTGrassTerrainSample_float(_Root.xyz,map,_U,_V,_Ready,_Strength,o.ground);return o;
            }
            float4 Frag(Varyings input):SV_Target
            {
                if(_ShowGround>.5)return input.ground;
                float3 color;LTGrassTerrainApply_float(_Albedo.rgb,input.ground,_Rest,_Base,_Height,_Strength,color);
                return float4(color,_Albedo.a);
            }
            ENDHLSL
        }
    }
}
