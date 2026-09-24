#ifndef LT_DETAIL_INDIRECT_INCLUDED
#define LT_DETAIL_INDIRECT_INCLUDED
// Included inside HDRP ShaderVariables, AFTER UnityInstancing and BEFORE
// ShaderVariablesFunctions/SpaceTransforms. Otherwise those functions capture
// the original matrix macros and all instances use the draw's identity matrix.
#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
#define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
#include "UnityIndirect.cginc"
struct LTDetailGpuData
{
    float4x4 objectToWorld;
    float4x4 worldToObject;
    float4 positionSize;
    float4 lodPositionRandom;
    float4 center;
    float4 extents;
};
StructuredBuffer<LTDetailGpuData> _LTInstances;
StructuredBuffer<uint> _LTVisible;
float4x4 _LTPartToRoot, _LTRootToPart;
float _LTForceNoMotion;
static float4x4 LTObjectToWorld, LTWorldToObject;
static float4 LTWorldTransformParams;
void LTSetupDetail()
{
    InitIndirectDrawArgs(0);
    uint index = _LTVisible[GetIndirectInstanceID(unity_InstanceID)];
    LTDetailGpuData data = _LTInstances[index];
    LTObjectToWorld = mul(data.objectToWorld, _LTPartToRoot);
    LTWorldToObject = mul(_LTRootToPart, data.worldToObject);
    LTWorldTransformParams = float4(0,0,0,determinant((float3x3)LTObjectToWorld)<0 ? -1 : 1);
}
#undef UNITY_MATRIX_M
#undef UNITY_MATRIX_I_M
#undef UNITY_PREV_MATRIX_M
#undef UNITY_PREV_MATRIX_I_M
#define UNITY_MATRIX_M ApplyCameraTranslationToMatrix(LTObjectToWorld)
#define UNITY_MATRIX_I_M ApplyCameraTranslationToInverseMatrix(LTWorldToObject)
// Roots are static. Never infer history from the append-buffer slot: GPU order
// is not stable. HDRP still evaluates previous vertex deformation/camera motion.
#define UNITY_PREV_MATRIX_M ApplyCameraTranslationToMatrix(LTObjectToWorld)
#define UNITY_PREV_MATRIX_I_M ApplyCameraTranslationToInverseMatrix(LTWorldToObject)
#undef unity_MotionVectorsParams
#define unity_MotionVectorsParams float4(0,1-_LTForceNoMotion,0,0)
#undef unity_WorldTransformParams
#define unity_WorldTransformParams LTWorldTransformParams
#undef unity_LODFade
#define unity_LODFade float4(1,1,0,0)
#endif
#endif
