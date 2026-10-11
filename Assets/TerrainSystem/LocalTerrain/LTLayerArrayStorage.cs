using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Zero retains existing scenes and version-1 RGBA32 bakes.
    public enum LTLayerArrayStorage { RGBA32=0, BC7_BC5=1 }

    public static class LTLayerArrayStorageUtility
    {
        public static TextureFormat Format(LTLayerArrayStorage storage,int kind)
        {
            if(kind<0||kind>2)throw new ArgumentOutOfRangeException(nameof(kind));
            if(storage==LTLayerArrayStorage.RGBA32)return TextureFormat.RGBA32;
            if(storage==LTLayerArrayStorage.BC7_BC5)return kind==1?TextureFormat.BC5:TextureFormat.BC7;
            throw new ArgumentOutOfRangeException(nameof(storage));
        }
        public static long MipBytes(int width,int height,TextureFormat format)
        {
            if(width<1||height<1)throw new ArgumentOutOfRangeException(nameof(width));
            if(format==TextureFormat.RGBA32)return (long)width*height*4;
            // Even the 2x2 and 1x1 tail mips occupy one complete 16-byte block.
            if(format==TextureFormat.BC5||format==TextureFormat.BC7)return ((long)width+3)/4*((height+3L)/4)*16;
            throw new ArgumentOutOfRangeException(nameof(format));
        }
        public static long Bytes(int width,int height,int depth,int mips,TextureFormat format)
        {
            if(depth<1||mips<1)throw new ArgumentOutOfRangeException(nameof(depth));
            long sum=0;
            for(int mip=0;mip<mips;mip++)
            {sum+=MipBytes(width,height,format);width=Math.Max(1,width/2);height=Math.Max(1,height/2);}
            return checked(sum*depth);
        }
        public static long Bytes(Texture2DArray array)=>array?Bytes(array.width,array.height,array.depth,array.mipmapCount,array.format):0;
        public static long Estimate(int count,int colorSize,int normalSize,int maskSize,LTLayerArrayStorage storage)
        {
            long Sum(int size,int kind)
            {
                int mips=1;for(int s=size;s>1;s>>=1)mips++;
                return Bytes(size,size,Math.Max(1,count),mips,Format(storage,kind));
            }
            return Sum(colorSize,0)+Sum(normalSize,1)+Sum(maskSize,2);
        }
        // _LTFlags.x: 0 = absent, 1 = canonical RGB, 2 = BC5 RG. Existing buffers
        // already carry these flags into terrain, rocks, road blending and bakers.
        public static float NormalFlag(TextureFormat format,bool present)=>!present?0:format==TextureFormat.BC5?2:1;
    }
}
