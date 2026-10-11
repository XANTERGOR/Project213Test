using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public enum LTTerrainLODMode { WholeChunk, WithinChunk }
    [Serializable] public sealed class LTSpatialLODLevel { public int[] cells; }
    [Serializable] public sealed class LTSpatialLODVariant { public int[] indices; }
    [Serializable] public struct LTSpatialLODCell
    {
        public int x,z,size,patch,child0,child1,child2,child3,possibleMask,requiredMask;
        public LTSpatialLODVariant[] variants;
        public int Child(int quadrant)=>quadrant==0?child0:quadrant==1?child1:quadrant==2?child2:child3;
    }
    [Serializable] public sealed class LTSpatialLODPatch
    {
        public int root;
        public Bounds bounds;
        public LTSpatialLODLevel[] levels;
    }
    // Dyadic hierarchy and pre-baked stitch variants. No height sampling at runtime.
    public static class LTSpatialLODMath
    {
        public const int N=1<<24, Version=2;
        public static int Divisions(int value)
        {int result=1;while(result<value&&result<8)result*=2;return result;}
        public static List<Vector3Int> PartitionLeaves(List<Vector3Int> leaves,int divisions)
        {
            int size=N/Divisions(divisions);var result=new List<Vector3Int>();
            void Add(Vector3Int p)
            {
                if(p.z<=size){result.Add(p);return;}
                int h=p.z/2;
                Add(new Vector3Int(p.x,p.y,h));Add(new Vector3Int(p.x+h,p.y,h));
                Add(new Vector3Int(p.x,p.y+h,h));Add(new Vector3Int(p.x+h,p.y+h,h));
            }
            foreach(var p in leaves)Add(p);return result;
        }
        public static int VariantIndex(int mask,int possible)
        {
            if((mask&~possible)!=0)throw new InvalidOperationException("Spatial LOD exceeds its baked edge resolution.");
            int index=0,bit=1;
            for(int side=0;side<4;side++)if((possible&(1<<side))!=0)
            {if((mask&(1<<side))!=0)index|=bit;bit<<=1;}
            return index;
        }
        public sealed class Output
        {
            public Vector3[] vertices,normals;public Vector2[] uv;
            public int[] baseIndices,baseCellOrder;public int divisions;
            public bool[] renderable;
            public LTSpatialLODCell[] cells;public LTSpatialLODPatch[] patches;
        }
        public static Output BuildLayout(List<Vector3Int>[] plans,int divisions,int budget,Func<Vector3Int,int> baseMask,Func<Vector3Int,bool> preserveContour=null)
        {
            Output result=null;foreach(int step in BuildLayoutSteps(plans,divisions,budget,baseMask,value=>result=value,preserveContour)){}
            return result;
        }
        // The unfinished hierarchy stays private. Iterative traversal preserves
        // recursive preorder, including callback order and all cell/vertex IDs.
        public static IEnumerable<int> BuildLayoutSteps(List<Vector3Int>[] plans,int divisions,int budget,Func<Vector3Int,int> baseMask,
            Action<Output> completed,Func<Vector3Int,bool> preserveContour=null,int workPerStep=512)
        {
            if(workPerStep<1)throw new ArgumentOutOfRangeException(nameof(workPerStep));
            if(completed==null)throw new ArgumentNullException(nameof(completed));
            divisions=Divisions(divisions);
            if(plans==null||plans.Length==0)throw new ArgumentException("Missing spatial LOD plans.");
            int width=N/divisions,work=0;
            var fine=new HashSet<Vector3Int>(plans[0]);
            var cells=new List<LTSpatialLODCell>();var lookup=new Dictionary<Vector3Int,int>();
            var patches=new LTSpatialLODPatch[divisions*divisions];
            var stack=new Stack<(Vector3Int cell,int patch,int parent,int quadrant)>();
            for(int patch=patches.Length-1;patch>=0;patch--)
                stack.Push((new Vector3Int(patch%divisions*width,patch/divisions*width,width),patch,-1,0));
            while(stack.Count>0)
            {
                var next=stack.Pop();var p=next.cell;int patch=next.patch;
                if(cells.Count>=budget*2L)throw new InvalidOperationException("Spatial LOD hierarchy budget exceeded.");
                int id=cells.Count;cells.Add(default);lookup.Add(p,id);
                if(next.parent<0)patches[patch]=new LTSpatialLODPatch{root=id,levels=new LTSpatialLODLevel[plans.Length]};
                else
                {
                    var parent=cells[next.parent];
                    if(next.quadrant==0)parent.child0=id;else if(next.quadrant==1)parent.child1=id;
                    else if(next.quadrant==2)parent.child2=id;else parent.child3=id;
                    cells[next.parent]=parent;
                }
                var c=new LTSpatialLODCell{x=p.x,z=p.y,size=p.z,patch=patch,child0=-1,child1=-1,child2=-1,child3=-1};
                if(fine.Contains(p))
                {
                    c.possibleMask=baseMask(p);
                    if(preserveContour!=null&&preserveContour(p))c.requiredMask=c.possibleMask;
                }
                else
                {
                    if(p.z<=2)throw new InvalidOperationException("Incomplete spatial LOD base partition.");
                    int h=p.z/2;
                    stack.Push((new Vector3Int(p.x+h,p.y+h,h),patch,id,3));stack.Push((new Vector3Int(p.x,p.y+h,h),patch,id,2));
                    stack.Push((new Vector3Int(p.x+h,p.y,h),patch,id,1));stack.Push((new Vector3Int(p.x,p.y,h),patch,id,0));
                    c.possibleMask=15;
                }
                cells[id]=c;
                if(++work%workPerStep==0)yield return work;
            }
            for(int level=0;level<plans.Length;level++)
            {
                var lists=new List<int>[patches.Length];var areas=new long[patches.Length];
                for(int p=0;p<patches.Length;p++)lists[p]=new List<int>();
                foreach(var key in plans[level])
                {
                    if(!lookup.TryGetValue(key,out int id))throw new InvalidOperationException("LOD plan is not a coarsening of its patch hierarchy.");
                    int p=cells[id].patch;lists[p].Add(id);areas[p]+=(long)key.z*key.z;
                    if(++work%workPerStep==0)yield return work;
                }
                for(int p=0;p<patches.Length;p++)
                {
                    if(areas[p]!=(long)width*width)throw new InvalidOperationException("Incomplete spatial LOD patch.");
                    patches[p].levels[level]=new LTSpatialLODLevel{cells=lists[p].ToArray()};
                }
            }
            var order=new int[plans[0].Count];for(int i=0;i<order.Length;i++)
            {order[i]=lookup[plans[0][i]];if(++work%workPerStep==0)yield return work;}
            var renderable=new bool[cells.Count];var pending=new Stack<int>();
            foreach(var patch in patches)foreach(var level in patch.levels)foreach(int id in level.cells)
            {
                pending.Push(id);
                while(pending.Count>0)
                {
                    int current=pending.Pop();
                    if(!renderable[current])
                    {
                        renderable[current]=true;
                        if(cells[current].child0>=0)for(int i=3;i>=0;i--)pending.Push(cells[current].Child(i));
                    }
                    if(++work%workPerStep==0)yield return work;
                }
            }
            completed(new Output{cells=cells.ToArray(),patches=patches,divisions=divisions,baseCellOrder=order,renderable=renderable});
        }
    }
}
