using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Stable signed authoring coordinates; dense IDs are only local topology indices.
    // Extending the envelope never changes old positions or the source height-map UVs.
    public readonly struct LTChunkGrid
    {
        public const int MaxAxis=64; // N=2^24: topology integer coordinates must not overflow.
        public readonly int minX,minZ,columns,rows;
        public readonly Vector2 cell;
        public LTChunkGrid(int minX,int minZ,int columns,int rows,Vector2 cell)
        {this.minX=minX;this.minZ=minZ;this.columns=columns;this.rows=rows;this.cell=cell;}
        public bool Valid=>columns>0&&rows>0&&columns<=MaxAxis&&rows<=MaxAxis&&
            cell.x>0&&cell.y>0&&!float.IsInfinity(cell.x)&&!float.IsInfinity(cell.y)&&
            Math.Abs((long)minX)+columns<1000000&&Math.Abs((long)minZ)+rows<1000000;
        public bool Contains(int x,int z)=>(long)x-minX>=0&&(long)x-minX<columns&&(long)z-minZ>=0&&(long)z-minZ<rows;
        public int Id(int x,int z)=>Contains(x,z)?(z-minZ)*columns+x-minX:-1;
        public Vector2Int Coordinate(int id)=>new Vector2Int(minX+id%columns,minZ+id/columns);
        public Rect Rect(int x,int z)=>new Rect(x*cell.x,z*cell.y,cell.x,cell.y);
        public Rect Bounds=>new Rect(minX*cell.x,minZ*cell.y,columns*cell.x,rows*cell.y);
        public LTChunkGrid Include(int x,int z)
        {
            long left=Math.Min(minX,x),back=Math.Min(minZ,z);
            long right=Math.Max((long)minX+columns,(long)x+1),front=Math.Max((long)minZ+rows,(long)z+1);
            if(right-left>MaxAxis||front-back>MaxAxis||Math.Abs(left)+right-left>=1000000||Math.Abs(back)+front-back>=1000000)
                throw new ArgumentOutOfRangeException(nameof(x),$"Область чанков ограничена {MaxAxis}×{MaxAxis}.");
            var result=new LTChunkGrid((int)left,(int)back,(int)(right-left),(int)(front-back),cell);
            if(!result.Valid)throw new InvalidOperationException("Некорректный размер чанка.");
            return result;
        }
    }
}
