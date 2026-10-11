$ErrorActionPreference='Stop'
$projectDir=Split-Path $PSScriptRoot -Parent
$shaderDir=Join-Path $projectDir 'Assets/TerrainSystem/LocalTerrain/Shaders'
$reportDir=Join-Path $projectDir ('Logs/RoadAutoGroundHlsl/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reportDir | Out-Null
# Reuse only the already-owned native compiler wrapper; do not execute its terrain test suite.
$compiler=Get-Content -Raw (Join-Path $PSScriptRoot 'CompileTerrainArrays.ps1')
$interop=[regex]::Match($compiler,"(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@").Groups[1].Value
if(-not $interop){throw 'Missing compiler interop'}
Add-Type -TypeDefinition $interop
$preamble=@'
#define SAMPLER(n) SamplerState n
#define TEXTURE2D(n) Texture2D<float4> n
#define TEXTURE2D_ARRAY(n) Texture2DArray<float4> n
#define SAMPLE_TEXTURE2D_ARRAY_LOD(t,s,u,i,l) t.SampleLevel(s,float3(u,i),l)
#define SAMPLE_TEXTURE2D_ARRAY_GRAD(t,s,u,i,x,y) t.SampleGrad(s,float3(u,i),x,y)
#define SAMPLE_TEXTURE2D_LOD(t,s,u,l) t.SampleLevel(s,u,l)
#define SAMPLE_TEXTURE2D_GRAD(t,s,u,x,y) t.SampleGrad(s,u,x,y)
#define LOAD_TEXTURE2D_ARRAY(t,p,s) t.Load(int4(p,s,0))
#define LOAD_TEXTURE2D(t,p) t.Load(int3(p,0))
SamplerState sampler_LinearClamp,sampler_LinearRepeat;
float3 UnpackNormalRGB(float4 c,float scale){float3 n=c.xyz*2-1;n.xy*=scale;return n;}
float3 UnpackNormalScale(float4 c,float scale){float2 xy=(c.xy*2-1)*scale;return float3(xy,sqrt(saturate(1-dot(xy,xy))));}
float3 LTProjectionGradient(float3 n,int axis){float2 s=-n.xy/max(n.z,.0001);return axis==0?float3(0,s.y,s.x):axis==1?float3(s.x,0,s.y):float3(s.x,s.y,0);}
float4x4 _LTSurfaceWorldToLocal;
#define _LT_ROAD_BLEND 1
#define LT_ROAD_BLEND_PROBE 1
'@
$files=@('LTRoadGroundProperties.hlsl','LTRockMaterialProperties.hlsl','LTRoadTerrainSampling.hlsl','LTRoadGroundSurface.hlsl','LTRockMaterialSampling.hlsl')
$hashes=@{};foreach($f in $files){$hashes[$f]=(Get-FileHash (Join-Path $shaderDir $f)).Hash}
$properties=(Get-Content -Raw (Join-Path $shaderDir $files[0]))+(Get-Content -Raw (Join-Path $shaderDir $files[1]))
$surface=Get-Content -Raw (Join-Path $shaderDir 'LTRoadGroundSurface.hlsl')
foreach($f in @('LTRoadTerrainSampling.hlsl','LTRockMaterialSampling.hlsl')){
 $body=Get-Content -Raw (Join-Path $shaderDir $f)
 $normal=Get-Content -Raw (Join-Path $shaderDir 'LTLayerNormal.hlsl')
 $body=$body.Replace('#include "Assets/TerrainSystem/LocalTerrain/Shaders/LTLayerNormal.hlsl"',$normal)
 $surface=[regex]::Replace($surface,'(?m)^#include.*'+[regex]::Escape($f)+'.*$', [System.Text.RegularExpressions.MatchEvaluator]{param($m) $body})
}
$entry=@'
float4 Main(float3 p:TEXCOORD0):SV_Target {
 float3 c,n;float a,s,m;
 float w=LTRoadGroundWeight(p,p);
 LTRockNear(p,normalize(float3(.2,1,.3)),float3(1,0,0),-1,p.xz,w,c,n,a,s,m);
 return float4(c+n+float3(a,s,m),w);
}
'@
$authoredEntry=@'
float4 Main(float3 p:TEXCOORD0):SV_Target {
 float3 c,g;float a,s,m;
 float w=LTRoadGroundWeight(p,p);
 LTSampleRoadGround(p,normalize(float3(.2,1,.3)),ddx(p),ddy(p),c,g,a,s,m);
 return float4(c+g+float3(a,s,m),w);
}
'@
# Compile-time separation must remove the other source's sampler bindings. Also
# reserve 13 live samplers around the authored hook to guard the D3D11 limit. This
# is a synthetic resource-budget test, NOT a native HDRP/ASE pass compilation.
$reserved='Texture2D<float4> ReservedTexture;'+"`n"
$reservedRead='float4 reserved=0;'
for($i=0;$i -lt 13;$i++){
 $reserved+='SamplerState Reserved'+$i+' : register(s'+($i+3)+');'+"`n"
 $reservedRead+='reserved+=ReservedTexture.SampleLevel(Reserved'+$i+',p.xz,0);'
}
$reservedEntry=$authoredEntry.Replace('return float4(c+g+float3(a,s,m),w);',$reservedRead+'return float4(c+g+float3(a,s,m),w)+reserved;')
$cases=@(
 @{Name='auto-ground';Auto=$true;Authored=$false;Count=6;Entry=$entry;Extra=''},
 @{Name='single-ground';Auto=$false;Authored=$false;Count=7;Entry=$entry;Extra=''},
 @{Name='authored-auto-hook';Auto=$true;Authored=$true;Count=3;Entry=$authoredEntry;Extra=''},
 @{Name='authored-single-hook';Auto=$false;Authored=$true;Count=4;Entry=$authoredEntry;Extra=''},
 @{Name='authored-auto-budget';Auto=$true;Authored=$true;Count=16;Entry=$reservedEntry;Extra=$reserved}
)
foreach($case in $cases){
 $defines='';if($case.Auto){$defines+="#define _LT_ROAD_TERRAIN 1`n"};if($case.Authored){$defines+="#define LT_ROAD_BLEND_AUTHORED 1`n"}
 $source=$defines+$preamble+"`n"+$properties+"`n"+$surface+"`n"+$case.Extra+"`n"+$case.Entry
 $output=Join-Path $reportDir $case.Name
 [NativeHlsl]::Compile($source,'ps_5_0',$output) | Write-Output
 $assembly=Get-Content -Raw ($output+'.asm')
 $count=[regex]::Matches($assembly,'(?m)^dcl_sampler\s+s\d+,').Count
 if($count -ne $case.Count){throw ($case.Name+': expected '+$case.Count+' sampler registers, got '+$count)}
 $unused=if($case.Auto){'sampler_LTRoadBlend'}else{'sampler_LTRoadTerrain'}
 if($assembly.Contains($unused)){throw ($case.Name+': inactive source samplers retained')}
 if($case.Auto -and ($assembly.Contains('sampler_LTRoadTerrainNormal') -or $assembly.Contains('sampler_LTRoadTerrainMask'))){throw 'Layer arrays did not share their sampler'}
 Write-Output ('PASS '+$case.Name+': '+$count+' sampler registers; inactive source stripped')
}
foreach($f in $files){if((Get-FileHash (Join-Path $shaderDir $f)).Hash -ne $hashes[$f]){throw ('Source changed: '+$f)}}
Write-Output ('PASS reduced D3D11 road/terrain shader compile; not Unity import or graphics validation. '+$reportDir)
