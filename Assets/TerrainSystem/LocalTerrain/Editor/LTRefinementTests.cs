using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
namespace LocalTerrainPrototype
{
    public static class LTRefinementTests
    {
        static void Check(bool ok,string message){if(!ok)throw new Exception("Density override test: "+message);}
        static LTStampMesh.Zone Zone(float size,float cell,float cx=.5f,float cz=.5f,LTStampShape shape=LTStampShape.Rectangle,float angle=0)
        {
            var m=Matrix4x4.TRS(new Vector3(cx,0,cz),Quaternion.Euler(0,angle,0),Vector3.one);
            float radius=size*.707107f;
            return new LTStampMesh.Zone{bounds=new Rect(cx-radius,cz-radius,radius*2,radius*2),inverse=m.inverse,size=new Vector2(size,size),shape=shape,cellSize=cell};
        }
        static void Topology(Vector3[] v,int[] t)
        {
            var edges=new Dictionary<ulong,int>();
            for(int i=0;i<t.Length;i+=3)
            {
                float ab=Vector2.Distance(new Vector2(v[t[i]].x,v[t[i]].z),new Vector2(v[t[i+1]].x,v[t[i+1]].z));
                float bc=Vector2.Distance(new Vector2(v[t[i+1]].x,v[t[i+1]].z),new Vector2(v[t[i+2]].x,v[t[i+2]].z));
                float ca=Vector2.Distance(new Vector2(v[t[i+2]].x,v[t[i+2]].z),new Vector2(v[t[i]].x,v[t[i]].z));
                Check(Mathf.Max(ab,Mathf.Max(bc,ca))/Mathf.Min(ab,Mathf.Min(bc,ca))<=2.01f,"long thin fan triangle");
                Check(Vector3.Cross(v[t[i+1]]-v[t[i]],v[t[i+2]]-v[t[i]]).y>0,"triangle winding");
                for(int k=0;k<3;k++)
                {uint a=(uint)t[i+k],b=(uint)t[i+(k+1)%3];ulong key=((ulong)Math.Min(a,b)<<32)|Math.Max(a,b);edges.TryGetValue(key,out int count);edges[key]=count+1;}
            }
            foreach(var e in edges)
            {
                Check(e.Value==1||e.Value==2,"non-manifold edge");if(e.Value==2)continue;
                var a=v[(int)(e.Key>>32)];var b=v[(int)(e.Key&0xffffffff)];
                Check((a.x==0&&b.x==0)||(a.x==1&&b.x==1)||(a.z==0&&b.z==0)||(a.z==1&&b.z==1),"interior T junction");
            }
        }
        static Vector3[] Build(List<LTStampMesh.Zone> zones,int baseCells,out int[] t,bool adaptive=true,int budget=200000)
        {
            var r=new Rect(0,0,1,1);int depth=LTStampMesh.RegionDepth(r,LTStampMesh.Depth(1,1f/baseCells),zones);
            LTStampMesh.Build(r,Vector2.one,baseCells,adaptive,.05f,budget,zones,new[]{depth,depth,depth,depth},(x,z)=>0,out var v,out var n,out var uv,out t);
            Topology(v,t);return v;
        }
        [MenuItem("Tools/Local Terrain/Run Refinement Tests")]
        public static void Run()
        {
            var fine=new List<LTStampMesh.Zone>{Zone(2,.01f)};
            var a=Build(fine,8,out var at);var b=Build(fine,64,out var bt);
            Check(a.Length==b.Length&&at.Length==bt.Length,"fine override must be independent of base density");
            var coarse=new List<LTStampMesh.Zone>{Zone(2,.01f),Zone(2,.25f)};
            var c=Build(coarse,64,out var ct);Check(c.Length<a.Length/4,"later coarse override reduces geometry");
            coarse.Reverse();var d=Build(coarse,64,out var dt);Check(d.Length==a.Length,"reordering restores fine density");
            var noAdaptive=Build(fine,8,out var nt,false);Check(noAdaptive.Length==a.Length,"density works with Adaptive off");
            var tiny=new List<LTStampMesh.Zone>{Zone(.04f,.001f)};
            var e=Build(tiny,8,out var et);Check(e.Any(p=>p.x>.48f&&p.x<.52f&&p.z>.48f&&p.z<.52f),"density finer than former 512-cell cap");
            Build(new List<LTStampMesh.Zone>{Zone(.3f,.02f,.5f,.5f,LTStampShape.Rectangle,35)},8,out var rotated);
            Build(new List<LTStampMesh.Zone>{Zone(.3f,.02f,.5f,.5f,LTStampShape.Ellipse)},8,out var ellipse);
            bool refused=false;try{Build(fine,8,out var unused,true,1000);}catch(InvalidOperationException){refused=true;}
            Check(refused,"budget must report failure instead of lowering density");
            var mask=new Color32[64*64];
            for(int y=16;y<48;y++)for(int x=16;x<48;x++)mask[y*64+x]=new Color32(255,255,0,255);
            var coverage=new LTPaintMath.CoverageGrid(mask,64);
            var maskBounds=new Rect(0,0,1,1);
            var paintZones=new List<LTStampMesh.Zone>{new LTStampMesh.Zone{bounds=maskBounds,cellSize=.125f,
                customCellSize=(x,z)=>.125f,coverageIntersects=r=>coverage.Any(r.xMin,r.yMin,r.xMax,r.yMax)}};
            var baseline=Build(paintZones,4,out var baselineIndices);
            paintZones.Add(new LTStampMesh.Zone{bounds=maskBounds,cellSize=.03125f,
                customCellSize=(x,z)=>.03125f,coverageIntersects=r=>coverage.Boundary(r.xMin,r.yMin,r.xMax,r.yMax)});
            var boundary=Build(paintZones,4,out var boundaryIndices);
            Check(boundary.Length>baseline.Length,"boundary prototype adds local source geometry");
            var raw=LTStampMesh.Plan(maskBounds,4,true,.05f,200000,paintZones,(x,z)=>0);
            float scale=LTBalancedForest.N;
            Check(raw.Any(p=>p.z/scale>=.125f),"prototype retains coarse source cells");
            Check(raw.Any(p=>p.z/scale<=.03125f),"prototype resolves mixed boundary cells");
            paintZones.RemoveAt(1);
            var reverted=Build(paintZones,4,out var revertedIndices);
            Check(reverted.Length==baseline.Length&&revertedIndices.SequenceEqual(baselineIndices),"disabling boundary prototype restores baseline topology");
            int n=LTBalancedForest.N;
            var plans=new Dictionary<int,List<Vector3Int>>{{0,new List<Vector3Int>{new Vector3Int(0,0,n)}},{1,new List<Vector3Int>{new Vector3Int(0,0,n)}}};
            var original=new LTBalancedForest(2,1,200000,plans);original.Balance();var before=original.BoundaryStitches(0,original.Plan(0));
            plans[1]=new List<Vector3Int>{new Vector3Int(0,0,n/2),new Vector3Int(n/2,0,n/2),new Vector3Int(0,n/2,n/2),new Vector3Int(n/2,n/2,n/2)};
            var changed=new LTBalancedForest(2,1,200000,plans);changed.Balance();changed.Validate();
            Check(original.Plan(0).SequenceEqual(changed.Plan(0)),"coarse neighbour cell unchanged");
            Check(!before.SequenceEqual(changed.BoundaryStitches(0,changed.Plan(0))),"neighbour edge still requires a stitch update");
            plans[1]=new List<Vector3Int>{new Vector3Int(0,0,n)};
            var restored=new LTBalancedForest(2,1,200000,plans);restored.Balance();Check(restored.Plan(0).Count==1&&restored.Plan(1).Count==1,"removal restores coarse topology");
            Debug.Log("Local Terrain v0.5: refinement tests PASSED (independent density, coarse override, hierarchy, shapes, topology, explicit budget).");
        }
    }
}
