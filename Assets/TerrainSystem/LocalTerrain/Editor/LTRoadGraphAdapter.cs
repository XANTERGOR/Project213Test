using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    internal static class LTRoadGraphAdapter
    {
        internal const string SourceGuid="9c895762697cbee47b42bf800e862623";
        internal const string AdapterPath="Assets/TerrainSystem/LocalTerrain/Resources/LTRoadGraphGround.shadergraph";
        internal const string TemplatePath="Assets/TerrainSystem/LocalTerrain/Editor/LTRoadGraphBlendTemplate.json";
        internal const string StatePath="Assets/TerrainSystem/LocalTerrain/Editor/LTRoadGraphBlendState.json";
        const string AdapterGuid="2712bdc31c3849b4ba0fe9b7e8de7e52";
        const string LegacySourceHash="040A283CF7549B8B4CA78104AFD2ADFE68FA7381982A612FEB745322E410DE3E";
        const string LegacyAdapterHash="5D8A496F06F1EBA6194CD14E17B6F2DB6072A9F653DE73B3036F99FDFA68A462";
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false);
        internal static bool Busy {get;private set;}
        [Serializable] sealed class State
        {public int version;public string sourceGuid,sourceHash,adapterHash,templateHash;}
        static string Hash(byte[] bytes){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-","");}
        static string HashFile(string path)=>Hash(File.ReadAllBytes(path));
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        internal static bool IsCurrent(out string reason)
        {
            reason="Road_Shader.shadergraph изменился; нажмите «Обновить адаптер бленда» в инспекторе Road";
            try
            {
                string source=AssetDatabase.GUIDToAssetPath(SourceGuid);if(!File.Exists(source)||!File.Exists(AdapterPath))return false;
                if(!File.Exists(StatePath))return HashFile(source)==LegacySourceHash&&HashFile(AdapterPath)==LegacyAdapterHash;
                var state=JsonUtility.FromJson<State>(File.ReadAllText(StatePath));
                return state!=null&&state.version==LTRoadGraphAdapterJson.Version&&state.sourceGuid==SourceGuid&&
                    state.sourceHash==HashFile(source)&&state.adapterHash==HashFile(AdapterPath)&&state.templateHash==HashFile(TemplatePath);
            }
            catch(Exception e){reason="Не удалось проверить адаптер бленда: "+e.Message;return false;}
        }
        static void CheckPath(string path)
        {
            // Reject linked files/directories before touching an owned output or backup.
            string full=Path.GetFullPath(path),project=Path.GetFullPath(".").TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            Require(full.StartsWith(project,StringComparison.OrdinalIgnoreCase),"Путь вне проекта: "+path);
            for(string p=full;!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))
                if(File.Exists(p)||Directory.Exists(p))Require((File.GetAttributes(p)&FileAttributes.ReparsePoint)==0,"Ссылка/reparse point в пути: "+p);
        }
        static void AtomicWrite(string path,byte[] bytes)
        {
            CheckPath(path);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllBytes(temp,bytes);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        static void Restore(string path,byte[] bytes)
        {if(bytes!=null)AtomicWrite(path,bytes);else if(File.Exists(path)){CheckPath(path);File.Delete(path);}}
        static void Report(string backup,string text)
        {try{File.WriteAllText(backup+"/result.txt",text,Utf8);}catch(Exception e){Debug.LogWarning("[Road Graph Adapter] Не удалось записать отчёт: "+e.Message);}}
        static void Import(string path)=>AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport|ImportAssetOptions.ForceUpdate);
        internal static string UpdateAdapter()
        {
            Require(!Busy&&!EditorApplication.isCompiling&&!EditorApplication.isUpdating&&!EditorApplication.isPlayingOrWillChangePlaymode,"Дождитесь окончания импорта/компиляции и выйдите из Play Mode.");
            string sourcePath=AssetDatabase.GUIDToAssetPath(SourceGuid);
            Require(sourcePath.EndsWith(".shadergraph",StringComparison.OrdinalIgnoreCase)&&File.Exists(sourcePath),"Исходный Road_Shader.shadergraph не найден.");
            Require(AssetDatabase.AssetPathToGUID(AdapterPath)==AdapterGuid,"Не совпадает GUID собственного адаптера. Чужой asset не будет перезаписан.");
            CheckPath(AdapterPath);CheckPath(AdapterPath+".meta");CheckPath(StatePath);CheckPath(StatePath+".meta");CheckPath(TemplatePath);
            byte[] sourceBytes=File.ReadAllBytes(sourcePath),templateBytes=File.ReadAllBytes(TemplatePath);
            // Decode BOM in the same way as File.ReadAllText, but hash exact bytes.
            string candidate=LTRoadGraphAdapterJson.Build(File.ReadAllText(sourcePath),File.ReadAllText(TemplatePath));
            var candidateBytes=Utf8.GetBytes(candidate);
            var state=new State{version=LTRoadGraphAdapterJson.Version,sourceGuid=SourceGuid,sourceHash=Hash(sourceBytes),adapterHash=Hash(candidateBytes),templateHash=Hash(templateBytes)};
            byte[] oldAdapter=File.ReadAllBytes(AdapterPath),oldState=File.Exists(StatePath)?File.ReadAllBytes(StatePath):null;
            byte[] oldStateMeta=File.Exists(StatePath+".meta")?File.ReadAllBytes(StatePath+".meta"):null;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var protectedFiles=Directory.GetFiles(Path.GetDirectoryName(sourcePath),"*.mat")
                .Concat(new[]{sourcePath,sourcePath+".meta",AdapterPath+".meta","Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path))
                .Concat(Directory.GetFiles("ProjectSettings")).Where(File.Exists).Distinct().ToDictionary(p=>p,HashFile);
            string backup="Logs/RoadGraphAdapterUpdates/"+Guid.NewGuid().ToString("N");CheckPath(backup);Directory.CreateDirectory(backup);
            File.WriteAllBytes(backup+"/adapter-before.shadergraph",oldAdapter);
            if(oldState!=null)File.WriteAllBytes(backup+"/state-before.json",oldState);
            if(oldStateMeta!=null)File.WriteAllBytes(backup+"/state-before.json.meta",oldStateMeta);
            File.WriteAllBytes(backup+"/source-used.shadergraph",sourceBytes);
            File.WriteAllText(backup+"/candidate.shadergraph",candidate,Utf8);
            bool async=ShaderUtil.allowAsyncCompilation,wrote=false,committed=false;var materials=new List<Material>();Busy=true;
            try
            {
                Require(HashFile(sourcePath)==state.sourceHash&&HashFile(TemplatePath)==state.templateHash,"Исходник изменился во время подготовки. Повторите обновление.");
                // No scripts, source materials, meshes or scenes are written. The
                // fixed adapter GUID preserves existing renderer references.
                wrote=true;AtomicWrite(AdapterPath,candidateBytes);Import(AdapterPath);
                var shader=AssetDatabase.LoadAssetAtPath<Shader>(AdapterPath);Require(shader,"Unity не импортировала граф адаптера.");
                materials.Add(new Material(shader){hideFlags=HideFlags.HideAndDontSave});
                foreach(string path in protectedFiles.Keys.Where(p=>p.EndsWith(".mat",StringComparison.OrdinalIgnoreCase)))
                {
                    var sourceMaterial=AssetDatabase.LoadAssetAtPath<Material>(path);
                    if(sourceMaterial&&sourceMaterial.shader&&AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(sourceMaterial.shader))==SourceGuid)
                        materials.Add(new Material(sourceMaterial){shader=shader,hideFlags=HideFlags.HideAndDontSave});
                }
                ShaderUtil.allowAsyncCompilation=false;
                foreach(var material in materials)foreach(bool terrain in new[]{false,true})
                {
                    if(terrain)material.EnableKeyword("_LT_ROAD_TERRAIN");else material.DisableKeyword("_LT_ROAD_TERRAIN");
                    foreach(string pass in new[]{"GBuffer","Forward","DepthOnly","ShadowCaster","MotionVectors","META"})
                    {
                        int index=material.FindPass(pass);Require(index>=0,"Отсутствует проход "+pass);
                        ShaderUtil.CompilePass(material,index,true);
                    }
                    Require(!ShaderUtil.ShaderHasError(shader),"Ошибка шейдера: "+string.Join("; ",ShaderUtil.GetShaderMessages(shader).Select(m=>m.message)));
                }
                Require(HashFile(sourcePath)==state.sourceHash&&HashFile(TemplatePath)==state.templateHash,"Исходник изменился во время компиляции. Повторите обновление.");
                Require(HashFile(AdapterPath)==state.adapterHash,"Unity изменила файл адаптера при импорте; обновление не подтверждено.");
                AtomicWrite(StatePath,Utf8.GetBytes(JsonUtility.ToJson(state,true)+"\n"));Import(StatePath);
                foreach(var f in protectedFiles)Require(File.Exists(f.Key)&&HashFile(f.Key)==f.Value,"Изменился защищённый файл: "+f.Key);
                foreach(var s in scenes)Require(s.scene.IsValid()&&s.scene.isLoaded&&s.scene.isDirty==s.dirty&&s.scene.rootCount==s.roots,"Состояние пользовательской сцены изменилось во время обновления.");
                committed=true;
                string report="Адаптер бленда обновлён; оба режима скомпилированы. Теперь нажмите «Перестроить / запечь» у дороги. Исходный граф, материалы, меши и сцена не сохранялись. Резервная копия: "+backup;
                Report(backup,report);Debug.Log("[Road Graph Adapter] "+report);return report;
            }
            catch(Exception e)
            {
                if(committed)throw new InvalidOperationException("Адаптер обновлён, но завершение операции вызвало ошибку: "+e.Message+" Резервная копия: "+backup,e);
                if(wrote&&!committed)
                {
                    try{Restore(AdapterPath,oldAdapter);Restore(StatePath,oldState);Restore(StatePath+".meta",oldStateMeta);Import(AdapterPath);if(oldState!=null)Import(StatePath);}
                    catch(Exception rollback){throw new AggregateException("Не удалось восстановить адаптер. Резервная копия: "+backup,e,rollback);}
                }
                string message="Обновление не подтверждено. Предыдущий адаптер восстановлен; исходники не перезаписывались. "+e.Message+" Резервная копия: "+backup;
                Report(backup,message);throw new InvalidOperationException(message,e);
            }
            finally
            {foreach(var material in materials)if(material)Object.DestroyImmediate(material);ShaderUtil.allowAsyncCompilation=async;Busy=false;}
        }
    }
}
