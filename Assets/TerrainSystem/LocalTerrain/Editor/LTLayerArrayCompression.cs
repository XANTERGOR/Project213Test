using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    public static partial class LTLayerArraySaving
    {
        static void RequireCompressionSupport()
        {
            if(!SystemInfo.supports2DArrayTextures||!SystemInfo.supportsAsyncGPUReadback||
                !SystemInfo.SupportsTextureFormat(TextureFormat.BC7)||!SystemInfo.SupportsTextureFormat(TextureFormat.BC5))
                throw new InvalidOperationException("BC7/BC5 arrays или GPU readback недоступны. Старый комплект не изменён; выберите RGBA32.");
        }
        // Compress a single owned slice at a time, keeping the GPU-built mip chain.
        // Never reimport or change the source textures, nor Apply on the source array.
        public static Texture2DArray ReadCompressedForSaving(Texture2DArray source,string label,TextureFormat format,bool showProgress=true)
        {
            RequireCompressionSupport();
            if(!source||source.format!=TextureFormat.RGBA32||(format!=TextureFormat.BC7&&format!=TextureFormat.BC5)||
                (format==TextureFormat.BC5&&source.isDataSRGB))
                throw new InvalidOperationException("Нужен RGBA32-источник и BC7 либо linear BC5.");
            var result=new Texture2DArray(source.width,source.height,source.depth,format,source.mipmapCount,!source.isDataSRGB)
            {name=label,wrapMode=source.wrapMode,filterMode=source.filterMode,anisoLevel=source.anisoLevel};
            try
            {
                for(int slice=0;slice<source.depth;slice++)
                {
                    var staging=new Texture2D(source.width,source.height,TextureFormat.RGBA32,source.mipmapCount,!source.isDataSRGB)
                    {name="Owned compression slice",hideFlags=HideFlags.HideAndDontSave};
                    try
                    {
                        for(int mip=0;mip<source.mipmapCount;mip++)
                        {
                            if(showProgress&&EditorUtility.DisplayCancelableProgressBar("Сжатие массивов",
                                $"{label}: срез {slice+1}/{source.depth}, mip {mip}",(slice+(float)mip/source.mipmapCount)/source.depth))
                                throw new OperationCanceledException();
                            int width=Math.Max(1,source.width>>mip),height=Math.Max(1,source.height>>mip);
                            var read=AsyncGPUReadback.Request(source,mip,0,width,0,height,slice,1);read.WaitForCompletion();
                            if(read.hasError||read.layerCount!=1)throw new InvalidOperationException($"GPU readback: {label}, slice {slice}, mip {mip}.");
                            var bytes=read.GetData<byte>();
                            if(bytes.Length!=LTLayerArrayStorageUtility.MipBytes(width,height,TextureFormat.RGBA32))
                                throw new InvalidOperationException("Неполный RGBA32 mip при сжатии.");
                            staging.SetPixelData(bytes,mip);
                        }
                        staging.Apply(false,false);
                        EditorUtility.CompressTexture(staging,format,TextureCompressionQuality.Best);
                        // CompressTexture may leave the texture unchanged on failure.
                        if(staging.format!=format||staging.mipmapCount!=source.mipmapCount||staging.isDataSRGB!=source.isDataSRGB)
                            throw new InvalidOperationException("Компрессор не сохранил требуемый формат, mips или цветовое пространство.");
                        for(int mip=0;mip<source.mipmapCount;mip++)
                        {
                            var bytes=staging.GetPixelData<byte>(mip);
                            if(bytes.Length!=LTLayerArrayStorageUtility.MipBytes(Math.Max(1,source.width>>mip),Math.Max(1,source.height>>mip),format))
                                throw new InvalidOperationException("Неполный блок compressed mip.");
                            result.SetPixelData(bytes,mip,slice);
                        }
                    }
                    finally{UnityEngine.Object.DestroyImmediate(staging);}
                }
                result.Apply(false,false); // Retain compressed CPU bytes for serialization/hash verification.
                return result;
            }
            catch{UnityEngine.Object.DestroyImmediate(result);throw;}
            finally{if(showProgress)EditorUtility.ClearProgressBar();}
        }
    }
}
