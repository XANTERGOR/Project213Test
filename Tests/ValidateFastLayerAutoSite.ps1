param([Parameter(Mandatory=$true)][string]$ValidationRoot)
$ErrorActionPreference='Stop'
$taskProject=Split-Path $PSScriptRoot -Parent
$taskRoot=[IO.Path]::GetFullPath($ValidationRoot)
if($taskRoot.StartsWith($taskProject.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Validation must be outside the working project.'}
if(Test-Path -LiteralPath $taskRoot){throw 'Use a fresh independent directory.'}
New-Item -ItemType Directory -Path "$taskRoot/Assets/Editor","$taskRoot/Packages","$taskRoot/ProjectSettings" | Out-Null
$sources=@('Assets/TerrainSystem/LocalTerrain/Editor/LTFastLayerBenchmarkSite.cs','Assets/TerrainSystem/LocalTerrain/Editor/LTFastLayerBenchmarkPolicy.cs','Assets/TerrainSystem/LocalTerrain/LTPaintMath.cs')
foreach($source in $sources){Copy-Item -LiteralPath (Join-Path $taskProject $source) -Destination "$taskRoot/Assets/Editor/"}
# Keep the production bin builder verbatim; omit unrelated snapshot/job classes.
$cache=Get-Content -LiteralPath "$taskProject/Assets/TerrainSystem/LocalTerrain/LTPaintWeightTerrainCache.cs" -Raw
$cut=$cache.IndexOf('        sealed class Entry')
if($cut -lt 0){throw 'Cannot locate snapshot cache boundary.'}
[IO.File]::WriteAllText("$taskRoot/Assets/Editor/LTPaintWeightTerrainCache.cs",$cache.Substring(0,$cut)+"`n    }`n}")
Copy-Item -LiteralPath "$PSScriptRoot/FastLayerAutoSiteNativeCheck.cs" -Destination "$taskRoot/Assets/Editor/"
$shader=@'
Shader "Hidden/AutoSiteProbe" {
Properties {
_LTWeights0("Weights",2D)="white"{} _LTWeights1("Weights",2D)="black"{} _LTWeights2("Weights",2D)="black"{}
_LTRect("Rect",Vector)=(0,0,32,32)
}
SubShader { Pass { CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#include "UnityCG.cginc"
float4x4 _LTSurfaceWorldToLocal;
float4 vert(float4 v:POSITION):SV_POSITION {return UnityObjectToClipPos(v);}
float4 frag():SV_Target {return 1;}
ENDCG } } }
'@
[IO.File]::WriteAllText("$taskRoot/Assets/Probe.shader",$shader)
[IO.File]::WriteAllText("$taskRoot/Packages/manifest.json",'{"dependencies":{}}')
Copy-Item -LiteralPath "$taskProject/ProjectSettings/ProjectVersion.txt" -Destination "$taskRoot/ProjectSettings/ProjectVersion.txt"
[IO.File]::WriteAllText("$taskRoot/ownership.txt",'Owned automatic terrain benchmark site validation. Independent source copies, no links or shared caches. Retain project and logs.')
$sources+=@('Assets/TerrainSystem/LocalTerrain/LTPaintWeightTerrainCache.cs','Tests/FastLayerAutoSiteNativeCheck.cs')
$sources | ForEach-Object { $p=Join-Path $taskProject $_; @{path=$p;hash=(Get-FileHash -LiteralPath $p).Hash} } | ConvertTo-Json | Set-Content "$taskRoot/source-hashes.json"
Write-Output $taskRoot
