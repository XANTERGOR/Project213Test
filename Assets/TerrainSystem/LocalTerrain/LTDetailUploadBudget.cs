using System;

namespace LocalTerrainPrototype
{
    // Clock is supplied by the owner: game frame or editor update, shared by cameras.
    public sealed class LTDetailUploadBudget
    {
        int frame;bool started;double limitMs;long limitBytes;int limitAllocations;
        public double Milliseconds { get; private set; }
        public long Bytes { get; private set; }
        public int Allocations { get; private set; }
        public void Begin(int frameId,double milliseconds,long bytes,int allocations)
        {
            limitMs=Math.Max(.01,milliseconds);limitBytes=Math.Max(192,bytes);limitAllocations=Math.Max(1,allocations);
            if(started&&frame==frameId)return;
            started=true;frame=frameId;Milliseconds=0;Bytes=0;Allocations=0;
        }
        public bool CanAllocate=>Milliseconds<limitMs&&Allocations<limitAllocations;
        public int UploadCount(int remaining,int stride,int chunk)
            =>Milliseconds>=limitMs?0:(int)Math.Min(Math.Min(remaining,chunk),Math.Max(0,limitBytes-Bytes)/stride);
        public void Spend(double milliseconds,long bytes=0,int allocations=0)
        {Milliseconds+=Math.Max(0,milliseconds);Bytes+=Math.Max(0,bytes);Allocations+=Math.Max(0,allocations);}
    }
}
