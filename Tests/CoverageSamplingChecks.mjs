// Static equivalence to a frozen pre-optimization oracle; native rendering is separate.
import fs from 'node:fs';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
const root='Assets/TerrainSystem/LocalTerrain/';
const read=p=>fs.readFileSync(root+p,'utf8').replace(/\r\n/g,'\n');
const core=read('Shaders/LTLayerBlendCore.hlsl'),projection=read('Shaders/LTProjectedLayers.hlsl');
const reference=read('Editor/LTCoverageSamplingReference.hlsl');
assert.equal(crypto.createHash('sha256').update(reference).digest('hex'),'f7f14d902bbfa34b442bafc9f52ee069566f13ae997d825756923247770d2dbe',
    'The frozen pre-hoist oracle must not drift with production code');
function body(s,name){
    const start=s.indexOf('void '+name+'(');assert.ok(start>=0,name);
    let a=s.indexOf('{',start),i=a+1,depth=1;
    for(;depth;i++){assert.ok(i<s.length);if(s[i]==='{')depth++;if(s[i]==='}')depth--;}
    return s.slice(a+1,i-1);
}
const compact=s=>s.replace(/\/\/[^\n]*/g,'').replace(/\s+/g,'');
const old=body(reference,'LTReferenceLayersMappedFrame');
const start=old.indexOf('    float2 coverageUV = '),end=old.indexOf('    float weights[12]');
assert.ok(start>0&&end>start);
const reads=old.slice(start,end);
let expected=old.slice(0,start)+old.slice(end);
for(let slot=0;slot<12;slot++){
    const coords=`        float2 uv = position / max(_LTTiling${slot}.xy, .001) + _LTTiling${slot}.zw;\n        float2 dx=uvDx/max(_LTTiling${slot}.xy,.001),dy=uvDy/max(_LTTiling${slot}.xy,.001);`;
    const map=`LTRoadMappedUV(${slot},coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);`;
    const defaults=`        colors[${slot}] = 0; normals[${slot}] = float3(0,0,1); masks[${slot}] = float3(1,.5,_LTSettings${slot}.z);`;
    const before=coords+`\n        float2 roadRight;\n        ${map}\n`+defaults+`\n        if(weights[${slot}] > 0.00001)\n        {`;
    const after=`        float2 roadRight=float2(1,0);\n`+defaults+`\n        [branch] if(weights[${slot}] > 0.00001)\n        {\n`+coords.replaceAll('        ','            ')+`\n            ${map}`;
    assert.ok(expected.includes(before),'frozen slot '+slot);
    expected=expected.replace(before,after);
}
assert.equal(compact(body(core,'LTSampleLayersMappedFrameWithCoverage')),compact(expected),
    'Only coverage sharing and weight-gated UV preparation may change; preserve defaults, sampling and all blend math');
assert.equal((core.match(/\[branch\] if\(weights\[\d+\] > 0\.00001\)/g)||[]).length,12);
assert.equal((core.match(/LTRoadMappedUV\(/g)||[]).length,12);
const loader=body(core,'LTReadLayerCoverage');
assert.equal(compact(loader),compact('w0=0;w1=0;w2=0; [branch] if(_LTBaseOnly > .5)return;'+reads.replace(/float4 (w[012]) =/g,'$1 =')),
    'Coverage coordinates, border remapping, clamp sampler and explicit mip must be unchanged');
assert.equal((core.match(/SAMPLE_TEXTURE2D_LOD\(_LTWeights/g)||[]).length,3);
const newProjection=body(projection,'LTSampleProjectedLayersGrad');
assert.ok(newProjection.indexOf('LTReadLayerCoverage(')<newProjection.indexOf('for(int axis'));
assert.equal((newProjection.match(/LTReadLayerCoverage\(/g)||[]).length,1);
const ordinaryProjection=newProjection
    .replace(/#if defined\(LT_FAST_LAYER_PATH\)[\s\S]*?#endif \/\/ LT_FAST_LAYER_PATH/g,'')
    .replace(/#if defined\(LT_FAST_PREHEIGHT_PATH\)[\s\S]*?#endif \/\/ LT_FAST_PREHEIGHT_PATH/g,'');
const restored=ordinaryProjection.replace('    float4 w0,w1,w2;LTReadLayerCoverage(p.xz,w0,w1,w2);\n','')
    .replace('LTSampleLayersMappedFrameWithCoverage','LTReferenceLayersMappedFrame').replace('axis,w0,w1,w2,','axis,');
assert.equal(compact(restored),compact(body(reference,'LTReferenceProjectedLayers')),'Projection axes/order/normal blending changed');
const planar=body(core,'LTSampleLayersMappedFrame');
assert.ok(planar.includes('LTReadLayerCoverage(coveragePosition,w0,w1,w2)'));
assert.ok(planar.includes('w0,w1,w2,color,normalTS,ao,smoothness,metallic,gradientLocal'));
const generated=read('Shaders/LTRoadTerrainSampling.hlsl');
assert.equal((generated.match(/SAMPLE_TEXTURE2D_ARRAY_LOD\(_LTRoadTerrainWeights/g)||[]).length,3);
assert.ok(generated.includes('LTAutoReadLayerCoverage(tile,p.xz,w0,w1,w2)'));
assert.ok(generated.includes('tile*3u+2u'));
const check=read('Editor/LTCoverageSamplingCheck.cs'),probe=read('Editor/LTCoverageSamplingProbe.shader');
assert.ok(probe.includes('LTCoverageSamplingReference.hlsl')&&probe.includes('GetFullScreenTriangleVertexPosition(id)'));
for(const marker of ['sentinel','float.IsNaN','float.IsInfinity','tint sensitivity','Zero coverage','RunValidation()'])assert.ok(check.includes(marker),marker);
for(const marker of ['active road UV thresholds','new[]{0f,.000009f,.00001f,.000011f,.5f}','An unsampled flat normal acquired a slope','Tiny positive weight lost its default AO contribution','owner boundary'])assert.ok(check.includes(marker),marker);
assert.ok(!check.includes('SaveScene(')&&!check.includes('AssetDatabase.CreateAsset('));
const compression=read('Editor/LTLayerArrayCompressionCheck.cs');
assert.ok(compression.includes('Pixel(baker,surfacePass,6)')&&compression.includes('Probe did not render'));
console.log('PASS shared coverage + active road UV: exact frozen blend math/defaults; 12 weight-gated UV/map calls; 3 coverage reads before axis loop; planar + road adapter; native probe liveness contracts. No native GPU execution.');
console.log('Frozen oracle SHA256: '+crypto.createHash('sha256').update(reference).digest('hex'));
