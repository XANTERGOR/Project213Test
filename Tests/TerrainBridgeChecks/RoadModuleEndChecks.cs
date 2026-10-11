using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadModuleEndChecks(bool readLegacyAsset=true)
    {
        LTRoadMesh.Chunk Grid(float mismatch)
        {
            var v=new List<Vector3>();var indices=new List<int>();
            for(int z=0;z<=32;z++)for(int x=0;x<=8;x++)
                v.Add(new Vector3(-2+x*.5f+(z==32?mismatch*x/8:0),.02f*x/8,z*.25f));
            for(int z=0;z<32;z++)for(int x=0;x<8;x++)
            {int a=z*9+x,b=a+9;indices.AddRange(new[]{a,b,a+1,a+1,b,b+1});}
            return EndFixture(v.ToArray(),indices.ToArray());
        }
        void Reject(Action action,string message)
        {bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Require(rejected,message);}
        var source=Grid(.028f);string before=LTRoadMesh.ContentHash(source);
        var plan=LTRoadModuleEnds.Create(source,.5f,.05f);var fitted=LTRoadModuleEnds.Apply(source,plan);
        Require(plan.PointCount==9&&Math.Abs(plan.Mismatch-.028f)<1e-6f,"measured mismatch");
        CheckEndCopy(source,fitted,.5f);
        Require(before==LTRoadMesh.ContentHash(source),"source geometry/channels untouched");
        Require(fitted.hash==LTRoadModuleEnds.Apply(source,plan).hash,"deterministic fitting");
        var neutral=Grid(0);Require(LTRoadMesh.ContentHash(neutral)==LTRoadModuleEnds.Apply(neutral,LTRoadModuleEnds.Create(neutral,.5f,.05f)).hash,"already matching flat profiles neutral");
        var clamped=LTRoadModuleEnds.Create(source,100,.05f);Require(Math.Abs(clamped.BlendLength-3.6f)<1e-6f,"nonoverlapping support bands");
        Reject(()=>LTRoadModuleEnds.Create(source,.5f,.01f),"mismatch limit enforced");
        Reject(()=>LTRoadModuleEnds.Create(source,float.NaN,.05f),"NaN rejected");
        Reject(()=>LTRoadModuleEnds.Create(source,.5f,float.PositiveInfinity),"infinite tolerance rejected");
        var bad=Grid(.028f);bad.vertices[32*9].z-=.1f;
        Reject(()=>LTRoadModuleEnds.Create(bad,.5f,.05f),"different profile count rejected");
        bad=Grid(0);bad.triangles[bad.triangles.Length-1]-=1;
        Reject(()=>LTRoadModuleEnds.Create(bad,.5f,.05f),"different rim connectivity rejected");
        bad=Grid(0);bad.vertices[32*9+1].x=bad.vertices[32*9].x+.0001f;
        Reject(()=>LTRoadModuleEnds.Create(bad,.5f,1),"nonbijective nearest matching rejected");
        // A very short band and an intentionally folded inner row must fail, not publish inverted faces.
        bad=Grid(.028f);int extra=bad.vertices.Length;
        bad=EndFixture(bad.vertices.Concat(new[]{new Vector3(2.021f,.02f,7.9f),new Vector3(2.021f,.02f,7.95f)}).ToArray(),
            bad.triangles.Concat(new[]{32*9+8,extra,extra+1}).ToArray());
        Reject(()=>LTRoadModuleEnds.Apply(bad,LTRoadModuleEnds.Create(bad,.01f,.05f)),"collapsed/inverted correction rejected");
        CheckFittedLODs(source,plan);
        // Authored LOD from the uncorrected source is aligned to the SAME LOD0 plan.
        var authored=LTRoadModuleMath.Simplify(source,.25f,.05f);
        var af=LTRoadModuleEnds.Apply(authored,plan);
        Require(EndSet(fitted,0).SetEquals(EndSet(af,0))&&EndSet(fitted,8).SetEquals(EndSet(af,8)),"authored LOD shares exact profile");
        // Real importers split vertices at UV/hard-normal seams. Coincident positions
        // must still deform identically without welding those attribute seams.
        var split=EndFixture(source.triangles.Select(i=>source.vertices[i]).ToArray(),Enumerable.Range(0,source.triangles.Length).ToArray());
        for(int i=0;i<split.vertices.Length;i++)
        {split.uv[i]=new Vector2(i%3,i/3);if((i/3)%2==0)split.normals[i]=Vector3.forward;}
        var splitFit=LTRoadModuleEnds.Apply(split,LTRoadModuleEnds.Create(split,.5f,.05f));
        CheckEndCopy(split,splitFit,.5f);
        Require(splitFit.vertices.Distinct().Count()==fitted.vertices.Distinct().Count(),"split import vertices share exact deformation");
        foreach(var group in Enumerable.Range(0,splitFit.vertices.Length).GroupBy(i=>splitFit.vertices[i]))
            if(group.Any(i=>split.normals[i].Equals(Vector3.forward)))
                Require(group.Where(i=>split.normals[i].Equals(Vector3.forward)).All(i=>Vector3.Dot(splitFit.normals[i],Vector3.forward)>.999f),"hard longitudinal/cap normal not averaged with top");
        for(int seed=0;seed<16;seed++)
        {
            var varied=Grid(.01f);var random=new System.Random(seed);
            for(int x=0;x<=8;x++)varied.vertices[32*9+x]+=new Vector3((float)random.NextDouble()*.01f, (float)random.NextDouble()*.01f,0);
            var vp=LTRoadModuleEnds.Create(varied,.75f,.05f);CheckEndCopy(varied,LTRoadModuleEnds.Apply(varied,vp),.75f);
        }

        if(!readLegacyAsset)
        {
            Console.WriteLine("PASS synthetic module end fitting: exact mixed LOD seams, copied channels, unchanged interior/source, safety rejections. Legacy mutable Road_1_Line asset probe excluded from this math-only run.");
            return;
        }
        string asset=Path.GetFullPath("Assets/Data/Models/Environment/Roads/Road_1_Line.fbx");
        Require(File.Exists(asset),"Road_1_Line diagnostic fixture exists");
        var raw=ReadRoadFbx(asset);string rawHash=LTRoadMesh.ContentHash(raw);
        var actualPlan=LTRoadModuleEnds.Create(raw,.5f,.05f);var actual=LTRoadModuleEnds.Apply(raw,actualPlan);
        Require(actualPlan.PointCount==10,"actual FBX ten-point profile");
        Require(actualPlan.Mismatch>.027f&&actualPlan.Mismatch<.029f,"actual FBX ~2.8cm mismatch");
        CheckEndCopy(raw,actual,.5f);CheckFittedLODs(raw,actualPlan);
        Require(rawHash==LTRoadMesh.ContentHash(raw),"actual FBX fixture never mutated");
        Console.WriteLine($"PASS module end fitting: synthetic + Road_1_Line FBX ({raw.vertices.Length} points, {raw.triangles.Length/3} tris, {actualPlan.PointCount} rim points, {actualPlan.Mismatch*1000:F2} mm mismatch); exact mixed LOD seams, curved/banked repeats, copied channels, unchanged interior/source, safety rejections. Raw FBX geometry only, not native Unity import/render.");
    }
    static LTRoadMesh.Chunk EndFixture(Vector3[] vertices,int[] indices)
    {
        int n=vertices.Length;
        return new LTRoadMesh.Chunk{vertices=vertices,triangles=indices,submeshes=new[]{(int[])indices.Clone()},
            normals=Enumerable.Repeat(Vector3.up,n).ToArray(),tangents=Enumerable.Repeat(new Vector4(1,0,0,-1),n).ToArray(),
            uv=vertices.Select(p=>new Vector2(p.x,p.z)).ToArray(),uv2=vertices.Select(p=>new Vector2(p.z,p.y)).ToArray(),
            uv3=vertices.Select(p=>new Vector2(p.x,p.y)).ToArray(),uv4=vertices.Select(p=>new Vector2(p.y,p.z)).ToArray(),
            colors=vertices.Select(p=>new Color(p.x,p.y,p.z,1)).ToArray()};
    }
    static HashSet<Vector2> EndSet(LTRoadMesh.Chunk c,float z)=>new HashSet<Vector2>(c.vertices.Where(p=>Math.Abs(p.z-z)<1e-5f).Select(p=>new Vector2(p.x,p.y)));
    static void CheckEndCopy(LTRoadMesh.Chunk source,LTRoadMesh.Chunk fitted,float band)
    {
        Require(source.vertices.Length==fitted.vertices.Length&&source.triangles.SequenceEqual(fitted.triangles),"fitting adds/removes no topology");
        Require(source.uv.SequenceEqual(fitted.uv)&&source.uv2.SequenceEqual(fitted.uv2)&&source.uv3.SequenceEqual(fitted.uv3)&&source.uv4.SequenceEqual(fitted.uv4)&&source.colors.SequenceEqual(fitted.colors),"all UV/color channels untouched");
        float min=source.vertices.Min(p=>p.z),max=source.vertices.Max(p=>p.z);
        Require(EndSet(fitted,min).SetEquals(EndSet(fitted,max)),"exact end profile equality");
        int unchanged=0;
        for(int i=0;i<source.vertices.Length;i++)
        {
            var p=source.vertices[i];
            if(p.z-min>=band&&max-p.z>=band)
            {Require(p.Equals(fitted.vertices[i])&&source.normals[i].Equals(fitted.normals[i])&&source.tangents[i].Equals(fitted.tangents[i]),"interior unchanged");unchanged++;}
            Require(LTRoadMesh.Finite(fitted.vertices[i])&&Math.Abs(fitted.normals[i].magnitude-1)<1e-4f,"finite normalized attributes");
            var t=fitted.tangents[i];Require(Math.Abs(Vector3.Dot(fitted.normals[i],new Vector3(t.x,t.y,t.z)))<1e-4f,"orthogonal tangents");
        }
        Require(unchanged>0,"fixture includes untouched interior");
    }
    static void CheckFittedLODs(LTRoadMesh.Chunk source,LTRoadModuleEnds.Plan plan)
    {
        var fitted=LTRoadModuleEnds.Apply(source,plan);var levels=new[]{fitted,
            LTRoadModuleMath.Simplify(fitted,.5f,.05f),LTRoadModuleMath.Simplify(fitted,.25f,.1f),LTRoadModuleMath.Simplify(fitted,.125f,.2f)};
        Vector3 min=fitted.vertices.Aggregate(Vector3.Min),max=fitted.vertices.Aggregate(Vector3.Max);
        var s=LTRoadMath.Settings.Default;s.mode=LTRoadMode.Asphalt;s.width=5;s.sampleSpacing=.5f;
        var path=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(0,1,0),4),new LTRoadPoint(new Vector3(1,3,25),9),new LTRoadPoint(new Vector3(8,5,55),-3)},Matrix4x4.identity,s);
        var bent=levels.Select(c=>LTRoadModuleMath.Bend(c,path,min,max,0,true,0,.06f,1,Matrix4x4.identity)).ToArray();
        for(int a=0;a<4;a++)for(int b=0;b<4;b++)
        {
            Require(EndSet(levels[a],min.z).SetEquals(EndSet(levels[b],max.z)),"all 16 LOD end combinations coincide");
            for(int section=1;section<bent[a].Count;section++)
            {
                var tail=new HashSet<Vector3>(Enumerable.Range(0,levels[a].vertices.Length).Where(i=>Math.Abs(levels[a].vertices[i].z-max.z)<1e-5f).Select(i=>bent[a][section-1].vertices[i]));
                var head=new HashSet<Vector3>(Enumerable.Range(0,levels[b].vertices.Length).Where(i=>Math.Abs(levels[b].vertices[i].z-min.z)<1e-5f).Select(i=>bent[b][section].vertices[i]));
                Require(tail.SetEquals(head),"bit-exact adjacent mixed LOD bent sections");
            }
        }
        Console.WriteLine("Module end fixture LOD tris: "+string.Join(" / ",levels.Select(c=>c.triangles.Length/3)));
    }
    // Read-only FBX binary control-point probe. Does not claim to emulate the Unity importer:
    // coordinates are cm -> metres, normals/UV/colors are synthetic test channels.
    static LTRoadMesh.Chunk ReadRoadFbx(string path,string geometryName=null)
    {
        using var stream=File.OpenRead(path);using var reader=new BinaryReader(stream);
        stream.Position=23;uint version=reader.ReadUInt32();Require(version>=7500,"probe expects 64-bit FBX nodes");
        double[] points=null;int[] polygons=null;string selected=null;int matches=0;
        bool Node(string geometry=null)
        {
            long end=(long)reader.ReadUInt64();reader.ReadUInt64();long length=(long)reader.ReadUInt64();int nameLength=reader.ReadByte();
            if(end==0)return false;
            string name=System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLength));long propsEnd=stream.Position+length;
            if(name=="Geometry")
            {
                Require(reader.ReadByte()=='L',"FBX geometry id");reader.ReadInt64();
                Require(reader.ReadByte()=='S',"FBX geometry name");
                geometry=System.Text.Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())).Split('\0')[0];
                bool match=geometryName==null||geometry==geometryName||
                    (geometryName.StartsWith("*",StringComparison.Ordinal)&&geometry.EndsWith(geometryName.Substring(1),StringComparison.Ordinal));
                if(match){matches++;if(selected==null)selected=geometry;}
            }
            if(geometry==selected&&selected!=null&&(name=="Vertices"||name=="PolygonVertexIndex"))
            {
                char type=(char)reader.ReadByte();int count=reader.ReadInt32(),encoding=reader.ReadInt32(),bytes=reader.ReadInt32();
                var data=reader.ReadBytes(bytes);using var raw=new MemoryStream(data);using var decoded=new MemoryStream();
                if(encoding==1){using var z=new ZLibStream(raw,CompressionMode.Decompress);z.CopyTo(decoded);}else raw.CopyTo(decoded);
                decoded.Position=0;using var values=new BinaryReader(decoded);
                if(name=="Vertices"){Require(type=='d',"FBX double vertices");points=Enumerable.Range(0,count).Select(_=>values.ReadDouble()).ToArray();}
                else {Require(type=='i',"FBX int polygon indices");polygons=Enumerable.Range(0,count).Select(_=>values.ReadInt32()).ToArray();}
            }
            stream.Position=propsEnd;
            while(stream.Position<end&&Node(geometry)){}
            stream.Position=end;return true;
        }
        while(Node()){}
        Require(geometryName==null||matches==1,"FBX requested geometry must match exactly one object");
        Require(points!=null&&polygons!=null,"FBX geometry arrays found");
        var vertices=Enumerable.Range(0,points.Length/3).Select(i=>new Vector3((float)(points[i*3]*.01),(float)(points[i*3+1]*.01),(float)(points[i*3+2]*.01))).ToArray();
        var indices=new List<int>();var face=new List<int>();
        foreach(int value in polygons)
        {
            face.Add(value<0?-value-1:value);
            if(value>=0)continue;
            for(int i=1;i+1<face.Count;i++)indices.AddRange(new[]{face[0],face[i],face[i+1]});face.Clear();
        }
        return EndFixture(vertices,indices.ToArray());
    }
}
