Shader "Sandstorm/Cheap Ground Shadow"
{
 SubShader
 {
 Tags {"RenderPipeline"="HDRenderPipeline"}
 Pass
 {
 Name "Ground Shadow"
 ZWrite Off ZTest Always Cull Off
 ColorMask RGB
 Blend DstColor Zero
 HLSLPROGRAM
 #pragma target 4.5
 #pragma vertex Vert
 #pragma fragment Frag
 #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
 TEXTURE2D(_CheapShadowMask);
 SAMPLER(sampler_LinearClamp);
 float4 _CheapShadowRect, _CheapShadowColor;
 float4 _ShadowFalloffArea, _ShadowFalloffSettings;
 float _CheapShadowGround, _CheapShadowHeightRange, _CheapShadowStrength;
 float4 Frag(Varyings input):SV_Target
 {
  UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
  float depth=LoadCameraDepth(input.positionCS.xy);
  if(depth==UNITY_RAW_FAR_CLIP_VALUE) return 1;
  PositionInputs p=GetPositionInput(input.positionCS.xy,_ScreenSize.zw,depth,UNITY_MATRIX_I_VP,UNITY_MATRIX_V);
  float3 world=GetAbsolutePositionWS(p.positionWS);
  float h=abs(world.y-_CheapShadowGround)/max(_CheapShadowHeightRange,.001);
  if(h>=1) return 1;
  float2 uv=(world.xz-_CheapShadowRect.xy)/_CheapShadowRect.zw;
  if(any(uv<0) || any(uv>1)) return 1;
  float mask=SAMPLE_TEXTURE2D_LOD(_CheapShadowMask,sampler_LinearClamp,uv,0).r;
  float fade=1-smoothstep(.5,1,h);
  [branch] if(_ShadowFalloffSettings.x>0.5)
  {
   float radius=length(world.xz-_ShadowFalloffArea.xy);
   float t=smoothstep(_ShadowFalloffArea.z,_ShadowFalloffArea.w,radius);
   t=pow(t,_ShadowFalloffSettings.w);
   fade*=lerp(_ShadowFalloffSettings.y,_ShadowFalloffSettings.z,t);
  }
  return float4(lerp(1,saturate(_CheapShadowColor.rgb),saturate(mask*_CheapShadowStrength*fade)),1);
 }
 ENDHLSL
 }
 }
}
