using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadCollarChecks()
    {
        LTRoadMesh.Chunk Grid(int across,int rows,float marker,bool closed=false)
        {
            var v=new List<Vector3>();var t=new List<int>();int columns=across+1;
            for(int z=0;z<=rows;z++)for(int x=0;x<=across;x++)
            {float a=2*Mathf.PI*x/across;v.Add(closed?new Vector3(Mathf.Cos(a),Mathf.Sin(a),z*8f/rows):new Vector3(x*4f/across-2,0,z*8f/rows));}
            if(closed)for(int z=0;z<=rows;z++)v[z*columns+across]=v[z*columns];
            for(int z=0;z<rows;z++)for(int x=0;x<across;x++)
            {int a=z*columns+x,b=a+columns;t.AddRange(new[]{a,b,a+1,a+1,b,b+1});}
            var c=EndFixture(v.ToArray(),t.ToArray());
            c.colors=Enumerable.Repeat(new Color(marker,.2f,.3f,1),v.Count).ToArray();return c;
        }
        foreach(bool closed in new[]{false,true})
        {
            var dense=Grid(12,16,1,closed);var coarse=Grid(6,4,2,closed);
            string before=LTRoadMesh.ContentHash(coarse),refBefore=LTRoadMesh.ContentHash(dense);
            // 1.00005 cuts just beyond a source row: tiny valid clipped triangles used
            // to lose their geometric normal via Vector3.normalized and falsely flip.
            foreach(float width in new[]{.5f,.67f,1.1f,1.00005f})
            {
                var joined=LTRoadModuleCollar.Apply(dense,coarse,width);
                VerifyCollarCentre(coarse,joined,width,8-width);VerifyCutManifold(joined,width*.5f,width,8-width,8-width*.5f);
                Require(joined.colors.Any(c=>c.r==1)&&joined.colors.Any(c=>c.r==2),"reference ends + authored centre attributes");
                Require(EndSet(joined,0).SetEquals(EndSet(dense,0))&&EndSet(joined,8).SetEquals(EndSet(dense,8)),"exact LOD0 outer contours");
                if(!closed&&width==1.00005f)
                {
                    int tiny=0;
                    for(int i=0;i<joined.triangles.Length;i+=3)
                    {
                        var p=joined.vertices;int a=joined.triangles[i],b=joined.triangles[i+1],c=joined.triangles[i+2];
                        var cross=Vector3.Cross(p[b]-p[a],p[c]-p[a]);
                        Require(cross.y>0,"tiny-cut regression has no genuinely reversed or collapsed faces");
                        if(cross.sqrMagnitude>1e-22f&&cross.normalized==Vector3.zero)tiny++;
                    }
                    Require(tiny>0,"fixture must reproduce Unity normalization losing valid tiny-face normals");
                }
            }
            var unchanged=LTRoadModuleCollar.Prepare(dense,dense,null,.5f,out bool rebuilt);
            Require(!rebuilt&&ReferenceEquals(unchanged,dense),"already compatible LOD has no new topology");
            var synthetic=LTRoadModuleCollar.Prepare(dense,coarse,null,.5f,out rebuilt);Require(rebuilt,"different end topology reconstructed");
            Require(before==LTRoadMesh.ContentHash(coarse)&&refBefore==LTRoadMesh.ContentHash(dense),"source chunks untouched");
            var split=SplitCollarFixture(coarse);var splitResult=LTRoadModuleCollar.Apply(SplitCollarFixture(dense),split,.67f);
            VerifyCollarCentre(split,splitResult,.67f,8-.67f);VerifyCutManifold(splitResult,.335f,.67f,8-.67f,8-.335f);
            Require(splitResult.submeshes.Length==2&&splitResult.submeshes.All(s=>s.Length>0),"multi-material surfaces retained");
        }
        void Reject(Action action,string label)
        {bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}Require(failed,label);}
        var guardDense=Grid(8,16,1);var guardCoarse=Grid(4,4,2);
        foreach(float width in new[]{0,.001f,4,float.NaN,float.PositiveInfinity})
            Reject(()=>LTRoadModuleCollar.Apply(guardDense,guardCoarse,width),"invalid zone width rejected");
        var outside=Grid(4,4,2);outside.vertices[0]=new Vector3(-2,0,-5);
        Reject(()=>LTRoadModuleCollar.Apply(guardDense,outside,.5f),"large longitudinal mismatch rejected");
        var emptyCentre=Grid(4,4,2);for(int i=0;i<emptyCentre.vertices.Length;i++){var v=emptyCentre.vertices[i];v.z*=.01f;emptyCentre.vertices[i]=v;}
        Reject(()=>LTRoadModuleCollar.Apply(guardDense,emptyCentre,.5f),"missing centre contour rejected");
        var duplicate=Grid(4,4,2);duplicate.triangles=duplicate.triangles.Concat(duplicate.triangles).ToArray();duplicate.submeshes=new[]{duplicate.triangles};
        Reject(()=>LTRoadModuleCollar.Apply(guardDense,duplicate,.5f),"overlapping/nonmanifold cut rejected");
        var folded=Grid(8,16,1);
        for(int i=0;i<folded.vertices.Length;i++){var p=folded.vertices[i];p.y=4*p.z;folded.vertices[i]=p;}
        bool flipRejected=false;
        try{LTRoadModuleCollar.Apply(folded,guardCoarse,.67f);}
        catch(InvalidOperationException e){flipRejected=e.Message.Contains("переворачивает поверхность");}
        Require(flipRejected,"genuinely folded bridge remains rejected by orientation guard");
        const string path="Assets/Data/Models/Environment/Roads/Road_1_Line.fbx";
        var raw=Enumerable.Range(0,3).Select(l=>ReadRoadFbx(path,"Asphalt_LOD"+l)).ToArray();
        var hashes=raw.Select(LTRoadMesh.ContentHash).ToArray();
        var plan=LTRoadModuleEnds.Create(raw[0],.5f,.05f);var reference=LTRoadModuleEnds.Apply(raw[0],plan);
        var levels=new[]{reference,LTRoadModuleCollar.Apply(reference,raw[1],.5f),LTRoadModuleCollar.Apply(reference,raw[2],.5f)};
        for(int l=1;l<3;l++)
        {
            Console.WriteLine($"Actual raw FBX LOD{l}: {raw[l].triangles.Length/3} -> {levels[l].triangles.Length/3} triangles with collars.");
            VerifyCollarCentre(raw[l],levels[l],-8.5f,8.5f);VerifyCutManifold(levels[l],-8.75f,-8.5f,8.5f,8.75f);
            LTRoadModuleEnds.ValidateRims(reference,levels[l]);
            Require(LTRoadMesh.ContentHash(levels[l])==LTRoadMesh.ContentHash(LTRoadModuleCollar.Apply(reference,raw[l],.5f)),"deterministic collar rebuild");
        }
        for(int l=0;l<3;l++)Require(hashes[l]==LTRoadMesh.ContentHash(raw[l]),"source asset geometry unchanged");
        var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.Asphalt;settings.width=5;settings.sampleSpacing=.5f;
        var snapshot=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(0,1,0),4),new LTRoadPoint(new Vector3(1,3,25),9),new LTRoadPoint(new Vector3(8,5,55),-3)},Matrix4x4.identity,settings);
        var min=reference.vertices.Aggregate(Vector3.Min);var max=reference.vertices.Aggregate(Vector3.Max);
        var bent=levels.Select(c=>LTRoadModuleMath.Bend(c,snapshot,min,max,0,true,0,.06f,1,Matrix4x4.identity)).ToArray();
        for(int a=0;a<3;a++)for(int b=0;b<3;b++)for(int i=1;i<bent[a].Count;i++)
        {var shared=new HashSet<Vector3>(bent[a][i-1].vertices);shared.IntersectWith(bent[b][i].vertices);Require(shared.Count==10,"all nine native-FBX-derived mixed LOD joints exact");}
        Console.WriteLine("PASS road collars: open/closed contours, different topology, split UV/normal vertices, materials/channels, identical source/centre, deterministic output, four watertight internal cuts, actual raw FBX 3 LODs and all nine bent/banked mixed-level joins. Raw FBX probe is not a native Unity import/render test.");
    }
    static LTRoadMesh.Chunk SplitCollarFixture(LTRoadMesh.Chunk input)
    {
        var source=EndFixture(input.triangles.Select(i=>input.vertices[i]).ToArray(),Enumerable.Range(0,input.triangles.Length).ToArray());
        for(int i=0;i<source.vertices.Length;i++){source.uv[i]=new Vector2(i%3,i/3);source.normals[i]=(i/3)%2==0?Vector3.up:Vector3.down;}
        source.submeshes=new[]{new List<int>(),new List<int>()}.Select((list,s)=>Enumerable.Range(0,source.triangles.Length/3).Where(i=>i%2==s).SelectMany(i=>new[]{3*i,3*i+1,3*i+2}).ToArray()).ToArray();
        return source;
    }
    static void VerifyCollarCentre(LTRoadMesh.Chunk source,LTRoadMesh.Chunk result,float min,float max)
    {
        var output=new HashSet<(Vector3,Vector3,Vector3)>();
        for(int i=0;i<result.triangles.Length;i+=3)output.Add((result.vertices[result.triangles[i]],result.vertices[result.triangles[i+1]],result.vertices[result.triangles[i+2]]));
        int preserved=0;
        for(int i=0;i<source.triangles.Length;i+=3)
        {
            int a=source.triangles[i],b=source.triangles[i+1],c=source.triangles[i+2];
            if(new[]{a,b,c}.Any(j=>source.vertices[j].z<min||source.vertices[j].z>max))continue;
            Require(output.Contains((source.vertices[a],source.vertices[b],source.vertices[c])),"authored centre triangle unchanged");preserved++;
            foreach(int j in new[]{a,b,c})Require(Enumerable.Range(0,result.vertices.Length).Any(k=>source.vertices[j].Equals(result.vertices[k])&&
                source.normals[j].Equals(result.normals[k])&&source.tangents[j].Equals(result.tangents[k])&&source.uv[j].Equals(result.uv[k])&&
                (source.uv2==null||source.uv2[j].Equals(result.uv2[k]))&&(source.uv3==null||source.uv3[j].Equals(result.uv3[k]))&&
                (source.uv4==null||source.uv4[j].Equals(result.uv4[k]))&&(source.colors==null||source.colors[j].Equals(result.colors[k]))),"author centre channels exactly preserved");
        }
        Require(preserved>10,"enough retained authored centre triangles");
    }
    static void VerifyCutManifold(LTRoadMesh.Chunk mesh,params float[] planes)
    {
        foreach(float z in planes)
        {
            var edges=new Dictionary<(Vector3,Vector3),int>();var counts=new Dictionary<(Vector3,Vector3),int>();
            for(int i=0;i<mesh.triangles.Length;i+=3)for(int j=0;j<3;j++)
            {
                var a=mesh.vertices[mesh.triangles[i+j]];var b=mesh.vertices[mesh.triangles[i+(j+1)%3]];
                if(Math.Abs(a.z-z)>1e-6||Math.Abs(b.z-z)>1e-6)continue;
                bool order=a.x<b.x||(a.x==b.x&&a.y<b.y);var key=order?(a,b):(b,a);edges.TryGetValue(key,out int uses);counts.TryGetValue(key,out int count);
                edges[key]=uses+(order?1:-1);counts[key]=count+1;
            }
            Require(edges.Count>0&&edges.Values.All(v=>v==0)&&counts.Values.All(v=>v==2),"cut edges have two opposite-wound uses");
        }
    }
}
