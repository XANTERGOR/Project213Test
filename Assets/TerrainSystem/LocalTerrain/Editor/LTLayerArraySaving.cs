using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    public static class LTLayerArraySaving
    {
        const string Root="Assets/LocalTerrainGenerated/LayerArrays";
        static LTWorld pendingWorld;
        static LTLayerTextureArrays pendingPack;
        static double deadline;
        public static bool IsSaving=>pendingPack!=null;
        public static void Cancel()
        {
            EditorApplication.update-=Poll;
            AssemblyReloadEvents.beforeAssemblyReload-=Cancel;
            EditorApplication.quitting-=Cancel;
            pendingPack?.Dispose();pendingPack=null;pendingWorld=null;
            EditorUtility.ClearProgressBar();
        }
        static void Folder(string path)
        {
            if(AssetDatabase.IsValidFolder(path))return;
            int slash=path.LastIndexOf('/');var parent=path.Substring(0,slash);
            Folder(parent);AssetDatabase.CreateFolder(parent,path.Substring(slash+1));
        }
        // Saving GPU-only arrays directly serializes stale/empty CPU data. Read every
        // mip/slice into a new readable array; never Apply on the live GPU-only source.
        public static Texture2DArray ReadForSaving(Texture2DArray source,string label)
        {
            if(!SystemInfo.supportsAsyncGPUReadback)throw new InvalidOperationException("GPU readback недоступен: сохранение отменено.");
            var result=new Texture2DArray(source.width,source.height,source.depth,TextureFormat.RGBA32,true,!source.isDataSRGB)
            {name=label,wrapMode=source.wrapMode,filterMode=source.filterMode,anisoLevel=source.anisoLevel};
            try
            {
                for(int mip=0;mip<source.mipmapCount;mip++)
                {
                    if(EditorUtility.DisplayCancelableProgressBar("Сохранение массивов",label+" — mip "+mip,(float)mip/source.mipmapCount))
                        throw new OperationCanceledException();
                    var read=AsyncGPUReadback.Request(source,mip);read.WaitForCompletion();
                    if(read.hasError||read.layerCount!=source.depth)throw new InvalidOperationException("Не удалось прочитать "+label+", mip "+mip);
                    int expected=Math.Max(1,source.width>>mip)*Math.Max(1,source.height>>mip)*4;
                    for(int slice=0;slice<source.depth;slice++)
                    {
                        var bytes=read.GetData<byte>(slice);
                        if(bytes.Length!=expected)throw new InvalidOperationException("Неверный размер GPU readback.");
                        result.SetPixelData(bytes,mip,slice);
                    }
                }
                result.Apply(false,false); // CPU bytes must remain available for asset serialization.
                return result;
            }
            catch{UnityEngine.Object.DestroyImmediate(result);throw;}
        }
        public static void BakeAndSave(LTWorld world)
        {
            if(IsSaving||!world||Application.isPlaying||EditorApplication.isCompiling||world.paintBenchmarkRunning)return;
            if(!world.enableLayerPainting||!world.baseLayer)
            {Debug.LogWarning("Включите покраску и назначьте базовый слой.",world);return;}
            pendingWorld=world;pendingPack=new LTLayerTextureArrays();deadline=EditorApplication.timeSinceStartup+120;
            EditorApplication.update+=Poll;
            AssemblyReloadEvents.beforeAssemblyReload+=Cancel;
            EditorApplication.quitting+=Cancel;
        }
        static void Poll()
        {
            var world=pendingWorld;
            try
            {
                if(!world||Application.isPlaying||EditorApplication.isCompiling||world.paintBenchmarkRunning||!world.enableLayerPainting)
                {Cancel();return;}
                if(EditorApplication.timeSinceStartup>deadline)throw new InvalidOperationException("Истекло время ожидания исходных текстур. Проверьте Texture Streaming и бюджет памяти.");
                var palette=LTLayerArrayBakeAsset.Palette(world);
                string signature=LTLayerArrayBakeAsset.Signature(world,palette);
                long bytes=LTLayerTextureArrays.EstimateBytes(palette.Count,(int)world.arrayColorResolution,(int)world.arrayNormalResolution,(int)world.arrayMaskResolution);
                long preview=world.arrayColorPreview&&world.arrayNormalPreview&&world.arrayMaskPreview?
                    LTLayerTextureArrays.EstimateBytes(world.arrayColorPreview.depth,world.arrayColorPreview.width,world.arrayNormalPreview.width,world.arrayMaskPreview.width):0;
                long staging=LTLayerTextureArrays.EstimateBytes(1,Math.Max((int)world.arrayColorResolution,Math.Max((int)world.arrayNormalResolution,(int)world.arrayMaskResolution)),1,1);
                if(preview+2*bytes+staging>(long)world.arrayMemoryBudgetMiB*1048576)
                    throw new InvalidOperationException($"Для сохранения нужен GPU-бюджет ≈ {(preview+2*bytes+staging)/1048576f:F1} МиБ (предпросмотр + упаковка + сохраняемые массивы). Увеличьте бюджет или уменьшите разрешение. Дополнительно нужна CPU-память для сериализации.");
                if(!pendingPack.Ensure(palette,(int)world.arrayColorResolution,(int)world.arrayNormalResolution,(int)world.arrayMaskResolution,
                    world.arrayMemoryBudgetMiB,0,world.arrayTrilinear,world.arrayAnisotropy))
                {
                    if(!pendingPack.WaitingForSources)throw new InvalidOperationException(pendingPack.Status);
                    // Retain mip leases across editor frames; do not block the editor or ask for repeated clicks.
                    if(EditorUtility.DisplayCancelableProgressBar("Подготовка массивов",pendingPack.Status,0))Cancel();
                    return;
                }
                SaveReady(world,pendingPack,palette,signature);Cancel();
            }
            catch(Exception error){Debug.LogException(error,world);Cancel();}
        }
        static void SaveReady(LTWorld world,LTLayerTextureArrays pack,List<LTSurfaceLayer> palette,string signature)
        {
            string folder=null;
            var owned=new List<UnityEngine.Object>();
            try
            {
                {
                    var bake=ScriptableObject.CreateInstance<LTLayerArrayBakeAsset>();owned.Add(bake);
                    bake.name=world.name+" — массивы слоёв";
                    bake.color=ReadForSaving(pack.color,"Color");owned.Add(bake.color);
                    bake.normal=ReadForSaving(pack.normal,"Normal");owned.Add(bake.normal);
                    bake.mask=ReadForSaving(pack.mask,"Mask");owned.Add(bake.mask);
                    bake.colorDataHash=DataHash(bake.color);bake.normalDataHash=DataHash(bake.normal);bake.maskDataHash=DataHash(bake.mask);
                    bake.layers=palette.ToArray();bake.sourceColors=new Texture2D[palette.Count];
                    bake.sourceNormals=new Texture2D[palette.Count];bake.sourceMasks=new Texture2D[palette.Count];
                    for(int i=0;i<palette.Count;i++)
                    {bake.sourceColors[i]=palette[i].baseColorMap;bake.sourceNormals[i]=palette[i].normalMap;bake.sourceMasks[i]=palette[i].maskMap;}
                    bake.colorSpace=QualitySettings.activeColorSpace;bake.sourceSignature=signature;
                    if(!bake.Matches(world,palette,out string reason))throw new InvalidOperationException(reason);
                    Folder(Root);folder=Root+"/"+Guid.NewGuid().ToString("N");Folder(folder);
                    AssetDatabase.CreateAsset(bake.color,folder+"/Color.asset");
                    AssetDatabase.CreateAsset(bake.normal,folder+"/Normal.asset");
                    AssetDatabase.CreateAsset(bake.mask,folder+"/Mask.asset");
                    AssetDatabase.CreateAsset(bake,folder+"/Palette.asset");
                    foreach(var value in owned){EditorUtility.SetDirty(value);AssetDatabase.SaveAssetIfDirty(value);}
                    // Import the written files before committing the world's reference.
                    foreach(string name in new[]{"Color","Normal","Mask","Palette"})
                        AssetDatabase.ImportAsset(folder+"/"+name+".asset",ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);
                    var loaded=AssetDatabase.LoadAssetAtPath<LTLayerArrayBakeAsset>(folder+"/Palette.asset");
                    if(!loaded||!loaded.Matches(world,palette,out reason))throw new InvalidOperationException("Проверка сохранённого комплекта не пройдена: "+reason);
                    ValidateData(loaded);
                    Undo.RecordObject(world,"Assign saved layer arrays");world.savedLayerArrays=loaded;world.arrayPackRevision=0;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(world);EditorUtility.SetDirty(world);
                    if(world.gameObject.scene.IsValid())EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
                    world.UpdatePainting();EditorGUIUtility.PingObject(loaded);
                    Debug.Log("Массивы сохранены: "+folder+". Сохраните сцену, чтобы сохранить ссылку LTWorld. Предыдущие версии не удалены.",world);
                }
            }
            catch(OperationCanceledException){Debug.Log("Сохранение массивов отменено. Ссылка LTWorld не изменена.",world);}
            catch(Exception error){Debug.LogException(error,world);if(folder!=null)Debug.LogWarning("Созданные файлы оставлены для проверки: "+folder,world);}
            finally
            {
                EditorUtility.ClearProgressBar();
                foreach(var value in owned)if(value&&!AssetDatabase.Contains(value))UnityEngine.Object.DestroyImmediate(value);
            }
        }
        public static string DataHash(Texture2DArray array)
        {
            if(!array||!array.isReadable)throw new InvalidOperationException("Нет сериализованных CPU-данных массива.");
            var hash=new Hash128();hash.Append(array.width);hash.Append(array.height);hash.Append(array.depth);
            for(int mip=0;mip<array.mipmapCount;mip++)for(int slice=0;slice<array.depth;slice++)
            {
                var bytes=array.GetPixelData<byte>(mip,slice);
                if(bytes.Length!=Math.Max(1,array.width>>mip)*Math.Max(1,array.height>>mip)*4)
                    throw new InvalidOperationException("Неполные данные сохранённого массива.");
                hash.Append(bytes);
            }
            return hash.ToString();
        }
        public static void ValidateData(LTLayerArrayBakeAsset bake)
        {
            if(DataHash(bake.color)!=bake.colorDataHash||DataHash(bake.normal)!=bake.normalDataHash||DataHash(bake.mask)!=bake.maskDataHash)
                throw new InvalidOperationException("Контрольная сумма сохранённых массивов не совпала.");
        }
        [MenuItem("Tools/Local Terrain/Validate Saved Layer Arrays")]
        public static void ValidateSelected()
        {
            var bake=Selection.activeObject as LTLayerArrayBakeAsset;
            if(!bake&&Selection.activeGameObject)bake=Selection.activeGameObject.GetComponentInParent<LTWorld>()?.savedLayerArrays;
            if(!bake){Debug.LogWarning("Выберите LTWorld или Palette.asset с сохранёнными массивами.");return;}
            ValidateData(bake);
            Debug.Log("PASS: данные всех срезов и mip-уровней совпадают с контрольными суммами запекания. Это проверка сохранённых CPU-данных, не HDRP-отрисовки.",bake);
        }
    }
    // Prevent shipping a scene that silently repacks its terrain on startup.
    public sealed class LTLayerArrayBuildCheck : IProcessSceneWithReport
    {
        public int callbackOrder=>0;
        public void OnProcessScene(Scene scene,BuildReport report)
        {
            if(!BuildPipeline.isBuildingPlayer)return;
            foreach(var root in scene.GetRootGameObjects())foreach(var world in root.GetComponentsInChildren<LTWorld>(true))
            {
                if(!world.enableLayerPainting)continue;
                string reason="нет сохранённого комплекта";
                if(!world.savedLayerArrays||!world.savedLayerArrays.Matches(world,LTLayerArrayBakeAsset.Palette(world),out reason))
                    throw new BuildFailedException($"LTWorld '{world.name}' ({scene.path}): {reason}. Откройте Массивы → Запечь и сохранить.");
            }
        }
    }
}
