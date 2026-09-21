// Included after HDRP's shader pass, which defines its standard tessellation types.
// Conservative max-mips, not averaged mips: enclosed small islands must survive.
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTCoverageCoordinates.hlsl"
#ifndef LT_OCCUPANCY_TEXTURE_DECLARED
#define LT_OCCUPANCY_TEXTURE_DECLARED
TEXTURE2D(_LTDisplacementOccupancy);
#endif
TEXTURE2D(_LTDeformationFactors);
// Separating-axis triangle/box test. Degenerate (a,b,b) is an edge query.
bool LTSeparated(float2 axis,float2 a,float2 b,float2 c,float2 center,float2 halfSize)
{
    float p=dot(axis,a-center),q=dot(axis,b-center),r=dot(axis,c-center);
    float radius=dot(abs(axis),halfSize);
    return min(p,min(q,r))>radius || max(p,max(q,r))<-radius;
}
bool LTTriangleBox(float2 a,float2 b,float2 c,float2 low,float2 high)
{
    if(any(max(a,max(b,c))<low)||any(min(a,min(b,c))>high))return false;
    float2 center=(low+high)*.5,halfSize=(high-low)*.5;
    float2 e=b-a;
    if(LTSeparated(float2(-e.y,e.x),a,b,c,center,halfSize))return false;
    e=c-b;
    if(LTSeparated(float2(-e.y,e.x),a,b,c,center,halfSize))return false;
    e=a-c;
    return !LTSeparated(float2(-e.y,e.x),a,b,c,center,halfSize);
}
// Query the actual primitive, not its AABB or the coarse mip containing it.
// A max mip is only an upper bound: descend until covered leaves intersect.
float LTMudTriangleFactor(float2 a,float2 b,float2 c)
{
    [branch] if(_LTDeformationEnabled<.5)return 1;
    float size=max(1,_LTDeformationCells);
    a=(a-_LTDeformationRect.xy)/_LTDeformationRect.zw*size;
    b=(b-_LTDeformationRect.xy)/_LTDeformationRect.zw*size;
    c=(c-_LTDeformationRect.xy)/_LTDeformationRect.zw*size;
    // Do not clamp outside vertices onto the map boundary.
    if(!LTTriangleBox(a,b,c,float2(0,0),size.xx))return 1;
    int level=(int)round(log2(size));
    float ceiling=SAMPLE_TEXTURE2D_LOD(_LTDeformationFactors,sampler_PointClamp,float2(.5,.5),level).r;
    if(ceiling<=1)return 1;
    float best=1;
    // 512 cells -> depth 9, at most 28 pending DFS siblings.
    int3 pending[40];
    int count=1;
    pending[0]=int3(0,0,level);
    [loop] for(int visit=0;visit<256 && count>0;visit++)
    {
        int3 node=pending[--count];
        float span=exp2((float)node.z);
        float2 low=float2(node.xy)*span;
        if(!LTTriangleBox(a,b,c,low,low+span))continue;
        float2 uv=(float2(node.xy)+.5)/(size/span);
        float factor=SAMPLE_TEXTURE2D_LOD(_LTDeformationFactors,sampler_PointClamp,uv,node.z).r;
        if(factor<=best)continue;
        if(node.z==0)
        {
            best=factor;
            if(best>=ceiling)return best;
            continue;
        }
        int2 child=node.xy*2;int next=node.z-1;
        pending[count++]=int3(child,next);
        pending[count++]=int3(child+int2(1,0),next);
        pending[count++]=int3(child+int2(0,1),next);
        pending[count++]=int3(child+int2(1,1),next);
    }
    // Bounded conservative fallback for complex masks. Only unresolved nodes
    // intersecting this primitive contribute; never the entire map maximum.
    [loop] while(count>0)
    {
        int3 node=pending[--count];
        float span=exp2((float)node.z);
        float2 low=float2(node.xy)*span;
        if(!LTTriangleBox(a,b,c,low,low+span))continue;
        float2 uv=(float2(node.xy)+.5)/(size/span);
        best=max(best,SAMPLE_TEXTURE2D_LOD(_LTDeformationFactors,sampler_PointClamp,uv,node.z).r);
    }
    return best;
}
float LTMudFactor(float2 a,float2 b)
{
    // Identical traversal and budget fallback for both sides of a shared edge.
    if(a.x>b.x || (a.x==b.x && a.y>b.y)){float2 swap=a;a=b;b=swap;}
    return LTMudTriangleFactor(a,b,b);
}
float LTOccupancyTrianglePadded(float2 a,float2 b,float2 c,float2 padding)
{
    float4 area=_LTDisplacementRect.z>0?_LTDisplacementRect:_LTRect;
    float size=max(1,_LTDisplacementCells);
    float2 halo=1+max(padding,0)/area.zw*size;
    a=(a-area.xy)/area.zw*size;
    b=(b-area.xy)/area.zw*size;
    c=(c-area.xy)/area.zw*size;
    // Do not clamp vertices: that would turn outside triangles into inside ones.
    // Descend non-empty max-mip nodes until an actual covered leaf intersects.
    // 1024 cells -> depth 10, at most 31 pending DFS siblings.
    int3 pending[40];
    int count=1;
    pending[0]=int3(0,0,(int)round(log2(size)));
    [loop] for(int visit=0;visit<256 && count>0;visit++)
    {
        int3 node=pending[--count];
        float span=exp2((float)node.z);
        float2 low=float2(node.xy)*span;
        // One base-cell halo, NOT a whole coarse mip cell.
        if(!LTTriangleBox(a,b,c,low-halo,low+span+halo))continue;
        float2 uv=(float2(node.xy)+.5)/(size/span);
        float4 channels=SAMPLE_TEXTURE2D_LOD(_LTDisplacementOccupancy,sampler_PointClamp,uv,node.z);
        float2 occupied=_LTDeformationEnabled>.5?channels.ba:channels.rg;
        if(occupied.r<.5)continue;
        if(node.z==0 || occupied.g>.5)return 1;
        int2 child=node.xy*2;int level=node.z-1;
        pending[count++]=int3(child,level);
        pending[count++]=int3(child+int2(1,0),level);
        pending[count++]=int3(child+int2(0,1),level);
        pending[count++]=int3(child+int2(1,1),level);
    }
    // Bounded work. Unresolved complex queries remain conservative, never erase islands.
    return count>0?2:0; // 2 = conservative fallback, distinct from a confirmed hit.
}
float LTOccupancyTriangle(float2 a,float2 b,float2 c)
{
    return LTOccupancyTrianglePadded(a,b,c,float2(0,0));
}
float LTOccupancyEdge(float2 a,float2 b)
{
    // Canonical order also makes floating-point evaluation identical on both sides.
    if(a.x>b.x || (a.x==b.x && a.y>b.y)){float2 swap=a;a=b;b=swap;}
    return LTOccupancyTriangle(a,b,b);
}
float LTTransitionEdge(float2 a,float2 b)
{
    if(a.x>b.x || (a.x==b.x && a.y>b.y)){float2 swap=a;a=b;b=swap;}
    return LTOccupancyTrianglePadded(a,b,b,_LTTessellationMaskPadding.xy);
}
struct LTAdaptiveFactors
{
    float edge[3] : SV_TessFactor;
    float inside : SV_InsideTessFactor;
    float reason : TEXCOORD0; // 0 empty, 1 hit, 2 budget, 3 skipped, 4 force-one.
};
// Based on HDRP 17.3 TessellationShare.hlsl (Unity Companion License).
// Cull complete patches, NOT individual off-screen edges. An edge can leave the
// view while its patch still covers visible pixels; forcing that edge to 1 also
// reduces the inside factor and creates a moving coarse wedge inside the image.
float4 LTViewSafeTessellationFactors(float3 p0,float3 p1,float3 p2,
    float3 inputFactors)
{
    float epsilon=-GetMaxDisplacement();
#if defined(SHADERPASS) && (SHADERPASS == SHADERPASS_SHADOWS)
    if(CullTriangleFrustum(p0,p1,p2,epsilon,_ShadowFrustumPlanes,4))return 0;
#elif !defined(SCENESELECTIONPASS) && !defined(SCENEPICKINGPASS)
    if(CullTriangleFrustum(p0,p1,p2,epsilon,_FrustumPlanes,5))return 0;
#endif
#if !defined(_DOUBLESIDED_ON) && !defined(SCENESELECTIONPASS) && !defined(SCENEPICKINGPASS)
    if(_TessellationBackFaceCullEpsilon>-1.0 &&
       CullTriangleBackFaceView(p0,p1,p2,_TessellationBackFaceCullEpsilon,
           GetWorldSpaceNormalizeViewDir(p0),unity_WorldTransformParams.w))return 0;
#endif
    // Density on a shared edge depends only on its endpoints, even when the
    // opposite vertices of adjacent patches straddle the view boundary.
    float3 scale=float3(1,1,1);
    if(_TessellationFactorTriangleSize>0)
        scale*=GetScreenSpaceTessFactor(p0,p1,p2,_CameraViewProjMatrix,_ScreenSize,_TessellationFactorTriangleSize);
    if(_TessellationFactorMaxDistance>0)
    {
        float3 fade=GetDistanceBasedTessFactor(p0,p1,p2,GetPrimaryCameraPosition(),
            _TessellationFactorMinDistance,_TessellationFactorMaxDistance);
        scale*=fade*fade;
    }
    return CalcTriTessFactorsFromEdgeTessFactors(max(float3(1,1,1),
        scale*inputFactors*_GlobalTessellationFactorMultiplier));
}
LTAdaptiveFactors LTAdaptiveHullConstant(InputPatch<PackedVaryingsToDS,3> input)
{
    UNITY_SETUP_INSTANCE_ID(input[0].vmesh);
    VaryingsToDS a=UnpackVaryingsToDS(input[0]);
    VaryingsToDS b=UnpackVaryingsToDS(input[1]);
    VaryingsToDS c=UnpackVaryingsToDS(input[2]);
    float3 factors=float3((b.vmesh.tessellationFactor+c.vmesh.tessellationFactor)*.5,
        (c.vmesh.tessellationFactor+a.vmesh.tessellationFactor)*.5,(a.vmesh.tessellationFactor+b.vmesh.tessellationFactor)*.5);
    if(_LTTargetEdgeLength>0)
    {
        // Shared edges depend ONLY on their two endpoints. RWS differences are
        // world lengths, independent of camera origin and terrain object scale.
        float3 lengths=float3(distance(b.vmesh.positionRWS,c.vmesh.positionRWS),
            distance(c.vmesh.positionRWS,a.vmesh.positionRWS),distance(a.vmesh.positionRWS,b.vmesh.positionRWS));
        factors=min(factors,max(float3(1,1,1),lengths/max(.02,_LTTargetEdgeLength)));
    }
    // Apply the existing HDRP distance/screen attenuation AFTER the length cap.
    float4 tf=LTViewSafeTessellationFactors(a.vmesh.positionRWS,b.vmesh.positionRWS,c.vmesh.positionRWS,factors);
    // Diagnostic override lives ONLY here, not in HDRP's ordinary factor binding.
    // Preserve culled patches; skip every coverage query for visible patches.
    if(_LTForceHullOne>.5 && tf.w>0 && all(tf.xyz>0))
    {
        LTAdaptiveFactors probe;
        probe.edge[0]=1;probe.edge[1]=1;probe.edge[2]=1;probe.inside=1;
        probe.reason=4;
        return probe;
    }
    if(_LTUniformHullFactor>0 && tf.w>0 && all(tf.xyz>0))
    {
        // Retain HDRP patch culling, discard all density attenuation. No occupancy
        // query or neighbour-dependent value: same factor on every visible patch.
        LTAdaptiveFactors uniformProbe;
        float factor=clamp(_LTUniformHullFactor,1,min(63,MAX_TESSELLATION_FACTORS));
        uniformProbe.edge[0]=factor;uniformProbe.edge[1]=factor;uniformProbe.edge[2]=factor;
        uniformProbe.inside=factor;uniformProbe.reason=5;
        return uniformProbe;
    }
    float reason=3;
    if(tf.w>1 || any(tf.xyz>1))
    {
        float2 p=LTCoveragePosition(a.vmesh.positionRWS,a.vmesh.texCoord0.xy);
        float2 q=LTCoveragePosition(b.vmesh.positionRWS,b.vmesh.texCoord0.xy);
        float2 r=LTCoveragePosition(c.vmesh.positionRWS,c.vmesh.texCoord0.xy);
        // Each shared edge uses only its own endpoints, never the opposite vertex.
        float4 queries=float4(LTOccupancyEdge(q,r),LTOccupancyEdge(r,p),
            LTOccupancyEdge(p,q),LTOccupancyTriangle(p,q,r));
        // Only report decisions for factors that could actually subdivide.
        float4 relevant=queries*float4(tf.x>1,tf.y>1,tf.z>1,tf.w>1);
        reason=max(max(relevant.x,relevant.y),max(relevant.z,relevant.w));
        float4 support=saturate(queries);
        if(any(_LTTessellationMaskPadding.xy>0))
        {
            // Only query previously empty edges. The result depends on the edge,
            // never the opposite vertex: neighbours agree within the same map.
            float3 transition=0;
            if(queries.x==0 && tf.x>1)transition.x=LTTransitionEdge(q,r);
            if(queries.y==0 && tf.y>1)transition.y=LTTransitionEdge(r,p);
            if(queries.z==0 && tf.z>1)transition.z=LTTransitionEdge(p,q);
            support.xyz=max(support.xyz,.5*saturate(transition));
            reason=max(reason,max(transition.x,max(transition.y,transition.z)));
        }
        tf=lerp(float4(1,1,1,1),tf,support);
        if(any(_LTTessellationMaskPadding.xy>0))
            // Fill the transition patch instead of leaving a dense border around
            // an inside factor of one. Never reduce enclosed-island subdivision.
            tf.w=max(tf.w,(tf.x+tf.y+tf.z)/3);
    }
    if(_LTDeformationEnabled>.5 && tf.w>0 && all(tf.xyz>0))
    {
        float2 p=LTCoveragePosition(a.vmesh.positionRWS,a.vmesh.texCoord0.xy);
        float2 q=LTCoveragePosition(b.vmesh.positionRWS,b.vmesh.texCoord0.xy);
        float2 r=LTCoveragePosition(c.vmesh.positionRWS,c.vmesh.texCoord0.xy);
        float3 mud=float3(LTMudFactor(q,r),LTMudFactor(r,p),LTMudFactor(p,q));
        float4 mudFactors=LTViewSafeTessellationFactors(a.vmesh.positionRWS,b.vmesh.positionRWS,c.vmesh.positionRWS,mud);
        float inside=LTMudTriangleFactor(p,q,r);
        float4 interior=LTViewSafeTessellationFactors(a.vmesh.positionRWS,b.vmesh.positionRWS,c.vmesh.positionRWS,inside.xxx);
        mudFactors.w=max(mudFactors.w,interior.w);
        tf=max(tf,mudFactors);
    }
    LTAdaptiveFactors result;
    result.reason=reason;
    result.edge[0]=min(tf.x,MAX_TESSELLATION_FACTORS);
    result.edge[1]=min(tf.y,MAX_TESSELLATION_FACTORS);
    result.edge[2]=min(tf.z,MAX_TESSELLATION_FACTORS);
    result.inside=min(tf.w,MAX_TESSELLATION_FACTORS);
    return result;
}
[maxtessfactor(MAX_TESSELLATION_FACTORS)]
[domain("tri")]
[partitioning("fractional_odd")]
[outputtopology("triangle_cw")]
[patchconstantfunc("LTAdaptiveHullConstant")]
[outputcontrolpoints(3)]
PackedVaryingsToDS LTAdaptiveHull(InputPatch<PackedVaryingsToDS,3> input,uint id:SV_OutputControlPointID)
{
    return input[id];
}

// Keep HDRP's complete domain path (including motion vectors and displacement).
// Carry the actual patch-constant decision after that path, so displacement cannot
// overwrite it. Vertex colour RGB is otherwise unchanged.
[domain("tri")]
PackedVaryingsToPS LTAdaptiveDomain(LTAdaptiveFactors factors,
    const OutputPatch<PackedVaryingsToDS,3> input,float3 baryCoords:SV_DomainLocation)
{
    TessellationFactors standard;
    standard.edge[0]=factors.edge[0];standard.edge[1]=factors.edge[1];
    standard.edge[2]=factors.edge[2];standard.inside=factors.inside;
    PackedVaryingsToPS result=Domain(standard,input,baryCoords);
#ifdef VARYINGS_NEED_COLOR
    if(_LTDebugCoverage>4.5 && _LTDebugCoverage<5.5)result.vmesh.interpolators5.w=factors.reason;
#endif
    return result;
}
