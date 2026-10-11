using System;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTRockRenderSaving
    {
        internal static void Store(LTWorld world,LTMeshStamp stamp,LTRockRenderOutput output)
        {
            // Called only by the normal terrain rebuild, never by validation.
            // Do not mutate a duplicated stamp's shared asset or an imported source mesh.
            if(string.IsNullOrEmpty(world.outputFolder)||!AssetDatabase.IsValidFolder(world.outputFolder))return;
            string key=GlobalObjectId.GetGlobalObjectIdSlow(stamp).ToString();
            // Unsaved scene objects do not have a reliable persistent owner identity.
            if(string.IsNullOrEmpty(stamp.gameObject.scene.path))key+="/session/"+stamp.GetInstanceID();
            var asset=stamp.renderAsset;
            if(!asset||asset.ownerKey!=key||!AssetDatabase.GetAssetPath(asset).StartsWith(world.outputFolder+"/",StringComparison.Ordinal))
            {
                asset=ScriptableObject.CreateInstance<LTRockRenderAsset>();asset.name=stamp.name+" Render LODs";asset.ownerKey=key;
                string path=AssetDatabase.GenerateUniqueAssetPath(world.outputFolder+"/RockRender.asset");AssetDatabase.CreateAsset(asset,path);
                stamp.renderAsset=asset;
            }
            Mesh Copy(Mesh input,Mesh existing)
            {
                if(!existing||AssetDatabase.GetAssetPath(existing)!=AssetDatabase.GetAssetPath(asset))
                {existing=new Mesh();AssetDatabase.AddObjectToAsset(existing,asset);}
                EditorUtility.CopySerialized(input,existing);existing.hideFlags=HideFlags.None;EditorUtility.SetDirty(existing);return existing;
            }
            Mesh[] CopyLevels(Mesh[] input,Mesh[] existing)
            {
                // Keep surplus old subassets recoverable; never delete external references.
                var result=new Mesh[input.Length];for(int i=0;i<result.Length;i++)result[i]=Copy(input[i],existing!=null&&i<existing.Length?existing[i]:null);return result;
            }
            asset.combined=Copy(output.combined,asset.combined);asset.rocks=CopyLevels(output.RockLODs,asset.rocks);
            asset.transitions=CopyLevels(output.TransitionLODs,asset.transitions);asset.sourceSubmeshes=output.sourceSubmeshes;asset.atlasUV=output.atlasUV;
            asset.rockAtlasPerLOD=output.rockAtlasPerLOD;
            EditorUtility.SetDirty(asset);EditorUtility.SetDirty(stamp);
        }
    }
}
