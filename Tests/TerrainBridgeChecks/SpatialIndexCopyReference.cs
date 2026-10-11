using System;
using System.Collections.Generic;

namespace LocalTerrainPrototype
{
    // Shared by ALL chunks: a chunk edge is not a permanently pinned LOD0 edge.
    // Only active cell IDs and index lists change. Dependency tracking allows previously
    // forced refinements to disappear again when the camera moves away.
    public sealed class SpatialIndexCopyReference
    {
        public sealed class Chunk { public LTSpatialLODCell[] cells; public LTSpatialLODPatch[] patches; }
        sealed class Patch
        {
            public int chunk,local,requested;
            public readonly HashSet<int> active=new HashSet<int>(),dependsOn=new HashSet<int>(),dependents=new HashSet<int>();
            public List<int> indices;
            public bool indicesValid;
            // 16 invalidates every mask; bits 0..3 only the corresponding rim.
            // Like indicesValid, this survives idle Updates and deferred reads.
            public int maskDirty=16;
            public int queuedSides;
        }
        readonly struct Cell
        {
            public readonly int chunk,id;
            public Cell(int chunk,int id){this.chunk=chunk;this.id=id;}
        }
        const int N=LTSpatialLODMath.N;
        readonly int columns,rows,divisions,patchColumns,patchSize;
        readonly Chunk[] chunks;
        // Keep the cached four-bit stitch mask in the same byte as membership.
        // This replaces bool[] storage, not a second per-cell cache allocation.
        const byte ActiveFlag=16;
        readonly byte[][] active;
        // Immutable parent links let nearby queries start at their common ancestor.
        // Four bytes per cell, bounded per topology; uncached chunks use root walks.
        readonly int[][] parents;
        readonly Patch[] patches;
        readonly int indexCacheBudget;
        int indexCacheCapacity;
        readonly HashSet<int> changed=new HashSet<int>(),invalidated=new HashSet<int>(),queuedPatches=new HashSet<int>();
        readonly Queue<int> patchQueue=new Queue<int>();
        readonly Queue<Cell> queue=new Queue<Cell>();
        public readonly HashSet<int> DirtyChunks=new HashSet<int>();
        public int ProcessedCells {get;private set;}
        public int ResetPatches {get;private set;}
        public int IndexCacheHits {get;private set;}
        public int IndexCacheBuilds {get;private set;}
        public int IndexCacheCells {get;private set;}
        public int IndexCacheFallbacks {get;private set;}
        public int IndexMaskQueries {get;private set;}
        // Retained index payload only; List/Patch descriptors are additional.
        public long IndexCacheBytes=>(long)indexCacheCapacity*sizeof(int);
        public long NeighbourCacheBytes {get;private set;}
        public SpatialIndexCopyReference(int columns,int rows,int divisions,Chunk[] chunks,int indexCacheBudgetBytes=64*1024*1024,int neighbourCacheBudgetBytes=16*1024*1024)
        {
            if(columns<1||rows<1||chunks==null||chunks.Length!=columns*rows)throw new ArgumentException("Invalid LOD world layout.");
            this.columns=columns;this.rows=rows;this.divisions=LTSpatialLODMath.Divisions(divisions);this.chunks=chunks;
            indexCacheBudget=Math.Max(0,indexCacheBudgetBytes)/sizeof(int);
            patchColumns=columns*this.divisions;patchSize=N/this.divisions;
            patches=new Patch[columns*rows*this.divisions*this.divisions];active=new byte[chunks.Length][];
            parents=new int[chunks.Length][];
            for(int c=0;c<chunks.Length;c++)
            {
                if(chunks[c]==null)continue; // Intentionally absent terrain: an exposed boundary.
                if(chunks[c].cells==null||chunks[c].patches==null||chunks[c].patches.Length!=this.divisions*this.divisions)
                    throw new ArgumentException("Rebake all spatial LOD chunks before enabling preview.");
                active[c]=new byte[chunks[c].cells.Length];
                long bytes=(long)chunks[c].cells.Length*sizeof(int);
                if(bytes<=Math.Max(0,neighbourCacheBudgetBytes)-NeighbourCacheBytes)
                {
                    var cells=chunks[c].cells;var links=new int[cells.Length];
                    for(int id=0;id<cells.Length;id++)
                        if(cells[id].child0>=0)for(int side=0;side<4;side++)links[cells[id].Child(side)]=id;
                    foreach(var patch in chunks[c].patches)links[patch.root]=-1;
                    parents[c]=links;NeighbourCacheBytes+=bytes;
                }
                for(int p=0;p<chunks[c].patches.Length;p++)
                {int id=PatchId(c,p);patches[id]=new Patch{chunk=c,local=p};changed.Add(id);}
            }
        }
        int PatchId(int c,int p)=>(c/columns*divisions+p/divisions)*patchColumns+c%columns*divisions+p%divisions;
        int PatchOf(Cell c)=>PatchId(c.chunk,chunks[c.chunk].cells[c.id].patch);
        public int Requested(int chunk,int patch)=>patches[PatchId(chunk,patch)].requested;
        public void SetLevel(int chunk,int patch,int level)
        {
            int id=PatchId(chunk,patch);var p=patches[id];
            level=Math.Max(0,Math.Min(level,chunks[chunk].patches[patch].levels.Length-1));
            if(p.requested==level)return;p.requested=level;changed.Add(id);
        }
        int Adjacent(int p,int side)
        {
            int x=p%patchColumns,z=p/patchColumns;
            if(side==0)x--;else if(side==1)x++;else if(side==2)z--;else z++;
            if(x<0||z<0||x>=patchColumns||z>=rows*divisions)return -1;
            int id=z*patchColumns+x;return patches[id]==null?-1:id;
        }
        void Dirty(int p)
        {
            // A changed active partition can change stitch masks on either side
            // of its four borders. Invalidation survives Update until actually read.
            patches[p].indicesValid=false;
            patches[p].maskDirty=16;
            DirtyChunks.Add(patches[p].chunk);
            for(int s=0;s<4;s++)
            {
                int n=Adjacent(p,s);if(n<0)continue;
                patches[n].indicesValid=false;patches[n].maskDirty|=1<<(s^1);DirtyChunks.Add(patches[n].chunk);
            }
        }
        Cell Find(int x,int z)
        {
            if(x<0||z<0||x>=columns*N||z>=rows*N)return new Cell(-1,-1);
            int cx=x/N,cz=z/N,c=cz*columns+cx; x-=cx*N;z-=cz*N;
            if(chunks[c]==null)return new Cell(-1,-1);
            int id=chunks[c].patches[z/patchSize*divisions+x/patchSize].root;
            return Descend(c,id,x,z);
        }
        Cell Descend(int c,int id,int x,int z)
        {
            var membership=active[c];var cells=chunks[c].cells;
            while((membership[id]&ActiveFlag)==0)
            {
                var node=cells[id];int h=node.size/2;
                id=node.Child((x>=node.x+h?1:0)+(z>=node.z+h?2:0));
                if(id<0)throw new InvalidOperationException("Incomplete active LOD partition.");
            }
            return new Cell(c,id);
        }
        Cell Neighbour(Cell c,int side)
        {
            var cells=chunks[c.chunk].cells;var node=cells[c.id];
            int x=side==0?node.x-1:side==1?node.x+node.size:node.x+node.size/2;
            int z=side==2?node.z-1:side==3?node.z+node.size:node.z+node.size/2;
            var links=parents[c.chunk];
            if(links!=null)
            {
                // Ancestors of an active cell cannot themselves be active. The
                // first containing the query is a safe root for its neighbour.
                int parent=links[c.id];
                while(parent>=0)
                {
                    var ancestor=cells[parent];
                    if(x>=ancestor.x&&x<ancestor.x+ancestor.size&&z>=ancestor.z&&z<ancestor.z+ancestor.size)
                        return Descend(c.chunk,parent,x,z);
                    parent=links[parent];
                }
            }
            return Find(c.chunk%columns*N+x,c.chunk/columns*N+z);
        }
        void Split(Cell cell,Cell cause)
        {
            var node=chunks[cell.chunk].cells[cell.id];
            if(node.child0<0)throw new InvalidOperationException("Spatial LOD requires finer cells than LOD0. Rebuild neighbours together.");
            int p=PatchOf(cell),from=PatchOf(cause);var patch=patches[p];
            if(p!=from){patch.dependsOn.Add(from);patches[from].dependents.Add(p);}
            active[cell.chunk][cell.id]&=15;patch.active.Remove(cell.id);
            for(int i=0;i<4;i++){int child=node.Child(i);active[cell.chunk][child]|=ActiveFlag;patch.active.Add(child);queue.Enqueue(new Cell(cell.chunk,child));}
            Dirty(p);
        }
        public bool Update()
        {
            DirtyChunks.Clear();ProcessedCells=ResetPatches=0;
            IndexCacheHits=IndexCacheBuilds=IndexCacheCells=IndexCacheFallbacks=IndexMaskQueries=0;
            if(changed.Count==0)return false;
            foreach(int p in queuedPatches)patches[p].queuedSides=0;
            invalidated.Clear();patchQueue.Clear();queue.Clear();queuedPatches.Clear();
            foreach(int p in changed){invalidated.Add(p);patchQueue.Enqueue(p);}
            while(patchQueue.Count>0)
            {
                int p=patchQueue.Dequeue();
                foreach(int dependent in patches[p].dependents)
                    if(invalidated.Add(dependent))patchQueue.Enqueue(dependent);
            }
            foreach(int p in invalidated)
            {
                var patch=patches[p];
                foreach(int dependency in patch.dependsOn)patches[dependency].dependents.Remove(p);
                patch.dependsOn.Clear();
                foreach(int id in patch.active)active[patch.chunk][id]&=15;
                patch.active.Clear();
                foreach(int id in chunks[patch.chunk].patches[patch.local].levels[patch.requested].cells)
                {active[patch.chunk][id]|=ActiveFlag;patch.active.Add(id);}
                Dirty(p);QueuePatch(p,16); // Whole reset partition, not only its rim.
                for(int s=0;s<4;s++){int n=Adjacent(p,s);if(n>=0)QueuePatch(n,1<<(s^1));}
            }
            ResetPatches=invalidated.Count;
            // An unchanged neighbour can only acquire a violation at the shared
            // border. Visit EVERY leaf on that border (not just one midpoint).
            // Split still queues all children and the cause, propagating further
            // refinements inwards exactly as before. Reset patches remain full scans.
            foreach(int p in queuedPatches)
            {
                var patch=patches[p];
                foreach(int id in patch.active)
                {
                    var node=chunks[patch.chunk].cells[id];int sides=patch.queuedSides;
                    if((sides&16)!=0||((sides&1)!=0&&node.x%patchSize==0)||
                        ((sides&2)!=0&&(node.x+node.size)%patchSize==0)||
                        ((sides&4)!=0&&node.z%patchSize==0)||((sides&8)!=0&&(node.z+node.size)%patchSize==0))
                        queue.Enqueue(new Cell(patch.chunk,id));
                }
            }
            while(queue.Count>0)
            {
                var cell=queue.Dequeue();if((active[cell.chunk][cell.id]&ActiveFlag)==0)continue;ProcessedCells++;
                var node=chunks[cell.chunk].cells[cell.id];
                for(int s=0;s<4;s++)
                {
                    var neighbour=Neighbour(cell,s);if(neighbour.chunk<0)continue;
                    var other=chunks[neighbour.chunk].cells[neighbour.id];
                    if(other.size>node.size*2||((node.requiredMask&(1<<s))!=0&&other.size>=node.size))
                    {Split(neighbour,cell);queue.Enqueue(cell);}
                    else if(node.size>other.size*2||((other.requiredMask&(1<<(s^1)))!=0&&node.size>=other.size))
                    {Split(cell,neighbour);queue.Enqueue(neighbour);break;}
                }
            }
            changed.Clear();return true;
        }
        void QueuePatch(int p,int sides){queuedPatches.Add(p);patches[p].queuedSides|=sides;}
        int Mask(Cell cell,int sides=15)
        {
            int mask=0;var node=chunks[cell.chunk].cells[cell.id];
            for(int s=0;s<4;s++)
            {
                if((sides&(1<<s))==0)continue;
                var n=Neighbour(cell,s);
                // A previously baked cut can require an edge midpoint even after
                // its neighbour is removed. Keep its contour on the exposed edge.
                if(n.chunk>=0?chunks[n.chunk].cells[n.id].size<node.size:(node.requiredMask&(1<<s))!=0)mask|=1<<s;
            }
            return mask;
        }
        int CachedMask(Cell cell,int dirty)
        {
            int sides=15;
            if((dirty&16)==0)
            {
                var node=chunks[cell.chunk].cells[cell.id];
                sides=0;
                if((dirty&1)!=0&&node.x%patchSize==0)sides|=1;
                if((dirty&2)!=0&&(node.x+node.size)%patchSize==0)sides|=2;
                if((dirty&4)!=0&&node.z%patchSize==0)sides|=4;
                if((dirty&8)!=0&&(node.z+node.size)%patchSize==0)sides|=8;
            }
            int previous=active[cell.chunk][cell.id]&15;
            if(sides==0)return previous;
            IndexMaskQueries+=(sides&1)+((sides>>1)&1)+((sides>>2)&1)+((sides>>3)&1);
            int mask=(previous&~sides)|Mask(cell,sides);
            active[cell.chunk][cell.id]=(byte)(ActiveFlag|mask);
            return mask;
        }
        public void WriteIndices(int chunk,List<int> output,LTRockSeamIndices.Plan seam=null)
        {
            output.Clear();
            for(int p=0;p<chunks[chunk].patches.Length;p++)
            {
                var patch=patches[PatchId(chunk,p)];
                if(patch.indicesValid){output.AddRange(patch.indices);IndexCacheHits++;continue;}
                int first=output.Count;IndexCacheBuilds++;
                foreach(int id in patch.active)
                {
                    var c=chunks[chunk].cells[id];int mask=CachedMask(new Cell(chunk,id),patch.maskDirty);
                    output.AddRange(c.variants[LTSpatialLODMath.VariantIndex(mask,c.possibleMask)].indices);
                }
                IndexCacheCells+=patch.active.Count;
                CacheIndices(patch,output,first);
                // Masks are still reusable when the index payload exceeds its
                // cache budget. Never clear this on Update before a successful read.
                patch.maskDirty=0;
            }
            // The seam owns invariant native faces. Keep the cache unmodified so
            // near/far selection and exact LOD0 restoration share the same cells.
            seam?.Apply(output);
        }
        void CacheIndices(Patch patch,List<int> output,int first)
        {
            int count=output.Count-first,old=patch.indices?.Capacity??0;
            if(count>old)
            {
                int available=indexCacheBudget-indexCacheCapacity+old;
                if(count>available){IndexCacheFallbacks++;return;}
                int capacity=(int)Math.Min(available,Math.Max(count,Math.Max(64L,(long)old*2)));
                if(patch.indices==null)patch.indices=new List<int>(capacity);else patch.indices.Capacity=capacity;
                indexCacheCapacity+=capacity-old;
            }
            else if(patch.indices==null)patch.indices=new List<int>();
            patch.indices.Clear();
            // Do not expose the cached list to Mesh/callers. Hot cache hits use
            // List.AddRange's bulk copy; only invalidated patches rebuild this list.
            for(int i=first;i<output.Count;i++)patch.indices.Add(output[i]);
            patch.indicesValid=true;
        }
        // Diagnostic/test-only. The camera hot path does not scan the entire world.
        public void Validate()
        {
            foreach(var patch in patches)
            {
                if(patch==null)continue;
                long area=0;
                foreach(int id in patch.active)
                {
                    var cell=new Cell(patch.chunk,id);var c=chunks[cell.chunk].cells[id];area+=(long)c.size*c.size;
                    int mask=Mask(cell);LTSpatialLODMath.VariantIndex(mask,c.possibleMask);
                    if((mask&c.requiredMask)!=c.requiredMask)throw new InvalidOperationException("Cut contour stitch lost.");
                    for(int s=0;s<4;s++)
                    {var n=Neighbour(cell,s);if(n.chunk>=0){int size=chunks[n.chunk].cells[n.id].size;if(size>c.size*2||c.size>size*2)throw new InvalidOperationException("LOD neighbour balance failed.");}}
                }
                if(area!=(long)patchSize*patchSize)throw new InvalidOperationException("LOD patch coverage failed.");
            }
        }
    }
}
