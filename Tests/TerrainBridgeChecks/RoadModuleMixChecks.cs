using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadModuleMixChecks(bool readLegacyAsset=true)
    {
        var weights=new[]{1f,3f,2f,0f};
        var order=LTRoadModuleMath.SelectModules(weights,10000,127,true);
        Require(order.SequenceEqual(LTRoadModuleMath.SelectModules(weights,10000,127,true)),"seed is deterministic");
        Require(!order.SequenceEqual(LTRoadModuleMath.SelectModules(weights,10000,128,true)),"seed changes order");
        Require(order.Take(20).SequenceEqual(LTRoadModuleMath.SelectModules(weights,20,127,true)),"prefix independent of road length/chunk partition");
        Require(order.All(v=>v!=3)&&order.Zip(order.Skip(1),(a,b)=>a!=b).All(b=>b),"zero weight and adjacent repeat guard");
        var weighted=LTRoadModuleMath.SelectModules(weights,10000,127,false);
        Require(weighted.Count(v=>v==1)>weighted.Count(v=>v==0)*2.5&&weighted.Count(v=>v==1)<weighted.Count(v=>v==0)*3.5,"weighted distribution without exclusion");
        Require(LTRoadModuleMath.SelectModules(new[]{0f,1f},100,0,true).All(v=>v==1),"single active module supports repeat guard");
        void Reject(Action action){bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}Require(failed,"invalid selection rejected");}
        Reject(()=>LTRoadModuleMath.SelectModules(new[]{0f,0f},1,0,true));
        Reject(()=>LTRoadModuleMath.SelectModules(new[]{float.NaN},1,0,true));
        Reject(()=>LTRoadModuleMath.SelectModules(new[]{-1f,2f},1,0,true));

        LTRoadMesh.Chunk Grid(int variant,int rows,float start,float length)
        {
            var v=new List<Vector3>();var t=new List<int>();
            for(int z=0;z<=rows;z++)for(int x=0;x<=4;x++)
                v.Add(new Vector3(x-2,.02f*x+((z>0&&z<rows)?variant*.02f*(float)Math.Sin(z*2*Math.PI/rows):0),start+length*z/rows));
            for(int z=0;z<rows;z++)for(int x=0;x<4;x++){int a=z*5+x,b=a+5;t.AddRange(new[]{a,b,a+1,a+1,b,b+1});}
            var c=EndFixture(v.ToArray(),t.ToArray());c.colors=Enumerable.Repeat(new Color(variant+1,0,0,1),v.Count).ToArray();
            c.submeshes=variant==1?new[]{Array.Empty<int>(),t.ToArray()}:new[]{t.ToArray(),Array.Empty<int>()};
            if(variant==1){c.uv2=null;c.uv4=null;}
            return c;
        }
        var raw=new[]{Grid(0,8,0,8),Grid(1,16,2,12),Grid(2,24,-4,4)};
        var originalHashes=raw.Select(LTRoadMesh.ContentHash).ToArray();
        var variants=new[]{raw[0],LTRoadModuleMath.NormalizeLength(raw[1],2,14,0,8),LTRoadModuleMath.NormalizeLength(raw[2],-4,0,0,8)};
        var levels=new LTRoadMesh.Chunk[4][];
        levels[0]=variants;
        for(int l=1;l<4;l++)levels[l]=variants.Select(c=>LTRoadModuleMath.Simplify(c,1f/(1<<l),.1f)).ToArray();
        foreach(var level in levels)foreach(var c in level)LTRoadModuleEnds.ValidateRims(variants[0],c);
        var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.Asphalt;settings.width=6;settings.sampleSpacing=.5f;
        var path=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(0,1,0),4),new LTRoadPoint(new Vector3(1,3,40),9),new LTRoadPoint(new Vector3(8,5,90),-3)},Matrix4x4.identity,settings);
        var min=new Vector3(-2,0,0);var max=new Vector3(2,.08f,8);var w=new[]{1f,3f,2f};
        var output=levels.Select(c=>LTRoadModuleMath.BendVariants(c,w,71,true,path,min,max,0,true,0,.06f,1,Matrix4x4.identity)).ToArray();
        var sequence=LTRoadModuleMath.SelectModules(w,output[0].Count,71,true);
        for(int l=0;l<4;l++)for(int c=0;c<output[l].Count;c++)
        {
            var chunk=output[l][c];int variant=sequence[c];
            Require(chunk.colors.All(v=>v.r==variant+1),"same chosen variant at every LOD");
            Require(chunk.submeshes[variant==1?0:1].Length==0,"material slot maintained");
            Require(chunk.triangles.All(i=>i>=0&&i<chunk.vertices.Length),"variable-size module indices valid");
            if(variant==1)Require(chunk.uv2.All(p=>p.Equals(Vector2.zero))&&chunk.uv4.All(p=>p.Equals(Vector2.zero)),"absent optional channels filled neutrally");
            Require(chunk.uv.SequenceEqual(levels[l][variant].uv),"module custom UV unchanged");
            if(c==0)continue;
            for(int other=0;other<4;other++)
            {var seam=new HashSet<Vector3>(output[other][c-1].vertices);seam.IntersectWith(chunk.vertices);Require(seam.Count==5,"mixed module + mixed LOD exact seam");}
        }
        var packed=LTRoadModuleMath.BendVariants(variants,w,71,true,path,min,max,0,true,0,.06f,32,Matrix4x4.identity);
        Require(packed.SelectMany(c=>c.vertices).SequenceEqual(output[0].SelectMany(c=>c.vertices)),"chunk size cannot reshuffle/deform modules");
        Require(packed.SelectMany(c=>c.colors).SequenceEqual(output[0].SelectMany(c=>c.colors)),"packed module order unchanged");
        Require(originalHashes.SequenceEqual(raw.Select(LTRoadMesh.ContentHash)),"sources untouched by normalization/bending/LOD");
        var legacy=LTRoadModuleMath.Bend(variants[0],path,min,max,0,true,0,.06f,32,Matrix4x4.identity);
        var one=LTRoadModuleMath.BendVariants(new[]{variants[0]},new[]{1f},135,true,path,min,max,0,true,0,.06f,32,Matrix4x4.identity);
        Require(legacy.Select(c=>c.hash).SequenceEqual(one.Select(c=>c.hash)),"single module compatibility");
        Console.WriteLine("PASS module pool: deterministic weighted sequence, seed, disabled entries, repeat exclusion, prefix stability, 3 unequal-density/length modules × 4 LODs, every adjacent LOD pair, two material slots/optional channels, partition invariance, unchanged source.");
        if(!readLegacyAsset)return;
        var actual1=ReadRoadFbx("Assets/Data/Models/Environment/Roads/Road_1_Line.fbx");
        var actual2=ReadRoadFbx("Assets/Data/Models/Environment/Roads/Road_2_Line.fbx");
        try
        {
            actual2=LTRoadModuleMath.NormalizeLength(actual2,actual2.vertices.Min(p=>p.z),actual2.vertices.Max(p=>p.z),actual1.vertices.Min(p=>p.z),actual1.vertices.Max(p=>p.z));
            var plan=LTRoadModuleEnds.Create(actual1,.5f,.05f);
            var a=LTRoadModuleEnds.Apply(actual1,plan);var b=LTRoadModuleEnds.Apply(actual2,plan);LTRoadModuleEnds.ValidateRims(a,b);
            Console.WriteLine("INFO actual Road_1_Line + Road_2_Line raw FBX profiles are compatible at 0.05m tolerance; native imported compatibility remains pending.");
        }
        catch(InvalidOperationException e){Console.WriteLine("INFO actual Road_2_Line is NOT compatible with Road_1_Line at the default tolerance: "+e.Message);}
    }

    static void RoadFootprintChecks()
    {
        var vertices=new List<Vector3>();var indices=new List<int>();
        // Ring with a real 2x2 hole; do not fill absent pavement with a bounding box.
        void Quad(float x0,float z0,float x1,float z1)
        {int i=vertices.Count;vertices.AddRange(new[]{new Vector3(x0,3,z0),new Vector3(x1,4,z0),new Vector3(x0,3,z1),new Vector3(x1,4,z1)});indices.AddRange(new[]{i,i+2,i+1,i+1,i+2,i+3});}
        Quad(-4,-6,-1,6);Quad(1,-6,4,6);Quad(-1,-6,1,-1);Quad(-1,1,1,6);
        var mask=new LTRoadFootprint(vertices.ToArray(),indices.ToArray(),0);
        var pad=new LTRoadFootprint(vertices.ToArray(),indices.ToArray(),.35f);
        Require(!mask.Contains(0,0)&&!pad.Contains(0,0),"large absent-pavement hole stays open");
        Require(mask.Contains(0,1)&&!mask.Contains(0,.99f)&&pad.Contains(0,.8f),"rim and padding");
        Require(!mask.Contains(4.1f,0)&&pad.Contains(4.1f,0)&&!pad.Contains(4.36f,0),"edge padding bounded");
        Require(!pad.Contains(4.3f,6.3f)&&pad.Contains(4.2f,6.2f),"circular padding at corners");
        Require(pad.Intersects(new Rect(4.2f,0,1,1))&&!mask.Intersects(new Rect(4.2f,0,1,1)),"broadphase includes mesh outside spline bounds plus padding");
        int samples=0;
        for(int z=-65;z<=65;z++)for(int x=-45;x<=45;x++)
        {
            float px=x*.1f,pz=z*.1f;bool expected=Math.Abs(px)<=4&&Math.Abs(pz)<=6&&!(Math.Abs(px)<1&&Math.Abs(pz)<1);
            Require(mask.Contains(px,pz)==expected,"footprint exact rectangle-minus-hole reference");samples++;
        }
        var shifted=new LTRoadFootprint(vertices.Select(p=>p+new Vector3(4096,20,2048)).ToArray(),indices.ToArray(),.35f);
        Require(shifted.Contains(4099,2048)&&!shifted.Contains(4096,2048),"world-local transformed footprint");
        var reversed=new LTRoadFootprint(vertices.ToArray(),indices.AsEnumerable().Reverse().ToArray(),0);
        Require(reversed.Contains(3,0)&&!reversed.Contains(0,0),"double-sided projection independent of winding");
        Require(!new LTRoadFootprint(Array.Empty<Vector3>(),Array.Empty<int>(),0).Contains(0,0),"empty mesh neutral");
        Console.WriteLine($"PASS asphalt vegetation footprint: {samples} exact reference points, real holes, edge/corner padding, broadphase, translated geometry, winding independence. No Unity grass/render/performance test.");
    }
}
