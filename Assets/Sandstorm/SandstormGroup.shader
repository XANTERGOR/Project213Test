Shader "Sandstorm/HDRP Group Raymarch"
{
 SubShader
 {
 Tags { "RenderPipeline"="HDRenderPipeline" }
 HLSLINCLUDE
 #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
 SAMPLER(sampler_LinearClamp);
 SAMPLER(sampler_LinearRepeat);
 int _VolumeCount;
            TEXTURE2D(_SandstormLowResColor);
            float4 _StormFullSize, _StormLowSize;
            float _StormHalfPass, _StormDepthTolerance, _StormRefineEdges;

            // The same integer depth coordinate is used in low-resolution rendering
            // and reconstruction. Snapping avoids pairing a ray with a neighbour's depth.
            float2 StormSourcePixel(int2 lowPixel)
            {
                return min(floor((float2(lowPixel) + 0.5) * _StormFullSize.xy / _StormLowSize.xy),
                    _StormFullSize.xy - 1) + 0.5;
            }

            bool IntersectBox(float3 o, float3 d, out float enter, out float leave)
            {
                enter = -1e20;
                leave = 1e20;
                [unroll] for (int axis = 0; axis < 3; axis++)
                {
                    if (abs(d[axis]) < 1e-8)
                    {
                        if (o[axis] < -0.5 || o[axis] > 0.5) return false;
                    }
                    else
                    {
                        float a = (-0.5 - o[axis]) / d[axis];
                        float b = ( 0.5 - o[axis]) / d[axis];
                        enter = max(enter, min(a, b));
                        leave = min(leave, max(a, b));
                    }
                }
                return leave > max(enter, 0.0);
            }





TEXTURE3D(_V0_DensityTex);

TEXTURE3D(_V0_DensityTexB);

float _V0_FrameBlend;
TEXTURE3D(_V0_StormEdgeNoiseTex); 
TEXTURE3D(_V0_StormInteriorNoiseTex); 
float _V0_StormBakedNoise;

float4 _V0_AxisOptions, _V0_DensityParams;
float4 _V0_StormContrast;
float4 _V0_EdgeShape, _V0_EdgeDetail, _V0_EdgeOffset;
float4 _V0_InteriorShape, _V0_InteriorDetail, _V0_InteriorOffset;

float StormHash0(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}
float StormValue0(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f*f*(3-2*f);
    float a = lerp(StormHash0(i), StormHash0(i+float3(1,0,0)), f.x);
    float b = lerp(StormHash0(i+float3(0,1,0)), StormHash0(i+float3(1,1,0)), f.x);
    float c = lerp(StormHash0(i+float3(0,0,1)), StormHash0(i+float3(1,0,1)), f.x);
    float d = lerp(StormHash0(i+float3(0,1,1)), StormHash0(i+float3(1,1,1)), f.x);
    return lerp(lerp(a,b,f.y),lerp(c,d,f.y),f.z);
}
float StormCellular0(float3 p)
{
    float3 cell = floor(p), f = frac(p);
    float nearest = 10;
    [loop] for (int z=-1; z<=1; z++)
    [loop] for (int y=-1; y<=1; y++)
    [loop] for (int x=-1; x<=1; x++)
    {
        float3 o = float3(x,y,z), q = cell+o;
        // Jitter restricted to the middle of each cell for a stable local neighbourhood.
        float3 feature = 0.25 + 0.5*float3(StormHash0(q),StormHash0(q+19.17),StormHash0(q+47.53));
        float3 delta = o + feature - f;
        nearest = min(nearest,dot(delta,delta));
    }
    return saturate(1-sqrt(nearest));
}
float StormNoise0(float3 p, int kind, int octaves)
{
    [branch] if (kind == 3) return StormCellular0(p);
    [branch] if (kind == 0) return StormValue0(p);
    float sum=0, weight=0, amplitude=1;
    [loop] for (int i=0; i<octaves; i++)
    {
        float n=StormValue0(p);
        if (kind == 2) n=1-abs(2*n-1);
        sum+=n*amplitude; weight+=amplitude;
        p=p*2.03+float3(17.1,9.2,3.7); amplitude*=0.5;
    }
    return sum/max(weight,0.0001);
}
float StormLayer0(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 q=p*shape.z+offset.xyz+detail.z*float3(0.173,0.317,0.619);
    [branch] if (_V0_StormBakedNoise > 0.5)
    {
        // The layer's shape is passed unchanged by every caller.
        if (all(shape == _V0_EdgeShape) && all(detail == _V0_EdgeDetail) && all(offset == _V0_EdgeOffset))
            return SAMPLE_TEXTURE3D_LOD(_V0_StormEdgeNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
        return SAMPLE_TEXTURE3D_LOD(_V0_StormInteriorNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
    }
    return StormNoise0(q,(int)shape.y,(int)detail.y);
}
float3 StormWarp0(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 n=float3(StormLayer0(p,shape,detail,offset),
        StormLayer0(p+float3(3.13,7.71,1.17),shape,detail,offset),
        StormLayer0(p+float3(9.43,2.37,5.31),shape,detail,offset));
    return (2*n-1)*detail.x;
}
float StormReadDensity0(float3 uvw)
{
    if (any(uvw<0) || any(uvw>1)) return 0;
    float d=SAMPLE_TEXTURE3D_LOD(_V0_DensityTex,sampler_LinearClamp,uvw,0).r;
    [branch] if (_V0_FrameBlend>0)
        d=lerp(d,SAMPLE_TEXTURE3D_LOD(_V0_DensityTexB,sampler_LinearClamp,uvw,0).r,_V0_FrameBlend);
    return max(d,0);
}
float Density0(float3 localPos)
{
    float3 boxUV=localPos+0.5;
    if (any(boxUV<0) || any(boxUV>1)) return 0;
    float3 edge=min(boxUV,1-boxUV);
    float fade=_V0_DensityParams.w>0 ? saturate(min(edge.x,min(edge.y,edge.z))/_V0_DensityParams.w) : 1;
    float3 uvw=boxUV;
    if (_V0_AxisOptions.x>0.5) uvw=uvw.xzy;
    uvw=lerp(uvw,1-uvw,_V0_AxisOptions.yzw);
    float raw=StormReadDensity0(uvw);
    float band=max(_V0_StormContrast.z,0.0001);
    // A low-density mask, not a geometric signed-distance surface.
    float edgeMask=1-smoothstep(0,band,raw);
    float3 warp=0;
    [branch] if (_V0_EdgeShape.x>0.5 && _V0_EdgeDetail.x>0)
        warp+=StormWarp0(uvw,_V0_EdgeShape,_V0_EdgeDetail,_V0_EdgeOffset)*edgeMask;
    [branch] if (_V0_InteriorShape.x>0.5 && _V0_InteriorDetail.x>0)
        warp+=StormWarp0(uvw,_V0_InteriorShape,_V0_InteriorDetail,_V0_InteriorOffset)*(1-edgeMask);
    [branch] if (any(warp!=0)) raw=StormReadDensity0(uvw+warp);
    edgeMask=1-smoothstep(0,band,raw);
    [branch] if (_V0_EdgeShape.x>0.5 && _V0_EdgeShape.w>0)
        raw*=saturate(1-_V0_EdgeShape.w*StormLayer0(uvw,_V0_EdgeShape,_V0_EdgeDetail,_V0_EdgeOffset)*edgeMask);
    [branch] if (_V0_InteriorShape.x>0.5 && _V0_InteriorShape.w>0)
        raw*=max(0,1+_V0_InteriorShape.w*(2*StormLayer0(uvw,_V0_InteriorShape,_V0_InteriorDetail,_V0_InteriorOffset)-1)*(1-edgeMask));
    float value=max(raw-_V0_DensityParams.y,0);
    [branch] if (value>0 && abs(_V0_StormContrast.x-1)>0.00001)
    {
        float pivot=max(_V0_StormContrast.y,0.0001);
        value=pivot*exp2(clamp(log2(max(value/pivot,1e-20))*_V0_StormContrast.x,-80,80));
    }
    return value*_V0_DensityParams.x*fade;
}


float4x4 _V0_VolumeWorldToLocal;
float4 _V0_MarchParams, _V0_AdaptiveMarch, _V0_SunDirection, _V0_SunRadiance, _V0_DustColor, _V0_AmbientRadiance;
float _V0_Anisotropy, _V0_UseLightCache, _V0_StormSkipEmpty, _V0_StormOccupancySize;
TEXTURE3D(_V0_SunLightCache); TEXTURE3D(_V0_StormOccupancy);
            float EmptyDistance0(float3 p, float3 dir)
            {
                float3 uv=p+0.5, d=dir;
                if (_V0_AxisOptions.x>0.5) { uv=uv.xzy; d=d.xzy; }
                uv=lerp(uv,1-uv,_V0_AxisOptions.yzw);
                d*=1-2*_V0_AxisOptions.yzw;
                int size=(int)_V0_StormOccupancySize;
                int3 cell=clamp((int3)floor(uv*size),0,size-1);
                if (_V0_StormOccupancy.Load(int4(cell,0)).r>0) return 0;
                float result=1e20;
                [unroll] for(int axis=0;axis<3;axis++)
                {
                    if(abs(d[axis])>1e-8)
                    {
                        float face=(cell[axis]+(d[axis]>0 ? 1:0))/(float)size;
                        result=min(result,max(0,(face-uv[axis])/d[axis]));
                    }
                }
                return result;
            }

            float DirectSunTransmission0(float3 p, float3 lightLocal)
            {
                float enter, leave;
                if (!IntersectBox(p, lightLocal, enter, leave)) return 1;
                int count = (int)_V0_MarchParams.y;
                float ds = leave / count;
                float opticalDepth = 0;
                [loop] for (int j = 0; j < count; j++)
                {
                    opticalDepth += Density0(p + lightLocal * ((j + 0.5) * ds)) * _V0_DensityParams.z * ds;
                    if (opticalDepth > 10) break;
                }
                return exp(-opticalDepth);
            }

            float SunTransmission0(float3 p, float3 lightLocal)
            {
                [branch] if (_V0_UseLightCache > 0.5)
                    return saturate(SAMPLE_TEXTURE3D_LOD(_V0_SunLightCache, sampler_LinearClamp, saturate(p + 0.5), 0).r);
                return DirectSunTransmission0(p, lightLocal);
            }



TEXTURE3D(_V1_DensityTex);

TEXTURE3D(_V1_DensityTexB);

float _V1_FrameBlend;
TEXTURE3D(_V1_StormEdgeNoiseTex); 
TEXTURE3D(_V1_StormInteriorNoiseTex); 
float _V1_StormBakedNoise;

float4 _V1_AxisOptions, _V1_DensityParams;
float4 _V1_StormContrast;
float4 _V1_EdgeShape, _V1_EdgeDetail, _V1_EdgeOffset;
float4 _V1_InteriorShape, _V1_InteriorDetail, _V1_InteriorOffset;

float StormHash1(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}
float StormValue1(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f*f*(3-2*f);
    float a = lerp(StormHash1(i), StormHash1(i+float3(1,0,0)), f.x);
    float b = lerp(StormHash1(i+float3(0,1,0)), StormHash1(i+float3(1,1,0)), f.x);
    float c = lerp(StormHash1(i+float3(0,0,1)), StormHash1(i+float3(1,0,1)), f.x);
    float d = lerp(StormHash1(i+float3(0,1,1)), StormHash1(i+float3(1,1,1)), f.x);
    return lerp(lerp(a,b,f.y),lerp(c,d,f.y),f.z);
}
float StormCellular1(float3 p)
{
    float3 cell = floor(p), f = frac(p);
    float nearest = 10;
    [loop] for (int z=-1; z<=1; z++)
    [loop] for (int y=-1; y<=1; y++)
    [loop] for (int x=-1; x<=1; x++)
    {
        float3 o = float3(x,y,z), q = cell+o;
        // Jitter restricted to the middle of each cell for a stable local neighbourhood.
        float3 feature = 0.25 + 0.5*float3(StormHash1(q),StormHash1(q+19.17),StormHash1(q+47.53));
        float3 delta = o + feature - f;
        nearest = min(nearest,dot(delta,delta));
    }
    return saturate(1-sqrt(nearest));
}
float StormNoise1(float3 p, int kind, int octaves)
{
    [branch] if (kind == 3) return StormCellular1(p);
    [branch] if (kind == 0) return StormValue1(p);
    float sum=0, weight=0, amplitude=1;
    [loop] for (int i=0; i<octaves; i++)
    {
        float n=StormValue1(p);
        if (kind == 2) n=1-abs(2*n-1);
        sum+=n*amplitude; weight+=amplitude;
        p=p*2.03+float3(17.1,9.2,3.7); amplitude*=0.5;
    }
    return sum/max(weight,0.0001);
}
float StormLayer1(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 q=p*shape.z+offset.xyz+detail.z*float3(0.173,0.317,0.619);
    [branch] if (_V1_StormBakedNoise > 0.5)
    {
        // The layer's shape is passed unchanged by every caller.
        if (all(shape == _V1_EdgeShape) && all(detail == _V1_EdgeDetail) && all(offset == _V1_EdgeOffset))
            return SAMPLE_TEXTURE3D_LOD(_V1_StormEdgeNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
        return SAMPLE_TEXTURE3D_LOD(_V1_StormInteriorNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
    }
    return StormNoise1(q,(int)shape.y,(int)detail.y);
}
float3 StormWarp1(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 n=float3(StormLayer1(p,shape,detail,offset),
        StormLayer1(p+float3(3.13,7.71,1.17),shape,detail,offset),
        StormLayer1(p+float3(9.43,2.37,5.31),shape,detail,offset));
    return (2*n-1)*detail.x;
}
float StormReadDensity1(float3 uvw)
{
    if (any(uvw<0) || any(uvw>1)) return 0;
    float d=SAMPLE_TEXTURE3D_LOD(_V1_DensityTex,sampler_LinearClamp,uvw,0).r;
    [branch] if (_V1_FrameBlend>0)
        d=lerp(d,SAMPLE_TEXTURE3D_LOD(_V1_DensityTexB,sampler_LinearClamp,uvw,0).r,_V1_FrameBlend);
    return max(d,0);
}
float Density1(float3 localPos)
{
    float3 boxUV=localPos+0.5;
    if (any(boxUV<0) || any(boxUV>1)) return 0;
    float3 edge=min(boxUV,1-boxUV);
    float fade=_V1_DensityParams.w>0 ? saturate(min(edge.x,min(edge.y,edge.z))/_V1_DensityParams.w) : 1;
    float3 uvw=boxUV;
    if (_V1_AxisOptions.x>0.5) uvw=uvw.xzy;
    uvw=lerp(uvw,1-uvw,_V1_AxisOptions.yzw);
    float raw=StormReadDensity1(uvw);
    float band=max(_V1_StormContrast.z,0.0001);
    // A low-density mask, not a geometric signed-distance surface.
    float edgeMask=1-smoothstep(0,band,raw);
    float3 warp=0;
    [branch] if (_V1_EdgeShape.x>0.5 && _V1_EdgeDetail.x>0)
        warp+=StormWarp1(uvw,_V1_EdgeShape,_V1_EdgeDetail,_V1_EdgeOffset)*edgeMask;
    [branch] if (_V1_InteriorShape.x>0.5 && _V1_InteriorDetail.x>0)
        warp+=StormWarp1(uvw,_V1_InteriorShape,_V1_InteriorDetail,_V1_InteriorOffset)*(1-edgeMask);
    [branch] if (any(warp!=0)) raw=StormReadDensity1(uvw+warp);
    edgeMask=1-smoothstep(0,band,raw);
    [branch] if (_V1_EdgeShape.x>0.5 && _V1_EdgeShape.w>0)
        raw*=saturate(1-_V1_EdgeShape.w*StormLayer1(uvw,_V1_EdgeShape,_V1_EdgeDetail,_V1_EdgeOffset)*edgeMask);
    [branch] if (_V1_InteriorShape.x>0.5 && _V1_InteriorShape.w>0)
        raw*=max(0,1+_V1_InteriorShape.w*(2*StormLayer1(uvw,_V1_InteriorShape,_V1_InteriorDetail,_V1_InteriorOffset)-1)*(1-edgeMask));
    float value=max(raw-_V1_DensityParams.y,0);
    [branch] if (value>0 && abs(_V1_StormContrast.x-1)>0.00001)
    {
        float pivot=max(_V1_StormContrast.y,0.0001);
        value=pivot*exp2(clamp(log2(max(value/pivot,1e-20))*_V1_StormContrast.x,-80,80));
    }
    return value*_V1_DensityParams.x*fade;
}


float4x4 _V1_VolumeWorldToLocal;
float4 _V1_MarchParams, _V1_AdaptiveMarch, _V1_SunDirection, _V1_SunRadiance, _V1_DustColor, _V1_AmbientRadiance;
float _V1_Anisotropy, _V1_UseLightCache, _V1_StormSkipEmpty, _V1_StormOccupancySize;
TEXTURE3D(_V1_SunLightCache); TEXTURE3D(_V1_StormOccupancy);
            float EmptyDistance1(float3 p, float3 dir)
            {
                float3 uv=p+0.5, d=dir;
                if (_V1_AxisOptions.x>0.5) { uv=uv.xzy; d=d.xzy; }
                uv=lerp(uv,1-uv,_V1_AxisOptions.yzw);
                d*=1-2*_V1_AxisOptions.yzw;
                int size=(int)_V1_StormOccupancySize;
                int3 cell=clamp((int3)floor(uv*size),0,size-1);
                if (_V1_StormOccupancy.Load(int4(cell,0)).r>0) return 0;
                float result=1e20;
                [unroll] for(int axis=0;axis<3;axis++)
                {
                    if(abs(d[axis])>1e-8)
                    {
                        float face=(cell[axis]+(d[axis]>0 ? 1:0))/(float)size;
                        result=min(result,max(0,(face-uv[axis])/d[axis]));
                    }
                }
                return result;
            }

            float DirectSunTransmission1(float3 p, float3 lightLocal)
            {
                float enter, leave;
                if (!IntersectBox(p, lightLocal, enter, leave)) return 1;
                int count = (int)_V1_MarchParams.y;
                float ds = leave / count;
                float opticalDepth = 0;
                [loop] for (int j = 0; j < count; j++)
                {
                    opticalDepth += Density1(p + lightLocal * ((j + 0.5) * ds)) * _V1_DensityParams.z * ds;
                    if (opticalDepth > 10) break;
                }
                return exp(-opticalDepth);
            }

            float SunTransmission1(float3 p, float3 lightLocal)
            {
                [branch] if (_V1_UseLightCache > 0.5)
                    return saturate(SAMPLE_TEXTURE3D_LOD(_V1_SunLightCache, sampler_LinearClamp, saturate(p + 0.5), 0).r);
                return DirectSunTransmission1(p, lightLocal);
            }



TEXTURE3D(_V2_DensityTex);

TEXTURE3D(_V2_DensityTexB);

float _V2_FrameBlend;
TEXTURE3D(_V2_StormEdgeNoiseTex); 
TEXTURE3D(_V2_StormInteriorNoiseTex); 
float _V2_StormBakedNoise;

float4 _V2_AxisOptions, _V2_DensityParams;
float4 _V2_StormContrast;
float4 _V2_EdgeShape, _V2_EdgeDetail, _V2_EdgeOffset;
float4 _V2_InteriorShape, _V2_InteriorDetail, _V2_InteriorOffset;

float StormHash2(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}
float StormValue2(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f*f*(3-2*f);
    float a = lerp(StormHash2(i), StormHash2(i+float3(1,0,0)), f.x);
    float b = lerp(StormHash2(i+float3(0,1,0)), StormHash2(i+float3(1,1,0)), f.x);
    float c = lerp(StormHash2(i+float3(0,0,1)), StormHash2(i+float3(1,0,1)), f.x);
    float d = lerp(StormHash2(i+float3(0,1,1)), StormHash2(i+float3(1,1,1)), f.x);
    return lerp(lerp(a,b,f.y),lerp(c,d,f.y),f.z);
}
float StormCellular2(float3 p)
{
    float3 cell = floor(p), f = frac(p);
    float nearest = 10;
    [loop] for (int z=-1; z<=1; z++)
    [loop] for (int y=-1; y<=1; y++)
    [loop] for (int x=-1; x<=1; x++)
    {
        float3 o = float3(x,y,z), q = cell+o;
        // Jitter restricted to the middle of each cell for a stable local neighbourhood.
        float3 feature = 0.25 + 0.5*float3(StormHash2(q),StormHash2(q+19.17),StormHash2(q+47.53));
        float3 delta = o + feature - f;
        nearest = min(nearest,dot(delta,delta));
    }
    return saturate(1-sqrt(nearest));
}
float StormNoise2(float3 p, int kind, int octaves)
{
    [branch] if (kind == 3) return StormCellular2(p);
    [branch] if (kind == 0) return StormValue2(p);
    float sum=0, weight=0, amplitude=1;
    [loop] for (int i=0; i<octaves; i++)
    {
        float n=StormValue2(p);
        if (kind == 2) n=1-abs(2*n-1);
        sum+=n*amplitude; weight+=amplitude;
        p=p*2.03+float3(17.1,9.2,3.7); amplitude*=0.5;
    }
    return sum/max(weight,0.0001);
}
float StormLayer2(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 q=p*shape.z+offset.xyz+detail.z*float3(0.173,0.317,0.619);
    [branch] if (_V2_StormBakedNoise > 0.5)
    {
        // The layer's shape is passed unchanged by every caller.
        if (all(shape == _V2_EdgeShape) && all(detail == _V2_EdgeDetail) && all(offset == _V2_EdgeOffset))
            return SAMPLE_TEXTURE3D_LOD(_V2_StormEdgeNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
        return SAMPLE_TEXTURE3D_LOD(_V2_StormInteriorNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
    }
    return StormNoise2(q,(int)shape.y,(int)detail.y);
}
float3 StormWarp2(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 n=float3(StormLayer2(p,shape,detail,offset),
        StormLayer2(p+float3(3.13,7.71,1.17),shape,detail,offset),
        StormLayer2(p+float3(9.43,2.37,5.31),shape,detail,offset));
    return (2*n-1)*detail.x;
}
float StormReadDensity2(float3 uvw)
{
    if (any(uvw<0) || any(uvw>1)) return 0;
    float d=SAMPLE_TEXTURE3D_LOD(_V2_DensityTex,sampler_LinearClamp,uvw,0).r;
    [branch] if (_V2_FrameBlend>0)
        d=lerp(d,SAMPLE_TEXTURE3D_LOD(_V2_DensityTexB,sampler_LinearClamp,uvw,0).r,_V2_FrameBlend);
    return max(d,0);
}
float Density2(float3 localPos)
{
    float3 boxUV=localPos+0.5;
    if (any(boxUV<0) || any(boxUV>1)) return 0;
    float3 edge=min(boxUV,1-boxUV);
    float fade=_V2_DensityParams.w>0 ? saturate(min(edge.x,min(edge.y,edge.z))/_V2_DensityParams.w) : 1;
    float3 uvw=boxUV;
    if (_V2_AxisOptions.x>0.5) uvw=uvw.xzy;
    uvw=lerp(uvw,1-uvw,_V2_AxisOptions.yzw);
    float raw=StormReadDensity2(uvw);
    float band=max(_V2_StormContrast.z,0.0001);
    // A low-density mask, not a geometric signed-distance surface.
    float edgeMask=1-smoothstep(0,band,raw);
    float3 warp=0;
    [branch] if (_V2_EdgeShape.x>0.5 && _V2_EdgeDetail.x>0)
        warp+=StormWarp2(uvw,_V2_EdgeShape,_V2_EdgeDetail,_V2_EdgeOffset)*edgeMask;
    [branch] if (_V2_InteriorShape.x>0.5 && _V2_InteriorDetail.x>0)
        warp+=StormWarp2(uvw,_V2_InteriorShape,_V2_InteriorDetail,_V2_InteriorOffset)*(1-edgeMask);
    [branch] if (any(warp!=0)) raw=StormReadDensity2(uvw+warp);
    edgeMask=1-smoothstep(0,band,raw);
    [branch] if (_V2_EdgeShape.x>0.5 && _V2_EdgeShape.w>0)
        raw*=saturate(1-_V2_EdgeShape.w*StormLayer2(uvw,_V2_EdgeShape,_V2_EdgeDetail,_V2_EdgeOffset)*edgeMask);
    [branch] if (_V2_InteriorShape.x>0.5 && _V2_InteriorShape.w>0)
        raw*=max(0,1+_V2_InteriorShape.w*(2*StormLayer2(uvw,_V2_InteriorShape,_V2_InteriorDetail,_V2_InteriorOffset)-1)*(1-edgeMask));
    float value=max(raw-_V2_DensityParams.y,0);
    [branch] if (value>0 && abs(_V2_StormContrast.x-1)>0.00001)
    {
        float pivot=max(_V2_StormContrast.y,0.0001);
        value=pivot*exp2(clamp(log2(max(value/pivot,1e-20))*_V2_StormContrast.x,-80,80));
    }
    return value*_V2_DensityParams.x*fade;
}


float4x4 _V2_VolumeWorldToLocal;
float4 _V2_MarchParams, _V2_AdaptiveMarch, _V2_SunDirection, _V2_SunRadiance, _V2_DustColor, _V2_AmbientRadiance;
float _V2_Anisotropy, _V2_UseLightCache, _V2_StormSkipEmpty, _V2_StormOccupancySize;
TEXTURE3D(_V2_SunLightCache); TEXTURE3D(_V2_StormOccupancy);
            float EmptyDistance2(float3 p, float3 dir)
            {
                float3 uv=p+0.5, d=dir;
                if (_V2_AxisOptions.x>0.5) { uv=uv.xzy; d=d.xzy; }
                uv=lerp(uv,1-uv,_V2_AxisOptions.yzw);
                d*=1-2*_V2_AxisOptions.yzw;
                int size=(int)_V2_StormOccupancySize;
                int3 cell=clamp((int3)floor(uv*size),0,size-1);
                if (_V2_StormOccupancy.Load(int4(cell,0)).r>0) return 0;
                float result=1e20;
                [unroll] for(int axis=0;axis<3;axis++)
                {
                    if(abs(d[axis])>1e-8)
                    {
                        float face=(cell[axis]+(d[axis]>0 ? 1:0))/(float)size;
                        result=min(result,max(0,(face-uv[axis])/d[axis]));
                    }
                }
                return result;
            }

            float DirectSunTransmission2(float3 p, float3 lightLocal)
            {
                float enter, leave;
                if (!IntersectBox(p, lightLocal, enter, leave)) return 1;
                int count = (int)_V2_MarchParams.y;
                float ds = leave / count;
                float opticalDepth = 0;
                [loop] for (int j = 0; j < count; j++)
                {
                    opticalDepth += Density2(p + lightLocal * ((j + 0.5) * ds)) * _V2_DensityParams.z * ds;
                    if (opticalDepth > 10) break;
                }
                return exp(-opticalDepth);
            }

            float SunTransmission2(float3 p, float3 lightLocal)
            {
                [branch] if (_V2_UseLightCache > 0.5)
                    return saturate(SAMPLE_TEXTURE3D_LOD(_V2_SunLightCache, sampler_LinearClamp, saturate(p + 0.5), 0).r);
                return DirectSunTransmission2(p, lightLocal);
            }



TEXTURE3D(_V3_DensityTex);

TEXTURE3D(_V3_DensityTexB);

float _V3_FrameBlend;
TEXTURE3D(_V3_StormEdgeNoiseTex); 
TEXTURE3D(_V3_StormInteriorNoiseTex); 
float _V3_StormBakedNoise;

float4 _V3_AxisOptions, _V3_DensityParams;
float4 _V3_StormContrast;
float4 _V3_EdgeShape, _V3_EdgeDetail, _V3_EdgeOffset;
float4 _V3_InteriorShape, _V3_InteriorDetail, _V3_InteriorOffset;

float StormHash3(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}
float StormValue3(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f*f*(3-2*f);
    float a = lerp(StormHash3(i), StormHash3(i+float3(1,0,0)), f.x);
    float b = lerp(StormHash3(i+float3(0,1,0)), StormHash3(i+float3(1,1,0)), f.x);
    float c = lerp(StormHash3(i+float3(0,0,1)), StormHash3(i+float3(1,0,1)), f.x);
    float d = lerp(StormHash3(i+float3(0,1,1)), StormHash3(i+float3(1,1,1)), f.x);
    return lerp(lerp(a,b,f.y),lerp(c,d,f.y),f.z);
}
float StormCellular3(float3 p)
{
    float3 cell = floor(p), f = frac(p);
    float nearest = 10;
    [loop] for (int z=-1; z<=1; z++)
    [loop] for (int y=-1; y<=1; y++)
    [loop] for (int x=-1; x<=1; x++)
    {
        float3 o = float3(x,y,z), q = cell+o;
        // Jitter restricted to the middle of each cell for a stable local neighbourhood.
        float3 feature = 0.25 + 0.5*float3(StormHash3(q),StormHash3(q+19.17),StormHash3(q+47.53));
        float3 delta = o + feature - f;
        nearest = min(nearest,dot(delta,delta));
    }
    return saturate(1-sqrt(nearest));
}
float StormNoise3(float3 p, int kind, int octaves)
{
    [branch] if (kind == 3) return StormCellular3(p);
    [branch] if (kind == 0) return StormValue3(p);
    float sum=0, weight=0, amplitude=1;
    [loop] for (int i=0; i<octaves; i++)
    {
        float n=StormValue3(p);
        if (kind == 2) n=1-abs(2*n-1);
        sum+=n*amplitude; weight+=amplitude;
        p=p*2.03+float3(17.1,9.2,3.7); amplitude*=0.5;
    }
    return sum/max(weight,0.0001);
}
float StormLayer3(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 q=p*shape.z+offset.xyz+detail.z*float3(0.173,0.317,0.619);
    [branch] if (_V3_StormBakedNoise > 0.5)
    {
        // The layer's shape is passed unchanged by every caller.
        if (all(shape == _V3_EdgeShape) && all(detail == _V3_EdgeDetail) && all(offset == _V3_EdgeOffset))
            return SAMPLE_TEXTURE3D_LOD(_V3_StormEdgeNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
        return SAMPLE_TEXTURE3D_LOD(_V3_StormInteriorNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
    }
    return StormNoise3(q,(int)shape.y,(int)detail.y);
}
float3 StormWarp3(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 n=float3(StormLayer3(p,shape,detail,offset),
        StormLayer3(p+float3(3.13,7.71,1.17),shape,detail,offset),
        StormLayer3(p+float3(9.43,2.37,5.31),shape,detail,offset));
    return (2*n-1)*detail.x;
}
float StormReadDensity3(float3 uvw)
{
    if (any(uvw<0) || any(uvw>1)) return 0;
    float d=SAMPLE_TEXTURE3D_LOD(_V3_DensityTex,sampler_LinearClamp,uvw,0).r;
    [branch] if (_V3_FrameBlend>0)
        d=lerp(d,SAMPLE_TEXTURE3D_LOD(_V3_DensityTexB,sampler_LinearClamp,uvw,0).r,_V3_FrameBlend);
    return max(d,0);
}
float Density3(float3 localPos)
{
    float3 boxUV=localPos+0.5;
    if (any(boxUV<0) || any(boxUV>1)) return 0;
    float3 edge=min(boxUV,1-boxUV);
    float fade=_V3_DensityParams.w>0 ? saturate(min(edge.x,min(edge.y,edge.z))/_V3_DensityParams.w) : 1;
    float3 uvw=boxUV;
    if (_V3_AxisOptions.x>0.5) uvw=uvw.xzy;
    uvw=lerp(uvw,1-uvw,_V3_AxisOptions.yzw);
    float raw=StormReadDensity3(uvw);
    float band=max(_V3_StormContrast.z,0.0001);
    // A low-density mask, not a geometric signed-distance surface.
    float edgeMask=1-smoothstep(0,band,raw);
    float3 warp=0;
    [branch] if (_V3_EdgeShape.x>0.5 && _V3_EdgeDetail.x>0)
        warp+=StormWarp3(uvw,_V3_EdgeShape,_V3_EdgeDetail,_V3_EdgeOffset)*edgeMask;
    [branch] if (_V3_InteriorShape.x>0.5 && _V3_InteriorDetail.x>0)
        warp+=StormWarp3(uvw,_V3_InteriorShape,_V3_InteriorDetail,_V3_InteriorOffset)*(1-edgeMask);
    [branch] if (any(warp!=0)) raw=StormReadDensity3(uvw+warp);
    edgeMask=1-smoothstep(0,band,raw);
    [branch] if (_V3_EdgeShape.x>0.5 && _V3_EdgeShape.w>0)
        raw*=saturate(1-_V3_EdgeShape.w*StormLayer3(uvw,_V3_EdgeShape,_V3_EdgeDetail,_V3_EdgeOffset)*edgeMask);
    [branch] if (_V3_InteriorShape.x>0.5 && _V3_InteriorShape.w>0)
        raw*=max(0,1+_V3_InteriorShape.w*(2*StormLayer3(uvw,_V3_InteriorShape,_V3_InteriorDetail,_V3_InteriorOffset)-1)*(1-edgeMask));
    float value=max(raw-_V3_DensityParams.y,0);
    [branch] if (value>0 && abs(_V3_StormContrast.x-1)>0.00001)
    {
        float pivot=max(_V3_StormContrast.y,0.0001);
        value=pivot*exp2(clamp(log2(max(value/pivot,1e-20))*_V3_StormContrast.x,-80,80));
    }
    return value*_V3_DensityParams.x*fade;
}


float4x4 _V3_VolumeWorldToLocal;
float4 _V3_MarchParams, _V3_AdaptiveMarch, _V3_SunDirection, _V3_SunRadiance, _V3_DustColor, _V3_AmbientRadiance;
float _V3_Anisotropy, _V3_UseLightCache, _V3_StormSkipEmpty, _V3_StormOccupancySize;
TEXTURE3D(_V3_SunLightCache); TEXTURE3D(_V3_StormOccupancy);
            float EmptyDistance3(float3 p, float3 dir)
            {
                float3 uv=p+0.5, d=dir;
                if (_V3_AxisOptions.x>0.5) { uv=uv.xzy; d=d.xzy; }
                uv=lerp(uv,1-uv,_V3_AxisOptions.yzw);
                d*=1-2*_V3_AxisOptions.yzw;
                int size=(int)_V3_StormOccupancySize;
                int3 cell=clamp((int3)floor(uv*size),0,size-1);
                if (_V3_StormOccupancy.Load(int4(cell,0)).r>0) return 0;
                float result=1e20;
                [unroll] for(int axis=0;axis<3;axis++)
                {
                    if(abs(d[axis])>1e-8)
                    {
                        float face=(cell[axis]+(d[axis]>0 ? 1:0))/(float)size;
                        result=min(result,max(0,(face-uv[axis])/d[axis]));
                    }
                }
                return result;
            }

            float DirectSunTransmission3(float3 p, float3 lightLocal)
            {
                float enter, leave;
                if (!IntersectBox(p, lightLocal, enter, leave)) return 1;
                int count = (int)_V3_MarchParams.y;
                float ds = leave / count;
                float opticalDepth = 0;
                [loop] for (int j = 0; j < count; j++)
                {
                    opticalDepth += Density3(p + lightLocal * ((j + 0.5) * ds)) * _V3_DensityParams.z * ds;
                    if (opticalDepth > 10) break;
                }
                return exp(-opticalDepth);
            }

            float SunTransmission3(float3 p, float3 lightLocal)
            {
                [branch] if (_V3_UseLightCache > 0.5)
                    return saturate(SAMPLE_TEXTURE3D_LOD(_V3_SunLightCache, sampler_LinearClamp, saturate(p + 0.5), 0).r);
                return DirectSunTransmission3(p, lightLocal);
            }



TEXTURE3D(_V4_DensityTex);

TEXTURE3D(_V4_DensityTexB);

float _V4_FrameBlend;
TEXTURE3D(_V4_StormEdgeNoiseTex); 
TEXTURE3D(_V4_StormInteriorNoiseTex); 
float _V4_StormBakedNoise;

float4 _V4_AxisOptions, _V4_DensityParams;
float4 _V4_StormContrast;
float4 _V4_EdgeShape, _V4_EdgeDetail, _V4_EdgeOffset;
float4 _V4_InteriorShape, _V4_InteriorDetail, _V4_InteriorOffset;

float StormHash4(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}
float StormValue4(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f*f*(3-2*f);
    float a = lerp(StormHash4(i), StormHash4(i+float3(1,0,0)), f.x);
    float b = lerp(StormHash4(i+float3(0,1,0)), StormHash4(i+float3(1,1,0)), f.x);
    float c = lerp(StormHash4(i+float3(0,0,1)), StormHash4(i+float3(1,0,1)), f.x);
    float d = lerp(StormHash4(i+float3(0,1,1)), StormHash4(i+float3(1,1,1)), f.x);
    return lerp(lerp(a,b,f.y),lerp(c,d,f.y),f.z);
}
float StormCellular4(float3 p)
{
    float3 cell = floor(p), f = frac(p);
    float nearest = 10;
    [loop] for (int z=-1; z<=1; z++)
    [loop] for (int y=-1; y<=1; y++)
    [loop] for (int x=-1; x<=1; x++)
    {
        float3 o = float3(x,y,z), q = cell+o;
        // Jitter restricted to the middle of each cell for a stable local neighbourhood.
        float3 feature = 0.25 + 0.5*float3(StormHash4(q),StormHash4(q+19.17),StormHash4(q+47.53));
        float3 delta = o + feature - f;
        nearest = min(nearest,dot(delta,delta));
    }
    return saturate(1-sqrt(nearest));
}
float StormNoise4(float3 p, int kind, int octaves)
{
    [branch] if (kind == 3) return StormCellular4(p);
    [branch] if (kind == 0) return StormValue4(p);
    float sum=0, weight=0, amplitude=1;
    [loop] for (int i=0; i<octaves; i++)
    {
        float n=StormValue4(p);
        if (kind == 2) n=1-abs(2*n-1);
        sum+=n*amplitude; weight+=amplitude;
        p=p*2.03+float3(17.1,9.2,3.7); amplitude*=0.5;
    }
    return sum/max(weight,0.0001);
}
float StormLayer4(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 q=p*shape.z+offset.xyz+detail.z*float3(0.173,0.317,0.619);
    [branch] if (_V4_StormBakedNoise > 0.5)
    {
        // The layer's shape is passed unchanged by every caller.
        if (all(shape == _V4_EdgeShape) && all(detail == _V4_EdgeDetail) && all(offset == _V4_EdgeOffset))
            return SAMPLE_TEXTURE3D_LOD(_V4_StormEdgeNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
        return SAMPLE_TEXTURE3D_LOD(_V4_StormInteriorNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
    }
    return StormNoise4(q,(int)shape.y,(int)detail.y);
}
float3 StormWarp4(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 n=float3(StormLayer4(p,shape,detail,offset),
        StormLayer4(p+float3(3.13,7.71,1.17),shape,detail,offset),
        StormLayer4(p+float3(9.43,2.37,5.31),shape,detail,offset));
    return (2*n-1)*detail.x;
}
float StormReadDensity4(float3 uvw)
{
    if (any(uvw<0) || any(uvw>1)) return 0;
    float d=SAMPLE_TEXTURE3D_LOD(_V4_DensityTex,sampler_LinearClamp,uvw,0).r;
    [branch] if (_V4_FrameBlend>0)
        d=lerp(d,SAMPLE_TEXTURE3D_LOD(_V4_DensityTexB,sampler_LinearClamp,uvw,0).r,_V4_FrameBlend);
    return max(d,0);
}
float Density4(float3 localPos)
{
    float3 boxUV=localPos+0.5;
    if (any(boxUV<0) || any(boxUV>1)) return 0;
    float3 edge=min(boxUV,1-boxUV);
    float fade=_V4_DensityParams.w>0 ? saturate(min(edge.x,min(edge.y,edge.z))/_V4_DensityParams.w) : 1;
    float3 uvw=boxUV;
    if (_V4_AxisOptions.x>0.5) uvw=uvw.xzy;
    uvw=lerp(uvw,1-uvw,_V4_AxisOptions.yzw);
    float raw=StormReadDensity4(uvw);
    float band=max(_V4_StormContrast.z,0.0001);
    // A low-density mask, not a geometric signed-distance surface.
    float edgeMask=1-smoothstep(0,band,raw);
    float3 warp=0;
    [branch] if (_V4_EdgeShape.x>0.5 && _V4_EdgeDetail.x>0)
        warp+=StormWarp4(uvw,_V4_EdgeShape,_V4_EdgeDetail,_V4_EdgeOffset)*edgeMask;
    [branch] if (_V4_InteriorShape.x>0.5 && _V4_InteriorDetail.x>0)
        warp+=StormWarp4(uvw,_V4_InteriorShape,_V4_InteriorDetail,_V4_InteriorOffset)*(1-edgeMask);
    [branch] if (any(warp!=0)) raw=StormReadDensity4(uvw+warp);
    edgeMask=1-smoothstep(0,band,raw);
    [branch] if (_V4_EdgeShape.x>0.5 && _V4_EdgeShape.w>0)
        raw*=saturate(1-_V4_EdgeShape.w*StormLayer4(uvw,_V4_EdgeShape,_V4_EdgeDetail,_V4_EdgeOffset)*edgeMask);
    [branch] if (_V4_InteriorShape.x>0.5 && _V4_InteriorShape.w>0)
        raw*=max(0,1+_V4_InteriorShape.w*(2*StormLayer4(uvw,_V4_InteriorShape,_V4_InteriorDetail,_V4_InteriorOffset)-1)*(1-edgeMask));
    float value=max(raw-_V4_DensityParams.y,0);
    [branch] if (value>0 && abs(_V4_StormContrast.x-1)>0.00001)
    {
        float pivot=max(_V4_StormContrast.y,0.0001);
        value=pivot*exp2(clamp(log2(max(value/pivot,1e-20))*_V4_StormContrast.x,-80,80));
    }
    return value*_V4_DensityParams.x*fade;
}


float4x4 _V4_VolumeWorldToLocal;
float4 _V4_MarchParams, _V4_AdaptiveMarch, _V4_SunDirection, _V4_SunRadiance, _V4_DustColor, _V4_AmbientRadiance;
float _V4_Anisotropy, _V4_UseLightCache, _V4_StormSkipEmpty, _V4_StormOccupancySize;
TEXTURE3D(_V4_SunLightCache); TEXTURE3D(_V4_StormOccupancy);
            float EmptyDistance4(float3 p, float3 dir)
            {
                float3 uv=p+0.5, d=dir;
                if (_V4_AxisOptions.x>0.5) { uv=uv.xzy; d=d.xzy; }
                uv=lerp(uv,1-uv,_V4_AxisOptions.yzw);
                d*=1-2*_V4_AxisOptions.yzw;
                int size=(int)_V4_StormOccupancySize;
                int3 cell=clamp((int3)floor(uv*size),0,size-1);
                if (_V4_StormOccupancy.Load(int4(cell,0)).r>0) return 0;
                float result=1e20;
                [unroll] for(int axis=0;axis<3;axis++)
                {
                    if(abs(d[axis])>1e-8)
                    {
                        float face=(cell[axis]+(d[axis]>0 ? 1:0))/(float)size;
                        result=min(result,max(0,(face-uv[axis])/d[axis]));
                    }
                }
                return result;
            }

            float DirectSunTransmission4(float3 p, float3 lightLocal)
            {
                float enter, leave;
                if (!IntersectBox(p, lightLocal, enter, leave)) return 1;
                int count = (int)_V4_MarchParams.y;
                float ds = leave / count;
                float opticalDepth = 0;
                [loop] for (int j = 0; j < count; j++)
                {
                    opticalDepth += Density4(p + lightLocal * ((j + 0.5) * ds)) * _V4_DensityParams.z * ds;
                    if (opticalDepth > 10) break;
                }
                return exp(-opticalDepth);
            }

            float SunTransmission4(float3 p, float3 lightLocal)
            {
                [branch] if (_V4_UseLightCache > 0.5)
                    return saturate(SAMPLE_TEXTURE3D_LOD(_V4_SunLightCache, sampler_LinearClamp, saturate(p + 0.5), 0).r);
                return DirectSunTransmission4(p, lightLocal);
            }



TEXTURE3D(_V5_DensityTex);

TEXTURE3D(_V5_DensityTexB);

float _V5_FrameBlend;
TEXTURE3D(_V5_StormEdgeNoiseTex); 
TEXTURE3D(_V5_StormInteriorNoiseTex); 
float _V5_StormBakedNoise;

float4 _V5_AxisOptions, _V5_DensityParams;
float4 _V5_StormContrast;
float4 _V5_EdgeShape, _V5_EdgeDetail, _V5_EdgeOffset;
float4 _V5_InteriorShape, _V5_InteriorDetail, _V5_InteriorOffset;

float StormHash5(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}
float StormValue5(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f*f*(3-2*f);
    float a = lerp(StormHash5(i), StormHash5(i+float3(1,0,0)), f.x);
    float b = lerp(StormHash5(i+float3(0,1,0)), StormHash5(i+float3(1,1,0)), f.x);
    float c = lerp(StormHash5(i+float3(0,0,1)), StormHash5(i+float3(1,0,1)), f.x);
    float d = lerp(StormHash5(i+float3(0,1,1)), StormHash5(i+float3(1,1,1)), f.x);
    return lerp(lerp(a,b,f.y),lerp(c,d,f.y),f.z);
}
float StormCellular5(float3 p)
{
    float3 cell = floor(p), f = frac(p);
    float nearest = 10;
    [loop] for (int z=-1; z<=1; z++)
    [loop] for (int y=-1; y<=1; y++)
    [loop] for (int x=-1; x<=1; x++)
    {
        float3 o = float3(x,y,z), q = cell+o;
        // Jitter restricted to the middle of each cell for a stable local neighbourhood.
        float3 feature = 0.25 + 0.5*float3(StormHash5(q),StormHash5(q+19.17),StormHash5(q+47.53));
        float3 delta = o + feature - f;
        nearest = min(nearest,dot(delta,delta));
    }
    return saturate(1-sqrt(nearest));
}
float StormNoise5(float3 p, int kind, int octaves)
{
    [branch] if (kind == 3) return StormCellular5(p);
    [branch] if (kind == 0) return StormValue5(p);
    float sum=0, weight=0, amplitude=1;
    [loop] for (int i=0; i<octaves; i++)
    {
        float n=StormValue5(p);
        if (kind == 2) n=1-abs(2*n-1);
        sum+=n*amplitude; weight+=amplitude;
        p=p*2.03+float3(17.1,9.2,3.7); amplitude*=0.5;
    }
    return sum/max(weight,0.0001);
}
float StormLayer5(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 q=p*shape.z+offset.xyz+detail.z*float3(0.173,0.317,0.619);
    [branch] if (_V5_StormBakedNoise > 0.5)
    {
        // The layer's shape is passed unchanged by every caller.
        if (all(shape == _V5_EdgeShape) && all(detail == _V5_EdgeDetail) && all(offset == _V5_EdgeOffset))
            return SAMPLE_TEXTURE3D_LOD(_V5_StormEdgeNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
        return SAMPLE_TEXTURE3D_LOD(_V5_StormInteriorNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
    }
    return StormNoise5(q,(int)shape.y,(int)detail.y);
}
float3 StormWarp5(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 n=float3(StormLayer5(p,shape,detail,offset),
        StormLayer5(p+float3(3.13,7.71,1.17),shape,detail,offset),
        StormLayer5(p+float3(9.43,2.37,5.31),shape,detail,offset));
    return (2*n-1)*detail.x;
}
float StormReadDensity5(float3 uvw)
{
    if (any(uvw<0) || any(uvw>1)) return 0;
    float d=SAMPLE_TEXTURE3D_LOD(_V5_DensityTex,sampler_LinearClamp,uvw,0).r;
    [branch] if (_V5_FrameBlend>0)
        d=lerp(d,SAMPLE_TEXTURE3D_LOD(_V5_DensityTexB,sampler_LinearClamp,uvw,0).r,_V5_FrameBlend);
    return max(d,0);
}
float Density5(float3 localPos)
{
    float3 boxUV=localPos+0.5;
    if (any(boxUV<0) || any(boxUV>1)) return 0;
    float3 edge=min(boxUV,1-boxUV);
    float fade=_V5_DensityParams.w>0 ? saturate(min(edge.x,min(edge.y,edge.z))/_V5_DensityParams.w) : 1;
    float3 uvw=boxUV;
    if (_V5_AxisOptions.x>0.5) uvw=uvw.xzy;
    uvw=lerp(uvw,1-uvw,_V5_AxisOptions.yzw);
    float raw=StormReadDensity5(uvw);
    float band=max(_V5_StormContrast.z,0.0001);
    // A low-density mask, not a geometric signed-distance surface.
    float edgeMask=1-smoothstep(0,band,raw);
    float3 warp=0;
    [branch] if (_V5_EdgeShape.x>0.5 && _V5_EdgeDetail.x>0)
        warp+=StormWarp5(uvw,_V5_EdgeShape,_V5_EdgeDetail,_V5_EdgeOffset)*edgeMask;
    [branch] if (_V5_InteriorShape.x>0.5 && _V5_InteriorDetail.x>0)
        warp+=StormWarp5(uvw,_V5_InteriorShape,_V5_InteriorDetail,_V5_InteriorOffset)*(1-edgeMask);
    [branch] if (any(warp!=0)) raw=StormReadDensity5(uvw+warp);
    edgeMask=1-smoothstep(0,band,raw);
    [branch] if (_V5_EdgeShape.x>0.5 && _V5_EdgeShape.w>0)
        raw*=saturate(1-_V5_EdgeShape.w*StormLayer5(uvw,_V5_EdgeShape,_V5_EdgeDetail,_V5_EdgeOffset)*edgeMask);
    [branch] if (_V5_InteriorShape.x>0.5 && _V5_InteriorShape.w>0)
        raw*=max(0,1+_V5_InteriorShape.w*(2*StormLayer5(uvw,_V5_InteriorShape,_V5_InteriorDetail,_V5_InteriorOffset)-1)*(1-edgeMask));
    float value=max(raw-_V5_DensityParams.y,0);
    [branch] if (value>0 && abs(_V5_StormContrast.x-1)>0.00001)
    {
        float pivot=max(_V5_StormContrast.y,0.0001);
        value=pivot*exp2(clamp(log2(max(value/pivot,1e-20))*_V5_StormContrast.x,-80,80));
    }
    return value*_V5_DensityParams.x*fade;
}


float4x4 _V5_VolumeWorldToLocal;
float4 _V5_MarchParams, _V5_AdaptiveMarch, _V5_SunDirection, _V5_SunRadiance, _V5_DustColor, _V5_AmbientRadiance;
float _V5_Anisotropy, _V5_UseLightCache, _V5_StormSkipEmpty, _V5_StormOccupancySize;
TEXTURE3D(_V5_SunLightCache); TEXTURE3D(_V5_StormOccupancy);
            float EmptyDistance5(float3 p, float3 dir)
            {
                float3 uv=p+0.5, d=dir;
                if (_V5_AxisOptions.x>0.5) { uv=uv.xzy; d=d.xzy; }
                uv=lerp(uv,1-uv,_V5_AxisOptions.yzw);
                d*=1-2*_V5_AxisOptions.yzw;
                int size=(int)_V5_StormOccupancySize;
                int3 cell=clamp((int3)floor(uv*size),0,size-1);
                if (_V5_StormOccupancy.Load(int4(cell,0)).r>0) return 0;
                float result=1e20;
                [unroll] for(int axis=0;axis<3;axis++)
                {
                    if(abs(d[axis])>1e-8)
                    {
                        float face=(cell[axis]+(d[axis]>0 ? 1:0))/(float)size;
                        result=min(result,max(0,(face-uv[axis])/d[axis]));
                    }
                }
                return result;
            }

            float DirectSunTransmission5(float3 p, float3 lightLocal)
            {
                float enter, leave;
                if (!IntersectBox(p, lightLocal, enter, leave)) return 1;
                int count = (int)_V5_MarchParams.y;
                float ds = leave / count;
                float opticalDepth = 0;
                [loop] for (int j = 0; j < count; j++)
                {
                    opticalDepth += Density5(p + lightLocal * ((j + 0.5) * ds)) * _V5_DensityParams.z * ds;
                    if (opticalDepth > 10) break;
                }
                return exp(-opticalDepth);
            }

            float SunTransmission5(float3 p, float3 lightLocal)
            {
                [branch] if (_V5_UseLightCache > 0.5)
                    return saturate(SAMPLE_TEXTURE3D_LOD(_V5_SunLightCache, sampler_LinearClamp, saturate(p + 0.5), 0).r);
                return DirectSunTransmission5(p, lightLocal);
            }



TEXTURE3D(_V6_DensityTex);

TEXTURE3D(_V6_DensityTexB);

float _V6_FrameBlend;
TEXTURE3D(_V6_StormEdgeNoiseTex); 
TEXTURE3D(_V6_StormInteriorNoiseTex); 
float _V6_StormBakedNoise;

float4 _V6_AxisOptions, _V6_DensityParams;
float4 _V6_StormContrast;
float4 _V6_EdgeShape, _V6_EdgeDetail, _V6_EdgeOffset;
float4 _V6_InteriorShape, _V6_InteriorDetail, _V6_InteriorOffset;

float StormHash6(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}
float StormValue6(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f*f*(3-2*f);
    float a = lerp(StormHash6(i), StormHash6(i+float3(1,0,0)), f.x);
    float b = lerp(StormHash6(i+float3(0,1,0)), StormHash6(i+float3(1,1,0)), f.x);
    float c = lerp(StormHash6(i+float3(0,0,1)), StormHash6(i+float3(1,0,1)), f.x);
    float d = lerp(StormHash6(i+float3(0,1,1)), StormHash6(i+float3(1,1,1)), f.x);
    return lerp(lerp(a,b,f.y),lerp(c,d,f.y),f.z);
}
float StormCellular6(float3 p)
{
    float3 cell = floor(p), f = frac(p);
    float nearest = 10;
    [loop] for (int z=-1; z<=1; z++)
    [loop] for (int y=-1; y<=1; y++)
    [loop] for (int x=-1; x<=1; x++)
    {
        float3 o = float3(x,y,z), q = cell+o;
        // Jitter restricted to the middle of each cell for a stable local neighbourhood.
        float3 feature = 0.25 + 0.5*float3(StormHash6(q),StormHash6(q+19.17),StormHash6(q+47.53));
        float3 delta = o + feature - f;
        nearest = min(nearest,dot(delta,delta));
    }
    return saturate(1-sqrt(nearest));
}
float StormNoise6(float3 p, int kind, int octaves)
{
    [branch] if (kind == 3) return StormCellular6(p);
    [branch] if (kind == 0) return StormValue6(p);
    float sum=0, weight=0, amplitude=1;
    [loop] for (int i=0; i<octaves; i++)
    {
        float n=StormValue6(p);
        if (kind == 2) n=1-abs(2*n-1);
        sum+=n*amplitude; weight+=amplitude;
        p=p*2.03+float3(17.1,9.2,3.7); amplitude*=0.5;
    }
    return sum/max(weight,0.0001);
}
float StormLayer6(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 q=p*shape.z+offset.xyz+detail.z*float3(0.173,0.317,0.619);
    [branch] if (_V6_StormBakedNoise > 0.5)
    {
        // The layer's shape is passed unchanged by every caller.
        if (all(shape == _V6_EdgeShape) && all(detail == _V6_EdgeDetail) && all(offset == _V6_EdgeOffset))
            return SAMPLE_TEXTURE3D_LOD(_V6_StormEdgeNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
        return SAMPLE_TEXTURE3D_LOD(_V6_StormInteriorNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
    }
    return StormNoise6(q,(int)shape.y,(int)detail.y);
}
float3 StormWarp6(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 n=float3(StormLayer6(p,shape,detail,offset),
        StormLayer6(p+float3(3.13,7.71,1.17),shape,detail,offset),
        StormLayer6(p+float3(9.43,2.37,5.31),shape,detail,offset));
    return (2*n-1)*detail.x;
}
float StormReadDensity6(float3 uvw)
{
    if (any(uvw<0) || any(uvw>1)) return 0;
    float d=SAMPLE_TEXTURE3D_LOD(_V6_DensityTex,sampler_LinearClamp,uvw,0).r;
    [branch] if (_V6_FrameBlend>0)
        d=lerp(d,SAMPLE_TEXTURE3D_LOD(_V6_DensityTexB,sampler_LinearClamp,uvw,0).r,_V6_FrameBlend);
    return max(d,0);
}
float Density6(float3 localPos)
{
    float3 boxUV=localPos+0.5;
    if (any(boxUV<0) || any(boxUV>1)) return 0;
    float3 edge=min(boxUV,1-boxUV);
    float fade=_V6_DensityParams.w>0 ? saturate(min(edge.x,min(edge.y,edge.z))/_V6_DensityParams.w) : 1;
    float3 uvw=boxUV;
    if (_V6_AxisOptions.x>0.5) uvw=uvw.xzy;
    uvw=lerp(uvw,1-uvw,_V6_AxisOptions.yzw);
    float raw=StormReadDensity6(uvw);
    float band=max(_V6_StormContrast.z,0.0001);
    // A low-density mask, not a geometric signed-distance surface.
    float edgeMask=1-smoothstep(0,band,raw);
    float3 warp=0;
    [branch] if (_V6_EdgeShape.x>0.5 && _V6_EdgeDetail.x>0)
        warp+=StormWarp6(uvw,_V6_EdgeShape,_V6_EdgeDetail,_V6_EdgeOffset)*edgeMask;
    [branch] if (_V6_InteriorShape.x>0.5 && _V6_InteriorDetail.x>0)
        warp+=StormWarp6(uvw,_V6_InteriorShape,_V6_InteriorDetail,_V6_InteriorOffset)*(1-edgeMask);
    [branch] if (any(warp!=0)) raw=StormReadDensity6(uvw+warp);
    edgeMask=1-smoothstep(0,band,raw);
    [branch] if (_V6_EdgeShape.x>0.5 && _V6_EdgeShape.w>0)
        raw*=saturate(1-_V6_EdgeShape.w*StormLayer6(uvw,_V6_EdgeShape,_V6_EdgeDetail,_V6_EdgeOffset)*edgeMask);
    [branch] if (_V6_InteriorShape.x>0.5 && _V6_InteriorShape.w>0)
        raw*=max(0,1+_V6_InteriorShape.w*(2*StormLayer6(uvw,_V6_InteriorShape,_V6_InteriorDetail,_V6_InteriorOffset)-1)*(1-edgeMask));
    float value=max(raw-_V6_DensityParams.y,0);
    [branch] if (value>0 && abs(_V6_StormContrast.x-1)>0.00001)
    {
        float pivot=max(_V6_StormContrast.y,0.0001);
        value=pivot*exp2(clamp(log2(max(value/pivot,1e-20))*_V6_StormContrast.x,-80,80));
    }
    return value*_V6_DensityParams.x*fade;
}


float4x4 _V6_VolumeWorldToLocal;
float4 _V6_MarchParams, _V6_AdaptiveMarch, _V6_SunDirection, _V6_SunRadiance, _V6_DustColor, _V6_AmbientRadiance;
float _V6_Anisotropy, _V6_UseLightCache, _V6_StormSkipEmpty, _V6_StormOccupancySize;
TEXTURE3D(_V6_SunLightCache); TEXTURE3D(_V6_StormOccupancy);
            float EmptyDistance6(float3 p, float3 dir)
            {
                float3 uv=p+0.5, d=dir;
                if (_V6_AxisOptions.x>0.5) { uv=uv.xzy; d=d.xzy; }
                uv=lerp(uv,1-uv,_V6_AxisOptions.yzw);
                d*=1-2*_V6_AxisOptions.yzw;
                int size=(int)_V6_StormOccupancySize;
                int3 cell=clamp((int3)floor(uv*size),0,size-1);
                if (_V6_StormOccupancy.Load(int4(cell,0)).r>0) return 0;
                float result=1e20;
                [unroll] for(int axis=0;axis<3;axis++)
                {
                    if(abs(d[axis])>1e-8)
                    {
                        float face=(cell[axis]+(d[axis]>0 ? 1:0))/(float)size;
                        result=min(result,max(0,(face-uv[axis])/d[axis]));
                    }
                }
                return result;
            }

            float DirectSunTransmission6(float3 p, float3 lightLocal)
            {
                float enter, leave;
                if (!IntersectBox(p, lightLocal, enter, leave)) return 1;
                int count = (int)_V6_MarchParams.y;
                float ds = leave / count;
                float opticalDepth = 0;
                [loop] for (int j = 0; j < count; j++)
                {
                    opticalDepth += Density6(p + lightLocal * ((j + 0.5) * ds)) * _V6_DensityParams.z * ds;
                    if (opticalDepth > 10) break;
                }
                return exp(-opticalDepth);
            }

            float SunTransmission6(float3 p, float3 lightLocal)
            {
                [branch] if (_V6_UseLightCache > 0.5)
                    return saturate(SAMPLE_TEXTURE3D_LOD(_V6_SunLightCache, sampler_LinearClamp, saturate(p + 0.5), 0).r);
                return DirectSunTransmission6(p, lightLocal);
            }



TEXTURE3D(_V7_DensityTex);

TEXTURE3D(_V7_DensityTexB);

float _V7_FrameBlend;
TEXTURE3D(_V7_StormEdgeNoiseTex); 
TEXTURE3D(_V7_StormInteriorNoiseTex); 
float _V7_StormBakedNoise;

float4 _V7_AxisOptions, _V7_DensityParams;
float4 _V7_StormContrast;
float4 _V7_EdgeShape, _V7_EdgeDetail, _V7_EdgeOffset;
float4 _V7_InteriorShape, _V7_InteriorDetail, _V7_InteriorOffset;

float StormHash7(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}
float StormValue7(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f*f*(3-2*f);
    float a = lerp(StormHash7(i), StormHash7(i+float3(1,0,0)), f.x);
    float b = lerp(StormHash7(i+float3(0,1,0)), StormHash7(i+float3(1,1,0)), f.x);
    float c = lerp(StormHash7(i+float3(0,0,1)), StormHash7(i+float3(1,0,1)), f.x);
    float d = lerp(StormHash7(i+float3(0,1,1)), StormHash7(i+float3(1,1,1)), f.x);
    return lerp(lerp(a,b,f.y),lerp(c,d,f.y),f.z);
}
float StormCellular7(float3 p)
{
    float3 cell = floor(p), f = frac(p);
    float nearest = 10;
    [loop] for (int z=-1; z<=1; z++)
    [loop] for (int y=-1; y<=1; y++)
    [loop] for (int x=-1; x<=1; x++)
    {
        float3 o = float3(x,y,z), q = cell+o;
        // Jitter restricted to the middle of each cell for a stable local neighbourhood.
        float3 feature = 0.25 + 0.5*float3(StormHash7(q),StormHash7(q+19.17),StormHash7(q+47.53));
        float3 delta = o + feature - f;
        nearest = min(nearest,dot(delta,delta));
    }
    return saturate(1-sqrt(nearest));
}
float StormNoise7(float3 p, int kind, int octaves)
{
    [branch] if (kind == 3) return StormCellular7(p);
    [branch] if (kind == 0) return StormValue7(p);
    float sum=0, weight=0, amplitude=1;
    [loop] for (int i=0; i<octaves; i++)
    {
        float n=StormValue7(p);
        if (kind == 2) n=1-abs(2*n-1);
        sum+=n*amplitude; weight+=amplitude;
        p=p*2.03+float3(17.1,9.2,3.7); amplitude*=0.5;
    }
    return sum/max(weight,0.0001);
}
float StormLayer7(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 q=p*shape.z+offset.xyz+detail.z*float3(0.173,0.317,0.619);
    [branch] if (_V7_StormBakedNoise > 0.5)
    {
        // The layer's shape is passed unchanged by every caller.
        if (all(shape == _V7_EdgeShape) && all(detail == _V7_EdgeDetail) && all(offset == _V7_EdgeOffset))
            return SAMPLE_TEXTURE3D_LOD(_V7_StormEdgeNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
        return SAMPLE_TEXTURE3D_LOD(_V7_StormInteriorNoiseTex,sampler_LinearRepeat,q/8.0,0).r;
    }
    return StormNoise7(q,(int)shape.y,(int)detail.y);
}
float3 StormWarp7(float3 p,float4 shape,float4 detail,float4 offset)
{
    float3 n=float3(StormLayer7(p,shape,detail,offset),
        StormLayer7(p+float3(3.13,7.71,1.17),shape,detail,offset),
        StormLayer7(p+float3(9.43,2.37,5.31),shape,detail,offset));
    return (2*n-1)*detail.x;
}
float StormReadDensity7(float3 uvw)
{
    if (any(uvw<0) || any(uvw>1)) return 0;
    float d=SAMPLE_TEXTURE3D_LOD(_V7_DensityTex,sampler_LinearClamp,uvw,0).r;
    [branch] if (_V7_FrameBlend>0)
        d=lerp(d,SAMPLE_TEXTURE3D_LOD(_V7_DensityTexB,sampler_LinearClamp,uvw,0).r,_V7_FrameBlend);
    return max(d,0);
}
float Density7(float3 localPos)
{
    float3 boxUV=localPos+0.5;
    if (any(boxUV<0) || any(boxUV>1)) return 0;
    float3 edge=min(boxUV,1-boxUV);
    float fade=_V7_DensityParams.w>0 ? saturate(min(edge.x,min(edge.y,edge.z))/_V7_DensityParams.w) : 1;
    float3 uvw=boxUV;
    if (_V7_AxisOptions.x>0.5) uvw=uvw.xzy;
    uvw=lerp(uvw,1-uvw,_V7_AxisOptions.yzw);
    float raw=StormReadDensity7(uvw);
    float band=max(_V7_StormContrast.z,0.0001);
    // A low-density mask, not a geometric signed-distance surface.
    float edgeMask=1-smoothstep(0,band,raw);
    float3 warp=0;
    [branch] if (_V7_EdgeShape.x>0.5 && _V7_EdgeDetail.x>0)
        warp+=StormWarp7(uvw,_V7_EdgeShape,_V7_EdgeDetail,_V7_EdgeOffset)*edgeMask;
    [branch] if (_V7_InteriorShape.x>0.5 && _V7_InteriorDetail.x>0)
        warp+=StormWarp7(uvw,_V7_InteriorShape,_V7_InteriorDetail,_V7_InteriorOffset)*(1-edgeMask);
    [branch] if (any(warp!=0)) raw=StormReadDensity7(uvw+warp);
    edgeMask=1-smoothstep(0,band,raw);
    [branch] if (_V7_EdgeShape.x>0.5 && _V7_EdgeShape.w>0)
        raw*=saturate(1-_V7_EdgeShape.w*StormLayer7(uvw,_V7_EdgeShape,_V7_EdgeDetail,_V7_EdgeOffset)*edgeMask);
    [branch] if (_V7_InteriorShape.x>0.5 && _V7_InteriorShape.w>0)
        raw*=max(0,1+_V7_InteriorShape.w*(2*StormLayer7(uvw,_V7_InteriorShape,_V7_InteriorDetail,_V7_InteriorOffset)-1)*(1-edgeMask));
    float value=max(raw-_V7_DensityParams.y,0);
    [branch] if (value>0 && abs(_V7_StormContrast.x-1)>0.00001)
    {
        float pivot=max(_V7_StormContrast.y,0.0001);
        value=pivot*exp2(clamp(log2(max(value/pivot,1e-20))*_V7_StormContrast.x,-80,80));
    }
    return value*_V7_DensityParams.x*fade;
}


float4x4 _V7_VolumeWorldToLocal;
float4 _V7_MarchParams, _V7_AdaptiveMarch, _V7_SunDirection, _V7_SunRadiance, _V7_DustColor, _V7_AmbientRadiance;
float _V7_Anisotropy, _V7_UseLightCache, _V7_StormSkipEmpty, _V7_StormOccupancySize;
TEXTURE3D(_V7_SunLightCache); TEXTURE3D(_V7_StormOccupancy);
            float EmptyDistance7(float3 p, float3 dir)
            {
                float3 uv=p+0.5, d=dir;
                if (_V7_AxisOptions.x>0.5) { uv=uv.xzy; d=d.xzy; }
                uv=lerp(uv,1-uv,_V7_AxisOptions.yzw);
                d*=1-2*_V7_AxisOptions.yzw;
                int size=(int)_V7_StormOccupancySize;
                int3 cell=clamp((int3)floor(uv*size),0,size-1);
                if (_V7_StormOccupancy.Load(int4(cell,0)).r>0) return 0;
                float result=1e20;
                [unroll] for(int axis=0;axis<3;axis++)
                {
                    if(abs(d[axis])>1e-8)
                    {
                        float face=(cell[axis]+(d[axis]>0 ? 1:0))/(float)size;
                        result=min(result,max(0,(face-uv[axis])/d[axis]));
                    }
                }
                return result;
            }

            float DirectSunTransmission7(float3 p, float3 lightLocal)
            {
                float enter, leave;
                if (!IntersectBox(p, lightLocal, enter, leave)) return 1;
                int count = (int)_V7_MarchParams.y;
                float ds = leave / count;
                float opticalDepth = 0;
                [loop] for (int j = 0; j < count; j++)
                {
                    opticalDepth += Density7(p + lightLocal * ((j + 0.5) * ds)) * _V7_DensityParams.z * ds;
                    if (opticalDepth > 10) break;
                }
                return exp(-opticalDepth);
            }

            float SunTransmission7(float3 p, float3 lightLocal)
            {
                [branch] if (_V7_UseLightCache > 0.5)
                    return saturate(SAMPLE_TEXTURE3D_LOD(_V7_SunLightCache, sampler_LinearClamp, saturate(p + 0.5), 0).r);
                return DirectSunTransmission7(p, lightLocal);
            }


float4 RaymarchAtPixel(float2 fullPixel)
{
 float rawDepth=LoadCameraDepth(fullPixel);
 PositionInputs input=GetPositionInput(fullPixel,_ScreenSize.zw,rawDepth,UNITY_MATRIX_I_VP,UNITY_MATRIX_V);
 float3 ray=-GetWorldSpaceNormalizeViewDir(input.positionWS);
 float3 camera=GetAbsolutePositionWS(GetCurrentViewPosition());
 float sceneEnd=1e20;
 if(rawDepth!=UNITY_RAW_FAR_CLIP_VALUE) sceneEnd=max(0,dot(GetAbsolutePositionWS(input.positionWS)-camera,ray));
 float en[8], ex[8], baseStep[8];
 float3 origin[8], direction[8];
 float start=1e20, finish=0, stopT=1;

 en[0]=1e20; ex[0]=-1; baseStep[0]=1e20; origin[0]=0; direction[0]=0;
 if(_VolumeCount>0)
 {
  origin[0]=mul(_V0_VolumeWorldToLocal,float4(camera,1)).xyz;
  direction[0]=mul((float3x3)_V0_VolumeWorldToLocal,ray);
  float a,b;
  if(IntersectBox(origin[0],direction[0],a,b))
  {
   a=max(a,0); b=min(b,sceneEnd);
   if(b>a) { en[0]=a; ex[0]=b; baseStep[0]=(b-a)/max(_V0_MarchParams.x,16); start=min(start,a); finish=max(finish,b); }
  }
  stopT=min(stopT,_V0_MarchParams.w);
 }

 en[1]=1e20; ex[1]=-1; baseStep[1]=1e20; origin[1]=0; direction[1]=0;
 if(_VolumeCount>1)
 {
  origin[1]=mul(_V1_VolumeWorldToLocal,float4(camera,1)).xyz;
  direction[1]=mul((float3x3)_V1_VolumeWorldToLocal,ray);
  float a,b;
  if(IntersectBox(origin[1],direction[1],a,b))
  {
   a=max(a,0); b=min(b,sceneEnd);
   if(b>a) { en[1]=a; ex[1]=b; baseStep[1]=(b-a)/max(_V1_MarchParams.x,16); start=min(start,a); finish=max(finish,b); }
  }
  stopT=min(stopT,_V1_MarchParams.w);
 }

 en[2]=1e20; ex[2]=-1; baseStep[2]=1e20; origin[2]=0; direction[2]=0;
 if(_VolumeCount>2)
 {
  origin[2]=mul(_V2_VolumeWorldToLocal,float4(camera,1)).xyz;
  direction[2]=mul((float3x3)_V2_VolumeWorldToLocal,ray);
  float a,b;
  if(IntersectBox(origin[2],direction[2],a,b))
  {
   a=max(a,0); b=min(b,sceneEnd);
   if(b>a) { en[2]=a; ex[2]=b; baseStep[2]=(b-a)/max(_V2_MarchParams.x,16); start=min(start,a); finish=max(finish,b); }
  }
  stopT=min(stopT,_V2_MarchParams.w);
 }

 en[3]=1e20; ex[3]=-1; baseStep[3]=1e20; origin[3]=0; direction[3]=0;
 if(_VolumeCount>3)
 {
  origin[3]=mul(_V3_VolumeWorldToLocal,float4(camera,1)).xyz;
  direction[3]=mul((float3x3)_V3_VolumeWorldToLocal,ray);
  float a,b;
  if(IntersectBox(origin[3],direction[3],a,b))
  {
   a=max(a,0); b=min(b,sceneEnd);
   if(b>a) { en[3]=a; ex[3]=b; baseStep[3]=(b-a)/max(_V3_MarchParams.x,16); start=min(start,a); finish=max(finish,b); }
  }
  stopT=min(stopT,_V3_MarchParams.w);
 }

 en[4]=1e20; ex[4]=-1; baseStep[4]=1e20; origin[4]=0; direction[4]=0;
 if(_VolumeCount>4)
 {
  origin[4]=mul(_V4_VolumeWorldToLocal,float4(camera,1)).xyz;
  direction[4]=mul((float3x3)_V4_VolumeWorldToLocal,ray);
  float a,b;
  if(IntersectBox(origin[4],direction[4],a,b))
  {
   a=max(a,0); b=min(b,sceneEnd);
   if(b>a) { en[4]=a; ex[4]=b; baseStep[4]=(b-a)/max(_V4_MarchParams.x,16); start=min(start,a); finish=max(finish,b); }
  }
  stopT=min(stopT,_V4_MarchParams.w);
 }

 en[5]=1e20; ex[5]=-1; baseStep[5]=1e20; origin[5]=0; direction[5]=0;
 if(_VolumeCount>5)
 {
  origin[5]=mul(_V5_VolumeWorldToLocal,float4(camera,1)).xyz;
  direction[5]=mul((float3x3)_V5_VolumeWorldToLocal,ray);
  float a,b;
  if(IntersectBox(origin[5],direction[5],a,b))
  {
   a=max(a,0); b=min(b,sceneEnd);
   if(b>a) { en[5]=a; ex[5]=b; baseStep[5]=(b-a)/max(_V5_MarchParams.x,16); start=min(start,a); finish=max(finish,b); }
  }
  stopT=min(stopT,_V5_MarchParams.w);
 }

 en[6]=1e20; ex[6]=-1; baseStep[6]=1e20; origin[6]=0; direction[6]=0;
 if(_VolumeCount>6)
 {
  origin[6]=mul(_V6_VolumeWorldToLocal,float4(camera,1)).xyz;
  direction[6]=mul((float3x3)_V6_VolumeWorldToLocal,ray);
  float a,b;
  if(IntersectBox(origin[6],direction[6],a,b))
  {
   a=max(a,0); b=min(b,sceneEnd);
   if(b>a) { en[6]=a; ex[6]=b; baseStep[6]=(b-a)/max(_V6_MarchParams.x,16); start=min(start,a); finish=max(finish,b); }
  }
  stopT=min(stopT,_V6_MarchParams.w);
 }

 en[7]=1e20; ex[7]=-1; baseStep[7]=1e20; origin[7]=0; direction[7]=0;
 if(_VolumeCount>7)
 {
  origin[7]=mul(_V7_VolumeWorldToLocal,float4(camera,1)).xyz;
  direction[7]=mul((float3x3)_V7_VolumeWorldToLocal,ray);
  float a,b;
  if(IntersectBox(origin[7],direction[7],a,b))
  {
   a=max(a,0); b=min(b,sceneEnd);
   if(b>a) { en[7]=a; ex[7]=b; baseStep[7]=(b-a)/max(_V7_MarchParams.x,16); start=min(start,a); finish=max(finish,b); }
  }
  stopT=min(stopT,_V7_MarchParams.w);
 }

 if(finish<=start) return 0;
 float t=start,T=1; float3 radiance=0;
 float hash=frac(52.9829189*frac(dot(fullPixel,float2(0.06711056,0.00583715))));
 // Up to 8*256 base intervals plus interval boundary splits.
 [loop] for(int iter=0;iter<4096 && t<finish;iter++)
 {
  float nextBoundary=finish, ds=1e20, emptyAdvance=1e20, jitter=0;
  bool active=false, allEmpty=true;

  if(en[0]>t) nextBoundary=min(nextBoundary,en[0]);
  if(t>=en[0] && t<ex[0])
  {
   active=true; nextBoundary=min(nextBoundary,ex[0]);
   float factor=_V0_AdaptiveMarch.x>0.5 ? lerp(1,_V0_AdaptiveMarch.y,smoothstep(_V0_AdaptiveMarch.z,_V0_AdaptiveMarch.w,1-T)) : 1;
   ds=min(ds,baseStep[0]*factor); jitter=max(jitter,_V0_MarchParams.z);
   float e=_V0_StormSkipEmpty>0.5 ? EmptyDistance0(origin[0]+direction[0]*t,direction[0]) : 0;
   if(e<=0) allEmpty=false; else emptyAdvance=min(emptyAdvance,e);
  }

  if(en[1]>t) nextBoundary=min(nextBoundary,en[1]);
  if(t>=en[1] && t<ex[1])
  {
   active=true; nextBoundary=min(nextBoundary,ex[1]);
   float factor=_V1_AdaptiveMarch.x>0.5 ? lerp(1,_V1_AdaptiveMarch.y,smoothstep(_V1_AdaptiveMarch.z,_V1_AdaptiveMarch.w,1-T)) : 1;
   ds=min(ds,baseStep[1]*factor); jitter=max(jitter,_V1_MarchParams.z);
   float e=_V1_StormSkipEmpty>0.5 ? EmptyDistance1(origin[1]+direction[1]*t,direction[1]) : 0;
   if(e<=0) allEmpty=false; else emptyAdvance=min(emptyAdvance,e);
  }

  if(en[2]>t) nextBoundary=min(nextBoundary,en[2]);
  if(t>=en[2] && t<ex[2])
  {
   active=true; nextBoundary=min(nextBoundary,ex[2]);
   float factor=_V2_AdaptiveMarch.x>0.5 ? lerp(1,_V2_AdaptiveMarch.y,smoothstep(_V2_AdaptiveMarch.z,_V2_AdaptiveMarch.w,1-T)) : 1;
   ds=min(ds,baseStep[2]*factor); jitter=max(jitter,_V2_MarchParams.z);
   float e=_V2_StormSkipEmpty>0.5 ? EmptyDistance2(origin[2]+direction[2]*t,direction[2]) : 0;
   if(e<=0) allEmpty=false; else emptyAdvance=min(emptyAdvance,e);
  }

  if(en[3]>t) nextBoundary=min(nextBoundary,en[3]);
  if(t>=en[3] && t<ex[3])
  {
   active=true; nextBoundary=min(nextBoundary,ex[3]);
   float factor=_V3_AdaptiveMarch.x>0.5 ? lerp(1,_V3_AdaptiveMarch.y,smoothstep(_V3_AdaptiveMarch.z,_V3_AdaptiveMarch.w,1-T)) : 1;
   ds=min(ds,baseStep[3]*factor); jitter=max(jitter,_V3_MarchParams.z);
   float e=_V3_StormSkipEmpty>0.5 ? EmptyDistance3(origin[3]+direction[3]*t,direction[3]) : 0;
   if(e<=0) allEmpty=false; else emptyAdvance=min(emptyAdvance,e);
  }

  if(en[4]>t) nextBoundary=min(nextBoundary,en[4]);
  if(t>=en[4] && t<ex[4])
  {
   active=true; nextBoundary=min(nextBoundary,ex[4]);
   float factor=_V4_AdaptiveMarch.x>0.5 ? lerp(1,_V4_AdaptiveMarch.y,smoothstep(_V4_AdaptiveMarch.z,_V4_AdaptiveMarch.w,1-T)) : 1;
   ds=min(ds,baseStep[4]*factor); jitter=max(jitter,_V4_MarchParams.z);
   float e=_V4_StormSkipEmpty>0.5 ? EmptyDistance4(origin[4]+direction[4]*t,direction[4]) : 0;
   if(e<=0) allEmpty=false; else emptyAdvance=min(emptyAdvance,e);
  }

  if(en[5]>t) nextBoundary=min(nextBoundary,en[5]);
  if(t>=en[5] && t<ex[5])
  {
   active=true; nextBoundary=min(nextBoundary,ex[5]);
   float factor=_V5_AdaptiveMarch.x>0.5 ? lerp(1,_V5_AdaptiveMarch.y,smoothstep(_V5_AdaptiveMarch.z,_V5_AdaptiveMarch.w,1-T)) : 1;
   ds=min(ds,baseStep[5]*factor); jitter=max(jitter,_V5_MarchParams.z);
   float e=_V5_StormSkipEmpty>0.5 ? EmptyDistance5(origin[5]+direction[5]*t,direction[5]) : 0;
   if(e<=0) allEmpty=false; else emptyAdvance=min(emptyAdvance,e);
  }

  if(en[6]>t) nextBoundary=min(nextBoundary,en[6]);
  if(t>=en[6] && t<ex[6])
  {
   active=true; nextBoundary=min(nextBoundary,ex[6]);
   float factor=_V6_AdaptiveMarch.x>0.5 ? lerp(1,_V6_AdaptiveMarch.y,smoothstep(_V6_AdaptiveMarch.z,_V6_AdaptiveMarch.w,1-T)) : 1;
   ds=min(ds,baseStep[6]*factor); jitter=max(jitter,_V6_MarchParams.z);
   float e=_V6_StormSkipEmpty>0.5 ? EmptyDistance6(origin[6]+direction[6]*t,direction[6]) : 0;
   if(e<=0) allEmpty=false; else emptyAdvance=min(emptyAdvance,e);
  }

  if(en[7]>t) nextBoundary=min(nextBoundary,en[7]);
  if(t>=en[7] && t<ex[7])
  {
   active=true; nextBoundary=min(nextBoundary,ex[7]);
   float factor=_V7_AdaptiveMarch.x>0.5 ? lerp(1,_V7_AdaptiveMarch.y,smoothstep(_V7_AdaptiveMarch.z,_V7_AdaptiveMarch.w,1-T)) : 1;
   ds=min(ds,baseStep[7]*factor); jitter=max(jitter,_V7_MarchParams.z);
   float e=_V7_StormSkipEmpty>0.5 ? EmptyDistance7(origin[7]+direction[7]*t,direction[7]) : 0;
   if(e<=0) allEmpty=false; else emptyAdvance=min(emptyAdvance,e);
  }

  if(!active) { t=nextBoundary; continue; }
  ds=min(ds,nextBoundary-t);
  if(allEmpty) ds=min(nextBoundary-t,emptyAdvance);
  // Snap boundary hits exactly; rounding must not terminate the entire ray.
  float nextT = ds >= nextBoundary-t ? nextBoundary : t+ds;
  if(nextT<=t)
  {
   // t is nonnegative: next representable float avoids a stalled boundary.
   t=asfloat(asuint(t)+1u);
   continue;
  }
  if(allEmpty) { t=nextT; continue; }
  float sampleT=t+lerp(0.5,hash,jitter)*ds;
  float sigma=0; float3 weightedLight=0;

  if(sampleT>=en[0] && sampleT<ex[0])
  {
   float3 p=origin[0]+direction[0]*sampleT;
   float d=Density0(p);
   float extinction=max(0,d*_V0_DensityParams.z);
   if(extinction>0)
   {
    float3 sun=normalize(_V0_SunDirection.xyz);
    float3 sunLocal=mul((float3x3)_V0_VolumeWorldToLocal,sun);
    float g=_V0_Anisotropy,mu=dot(ray,sun);
    float phase=(1-g*g)/pow(max(1+g*g-2*g*mu,0.001),1.5);
    float shadow=SunTransmission0(p,sunLocal);
    float3 lit=_V0_DustColor.rgb*(_V0_AmbientRadiance.rgb+_V0_SunRadiance.rgb*shadow*phase);
    sigma+=extinction; weightedLight+=extinction*lit;
   }
  }

  if(sampleT>=en[1] && sampleT<ex[1])
  {
   float3 p=origin[1]+direction[1]*sampleT;
   float d=Density1(p);
   float extinction=max(0,d*_V1_DensityParams.z);
   if(extinction>0)
   {
    float3 sun=normalize(_V1_SunDirection.xyz);
    float3 sunLocal=mul((float3x3)_V1_VolumeWorldToLocal,sun);
    float g=_V1_Anisotropy,mu=dot(ray,sun);
    float phase=(1-g*g)/pow(max(1+g*g-2*g*mu,0.001),1.5);
    float shadow=SunTransmission1(p,sunLocal);
    float3 lit=_V1_DustColor.rgb*(_V1_AmbientRadiance.rgb+_V1_SunRadiance.rgb*shadow*phase);
    sigma+=extinction; weightedLight+=extinction*lit;
   }
  }

  if(sampleT>=en[2] && sampleT<ex[2])
  {
   float3 p=origin[2]+direction[2]*sampleT;
   float d=Density2(p);
   float extinction=max(0,d*_V2_DensityParams.z);
   if(extinction>0)
   {
    float3 sun=normalize(_V2_SunDirection.xyz);
    float3 sunLocal=mul((float3x3)_V2_VolumeWorldToLocal,sun);
    float g=_V2_Anisotropy,mu=dot(ray,sun);
    float phase=(1-g*g)/pow(max(1+g*g-2*g*mu,0.001),1.5);
    float shadow=SunTransmission2(p,sunLocal);
    float3 lit=_V2_DustColor.rgb*(_V2_AmbientRadiance.rgb+_V2_SunRadiance.rgb*shadow*phase);
    sigma+=extinction; weightedLight+=extinction*lit;
   }
  }

  if(sampleT>=en[3] && sampleT<ex[3])
  {
   float3 p=origin[3]+direction[3]*sampleT;
   float d=Density3(p);
   float extinction=max(0,d*_V3_DensityParams.z);
   if(extinction>0)
   {
    float3 sun=normalize(_V3_SunDirection.xyz);
    float3 sunLocal=mul((float3x3)_V3_VolumeWorldToLocal,sun);
    float g=_V3_Anisotropy,mu=dot(ray,sun);
    float phase=(1-g*g)/pow(max(1+g*g-2*g*mu,0.001),1.5);
    float shadow=SunTransmission3(p,sunLocal);
    float3 lit=_V3_DustColor.rgb*(_V3_AmbientRadiance.rgb+_V3_SunRadiance.rgb*shadow*phase);
    sigma+=extinction; weightedLight+=extinction*lit;
   }
  }

  if(sampleT>=en[4] && sampleT<ex[4])
  {
   float3 p=origin[4]+direction[4]*sampleT;
   float d=Density4(p);
   float extinction=max(0,d*_V4_DensityParams.z);
   if(extinction>0)
   {
    float3 sun=normalize(_V4_SunDirection.xyz);
    float3 sunLocal=mul((float3x3)_V4_VolumeWorldToLocal,sun);
    float g=_V4_Anisotropy,mu=dot(ray,sun);
    float phase=(1-g*g)/pow(max(1+g*g-2*g*mu,0.001),1.5);
    float shadow=SunTransmission4(p,sunLocal);
    float3 lit=_V4_DustColor.rgb*(_V4_AmbientRadiance.rgb+_V4_SunRadiance.rgb*shadow*phase);
    sigma+=extinction; weightedLight+=extinction*lit;
   }
  }

  if(sampleT>=en[5] && sampleT<ex[5])
  {
   float3 p=origin[5]+direction[5]*sampleT;
   float d=Density5(p);
   float extinction=max(0,d*_V5_DensityParams.z);
   if(extinction>0)
   {
    float3 sun=normalize(_V5_SunDirection.xyz);
    float3 sunLocal=mul((float3x3)_V5_VolumeWorldToLocal,sun);
    float g=_V5_Anisotropy,mu=dot(ray,sun);
    float phase=(1-g*g)/pow(max(1+g*g-2*g*mu,0.001),1.5);
    float shadow=SunTransmission5(p,sunLocal);
    float3 lit=_V5_DustColor.rgb*(_V5_AmbientRadiance.rgb+_V5_SunRadiance.rgb*shadow*phase);
    sigma+=extinction; weightedLight+=extinction*lit;
   }
  }

  if(sampleT>=en[6] && sampleT<ex[6])
  {
   float3 p=origin[6]+direction[6]*sampleT;
   float d=Density6(p);
   float extinction=max(0,d*_V6_DensityParams.z);
   if(extinction>0)
   {
    float3 sun=normalize(_V6_SunDirection.xyz);
    float3 sunLocal=mul((float3x3)_V6_VolumeWorldToLocal,sun);
    float g=_V6_Anisotropy,mu=dot(ray,sun);
    float phase=(1-g*g)/pow(max(1+g*g-2*g*mu,0.001),1.5);
    float shadow=SunTransmission6(p,sunLocal);
    float3 lit=_V6_DustColor.rgb*(_V6_AmbientRadiance.rgb+_V6_SunRadiance.rgb*shadow*phase);
    sigma+=extinction; weightedLight+=extinction*lit;
   }
  }

  if(sampleT>=en[7] && sampleT<ex[7])
  {
   float3 p=origin[7]+direction[7]*sampleT;
   float d=Density7(p);
   float extinction=max(0,d*_V7_DensityParams.z);
   if(extinction>0)
   {
    float3 sun=normalize(_V7_SunDirection.xyz);
    float3 sunLocal=mul((float3x3)_V7_VolumeWorldToLocal,sun);
    float g=_V7_Anisotropy,mu=dot(ray,sun);
    float phase=(1-g*g)/pow(max(1+g*g-2*g*mu,0.001),1.5);
    float shadow=SunTransmission7(p,sunLocal);
    float3 lit=_V7_DustColor.rgb*(_V7_AmbientRadiance.rgb+_V7_SunRadiance.rgb*shadow*phase);
    sigma+=extinction; weightedLight+=extinction*lit;
   }
  }

  if(sigma>0)
  {
   float alpha=1-exp(-sigma*ds);
   radiance+=T*alpha*weightedLight/sigma; T*=1-alpha;
   if(T<stopT) break;
  }
  t=nextT;
 }
 return float4(radiance*GetCurrentExposureMultiplier(),1-T);
}
            float4 FullScreenPass(Varyings varyings) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
                float2 pixel = varyings.positionCS.xy;
                if (_StormHalfPass > 0.5) pixel = StormSourcePixel(int2(pixel));
                return RaymarchAtPixel(pixel);
            }

            float DepthMismatch(float a, float b)
            {
                bool skyA = a == UNITY_RAW_FAR_CLIP_VALUE;
                bool skyB = b == UNITY_RAW_FAR_CLIP_VALUE;
                if (skyA && skyB) return 0;
                if (skyA != skyB) return 1e6;
                float za = LinearEyeDepth(a, _ZBufferParams);
                float zb = LinearEyeDepth(b, _ZBufferParams);
                return abs(za - zb) / max(min(za, zb), 1.0);
            }

            float4 CompositePass(Varyings varyings) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(varyings);
                float2 fullPixel = varyings.positionCS.xy;
                float targetDepth = LoadCameraDepth(uint2(fullPixel));
                float2 coord = fullPixel * _StormLowSize.xy / _StormFullSize.xy - 0.5;
                int2 basePixel = int2(floor(coord));
                float2 f = frac(coord);
                float4 sum = 0;
                float weightSum = 0;
                float bestError = 1e20;
                float4 closestColor = 0;
                [unroll] for (int y = 0; y < 2; y++)
                {
                    [unroll] for (int x = 0; x < 2; x++)
                    {
                        int2 q = clamp(basePixel + int2(x,y), int2(0,0), int2(_StormLowSize.xy)-1);
                        float4 color = LOAD_TEXTURE2D(_SandstormLowResColor, q);
                        float d = LoadCameraDepth(uint2(StormSourcePixel(q)));
                        float error = DepthMismatch(targetDepth, d);
                        if (error < bestError) { bestError = error; closestColor = color; }
                        float spatial = (x == 0 ? 1-f.x : f.x) * (y == 0 ? 1-f.y : f.y);
                        // Reject depth discontinuities instead of blurring across them.
                        float agreement = saturate(1 - error / max(_StormDepthTolerance, 0.0001));
                        float w = spatial * agreement * agreement;
                        sum += color * w;
                        weightSum += w;
                    }
                }
                if (weightSum > 1e-5) return sum / weightSum;
                // Thin geometry can be absent from all four low-res samples.
                // Re-evaluate only these pixels at full resolution.
                if (_StormRefineEdges > 0.5) return RaymarchAtPixel(fullPixel);
                return closestColor;
            }

        ENDHLSL
        Pass
        {
            Name "Sandstorm"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex Vert
            #pragma fragment FullScreenPass
            #pragma multi_compile_instancing
            ENDHLSL
        }
        Pass
        {
            Name "DepthAwareUpsample"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex Vert
            #pragma fragment CompositePass
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }
    Fallback Off
}
