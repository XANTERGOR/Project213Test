using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public static class LTGlobalBakeSaving
    {
        static Texture2D ReadMap(RenderTexture source,string name)
        {
            var previous=RenderTexture.active;
            var texture=new Texture2D(source.width,source.height,TextureFormat.RGBA32,true,true)
            {name=name,wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Trilinear};
            try
            {
                RenderTexture.active=source;
                texture.ReadPixels(new Rect(0,0,source.width,source.height),0,0,false);
                texture.Apply(true,false);return texture;
            }
            catch {UnityEngine.Object.DestroyImmediate(texture);throw;}
            finally {RenderTexture.active=previous;}
        }
        public static void BakeAndSave(LTWorld world)
        {
            if(Application.isPlaying||world.paintBenchmarkRunning)
            {Debug.LogWarning("Сохраняйте запекание вне Play Mode и GPU-теста.",world);return;}
            if(!world.enableLayerPainting||!world.enableGlobalLayerMaps||!world.source||!world.generatedRoot)
            {Debug.LogWarning("Включите покраску, глобальные карты и создайте чанки.",world);return;}
            string folder=!string.IsNullOrEmpty(world.outputFolder)&&AssetDatabase.IsValidFolder(world.outputFolder)?world.outputFolder:"Assets";
            string path=EditorUtility.SaveFilePanelInProject("Сохранить глобальные карты",world.name+"_GlobalBake","asset",
                "Один asset с Albedo и Normal. Существующие файлы не перезаписываются.",folder);
            if(string.IsNullOrEmpty(path))return;
            path=AssetDatabase.GenerateUniqueAssetPath(path);
            Texture2D color=null,normal=null;LTGlobalBakeAsset snapshot=null;
            try
            {
                foreach(string resource in new[]{"LTEightLayers","LTGlobalLayerBake"})
                {
                    var shader=Resources.Load<Shader>(resource);
                    if(!shader||!shader.isSupported||ShaderUtil.ShaderHasError(shader))
                        throw new InvalidOperationException("Проверьте компиляцию шейдера "+resource);
                }
                int revision=world.globalBakeRevision;
                world.BakeGlobalLayerMaps();
                if(world.globalBakeRevision==revision||!(world.globalAlbedoPreview is RenderTexture albedo)||!(world.globalNormalPreview is RenderTexture normals))
                    throw new InvalidOperationException("Запекание не завершено. "+world.globalLayerStatus);
                color=ReadMap(albedo,"Global Albedo (Linear)");normal=ReadMap(normals,"Global Normal (XYZ Linear)");
                snapshot=ScriptableObject.CreateInstance<LTGlobalBakeAsset>();
                snapshot.albedo=color;snapshot.normal=normal;snapshot.worldSize=world.source.size;
                snapshot.chunksX=world.chunksX;snapshot.chunksZ=world.chunksZ;snapshot.resolution=color.width;
                snapshot.signature=LTGlobalBakeAsset.Signature(world);
                snapshot.triplanar=world.triplanarTexturing;
                AssetDatabase.CreateAsset(snapshot,path);
                AssetDatabase.AddObjectToAsset(color,snapshot);AssetDatabase.AddObjectToAsset(normal,snapshot);
                EditorUtility.SetDirty(snapshot);EditorUtility.SetDirty(color);EditorUtility.SetDirty(normal);
                AssetDatabase.SaveAssetIfDirty(snapshot);
                Undo.RecordObject(world,"Assign global terrain bake");world.savedGlobalBake=snapshot;
                PrefabUtility.RecordPrefabInstancePropertyModifications(world);
                EditorUtility.SetDirty(world);EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
                // Switch to the persistent pair immediately: don't retain a second GPU atlas.
                world.ReleasePainting();world.UpdatePainting();
                EditorGUIUtility.PingObject(snapshot);
                Debug.Log("Глобальные карты сохранены: "+path+". Сохраните сцену, чтобы сохранить ссылку LTWorld.",world);
            }
            catch(Exception error)
            {
                Debug.LogException(error,world);
                // Never delete an existing/partially created asset on failure; leave it recoverable.
                if(snapshot&&AssetDatabase.Contains(snapshot))Debug.LogWarning("Неполный asset сохранён для проверки: "+path,world);
            }
            finally
            {
                if(color&&!AssetDatabase.Contains(color))UnityEngine.Object.DestroyImmediate(color);
                if(normal&&!AssetDatabase.Contains(normal))UnityEngine.Object.DestroyImmediate(normal);
                if(snapshot&&!AssetDatabase.Contains(snapshot))UnityEngine.Object.DestroyImmediate(snapshot);
            }
        }
    }
}
