using System.Collections.Generic;

namespace LocalTerrainPrototype
{
    // Exact live-poll comparison, not a hash and not an event-invalidated cache.
    // The caller validates coordinates before Add. Failed polls never Accept.
    internal sealed class LTChunkLayoutProbe
    {
        Dictionary<int,int> accepted=new Dictionary<int,int>(),observed=new Dictionary<int,int>();
        (int x,int z,int columns,int rows) previous,current;
        bool valid;
        public void Begin(int x,int z,int columns,int rows)
        {current=(x,z,columns,rows);observed.Clear();}
        public bool Add(int slot,int instance)=>observed.TryAdd(slot,instance);
        public IEnumerable<int> ObservedIds=>observed.Keys;
        public bool Unchanged
        {
            get
            {
                if(!valid||current!=previous||observed.Count!=accepted.Count)return false;
                foreach(var item in observed)
                    if(!accepted.TryGetValue(item.Key,out int instance)||instance!=item.Value)return false;
                return true;
            }
        }
        public void Accept()
        {
            var scratch=accepted;accepted=observed;observed=scratch;
            previous=current;valid=true;
        }
    }
}
