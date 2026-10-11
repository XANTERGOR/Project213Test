using System;
using System.Collections.Generic;
using UnityEngine;
using LocalTerrainPrototype;
using Output=LocalTerrainPrototype.LTSpatialLODMath.Output;
// Frozen pre-slicing layout oracle; never used by production.
static class SpatialLayoutReference
{
    const int N=LTSpatialLODMath.N;
    static int Divisions(int value)=>LTSpatialLODMath.Divisions(value);
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

