using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTHoleVolumeLODCheck
    {
        internal static string Run(Action<Vector3[],int[],bool> checkFlatCollider=null)
        {
            var unit=new[]{new Vector3(-1,-1,-1),new Vector3(1,-1,-1),new Vector3(1,-1,1),new Vector3(-1,-1,1),
                new Vector3(-1,1,-1),new Vector3(1,1,-1),new Vector3(1,1,1),new Vector3(-1,1,1)};
            var faces=new[]{0,1,2,0,2,3,4,6,5,4,7,6,0,4,5,0,5,1,1,5,6,1,6,2,2,6,7,2,7,3,3,7,4,3,4,0};
            LTHoleStampSnapshot Box(Vector3 centre,Vector3 scale)
            {
                var points=unit.Select(p=>Vector3.Scale(p,scale)+centre).ToArray();
                return new LTHoleStampSnapshot(new LTHoleMeshVolume(new LTHoleMeshData(points,faces),Matrix4x4.identity),.25f);
            }
            var outer=unit.Select(p=>Vector3.Scale(p,new Vector3(6,2,6))+new Vector3(8,0,8));
            var inner=unit.Select(p=>Vector3.Scale(p,new Vector3(3,1,3))+new Vector3(8,0,8));
            var nested=new LTHoleStampSnapshot(new LTHoleMeshVolume(new LTHoleMeshData(outer.Concat(inner).ToArray(),
                faces.Concat(faces.Select(i=>i+8)).ToArray()),Matrix4x4.identity),.25f);
            var cases=new[]{
                new[]{Box(new Vector3(8,3,8),new Vector3(6,1,6))},
                new[]{Box(new Vector3(8,-3,8),new Vector3(6,1,6))},
                new[]{Box(new Vector3(8,0,8),new Vector3(6,1,6))},
                new[]{Box(new Vector3(8,1.6f,8),new Vector3(6,1,6))},
                new[]{Box(new Vector3(6.8f,0,8),new Vector3(1,2,3)),Box(new Vector3(9.2f,0,8),new Vector3(1,2,3))},
                new[]{nested}
            };
            var messages=new List<string>();int comparisons=0;
            for(int fixture=0;fixture<cases.Length;fixture++)
            {
                var holes=cases[fixture];bool slope=fixture==3;
                float Height(float x,float z)=>slope?.25f*(x-8)+.1f*(float)Math.Sin(z):0;
                bool Cut(float x,float z)=>holes.Any(h=>h.Contains(x,Height(x,z),z));
                var rect=new Rect(0,0,16,16);var progress=new LTLODMesh.BuildProgress(false);
                var fine=LTStampMesh.Plan(rect,64,false,.1f,150000,new List<LTStampMesh.Zone>(),Height,progress);
                var forest=new LTBalancedForest(1,1,150000,new Dictionary<int,List<Vector3Int>>{{0,fine}});forest.Balance();fine=forest.Plan(0);
                var edges=new LTHoleVolumeLODProtection();int reads=0;
                foreach(var _ in edges.Prepare(holes,fine,rect,(x,z)=>{reads++;return Height(x,z);})){}
                Require(fixture<2?edges.BoundaryCellCount==0:edges.BoundaryCellCount>0,"height-sensitive contour classification");
                Require(edges.BoundaryCellCount<fine.Count/4,"projected area still frozen instead of its contour");
                int capturedReads=reads;
                var preparation=new LTLODMesh.Preparation(fine,rect,Height,progress);var levels=new List<Vector3Int>[5];levels[0]=fine;
                for(int l=1;l<=4;l++)levels[l]=preparation.Coarsen(l,100,null,edges.Protects,4,false);
                Require(levels[4].Count<fine.Count/2,"far plan did not simplify beneath the mesh projection");
                Require(reads==capturedReads,"published LOD protection retained a height query");
                int Mask(Vector3Int p){int bits=0;for(int s=0;s<4;s++)if(forest.Midpoint(0,p,s))bits|=1<<s;return bits;}
                bool Protected(Vector3Int p)=>edges.Protects(new Rect(p.x/(float)LTSpatialLODMath.N*16,p.y/(float)LTSpatialLODMath.N*16,
                    p.z/(float)LTSpatialLODMath.N*16,p.z/(float)LTSpatialLODMath.N*16));
                var output=LTSpatialLODMath.BuildLayout(levels,4,150000,Mask,Protected);
                LTStampMesh.EmitSpatialVariants(output,rect,new Vector2(16,16),150000,Height,Cut);
                var topology=new LTSpatialLODTopology(1,1,4,new[]{new LTSpatialLODTopology.Chunk{cells=output.cells,patches=output.patches}});
                topology.Update();var indices=new List<int>();topology.WriteIndices(0,indices);int near=indices.Count,far=0;
                var boundary=LTHoleStampLODCheck.Boundary(output.vertices,indices);
                Require(fixture<2?boundary.Count==0:boundary.Count>0,"baseline hole boundary");
                foreach(int level in new[]{4,0,3,4,-1})
                {
                    for(int p=0;p<16;p++)topology.SetLevel(0,p,level<0?p%5:level);
                    topology.Update();topology.Validate();topology.WriteIndices(0,indices);
                    Require(LTHoleStampLODCheck.Boundary(output.vertices,indices).SetEquals(boundary),"contour changed or crack opened at distant/mixed LOD");
                    if(level==4){far=indices.Count;Require(far<near*.6f,"far emitted triangle count did not reduce");}
                    if(level==0)Require(indices.Count==near,"returning near did not restore detail");
                    if(!slope)checkFlatCollider?.Invoke(output.vertices,indices.ToArray(),!Cut(8,8));
                    comparisons++;
                }
                messages.Add($"case {fixture}: {edges.BoundaryCellCount}/{fine.Count} contour cells; triangles {near/3}->{far/3}");
            }
            return $"{comparisons} near/far/mixed checks; above/below volume, slope, narrow bridge, nested cavity, exact rim retained; "+string.Join("; ",messages);
        }
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Hole Volume LOD FAIL: "+message);}
    }
}
