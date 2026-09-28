#ifndef LT_ROAD_PROJECTION_INCLUDED
#define LT_ROAD_PROJECTION_INCLUDED
// Uniforms live in UnityPerMaterial in both the live shader and atlas baker.
// abs(slot)-1 is the slice; negative slots have an adjacent Jacobian slice.
// Zero retains the original projection.
TEXTURE2D_ARRAY(_LTRoadProjectionMap);
TEXTURE2D(_LTRoadSuppressionMap);
float LTRoadDisplacementMultiplier(float2 position)
{
    [branch] if(_LTRoadSuppressionEnabled<.5)return 1;
    float2 uv=((position-_LTRect.xy)/_LTRect.zw*256+.5)/257;
    return 1-saturate(SAMPLE_TEXTURE2D_LOD(_LTRoadSuppressionMap,sampler_LinearClamp,uv,0).r);
}
float LTRoadSlot(int slot)
{
    // Called only inside the uniform enabled branch. Repeating the inverted
    // early-out here triggers recursive optimization in the legacy D3D compiler.
    // Keep BOTH operands in range: shader compilers may evaluate both sides of
    // a ternary even when an unrolled slot makes its condition constant.
    float4 slots=slot<4?_LTRoadProjectionSlots0:(slot<8?_LTRoadProjectionSlots1:_LTRoadProjectionSlots2);
    return slots[slot&3];
}
float LTRoadSlice(int slot) { return abs(LTRoadSlot(slot))-1; }
float2 LTRoadRight(float2 r) { return dot(r,r)>1e-12?normalize(r):float2(1,0); }
// Explicit bilinear evaluation also supplies a Jacobian for mip selection. No
// derivatives inside divergent layer branches; domain and fragment use identical UV.
float4 LTRoadMap(float2 position,float slice,out float2 uvX,out float2 uvZ)
{
    float2 p=saturate((position-_LTRect.xy)/_LTRect.zw)*256;
    int2 cell=min((int2)floor(p),int2(255,255));
    float2 f=p-cell;
    float4 a=LOAD_TEXTURE2D_ARRAY(_LTRoadProjectionMap,cell,(int)slice);
    float4 b=LOAD_TEXTURE2D_ARRAY(_LTRoadProjectionMap,cell+int2(1,0),(int)slice);
    float4 c=LOAD_TEXTURE2D_ARRAY(_LTRoadProjectionMap,cell+int2(0,1),(int)slice);
    float4 d=LOAD_TEXTURE2D_ARRAY(_LTRoadProjectionMap,cell+int2(1,1),(int)slice);
    uvX=lerp(b.xy-a.xy,d.xy-c.xy,f.y)*(256/_LTRect.z);
    uvZ=lerp(c.xy-a.xy,d.xy-b.xy,f.x)*(256/_LTRect.w);
    return lerp(lerp(a,b,f.x),lerp(c,d,f.x),f.y);
}
float LTRoadOwner(float4 value) { return floor(length(value.zw)+.5); }
float4 LTRoadNetworkMap(float2 position,float slice,out float2 uvX,out float2 uvZ)
{
    float2 p=saturate((position-_LTRect.xy)/_LTRect.zw)*256;
    int2 cell=min((int2)floor(p),int2(255,255));float2 f=p-cell;
    float4 a=LOAD_TEXTURE2D_ARRAY(_LTRoadProjectionMap,cell,(int)slice);
    float4 b=LOAD_TEXTURE2D_ARRAY(_LTRoadProjectionMap,cell+int2(1,0),(int)slice);
    float4 c=LOAD_TEXTURE2D_ARRAY(_LTRoadProjectionMap,cell+int2(0,1),(int)slice);
    float4 d=LOAD_TEXTURE2D_ARRAY(_LTRoadProjectionMap,cell+int2(1,1),(int)slice);
    float owner=LTRoadOwner(a);
    if(owner==LTRoadOwner(b)&&owner==LTRoadOwner(c)&&owner==LTRoadOwner(d))
    {
        uvX=lerp(b.xy-a.xy,d.xy-c.xy,f.y)*(256/_LTRect.z);
        uvZ=lerp(c.xy-a.xy,d.xy-b.xy,f.x)*(256/_LTRect.w);
        return lerp(lerp(a,b,f.x),lerp(c,d,f.x),f.y);
    }
    int2 offset=int2(f.x>=.5?1:0,f.y>=.5?1:0);
    float4 chosen=LOAD_TEXTURE2D_ARRAY(_LTRoadProjectionMap,cell+offset,(int)slice);
    if(LTRoadOwner(chosen)<.5)
    {
        float best=-1;
        [unroll] for(int z=0;z<2;z++)[unroll] for(int x=0;x<2;x++)
        {
            float4 candidate=LOAD_TEXTURE2D_ARRAY(_LTRoadProjectionMap,cell+int2(x,z),(int)slice);
            float weight=(x==0?1-f.x:f.x)*(z==0?1-f.y:f.y);
            if(LTRoadOwner(candidate)>.5&&weight>best){best=weight;offset=int2(x,z);chosen=candidate;}
        }
    }
    float4 jacobian=LOAD_TEXTURE2D_ARRAY(_LTRoadProjectionMap,cell+offset,(int)slice+1);
    uvX=jacobian.xy;uvZ=jacobian.zw;
    chosen.xy+=uvX*((f.x-offset.x)*_LTRect.z/256)+uvZ*((f.y-offset.y)*_LTRect.w/256);
    return chosen;
}
void LTRoadMappedUV(int slot,float2 coveragePosition,float2 coverageDx,float2 coverageDy,
    inout float2 uv,inout float2 dx,inout float2 dy,out float2 right)
{
    right=float2(1,0);
    [branch] if(_LTRoadProjectionEnabled>.5)
    {
        float slice=LTRoadSlice(slot);
        [branch] if(slice>=0)
        {
            float2 ux,uz;float4 map;
            if(LTRoadSlot(slot)<0)map=LTRoadNetworkMap(coveragePosition,slice,ux,uz);
            else map=LTRoadMap(coveragePosition,slice,ux,uz);
            uv=map.xy;right=LTRoadRight(map.zw);
            dx=ux*coverageDx.x+uz*coverageDx.y;
            dy=ux*coverageDy.x+uz*coverageDy.y;
        }
    }
}
float2 LTRoadDisplacementUV(int slot,float2 position,float4 tiling)
{
    float2 uv=position/max(tiling.xy,.001)+tiling.zw;
    [branch] if(_LTRoadProjectionEnabled>.5)
    {
        float slice=LTRoadSlice(slot);
        if(slice>=0){float2 ux,uz;if(LTRoadSlot(slot)<0)uv=LTRoadNetworkMap(position,slice,ux,uz).xy;else uv=LTRoadMap(position,slice,ux,uz).xy;}
    }
    return uv;
}
float2 LTRoadRotate(float2 v,float2 right)
{
    // U points right, V points forward. Positive Z road: identity mapping.
    return v.x*right+v.y*float2(-right.y,right.x);
}
float3 LTProjectionGradient(float3 n,int axis)
{
    float2 slope=-n.xy/max(n.z,.0001);
    return axis==0?float3(0,slope.y,slope.x):axis==1?float3(slope.x,0,slope.y):float3(slope.x,slope.y,0);
}
float3 LTRoadLayerGradient(int slot,float3 normalTS,float2 right,int axis)
{
    if(LTRoadSlice(slot)>=0)
    {
        float2 slope=LTRoadRotate(-normalTS.xy/max(normalTS.z,.0001),right);
        return float3(slope.x,0,slope.y);
    }
    return LTProjectionGradient(normalTS,axis);
}
#endif
