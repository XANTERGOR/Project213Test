Shader "Hidden/LocalTerrain/HoleStampPreview"
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
            float _Opacity;
            struct Input { float4 vertex : POSITION; float4 color : COLOR; };
            struct Varying { float4 position : SV_POSITION; float cut : TEXCOORD0; };
            Varying Vert(Input i){Varying o;o.position=UnityObjectToClipPos(i.vertex);o.cut=i.color.r;return o;}
            float4 Frag(Varying i) : SV_Target
            {
                clip(i.cut-.5);
                float rim=1-saturate((i.cut-.5)/max(.015,2*fwidth(i.cut)));
                return float4(lerp(float3(1,.1,.035),float3(1,.9,.05),rim),_Opacity);
            }
            ENDCG
        }
    }
    Fallback Off
}
