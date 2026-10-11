using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Pure geometry: shared by the native menu and the managed regression harness.
    internal static class LTRockFarLODCheck
    {
        internal static string Run()
        {
            CheckUVGuard();
            const int n=24;int checks=0;long near=0,far=0;
            for(int fixture=0;fixture<4;fixture++)
            {
                var p=new Vector3[(n+1)*(n+1)];var uv=new Vector2[p.Length];var mirror=new Vector2[p.Length];
                for(int z=0;z<=n;z++)for(int x=0;x<=n;x++)
                {
                    float u=x/(float)n,v=z/(float)n;int id=z*(n+1)+x;
                    float h=fixture==0?0:fixture==1?Mathf.Sin(u*Mathf.PI)*Mathf.Sin(v*Mathf.PI)*3:
                        fixture==2?Mathf.Sin(u*Mathf.PI*4)*Mathf.Sin(v*Mathf.PI*3)*.3f:u+Mathf.Sin(v*Mathf.PI*2)*.5f;
                    p[id]=new Vector3(u*6,h,v*6);uv[id]=new Vector2(u,v);mirror[id]=new Vector2(1-u,v);
                }
                var a=new List<int>();var b=new List<int>();
                for(int z=0;z<n;z++)for(int x=0;x<n;x++)
                {
                    // A hole exercises a second boundary loop; material division
                    // must keep its complete interface on both sides.
                    if(fixture==2&&x>=10&&x<14&&z>=10&&z<14)continue;
                    int i=z*(n+1)+x;var tris=fixture==3&&x>=n/2?b:a;
                    tris.AddRange(new[]{i,i+n+1,i+1,i+1,i+n+1,i+n+2});
                }
                var source=new[]{a.ToArray(),b.ToArray()};var saved=source.Select(t=>(int[])t.Clone()).ToArray();
                var positions=(Vector3[])p.Clone();var tex=(Vector2[])uv.Clone();
                foreach(float error in new[]{.15f,.5f,2f})
                {
                    var levels=LTRockLODMath.RockLevels(p,source,null,error,4,new[]{uv,mirror});
                    for(int l=0;l<=4;l++)
                    {
                        if(l<=2)
                        {
                            var legacy=l==0?source:LTRockLODMath.Reduce(p,source,null,error*Mathf.Pow(2,l-1),l+2);
                            Require(legacy.Zip(levels[l],(x,y)=>x.SequenceEqual(y)).All(s=>s),"near indices changed");
                        }
                        if(l>=3)Require(levels[l].Sum(t=>t.Length)<=levels[l-1].Sum(t=>t.Length),"far density increased");
                        Validate(p,source,levels[l]);
                        var used=levels[l].SelectMany(t=>t).Distinct().ToArray();
                        float budget=l==0?0:error*Mathf.Pow(2,l-1);
                        foreach(int v in source.SelectMany(t=>t).Distinct())
                            Require(used.Min(u=>Vector3.Distance(p[v],p[u]))<=budget+.0001f,"source vertex lost every endpoint within its metre budget");
                        foreach(var tris in levels[l])for(int t=0;t<tris.Length;t+=3)
                        {
                            Require(Area(uv[tris[t]],uv[tris[t+1]],uv[tris[t+2]])<0,"UV0 flipped/flattened");
                            Require(Area(mirror[tris[t]],mirror[tris[t+1]],mirror[tris[t+2]])>0,"mirrored chart flipped/flattened");
                        }
                        checks++;
                    }
                    near+=source.Sum(t=>t.Length)/3;far+=levels[4].Sum(t=>t.Length)/3;
                    Require(LTRockLODMath.RockLevels(p,source,null,error,4,new[]{uv,mirror})[4].Zip(levels[4],(x,y)=>x.SequenceEqual(y)).All(s=>s),"nondeterministic far LOD");
                }
                Require(source.Zip(saved,(x,y)=>x.SequenceEqual(y)).All(v=>v)&&p.SequenceEqual(positions)&&uv.SequenceEqual(tex),"input streams changed");
            }
            Require(far<near,"far fixture did not reduce");
            return $"strong rock LOD: {checks} levels, {near}->{far} triangles; exact legacy LOD0/1/2, monotone LOD3/4, fixed contact/UV/material rims, holes, UV inversion rejection including pre-atlas near levels, oriented mirrored UVs, deterministic and immutable inputs";
        }
        static void CheckUVGuard()
        {
            var p=new[]{new Vector3(-1,0,-1),new Vector3(1,0,-1),new Vector3(1,0,1),new Vector3(-1,0,1),new Vector3(.9f,0,-.9f)};
            var source=new[]{new[]{0,4,1,1,4,2,2,4,3,3,4,0}};
            // The geometry is convex, but its UV chart has a concave corner.
            // The nearest geometric contraction is legal in 3D and flips a UV face.
            var uv=new[]{new Vector2(-1,-1),new Vector2(1,-1),new Vector2(-.5f,0),new Vector2(-1,1),new Vector2(-.75f,0)};
            var unguarded=LTRockLODMath.RockLevels(p,source,null,.05f,4);
            var guarded=LTRockLODMath.RockLevels(p,source,null,.05f,4,new[]{uv});
            Require(unguarded[3][0].Length<source[0].Length,"UV rejection fixture did not contract");
            Require(guarded[3][0].SequenceEqual(source[0])&&guarded[4][0].SequenceEqual(source[0]),"new UV inversion accepted");
            // With charts generated after LOD, raw UV0 must also protect LOD1/2.
            var raw=LTRockLODMath.RockLevels(p,source,null,.2f,4,new[]{uv},protectSourceUV:true);
            Require(raw.All(l=>l[0].SequenceEqual(source[0])),"pre-atlas source UV0 inversion accepted at a near LOD");
        }
        static double Area(Vector2 a,Vector2 b,Vector2 c)=>((double)b.x-a.x)*(c.y-a.y)-((double)b.y-a.y)*(c.x-a.x);
        internal static void Validate(Vector3[] p,int[][] source,int[][] output)
        {
            Require(source.Length==output.Length,"material slots changed");
            Dictionary<long,(int count,int winding)> Edges(int[][] arrays)
            {
                var edges=new Dictionary<long,(int,int)>();var unique=new HashSet<(int,int,int)>();
                foreach(var indices in arrays)for(int t=0;t<indices.Length;t+=3)
                {
                    int a=indices[t],b=indices[t+1],c=indices[t+2];
                    Require(a!=b&&a!=c&&b!=c&&Vector3.Cross(p[b]-p[a],p[c]-p[a]).sqrMagnitude>1e-14f,"degenerate face");
                    var sorted=new[]{a,b,c};Array.Sort(sorted);Require(unique.Add((sorted[0],sorted[1],sorted[2])),"duplicate face");
                    for(int k=0;k<3;k++)
                    {
                        int x=indices[t+k],y=indices[t+(k+1)%3];long key=((long)Math.Min(x,y)<<32)|(uint)Math.Max(x,y);
                        edges.TryGetValue(key,out var e);edges[key]=(e.Item1+1,e.Item2+(x<y?1:-1));
                    }
                }
                return edges;
            }
            var before=Edges(source);var after=Edges(output);
            Require(after.All(e=>e.Value.count<=2&&(e.Value.count!=2||e.Value.winding==0)),"nonmanifold / incoherent winding");
            Require(before.Where(e=>e.Value.count==1).OrderBy(e=>e.Key).SequenceEqual(after.Where(e=>e.Value.count==1).OrderBy(e=>e.Key)),"contact/chart/hole border changed");
            for(int s=0;s<source.Length;s++)
                Require(Edges(new[]{source[s]}).Where(e=>e.Value.count==1).OrderBy(e=>e.Key).SequenceEqual(Edges(new[]{output[s]}).Where(e=>e.Value.count==1).OrderBy(e=>e.Key)),"material border changed");
        }
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException("Strong rock LOD: "+message);}
    }
}
