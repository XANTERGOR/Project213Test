using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    public static class LTDetailGpuCheck
    {
        [MenuItem("Tools/Local Terrain/Check GPU Details")]
        public static void Run()
        {
            var report=new StringBuilder();bool passed=false;
            try
            {
                report.AppendLine("GPU: "+SystemInfo.graphicsDeviceName+" / "+SystemInfo.graphicsDeviceType);
                foreach(string adapterName in new[]{"GrassWind","BaseProps","HdrpLit"})
                {
                string path="Assets/TerrainSystem/LocalTerrain/Resources/LTDetailGpu/"+adapterName+".ltdetailshader";
                report.AppendLine("Adapter: "+adapterName);
                AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceUpdate|ImportAssetOptions.ForceSynchronousImport);
                var adapter=AssetDatabase.LoadAssetAtPath<LTDetailGpuShader>(path);
                if(!adapter||!adapter.indirect||!string.IsNullOrEmpty(adapter.error))throw new Exception("Shader adapter: "+(adapter?adapter.error:"missing"));
                report.AppendLine("Retained material keyword sets: "+(adapter.buildVariants?.Length??0));
                if(adapter.buildVariants!=null)foreach(var variant in adapter.buildVariants)
                    if(!variant||variant.shader!=adapter.indirect||!variant.enableInstancing)throw new Exception("Invalid retained material variant.");
                var retained=new System.Collections.Generic.HashSet<string>();
                if(adapter.buildVariants!=null)foreach(var variant in adapter.buildVariants)retained.Add(KeywordSignature(variant,adapter.source));
                foreach(var guid in AssetDatabase.FindAssets("t:Material",new[]{"Assets"}))
                {
                    string materialPath=AssetDatabase.GUIDToAssetPath(guid);
                    if(!materialPath.EndsWith(".mat",StringComparison.OrdinalIgnoreCase))continue;
                    var sourceMaterial=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                    if(sourceMaterial&&sourceMaterial.shader==adapter.source&&!retained.Contains(KeywordSignature(sourceMaterial,adapter.source)))
                        throw new Exception("Source material keyword set was lost: "+materialPath+" / "+KeywordSignature(sourceMaterial,adapter.source));
                }
                report.AppendLine("PASS retained keyword coverage of source .mat assets.");
                var original=new Material(adapter.source);
                var material=new Material(adapter.indirect){enableInstancing=true};
                try
                {
                    material.EnableKeyword("PROCEDURAL_INSTANCING_ON");
                    report.AppendLine("Shader supported: "+adapter.indirect.isSupported+"; passes: "+material.passCount+"; pipeline: "+GraphicsSettings.currentRenderPipeline);
                    for(int p=0;p<material.passCount;p++)report.AppendLine("Available pass: "+material.GetPassName(p));
                    if(!adapter.indirect.isSupported||material.FindPass("GBuffer")<0||material.FindPass("DepthOnly")<0||material.FindPass("ShadowCaster")<0)
                        throw new Exception("HDRP raster passes unavailable. Run with an active HDRP asset; a fallback/error shader is not a valid test.");
                    // BaseShaderProps itself has no motion-vector pass. Preserve source
                    // capabilities rather than inventing an unrelated material pass.
                    for(int p=0;p<original.passCount;p++)
                        if(material.FindPass(original.GetPassName(p))<0)throw new Exception("Lost source pass: "+original.GetPassName(p));
                    report.AppendLine("MotionVectors source / indirect: "+original.FindPass("MotionVectors")+" / "+material.FindPass("MotionVectors"));
                    int compiled=0;
                    for(int p=0;p<material.passCount;p++)
                    {
                        // Raster passes only; ray tracing is deliberately not supported by this backend.
                        string name=material.GetPassName(p);
                        if(name.Contains("DXR")||name.Contains("RayTracing")||name.Contains("META"))continue;
                        ShaderUtil.CompilePass(material,p,true);
                        report.AppendLine("Compiled raster pass: "+name);compiled++;
                    }
                    foreach(var message in ShaderUtil.GetShaderMessages(adapter.indirect))report.AppendLine(message.severity+": "+message.message);
                    if(ShaderUtil.ShaderHasError(adapter.indirect))throw new Exception("Indirect shader compilation failed.");
                    if(compiled==0)throw new Exception("No raster passes compiled.");
                    report.AppendLine("PASS indirect raster shader compilation ("+compiled+" passes).");
                    if(adapter.buildVariants!=null)foreach(var variant in adapter.buildVariants)
                    {
                        material.CopyPropertiesFromMaterial(variant);material.enableInstancing=true;
                        material.EnableKeyword("PROCEDURAL_INSTANCING_ON");
                        for(int p=0;p<material.passCount;p++)
                        {
                            string pass=material.GetPassName(p);
                            if(pass.Contains("DXR")||pass.Contains("RayTracing")||pass.Contains("META"))continue;
                            ShaderUtil.CompilePass(material,p,true);
                        }
                        if(ShaderUtil.ShaderHasError(adapter.indirect))
                        {
                            foreach(var message in ShaderUtil.GetShaderMessages(adapter.indirect))report.AppendLine(message.severity+": "+message.message);
                            throw new Exception("Retained material variant compilation failed: "+variant.name);
                        }
                        report.AppendLine("PASS retained keyword set: "+string.Join(";",variant.shaderKeywords));
                    }
                }
                finally{UnityEngine.Object.DestroyImmediate(material);UnityEngine.Object.DestroyImmediate(original);}
                }
                CheckCull(report);passed=true;
                report.AppendLine("PASS native checks. Scene lighting, wind, shadows, camera motion and FPS still require visual validation.");
            }
            catch(Exception ex){report.AppendLine("FAIL: "+ex);Debug.LogException(ex);}
            Directory.CreateDirectory("Logs/DetailProfiles");
            File.WriteAllText("Logs/DetailProfiles/gpu-details-check.txt",report.ToString());
            Debug.Log(report.ToString());
            if(Application.isBatchMode)EditorApplication.Exit(passed?0:1);
        }
        static string KeywordSignature(Material material,Shader source)
        {
            var result=new System.Collections.Generic.List<string>();
            foreach(string keyword in material.shaderKeywords)
                if(source.keywordSpace.FindKeyword(keyword).isValid)result.Add(keyword);
            result.Sort(StringComparer.Ordinal);return string.Join(";",result);
        }
        static void CheckCull(StringBuilder report)
        {
            var source=Resources.Load<ComputeShader>("LTDetailCull");
            if(!source||!SystemInfo.supportsComputeShaders)throw new Exception("Compute shader unavailable.");
            var shader=UnityEngine.Object.Instantiate(source);
            GraphicsBuffer instances=null,visible=null,args=null;
            try
            {
                const int count=65;var data=new LTDetailGpuData[count];
                for(int i=0;i<count;i++)data[i]=new LTDetailGpuData{objectToWorld=Matrix4x4.identity,worldToObject=Matrix4x4.identity,
                    positionSize=new Vector4(i,0,5,1),lodPositionRandom=new Vector4(i,0,5,.25f),center=new Vector4(i,0,5,0),extents=new Vector4(.1f,.1f,.1f,0)};
                instances=new GraphicsBuffer(GraphicsBuffer.Target.Structured,count,LTDetailGpuData.Stride);instances.SetData(data);
                visible=new GraphicsBuffer(GraphicsBuffer.Target.Append,count,4);
                args=new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments,1,GraphicsBuffer.IndirectDrawIndexedArgs.size);
                args.SetData(new[]{new GraphicsBuffer.IndirectDrawIndexedArgs{indexCountPerInstance=3}});
                int kernel=shader.FindKernel("Cull");
                shader.SetBuffer(kernel,"_LTInstances",instances);shader.SetBuffer(kernel,"_LTVisible",visible);
                shader.SetVectorArray("_Planes",new[]{new Vector4(1,0,0,1),new Vector4(-1,0,0,10),new Vector4(0,1,0,100),new Vector4(0,-1,0,100),new Vector4(0,0,1,100),new Vector4(0,0,-1,100)});
                shader.SetVectorArray("_LodThresholds",new[]{new Vector4(.5f,0,0,0),new Vector4(.01f,0,0,0),Vector4.zero,Vector4.zero,Vector4.zero,Vector4.zero,Vector4.zero,Vector4.zero});
                shader.SetVector("_CameraPosition",Vector4.zero);shader.SetVector("_CameraForward",new Vector4(0,0,1,0));
                shader.SetVector("_Projection",new Vector4(0,1,1,0));shader.SetVector("_Distance",new Vector4(1000,50,.5f,1000));
                shader.SetInt("_Count",count);shader.SetInt("_HasLod",0);shader.SetInt("_LodCount",2);shader.SetInt("_TargetLod",0);
                shader.SetInt("_Thin",0);shader.SetInt("_LimitShadows",0);shader.SetInt("_PrefabShadowMode",0);shader.SetInt("_DrawShadowMode",0);
                void Expect(int expected,string name)
                {
                    visible.SetCounterValue(0);shader.Dispatch(kernel,2,1,1);GraphicsBuffer.CopyCount(visible,args,4);
                    var result=new GraphicsBuffer.IndirectDrawIndexedArgs[1];args.GetData(result);
                    if(result[0].instanceCount!=expected)throw new Exception(name+": "+result[0].instanceCount+" != "+expected);
                    var indices=new uint[count];visible.GetData(indices);
                    var unique=new System.Collections.Generic.HashSet<uint>();
                    for(int i=0;i<expected;i++)if(indices[i]>=count||!unique.Add(indices[i]))throw new Exception(name+": invalid/duplicate visible ID.");
                    report.AppendLine("PASS GPU cull: "+name+" = "+expected);
                }
                Expect(11,"frustum + partial thread group");
                shader.SetInt("_PrefabShadowMode",1);shader.SetInt("_DrawShadowMode",1);Expect(65,"offscreen shadow casters");
                shader.SetInt("_LimitShadows",1);shader.SetVector("_Distance",new Vector4(1000,50,.5f,6));Expect(4,"shadow range near");
                shader.SetInt("_DrawShadowMode",0);Expect(7,"shadow range far visible only");
                shader.SetInt("_PrefabShadowMode",3);Expect(0,"expired ShadowsOnly never becomes color");
                shader.SetInt("_PrefabShadowMode",0);shader.SetInt("_HasLod",1);shader.SetInt("_LimitShadows",0);Expect(0,"LOD0 excluded");
                shader.SetInt("_TargetLod",1);Expect(11,"LOD1 selected");
                shader.SetVector("_Projection",new Vector4(0,1,1,1));
                shader.SetVectorArray("_LodThresholds",new[]{Vector4.zero,Vector4.zero,Vector4.zero,Vector4.zero,Vector4.zero,Vector4.zero,Vector4.zero,Vector4.zero});
                Expect(11,"quality LOD clamp");
                shader.SetInt("_HasLod",0);shader.SetInt("_TargetLod",0);shader.SetInt("_Thin",1);
                shader.SetVector("_Distance",new Vector4(1000,0,1,0));Expect(11,"thinning keeps full density");
                shader.SetVector("_Distance",new Vector4(10,0,0,0));Expect(6,"deterministic thinning excludes sparse tail");
                shader.SetVector("_Projection",new Vector4(1,1,1,0));shader.SetInt("_Thin",0);shader.SetInt("_HasLod",1);
                shader.SetVector("_Distance",new Vector4(1000,50,1,0));Expect(11,"orthographic LOD");
                shader.SetInt("_Thin",1);shader.SetVector("_Distance",new Vector4(1,0,0,0));Expect(0,"distance exclusion");
            }
            finally{instances?.Dispose();visible?.Dispose();args?.Dispose();UnityEngine.Object.DestroyImmediate(shader);}
        }
    }
}
