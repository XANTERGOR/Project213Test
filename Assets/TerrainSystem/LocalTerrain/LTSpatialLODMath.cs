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
            divisions=Divisions(divisions);
            if(plans==null||plans.Length==0)throw new ArgumentException("Missing spatial LOD plans.");
            int width=N/divisions;
            var fine=new HashSet<Vector3Int>(plans[0]);
            var cells=new List<LTSpatialLODCell>();var lookup=new Dictionary<Vector3Int,int>();
            int Add(Vector3Int p,int patch)
            {
                if(cells.Count>=budget*2L)throw new InvalidOperationException("Spatial LOD hierarchy budget exceeded.");
                int id=cells.Count;cells.Add(default);lookup.Add(p,id);
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
                    c.child0=Add(new Vector3Int(p.x,p.y,h),patch);c.child1=Add(new Vector3Int(p.x+h,p.y,h),patch);
                    c.child2=Add(new Vector3Int(p.x,p.y+h,h),patch);c.child3=Add(new Vector3Int(p.x+h,p.y+h,h),patch);
                    c.possibleMask=15;
                }
                cells[id]=c;return id;
            }
            var patches=new LTSpatialLODPatch[divisions*divisions];
            for(int p=0;p<patches.Length;p++)patches[p]=new LTSpatialLODPatch
            {root=Add(new Vector3Int(p%divisions*width,p/divisions*width,width),p),levels=new LTSpatialLODLevel[plans.Length]};
            for(int level=0;level<plans.Length;level++)
            {
                var lists=new List<int>[patches.Length];var areas=new long[patches.Length];
                for(int p=0;p<patches.Length;p++)lists[p]=new List<int>();
                foreach(var key in plans[level])
                {
                    if(!lookup.TryGetValue(key,out int id))throw new InvalidOperationException("LOD plan is not a coarsening of its patch hierarchy.");
                    int p=cells[id].patch;lists[p].Add(id);areas[p]+=(long)key.z*key.z;
                }
                for(int p=0;p<patches.Length;p++)
                {
                    if(areas[p]!=(long)width*width)throw new InvalidOperationException("Incomplete spatial LOD patch.");
                    patches[p].levels[level]=new LTSpatialLODLevel{cells=lists[p].ToArray()};
                }
            }
            var order=new int[plans[0].Count];for(int i=0;i<order.Length;i++)order[i]=lookup[plans[0][i]];
            var renderable=new bool[cells.Count];
            void Mark(int id)
            {if(renderable[id])return;renderable[id]=true;if(cells[id].child0>=0)for(int i=0;i<4;i++)Mark(cells[id].Child(i));}
            foreach(var patch in patches)foreach(var level in patch.levels)foreach(int id in level.cells)Mark(id);
            return new Output{cells=cells.ToArray(),patches=patches,divisions=divisions,baseCellOrder=order,renderable=renderable};
        }
    }
}
