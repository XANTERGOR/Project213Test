using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTGlobalBakeSaving
    {
        public static bool IsSaving {get;private set;}
        static void RequireSupport()
        {
            if(!SystemInfo.supportsAsyncGPUReadback||!SystemInfo.SupportsTextureFormat(TextureFormat.BC7)||!SystemInfo.SupportsTextureFormat(TextureFormat.BC5))
                throw new InvalidOperationException("Для сохранения нужны BC7, BC5 и GPU readback. Старое запекание не изменено.");
        }
        // Only owned staging textures are compressed. Preserve the original GPU
        // mip chain, orientation and linear encoding; never alter the source.
        public static Texture2D ReadCompressed(Texture source,string name,TextureFormat format,bool progress=true)
        {
            RequireSupport();
            if(!source||(format!=TextureFormat.BC7&&format!=TextureFormat.BC5)||
                source.width<4||source.height<4||!Mathf.IsPowerOfTwo(source.width)||!Mathf.IsPowerOfTwo(source.height)||
                GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat))
                throw new InvalidOperationException("Нужна linear-карта фона со сторонами степеней двойки, от 4 пикселей.");
            int mips=source is Texture2D texture?texture.mipmapCount:source is RenderTexture rt?rt.mipmapCount:0;
            if(mips<1)throw new InvalidOperationException("Неподдержанный тип карты фона.");
            Texture2D staging=null;
            try
            {
                staging=new Texture2D(source.width,source.height,TextureFormat.RGBA32,mips,true)
                    {name=name,wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Trilinear,anisoLevel=source.anisoLevel};
                for(int mip=0;mip<mips;mip++)
                {
                    if(progress&&EditorUtility.DisplayCancelableProgressBar("Сжатие глобального фона",name+" / mip "+mip,(float)mip/mips))
                        throw new OperationCanceledException();
                    var read=AsyncGPUReadback.Request(source,mip,TextureFormat.RGBA32);read.WaitForCompletion();
                    if(read.hasError)throw new InvalidOperationException("GPU readback: "+name+", mip "+mip);
                    var bytes=read.GetData<byte>();
                    long expected=LTLayerArrayStorageUtility.MipBytes(Math.Max(1,source.width>>mip),Math.Max(1,source.height>>mip),TextureFormat.RGBA32);
                    if(bytes.Length!=expected)throw new InvalidOperationException("Неполные пиксели mip "+mip);
                    staging.SetPixelData(bytes,mip);
                }
                staging.Apply(false,false);
                if(progress&&EditorUtility.DisplayCancelableProgressBar("Сжатие глобального фона",name+" → "+format+" (Best)",1))
                    throw new OperationCanceledException();
                EditorUtility.CompressTexture(staging,format,TextureCompressionQuality.Best);
                // Unity can leave an unsupported/failed compression unchanged.
                if(staging.format!=format||staging.mipmapCount!=mips||staging.isDataSRGB)
                    throw new InvalidOperationException("Компрессор не сохранил формат, mips или linear-пространство.");
                for(int mip=0;mip<mips;mip++)
                    if(staging.GetPixelData<byte>(mip).Length!=LTLayerArrayStorageUtility.MipBytes(Math.Max(1,source.width>>mip),Math.Max(1,source.height>>mip),format))
                        throw new InvalidOperationException("Неполные BC-блоки mip "+mip);
                // Keep the much smaller BC CPU payload for asset serialization,
                // as with saved layer arrays. No persistent RGBA32 copy remains.
                return staging;
            }
            catch {if(staging)Object.DestroyImmediate(staging);throw;}
            finally {if(progress)EditorUtility.ClearProgressBar();}
        }

        public static void BakeAndSave(LTWorld world)=>Save(world,false);
        public static void CompressSaved(LTWorld world)=>Save(world,true);
        static void Save(LTWorld world,bool copySaved)
        {
            if(!world||IsSaving||Application.isPlaying||world.paintBenchmarkRunning||EditorApplication.isCompiling)return;
            var previous=world.savedGlobalBake;
            if(copySaved&&(!previous||!previous.albedo||!previous.normal))
            {Debug.LogWarning("Сначала назначьте сохранённое запекание.",world);return;}
            if(copySaved&&previous.IsBCCompressed)
            {Debug.Log("Глобальные карты уже в BC7/BC5.",world);return;}
            if(!copySaved&&(!world.enableLayerPainting||!world.enableGlobalLayerMaps||!world.source||!world.generatedRoot))
            {Debug.LogWarning("Включите покраску, глобальные карты и создайте чанки.",world);return;}
            string folder=!string.IsNullOrEmpty(world.outputFolder)&&AssetDatabase.IsValidFolder(world.outputFolder)?world.outputFolder:"Assets";
            string path=EditorUtility.SaveFilePanelInProject(copySaved?"Сжатая копия глобальных карт":"Сохранить глобальные карты BC7/BC5",
                world.name+"_GlobalBake_BC","asset","Новый asset: BC7 цвет + BC5 нормали. Старые файлы не перезаписываются.",folder);
            if(string.IsNullOrEmpty(path))return;
            path=AssetDatabase.GenerateUniqueAssetPath(path);
            Texture2D color=null,normal=null;LTGlobalBakeAsset snapshot=null;
            IsSaving=true;
            try
            {
                RequireSupport();
                Texture colorSource,normalSource;
                if(copySaved)
                {colorSource=previous.albedo;normalSource=previous.normal;}
                else
                {
                    foreach(string resource in new[]{"LTEightLayers","LTGlobalLayerBake"})
                    {
                        var shader=Resources.Load<Shader>(resource);
                        if(!shader||!shader.isSupported||ShaderUtil.ShaderHasError(shader))
                            throw new InvalidOperationException("Проверьте компиляцию шейдера "+resource);
                    }
                    int revision=world.globalBakeRevision;world.BakeGlobalLayerMaps();
                    if(world.globalBakeRevision==revision||!(world.globalAlbedoPreview is RenderTexture)||!(world.globalNormalPreview is RenderTexture))
                        throw new InvalidOperationException("Запекание не завершено. "+world.globalLayerStatus);
                    colorSource=world.globalAlbedoPreview;normalSource=world.globalNormalPreview;
                }
                color=ReadCompressed(colorSource,"Global Albedo (BC7 Linear)",TextureFormat.BC7);
                normal=ReadCompressed(normalSource,"Global Normal (BC5 XY Linear)",TextureFormat.BC5);
                // Conversion copies the old signature/layout verbatim. It must
                // never declare stale content freshly baked from today's scene.
                snapshot=copySaved?Object.Instantiate(previous):ScriptableObject.CreateInstance<LTGlobalBakeAsset>();
                snapshot.name=world.name+"_GlobalBake_BC";snapshot.albedo=color;snapshot.normal=normal;
                if(!copySaved)
                {
                    snapshot.worldSize=world.source.size;snapshot.gridRect=world.TerrainRect;
                    snapshot.chunksX=world.chunksX;snapshot.chunksZ=world.chunksZ;snapshot.resolution=color.width;
                    snapshot.signature=LTGlobalBakeAsset.Signature(world);snapshot.triplanar=world.triplanarTexturing;
                }
                AssetDatabase.CreateAsset(snapshot,path);
                AssetDatabase.AddObjectToAsset(color,snapshot);AssetDatabase.AddObjectToAsset(normal,snapshot);
                EditorUtility.SetDirty(snapshot);EditorUtility.SetDirty(color);EditorUtility.SetDirty(normal);
                AssetDatabase.SaveAssetIfDirty(snapshot);
                Undo.RecordObject(world,"Assign compressed global terrain bake");world.savedGlobalBake=snapshot;
                PrefabUtility.RecordPrefabInstancePropertyModifications(world);
                EditorUtility.SetDirty(world);EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
                // Switch immediately; do not retain the transient uncompressed atlas.
                world.ReleasePainting();world.UpdatePainting();
                EditorGUIUtility.PingObject(snapshot);
                Debug.Log($"Глобальные карты сохранены: {path}. BC7/BC5, {snapshot.TextureBytes/(1024.0*1024):0.0} МиБ GPU (+ BC-копия для CPU). Старое запекание сохранено. Сохраните сцену для ссылки LTWorld.",world);
            }
            catch(OperationCanceledException){Debug.Log("Сжатие отменено. Ссылка на старое запекание не изменена.",world);}
            catch(Exception error)
            {
                Debug.LogException(error,world);
                if(snapshot&&AssetDatabase.Contains(snapshot))Debug.LogWarning("Неполный asset оставлен для проверки: "+path,world);
            }
            finally
            {
                IsSaving=false;EditorUtility.ClearProgressBar();
                if(color&&!AssetDatabase.Contains(color))Object.DestroyImmediate(color);
                if(normal&&!AssetDatabase.Contains(normal))Object.DestroyImmediate(normal);
                if(snapshot&&!AssetDatabase.Contains(snapshot))Object.DestroyImmediate(snapshot);
            }
        }
    }
}
