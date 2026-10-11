using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Main-thread staging only, one idle array of each type and a shared byte
    // limit. Jobs NEVER borrow these arrays: they synchronously copy to their
    // own NativeArrays before the caller disposes the input lease.
    public sealed class LTPaintWeightInputPool
    {
        public const int ByteLimit=16*1024*1024;
        public const int StampBytes=512; // conservative allowance, includes descriptor padding
        LTPaintWeightMath.Stamp[] stamps;
        LTPaintWeightMath.RoadHit[] hits;
        float[] masks;
        Vector2[] coordinates;
        long generation;
        public long RetainedBytes {get;private set;}
        public void Clear(){stamps=null;hits=null;masks=null;coordinates=null;RetainedBytes=0;generation++;}
        T[] Take<T>(ref T[] idle,int length,int stride,ref int created,ref int reused,ref int allocated)
        {
            if(length==0)return Array.Empty<T>();
            var array=idle;idle=null;if(array!=null)RetainedBytes-=(long)array.Length*stride;
            if(array!=null&&array.Length==length){reused++;return array;}
            created++;allocated+=length*stride;return new T[length];
        }
        void Keep<T>(ref T[] idle,T[] value,int stride)
        {
            if(value==null||value.Length==0)return;
            long bytes=(long)value.Length*stride;
            if(bytes>ByteLimit||RetainedBytes-(idle==null?0:(long)idle.Length*stride)+bytes>ByteLimit)return;
            if(idle!=null)RetainedBytes-=(long)idle.Length*stride;
            idle=value;RetainedBytes+=bytes;
        }
        public LTPaintWeightInput Rent(int stampCount,int hitCount,int maskCount,int coordinateCount,
            out int created,out int reused,out int allocated)
        {
            if(stampCount<0||stampCount>64||hitCount<0||maskCount<0||coordinateCount<0||
                (long)stampCount*StampBytes+(long)hitCount*16+(long)maskCount*4+(long)coordinateCount*8>ByteLimit)
                throw new ArgumentException("Paint input pool request exceeds its bounded layout.");
            created=reused=allocated=0;
            var input=new LTPaintWeightInput{owner=this,poolGeneration=generation};
            try
            {
                input.stamps=Take(ref stamps,stampCount,StampBytes,ref created,ref reused,ref allocated);
                input.hits=Take(ref hits,hitCount,16,ref created,ref reused,ref allocated);
                input.masks=Take(ref masks,maskCount,4,ref created,ref reused,ref allocated);
                input.coordinates=Take(ref coordinates,coordinateCount,8,ref created,ref reused,ref allocated);
                // Descriptors, hits and coordinates are completely overwritten by
                // preparation. Masks are sparse: uncovered texels must stay zero.
                Array.Clear(input.masks,0,input.masks.Length);
                return input;
            }
            catch{input.Dispose();throw;}
        }
        internal void Return(LTPaintWeightInput input)
        {
            if(!ReferenceEquals(input.owner,this))return;
            input.owner=null;
            if(input.poolGeneration==generation)
            {
                Keep(ref stamps,input.stamps,StampBytes);Keep(ref hits,input.hits,16);
                Keep(ref masks,input.masks,4);Keep(ref coordinates,input.coordinates,8);
            }
            input.stamps=null;input.hits=null;input.masks=null;input.coordinates=null;input.terrain=null;
        }
    }
}
