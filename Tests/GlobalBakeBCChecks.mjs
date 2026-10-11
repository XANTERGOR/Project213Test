import assert from 'node:assert/strict';
import fs from 'node:fs';
const root='Assets/TerrainSystem/LocalTerrain/';
const read=p=>fs.readFileSync(root+p,'utf8');
const saving=read('Editor/LTGlobalBakeSaving.cs'),binding=read('LTPaintGlobals.cs'),asset=read('LTGlobalBakeAsset.cs');
const normal=read('Shaders/LTGlobalNormalEncoding.hlsl'),live=read('Shaders/LTEightLayerSampling.hlsl');
for(const text of ['TextureFormat.BC7','TextureFormat.BC5','TextureCompressionQuality.Best','staging.mipmapCount!=mips',
  'staging.isDataSRGB','staging.GetPixelData<byte>(mip).Length','AssetDatabase.GenerateUniqueAssetPath(path)',
  'copySaved?Object.Instantiate(previous)','if(!copySaved)','snapshot.signature=LTGlobalBakeAsset.Signature(world)',
  'world.ReleasePainting();world.UpdatePainting();','catch(OperationCanceledException)',
  'if(color&&!AssetDatabase.Contains(color))','if(normal&&!AssetDatabase.Contains(normal))'])assert(saving.includes(text),text);
assert(saving.indexOf('color=ReadCompressed')<saving.indexOf('AssetDatabase.CreateAsset(snapshot,path)'));
assert(saving.indexOf('normal=ReadCompressed')<saving.indexOf('AssetDatabase.CreateAsset(snapshot,path)'));
assert(saving.indexOf('AssetDatabase.SaveAssetIfDirty(snapshot)')<saving.indexOf('world.savedGlobalBake=snapshot'));
assert(!/SaveScene\(|SaveAssets\(|DeleteAsset\(|source\.Apply\(|source\.SetPixels\(|CompressTexture\(source/.test(saving));
assert(saving.includes('AsyncGPUReadback.Request(source,mip,TextureFormat.RGBA32)')&&saving.includes('staging.Apply(false,false)'));
assert(!saving.includes('Apply(true'),'Do not regenerate the preserved GPU mip chain');
assert(saving.includes('GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat)'),'Reject accidental gamma conversion');
assert(asset.includes('texture.format==TextureFormat.BC5?1:0'),'Old XYZ and new BC5 identified by actual bound texture');
assert(binding.includes('LTGlobalBakeAsset.NormalEncoding(normalMap)')&&binding.includes('state.boundGlobalParams.Equals(globalParams)'));
assert(binding.includes('saved.FormatsSupported'),'Unsupported compressed assets are not silently used');
assert(normal.includes('if(bc5>.5)')&&normal.includes('sqrt(saturate(1-dot(xy,xy)))'));
assert(live.includes('Shaders/LTGlobalNormalEncoding.hlsl')&&live.includes('_LTGlobalParams.w'));
assert.equal((live.match(/LTGlobalNormalRGB\(/g)||[]).length,2,'Both raster and ray original-layer paths');
const editor=read('Editor/LTEditor.cs');assert(editor.includes('LTGlobalBakeSaving.CompressSaved(world)'));
const check=read('Editor/LTGlobalBakeCompressionCheck.cs');
for(const text of ['Pixels(reopened)==Pixels(packed)','maximum<.045f','source.mipmapCount','new Texture[]{source,rt}',
  'scene.scene.isDirty==scene.dirty','Hash(file.Key)==file.Value'])assert(check.includes(text),text);
assert(!/CreateAsset\(|SaveAssets\(|SaveScene\(/.test(check),'Native check must not create assets or save user scenes');
// Independent numeric decoder oracle: legacy encoding untouched; BC5 reconstructs
// a finite positive-Z hemisphere, including slightly overlength quantized XY.
let cases=0;
const decode=(v,bc)=>bc?[v[0],v[1],Math.sqrt(Math.max(0,1-(v[0]*2-1)**2-(v[1]*2-1)**2))*.5+.5]:v;
for(let x=-100;x<=100;x++)for(let y=-100;y<=100;y++){
  const len=Math.hypot(x/100,y/100,1),n=[x/100/len,y/100/len,1/len],packed=n.map(v=>v*.5+.5);
  assert.deepEqual(decode(packed,false),packed);
  const result=decode([packed[0],packed[1],0],true).map(v=>v*2-1);
  for(let c=0;c<3;c++)assert(Math.abs(n[c]-result[c])<1e-12);cases++;
}
for(const xy of [[0,0],[1,1],[.5,.5],[1,.5],[0,1]])assert(Number.isFinite(decode([...xy,0],true)[2]));
const bytes=(size,bc)=>{let total=0;for(;;size=Math.max(1,size>>1)){total+=bc?Math.ceil(size/4)**2*16:size*size*4;if(size===1)return total*2;}};
assert.equal(bytes(2048,true),11184864);assert.equal(bytes(2048,false),44739240);
// Brightness is a display-only linear albedo multiplier, not baked into either
// atlas (BC7/BC5 or the live RGBA32 preview). Legacy z=0 must remain neutral.
const world=read('LTWorld.cs');
assert(world.includes('public float farLayerBrightness=1;'));
assert(editor.includes('FindProperty("farLayerBrightness")')&&editor.includes('Яркость дальнего фона'));
assert(binding.includes('Mathf.Clamp(world.farLayerBrightness,0,4)-1,0)'));
assert(binding.includes('!state.boundFarSurface.Equals(farSurface)'));
assert(binding.includes('state.material.SetVector("_LTFarSurface",farSurface)'));
for(const name of ['farMaterial','flatMaterial'])
  assert(binding.includes(`state.${name}.CopyPropertiesFromMaterial(state.material)`));
assert(binding.includes('state.diagnosticMaterialRevision++'),'Refresh diagnostic clones too');
assert(!asset.includes('farLayerBrightness'),'Brightness must not invalidate the saved bake');
assert(!saving.includes('farLayerBrightness'),'Never modify pixels when saving/compressing');
assert(!read('Resources/LTGlobalLayerBake.shader').includes('LTEightLayerSampling.hlsl'),
  'Atlas baker must not include the display-only correction');
const correction='farColor*=max(0,1+_LTFarSurface.z);';
assert.equal(live.split(correction).length-1,1,'One shared correction for raster/ray and near/far variants');
assert(live.indexOf(correction)>live.lastIndexOf('float3 farColor='));
assert(live.indexOf(correction)<live.indexOf('if(fade>=1){color=farColor;'));
assert(live.indexOf(correction)<live.indexOf('color=lerp(color,farColor,fade)'));
for(const shader of ['Resources/LTEightLayers.shader','Resources/LTEightLayersTessellation.shader',
  'Resources/LTGlobalLayerBake.shader','Resources/LTRockSurfaceBake.shader','Editor/LTCoverageSamplingProbe.shader'])
  assert(read(shader).includes('_LTFarSurface("Far surface", Vector) = (.3,0,0,0)'),shader+' neutral default');
const near=[.12,.35,.7],far=[.25,.4,.8];
const blend=(gain,fade)=>far.map((c,i)=>near[i]*(1-fade)+c*gain*fade);
let brightnessCases=0;
for(const brightness of [-1,0,.25,.5,1,1.5,2,4,9]) {
  const gain=Math.min(4,Math.max(0,brightness));
  const encoded=gain-1,decoded=Math.max(0,1+encoded);
  assert.equal(decoded,gain);
  for(const fade of [0,.1,.25,.5,.75,.9,1]) {
    assert.deepEqual(blend(decoded,fade),blend(gain,fade));brightnessCases++;
  }
  assert.deepEqual(blend(decoded,0),near,'Near shading stays unchanged');
}
assert.deepEqual(blend(Math.max(0,1+0),1),far,'Legacy material z=0 is neutral');
assert.deepEqual(blend(0,1),[0,0,0]);
assert(blend(4,1)[2]>1,'Do not saturate linear albedo correction');
console.log('PASS far brightness binding/default/bake/transition contracts; '+brightnessCases+' numeric cases (not a GPU render).');
console.log('PASS global BC source/safety/compatibility contracts, '+cases+' decoder cases; 2048 pair: '+bytes(2048,false)+' -> '+bytes(2048,true)+' bytes. Native compression/GPU serialization still requires Unity check.');
