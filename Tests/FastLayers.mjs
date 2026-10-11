// CPU oracle for the bounded blend/projection policy + production wiring checks.
// This is NOT a GPU test; the editor menu compares the actual HLSL to a frozen shader.
import assert from 'node:assert/strict';
import fs from 'node:fs';
const root='Assets/TerrainSystem/LocalTerrain/';
const read=p=>fs.readFileSync(root+p,'utf8');
let checks=0;function check(v,s){checks++;assert.ok(v,s);}
const sum=w=>w.reduce((a,b)=>a+b,0);
const normalize=w=>w.map(x=>x/Math.max(sum(w),.00001));
const clamp=(v,a,b)=>Math.max(a,Math.min(b,v));
function height(raw,heights,blend){
    const max=Math.max(-1,...raw.map((w,i)=>w>.00001?heights[i]+w:-1));
    return normalize(raw.map((w,i)=>w*(1-blend+blend*clamp((heights[i]+w-max+.2)/.2,0,1))));
}
function sparse(weights,budget){
    let first=0,second=0,third=0;
    for(const w of weights){third=Math.max(third,Math.min(second,w));second=Math.max(second,Math.min(first,w));first=Math.max(first,w);}
    const cutoff=Math.min(third,clamp(budget,0,.15)/12);
    const next=weights.map(w=>Math.max(0,w-cutoff));
    return {weights:normalize(next),removed:sum(weights)-sum(next)};
}
function projection(normal,cutoff){const length=Math.hypot(...normal);return normalize(normalize(normal.map(n=>Math.abs(n/length)**4)).map(w=>Math.max(0,w-cutoff)));}
const pad=w=>[...w,...Array(12-w.length).fill(0)];
check(sparse(pad([.998,.001,.001]),.03).weights[0]===1,'Tiny contributions really disappear');
check(sparse(pad([.5,.5]),.15).weights[0]===.5,'Two-layer blend remains exact');
check(sparse(pad([1/3,1/3,1/3]),.15).weights.filter(x=>x>0).length===3,'Important triple junction is not truncated');
const weakWinner=height(pad([.45,.45,.1]),pad([0,0,1]),1);
check(weakWinner[2]===1,'Small coverage, high height must win BEFORE selection');
check(sparse(weakWinner,.15).weights[2]===1,'Height winner survives simplification');
let seed=923812;
function random(){seed=(Math.imul(seed,1664525)+1013904223)>>>0;return seed/4294967296;}
for(let trial=0;trial<20000;trial++){
    const raw=normalize(Array.from({length:12},()=>random()<.6?0:random()));
    const weights=height(raw,Array.from({length:12},random),random());
    const budget=random()*.15,{weights:actual,removed}=sparse(weights,budget);
    const ordered=weights.toSorted((a,b)=>b-a),cutoff=Math.min(ordered[2],budget/12);
    const oracle=normalize(weights.map(w=>Math.max(0,w-cutoff)));
    check(actual.every((x,i)=>Math.abs(x-oracle[i])<1e-12),'Ranking matches independent sort oracle');
    check(actual.every(Number.isFinite)&&actual.every(x=>x>=0),'Non-negative finite blend');
    check(removed<=budget+1e-12,'Removed weight respects budget');
    check(sum(weights)===0||Math.abs(sum(actual)-1)<1e-10,'Normalized non-empty blend');
    check(sparse(weights,0).weights.every((x,i)=>Math.abs(x-weights[i])<1e-10),'Zero budget parity');
    // For color/AO in [0,1], total variation bounds change in any convex mixture.
    const tv=sum(actual.map((x,i)=>Math.abs(x-weights[i])))/2;
    check(tv<=budget/Math.max(1-budget,.01)+1e-10,'Bounded output error for unit-range channels');
    const perturbed=normalize(weights.map((w,i)=>Math.max(0,w+(i===0?1e-7:i===1?-1e-7:0))));
    const next=sparse(perturbed,budget).weights;
    check(sum(weights)===0||sum(next.map((x,i)=>Math.abs(x-actual[i])))<.0001,'No rank-swap/active-count discontinuity on a covered surface');
    const axes=projection([random()-.5,random()-.5,random()-.5],random()*.15);
    check(axes.every(Number.isFinite)&&Math.abs(sum(axes)-1)<1e-10,'Projection always normalized, including triple corners');
}
check(projection([.1,1,.1],.025).filter(x=>x>0).length===1,'Gentle slope uses one axis');
check(projection([1,1,0],.025).filter(x=>x>0).length===2,'Diagonal slope uses two axes');
check(projection([1,1,1],.025).filter(x=>x>0).length===3,'Corner retains three axes, no biplanar pinch');
const fast=read('Shaders/LTFastLayerBlend.hlsl'),core=read('Shaders/LTLayerBlendCore.hlsl');
check(fast.indexOf('mask=SAMPLE_TEXTURE2D_ARRAY_GRAD',fast.indexOf('float raw[12]'))<fast.indexOf('float total=0'),'Masks before final weights');
check(fast.indexOf('float total=0')<fast.indexOf('c=SAMPLE_TEXTURE2D_ARRAY_GRAD'),'Weights before color fetches');
check(fast.includes('#if defined(_LT_FAST_ROADS)')&&fast.includes('LTRoadMappedUV')&&fast.includes('LTApplyAsphaltCracks'),'Optional road variant preserves spline/cracks');
check(fast.includes('clamp(_LTFastLayerParams.x,0,.15)/12'),'Production bounded threshold matches math oracle');
check(core.includes('#if defined(_LT_FAST_LAYERS)')&&core.includes('#else\nvoid LTSampleLayersMappedFrameWithCoverage'),'Separate compile-time paths');
const bind=read('LTPaintRuntime.cs');
check(bind.includes('enabled=world.FastLiveLayersActive;'),'Opt-in includes displaced terrain chunks');
check(bind.includes('roadProjections.TryGetValue(state,out _)||asphaltStates.TryGetValue(state,out _)'),'Road support has its own compiled variant');
check(bind.includes('BindFastLayers(world,state);')&&read('LTPaintRocks.cs').includes('BindFastLayers(world,state);'),'Terrain and rock layers follow settings');
check(read('Resources/LTEightLayers.shader').includes('#pragma multi_compile_local _ _LT_FAST_LAYERS'),'Runtime-toggle variant included in builds');
check(!read('Resources/LTGlobalLayerBake.shader').includes('_LT_FAST_LAYERS')&&!read('Resources/LTRockSurfaceBake.shader').includes('_LT_FAST_LAYERS'),'Far atlas bakers unchanged');
console.log(`PASS ${checks} fast-layer CPU/source assertions. Not Unity import, GPU readback or FPS.`);
