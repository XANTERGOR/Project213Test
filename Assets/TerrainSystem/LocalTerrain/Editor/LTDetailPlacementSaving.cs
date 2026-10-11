using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public static class LTDetailPlacementSaving
    {
        const string BakeFolder="Assets/TerrainSystem/GrassGen";
        static string BakePath(LTDetailRenderer renderer)
        {
            if(!AssetDatabase.IsValidFolder(BakeFolder))
            {
                var guid=AssetDatabase.CreateFolder("Assets/TerrainSystem","GrassGen");
                if(string.IsNullOrEmpty(guid))throw new InvalidOperationException("Не удалось создать "+BakeFolder);
            }
            string name=renderer.name;
            foreach(char c in System.IO.Path.GetInvalidFileNameChars())name=name.Replace(c,'_');
            return AssetDatabase.GenerateUniqueAssetPath(BakeFolder+"/"+name+"_WorldPlacement.asset");
        }
        public static void BakeWorld(LTDetailRenderer renderer)
        {
            if(!renderer||Application.isPlaying||EditorUtility.IsPersistent(renderer))return;
            LTDetailPlacementAsset asset=null;
            try
            {
                var data=renderer.ProcessWorldPlacement(false,(done,total)=>!EditorUtility.DisplayCancelableProgressBar(
                    "Запекание зон детализации",total==0?"Определение зон по слоям и маскам…":$"Проверено: {done} / {total}; без изменений: {renderer.PlacementReusedCells}; пересчитано: {renderer.PlacementGeneratedCells}",total==0?0:(float)done/total));
                foreach(var cell in data)foreach(var reference in cell.references)
                    if(reference&&!EditorUtility.IsPersistent(reference))
                        throw new InvalidOperationException("Нужны префабы и слои из Project: "+reference.name);
                asset=ScriptableObject.CreateInstance<LTDetailPlacementAsset>();asset.name=renderer.name+" World Placement";
                asset.Initialize(data);string path=BakePath(renderer);
                AssetDatabase.CreateAsset(asset,path);EditorUtility.SetDirty(asset);AssetDatabase.SaveAssetIfDirty(asset);
                Undo.RecordObject(renderer,"Assign full world detail placement");
                renderer.savedPlacement=asset;renderer.useSavedPlacement=true;
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);EditorUtility.SetDirty(renderer);
                EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);EditorGUIUtility.PingObject(asset);
                Debug.Log($"Запечены зоны детализации: {data.Length} ячеек; без изменений: {renderer.PlacementReusedCells}, пересчитано: {renderer.PlacementGeneratedCells}. {path}. Сохраните сцену перед сборкой.",renderer);
            }
            catch(OperationCanceledException){Debug.Log("Запекание отменено; предыдущая расстановка сохранена.",renderer);}
            catch(Exception error){Debug.LogException(error,renderer);}
            finally
            {
                EditorUtility.ClearProgressBar();
                if(asset&&!EditorUtility.IsPersistent(asset))UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        public static void ValidateWorld(LTDetailRenderer renderer)
        {
            if(!renderer)return;
            try
            {
                renderer.ProcessWorldPlacement(true,(done,total)=>!EditorUtility.DisplayCancelableProgressBar(
                    "Проверка зон детализации",total==0?"Определение зон по слоям и маскам…":$"Ячейки зон: {done} / {total}",total==0?0:(float)done/total));
                Debug.Log("Расстановка зон детализации полная и актуальная. Перед билдом сохраните сцену.",renderer);
            }
            catch(OperationCanceledException){Debug.Log("Проверка отменена.",renderer);}
            catch(Exception error){Debug.LogException(error,renderer);}
            finally{EditorUtility.ClearProgressBar();}
        }

        public static void Save(LTDetailRenderer renderer)
        {
            if(!renderer||!renderer.CanSavePlacement||EditorUtility.IsPersistent(renderer))
            {Debug.LogWarning("Дождитесь готовой расстановки в сцене вне Play Mode.",renderer);return;}
            var world=renderer.GetComponent<LTWorld>();
            string folder=world&&!string.IsNullOrEmpty(world.outputFolder)&&AssetDatabase.IsValidFolder(world.outputFolder)?world.outputFolder:"Assets";
            string path=EditorUtility.SaveFilePanelInProject("Сохранить расстановку детализации",renderer.name+"_DetailPlacement","asset",
                "Сохраняются готовые загруженные ячейки и предыдущие сохранённые ячейки. Это не запекание всего мира. Существующие файлы не перезаписываются.",folder);
            if(string.IsNullOrEmpty(path))return;
            // The modal dialog may yield to editor updates; capture only afterwards.
            LTDetailPlacementAsset asset=null;
            try
            {
                var ready=renderer.CapturePlacement();
                if(ready.Length==0)throw new InvalidOperationException("Нет полностью подготовленных ячеек для сохранения.");
                foreach(var cell in ready)foreach(var reference in cell.references)
                    if(reference&&!EditorUtility.IsPersistent(reference))
                        throw new InvalidOperationException("Для сохранения используйте префабы и слои из Project: "+reference.name);
                var merged=new Dictionary<Vector2Int,LTDetailPlacementAsset.Cell>();
                // Immutable old cells can be retained across visits. Their keys
                // are revalidated against live inputs on every future reload.
                if(renderer.savedPlacement&&renderer.savedPlacement.Compatible)
                    foreach(var cell in renderer.savedPlacement.Cells)if(cell!=null)merged[cell.coordinate]=cell;
                foreach(var cell in ready)merged[cell.coordinate]=cell;
                asset=ScriptableObject.CreateInstance<LTDetailPlacementAsset>();asset.name=renderer.name+" Detail Placement";
                asset.Initialize(merged.OrderBy(p=>p.Key.y).ThenBy(p=>p.Key.x).Select(p=>p.Value).ToArray());
                path=AssetDatabase.GenerateUniqueAssetPath(path);AssetDatabase.CreateAsset(asset,path);
                EditorUtility.SetDirty(asset);AssetDatabase.SaveAssetIfDirty(asset);
                Undo.RecordObject(renderer,"Assign saved detail placement");renderer.savedPlacement=asset;renderer.useSavedPlacement=true;
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);EditorUtility.SetDirty(renderer);
                EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
                EditorGUIUtility.PingObject(asset);
                Debug.Log($"Расстановка сохранена: {path}. Обновлено {ready.Length} ячеек, всего {asset.CellCount}. Сохраните сцену для ссылки на asset. Остальные/устаревшие ячейки генерируются как обычно.",renderer);
            }
            catch(Exception error)
            {
                // Never delete an existing or partially written asset on failure.
                if(asset&&!EditorUtility.IsPersistent(asset))UnityEngine.Object.DestroyImmediate(asset);
                Debug.LogException(error,renderer);
            }
        }
    }
}
