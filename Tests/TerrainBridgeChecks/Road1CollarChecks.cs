using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void Road1CollarChecks()
    {
        var sources=new LTRoadMesh.Chunk[3][];
        LTRoadModuleEnds.Plan commonFit=null;
        for(int variant=1;variant<=3;variant++)
        {
            var raw=Enumerable.Range(0,3).Select(l=>ReadRoadFbx($"Assets/Data/Models/Environment/Roads/Roads_1/Road_{variant}.fbx","Asphalt_Derbis_1_LOD"+l)).ToArray();
            Console.WriteLine($"Road_{variant}: "+string.Join(" / ",raw.Select(c=>$"{c.vertices.Length}v {c.triangles.Length/3}t z=[{c.vertices.Min(p=>p.z):R},{c.vertices.Max(p=>p.z):R}]")));
            var hashes=raw.Select(LTRoadMesh.ContentHash).ToArray();
            var fit=LTRoadModuleEnds.Create(raw[0],1,1);
            if(commonFit==null)commonFit=fit;
            var reference=LTRoadModuleEnds.Apply(raw[0],fit);
            var levels=sources[variant-1]=new[]{reference,raw[1],raw[2]};
            for(int l=1;l<3;l++)
            {
                var joined=LTRoadModuleCollar.Prepare(reference,raw[l],fit,2,out bool restored);
                levels[l]=joined;
                Console.WriteLine($"Road_{variant} LOD{l}: restored={restored}, {joined.triangles.Length/3} tris");
                float min=reference.vertices.Min(p=>p.z),max=reference.vertices.Max(p=>p.z);
                VerifyCollarCentre(raw[l],joined,min+2,max-2);
                if(restored)VerifyCutManifold(joined,min+1,min+2,max-2,max-1);
                LTRoadModuleEnds.ValidateRims(reference,joined);
                Require(joined.triangles.Length<reference.triangles.Length,"authored LOD remains cheaper than LOD0");
                Require(LTRoadMesh.ContentHash(joined)==LTRoadMesh.ContentHash(LTRoadModuleCollar.Prepare(reference,raw[l],fit,2,out _)),"Roads_1 deterministic collars");
            }
            for(int l=0;l<3;l++)Require(hashes[l]==LTRoadMesh.ContentHash(raw[l]),"Roads_1 source channels unchanged");
        }
        var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.Asphalt;settings.width=6;settings.sampleSpacing=.5f;
        var snapshot=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(0,1,0),4),new LTRoadPoint(new Vector3(1,3,25),9),new LTRoadPoint(new Vector3(8,5,55),-3)},Matrix4x4.identity,settings);
        var minBounds=sources[0][0].vertices.Aggregate(Vector3.Min);var maxBounds=sources[0][0].vertices.Aggregate(Vector3.Max);
        int checks=0;
        foreach(var levels in sources)
        {
            var bent=levels.Select(c=>LTRoadModuleMath.Bend(c,snapshot,minBounds,maxBounds,8,true,.02f,.06f,1,Matrix4x4.identity)).ToArray();
            int endCount=EndSet(levels[0],minBounds.z).Count;
            for(int a=0;a<bent.Length;a++)for(int b=0;b<bent.Length;b++)for(int i=1;i<bent[a].Count;i++)
            {
                var seam=new HashSet<Vector3>(bent[a][i-1].vertices);seam.IntersectWith(bent[b][i].vertices);
                Require(seam.Count==endCount,"Roads_1 all LOD combination seams exact");checks++;
            }
        }
        // Report pool compatibility separately: restoring authored LOD collars does not
        // change the original LOD0 topology of distinct source modules.
        for(int v=1;v<sources.Length;v++)
        {
            try{LTRoadModuleEnds.Apply(sources[v][0],commonFit);Console.WriteLine($"Pool Road_1 + Road_{v+1}: compatible LOD0 ends.");}
            catch(InvalidOperationException e){Console.WriteLine($"POOL LIMITATION Road_1 + Road_{v+1}: "+e.Message);}
        }
        Console.WriteLine($"PASS Roads_1 raw FBX: all 6 authored LOD collars, unchanged centre/attributes/source, paired cuts, deterministic output, {checks} curved/banked mixed LOD joins. Pool limitations above are separate. Not a native Unity import/render test.");
    }
}
