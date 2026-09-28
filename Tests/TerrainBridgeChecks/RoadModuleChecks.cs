using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;
partial class Checks
{
    static void RoadModuleChecks()
    {
        const int nx=8,nz=16;
        var v=new List<Vector3>();var n=new List<Vector3>();var t=new List<Vector4>();var uv=new List<Vector2>();var colors=new List<Color>();var faces=new List<int>();
        for(int z=0;z<=nz;z++)for(int x=0;x<=nx;x++)
        {v.Add(new Vector3(-2+x*.5f,0,z*.5f));n.Add(Vector3.up);t.Add(new Vector4(1,0,0,-1));uv.Add(new Vector2(x/(float)nx,z/(float)nz));colors.Add(new Color(x/(float)nx,0,z/(float)nz,1));}
        for(int z=0;z<nz;z++)for(int x=0;x<nx;x++)
        {int a=z*(nx+1)+x,b=a+nx+1;faces.AddRange(new[]{a,b,a+1,a+1,b,b+1});}
        var source=new LTRoadMesh.Chunk{vertices=v.ToArray(),normals=n.ToArray(),tangents=t.ToArray(),uv=uv.ToArray(),uv2=uv.ToArray(),colors=colors.ToArray(),triangles=faces.ToArray(),submeshes=new[]{faces.ToArray()}};
        string hash=LTRoadMesh.ContentHash(source);
        foreach(float ratio in new[]{.5f,.25f,.125f})
        {
            var reduced=LTRoadModuleMath.Simplify(source,ratio,.02f);
            Require(reduced.triangles.Length<source.triangles.Length,"automatic module LOD reduces grid");
            var border=new HashSet<Vector3>(source.vertices.Where(p=>p.x==-2||p.x==2||p.z==0||p.z==8));
            var reducedBorder=new HashSet<Vector3>(reduced.vertices.Where(p=>p.x==-2||p.x==2||p.z==0||p.z==8));
            Require(border.SetEquals(reducedBorder),"module open edges and torces locked");
            var edgeCounts=new Dictionary<(int,int),int>();
            for(int i=0;i<reduced.triangles.Length;i+=3)
            {
                var a=reduced.vertices[reduced.triangles[i]];var b=reduced.vertices[reduced.triangles[i+1]];var c=reduced.vertices[reduced.triangles[i+2]];
                Require(Vector3.Cross(b-a,c-a).y>0,"simplifier never flips or collapses a face");
                for(int k=0;k<3;k++){int x=reduced.triangles[i+k],y=reduced.triangles[i+(k+1)%3];var key=(Math.Min(x,y),Math.Max(x,y));edgeCounts.TryGetValue(key,out int count);edgeCounts[key]=count+1;}
            }
            Require(edgeCounts.Values.All(c=>c<=2),"simplifier remains manifold");
            Require(LTRoadMesh.ContentHash(reduced)==LTRoadMesh.ContentHash(LTRoadModuleMath.Simplify(source,ratio,.02f)),"deterministic simplification");
        }
        var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.Asphalt;settings.width=6;settings.sampleSpacing=.5f;
        var path=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(0,2,0),5),new LTRoadPoint(new Vector3(0,4,25),12),new LTRoadPoint(new Vector3(10,6,50),-5)},Matrix4x4.identity,settings);
        var chunks=LTRoadModuleMath.Bend(source,path,new Vector3(-2,0,0),new Vector3(2,0,8),8,true,0,.06f,8,Matrix4x4.identity);
        Require(chunks.Count>1,"module partitioned into sections");
        for(int c=0;c<chunks.Count;c++)
        {
            var chunk=chunks[c];Require(chunk.vertices.Length==source.vertices.Length,"fixture exactly one repeat per section");
            Require(chunk.uv.SequenceEqual(source.uv)&&chunk.uv2.SequenceEqual(source.uv2)&&chunk.colors.SequenceEqual(source.colors),"custom UVs and colors preserved");
            for(int i=0;i<chunk.vertices.Length;i++)Require(Math.Abs(chunk.normals[i].magnitude-1)<1e-4f,"warped normals normalized");
            if(c>0)for(int x=0;x<=nx;x++)
            {Require((chunks[c-1].vertices[nz*(nx+1)+x]-chunk.vertices[x]).sqrMagnitude<1e-10f,"repeat seam geometry");Require(Vector3.Dot(chunks[c-1].normals[nz*(nx+1)+x],chunk.normals[x])>.99999f,"repeat seam shading");}
        }
        Require(hash==LTRoadMesh.ContentHash(source),"bake and LOD never mutate source");
        Console.WriteLine("PASS custom asphalt: automatic reduction, determinism, locked edges, manifold/winding, curved/banked spline repeats, endpoint normals, UV/color preservation, unchanged source. No native prefab/import/render test.");
    }
}
