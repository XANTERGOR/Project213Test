using System;
using System.Collections.Generic;
using UnityEngine;
namespace LocalTerrainPrototype
{
    // Immutable revision shared by LOD0, deferred LOD preparation and cuts.
    // Shape/mask holes use XZ; volume holes test the actual heightfield point.
    // Neither mode generates a floor or side walls.
    internal sealed class LTHoleStampSnapshot
    {
        readonly Matrix4x4 inverse;
        readonly Vector2 size;
        readonly LTStampMesh.Zone footprint;
        readonly LTStampDensityMask mask;
        readonly Vector2[] corners;
        readonly LTStampShape shape;
        readonly float threshold;
        readonly LTHoleMeshVolume volume;
        public bool RequiresHeight=>volume!=null;
        public readonly Rect Bounds,ProtectedBounds;
        public readonly float CellSize,LODEdgeCellSize,LODCornerError,LODContourError;
        public LTHoleStampSnapshot(LTHoleMeshVolume volume,float cellSize,float lodEdgeCellSize=0,float lodContourError=0)
        {
            if(volume==null||!Positive(cellSize))throw new ArgumentException("Hole Mesh: некорректный объём или Edge Cell Size.");
            this.volume=volume;Bounds=volume.BoundsXZ;
            CellSize=Mathf.Min(cellSize,Mathf.Min(Bounds.width,Bounds.height)/8);
            if(!Positive(CellSize))throw new ArgumentException("Hole Mesh: слишком малый объём.");
            if(!LTHoleMeshData.Finite(lodEdgeCellSize)||lodEdgeCellSize<0||!LTHoleMeshData.Finite(lodContourError)||lodContourError<0)
                throw new ArgumentException("Hole Mesh: LOD cell size and contour error must be finite and non-negative.");
            LODEdgeCellSize=lodEdgeCellSize;LODContourError=lodContourError;LODCornerError=0;
            ProtectedBounds=Rect.MinMaxRect(Bounds.xMin-2*CellSize,Bounds.yMin-2*CellSize,Bounds.xMax+2*CellSize,Bounds.yMax+2*CellSize);
        }
        public LTHoleStampSnapshot(Matrix4x4 matrix,Matrix4x4 inverse,Vector2 size,LTStampShape shape,float cellSize,
            LTStampDensityMask mask=null,float threshold=.5f,float lodEdgeCellSize=4,float lodCornerError=1)
        {
            if(!Positive(size.x)||!Positive(size.y)||!Positive(cellSize)||float.IsNaN(threshold)||threshold<.001f||threshold>1)
                throw new ArgumentException("Hole Stamp: use positive finite Size/Edge Cell Size and a threshold in 0.001..1.");
            this.inverse=inverse;this.size=size;this.mask=mask;this.threshold=threshold;
            if(float.IsNaN(lodEdgeCellSize)||float.IsInfinity(lodEdgeCellSize)||lodEdgeCellSize<0)
                throw new ArgumentException("Hole Stamp: LOD Edge Cell Size must be finite and non-negative.");
            if(float.IsNaN(lodCornerError)||float.IsInfinity(lodCornerError)||lodCornerError<0)
                throw new ArgumentException("Hole Stamp: LOD Corner Error must be finite and non-negative.");
            this.shape=shape;corners=new Vector2[4];
            Vector3 lo=new Vector3(float.MaxValue,0,float.MaxValue),hi=new Vector3(float.MinValue,0,float.MinValue);
            for(int i=0;i<4;i++)
            {
                var p=matrix.MultiplyPoint3x4(new Vector3((i&1)==0?-size.x*.5f:size.x*.5f,0,(i&2)==0?-size.y*.5f:size.y*.5f));
                corners[i]=new Vector2(p.x,p.z);
                lo.x=Mathf.Min(lo.x,p.x);lo.z=Mathf.Min(lo.z,p.z);hi.x=Mathf.Max(hi.x,p.x);hi.z=Mathf.Max(hi.z,p.z);
            }
            Bounds=Rect.MinMaxRect(lo.x,lo.z,hi.x,hi.z);
            var axisX=matrix.MultiplyVector(Vector3.right)*size.x;var axisZ=matrix.MultiplyVector(Vector3.forward)*size.y;
            // Minimum singular axis also handles nested, non-uniformly scaled
            // Y rotations (sheared footprints), not just two vector lengths.
            double a=(double)axisX.x*axisX.x+(double)axisX.z*axisX.z;
            double b=(double)axisZ.x*axisZ.x+(double)axisZ.z*axisZ.z;
            double dot=(double)axisX.x*axisZ.x+(double)axisX.z*axisZ.z;
            double determinant=(double)axisX.x*axisZ.z-(double)axisX.z*axisZ.x;
            double largest=a+b+Math.Sqrt((a-b)*(a-b)+4*dot*dot);
            float span=(float)Math.Sqrt(2*determinant*determinant/largest);
            if(!Positive(span))throw new ArgumentException("Hole Stamp: invalid transform scale.");
            // A sub-cell shape still needs interior samples to become a hole.
            CellSize=Mathf.Min(cellSize,span/8);
            // Straight sides do not need eight cells across the opening. Keep
            // that curvature guard for ellipses; rectangles can reach two.
            LODEdgeCellSize=Mathf.Min(lodEdgeCellSize,span/(shape==LTStampShape.Rectangle?2:8));
            LODCornerError=Mathf.Min(lodCornerError,span/16);
            if(!Positive(CellSize))throw new ArgumentException("Hole Stamp: invalid transform scale.");
            ProtectedBounds=Rect.MinMaxRect(Bounds.xMin-2*CellSize,Bounds.yMin-2*CellSize,Bounds.xMax+2*CellSize,Bounds.yMax+2*CellSize);
            footprint=new LTStampMesh.Zone{bounds=Bounds,inverse=inverse,size=size,shape=shape,cellSize=CellSize,edgeCellSize=CellSize};
        }
        static bool Positive(float v)=>v>0&&!float.IsInfinity(v);
        Vector2 UV(float x,float z)
        {var p=inverse.MultiplyPoint3x4(new Vector3(x,0,z));return new Vector2(p.x/size.x+.5f,p.z/size.y+.5f);}
        public bool Contains(float x,float z)
        {
            if(volume!=null)throw new InvalidOperationException("Mesh Volume hole requires terrain height.");
            if(!footprint.Contains(x,z))return false;
            if(mask==null)return true;
            var uv=UV(x,z);return mask.Sample(uv.x,uv.y)>=threshold;
        }
        public bool Intersects(Rect area)
        {
            if(volume!=null)return volume.Intersects(area);
            if(!footprint.Intersects(area))return false;
            if(mask==null)return true;
            var lo=new Vector2(float.MaxValue,float.MaxValue);var hi=new Vector2(float.MinValue,float.MinValue);
            for(int i=0;i<4;i++)
            {var uv=UV((i&1)==0?area.xMin:area.xMax,(i&2)==0?area.yMin:area.yMax);lo=Vector2.Min(lo,uv);hi=Vector2.Max(hi,uv);}
            return mask.Maximum(Rect.MinMaxRect(lo.x,lo.y,hi.x,hi.y))>=threshold;
        }
        // This is a merge veto, not a blanket LOD0 lock. The normal LOD step and
        // height-error budgets still govern when eligible cells are merged.
        public bool ProtectsLOD(Rect area)
        {
            // Conservative fallback for callers without a heightfield. Production
            // LOD paths replace this with per-chunk LTHoleVolumeLODProtection.
            if(volume!=null)return volume.Intersects(area);
            if(LODEdgeCellSize==0)
            {
                float pad=2*CellSize;
                return Intersects(Rect.MinMaxRect(area.xMin-pad,area.yMin-pad,area.xMax+pad,area.yMax+pad));
            }
            if(!Intersects(area))return false;
            // Do not lose thin/disconnected white islands or black islands in a
            // texture. Only their actual cut cells are fixed, not a padded ring.
            if(mask!=null)return true;
            if(Mathf.Max(area.width,area.height)>LODEdgeCellSize)return true;
            // Check the contour produced by clipping, not the size of its cell.
            // A large cell can retain an accurate corner; a small one can bevel
            // it badly. Check all stitch/diagonal variants before allowing merge.
            if(shape==LTStampShape.Rectangle)foreach(var corner in corners)
                if(corner.x>=area.xMin&&corner.x<=area.xMax&&corner.y>=area.yMin&&corner.y<=area.yMax&&
                    !CornerFits(area,corner))return true;
            return false;
        }
        bool CornerFits(Rect area,Vector2 corner)
        {
            if(LODCornerError==0)return false;
            var points=new[]{new Vector2(area.xMin,area.yMin),new Vector2(area.xMin,area.center.y),
                new Vector2(area.xMin,area.yMax),new Vector2(area.center.x,area.yMax),
                new Vector2(area.xMax,area.yMax),new Vector2(area.xMax,area.center.y),
                new Vector2(area.xMax,area.yMin),new Vector2(area.center.x,area.yMin),area.center};
            var uv=new Vector2[9];var inside=new bool[9];
            for(int i=0;i<9;i++){uv[i]=UV(points[i].x,points[i].y);inside[i]=uv[i].x>=0&&uv[i].x<=1&&uv[i].y>=0&&uv[i].y<=1;}
            // Cache the tiny, numeric nine-point stencil locally. No mutable
            // cache or Unity object is retained in the immutable hole snapshot.
            var hits=new Vector2[81];var hasHit=new bool[81];
            Vector2 Hit(int a,int b)
            {
                if(a>b){int swap=a;a=b;b=swap;}int key=a*9+b;
                if(hasHit[key])return hits[key];
                var p=uv[a];var d=uv[b]-p;double lo=0,hi=1;
                for(int axis=0;axis<2;axis++)
                {
                    double delta=axis==0?d.x:d.y,origin=axis==0?p.x:p.y;
                    if(delta==0)continue;
                    double t0=-origin/delta,t1=(1-origin)/delta;
                    lo=Math.Max(lo,Math.Min(t0,t1));hi=Math.Min(hi,Math.Max(t0,t1));
                }
                hits[key]=Vector2.Lerp(points[a],points[b],(float)(inside[a]?hi:lo));hasHit[key]=true;return hits[key];
            }
            float Triangle(int a,int b,int c)
            {
                if(inside[a]==inside[b]&&inside[b]==inside[c])return float.PositiveInfinity;
                Vector2 p,q;
                if(inside[a]==inside[b]){p=Hit(a,c);q=Hit(b,c);}
                else if(inside[b]==inside[c]){p=Hit(a,b);q=Hit(a,c);}
                else {p=Hit(a,b);q=Hit(b,c);}
                var d=q-p;float t=d.sqrMagnitude>0?Mathf.Clamp01(Vector2.Dot(corner-p,d)/d.sqrMagnitude):0;
                return (corner-p-d*t).sqrMagnitude;
            }
            float limit=LODCornerError*LODCornerError;
            if(Mathf.Min(Triangle(0,2,4),Triangle(4,6,0))>limit||Mathf.Min(Triangle(0,2,6),Triangle(6,2,4))>limit)return false;
            var edge=new List<int>(8);
            for(int mask=1;mask<16;mask++)
            {
                edge.Clear();for(int i=0;i<8;i++)if((i&1)==0||(mask&(1<<((i-1)/2)))!=0)edge.Add(i);
                float distance=float.PositiveInfinity;
                for(int i=0;i<edge.Count;i++)distance=Mathf.Min(distance,Triangle(8,edge[i],edge[(i+1)%edge.Count]));
                if(distance>limit)return false;
                // The road projection policy can replace centre fans with a
                // regular triangulation. Its variants need the same guarantee.
                var polygon=new Vector2[edge.Count];for(int i=0;i<edge.Count;i++)polygon[i]=points[edge[i]]-area.min;
                var indices=LTPaintMath.TriangulateTransition(polygon);distance=float.PositiveInfinity;
                for(int i=0;i<indices.Length;i+=3)distance=Mathf.Min(distance,Triangle(edge[indices[i]],edge[indices[i+1]],edge[indices[i+2]]));
                if(indices.Length>0&&distance>limit)return false;
            }
            return true;
        }
        public static bool ProtectsAnyLOD(IReadOnlyList<LTHoleStampSnapshot> holes,Rect area)
        {
            int nearby=0;
            foreach(var hole in holes)
            {
                if(hole.ProtectsLOD(area))return true;
                // Two nearby holes can leave a narrow bridge even when either
                // footprint alone is simple. Keep its original topology.
                float pad=Mathf.Max(hole.CellSize,hole.LODEdgeCellSize);
                if(hole.Intersects(Rect.MinMaxRect(area.xMin-pad,area.yMin-pad,area.xMax+pad,area.yMax+pad))&&++nearby>1)return true;
            }
            return false;
        }
        // Never claims full ownership: cut refinement combines with finer zones.
        public bool Contains(float x,float height,float z)=>volume!=null?volume.Contains(x,height,z):Contains(x,z);
        public LTStampMesh.Zone CreateZone()=>new LTStampMesh.Zone{bounds=Bounds,cellSize=CellSize,edgeCellSize=CellSize,
            customCellSize=(x,z)=>CellSize,coverageIntersects=Intersects};
    }
}
