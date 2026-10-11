using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTRockSeamCheck
    {
#if UNITY_EDITOR
        // Archived validation menu: Tools/Local Terrain/Validate Joint Rock Terrain LOD (no scene changes)
        static void Menu()
        {
            try{Debug.Log("Joint Rock Terrain LOD v4 PASS: "+Run()+"; "+LTRockSeamRuntimeCheck.Run());}
            catch(Exception error){Debug.LogError("Joint Rock Terrain LOD v4 FAIL: "+error);}
        }
#endif
        internal static string Run()
        {
            const int count=48;int checks=0,near=0,far=0,rim0=0,rim4=0;
            for(int fixture=0;fixture<3;fixture++)
            {
                LTRockSeamMath.Part Ring(float[] radii,bool terrain,bool split)
                {
                    var vertices=new List<Vector3>();var triangles=new List<int>();var safe=new List<bool>();
                    // A duplicated UV chart boundary has distinct endpoint indices.
                    int columns=split?count+1:count;
                    for(int row=0;row<radii.Length;row++)for(int j=0;j<columns;j++)
                    {
                        float a=(j%count)*Mathf.PI*2/count,r=radii[row];
                        float x=r*Mathf.Cos(a),z=r*Mathf.Sin(a);
                        vertices.Add(new Vector3(x,fixture==1?.06f*x+.025f*z:0,z));
                        safe.Add(terrain?row<radii.Length-1:row==radii.Length-1);
                    }
                    for(int row=0;row<radii.Length-1;row++)for(int j=0;j<count;j++)
                    {int a=row*columns+j,b=row*columns+(split?j+1:(j+1)%columns),c=a+columns,d=b+columns;triangles.AddRange(new[]{a,b,c,b,d,c});}
                    return new LTRockSeamMath.Part{positions=vertices.ToArray(),triangles=new[]{triangles.ToArray()},movable=safe.ToArray(),terrain=terrain};
                }
                var terrain=Ring(new[]{3f,3.5f,4f,4.5f,5f,6f},true,false);
                var bridge=Ring(new[]{2f,2.5f,3f},false,fixture==2);
                var parts=new[]{terrain,bridge};var errors=new[]{0f,.4f,.8f,1.6f,3.2f};
                var result=LTRockSeamMath.Build(parts,errors,16);
                var originals=parts.Select(p=>(int[])p.triangles[0].Clone()).ToArray();
                HashSet<(Vector3Int,Vector3Int)> Rim(LTRockSeamMath.Part p,int[] indices,bool contact)
                {
                    var edges=new HashSet<(Vector3Int,Vector3Int)>();
                    for(int i=0;i<indices.Length;i+=3)for(int k=0;k<3;k++)
                    {
                        var a=LTRockSeamMath.Key(p.positions[indices[i+k]]);var b=LTRockSeamMath.Key(p.positions[indices[i+(k+1)%3]]);
                        if(!edges.Remove((b,a)))Require(edges.Add((a,b)),"duplicate/nonmanifold face");
                    }
                    if(contact)edges.RemoveWhere(e=>Math.Abs(Math.Sqrt((double)e.Item1.x*e.Item1.x+(double)e.Item1.z*e.Item1.z)-30000)>2||Math.Abs(Math.Sqrt((double)e.Item2.x*e.Item2.x+(double)e.Item2.z*e.Item2.z)-30000)>2);
                    return edges;
                }
                var originalRim=Rim(terrain,originals[0],true);rim0+=originalRim.Count;
                for(int level=0;level<errors.Length;level++)
                {
                    var output=new int[parts.Length][];
                    for(int part=0;part<parts.Length;part++)
                    {
                        var list=new List<int>(originals[part]);LTRockSeamMath.Remap(list,result.maps[level][part]);output[part]=list.ToArray();
                        if(level==0)Require(output[part].SequenceEqual(originals[part]),"LOD0 modified");
                        for(int i=0;i<result.maps[level][part].Length;i++)
                        {
                            int target=result.maps[level][part][i];
                            Require(Vector3.Distance(parts[part].positions[i],parts[part].positions[target])<=errors[level]+.0001f,"endpoint error exceeded");
                            if(!parts[part].movable[i])Require(target==i,"locked boundary changed");
                        }
                        Require(parts[part].triangles[0].SequenceEqual(originals[part]),"source indices mutated");
                    }
                    var groundRim=Rim(terrain,output[0],true);var bridgeRim=Rim(bridge,output[1],true);
                    Require(groundRim.SetEquals(bridgeRim.Select(e=>(e.Item2,e.Item1))),"terrain/bridge contours differ");
                    Require(groundRim.Count>=3,"contact loop closed up");
                    if(level==4){rim4+=groundRim.Count;Require(groundRim.Count<originalRim.Count*.8f,"shared contour did not simplify");far+=output.Sum(i=>i.Length/3);}
                    if(level==0)near+=output.Sum(i=>i.Length/3);
                    checks++;
                }
                // Same baked map after arbitrary other selections must restore the exact near arrays.
                var restored=new List<int>(originals[0]);LTRockSeamMath.Remap(restored,result.maps[0][0]);Require(restored.SequenceEqual(originals[0]),"near restore");
            }
            return $"{checks} joint levels; contour {rim0}->{rim4} segments; triangles {near}->{far}; exact shared 3D endpoints, slope, UV chart boundary, displacement budget, fixed outside/inside rims and unchanged LOD0 inputs; "+LTRockSeamBandCheck.Run()+"; "+LTRockNativeLODCheck.Run();
        }
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException("Joint seam: "+message);}
    }
}
