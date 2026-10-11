using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using LocalTerrainPrototype;

partial class Checks
{
    static void RockTransitionChecks()
    {
        int checks=0;
        void Check(bool value,string message){Require(value,"Rock transition: "+message);checks++;}
        var p=new List<Vector3>();for(int x=0;x<6;x++){p.Add(new Vector3(x,0,0));p.Add(new Vector3(x,0,1));}
        var stone=new List<int>();var bridge=new List<int>();
        for(int x=0;x<5;x++)
        {
            var target=x==4?bridge:stone;int a=x*2;
            target.AddRange(new[]{a,a+1,a+2,a+2,a+1,a+3});
        }
        var indices=new[]{stone.ToArray(),bridge.ToArray()};var vertices=p.ToArray();var original=(Vector3[])vertices.Clone();
        foreach(float width in new[]{.1f,1f,2f,4f})foreach(float scale in new[]{.5f,1f,2f})
        {
            var plan=LTRockTransitionMath.Split(vertices,indices,1,Matrix4x4.Scale(Vector3.one*scale),width,1);
            Check(plan.rock.Sum(v=>v.Length)+plan.transition.Sum(v=>v.Length)==indices.Sum(v=>v.Length),"triangle conservation");
            var all=plan.rock.Concat(plan.transition).SelectMany(v=>v).ToArray();
            Check(all.OrderBy(v=>v).SequenceEqual(indices.SelectMany(v=>v).OrderBy(v=>v)),"no missing or duplicate triangle indices");
            Check(plan.terrainWeight[8]==1&&plan.terrainWeight[9]==1&&plan.terrainWeight[10]==1,"rim and bridge fully terrain");
            for(int x=0;x<4;x++)Check(Math.Abs(plan.terrainWeight[x*2]-LTRockTransitionMath.Weight((4-x)*scale,width,1))<1e-5,"surface distance width");
            Check(vertices.SequenceEqual(original),"source positions unchanged");
        }
        var noBridge=LTRockTransitionMath.Split(vertices,new[]{stone.ToArray()},1,Matrix4x4.identity,2,1);
        Check(noBridge.transition.All(v=>v.Length==0)&&noBridge.rock[0].SequenceEqual(stone),"no contact keeps original renderer");
        var copied=vertices.Concat(new[]{vertices[8],vertices[9]}).ToArray();
        var copiedBridge=bridge.Select(v=>v==8?12:v==9?13:v).ToArray();
        var duplicate=LTRockTransitionMath.Split(copied,new[]{stone.ToArray(),copiedBridge},1,Matrix4x4.identity,2,1);
        Check(duplicate.terrainWeight[8]==1&&duplicate.terrainWeight[12]==1,"UV seam duplicates share contact distance");
        // Dense planar grid: simplify the interior, retain EVERY original outer edge.
        const int size=16;var grid=new Vector3[(size+1)*(size+1)];var faces=new List<int>();
        for(int z=0;z<=size;z++)for(int x=0;x<=size;x++)grid[z*(size+1)+x]=new Vector3(x,0,z);
        for(int z=0;z<size;z++)for(int x=0;x<size;x++)
        {int a=z*(size+1)+x;faces.AddRange(new[]{a,a+size+1,a+1,a+1,a+size+1,a+size+2});}
        Dictionary<long,int> Edges(int[] tri)
        {
            var result=new Dictionary<long,int>();
            for(int i=0;i<tri.Length;i+=3)for(int k=0;k<3;k++)
            {int a=tri[i+k],b=tri[i+(k+1)%3];long key=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);result.TryGetValue(key,out int count);result[key]=count+1;}
            return result;
        }
        var originalBoundary=Edges(faces.ToArray()).Where(e=>e.Value==1).Select(e=>e.Key).OrderBy(k=>k).ToArray();
        foreach(float error in new[]{1f,2f,4f})
        {
            var reduced=LTRockLODMath.Reduce(grid,new[]{faces.ToArray()},null,error,5)[0];var edges=Edges(reduced);
            Check(reduced.Length<faces.Count,"LOD reduces interior triangles");
            Check(edges.All(e=>e.Value<=2),"manifold edge incidence");
            Check(edges.Where(e=>e.Value==1).Select(e=>e.Key).OrderBy(k=>k).SequenceEqual(originalBoundary),"exact fixed border");
            for(int i=0;i<reduced.Length;i+=3)
                Check(Vector3.Cross(grid[reduced[i+1]]-grid[reduced[i]],grid[reduced[i+2]]-grid[reduced[i]]).y>0,"consistent winding / positive area");
            Check(reduced.SequenceEqual(LTRockLODMath.Reduce(grid,new[]{faces.ToArray()},null,error,5)[0]),"deterministic LOD");
        }
        Console.WriteLine($"PASS rock transition: {checks} checks (partition, UV duplicates, metre width, LOD reduction, exact border, winding). No native rendering tested.");
    }
}
