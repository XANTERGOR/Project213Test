import fs from 'node:fs';
import assert from 'node:assert/strict';
const read=p=>fs.readFileSync(p,'utf8');
const parse=s=>s.trim().split(/(?=^\{)/m).filter(s=>s.trim()).map(JSON.parse);
const objects=parse(read('Assets/DA/Shared Assets/DA_Simple_Wind/DA_Grass_WIND.shadergraph'));
const graph=objects[0],ids=new Map(objects.map(o=>[o.m_ObjectId,o]));
assert.equal(ids.size,objects.length);
const fn=n=>objects.find(o=>o.m_FunctionName===n),prop=n=>objects.find(o=>o.m_OverrideReferenceName===n);
const input=(node,port)=>{
  const edges=graph.m_Edges.filter(e=>e.m_InputSlot.m_Node.m_Id===node.m_ObjectId&&e.m_InputSlot.m_SlotId===port);
  assert.equal(edges.length,1);return {node:ids.get(edges[0].m_OutputSlot.m_Node.m_Id),port:edges[0].m_OutputSlot.m_SlotId};
};
for(const edge of graph.m_Edges)for(const e of [edge.m_InputSlot,edge.m_OutputSlot])
  assert.ok(ids.get(e.m_Node.m_Id)?.m_Slots.some(s=>ids.get(s.m_Id).m_Id===e.m_SlotId));
assert.ok(!objects.some(o=>JSON.stringify(o).includes('LTGrassZone')),'removed stamp zones stay absent');
for(const name of ['Strength','Height','Ready','Map','U','V'])assert.ok(prop('_LTGrassTerrain'+name));
assert.equal(prop('_LTGrassTerrainStrength').m_Value,0);
assert.equal(prop('_LTGrassTerrainReady').m_Value,0);
for(const name of ['Ready','Map','U','V'])assert.equal(prop('_LTGrassTerrain'+name).m_Hidden,true);
const sample=fn('LTGrassTerrainSample'),apply=fn('LTGrassTerrainApply'),roots=fn('LTGrassRootTint');
const guid=read('Assets/TerrainSystem/LocalTerrain/Shaders/LTGrassTerrain.hlsl.meta').match(/guid: (\w+)/)[1];
for(const node of [sample,apply]){assert.equal(node.m_FunctionSource,guid);assert.equal(node.m_Precision,1);}
assert.equal(input(sample,0).node,fn('LTGrassVariationRoot'),'stable shared planting root, not blade/world vertex');
assert.equal(input(sample,0).port,2);
assert.equal(input(sample,5).node.m_Property.m_Id,prop('_LTGrassTerrainStrength').m_ObjectId,'zero strength skips vertex sampling');
const vertex=objects.find(o=>o.m_SerializedDescriptor==='VertexDescription.LTGrassTerrain#4');
assert.ok(graph.m_VertexContext.m_Blocks.some(r=>r.m_Id===vertex.m_ObjectId));
assert.equal(input(vertex,0).node,sample);assert.equal(input(vertex,0).port,6);
assert.equal(input(apply,1).node.customBlockNodeName,'LTGrassTerrain');
assert.equal(input(apply,2).node,input(roots,1).node,'same pre-wind height');
assert.equal(input(apply,3).node,input(roots,4).node,'same base, independent fade height');
assert.equal(input(apply,4).node.m_Property.m_Id,prop('_LTGrassTerrainHeight').m_ObjectId);
assert.equal(input(apply,0).node,fn('LTGrassVariationApply'));
assert.equal(input(roots,0).node,apply);
if(process.argv[2]){
  const before=parse(JSON.parse(read(process.argv[2])).graphSource);
  for(const obj of before.slice(1))assert.deepEqual(ids.get(obj.m_ObjectId),obj,'all old graph objects unchanged');
  for(const e of before[0].m_Edges){
    if(e.m_InputSlot.m_Node.m_Id===roots.m_ObjectId&&e.m_InputSlot.m_SlotId===0)continue;
    assert.ok(graph.m_Edges.some(n=>JSON.stringify(n)===JSON.stringify(e)),'old non-insertion edges preserved');
  }
  assert.deepEqual(graph.m_FragmentContext,before[0].m_FragmentContext);
  assert.deepEqual(graph.m_ActiveTargets,before[0].m_ActiveTargets);
}
const hlsl=read('Assets/TerrainSystem/LocalTerrain/Shaders/LTGrassTerrain.hlsl');
const scalar=hlsl.match(/float t =[^]*?float weight =[^;]+;/)[0].replace(/\bfloat\b/g,'const').replace(/\bmax\(/g,'Math.max(').replace(/Ground\.a/g,'alpha');
const clamp=x=>Math.min(1,Math.max(0,x));
const weight=new Function('RestHeight','RootBase','BlendHeight','Strength','alpha','saturate',scalar+'return weight;');
const lum=c=>c[0]*.2126+c[1]*.7152+c[2]*.0722;
const mix=(a,b,t)=>a+(b-a)*t;
assert.match(hlsl,/0\.5 \+ 0\.5 \* grassY \/ max\(groundY, 0\.02\)/);
const color=(base,ground,w)=>base.map((v,i)=>mix(v,clamp(ground[i]*(.5+.5*lum(base)/Math.max(lum(ground),.02))),w));
let checks=0;
for(const base of [[.7,.45,.18],[0,0,0],[1,1,1],[.05,.05,.05]])
for(const ground of [[.2,.4,.1],[0,0,0],[.001,.002,.001],[.6,.2,.1],[1,1,1]])
for(const strength of [-1,0,.5,1,2])for(const alpha of [0,.25,1])for(const height of [-1,0,.00001,.4,2]){
  let previous=Infinity;
  for(let i=0;i<=20;i++){
    const rest=i*.1-.1,w=weight(rest,0,height,strength,alpha,clamp);
    assert.ok(w>=0&&w<=1&&w<=previous+1e-8);previous=w;
    const result=color(base,ground,w);assert.ok(result.every(Number.isFinite));
    if(strength<=0||alpha===0||rest>=Math.max(.0001,height))assert.deepEqual(result,base);
    checks++;
  }
}
assert.notDeepEqual(color([.7,.45,.18],[.6,.2,.1],1),color([.4,.3,.1],[.6,.2,.1],1),'grass brightness detail is retained');
assert.doesNotMatch(hlsl,/\b_Time\b|_WorldSpaceCameraPos|clip\s*\(|\.a\s*=|tex2D/);
assert.equal((hlsl.match(/SAMPLE_TEXTURE2D_LOD\(/g)||[]).length,1,'one vertex texture fetch at root');
const map=read('Assets/TerrainSystem/LocalTerrain/LTGrassTerrainMap.cs');
assert.match(map,/RenderTextureReadWrite.Linear/);assert.match(map,/ClearRenderTarget\(false,true,Color.clear\)/);
assert.match(map,/CopyPropertiesFromMaterial\(tile.material\)/);
assert.match(map,/!previous.Equals\(tile\)/);assert.match(map,/TilesRendered\+\+/);
assert.match(map,/geometryRevision==b.geometryRevision/);assert.match(map,/membership.Count!=tiles.Count/);
assert.doesNotMatch(map,/ReadPixels|GetPixels|SetGlobal|AssetDatabase|enableGlobalLayerMaps|SaveAssets/);
const paint=read('Assets/TerrainSystem/LocalTerrain/LTPaintRuntime.cs');
assert.match(paint,/state.grassColorRevision\+\+/);assert.match(paint,/GrassTerrain.Dispose\(\)/);assert.match(paint,/TickGrassTerrain\(world\)/);
const integration=read('Assets/TerrainSystem/LocalTerrain/LTPaintGrassTerrain.cs');
assert.match(integration,/details.UsesGrassTerrainTint/);assert.match(integration,/world.triplanarTexturing\?chunk.mesh:null/);
assert.match(read('Assets/TerrainSystem/LocalTerrain/LTDetailRenderer.cs'),/LTGrassTerrainMap.Bind\(/);
assert.match(read('Assets/TerrainSystem/LocalTerrain/LTDetailGpuRenderer.cs'),/draw.terrainBinding.Apply\(draw.properties,terrainBinding\)/);
assert.doesNotMatch(read('Assets/TerrainSystem/LocalTerrain/LTPaintStamp.cs'),/grassColor|GrassTerrain/,'no stamp controls');
const check=read('Assets/TerrainSystem/LocalTerrain/Editor/LTGrassTerrainCheck.cs');
assert.doesNotMatch(check,/\.EnableKeyword\("INSTANCING_ON"\)|SaveScene|SaveAssets|new GameObject/);
console.log(`PASS: terrain graph insertion/preservation, opt-in defaults, vertex planting root, CPU/GPU binding, cache/invalidation wiring and ${checks} fade/color cases. Native GPU pixels remain a separate Unity check.`);
