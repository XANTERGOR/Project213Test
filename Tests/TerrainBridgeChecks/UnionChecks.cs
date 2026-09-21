using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using LocalTerrainPrototype;
partial class Checks
{
    static List<LTUnionGeometry.Face> Box(Vector3 centre,Vector3 size,int owner)
    {
        var p=new[]{new Vector3(-1,-1,-1),new Vector3(1,-1,-1),new Vector3(1,-1,1),new Vector3(-1,-1,1),
            new Vector3(-1,1,-1),new Vector3(1,1,-1),new Vector3(1,1,1),new Vector3(-1,1,1)}.Select(v=>centre+Vector3.Scale(v,size)*.5f).ToArray();
        var tris=new[]{0,1,2,0,2,3,4,6,5,4,7,6,0,5,1,0,4,5,1,6,2,1,5,6,2,7,3,2,6,7,3,4,0,3,7,4};
        var faces=new List<LTUnionGeometry.Face>();
        for(int i=0;i<tris.Length;i+=3)faces.Add(new LTUnionGeometry.Face(new[]{
            new LTUnionGeometry.Vertex(p[tris[i]],Vector2.zero),new LTUnionGeometry.Vertex(p[tris[i+1]],Vector2.right),new LTUnionGeometry.Vertex(p[tris[i+2]],Vector2.up)},owner,owner));
        return faces;
    }
    static double Volume(LTUnionGeometry.Surface s)
    {
        double volume=0;var o=s.vertices[0].p;
        foreach(var t in s.triangles)volume+=Vector3.Dot(s.vertices[t[0]].p-o,Vector3.Cross(s.vertices[t[1]].p-o,s.vertices[t[2]].p-o))/6;
        return volume;
    }
    static void UnionChecks()
    {
        const int budget=50000;
        foreach(var shift in new[]{Vector3.zero,new Vector3(200,10,95)})
        {
            var a=Box(shift,Vector3.one*2,0);var b=Box(shift+Vector3.right,Vector3.one*2,1);
            var surface=LTUnionGeometry.Triangulate(LTUnionGeometry.Union(a,b,budget),budget);
            Require(Math.Abs(Volume(surface)-12)<.001,"Union volume (internal faces remain or external faces removed)");
            var reversed=LTUnionGeometry.Triangulate(LTUnionGeometry.Union(b,a,budget),budget);
            Require(Math.Abs(Volume(reversed)-Volume(surface))<.001,"Union order changes volume");
            var before=surface.vertices.Select(v=>v.p).ToArray();
            var seam=surface.seam.ToArray();
            LTUnionGeometry.RemeshSeam(surface,p=>.5f,.4f,budget);
            Require(surface.vertices.Count>=before.Length,"remesh corrupted vertices");
            Require(surface.vertices.All(v=>float.IsFinite(v.p.x)&&float.IsFinite(v.p.y)&&float.IsFinite(v.p.z)),"nonfinite Union coordinates");
            float Distance(Vector3 p)
            {float best=float.MaxValue;for(int i=0;i+1<seam.Length;i+=2){var d=seam[i+1]-seam[i];float t=Mathf.Clamp01(Vector3.Dot(p-seam[i],d)/d.sqrMagnitude);best=Math.Min(best,Vector3.Distance(p,seam[i]+t*d));}return best;}
            for(int i=0;i<before.Length;i++)if(Distance(before[i])>=.4f)Require(surface.vertices[i].p==before[i],"remesh changed geometry outside its band");
            LTUnionGeometry.ValidateClosed(surface);
        }
        var outer=Box(Vector3.zero,Vector3.one*4,0);var inner=Box(Vector3.zero,Vector3.one,1);
        var contained=LTUnionGeometry.Triangulate(LTUnionGeometry.Union(outer,inner,budget),budget);
        Require(Math.Abs(Volume(contained)-64)<.001,"contained union");
        var disjoint=LTUnionGeometry.Triangulate(LTUnionGeometry.Union(Box(Vector3.zero,Vector3.one*2,0),Box(Vector3.right*5,Vector3.one*2,1),budget),budget);
        Require(Math.Abs(Volume(disjoint)-16)<.001,"disjoint union");
        Vector3 Rotate(Vector3 p){float c=(float)Math.Cos(27*Math.PI/180),s=(float)Math.Sin(27*Math.PI/180);return new Vector3(c*p.x+s*p.z,p.y,-s*p.x+c*p.z);}
        var rotated=Box(new Vector3(.8f,0,.3f),new Vector3(2,4,2),1).Select(f=>new LTUnionGeometry.Face(f.vertices.Select(v=>new LTUnionGeometry.Vertex(Rotate(v.p),v.uv)),f.material,f.owner)).ToList();
        var joint=LTUnionGeometry.Triangulate(LTUnionGeometry.Union(Box(Vector3.zero,new Vector3(2,4,2),0),rotated,budget),budget);
        LTUnionGeometry.RemeshSeam(joint,p=>Mathf.Lerp(.3f,.7f,Mathf.SmoothStep(0,1,(p.x+2)/4)),.5f,budget);
        foreach(float offset in new[]{.5f,2f})foreach(float height in new[]{.1f,.6f})
            Case(offset,height,.7f,1,.1f,false,.1f,false,joint.vertices.Select(v=>v.p).ToArray(),joint.triangles.SelectMany(t=>t).ToArray(),p=>Mathf.Lerp(.7f,1.5f,Mathf.SmoothStep(0,1,(p.x-198)/4)));
        var third=LTUnionGeometry.Triangulate(LTUnionGeometry.Union(LTUnionGeometry.Union(Box(Vector3.zero,Vector3.one*2,0),Box(Vector3.right,Vector3.one*2,1),budget),Box(Vector3.right*2,Vector3.one*2,2),budget),budget);
        Require(Math.Abs(Volume(third)-16)<.001,"three-object union volume");
        var lShape=LTUnionGeometry.Union(LTUnionGeometry.Union(Box(Vector3.zero,Vector3.one*2,0),Box(Vector3.right,Vector3.one*2,1),budget),Box(Vector3.forward,Vector3.one*2,2),budget);
        Require(Math.Abs(Volume(LTUnionGeometry.Triangulate(lShape,budget))-16)<.001,"nonconvex group union volume");
        var same=Box(Vector3.zero,Vector3.one*2,0);
        var junction=Box(Vector3.zero,Vector3.one*2,0);
        var originalFace=junction[0];var va=originalFace.vertices[0];var vb=originalFace.vertices[1];var vc=originalFace.vertices[2];
        var middle=LTUnionGeometry.Vertex.Lerp(va,vb,.5f);middle.p+=new Vector3(0,0,.00006f);
        junction.RemoveAt(0);
        junction.Insert(0,new LTUnionGeometry.Face(new[]{va,middle,vc},0,0));
        junction.Insert(1,new LTUnionGeometry.Face(new[]{middle,vb,vc},0,0));
        LTUnionGeometry.ValidateClosed(LTUnionGeometry.Triangulate(junction,budget));
        var translatedJunction=junction.Select(f=>new LTUnionGeometry.Face(f.vertices.Select(v=>new LTUnionGeometry.Vertex(v.p+new Vector3(86,8,110),v.uv)),f.material,f.owner)).ToList();
        LTUnionGeometry.ValidateClosed(LTUnionGeometry.Triangulate(translatedJunction,budget));
        Console.WriteLine("PASS near-edge T-junction reconciliation at origin and translated scene coordinates.");
        Require(Math.Abs(Volume(LTUnionGeometry.Triangulate(LTUnionGeometry.Union(same,same,budget),budget))-8)<.001,"identical objects");
        Require(same.Count==12,"Union modified source faces");
        bool openRejected=false;try{LTUnionGeometry.Triangulate(same.Take(11).ToList(),budget);}catch(InvalidOperationException){openRejected=true;}
        Require(openRejected,"open source must be rejected");
        List<LTUnionGeometry.Face> Prism(int sides)
        {
            var lower=new List<LTUnionGeometry.Vertex>();var upper=new List<LTUnionGeometry.Vertex>();
            for(int i=0;i<sides;i++)
            {
                float x=5*(float)Math.Cos(2*Math.PI*i/sides),z=5*(float)Math.Sin(2*Math.PI*i/sides);
                lower.Add(new LTUnionGeometry.Vertex(new Vector3(x,-1,z),Vector2.zero));upper.Add(new LTUnionGeometry.Vertex(new Vector3(x,1,z),Vector2.zero));
            }
            var result=new List<LTUnionGeometry.Face>{new LTUnionGeometry.Face(lower,0,0),new LTUnionGeometry.Face(upper.AsEnumerable().Reverse(),0,0)};
            for(int i=0;i<sides;i++){int j=(i+1)%sides;result.Add(new LTUnionGeometry.Face(new[]{lower[i],upper[i],upper[j],lower[j]},0,0));}
            return result;
        }
        var detailed=Prism(300);
        var deep=LTUnionGeometry.Triangulate(LTUnionGeometry.Union(detailed,detailed,budget),budget);
        double expected=300*25*Math.Sin(2*Math.PI/300);
        Require(Math.Abs(Volume(deep)-expected)<.01,"deep BSP regression volume");
        var shifted=detailed.Select(f=>new LTUnionGeometry.Face(f.vertices.Select(v=>new LTUnionGeometry.Vertex(v.p+Vector3.right*2,v.uv)),1,1)).ToList();
        var overlappingDetailed=LTUnionGeometry.Triangulate(LTUnionGeometry.Union(detailed,shifted,budget),budget);
        Require(Volume(overlappingDetailed)>expected&&Volume(overlappingDetailed)<2*expected,"deep overlapping BSP union volume");
        Console.WriteLine("PASS BSP deeper than 256 planes: 300-sided closed prism union, preserved volume and manifold topology.");
        Console.WriteLine("PASS Union kernel: overlap volume, order reversal, translated coordinates, local remesh closure, containment, disjoint solids.");
        Console.WriteLine("PASS rotated Union -> local remesh -> terrain clipping -> Bridge -> weld (4 offset/height cases).");
    }
}
