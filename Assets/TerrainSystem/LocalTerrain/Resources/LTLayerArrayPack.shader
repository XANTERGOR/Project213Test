Shader "Hidden/Local Terrain/Layer Array Pack"
{
    Properties { _MainTex("Source",2D)="white" {} _PackKind("Kind",Float)=0 _HasSource("Has source",Float)=1 }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment Pack
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _PackKind,_HasSource;
            float4 Pack(v2f_img input):SV_Target
            {
                if(_HasSource<.5)
                    return _PackKind<.5?float4(1,1,1,1):_PackKind<1.5?float4(.5,.5,1,1):float4(1,.5,.5,1);
                float4 value=tex2D(_MainTex,input.uv);
                // Decode the platform's imported normal encoding BEFORE putting all
                // normals into one canonical RGB array. Color uses sRGB target; data does not.
                if(_PackKind>.5&&_PackKind<1.5)value=float4(UnpackNormal(value)*.5+.5,1);
                return value;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
