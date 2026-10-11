using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Main-thread snapshot packing only. Published sources/snapshots are immutable;
    // jobs still own independent native copies. Eviction never changes a reader.
    public sealed class LTPaintWeightTerrainCache
    {
        public const long PayloadLimit=16L*1024*1024;
        public const int EntryLimit=4;
        public sealed class Source
        {
            // Entries retain this small token, not old mesh arrays/bin lists.
            public readonly object identity=new object();
            public readonly Rect rect;
            public readonly int axis;
            public readonly Vector3[] vertices,normals;
            public readonly int[] indices;
            public readonly List<int>[] bins;
            public readonly long binReferences;
            public Source(Rect rect,int axis,Vector3[] vertices,Vector3[] normals,int[] indices,List<int>[] bins)
            {
                this.rect=rect;this.axis=axis;this.vertices=vertices;this.normals=normals;this.indices=indices;this.bins=bins;
                foreach(var bin in bins)if(bin!=null)binReferences+=bin.Count;
            }
        }
        // Bins depend on XZ and triangle order, not height or shading normals.
        // Compare exact components (Vector3.operator== is approximate). Never
        // mutate a published source: outstanding regional snapshots may use it.
        public static Source BuildSource(Rect rect,Vector3[] vertices,Vector3[] normals,int[] indices,Source previous,
            out bool reusedBins,out bool reusedSurface)
        {
            int axis=LTPaintMath.TerrainBinAxis(indices.Length/3);
            reusedBins=previous!=null&&previous.rect.Equals(rect)&&previous.axis==axis&&
                previous.vertices.Length==vertices.Length&&previous.indices.Length==indices.Length;
            if(reusedBins)
            {
                for(int i=0;i<indices.Length;i++)if(indices[i]!=previous.indices[i]){reusedBins=false;break;}
                for(int i=0;reusedBins&&i<vertices.Length;i++)
                    if(!SameBits(vertices[i].x,previous.vertices[i].x)||!SameBits(vertices[i].z,previous.vertices[i].z))reusedBins=false;
            }
            reusedSurface=reusedBins&&normals.Length==previous.normals.Length;
            if(reusedSurface)
            {
                for(int i=0;i<vertices.Length;i++)if(!SameBits(vertices[i].y,previous.vertices[i].y)){reusedSurface=false;break;}
                for(int i=0;reusedSurface&&i<normals.Length;i++)
                    if(!SameBits(normals[i].x,previous.normals[i].x)||!SameBits(normals[i].y,previous.normals[i].y)||!SameBits(normals[i].z,previous.normals[i].z))reusedSurface=false;
            }
            if(reusedSurface)return previous;
            var bins=reusedBins?previous.bins:new List<int>[axis*axis];
            if(!reusedBins)for(int i=0;i+2<indices.Length;i+=3)
            {
                var range=LTPaintMath.TerrainTriangleBins(rect,vertices[indices[i]],vertices[indices[i+1]],vertices[indices[i+2]],axis);
                for(int z=range.yMin;z<range.yMax;z++)for(int x=range.xMin;x<range.xMax;x++)
                {int id=z*axis+x;if(bins[id]==null)bins[id]=new List<int>();bins[id].Add(i);}
            }
            return new Source(rect,axis,vertices,normals,indices,bins);
        }
        static bool SameBits(float a,float b)=>new FloatBits{value=a}.bits==new FloatBits{value=b}.bits;
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)]public float value;
            [System.Runtime.InteropServices.FieldOffset(0)]public int bits;
        }
        sealed class Entry
        {
            public RectInt range;
            public LTPaintWeightMath.Terrain layout;
            public object[] identities;
            public LTPaintTerrainSnapshot snapshot;
            public long bytes;
        }
        readonly List<Entry> entries=new List<Entry>(); // oldest first
        public int Count=>entries.Count;
        public long RetainedBytes {get;private set;}
        public void Clear(){entries.Clear();RetainedBytes=0;}
        void Remove(int i){RetainedBytes-=entries[i].bytes;entries.RemoveAt(i);}
        static bool SameLayout(LTPaintWeightMath.Terrain a,LTPaintWeightMath.Terrain b)=>
            a.countX==b.countX&&a.countZ==b.countZ&&a.sizeX.Equals(b.sizeX)&&a.sizeZ.Equals(b.sizeZ)&&a.width.Equals(b.width)&&a.depth.Equals(b.depth)&&a.minX.Equals(b.minX)&&a.minZ.Equals(b.minZ);
        public bool TryGet(LTPaintWeightMath.Terrain layout,RectInt range,Func<int,int,Source> lookup,
            out LTPaintTerrainSnapshot snapshot,out bool reused)
        {
            snapshot=null;reused=false;
            long tileCount=(long)layout.countX*layout.countZ;
            if(layout.countX<=0||layout.countZ<=0||tileCount>PayloadLimit/64||
                range.xMin<0||range.yMin<0||(long)range.xMin+range.width>layout.countX||
                (long)range.yMin+range.height>layout.countZ||range.width<=0||range.height<=0)return false;
            var sources=new Source[range.width*range.height];int at=0;
            for(int z=range.yMin;z<range.yMax;z++)for(int x=range.xMin;x<range.xMax;x++)sources[at++]=lookup(x,z);
            for(int i=entries.Count-1;i>=0;i--)
            {
                var e=entries[i];
                if(!SameLayout(layout,e.layout)){Remove(i);continue;}
                if(!e.range.Equals(range))continue;
                bool match=true;
                for(int j=0;j<sources.Length;j++)if(!ReferenceEquals(e.identities[j],sources[j]?.identity)){match=false;break;}
                if(!match){Remove(i);continue;}
                entries.RemoveAt(i);entries.Add(e);snapshot=e.snapshot;reused=true;return true;
            }
            long vertexCount=0,indexCount=0,binCount=0,triangleCount=0;
            foreach(var s in sources)if(s!=null)
            {vertexCount+=s.vertices.Length;indexCount+=s.indices.Length;binCount+=s.bins.Length;triangleCount+=s.binReferences;}
            long bytes=tileCount*64+vertexCount*24+indexCount*4+binCount*8+triangleCount*4;
            if(bytes>PayloadLimit)return false;
            // Budget includes the lightweight dependency tokens. Do not retain
            // source meshes, NativeArrays or an unbounded history of revisions.
            long retained=bytes+(long)sources.Length*IntPtr.Size;
            while(entries.Count>0&&(entries.Count>=EntryLimit||RetainedBytes+retained>PayloadLimit))Remove(0);
            var result=new LTPaintTerrainSnapshot{terrain=layout,tiles=new LTPaintWeightMath.Tile[(int)tileCount],
                vertices=new Vector3[(int)vertexCount],normals=new Vector3[(int)vertexCount],indices=new int[(int)indexCount],
                bins=new Vector2Int[(int)binCount],triangles=new int[(int)triangleCount]};
            int vertexAt=0,indexAt=0,binAt=0,triangleAt=0;at=0;
            var identities=new object[sources.Length];
            for(int z=range.yMin;z<range.yMax;z++)for(int x=range.xMin;x<range.xMax;x++)
            {
                var s=sources[at];identities[at++]=s?.identity;if(s==null)continue;
                bool hasNormals=s.normals.Length==s.vertices.Length;
                result.tiles[z*layout.countX+x]=new LTPaintWeightMath.Tile{valid=1,rect=s.rect,axis=s.axis,firstBin=binAt,hasNormals=hasNormals?1:0};
                Array.Copy(s.vertices,0,result.vertices,vertexAt,s.vertices.Length);
                if(hasNormals)Array.Copy(s.normals,0,result.normals,vertexAt,s.normals.Length);
                for(int i=0;i<s.indices.Length;i++)result.indices[indexAt+i]=vertexAt+s.indices[i];
                foreach(var bin in s.bins)
                {
                    result.bins[binAt++]=new Vector2Int(triangleAt,bin?.Count??0);
                    if(bin!=null)for(int i=0;i<bin.Count;i++)result.triangles[triangleAt++]=indexAt+bin[i];
                }
                vertexAt+=s.vertices.Length;indexAt+=s.indices.Length;
            }
            snapshot=result;
            if(retained<=PayloadLimit)
            {entries.Add(new Entry{range=range,layout=layout,identities=identities,snapshot=result,bytes=retained});RetainedBytes+=retained;}
            return true;
        }
    }
}
