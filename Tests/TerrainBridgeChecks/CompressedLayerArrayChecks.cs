using System;
using System.IO;
using System.Text.RegularExpressions;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void CompressedLayerArrayChecks()
    {
        int checks=0;
        void Check(bool value,string label){Require(value,label);checks++;}
        foreach(var format in new[]{TextureFormat.RGBA32,TextureFormat.BC5,TextureFormat.BC7})
        for(int width=1;width<=65;width++)for(int height=1;height<=33;height++)
        {
            long expected=format==TextureFormat.RGBA32?(long)width*height*4:(long)Math.Ceiling(width/4d)*(long)Math.Ceiling(height/4d)*16;
            Check(LTLayerArrayStorageUtility.MipBytes(width,height,format)==expected,"complete compressed blocks including tail mips");
        }
        Check(LTLayerArrayStorageUtility.Estimate(10,2048,2048,2048,LTLayerArrayStorage.RGBA32)==671088600,"legacy 640 MiB payload");
        Check(LTLayerArrayStorageUtility.Estimate(10,2048,2048,2048,LTLayerArrayStorage.BC7_BC5)==167772960,"160 MiB compressed payload including tail blocks");
        foreach(var storage in new[]{LTLayerArrayStorage.RGBA32,LTLayerArrayStorage.BC7_BC5})for(int kind=0;kind<3;kind++)
            Check(LTLayerArrayStorageUtility.Format(storage,kind)==(storage==LTLayerArrayStorage.RGBA32?TextureFormat.RGBA32:kind==1?TextureFormat.BC5:TextureFormat.BC7),"storage format mapping");
        Check(LTLayerArrayStorageUtility.NormalFlag(TextureFormat.BC5,true)==2&&LTLayerArrayStorageUtility.NormalFlag(TextureFormat.RGBA32,true)==1&&
            LTLayerArrayStorageUtility.NormalFlag(TextureFormat.BC5,false)==0,"normal presence/encoding ABI");
        bool rejected=false;try{LTLayerArrayStorageUtility.Format((LTLayerArrayStorage)99,0);}catch(ArgumentOutOfRangeException){rejected=true;}
        Check(rejected,"unknown storage rejected");
        const string root="Assets/TerrainSystem/LocalTerrain/";
        string Read(string file)=>File.ReadAllText(root+file);
        var compression=Read("Editor/LTLayerArrayCompression.cs");
        Check(compression.Contains("EditorUtility.CompressTexture(staging,format,TextureCompressionQuality.Best)")&&compression.Contains("staging.format!=format"),"compress owned slices and reject silent codec failure");
        Check(!compression.Contains("AssetDatabase.")&&!compression.Contains("source.Apply("),"compression cannot reimport/write sources");
        Check(compression.Contains("result.SetPixelData(bytes,mip,slice)")&&compression.Contains("staging.GetPixelData<byte>(mip)"),"copy exact compressed mip payloads");
        var asset=Read("LTLayerArrayBakeAsset.cs");
        Check(asset.Contains("if(world.arrayStorage!=LTLayerArrayStorage.RGBA32)text.Append(\":storage:\")"),"RGBA32 saved signature remains unchanged");
        Check(asset.Contains("storage!=world.arrayStorage")&&asset.Contains("LTLayerArrayStorageUtility.Format(storage,kind)"),"saved format is validated");
        Check(Read("LTWorld.cs").Contains("arrayStorage=LTLayerArrayStorage.RGBA32"),"existing scenes default to legacy storage");
        var normal=Read("Shaders/LTLayerNormal.hlsl");
        Check(normal.Contains("if(normalFlag<1.5)return UnpackNormalRGB(packedNormal,strength)")&&normal.Contains("xy*strength,z"),"legacy decode preserved; BC5 strength applied after Z reconstruction");
        Check(Regex.Matches(Read("Shaders/LTLayerBlendCore.hlsl"),"LTUnpackLayerNormal\\(").Count==26,"every terrain LOD/gradient normal read handles BC5");
        Check(Regex.Matches(Read("Shaders/LTRoadTerrainSampling.hlsl"),"LTUnpackLayerNormal\\(").Count==26,"every generated road normal read handles BC5");
        Check(Read("LTPaintRuntime.cs").Contains("NormalFlag(layerArrays.normal.format,layer.normalMap)"),"flags follow the actual bound array, including RGBA32 preview");
        Console.WriteLine($"PASS compressed-array memory/layout and source contracts: {checks} assertions. Native codec/rendering tests are separate.");
    }
}
