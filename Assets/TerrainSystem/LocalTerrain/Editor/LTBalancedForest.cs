using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace LocalTerrainPrototype
{
    // 2:1 balance in XZ across the entire chunk topology cache. No height evaluation here.
    public sealed class LTBalancedForest
    {
        public const int N=1<<24;
        // Identity is a version: contents are copied once and never mutated.
        public sealed class PlanSnapshot
        {
            internal readonly List<Vector3Int> values;
            public PlanSnapshot(IEnumerable<Vector3Int> values){this.values=new List<Vector3Int>(values);}
            public int Count=>values.Count;
        }
        // Cache the closure of each raw plan in isolation, never the final
        // neighbour-refined plan. This allows border refinements to coarsen again.
        public sealed class InternalPlanCache
        {
            sealed class Entry
            {
                public HashSet<Vector3Int> source;
                public List<Vector3Int> balanced;
                public PlanSnapshot owner;
            }
            readonly Dictionary<int,Entry> entries=new Dictionary<int,Entry>();
            public int Reused {get;private set;}
            public int Rebuilt {get;private set;}
            public int ProcessedCells {get;private set;}
            public int IdentityHits {get;private set;}
            public int ComparedPlans {get;private set;}
            public IEnumerable<int> PrepareOwnedSteps(Dictionary<int,PlanSnapshot> raw,int budget,
                Dictionary<int,List<Vector3Int>> result,int workPerStep=1024)
            {
                foreach(int step in PrepareCoreSteps(raw.ToDictionary(p=>p.Key,p=>p.Value.values),budget,result,workPerStep,raw))yield return step;
            }
            public Dictionary<int,List<Vector3Int>> Prepare(Dictionary<int,List<Vector3Int>> raw,int budget,LTLODMesh.BuildProgress progress=null)
            {
                var result=new Dictionary<int,List<Vector3Int>>();
                progress=progress??new LTLODMesh.BuildProgress();
                foreach(int step in PrepareSteps(raw,budget,result))
                    progress.Report("Balancing local transitions; Cancel preserves old meshes.",.5f);
                return result;
            }
            // result stays private to the owning build until this iterator finishes.
            // Completed cache entries are pure and reusable; partial ones never enter it.
            public IEnumerable<int> PrepareSteps(Dictionary<int,List<Vector3Int>> raw,int budget,
                Dictionary<int,List<Vector3Int>> result,int workPerStep=1024)
                =>PrepareCoreSteps(raw,budget,result,workPerStep,null);
            IEnumerable<int> PrepareCoreSteps(Dictionary<int,List<Vector3Int>> raw,int budget,
                Dictionary<int,List<Vector3Int>> result,int workPerStep,Dictionary<int,PlanSnapshot> owners)
            {
                if(workPerStep<1)throw new ArgumentOutOfRangeException(nameof(workPerStep));
                Reused=Rebuilt=ProcessedCells=IdentityHits=ComparedPlans=0;result.Clear();
                foreach(int id in entries.Keys.Where(id=>!raw.ContainsKey(id)).ToArray())entries.Remove(id);
                foreach(var pair in raw)
                {
                    PlanSnapshot owner=null;owners?.TryGetValue(pair.Key,out owner);
                    bool hit=false;
                    if(entries.TryGetValue(pair.Key,out var cached))
                    {
                        if(owner!=null&&ReferenceEquals(owner,cached.owner)){IdentityHits++;hit=true;}
                        else {ComparedPlans++;hit=cached.source.Count==pair.Value.Count&&cached.source.SetEquals(pair.Value);}
                    }
                    if(hit)
                    {
                        if(cached.balanced.Count>budget)throw new InvalidOperationException("Topology budget exceeded during balancing. Previous meshes were preserved.");
                        cached.owner=owner;result[pair.Key]=cached.balanced;Reused++;yield return ProcessedCells;continue;
                    }
                    var local=new LTBalancedForest(1,1,budget,new Dictionary<int,List<Vector3Int>>{{0,pair.Value}});
                    foreach(int step in local.BalanceSteps(workPerStep))yield return ProcessedCells+step;
                    var entry=new Entry{source=new HashSet<Vector3Int>(pair.Value),balanced=local.Plan(0),owner=owner};
                    // Only publish a completed pure calculation; aborted scene
                    // rebuilds can safely retain it. Source lists are never aliased.
                    entries[pair.Key]=entry;result[pair.Key]=entry.balanced;
                    Rebuilt++;ProcessedCells+=local.ProcessedCells;
                    yield return ProcessedCells;
                }
            }
        }
        public sealed class Cell
        {
            public int chunk,x,z,w;public Vector3Int key;
        }
        sealed class ChunkData
        {
            public Dictionary<Vector3Int,Cell> cells=new Dictionary<Vector3Int,Cell>();
            public List<Vector3Int> sorted;
            public List<Cell> border;
        }
        readonly Dictionary<int,ChunkData> byChunk=new Dictionary<int,ChunkData>();
        readonly HashSet<int> owned=new HashSet<int>(),changed=new HashSet<int>(),refined=new HashSet<int>();
        readonly Dictionary<int,List<Vector3Int>> sources=new Dictionary<int,List<Vector3Int>>();
        readonly int columns,rows,budget;
        readonly object topologyIdentity=new object();
        int topologyRevision;
        readonly Queue<Cell> queue=new Queue<Cell>();
        bool balanced,frozen;
        TopologyCache proposalOwner;int proposalRevision;
        public int ProcessedCells {get;private set;}
        public int ReusedChunks=>byChunk.Count-owned.Count;
        public int ChangedChunks=>changed.Count;
        public bool MayChangeOutput(int chunk)=>changed.Contains(chunk)||Neighbours(chunk).Any(changed.Contains);
        // The accepted forest is never mutated by a proposal. Chunk dictionaries
        // are shared until first write; a failed/disposed proposal needs no rollback.
        public sealed class TopologyCache
        {
            LTBalancedForest accepted;int revision;
            // Inputs are the immutable completed outputs of InternalPlanCache.
            public LTBalancedForest Begin(int columns,int rows,int budget,Dictionary<int,List<Vector3Int>> internallyBalanced)
            {
                LTBalancedForest proposal;
                if(accepted==null||accepted.columns!=columns||accepted.rows!=rows||accepted.budget!=budget||
                    accepted.sources.Count!=internallyBalanced.Count||accepted.sources.Keys.Any(id=>!internallyBalanced.ContainsKey(id)))
                    proposal=new LTBalancedForest(columns,rows,budget,internallyBalanced,new HashSet<int>(internallyBalanced.Keys));
                else proposal=new LTBalancedForest(accepted,internallyBalanced);
                proposal.proposalOwner=this;proposal.proposalRevision=revision;return proposal;
            }
            public void Commit(LTBalancedForest proposal)
            {
                if(proposal.proposalOwner!=this||proposal.proposalRevision!=revision)throw new InvalidOperationException("Stale terrain topology proposal.");
                if(!proposal.balanced||proposal.queue.Count!=0)throw new InvalidOperationException("Cannot accept unfinished terrain topology.");
                proposal.frozen=true;proposal.proposalOwner=null;accepted=proposal;revision++;
            }
            public void Clear(){accepted=null;revision++;}
        }
        IEnumerable<int> Neighbours(int id)
        {
            if(id%columns>0&&byChunk.ContainsKey(id-1))yield return id-1;
            if(id%columns+1<columns&&byChunk.ContainsKey(id+1))yield return id+1;
            if(id>=columns&&byChunk.ContainsKey(id-columns))yield return id-columns;
            if(id+columns<columns*rows&&byChunk.ContainsKey(id+columns))yield return id+columns;
        }
        List<Cell> Border(int id)
        {
            var data=byChunk[id];
            return data.border??(data.border=data.cells.Values.Where(c=>c.x==0||c.z==0||c.x+c.w==N||c.z+c.w==N).ToList());
        }
        LTBalancedForest(LTBalancedForest previous,Dictionary<int,List<Vector3Int>> plans)
        {
            columns=previous.columns;rows=previous.rows;budget=previous.budget;
            foreach(var pair in previous.byChunk)byChunk.Add(pair.Key,pair.Value);
            foreach(var pair in plans)sources.Add(pair.Key,pair.Value);
            refined.UnionWith(previous.refined);
            var reset=new HashSet<int>(plans.Keys.Where(id=>!ReferenceEquals(plans[id],previous.sources[id])));
            // Reset the connected old cross-refinement closure to INTERNAL plans.
            // This is essential for coarsening: retaining final refined neighbours
            // would permanently pin detail left behind by a moved/deleted stamp.
            var visit=new Queue<int>(reset);
            while(visit.Count>0)foreach(int id in Neighbours(visit.Dequeue()))
                if(previous.refined.Contains(id)&&reset.Add(id))visit.Enqueue(id);
            foreach(int id in reset)
            {
                byChunk[id]=new ChunkData();owned.Add(id);changed.Add(id);refined.Remove(id);
                foreach(var p in plans[id])Add(id,p.x,p.y,p.z,p.x==0||p.y==0||p.x+p.z==N||p.y+p.z==N);
            }
            var readers=new HashSet<int>();foreach(int id in reset)readers.UnionWith(Neighbours(id));
            foreach(int id in readers)if(!reset.Contains(id))foreach(var c in Border(id))queue.Enqueue(c);
            balanced=queue.Count==0;
        }
        ChunkData Writable(int id)
        {
            if(frozen)throw new InvalidOperationException("Accepted topology is immutable.");
            if(owned.Add(id))byChunk[id]=new ChunkData{cells=new Dictionary<Vector3Int,Cell>(byChunk[id].cells)};
            var data=byChunk[id];data.sorted=null;data.border=null;changed.Add(id);return data;
        }
        bool TryCell(Vector3Int key,out Cell cell)
        {
            cell=null;
            if(key.x<0||key.y<0||key.x>=columns*N||key.y>=rows*N)return false;
            return byChunk.TryGetValue(key.x/N+key.y/N*columns,out var data)&&data.cells.TryGetValue(key,out cell);
        }
        // Trusted chunks must already be internally 2:1 balanced. Their borders
        // remain queued; any new split queues its children and propagates inward.
        public LTBalancedForest(int columns,int rows,int budget,Dictionary<int,List<Vector3Int>> plans,ISet<int> internallyBalancedChunks=null)
        {
            this.columns=columns;this.rows=rows;this.budget=budget;
            foreach(var pair in plans)
            {
                if(pair.Key<0||pair.Key>=(long)columns*rows)throw new ArgumentException("Terrain chunk is outside the grid.");
                byChunk[pair.Key]=new ChunkData();owned.Add(pair.Key);sources[pair.Key]=pair.Value;
                bool trusted=internallyBalancedChunks!=null&&internallyBalancedChunks.Contains(pair.Key);
                foreach(var p in pair.Value)Add(pair.Key,p.x,p.y,p.z,
                    !trusted||p.x==0||p.y==0||p.x+p.z==N||p.y+p.z==N);
            }
        }
        void Add(int chunk,int x,int z,int w,bool enqueue=true)
        {
            topologyRevision++;
            var key=new Vector3Int(chunk%columns*N+x,chunk/columns*N+z,w);
            var cell=new Cell{chunk=chunk,x=x,z=z,w=w,key=key};var data=Writable(chunk);data.cells.Add(key,cell);if(enqueue)queue.Enqueue(cell);
            if(data.cells.Count>budget)throw new InvalidOperationException("Topology budget exceeded during balancing. Previous meshes were preserved.");
        }
        Cell Find(int x,int z)
        {
            if(x<0||z<0||x>=columns*N||z>=rows*N)return null;
            int cx=x/N,cz=z/N,lx=x-cx*N,lz=z-cz*N;
            if(!byChunk.TryGetValue(cx+cz*columns,out var data))return null; // Sparse grid boundary.
            for(int w=N;w>=2;w/=2)
            {
                var key=new Vector3Int(cx*N+lx/w*w,cz*N+lz/w*w,w);
                if(data.cells.TryGetValue(key,out var c))return c;
            }
            throw new InvalidOperationException("Missing terrain topology cell.");
        }
        Cell Neighbour(Cell c,int side)
        {
            int x=c.key.x,z=c.key.y,w=c.w;
            if(side==0)return Find(x-1,z+w/2);
            if(side==1)return Find(x+w,z+w/2);
            if(side==2)return Find(x+w/2,z-1);
            return Find(x+w/2,z+w);
        }
        void Split(Cell c)
        {
            Writable(c.chunk).cells.Remove(c.key);refined.Add(c.chunk);int h=c.w/2;
            Add(c.chunk,c.x,c.z,h);Add(c.chunk,c.x+h,c.z,h);Add(c.chunk,c.x,c.z+h,h);Add(c.chunk,c.x+h,c.z+h,h);
        }
        public void Balance(LTLODMesh.BuildProgress progress=null)
        {
            progress=progress??new LTLODMesh.BuildProgress();
            foreach(int step in BalanceSteps())
                progress.Report("Balancing local transitions; Cancel preserves old meshes.",.5f);
        }
        // Counts queue visits, including obsolete entries. A disposed iterator has
        // no global effects: only its privately owned forest has partial topology.
        public IEnumerable<int> BalanceSteps(int workPerStep=1024)
        {
            if(workPerStep<1)throw new ArgumentOutOfRangeException(nameof(workPerStep));
            int visited=0;
            while(queue.Count>0)
            {
                if(visited==workPerStep){visited=0;yield return ProcessedCells;}
                visited++;
                var c=queue.Dequeue();if(!TryCell(c.key,out var live)||!ReferenceEquals(c,live))continue;
                ProcessedCells++;
                for(int side=0;side<4;side++)
                {
                    var neighbour=Neighbour(c,side);
                    if(neighbour!=null&&neighbour.w>2*c.w){Split(neighbour);queue.Enqueue(c);}
                }
            }
            if(visited>0)yield return ProcessedCells;
            balanced=true;
        }
        public List<Vector3Int> Plan(int chunk)
        {
            var data=byChunk[chunk];
            if(data.sorted==null)data.sorted=data.cells.Values.Select(c=>new Vector3Int(c.x,c.z,c.w)).OrderBy(p=>p.y).ThenBy(p=>p.x).ThenBy(p=>p.z).ToList();
            return new List<Vector3Int>(data.sorted);
        }
        public bool Midpoint(int chunk,Vector3Int p,int side)
        {
            var key=new Vector3Int(chunk%columns*N+p.x,chunk/columns*N+p.y,p.z);
            if(!TryCell(key,out var c))throw new InvalidOperationException("Missing terrain topology cell.");
            if(balanced)return BalancedMidpoint(c,side);
            var neighbour=Neighbour(c,side);return neighbour!=null&&neighbour.w<c.w;
        }
        bool BalancedMidpoint(Cell c,int side)
        {
            // Once 2:1 is established, a finer neighbour can ONLY have size w/2.
            // Do not use this during balancing, when larger ratios still exist.
            int h=c.w/2;if(h<2)return false;
            int x=side==0?c.key.x-1:side==1?c.key.x+c.w:c.key.x+h;
            int z=side==2?c.key.y-1:side==3?c.key.y+c.w:c.key.y+h;
            if(x<0||z<0||x>=columns*N||z>=rows*N)return false;
            return TryCell(new Vector3Int(x/h*h,z/h*h,h),out _);
        }
        // One immutable, build-owned snapshot shared by height prefetch and Emit.
        // No global cache, aliased plan, or reference retaining the entire forest.
        public sealed class StitchSnapshot
        {
            readonly object identity;readonly int revision,chunk;
            readonly Vector3Int[] leaves;readonly byte[] masks;
            internal StitchSnapshot(object identity,int revision,int chunk,Vector3Int[] leaves,byte[] masks)
            {this.identity=identity;this.revision=revision;this.chunk=chunk;this.leaves=leaves;this.masks=masks;}
            public int Count=>leaves.Length;
            public Vector3Int Cell(int index)=>leaves[index];
            public int Mask(int index)=>masks[index];
            public Dictionary<Vector3Int,int> MaskLookup()
            {var result=new Dictionary<Vector3Int,int>(Count);for(int i=0;i<Count;i++)result.Add(leaves[i],masks[i]);return result;}
            public void Validate(LTBalancedForest forest,int chunk,List<Vector3Int> plan)
            {
                if(forest==null||!ReferenceEquals(identity,forest.topologyIdentity)||revision!=forest.topologyRevision||this.chunk!=chunk||plan.Count!=Count)
                    throw new InvalidOperationException("Stitch snapshot belongs to a different topology build or chunk.");
                for(int i=0;i<Count;i++)if(plan[i]!=leaves[i])
                    throw new InvalidOperationException("Stitch snapshot does not match the ordered emission plan.");
            }
        }
        // Call only after Balance completes. Abandoning this iterator publishes
        // nothing; the callback receives a snapshot only after every mask is ready.
        public IEnumerable<int> StitchSnapshotSteps(int chunk,List<Vector3Int> plan,Action<StitchSnapshot> completed,int workPerStep=512)
        {
            if(workPerStep<1)throw new ArgumentOutOfRangeException(nameof(workPerStep));
            if(completed==null)throw new ArgumentNullException(nameof(completed));
            if(!balanced||queue.Count!=0)throw new InvalidOperationException("Balance must finish before preparing stitch flags.");
            int revision=topologyRevision;var leaves=plan.ToArray();var masks=new byte[leaves.Length];
            for(int i=0;i<leaves.Length;i++)
            {
                if(revision!=topologyRevision)throw new InvalidOperationException("Topology changed while preparing stitch flags.");
                var p=leaves[i];var key=new Vector3Int(chunk%columns*N+p.x,chunk/columns*N+p.y,p.z);
                if(!TryCell(key,out var c))throw new InvalidOperationException("Missing terrain topology cell.");int mask=0;
                for(int side=0;side<4;side++)if(BalancedMidpoint(c,side))mask|=1<<side;
                masks[i]=(byte)mask;
                if((i+1)%workPerStep==0&&i+1<leaves.Length)yield return i+1;
            }
            completed(new StitchSnapshot(topologyIdentity,revision,chunk,leaves,masks));
        }
        public List<int> BoundaryStitches(int chunk,List<Vector3Int> plan)
        {
            var result=new List<int>();
            foreach(var p in plan)
            {
                if(p.x!=0&&p.y!=0&&p.x+p.z!=N&&p.y+p.z!=N)continue;
                int bits=0;
                if(p.x==0&&Midpoint(chunk,p,0))bits|=1;
                if(p.x+p.z==N&&Midpoint(chunk,p,1))bits|=2;
                if(p.y==0&&Midpoint(chunk,p,2))bits|=4;
                if(p.y+p.z==N&&Midpoint(chunk,p,3))bits|=8;
                result.Add(bits);
            }
            return result;
        }
        public void Validate()
        {
            foreach(var data in byChunk.Values)foreach(var c in data.cells.Values)for(int side=0;side<4;side++)
            {var n=Neighbour(c,side);if(n!=null&&(n.w>2*c.w||c.w>2*n.w))throw new InvalidOperationException("Unbalanced terrain edge.");}
        }
    }
}
