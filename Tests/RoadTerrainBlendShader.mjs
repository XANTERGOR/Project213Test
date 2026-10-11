// Mechanical adapter of the production terrain sampler: no hand-maintained copy
// of height blending, spline UVs, normal decoding or asphalt crack math.
import fs from 'node:fs';
import assert from 'node:assert/strict';
const root='Assets/TerrainSystem/LocalTerrain/Shaders/';
const read=n=>fs.readFileSync(root+n,'utf8').replace(/\r\n/g,'\n');
function removeFunction(s,name,replacement=''){
    const start=s.search(new RegExp('^\\w+ '+name+'\\(','m'));
    assert.ok(start>=0,name);let i=s.indexOf('{',start),depth=1;
    for(i++;depth;i++){if(s[i]==='{')depth++;if(s[i]==='}')depth--;}
    return s.slice(0,start)+replacement+s.slice(i);
}
// Automatic road terrain uses a per-tile uniform switch, since the road renderer
// can span chunks/worlds with different modes. Keep the ordinary sampler exact.
let core=read('LTLayerBlendCore.hlsl');
const start=core.indexOf('#if defined(_LT_FAST_LAYERS)');
const middle=core.indexOf('#else\nvoid LTSampleLayersMappedFrameWithCoverage',start);
const end=core.indexOf('#endif // Separate compiled implementation',middle);
assert.ok(start>=0&&middle>start&&end>middle);
const fn='LTSampleLayersMappedFrameWithCoverage';
const fast=read('LTFastLayerBlend.hlsl')
    .replace('#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTFastLayerPair.hlsl"',read('LTFastLayerPair.hlsl'))
    .replaceAll('#if !defined(_LT_FAST_PREHEIGHT)','#if 1').replaceAll('#if defined(_LT_FAST_PREHEIGHT)','#if 0')
    .replaceAll('#if defined(_LT_FAST_COLOR_REMAINDER)','#if 1')
    .replaceAll('#if defined(_LT_FAST_ROADS)','#if 1').replaceAll(fn,fn+'Fast')
    // Buffer-indexed palettes plus both selectable implementations otherwise cause
    // pathological FXC optimization. Texture derivatives are explicit in these loops.
    .replaceAll('[unroll] for','[loop] for');
const legacy=core.slice(middle+'#else\n'.length,end).replaceAll(fn,fn+'Full');
const wrapper=`void ${fn}(float2 coveragePosition,float2 coverageDx,float2 coverageDy,
    float2 position,float2 uvDx,float2 uvDy,int projectionAxis,float4 w0,float4 w1,float4 w2,
    out float3 color,out float3 normalTS,out float ao,out float smoothness,out float metallic,out float3 gradientLocal)
{
    [branch] if(_LTFastLayerParams.x>=0 && _LTFastLayerParams.z>.5 && _LTFastLayerParams.w>.5 && _LTBaseOnly<=.5)
    {
        LTLayerPairData first,second;LTPrepareLayerPair(w0,w1,w2,first,second);
        LTSampleLayerPair(first,second,coveragePosition,coverageDx,coverageDy,position,uvDx,uvDy,projectionAxis,
            color,normalTS,ao,smoothness,metallic,gradientLocal);
        LTBlendPairRemainderColor(first,second,w0,w1,w2,coveragePosition,coverageDx,coverageDy,position,uvDx,uvDy,color);return;
    }
    [branch] if(_LTFastLayerParams.x>=0)
        ${fn}Fast(coveragePosition,coverageDx,coverageDy,position,uvDx,uvDy,projectionAxis,w0,w1,w2,color,normalTS,ao,smoothness,metallic,gradientLocal);
    else
        ${fn}Full(coveragePosition,coverageDx,coverageDy,position,uvDx,uvDy,projectionAxis,w0,w1,w2,color,normalTS,ao,smoothness,metallic,gradientLocal);
}
`;
core=core.slice(0,start)+fast+'\n'+legacy+wrapper+core.slice(core.indexOf('\n',end)+1);
let projected=read('LTProjectedLayers.hlsl')
    .replaceAll('#if defined(_LT_FAST_COLOR_REMAINDER)','#if 1')
    .replace('#if defined(LT_FAST_LAYER_PATH)','if(_LTFastLayerParams.x>=0)\n{').replace('#endif // LT_FAST_LAYER_PATH','}')
    .replace('#if defined(LT_FAST_PREHEIGHT_PATH)','[branch] if(_LTFastLayerParams.x>=0 && _LTFastLayerParams.z>.5 && _LTFastLayerParams.w>.5)\n{')
    .replace('#endif // LT_FAST_PREHEIGHT_PATH','}');
let code=[read('LTRoadProjection.hlsl'),read('LTRoadAsphalt.hlsl'),core,projected].join('\n');
code=removeFunction(code,'LTLoadPairLayer',`void LTLoadPairLayer(int slot,float raw,out LTLayerPairData layer)
{
    LTRoadTerrainLayer source=_LTRoadTerrainLayers[tile*12u+(uint)slot];
    layer.slot=slot;layer.raw=raw;layer.slice=source.indices.x;
    layer.tiling=source.tiling;layer.tint=source.tint;layer.settings=source.settings;layer.flags=source.flags;
}`);
// Production and adapter are included together by probes and some road shaders.
code=code.replace(/\bLTLayerPair(Data|Sample)\b/g,'LTAutoLayerPair$1');
code=code.replace(/^#include .*\n/gm,'').replace(/^#ifndef LT_ROAD_(?:PROJECTION|ASPHALT)_INCLUDED\n#define LT_ROAD_(?:PROJECTION|ASPHALT)_INCLUDED\n/gm,'');
// Remove only those two include-guard endings, retaining shader-stage conditionals.
code=code.replace(/\n#endif\n(?=\n?(?:TEXTURE2D_ARRAY\(_LTAsphaltCracks\)|\/\/ Three world-shared))/g,'\n');
code=removeFunction(code,'LTRoadDisplacementMultiplier');code=removeFunction(code,'LTRoadDisplacementUV');
code=code.replace(/^TEXTURE2D\(_LTRoadSuppressionMap\);\n/gm,'');
for(const [fn,body] of [
    ['LTLayerSlice','return _LTRoadTerrainLayers[tile*12u+(uint)slot].indices.x;'],
    ['LTRoadSlot','return _LTRoadTerrainLayers[tile*12u+(uint)slot].indices.y;'],
    ['LTAsphaltStyle','return _LTRoadTerrainLayers[tile*12u+(uint)slot].asphaltStyle;']]){
    const type=fn==='LTAsphaltStyle'?'float4':'float';
    code=removeFunction(code,fn);code=type+' '+fn+'(int slot) { '+body+' }\n'+code;
}
code=code.replace(/^TEXTURE2D\(_LTWeights[012]\);\n/gm,'');
code=code.replace(/SAMPLE_TEXTURE2D_LOD\(_LTWeights([012]), sampler_LinearClamp, coverageUV, 0\)/g,
    (_,i)=>`SAMPLE_TEXTURE2D_ARRAY_LOD(_LTRoadTerrainWeights, sampler_LinearClamp, coverageUV, tile*3u+${i}u, 0)`);
code=code.replace(/\b_LT(Tiling|Tint|Settings|Flags)(\d+)\b/g,(_,field,slot)=>`_LTRoadTerrainLayers[tile*12u+${slot}u].${field.toLowerCase()}`);
for(const [from,to] of Object.entries({_LTRect:'_LTRoadTerrainChunks[tile].rect',_LTBaseOnly:'_LTRoadTerrainChunks[tile].options.z',_LTHeightBlend:'_LTRoadTerrainChunks[tile].options.x',_LTRoadProjectionEnabled:'_LTRoadTerrainChunks[tile].options.w',_LTFastLayerParams:'_LTRoadTerrainChunks[tile].fastLayerParams'}))
    code=code.replace(new RegExp('\\b'+from+'\\b','g'),to);
for(const [from,to] of Object.entries({_LTColorArray:'_LTRoadTerrainColor',_LTNormalArray:'_LTRoadTerrainNormal',_LTMaskArray:'_LTRoadTerrainMask',_LTRoadProjectionMap:'_LTRoadTerrainProjection',_LTAsphaltCracks:'_LTRoadTerrainCracks',_LTAsphaltDamage:'_LTRoadTerrainDamage'}))
    code=code.replaceAll(from,to);
const names=[...code.matchAll(/^(?:float\d?|void) (LT\w+)\(/gm)].map(m=>m[1]);
for(const name of new Set(names))code=code.replace(new RegExp('\\b'+name+'\\(','g'),'LTAuto'+name.slice(2)+'(tile,');
code=code.replace(/^(float\d?|void) (LTAuto\w+)\(tile,/gm,'$1 $2(uint tile,');
// With buffer-addressed palettes FXC cannot fully unroll 3 copies of the 12-layer
// sampler. Keep the identical axis order/math in a real loop (explicit gradients).
code=code.replaceAll('[unroll] for(int axis=0;axis<3;axis++)','[loop] for(int axis=0;axis<3;axis++)');
code=code.replaceAll('float3 c,tn,g;float a,s,m;','float3 c=0,tn=0,g=0;float a=1,s=0,m=0;');
// LTLayerTextureArrays applies identical filtering/wrap/aniso to all three arrays.
// The road cache verifies that contract before sharing the color sampler.
code=code.replace('SAMPLER(sampler_LTRoadTerrainNormal);\nSAMPLER(sampler_LTRoadTerrainMask);',
    '#define sampler_LTRoadTerrainNormal sampler_LTRoadTerrainColor\n#define sampler_LTRoadTerrainMask sampler_LTRoadTerrainColor');
assert.ok(!code.includes('_LTWeights')&&!code.includes('_LTLayerSlices'));
const header=`// GENERATED by Tests/RoadTerrainBlendShader.mjs from the live terrain sampling sources.
// Adapts per-chunk addressing/context and shares identically configured layer samplers.
// Check this file against the sources whenever terrain blend math changes.
#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTLayerNormal.hlsl"
struct LTRoadTerrainChunk { float4 rect; float4 options; float4 fastLayerParams; };
struct LTRoadTerrainLayer { float4 tiling; float4 tint; float4 settings; float4 flags; float4 indices; float4 asphaltStyle; };
StructuredBuffer<LTRoadTerrainChunk> _LTRoadTerrainChunks;
StructuredBuffer<LTRoadTerrainLayer> _LTRoadTerrainLayers;
TEXTURE2D(_LTRoadTerrainLookup);
TEXTURE2D_ARRAY(_LTRoadTerrainWeights);
`;
const tail=`
int LTRoadTerrainTile(float2 p)
{
    if(_LTRoadTerrainReady<.5)return -1;
    float2 relative=p/_LTRoadTerrainCell.xy-_LTRoadTerrainGrid.xy;
    if(any(relative<0)||any(relative>_LTRoadTerrainGrid.zw))return -1;
    int2 cell=min((int2)floor(relative),(int2)_LTRoadTerrainGrid.zw-1);
    return (int)LOAD_TEXTURE2D(_LTRoadTerrainLookup,cell).r-1;
}
void LTSampleRoadTerrain(float3 p,float3 normalWS,float3 dx,float3 dy,
    out float3 color,out float3 gradient,out float ao,out float smoothness,out float metallic)
{
    int tile=LTRoadTerrainTile(p.xz);
    color=0;gradient=0;ao=1;smoothness=0;metallic=0;
    if(tile<0)return;
    LTAutoSampleProjectedLayersGrad((uint)tile,p,normalWS,_LTRoadTerrainChunks[tile].options.y>.5,dx,dy,color,gradient,ao,smoothness,metallic);
}
`;
const generated=header+code+tail;
const target=root+'LTRoadTerrainSampling.hlsl';
if(process.argv.includes('--write')){
    fs.writeFileSync(target,generated);
}else if(process.argv.includes('--patch')){
    console.log('*** Begin Patch\n*** Add File: '+target+'\n'+generated.split('\n').map(s=>'+'+s).join('\n')+'\n*** End Patch');
}else{
    assert.equal(fs.readFileSync(target,'utf8').replace(/\r\n/g,'\n').trimEnd(),generated.trimEnd());
    console.log('PASS exact terrain sampling adapter: 12 layers, height blend, projected normals, spline ownership and asphalt damage math. Not a shader compile.');
}
