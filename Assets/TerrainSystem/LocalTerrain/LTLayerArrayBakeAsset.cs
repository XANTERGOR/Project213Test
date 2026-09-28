using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Persistent arrays are borrowed, never destroyed or modified by the runtime owner.
    public sealed class LTLayerArrayBakeAsset : ScriptableObject
    {
        public const int CurrentVersion=1;
        public int version=CurrentVersion;
        public Texture2DArray color,normal,mask;
        public LTSurfaceLayer[] layers;
        public Texture2D[] sourceColors,sourceNormals,sourceMasks;
        public ColorSpace colorSpace;
        public string sourceSignature;
        public string colorDataHash,normalDataHash,maskDataHash;

        public static List<LTSurfaceLayer> Palette(LTWorld world)
        {
            var result=new List<LTSurfaceLayer>();
            if(world.baseLayer)result.Add(world.baseLayer);
            foreach(var stamp in world.CollectPaintStamps())
                stamp.AppendLayers(result,true);
            return result;
        }
        public bool Matches(LTWorld world,IList<LTSurfaceLayer> palette,out string reason)
        {
            reason="Нужна перепаковка: ";
            if(version!=CurrentVersion){reason+="версия формата.";return false;}
            if(!color||!normal||!mask){reason+="отсутствует один из массивов.";return false;}
            if(colorSpace!=QualitySettings.activeColorSpace){reason+="изменилось цветовое пространство.";return false;}
            if(palette==null||palette.Count==0||layers==null||layers.Length!=palette.Count||
                sourceColors==null||sourceColors.Length!=layers.Length||sourceNormals==null||sourceNormals.Length!=layers.Length||
                sourceMasks==null||sourceMasks.Length!=layers.Length)
            {reason+="состав палитры.";return false;}
            bool ArrayMatches(Texture2DArray array,int size,bool linear)
            {
                int mips=1;for(int s=size;s>1;s>>=1)mips++;
                return array.width==size&&array.height==size&&array.depth==layers.Length&&array.mipmapCount==mips&&
                    array.format==TextureFormat.RGBA32&&array.isDataSRGB==!linear&&array.wrapMode==TextureWrapMode.Repeat&&
                    array.filterMode==(world.arrayTrilinear?FilterMode.Trilinear:FilterMode.Bilinear)&&
                    array.anisoLevel==Mathf.Clamp(world.arrayAnisotropy,1,16);
            }
            if(!ArrayMatches(color,(int)world.arrayColorResolution,false)||!ArrayMatches(normal,(int)world.arrayNormalResolution,true)||
                !ArrayMatches(mask,(int)world.arrayMaskResolution,true))
            {reason+="разрешение, фильтрация или структура массивов.";return false;}
            for(int i=0;i<layers.Length;i++)
                if(!layers[i]||layers[i]!=palette[i]||sourceColors[i]!=layers[i].baseColorMap||
                    sourceNormals[i]!=layers[i].normalMap||sourceMasks[i]!=layers[i].maskMap)
                {reason+="слои или исходные текстуры.";return false;}
#if UNITY_EDITOR
            if(sourceSignature!=Signature(world,palette))
            {reason+="изменилось содержимое текстур или шейдер упаковки.";return false;}
#endif
            reason="Сохранённые массивы актуальны.";return true;
        }
#if UNITY_EDITOR
        // Stable across editor restarts; layer scalar settings deliberately excluded.
        public static string Signature(LTWorld world,IList<LTSurfaceLayer> palette)
        {
            var text=new System.Text.StringBuilder("LT layer arrays:").Append(CurrentVersion).Append(':')
                .Append(QualitySettings.activeColorSpace).Append(':').Append((int)world.arrayColorResolution).Append(':')
                .Append((int)world.arrayNormalResolution).Append(':').Append((int)world.arrayMaskResolution);
            void Reference(UnityEngine.Object value,bool contents)
            {
                if(!value){text.Append("/null");return;}
                if(!UnityEditor.AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value,out string guid,out long id))
                    throw new InvalidOperationException("Сначала сохраните исходный asset: "+value.name);
                text.Append('/').Append(guid).Append(':').Append(id);
                if(contents)
                {
                    text.Append(':').Append(UnityEditor.AssetDatabase.GetAssetDependencyHash(UnityEditor.AssetDatabase.GetAssetPath(value)));
                    if(value is Texture2D texture)text.Append(':').Append(texture.imageContentsHash);
                }
            }
            foreach(var layer in palette)
            {Reference(layer,false);Reference(layer.baseColorMap,true);Reference(layer.normalMap,true);Reference(layer.maskMap,true);}
            Reference(Resources.Load<Shader>("LTLayerArrayPack"),true);
            return Hash128.Compute(text.ToString()).ToString();
        }
#endif
    }
}
