Shader "Hidden/Local Terrain/Road Crack Pack"
{
    Properties { _MainTex("Cracks (white)",2D)="black" {} _HasSource("Has source",Float)=0 }
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
            sampler2D _MainTex;float _HasSource;
            float2 Hash(float2 cell)
            {
                cell=cell-floor(cell/6)*6;
                return frac(sin(float2(dot(cell,float2(127.1,311.7)),dot(cell,float2(269.5,183.3))))*43758.5453);
            }
            float Crack(float2 uv)
            {
                if(_HasSource>.5)return tex2Dlod(_MainTex,float4(frac(uv),0,0)).r;
                float2 p=uv*6,cell=floor(p);float a=100,b=100;
                [unroll] for(int y=-1;y<=1;y++)[unroll] for(int x=-1;x<=1;x++)
                {
                    float2 q=cell+float2(x,y),delta=q+.2+.6*Hash(q)-p;
                    float d=length(delta);if(d<a){b=a;a=d;}else b=min(b,d);
                }
                return 1-smoothstep(.025,.075,b-a);
            }
            float4 Pack(v2f_img input):SV_Target
            {
                float c=Crack(input.uv),u=Crack(input.uv+float2(1.0/256,0)),v=Crack(input.uv+float2(0,1.0/256));
                // R=coverage, GB=finite-difference normal perturbation, linear data.
                return float4(c,saturate(.5+(u-c)*.5),saturate(.5+(v-c)*.5),1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
