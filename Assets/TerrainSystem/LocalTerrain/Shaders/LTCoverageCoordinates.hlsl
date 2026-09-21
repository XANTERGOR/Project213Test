#ifndef LT_COVERAGE_COORDINATES_INCLUDED
#define LT_COVERAGE_COORDINATES_INCLUDED
// HDRP positions are camera-relative; the authored crop is LTWorld-local XZ.
// UV fallback is for synthetic benchmark materials without a terrain transform.
float2 LTCoveragePosition(float3 positionRWS,float2 terrainUV)
{
    if(_LTCoverageUseWorldPosition>.5)
        return mul(_LTCoverageWorldToLocal,float4(GetAbsolutePositionWS(positionRWS),1)).xz;
    return terrainUV*_LTWorldSize.xy;
}
#endif
