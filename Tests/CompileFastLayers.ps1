param([string[]]$Cases=@('live','fast','rock-fast','road-fast','tess-fast','fast-lod','road-lod','tess-road-fast','pair','pair-road','pair-lod','pair-tess','pair-color','pair-color-road','pair-color-lod','pair-color-tess'))
$ErrorActionPreference='Stop'
$projectDir=Split-Path $PSScriptRoot -Parent
$shaderDir=Join-Path $projectDir 'Assets/TerrainSystem/LocalTerrain/Shaders'
$reportDir=Join-Path $projectDir ('Logs/FastLayerChecks/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
# Reuse only the D3D compiler wrapper, not the old harness's source expansion.
$harness=Get-Content -Raw (Join-Path $PSScriptRoot 'CompileTerrainArrays.ps1')
$wrapper=[regex]::Match($harness,"(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@")
if(-not $wrapper.Success){throw 'Native HLSL wrapper not found'}
Add-Type -TypeDefinition $wrapper.Groups[1].Value
$hashes=@{}
function Expand-Source([string]$path) {
    $hashes[$path]=(Get-FileHash -LiteralPath $path).Hash
    $source=Get-Content -LiteralPath $path -Raw
    return [regex]::Replace($source,'(?m)^#include "([^"]+)"[^\r\n]*',[System.Text.RegularExpressions.MatchEvaluator]{param($m)
        $include=$m.Groups[1].Value
        if($include.StartsWith('Packages/')){return ''}
        if(-not $include.StartsWith('Assets/TerrainSystem/LocalTerrain/')){throw "Unexpected include: $include"}
        return (Expand-Source (Join-Path $projectDir $include))
    })
}
$properties=Expand-Source (Join-Path $shaderDir 'LTEightLayerProperties.hlsl')
$uniforms=(($properties -split 'CBUFFER_START\(UnityPerMaterial\)',2)[1] -split '// shared constant between lit and layered lit',2)[0]
$preamble=@'
#define SAMPLER(n) SamplerState n
#define SAMPLE_TEXTURE2D_ARRAY_LOD(t,s,u,i,l) t.SampleLevel(s,float3(u,i),l)
#define SAMPLE_TEXTURE2D_ARRAY_GRAD(t,s,u,i,x,y) t.SampleGrad(s,float3(u,i),x,y)
#define TEXTURE2D(n) Texture2D<float4> n
#define TEXTURE2D_ARRAY(n) Texture2DArray<float4> n
#define SAMPLE_TEXTURE2D_LOD(t,s,u,l) t.SampleLevel(s,u,l)
#define SAMPLE_TEXTURE2D_GRAD(t,s,u,x,y) t.SampleGrad(s,u,x,y)
#define LOAD_TEXTURE2D_ARRAY(t,p,s) t.Load(int4(p,s,0))
SamplerState sampler_LinearClamp;
SamplerState sampler_LinearRepeat;
float3 UnpackNormalRGB(float4 packed,float scale){float3 n=packed.rgb*2-1;n.xy*=scale;return n;}
'@
$common=$preamble+"`ncbuffer UnityPerMaterial {`n"+$uniforms+"`n};`n"+
    (Expand-Source (Join-Path $shaderDir 'LTLayerBlendCore.hlsl'))+"`n"+
    (Expand-Source (Join-Path $shaderDir 'LTProjectedLayers.hlsl'))
$entry=@'
float4 Main(float4 p:SV_Position,float3 uv:TEXCOORD0,float3 normal:TEXCOORD1):SV_Target {
 float3 c,g;float a,s,m;
 LTSampleProjectedLayers(uv,normalize(normal),_LTTriplanar>.5,c,g,a,s,m);
 return float4(c+g+float3(a,s,m),1);
}
'@
$results=@()
foreach($case in @(
    @{Name='live';Defines='';Fast=$false},
    @{Name='fast';Defines="#define _LT_FAST_LAYERS 1`n";Fast=$true},
    @{Name='rock-fast';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_ROCK_SURFACE 1`n";Fast=$true},
    @{Name='road-fast';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_FAST_ROADS 1`n#define _LT_ROAD_BLEND 1`n";Fast=$true;Road=$true},
    @{Name='tess-fast';Defines="#define _LT_FAST_LAYERS 1`n#define TESSELLATION_ON 1`n";Fast=$true},
    @{Name='fast-lod';Defines="#define _LT_FAST_LAYERS 1`n#define SHADER_STAGE_RAY_TRACING 1`n";Fast=$true},
    @{Name='road-lod';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_FAST_ROADS 1`n#define SHADER_STAGE_RAY_TRACING 1`n";Fast=$true;Road=$true},
    @{Name='tess-road-fast';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_FAST_ROADS 1`n#define TESSELLATION_ON 1`n";Fast=$true;Road=$true},
    @{Name='pair';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_FAST_PREHEIGHT 1`n";Fast=$true},
    @{Name='pair-road';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_FAST_PREHEIGHT 1`n#define _LT_FAST_ROADS 1`n";Fast=$true;Road=$true},
    @{Name='pair-lod';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_FAST_PREHEIGHT 1`n#define SHADER_STAGE_RAY_TRACING 1`n";Fast=$true},
    @{Name='pair-tess';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_FAST_PREHEIGHT 1`n#define TESSELLATION_ON 1`n";Fast=$true},
    @{Name='pair-color';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_FAST_PREHEIGHT 1`n#define _LT_FAST_COLOR_REMAINDER 1`n";Fast=$true},
    @{Name='pair-color-road';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_FAST_PREHEIGHT 1`n#define _LT_FAST_COLOR_REMAINDER 1`n#define _LT_FAST_ROADS 1`n";Fast=$true;Road=$true},
    @{Name='pair-color-lod';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_FAST_PREHEIGHT 1`n#define _LT_FAST_COLOR_REMAINDER 1`n#define SHADER_STAGE_RAY_TRACING 1`n";Fast=$true},
    @{Name='pair-color-tess';Defines="#define _LT_FAST_LAYERS 1`n#define _LT_FAST_PREHEIGHT 1`n#define _LT_FAST_COLOR_REMAINDER 1`n#define TESSELLATION_ON 1`n";Fast=$true}
)) {
    if($case.Name -notin $Cases){continue}
    $out=Join-Path $reportDir $case.Name
    $source=$case.Defines+$common+"`n"+$entry
    [IO.File]::WriteAllText($out+'.hlsl',$source)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    try {
        $diagnostics=[NativeHlsl]::Compile($source,'ps_5_0',$out)
        $assembly=Get-Content -Raw ($out+'.asm')
        $bindings=[regex]::Match($assembly,'(?s)// Resource Bindings:.*?(?=\r?\n\s*ps_5_0)').Value
        if(-not $bindings){throw 'Missing resource reflection'}
        foreach($kind in @('Color','Normal','Mask')) {
            if($bindings -notmatch ("_LT"+$kind+"Array\s")){throw "Lost $kind texture"}
        }
        $hasRoad=$bindings -match '_LTRoadProjectionMap\s'
        if($case.Fast -and -not $case.Road -and ($hasRoad -or $bindings -match '_LTAsphalt')){throw 'Ordinary fast path still binds road/crack textures'}
        if(((-not $case.Fast) -or $case.Road) -and -not $hasRoad){throw 'Road-capable path lost road mapping'}
        $temps=[regex]::Match($assembly,'dcl_temps (\d+)').Groups[1].Value
        $slots=[regex]::Match($assembly,'Approximately (\d+) instruction slots').Groups[1].Value
        $scratch=[regex]::Matches($assembly,'(?m)^dcl_indexableTemp ').Count
        if($case.Name.StartsWith('pair') -and $scratch -ne 0){throw 'Pair specialization retained indexable scratch arrays'}
        $results+=[pscustomobject]@{Case=$case.Name;Result='PASS';Temps=$temps;InstructionSlots=$slots;IndexableScratchArrays=$scratch;Seconds=$timer.Elapsed.TotalSeconds;Diagnostics=$diagnostics}
    } catch {
        $results+=[pscustomobject]@{Case=$case.Name;Result='FAIL';Diagnostics=$_.Exception.Message}
    }
    $results[-1] | ConvertTo-Json -Compress | Write-Output
}
foreach($entryHash in $hashes.GetEnumerator()) {
    if((Get-FileHash -LiteralPath $entryHash.Key).Hash -ne $entryHash.Value){throw "Input changed: $($entryHash.Key)"}
}
$hashes | ConvertTo-Json | Set-Content (Join-Path $reportDir 'source-hashes.json')
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $reportDir 'results.json')
Write-Output "Reduced HLSL only; NOT Unity import, HDRP draw or FPS measurement. Reports: $reportDir"
if($results.Count -ne $Cases.Count){throw 'Unknown or duplicate requested case'}
if(@($results | Where-Object Result -eq 'FAIL').Count){exit 1}
