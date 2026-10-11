import fs from 'node:fs';
import assert from 'node:assert/strict';
const root='Assets/TerrainSystem/LocalTerrain/';
const read=p=>fs.readFileSync(root+p,'utf8');
const source=read('LTPaintRuntime.cs'),asphalt=read('LTPaintAsphalt.cs');
const core=read('Shaders/LTLayerBlendCore.hlsl'),helper=read('Shaders/LTRoadAsphalt.hlsl');
const uniforms=read('Shaders/LTRoadAsphaltProperties.hlsl');
for(const file of ['Resources/LTEightLayers.shader','Resources/LTEightLayersTessellation.shader','Resources/LTGlobalLayerBake.shader','Resources/LTRockSurfaceBake.shader']){
 const shader=read(file);
 for(const name of ['_LTAsphaltCracks','_LTAsphaltDamage'])assert.match(shader,new RegExp(name+'\\("[^"\\n]*",2DArray\\)'));
 for(let i=0;i<12;i++)assert.ok(shader.includes(`_LTAsphaltStyle${i}("Asphalt style ${i}",Vector)=(0,0,0,0)`));
}
for(let i=0;i<12;i++){
 assert.equal(core.split(`LTApplyAsphaltCracks(${i},`).length-1,1,'exactly one hook for each painted slot');
 assert.ok(uniforms.includes(`float4 _LTAsphaltStyle${i};`));
}
for(const file of ['Shaders/LTEightLayerProperties.hlsl','Resources/LTGlobalLayerBake.shader','Resources/LTRockSurfaceBake.shader'])
 assert.ok(read(file).includes('#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadAsphaltProperties.hlsl"'));
assert.ok(helper.includes('if(style.x<.5)return;')&&helper.includes('if(strength<=.0001)return;'));
assert.ok(helper.indexOf('if(style.x<.5)')<helper.indexOf('SAMPLE_TEXTURE2D_ARRAY_LOD'),'neutral mode skips texture reads');
assert.ok(helper.includes('SAMPLE_TEXTURE2D_ARRAY_GRAD'),'explicit derivatives for dynamic branches');
assert.ok(!/SV_Depth|discard|clip\(/.test(helper),'cracks do not create geometry/holes');
assert.ok(source.indexOf('BakeAsphalt(world,rect,stamps,layers,state,bakeWeights)')<source.indexOf('if(!bakeWeights)'));
for(const hook of ['ReleaseAsphalt(state)','BindAsphalt(material,state)','asphaltCracks.Dispose()','asphaltCracks.Ensure(world)'])assert.ok(source.includes(hook));
assert.ok(asphalt.includes('snapshot.PaintWeights(hit,px,pz,out float weight,out float wheels)'),'reuse nearest-road query');
assert.ok(asphalt.includes('if(!bakeWeights&&')&&asphalt.includes('OtherWeight(i,px,pz)'),'preserve cross-layer ordered ownership and cache invalidation');
assert.ok(asphalt.includes('old.hash==hash')&&asphalt.includes('asphaltCracks.Revision'),'mask cache includes atlas revision');
assert.ok(read('LTRoad.cs').includes('LTRoadMath.IsTerrainAsphalt(mode)?LTRoadProjection.Spline:projection'));
assert.ok(read('LTRoadMath.cs').includes('mode==LTRoadMode.TerrainAsphalt||mode==LTRoadMode.GeometricAsphalt'));
assert.ok(read('Editor/LTLODMesh.cs').includes('RequiresFixedRoadSurface(LTRoadMode mode)=>mode==LTRoadMode.Asphalt'));
assert.ok(read('LTGlobalBakeAsset.cs').includes('terrain-asphalt-v1:'));
const native=read('Editor/LTRoadTerrainAsphaltCheck.cs');
assert.ok(!/SaveAssets\(|SaveScene\(|LTEditorEngine\.Refresh/.test(native));
for(const file of ['LTPaintAsphalt.cs','LTRoadCrackTextures.cs','LTRoadAsphaltDamage.cs','Shaders/LTRoadAsphalt.hlsl','Shaders/LTRoadAsphaltProperties.hlsl','Resources/LTRoadCrackPack.shader','Editor/LTRoadTerrainAsphaltCheck.cs']){
 assert.ok(!/^\+|PLACEHOLDER/m.test(read(file)),file);
 assert.match(read(file+'.meta'),/guid: [a-f\d]{32}/);
}
console.log('PASS terrain asphalt integration: 12 layer hooks, neutral shaders, shared bake layout, cache/lifetime, one-layer spline route, terrain LOD policy, metadata and no native-test scene saves. Static contracts only.');
const roadEditor=read('Editor/LTRoadEditor.cs'),roadMath=read('LTRoadMath.cs'),geometryCheck=read('Editor/LTRoadGeometryCheck.cs');
for(const control of ['pitBevelWidth','pitBevelDepth','pitShapeVariation']){
 assert.ok(read('LTRoad.cs').includes(`${control}=${control}`),'capture new detail settings');
 assert.ok(roadEditor.includes(`Field("${control}"`),'expose opt-in geometry detail');
 assert.ok(roadMath.includes(`Range(${control},`),'validate detail ranges');
}
assert.ok(roadEditor.includes('Попробовать сколы и разные формы')&&roadEditor.includes('GUI.changed=true;'));
assert.ok(geometryCheck.includes('v7 / Connected')&&geometryCheck.includes('bevelProbes++'));
assert.ok(!/SaveAssets\(|SaveScene\(|LTEditorEngine\.Refresh/.test(geometryCheck));
console.log('PASS bevel integration: captured/validated inspector controls, opt-in preset, owned-preview collider probes, no scene saves.');
for(const control of ['connectedDamage','connectedFloorAsphalt','repairPatchStrength']){
 assert.ok(read('LTRoad.cs').includes(`${control}=${control}`),'capture connected settings');
 assert.ok(roadEditor.includes(`Field("${control}"`),'expose connected settings');
}
assert.ok(roadEditor.includes('Тест: изношенная дорога')&&roadEditor.includes('DisabledScope(connected)'));
assert.ok(helper.includes(').rg;')&&helper.includes('float repair=saturate(influence.g)'));
assert.equal((helper.match(/SAMPLE_TEXTURE2D_ARRAY_LOD\(_LTAsphaltDamage/g)||[]).length,1,'repairs reuse the existing influence texture fetch');
assert.ok(helper.indexOf('color*=1-.32*repair')<helper.indexOf('if(strength<=.0001)return;'),'repair-only mode does not depend on crack strength');
assert.ok(asphalt.includes('CompositeSurface')&&asphalt.includes('snapshot.RepairPatch(hit)'));
assert.ok(read('LTRoadCrackTextures.cs').includes('road.connectedDamage&&road.repairPatchStrength>0'),'patch-only lifetime');
assert.ok(read('LTGlobalBakeAsset.cs').includes('connected-damage-v1:'));
assert.ok(geometryCheck.includes('Validate Worn Asphalt')&&geometryCheck.includes('connectedFloors++')&&geometryCheck.includes('notches++'));
assert.ok(native.includes('v2 / Patches')&&native.includes('new Color(0,1,0,1)'));
console.log('PASS connected integration: opt-in controls/preset, shared RG influence read, ordered patch ownership, cache/lifetime, collider/concavity and patch GPU probes. Native execution still required.');
