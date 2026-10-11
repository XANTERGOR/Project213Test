Shader "Hidden/Local Terrain/Rock Atlas Padding"
{
    Properties { _MainTex("Color AO",2D)="black"{} _LTRockPadNormal("Normal surface",2D)="black"{} }
    SubShader
    {
        Cull Off ZTest Always ZWrite Off
        CGINCLUDE
        #pragma target 4.5
        #include "UnityCG.cginc"
        sampler2D _MainTex;sampler2D _LTRockPadNormal;
        float4 _MainTex_TexelSize;
        float2 Find(float2 uv)
        {
            if(tex2Dlod(_MainTex,float4(uv,0,0)).a>=0)return uv;
            [unroll] for(int y=-1;y<=1;y++)[unroll] for(int x=-1;x<=1;x++)
            {
                float2 p=uv+float2(x,y)*abs(_MainTex_TexelSize.xy);
                if(all(p>=0)&&all(p<=1)&&tex2Dlod(_MainTex,float4(p,0,0)).a>=0)return p;
            }
            return uv;
        }
        float4 ColorFrag(v2f_img i):SV_Target{return tex2Dlod(_MainTex,float4(Find(i.uv),0,0));}
        float4 NormalFrag(v2f_img i):SV_Target{return tex2Dlod(_LTRockPadNormal,float4(Find(i.uv),0,0));}
        ENDCG
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment ColorFrag
        ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment NormalFrag
        ENDCG }
    }
}
