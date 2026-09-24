// Included after the shared layer samplers and HDRP varying declarations.
// Displacement is raster-only; original meshes/colliders remain untouched.
TEXTURE2D(_LTDisplacementEdgeDistance);
TEXTURE2D(_LTDeformationDepth);
TEXTURE2D(_LTDeformationRegion0);
TEXTURE2D(_LTDeformationRegion1);
TEXTURE2D(_LTDeformationRegion2);
TEXTURE2D(_LTDeformationRegion3);
TEXTURE2D(_LTDeformationRegion4);
TEXTURE2D(_LTDeformationRegion5);
TEXTURE2D(_LTDeformationRegion6);
TEXTURE2D(_LTDeformationRegion7);
float LTDeformationDepth(float2 position)
{
    [branch] if(_LTDeformationEnabled<.5)return 0;
    float2 uv=(position-_LTDeformationRect.xy)/max(_LTDeformationRect.zw,.00001);
    if(any(uv<0)||any(uv>1))return 0;
    uv=(uv*_LTDeformationCells+.5)/(_LTDeformationCells+1);
    float depth=max(0,SAMPLE_TEXTURE2D_LOD(_LTDeformationDepth,sampler_LinearClamp,uv,0).r);
    [branch] if(_LTDeformationRegionCount>0.5)
    {
        float2 p=(position-_LTDeformationRegionRect0.xy)/max(_LTDeformationRegionRect0.zw,.00001);
        [branch] if(all(p>=0)&&all(p<=1))
        {
            p=(p*(_LTDeformationRegionSize0.xy-1)+.5)/_LTDeformationRegionSize0.xy;
            depth=max(depth,SAMPLE_TEXTURE2D_LOD(_LTDeformationRegion0,sampler_LinearClamp,p,0).r);
        }
    }
    [branch] if(_LTDeformationRegionCount>1.5)
    {
        float2 p=(position-_LTDeformationRegionRect1.xy)/max(_LTDeformationRegionRect1.zw,.00001);
        [branch] if(all(p>=0)&&all(p<=1))
        {
            p=(p*(_LTDeformationRegionSize1.xy-1)+.5)/_LTDeformationRegionSize1.xy;
            depth=max(depth,SAMPLE_TEXTURE2D_LOD(_LTDeformationRegion1,sampler_LinearClamp,p,0).r);
        }
    }
    [branch] if(_LTDeformationRegionCount>2.5)
    {
        float2 p=(position-_LTDeformationRegionRect2.xy)/max(_LTDeformationRegionRect2.zw,.00001);
        [branch] if(all(p>=0)&&all(p<=1))
        {
            p=(p*(_LTDeformationRegionSize2.xy-1)+.5)/_LTDeformationRegionSize2.xy;
            depth=max(depth,SAMPLE_TEXTURE2D_LOD(_LTDeformationRegion2,sampler_LinearClamp,p,0).r);
        }
    }
    [branch] if(_LTDeformationRegionCount>3.5)
    {
        float2 p=(position-_LTDeformationRegionRect3.xy)/max(_LTDeformationRegionRect3.zw,.00001);
        [branch] if(all(p>=0)&&all(p<=1))
        {
            p=(p*(_LTDeformationRegionSize3.xy-1)+.5)/_LTDeformationRegionSize3.xy;
            depth=max(depth,SAMPLE_TEXTURE2D_LOD(_LTDeformationRegion3,sampler_LinearClamp,p,0).r);
        }
    }
    [branch] if(_LTDeformationRegionCount>4.5)
    {
        float2 p=(position-_LTDeformationRegionRect4.xy)/max(_LTDeformationRegionRect4.zw,.00001);
        [branch] if(all(p>=0)&&all(p<=1))
        {
            p=(p*(_LTDeformationRegionSize4.xy-1)+.5)/_LTDeformationRegionSize4.xy;
            depth=max(depth,SAMPLE_TEXTURE2D_LOD(_LTDeformationRegion4,sampler_LinearClamp,p,0).r);
        }
    }
    [branch] if(_LTDeformationRegionCount>5.5)
    {
        float2 p=(position-_LTDeformationRegionRect5.xy)/max(_LTDeformationRegionRect5.zw,.00001);
        [branch] if(all(p>=0)&&all(p<=1))
        {
            p=(p*(_LTDeformationRegionSize5.xy-1)+.5)/_LTDeformationRegionSize5.xy;
            depth=max(depth,SAMPLE_TEXTURE2D_LOD(_LTDeformationRegion5,sampler_LinearClamp,p,0).r);
        }
    }
    [branch] if(_LTDeformationRegionCount>6.5)
    {
        float2 p=(position-_LTDeformationRegionRect6.xy)/max(_LTDeformationRegionRect6.zw,.00001);
        [branch] if(all(p>=0)&&all(p<=1))
        {
            p=(p*(_LTDeformationRegionSize6.xy-1)+.5)/_LTDeformationRegionSize6.xy;
            depth=max(depth,SAMPLE_TEXTURE2D_LOD(_LTDeformationRegion6,sampler_LinearClamp,p,0).r);
        }
    }
    [branch] if(_LTDeformationRegionCount>7.5)
    {
        float2 p=(position-_LTDeformationRegionRect7.xy)/max(_LTDeformationRegionRect7.zw,.00001);
        [branch] if(all(p>=0)&&all(p<=1))
        {
            p=(p*(_LTDeformationRegionSize7.xy-1)+.5)/_LTDeformationRegionSize7.xy;
            depth=max(depth,SAMPLE_TEXTURE2D_LOD(_LTDeformationRegion7,sampler_LinearClamp,p,0).r);
        }
    }
    return depth;
}
float LTLayerDisplacement(float2 position)
{
    // Mud-only layers need one depth sample, not ordinary heightmap blending.
    float activeAmplitude=_LTDisplacement0.x+_LTDisplacement1.x+_LTDisplacement2.x+_LTDisplacement3.x+
        _LTDisplacement4.x+_LTDisplacement5.x+_LTDisplacement6.x+_LTDisplacement7.x+
        _LTDisplacement8.x+_LTDisplacement9.x+_LTDisplacement10.x+_LTDisplacement11.x;
    [branch] if(activeAmplitude<=0)return 0;
    float2 uv=((position-_LTRect.xy)/_LTRect.zw*256.0+.5)/257.0;
    float4 a=SAMPLE_TEXTURE2D_LOD(_LTWeights0,sampler_LinearClamp,uv,0);
    float4 b=SAMPLE_TEXTURE2D_LOD(_LTWeights1,sampler_LinearClamp,uv,0);
    float4 c=SAMPLE_TEXTURE2D_LOD(_LTWeights2,sampler_LinearClamp,uv,0);
    float weights[12]={a.x,a.y,a.z,a.w,b.x,b.y,b.z,b.w,c.x,c.y,c.z,c.w};
    float heights[12],offsets[12];
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(0,position,_LTTiling0);
        if(weights[0]>.00001 && _LTSettings0.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(0),0).g;
        heights[0]=saturate((h-.5)*_LTFlags0.z+.5+_LTFlags0.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[0]>.00001 && _LTSettings0.w>.5 && _LTDisplacement0.x>0 && _LTDisplacement0.z>0)
            geometryHeight=LTSampleLayerMask(0,
                layerUV,_LTDisplacement0.z).g;
        offsets[0]=(geometryHeight-_LTDisplacement0.y)*_LTDisplacement0.x;
    }
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(1,position,_LTTiling1);
        if(weights[1]>.00001 && _LTSettings1.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(1),0).g;
        heights[1]=saturate((h-.5)*_LTFlags1.z+.5+_LTFlags1.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[1]>.00001 && _LTSettings1.w>.5 && _LTDisplacement1.x>0 && _LTDisplacement1.z>0)
            geometryHeight=LTSampleLayerMask(1,
                layerUV,_LTDisplacement1.z).g;
        offsets[1]=(geometryHeight-_LTDisplacement1.y)*_LTDisplacement1.x;
    }
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(2,position,_LTTiling2);
        if(weights[2]>.00001 && _LTSettings2.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(2),0).g;
        heights[2]=saturate((h-.5)*_LTFlags2.z+.5+_LTFlags2.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[2]>.00001 && _LTSettings2.w>.5 && _LTDisplacement2.x>0 && _LTDisplacement2.z>0)
            geometryHeight=LTSampleLayerMask(2,
                layerUV,_LTDisplacement2.z).g;
        offsets[2]=(geometryHeight-_LTDisplacement2.y)*_LTDisplacement2.x;
    }
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(3,position,_LTTiling3);
        if(weights[3]>.00001 && _LTSettings3.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(3),0).g;
        heights[3]=saturate((h-.5)*_LTFlags3.z+.5+_LTFlags3.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[3]>.00001 && _LTSettings3.w>.5 && _LTDisplacement3.x>0 && _LTDisplacement3.z>0)
            geometryHeight=LTSampleLayerMask(3,
                layerUV,_LTDisplacement3.z).g;
        offsets[3]=(geometryHeight-_LTDisplacement3.y)*_LTDisplacement3.x;
    }
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(4,position,_LTTiling4);
        if(weights[4]>.00001 && _LTSettings4.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(4),0).g;
        heights[4]=saturate((h-.5)*_LTFlags4.z+.5+_LTFlags4.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[4]>.00001 && _LTSettings4.w>.5 && _LTDisplacement4.x>0 && _LTDisplacement4.z>0)
            geometryHeight=LTSampleLayerMask(4,
                layerUV,_LTDisplacement4.z).g;
        offsets[4]=(geometryHeight-_LTDisplacement4.y)*_LTDisplacement4.x;
    }
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(5,position,_LTTiling5);
        if(weights[5]>.00001 && _LTSettings5.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(5),0).g;
        heights[5]=saturate((h-.5)*_LTFlags5.z+.5+_LTFlags5.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[5]>.00001 && _LTSettings5.w>.5 && _LTDisplacement5.x>0 && _LTDisplacement5.z>0)
            geometryHeight=LTSampleLayerMask(5,
                layerUV,_LTDisplacement5.z).g;
        offsets[5]=(geometryHeight-_LTDisplacement5.y)*_LTDisplacement5.x;
    }
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(6,position,_LTTiling6);
        if(weights[6]>.00001 && _LTSettings6.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(6),0).g;
        heights[6]=saturate((h-.5)*_LTFlags6.z+.5+_LTFlags6.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[6]>.00001 && _LTSettings6.w>.5 && _LTDisplacement6.x>0 && _LTDisplacement6.z>0)
            geometryHeight=LTSampleLayerMask(6,
                layerUV,_LTDisplacement6.z).g;
        offsets[6]=(geometryHeight-_LTDisplacement6.y)*_LTDisplacement6.x;
    }
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(7,position,_LTTiling7);
        if(weights[7]>.00001 && _LTSettings7.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(7),0).g;
        heights[7]=saturate((h-.5)*_LTFlags7.z+.5+_LTFlags7.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[7]>.00001 && _LTSettings7.w>.5 && _LTDisplacement7.x>0 && _LTDisplacement7.z>0)
            geometryHeight=LTSampleLayerMask(7,
                layerUV,_LTDisplacement7.z).g;
        offsets[7]=(geometryHeight-_LTDisplacement7.y)*_LTDisplacement7.x;
    }
    heights[8]=.5; offsets[8]=0;
    [branch] if(weights[8]>.00001)
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(8,position,_LTTiling8);
        if(weights[8]>.00001 && _LTSettings8.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(8),0).g;
        heights[8]=saturate((h-.5)*_LTFlags8.z+.5+_LTFlags8.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[8]>.00001 && _LTSettings8.w>.5 && _LTDisplacement8.x>0 && _LTDisplacement8.z>0)
            geometryHeight=LTSampleLayerMask(8,
                layerUV,_LTDisplacement8.z).g;
        offsets[8]=(geometryHeight-_LTDisplacement8.y)*_LTDisplacement8.x;
    }
    heights[9]=.5; offsets[9]=0;
    [branch] if(weights[9]>.00001)
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(9,position,_LTTiling9);
        if(weights[9]>.00001 && _LTSettings9.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(9),0).g;
        heights[9]=saturate((h-.5)*_LTFlags9.z+.5+_LTFlags9.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[9]>.00001 && _LTSettings9.w>.5 && _LTDisplacement9.x>0 && _LTDisplacement9.z>0)
            geometryHeight=LTSampleLayerMask(9,
                layerUV,_LTDisplacement9.z).g;
        offsets[9]=(geometryHeight-_LTDisplacement9.y)*_LTDisplacement9.x;
    }
    heights[10]=.5; offsets[10]=0;
    [branch] if(weights[10]>.00001)
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(10,position,_LTTiling10);
        if(weights[10]>.00001 && _LTSettings10.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(10),0).g;
        heights[10]=saturate((h-.5)*_LTFlags10.z+.5+_LTFlags10.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[10]>.00001 && _LTSettings10.w>.5 && _LTDisplacement10.x>0 && _LTDisplacement10.z>0)
            geometryHeight=LTSampleLayerMask(10,
                layerUV,_LTDisplacement10.z).g;
        offsets[10]=(geometryHeight-_LTDisplacement10.y)*_LTDisplacement10.x;
    }
    heights[11]=.5; offsets[11]=0;
    [branch] if(weights[11]>.00001)
    {
        float h=.5;
        float2 layerUV=LTRoadDisplacementUV(11,position,_LTTiling11);
        if(weights[11]>.00001 && _LTSettings11.w>.5)
            h=SAMPLE_TEXTURE2D_ARRAY_LOD(_LTMaskArray,sampler_LTMaskArray,layerUV,LTLayerSlice(11),0).g;
        heights[11]=saturate((h-.5)*_LTFlags11.z+.5+_LTFlags11.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[11]>.00001 && _LTSettings11.w>.5 && _LTDisplacement11.x>0 && _LTDisplacement11.z>0)
            geometryHeight=LTSampleLayerMask(11,
                layerUV,_LTDisplacement11.z).g;
        offsets[11]=(geometryHeight-_LTDisplacement11.y)*_LTDisplacement11.x;
    }
    float maxHeight=-1;
    [unroll] for(int i=0;i<12;i++) if(weights[i]>.00001) maxHeight=max(maxHeight,heights[i]+weights[i]);
    float amplitudes[12]={_LTDisplacement0.x,_LTDisplacement1.x,_LTDisplacement2.x,_LTDisplacement3.x,
        _LTDisplacement4.x,_LTDisplacement5.x,_LTDisplacement6.x,_LTDisplacement7.x,
        _LTDisplacement8.x,_LTDisplacement9.x,_LTDisplacement10.x,_LTDisplacement11.x};
    float total=0,offset=0,visible=0;
    [unroll] for(int j=0;j<12;j++)
    {
        if(_LTHeightBlend>.0001)
            weights[j]*=lerp(1,saturate((heights[j]+weights[j]-maxHeight+.2)/.2),_LTHeightBlend);
        total+=weights[j];offset+=weights[j]*offsets[j];
        if(amplitudes[j]>0)visible+=weights[j];
    }
    float displacement=offset/max(total,.00001);
    // Match LTPaintMath.DisplacementVisibility: dark, other-layer-dominant areas
    // stay undisplaced. Zero raw height within the visible layer is still refined.
    float coverage=visible/max(total,.00001);
    float edgeFade=1;
    if(_LTMaskEdgeFade>0)
    {
        float2 edgeUV=(position-_LTDisplacementRect.xy)/max(_LTDisplacementRect.zw,.00001);
        // Distance texture contains both endpoints, just like the paint weights.
        float samples=_LTDisplacementCells+1;
        edgeUV=(saturate(edgeUV)*_LTDisplacementCells+.5)/samples;
        float distanceToEdge=SAMPLE_TEXTURE2D_LOD(_LTDisplacementEdgeDistance,sampler_LinearClamp,edgeUV,0).r;
        edgeFade=smoothstep(0,_LTMaskEdgeFade,distanceToEdge);
    }
    return displacement*smoothstep(.5,.65,coverage)*edgeFade;
}
void ApplyVertexModification(AttributesMesh input,float3 normalWS,inout float3 positionRWS,float3 timeParameters) {}
#ifdef TESSELLATION_ON
float GetTessellationFactor(AttributesMesh input) { return _TessellationFactor; }
float LTDisplacementEnvelope(float2 position,float3 positionRWS)
{
    float fade=1-smoothstep(_LTDisplacementParams.x,_LTDisplacementParams.y,
        distance(positionRWS,GetPrimaryCameraPosition()));
    float2 edge=min(position-_LTRect.xy,_LTRect.xy+_LTRect.zw-position);
    return fade*smoothstep(0,max(.1,_LTDisplacementParams.z),min(edge.x,edge.y));
}
VaryingsMeshToDS ApplyTessellationModification(VaryingsMeshToDS input,float3 timeParameters)
{
#ifdef VARYINGS_DS_NEED_COLOR
    // Triplanar textures stay attached to the undisplaced surface.
    if(_LTTriplanar>.5)input.color=float4(input.positionRWS,1);
#endif
#ifdef VARYINGS_DS_NEED_TEXCOORD0
    float2 position=input.texCoord0.xy*_LTWorldSize.xy;
    float envelope=LTDisplacementEnvelope(position,input.positionRWS);
    if(envelope>0)
    {
        float3 n=normalize(input.normalWS);
        float roadDisplacement=LTRoadDisplacementMultiplier(position);
        input.positionRWS+=n*LTLayerDisplacement(position)*envelope*roadDisplacement;
        // Visual tracks lower world Y only. Original terrain collider and TBN stay intact.
        input.positionRWS.y-=LTDeformationDepth(position)*envelope*roadDisplacement;
        // Preserve source terrain normals/tangents; no displacement normal correction.
    }
#endif
    return input;
}
#endif
