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
        // Cache the closure of each raw plan in isolation, never the final
        // neighbour-refined plan. This allows border refinements to coarsen again.
        public sealed class InternalPlanCache
        {
            sealed class Entry
            {
                public HashSet<Vector3Int> source;
                public List<Vector3Int> balanced;
            }
            readonly Dictionary<int,Entry> entries=new Dictionary<int,Entry>();
            public int Reused {get;private set;}
            public int Rebuilt {get;private set;}
            public int ProcessedCells {get;private set;}
            public Dictionary<int,List<Vector3Int>> Prepare(Dictionary<int,List<Vector3Int>> raw,int budget)
            {
                Reused=Rebuilt=ProcessedCells=0;
                var result=new Dictionary<int,List<Vector3Int>>();
                foreach(int id in entries.Keys.Where(id=>!raw.ContainsKey(id)).ToArray())entries.Remove(id);
                foreach(var pair in raw)
                {
                    if(entries.TryGetValue(pair.Key,out var cached)&&cached.source.Count==pair.Value.Count&&cached.source.SetEquals(pair.Value))
                    {
                        if(cached.balanced.Count>budget)throw new InvalidOperationException("Topology budget exceeded during balancing. Previous meshes were preserved.");
                        result[pair.Key]=cached.balanced;Reused++;continue;
                    }
                    var local=new LTBalancedForest(1,1,budget,new Dictionary<int,List<Vector3Int>>{{0,pair.Value}});
                    local.Balance();
                    var entry=new Entry{source=new HashSet<Vector3Int>(pair.Value),balanced=local.Plan(0)};
                    // Only publish a completed pure calculation; aborted scene
                    // rebuilds can safely retain it. Source lists are never aliased.
                    entries[pair.Key]=entry;result[pair.Key]=entry.balanced;
                    Rebuilt++;ProcessedCells+=local.ProcessedCells;
                }
                return result;
            }
        }
        public sealed class Cell
        {
            public int chunk,x,z,w;public Vector3Int key;
        }
        readonly Dictionary<Vector3Int,Cell> cells=new Dictionary<Vector3Int,Cell>();
        readonly Dictionary<int,HashSet<Vector3Int>> byChunk=new Dictionary<int,HashSet<Vector3Int>>();
        readonly int columns,rows,budget;
        readonly Queue<Cell> queue=new Queue<Cell>();
        public int ProcessedCells {get;private set;}
        // Trusted chunks must already be internally 2:1 balanced. Their borders
        // remain queued; any new split queues its children and propagates inward.
        public LTBalancedForest(int columns,int rows,int budget,Dictionary<int,List<Vector3Int>> plans,ISet<int> internallyBalancedChunks=null)
        {
            this.columns=columns;this.rows=rows;this.budget=budget;
            foreach(var pair in plans)
            {
                byChunk[pair.Key]=new HashSet<Vector3Int>();
                bool trusted=internallyBalancedChunks!=null&&internallyBalancedChunks.Contains(pair.Key);
                foreach(var p in pair.Value)Add(pair.Key,p.x,p.y,p.z,
                    !trusted||p.x==0||p.y==0||p.x+p.z==N||p.y+p.z==N);
            }
        }
        void Add(int chunk,int x,int z,int w,bool enqueue=true)
        {
            var key=new Vector3Int(chunk%columns*N+x,chunk/columns*N+z,w);
            var cell=new Cell{chunk=chunk,x=x,z=z,w=w,key=key};cells.Add(key,cell);byChunk[chunk].Add(new Vector3Int(x,z,w));if(enqueue)queue.Enqueue(cell);
            if(byChunk[chunk].Count>budget)throw new InvalidOperationException("Topology budget exceeded during balancing. Previous meshes were preserved.");
        }
        Cell Find(int x,int z)
        {
            if(x<0||z<0||x>=columns*N||z>=rows*N)return null;
            int cx=x/N,cz=z/N,lx=x-cx*N,lz=z-cz*N;
            for(int w=N;w>=2;w/=2)
            {
                var key=new Vector3Int(cx*N+lx/w*w,cz*N+lz/w*w,w);
                if(cells.TryGetValue(key,out var c))return c;
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
            cells.Remove(c.key);byChunk[c.chunk].Remove(new Vector3Int(c.x,c.z,c.w));int h=c.w/2;
            Add(c.chunk,c.x,c.z,h);Add(c.chunk,c.x+h,c.z,h);Add(c.chunk,c.x,c.z+h,h);Add(c.chunk,c.x+h,c.z+h,h);
        }
        public void Balance()
        {
            int processed=0;
            while(queue.Count>0)
            {
                var c=queue.Dequeue();if(!cells.TryGetValue(c.key,out var live)||!ReferenceEquals(c,live))continue;
                ProcessedCells++;
                for(int side=0;side<4;side++)
                {
                    var neighbour=Neighbour(c,side);
                    if(neighbour!=null&&neighbour.w>2*c.w){Split(neighbour);queue.Enqueue(c);}
                }
                if((++processed&16383)==0&&UnityEditor.EditorUtility.DisplayCancelableProgressBar("Local Terrain","Balancing local transitions; Cancel preserves old meshes.",.5f))
                    throw new OperationCanceledException("Terrain balancing cancelled.");
            }
        }
        public List<Vector3Int> Plan(int chunk)=>byChunk[chunk].OrderBy(p=>p.y).ThenBy(p=>p.x).ThenBy(p=>p.z).ToList();
        public bool Midpoint(int chunk,Vector3Int p,int side)
        {
            var key=new Vector3Int(chunk%columns*N+p.x,chunk/columns*N+p.y,p.z);
            var c=cells[key];var neighbour=Neighbour(c,side);return neighbour!=null&&neighbour.w<c.w;
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
            foreach(var c in cells.Values)for(int side=0;side<4;side++)
            {var n=Neighbour(c,side);if(n!=null&&(n.w>2*c.w||c.w>2*n.w))throw new InvalidOperationException("Unbalanced terrain edge.");}
        }
    }
}
