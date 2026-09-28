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
        IncrementalSeamChecks();
        Console.WriteLine("PASS shared normals: merged area-weighted reference, four-chunk corner, changed neighbour, repeat without drift.");
    }
    static void IncrementalSeamChecks()
    {
        const int size=8;
        var points=new Vector3[size*size][];var faces=new int[size*size][];var rects=new Rect[size*size];
        var cache=new LocalTerrainPrototype.LTPaintMath.SeamContribution[size*size][];
        for(int z=0;z<size;z++)for(int x=0;x<size;x++)
        {
            int id=z*size+x;rects[id]=new Rect(x,z,1,1);points[id]=new Vector3[9];
            for(int j=0;j<3;j++)for(int i=0;i<3;i++)
            {float px=x+i*.5f,pz=z+j*.5f;points[id][j*3+i]=new Vector3(px,.2f*px*px+.1f*pz*pz,pz);}
            var t=new List<int>();
            for(int j=0;j<2;j++)for(int i=0;i<2;i++){int a=j*3+i;t.AddRange(new[]{a,a+3,a+1,a+1,a+3,a+4});}
            faces[id]=t.ToArray();cache[id]=LocalTerrainPrototype.LTPaintMath.TerrainSeamContributions(rects[id],points[id],faces[id]);
        }
        Dictionary<Vector3Int,Vector3> Full()
        {
            var accumulator=new SeamNormalAccumulator();
            for(int id=0;id<points.Length;id++)accumulator.Add(id,rects[id],points[id],faces[id]);
            return accumulator.Finish();
        }
        Dictionary<Vector3Int,Vector3> Cached()
        {
            var sums=new Dictionary<Vector3Int,(int owner,bool shared,Vector3 sum)>();
            for(int id=0;id<cache.Length;id++)foreach(var item in cache[id])
            {
                if(!sums.TryGetValue(item.key,out var v))v=(id,false,Vector3.zero);
                v.shared|=v.owner!=id;v.sum+=item.normal;sums[item.key]=v;
            }
            return sums.Where(p=>p.Value.shared&&p.Value.sum.sqrMagnitude>1e-20f).ToDictionary(p=>p.Key,p=>p.Value.sum.normalized);
        }
        for(int iteration=0;iteration<40;iteration++)
        {
            var before=Full();int changed=iteration*17%(size*size);
            // Interior displacement changes all four edge normals without moving
            // the shared border. Refine/cut variants exercise face count/order too.
            points[changed][4]+=new Vector3(0,iteration%2==0?.3f:-.2f,0);
            if(iteration==11)faces[changed]=faces[changed].Skip(3).ToArray();
            var proposed=LocalTerrainPrototype.LTPaintMath.TerrainSeamContributions(rects[changed],points[changed],faces[changed]);
            if(iteration==17)
            {
                // A discarded proposal must not mutate the old cached arrays.
                var old=cache[changed];var snapshot=old.Select(c=>(c.key,c.normal)).ToArray();
                var ignored=LocalTerrainPrototype.LTPaintMath.TerrainSeamContributions(rects[changed],points[changed],faces[changed]);
                Require(snapshot.SequenceEqual(old.Select(c=>(c.key,c.normal))),"discarded seam proposal mutates cache");
            }
            cache[changed]=proposed;
            var full=Full();var cached=Cached();
            Require(full.Count==cached.Count&&full.All(p=>cached.TryGetValue(p.Key,out var n)&&n.Equals(p.Value)),
                "cached per-face contributions differ from exact full area-weighted accumulation");
            var affected=LocalTerrainPrototype.LTPaintMath.TerrainNeighbours(new[]{changed},size,size);
            for(int id=0;id<points.Length;id++)if(!affected.Contains(id))foreach(var p in points[id])
            {
                var key=BoundaryPointKey(p);
                bool was=before.TryGetValue(key,out var old),now=full.TryGetValue(key,out var current);
                Require(was==now&&(!was||old.Equals(current)),"normal writes escaped changed chunk plus one-ring halo");
            }
        }
        Console.WriteLine("PASS incremental seam contributions: 40 edits/cuts, exact full-rebuild normals, four-corner halo, discarded proposal isolation; production math, no native mesh upload.");
    }
}
