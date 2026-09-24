using System;
using UnityEngine;
partial class Checks
{
    static void DisplacementNormalChecks()
    {
        // Source contract only: native shader compilation/rendering is tested in Unity.
        const string root="Assets/TerrainSystem/LocalTerrain/Shaders/";
        Require(System.IO.File.Exists(root+"LTEightLayerSampling.hlsl"),"run terrain checks from the project root");
        var fragment=System.IO.File.ReadAllText(root+"LTEightLayerSampling.hlsl");
        var domain=System.IO.File.ReadAllText(root+"LTLayerTessellation.hlsl");
        Require(!fragment.Contains("LTDisplacementProbe")&&!domain.Contains("LTDisplacementProbe"),"displacement normal probes must be removed");
        Require(!fragment.Contains("_LTDisplacementNormalStep")&&!fragment.Contains("cross(ddy("),"fragment must not reconstruct displaced face normals");
        Require(!domain.Contains("input.normalWS=")&&!domain.Contains("input.tangentWS.xyz="),"domain must preserve source terrain normals/tangents");
        Require(domain.Contains("input.positionRWS+=n*LTLayerDisplacement(position)*envelope*roadDisplacement;"),"position displacement must remain");
        Require(domain.Contains("float roadDisplacement=LTRoadDisplacementMultiplier(position);"),"asphalt suppression must use shared road coordinates");
        Require(fragment.Contains("LTSampleLayers(position,dx,dy,color,n,ao,smoothness,metallic)")&&fragment.Contains("normalTS=n;"),"material normal map must remain");
        var data=System.IO.File.ReadAllText(root+"LTEightLayerData.hlsl");
        Require(fragment.Contains("out float3 sampledNormalTS")&&fragment.Contains("sampledNormalTS=n;"),"normal probe must reuse blended texture sample");
        Require(fragment.IndexOf("sampledNormalTS=n;")<fragment.IndexOf("if(_LTDebugCoverage>6.5 && _LTDebugCoverage<9.5)"),"vector probes must not get grey-mode flat normals");
        Require(data.IndexOf("float3 ltSourceNormalWS=input.tangentToWorld[2];")<data.IndexOf("ApplyDoubleSidedFlipOrMirror(input"),"source probe must precede double-sided flip");
        int probeStart=data.IndexOf("if(_LTDebugCoverage>9.5 && _LTDebugCoverage<16.5)");
        Require(probeStart>data.IndexOf("GetBuiltinData(input"),"unlit probe must override builtin lighting after initialization");
        var probe=data.Substring(probeStart,data.IndexOf("#ifdef _ALPHATEST_ON",probeStart)-probeStart);
        foreach(var expected in new[]{"vectorValue=ltSampledNormalTS","vectorValue=surfaceData.normalWS","vectorValue=ltSourceNormalWS","vectorValue=input.tangentToWorld[0]","vectorValue=input.tangentToWorld[1]","surfaceData.baseColor=0;","surfaceData.specularColor=0;","surfaceData.coatMask=0;","builtinData.bakeDiffuseLighting=0;","builtinData.backBakeDiffuseLighting=0;","GetInverseCurrentExposureMultiplier()","defined(VARYINGS_NEED_CULLFACE)"})
            Require(probe.Contains(expected),"vector probe contract missing: "+expected);
        Require(!probe.Contains("SAMPLE_TEXTURE")&&!probe.Contains("surfaceData.normalWS="),"diagnostics must neither resample textures nor replace normals");
        var runtime=System.IO.File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTPaintRuntime.cs");
        Require(runtime.Contains("surfaceProbe>=6&&surfaceProbe<=16"),"runtime must accept every vector probe");
        Console.WriteLine("PASS vector diagnostic source contracts: original/blended/final normals, TBN, facing availability, lighting suppression, no extra texture reads. GPU validation still required.");
        Console.WriteLine("PASS terrain normal source contract: position displacement retained, terrain frame and material normal maps preserved, no displacement normal probes or face correction.");
    }
}
