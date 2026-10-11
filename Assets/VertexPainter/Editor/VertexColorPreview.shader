Shader "Hidden/Project213/VertexColorPreview"
{
    Properties { _Channel ("Channel", Float) = -1 }
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            float _Channel;
            struct Input { float4 position : POSITION; float4 color : COLOR; };
            struct Output { float4 position : SV_POSITION; float4 color : COLOR; };
            Output Vert(Input v)
            {
                Output o;
                o.position = UnityObjectToClipPos(v.position);
                o.color = v.color;
                return o;
            }
            float4 Frag(Output i) : SV_Target
            {
                if (_Channel < -0.5) return float4(i.color.rgb, 1);
                float value = _Channel < 0.5 ? i.color.r : _Channel < 1.5 ? i.color.g : _Channel < 2.5 ? i.color.b : i.color.a;
                return float4(value, value, value, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
