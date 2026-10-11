using System;
using System.Linq;
using System.Collections.Generic;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadFuseChecks()
    {
        LTRoadMesh.Chunk Grid(float offset=0,bool hole=false)
        {
            var v=new List<Vector3>();var t=new List<int>();
            for(int z=0;z<=8;z++)for(int x=0;x<=8;x++)v.Add(new Vector3(x*.5f-2,0,z+(z==8?offset:0)));
            for(int z=0;z<8;z++)for(int x=0;x<8;x++)
            {if(hole&&z==4&&x==4)continue;int a=z*9+x,b=a+9;t.AddRange(new[]{a,b,a+1,a+1,b,b+1});}
            return EndFixture(v.ToArray(),t.ToArray());
        }
        void Reject(Action action,string label)
        {bool fail=false;try{action();}catch(InvalidOperationException){fail=true;}Require(fail,label);}
        var reference=Grid();var plan=LTRoadModuleFuse.Create(reference);
        foreach(var mesh in new[]{reference,Grid(.00003f),Grid(-.00003f),Grid(.00003f,true),SplitCollarFixture(Grid(.00003f,true))})
        {
            string hash=LTRoadMesh.ContentHash(mesh);
            var result=LTRoadModuleFuse.Apply(mesh,plan,out int moved,out float maxMove);
            CheckFuseCopy(mesh,result,0,8,.0001f);
            Require(hash==LTRoadMesh.ContentHash(mesh),"Fuse source unchanged");
            LTRoadModuleFuse.ValidateRims(reference,result);
            Require(maxMove<=.0001f,"Fuse movement bounded");
            Require(result.hash==LTRoadModuleFuse.Apply(mesh,plan,out _,out _).hash,"Fuse deterministic");
            var neutral=LTRoadModuleFuse.Apply(result,plan,out moved,out maxMove);
            Require(moved==0&&maxMove==0&&neutral.hash==result.hash,"Fuse idempotent");
        }
        Reject(()=>LTRoadModuleFuse.Apply(Grid(.001f),plan,out _,out _),"Fuse excessive length mismatch");
        var shifted=Grid();for(int i=0;i<shifted.vertices.Length;i++)shifted.vertices[i]+=Vector3.right*.001f;
        Reject(()=>LTRoadModuleFuse.Apply(shifted,plan,out _,out _),"Fuse shape mismatch");
        var wrong=Grid();wrong.vertices[0]+=Vector3.forward*.01f;
        Reject(()=>LTRoadModuleFuse.Apply(wrong,plan,out _,out _),"Fuse missing rim point");
        wrong=Grid();wrong.triangles=wrong.triangles.Concat(wrong.triangles.Take(3)).ToArray();
        Reject(()=>LTRoadModuleFuse.Apply(wrong,plan,out _,out _),"Fuse duplicate rim faces");
        wrong=Grid();wrong.vertices[0]=new Vector3(float.NaN,0,0);
        Reject(()=>LTRoadModuleFuse.Apply(wrong,plan,out _,out _),"Fuse nonfinite");
        foreach(float tolerance in new[]{0,-1,float.NaN,float.PositiveInfinity,.1f})
            Reject(()=>LTRoadModuleFuse.Create(reference,tolerance),"Fuse invalid tolerance");
        // A thin extra row must not silently collapse when included in the snap band.
        var tiny=EndFixture(new[]{new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(0,0,.00003f),new Vector3(1,0,.00003f),new Vector3(0,0,8),new Vector3(1,0,8)},
            new[]{0,2,1,1,2,3,2,4,3,3,4,5});
        var tinyPlan=LTRoadModuleFuse.Create(tiny);
        Reject(()=>LTRoadModuleFuse.Apply(tiny,tinyPlan,out _,out _),"Fuse must not remove tiny triangles");
        // Closed rims with different cap triangulation: cap centres are not seam anchors.
        LTRoadMesh.Chunk Box(bool capCentre)
        {
            var v=new List<Vector3>();var t=new List<int>();
            foreach(float z in new[]{0f,4f,8f})v.AddRange(new[]{new Vector3(-1,-1,z),new Vector3(1,-1,z),new Vector3(1,1,z),new Vector3(-1,1,z)});
            for(int row=0;row<2;row++)for(int x=0;x<4;x++)
            {int a=row*4+x,b=row*4+(x+1)%4;t.AddRange(new[]{a,b,a+4,b,b+4,a+4});}
            for(int end=0;end<2;end++)
            {
                int row=end*8;
                if(capCentre)
                {int centre=v.Count;v.Add(new Vector3(.1f,0,end*8));for(int x=0;x<4;x++)t.AddRange(new[]{centre,row+x,row+(x+1)%4});}
                else t.AddRange(new[]{row,row+1,row+2,row,row+2,row+3});
            }
            return EndFixture(v.ToArray(),t.ToArray());
        }
        var box=Box(false);var cap=Box(true);var bp=LTRoadModuleFuse.Create(box);
        var capResult=LTRoadModuleFuse.Apply(cap,bp,out _,out _);CheckFuseCopy(cap,capResult,0,8,.0001f);LTRoadModuleFuse.ValidateRims(box,capResult);
        // Vertex numbering is irrelevant; all indexed channels and winding still survive.
        var reordered=Grid(.00003f);int count=reordered.vertices.Length;
        reordered.vertices=reordered.vertices.Reverse().ToArray();reordered.triangles=reordered.triangles.Select(i=>count-1-i).ToArray();reordered.submeshes=new[]{(int[])reordered.triangles.Clone()};
        var rr=LTRoadModuleFuse.Apply(reordered,plan,out _,out _);CheckFuseCopy(reordered,rr,0,8,.0001f);LTRoadModuleFuse.ValidateRims(reference,rr);
        foreach(float band in new[]{0,float.NaN,.001f})Reject(()=>LTRoadModuleFuse.Create(reference,.0001f,band),"Invalid search band");
        // The same canonical rim is retained by automatic simplification too.
        var reduced=LTRoadModuleMath.Simplify(Grid(0,true),.5f,.01f);
        LTRoadModuleFuse.ValidateRims(reference,LTRoadModuleFuse.Apply(reduced,plan,out _,out _));
        Console.WriteLine("PASS Fuse synthetic: unchanged topology/all channels/interior holes, split vertices, deterministic/idempotent snapping, bounded movement, invalid input and fold guards.");
        var raw=Enumerable.Range(1,2).Select(v=>Enumerable.Range(0,3).Select(l=>ReadRoadFbx($"Assets/Data/Models/Environment/Roads/Roads_1/Road_{v}.fbx","*_LOD"+l)).ToArray()).ToArray();
        float min=raw[0][0].vertices.Min(p=>p.z),max=raw[0][0].vertices.Max(p=>p.z);
        var inputs=raw.Select(levels=>levels.Select(c=>LTRoadModuleMath.NormalizeLength(c,levels[0].vertices.Min(p=>p.z),levels[0].vertices.Max(p=>p.z),min,max,true)).ToArray()).ToArray();
        const float assetTolerance=.001f;
        plan=LTRoadModuleFuse.Create(inputs[0][0],assetTolerance);var outputs=new List<LTRoadMesh.Chunk>();int rejectedLevels=0;
        for(int v=0;v<2;v++)for(int l=0;l<3;l++)
        {
            string hash=LTRoadMesh.ContentHash(inputs[v][l]);
            LTRoadMesh.Chunk fused;int moved;float maxMove;
            try{fused=LTRoadModuleFuse.Apply(inputs[v][l],plan,out moved,out maxMove);}
            catch(InvalidOperationException e)
            {
                if(l!=2)throw;
                Require(hash==LTRoadMesh.ContentHash(inputs[v][l]),"Rejected source unchanged");
                Console.WriteLine($"ASSET INCOMPATIBLE Road_{v+1} LOD{l}: {e.Message}");rejectedLevels++;continue;
            }
            outputs.Add(fused);
            CheckFuseCopy(inputs[v][l],fused,min,max,assetTolerance);LTRoadModuleFuse.ValidateRims(outputs[0],fused);
            Require(hash==LTRoadMesh.ContentHash(inputs[v][l]),"Raw FBX copy unchanged");
            Console.WriteLine($"Fuse Road_{v+1} LOD{l}: {fused.triangles.Length/3} tris unchanged, moved {moved}, max {maxMove*1000:G5} mm");
        }
        var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.Asphalt;settings.width=6;settings.sampleSpacing=.5f;
        var snapshot=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(0,1,0),4),new LTRoadPoint(new Vector3(1,3,25),9),new LTRoadPoint(new Vector3(8,5,55),-3)},Matrix4x4.identity,settings);
        var lo=outputs[0].vertices.Aggregate(Vector3.Min);var hi=outputs[0].vertices.Aggregate(Vector3.Max);
        var bent=outputs.Select(c=>LTRoadModuleMath.Bend(c,snapshot,lo,hi,8,true,.02f,.06f,1,Matrix4x4.identity)).ToArray();
        int seams=0;
        for(int a=0;a<bent.Length;a++)for(int b=0;b<bent.Length;b++)for(int j=1;j<bent[a].Count;j++)
        {
            var tail=new HashSet<Vector3>(Enumerable.Range(0,outputs[a].vertices.Length).Where(i=>outputs[a].vertices[i].z==max).Select(i=>bent[a][j-1].vertices[i]));
            var head=new HashSet<Vector3>(Enumerable.Range(0,outputs[b].vertices.Length).Where(i=>outputs[b].vertices[i].z==min).Select(i=>bent[b][j].vertices[i]));
            Require(tail.SetEquals(head),"Exact mixed module/LOD Fuse seam after bending");seams++;
        }
        Require(outputs.Count>=4,"Road_1/2 LOD0/1 must remain compatible");
        Console.WriteLine($"PASS Fuse math + {outputs.Count} compatible Road_1/2 levels: {seams} exact mixed bent seams, same vertex/triangle counts and all authored channels. ASSET LIMITATION: {rejectedLevels} LOD2 levels rejected, not repaired or dropped. Raw FBX math only, not native import/render.");
    }
    static void CheckFuseCopy(LTRoadMesh.Chunk a,LTRoadMesh.Chunk b,float min,float max,float tolerance)
    {
        Require(a.vertices.Length==b.vertices.Length&&a.triangles.SequenceEqual(b.triangles),"Fuse topology unchanged");
        Require(a.submeshes.Length==b.submeshes.Length&&a.submeshes.Zip(b.submeshes,(x,y)=>x.SequenceEqual(y)).All(x=>x),"Fuse submesh indices unchanged");
        Require(a.normals.SequenceEqual(b.normals)&&a.tangents.SequenceEqual(b.tangents)&&a.uv.SequenceEqual(b.uv)&&a.uv2.SequenceEqual(b.uv2)&&a.uv3.SequenceEqual(b.uv3)&&a.uv4.SequenceEqual(b.uv4)&&a.colors.SequenceEqual(b.colors),"Fuse all authored attributes unchanged");
        for(int i=0;i<a.vertices.Length;i++)
        {
            var p=a.vertices[i];
            if(Math.Abs(p.z-min)>tolerance&&Math.Abs(p.z-max)>tolerance)Require(p.Equals(b.vertices[i]),"Fuse interior unchanged");
            Require((p-b.vertices[i]).magnitude<=tolerance,"Fuse per vertex move bounded");
        }
    }
}
