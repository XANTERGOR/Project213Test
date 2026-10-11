using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Conservative endpoint collapses. Rim/UV-chart vertices never move or disappear.
    // Operates on indices only, preserving UVs, baked atlas coordinates and normal frames.
    public static class LTRockLODMath
    {
        struct Face { public int a,b,c,sub; }
        static long Key(int a,int b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
        // Rock only: retain the near policy, then continue
        // from LOD2 with the SAME displacement radii. Far levels cannot restore
        // removed triangles or reset the world-space error budget.
        public static int[][][] RockLevels(Vector3[] positions,int[][] source,float[] blend,float error,int levels,Vector2[][] uvCharts=null,bool protectSourceUV=false)
        {
            if(levels<0||levels>4)throw new ArgumentOutOfRangeException(nameof(levels));
            if(uvCharts!=null)foreach(var uv in uvCharts)if(uv==null||uv.Length!=positions.Length)throw new ArgumentException("Invalid rock UV channel.");
            var result=new int[levels+1][][];
            result[0]=new int[source.Length][];for(int s=0;s<source.Length;s++)result[0][s]=(int[])source[s].Clone();
            // Raw geometry no longer has atlas locks protecting the near levels.
            // Guard source UV0 at every level there; legacy post-atlas callers stay compatible.
            if(levels>=1)result[1]=ReduceCore(positions,source,blend,error,3,.95f,protectSourceUV?uvCharts:null,new float[positions.Length]);
            var radius=new float[positions.Length];
            if(levels>=2)result[2]=ReduceCore(positions,source,blend,error*2,4,.95f,protectSourceUV?uvCharts:null,radius);
            if(levels>=3)result[3]=ReduceCore(positions,result[2],blend,error*4,16,.85f,uvCharts,radius,true);
            if(levels>=4)result[4]=ReduceCore(positions,result[3],blend,error*8,32,.7f,uvCharts,radius,true);
            return result;
        }
        public static int[][] Reduce(Vector3[] positions,int[][] source,float[] blend,float error,int passes)
            =>ReduceCore(positions,source,blend,error,passes,.95f,null,new float[positions.Length]);
        // Inset road + land is a heightfield, unlike a free-form rock. A nearly
        // vertical thin face must never fold across its XZ projection.
        public static int[] ReduceHeightfield(Vector3[] positions,int[] source,float error,int passes,bool[] protectedVertices=null)
            =>ReduceCore(positions,new[]{source},null,error,passes,.95f,null,new float[positions.Length],true,true,protectedVertices)[0];
        static double UVArea(Vector2 a,Vector2 b,Vector2 c)
            =>((double)b.x-a.x)*(c.y-a.y)-((double)b.y-a.y)*(c.x-a.x);
        static int[][] ReduceCore(Vector3[] positions,int[][] source,float[] blend,float error,int passes,float normalCos,Vector2[][] uvCharts,float[] radius,bool bothDirections=false,bool preserveUp=false,bool[] protectedVertices=null)
        {
            int n=positions.Length;var faces=new List<Face>();
            for(int sub=0;sub<source.Length;sub++)for(int t=0;t<source[sub].Length;t+=3)
                faces.Add(new Face{a=source[sub][t],b=source[sub][t+1],c=source[sub][t+2],sub=sub});
            var locked=new bool[n];
            if(protectedVertices!=null){if(protectedVertices.Length!=n)throw new ArgumentException("Invalid protected vertex flags.");Array.Copy(protectedVertices,locked,n);}
            var groups=new Dictionary<Vector3Int,int>();
            for(int i=0;i<n;i++)
            {
                var v=positions[i];var key=new Vector3Int(Mathf.RoundToInt(v.x*10000),Mathf.RoundToInt(v.y*10000),Mathf.RoundToInt(v.z*10000));
                if(groups.TryGetValue(key,out int other)){locked[i]=true;locked[other]=true;}else groups[key]=i;
            }
            for(int pass=0;pass<passes;pass++)
            {
                var edges=new Dictionary<long,(int count,int sub)>();var neighbours=new HashSet<int>[n];var incident=new List<int>[n];
                void Add(int a,int b,int sub)
                {
                    long key=Key(a,b);edges.TryGetValue(key,out var e);
                    if(e.count>0&&e.sub!=sub){locked[a]=true;locked[b]=true;}
                    edges[key]=(e.count+1,sub);
                    (neighbours[a]??=new HashSet<int>()).Add(b);(neighbours[b]??=new HashSet<int>()).Add(a);
                }
                for(int i=0;i<faces.Count;i++)
                {
                    var f=faces[i];Add(f.a,f.b,f.sub);Add(f.b,f.c,f.sub);Add(f.c,f.a,f.sub);
                    (incident[f.a]??=new List<int>()).Add(i);(incident[f.b]??=new List<int>()).Add(i);(incident[f.c]??=new List<int>()).Add(i);
                }
                var candidates=new List<(long key,float length,bool reverse)>();
                foreach(var edge in edges)
                {
                    int a=(int)(edge.Key>>32),b=(int)edge.Key;
                    if(edge.Value.count!=2){locked[a]=true;locked[b]=true;continue;}
                    float length=Vector3.Distance(positions[a],positions[b]);
                    if(length<=error)
                    {
                        candidates.Add((edge.Key,length,false));
                        if(bothDirections)candidates.Add((edge.Key,length,true));
                    }
                }
                candidates.Sort((a,b)=>{int order=a.length.CompareTo(b.length);if(order!=0)return order;order=a.key.CompareTo(b.key);return order!=0?order:a.reverse.CompareTo(b.reverse);});
                var touched=new bool[n];var map=new int[n];for(int i=0;i<n;i++)map[i]=i;
                int changes=0;
                foreach(var candidate in candidates)
                {
                    int a=(int)(candidate.key>>32),b=(int)candidate.key;
                    if(candidate.reverse||!bothDirections&&locked[b]){int swap=a;a=b;b=swap;}
                    if(locked[b]||touched[a]||touched[b]||candidate.length+radius[b]>error)continue;
                    if(blend!=null&&Mathf.Abs(blend[a]-blend[b])>.05f)continue;
                    int shared=0;foreach(int v in neighbours[a])if(neighbours[b].Contains(v))shared++;
                    if(shared!=2)continue; // manifold link condition; do not close a hole/handle
                    bool safe=true;
                    foreach(int id in incident[b])
                    {
                        var f=faces[id];if(f.a==a||f.b==a||f.c==a)continue;
                        foreach(int otherId in incident[a])
                        {
                            var other=faces[otherId];
                            bool Contains(int v)=>v==a||v==f.a||v==f.b||v==f.c;
                            if(Contains(other.a)&&Contains(other.b)&&Contains(other.c)&&other.a!=b&&other.b!=b&&other.c!=b)
                            {safe=false;break;}
                        }
                        if(!safe)break;
                        Vector3 old=Vector3.Cross(positions[f.b]-positions[f.a],positions[f.c]-positions[f.a]);
                        Vector3 pa=positions[f.a==b?a:f.a],pb=positions[f.b==b?a:f.b],pc=positions[f.c==b?a:f.c];
                        Vector3 next=Vector3.Cross(pb-pa,pc-pa);
                        if(next.sqrMagnitude<1e-14f||preserveUp&&next.y<=1e-9f||Vector3.Dot(old.normalized,next.normalized)<normalCos){safe=false;break;}
                        if(uvCharts!=null)foreach(var uv in uvCharts)
                        {
                            double before=UVArea(uv[f.a],uv[f.b],uv[f.c]);
                            double after=UVArea(uv[f.a==b?a:f.a],uv[f.b==b?a:f.b],uv[f.c==b?a:f.c]);
                            // Empty/collapsed source UVs carry no orientation.
                            // Mirrored charts are allowed, new flips or flattened
                            // textured faces are not. No UV/normal/tangent is edited.
                            if(Math.Abs(before)>1e-15&&(before*after<=0||Math.Abs(after)<Math.Abs(before)*1e-6)){safe=false;break;}
                        }
                        if(!safe)break;
                    }
                    if(!safe)continue;
                    map[b]=a;radius[a]=Mathf.Max(radius[a],candidate.length+radius[b]);changes++;
                    touched[a]=touched[b]=true;
                    foreach(int v in neighbours[a])touched[v]=true;foreach(int v in neighbours[b])touched[v]=true;
                }
                if(changes==0)break;
                var nextFaces=new List<Face>(faces.Count);
                foreach(var f in faces)
                {
                    int a=map[f.a],b=map[f.b],c=map[f.c];
                    if(a!=b&&b!=c&&a!=c)nextFaces.Add(new Face{a=a,b=b,c=c,sub=f.sub});
                }
                faces=nextFaces;
            }
            var lists=new List<int>[source.Length];for(int i=0;i<lists.Length;i++)lists[i]=new List<int>();
            foreach(var f in faces){lists[f.sub].Add(f.a);lists[f.sub].Add(f.b);lists[f.sub].Add(f.c);}
            var result=new int[lists.Length][];for(int i=0;i<lists.Length;i++)result[i]=lists[i].ToArray();return result;
        }
    }
}
