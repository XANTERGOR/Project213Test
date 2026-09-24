using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    public enum LTArrayResolution { R256=256, R512=512, R1024=1024, R2048=2048, R4096=4096 }

    // One palette per world, NOT per chunk. GPU-only conversion, owned transient resources.
    // Rebuild transactionally on texture/palette/settings changes; scalar layer edits are free.
    public sealed class LTLayerTextureArrays : IDisposable
    {
        public Texture2DArray color { get; private set; }
        public Texture2DArray normal { get; private set; }
        public Texture2DArray mask { get; private set; }
        readonly Dictionary<LTSurfaceLayer,int> slices=new Dictionary<LTSurfaceLayer,int>();
        sealed class MipLease { public int originalLevel,users; }
        // All access is on Unity's main thread. Shared textures may belong to several worlds.
        static readonly Dictionary<Texture2D,MipLease> sharedMipRequests=new Dictionary<Texture2D,MipLease>();
        readonly HashSet<Texture2D> mipRequests=new HashSet<Texture2D>();
        string streamingSignature;
        string signature,failedSignature,readyStatus;
        LTLayerArrayBakeAsset borrowed;
        bool ownsArrays=true;
        public string Status { get; private set; }="Ещё не упаковано.";
        public long Bytes { get; private set; }
        public int Count=>slices.Count;
        public int Revision { get; private set; }
        public bool WaitingForSources=>mipRequests.Count>0;
        public static long EstimateBytes(int count,int colorSize,int normalSize,int maskSize)
        {
            long Mips(int size){long sum=0;for(;size>0;size>>=1)sum+=(long)size*size*4;return sum;}
            return Math.Max(1,count)*(Mips(colorSize)+Mips(normalSize)+Mips(maskSize));
        }
        static void Destroy(UnityEngine.Object value)
        {if(value){if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value);}}
        public void Dispose()
        {
            ReleaseMipRequests();
            if(ownsArrays){Destroy(color);Destroy(normal);Destroy(mask);}
            ownsArrays=true;borrowed=null;color=normal=mask=null;slices.Clear();signature=failedSignature=null;Bytes=0;
        }
        void ReleaseMipRequests()
        {
            foreach(var texture in mipRequests)
            {
                if(!sharedMipRequests.TryGetValue(texture,out var lease)||--lease.users>0)continue;
                sharedMipRequests.Remove(texture);
                if(texture&&texture.requestedMipmapLevel==0)
                {if(lease.originalLevel<0)texture.ClearRequestedMipmapLevel();else texture.requestedMipmapLevel=lease.originalLevel;}
            }
            mipRequests.Clear();streamingSignature=null;
        }
        bool SourcesReady(List<LTSurfaceLayer> palette,string next)
        {
            if(streamingSignature!=next){ReleaseMipRequests();streamingSignature=next;}
            bool ready=true;
            foreach(var layer in palette)foreach(var texture in new[]{layer.baseColorMap,layer.normalMap,layer.maskMap})
            {
                if(!texture||!texture.streamingMipmaps)continue;
                if(mipRequests.Add(texture))
                {
                    if(!sharedMipRequests.TryGetValue(texture,out var lease))
                    {lease=new MipLease{originalLevel=texture.requestedMipmapLevel};sharedMipRequests.Add(texture,lease);}
                    lease.users++;
                }
                texture.requestedMipmapLevel=0;
                ready&=texture.IsRequestedMipmapLevelLoaded();
            }
            return ready;
        }
        public bool Ensure(LTWorld world,List<LTSurfaceLayer> palette)
        {
            var saved=world.savedLayerArrays;
            string reason="";
            bool valid=false;
            try{valid=saved&&saved.Matches(world,palette,out reason);}
            catch(InvalidOperationException error){reason=error.Message;}
            if(valid&&world.arrayPackRevision==0)
            {
                if(borrowed!=saved||color!=saved.color||normal!=saved.normal||mask!=saved.mask)
                {
                    Dispose();borrowed=saved;ownsArrays=false;color=saved.color;normal=saved.normal;mask=saved.mask;
                    for(int i=0;i<palette.Count;i++)slices.Add(palette[i],i);
                    Bytes=EstimateBytes(palette.Count,color.width,normal.width,mask.width);Revision++;
                }
                Status=$"Загружено из asset: {saved.name}; {Count} срезов; GPU ≈ {Bytes/1048576f:F1} МиБ. Упаковка не выполнялась.";
                return true;
            }
            if(!ownsArrays)Dispose();
            bool result=Ensure(palette,(int)world.arrayColorResolution,(int)world.arrayNormalResolution,(int)world.arrayMaskResolution,
                world.arrayMemoryBudgetMiB,world.arrayPackRevision,world.arrayTrilinear,world.arrayAnisotropy);
            if(saved)Status+=" "+(valid?"Временный предпросмотр. Для сохранения нажмите «Запечь и сохранить».":reason);
            return result;
        }
        public bool Ensure(List<LTSurfaceLayer> palette,int c,int n,int m,int budgetMiB,int revision,bool trilinear,int anisotropy)
        {
            if(!ownsArrays)Dispose();
            // Validate before hashing or allocation: publishing must not fail on duplicate keys.
            var unique=new HashSet<LTSurfaceLayer>();
            if(palette==null||palette.Count==0)
            {ReleaseMipRequests();Status="Массивы не обновлены: пустая палитра.";return false;}
            foreach(var layer in palette)if(!layer||!unique.Add(layer))
            {ReleaseMipRequests();Status="Массивы не обновлены: пустой или повторяющийся слой в палитре.";return false;}
            var key=new StringBuilder().Append(c).Append('/').Append(n).Append('/').Append(m).Append('/')
                .Append(budgetMiB).Append('/').Append(revision).Append('/').Append(QualitySettings.activeColorSpace);
            void TextureKey(Texture2D texture)
            {if(!texture){key.Append("/null");return;}key.Append('/').Append(texture.GetInstanceID()).Append(':').Append(texture.imageContentsHash).Append(':').Append(texture.updateCount);}
            foreach(var layer in palette)
            {key.Append('|').Append(layer.GetInstanceID());TextureKey(layer.baseColorMap);TextureKey(layer.normalMap);TextureKey(layer.maskMap);}
            string next=key.ToString();
            if(signature==next&&color&&normal&&mask){ReleaseMipRequests();SetFiltering(trilinear,anisotropy);Status=readyStatus;return true;}
            if(failedSignature==next){ReleaseMipRequests();return false;}
            var timer=System.Diagnostics.Stopwatch.StartNew();
            Texture2DArray newColor=null,newNormal=null,newMask=null;
            bool waiting=false;
            string nativeFailure=null;var logLock=new object();
            void Capture(string message,string stack,LogType type)
            {
                if(type==LogType.Error||type==LogType.Assert||type==LogType.Exception||message.Contains("texture parameters"))
                    lock(logLock)nativeFailure=message;
            }
            try
            {
                var requiredCopy=CopyTextureSupport.DifferentTypes|CopyTextureSupport.RTToTexture;
                if(!SystemInfo.supports2DArrayTextures||(SystemInfo.copyTextureSupport&requiredCopy)!=requiredCopy)
                    throw new InvalidOperationException("GPU не поддерживает массивы/копирование текстур.");
                foreach(int size in new[]{c,n,m})
                    if(size<256||size>4096||(size&(size-1))!=0||size>SystemInfo.maxTextureSize)
                        throw new InvalidOperationException("Недопустимое разрешение массива: "+size);
                if(palette.Count==0||palette.Count>SystemInfo.maxTextureArraySlices)
                    throw new InvalidOperationException("Недопустимое число срезов: "+palette.Count);
                long bytes=EstimateBytes(palette.Count,c,n,m);
                // During a transactional rebuild both generations and one staging RT coexist.
                long staging=EstimateBytes(1,Math.Max(c,Math.Max(n,m)),1,1);
                if(bytes+Bytes+staging>(long)Math.Max(16,budgetMiB)*1024*1024)
                    throw new InvalidOperationException($"Пик упаковки ≈ {(bytes+Bytes+staging)/1048576f:F1} МиБ превышает бюджет {budgetMiB} МиБ. Уменьшите разрешение или увеличьте бюджет.");
                if(!SourcesReady(palette,next)){waiting=true;Status="Ожидание загрузки полных mip-уровней исходников (Texture Streaming).";return false;}
                Application.logMessageReceivedThreaded+=Capture;
                var shader=Resources.Load<Shader>("LTLayerArrayPack");
                if(!shader||!shader.isSupported)throw new InvalidOperationException("Шейдер упаковки недоступен.");
                var converter=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                try
                {
                    newColor=Build(palette,c,0,converter);
                    newNormal=Build(palette,n,1,converter);
                    newMask=Build(palette,m,2,converter);
                    lock(logLock)if(nativeFailure!=null)throw new InvalidOperationException(nativeFailure);
                }
                finally{Destroy(converter);}
                var oldColor=color;var oldNormal=normal;var oldMask=mask;
                color=newColor;normal=newNormal;mask=newMask;newColor=newNormal=newMask=null;
                slices.Clear();for(int i=0;i<palette.Count;i++)slices.Add(palette[i],i);
                signature=next;failedSignature=null;Bytes=bytes;Revision++;
                SetFiltering(trilinear,anisotropy);
                Destroy(oldColor);Destroy(oldNormal);Destroy(oldMask);
                Status=$"Готово: {Count} срезов × 3 массива; RGBA32 + mipmaps; GPU ≈ {Bytes/1048576f:F1} МиБ. Упаковка {timer.Elapsed.TotalMilliseconds:F1} мс (CPU, не GPU-таймер).";
                readyStatus=Status;
                return true;
            }
            catch(Exception error)
            {
                Destroy(newColor);Destroy(newNormal);Destroy(newMask);failedSignature=next;
                Status="Массивы не обновлены: "+error.Message;return false;
            }
            finally{Application.logMessageReceivedThreaded-=Capture;if(!waiting)ReleaseMipRequests();}
        }
        void SetFiltering(bool trilinear,int anisotropy)
        {
            foreach(var array in new[]{color,normal,mask})if(array)
            {array.filterMode=trilinear?FilterMode.Trilinear:FilterMode.Bilinear;array.anisoLevel=Mathf.Clamp(anisotropy,1,16);}
        }
        static Texture2DArray Build(List<LTSurfaceLayer> palette,int size,int kind,Material converter)
        {
            var array=new Texture2DArray(size,size,palette.Count,TextureFormat.RGBA32,true,kind!=0)
            {name="LT "+new[]{"Color sRGB","Normal RGB linear","Mask linear"}[kind],hideFlags=HideFlags.HideAndDontSave,wrapMode=TextureWrapMode.Repeat};
            // Release CPU backing BEFORE GPU copies. Never Apply afterwards (would overwrite copies).
            RenderTexture staging=null;
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite;
            try
            {
                array.Apply(false,true);
                staging=new RenderTexture(size,size,0,RenderTextureFormat.ARGB32,kind==0?RenderTextureReadWrite.sRGB:RenderTextureReadWrite.Linear)
                {hideFlags=HideFlags.HideAndDontSave,useMipMap=true,autoGenerateMips=false};
                if(!staging.Create())throw new InvalidOperationException("Не удалось выделить временную текстуру.");
                // Use precisely the same GPU format for CopyTexture on all graphics backends.
                if(array.graphicsFormat!=staging.graphicsFormat)throw new InvalidOperationException("Форматы массива и временной текстуры различаются.");
                converter.SetFloat("_PackKind",kind);
                for(int slice=0;slice<palette.Count;slice++)
                {
                    var layer=palette[slice];var input=kind==0?layer.baseColorMap:kind==1?layer.normalMap:layer.maskMap;
                    converter.SetFloat("_HasSource",input?1:0);
                    GL.sRGBWrite=kind==0&&QualitySettings.activeColorSpace==ColorSpace.Linear;
                    Graphics.Blit(input?input:Texture2D.whiteTexture,staging,converter,0);
                    staging.GenerateMips();
                    for(int mip=0;mip<array.mipmapCount;mip++)Graphics.CopyTexture(staging,0,mip,array,slice,mip);
                }
                return array;
            }
            catch{Destroy(array);throw;}
            finally{GL.sRGBWrite=srgb;RenderTexture.active=previous;if(staging)staging.Release();Destroy(staging);}
        }
        public void Bind(Material material,IList<LTSurfaceLayer> layers)
        {
            material.SetTexture("_LTColorArray",color);material.SetTexture("_LTNormalArray",normal);material.SetTexture("_LTMaskArray",mask);
            for(int group=0;group<3;group++)
            {
                var indices=Vector4.zero;
                for(int channel=0;channel<4;channel++)
                {int slot=group*4+channel;if(slot<layers.Count&&layers[slot]&&slices.TryGetValue(layers[slot],out int slice))indices[channel]=slice;}
                material.SetVector("_LTLayerSlices"+group,indices);
            }
        }
    }
}
