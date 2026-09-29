using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Blittable arithmetic shared by the Burst job and the managed parity tests.
    // A parent's error is >= every child's error, and its generation is larger.
    // Therefore an eligible parent implies eligible children at the same LOD.
    // Bottom-up evaluation replaces repeated per-LOD dictionary merge passes.
    internal static class LTLODJobMath
    {
        internal struct Cell
        {
            public int x,z,size,parent,c0,c1,c2,c3,leaf,merge;
            public float h00,h10,h20,h01,h11,h21,h02,h12,h22;
        }
        internal struct Metric
        {
            public float a,sx,sz;
            public double error;
            public int generation,valid;
        }
        internal struct Level
        {
            public int steps;
            public float tolerance;
        }
        static float Height(Cell p,Metric n,int x,int z)=>n.a+(x-p.x)*n.sx+(z-p.z)*n.sz;
        static Metric Plane(Cell p)=>new Metric{a=p.h00,sx=(p.h20-p.h00)/p.size,sz=(p.h02-p.h00)/p.size,valid=1};
        static void Sample(ref Metric n,Cell p,int x,int z,float height)
        {n.error=Math.Max(n.error,Math.Abs(height-Height(p,n,x,z)));}
        internal static Metric Leaf(Cell p)
        {
            var n=Plane(p);int h=p.size/2;if(h==0)return n;
            Sample(ref n,p,p.x,p.z,p.h00);Sample(ref n,p,p.x+h,p.z,p.h10);Sample(ref n,p,p.x+2*h,p.z,p.h20);
            Sample(ref n,p,p.x,p.z+h,p.h01);Sample(ref n,p,p.x+h,p.z+h,p.h11);Sample(ref n,p,p.x+2*h,p.z+h,p.h21);
            Sample(ref n,p,p.x,p.z+2*h,p.h02);Sample(ref n,p,p.x+h,p.z+2*h,p.h12);Sample(ref n,p,p.x+2*h,p.z+2*h,p.h22);
            return n;
        }
        static void Child(ref Metric parent,Cell p,Cell child,Metric n)
        {
            double delta=0;int h=child.size;
            for(int j=0;j<=1;j++)for(int i=0;i<=1;i++)
            {int x=child.x+i*h,z=child.z+j*h;delta=Math.Max(delta,Math.Abs(Height(child,n,x,z)-Height(p,parent,x,z)));}
            parent.error=Math.Max(parent.error,n.error+delta);
            parent.generation=Math.Max(parent.generation,n.generation+1);
        }
        internal static Metric Parent(Cell p,Cell a,Metric na,Cell b,Metric nb,Cell c,Metric nc,Cell d,Metric nd)
        {
            if(p.merge==0||na.valid==0||nb.valid==0||nc.valid==0||nd.valid==0)return default;
            var n=Plane(p);
            Child(ref n,p,a,na);Child(ref n,p,b,nb);Child(ref n,p,c,nc);Child(ref n,p,d,nd);
            return n;
        }
        internal static bool Eligible(Cell p,Metric n,Level level)=>p.leaf!=0||
            (n.valid!=0&&n.generation<=level.steps&&n.error<=level.tolerance*.5);
        internal static void Validate(Level level)
        {
            if(level.steps<0||level.steps>12||float.IsNaN(level.tolerance)||float.IsInfinity(level.tolerance)||level.tolerance<0)
                throw new InvalidOperationException("LOD: use 0..12 simplification steps and a finite non-negative height error.");
        }
    }

    // This part deliberately stays on the editor thread: evaluate/protection can
    // read Texture2D, terrain sources and road snapshots. No such objects or
    // delegates cross the job boundary. Work yields between small sample batches.
    internal sealed class LTLODJobInput
    {
        const int N=LTBalancedForest.N;
        public LTLODJobMath.Cell[] Cells {get;private set;}
        public LTLODJobMath.Level[] Levels {get;private set;}
        public Vector3Int[] Fine {get;private set;}
        public int HeightSampleCount {get;private set;}

        public IEnumerable<LTBuildStep> Prepare(List<Vector3Int> fine,Rect rect,LTLODJobMath.Level[] levels,
            Func<float,float,float> evaluate,List<Rect> protectedRegions=null,Func<Rect,bool> protectedArea=null,
            int patchDivisions=1,bool lockBoundary=true)
        {
            if(Cells!=null)throw new InvalidOperationException("LOD snapshot already prepared.");
            int maxSteps=0;foreach(var level in levels){LTLODJobMath.Validate(level);maxSteps=Math.Max(maxSteps,level.steps);}
            Levels=(LTLODJobMath.Level[])levels.Clone();Fine=fine.ToArray();
            int maxSize=N/LTSpatialLODMath.Divisions(patchDivisions);
            var leaves=new HashSet<Vector3Int>();var keys=new HashSet<Vector3Int>();
            var clock=Stopwatch.StartNew();int visits=0;
            bool Yield()
            {
                if(++visits<256&&clock.Elapsed.TotalMilliseconds<2)return false;
                visits=0;clock.Restart();return true;
            }
            foreach(var leaf in Fine)
            {
                if(leaf.z<1||leaf.z>N||(leaf.z&(leaf.z-1))!=0||leaf.x<0||leaf.y<0||leaf.x+leaf.z>N||leaf.y+leaf.z>N||leaf.x%leaf.z!=0||leaf.y%leaf.z!=0||!leaves.Add(leaf))
                    throw new InvalidOperationException("Invalid LOD quadtree leaf.");
                keys.Add(leaf);var p=leaf;
                for(int depth=0;depth<maxSteps&&p.z<maxSize;depth++)
                {
                    int width=p.z*2;p=new Vector3Int(p.x/width*width,p.y/width*width,width);
                    keys.Add(p);
                }
                if(Yield())yield return LTBuildStep.Working;
            }
            var ordered=new List<Vector3Int>(keys);
            ordered.Sort((a,b)=>{int c=a.z.CompareTo(b.z);if(c==0)c=a.y.CompareTo(b.y);return c!=0?c:a.x.CompareTo(b.x);});
            yield return LTBuildStep.Working;
            var lookup=new Dictionary<Vector3Int,int>(ordered.Count);
            for(int i=0;i<ordered.Count;i++){lookup.Add(ordered[i],i);if(Yield())yield return LTBuildStep.Working;}
            var cells=new LTLODJobMath.Cell[ordered.Count];
            var heights=new Dictionary<Vector2Int,float>();
            float H(int x,int z)
            {
                var key=new Vector2Int(x,z);
                if(!heights.TryGetValue(key,out float value))
                {
                    value=evaluate((float)(rect.xMin+(double)x/N*rect.width),(float)(rect.yMin+(double)z/N*rect.height));
                    if(float.IsNaN(value)||float.IsInfinity(value))throw new InvalidOperationException("Non-finite LOD height sample. Previous meshes preserved.");
                    heights.Add(key,value);
                }
                return value;
            }
            int Index(int x,int z,int size)=>lookup.TryGetValue(new Vector3Int(x,z,size),out int index)?index:-1;
            for(int i=0;i<ordered.Count;i++)
            {
                var p=ordered[i];int h=p.z/2,width=p.z*2;
                var n=new LTLODJobMath.Cell{x=p.x,z=p.y,size=p.z,leaf=leaves.Contains(p)?1:0,
                    parent=Index(p.x/width*width,p.y/width*width,width),
                    c0=Index(p.x,p.y,h),c1=Index(p.x+h,p.y,h),c2=Index(p.x,p.y+h,h),c3=Index(p.x+h,p.y+h,h)};
                if(n.leaf==0&&n.c0>=0&&n.c1>=0&&n.c2>=0&&n.c3>=0&&
                    !(lockBoundary&&(p.x==0||p.y==0||p.x+p.z==N||p.y+p.z==N)))
                {
                    var area=new Rect(rect.xMin+p.x/(float)N*rect.width,rect.yMin+p.y/(float)N*rect.height,p.z/(float)N*rect.width,p.z/(float)N*rect.height);
                    bool blocked=false;
                    if(protectedRegions!=null)foreach(var region in protectedRegions)if(LTStampMesh.Overlap(region,area)){blocked=true;break;}
                    if(!blocked&&protectedArea!=null)blocked=protectedArea(area);
                    n.merge=blocked?0:1;
                }
                if(maxSteps>0&&(n.leaf!=0||n.merge!=0))
                {
                    n.h00=H(p.x,p.y);n.h20=H(p.x+p.z,p.y);n.h02=H(p.x,p.y+p.z);
                    if(n.leaf!=0)
                    {
                        n.h10=H(p.x+h,p.y);n.h01=H(p.x,p.y+h);n.h11=H(p.x+h,p.y+h);
                        n.h21=H(p.x+p.z,p.y+h);n.h12=H(p.x+h,p.y+p.z);n.h22=H(p.x+p.z,p.y+p.z);
                    }
                }
                cells[i]=n;if(Yield())yield return LTBuildStep.Working;
            }
            HeightSampleCount=heights.Count;Cells=cells;
        }
        public static void SortPlan(List<Vector3Int> plan)=>plan.Sort((a,b)=>
        {int c=a.y.CompareTo(b.y);if(c==0)c=a.x.CompareTo(b.x);return c!=0?c:a.z.CompareTo(b.z);});
    }
}
