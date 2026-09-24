using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Narrow adapter for HDRP raster Shader Graphs and two qualified source shaders. Internal generation API is
    // version checked; unsupported package layouts fail closed to the CPU path.
    [ScriptedImporter(3,"ltdetailshader")]
    public sealed class LTDetailGpuShaderImporter : ScriptedImporter
    {
        const string Variables="Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl";
        const string Instancing="#include \"Packages/com.unity.render-pipelines.core/ShaderLibrary/UnityInstancing.hlsl\"";
        const string Hook="Assets/TerrainSystem/LocalTerrain/Shaders/LTDetailIndirect.hlsl";
        [Serializable] sealed class Definition { public string sourceGuid; }
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var adapter=ScriptableObject.CreateInstance<LTDetailGpuShader>();
            adapter.name=Path.GetFileNameWithoutExtension(ctx.assetPath);
            ctx.AddObjectToAsset("Adapter",adapter);ctx.SetMainObject(adapter);
            try
            {
                var definition=JsonUtility.FromJson<Definition>(File.ReadAllText(ctx.assetPath));
                string sourcePath=AssetDatabase.GUIDToAssetPath(definition.sourceGuid);
                bool graph=sourcePath.EndsWith(".shadergraph",StringComparison.OrdinalIgnoreCase);
                bool qualifiedSource=definition.sourceGuid=="8ce045feb4d898749ad119ec3bb00567"||
                    definition.sourceGuid=="6e4ae4064600d784cac1e41a9e6f2e59";
                if(!graph&&(!qualifiedSource||!sourcePath.EndsWith(".shader",StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Нужен HDRP Shader Graph или проверенный BaseShaderProps / HDRP Lit.");
                foreach(var path in AssetDatabase.GetDependencies(sourcePath))ctx.DependsOnSourceAsset(path);
                ctx.DependsOnSourceAsset(Variables);ctx.DependsOnSourceAsset(Hook);
                adapter.source=AssetDatabase.LoadAssetAtPath<Shader>(sourcePath);
                if(!adapter.source)throw new InvalidOperationException("Исходный шейдер не найден.");
                IEnumerable textures=null;string shader;
                if(graph)
                {
                    var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("UnityEditor.ShaderGraph.ShaderGraphImporter")).FirstOrDefault(t=>t!=null);
                    if(type==null)throw new InvalidOperationException("ShaderGraphImporter не найден.");
                    var method=type.GetMethods(BindingFlags.Static|BindingFlags.NonPublic).SingleOrDefault(m=>
                        m.Name=="GetShaderText"&&m.GetParameters().Length==4&&m.GetParameters()[3].IsOut);
                    if(method==null)throw new InvalidOperationException("Версия API генератора Shader Graph не поддерживается.");
                    object[] args={sourcePath,null,null,null};
                    shader=(string)method.Invoke(null,args);textures=(IEnumerable)args[1];
                }
                else shader=ReadSource(sourcePath);
                if(string.IsNullOrEmpty(shader)||!shader.Contains("#include \""+Variables+"\""))throw new InvalidOperationException("Не удалось получить HDRP shader source.");
                var package=UnityEditor.PackageManager.PackageInfo.FindForAssetPath(Variables);
                if(package==null)throw new InvalidOperationException("Пакет HDRP не найден.");
                string variables=File.ReadAllText(Path.Combine(package.resolvedPath,Variables.Substring(package.assetPath.Length+1)));
                if(!variables.Contains(Instancing))throw new InvalidOperationException("Структура ShaderVariables изменилась.");
                variables=variables.Replace(Instancing,Instancing+"\n#include \""+Hook+"\"");
                shader=shader.Replace("#include \""+Variables+"\"",variables);
                if(shader.Contains("procedural:"))throw new InvalidOperationException("Шейдер уже использует procedural instancing.");
                if(!Regex.IsMatch(shader,@"#pragma\s+multi_compile_instancing"))throw new InvalidOperationException("Instancing passes не найдены.");
                shader=Regex.Replace(shader,@"(#pragma\s+multi_compile_instancing[^\r\n]*)","$1\n#pragma instancing_options procedural:LTSetupDetail");
                shader=Regex.Replace(shader,"Shader\\s+\"[^\"]+\"", "Shader \"Hidden/LocalTerrain/Indirect/"+definition.sourceGuid+"\"",RegexOptions.None,TimeSpan.FromSeconds(1));
                adapter.indirect=ShaderUtil.CreateShaderAsset(ctx,shader,false);
                if(!adapter.indirect)throw new InvalidOperationException("Indirect shader не создан.");
                CopyTextureDefaults(adapter.indirect,textures);
                ctx.AddObjectToAsset("IndirectShader",adapter.indirect);
                PreserveMaterialVariants(ctx,adapter);
            }
            catch(Exception ex)
            {
                adapter.error=(ex.InnerException??ex).Message;
                ctx.LogImportWarning("GPU details: "+adapter.error+" Используется CPU fallback.");
            }
        }
        static string ReadSource(string path)
        {
            if(!path.StartsWith("Packages/",StringComparison.Ordinal))return File.ReadAllText(path);
            var package=UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            if(package==null)throw new InvalidOperationException("Пакет исходного шейдера не найден.");
            return File.ReadAllText(Path.Combine(package.resolvedPath,path.Substring(package.assetPath.Length+1)));
        }
        static void PreserveMaterialVariants(AssetImportContext ctx,LTDetailGpuShader adapter)
        {
            // Runtime clones alone do not keep shader_feature variants in a player.
            // Import hidden material subassets for the source materials' keyword sets.
            var variants=new List<Material>();var keys=new HashSet<string>();
            var presenceTextures=new Dictionary<UnityEngine.Rendering.TextureDimension,Texture>();
            Texture Presence(Texture source)
            {
                if(!source)return null;
                if(presenceTextures.TryGetValue(source.dimension,out var result))return result;
                switch(source.dimension)
                {
                    case UnityEngine.Rendering.TextureDimension.Tex2D:
                        var t2=new Texture2D(1,1,TextureFormat.RGBA32,false);t2.Apply();result=t2;break;
                    case UnityEngine.Rendering.TextureDimension.Cube:
                        var cube=new Cubemap(1,TextureFormat.RGBA32,false);cube.Apply();result=cube;break;
                    case UnityEngine.Rendering.TextureDimension.Tex2DArray:
                        var array=new Texture2DArray(1,1,1,TextureFormat.RGBA32,false);array.Apply();result=array;break;
                    case UnityEngine.Rendering.TextureDimension.Tex3D:
                        var volume=new Texture3D(1,1,1,TextureFormat.RGBA32,false);volume.Apply();result=volume;break;
                    case UnityEngine.Rendering.TextureDimension.CubeArray:
                        var cubes=new CubemapArray(1,1,TextureFormat.RGBA32,false);cubes.Apply();result=cubes;break;
                    default:throw new InvalidOperationException("Unsupported retained texture dimension: "+source.dimension);
                }
                result.name="Variant texture presence "+source.dimension;result.hideFlags=HideFlags.HideInHierarchy;
                ctx.AddObjectToAsset("TexturePresence_"+source.dimension,result);presenceTextures.Add(source.dimension,result);
                return result;
            }
            var guids=AssetDatabase.FindAssets("t:Material",new[]{"Assets"});Array.Sort(guids,StringComparer.Ordinal);
            foreach(var guid in guids)
            {
                string path=AssetDatabase.GUIDToAssetPath(guid);
                if(!path.EndsWith(".mat",StringComparison.OrdinalIgnoreCase))continue;
                var source=AssetDatabase.LoadAssetAtPath<Material>(path);
                if(!source||source.shader!=adapter.source)continue;
                ctx.DependsOnSourceAsset(path);
                var keywords=source.shaderKeywords;Array.Sort(keywords,StringComparer.Ordinal);
                if(!keys.Add(string.Join(";",keywords)))continue;
                var material=new Material(adapter.indirect){name="Indirect variant "+variants.Count,hideFlags=HideFlags.HideInHierarchy};
                material.CopyPropertiesFromMaterial(source);material.enableInstancing=true;
                // These subassets retain keywords/render state, not an unrelated texture library.
                // In particular HDRP/Lit may be used by many non-detail materials in the project.
                // Preserve texture PRESENCE: HDRP validation derives _NORMALMAP/_MASKMAP
                // from non-null slots. Clearing slots would silently lose required variants.
                for(int p=0;p<adapter.indirect.GetPropertyCount();p++)
                    if(adapter.indirect.GetPropertyType(p)==UnityEngine.Rendering.ShaderPropertyType.Texture&&
                        (adapter.indirect.GetPropertyFlags(p)&UnityEngine.Rendering.ShaderPropertyFlags.NonModifiableTextureData)==0)
                    {
                        string property=adapter.indirect.GetPropertyName(p);
                        material.SetTexture(property,Presence(source.GetTexture(property)));
                    }
                material.EnableKeyword("PROCEDURAL_INSTANCING_ON");
                ctx.AddObjectToAsset("Variant_"+guid,material);variants.Add(material);
            }
            adapter.buildVariants=variants.ToArray();
        }
        static void CopyTextureDefaults(Shader shader,IEnumerable textures)
        {
            if(textures==null)return;
            var names=new List<string>();var values=new List<Texture>();
            var fixedNames=new List<string>();var fixedValues=new List<Texture>();
            foreach(var item in textures)
            {
                var type=item.GetType();
                object Field(string name)=>type.GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(item);
                string name=(string)Field("name");bool modifiable=(bool)Field("modifiable");
                // EntityId is new in Unity 6; reflection avoids converting it through obsolete integer IDs.
                object id=Field("textureId");
                var resolve=typeof(EditorUtility).GetMethods(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)
                    .First(m=>m.Name=="EntityIdToObject"&&m.GetParameters().Length==1);
                var texture=resolve.Invoke(null,new[]{id}) as Texture;
                (modifiable?names:fixedNames).Add(name);(modifiable?values:fixedValues).Add(texture);
            }
            var utility=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("UnityEditor.EditorMaterialUtility")??a.GetType("UnityEditor.Rendering.EditorMaterialUtility")).FirstOrDefault(t=>t!=null);
            if(utility==null)throw new InvalidOperationException("Не найден API для сохранения текстур графа.");
            void Set(string name,List<string> n,List<Texture> v)
            {
                if(n.Count==0)return;
                var setter=utility.GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
                if(setter==null)throw new InvalidOperationException(name+" не найден.");
                setter.Invoke(null,new object[]{shader,n.ToArray(),v.ToArray()});
            }
            Set("SetShaderDefaults",names,values);Set("SetShaderNonModifiableDefaults",fixedNames,fixedValues);
        }
    }

    // Include newly added materials too, even if the adapter was last imported
    // before they existed. No source material or scene is modified.
    public sealed class LTDetailGpuBuild : UnityEditor.Build.IPreprocessBuildWithReport
    {
        public int callbackOrder=>-1000;
        public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
        {
            foreach(var guid in AssetDatabase.FindAssets("t:LTDetailGpuShader"))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid);
                AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceUpdate|ImportAssetOptions.ForceSynchronousImport);
                var adapter=AssetDatabase.LoadAssetAtPath<LTDetailGpuShader>(path);
                if(adapter&&!string.IsNullOrEmpty(adapter.error))Debug.LogWarning("GPU details build fallback: "+adapter.error,adapter);
            }
        }
    }
}
