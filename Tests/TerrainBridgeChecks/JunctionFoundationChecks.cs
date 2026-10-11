using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void JunctionFoundationChecks()
    {
        int checks=0;
        void Check(bool b,string name){Require(b,name);checks++;}
        void Reject(Action a){bool rejected=false;try{a();}catch(ArgumentException){rejected=true;}Check(rejected,"invalid footprint settings rejected");}
        var vertices=new List<Vector3>();var indices=new List<int>();var quads=new List<Rect>();
        void Quad(float x0,float z0,float x1,float z1)
        {
            int start=vertices.Count;vertices.AddRange(new[]{new Vector3(x0,0,z0),new Vector3(x1,0,z0),new Vector3(x0,0,z1),new Vector3(x1,0,z1)});
            indices.AddRange(new[]{start,start+2,start+1,start+1,start+2,start+3});quads.Add(Rect.MinMaxRect(x0,z0,x1,z1));
        }
        // T junction, ring with a real hole, and disconnected islands: >12 triangles
        // forces recursive BVH traversal rather than testing a single leaf only.
        Quad(-2,-10,2,4);Quad(-10,2,10,6);
        Quad(20,-5,22,5);Quad(28,-5,30,5);Quad(22,-5,28,-3);Quad(22,3,28,5);
        for(int i=0;i<24;i++)Quad(40+i*3,-2,41+i*3,2);
        var originalVertices=vertices.ToArray();var originalIndices=indices.ToArray();
        var shape=new LTRoadFootprint(originalVertices,originalIndices,5);
        var node=LTRoadJunctionMath.MeshFoundation(new Vector3(0,3,0),shape,.5f,2,.5f,1);
        float Distance(float x,float z)
        {
            float d=float.PositiveInfinity;
            foreach(var r in quads){float dx=Mathf.Max(r.xMin-x,Mathf.Max(0,x-r.xMax)),dz=Mathf.Max(r.yMin-z,Mathf.Max(0,z-r.yMax));d=Mathf.Min(d,Mathf.Sqrt(dx*dx+dz*dz));}
            return d;
        }
        var random=new System.Random(127);
        for(int i=0;i<20000;i++)
        {
            float x=(float)random.NextDouble()*140-15,z=(float)random.NextDouble()*40-20,d=Distance(x,z);
            float expected=1-Mathf.SmoothStep(0,1,(d-.5f)/2);
            Check(Math.Abs(node.Weight(x,z)-expected)<.000002f,"analytic rectangle union falloff");
            Check(Math.Abs(node.ApplyHeight(x,z,10)-(10-7*expected))<.00002f,"terrain height");
            float actual=shape.SurfaceDistance(x,z,100);
            Check(Math.Abs(actual-d)<.00002f,"BVH exact distance");
            Check(node.FootprintWeight(x,z,.2f)>=node.Weight(x,z),"displacement guard never shrinks core");
            if(node.Weight(x,z)>0)Check(node.Intersects(new Rect(x-.02f,z-.02f,.04f,.04f)),"density broadphase must not miss covered cell");
        }
        Check(node.Weight(0,0)==1&&node.Weight(-8,4)==1,"T centre and arm");
        Check(node.Weight(8,-8)==0&&!node.Intersects(new Rect(7,-9,1,1)),"concave quadrant remains untouched");
        Check(node.Weight(25,0)==0&&!node.Intersects(new Rect(24.9f,-.1f,.2f,.2f)),"large hole stays open");
        Check(node.Weight(25,2.6f)==1&&Math.Abs(node.Weight(25,1.5f)-.5f)<.00001f,"hole shoulder and blend");
        Check(node.Intersects(new Rect(-100,-100,200,200)),"large cell enclosing narrow arms");
        Check(shape.SurfaceDistance(8,-8,1)==float.PositiveInfinity,"bounded distance");
        Check(shape.SurfaceDistance(0,0,0)==0&&float.IsPositiveInfinity(shape.SurfaceDistance(8,-8,0)),"zero distance exact membership");
        var hard=LTRoadJunctionMath.MeshFoundation(Vector3.zero,shape,0,0,.5f,1);
        Check(hard.Weight(0,0)==1&&hard.Weight(2.01f,-5)==0,"zero shoulder/blend");
        Check(shape.Contains(8,0)&&hard.Weight(8,0)==0,"foundation ignores vegetation-specific constructor padding");
        var reversed=new LTRoadFootprint(vertices.ToArray(),indices.AsEnumerable().Reverse().ToArray(),0);
        Check(reversed.SurfaceDistance(3,-3,10)==shape.SurfaceDistance(3,-3,10),"winding-independent projection");
        var shifted=new LTRoadFootprint(vertices.Select(v=>v+new Vector3(902,8,137)).ToArray(),indices.ToArray(),0);
        Check(shifted.SurfaceDistance(905,134,10)==shape.SurfaceDistance(3,-3,10),"translated projection");
        string key=shape.ContentKey;originalVertices[0]=Vector3.one*999;originalIndices[0]=999;
        Check(shape.ContentKey==key&&shape.SurfaceDistance(0,-9,0)==0,"snapshot owns geometry");
        Check(node.hash!=LTRoadJunctionMath.MeshFoundation(node.centre,shape,.6f,2,.5f,1).hash,"shoulder hash");
        Check(node.hash!=LTRoadJunctionMath.MeshFoundation(node.centre,shape,.5f,1,.5f,1).hash,"blend hash");
        Reject(()=>LTRoadJunctionMath.MeshFoundation(Vector3.zero,null,0,1,1,1));
        foreach(float bad in new[]{-1f,float.NaN,float.PositiveInfinity})
        {Reject(()=>LTRoadJunctionMath.MeshFoundation(Vector3.zero,shape,bad,1,1,1));Reject(()=>shape.SurfaceDistance(0,0,bad));}
        Reject(()=>new LTRoadFootprint(new[]{Vector3.zero},new[]{0,0,5},0));
        var circle=LTRoadJunctionMath.Foundation(new Vector3(0,3,0),8,2,.5f,1);
        Check(circle.Weight(0,0)==1&&circle.Weight(9,0)==.5f&&circle.Weight(10,0)==0,"legacy circle unchanged");
        const int size=101;var masks=new float[size*size];var coordinates=new Vector2[masks.Length];
        for(int p=0;p<masks.Length;p++)
        {coordinates[p]=new Vector2(-15+(p%size)*.5f,-15+(p/size)*.5f);masks[p]=node.Weight(coordinates[p].x,coordinates[p].y);}
        var fixture=new LTPaintWeightFixture{input=new LTPaintWeightInput{size=size,masks=masks,coordinates=coordinates,
            hits=Array.Empty<LTPaintWeightMath.RoadHit>(),terrain=new LTPaintTerrainSnapshot(),
            stamps=new[]{new LTPaintWeightMath.Stamp{kind=3,slot=1,maskOffset=0}}}};
        for(int p=0;p<masks.Length;p++)Check(fixture.Scalar(p).first.g==(byte)Mathf.RoundToInt(masks[p]*255),"paint job kernel preserves mesh coverage");
        for(int i=0;i<1000;i++)node.Weight(i%13,i%7);
        long before=GC.GetAllocatedBytesForCurrentThread();float sum=0;
        for(int i=0;i<10000;i++)sum+=node.Weight(i%13,i%7);
        Check(GC.GetAllocatedBytesForCurrentThread()-before<256&&sum>0,"hot queries do not allocate");
        RoadFootprintChecks();
        Console.WriteLine($"PASS mesh junction foundation: {checks} assertions; T, holes, islands, exact BVH distances, shoulders/falloff, displacement padding, density, immutable data, zero/invalid settings, circle regression. Managed math only.");
    }
}
