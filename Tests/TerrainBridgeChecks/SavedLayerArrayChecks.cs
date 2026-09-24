using System;
using System.IO;
using System.Text.RegularExpressions;

partial class Checks
{
    static void SavedLayerArrayContracts()
    {
        const string root="Assets/TerrainSystem/LocalTerrain/";
        string Read(string name)=>Regex.Replace(Regex.Replace(File.ReadAllText(root+name),@"(?m)//[^\r\n]*|/\*[\s\S]*?\*/",""),@"\s+","");
        var asset=Read("LTLayerArrayBakeAsset.cs");var owner=Read("LTLayerTextureArrays.cs");
        var saving=Read("Editor/LTLayerArraySaving.cs");var editor=Read("Editor/LTEditor.cs");
        void Check(bool value,string message){if(!value)throw new Exception("Saved array source contract: "+message);}
        Check(Read("LTWorld.cs").Contains("publicLTLayerArrayBakeAssetsavedLayerArrays;"),"serialized world reference");
        Check(owner.Contains("if(valid&&world.arrayPackRevision==0)")&&owner.Contains("ownsArrays=false;color=saved.color;normal=saved.normal;mask=saved.mask;"),"borrow persistent arrays without copying or packing");
        Check(owner.Contains("if(ownsArrays){Destroy(color);Destroy(normal);Destroy(mask);}")&&owner.Contains("if(!ownsArrays)Dispose();"),"borrowed arrays survive disposal and transition to preview");
        var savedPath=owner.Substring(owner.IndexOf("if(valid&&world.arrayPackRevision==0)",StringComparison.Ordinal));
        savedPath=savedPath.Substring(0,savedPath.IndexOf("if(!ownsArrays)Dispose();",StringComparison.Ordinal));
        Check(!savedPath.Contains("SetFiltering(")&&!savedPath.Contains("Build(")&&!savedPath.Contains(".Apply("),"saved fast path does not modify shared assets");
        foreach(var token in new[]{"version!=CurrentVersion","colorSpace!=QualitySettings.activeColorSpace","array.depth==layers.Length",
            "array.mipmapCount==mips","array.isDataSRGB==!linear","sourceColors[i]!=layers[i].baseColorMap",
            "sourceNormals[i]!=layers[i].normalMap","sourceMasks[i]!=layers[i].maskMap","sourceSignature!=Signature(world,palette)"})
            Check(asset.Contains(token),"staleness/structure validation: "+token);
        var fingerprint=asset.Substring(asset.IndexOf("publicstaticstringSignature(",StringComparison.Ordinal));
        Check(fingerprint.Contains("TryGetGUIDAndLocalFileIdentifier")&&fingerprint.Contains("GetAssetDependencyHash")&&fingerprint.Contains("texture.imageContentsHash"),"restart-stable identities and input contents");
        Check(!fingerprint.Contains("GetInstanceID")&&!fingerprint.Contains(".tint")&&!fingerprint.Contains(".normalStrength")&&!fingerprint.Contains("JsonUtility"),"no transient identities or scalar settings in saved fingerprint");
        Check(saving.Contains("AsyncGPUReadback.Request(source,mip)")&&saving.Contains("read.WaitForCompletion();")&&
            saving.Contains("read.layerCount!=source.depth")&&saving.Contains("result.SetPixelData(bytes,mip,slice);")&&
            saving.Contains("result.Apply(false,false);")&&!saving.Contains("source.Apply("),"all GPU slices/mips copied to serializable CPU bytes, not empty live arrays");
        Check(saving.Contains("hash.Append(bytes);")&&saving.Contains("ValidateData(loaded);")&&saving.Contains("ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate"),"post-write data hashes after import");
        Check(saving.IndexOf("ValidateData(loaded);",StringComparison.Ordinal)<saving.IndexOf("world.savedLayerArrays=loaded;",StringComparison.Ordinal),"publish reference only after validation");
        foreach(var name in new[]{"Color","Normal","Mask","Palette"})Check(saving.Contains("/"+name+".asset"),"separate saved "+name+" asset");
        Check(saving.Contains("Guid.NewGuid().ToString(\"N\")")&&!saving.Contains("AssetDatabase.DeleteAsset(")&&!saving.Contains("AssetDatabase.SaveAssets(")&&!saving.Contains("SaveScene("),"new version paths, no deletion/global save/scene save");
        Check(saving.Contains("Undo.RecordObject(world,")&&saving.Contains("PrefabUtility.RecordPrefabInstancePropertyModifications(world)"),"undo/prefab assignment");
        Check(saving.Contains("EditorApplication.update+=Poll;")&&saving.Contains("EditorApplication.update-=Poll;")&&saving.Contains("pendingPack?.Dispose();")&&saving.Contains("pendingPack.WaitingForSources"),"streaming wait keeps leases and cancellation cleans up");
        Check(saving.Contains("AssemblyReloadEvents.beforeAssemblyReload+=Cancel;")&&saving.Contains("EditorApplication.quitting+=Cancel;"),"cancel on reload/quit");
        Check(saving.Contains("IProcessSceneWithReport")&&saving.Contains("BuildPipeline.isBuildingPlayer")&&saving.Contains("thrownewBuildFailedException("),"player build rejects missing/stale saved arrays");
        Check(editor.Contains("LTLayerArraySaving.BakeAndSave(world)")&&editor.Contains("LTLayerArraySaving.Cancel()"),"save/cancel inspector actions");
        Console.WriteLine("PASS saved layer-array source contracts: persistent ownership, stable signatures, GPU readback/all mips, post-save hashes, transactional assignment, cancellation and build guard. Native save/reopen not executed.");
    }
}
