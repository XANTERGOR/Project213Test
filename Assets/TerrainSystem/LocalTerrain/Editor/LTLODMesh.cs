using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEditor;
namespace LocalTerrainPrototype
{
    public static class LTLODMesh
    {
        const int N=LTBalancedForest.N;
        // Offroad is the terrain surface itself: its ruts and shoulders are governed
        // by the normal LOD height-error budget, not a permanent density-zone lock.
        // Asphalt has a separate, independently LODed mesh. Retain its foundation
        // until the two surfaces can be simplified together without intersections.
        public static bool RequiresFixedRoadSurface(LTRoadMode mode)=>mode==LTRoadMode.Asphalt;
        sealed class Node
        {
            public Vector3Int p;public float a,sx,sz;public double error;public int generation;
            public float Height(float x,float z)=>a+(x-p.x)*sx+(z-p.y)*sz;
        }
        // Share the clock across all chunks/levels in one rebuild. Fast work stays
        // silent; long multi-chunk work remains cancellable even with small chunks.
        // The rebuild's existing finally block owns ClearProgressBar.
        public sealed class BuildProgress
        {
            readonly Func<double> seconds;
            readonly Func<bool> display;
            readonly double started;
            double nextDisplay;
            int visits;
            public BuildProgress():this(()=>Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency,
                ()=>EditorUtility.DisplayCancelableProgressBar("Local Terrain","Building LOD meshes; Cancel preserves old meshes.",.65f)){}
            internal BuildProgress(Func<double> seconds,Func<bool> display)
            {
                this.seconds=seconds;this.display=display;started=seconds();nextDisplay=started+1;
            }
            public void Poll()
            {
                if((++visits&1023)!=0)return;
                double now=seconds();
                if(now<nextDisplay)return;
                nextDisplay=now+.1;
                if(display())throw new OperationCanceledException("LOD generation cancelled.");
            }
        }
        // A short-lived snapshot for ONE chunk and ONE synchronous rebuild.
        // Never retain across edits: evaluate must describe the same surface for
        // every level. Mutable simplification nodes are separate for each level.
        public sealed class Preparation
        {
            readonly Vector3Int[] fine;
            readonly Rect rect;
            readonly Func<float,float,float> evaluate;
            readonly BuildProgress progress;
            readonly Dictionary<Vector2Int,float> heights=new Dictionary<Vector2Int,float>();
            Node[] baseNodes;
            public int HeightSampleCount=>heights.Count;
            public Preparation(List<Vector3Int> fine,Rect rect,Func<float,float,float> evaluate,BuildProgress progress=null)
            {
                this.fine=fine.ToArray();this.rect=rect;this.evaluate=evaluate;
                this.progress=progress??new BuildProgress();
            }
            float H(int x,int z)
            {
                var key=new Vector2Int(x,z);
                if(!heights.TryGetValue(key,out float h))
                    heights[key]=h=evaluate((float)(rect.xMin+(double)x/N*rect.width),(float)(rect.yMin+(double)z/N*rect.height));
                return h;
            }
            Node Make(Vector3Int p)
            {
                float a=H(p.x,p.y);
                return new Node{p=p,a=a,sx=(H(p.x+p.z,p.y)-a)/p.z,sz=(H(p.x,p.y+p.z)-a)/p.z};
            }
            void PrepareBase()
            {
                if(baseNodes!=null)return;
                var result=new Node[fine.Length];
                for(int k=0;k<fine.Length;k++)
                {
                    progress.Poll();var p=fine[k];var n=Make(p);
                    for(int j=0;j<=2;j++)for(int i=0;i<=2;i++)
                    {int x=p.x+i*(p.z/2),z=p.y+j*(p.z/2);n.error=Math.Max(n.error,Math.Abs(H(x,z)-n.Height(x,z)));}
                    result[k]=n;
                }
                // Publish only after preparation succeeds (including cancellation).
                baseNodes=result;
            }
            // Whole-chunk meshes retain their border; spatial LOD stitches it at
            // runtime. Keep the original plane-error bound and merge order exactly.
            public List<Vector3Int> Coarsen(int steps,float tolerance,List<Rect> protectedRegions=null,Func<Rect,bool> protectedArea=null,int patchDivisions=1,bool lockBoundary=true)
            {
                if(steps<0||steps>12||float.IsNaN(tolerance)||float.IsInfinity(tolerance)||tolerance<0)
                    throw new InvalidOperationException("LOD: use 0..12 simplification steps and a finite non-negative height error.");
                if(steps==0)return new List<Vector3Int>(fine);
                PrepareBase();
                var nodes=new Dictionary<Vector3Int,Node>(fine.Length);
                // Base nodes are immutable after PrepareBase; only fresh parents
                // are mutated below. No merge state is shared between LOD levels.
                foreach(var n in baseNodes){progress.Poll();nodes.Add(n.p,n);}
                for(int pass=0;pass<steps;pass++)
                {
                    bool changed=false;
                    var parents=new HashSet<Vector3Int>();
                    foreach(var p in nodes.Keys)
                    {if(p.z>=N)continue;int w=p.z*2;parents.Add(new Vector3Int(p.x/w*w,p.y/w*w,w));}
                    foreach(var p in parents.OrderBy(p=>p.z).ThenBy(p=>p.y).ThenBy(p=>p.x))
                    {
                        progress.Poll();if(lockBoundary&&(p.x==0||p.y==0||p.x+p.z==N||p.y+p.z==N))continue;
                        if(p.z>N/LTSpatialLODMath.Divisions(patchDivisions))continue;
                        var area=new Rect(rect.xMin+p.x/(float)N*rect.width,rect.yMin+p.y/(float)N*rect.height,p.z/(float)N*rect.width,p.z/(float)N*rect.height);
                        // Preserve the contact cells: the same rock bridge is used by all LODs.
                        if(protectedRegions!=null&&protectedRegions.Any(region=>LTStampMesh.Overlap(region,area)))continue;
                        if(protectedArea!=null&&protectedArea(area))continue;
                        int h=p.z/2;
                        var keys=new[]{new Vector3Int(p.x,p.y,h),new Vector3Int(p.x+h,p.y,h),new Vector3Int(p.x,p.y+h,h),new Vector3Int(p.x+h,p.y+h,h)};
                        if(keys.Any(k=>!nodes.ContainsKey(k)))continue;
                        var parent=Make(p);
                        foreach(var key in keys)
                        {
                            var child=nodes[key];double delta=0;
                            for(int j=0;j<=1;j++)for(int i=0;i<=1;i++)
                            {int x=key.x+i*h,z=key.y+j*h;delta=Math.Max(delta,Math.Abs(child.Height(x,z)-parent.Height(x,z)));}
                            parent.error=Math.Max(parent.error,child.error+delta);
                            parent.generation=Math.Max(parent.generation,child.generation+1);
                        }
                        if(parent.generation>steps||parent.error>tolerance*.5)continue;
                        foreach(var key in keys)nodes.Remove(key);
                        nodes.Add(p,parent);changed=true;
                    }
                    if(!changed)break;
                }
                return nodes.Keys.OrderBy(p=>p.y).ThenBy(p=>p.x).ThenBy(p=>p.z).ToList();
            }
        }
        // Compatibility entry point for isolated one-level callers.
        public static List<Vector3Int> Coarsen(List<Vector3Int> fine,Rect rect,int steps,float tolerance,Func<float,float,float> evaluate,List<Rect> protectedRegions=null,Func<Rect,bool> protectedArea=null,int patchDivisions=1,bool lockBoundary=true)
            =>new Preparation(fine,rect,evaluate).Coarsen(steps,tolerance,protectedRegions,protectedArea,patchDivisions,lockBoundary);
        public static void ValidateBoundary(List<Vector3Int> fine,List<Vector3Int> coarse)
        {
            Func<Vector3Int,bool> boundary=p=>p.x==0||p.y==0||p.x+p.z==N||p.y+p.z==N;
            if(!new HashSet<Vector3Int>(fine.Where(boundary)).SetEquals(coarse.Where(boundary)))
                throw new InvalidOperationException("LOD boundary mismatch. Previous meshes preserved.");
        }
    }
}
