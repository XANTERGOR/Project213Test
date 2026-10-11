// CPU/source contracts only. Actual sampler readback lives in TopTwoLayersGpuCheck.cs.
import fs from 'node:fs';
import assert from 'node:assert/strict';
const root='Assets/TerrainSystem/LocalTerrain/';
const read=p=>fs.readFileSync(root+p,'utf8');
let checks=0;
function check(value,message){checks++;assert.ok(value,message);}
const sum=a=>a.reduce((s,x)=>s+x,0);
function pair(raw,ids){return raw.map((w,i)=>i).filter(i=>raw[i]>0).sort((a,b)=>raw[b]-raw[a]||ids[a]-ids[b]||a-b).slice(0,2);}
function mixed(raw,ids,colors,pairColor){
    const selected=pair(raw,ids),mass=sum(selected.map(i=>raw[i]));
    let tail=0,c=[0,0,0];
    for(let i=0;i<12;i++){
        if(selected.includes(i)||raw[i]<=.00001)continue;
        c=c.map((x,k)=>x+colors[i][k]*raw[i]);tail+=raw[i];
    }
    return tail>0?c.map((x,k)=>(pairColor[k]*mass+x)/Math.max(mass+tail,.00001)):[...pairColor];
}
let seed=314159;
function random(){seed=(Math.imul(seed,1664525)+1013904223)>>>0;return seed/4294967296;}
for(let test=0;test<10000;test++){
    const raw=Array.from({length:12},()=>random()<.4?0:random());
    const ids=Array.from({length:12},(_,i)=>i),colors=raw.map(()=>Array.from({length:3},()=>random()*4));
    const selected=pair(raw,ids),pairColor=Array.from({length:3},()=>random()*4);
    const actual=mixed(raw,ids,colors,pairColor),mass=sum(selected.map(i=>raw[i]));
    const tail=raw.map((w,i)=>selected.includes(i)||w<=.00001?0:w),total=mass+sum(tail);
    const expected=sum(tail)>0?pairColor.map((c,k)=>c*mass/total+sum(tail.map((w,i)=>w*colors[i][k]/total))):pairColor;
    check(actual.every((x,k)=>Number.isFinite(x)&&Math.abs(x-expected[k])<1e-12),'convex weighted-color oracle');
    const min=Array.from({length:3},(_,k)=>Math.min(pairColor[k],...colors.filter((_,i)=>tail[i]>0).map(c=>c[k])));
    const max=Array.from({length:3},(_,k)=>Math.max(pairColor[k],...colors.filter((_,i)=>tail[i]>0).map(c=>c[k])));
    check(actual.every((x,k)=>x>=min[k]-1e-12&&x<=max[k]+1e-12),'no overbrightening or HDR clamp');
    const reverse=a=>a.toReversed();
    check(mixed(reverse(raw),reverse(ids),reverse(colors),pairColor).every((x,k)=>Math.abs(x-actual[k])<1e-12),'palette slot permutation invariance');
    const two=raw.map((w,i)=>selected.includes(i)?w:0);
    check(mixed(two,ids,colors,pairColor).every((x,k)=>x===pairColor[k]),'strict pair exact no-op');
}
const binding=read('LTPaintRuntime.cs'),world=read('LTLayerRenderWorld.cs'),ui=read('Editor/LTLayerRenderEditor.cs');
check(world.includes('public bool fastLayerColorRemainder;'),'serialized opt-in defaults OFF');
check(binding.includes('world.fastLayerTopTwo&&world.fastLayerTopTwoBeforeHeight?(world.fastLayerColorRemainder?2:1):0'),'remainder requires both parent options');
check(binding.includes('enabled&&parameters.z>.5f&&parameters.w>1.5f'),'keyword follows enabled pre-height/color mode');
check(binding.includes('else material.DisableKeyword("_LT_FAST_COLOR_REMAINDER")'),'stale keyword cleared when mode disabled');
check(ui.includes('DisabledScope(!beforeHeight)')&&ui.includes('Остальные слои — только цвет'),'inspector dependency and label');
for(const shader of ['Resources/LTEightLayers.shader','Resources/LTEightLayersTessellation.shader','Editor/LTCoverageSamplingProbe.shader'])
    check(read(shader).includes('#pragma multi_compile_local _ _LT_FAST_COLOR_REMAINDER'),'compiled keyword declared: '+shader);
for(const shader of ['Resources/LTGlobalLayerBake.shader','Resources/LTRockSurfaceBake.shader'])
    check(!read(shader).includes('_LT_FAST_COLOR_REMAINDER'),'global/rock bake unchanged: '+shader);
const adapter=read('Shaders/LTRoadTerrainSampling.hlsl');
check(adapter.includes('_LTRoadTerrainChunks[tile].fastLayerParams.w<1.5'),'road per-tile enable gate');
check(adapter.includes('LTAutoBlendPairRemainderColor(tile,first,second,w0,w1,w2'),'road adapter dispatches remainder');
check(read('LTRoadTerrainBlendCache.cs').includes('m.GetVector("_LTFastLayerParams")'),'road cache carries the same mode vector');
check(read('LTPaintRockRendering.cs').includes('state.paint.fastLayerParams'),'rocks share mode binding');
console.log(`PASS ${checks} color-only remainder CPU/source assertions; not Unity/GPU or FPS.`);
