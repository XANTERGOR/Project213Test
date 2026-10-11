using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void SavedRockFarProbe(bool beforeAtlas=false)
    {
        const string folder="Assets/LocalTerrainGenerated/World_b06146b4c1/";
        var scene=File.ReadAllText("Assets/TerrainTest.unity");
        var blocks=Regex.Matches(scene,@"(?ms)^--- !u!\d+ &(-?\d+)[^\r\n]*\r?\n(.*?)(?=^---|\z)").Cast<Match>().ToDictionary(m=>m.Groups[1].Value,m=>m.Groups[2].Value);
        string Field(string block,string key)=>Regex.Match(block,@"(?m)^  "+Regex.Escape(key)+@": (.*)$").Groups[1].Value.Trim();
        string Ref(string value)=>Regex.Match(value,@"fileID: (-?\d+)").Groups[1].Value;
        float Number(string value)=>float.Parse(value,System.Globalization.CultureInfo.InvariantCulture);
        Vector4 Vec(string value)
        {
            var n=Regex.Matches(value,@"[xyzw]: ([^,}]+)").Cast<Match>().Select(m=>Number(m.Groups[1].Value)).ToArray();
            return new Vector4(n[0],n[1],n[2],n.Length>3?n[3]:0);
        }
        Matrix4x4 Matrix(string transform)
        {
            var block=blocks[transform];var p=Vec(Field(block,"m_LocalPosition"));var s=Vec(Field(block,"m_LocalScale"));var q=Vec(Field(block,"m_LocalRotation"));
            float x=q.x,y=q.y,z=q.z,w=q.w;var m=Matrix4x4.identity;
            m.m00=(1-2*(y*y+z*z))*s.x;m.m10=2*(x*y+z*w)*s.x;m.m20=2*(x*z-y*w)*s.x;
            m.m01=2*(x*y-z*w)*s.y;m.m11=(1-2*(x*x+z*z))*s.y;m.m21=2*(y*z+x*w)*s.y;
            m.m02=2*(x*z+y*w)*s.z;m.m12=2*(y*z-x*w)*s.z;m.m22=(1-2*(x*x+y*y))*s.z;
            m.m03=p.x;m.m13=p.y;m.m23=p.z;string parent=Ref(Field(block,"m_Father"));return parent=="0"?m:Matrix(parent)*m;
        }
        int totalOld=0,totalNew=0,meshes=0;
        foreach(var path in Directory.GetFiles(folder,"RockRender*.asset").OrderBy(p=>p,StringComparer.Ordinal))
        {
            string owner=Regex.Match(File.ReadLines(path).Single(l=>l.StartsWith("  ownerKey:")),@"-(-?\d+)-0$").Groups[1].Value;
            string go=Ref(Field(blocks[owner],"m_GameObject"));
            string transform=blocks.Single(b=>b.Value.StartsWith("Transform:")&&Ref(Field(b.Value,"m_GameObject"))==go).Key;
            var matrix=Matrix(transform);float error=Number(Field(blocks[owner],"meshLodError"));
            var savedMeshes=ReadSeamMeshes(path,true);
            var mesh=savedMeshes.Single(m=>m.name.EndsWith(" / Rock",StringComparison.Ordinal));
            var bakedRock=mesh;
            if(beforeAtlas)
            {
                string asset=File.ReadAllText(path);
                var combined=savedMeshes.Single(m=>m.id==Ref(Field(asset,"combined")));
                int slots=int.Parse(Field(asset,"sourceSubmeshes"));
                var partition=LTRockTransitionMath.Split(combined.vertices,combined.triangles,slots,matrix,
                    Number(Field(blocks[owner],"materialBlendWidth")),Number(Field(blocks[owner],"materialBlendSoftness")));
                // Exact Extract order: compact original IDs, never weld source attributes.
                var map=new Dictionary<int,int>();var ids=new List<int>();
                int Remap(int v){if(!map.TryGetValue(v,out int i)){i=ids.Count;map[v]=i;ids.Add(v);}return i;}
                var triangles=partition.rock.Select(t=>t.Select(Remap).ToArray()).ToArray();
                mesh=new SavedSeamMesh{name=bakedRock.name,vertices=ids.Select(i=>combined.vertices[i]).ToArray(),triangles=triangles,
                    uv=new[]{ids.Select(i=>combined.uv[0][i]).ToArray()},blend=new float[ids.Count]};
                Require(mesh.triangles.Sum(t=>t.Length)==bakedRock.triangles.Sum(t=>t.Length),"raw partition disagrees with saved LOD0");
            }
            var positions=mesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
            var before=(Vector3[])mesh.vertices.Clone();var uvBefore=mesh.uv.Select(u=>(Vector2[])u.Clone()).ToArray();
            var clock=System.Diagnostics.Stopwatch.StartNew();
            var levels=LTRockLODMath.RockLevels(positions,mesh.triangles,mesh.blend,error,4,mesh.uv,protectSourceUV:beforeAtlas);
            var old=new int[5];old[0]=mesh.triangles.Sum(t=>t.Length)/3;
            for(int l=1;l<=4;l++)
            {
                var original=LTRockLODMath.Reduce(positions,mesh.triangles,mesh.blend,error*Mathf.Pow(2,l-1),l+2);
                old[l]=original.Sum(t=>t.Length)/3;
                if(l<=2)
                {
                    if(!beforeAtlas)Require(original.Zip(levels[l],(a,b)=>a.SequenceEqual(b)).All(v=>v),"saved near rock LOD changed");
                    if(!beforeAtlas)
                    {
                        var saved=savedMeshes.Single(m=>m.name==mesh.name+" LOD"+l);
                        Require(saved.triangles.Zip(levels[l],(a,b)=>a.SequenceEqual(b)).All(v=>v),"near indices differ from the pre-existing baked asset");
                    }
                }
                RockFarTopology(positions,mesh.triangles,levels[l]);
                if(l>=3)Require(levels[l].Sum(t=>t.Length)<=levels[l-1].Sum(t=>t.Length),"saved far rock density backslide");
            }
            Require(before.SequenceEqual(mesh.vertices)&&uvBefore.Zip(mesh.uv,(a,b)=>a.SequenceEqual(b)).All(v=>v),"saved rock vertex/UV streams modified");
            int baseline=beforeAtlas?savedMeshes.Single(m=>m.name==mesh.name+" LOD4").triangles.Sum(t=>t.Length)/3:old[4];
            Require(levels[4].Sum(t=>t.Length)/3<baseline,"saved rock did not improve over previous LOD4");
            totalOld+=baseline;totalNew+=levels[4].Sum(t=>t.Length)/3;meshes++;
            if(beforeAtlas)Console.WriteLine($"{Path.GetFileName(path)} saved LOD4 {baseline}; raw pre-atlas vertices {mesh.vertices.Length}, atlas vertices {bakedRock.vertices.Length}");
            Console.WriteLine($"{Path.GetFileName(path)}: {(beforeAtlas?"legacy reducer on raw geometry":"previous")} LOD0..4 {string.Join("/",old)}; new {string.Join("/",levels.Select(l=>l.Sum(t=>t.Length)/3))}; {mesh.uv.Length} UV channels; {clock.Elapsed.TotalSeconds:F2}s managed bake+checks");
        }
        Require(meshes==4&&totalNew<totalOld,"saved far rocks did not improve");
        Console.WriteLine($"PASS saved rock-only LOD4: {totalOld}->{totalNew} triangles in {meshes} rocks; {(beforeAtlas?"LOD before atlas, near policy on raw geometry":"exact LOD1/2")}, fixed source/contact borders, submesh seams and manifold winding. Assets/scene read only; not native rendering.");
    }

    static void RockFarTopology(Vector3[] positions,int[][] original,int[][] output)
    {
        Dictionary<long,(int uses,int orientation)> Edges(int[][] faces)
        {
            var edges=new Dictionary<long,(int,int)>();var unique=new HashSet<(int,int,int)>();
            foreach(var tris in faces)for(int t=0;t<tris.Length;t+=3)
            {
                int a=tris[t],b=tris[t+1],c=tris[t+2];
                Require(a!=b&&a!=c&&b!=c&&Vector3.Cross(positions[b]-positions[a],positions[c]-positions[a]).sqrMagnitude>1e-14f,"rock degenerate triangle");
                var sorted=new[]{a,b,c};Array.Sort(sorted);Require(unique.Add((sorted[0],sorted[1],sorted[2])),"rock duplicate triangle");
                for(int k=0;k<3;k++)
                {
                    int x=tris[t+k],y=tris[t+(k+1)%3];long key=((long)Math.Min(x,y)<<32)|(uint)Math.Max(x,y);
                    edges.TryGetValue(key,out var e);edges[key]=(e.Item1+1,e.Item2+(x<y?1:-1));
                }
            }
            return edges;
        }
        Require(original.Length==output.Length,"rock material slot changed");
        var before=Edges(original);var after=Edges(output);
        Require(after.All(e=>e.Value.uses<=2&&(e.Value.uses!=2||e.Value.orientation==0)),"rock nonmanifold or incoherent winding");
        Require(before.Where(e=>e.Value.uses==1).OrderBy(e=>e.Key).SequenceEqual(after.Where(e=>e.Value.uses==1).OrderBy(e=>e.Key)),"rock contact/UV border changed");
        for(int s=0;s<original.Length;s++)
        {
            var a=Edges(new[]{original[s]});var b=Edges(new[]{output[s]});
            Require(a.Where(e=>e.Value.uses==1).OrderBy(e=>e.Key).SequenceEqual(b.Where(e=>e.Value.uses==1).OrderBy(e=>e.Key)),"rock material boundary changed");
        }
    }
}
