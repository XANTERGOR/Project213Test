using System;
using System.Collections.Generic;

namespace LocalTerrainPrototype
{
    // Shared by ALL chunks: a chunk edge is not a permanently pinned LOD0 edge.
    // Only active cell IDs and index lists change. Dependency tracking allows previously
    // forced refinements to disappear again when the camera moves away.
    public sealed class LTSpatialLODTopology
    {
        public sealed class Chunk { public LTSpatialLODCell[] cells; public LTSpatialLODPatch[] patches; }
        sealed class Patch
        {
            public int chunk,local,requested;
            public readonly HashSet<int> active=new HashSet<int>(),dependsOn=new HashSet<int>(),dependents=new HashSet<int>();
        }
        readonly struct Cell
        {
            public readonly int chunk,id;
            public Cell(int chunk,int id){this.chunk=chunk;this.id=id;}
        }
        const int N=LTSpatialLODMath.N;
        readonly int columns,rows,divisions,patchColumns,patchSize;
        readonly Chunk[] chunks;
        readonly bool[][] active;
        readonly Patch[] patches;
        readonly HashSet<int> changed=new HashSet<int>(),invalidated=new HashSet<int>(),queuedPatches=new HashSet<int>();
        readonly Queue<int> patchQueue=new Queue<int>();
        readonly Queue<Cell> queue=new Queue<Cell>();
        public readonly HashSet<int> DirtyChunks=new HashSet<int>();
        public int ProcessedCells {get;private set;}
        public int ResetPatches {get;private set;}
        public LTSpatialLODTopology(int columns,int rows,int divisions,Chunk[] chunks)
        {
            if(columns<1||rows<1||chunks==null||chunks.Length!=columns*rows)throw new ArgumentException("Invalid LOD world layout.");
            this.columns=columns;this.rows=rows;this.divisions=LTSpatialLODMath.Divisions(divisions);this.chunks=chunks;
            patchColumns=columns*this.divisions;patchSize=N/this.divisions;
            patches=new Patch[columns*rows*this.divisions*this.divisions];active=new bool[chunks.Length][];
            for(int c=0;c<chunks.Length;c++)
            {
                if(chunks[c]==null||chunks[c].cells==null||chunks[c].patches==null||chunks[c].patches.Length!=this.divisions*this.divisions)
                    throw new ArgumentException("Rebake all spatial LOD chunks before enabling preview.");
                active[c]=new bool[chunks[c].cells.Length];
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
            return x<0||z<0||x>=patchColumns||z>=rows*divisions?-1:z*patchColumns+x;
        }
        void Dirty(int p)
        {
            DirtyChunks.Add(patches[p].chunk);
            for(int s=0;s<4;s++){int n=Adjacent(p,s);if(n>=0)DirtyChunks.Add(patches[n].chunk);}
        }
        Cell Find(int x,int z)
        {
            if(x<0||z<0||x>=columns*N||z>=rows*N)return new Cell(-1,-1);
            int cx=x/N,cz=z/N,c=cz*columns+cx; x-=cx*N;z-=cz*N;
            int id=chunks[c].patches[z/patchSize*divisions+x/patchSize].root;
            while(!active[c][id])
            {
                var node=chunks[c].cells[id];int h=node.size/2;
                id=node.Child((x>=node.x+h?1:0)+(z>=node.z+h?2:0));
                if(id<0)throw new InvalidOperationException("Incomplete active LOD partition.");
            }
            return new Cell(c,id);
        }
        Cell Neighbour(Cell c,int side)
        {
            var node=chunks[c.chunk].cells[c.id];
            int x=c.chunk%columns*N+node.x,z=c.chunk/columns*N+node.z;
            return side==0?Find(x-1,z+node.size/2):side==1?Find(x+node.size,z+node.size/2):
                side==2?Find(x+node.size/2,z-1):Find(x+node.size/2,z+node.size);
        }
        void Split(Cell cell,Cell cause)
        {
            var node=chunks[cell.chunk].cells[cell.id];
            if(node.child0<0)throw new InvalidOperationException("Spatial LOD requires finer cells than LOD0. Rebuild neighbours together.");
            int p=PatchOf(cell),from=PatchOf(cause);var patch=patches[p];
            if(p!=from){patch.dependsOn.Add(from);patches[from].dependents.Add(p);}
            active[cell.chunk][cell.id]=false;patch.active.Remove(cell.id);
            for(int i=0;i<4;i++){int child=node.Child(i);active[cell.chunk][child]=true;patch.active.Add(child);queue.Enqueue(new Cell(cell.chunk,child));}
            Dirty(p);
        }
        public bool Update()
        {
            DirtyChunks.Clear();ProcessedCells=ResetPatches=0;
            if(changed.Count==0)return false;
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
                foreach(int id in patch.active)active[patch.chunk][id]=false;
                patch.active.Clear();
                foreach(int id in chunks[patch.chunk].patches[patch.local].levels[patch.requested].cells)
                {active[patch.chunk][id]=true;patch.active.Add(id);}
                Dirty(p);queuedPatches.Add(p);
                for(int s=0;s<4;s++){int n=Adjacent(p,s);if(n>=0)queuedPatches.Add(n);}
            }
            ResetPatches=invalidated.Count;
            // Queue the whole adjacent patch, not just one edge midpoint: an edge can
            // contain much smaller leaves than its midpoint happens to encounter.
            foreach(int p in queuedPatches)foreach(int id in patches[p].active)queue.Enqueue(new Cell(patches[p].chunk,id));
            while(queue.Count>0)
            {
                var cell=queue.Dequeue();if(!active[cell.chunk][cell.id])continue;ProcessedCells++;
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
        int Mask(Cell cell)
        {
            int mask=0;var node=chunks[cell.chunk].cells[cell.id];
            for(int s=0;s<4;s++){var n=Neighbour(cell,s);if(n.chunk>=0&&chunks[n.chunk].cells[n.id].size<node.size)mask|=1<<s;}
            return mask;
        }
        public void WriteIndices(int chunk,List<int> output)
        {
            output.Clear();
            for(int p=0;p<chunks[chunk].patches.Length;p++)foreach(int id in patches[PatchId(chunk,p)].active)
            {
                var c=chunks[chunk].cells[id];int mask=Mask(new Cell(chunk,id));
                output.AddRange(c.variants[LTSpatialLODMath.VariantIndex(mask,c.possibleMask)].indices);
            }
        }
        // Diagnostic/test-only. The camera hot path does not scan the entire world.
        public void Validate()
        {
            foreach(var patch in patches)
            {
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
