using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
partial class Checks
{
        sealed class SeamNormalAccumulator
        {
            sealed class Entry {public Vector3 sum;public int owner;public bool shared;}
            readonly Dictionary<Vector3Int,Entry> entries=new Dictionary<Vector3Int,Entry>();
            public void Add(int owner,Rect rect,Vector3[] points,int[] triangles)
            {
                var border=new bool[points.Length];
                for(int i=0;i<points.Length;i++)
                {
                    var p=points[i];
                    border[i]=Mathf.Abs(p.x-rect.xMin)<=.0001f||Mathf.Abs(p.x-rect.xMax)<=.0001f||
                        Mathf.Abs(p.z-rect.yMin)<=.0001f||Mathf.Abs(p.z-rect.yMax)<=.0001f;
                }
                for(int i=0;i+2<triangles.Length;i+=3)
                {
                    int a=triangles[i],b=triangles[i+1],c=triangles[i+2];
                    if(!border[a]&&!border[b]&&!border[c])continue;
                    // Unnormalised cross product weights each face by its area.
                    // Always use geometry, never previously smoothed vertex normals.
                    Vector3 normal=Vector3.Cross(points[b]-points[a],points[c]-points[a]);
                    for(int k=0;k<3;k++)
                    {
                        int vertex=triangles[i+k];if(!border[vertex])continue;
                        var key=BoundaryPointKey(points[vertex]);
                        if(!entries.TryGetValue(key,out var entry))entries[key]=entry=new Entry{owner=owner};
                        entry.shared|=entry.owner!=owner;entry.sum+=normal;
                    }
                }
            }
            public Dictionary<Vector3Int,Vector3> Finish()=>entries.Where(p=>p.Value.shared&&p.Value.sum.sqrMagnitude>1e-20f)
                .ToDictionary(p=>p.Key,p=>p.Value.sum.normalized);
        }

    static void SeamNormalChecks()
    {
        var chunks=new List<(Rect rect,Vector3[] v,int[] t)>();
        for(int z=0;z<2;z++)for(int x=0;x<2;x++)
        {
            float H(float a,float b)=>a*a+.3f*b*b+.2f*a*b;
            var v=new[]{new Vector3(x,H(x,z),z),new Vector3(x+1,H(x+1,z),z),
                new Vector3(x,H(x,z+1),z+1),new Vector3(x+1,H(x+1,z+1),z+1)};
            chunks.Add((new Rect(x,z,1,1),v,new[]{0,2,1,1,2,3}));
        }
        Dictionary<Vector3Int,Vector3> Run()
        {
            var accumulator=new SeamNormalAccumulator();
            for(int i=0;i<chunks.Count;i++)accumulator.Add(i,chunks[i].rect,chunks[i].v,chunks[i].t);
            return accumulator.Finish();
        }
        void Check(Dictionary<Vector3Int,Vector3> actual)
        {
            var sum=new Dictionary<Vector3Int,Vector3>();
            var owners=new Dictionary<Vector3Int,HashSet<int>>();
            for(int j=0;j<chunks.Count;j++)
            {
                var c=chunks[j];
                foreach(var p in c.v){var key=BoundaryPointKey(p);if(!owners.ContainsKey(key))owners[key]=new HashSet<int>();owners[key].Add(j);}
                for(int i=0;i<c.t.Length;i+=3)
                {
                    var n=Vector3.Cross(c.v[c.t[i+1]]-c.v[c.t[i]],c.v[c.t[i+2]]-c.v[c.t[i]]);
                    for(int k=0;k<3;k++){var key=BoundaryPointKey(c.v[c.t[i+k]]);sum.TryGetValue(key,out var old);sum[key]=old+n;}
                }
            }
            Require(actual.Count==owners.Count(p=>p.Value.Count>1),"shared seam group count");
            foreach(var p in actual)Require((p.Value-sum[p.Key].normalized).sqrMagnitude<1e-12f,"seam normal differs from merged area-weighted mesh");
        }
        var before=Run();Check(before);
        var corner=BoundaryPointKey(chunks[0].v[3]);Require(before.ContainsKey(corner),"four-chunk corner missing");
        var sharedEdge=BoundaryPointKey(chunks[0].v[1]);
        Vector3 unchangedNeighborNormal=before[sharedEdge];
        // Change one interior-side vertex only; adjacent seam must respond too.
        chunks[0].v[0]+=new Vector3(0,2,0);
        var after=Run();Check(after);
        Require((after[sharedEdge]-unchangedNeighborNormal).sqrMagnitude>1e-8f,"unchanged neighbour seam did not respond");
        var repeated=Run();Require(after.All(p=>repeated[p.Key].Equals(p.Value)),"repeated seam computation drifts");
        Console.WriteLine("PASS shared normals: merged area-weighted reference, four-chunk corner, changed neighbour, repeat without drift.");
    }
}

