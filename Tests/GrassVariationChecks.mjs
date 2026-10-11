import fs from 'node:fs';
import assert from 'node:assert/strict';
const read=p=>fs.readFileSync(p,'utf8');
const parse=t=>t.trim().split(/(?=^\{)/m).filter(x=>x.trim()).map(JSON.parse);
const objects=parse(read('Assets/DA/Shared Assets/DA_Simple_Wind/DA_Grass_WIND.shadergraph'));
const graph=objects[0],byId=new Map(objects.map(o=>[o.m_ObjectId,o]));
assert.equal(byId.size,objects.length);
const prop=n=>objects.find(o=>o.m_OverrideReferenceName===n);
const fn=n=>objects.find(o=>o.m_FunctionName===n);
const input=(node,port)=>{
  const edges=graph.m_Edges.filter(e=>e.m_InputSlot.m_Node.m_Id===node.m_ObjectId&&e.m_InputSlot.m_SlotId===port);
  assert.equal(edges.length,1);return {node:byId.get(edges[0].m_OutputSlot.m_Node.m_Id),port:edges[0].m_OutputSlot.m_SlotId};
};
for(const node of graph.m_Nodes){assert.ok(byId.has(node.m_Id));for(const s of byId.get(node.m_Id).m_Slots)assert.ok(byId.has(s.m_Id));}
for(const edge of graph.m_Edges)for(const e of [edge.m_InputSlot,edge.m_OutputSlot]){
  assert.ok(byId.get(e.m_Node.m_Id).m_Slots.some(s=>byId.get(s.m_Id).m_Id===e.m_SlotId));
}
for(const name of ['_LTGrassTintA','_LTGrassTintB','_LTGrassClumpStrength','_LTGrassPatchStrength','_LTGrassPatchSize','_LTGrassVariationSeed','_LTGrassRootInObject']){
  assert.ok(prop(name)?.m_GeneratePropertyBlock);assert.ok(graph.m_Properties.some(r=>r.m_Id===prop(name).m_ObjectId));
}
assert.equal(prop('_LTGrassClumpStrength').m_Value,0);assert.equal(prop('_LTGrassPatchStrength').m_Value,0);
assert.equal(prop('_LTGrassRootInObject').m_Hidden,true);
assert.deepEqual(prop('_LTGrassRootInObject').m_Value,{x:0,y:0,z:0,w:0});
assert.equal(prop('_LTGrassTintA').m_SGVersion,3);assert.equal(prop('_LTGrassTintB').m_SGVersion,3);
const variation=fn('LTGrassVariation'),root=fn('LTGrassVariationRoot'),apply=fn('LTGrassVariationApply');
const guid=read('Assets/TerrainSystem/LocalTerrain/Shaders/LTGrassVariation.hlsl.meta').match(/guid: (\w+)/)[1];
for(const f of [variation,root,apply]){assert.equal(f.m_FunctionSource,guid);assert.equal(f.m_Precision,1,'float precision for world-space noise');}
assert.equal(input(root,0).node.m_Type,'UnityEditor.ShaderGraph.ObjectNode','absolute instance origin, not camera-relative vertex/time');
assert.equal(input(root,1).node.m_Property.m_Id,prop('_LTGrassRootInObject').m_ObjectId);
assert.equal(input(variation,0).node,root);
const block=objects.find(o=>o.m_SerializedDescriptor==='VertexDescription.LTGrassVariationTint#3');
assert.ok(graph.m_VertexContext.m_Blocks.some(n=>n.m_Id===block.m_ObjectId));
assert.equal(input(block,0).node,variation);assert.equal(input(block,0).port,7,'noise computed in vertex stage');
const vary=input(apply,1).node;assert.equal(vary.customBlockNodeName,'LTGrassVariationTint');assert.equal(vary.serializedType,3);
assert.equal(input(apply,0).node.m_ObjectId,'4730a1a4e97d4390ab86352570077eb1');
const terrainApply=fn('LTGrassTerrainApply');
assert.equal(input(fn('LTGrassRootTint'),0).node,terrainApply);
assert.equal(input(terrainApply,0).node,apply,'variation retained before terrain color and manual roots');

if(process.argv[2]){
  const before=parse(JSON.parse(read(process.argv[2])).graphSource);
  for(const o of before.slice(1))assert.deepEqual(byId.get(o.m_ObjectId),o,'original object untouched');
  for(const e of before[0].m_Edges){
    if(e.m_InputSlot.m_Node.m_Id===fn('LTGrassRootTint').m_ObjectId&&e.m_InputSlot.m_SlotId===0)continue;
    assert.ok(graph.m_Edges.some(x=>JSON.stringify(x)===JSON.stringify(e)),'old non-color edge preserved');
  }
  assert.deepEqual(graph.m_FragmentContext,before[0].m_FragmentContext);
  assert.deepEqual(graph.m_ActiveTargets,before[0].m_ActiveTargets);
  assert.equal(objects.filter(o=>o.m_Type.includes('SampleTexture')).length,before.filter(o=>o.m_Type.includes('SampleTexture')).length);
}
const hlsl=read('Assets/TerrainSystem/LocalTerrain/Shaders/LTGrassVariation.hlsl');
assert.doesNotMatch(hlsl,/SAMPLE_TEXTURE|tex2D|\b_Time\b|unity_InstanceID|_WorldSpaceCameraPos/);
assert.match(hlsl,/ObjectOrigin \+ mul\(\(float3x3\)UNITY_MATRIX_M, RootInObject\)/);
// Execute scalar arithmetic extracted from the HLSL uint hash (same wrap semantics).
let body=hlsl.match(/float LTGrassHash[^]*?\{([^]*?)\n\}/)[1];
body=body.replace(/\(uint\)cell\.x/g,'x').replace(/\(uint\)cell\.y/g,'z').replace(/\b(\d+)u\b/g,'$1')
  .replace(/uint h = ([^;]+);/,'let h = $1;').replace(/>>/g,'>>>')
  .replace(/\(h \^ \(h >>> (\d+)\)\) \* (\d+)/g,'Math.imul(h ^ (h >>> $1), $2)')
  .replace(/\b([xz]|seed) \* (\d+)/g,'Math.imul($1, $2)');
const hash=new Function('x','z','seed',body),mix=(a,b,t)=>a+(b-a)*t,clamp=x=>Math.max(0,Math.min(1,x));
// Independent noise reference; native test compares actual HLSL pixels against C#.
const noise=(x,z,seed)=>{
  const ix=Math.floor(x),iz=Math.floor(z);let u=x-ix,v=z-iz;
  u=u*u*(3-2*u);v=v*v*(3-2*v);
  return mix(mix(hash(ix,iz,seed),hash(ix+1,iz,seed),u),mix(hash(ix,iz+1,seed),hash(ix+1,iz+1,seed),u),v);
};
const multiplier=(x,z,a,b,cs,ps,size,seed)=>{
  cs=clamp(cs);ps=clamp(ps);const sum=cs+ps;
  seed=Math.min(65535,Math.max(0,Math.floor(seed+.5)));
  const t=(noise(x*8,z*8,seed)*cs+noise(x/Math.max(size,.1),z/Math.max(size,.1),seed^1867)*ps)/Math.max(sum,.0001);
  return a.map((v,i)=>mix(1,mix(clamp(v),clamp(b[i]),t),clamp(sum)));
};
let checks=0;let min=1,max=0;
for(let i=-50;i<=50;i++)for(let j=-10;j<=10;j++){
  const x=i*.31,z=j*.47,n=noise(x,z,17);min=Math.min(min,n);max=Math.max(max,n);
  assert.ok(n>=0&&n<=1);assert.equal(n,noise(x,z,17));
  assert.ok(Math.abs(noise(x+1e-5,z,17)-n)<.0001,'smooth across lattice boundaries');
  for(const cs of [0,.2,1])for(const ps of [0,.35,1]){
    const a=[.9,.95,.7],b=[.6,.5,.4],c=multiplier(x,z,a,b,cs,ps,12,17);
    assert.ok(c.every((v,k)=>Number.isFinite(v)&&v>=b[k]&&v<=1));
    if(!cs&&!ps)assert.deepEqual(c,[1,1,1]);
    assert.deepEqual(multiplier(x,z,[1,1,1],[1,1,1],cs,ps,12,17),[1,1,1]);checks++;
  }
}
assert.ok(max-min>.8,'not a constant tint');
assert.notEqual(noise(12.3,-8.7,1),noise(12.3,-8.7,2),'seed changes pattern');
assert.equal(noise(2/10,3/10,1),noise(4/20,6/20,1),'world patch scaling');
for(const size of [-1,0,1e-8,.1,100])assert.ok(multiplier(-1,2,[1,1,1],[.5,.5,.5],.2,.3,size,0).every(Number.isFinite));

const prefab=read('Assets/TerrainSystem/LocalTerrain/LTDetailPrefab.cs');
assert.match(prefab,/grassRootInPart=matrix.inverse.MultiplyPoint3x4\(Vector3.zero\)/);
assert.match(prefab,/grassProperties.SetVector\(GrassRootProperty,grassRootInPart\)/);
const renderer=read('Assets/TerrainSystem/LocalTerrain/LTDetailRenderer.cs');
assert.match(renderer,/grassRoot.Equals\(other.grassRoot\)/,'CPU merged batches split on root offset');
assert.match(renderer,/grassRoot.GetHashCode\(\)/);
assert.match(renderer,/part.SupportsGrassColor\?part.grassRootInPart:Vector3.zero/);
assert.match(renderer,/parameters.matProps=part.GrassProperties/);
assert.match(read('Assets/TerrainSystem/LocalTerrain/LTDetailGpuRenderer.cs'),/SetVector\(LTDetailPrefab.Part.GrassRootProperty,part.grassRootInPart\)/);
for(const path of ['Assets/TerrainSystem/LocalTerrain/Shaders/LTGrassVariation.hlsl.meta','Assets/TerrainSystem/LocalTerrain/Editor/LTGrassVariationCheck.cs.meta'])
  assert.match(read(path),/guid: [0-9a-f]{32}\s/);
console.log(`PASS: graph/old behavior, vertex-only stable color, absolute root + CPU/GPU/merged anchor wiring, ${checks} noise/color cases. Not a shader/GPU/live-instancing test.`);
