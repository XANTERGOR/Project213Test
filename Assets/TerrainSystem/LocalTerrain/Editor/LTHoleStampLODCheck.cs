using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // The same numerical regression runs in the managed harness and the Unity
    // menu. It owns no scene objects/assets and can optionally verify a mesh.
    internal static class LTHoleStampLODCheck
    {
        internal static string Run(Action<Vector3[],int[]> checkMesh=null)
        {
            var messages=new List<string>();
            var mask=new LTStampDensityMask(32,32,y=>Enumerable.Range(0,32).Select(x=>
                new Color(x>=13&&x<19&&y>=13&&y<19?1:0,0,0,1)).ToArray(),false,new Vector2(0,1),1);
            foreach(float angle in new[]{0f,27f})
            {
                float c=(float)Math.Cos(angle*Math.PI/180),s=(float)Math.Sin(angle*Math.PI/180);
                var m=Matrix4x4.identity;m.m00=c;m.m02=s;m.m20=-s;m.m22=c;m.m03=8;m.m23=8;
                var inv=Matrix4x4.identity;inv.m00=c;inv.m02=-s;inv.m20=s;inv.m22=c;inv.m03=-8*c+8*s;inv.m23=-8*s-8*c;
                var hole=new LTHoleStampSnapshot(m,inv,new Vector2(16,16),LTStampShape.Rectangle,.25f,mask);
                var rect=new Rect(0,0,16,16);var progress=new LTLODMesh.BuildProgress(false);
                float Height(float x,float z)=>.05f*x+.025f*z;
                var fine=LTStampMesh.Plan(rect,64,false,.1f,100000,new List<LTStampMesh.Zone>(),Height,progress);
                var forest=new LTBalancedForest(1,1,100000,new Dictionary<int,List<Vector3Int>>{{0,fine}});forest.Balance();fine=forest.Plan(0);
                var prep=new LTLODMesh.Preparation(fine,rect,Height,progress);
                var old=prep.Coarsen(4,100,new List<Rect>{hole.ProtectedBounds},null,4,false);
                Require(old.Count==fine.Count,"fixture must reproduce the old whole-canvas LOD lock");
                var levels=new List<Vector3Int>[5];levels[0]=fine;
                for(int l=1;l<5;l++)levels[l]=prep.Coarsen(l,100,null,hole.ProtectsLOD,4,false);
                Require(levels[4].Count<fine.Count/2,"hole chunk did not simplify outside the white mask");
                int Midpoints(Vector3Int p){int bits=0;for(int side=0;side<4;side++)if(forest.Midpoint(0,p,side))bits|=1<<side;return bits;}
                bool Protected(Vector3Int p)=>hole.ProtectsLOD(new Rect(p.x/(float)LTSpatialLODMath.N*16,p.y/(float)LTSpatialLODMath.N*16,
                    p.z/(float)LTSpatialLODMath.N*16,p.z/(float)LTSpatialLODMath.N*16));
                var data=LTSpatialLODMath.BuildLayout(levels,4,100000,Midpoints,Protected);
                LTStampMesh.EmitSpatialVariants(data,rect,new Vector2(16,16),100000,Height,hole.Contains);
                var topology=new LTSpatialLODTopology(1,1,4,new[]{new LTSpatialLODTopology.Chunk{cells=data.cells,patches=data.patches}});
                topology.Update();var indices=new List<int>();topology.WriteIndices(0,indices);
                int near=indices.Count,far=0;var rim=Boundary(data.vertices,indices);
                Require(rim.Count>0,"fixture has a real hole boundary");checkMesh?.Invoke(data.vertices,indices.ToArray());
                foreach(int l in new[]{4,0,3,4})
                {
                    for(int p=0;p<16;p++)topology.SetLevel(0,p,l);
                    topology.Update();topology.Validate();topology.WriteIndices(0,indices);
                    Require(Boundary(data.vertices,indices).SetEquals(rim),"LOD changed the hole rim or created a crack");
                    if(l==0)Require(indices.Count==near,"returning to LOD0 did not restore detail");
                    if(l==4){far=indices.Count;Require(far<near/2,"far render indices did not reduce despite a coarser plan");}
                    checkMesh?.Invoke(data.vertices,indices.ToArray());
                }
                messages.Add($"mask yaw {angle}: cells {fine.Count}->{levels[4].Count}, triangles {near/3}->{far/3}, exact rim preserved");
            }
            return string.Join("; ",messages);
        }
        internal static HashSet<(Vector3,Vector3)> Boundary(Vector3[] v,IList<int> indices)
        {
            var edges=new Dictionary<(Vector3,Vector3),int>();
            for(int i=0;i<indices.Count;i+=3)for(int side=0;side<3;side++)
            {
                var a=v[indices[i+side]];var b=v[indices[i+(side+1)%3]];
                if(edges.TryGetValue((b,a),out int count)){if(count==1)edges.Remove((b,a));else edges[(b,a)]=count-1;}
                else{edges.TryGetValue((a,b),out count);edges[(a,b)]=count+1;}
            }
            var rim=new HashSet<(Vector3,Vector3)>();
            foreach(var edge in edges)
            {
                Require(edge.Value==1,"non-manifold mesh boundary");var a=edge.Key.Item1;var b=edge.Key.Item2;
                if(a.x==b.x&&(a.x==0||a.x==16)||a.z==b.z&&(a.z==0||a.z==16))continue;
                rim.Add(edge.Key);
            }
            return rim;
        }
        static void Require(bool ok,string message)
        {if(!ok)throw new InvalidOperationException("Hole Stamp LOD FAIL: "+message);}
    }
}
