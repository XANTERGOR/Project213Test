#ifndef SANDSTORM_DENSITY_INCLUDED
#define SANDSTORM_DENSITY_INCLUDED
TEXTURE3D(_DensityTex);
SAMPLER(sampler_DensityTex);
TEXTURE3D(_DensityTexB);
SAMPLER(sampler_DensityTexB);
float _FrameBlend;
TEXTURE3D(_StormEdgeNoiseTex); SAMPLER(sampler_StormEdgeNoiseTex);
TEXTURE3D(_StormInteriorNoiseTex); SAMPLER(sampler_StormInteriorNoiseTex);
float _StormBakedNoise;

float4 _AxisOptions, _DensityParams;
float4 _StormContrast;
float4 _EdgeShape, _EdgeDetail, _EdgeOffset;
float4 _InteriorShape, _InteriorDetail, _InteriorOffset;

float StormHash(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}
float StormValue(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f*f*(3-2*f);
    float a = lerp(StormHash(i), StormHash(i+float3(1,0,0)), f.x);
    float b = lerp(StormHash(i+float3(0,1,0)), StormHash(i+float3(1,1,0)), f.x);
    float c = lerp(StormHash(i+float3(0,0,1)), StormHash(i+float3(1,0,1)), f.x);
    float d = lerp(StormHash(i+float3(0,1,1)), StormHash(i+float3(1,1,1)), f.x);
    return lerp(lerp(a,b,f.y),lerp(c,d,f.y),f.z);
}
float StormCellular(float3 p)
{
    float3 cell = floor(p), f = frac(p);
    float nearest = 10;
    [loop] for (int z=-1; z<=1; z++)
    [loop] for (int y=-1; y<=1; y++)
    [loop] for (int x=-1; x<=1; x++)
    {
        float3 o = float3(x,y,z), q = cell+o;
        // Jitter restricted to the middle of each cell for a stable local neighbourhood.
        float3 feature = 0.25 + 0.5*float3(StormHash(q),StormHash(q+19.17),StormHash(q+47.53));
        float3 delta = o + feature - f;
        nearest = min(nearest,dot(delta,delta));
    }
    return saturate(1-sqrt(nearest));
}
float StormNoise(float3 p, int kind, int octaves)
{
    [branch] if (kind == 3) return StormCellular(p);
    [branch] if (kind == 0) return StormValue(p);
    float sum=0, weight=0, amplitude=1;
    [loop] for (int i=0; i<octaves; i++)
    {
        float n=StormValue(p);
        if (kind == 2) n=1-abs(2*n-1);
        sum+=n*amplitude; weight+=amplitude;
        p=p*2.03+float3(17.1,9.2,3.7); amplitude*=0.5;
    }
    return sum/max(weight,0.0001);
}
float StormLayer(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 q=p*shape.z+offset.xyz+detail.z*float3(0.173,0.317,0.619);
    [branch] if (_StormBakedNoise > 0.5)
    {
        // The layer's shape is passed unchanged by every caller.
        if (all(shape == _EdgeShape) && all(detail == _EdgeDetail) && all(offset == _EdgeOffset))
            return SAMPLE_TEXTURE3D_LOD(_StormEdgeNoiseTex,sampler_StormEdgeNoiseTex,q/8.0,0).r;
        return SAMPLE_TEXTURE3D_LOD(_StormInteriorNoiseTex,sampler_StormInteriorNoiseTex,q/8.0,0).r;
    }
    return StormNoise(q,(int)shape.y,(int)detail.y);
}
float3 StormWarp(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 n=float3(StormLayer(p,shape,detail,offset),
        StormLayer(p+float3(3.13,7.71,1.17),shape,detail,offset),
        StormLayer(p+float3(9.43,2.37,5.31),shape,detail,offset));
    return (2*n-1)*detail.x;
}
float StormReadDensity(float3 uvw)
{
    if (any(uvw<0) || any(uvw>1)) return 0;
    float d=SAMPLE_TEXTURE3D_LOD(_DensityTex,sampler_DensityTex,uvw,0).r;
    [branch] if (_FrameBlend>0)
        d=lerp(d,SAMPLE_TEXTURE3D_LOD(_DensityTexB,sampler_DensityTexB,uvw,0).r,_FrameBlend);
    return max(d,0);
}
float Density(float3 localPos)
{
    float3 boxUV=localPos+0.5;
    if (any(boxUV<0) || any(boxUV>1)) return 0;
    float3 edge=min(boxUV,1-boxUV);
    float fade=_DensityParams.w>0 ? saturate(min(edge.x,min(edge.y,edge.z))/_DensityParams.w) : 1;
    float3 uvw=boxUV;
    if (_AxisOptions.x>0.5) uvw=uvw.xzy;
    uvw=lerp(uvw,1-uvw,_AxisOptions.yzw);
    float raw=StormReadDensity(uvw);
    float band=max(_StormContrast.z,0.0001);
    // A low-density mask, not a geometric signed-distance surface.
    float edgeMask=1-smoothstep(0,band,raw);
    float3 warp=0;
    [branch] if (_EdgeShape.x>0.5 && _EdgeDetail.x>0)
        warp+=StormWarp(uvw,_EdgeShape,_EdgeDetail,_EdgeOffset)*edgeMask;
    [branch] if (_InteriorShape.x>0.5 && _InteriorDetail.x>0)
        warp+=StormWarp(uvw,_InteriorShape,_InteriorDetail,_InteriorOffset)*(1-edgeMask);
    [branch] if (any(warp!=0)) raw=StormReadDensity(uvw+warp);
    edgeMask=1-smoothstep(0,band,raw);
    [branch] if (_EdgeShape.x>0.5 && _EdgeShape.w>0)
        raw*=saturate(1-_EdgeShape.w*StormLayer(uvw,_EdgeShape,_EdgeDetail,_EdgeOffset)*edgeMask);
    [branch] if (_InteriorShape.x>0.5 && _InteriorShape.w>0)
        raw*=max(0,1+_InteriorShape.w*(2*StormLayer(uvw,_InteriorShape,_InteriorDetail,_InteriorOffset)-1)*(1-edgeMask));
    float value=max(raw-_DensityParams.y,0);
    [branch] if (value>0 && abs(_StormContrast.x-1)>0.00001)
    {
        float pivot=max(_StormContrast.y,0.0001);
        value=pivot*exp2(clamp(log2(max(value/pivot,1e-20))*_StormContrast.x,-80,80));
    }
    return value*_DensityParams.x*fade;
}
#endif
