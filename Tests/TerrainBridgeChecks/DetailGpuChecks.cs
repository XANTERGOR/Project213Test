using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LocalTerrainPrototype;

partial class Checks
{
    static void DetailGpuChecks()
    {
        Require(Marshal.SizeOf<LTDetailGpuData>()==LTDetailGpuData.Stride&&LTDetailGpuData.Stride==192,"GPU structured buffer stride");
        var names=new[]{"objectToWorld","worldToObject","positionSize","lodPositionRandom","center","extents"};
        var offsets=new[]{0,64,128,144,160,176};
        for(int i=0;i<names.Length;i++)Require(Marshal.OffsetOf<LTDetailGpuData>(names[i]).ToInt32()==offsets[i],"GPU ABI: "+names[i]);
        const string root="Assets/TerrainSystem/LocalTerrain/";
        var gpu=File.ReadAllText(root+"LTDetailGpuRenderer.cs");
        var renderer=File.ReadAllText(root+"LTDetailRenderer.cs");
        var hook=File.ReadAllText(root+"Shaders/LTDetailIndirect.hlsl");
        var cull=File.ReadAllText(root+"Resources/LTDetailCull.compute");
        Require(!gpu.Contains(".GetData(")&&!gpu.Contains("AsyncGPUReadback"),"runtime GPU path has no readback");
        Require(gpu.Contains("Graphics.RenderMeshIndirect")&&gpu.Contains("GraphicsBuffer.CopyCount(draw.visible,draw.args,4)"),"GPU controls indirect instance count");
        Require(gpu.Contains("Dictionary<Camera,View>")&&gpu.Contains("group.views.TryGetValue(camera"),"deferred cameras own independent outputs");
        Require(renderer.Contains("gpuRenderer?.Remove(group)")&&renderer.Contains("gpuRenderer?.Dispose();gpuRenderer=null;"),"unload and reset release GPU resources");
        Require(hook.Contains("#define UNITY_PREV_MATRIX_M ApplyCameraTranslationToMatrix(LTObjectToWorld)")&&hook.Contains("_LTVisible[GetIndirectInstanceID(unity_InstanceID)]"),"stable static motion history by source identity");
        Require(cull.Contains("if(i>=_Count)return")&&cull.Contains("_PrefabShadowMode==3 && mode==0"),"compute guards partial threads and ShadowsOnly");
        Require(gpu.Contains("ReservedBytes+bytes+viewBytes>budget"),"budget reservation includes pending outputs before root upload");
        Require(!gpu.Contains("BuildGpuData()")&&gpu.Contains("source.CopyGpuData(group.uploaded,count,staging)"),"incremental matrix preparation uses bounded reusable staging");
        Require(gpu.Contains("if(!Prepare(source,group,view)){PendingGroups++;RecordFallback(source,\"Ожидание GPU upload / выделения буферов.\");return false;}"),"partial uploads never submit incomplete GPU groups");
        Require(gpu.Contains("AllocatedBytes-ReleasedBytes==ResidentBytes"),"memory audit checks total allocation/free balance");
        Require(gpu.Contains("selection.prefabMode==part.castShadows&&selection.mode==draw.mode")&&gpu.Contains("selection.lod==lod"),"shared visibility retains LOD and source/draw shadow partitions");
        Require(gpu.Contains("if(!draw.selection.culled)")&&gpu.Contains("foreach(var selection in view.selections)selection.culled=false"),"one dispatch per selection per camera call");
        Require(gpu.Contains("foreach(var selection in view.selections)Count(selection.visible)"),"shared append buffers counted once");
        var importer=File.ReadAllText(root+"Editor/LTDetailGpuShaderImporter.cs");
        Require(importer.Contains("material.SetTexture(property,Presence(source.GetTexture(property)))")&&importer.Contains("if(!source)return null"),"retained shader variants preserve texture presence without unrelated texture libraries");
        Require(File.ReadAllText(root+"Resources/LTDetailGpu/BaseProps.ltdetailshader").Contains("8ce045feb4d898749ad119ec3bb00567")&&
            File.ReadAllText(root+"Resources/LTDetailGpu/HdrpLit.ltdetailshader").Contains("6e4ae4064600d784cac1e41a9e6f2e59"),"stone and wood adapters target audited source shader GUIDs");
        var upload=new LTDetailUploadBudget();upload.Begin(1,1,1920,2);
        Require(upload.UploadCount(100,192,2048)==10,"upload byte budget limits chunk");
        upload.Spend(.4,960,1);upload.Begin(1,1,1920,2);
        Require(upload.Bytes==960&&upload.UploadCount(100,192,2048)==5,"second camera shares remaining upload budget");
        upload.Spend(.7,960,1);
        Require(!upload.CanAllocate&&upload.UploadCount(100,192,2048)==0,"soft time budget stops subsequent operations");
        upload.Begin(2,1,1920,2);Require(upload.Bytes==0&&upload.Milliseconds==0&&upload.CanAllocate,"next frame resets budget");
        upload.Spend(0,0,2);Require(!upload.CanAllocate&&upload.UploadCount(3,192,2048)==3,"allocation cap independent of upload byte cap");
        upload.Begin(3,1,192,2);Require(upload.UploadCount(100,192,2048)==1,"large groups make bounded progress");
        upload.Spend(0,192);Require(upload.UploadCount(100,192,2048)==0,"exhausted byte quota cannot overshoot");
        foreach(var file in new[]{"LTDetailGpuData.cs","LTDetailGpuRenderer.cs","LTDetailGpuShader.cs","Editor/LTDetailGpuShaderImporter.cs","Editor/LTDetailGpuCheck.cs","Shaders/LTDetailIndirect.hlsl","Resources/LTDetailCull.compute","Resources/LTDetailGpu","Resources/LTDetailGpu/GrassWind.ltdetailshader","Resources/LTDetailGpu/BaseProps.ltdetailshader","Resources/LTDetailGpu/HdrpLit.ltdetailshader"})
            Require(Regex.IsMatch(File.ReadAllText(root+file+".meta"),@"(?m)^guid: [0-9a-f]{32}\r?$"),"valid Unity GUID: "+file);
        Console.WriteLine("GPU data layout and source-contract checks passed (native shader/cull tests are separate).");
    }
}
