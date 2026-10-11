using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Shared numerical regression, no scene/asset/native-object writes.
    public static class LTRoadInsetCheck
    {
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException("Inset road check: "+message);}
        static void Area(Vector3[] v,int[] indices,double expected)
        {
            bool Finite(Vector3 p)=>!float.IsNaN(p.x)&&!float.IsNaN(p.y)&&!float.IsNaN(p.z)&&!float.IsInfinity(p.x)&&!float.IsInfinity(p.y)&&!float.IsInfinity(p.z);
            double area=0;var faces=new HashSet<(int,int,int)>();
            for(int i=0;i<indices.Length;i+=3)
            {
                var a=v[indices[i]];var b=v[indices[i+1]];var c=v[indices[i+2]];
                var cross=Vector3.Cross(b-a,c-a);
                int lo=Math.Min(indices[i],Math.Min(indices[i+1],indices[i+2])),hi=Math.Max(indices[i],Math.Max(indices[i+1],indices[i+2]));
                Require(faces.Add((lo,indices[i]+indices[i+1]+indices[i+2]-lo-hi,hi)),"duplicate triangle");
                Require(Finite(a)&&Finite(b)&&Finite(c)&&cross.sqrMagnitude>1e-18f,"invalid/degenerate face");
                Require(cross.y>=-1e-7,"flipped surface");area+=cross.y*.5;
            }
            Require(Math.Abs(area-expected)<.015,$"projected area {area:R} != {expected:R}: gap or overlap");
        }
        public static string Run(Action<Vector3[][],int[][],Vector3[],int> boundaries,float reduction=1,bool damaged=false,bool beveled=false,bool connected=false,int seedOffset=0,bool wornPreset=false)
        {
            int mixed=0;int[] counts=null;int fixtures=0;
            foreach(float origin in new[]{0f,4096f})foreach(bool curved in new[]{false,true})
            {
                var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.GeometricAsphalt;
                settings.width=4;settings.terrainCellSize=1;settings.insetCellSize=1;settings.asphaltDamage.strength=0;
                if(damaged)
                {
                    settings.asphaltDamage=LTRoadAsphaltDamage.Default;settings.asphaltDamage.strength=1;
                    settings.asphaltDamage.potholes=1;settings.asphaltDamage.brokenEdges=.5f;
                    settings.asphaltDamage.size=1.5f;settings.asphaltDamage.spacing=5;settings.asphaltDamage.depth=.3f;
                }
                settings.insetLodReduction=reduction;
                if(beveled){settings.pitBevelWidth=.12f;settings.pitBevelDepth=.05f;settings.pitShapeVariation=.7f;}
                if(connected){settings.connectedDamage=true;settings.asphaltDamage.depth=.06f;settings.asphaltDamage.seed+=seedOffset;}
                if(wornPreset)
                {
                    settings.width=6;settings.connectedDamage=true;settings.connectedFloorAsphalt=.7f;settings.repairPatchStrength=.75f;
                    settings.asphaltDamage.potholes=.8f;settings.asphaltDamage.spacing=7;settings.asphaltDamage.size=4.5f;
                    settings.asphaltDamage.brokenEdges=.3f;settings.asphaltDamage.cracks=.45f;settings.asphaltDamage.depth=.06f;
                }
                var path=curved?new[]{new LTRoadPoint(new Vector3(origin-4,1,6),4),new LTRoadPoint(new Vector3(origin+16,2,9),-3),new LTRoadPoint(new Vector3(origin+36,1.5f,7),2)}:
                    new[]{new LTRoadPoint(new Vector3(origin-4,2,7.3f)),new LTRoadPoint(new Vector3(origin+36,2,9.7f))};
                var road=LTRoadMath.Build(path,Matrix4x4.identity,settings);
                float H(float x,float z)=>road.ApplyHeight(x,z,curved?1+.01f*(x-origin):2);
                var outputs=new LTSpatialLODMath.Output[2];var chunks=new LTSpatialLODTopology.Chunk[2];
                var plans=new Dictionary<int,List<Vector3Int>>();var offsets=new[]{new Vector3(origin,0,0),new Vector3(origin+16,0,0)};
                for(int c=0;c<2;c++)plans[c]=LTStampMesh.Plan(new Rect(origin+c*16,0,16,16),16,false,.01f,300000,new List<LTStampMesh.Zone>(),H,new LTLODMesh.BuildProgress(false));
                var forest=new LTBalancedForest(2,1,300000,plans);forest.Balance();
                for(int c=0;c<2;c++)
                {
                    var rect=new Rect(origin+c*16,0,16,16);var fine=forest.Plan(c);var prep=new LTLODMesh.Preparation(fine,rect,H);
                    var levels=new List<Vector3Int>[5];levels[0]=fine;for(int l=1;l<5;l++)levels[l]=prep.Coarsen(l,100,null,null,2,false);
                    int chunk=c;int Mask(Vector3Int p){int mask=0;for(int e=0;e<4;e++)if(forest.Midpoint(chunk,p,e))mask|=1<<e;return mask;}
                    var output=LTSpatialLODMath.BuildLayout(levels,2,300000,Mask);outputs[c]=output;
                    var field=new LTRoadInsetMesh.Field(new[]{road},rect);
                    LTStampMesh.EmitSpatialVariants(output,rect,new Vector2(32,16),300000,H,null,progress:new LTLODMesh.BuildProgress(false),insetRoads:field);
                    chunks[c]=new LTSpatialLODTopology.Chunk{cells=output.cells,patches=output.patches};
                    LTStampMesh.Emit(rect,new Vector2(32,16),300000,forest,c,fine,H,null,out var v,out _,out _,out var t,progress:new LTLODMesh.BuildProgress(false),insetRoads:field);
                    string[] Faces(Vector3[] vertices,int[] indices)
                    {
                        string P(int id){var p=vertices[id];return FormattableString.Invariant($"{p.x:R}/{p.y:R}/{p.z:R}");}
                        var result=new string[indices.Length/3];for(int i=0;i<indices.Length;i+=3){var tri=new[]{P(indices[i]),P(indices[i+1]),P(indices[i+2])};Array.Sort(tri,StringComparer.Ordinal);result[i/3]=string.Join("|",tri);}Array.Sort(result,StringComparer.Ordinal);return result;
                    }
                    Require(Faces(v,t).SequenceEqual(Faces(output.vertices,output.baseIndices)),"LOD0 render differs from collider geometry");
                }
                var topology=new LTSpatialLODTopology(2,1,2,chunks);var random=new System.Random(418);
                counts=new int[5];
                for(int attempt=0;attempt<17;attempt++)
                {
                    for(int c=0;c<2;c++)for(int p=0;p<4;p++)topology.SetLevel(c,p,attempt<5?attempt:random.Next(5));
                    topology.Update();topology.Validate();var indices=new int[2][];
                    for(int c=0;c<2;c++)
                    {
                        var list=new List<int>();topology.WriteIndices(c,list);indices[c]=list.ToArray();
                        try{Area(outputs[c].vertices,indices[c],256);}catch(Exception error){throw new InvalidOperationException($"origin={origin}, curved={curved}, attempt={attempt}, chunk={c}: "+error.Message,error);}
                    }
                    try{boundaries(new[]{outputs[0].vertices,outputs[1].vertices},indices,offsets,1);}
                    catch(Exception error)
                    {
                        string relative="relative frame passed";
                        try{boundaries(new[]{outputs[0].vertices,outputs[1].vertices},indices,new[]{Vector3.zero,new Vector3(16,0,0)},1);}
                        catch(Exception local){relative="relative frame failed: "+local.Message;}
                        throw new InvalidOperationException($"origin={origin}, curved={curved}, attempt={attempt}, {relative}: "+error.Message,error);
                    }
                    if(attempt<5)
                    {
                        counts[attempt]=(indices[0].Length+indices[1].Length)/3;
                        Require(reduction==0||attempt==0||counts[attempt]<=counts[attempt-1],$"origin={origin} curved={curved}: LOD{attempt} becomes denser ({string.Join(",",counts)})");
                    }
                    else mixed++;
                }
                Require(reduction==0||counts[4]<counts[0]/2,"far ribbon not simplified");fixtures++;
            }
            return $"{fixtures} two-chunk straight/curved/banked fixtures at 0/4096 m (damage={damaged}, bevel={beveled}, connected={connected}); {mixed} mixed LOD layouts; area, strict boundaries, identical LOD0 collider; last LOD counts {string.Join(" / ",counts)}";
        }
    }
}
