using System;
using System.IO;
partial class Checks
{
    static void NormalFilteringChecks()
    {
        const string root="Assets/TerrainSystem/LocalTerrain/Shaders/";
        var core=File.ReadAllText(root+"LTLayerBlendCore.hlsl");
        var sampling=File.ReadAllText(root+"LTEightLayerSampling.hlsl");
        var domain=File.ReadAllText(root+"LTLayerTessellation.hlsl");
        Require(!sampling.Contains("LTSourceNormalGradients")&&!core.Contains("normalUvDx"),"experimental normal Jacobian must be removed");
        Require(domain.Contains("if(_LTTriplanar>.5)input.color=float4(input.positionRWS,1);"),"source position payload must be limited to triplanar mapping");
        for(int i=0;i<12;i++)
        {
            Require(core.Contains($"float2 dx=uvDx/max(_LTTiling{i}.xy,.001),dy=uvDy/max(_LTTiling{i}.xy,.001);"),"default world gradients missing: "+i);
            Require(core.Contains($"LTRoadMappedUV({i},coveragePosition,coverageDx,coverageDy,uv,dx,dy,roadRight);"),"road UV/gradient mapping missing: "+i);
            foreach(var texture in new[]{"Normal","Color","Mask"})
                Require(System.Text.RegularExpressions.Regex.Replace(core,@"\s+","").Contains(
                    $"SAMPLE_TEXTURE2D_ARRAY_GRAD(_LT{texture}Array,sampler_LT{texture}Array,uv,LTLayerSlice({i}),dx,dy)"),
                    "shared array UV gradients/local-to-world slice mapping missing: "+texture+i);
        }
        Require(sampling.Contains("LTSampleLayers(position,dx,dy,color,n,ao,smoothness,metallic)"),"shared sampler entry point missing");
        Require(sampling.Contains("_LTGlobalNormal,sampler_TrilinearClamp,input.texCoord0.xy,dx/_LTWorldSize.xy,dy/_LTWorldSize.xy"),"far normal must use ordinary UV gradients");
        var editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        Require(editor.Contains("if(!tessellationDebugExpanded)return;")&&editor.Contains("Отладка тесселяции (АКТИВНА)")&&editor.Contains("Выключить всю отладку тесселяции"),"collapsed debug must expose active state and reset");
        Console.WriteLine("PASS normal filtering source contracts: standard gradients, no source-screen Jacobian; triplanar-only position payload; debug foldout reset. GPU validation required.");
    }
}
