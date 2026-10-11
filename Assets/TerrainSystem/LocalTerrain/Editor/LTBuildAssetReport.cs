using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Reports actual packed dependencies, including Resources and engine assets.
    // Packed sizes are not download sizes and do not include executable code.
    public sealed class LTBuildAssetReport : IPostprocessBuildWithReport
    {
        public int callbackOrder => 1000;
        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                var sizes = new Dictionary<string, ulong>();
                foreach (var packed in report.packedAssets)
                    foreach (var asset in packed.contents)
                    {
                        string path = string.IsNullOrEmpty(asset.sourceAssetPath) ? "(built-in / unknown)" : asset.sourceAssetPath;
                        sizes.TryGetValue(path, out ulong size);
                        sizes[path] = size + asset.packedSize;
                    }
                string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/BuildAssets"));
                Directory.CreateDirectory(folder);
                string output = Path.Combine(folder, "assets-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".tsv");
                var text = new StringBuilder("Packed bytes\tAsset path\n");
                foreach (var item in sizes.OrderByDescending(item => item.Value))
                    text.Append(item.Value).Append('\t').Append(item.Key.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
                File.WriteAllText(output, text.ToString(), new UTF8Encoding(false));
                Debug.Log("Состав сборки (размер упакованных ресурсов, не размер EXE): " + output);
            }
            catch (Exception error)
            {
                Debug.LogWarning("Не удалось сохранить отчёт о ресурсах сборки: " + error.Message);
            }
        }
    }
}
