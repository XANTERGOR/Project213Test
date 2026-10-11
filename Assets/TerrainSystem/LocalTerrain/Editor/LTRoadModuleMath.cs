using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace LocalTerrainPrototype
{
    // Pure, deterministic preparation. Never edits the imported mesh.
    public static class LTRoadModuleMath
    {
        static Vector3 NormalTransform(Matrix4x4 m,Vector3 n)
        {
            var x=m.MultiplyVector(Vector3.right);var y=m.MultiplyVector(Vector3.up);var z=m.MultiplyVector(Vector3.forward);
            return (Vector3.Cross(y,z)*n.x+Vector3.Cross(z,x)*n.y+Vector3.Cross(x,y)*n.z).normalized;
        }
        public static LTRoadMath.Hit Along(LTRoadMath.Snapshot path,float distance)
        {
            var s=path.samples;distance=Mathf.Clamp(distance,0,path.length);int lo=0,hi=s.Count-1;
            while(hi-lo>1){int mid=(lo+hi)/2;if(s[mid].distance<=distance)lo=mid;else hi=mid;}
            float t=(distance-s[lo].distance)/(s[hi].distance-s[lo].distance);
            return new LTRoadMath.Hit{position=Vector3.LerpUnclamped(s[lo].position,s[hi].position,t),right=Vector3.LerpUnclamped(s[lo].right,s[hi].right,t).normalized,
                bank=Mathf.LerpUnclamped(s[lo].bank,s[hi].bank,t),distance=distance};
        }
        public static LTRoadMesh.Chunk Compact(LTRoadMesh.Chunk source,int[][] faces)
        {
            var map=new Dictionary<int,int>();var old=new List<int>();
            var sub=new int[faces.Length][];
            for(int s=0;s<faces.Length;s++)
            {sub[s]=new int[faces[s].Length];for(int i=0;i<faces[s].Length;i++){int id=faces[s][i];if(!map.TryGetValue(id,out int next)){next=map.Count;map.Add(id,next);old.Add(id);}sub[s][i]=next;}}
            T[] Pick<T>(T[] a)=>a!=null&&a.Length==source.vertices.Length?old.Select(i=>a[i]).ToArray():null;
            var result=new LTRoadMesh.Chunk{vertices=Pick(source.vertices),normals=Pick(source.normals),tangents=Pick(source.tangents),uv=Pick(source.uv),
                uv2=Pick(source.uv2),uv3=Pick(source.uv3),uv4=Pick(source.uv4),colors=Pick(source.colors),submeshes=sub,triangles=sub.SelectMany(a=>a).ToArray()};
            result.hash=LTRoadMesh.ContentHash(result);return result;
        }
        public static LTRoadMesh.Chunk RibbonLOD(LTRoadMesh.Chunk source,int level)
        {
            int rows=source.vertices.Length/2,step=1<<Mathf.Clamp(level,0,8);var indices=new List<int>();
            int first=0;while(first<rows-1)
            {
                int last=Math.Min(rows-1,first+step),a=first*2,b=last*2;
                indices.AddRange(new[]{a,b,a+1,a+1,b,b+1});first=last;
            }
            return Compact(source,new[]{indices.ToArray()});
        }
        public static List<LTRoadMesh.Chunk> Bend(LTRoadMesh.Chunk source,LTRoadMath.Snapshot path,
            Vector3 referenceMin,Vector3 referenceMax,float desiredLength,bool fitWidth,float baseY,float offset,float chunkLength,Matrix4x4 toRoad)
            =>BendVariants(new[]{source},new[]{1f},0,false,path,referenceMin,referenceMax,desiredLength,fitWidth,baseY,offset,chunkLength,toRoad);

        public static int[] SelectModules(float[] weights,int count,int seed,bool avoidRepeat)
        {
            if(weights==null||weights.Length==0||weights.Length>32||count<0||count>32768)throw new InvalidOperationException("Нужны 1–32 варианта и не более 32768 повторов.");
            foreach(float w in weights)if(!LTRoadMesh.Finite(w)||w<0)throw new InvalidOperationException("Вес модуля должен быть конечным и неотрицательным.");
            int active=weights.Count(w=>w>0);if(active==0)throw new InvalidOperationException("В наборе нет модулей с положительным весом.");
            var result=new int[count];int previous=-1;
            for(int i=0;i<count;i++)
            {
                int excluded=avoidRepeat&&active>1?previous:-1;double total=0;
                for(int m=0;m<weights.Length;m++)if(m!=excluded)total+=weights[m];
                uint hash;unchecked{hash=(uint)seed^(uint)i*0x9e3779b9u;hash^=hash>>16;hash*=0x7feb352du;hash^=hash>>15;hash*=0x846ca68bu;hash^=hash>>16;}
                double pick=(hash/4294967296.0)*total;int selected=-1;
                for(int m=0;m<weights.Length;m++)if(m!=excluded&&weights[m]>0)
                {selected=m;pick-=weights[m];if(pick<0)break;}
                result[i]=previous=selected;
            }
            return result;
        }
        public static LTRoadMesh.Chunk NormalizeLength(LTRoadMesh.Chunk source,float minZ,float maxZ,float targetMin,float targetMax,bool allowEndOvershoot=false)
        {
            if(maxZ-minZ<.001f||targetMax-targetMin<.001f)throw new InvalidOperationException("Модуль должен иметь ненулевую продольную длину.");
            if(minZ==targetMin&&maxZ==targetMax)return source;
            var output=Compact(source,source.submeshes??new[]{source.triangles});float scale=(targetMax-targetMin)/(maxZ-minZ);
            for(int i=0;i<output.vertices.Length;i++)
            {
                var p=output.vertices[i];
                if(!allowEndOvershoot&&(p.z<minZ-.00001f||p.z>maxZ+.00001f))throw new InvalidOperationException("Готовый LOD выходит за длину своего модуля LOD0.");
                p.z=Math.Abs(p.z-minZ)<.00001f?targetMin:Math.Abs(p.z-maxZ)<.00001f?targetMax:targetMin+(p.z-minZ)*scale;
                output.vertices[i]=p;var n=output.normals[i];n=new Vector3(n.x,n.y,n.z/scale).normalized;output.normals[i]=n;
                var st=output.tangents[i];var t=new Vector3(st.x,st.y,st.z*scale);t=(t-n*Vector3.Dot(n,t)).normalized;
                output.tangents[i]=new Vector4(t.x,t.y,t.z,st.w);
            }
            output.hash=LTRoadMesh.ContentHash(output);return output;
        }
        public static List<LTRoadMesh.Chunk> BendVariants(LTRoadMesh.Chunk[] sources,float[] weights,int seed,bool avoidRepeat,LTRoadMath.Snapshot path,
            Vector3 referenceMin,Vector3 referenceMax,float desiredLength,bool fitWidth,float baseY,float offset,float chunkLength,Matrix4x4 toRoad)
        {
            float originalLength=referenceMax.z-referenceMin.z,originalWidth=referenceMax.x-referenceMin.x;
            if(originalLength<.001f||originalWidth<.001f)throw new InvalidOperationException("Модуль должен иметь ненулевую длину Z и ширину X после выбора оси.");
            float target=desiredLength>0?desiredLength:originalLength;
            int count=Math.Max(1,(int)Math.Ceiling(path.length/target));
            if(sources.Length!=weights.Length)throw new InvalidOperationException("Набор модулей не совпадает с весами.");
            var choices=SelectModules(weights,count,seed,avoidRepeat);
            if(choices.Sum(i=>(long)sources[i].vertices.Length)>4000000)throw new InvalidOperationException("Модуль дороги превышает бюджет 4 млн вершин. Увеличьте длину модуля или упростите его.");
            float length=path.length/count,scaleX=fitWidth?path.width/originalWidth:1,centerX=(referenceMin.x+referenceMax.x)*.5f;
            int perChunk=Math.Max(1,(int)Math.Floor(chunkLength/length));
            if((count+perChunk-1)/perChunk>LTRoadMesh.MaxChunks)throw new InvalidOperationException("Слишком много секций дороги. Увеличьте длину чанка.");
            var result=new List<LTRoadMesh.Chunk>();
            for(int start=0;start<count;start+=perChunk)
            {
                int repeats=Math.Min(perChunk,count-start),vertices=0;
                for(int c=0;c<repeats;c++)vertices+=sources[choices[start+c]].vertices.Length;
                var output=new LTRoadMesh.Chunk{vertices=new Vector3[vertices],normals=new Vector3[vertices],tangents=new Vector4[vertices],uv=new Vector2[vertices]};
                if(sources.Any(s=>s.uv2!=null))output.uv2=new Vector2[vertices];if(sources.Any(s=>s.uv3!=null))output.uv3=new Vector2[vertices];
                if(sources.Any(s=>s.uv4!=null))output.uv4=new Vector2[vertices];if(sources.Any(s=>s.colors!=null))output.colors=Enumerable.Repeat(Color.white,vertices).ToArray();
                int subCount=sources.Max(s=>s.submeshes?.Length??1);var subs=Enumerable.Range(0,subCount).Select(_=>new List<int>()).ToArray();
                Vector3 Position(float distance,float x,float y)
                {var frame=Along(path,distance);var p=frame.position+frame.right*x;p.y=path.SurfaceHeight(frame,x)+offset+y;return p;}
                int vertexStart=0;
                for(int copy=0;copy<repeats;copy++)
                {
                var source=sources[choices[start+copy]];int n=source.vertices.Length;
                for(int i=0;i<n;i++)
                {
                    int id=vertexStart+i;var p=source.vertices[i];float x=(p.x-centerX)*scaleX;
                    // Evaluate shared module endpoints through the exact same distance expression.
                    float fraction=(p.z-referenceMin.z)/originalLength;
                    float distance=(start+copy+fraction)*length;
                    var frame=Along(path,distance);
                    var dx=(frame.right+Vector3.up*Mathf.Tan(frame.bank*Mathf.Deg2Rad))*scaleX;
                    var dy=Vector3.up;
                    float a=Math.Max(0,distance-.01f),b=Math.Min(path.length,distance+.01f);
                    var dz=(Position(b,x,0)-Position(a,x,0))*(length/originalLength/(b-a));
                    float determinant=Vector3.Dot(dx,Vector3.Cross(dy,dz));
                    if(determinant<=1e-8f)throw new InvalidOperationException("Модуль складывается на повороте: уменьшите ширину или сделайте сплайн плавнее.");
                    var sn=source.normals[i];
                    var normal=(Vector3.Cross(dy,dz)*sn.x+Vector3.Cross(dz,dx)*sn.y+Vector3.Cross(dx,dy)*sn.z).normalized;
                    var st=source.tangents[i];var tangent=dx*st.x+dy*st.y+dz*st.z;
                    normal=NormalTransform(toRoad,normal);tangent=toRoad.MultiplyVector(tangent);tangent=(tangent-normal*Vector3.Dot(normal,tangent)).normalized;
                    output.vertices[id]=toRoad.MultiplyPoint3x4(Position(distance,x,p.y-baseY));output.normals[id]=normal;
                    output.tangents[id]=new Vector4(tangent.x,tangent.y,tangent.z,st.w);output.uv[id]=source.uv[i];
                    if(source.uv2!=null)output.uv2[id]=source.uv2[i];if(source.uv3!=null)output.uv3[id]=source.uv3[i];if(source.uv4!=null)output.uv4[id]=source.uv4[i];if(source.colors!=null)output.colors[id]=source.colors[i];
                }
                var sourceSubs=source.submeshes??new[]{source.triangles};
                for(int s=0;s<sourceSubs.Length;s++)foreach(int id in sourceSubs[s])subs[s].Add(vertexStart+id);
                vertexStart+=n;
                }
                output.submeshes=subs.Select(s=>s.ToArray()).ToArray();output.triangles=output.submeshes.SelectMany(s=>s).ToArray();
                output.hash=LTRoadMesh.ContentHash(output);result.Add(output);
            }
            return result;
        }

        struct Edge : IComparable<Edge>
        {
            public int a,b,versionA,versionB,serial;public float cost;
            public int CompareTo(Edge other){int c=cost.CompareTo(other.cost);return c!=0?c:serial.CompareTo(other.serial);}
        }
        // Conservative endpoint edge collapse: open/material/UV boundaries and ends stay fixed.
        // Link condition and normal checks reject nonmanifold or inverted collapses.
        public static LTRoadMesh.Chunk Simplify(LTRoadMesh.Chunk source,float ratio,float maxError)
        {
            int n=source.vertices.Length;var faces=source.submeshes??new[]{source.triangles};
            if(n>20000)throw new InvalidOperationException("Авто-LOD модуля: максимум 20000 исходных вершин. Используйте готовые LOD для более крупного модуля.");
            var tris=new List<int[]>();var materials=new List<int>();var adjacency=new HashSet<int>[n];
            for(int i=0;i<n;i++)adjacency[i]=new HashSet<int>();
            for(int s=0;s<faces.Length;s++)for(int i=0;i<faces[s].Length;i+=3)
            {int f=tris.Count;var t=new[]{faces[s][i],faces[s][i+1],faces[s][i+2]};tris.Add(t);materials.Add(s);foreach(int v in t)adjacency[v].Add(f);}
            var locked=new bool[n];var versions=new int[n];var alive=Enumerable.Repeat(true,n).ToArray();
            var planes=new HashSet<Vector4>[n];for(int i=0;i<n;i++)planes[i]=new HashSet<Vector4>();
            foreach(var t in tris)
            {
                var normal=Vector3.Cross(source.vertices[t[1]]-source.vertices[t[0]],source.vertices[t[2]]-source.vertices[t[0]]).normalized;
                var plane=new Vector4(normal.x,normal.y,normal.z,-Vector3.Dot(normal,source.vertices[t[0]]));
                foreach(int v in t)planes[v].Add(plane);
            }
            float minZ=source.vertices.Min(p=>p.z),maxZ=source.vertices.Max(p=>p.z);
            var edgeFaces=new Dictionary<(int,int),List<int>>();
            for(int f=0;f<tris.Count;f++)for(int k=0;k<3;k++)
            {int a=tris[f][k],b=tris[f][(k+1)%3];var key=(Math.Min(a,b),Math.Max(a,b));if(!edgeFaces.TryGetValue(key,out var list))edgeFaces[key]=list=new List<int>();list.Add(f);}
            foreach(var pair in edgeFaces)if(pair.Value.Count!=2||materials[pair.Value[0]]!=materials[pair.Value[1]])locked[pair.Key.Item1]=locked[pair.Key.Item2]=true;
            for(int i=0;i<n;i++)if(Math.Abs(source.vertices[i].z-minZ)<.00001f||Math.Abs(source.vertices[i].z-maxZ)<.00001f)locked[i]=true;
            var queue=new SortedSet<Edge>();int serial=0;
            void Enqueue(int a,int b)
            {
                if(a==b||!alive[a]||!alive[b]||(locked[a]&&locked[b]))return;
                if(locked[a]||(!locked[b]&&a>b)){int swap=a;a=b;b=swap;}
                float d=Vector3.Distance(source.vertices[a],source.vertices[b]);
                float error=0;var p=source.vertices[b];
                foreach(var plane in planes[a])error=Math.Max(error,Math.Abs(plane.x*p.x+plane.y*p.y+plane.z*p.z+plane.w));
                if(error>maxError)return;
                queue.Add(new Edge{a=a,b=b,versionA=versions[a],versionB=versions[b],cost=error*error+d*d*.00001f,serial=serial++});
                if(queue.Count>2000000)throw new InvalidOperationException("Авто-LOD превысил бюджет упрощения. Используйте готовые LOD.");
            }
            foreach(var key in edgeFaces.Keys)Enqueue(key.Item1,key.Item2);
            int remaining=tris.Count,target=Math.Max(1,(int)(remaining*ratio));
            while(queue.Count>0&&remaining>target)
            {
                var e=queue.Min;queue.Remove(e);int a=e.a,b=e.b;
                if(!alive[a]||!alive[b]||versions[a]!=e.versionA||versions[b]!=e.versionB||locked[a])continue;
                var common=adjacency[a].Intersect(adjacency[b]).ToArray();if(common.Length!=2)continue;
                HashSet<int> Neighbours(int v)=>new HashSet<int>(adjacency[v].SelectMany(f=>tris[f]).Where(i=>i!=v));
                var na=Neighbours(a);var nb=Neighbours(b);na.IntersectWith(nb);if(na.Count!=2)continue;
                bool valid=true;
                foreach(int f in adjacency[a])
                {
                    var t=tris[f];if(t.Contains(b))continue;
                    var oldN=Vector3.Cross(source.vertices[t[1]]-source.vertices[t[0]],source.vertices[t[2]]-source.vertices[t[0]]);
                    Vector3 P(int i)=>source.vertices[t[i]==a?b:t[i]];
                    var newN=Vector3.Cross(P(1)-P(0),P(2)-P(0));
                    if(newN.sqrMagnitude<1e-14f||Vector3.Dot(oldN.normalized,newN.normalized)<.5f){valid=false;break;}
                }
                if(!valid)continue;
                var affected=Neighbours(a);affected.UnionWith(Neighbours(b));affected.Add(b);
                foreach(int f in adjacency[a].ToArray())
                {
                    var t=tris[f];foreach(int v in t)adjacency[v].Remove(f);
                    if(t.Contains(b)){tris[f]=null;remaining--;continue;}
                    for(int k=0;k<3;k++)if(t[k]==a)t[k]=b;
                    foreach(int v in t)adjacency[v].Add(f);
                }
                planes[b].UnionWith(planes[a]);alive[a]=false;
                foreach(int v in affected)versions[v]++;
                foreach(int v in affected)foreach(int neighbour in Neighbours(v))Enqueue(v,neighbour);
            }
            var output=new List<int>[faces.Length];for(int i=0;i<output.Length;i++)output[i]=new List<int>();
            for(int f=0;f<tris.Count;f++)if(tris[f]!=null)output[materials[f]].AddRange(tris[f]);
            return Compact(source,output.Select(s=>s.ToArray()).ToArray());
        }
    }
}
