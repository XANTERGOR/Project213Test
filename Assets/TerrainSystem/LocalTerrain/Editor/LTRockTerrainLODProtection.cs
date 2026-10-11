using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Keep the actual LOD0 cut stencil, not the entire mesh-stamp AABB.
    // Nine samples cover corners, all possible stitch midpoints and the centre.
    // The one-cell halo keeps neighbouring LOD0 subdivisions, so legacy LODs
    // cannot change the boundary cell's fan/diagonal when their neighbours merge.
    // Prepared lookups retain no source mesh, height/cut delegate or Unity object.
    internal sealed class LTRockTerrainLODProtection
    {
        const int N=LTSpatialLODMath.N;
        HashSet<Vector3Int> blocked;
        Rect region;
        bool started;
        internal int BoundaryCellCount {get;private set;}
        internal int ProtectedCellCount {get;private set;}
        internal int SampleCount {get;private set;}

        internal IEnumerable<LTBuildStep> Prepare(IReadOnlyList<Vector3Int> fine,Rect rect,
            IReadOnlyList<Rect> candidates,Func<float,float,bool> cut)
        {
            if(started)throw new InvalidOperationException("Rock terrain LOD protection already started.");started=true;
            if(fine==null||cut==null||candidates==null||rect.width<=0||rect.height<=0)throw new ArgumentException("Invalid rock terrain LOD input.");
            var leaves=new HashSet<Vector3Int>(fine);var edgeCells=new List<Vector3Int>();
            var samples=new Dictionary<Vector2Int,bool>();var protectedLeaves=new HashSet<Vector3Int>();var next=new HashSet<Vector3Int>();
            bool Cut(int twiceX,int twiceZ)
            {
                var key=new Vector2Int(twiceX,twiceZ);
                if(!samples.TryGetValue(key,out bool value))
                {value=cut(rect.xMin+twiceX*.5f/N*rect.width,rect.yMin+twiceZ*.5f/N*rect.height);samples.Add(key,value);}
                return value;
            }
            int visits=0;var watch=Stopwatch.StartNew();
            foreach(var cell in fine)
            {
                var area=new Rect(rect.xMin+cell.x/(float)N*rect.width,rect.yMin+cell.y/(float)N*rect.height,
                    cell.z/(float)N*rect.width,cell.z/(float)N*rect.height);
                bool candidate=false;
                foreach(var bounds in candidates)if(area.xMin<=bounds.xMax&&area.xMax>=bounds.xMin&&area.yMin<=bounds.yMax&&area.yMax>=bounds.yMin)
                {candidate=true;break;}
                if(candidate)
                {
                    bool first=Cut(cell.x*2,cell.y*2),mixed=false;
                    for(int z=0;z<3&&!mixed;z++)for(int x=0;x<3;x++)
                        if(Cut(cell.x*2+x*cell.z,cell.y*2+z*cell.z)!=first){mixed=true;break;}
                    if(mixed)edgeCells.Add(cell);
                }
                if(++visits>=256||watch.Elapsed.TotalMilliseconds>=2){visits=0;yield return LTBuildStep.Working;watch.Restart();}
            }
            var stack=new Stack<Vector3Int>();
            foreach(var edge in edgeCells)
            {
                int minX=edge.x-edge.z,minZ=edge.y-edge.z,maxX=edge.x+2*edge.z,maxZ=edge.y+2*edge.z;
                stack.Push(new Vector3Int(0,0,N));
                while(stack.Count>0)
                {
                    var p=stack.Pop();
                    if(p.x>=maxX||p.y>=maxZ||p.x+p.z<=minX||p.y+p.z<=minZ)continue;
                    if(leaves.Contains(p))
                    {
                        if(protectedLeaves.Add(p))while(true)
                        {
                            if(!next.Add(p)||p.z>=N)break;
                            int size=p.z*2;p=new Vector3Int(p.x/size*size,p.y/size*size,size);
                        }
                    }
                    else
                    {
                        if(p.z<=1)throw new InvalidOperationException("Incomplete rock terrain LOD0 plan.");
                        int half=p.z/2;
                        stack.Push(new Vector3Int(p.x,p.y,half));stack.Push(new Vector3Int(p.x+half,p.y,half));
                        stack.Push(new Vector3Int(p.x,p.y+half,half));stack.Push(new Vector3Int(p.x+half,p.y+half,half));
                    }
                    if(++visits>=256||watch.Elapsed.TotalMilliseconds>=2){visits=0;yield return LTBuildStep.Working;watch.Restart();}
                }
            }
            region=rect;BoundaryCellCount=edgeCells.Count;ProtectedCellCount=protectedLeaves.Count;SampleCount=samples.Count;blocked=next;
        }
        internal bool Protects(Rect area)
        {
            if(blocked==null)throw new InvalidOperationException("Rock terrain LOD protection not prepared.");
            int size=Mathf.RoundToInt(area.width/region.width*N);if(size<1)return false;
            int x=Mathf.RoundToInt((area.xMin-region.xMin)/region.width*N/size)*size;
            int z=Mathf.RoundToInt((area.yMin-region.yMin)/region.height*N/size)*size;
            return blocked.Contains(new Vector3Int(x,z,size));
        }
    }
}
