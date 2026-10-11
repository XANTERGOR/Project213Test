// Identical layout in all HDRP passes and the read-only graphics probe.
float4 _LTRoadBlendRect, _LTRoadBlendParams, _LTRoadBlendNoise;
float4 _LTRoadBlendDistances_TexelSize;
float4 _LTRoadBlendTiling, _LTRoadBlendTint, _LTRoadBlendSettings, _LTRoadBlendFlags;
float4x4 _LTRoadBlendWorldToMask;
float _LTRoadBlendSource, _LTRoadTerrainReady;
float4 _LTRoadTerrainGrid; // min chunk x/z, grid width/height
float4 _LTRoadTerrainCell; // terrain-local chunk width/depth
