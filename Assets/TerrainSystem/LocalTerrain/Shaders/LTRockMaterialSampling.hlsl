// Near and baked paths share EXACTLY the same sampling and blend math.
TEXTURE2D(_LTRockColorMap);
TEXTURE2D(_LTRockNormalMap);
TEXTURE2D(_LTRockMaskMap);
SAMPLER(sampler_LTRockColorMap);
SAMPLER(sampler_LTRockNormalMap);
SAMPLER(sampler_LTRockMaskMap);
float2 LTRockOctEncode(float3 n)
{
    n/=max(abs(n.x)+abs(n.y)+abs(n.z),1e-6);
    float2 p=n.xy;
    if(n.z<0)p=(1-abs(p.yx))*float2(p.x>=0?1:-1,p.y>=0?1:-1);
    return p*.5+.5;
}
float3 LTRockOctDecode(float2 p)
{
    p=p*2-1;float3 n=float3(p,1-abs(p.x)-abs(p.y));
    float t=saturate(-n.z);n.xy+=float2(n.x>=0?-t:t,n.y>=0?-t:t);
    return normalize(n);
}
void LTRockNear(float3 p,float3 baseN,float3 tangent,float handedness,float2 sourceUV,float terrainWeight,
    out float3 color,out float3 normalTS,out float ao,out float smoothness,out float metallic)
{
    float w=_LTRockFlags.x>.5?saturate(terrainWeight):1;
    float3 t=normalize(tangent-baseN*dot(baseN,tangent));
    float3 b=cross(baseN,t)*handedness;
    float2 uv=sourceUV*_LTRockUV.xy+_LTRockUV.zw;
    float3 px=0,py=0;float2 ux=0,uy=0;
#if !defined(SHADER_STAGE_RAY_TRACING)
    px=ddx(p);py=ddy(p);ux=ddx(uv);uy=ddy(uv);
#endif
    color=0;normalTS=float3(0,0,1);ao=1;smoothness=0;metallic=0;
    // This shader variant is only assigned to the transition and terrain-painted rock.
    // The original rock material never executes this layer sampler.
    [branch] if(w>0)
    {
        float3 gradient;
#if defined(_LT_ROAD_BLEND)
        LTSampleRoadGround(p,baseN,px,py,color,gradient,ao,smoothness,metallic);
#else
        LTSampleProjectedLayersGrad(p,baseN,_LTTriplanar>.5,px,py,color,gradient,ao,smoothness,metallic);
#endif
        normalTS=normalize(float3(-dot(gradient,t),-dot(gradient,b),1));
    }
    [branch] if(w<1)
    {
#if defined(SHADER_STAGE_RAY_TRACING)
        float3 ownColor=SAMPLE_TEXTURE2D_LOD(_LTRockColorMap,sampler_LTRockColorMap,uv,0).rgb*_LTRockTint.rgb;
#else
        float3 ownColor=SAMPLE_TEXTURE2D_GRAD(_LTRockColorMap,sampler_LTRockColorMap,uv,ux,uy).rgb*_LTRockTint.rgb;
#endif
        float3 ownNormal=float3(0,0,1);
        if(_LTRockFlags.y>.5)
#if defined(SHADER_STAGE_RAY_TRACING)
            ownNormal=UnpackNormalScale(SAMPLE_TEXTURE2D_LOD(_LTRockNormalMap,sampler_LTRockNormalMap,uv,0),_LTRockSettings.x);
#else
            ownNormal=UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_LTRockNormalMap,sampler_LTRockNormalMap,uv,ux,uy),_LTRockSettings.x);
#endif
        float ownAO=1,ownSmooth=_LTRockSettings.y,ownMetal=_LTRockSettings.z;
        if(_LTRockFlags.z>.5)
        {
#if defined(SHADER_STAGE_RAY_TRACING)
            float4 mask=SAMPLE_TEXTURE2D_LOD(_LTRockMaskMap,sampler_LTRockMaskMap,uv,0);
#else
            float4 mask=SAMPLE_TEXTURE2D_GRAD(_LTRockMaskMap,sampler_LTRockMaskMap,uv,ux,uy);
#endif
            ownAO=lerp(_LTRockRemap.x,_LTRockRemap.y,mask.g);
            ownSmooth=lerp(_LTRockRemap.z,_LTRockRemap.w,mask.a);
            ownMetal=lerp(_LTRockSettings.w,_LTRockFlags.w,mask.r);
        }
        color=lerp(ownColor,color,w);normalTS=normalize(lerp(ownNormal,normalTS,w));
        ao=lerp(ownAO,ao,w);smoothness=lerp(ownSmooth,smoothness,w);metallic=lerp(ownMetal,metallic,w);
    }
}
