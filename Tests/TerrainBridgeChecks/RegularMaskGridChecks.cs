using System;
using System.Collections.Generic;
using UnityEngine;
using LocalTerrainPrototype;

// Only editor progress UI is stubbed; planner, balancing and mesh emission are production code.
namespace UnityEditor
{
    public static class EditorUtility
    {
        public static int ProgressCalls;
        public static bool DisplayCancelableProgressBar(string title,string message,float progress){ProgressCalls++;return false;}
        public static void ClearProgressBar(){}
    }
}
namespace LocalTerrainPrototype {public enum LTStampShape {Ellipse,Rectangle}}
partial class Checks
{
    static void RegularMaskGridChecks()
    {
        var region=new Rect(0,0,16,16);
        var mask=new Rect(4,4,8,8);
        var zones=new List<LTStampMesh.Zone>{new LTStampMesh.Zone{
            bounds=mask,cellSize=.5f,customCellSize=(x,z)=>.5f,
            coverageIntersects=r=>LTStampMesh.Overlap(mask,r)}};
        var raw=LTStampMesh.Plan(region,4,false,0,100000,zones,(x,z)=>0);
        var forest=new LTBalancedForest(1,1,100000,new Dictionary<int,List<Vector3Int>>{{0,raw}});
        forest.Balance();
        var plan=forest.Plan(0);
        bool fine=false,coarse=false,transition=false;
        foreach(var p in plan)
        {
            float step=p.z/(float)(1<<24)*16;
            fine|=Math.Abs(step-.5f)<1e-6;
            coarse|=step>=2;
            for(int side=0;side<4;side++)transition|=forest.Midpoint(0,p,side);
        }
        Require(fine&&coarse&&transition,"mask grid: fine interior, coarse exterior and stitched transitions");
        foreach(bool hole in new[]{false,true})
        {
            Func<float,float,bool> cut=hole?(x,z)=>(x-8)*(x-8)+(z-8)*(z-8)<4:null;
            LTStampMesh.Emit(region,new Vector2(16,16),100000,forest,0,plan,(x,z)=>0,cut,
                out var v,out var normals,out var uv,out var indices,r=>true);
            var edges=new Dictionary<(int,int),int>();double area=0;
            for(int i=0;i<indices.Length;i+=3)
            {
                int a=indices[i],b=indices[i+1],c=indices[i+2];
                float signed=Vector3.Cross(v[b]-v[a],v[c]-v[a]).y;
                Require(signed>0,"mask grid: nondegenerate upward triangles");area+=signed*.5;
                foreach(var e in new[]{(a,b),(b,c),(c,a)})
                {var key=e.Item1<e.Item2?e:(e.Item2,e.Item1);edges.TryGetValue(key,out int count);edges[key]=count+1;}
            }
            // Curved contours are polygonal approximations at the requested cell size.
            Require(Math.Abs(area-(hole?256-4*Math.PI:256))<(hole?.3:.001),$"mask grid: area preserved except requested cut ({area})");
            foreach(var e in edges)
            {
                Require(e.Value<=2,"mask grid: manifold edges");
                if(e.Value==2)continue;
                Vector3 a=v[e.Key.Item1],b=v[e.Key.Item2];
                bool outer=(a.x==0&&b.x==0)||(a.x==16&&b.x==16)||(a.z==0&&b.z==0)||(a.z==16&&b.z==16);
                bool rim=hole&&Math.Abs((a.x-8)*(a.x-8)+(a.z-8)*(a.z-8)-4)<.001&&
                    Math.Abs((b.x-8)*(b.x-8)+(b.z-8)*(b.z-8)-4)<.001;
                Require(outer||rim,"mask grid: no open interior seams or T-junctions");
            }
        }
        Console.WriteLine("Regular mask grid: production planner/balancer/emitter, transitions and cut passed.");
    }
}
