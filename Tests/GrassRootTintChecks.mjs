import fs from 'node:fs';
import assert from 'node:assert/strict';

const graphPath='Assets/DA/Shared Assets/DA_Simple_Wind/DA_Grass_WIND.shadergraph';
const parse=text=>text.trim().split(/(?=^\{)/m).filter(x=>x.trim()).map(x=>JSON.parse(x));
const objects=parse(fs.readFileSync(graphPath,'utf8'));
const byId=new Map(objects.map(o=>[o.m_ObjectId,o]));
assert.equal(byId.size,objects.length,'unique graph object IDs');
const graph=objects[0];
for(const n of graph.m_Nodes){const node=byId.get(n.m_Id);assert.ok(node,'node reference');for(const s of node.m_Slots)assert.ok(byId.has(s.m_Id),'slot reference');}
for(const edge of graph.m_Edges)for(const endpoint of [edge.m_InputSlot,edge.m_OutputSlot]){
  const node=byId.get(endpoint.m_Node.m_Id);assert.ok(node);
  assert.ok(node.m_Slots.some(s=>byId.get(s.m_Id).m_Id===endpoint.m_SlotId),'edge targets actual slot');
}
const property=name=>objects.find(o=>o.m_OverrideReferenceName===name);
for(const name of ['_LTGrassRootTint','_LTGrassRootStrength','_LTGrassRootBase','_LTGrassRootHeight']){
  const p=property(name);assert.ok(p?.m_GeneratePropertyBlock);assert.ok(graph.m_Properties.some(r=>r.m_Id===p.m_ObjectId));
}
assert.equal(property('_LTGrassRootStrength').m_Value,0,'legacy materials neutral');
assert.equal(property('_LTGrassRootTint').m_SGVersion,3,'correct color-space version');
const fn=objects.find(o=>o.m_FunctionName==='LTGrassRootTint');assert.ok(fn);assert.equal(fn.m_SourceType,0);
const includeGuid=fs.readFileSync('Assets/TerrainSystem/LocalTerrain/Shaders/LTGrassRootTint.hlsl.meta','utf8').match(/guid: (\w+)/)[1];
assert.equal(fn.m_FunctionSource,includeGuid);
const input=(node,port)=>{
  const edges=graph.m_Edges.filter(e=>e.m_InputSlot.m_Node.m_Id===node.m_ObjectId&&e.m_InputSlot.m_SlotId===port);
  assert.equal(edges.length,1,'one incoming edge');return {node:byId.get(edges[0].m_OutputSlot.m_Node.m_Id),port:edges[0].m_OutputSlot.m_SlotId};
};
const base=objects.find(o=>o.m_Name==='SurfaceDescription.BaseColor');assert.equal(input(base,0).node,fn);
const terrainApply=input(fn,0).node;
assert.equal(terrainApply.m_FunctionName,'LTGrassTerrainApply');
const variationApply=input(terrainApply,0).node;
assert.equal(variationApply.m_FunctionName,'LTGrassVariationApply');
assert.equal(input(variationApply,0).node.m_ObjectId,'4730a1a4e97d4390ab86352570077eb1','original base map still sampled once');
const interpolator=input(fn,1).node;assert.equal(interpolator.m_Type,'UnityEditor.ShaderGraph.CustomInterpolatorNode');
assert.equal(interpolator.serializedType,1,'one scalar varying');
const block=objects.find(o=>o.m_SerializedDescriptor==='VertexDescription.LTGrassRestHeight#1');
assert.ok(graph.m_VertexContext.m_Blocks.some(n=>n.m_Id===block.m_ObjectId));
assert.equal(interpolator.customBlockNodeName,'LTGrassRestHeight');
const split=input(block,0);assert.equal(split.port,2,'Y component');assert.equal(split.node.m_Type,'UnityEditor.ShaderGraph.SplitNode');
const position=input(split.node,0).node;assert.equal(position.m_Type,'UnityEditor.ShaderGraph.PositionNode');assert.equal(position.m_Space,0,'mesh-local before wind');
assert.equal(input(objects.find(o=>o.m_Name==='VertexDescription.Position'),0).node.m_Name,'Global Wind Noise','wind path unchanged');

// Optionally compare every pre-existing graph object and edge to a per-edit backup.
const baselinePath=process.argv[2];
if(baselinePath){
  const old=parse(JSON.parse(fs.readFileSync(baselinePath,'utf8')).graphSource);
  for(const o of old.slice(1))assert.deepEqual(byId.get(o.m_ObjectId),o,'pre-existing object unchanged: '+o.m_ObjectId);
  for(const e of old[0].m_Edges){
    if(e.m_InputSlot.m_Node.m_Id===base.m_ObjectId)continue;
    assert.ok(graph.m_Edges.some(n=>JSON.stringify(n)===JSON.stringify(e)),'all non-albedo edges preserved');
  }
  assert.deepEqual(graph.m_FragmentContext,old[0].m_FragmentContext,'no alpha/lighting output changes');
  assert.deepEqual(graph.m_ActiveTargets,old[0].m_ActiveTargets,'render targets preserved');
  assert.equal(objects.filter(o=>o.m_Type==='UnityEditor.ShaderGraph.SampleTexture2DNode').length,old.filter(o=>o.m_Type==='UnityEditor.ShaderGraph.SampleTexture2DNode').length,'no additional texture samples');
}

const hlsl=fs.readFileSync('Assets/TerrainSystem/LocalTerrain/Shaders/LTGrassRootTint.hlsl','utf8');
assert.ok(!/SAMPLE_TEXTURE|tex2D|clip\s*\(/.test(hlsl),'no extra sampling/discard');
assert.ok(hlsl.includes('BaseColor * lerp(float3(1.0, 1.0, 1.0), saturate(RootTint), weight)'));
assert.ok(hlsl.includes('void LTGrassRootTint_half('));
// Execute the scalar expressions extracted from HLSL, not a second copy of the mask.
const scalar=hlsl.match(/float t =[^]*?float weight =[^;]+;/)[0]
  .replace(/\bfloat\b/g,'const').replace(/\bmax\(/g,'Math.max(');
const clamp=x=>Math.min(1,Math.max(0,x));
const weight=new Function('RestHeight','RootBase','RootHeight','Strength','saturate',scalar+'return weight;');
let checks=0;
for(const bottom of [-100,-.2,0,50])for(const height of [-1,0,1e-8,.01,.4,2,100])for(const strength of [-1,0,.1,.65,1,2]){
  let previous=Infinity;const width=Math.max(.0001,height);
  for(let i=0;i<=100;i++){
    const y=bottom+width*(i/50-.5),w=weight(y,bottom,height,strength,clamp);
    assert.ok(Number.isFinite(w)&&w>=0&&w<=1);assert.ok(w<=previous+1e-8,'monotonic fade');previous=w;
    const t=clamp((y-bottom)/width),expected=clamp(strength)*(1-(3*t*t-2*t*t*t));assert.ok(Math.abs(w-expected)<1e-7);
    if(i===25)assert.ok(Math.abs(w-clamp(strength))<1e-6,'root endpoint');
    if(i===75)assert.ok(Math.abs(w)<1e-6,'unmodified tips');
    checks++;
  }
}
// Same rest height is carried through scale, translation, orientation and wind;
// no world position or frame/time is a dependency of the custom interpolator.
for(const strength of [0,1])for(const tint of [[1,1,1],[.3,.2,.1]]){
  const w=weight(0,0,.4,strength,clamp),albedo=[.7,.45,.18];
  const result=albedo.map((c,i)=>c*(1+(clamp(tint[i])-1)*w));
  if(strength===0||tint.every(x=>x===1))assert.deepEqual(result,albedo);
}
const adapter=JSON.parse(fs.readFileSync('Assets/TerrainSystem/LocalTerrain/Resources/LTDetailGpu/GrassWind.ltdetailshader','utf8'));
assert.equal(adapter.sourceGuid,'09db06601f37f3a4da04a65182702fd6','existing indirect adapter still uses this graph');
const validator=fs.readFileSync('Assets/TerrainSystem/LocalTerrain/Editor/LTGrassRootTintCheck.cs','utf8');
assert.doesNotMatch(validator,/\.EnableKeyword\(\s*"INSTANCING_ON"\s*\)/,'Unity manages INSTANCING_ON internally');
assert.match(validator,/material\.enableInstancing\s*=\s*true/,'standard instancing uses the supported material flag');
console.log(`PASS: graph references, legacy defaults, pre-wind height varying, unchanged wind/alpha/texture count, adapter binding, ${checks} scalar mask checks. Native import/GPU validation must run in Unity.`);
