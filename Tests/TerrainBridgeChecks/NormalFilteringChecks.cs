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
        for(int i=0;i<8;i++)
            foreach(var texture in new[]{"Normal","Color","Mask"})
                Require(core.Contains($"SAMPLE_TEXTURE2D_GRAD(_LT{texture}{i}, sampler_LinearRepeat, uv, uvDx/max(_LTTiling{i}.xy,.001), uvDy/max(_LTTiling{i}.xy,.001))"),"shared UV gradients missing: "+texture+i);
        Require(sampling.Contains("LTSampleLayers(position,dx,dy,color,n,ao,smoothness,metallic)"),"shared sampler entry point missing");
        Require(sampling.Contains("_LTGlobalNormal,sampler_TrilinearClamp,input.texCoord0.xy,dx/_LTWorldSize.xy,dy/_LTWorldSize.xy"),"far normal must use ordinary UV gradients");
        var editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        Require(editor.Contains("if(!tessellationDebugExpanded)return;")&&editor.Contains("Отладка тесселяции (АКТИВНА)")&&editor.Contains("Выключить всю отладку тесселяции"),"collapsed debug must expose active state and reset");
        Console.WriteLine("PASS normal filtering source contracts: standard gradients, no source-screen Jacobian; triplanar-only position payload; debug foldout reset. GPU validation required.");
    }
}
