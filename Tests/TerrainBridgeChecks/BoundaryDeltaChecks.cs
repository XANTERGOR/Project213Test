using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
partial class Checks
{
        static Vector3Int BoundaryPointKey(Vector3 p)=>new Vector3Int(Mathf.RoundToInt(p.x*1000),Mathf.RoundToInt(p.y*1000),Mathf.RoundToInt(p.z*1000));
        static (Vector3Int a,Vector3Int b) BoundaryEdgeKey(Vector3 a,Vector3 b)
        {
            var x=BoundaryPointKey(a);var y=BoundaryPointKey(b);
            bool swap=x.x>y.x||(x.x==y.x&&(x.y>y.y||(x.y==y.y&&x.z>y.z)));
            return swap?(y,x):(x,y);
        }
        sealed class BoundaryCache : Dictionary<int,ChunkBoundaryData>
        {
            public readonly Dictionary<(Vector3Int a,Vector3Int b),int> edgeTotals;
            public BoundaryCache(){edgeTotals=new Dictionary<(Vector3Int,Vector3Int),int>();}
            public BoundaryCache(BoundaryCache source):base(source)
            {edgeTotals=new Dictionary<(Vector3Int,Vector3Int),int>(source.edgeTotals);}
            public new void Clear(){base.Clear();edgeTotals.Clear();}
            void Contribute(ChunkBoundaryData data,int sign)
            {
                foreach(var edge in data.edges)
                {
                    var key=BoundaryEdgeKey(data.points[edge.a],data.points[edge.b]);
                    edgeTotals.TryGetValue(key,out int total);total+=sign*edge.count;
                    if(total<0)throw new InvalidOperationException("Negative cached boundary edge count.");
                    if(total==0)edgeTotals.Remove(key);else edgeTotals[key]=total;
                }
            }
            public void Replace(int id,ChunkBoundaryData data)
            {
                if(TryGetValue(id,out var old))Contribute(old,-1);
                Contribute(data,1);this[id]=data;
            }
            public void RemoveChunk(int id)
            {if(TryGetValue(id,out var old)){Contribute(old,-1);Remove(id);}}
        }

    static void BoundaryDeltaChecks()
    {
        var active=new Dictionary<int,ChunkBoundaryData>();var cache=new BoundaryCache();
        ChunkBoundaryData Chunk(int number,int revision)
        {
            var vertices=new List<Vector3>();var normals=new List<Vector3>();var indices=new List<int>();
            for(int z=0;z<=8;z++)for(int x=0;x<=8;x++)
            {
                vertices.Add(new Vector3(number*8+x,revision*.013f*(x>0&&x<8?1:0),z));normals.Add(Vector3.up);
            }
            for(int z=0;z<8;z++)for(int x=0;x<8;x++)
            {
                if(revision>0&&x==revision%7&&z==3)continue;
                int a=z*9+x,b=a+1,c=a+9,d=c+1;indices.AddRange(new[]{a,c,b,b,c,d});
            }
            if(revision==5)indices.AddRange(indices.Take(3).ToArray());
            return BuildChunkBoundary(vertices.ToArray(),normals.ToArray(),indices.ToArray(),Matrix4x4.identity,Matrix4x4.identity);
        }
        void Check()
        {
            var full=new BoundaryCache();
            foreach(var entry in active.OrderBy(p=>p.Key))full.Replace(entry.Key,entry.Value);
            Require(full.edgeTotals.Count==cache.edgeTotals.Count&&full.edgeTotals.All(p=>cache.edgeTotals[p.Key]==p.Value),"incremental/full edge totals differ");
            Require(cache.edgeTotals.All(p=>p.Value>0),"zero/negative edge count retained");
            var needed=new HashSet<Vector3Int>(cache.edgeTotals.Where(p=>p.Value==1).SelectMany(p=>new[]{p.Key.a,p.Key.b}));
            var all=new Dictionary<Vector3Int,(Vector3 position,Vector3 normal)>();
            var selected=new Dictionary<Vector3Int,(Vector3 position,Vector3 normal)>();
            void Add(Dictionary<Vector3Int,(Vector3 position,Vector3 normal)> into,ChunkBoundaryData d,int i)
            {
                var key=BoundaryPointKey(d.points[i]);
                if(into.TryGetValue(key,out var old))into[key]=(old.position,old.normal+d.normals[i]);
                else into[key]=(d.points[i],d.normals[i]);
            }
            foreach(var d in active.Values)
            {
                for(int i=0;i<d.points.Length;i++)Add(all,d,i);
                foreach(var key in needed)if(d.pointIndices.TryGetValue(key,out var uses))
                    foreach(int i in uses)Add(selected,d,i);
            }
            Require(selected.Count==needed.Count&&needed.All(k=>selected[k].Equals(all[k])),"filtered boundary points/normals differ from full merge");
        }
        for(int i=0;i<9;i++){active[i]=Chunk(i,0);cache.Replace(i,active[i]);}Check();
        var normalsProposal=new BoundaryCache(cache);
        var original=cache[0];
        var refreshed=original.WithNormals(Enumerable.Repeat(Vector3.right,original.points.Length).ToArray(),42,Matrix4x4.identity);
        normalsProposal[0]=refreshed;
        Require(ReferenceEquals(refreshed.edges,original.edges)&&ReferenceEquals(refreshed.points,original.points)&&ReferenceEquals(refreshed.pointIndices,original.pointIndices),"normal refresh rebuilt geometry");
        Require(!ReferenceEquals(refreshed.normals,original.normals)&&original.normals.All(v=>v==Vector3.up),"normal refresh mutated committed data");
        Require(refreshed.normals.All(v=>v==Vector3.right)&&refreshed.dirtyVersion==42&&original.dirtyVersion==0,"normal refresh version/data mismatch");
        Require(normalsProposal.edgeTotals.Count==cache.edgeTotals.Count&&cache.edgeTotals.All(p=>normalsProposal.edgeTotals[p.Key]==p.Value),"normal refresh changed edge totals");
        for(int revision=1;revision<=30;revision++)
        {
            int id=revision%9;
            var proposed=new BoundaryCache(cache);var next=Chunk(id,revision);
            proposed.Replace(id,next);
            // Simulate a rejected rebuild: discarding proposal must preserve committed totals.
            Check();Require(ReferenceEquals(cache[id],active[id]),"proposal modified committed entry");
            cache=proposed;active[id]=next;Check();
            if(revision%3==0){cache.RemoveChunk(id);active.Remove(id);Check();cache.Replace(id,next);active[id]=next;Check();}
        }
        cache.Clear();active.Clear();Check();
        Console.WriteLine("PASS incremental boundary cache: 30 partial edits, discarded proposals, removal/reinsert, non-manifold counts, clear; exact full-rebuild totals.");
    }
}
