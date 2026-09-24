$ErrorActionPreference = 'Stop'
$projectDir = Split-Path $PSScriptRoot -Parent
$shaderDir = Join-Path $projectDir 'Assets/TerrainSystem/LocalTerrain/Shaders'
$reportDir = Join-Path $projectDir ('Logs/LayerArrayChecks/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
Write-Output ('Reduced HLSL compile only, not Unity/HDRP draw. Reports: ' + $reportDir)
$nl=[Environment]::NewLine
$inputs=@('LTEightLayerProperties.hlsl','LTLayerBlendCore.hlsl','LTRoadProjection.hlsl','LTProjectedLayers.hlsl','LTLayerTessellation.hlsl')
$before=@{}
foreach($name in $inputs){$before[$name]=(Get-FileHash (Join-Path $shaderDir $name)).Hash}
$before | ConvertTo-Json | Set-Content (Join-Path $reportDir 'hashes-before.json')
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
public static class NativeHlsl {
 [DllImport("d3dcompiler_47.dll", CallingConvention=CallingConvention.StdCall)]
 static extern int D3DCompile(byte[] src, UIntPtr size, string name, IntPtr defines, IntPtr include,
 string entry, string target, uint flags, uint flags2, out IntPtr code, out IntPtr errors);
 [DllImport("d3dcompiler_47.dll", CallingConvention=CallingConvention.StdCall)]
 static extern int D3DDisassemble(IntPtr src, UIntPtr size, uint flags, string comments, out IntPtr text);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetPointer(IntPtr self);
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate UIntPtr GetSize(IntPtr self);
 static IntPtr Ptr(IntPtr b) {
 var f=Marshal.ReadIntPtr(Marshal.ReadIntPtr(b),3*IntPtr.Size);
 return ((GetPointer)Marshal.GetDelegateForFunctionPointer(f,typeof(GetPointer)))(b);
 }
 static UIntPtr Size(IntPtr b) {
 var f=Marshal.ReadIntPtr(Marshal.ReadIntPtr(b),4*IntPtr.Size);
 return ((GetSize)Marshal.GetDelegateForFunctionPointer(f,typeof(GetSize)))(b);
 }
 static byte[] Bytes(IntPtr b) {var bytes=new byte[(int)Size(b).ToUInt64()];Marshal.Copy(Ptr(b),bytes,0,bytes.Length);return bytes;}
 public static string Compile(string source,string target,string output) {
 byte[] bytes=Encoding.UTF8.GetBytes(source); IntPtr code=IntPtr.Zero,errors=IntPtr.Zero,asm=IntPtr.Zero;
 try {
 int hr=D3DCompile(bytes,(UIntPtr)bytes.Length,output,IntPtr.Zero,IntPtr.Zero,"Main",target,2048,0,out code,out errors);
 string diagnostics=errors==IntPtr.Zero?"":Encoding.UTF8.GetString(Bytes(errors)).TrimEnd('\0');
 File.WriteAllText(output+".log",diagnostics);
 if(hr<0) throw new Exception("D3DCompile "+target+" HRESULT "+hr+": "+diagnostics);
 File.WriteAllBytes(output+".dxbc",Bytes(code));
 int dis=D3DDisassemble(Ptr(code),Size(code),0,null,out asm);
 if(dis<0)throw new Exception("D3DDisassemble failed "+dis);
 File.WriteAllText(output+".asm",Encoding.UTF8.GetString(Bytes(asm)).TrimEnd('\0'));
 return diagnostics;
 } finally { if(asm!=IntPtr.Zero)Marshal.Release(asm);if(errors!=IntPtr.Zero)Marshal.Release(errors);if(code!=IntPtr.Zero)Marshal.Release(code); }
 }
}
'@
$properties=Get-Content -Raw (Join-Path $shaderDir 'LTEightLayerProperties.hlsl')
$uniforms=($properties -split 'CBUFFER_START\(UnityPerMaterial\)',2)[1]
$uniforms=($uniforms -split '// shared constant between lit and layered lit',2)[0]
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
// Same canonical RGB decoding as installed SRP Packing.hlsl; no imported texture/GPU test.
float3 UnpackNormalRGB(float4 packed,float scale) {
 float3 n=packed.rgb*2-1; n.xy*=scale; return n;
}
struct AttributesMesh {float3 positionOS:POSITION;};
'@
$core=Get-Content -Raw (Join-Path $shaderDir 'LTLayerBlendCore.hlsl')
$core=[regex]::Replace($core,'(?m)^#include.*GlobalSamplers.*\r?\n','')
$road=Get-Content -Raw (Join-Path $shaderDir 'LTRoadProjection.hlsl')
$core=[regex]::Replace($core,'(?m)^#include.*LTRoadProjection.*$',[System.Text.RegularExpressions.MatchEvaluator]{param($m) $road})
$projected=Get-Content -Raw (Join-Path $shaderDir 'LTProjectedLayers.hlsl')
$tess=Get-Content -Raw (Join-Path $shaderDir 'LTLayerTessellation.hlsl')
$common=$preamble+$nl+'cbuffer UnityPerMaterial {'+$nl+$uniforms+$nl+'};'+$nl+$core+$nl+$projected
$planarEntry=@'
float4 Main(float4 p:SV_Position,float2 uv:TEXCOORD0):SV_Target {
 float3 c,n;float a,s,m;
 LTSampleLayers(uv,ddx(uv),ddy(uv),c,n,a,s,m);
 return float4(c+n+float3(a,s,m),1);
}
'@
$projectedEntry=@'
float4 Main(float4 p:SV_Position,float3 uv:TEXCOORD0,float3 normal:TEXCOORD1):SV_Target {
 float3 c,g;float a,s,m;
 LTSampleProjectedLayers(uv,normalize(normal),_LTTriplanar>.5,c,g,a,s,m);
 return float4(c+g+float3(a,s,m),1);
}
'@
$vertexEntry=@'
float4 Main(float3 uv:POSITION,float3 normal:NORMAL):SV_Position {
 float3 c,g;float a,s,m;
 LTSampleProjectedLayers(uv,normalize(normal),_LTTriplanar>.5,c,g,a,s,m);
 return float4(c+g+float3(a,s,m),1);
}
'@
$geometryEntry=@'
float4 Main(float4 p:POSITION):SEMANTIC {
 float2 uv=p.xy;
 float d=LTLayerDisplacement(uv);
 float mud=LTDeformationDepth(uv);
 float road=LTRoadDisplacementMultiplier(uv);
 return float4(d,mud,road,1);
}
'@
$combinedEntry=@'
float4 Main(float4 p:SV_Position,float3 uv:TEXCOORD0,float3 normal:TEXCOORD1):SV_Target {
 float3 c,g;float a,s,m;
 LTSampleProjectedLayers(uv,normalize(normal),_LTTriplanar>.5,c,g,a,s,m);
 float d=LTLayerDisplacement(uv.xz);
 float mud=LTDeformationDepth(uv.xz);
 float road=LTRoadDisplacementMultiplier(uv.xz);
 return float4(c+g+float3(a+d,s+mud,m+road),1);
}
'@
$cases=@(
 @{Name='combined-ps';Target='ps_5_0';Source=$common+$nl+$tess+$nl+$combinedEntry;Sampling=$true},
 @{Name='planar-ps';Target='ps_5_0';Source=$common+$nl+$planarEntry;Sampling=$true},
 @{Name='projected-ps';Target='ps_5_0';Source=$common+$nl+$projectedEntry;Sampling=$true},
 @{Name='lod-sampling-vs';Target='vs_5_0';Source='#define SHADER_STAGE_RAY_TRACING 1'+$nl+$common+$nl+$vertexEntry;Sampling=$true},
 @{Name='geometry-ps';Target='ps_5_0';Source=$common+$nl+$tess+$nl+$geometryEntry.Replace('SEMANTIC','SV_Target');Sampling=$false},
 @{Name='geometry-vs';Target='vs_5_0';Source='#define SHADER_STAGE_RAY_TRACING 1'+$nl+$common+$nl+$tess+$nl+$geometryEntry.Replace('SEMANTIC','SV_Position');Sampling=$false}
)
$results=@()
foreach($case in $cases){
 $out=Join-Path $reportDir $case.Name
 [IO.File]::WriteAllText($out+'.hlsl',$case.Source)
 $timer=[Diagnostics.Stopwatch]::StartNew()
 try{
  $diagnostics=[NativeHlsl]::Compile($case.Source,$case.Target,$out)
  $assembly=Get-Content -Raw ($out+'.asm')
  $bindingTable=([regex]::Match($assembly,'(?s)// Resource Bindings:.*?(?=\r?\n\s*'+$case.Target+')')).Value
  if(-not $bindingTable){throw 'Missing resource table'}
  $families=if($case.Sampling){@('Color','Normal','Mask')}else{@('Mask')}
  foreach($family in $families){
   if($bindingTable -notmatch ("_LT"+$family+"Array\s")){throw "Missing live array _LT${family}Array"}
   if($bindingTable -notmatch ("sampler_LT"+$family+"Array\s")){throw "Missing matching sampler for _LT${family}Array"}
  }
  foreach($binding in [regex]::Matches($bindingTable,'(?m)^//\s+sampler_(LT(?:Color|Normal|Mask)Array)\s+sampler\s')){
   $texture='_'+$binding.Groups[1].Value
   if($bindingTable -notmatch ('(?m)^//\s+'+[regex]::Escape($texture)+'\s+texture\s')){throw "Sampler owner texture stripped: $texture"}
  }
  if(-not $case.Sampling -and $bindingTable -match 'sampler_LT(?:Color|Normal)Array\s'){throw 'Geometry-only stage retains unrelated sampler'}
  if($bindingTable -match '_LT(?:Color|Normal|Mask)\d+\s'){throw 'Legacy individual layer texture still live'}
  if($assembly -notmatch '_LTLayerSlices2'){throw 'Missing third array mapping vector'}
  foreach($i in 0..2){if($bindingTable -notmatch ("_LTWeights"+$i+"\s")){throw "Missing live weights $i"}}
  if($assembly -notmatch '_LTRoadProjectionSlots2'){throw 'Missing third road slot vector'}
  $resourceLines=@($bindingTable -split '\r?\n' | Where-Object {$_ -match '^//\s+\S+\s+(texture|sampler|cbuffer)\s'})
  $results+=[pscustomobject]@{Case=$case.Name;Target=$case.Target;Result='PASS';Seconds=$timer.Elapsed.TotalSeconds;ResourceBindings=$resourceLines.Count;Diagnostics=$diagnostics}
 }catch{
  $results+=[pscustomobject]@{Case=$case.Name;Target=$case.Target;Result='FAIL';Seconds=$timer.Elapsed.TotalSeconds;Diagnostics=$_.Exception.Message}
 }
 $results[-1] | ConvertTo-Json -Compress | Write-Output
}
foreach($name in $inputs){
 if((Get-FileHash (Join-Path $shaderDir $name)).Hash -ne $before[$name]){throw "Source changed during validation: $name"}
}
$results | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $reportDir 'results.json')
Write-Output 'Source hashes unchanged.'
if(@($results | Where-Object Result -eq 'FAIL').Count){exit 1}
