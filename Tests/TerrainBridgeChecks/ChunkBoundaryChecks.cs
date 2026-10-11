using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
partial class Checks
{
        sealed class ChunkBoundaryData
        {
            public Mesh mesh;public int dirtyVersion;public Matrix4x4 matrix;
            public Vector3[] points,normals;
            public Vector3Int[] keys;
            public List<(int count,int a,int b)> edges;
            public Dictionary<Vector3Int,List<int>> pointIndices;
            public ChunkBoundaryData WithNormals(Vector3[] localNormals,int version,Matrix4x4 normalMatrix)
            {
                var transformed=new Vector3[points.Length];
                for(int i=0;i<transformed.Length;i++)
                    transformed[i]=i<localNormals.Length?normalMatrix.MultiplyVector(localNormals[i]).normalized:Vector3.up;
                return new ChunkBoundaryData{mesh=mesh,dirtyVersion=version,matrix=matrix,
                    points=points,keys=keys,edges=edges,normals=transformed,pointIndices=pointIndices};
            }
        }
        static ChunkBoundaryData BuildChunkBoundary(Vector3[] vertices,Vector3[] normals,int[] indices,Matrix4x4 matrix,Matrix4x4 normalMatrix)
        {
            var data=new ChunkBoundaryData{matrix=matrix,points=new Vector3[vertices.Length],normals=new Vector3[vertices.Length]};
            var relative=new Vector3[vertices.Length];
            data.keys=new Vector3Int[vertices.Length];var offset=new Vector3(matrix.m03,matrix.m13,matrix.m23);
            var ids=new int[vertices.Length];var positions=new Dictionary<Vector3Int,int>();
            var referenced=new bool[vertices.Length];foreach(int index in indices)referenced[index]=true;
            data.pointIndices=new Dictionary<Vector3Int,List<int>>();
            for(int i=0;i<vertices.Length;i++)
            {
                var p=data.points[i]=matrix.MultiplyPoint3x4(vertices[i]);
                relative[i]=matrix.MultiplyVector(vertices[i]);
                data.normals[i]=i<normals.Length?normalMatrix.MultiplyVector(normals[i]).normalized:Vector3.up;
                if(!referenced[i])continue;
                var key=data.keys[i]=LocalTerrainPrototype.LTRoadGeometryBoundary.TranslatedKey(relative[i],offset);
                if(!data.pointIndices.TryGetValue(key,out var uses))data.pointIndices[key]=uses=new List<int>();
                uses.Add(i);
                var localKey=LocalTerrainPrototype.LTRoadGeometryBoundary.PointKey(relative[i]);
                if(!positions.TryGetValue(localKey,out int id))positions[localKey]=id=i;
                ids[i]=id;
            }
            var edges=new Dictionary<long,(int count,int a,int b)>(EdgeKeyComparer.Instance);
            for(int i=0;i+2<indices.Length;i+=3)
            {
                int x=ids[indices[i]],y=ids[indices[i+1]],z=ids[indices[i+2]];
                if(x==y||y==z||z==x)continue;
                for(int k=0;k<3;k++)
                {
                    int a=ids[indices[i+k]],b=ids[indices[i+(k+1)%3]];long key=MeshEdge(a,b);
                    edges.TryGetValue(key,out var use);edges[key]=(use.count+1,a,b);
                }
            }
            // Keep counts > 1 too: another chunk must not turn an internal or
            // non-manifold edge into an apparent opening during global merging.
            data.edges=LocalTerrainPrototype.LTRoadGeometryBoundary.NormalizeChunkEdges(relative,edges.Values.ToList());return data;
        }

    static void ChunkBoundaryChecks()
    {
        Vector3Int Key(Vector3 p)=>LocalTerrainPrototype.LTRoadGeometryBoundary.PointKey(p);
        var chunks=new List<(Vector3[] v,Vector3[] n,int[] t,Matrix4x4 m)>();
        for(int chunk=0;chunk<4;chunk++)
        {
            var v=new List<Vector3>();var n=new List<Vector3>();var t=new List<int>();
            for(int z=0;z<=5;z++)for(int x=0;x<=5;x++){v.Add(new Vector3(x,0,z));n.Add(Vector3.up);}
            for(int z=0;z<5;z++)for(int x=0;x<5;x++)
            {
                if(chunk==0&&x==2&&z==2)continue; // interior cut
                int a=z*6+x,b=a+1,c=a+6,d=c+1;t.AddRange(new[]{a,c,b,b,c,d});
            }
            var matrix=Matrix4x4.identity;matrix.m03=(chunk%2)*5;matrix.m23=(chunk/2)*5;
            chunks.Add((v.ToArray(),n.ToArray(),t.ToArray(),matrix));
        }
        var cache=chunks.Select(c=>BuildChunkBoundary(c.v,c.n,c.t,c.m,Matrix4x4.identity)).ToArray();
        Dictionary<long,(int count,int a,int b)> Gather(bool cached)
        {
            var ids=new Dictionary<Vector3Int,int>();var edges=new Dictionary<long,(int count,int a,int b)>();
            int Id(Vector3 p){var k=Key(p);if(!ids.TryGetValue(k,out int id))ids[k]=id=ids.Count;return id;}
            void Edge(int a,int b,int count){long k=MeshEdge(a,b);edges.TryGetValue(k,out var e);edges[k]=(e.count+count,a,b);}
            for(int j=0;j<chunks.Count;j++)
            {
                var c=chunks[j];var data=cache[j];
                var remap=c.v.Select(p=>Id(c.m.MultiplyPoint3x4(p))).ToArray();
                if(cached)foreach(var e in data.edges)Edge(remap[e.a],remap[e.b],e.count);
                else for(int i=0;i<c.t.Length;i+=3)for(int k=0;k<3;k++)Edge(remap[c.t[i+k]],remap[c.t[i+(k+1)%3]],1);
                if(cached)for(int i=0;i<c.v.Length;i++)
                    Require(data.points[i]==c.m.MultiplyPoint3x4(c.v[i])&&data.normals[i]==Vector3.up,"cached point/normal mismatch");
            }
            return edges;
        }
        void Check(){var full=Gather(false);var cached=Gather(true);Require(full.Count==cached.Count&&full.All(e=>cached[e.Key].count==e.Value.count),"cached/full undirected boundary edge/count mismatch");}
        Check();
        // Update only one chunk; unchanged entries must remain reusable.
        var old=cache[1];var changed=chunks[0];changed.t=changed.t.Skip(6).ToArray();chunks[0]=changed;
        cache[0]=BuildChunkBoundary(changed.v,changed.n,changed.t,changed.m,Matrix4x4.identity);Check();
        Require(ReferenceEquals(old,cache[1]),"unchanged cache replaced");
        // Duplicate/non-manifold faces must retain counts, not become open edges.
        changed.t=changed.t.Concat(changed.t.Take(3)).ToArray();chunks[0]=changed;
        cache[0]=BuildChunkBoundary(changed.v,changed.n,changed.t,changed.m,Matrix4x4.identity);Check();
        // Removed chunk no longer contributes.
        chunks.RemoveAt(3);Check();
        Console.WriteLine("PASS chunk boundary aggregation: full/cached counts and endpoints, normals, shared seams, cut, partial edit, duplicate face, deletion.");
    }
}
