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
        _LTDisplacement4.x+_LTDisplacement5.x+_LTDisplacement6.x+_LTDisplacement7.x;
    [branch] if(activeAmplitude<=0)return 0;
    float2 uv=((position-_LTRect.xy)/_LTRect.zw*256.0+.5)/257.0;
    float4 a=SAMPLE_TEXTURE2D_LOD(_LTWeights0,sampler_LinearClamp,uv,0);
    float4 b=SAMPLE_TEXTURE2D_LOD(_LTWeights1,sampler_LinearClamp,uv,0);
    float weights[8]={a.x,a.y,a.z,a.w,b.x,b.y,b.z,b.w};
    float heights[8],offsets[8];
    {
        float h=.5;
        if(weights[0]>.00001 && _LTSettings0.w>.5)
            h=SAMPLE_TEXTURE2D_LOD(_LTMask0,sampler_LinearRepeat,position/max(_LTTiling0.xy,.001)+_LTTiling0.zw,0).g;
        heights[0]=saturate((h-.5)*_LTFlags0.z+.5+_LTFlags0.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[0]>.00001 && _LTSettings0.w>.5 && _LTDisplacement0.x>0 && _LTDisplacement0.z>0)
            geometryHeight=SAMPLE_TEXTURE2D_LOD(_LTMask0,sampler_LinearRepeat,
                position/max(_LTTiling0.xy,.001)+_LTTiling0.zw,_LTDisplacement0.z).g;
        offsets[0]=(geometryHeight-_LTDisplacement0.y)*_LTDisplacement0.x;
    }
    {
        float h=.5;
        if(weights[1]>.00001 && _LTSettings1.w>.5)
            h=SAMPLE_TEXTURE2D_LOD(_LTMask1,sampler_LinearRepeat,position/max(_LTTiling1.xy,.001)+_LTTiling1.zw,0).g;
        heights[1]=saturate((h-.5)*_LTFlags1.z+.5+_LTFlags1.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[1]>.00001 && _LTSettings1.w>.5 && _LTDisplacement1.x>0 && _LTDisplacement1.z>0)
            geometryHeight=SAMPLE_TEXTURE2D_LOD(_LTMask1,sampler_LinearRepeat,
                position/max(_LTTiling1.xy,.001)+_LTTiling1.zw,_LTDisplacement1.z).g;
        offsets[1]=(geometryHeight-_LTDisplacement1.y)*_LTDisplacement1.x;
    }
    {
        float h=.5;
        if(weights[2]>.00001 && _LTSettings2.w>.5)
            h=SAMPLE_TEXTURE2D_LOD(_LTMask2,sampler_LinearRepeat,position/max(_LTTiling2.xy,.001)+_LTTiling2.zw,0).g;
        heights[2]=saturate((h-.5)*_LTFlags2.z+.5+_LTFlags2.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[2]>.00001 && _LTSettings2.w>.5 && _LTDisplacement2.x>0 && _LTDisplacement2.z>0)
            geometryHeight=SAMPLE_TEXTURE2D_LOD(_LTMask2,sampler_LinearRepeat,
                position/max(_LTTiling2.xy,.001)+_LTTiling2.zw,_LTDisplacement2.z).g;
        offsets[2]=(geometryHeight-_LTDisplacement2.y)*_LTDisplacement2.x;
    }
    {
        float h=.5;
        if(weights[3]>.00001 && _LTSettings3.w>.5)
            h=SAMPLE_TEXTURE2D_LOD(_LTMask3,sampler_LinearRepeat,position/max(_LTTiling3.xy,.001)+_LTTiling3.zw,0).g;
        heights[3]=saturate((h-.5)*_LTFlags3.z+.5+_LTFlags3.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[3]>.00001 && _LTSettings3.w>.5 && _LTDisplacement3.x>0 && _LTDisplacement3.z>0)
            geometryHeight=SAMPLE_TEXTURE2D_LOD(_LTMask3,sampler_LinearRepeat,
                position/max(_LTTiling3.xy,.001)+_LTTiling3.zw,_LTDisplacement3.z).g;
        offsets[3]=(geometryHeight-_LTDisplacement3.y)*_LTDisplacement3.x;
    }
    {
        float h=.5;
        if(weights[4]>.00001 && _LTSettings4.w>.5)
            h=SAMPLE_TEXTURE2D_LOD(_LTMask4,sampler_LinearRepeat,position/max(_LTTiling4.xy,.001)+_LTTiling4.zw,0).g;
        heights[4]=saturate((h-.5)*_LTFlags4.z+.5+_LTFlags4.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[4]>.00001 && _LTSettings4.w>.5 && _LTDisplacement4.x>0 && _LTDisplacement4.z>0)
            geometryHeight=SAMPLE_TEXTURE2D_LOD(_LTMask4,sampler_LinearRepeat,
                position/max(_LTTiling4.xy,.001)+_LTTiling4.zw,_LTDisplacement4.z).g;
        offsets[4]=(geometryHeight-_LTDisplacement4.y)*_LTDisplacement4.x;
    }
    {
        float h=.5;
        if(weights[5]>.00001 && _LTSettings5.w>.5)
            h=SAMPLE_TEXTURE2D_LOD(_LTMask5,sampler_LinearRepeat,position/max(_LTTiling5.xy,.001)+_LTTiling5.zw,0).g;
        heights[5]=saturate((h-.5)*_LTFlags5.z+.5+_LTFlags5.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[5]>.00001 && _LTSettings5.w>.5 && _LTDisplacement5.x>0 && _LTDisplacement5.z>0)
            geometryHeight=SAMPLE_TEXTURE2D_LOD(_LTMask5,sampler_LinearRepeat,
                position/max(_LTTiling5.xy,.001)+_LTTiling5.zw,_LTDisplacement5.z).g;
        offsets[5]=(geometryHeight-_LTDisplacement5.y)*_LTDisplacement5.x;
    }
    {
        float h=.5;
        if(weights[6]>.00001 && _LTSettings6.w>.5)
            h=SAMPLE_TEXTURE2D_LOD(_LTMask6,sampler_LinearRepeat,position/max(_LTTiling6.xy,.001)+_LTTiling6.zw,0).g;
        heights[6]=saturate((h-.5)*_LTFlags6.z+.5+_LTFlags6.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[6]>.00001 && _LTSettings6.w>.5 && _LTDisplacement6.x>0 && _LTDisplacement6.z>0)
            geometryHeight=SAMPLE_TEXTURE2D_LOD(_LTMask6,sampler_LinearRepeat,
                position/max(_LTTiling6.xy,.001)+_LTTiling6.zw,_LTDisplacement6.z).g;
        offsets[6]=(geometryHeight-_LTDisplacement6.y)*_LTDisplacement6.x;
    }
    {
        float h=.5;
        if(weights[7]>.00001 && _LTSettings7.w>.5)
            h=SAMPLE_TEXTURE2D_LOD(_LTMask7,sampler_LinearRepeat,position/max(_LTTiling7.xy,.001)+_LTTiling7.zw,0).g;
        heights[7]=saturate((h-.5)*_LTFlags7.z+.5+_LTFlags7.w);
        // Keep h at mip 0 for visibility/height blending; filter ONLY geometry.
        float geometryHeight=h;
        if(weights[7]>.00001 && _LTSettings7.w>.5 && _LTDisplacement7.x>0 && _LTDisplacement7.z>0)
            geometryHeight=SAMPLE_TEXTURE2D_LOD(_LTMask7,sampler_LinearRepeat,
                position/max(_LTTiling7.xy,.001)+_LTTiling7.zw,_LTDisplacement7.z).g;
        offsets[7]=(geometryHeight-_LTDisplacement7.y)*_LTDisplacement7.x;
    }
    float maxHeight=-1;
    [unroll] for(int i=0;i<8;i++) if(weights[i]>.00001) maxHeight=max(maxHeight,heights[i]+weights[i]);
    float amplitudes[8]={_LTDisplacement0.x,_LTDisplacement1.x,_LTDisplacement2.x,_LTDisplacement3.x,
        _LTDisplacement4.x,_LTDisplacement5.x,_LTDisplacement6.x,_LTDisplacement7.x};
    float total=0,offset=0,visible=0;
    [unroll] for(int j=0;j<8;j++)
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
        input.positionRWS+=n*LTLayerDisplacement(position)*envelope;
        // Visual tracks lower world Y only. Original terrain collider and TBN stay intact.
        input.positionRWS.y-=LTDeformationDepth(position)*envelope;
        // Preserve source terrain normals/tangents; no displacement normal correction.
    }
#endif
    return input;
}
#endif
