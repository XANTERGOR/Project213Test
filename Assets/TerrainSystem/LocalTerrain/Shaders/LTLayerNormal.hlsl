#ifndef LT_LAYER_NORMAL_INCLUDED
#define LT_LAYER_NORMAL_INCLUDED
// Encoding travels in the existing per-layer normal-presence flag. No extra
// material uniforms or shader variants; roads copy the same flags verbatim.
float3 LTUnpackLayerNormal(float4 packedNormal,float strength,float normalFlag)
{
    if(normalFlag<1.5)return UnpackNormalRGB(packedNormal,strength);
    float2 xy=packedNormal.rg*2-1;
    float z=sqrt(max(1e-16,1-saturate(dot(xy,xy))));
    return float3(xy*strength,z);
}
#endif
