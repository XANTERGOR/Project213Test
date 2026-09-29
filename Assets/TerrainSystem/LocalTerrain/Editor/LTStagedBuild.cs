using System;
using System.Collections.Generic;

namespace LocalTerrainPrototype
{
    // Cooperative editor work, NOT a worker thread. MoveNext executes one
    // algorithmic stage (not a hard time bound); Waiting yields to native Jobs.
    internal enum LTBuildStep { Working, LOD0Ready, Waiting }
    internal sealed class LTStagedBuild
    {
        IEnumerator<LTBuildStep> steps;
        long revision;
        readonly HashSet<int> affected=new HashSet<int>();
        public readonly HashSet<int> Dependencies=new HashSet<int>();
        Dictionary<int,long> inputVersions;
        Func<int,long> currentVersion;
        public bool Active=>steps!=null;
        public bool Lod0Ready {get;private set;}
        public bool Waiting {get;private set;}
        public int PendingCount=>Active?affected.Count:0;
        public void Invalidate(long version,ISet<int> dirty)
        {if(Active&&(version!=revision||InputsChanged()))Cancel(dirty);}
        bool InputsChanged()
        {
            if(currentVersion==null)return false;
            foreach(int id in affected)
                if(currentVersion(id)!=(inputVersions.TryGetValue(id,out long value)?value:0))return true;
            foreach(int id in Dependencies)
                if(currentVersion(id)!=(inputVersions.TryGetValue(id,out long value)?value:0))return true;
            return false;
        }
        public void Begin(long version,IEnumerable<int> dirty,Func<HashSet<int>,IEnumerator<LTBuildStep>> create,
            Dictionary<int,long> inputVersions=null,Func<int,long> currentVersion=null)
        {
            if(Active)throw new InvalidOperationException("A terrain build is already active.");
            affected.Clear();affected.UnionWith(dirty);revision=version;Lod0Ready=false;Waiting=false;
            Dependencies.Clear();
            this.inputVersions=inputVersions;this.currentVersion=currentVersion;
            steps=create(affected);
        }
        public bool Advance(long version,ISet<int> dirty)
        {
            if(!Active)return false;
            if(version!=revision||InputsChanged()){Cancel(dirty);return false;}
            if(steps.MoveNext())
            {
                Waiting=steps.Current==LTBuildStep.Waiting;
                bool published=steps.Current==LTBuildStep.LOD0Ready;
                Lod0Ready|=published;return published;
            }
            var completed=steps;steps=null;Waiting=false;completed.Dispose();affected.Clear();Dependencies.Clear();inputVersions=null;currentVersion=null;
            return false;
        }
        public void Cancel(ISet<int> dirty)
        {
            if(!Active)return;
            // Includes actual output neighbours discovered during planning, not
            // read-only dependencies. Published LOD0 transfers ownership to the
            // per-chunk queue and clears these IDs before its checkpoint.
            foreach(int id in affected)dirty.Add(id);
            var obsolete=steps;steps=null;
            try{obsolete.Dispose();}finally{affected.Clear();Dependencies.Clear();Lod0Ready=false;Waiting=false;inputVersions=null;currentVersion=null;}
        }
    }

    // One continuation per chunk. Input revisions are independent of publication:
    // normal-only neighbour writes must not cancel a valid geometry calculation.
    // This scheduler deliberately has no Unity dependency; it is still cooperative.
    internal sealed class LTChunkLODQueue
    {
        sealed class Work
        {
            public int id;public long version;
            public IEnumerator<LTBuildStep> steps;public Func<bool> valid;
        }
        readonly Dictionary<int,long> versions=new Dictionary<int,long>();
        readonly Dictionary<int,LinkedListNode<Work>> byId=new Dictionary<int,LinkedListNode<Work>>();
        readonly LinkedList<Work> ready=new LinkedList<Work>();
        public int Count=>byId.Count;
        public IEnumerable<int> PendingIds=>byId.Keys;
        public int Completed {get;private set;}
        public int Discarded {get;private set;}
        public bool Waiting {get;private set;}
        public bool Contains(int id)=>byId.ContainsKey(id);
        public long Version(int id)=>versions.TryGetValue(id,out long value)?value:0;
        public Dictionary<int,long> CaptureVersions()=>new Dictionary<int,long>(versions);
        public void Touch(int id)
        {
            versions[id]=Version(id)+1;
            if(byId.TryGetValue(id,out var node)){Remove(node);Discarded++;}
        }
        public void Enqueue(int id,long version,IEnumerator<LTBuildStep> steps,Func<bool> valid)
        {
            if(version!=Version(id)){steps.Dispose();Discarded++;return;}
            if(byId.TryGetValue(id,out var old))Remove(old);
            byId.Add(id,ready.AddLast(new Work{id=id,version=version,steps=steps,valid=valid}));
        }
        public bool Advance(ISet<int> dirty)
        {
            Waiting=false;
            var node=ready.First;if(node==null)return false;
            var work=node.Value;
            if(work.version!=Version(work.id)||(work.valid!=null&&!work.valid()))
            {Remove(node);dirty.Add(work.id);Discarded++;return false;}
            try
            {
                if(work.steps.MoveNext())
                {
                    Waiting=work.steps.Current==LTBuildStep.Waiting;
                    // Retain the active chunk at the head. Other iterators stay
                    // lazy: only ONE height cache / generated coarse set is live.
                    return false;
                }
                Remove(node);Completed++;return true;
            }
            catch {if(byId.ContainsKey(work.id))Remove(node);dirty.Add(work.id);throw;}
        }
        void Remove(LinkedListNode<Work> node)
        {byId.Remove(node.Value.id);ready.Remove(node);node.Value.steps.Dispose();}
        public void CancelAll(ISet<int> dirty)
        {
            while(ready.First!=null)
            {var node=ready.First;dirty.Add(node.Value.id);Remove(node);Discarded++;}
        }
    }
}
