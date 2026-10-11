using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Per accepted chunk revision, not per camera update. Capture cut transitions
    // on the actual LOD0 lattice, including every possible stitch/centre sample.
    // Mixed leaves initially veto merging. Ancestors can be released only after
    // all transition stencils pass the contour topology and XZ-error checks.
    // Uniform kept ground below a cutter's projection may coarsen normally.
    // The published lookup retains no height delegates or Unity source objects.
    internal sealed class LTHoleVolumeLODProtection
    {
        const int N=LTSpatialLODMath.N;
        HashSet<Vector3Int> blocked;
        Rect region;
        bool started;
        internal int BoundaryCellCount {get;private set;}
        internal int SampleCount {get;private set;}
        internal int ReleasedMergeCount {get;private set;}

        internal IEnumerable<LTBuildStep> Prepare(IReadOnlyList<LTHoleStampSnapshot> holes,
            IReadOnlyList<Vector3Int> fine,Rect rect,Func<float,float,float> height,Func<Vector3Int,int> baseMask=null,
            Func<Rect,bool> regularTopology=null,IReadOnlyList<LTHoleStampSnapshot> otherHoles=null)
        {
            if(started)throw new InvalidOperationException("Volume LOD protection already started.");
            started=true;
            if(rect.width<=0||rect.height<=0||height==null)throw new ArgumentException("Invalid volume LOD input.");
            var next=new HashSet<Vector3Int>();var samples=new Dictionary<Vector2Int,bool>();
            var references=new Dictionary<Vector3Int,List<LTHoleContourSampler.Segment>>();var uncertain=new HashSet<Vector3Int>();
            float maxSize=0;
            if(baseMask!=null)foreach(var hole in holes)if(hole.LODContourError>0)maxSize=Mathf.Max(maxSize,hole.LODEdgeCellSize);
            int boundaryCount=0,visits=0;var watch=Stopwatch.StartNew();
            bool CutPosition(Vector2 position)
            {
                float x=rect.xMin+position.x,z=rect.yMin+position.y;
                float y=0;bool sampled=false;
                foreach(var hole in holes)
                {
                    if(!hole.RequiresHeight)continue;
                    var bounds=hole.Bounds;
                    if(x<bounds.xMin||x>bounds.xMax||z<bounds.yMin||z>bounds.yMax)continue;
                    if(!sampled)
                    {
                        y=height(x,z);sampled=true;
                        if(!LTHoleMeshData.Finite(y))throw new InvalidOperationException("Non-finite volume LOD height. Previous mesh preserved.");
                    }
                    if(hole.Contains(x,y,z))return true;
                }
                return false;
            }
            bool Cut(int twiceX,int twiceZ)
            {
                var key=new Vector2Int(twiceX,twiceZ);
                if(samples.TryGetValue(key,out bool value))return value;
                value=CutPosition(new Vector2(twiceX*.5f/N*rect.width,twiceZ*.5f/N*rect.height));
                samples.Add(key,value);return value;
            }
            var contours=new LTHoleContourSampler(rect,Cut,CutPosition);
            Rect Area(Vector3Int p)=>new Rect(rect.xMin+p.x/(float)N*rect.width,rect.yMin+p.y/(float)N*rect.height,
                p.z/(float)N*rect.width,p.z/(float)N*rect.height);
            foreach(var cell in fine)
            {
                var area=new Rect(rect.xMin+cell.x/(float)N*rect.width,rect.yMin+cell.y/(float)N*rect.height,
                    cell.z/(float)N*rect.width,cell.z/(float)N*rect.height);
                bool candidate=false;
                foreach(var hole in holes)if(hole.RequiresHeight&&hole.Intersects(area)){candidate=true;break;}
                if(candidate)
                {
                    bool first=Cut(cell.x*2,cell.y*2),mixed=false;
                    for(int z=0;z<3&&!mixed;z++)for(int x=0;x<3;x++)
                        if(Cut(cell.x*2+x*cell.z,cell.y*2+z*cell.z)!=first){mixed=true;break;}
                    if(mixed)
                    {
                        boundaryCount++;var p=cell;
                        while(true)
                        {
                            if(!next.Add(p)||p.z>=N)break;
                            int size=p.z*2;p=new Vector3Int(p.x/size*size,p.y/size*size,size);
                        }
                        if(maxSize>0)
                        {
                            var contour=contours.Segments(cell,baseMask(cell),regularTopology?.Invoke(area)??false);
                            p=cell;
                            while(p.z<N)
                            {
                                int size=p.z*2;p=new Vector3Int(p.x/size*size,p.y/size*size,size);
                                var parentArea=Area(p);if(Mathf.Max(parentArea.width,parentArea.height)>maxSize)break;
                                if(contour.Count==0){uncertain.Add(p);continue;}
                                if(!references.TryGetValue(p,out var list))references.Add(p,list=new List<LTHoleContourSampler.Segment>());
                                if(list.Count<=256)list.AddRange(contour);
                            }
                        }
                    }
                }
                if(++visits>=256||watch.Elapsed.TotalMilliseconds>=2)
                {visits=0;yield return LTBuildStep.Working;watch.Restart();}
            }
            int released=0;
            foreach(var pair in references)
            {
                var area=Area(pair.Key);float limit=float.PositiveInfinity,error=float.PositiveInfinity;
                foreach(var hole in holes)if(hole.Intersects(area))
                {limit=Mathf.Min(limit,hole.LODEdgeCellSize);error=Mathf.Min(error,hole.LODContourError);}
                bool nearby=false;
                if(otherHoles!=null&&LTHoleMeshData.Finite(error))foreach(var hole in otherHoles)
                    if(hole.Intersects(Rect.MinMaxRect(area.xMin-error,area.yMin-error,area.xMax+error,area.yMax+error))){nearby=true;break;}
                if(!nearby&&!uncertain.Contains(pair.Key)&&Mathf.Max(area.width,area.height)<=limit&&
                    error>Mathf.Max(rect.width,rect.height)*.000001f&&LTHoleMeshData.Finite(error)&&
                    contours.CanMerge(pair.Key,pair.Value,error,regularTopology?.Invoke(area)??false))
                {next.Remove(pair.Key);released++;}
                if(++visits>=64||watch.Elapsed.TotalMilliseconds>=2)
                {visits=0;yield return LTBuildStep.Working;watch.Restart();}
            }
            region=rect;BoundaryCellCount=boundaryCount;SampleCount=samples.Count;ReleasedMergeCount=released;blocked=next;
        }
        // Callers supply quadtree cell rectangles from this chunk. Snap the
        // origin to that cell's size, avoiding global-coordinate roundoff.
        internal bool Protects(Rect area)
        {
            if(blocked==null)throw new InvalidOperationException("Volume LOD protection not prepared.");
            int size=Mathf.RoundToInt(area.width/region.width*N);
            if(size<1)return false;
            int x=Mathf.RoundToInt((area.xMin-region.xMin)/region.width*N/size)*size;
            int z=Mathf.RoundToInt((area.yMin-region.yMin)/region.height*N/size)*size;
            return blocked.Contains(new Vector3Int(x,z,size));
        }
    }
}
