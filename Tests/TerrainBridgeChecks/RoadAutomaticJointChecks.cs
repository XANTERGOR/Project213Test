using System;
using System.Linq;
using System.Collections.Generic;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadAutomaticJointChecks()
    {
        AutomaticJointSyntheticChecks();
        var raw=Enumerable.Range(1,3).Select(v=>Enumerable.Range(0,3).Select(l=>ReadRoadFbx($"Assets/Data/Models/Environment/Roads/Roads_1/Road_{v}.fbx","*_LOD"+l)).ToArray()).ToArray();
        var hashes=raw.SelectMany(s=>s).Select(LTRoadMesh.ContentHash).ToArray();
        float min=raw[0][0].vertices.Min(p=>p.z),max=raw[0][0].vertices.Max(p=>p.z);
        var inputs=raw.Select(levels=>levels.Select(c=>LTRoadModuleMath.NormalizeLength(c,levels[0].vertices.Min(p=>p.z),levels[0].vertices.Max(p=>p.z),min,max,true)).ToArray()).ToArray();
        var plan=LTRoadModuleCollar.CreateAutomatic(inputs[0][0],2,1,.01f);
        Console.WriteLine($"Common automatic profile: {plan.PointCount} points, closed="+string.Join(",",plan.profiles.Select(p=>p.closed)));
        var output=new LTRoadMesh.Chunk[3][];
        for(int v=0;v<3;v++)
        {
            output[v]=new LTRoadMesh.Chunk[3];
            for(int l=0;l<3;l++)
            {
                var c=output[v][l]=LTRoadModuleCollar.ApplyAutomatic(inputs[v][l],plan);
                Console.WriteLine($"Road_{v+1} LOD{l}: {raw[v][l].triangles.Length/3} -> {c.triangles.Length/3} tris");
                VerifyCollarCentre(inputs[v][l],c,min+2,max-2);VerifyCutManifold(c,min+2,max-2);
                LTRoadModuleEnds.ValidateRims(output[0][0],c);
                Require(LTRoadMesh.ContentHash(c)==LTRoadMesh.ContentHash(LTRoadModuleCollar.ApplyAutomatic(inputs[v][l],plan)),"automatic joints deterministic");
            }
        }
        Require(hashes.SequenceEqual(raw.SelectMany(s=>s).Select(LTRoadMesh.ContentHash)),"all 9 source meshes unchanged");
        var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.Asphalt;settings.width=6;settings.sampleSpacing=.5f;
        var snapshot=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(0,1,0),4),new LTRoadPoint(new Vector3(1,3,25),9),new LTRoadPoint(new Vector3(8,5,55),-3)},Matrix4x4.identity,settings);
        var minBounds=output[0][0].vertices.Aggregate(Vector3.Min);var maxBounds=output[0][0].vertices.Aggregate(Vector3.Max);
        var bent=output.SelectMany(s=>s).Select(c=>LTRoadModuleMath.Bend(c,snapshot,minBounds,maxBounds,8,true,.02f,.06f,1,Matrix4x4.identity)).ToArray();
        int checks=0;
        for(int a=0;a<bent.Length;a++)for(int b=0;b<bent.Length;b++)for(int i=1;i<bent[a].Count;i++)
        {
            var seam=new HashSet<Vector3>(bent[a][i-1].vertices);seam.IntersectWith(bent[b][i].vertices);
            Require(seam.Count==plan.PointCount,"automatic mixed module+LOD joint exact");checks++;
        }
        Console.WriteLine($"PASS automatic joints: 3 modules x 3 LOD, exact centre/channels, no source writes, paired cuts, {checks} mixed module/LOD joins. Raw FBX, not native Unity import/render.");
    }
    static void AutomaticJointSyntheticChecks()
    {
        LTRoadMesh.Chunk Grid(int across,int rows,bool closed,bool caps=false,bool hole=false,bool split=false,bool frontHole=false)
        {
            var vertices=new List<Vector3>();var triangles=new List<int>();int stride=across+1;
            for(int z=0;z<=rows;z++)for(int x=0;x<=across;x++)
            {
                float angle=2*Mathf.PI*x/across;
                vertices.Add(closed?new Vector3(x==across?1:Mathf.Cos(angle),x==across?0:Mathf.Sin(angle),8f*z/rows):new Vector3(4f*x/across-2,0,8f*z/rows));
            }
            for(int z=0;z<rows;z++)for(int x=0;x<across;x++)
            {
                if(hole&&x==across/2&&(split||(frontHole?z==1:z>=rows/2-1&&z<=rows/2)))continue;
                int a=z*stride+x,b=a+stride;
                triangles.AddRange(closed?new[]{a,a+1,b,a+1,b+1,b}:new[]{a,b,a+1,a+1,b,b+1});
            }
            if(caps)for(int end=0;end<2;end++)
            {
                int centre=vertices.Count;vertices.Add(new Vector3(0,0,end*8));int row=end*rows*stride;
                for(int x=0;x<across;x++)triangles.AddRange(end==0?new[]{centre,row+x+1,row+x}:new[]{centre,row+x,row+x+1});
            }
            return EndFixture(vertices.ToArray(),triangles.ToArray());
        }
        void Reject(Action action,string label)
        {bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}Require(failed,label);}
        foreach(bool closed in new[]{false,true})foreach(bool cap in new[]{false,true})
        {
            if(cap&&!closed)continue;
            var reference=Grid(16,16,closed,cap);var authored=Grid(9,12,closed,cap);
            string before=LTRoadMesh.ContentHash(authored);
            foreach(float width in new[]{.67f,1.00005f,2})
            {
                var plan=LTRoadModuleCollar.CreateAutomatic(reference,width,.5f,.03f);
                var result=LTRoadModuleCollar.ApplyAutomatic(authored,plan);
                VerifyCollarCentre(authored,result,width,8-width);VerifyCutManifold(result,width,8-width);
                LTRoadModuleEnds.ValidateRims(result,result);
                Require(EndSet(result,0).Count==plan.PointCount,"common end profile count");
                Require(result.normals.All(LTRoadMesh.Finite)&&result.tangents.All(t=>float.IsFinite(t.x)&&float.IsFinite(t.y)&&float.IsFinite(t.z)&&Math.Abs(t.w)==1),"finite frames, exact tangent handedness");
                int capsFound=0;
                for(int i=0;i<result.triangles.Length;i+=3)
                {
                    var a=result.vertices[result.triangles[i]];var b=result.vertices[result.triangles[i+1]];var c=result.vertices[result.triangles[i+2]];
                    var cross=Vector3.Cross(b-a,c-a);Require(cross.sqrMagnitude>1e-22f,"automatic joint no collapsed face");
                    if(a.z==b.z&&b.z==c.z&&(a.z==0||a.z==8))
                    {Require(cap&&cross.z*(a.z==0?-1:1)>0,"end cap winding");capsFound++;}
                }
                Require(!cap||capsFound>0,"source caps not silently lost");
            }
            Require(before==LTRoadMesh.ContentHash(authored),"synthetic source unchanged");
            var split=SplitCollarFixture(authored);
            var splitResult=LTRoadModuleCollar.ApplyAutomatic(split,LTRoadModuleCollar.CreateAutomatic(reference,2,.5f,.03f));
            VerifyCollarCentre(split,splitResult,2,6);VerifyCutManifold(splitResult,2,6);
            Require(splitResult.submeshes.Length==2&&splitResult.submeshes.All(t=>t.Length>0),"split normals/UVs/material slots retained");
        }
        var open=Grid(12,16,false);var defaultPlan=LTRoadModuleCollar.CreateAutomatic(open,1,.5f,.01f);
        var holed=Grid(12,16,false,hole:true);var holedOut=LTRoadModuleCollar.ApplyAutomatic(holed,defaultPlan);
        VerifyCollarCentre(holed,holedOut,1,7);VerifyCutManifold(holedOut,1,7);
        Reject(()=>LTRoadModuleCollar.ApplyAutomatic(Grid(12,16,false,hole:true,split:true),defaultPlan),"disconnected cut must not close holes");
        Reject(()=>LTRoadModuleCollar.ApplyAutomatic(Grid(12,16,false,hole:true,frontHole:true),LTRoadModuleCollar.CreateAutomatic(open,2,.5f,.01f)),"hole wholly inside rebuilt end zone must not disappear");
        Reject(()=>LTRoadModuleCollar.ApplyAutomatic(Grid(12,16,true),defaultPlan),"open/closed contour mismatch rejected");
        foreach(float width in new[]{0,4,float.NaN,float.PositiveInfinity})Reject(()=>LTRoadModuleCollar.CreateAutomatic(open,width,.5f,.01f),"invalid width");
        foreach(float precision in new[]{0,1,float.NaN})Reject(()=>LTRoadModuleCollar.CreateAutomatic(open,1,.5f,precision),"invalid precision");
        var shifted=Grid(12,16,false);for(int i=0;i<shifted.vertices.Length;i++)shifted.vertices[i]+=Vector3.up;
        Reject(()=>LTRoadModuleCollar.ApplyAutomatic(shifted,defaultPlan),"shape tolerance enforced");
        var bad=Grid(12,16,false);bad.vertices[0]=new Vector3(float.NaN,0,0);
        Reject(()=>LTRoadModuleCollar.ApplyAutomatic(bad,defaultPlan),"nonfinite geometry rejected");
        var duplicate=Grid(12,16,false);duplicate.triangles=duplicate.triangles.Concat(duplicate.triangles.Take(3)).ToArray();duplicate.submeshes=new[]{duplicate.triangles};
        Reject(()=>LTRoadModuleCollar.ApplyAutomatic(duplicate,defaultPlan),"nonmanifold input rejected");
        Console.WriteLine("PASS automatic joint synthetic fixtures: open/closed, caps, tiny cuts, unequal topology, split UV/normal/materials, centre holes, finite frames, invalid inputs and shape/topology guards.");
    }
}
