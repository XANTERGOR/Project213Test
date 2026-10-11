using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTRockSeamBandCheck
    {
        internal static string Run()
        {
            int before=0,after=0;
            for(int fixture=0;fixture<3;fixture++)
            {
                const int n=14;var vertices=new Vector3[(n+1)*(n+1)];var triangles=new List<int>();
                for(int z=0;z<=n;z++)for(int x=0;x<=n;x++)
                    vertices[z*(n+1)+x]=new Vector3(x,fixture==0?0:.2f*Mathf.Sin(x*.8f)*Mathf.Cos(z*.9f)+(fixture==2&&x==4&&z==6?2:0),z);
                for(int z=0;z<n;z++)for(int x=0;x<n;x++)
                {
                    if(x>=6&&x<=8&&z>=5&&z<=8)continue;
                    int a=z*(n+1)+x,b=a+1,c=a+n+1,d=c+1;triangles.AddRange(new[]{a,c,b,b,c,d});
                }
                var original=triangles.ToArray();var saved=(int[])original.Clone();var positions=(Vector3[])vertices.Clone();
                var map=Enumerable.Range(0,vertices.Length).ToArray();var stable=Enumerable.Repeat(true,vertices.Length).ToArray();
                float error=fixture==0?.01f:.12f;
                var band=LTRockSeamBandMath.Build(vertices,original,stable,map,error);
                Require(band!=null,"band did not retriangulate");var plan=new LTRockSeamIndices.Plan(map,band);
                var output=new List<int>(original);plan.Apply(output);
                Require(output.Count<original.Length*.7f,"wide band stayed dense");
                Require(Boundary(original).SetEquals(Boundary(output)),"outer rim or hole changed");
                for(int i=0;i<output.Count;i+=3)
                {
                    var a=vertices[output[i]];var b=vertices[output[i+1]];var c=vertices[output[i+2]];
                    Require(Vector3.Cross(b-a,c-a).y>0,"terrain triangle flipped");
                }
                // Independent fine-grid samples include the sharp summit and holes.
                for(int z=0;z<n*4;z++)for(int x=0;x<n*4;x++)
                {
                    var point=new Vector3((x+.25f)/4,0,(z+.25f)/4);
                    bool hasNear=Height(vertices,original,point,out float h0),hasFar=Height(vertices,output,point,out float h1);
                    Require(hasNear==hasFar,"band covered a hole or lost surface");
                    if(hasNear)Require(Mathf.Abs(h1-h0)<=error+.0001f,"band accumulated excessive height error");
                }
                Require(original.SequenceEqual(saved)&&vertices.SequenceEqual(positions),"source data modified");
                Require(LTRockSeamBandMath.Build(vertices,original,stable,map,0)==null,"zero error must preserve band");
                bool Road(int a,int b,int c)=>Mathf.Min(vertices[a].x,Mathf.Min(vertices[b].x,vertices[c].x))<=3;
                var guarded=LTRockSeamBandMath.Build(vertices,original,stable,map,error,null,Road);
                var roadOutput=new List<int>(original);new LTRockSeamIndices.Plan(map,guarded==null?Array.Empty<LTRockSeamIndices.Band>():new[]{guarded}).Apply(roadOutput);
                var retained=new HashSet<(int,int,int)>();for(int t=0;t<roadOutput.Count;t+=3)retained.Add(LTRockSeamIndices.Face(roadOutput[t],roadOutput[t+1],roadOutput[t+2]));
                for(int t=0;t<original.Length;t+=3)if(Road(original[t],original[t+1],original[t+2]))Require(retained.Contains(LTRockSeamIndices.Face(original[t],original[t+1],original[t+2])),"protected road changed");
                before+=original.Length/3;after+=output.Count/3;
            }
            CheckStableStitches();CheckFixedTarget();CheckPublication();
            return $"band v2: {before}->{after} triangles; fixed inner/outer rims, holes, slope/spike height budget, optional stitches, target locks, alternative publication/fallback";
        }
        static HashSet<(int,int)> Boundary(IReadOnlyList<int> indices)
        {
            var edges=new HashSet<(int,int)>();var counts=new Dictionary<(int,int),int>();
            for(int i=0;i<indices.Count;i+=3)for(int k=0;k<3;k++)
            {
                int a=indices[i+k],b=indices[i+(k+1)%3];var key=(Math.Min(a,b),Math.Max(a,b));counts.TryGetValue(key,out int uses);counts[key]=uses+1;
                Require(uses<2,"nonmanifold edge");if(!edges.Remove((b,a)))Require(edges.Add((a,b)),"overlapping face");
            }
            return edges;
        }
        static bool Height(Vector3[] v,IReadOnlyList<int> tris,Vector3 q,out float height)
        {
            for(int i=0;i<tris.Count;i+=3)
            {
                var a=v[tris[i]];var b=v[tris[i+1]];var c=v[tris[i+2]];
                float Cross(Vector3 p,Vector3 r,Vector3 s)=>(r.x-p.x)*(s.z-p.z)-(r.z-p.z)*(s.x-p.x);
                float area=Cross(a,b,c),u=Cross(q,b,c)/area,w=Cross(a,b,q)/area,t=1-u-w;
                if(u>=-1e-5f&&t>=-1e-5f&&w>=-1e-5f){height=u*a.y+t*b.y+w*c.y;return true;}
            }
            height=0;return false;
        }
        static void CheckStableStitches()
        {
            var v=new[]{new Vector3(1,0,1),new Vector3(2,0,1),new Vector3(1,0,2),new Vector3(2,0,2),new Vector3(2,0,1.5f)};
            var cells=new[]{new LTSpatialLODCell{child0=-1,possibleMask=1,variants=new[]{new LTSpatialLODVariant{indices=new[]{0,1,2,2,1,3}},new LTSpatialLODVariant{indices=new[]{0,1,2,2,1,4,4,1,3}}}}};
            var patches=new[]{new LTSpatialLODPatch{levels=new[]{new LTSpatialLODLevel{cells=new[]{0}},new LTSpatialLODLevel{cells=new[]{0}}}}};
            var safe=LTRockSeamMath.StableSpatialVertices(v,cells,patches,10,10);
            Require(safe[0]&&!safe[1],"optional stitch must only lock changing stars");
            cells[0].requiredMask=1;safe=LTRockSeamMath.StableSpatialVertices(v,cells,patches,10,10);
            Require(safe.All(b=>b),"unreachable stitch variant locked the band");
        }
        static void CheckFixedTarget()
        {
            var p=new LTRockSeamMath.Part{positions=new[]{new Vector3(1,0,1),new Vector3(0,0,0),new Vector3(0,0,2),new Vector3(2,0,2),new Vector3(2,0,0)},
                triangles=new[]{new[]{0,1,2,0,2,3,0,3,4,0,4,1}},movable=new[]{true,false,false,false,false},terrain=true};
            var result=LTRockSeamMath.Build(new[]{p},new[]{0f,3f});Require(result.collapses[1]==1,"fixed edge cannot absorb an interior point");
            p.targets=new bool[5];result=LTRockSeamMath.Build(new[]{p},new[]{0f,3f});Require(result.collapses[1]==0,"collapse escaped its owned region");
        }
        static void CheckPublication()
        {
            var source=new[]{0,1,4,1,2,4,2,3,4,3,0,4};var replacement=new[]{0,1,2,0,2,3};var identity=Enumerable.Range(0,10).ToArray();
            var band=new LTRockSeamIndices.Band{source=source,replacement=replacement};
            var plan=new LTRockSeamIndices.Plan(identity,band,band);var list=new List<int>(source);plan.Apply(list);
            Require(list.SequenceEqual(replacement),"matching alternatives published twice");
            list=new List<int>(source.Take(9));plan.Apply(list);Require(list.SequenceEqual(source.Take(9)),"incomplete band must keep original surface");
            list=new List<int>(source.Concat(source.Take(3)));plan.Apply(list);Require(list.Count==15,"duplicate source should disable replacement");
            var shifted=new LTRockSeamIndices.Band{source=source.Select(i=>i+5).ToArray(),replacement=replacement.Select(i=>i+5).ToArray()};
            var combined=LTRockSeamIndices.Plan.Combine(new LTRockSeamIndices.Plan(identity,band),new LTRockSeamIndices.Plan(identity,shifted));
            list=new List<int>(source.Concat(shifted.source));combined.Apply(list);Require(list.Count==12,"independent group composition failed");
        }
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("Rock band: "+message);}
    }
}
