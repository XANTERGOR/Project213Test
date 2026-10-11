#ifndef LT_GLOBAL_NORMAL_ENCODING_INCLUDED
#define LT_GLOBAL_NORMAL_ENCODING_INCLUDED
// Global atlases store a positive-Z tangent-space perturbation. BC5 keeps XY;
// reconstruct Z before blending with full XYZ page normals. Flag 0 preserves
// old RGBA32 assets and transient bakes byte-for-byte in encoded space.
float3 LTGlobalNormalRGB(float3 packed,float bc5)
{
    if(bc5>.5)
    {
        float2 xy=packed.xy*2-1;
        packed.z=sqrt(saturate(1-dot(xy,xy)))*.5+.5;
    }
    return packed;
}
#endif
