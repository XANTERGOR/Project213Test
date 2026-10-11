// Shared by live shading and the geometry-aware far-atlas baker.
// Texture normal gradients only: never reconstruct normals from displacement.
void LTSampleProjectedLayersGrad(float3 p,float3 normalWS,bool triplanar,float3 dx,float3 dy,
    out float3 color,out float3 gradientWS,out float ao,out float smoothness,out float metallic)
{
    float3 n=normalize(mul((float3x3)_LTSurfaceWorldToLocal,normalWS));
    float3 weights=triplanar?pow(abs(n),4):float3(0,1,0);
    weights/=max(dot(weights,float3(1,1,1)),.00001);
#if defined(LT_FAST_LAYER_PATH)
    // Continuous, symmetric reduction avoids hard axis swaps/seams. Preserve all
    // three significant axes at corners; never force biplanar's triple-point pinch.
    weights=max(weights-clamp(_LTFastLayerParams.y,0,.15),0);
    weights/=max(dot(weights,float3(1,1,1)),.00001);
#endif // LT_FAST_LAYER_PATH
    color=0;gradientWS=0;ao=0;smoothness=0;metallic=0;
    float3 gradient=0;
    float4 w0,w1,w2;LTReadLayerCoverage(p.xz,w0,w1,w2);
#if defined(LT_FAST_PREHEIGHT_PATH)
    [branch] if(_LTBaseOnly<=.5)
    {
        LTLayerPairData first,second;LTPrepareLayerPair(w0,w1,w2,first,second);
        [unroll] for(int axis=0;axis<3;axis++)
        {
            if(weights[axis]<=.00001)continue;
            float2 uv=axis==0?p.zy:axis==1?p.xz:p.xy;
            float2 ux=axis==0?dx.zy:axis==1?dx.xz:dx.xy;
            float2 uy=axis==0?dy.zy:axis==1?dy.xz:dy.xy;
            float3 c,tn,g;float a,s,m;
            LTSampleLayerPair(first,second,p.xz,dx.xz,dy.xz,uv,ux,uy,axis,c,tn,a,s,m,g);
#if defined(_LT_FAST_COLOR_REMAINDER)
            LTBlendPairRemainderColor(first,second,w0,w1,w2,p.xz,dx.xz,dy.xz,uv,ux,uy,c);
#endif // _LT_FAST_COLOR_REMAINDER
            color+=weights[axis]*c;gradient+=weights[axis]*g;
            ao+=weights[axis]*a;smoothness+=weights[axis]*s;metallic+=weights[axis]*m;
        }
        gradientWS=mul(gradient,(float3x3)_LTSurfaceWorldToLocal);
        gradientWS-=normalWS*dot(normalWS,gradientWS);return;
    }
#endif // LT_FAST_PREHEIGHT_PATH
    [unroll] for(int axis=0;axis<3;axis++)
    {
        if(weights[axis]<=.00001)continue;
        float2 uv=axis==0?p.zy:axis==1?p.xz:p.xy;
        float2 ux=axis==0?dx.zy:axis==1?dx.xz:dx.xy;
        float2 uy=axis==0?dy.zy:axis==1?dy.xz:dy.xy;
        float3 c,tn,g;float a,s,m;
        // Roads always sample their XZ spline frame, including on side projections.
        // Rotate each road normal before blending it with the other layer gradients.
        LTSampleLayersMappedFrameWithCoverage(p.xz,dx.xz,dy.xz,uv,ux,uy,axis,w0,w1,w2,c,tn,a,s,m,g);
        color+=weights[axis]*c;gradient+=weights[axis]*g;
        ao+=weights[axis]*a;smoothness+=weights[axis]*s;metallic+=weights[axis]*m;
    }
    gradientWS=mul(gradient,(float3x3)_LTSurfaceWorldToLocal);
    gradientWS-=normalWS*dot(normalWS,gradientWS);
}
void LTSampleProjectedLayers(float3 p,float3 normalWS,bool triplanar,
    out float3 color,out float3 gradientWS,out float ao,out float smoothness,out float metallic)
{
    float3 dx=0,dy=0;
#if !defined(SHADER_STAGE_RAY_TRACING)
    dx=ddx(p);dy=ddy(p);
#endif
    LTSampleProjectedLayersGrad(p,normalWS,triplanar,dx,dy,color,gradientWS,ao,smoothness,metallic);
}
