param([Parameter(Mandatory=$true)][string]$ValidationRoot,[string]$UnityExe='D:/Unity/6000.3.23f1/Editor/Unity.exe')
$ErrorActionPreference='Stop'
$taskProject=Split-Path $PSScriptRoot -Parent
$taskRoot=[IO.Path]::GetFullPath($ValidationRoot)
if($taskRoot.StartsWith($taskProject.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Validation must be outside the working project.'}
if(Test-Path -LiteralPath $taskRoot){throw 'Use a fresh independent directory.'}
$hashes=@{}
function Expand-Source([string]$path) {
    $hashes[$path]=(Get-FileHash -LiteralPath $path).Hash
    $source=Get-Content -LiteralPath $path -Raw
    return [regex]::Replace($source,'(?m)^\s*#include "([^"]+)"[^\r\n]*',[System.Text.RegularExpressions.MatchEvaluator]{param($m)
        $include=$m.Groups[1].Value
        if($include.StartsWith('Packages/')){return ''}
        if(-not $include.StartsWith('Assets/TerrainSystem/LocalTerrain/')){throw "Unexpected include: $include"}
        return (Expand-Source (Join-Path $taskProject $include))
    })
}
$shader=Expand-Source (Join-Path $taskProject 'Assets/TerrainSystem/LocalTerrain/Editor/LTCoverageSamplingProbe.shader')
$preamble=@'
#define CBUFFER_START(n) cbuffer n {
#define CBUFFER_END };
#define SAMPLER(n) SamplerState n
#define TEXTURE2D(n) Texture2D<float4> n
#define TEXTURE2D_ARRAY(n) Texture2DArray<float4> n
#define SAMPLE_TEXTURE2D_ARRAY_LOD(t,s,u,i,l) t.SampleLevel(s,float3(u,i),l)
#define SAMPLE_TEXTURE2D_ARRAY_GRAD(t,s,u,i,x,y) t.SampleGrad(s,float3(u,i),x,y)
#define SAMPLE_TEXTURE2D_LOD(t,s,u,l) t.SampleLevel(s,u,l)
#define SAMPLE_TEXTURE2D_GRAD(t,s,u,x,y) t.SampleGrad(s,u,x,y)
#define LOAD_TEXTURE2D_ARRAY(t,p,s) t.Load(int4(p,s,0))
#define LOAD_TEXTURE2D(t,p) t.Load(int3(p,0))
SamplerState sampler_LinearClamp;
SamplerState sampler_LinearRepeat;
float3 UnpackNormalRGB(float4 p,float s){float3 n=p.rgb*2-1;n.xy*=s;return n;}
float4 GetFullScreenTriangleVertexPosition(uint id){float2 uv=float2((id<<1)&2,id&2);return float4(uv*2-1,0,1);}
'@
$shader=$shader.Replace('HLSLINCLUDE',"HLSLINCLUDE`n"+$preamble).Replace('Tags { "RenderPipeline"="HDRenderPipeline" }','')
# Probe the generated, buffer-addressed road adapter against the same frozen sampler.
$adapter=Expand-Source (Join-Path $taskProject 'Assets/TerrainSystem/LocalTerrain/Shaders/LTRoadTerrainSampling.hlsl')
$adapter='float _LTRoadTerrainReady; float4 _LTRoadTerrainGrid,_LTRoadTerrainCell;'+"`n"+$adapter
$adapter+=@'
float4 PlanarFrag():SV_Target
{
 float3 c,n,g;float a,s,m;
 LTSampleLayersMappedFrame(_CheckPoint.xz,_CheckDx.xz,_CheckDy.xz,_CheckPoint.xz,_CheckDx.xz,_CheckDy.xz,1,c,n,a,s,m,g);
 return Result(c,g,a,s,m);
}
float4 AdapterFrag():SV_Target
{
 float3 c,g;float a,s,m;
 LTAutoSampleProjectedLayersGrad(0,_CheckPoint.xyz,normalize(_LTProbeNormal.xyz),_LTTriplanar>.5,_CheckDx.xyz,_CheckDy.xyz,c,g,a,s,m);
 return Result(c,g,a,s,m);
}
'@
$shader=$shader.Insert($shader.IndexOf('        ENDHLSL'),"`n"+$adapter+"`n")
$shader=$shader.Insert($shader.LastIndexOf('    }'),@'
        Pass { Name "RoadAdapter"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment AdapterFrag
            ENDHLSL
        }
        Pass { Name "Planar"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment PlanarFrag
            ENDHLSL
        }
'@)
New-Item -ItemType Directory -Path "$taskRoot/Assets/Editor","$taskRoot/Packages","$taskRoot/ProjectSettings" | Out-Null
[IO.File]::WriteAllText("$taskRoot/Assets/TopTwoProbe.shader",$shader)
Copy-Item -LiteralPath "$PSScriptRoot/TopTwoLayersGpuCheck.cs" -Destination "$taskRoot/Assets/Editor/TopTwoLayersGpuCheck.cs"
[IO.File]::WriteAllText("$taskRoot/Packages/manifest.json",'{"dependencies":{}}')
Copy-Item -LiteralPath "$taskProject/ProjectSettings/ProjectVersion.txt" -Destination "$taskRoot/ProjectSettings/ProjectVersion.txt"
[IO.File]::WriteAllText("$taskRoot/ownership.txt",'Owned top-two GPU validation. Independent copies, no shared caches or links. Retain directory and logs.')
$hashes | ConvertTo-Json | Set-Content "$taskRoot/source-hashes.json"
$taskProcess=Start-Process -FilePath $UnityExe -ArgumentList @('-batchmode','-force-d3d11','-projectPath',('"'+$taskRoot+'"'),'-executeMethod','TopTwoLayersGpuCheck.Run','-logFile',('"'+$taskRoot+'/unity.log"')) -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
[pscustomobject]@{PID=$taskProcess.Id;StartTime=$taskProcess.StartTime;Project=$taskRoot;Result="$taskRoot/top-two-result.txt"}|ConvertTo-Json
