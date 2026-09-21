// Shared by live shading and the geometry-aware far-atlas baker.
// Texture normal gradients only: never reconstruct normals from displacement.
void LTSampleProjectedLayers(float3 p,float3 normalWS,bool triplanar,
    out float3 color,out float3 gradientWS,out float ao,out float smoothness,out float metallic)
{
    float3 dx=0,dy=0;
#if !defined(SHADER_STAGE_RAY_TRACING)
    dx=ddx(p);dy=ddy(p);
#endif
    float3 n=normalize(mul((float3x3)_LTSurfaceWorldToLocal,normalWS));
    float3 weights=triplanar?pow(abs(n),4):float3(0,1,0);
    weights/=max(dot(weights,float3(1,1,1)),.00001);
    color=0;gradientWS=0;ao=0;smoothness=0;metallic=0;
    float3 gradient=0;
    [unroll] for(int axis=0;axis<3;axis++)
    {
        if(weights[axis]<=.00001)continue;
        float2 uv=axis==0?p.zy:axis==1?p.xz:p.xy;
        float2 ux=axis==0?dx.zy:axis==1?dx.xz:dx.xy;
        float2 uy=axis==0?dy.zy:axis==1?dy.xz:dy.xy;
        float3 c,tn;float a,s,m;
        LTSampleLayersMapped(p.xz,uv,ux,uy,c,tn,a,s,m);
        float2 slope=-tn.xy/max(tn.z,.0001);
        float3 g=axis==0?float3(0,slope.y,slope.x):axis==1?float3(slope.x,0,slope.y):float3(slope.x,slope.y,0);
        color+=weights[axis]*c;gradient+=weights[axis]*g;
        ao+=weights[axis]*a;smoothness+=weights[axis]*s;metallic+=weights[axis]*m;
    }
    gradientWS=mul(gradient,(float3x3)_LTSurfaceWorldToLocal);
    gradientWS-=normalWS*dot(normalWS,gradientWS);
}
