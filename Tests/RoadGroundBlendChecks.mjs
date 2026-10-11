import fs from 'node:fs';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
const root='Assets/TerrainSystem/LocalTerrain/';
const read=p=>fs.readFileSync(p,'utf8').replace(/\r\n/g,'\n');
const sourcePath='Assets/Data/Models/Environment/Shaders/Road_Shader.shader';
const raw=fs.readFileSync(sourcePath),source=read(sourcePath);
assert.equal(crypto.createHash('sha256').update(raw).digest('hex').toUpperCase(),'3BF057C6AC6220262EC42D5ECFA2DF3A2F80BB1555FC06365BA4EC0E2ED61D30');
const properties=read(root+'Resources/LTEightLayers.shader').match(/\[HideInInspector\] _LTRoadBlendDistances[\s\S]*?(?=\s*\[HideInInspector\] _LTRockColorMap)/)[0];
let generated=source.slice(0,source.indexOf('/*ASEBEGIN'));
generated='// Generated owned derivative of Assets/Data/Models/Environment/Shaders/Road_Shader.shader.\n// Source SHA256: 3BF057C6AC6220262EC42D5ECFA2DF3A2F80BB1555FC06365BA4EC0E2ED61D30\n// Original shader/materials are not modified. Regenerate this adapter if the source changes.\n'+generated.replace('Shader "RoadShader"','Shader "Local Terrain/Authored Road Ground HDRP"');
generated=generated.replace(/Properties\s*\{/,'Properties\n\t{\n'+properties);
generated=generated.replace(/#pragma target 4.5/g,'#pragma target 4.5\n            #pragma shader_feature_local _ _LT_ROAD_TERRAIN');
generated=generated.replace(/CBUFFER_START\( UnityPerMaterial \)/g,'CBUFFER_START( UnityPerMaterial )\n#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadGroundProperties.hlsl"\nfloat4x4 _LTSurfaceWorldToLocal;');
generated=generated.replace(/(\s*)(void BuildSurfaceData\(FragInputs[^]*?\))\s*\{/g,(_,space,sig)=>space+'#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadAuthoredGroundSurface.hlsl"\n'+space+sig+'\n\t\t\t{\n                LTBlendAuthoredRoad(fragInputs, surfaceDescription);');
assert.equal(read(root+'Resources/LTRoadAuthoredGround.shader').trimEnd(),generated.trimEnd(),'Owned adapter must differ ONLY in blend hooks/properties/name; original ASE computations preserved');
assert.equal((generated.match(/LTBlendAuthoredRoad\(fragInputs/g)||[]).length,6);
assert.equal((generated.match(/LTRoadGroundProperties.hlsl/g)||[]).length,7);
for(const p of ['LTRoadGroundBlend.cs','LTRoadTerrainFallback.cs','Editor/LTRoadBlendMask.cs','Editor/LTRoadGroundBlendBuild.cs','Editor/LTRoadGroundBlendCheck.cs','Editor/LTRoadBatcherWait.cs','Shaders/LTRoadGroundProperties.hlsl','Shaders/LTRoadGroundSurface.hlsl','Shaders/LTRoadAuthoredGroundSurface.hlsl','Editor/LTRoadGroundProbe.shader','Resources/LTRoadAuthoredGround.shader'])
    assert.match(read(root+p+'.meta'),/^guid: [0-9a-f]{32}$/m);
const build=read(root+'Editor/LTRoadGroundBlendBuild.cs'),runtime=read(root+'LTRoadGroundBlend.cs');
assert.ok(build.includes('source.levels[0]'),'Mask only reads LOD0');
assert.ok(!runtime.includes('sharedMaterial'),'Runtime does not mutate saved material assets');
assert.ok(read(root+'Editor/LTRoadGroundBlendCheck.cs').includes('no scene changes'));
assert.ok(read(root+'Editor/LTRoadGroundBlendCheck.cs').includes('probe.SetVector("_LTProbeNormal",Vector3.up)'),'Copied test materials need an explicit normal');
const groundSurface=read(root+'Shaders/LTRoadGroundSurface.hlsl');
assert.ok(!groundSurface.includes('if(_LTRoadBlendSource'),'Source selection must be compile-time, not a sampler-retaining runtime branch');
assert.match(groundSurface,/#if defined\(_LT_ROAD_TERRAIN\)\n#include [^\n]*LTRoadTerrainSampling.hlsl[^\n]*\n#else\n#if !defined\(LT_ROAD_GRAPH_PROPERTIES\)\nTEXTURE2D\(_LTRoadBlendColor\)/);
// Every scalar/vector material field must have a ShaderLab property. Matrices
// are supplied separately, and texture TexelSize is synthesized by Unity.
const fields=[...read(root+'Shaders/LTRoadGroundProperties.hlsl').matchAll(/^float(?:4)?\s+([^;]+);/gm)]
    .flatMap(m=>m[1].split(',').map(n=>n.trim())).filter(n=>!n.endsWith('_TexelSize'));
for(const file of ['Resources/LTEightLayers.shader','Resources/LTRoadAuthoredGround.shader','Editor/LTRoadGroundProbe.shader']){
    const props=read(root+file).split(/\bSubShader\b/)[0];
    for(const name of fields)assert.match(props,new RegExp('\\b'+name+'\\s*\\('),file+' SRP Batcher property '+name);
}
const fallback=read(root+'LTRoadTerrainFallback.cs'),cache=read(root+'LTRoadTerrainBlendCache.cs');
const native=read(root+'Editor/LTRoadTerrainBlendCheck.cs');
assert.ok(cache.includes('if(!Ready){LTRoadTerrainFallback.Bind(block);return;}'),'Unready cache must bind safe SRVs');
assert.ok(runtime.includes('else LTRoadTerrainFallback.Bind(block);'),'Missing world cache must bind safe SRVs');
assert.ok(fallback.includes('Shader.SetGlobalBuffer("_LTRoadTerrainChunks",chunks)')&&fallback.includes('Shader.SetGlobalBuffer("_LTRoadTerrainLayers",layers)'),'Default draws need global SRVs');
assert.ok(fallback.includes('beforeAssemblyReload+=Release')&&fallback.includes('EditorApplication.quitting+=Release'),'Editor domain owns default GPU resources');
assert.ok(native.includes('GetSRPBatcherCompatibilityCode')&&native.includes('logMessageReceivedThreaded+=Capture'),'Native test must inspect batching and graphics diagnostics');
assert.ok(native.includes('if(++updates<2)return;'),'Do not announce PASS before deferred driver diagnostics');
assert.ok(!native.includes('SetPass(0)')&&native.includes('material.FindPass("GBuffer")'),'Inspect the surface pass, not terrain ScenePicking');
assert.ok(native.includes('wait.Tick(EditorApplication.timeSinceStartup')&&native.includes('INCONCLUSIVE (not a PASS)'),'Uninitialized native data requires bounded, non-blocking retries');
for(const hook of ['AssemblyReloadEvents.beforeAssemblyReload','EditorApplication.quitting','EditorApplication.playModeStateChanged']){
    assert.ok(native.includes(hook+'+=')&&native.includes(hook+'-='),'Clean native inspection lifecycle: '+hook);
}
assert.ok(native.includes('!batcherMaterials.Contains(owned[i] as Material)')&&native.includes('ReleaseMaterials();VerifyUnchanged();'),'Retain only inspection materials and verify protected state at completion');
assert.ok(native.includes('finally{ShaderUtil.allowAsyncCompilation=async;}'),'Restore editor compilation state between native polls');
const diagnostic=read(root+'Editor/LTRoadGroundSceneCheck.cs');
assert.ok(!diagnostic.includes('material.GetMatrix('),'Diagnostics must read renderer property-block matrices, not nonserialized material matrix properties');
assert.ok(diagnostic.includes('block.GetMatrix("_LTRoadBlendWorldToMask")'),'Inspect the actual renderer binding');
assert.ok(read(root+'LTGlobalBakeAsset.cs').includes('Number((int)road.GroundProjection)'),'Saved terrain textures must invalidate when the effective ground projection changes');
console.log('PASS static road blend: exact authored shader preservation (6 surface hooks/7 identical cbuffers), source SHA, valid GUIDs, LOD0 mask, nonmutating runtime. Not a GPU shader compile.');
