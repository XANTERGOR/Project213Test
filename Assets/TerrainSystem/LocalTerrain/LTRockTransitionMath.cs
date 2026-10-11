using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Pure topology partition. No source mesh, scene or material writes.
    public static class LTRockTransitionMath
    {
        public sealed class Partition
        {
            public int[][] rock, transition;
            public float[] terrainWeight;
        }
        struct Edge { public int to; public float length; }
        sealed class Heap
        {
            readonly List<(int id,float distance)> values=new List<(int,float)>();
            public int Count=>values.Count;
            public void Push(int id,float distance)
            {
                int i=values.Count;values.Add((id,distance));
                while(i>0){int p=(i-1)/2;if(values[p].distance<=distance)break;values[i]=values[p];i=p;}
                values[i]=(id,distance);
            }
            public (int id,float distance) Pop()
            {
                var first=values[0];var last=values[values.Count-1];values.RemoveAt(values.Count-1);
                if(values.Count==0)return first;
                int i=0;
                while(i*2+1<values.Count)
                {
                    int c=i*2+1;if(c+1<values.Count&&values[c+1].distance<values[c].distance)c++;
                    if(values[c].distance>=last.distance)break;values[i]=values[c];i=c;
                }
                values[i]=last;return first;
            }
        }
        public static float Weight(float distance,float width,float softness)
        {
            if(width<=0)return distance<=0?1:0;
            float t=Mathf.Clamp01(1-distance/width);
            return Mathf.Lerp(t,t*t*(3-2*t),Mathf.Clamp01(softness));
        }
        public static Partition Split(Vector3[] positions,int[][] triangles,int sourceSubmeshes,
            Matrix4x4 localToWorld,float width,float softness)
        {
            if(positions==null||triangles==null||sourceSubmeshes<1||sourceSubmeshes>triangles.Length)
                throw new ArgumentException("Invalid rock partition inputs.");
            if(float.IsNaN(width)||float.IsInfinity(width)||width<0)throw new ArgumentOutOfRangeException(nameof(width));
            int n=positions.Length;
            var world=new Vector3[n];var source=new bool[n];var bridge=new bool[n];
            var adjacency=new List<Edge>[n];var distance=new float[n];
            for(int i=0;i<n;i++){world[i]=localToWorld.MultiplyPoint3x4(positions[i]);distance[i]=float.PositiveInfinity;}
            for(int sub=0;sub<triangles.Length;sub++)
            {
                var indices=triangles[sub];if(indices.Length%3!=0)throw new ArgumentException("Incomplete triangles.");
                foreach(int v in indices)
                {if(v<0||v>=n)throw new ArgumentException("Index outside vertex array.");if(sub<sourceSubmeshes)source[v]=true;else bridge[v]=true;}
                if(sub>=sourceSubmeshes)continue;
                for(int t=0;t<indices.Length;t+=3)for(int k=0;k<3;k++)
                {
                    int a=indices[t+k],b=indices[t+(k+1)%3];float length=Vector3.Distance(world[a],world[b]);
                    (adjacency[a]??=new List<Edge>()).Add(new Edge{to=b,length=length});
                    (adjacency[b]??=new List<Edge>()).Add(new Edge{to=a,length=length});
                }
            }
            // UV/hard-normal duplicates can have different indices at the same rim.
            // Link coincident SOURCE vertices for distance only, never weld attributes.
            var coincident=new Dictionary<Vector3Int,List<int>>();
            Vector3Int Key(Vector3 v)=>new Vector3Int(Mathf.RoundToInt(v.x*10000),Mathf.RoundToInt(v.y*10000),Mathf.RoundToInt(v.z*10000));
            for(int i=0;i<n;i++)if(source[i])
            {
                var key=Key(positions[i]);if(!coincident.TryGetValue(key,out var group))coincident[key]=group=new List<int>();
                if(group.Count>0)
                {
                    int other=group[0];
                    (adjacency[i]??=new List<Edge>()).Add(new Edge{to=other});
                    (adjacency[other]??=new List<Edge>()).Add(new Edge{to=i});
                }
                group.Add(i);
            }
            var heap=new Heap();
            for(int i=0;i<n;i++)if(bridge[i]&&coincident.TryGetValue(Key(positions[i]),out var group))
                foreach(int v in group)if(distance[v]>0){distance[v]=0;heap.Push(v,0);}
            while(heap.Count>0)
            {
                var item=heap.Pop();if(item.distance!=distance[item.id]||item.distance>width)continue;
                if(adjacency[item.id]==null)continue;
                foreach(var edge in adjacency[item.id])
                {
                    float d=item.distance+edge.length;
                    if(d>=distance[edge.to])continue;distance[edge.to]=d;
                    if(d<=width)heap.Push(edge.to,d);
                }
            }
            var result=new Partition{rock=new int[sourceSubmeshes][],transition=new int[sourceSubmeshes+1][],terrainWeight=new float[n]};
            for(int i=0;i<n;i++)result.terrainWeight[i]=source[i]?Weight(distance[i],width,softness):bridge[i]?1:0;
            for(int sub=0;sub<sourceSubmeshes;sub++)
            {
                var rockIndices=new List<int>();var transitionIndices=new List<int>();var indices=triangles[sub];
                for(int t=0;t<indices.Length;t+=3)
                {
                    bool blend=false;for(int k=0;k<3;k++)blend|=result.terrainWeight[indices[t+k]]>0;
                    var output=blend?transitionIndices:rockIndices;
                    output.Add(indices[t]);output.Add(indices[t+1]);output.Add(indices[t+2]);
                }
                result.rock[sub]=rockIndices.ToArray();result.transition[sub]=transitionIndices.ToArray();
            }
            var strip=new List<int>();for(int sub=sourceSubmeshes;sub<triangles.Length;sub++)strip.AddRange(triangles[sub]);
            result.transition[sourceSubmeshes]=strip.ToArray();
            return result;
        }
    }
}
