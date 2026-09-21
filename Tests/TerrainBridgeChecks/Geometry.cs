using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
partial class Checks {
        static float ContactCutDistance(List<Vector3> segments,Vector3 point,float offset)
        {
            bool inside=false;float closest=float.MaxValue;var query=new Vector2(point.x,point.z);
            for(int i=0;i+1<segments.Count;i+=2)
            {
                var a=new Vector2(segments[i].x,segments[i].z);var b=new Vector2(segments[i+1].x,segments[i+1].z);var ab=b-a;
                float t=ab.sqrMagnitude>1e-12f?Mathf.Clamp01(Vector2.Dot(query-a,ab)/ab.sqrMagnitude):0;
                closest=Mathf.Min(closest,(query-a-t*ab).sqrMagnitude);
                if((a.y>query.y)!=(b.y>query.y)&&query.x<(b.x-a.x)*(query.y-a.y)/(b.y-a.y)+a.x)inside=!inside;
            }
            return (inside?-1:1)*Mathf.Sqrt(closest)-offset;
        }
        static List<RockClipVertex> SelectOwnedBoundaryLoops(List<List<RockClipVertex>> loops,int owner,Func<Vector3,int> ownerAt)
        {
            var result=new List<RockClipVertex>();
            foreach(var loop in loops)
            {
                var owners=new HashSet<int>();
                for(int i=0;i<loop.Count;i++)owners.Add(ownerAt((loop[i].position+loop[(i+1)%loop.Count].position)*.5f));
                if(!owners.Contains(owner))continue;
                if(owners.Count!=1)
                    throw new InvalidOperationException("Terrain cut contours merge between mesh stamps or reach the terrain boundary. Separate their cut regions (reduce Terrain Cut Offset or move the objects). Shared multi-object bridges are not yet supported; previous geometry is preserved.");
                for(int i=0;i<loop.Count;i++){result.Add(loop[i]);result.Add(loop[(i+1)%loop.Count]);}
            }
            return result;
        }
struct RockClipVertex
        {
            public Vector3 position,normal;
            public Vector4 tangent;
            public Vector2 uv;
            public float distance;
        }
static RockClipVertex LerpRockVertex(RockClipVertex a,RockClipVertex b,float t)
        {
            var r=a;
            r.position=Vector3.LerpUnclamped(a.position,b.position,t);
            r.normal=Vector3.LerpUnclamped(a.normal,b.normal,t).normalized;
            r.tangent=Vector4.LerpUnclamped(a.tangent,b.tangent,t);
            var tangent3=new Vector3(r.tangent.x,r.tangent.y,r.tangent.z).normalized;
            r.tangent=new Vector4(tangent3.x,tangent3.y,tangent3.z,r.tangent.w>=0?1:-1);
            r.uv=Vector2.LerpUnclamped(a.uv,b.uv,t);r.distance=0;return r;
        }
        static List<List<RockClipVertex>> OrderedBoundaryLoops(List<RockClipVertex> segments,string context="intersection")
        {
            var vertices=new List<RockClipVertex>();var ids=new Dictionary<Vector3Int,List<int>>();
            var adjacency=new Dictionary<int,List<int>>();var edges=new HashSet<long>(GeometryEdgeComparer);
            Func<RockClipVertex,int> id=v=>
            {
                Vector3 p=v.position;var key=new Vector3Int(Mathf.RoundToInt(p.x*10000),Mathf.RoundToInt(p.y*10000),Mathf.RoundToInt(p.z*10000));
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                    if(ids.TryGetValue(key+new Vector3Int(x,y,z),out var nearby))
                        foreach(int found in nearby)if((vertices[found].position-p).sqrMagnitude<=1e-8f)return found;
                int added=vertices.Count;vertices.Add(v);
                if(!ids.TryGetValue(key,out var bucket))ids.Add(key,bucket=new List<int>());
                bucket.Add(added);adjacency.Add(added,new List<int>());return added;
            };
            for(int i=0;i+1<segments.Count;i+=2)
            {
                int a=id(segments[i]),b=id(segments[i+1]);if(a==b)continue;
                long key=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
                if(!edges.Add(key))continue;adjacency[a].Add(b);adjacency[b].Add(a);
            }
            var invalid=adjacency.Where(pair=>pair.Value.Count!=0&&pair.Value.Count!=2).ToArray();
            if(invalid.Length>0)
            {
                int open=invalid.Count(pair=>pair.Value.Count==1),branches=invalid.Length-open;
                var first=invalid[0];Vector3 p=vertices[first.Key].position;
                throw new InvalidOperationException($"{context}: contour has {open} open endpoints and {branches} branch points. First at ({p.x:R}, {p.y:R}, {p.z:R}), degree {first.Value.Count}. Check cut segments and source topology; final Weld Distance is applied later and cannot repair this contour.");
            }
            var result=new List<List<RockClipVertex>>();var visited=new HashSet<int>();
            foreach(var pair in adjacency)
            {
                if(pair.Value.Count==0||visited.Contains(pair.Key))continue;
                var loop=new List<RockClipVertex>();int first=pair.Key,current=first,previous=-1;
                do
                {
                    if(!visited.Add(current))throw new InvalidOperationException("Bridge contour crosses itself.");
                    loop.Add(vertices[current]);var next=adjacency[current];int target=next[0]==previous?next[1]:next[0];previous=current;current=target;
                }while(current!=first);
                if(loop.Count>=3)result.Add(loop);
            }
            return result;
        }
        static List<RockClipVertex> ClipRockTriangle(RockClipVertex a,RockClipVertex b,RockClipVertex c)
        {
            var input=new List<RockClipVertex>(4){a,b,c};var output=new List<RockClipVertex>(4);
            var previous=input[input.Count-1];bool previousInside=previous.distance>=0;
            foreach(var current in input)
            {
                bool currentInside=current.distance>=0;
                if(currentInside!=previousInside)
                {
                    // Always interpolate a shared edge in the same direction. Reversed
                    // triangle winding must not produce a different cut point.
                    var from=previous.distance<0?previous:current;
                    var to=previous.distance<0?current:previous;
                    double denominator=(double)from.distance-to.distance;
                    float t=(float)(from.distance/denominator);
                    output.Add(LerpRockVertex(from,to,t));
                }
                if(currentInside)output.Add(current);
                previous=current;previousInside=currentInside;
            }
            return output;
        }
static void CollectRockBoundarySegment(List<RockClipVertex> polygon,List<RockClipVertex> segments)
        {
            var cut=new List<RockClipVertex>(2);
            foreach(var vertex in polygon)
                if(Mathf.Abs(vertex.distance)<.00001f&&cut.All(p=>(p.position-vertex.position).sqrMagnitude>.00000001f))cut.Add(vertex);
            if(cut.Count==2){segments.Add(cut[0]);segments.Add(cut[1]);}
        }
static float[] ContourParameters(List<RockClipVertex> loop)
        {
            var cumulative=new float[loop.Count+1];
            for(int i=0;i<loop.Count;i++)cumulative[i+1]=cumulative[i]+Vector3.Distance(loop[i].position,loop[(i+1)%loop.Count].position);
            float length=cumulative[loop.Count];
            if(length<.0001f)throw new InvalidOperationException("Degenerate bridge contour.");
            for(int i=1;i<cumulative.Length;i++)cumulative[i]/=length;
            return cumulative;
        }
static RockClipVertex SampleContour(List<RockClipVertex> loop,float[] parameters,float u)
        {
            int index=Array.BinarySearch(parameters,u);
            if(index>=0)return loop[index%loop.Count];
            index=Mathf.Clamp(~index-1,0,loop.Count-1);
            return LerpRockVertex(loop[index],loop[(index+1)%loop.Count],(u-parameters[index])/Mathf.Max(1e-9f,parameters[index+1]-parameters[index]));
        }
static float ContourArea(List<RockClipVertex> loop)
        {
            float area=0;
            for(int i=0;i<loop.Count;i++){Vector3 a=loop[i].position,b=loop[(i+1)%loop.Count].position;area+=a.x*b.z-b.x*a.z;}
            return area*.5f;
        }
        static List<RockClipVertex> SampleRim(List<RockClipVertex> loop,float cell,int budget,out float[] parameters,Func<Vector3,float> cellAt=null)
        {
            var original=ContourParameters(loop);var result=new List<RockClipVertex>();var u=new List<float>();
            for(int i=0;i<loop.Count;i++)
            {
                float localCell=cellAt==null?cell:cellAt((loop[i].position+loop[(i+1)%loop.Count].position)*.5f);
                int steps=Math.Max(1,Mathf.CeilToInt(Vector3.Distance(loop[i].position,loop[(i+1)%loop.Count].position)/Mathf.Max(.00001f,localCell)));
                if((long)result.Count+steps>budget)throw new InvalidOperationException("Bridge rim exceeds the mesh budget.");
                for(int k=0;k<steps;k++)
                {
                    float t=k/(float)steps;result.Add(k==0?loop[i]:LerpRockVertex(loop[i],loop[(i+1)%loop.Count],t));
                    u.Add(Mathf.Lerp(original[i],original[i+1],t));
                }
            }
            u.Add(1);parameters=u.ToArray();return result;
        }
static void ZipBridgeRings(int[] inner,float[] innerU,int[] outer,float[] outerU,Action<int,int,int> triangle)
        {
            int i=0,j=0;
            while(i<inner.Length||j<outer.Length)
            {
                int a=inner[i%inner.Length],c=outer[j%outer.Length];
                float ni=i<inner.Length?innerU[i+1]:float.PositiveInfinity;
                float nj=j<outer.Length?outerU[j+1]:float.PositiveInfinity;
                if(ni==nj)
                {
                    int b=inner[(i+1)%inner.Length],d=outer[(j+1)%outer.Length];
                    if(((i+j)&1)==0){triangle(a,b,c);triangle(c,b,d);}
                    else{triangle(a,b,d);triangle(a,d,c);}
                    i++;j++;
                }
                else if(ni<nj){triangle(a,inner[(i+1)%inner.Length],c);i++;}
                else{triangle(a,outer[(j+1)%outer.Length],c);j++;}
            }
        }
        static void BuildContourBridge(List<List<RockClipVertex>> rockLoops,List<List<RockClipVertex>> terrainLoops,
            float cell,int budget,Vector3 worldSize,Func<Vector3,Vector3,Vector4,Vector2,int> vertex,Action<int,int,int> triangle,
            HashSet<int> outerRim,HashSet<long> outerEdges,float blendDistance,Func<Vector3,float> cellAt=null)
        {
            if(rockLoops.Count!=terrainLoops.Count)
                throw new InvalidOperationException("Bridge contours merged or disappeared. The previous geometry is preserved; reduce Cut Offset or Retopo Height.");
            var unused=new List<List<RockClipVertex>>(terrainLoops);int total=0;
            foreach(var sourceLoop in rockLoops)
            {
                var rock=new List<RockClipVertex>(sourceLoop);
                if(ContourArea(rock)<0)rock.Reverse();
                Vector3 centre=Vector3.zero;foreach(var v in rock)centre+=v.position;centre/=rock.Count;
                var matched=unused.OrderBy(loop=>
                {
                    Vector3 c=Vector3.zero;foreach(var v in loop)c+=v.position;return (c/loop.Count-centre).sqrMagnitude;
                }).First();unused.Remove(matched);
                var terrain=new List<RockClipVertex>(matched);if(ContourArea(terrain)<0)terrain.Reverse();
                var rp=ContourParameters(rock);int phase=0;float best=float.MaxValue;
                int stride=Math.Max(1,terrain.Count/128);
                for(int candidate=0;candidate<terrain.Count;candidate+=stride)
                {
                    var rotated=terrain.Skip(candidate).Concat(terrain.Take(candidate)).ToList();var tp=ContourParameters(rotated);float error=0;
                    for(int k=0;k<32;k++)error+=(SampleContour(rock,rp,k/32f).position-SampleContour(rotated,tp,k/32f).position).sqrMagnitude;
                    if(error<best){best=error;phase=candidate;}
                }
                terrain=terrain.Skip(phase).Concat(terrain.Take(phase)).ToList();var tpFinal=ContourParameters(terrain);
                float rockLength=0,terrainLength=0;
                for(int i=0;i<rock.Count;i++)rockLength+=Vector3.Distance(rock[i].position,rock[(i+1)%rock.Count].position);
                for(int i=0;i<terrain.Count;i++)terrainLength+=Vector3.Distance(terrain[i].position,terrain[(i+1)%terrain.Count].position);
                int columns=Math.Max(3,Mathf.CeilToInt(Mathf.Max(rockLength,terrainLength)/cell));
                float[] interiorParameters=null;
                if(cellAt!=null)
                {
                    var sampling=new List<float>{0};float u=0;
                    while(u<1)
                    {
                        Vector3 p=(SampleContour(rock,rp,u).position+SampleContour(terrain,tpFinal,u).position)*.5f;
                        u+=Mathf.Max(.00001f,cellAt(p))/Mathf.Max(rockLength,terrainLength);
                        if(u<1)sampling.Add(u);
                        if(sampling.Count>budget)throw new InvalidOperationException("Union Bridge density exceeds the mesh budget.");
                    }
                    if(sampling.Count<3)sampling=new List<float>{0,1f/3,2f/3};
                    else for(int i=0;i<sampling.Count;i++)sampling[i]/=u;
                    sampling.Add(1);interiorParameters=sampling.ToArray();columns=sampling.Count-1;
                }
                if(columns>budget)throw new InvalidOperationException("Bridge density exceeds the mesh budget.");
                float width=0;
                for(int i=0;i<columns;i++)width=Mathf.Max(width,Vector3.Distance(SampleContour(rock,rp,i/(float)columns).position,SampleContour(terrain,tpFinal,i/(float)columns).position));
                int rows=Math.Max(2,Mathf.CeilToInt(width*1.5f/cell));
                var rockRim=SampleRim(rock,cell,budget,out var rockU,cellAt);
                var terrainRim=SampleRim(terrain,cell,budget,out var terrainU,cellAt);
                if((long)(rows-1)*columns+rockRim.Count+terrainRim.Count+total>budget)throw new InvalidOperationException("Bridge exceeds Max Vertices Per Chunk.");
                total+=(rows-1)*columns+rockRim.Count+terrainRim.Count;
                int[] previous=null;float[] previousU=null;
                // Interior rows are evenly sampled. Rim corners are retained only on
                // their own boundary and zipped to the next row, never propagated as
                // entire nearly coincident strips across the patch.
                for(int row=0;row<=rows;row++)
                {
                    int count=row==0?rockRim.Count:row==rows?terrainRim.Count:columns;
                    var ring=new int[count];float[] parameters;
                    if(row==0)parameters=rockU;
                    else if(row==rows)parameters=terrainU;
                    else if(interiorParameters!=null)parameters=interiorParameters;
                    else{parameters=new float[count+1];for(int i=0;i<=count;i++)parameters[i]=i/(float)count;}
                    float t=row/(float)rows;
                    for(int i=0;i<count;i++)
                    {
                        float u=parameters[i];var a=SampleContour(rock,rp,u);var b=SampleContour(terrain,tpFinal,u);
                        Vector3 delta=b.position-a.position;float bend=Mathf.Min(delta.magnitude,Mathf.Max(0,blendDistance));
                        Vector3 ta=Vector3.ProjectOnPlane(delta,a.normal).normalized*bend;
                        Vector3 tb=Vector3.ProjectOnPlane(delta,b.normal).normalized*bend;
                        // Blend between a straight bridge and bounded Hermite tangents.
                        // Density and row count do not depend on Blend Distance.
                        float smooth=Mathf.Clamp01(blendDistance/Mathf.Max(.001f,delta.magnitude));
                        Vector3 m0=Vector3.Lerp(delta,ta,smooth),m1=Vector3.Lerp(delta,tb,smooth);
                        float t2=t*t,t3=t2*t;
                        Vector3 position=(2*t3-3*t2+1)*a.position+(t3-2*t2+t)*m0+(-2*t3+3*t2)*b.position+(t3-t2)*m1;
                        if(row==0)position=rockRim[i].position;
                        else if(row==rows)position=terrainRim[i].position;
                        Vector3 normal=Vector3.Lerp(a.normal,b.normal,t*t*(3-2*t)).normalized;
                        Vector3 tangent=Vector3.ProjectOnPlane(Vector3.right,normal).normalized;
                        if(tangent.sqrMagnitude<.01f)tangent=Vector3.ProjectOnPlane(Vector3.forward,normal).normalized;
                        Vector2 uv=Vector2.Lerp(a.uv,new Vector2(position.x/worldSize.x,position.z/worldSize.z),t*t*(3-2*t));
                        ring[i]=vertex(position,normal,row==0?a.tangent:new Vector4(tangent.x,tangent.y,tangent.z,1),uv);
                        if(row==rows)outerRim.Add(ring[i]);
                    }
                    if(previous!=null)ZipBridgeRings(previous,previousU,ring,parameters,triangle);
                    if(row==rows)for(int i=0;i<count;i++)outerEdges.Add(MeshEdge(ring[i],ring[(i+1)%count]));
                    previous=ring;previousU=parameters;
                }
            }
        }
static void StitchRockRim(List<Vector3> positions,List<Vector3> normals,List<Vector4> tangents,List<Vector2> uv,
            List<int>[] submeshes,int sourceSubmeshes,int originalCount,List<RockClipVertex> rim)
        {
            const float rimTolerance=.0001f;
            Func<Vector3,Vector3Int> key=p=>new Vector3Int(Mathf.RoundToInt(p.x/rimTolerance),Mathf.RoundToInt(p.y/rimTolerance),Mathf.RoundToInt(p.z/rimTolerance));
            var bridgeIds=new Dictionary<Vector3Int,int>();
            for(int i=originalCount;i<positions.Count;i++)bridgeIds[key(positions[i])]=i;
            var splitEdges=new Dictionary<(Vector3Int,Vector3Int),List<int>>();var rimKeys=new HashSet<Vector3Int>();
            for(int i=0;i+1<rim.Count;i+=2)
            {
                Vector3 a=rim[i].position,b=rim[i+1].position,ab=b-a;float length=ab.sqrMagnitude;
                if(length<1e-14f)continue;
                var ka=key(a);var kb=key(b);rimKeys.Add(ka);rimKeys.Add(kb);
                var candidates=new List<(float t,int id)>();
                for(int j=originalCount;j<positions.Count;j++)
                {
                    float t=Vector3.Dot(positions[j]-a,ab)/length;
                    if(t<=0||t>=1||(positions[j]-a).sqrMagnitude<rimTolerance*rimTolerance||(positions[j]-b).sqrMagnitude<rimTolerance*rimTolerance)continue;
                    if((positions[j]-(a+t*ab)).sqrMagnitude<rimTolerance*rimTolerance)candidates.Add((t,j));
                }
                var ordered=candidates.OrderBy(c=>c.t).Select(c=>c.id).ToList();
                splitEdges[(ka,kb)]=ordered;splitEdges[(kb,ka)]=ordered.AsEnumerable().Reverse().ToList();
            }
            Func<int,int> weld=i=>
            {
                var k=key(positions[i]);if(!rimKeys.Contains(k))return i;
                // Source -> terrain -> source transforms can land on opposite sides
                // of a quantization bin even when the rim positions coincide.
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                    if(bridgeIds.TryGetValue(k+new Vector3Int(x,y,z),out int bridge)&&(positions[bridge]-positions[i]).sqrMagnitude<rimTolerance*rimTolerance)return bridge;
                throw new InvalidOperationException("Bridge rim vertex could not be welded to the source rock.");
            };
            for(int sub=0;sub<sourceSubmeshes;sub++)
            {
                var input=submeshes[sub];var output=new List<int>();
                for(int i=0;i+2<input.Count;i+=3)
                {
                    var polygon=new List<int>();bool split=false;
                    for(int k=0;k<3;k++)
                    {
                        int a=input[i+k],b=input[i+(k+1)%3];polygon.Add(weld(a));
                        if(splitEdges.TryGetValue((key(positions[a]),key(positions[b])),out var extra)&&extra.Count>0)
                        {polygon.AddRange(extra);split=true;}
                    }
                    if(!split){output.AddRange(polygon);continue;}
                    // Only the source triangles touched by the new rim are retriangulated.
                    // The bridge and rock now reference the same rim vertex indices.
                    int centre=positions.Count;int ia=input[i],ib=input[i+1],ic=input[i+2];
                    positions.Add((positions[ia]+positions[ib]+positions[ic])/3);
                    normals.Add((normals[ia]+normals[ib]+normals[ic]).normalized);
                    tangents.Add(tangents[ia]);uv.Add((uv[ia]+uv[ib]+uv[ic])/3);
                    for(int k=0;k<polygon.Count;k++)
                    {int a=polygon[k],b=polygon[(k+1)%polygon.Count];if(a==b)continue;output.Add(centre);output.Add(a);output.Add(b);}
                }
                submeshes[sub]=output;
            }
        }
static void OrientConnectedFaces(List<int>[] submeshes)
        {
            var faces=new List<(int sub,int offset)>();var edges=new Dictionary<long,List<(int face,bool direction)>>(GeometryEdgeComparer);
            for(int sub=0;sub<submeshes.Length;sub++)for(int i=0;i+2<submeshes[sub].Count;i+=3)
            {
                int face=faces.Count;faces.Add((sub,i));
                for(int k=0;k<3;k++)
                {
                    int a=submeshes[sub][i+k],b=submeshes[sub][i+(k+1)%3];long key=MeshEdge(a,b);
                    if(!edges.TryGetValue(key,out var incident))edges.Add(key,incident=new List<(int,bool)>());
                    incident.Add((face,a<b));
                    if(incident.Count>2)throw new InvalidOperationException("Non-manifold bridge edge; previous geometry preserved.");
                }
            }
            var visited=new bool[faces.Count];var flipped=new bool[faces.Count];var queue=new Queue<int>();
            for(int seed=0;seed<faces.Count;seed++)
            {
                if(visited[seed])continue;visited[seed]=true;queue.Enqueue(seed);
                while(queue.Count>0)
                {
                    int f=queue.Dequeue();var face=faces[f];var indices=submeshes[face.sub];
                    for(int k=0;k<3;k++)
                    {
                        int a=indices[face.offset+k],b=indices[face.offset+(k+1)%3];
                        foreach(var neighbor in edges[MeshEdge(a,b)])
                        {
                            if(neighbor.face==f)continue;
                            bool flip=flipped[f]^((a<b)==neighbor.direction);
                            if(visited[neighbor.face])
                            {
                                if(flipped[neighbor.face]!=flip)throw new InvalidOperationException("Inconsistent bridge orientation; previous geometry preserved.");
                            }
                            else{visited[neighbor.face]=true;flipped[neighbor.face]=flip;queue.Enqueue(neighbor.face);}
                        }
                    }
                }
            }
            for(int i=0;i<faces.Count;i++)if(flipped[i])
            {var f=faces[i];var indices=submeshes[f.sub];int swap=indices[f.offset+1];indices[f.offset+1]=indices[f.offset+2];indices[f.offset+2]=swap;}
        }
        static void WeldGeneratedTopology(List<Vector3> positions,List<Vector3> normals,List<Vector4> tangents,List<Vector2> uv,
            List<int>[] submeshes,Matrix4x4 toTerrain,int bridgeStart,int bridgeEnd,float weldDistance,HashSet<int> outerRim,HashSet<long> outerEdges=null)
        {
            int count=positions.Count;if(count==0)return;
            var parent=Enumerable.Range(0,count).ToArray();
            Func<int,int> root=null;root=i=>parent[i]==i?i:parent[i]=root(parent[i]);
            var terrainPositions=new Vector3[count];
            for(int i=0;i<count;i++)terrainPositions[i]=toTerrain.MultiplyPoint3x4(positions[i]);
            var protectedRoots=new HashSet<int>(outerRim);
            Action<int,int> join=(a,b)=>
            {
                a=root(a);b=root(b);if(a==b)return;
                bool aProtected=protectedRoots.Contains(a),bProtected=protectedRoots.Contains(b);
                if(aProtected&&bProtected&&a!=b)return;
                bool aBridge=a>=bridgeStart&&a<bridgeEnd,bBridge=b>=bridgeStart&&b<bridgeEnd;
                if(aProtected&&!bProtected)parent[b]=a;
                else if(bProtected&&!aProtected)parent[a]=b;
                else if(aBridge&&!bBridge)parent[a]=b;
                else if(bBridge&&!aBridge)parent[b]=a;
                else if(a<b)parent[b]=a;else parent[a]=b;
            };
            Func<int,int,long> edgeKey=(a,b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
            Func<Dictionary<long,int>> edgeCounts=()=>
            {
                var result=new Dictionary<long,int>(GeometryEdgeComparer);
                foreach(var triangles in submeshes)for(int i=0;i+2<triangles.Count;i+=3)
                {
                    int a=root(triangles[i]),b=root(triangles[i+1]),c=root(triangles[i+2]);
                    if(a==b||b==c||a==c)continue;
                    foreach(long key in new[]{edgeKey(a,b),edgeKey(b,c),edgeKey(c,a)})
                        result[key]=result.TryGetValue(key,out int value)?value+1:1;
                }
                return result;
            };
            // Weld exact per-triangle copies first. Neighbour buckets are checked too,
            // so equal points on opposite sides of a quantization cell still connect.
            const float exact=.0001f;var buckets=new Dictionary<Vector3Int,List<int>>();
            for(int i=0;i<count;i++)
            {
                Vector3 p=terrainPositions[i];var key=new Vector3Int(Mathf.FloorToInt(p.x/exact),Mathf.FloorToInt(p.y/exact),Mathf.FloorToInt(p.z/exact));
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                    if(buckets.TryGetValue(key+new Vector3Int(x,y,z),out var candidates))
                        foreach(int other in candidates)if((terrainPositions[other]-p).sqrMagnitude<=exact*exact)join(i,other);
                if(!buckets.TryGetValue(key,out var own))buckets.Add(key,own=new List<int>());own.Add(i);
            }
            protectedRoots=new HashSet<int>(outerRim.Select(root));
            // Any open edge away from the terrain rim is a crack. Pair its vertices
            // with the nearest non-neighbouring open vertex inside Weld Distance.
            float weld=Mathf.Max(0,weldDistance),weldSq=weld*weld;
            for(int iteration=0;iteration<8&&weld>0;iteration++)
            {
                var edges=edgeCounts();var boundaryEdges=edges.Where(p=>p.Value==1).Select(p=>p.Key).ToArray();
                var boundaryVertices=new HashSet<int>();
                var connected=new HashSet<long>(GeometryEdgeComparer);
                foreach(long key in boundaryEdges)
                {
                    int a=root((int)(key>>32)),b=root((int)(key&0xffffffff));boundaryVertices.Add(a);boundaryVertices.Add(b);connected.Add(edgeKey(a,b));
                }
                var loose=boundaryVertices.Where(v=>!protectedRoots.Contains(root(v))).ToArray();
                if(loose.Length==0)break;
                bool changed=false;var reserved=new HashSet<int>();
                foreach(int source in loose)
                {
                    int a=root(source);if(protectedRoots.Contains(a)||reserved.Contains(a))continue;
                    int nearest=-1;float best=weldSq;
                    foreach(int candidate in boundaryVertices)
                    {
                        int b=root(candidate);if(a==b||reserved.Contains(b)||connected.Contains(edgeKey(a,b)))continue;
                        float distance=(terrainPositions[a]-terrainPositions[b]).sqrMagnitude;
                        if(distance<best){best=distance;nearest=b;}
                    }
                    if(nearest<0)continue;
                    bool targetProtected=protectedRoots.Contains(nearest);
                    join(a,nearest);int representative=root(a);
                    if(targetProtected)protectedRoots.Add(representative);
                    reserved.Add(representative);changed=true;
                }
                protectedRoots=new HashSet<int>(protectedRoots.Select(root));
                if(!changed)break;
            }
            // Density and rim stitching are finished. Collapse short connected edges too,
            // not just disconnected crack vertices. Keep the terrain rim fixed so the
            // independently rendered terrain still has exactly the same boundary.
            if(weld>0)
            {
                var faces=new List<int[]>();var incident=new Dictionary<int,HashSet<int>>();
                foreach(var triangles in submeshes)for(int i=0;i+2<triangles.Count;i+=3)
                {
                    int face=faces.Count;var ids=new[]{triangles[i],triangles[i+1],triangles[i+2]};faces.Add(ids);
                    foreach(int id in ids)
                    {
                        int r=root(id);if(!incident.TryGetValue(r,out var set))incident[r]=set=new HashSet<int>();set.Add(face);
                    }
                }
                var candidates=edgeCounts().Keys.Select(key=>((int)(key>>32),(int)(key&0xffffffff)))
                    .Where(e=>(terrainPositions[e.Item1]-terrainPositions[e.Item2]).sqrMagnitude<=weldSq)
                    .OrderBy(e=>(terrainPositions[e.Item1]-terrainPositions[e.Item2]).sqrMagnitude)
                    .ThenBy(e=>e.Item1).ThenBy(e=>e.Item2).ToArray();
                foreach(var candidate in candidates)
                {
                    int a=root(candidate.Item1),b=root(candidate.Item2);
                    if(a==b||(terrainPositions[a]-terrainPositions[b]).sqrMagnitude>weldSq)continue;
                    bool movableA=a>=bridgeStart&&a<bridgeEnd&&!protectedRoots.Contains(a);
                    bool movableB=b>=bridgeStart&&b<bridgeEnd&&!protectedRoots.Contains(b);
                    if(!movableA&&!movableB)continue;
                    int remove=movableA?a:b,keep=remove==a?b:a;
                    var neighboursA=new HashSet<int>();var neighboursB=new HashSet<int>();
                    var opposite=new HashSet<int>();bool safe=true;int shared=0;
                    var affected=new HashSet<int>(incident[remove]);affected.UnionWith(incident[keep]);
                    foreach(int face in affected)
                    {
                            var triangle=faces[face];
                            int x=root(triangle[0]),y=root(triangle[1]),z=root(triangle[2]);
                            if(x==y||y==z||z==x)continue;
                            bool hasA=x==remove||y==remove||z==remove;
                            bool hasB=x==keep||y==keep||z==keep;
                            if(hasA){neighboursA.Add(x);neighboursA.Add(y);neighboursA.Add(z);}
                            if(hasB){neighboursB.Add(x);neighboursB.Add(y);neighboursB.Add(z);}
                            if(hasA&&hasB)
                            {
                                shared++;opposite.Add(x!=remove&&x!=keep?x:y!=remove&&y!=keep?y:z);continue;
                            }
                            if(!hasA)continue;
                            Vector3 oldNormal=Vector3.Cross(terrainPositions[y]-terrainPositions[x],terrainPositions[z]-terrainPositions[x]);
                            int nx=x==remove?keep:x,ny=y==remove?keep:y,nz=z==remove?keep:z;
                            Vector3 newNormal=Vector3.Cross(terrainPositions[ny]-terrainPositions[nx],terrainPositions[nz]-terrainPositions[nx]);
                            if(newNormal.sqrMagnitude<1e-14f||Vector3.Dot(oldNormal,newNormal)<=0){safe=false;break;}
                    }
                    // The edge-collapse link condition prevents pinched surfaces and
                    // duplicate faces. Boundary edges are deliberately not collapsed.
                    neighboursA.Remove(remove);neighboursA.Remove(keep);
                    neighboursB.Remove(remove);neighboursB.Remove(keep);
                    neighboursA.IntersectWith(neighboursB);
                    if(!safe||shared!=2||!neighboursA.SetEquals(opposite)||opposite.Count!=2)continue;
                    parent[remove]=keep;
                    incident[keep]=affected;incident.Remove(remove);
                }
            }
            var uniqueTriangles=new HashSet<(int,int,int)>();
            for(int sub=0;sub<submeshes.Length;sub++)
            {
                var input=submeshes[sub];var output=new List<int>(input.Count);
                for(int i=0;i+2<input.Count;i+=3)
                {
                    int a=root(input[i]),b=root(input[i+1]),c=root(input[i+2]);
                    if(a==b||b==c||a==c)continue;
                    if(Vector3.Cross(terrainPositions[b]-terrainPositions[a],terrainPositions[c]-terrainPositions[a]).sqrMagnitude<1e-14f)continue;
                    int lo=Math.Min(a,Math.Min(b,c)),hi=Math.Max(a,Math.Max(b,c)),mid=a+b+c-lo-hi;
                    if(!uniqueTriangles.Add((lo,mid,hi)))continue;
                    output.Add(a);output.Add(b);output.Add(c);
                }
                submeshes[sub]=output;
            }
            protectedRoots=new HashSet<int>(protectedRoots.Select(root));
            OrientConnectedFaces(submeshes);
            var finalEdges=edgeCounts();
            var actualOuter=new HashSet<long>(GeometryEdgeComparer);
            foreach(var pair in finalEdges)
            {
                if(pair.Value>2)throw new InvalidOperationException("Weld Distance creates a non-manifold bridge. Reduce Weld Distance.");
                if(pair.Value!=1)continue;
                int a=root((int)(pair.Key>>32)),b=root((int)(pair.Key&0xffffffff));
                if(!protectedRoots.Contains(a)||!protectedRoots.Contains(b))
                    throw new InvalidOperationException("Bridge still contains an open crack. Increase Weld Distance or Grid Density.");
                actualOuter.Add(edgeKey(a,b));
            }
            if(outerEdges!=null)
            {
                var expected=new HashSet<long>(outerEdges.Select(e=>edgeKey(root((int)(e>>32)),root((int)(e&0xffffffff)))),GeometryEdgeComparer);
                if(!actualOuter.SetEquals(expected))throw new InvalidOperationException("Bridge no longer matches the complete terrain rim; previous geometry preserved.");
            }
            // Compact after remapping; unused isolated points disappear here.
            var used=new bool[count];foreach(var triangles in submeshes)foreach(int index in triangles)used[root(index)]=true;
            var remap=Enumerable.Repeat(-1,count).ToArray();var newPositions=new List<Vector3>();var newNormals=new List<Vector3>();
            var newTangents=new List<Vector4>();var newUv=new List<Vector2>();
            for(int i=0;i<count;i++)
            {
                int representative=root(i);if(representative!=i||!used[i])continue;
                remap[i]=newPositions.Count;newPositions.Add(positions[i]);newNormals.Add(normals[i]);newTangents.Add(tangents[i]);newUv.Add(uv[i]);
            }
            for(int sub=0;sub<submeshes.Length;sub++)for(int i=0;i<submeshes[sub].Count;i++)submeshes[sub][i]=remap[root(submeshes[sub][i])];
            positions.Clear();positions.AddRange(newPositions);normals.Clear();normals.AddRange(newNormals);
            tangents.Clear();tangents.AddRange(newTangents);uv.Clear();uv.AddRange(newUv);
        }
static long MeshEdge(int a,int b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
static float MeshContactCell(float density)=>1f/Mathf.Max(.1f,density);
}
