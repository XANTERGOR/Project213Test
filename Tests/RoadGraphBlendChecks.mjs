import fs from 'node:fs';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
const root='Assets/TerrainSystem/LocalTerrain/';
const source='Assets/Data/Models/Environment/Roads/Roads_1/Road_Shader.shadergraph';
const output=root+'Resources/LTRoadGraphGround.shadergraph';
const templatePath=root+'Editor/LTRoadGraphBlendTemplate.json';
const statePath=root+'Editor/LTRoadGraphBlendState.json';
const hash=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex').toUpperCase();
// Objects are separated only at depth zero; strings may contain braces/newlines.
export function parse(s){
  const a=[];let start=-1,depth=0,quoted=false,escaped=false;
  for(let i=0;i<s.length;i++){
    const c=s[i];if(start<0){if(/\s/.test(c)||c==='\ufeff')continue;assert.equal(c,'{');start=i;}
    if(quoted){if(escaped)escaped=false;else if(c==='\\')escaped=true;else if(c==='"')quoted=false;continue;}
    if(c==='"'){quoted=true;continue;}if(c==='{')depth++;
    else if(c==='}'&&!--depth){a.push(JSON.parse(s.slice(start,i+1)));start=-1;}
  }
  assert.equal(start,-1);return a;
}
export function generate(){
  const objects=parse(fs.readFileSync(source,'utf8')),g=objects[0],t=JSON.parse(fs.readFileSync(templatePath,'utf8'));
  const map=new Map(objects.map(x=>[x.m_ObjectId,x]));
  for(const x of t.objects){assert.ok(!map.has(x.m_ObjectId));objects.push(x);map.set(x.m_ObjectId,x);}
  for(const [a,b] of [['m_Properties','properties'],['m_Keywords','keywords'],['m_Nodes','nodes'],['m_Edges','edges']])g[a].push(...t[b]);
  const fn=map.get(t.functionId),blocks=g.m_FragmentContext.m_Blocks.map(x=>map.get(x.m_Id));
  for(const [i,name] of ['BaseColor','NormalTS','Smoothness','Occlusion','Metallic'].entries()){
    const block=blocks.find(x=>x.m_Name==='SurfaceDescription.'+name);assert.ok(block);
    const slot=map.get(block.m_Slots[0].m_Id),input=fn.m_Slots.map(x=>map.get(x.m_Id)).find(x=>x.m_Id===i);
    if((i===0&&slot.m_Type.endsWith('.ColorRGBMaterialSlot'))||(i===1&&slot.m_Type.endsWith('.NormalMaterialSlot')))
      for(const k of ['m_Type','m_ColorMode','m_DefaultColor','m_Space'])if(k in slot)input[k]=structuredClone(slot[k]);
    let value=structuredClone(slot.m_Value);
    if(i===0&&'r' in value)value={x:value.r,y:value.g,z:value.b};
    input.m_Value=value;input.m_DefaultValue=structuredClone(value);
    const edge=g.m_Edges.find(x=>x.m_InputSlot.m_Node.m_Id===block.m_ObjectId);
    if(edge)edge.m_InputSlot={m_Node:{m_Id:fn.m_ObjectId},m_SlotId:i};
    g.m_Edges.push({m_OutputSlot:{m_Node:{m_Id:fn.m_ObjectId},m_SlotId:9+i},m_InputSlot:{m_Node:{m_Id:block.m_ObjectId},m_SlotId:slot.m_Id}});
  }
  g.m_Path='Local Terrain';return objects;
}
const generated=generate();
if(process.argv.includes('--emit'))process.stdout.write(generated.map(x=>JSON.stringify(x,null,4)).join('\n\n')+'\n');
else {
  const originals=parse(fs.readFileSync(source,'utf8'));
  const byId=new Map(generated.map(x=>[x.m_ObjectId,x]));
  for(const x of originals.slice(1))assert.deepEqual(byId.get(x.m_ObjectId),x,'Authored graph object changed');
  assert.equal(byId.size,generated.length);
  const hook=fs.readFileSync(root+'Shaders/LTRoadGraphGround.hlsl','utf8');
  for(const key of ['BaseColor','NormalTS','Smoothness','Occlusion','Metallic'])assert.ok(hook.includes('Out'+key+'='+key));
  for(const p of ['Editor/LTRoadGraphAdapter.cs','Editor/LTRoadGraphAdapterJson.cs','Editor/LTRoadGraphBlendTemplate.json'])
    assert.match(fs.readFileSync(root+p+'.meta','utf8'),/^guid: [0-9a-f]{32}$/m);
  if(process.argv.includes('--generation-only')){
    console.log('PASS reference generation: '+(originals.length-1)+' authored objects unchanged. Saved adapter/native import not checked.');
  } else {
    assert.ok(fs.existsSync(statePath),'Adapter not updated yet: click Обновить адаптер бленда in Unity.');
    const state=JSON.parse(fs.readFileSync(statePath,'utf8'));
    assert.equal(state.version,1);assert.equal(state.sourceHash,hash(source));assert.equal(state.adapterHash,hash(output));assert.equal(state.templateHash,hash(templatePath));
    assert.deepEqual(parse(fs.readFileSync(output,'utf8')),generated,'Saved adapter differs from deterministic reference transformation');
    console.log('PASS saved Shader Graph adapter: source/template/output fingerprints match; '+(originals.length-1)+' authored objects preserved; exact final surface hook. Not a GPU check.');
  }
}
