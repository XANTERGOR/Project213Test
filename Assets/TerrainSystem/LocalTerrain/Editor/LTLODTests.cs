using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
namespace LocalTerrainPrototype
{
    public static class LTLODTests
    {
        static void Check(bool value,string message){if(!value)throw new Exception("LOD test: "+message);}
        [MenuItem("Tools/Local Terrain/Run LOD Tests")]
        public static void Run()
        {
            try
            {
                int n=LTBalancedForest.N,step=n/32;
                var fine=new List<Vector3Int>();
                for(int z=0;z<n;z+=step)for(int x=0;x<n;x+=step)fine.Add(new Vector3Int(x,z,step));
                var r=new Rect(0,0,32,32);
                Func<float,float,float> flat=(x,z)=>0;
                var zero=LTLODMesh.Coarsen(fine,r,0,1,flat);
                Check(zero.SequenceEqual(fine),"zero steps must preserve topology");
                int previous=fine.Count;
                for(int level=1;level<=3;level++)
                {
                    var coarse=LTLODMesh.Coarsen(fine,r,level,1,flat);
                    var plans=new Dictionary<int,List<Vector3Int>>{{0,fine},{1,coarse}};
                    var forest=new LTBalancedForest(2,1,100000,plans);forest.Balance();forest.Validate();
                    coarse=forest.Plan(1);LTLODMesh.ValidateBoundary(fine,coarse);
                    Check(coarse.Count<previous,"flat LOD must reduce leaf count");previous=coarse.Count;
                    // Every boundary vertex must coincide for arbitrary adjacent LOD levels.
                    LTStampMesh.Emit(r,new Vector2(64,32),100000,forest,0,forest.Plan(0),flat,out var v0,out var n0,out var uv0,out var t0);
                    LTStampMesh.Emit(new Rect(32,0,32,32),new Vector2(64,32),100000,forest,1,coarse,flat,out var v1,out var n1,out var uv1,out var t1);
                    var a=v0.Where(v=>v.x==32).Select(v=>v.z).OrderBy(z=>z);
                    var b=v1.Where(v=>v.x==0).Select(v=>v.z).OrderBy(z=>z);
                    Check(a.SequenceEqual(b),"mixed LOD seam positions");
                    Debug.Log($"LOD{level}: {coarse.Count} leaves; {t1.Length/3} triangles; seam OK");
                }
                Func<float,float,float> curved=(x,z)=>.1f*(x*x+z*z);
                Check(LTLODMesh.Coarsen(fine,r,3,0,curved).Count==fine.Count,"zero tolerance must preserve curved terrain");
                var settings=new[]{new LTLODSettings(1,.1f,80),new LTLODSettings(2,.5f,180),new LTLODSettings(3,1,400)};
                Check(LTWorld.SelectLOD(84,0,settings,5)==0,"hysteresis outward");
                Check(LTWorld.SelectLOD(86,0,settings,5)==1,"LOD1 transition");
                Check(LTWorld.SelectLOD(76,1,settings,5)==1,"hysteresis inward");
                Check(LTWorld.SelectLOD(74,1,settings,5)==0,"return LOD0");
                Check(LTWorld.SelectLOD(1000,0,settings,5)==3,"teleport selects LOD3");
                var four=settings.Concat(new[]{new LTLODSettings(4,2,800)}).ToArray();
                Check(LTWorld.SelectLOD(1000,0,four,5)==4,"fourth authored terrain LOD is selectable");
                Debug.Log("Local Terrain LOD tests passed.");
            }
            finally{EditorUtility.ClearProgressBar();}
        }
    }
}
