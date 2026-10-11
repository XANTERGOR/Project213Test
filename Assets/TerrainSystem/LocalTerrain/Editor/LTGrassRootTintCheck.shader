Shader "Hidden/LocalTerrain/GrassRootTintCheck"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"
            #include "../Shaders/LTGrassRootTint.hlsl"
            #define LT_GRASS_VARIATION_MATH_ONLY
            #include "../Shaders/LTGrassVariation.hlsl"
            float4 _TestBaseColor, _TestTint;
            float _TestStrength, _TestBase, _TestHeight, _TestRestHeight;
            float4 _TestRoot, _TestTintA, _TestTintB;
            float _TestVariation, _TestClump, _TestPatch, _TestSize, _TestSeed;
            float4 frag(v2f_img input) : SV_Target
            {
                float3 color;
                float3 baseColor = _TestBaseColor.rgb;
                if (_TestVariation > 0.5)
                {
                    float3 multiplier;
                    LTGrassVariation_float(_TestRoot.xyz, _TestTintA.rgb, _TestTintB.rgb,
                        _TestClump, _TestPatch, _TestSize, _TestSeed, multiplier);
                    LTGrassVariationApply_float(baseColor, multiplier, baseColor);
                }
                LTGrassRootTint_float(baseColor, _TestRestHeight, _TestTint.rgb,
                    _TestStrength, _TestBase, _TestHeight, color);
                return float4(color, _TestBaseColor.a);
            }
            ENDHLSL
        }
    }
}
