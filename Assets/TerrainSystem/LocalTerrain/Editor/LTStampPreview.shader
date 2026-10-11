Shader "Hidden/LocalTerrain/StampPreview"
{
    SubShader
    {
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            sampler2D _PreviewMask;
            float4x4 _MeshToStamp;
            float2 _StampSize;
            float _Shape, _Mode, _Opacity, _Edge, _Strength, _MaskIsHeight, _MaskDensity, _UniformDensity;
            struct Varyings { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vert(float4 vertex : POSITION)
            {
                Varyings o;
                o.position = UnityObjectToClipPos(vertex);
                o.uv = mul(_MeshToStamp, vertex).xz / _StampSize + .5;
                return o;
            }
            float3 Ramp(float v)
            {
                float3 cold = float3(.04, .25, 1), middle = float3(.08, 1, .55), hot = float3(1, .22, .035);
                return v < .5 ? lerp(cold, middle, v * 2) : lerp(middle, hot, v * 2 - 1);
            }
            float4 Frag(Varyings i) : SV_Target
            {
                float2 n = abs(i.uv * 2 - 1);
                float d = _Shape < .5 ? length(n) : max(n.x, n.y);
                clip(1 - d);
                float mask = tex2D(_PreviewMask, i.uv).r;
                float value;
                if (_Mode > 2.5)
                {
                    // Shape mode has a coarse outer 20%, independent of Edge Falloff.
                    value = _MaskDensity > .5 ? mask : 1 - smoothstep(0, .8, d);
                    if (_UniformDensity > .5) value = 1;
                }
                else if (_Mode > 1.5)
                {
                    float t = saturate((1 - d) / max(.001, _Edge));
                    value = saturate(t * t * (3 - 2 * t) * _Strength * (_MaskIsHeight > .5 ? 1 : mask));
                }
                else value = saturate(mask);
                // A heatmap (including zero), not a new terrain/material/height pass.
                float edgeAA = saturate((1 - d) / max(fwidth(d), .0001));
                return float4(Ramp(value), _Opacity * edgeAA);
            }
            ENDCG
        }
    }
    Fallback Off
}
