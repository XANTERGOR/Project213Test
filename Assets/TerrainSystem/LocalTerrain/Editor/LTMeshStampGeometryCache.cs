using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal readonly struct LTMeshStampGeometryKey : IEquatable<LTMeshStampGeometryKey>
    {
        internal readonly int mesh, revision, vertices, submeshes;
        internal readonly long indices;
        internal readonly Matrix4x4 matrix;
        internal readonly string asset;
        internal LTMeshStampGeometryKey(int mesh,int revision,int vertices,int submeshes,long indices,Matrix4x4 matrix,string asset)
        {this.mesh=mesh;this.revision=revision;this.vertices=vertices;this.submeshes=submeshes;this.indices=indices;this.matrix=matrix;this.asset=asset;}
        public bool Equals(LTMeshStampGeometryKey other)=>mesh==other.mesh&&revision==other.revision&&vertices==other.vertices&&
            submeshes==other.submeshes&&indices==other.indices&&matrix.Equals(other.matrix)&&string.Equals(asset,other.asset,StringComparison.Ordinal);
    }

    // Owned snapshots. Consumers must not mutate the arrays: an in-flight build
    // may still hold the previous snapshot when a stamp moves or is removed.
    internal sealed class LTMeshStampGeometry
    {
        readonly Vector3[] sourceVertices;
        internal readonly Vector3[] vertices;
        internal readonly int[] triangles,contactTriangles,boundaryEdges;
        internal readonly Vector3 min,max;
        internal readonly ulong contentHash;
        internal long Bytes=>(long)(sourceVertices.Length+vertices.Length)*12+
            (long)(triangles.Length+contactTriangles.Length+boundaryEdges.Length)*4;
        LTMeshStampGeometry(Vector3[] source,Vector3[] vertices,int[] triangles,int[] contact,int[] edges,Vector3 min,Vector3 max,ulong hash)
        {sourceVertices=source;this.vertices=vertices;this.triangles=triangles;contactTriangles=contact;boundaryEdges=edges;this.min=min;this.max=max;contentHash=hash;}
        internal bool SameSource(IReadOnlyList<Vector3> positions,IReadOnlyList<int> indices)
        {
            if(positions.Count!=sourceVertices.Length||indices.Count!=triangles.Length)return false;
            for(int i=0;i<positions.Count;i++)if(!positions[i].Equals(sourceVertices[i]))return false;
            for(int i=0;i<indices.Count;i++)if(indices[i]!=triangles[i])return false;
            return true;
        }
        internal static LTMeshStampGeometry Build(Vector3[] positions,int[] indices,Matrix4x4 matrix)
        {
            var source=(Vector3[])positions.Clone();var triangles=(int[])indices.Clone();
            var vertices=new Vector3[source.Length];
            Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),max=new Vector3(float.MinValue,float.MinValue,float.MinValue);
            ulong hash=14695981039346656037UL;
            void Mix(int value){unchecked{hash=(hash^(uint)value)*1099511628211UL;}}
            Mix(source.Length);Mix(triangles.Length);
            for(int i=0;i<vertices.Length;i++)
            {
                var p=source[i];Mix(BitConverter.SingleToInt32Bits(p.x));Mix(BitConverter.SingleToInt32Bits(p.y));Mix(BitConverter.SingleToInt32Bits(p.z));
                vertices[i]=matrix.MultiplyPoint3x4(p);min=Vector3.Min(min,vertices[i]);max=Vector3.Max(max,vertices[i]);
            }
            foreach(int index in triangles)Mix(index);
            var contact=new List<int>();var counts=new Dictionary<long,int>();var edgeVertices=new Dictionary<long,Vector2Int>();
            var weldedIds=new int[vertices.Length];var weldedByPosition=new Dictionary<Vector3Int,int>();int nextId=0;
            for(int i=0;i<vertices.Length;i++)
            {
                var key=new Vector3Int(Mathf.RoundToInt(vertices[i].x*1000),Mathf.RoundToInt(vertices[i].y*1000),Mathf.RoundToInt(vertices[i].z*1000));
                if(!weldedByPosition.TryGetValue(key,out int id)){id=nextId++;weldedByPosition.Add(key,id);}weldedIds[i]=id;
            }
            void Edge(int a,int b)
            {
                int wa=weldedIds[a],wb=weldedIds[b];long key=((long)Math.Min(wa,wb)<<32)|(uint)Math.Max(wa,wb);
                counts[key]=counts.TryGetValue(key,out int count)?count+1:1;edgeVertices[key]=new Vector2Int(a,b);
            }
            for(int i=0;i+2<triangles.Length;i+=3)
            {
                int a=triangles[i],b=triangles[i+1],c=triangles[i+2];
                if(Vector3.Cross(vertices[b]-vertices[a],vertices[c]-vertices[a]).y>=-.000001f)continue;
                contact.Add(a);contact.Add(b);contact.Add(c);Edge(a,b);Edge(b,c);Edge(c,a);
            }
            var edges=new List<int>();foreach(var pair in counts)if(pair.Value==1)
            {var edge=edgeVertices[pair.Key];edges.Add(edge.x);edges.Add(edge.y);}
            return new LTMeshStampGeometry(source,vertices,triangles,contact.ToArray(),edges.ToArray(),min,max,hash);
        }
    }

    internal enum LTMeshStampGeometryLookup { Miss, Hit, Audit }
    internal sealed class LTMeshStampGeometryCache
    {
        sealed class Entry {internal LTMeshStampGeometryKey key;internal LTMeshStampGeometry data;internal double auditAt;internal long seen;}
        readonly Dictionary<int,Entry> entries=new Dictionary<int,Entry>();
        readonly List<int> removed=new List<int>();
        readonly long budget;long epoch;
        internal long Bytes{get;private set;}
        internal int Count=>entries.Count;
        internal const double AuditSeconds=1;
        internal LTMeshStampGeometryCache(long budget=64L*1024*1024){this.budget=Math.Max(0,budget);}
        internal void Begin(){epoch++;}
        internal LTMeshStampGeometryLookup Lookup(int owner,LTMeshStampGeometryKey key,double now,out LTMeshStampGeometry data)
        {
            data=null;
            if(!entries.TryGetValue(owner,out var entry))return LTMeshStampGeometryLookup.Miss;
            entry.seen=epoch;
            if(!entry.key.Equals(key)){Remove(owner);return LTMeshStampGeometryLookup.Miss;}
            data=entry.data;
            return now>=entry.auditAt?LTMeshStampGeometryLookup.Audit:LTMeshStampGeometryLookup.Hit;
        }
        internal void ConfirmAudit(int owner,double now){if(entries.TryGetValue(owner,out var entry))entry.auditAt=now+AuditSeconds;}
        internal void Store(int owner,LTMeshStampGeometryKey key,LTMeshStampGeometry data,double now)
        {
            Remove(owner);
            if(data.Bytes>budget-Bytes)return; // Oversized snapshots still work, but are not retained here.
            entries.Add(owner,new Entry{key=key,data=data,auditAt=now+AuditSeconds,seen=epoch});Bytes+=data.Bytes;
        }
        internal void Remove(int owner)
        {if(entries.TryGetValue(owner,out var entry)){Bytes-=entry.data.Bytes;entries.Remove(owner);}}
        internal void End()
        {
            removed.Clear();foreach(var pair in entries)if(pair.Value.seen!=epoch)removed.Add(pair.Key);
            foreach(int owner in removed)Remove(owner);removed.Clear();
        }
        internal void Clear(){entries.Clear();removed.Clear();Bytes=0;}
    }
}
