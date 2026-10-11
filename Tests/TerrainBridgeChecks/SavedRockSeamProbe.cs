using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    sealed class SavedSeamMesh {public string name,id;public Vector3[] vertices;public int[][] triangles;public Vector2[][] uv;public float[] blend;}
    static List<SavedSeamMesh> ReadSeamMeshes(string path,bool attributes=false)
    {
        var output=new List<SavedSeamMesh>();SavedSeamMesh mesh=null;var ranges=new List<(int start,int count)>();
        byte[] indices=null,vertices=null;int format=0,count=0,first=0;
        var channels=new List<(int stream,int offset,int format,int dimension)>();int stream=0,offset=0,channelFormat=0;
        int V(string s)=>int.Parse(s.Substring(s.IndexOf(':')+1));
        void Finish()
        {
            if(mesh==null)return;int stride=vertices.Length/count;Require(stride*count==vertices.Length&&stride>=12,"saved vertex stride");
            mesh.vertices=new Vector3[count];for(int i=0;i<count;i++)mesh.vertices[i]=new Vector3(BitConverter.ToSingle(vertices,i*stride),BitConverter.ToSingle(vertices,i*stride+4),BitConverter.ToSingle(vertices,i*stride+8));
            if(attributes)
            {
                Require(channels.Where(c=>c.dimension>0).All(c=>c.stream==0),"saved attributes require single stream");
                mesh.blend=new float[count];var color=channels[3];
                for(int i=0;i<count&&color.dimension>0;i++)
                {
                    Require(color.format==0||color.format==2,"saved color format");
                    mesh.blend[i]=color.format==0?BitConverter.ToSingle(vertices,i*stride+color.offset):vertices[i*stride+color.offset]/255f;
                }
                var uvs=new List<Vector2[]>();
                for(int channel=4;channel<=6;channel++)if(channels[channel].dimension>0)
                {
                    var c=channels[channel];Require(c.format==0&&c.dimension>=2,"saved UV format");var uv=new Vector2[count];
                    for(int i=0;i<count;i++)uv[i]=new Vector2(BitConverter.ToSingle(vertices,i*stride+c.offset),BitConverter.ToSingle(vertices,i*stride+c.offset+4));
                    uvs.Add(uv);
                }
                mesh.uv=uvs.ToArray();
            }
            int bytes=format==0?2:4;mesh.triangles=ranges.Select(r=>Enumerable.Range(0,r.count).Select(i=>bytes==2?(int)BitConverter.ToUInt16(indices,r.start+i*bytes):BitConverter.ToInt32(indices,r.start+i*bytes)).ToArray()).ToArray();
            output.Add(mesh);mesh=null;
        }
        foreach(var line in File.ReadLines(path))
        {
            if(line.StartsWith("---")){Finish();if(line.StartsWith("--- !u!43")){mesh=new SavedSeamMesh{id=line.Substring(line.IndexOf('&')+1)};ranges.Clear();channels.Clear();indices=vertices=null;}continue;}
            if(mesh==null)continue;
            if(line.StartsWith("  m_Name:"))mesh.name=line.Substring(9).Trim();
            else if(line.StartsWith("    firstByte:"))first=V(line);
            else if(line.StartsWith("    indexCount:"))ranges.Add((first,V(line)));
            else if(line.StartsWith("  m_IndexFormat:"))format=V(line);
            else if(line.StartsWith("  m_IndexBuffer:"))indices=Convert.FromHexString(line.Substring(line.IndexOf(':')+1).Trim());
            else if(line.StartsWith("    m_VertexCount:"))count=V(line);
            else if(line.StartsWith("    - stream:"))stream=V(line);
            else if(line.StartsWith("      offset:"))offset=V(line);
            else if(line.StartsWith("      format:"))channelFormat=V(line);
            else if(line.StartsWith("      dimension:"))channels.Add((stream,offset,channelFormat,V(line)));
            else if(line.StartsWith("    _typelessdata:"))vertices=Convert.FromHexString(line.Substring(line.IndexOf(':')+1).Trim());
        }
        Finish();return output;
    }
    static void SavedRockSeamProbe()
    {
        const string folder="Assets/LocalTerrainGenerated/World_b06146b4c1/";
        var data=ReadSavedHoleChunk(folder+"Chunk_0_0_SpatialLOD.asset");
        var bank=ReadSeamMeshes(folder+"Chunk_0_0_SpatialLOD.asset").Single();
        var safe=LTRockSeamMath.InteriorSpatialVertices(bank.vertices,64,64);
        var keys=bank.vertices.Select(LTRockSeamMath.Key).ToHashSet();var safeKeys=bank.vertices.Where((p,i)=>safe[i]).Select(LTRockSeamMath.Key).ToHashSet();
        var parts=new List<LTRockSeamMath.Part>{new LTRockSeamMath.Part{positions=bank.vertices,triangles=bank.triangles,movable=safe,terrain=true,
            constraints=LTRockSeamMath.SpatialConstraints(data.topology.cells,data.topology.patches)}};
        var lod0=ReadSeamMeshes(folder+"Chunk_0_0.asset").Single();parts.Add(new LTRockSeamMath.Part{positions=lod0.vertices,triangles=lod0.triangles,movable=lod0.vertices.Select(p=>safeKeys.Contains(LTRockSeamMath.Key(p))).ToArray(),terrain=true});
        var text=File.ReadAllText("Assets/TerrainTest.unity");
        var groupBounds=new List<Bounds>();
        float tolerance=float.PositiveInfinity;
        bool Contains(Bounds box,Vector3 v)=>v.x>=box.min.x&&v.x<=box.max.x&&v.y>=box.min.y&&v.y<=box.max.y&&v.z>=box.min.z&&v.z<=box.max.z;
        var blocks=Regex.Matches(text,@"(?ms)^--- !u!\d+ &(-?\d+)[^\r\n]*\r?\n(.*?)(?=^---|\z)").Cast<Match>().ToDictionary(m=>m.Groups[1].Value,m=>m.Groups[2].Value);
        string Field(string block,string key)=>Regex.Match(block,@"(?m)^  "+Regex.Escape(key)+@": (.*)$").Groups[1].Value.Trim();
        string Ref(string value)=>Regex.Match(value,@"fileID: (-?\d+)").Groups[1].Value;
        Vector4 Vec(string value)
        {
            var nums=Regex.Matches(value,@"[xyzw]: ([^,}]+)").Cast<Match>().Select(m=>float.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return new Vector4(nums[0],nums[1],nums[2],nums.Length>3?nums[3]:0);
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
        foreach(var path in Directory.GetFiles(folder,"RockRender*.asset"))
        {
            string owner=Regex.Match(File.ReadLines(path).Single(l=>l.StartsWith("  ownerKey:")),@"-(-?\d+)-0$").Groups[1].Value;
            string go=Ref(Field(blocks[owner],"m_GameObject"));
            tolerance=Math.Min(tolerance,float.Parse(Field(blocks[owner],"terrainSeamLodError"),System.Globalization.CultureInfo.InvariantCulture));
            string transform=blocks.Single(b=>b.Value.StartsWith("Transform:")&&Ref(Field(b.Value,"m_GameObject"))==go).Key;
            var matrix=Matrix(transform);
            var stampContact=new List<Vector3>();
            foreach(var mesh in ReadSeamMeshes(path).Where(m=>m.name.Contains("/ Transition")))
            {
                var v=mesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();var mask=v.Select(p=>keys.Contains(LTRockSeamMath.Key(p))).ToArray();
                var lod=Regex.Match(mesh.name,@"LOD(\d+)$");
                if(!lod.Success)stampContact.AddRange(v);
                parts.Add(new LTRockSeamMath.Part{positions=v,triangles=mesh.triangles,movable=mask,onlyLevel=lod.Success?int.Parse(lod.Groups[1].Value):0});
                Console.WriteLine(Path.GetFileName(path)+" "+mesh.name+": shared vertices "+mask.Count(b=>b));
            }
            var box=new Bounds(stampContact[0],Vector3.zero);foreach(var v in stampContact)box.Encapsulate(v);
            box.Expand(new Vector3(16,100000,16));bool joined;
            do{joined=false;foreach(var old in groupBounds.ToArray())if(box.Intersects(old)){box.Encapsulate(old);groupBounds.Remove(old);joined=true;}}while(joined);
            groupBounds.Add(box);
        }
        var contact=parts.Skip(2).SelectMany(p=>p.positions).ToArray();
        float minX=contact.Min(p=>p.x)-8,maxX=contact.Max(p=>p.x)+8,minZ=contact.Min(p=>p.z)-8,maxZ=contact.Max(p=>p.z)+8;
        var eligible=bank.vertices.Select(v=>v.x>=minX&&v.x<=maxX&&v.z>=minZ&&v.z<=maxZ).ToArray();
        foreach(var part in parts)
        {
            part.targets=part.positions.Select(v=>v.x>=minX&&v.x<=maxX&&v.z>=minZ&&v.z<=maxZ).ToArray();
            for(int v=0;v<part.movable.Length;v++)part.movable[v]&=part.targets[v];
            if(part.terrain)part.heightErrors=new[]{0f,2f,30f,40f,50f};
        }
        Console.WriteLine("Eligible terrain vertices: "+safe.Count(b=>b));
        var clock=System.Diagnostics.Stopwatch.StartNew();
        var errors=Enumerable.Range(0,5).Select(l=>l==0?0:tolerance*Mathf.Pow(2,l-1)).ToArray();
        var result=new LTRockSeamMath.Result{maps=errors.Select(_=>parts.Select(p=>Enumerable.Range(0,p.positions.Length).ToArray()).ToArray()).ToArray(),collapses=new int[5]};
        var groupResults=new List<(Bounds box,LTRockSeamMath.Result built)>();
        var baselineMap=Enumerable.Range(0,bank.vertices.Length).ToArray();
        var oldSafe=LTRockSeamMath.StableSpatialVertices(bank.vertices,data.topology.cells,data.topology.patches,64,64);
        var oldKeys=bank.vertices.Where((v,i)=>oldSafe[i]).Select(LTRockSeamMath.Key).ToHashSet();
        foreach(var box in groupBounds)
        {
            var groupParts=parts.Select(p=>new LTRockSeamMath.Part{positions=p.positions,triangles=p.triangles,
                movable=p.positions.Select((v,i)=>p.movable[i]&&Contains(box,v)).ToArray(),targets=p.positions.Select(v=>Contains(box,v)).ToArray(),
                onlyLevel=p.onlyLevel,terrain=p.terrain,constraints=p.constraints,heightErrors=p.heightErrors}).ToArray();
            // Take the v3 baseline before the new disjoint-group star protection.
            var baselineParts=groupParts.Select(p=>new LTRockSeamMath.Part{positions=p.positions,triangles=p.triangles,terrain=p.terrain,targets=p.targets,
                movable=p.positions.Select((v,i)=>p.movable[i]&&(!p.terrain||oldKeys.Contains(LTRockSeamMath.Key(v)))).ToArray(),
                onlyLevel=p.onlyLevel<0?-1:p.onlyLevel==4?1:2}).ToArray();
            foreach(var p in groupParts)if(p.terrain)LTRockSeamMath.ProtectOutsideStars(p.movable,p.targets,p.constraints==null?p.triangles:p.triangles.Concat(new[]{p.constraints}));
            var built=LTRockSeamMath.Build(groupParts,errors,progressive:true);
            groupResults.Add((box,built));
            var baseline=LTRockSeamMath.Build(baselineParts,new[]{0f,errors[4]});
            for(int v=0;v<baselineMap.Length;v++)if(baseline.maps[1][0][v]!=v)baselineMap[v]=baseline.maps[1][0][v];
            Console.WriteLine("Actual separate group: "+string.Join("/",built.collapses));
            for(int l=0;l<5;l++)
            {
                result.collapses[l]+=built.collapses[l];
                for(int p=0;p<parts.Count;p++)for(int v=0;v<parts[p].positions.Length;v++)if(built.maps[l][p][v]!=v)
                {Require(result.maps[l][p][v]==v,"independent groups moved the same vertex");result.maps[l][p][v]=built.maps[l][p][v];}
            }
        }
        Console.WriteLine("Saved geometry only; collapses "+string.Join("/",result.collapses)+"; elapsed "+clock.Elapsed);
        var bandPlans=new LTRockSeamIndices.Plan[5];
        var runtimePlans=new LTRockSeamIndices.Plan[5];
        var baselinePlan=new LTRockSeamIndices.Plan(baselineMap,groupBounds.SelectMany(box=>LTRockSeamBandMath.BuildSpatial(bank.vertices,bank.triangles[0],data.topology.cells,data.topology.patches,
            bank.vertices.Select(v=>Contains(box,v)).ToArray(),baselineMap,parts[0].heightErrors[4])).ToArray());
        for(int level=0;level<5;level++)
        {
            var bands=level==0?Array.Empty<LTRockSeamIndices.Band>():groupBounds.SelectMany(box=>LTRockSeamBandMath.BuildSpatial(bank.vertices,bank.triangles[0],data.topology.cells,data.topology.patches,
                bank.vertices.Select(v=>Contains(box,v)).ToArray(),result.maps[level][0],parts[0].heightErrors[level])).ToArray();
            bandPlans[level]=new LTRockSeamIndices.Plan(result.maps[level][0],bands);
            foreach(var group in groupResults)
            {
                var perGroup=level==0?Array.Empty<LTRockSeamIndices.Band>():LTRockSeamBandMath.BuildSpatial(bank.vertices,bank.triangles[0],data.topology.cells,data.topology.patches,
                    bank.vertices.Select(v=>Contains(group.box,v)).ToArray(),group.built.maps[level][0],parts[0].heightErrors[level]);
                var plan=new LTRockSeamIndices.Plan(group.built.maps[level][0],perGroup);
                runtimePlans[level]=runtimePlans[level]==null?plan:LTRockSeamIndices.Plan.Combine(runtimePlans[level],plan);
            }
            Console.WriteLine($"Native seam LOD{level}: {bands.Length} invariant regions");
        }
        HashSet<(Vector3Int,Vector3Int)> Boundary(LTRockSeamMath.Part part,IReadOnlyList<int> indices,bool ground=false)
        {
            var edges=new HashSet<(Vector3Int,Vector3Int)>();
            for(int i=0;i<indices.Count;i+=3)for(int k=0;k<3;k++)
            {
                var a=LTRockSeamMath.Key(part.positions[indices[i+k]]);var b=LTRockSeamMath.Key(part.positions[indices[i+(k+1)%3]]);if(a==b)continue;
                if(!edges.Remove((b,a)))Require(edges.Add((a,b)),"saved remap produced overlapping edges");
            }
            if(ground)edges.RemoveWhere(e=>e.Item1.x==e.Item2.x&&(e.Item1.x==0||e.Item1.x==2560000)||e.Item1.z==e.Item2.z&&(e.Item1.z==0||e.Item1.z==2560000));return edges;
        }
        HashSet<(Vector3Int,Vector3Int)> BridgeRim(int level,bool remap)
        {
            var set=new HashSet<(Vector3Int,Vector3Int)>();
            for(int p=2;p<parts.Count;p++)if(parts[p].onlyLevel==level)
            {var indices=parts[p].triangles.SelectMany(t=>t).ToList();if(remap)LTRockSeamMath.Remap(indices,result.maps[level][p]);foreach(var e in Boundary(parts[p],indices))set.Add((e.Item2,e.Item1));}
            return set;
        }
        var originalRim=Boundary(parts[0],bank.triangles.SelectMany(t=>t).ToArray(),true);
        var topo=new LTSpatialLODTopology(1,1,data.divisions,new[]{data.topology});
        foreach(int level in new[]{0,1,2,3,4})
        {
            for(int p=0;p<data.topology.patches.Length;p++)topo.SetLevel(0,p,level);topo.Update();
            var indices=new List<int>();topo.WriteIndices(0,indices);int before=indices.Count/3;bandPlans[level].Apply(indices);
            var actual=new List<int>();topo.WriteIndices(0,actual,runtimePlans[level]);
            Require(SpatialIndexTriangles(actual.ToArray()).SetEquals(SpatialIndexTriangles(indices.ToArray())),"separate group composition differs from joint map");
            if(level==4)
            {
                var baseline=new List<int>();topo.WriteIndices(0,baseline,baselinePlan);
                Console.WriteLine($"Same settings / separate groups LOD4: stable-star v3 {baseline.Count/3} -> all-variant v4 {actual.Count/3} triangles");
                Require(actual.Count<baseline.Count*.8f,"saved far seam did not improve materially over v3");
            }
            var rim=Boundary(parts[0],indices,true);var missing=originalRim.Except(BridgeRim(level,false)).ToHashSet();
            var bridgeEdges=BridgeRim(level,true);
            bool Close(Vector3Int a,Vector3Int b)=>Math.Abs(a.x-b.x)<=3&&Math.Abs(a.y-b.y)<=3&&Math.Abs(a.z-b.z)<=3;
            var mismatched=rim.Where(e=>!bridgeEdges.Contains(e)&&!bridgeEdges.Any(b=>Close(e.Item1,b.Item1)&&Close(e.Item2,b.Item2))).ToArray();
            double Distance(Vector3Int a,Vector3Int b,Vector3 p)
            {var av=(Vector3)a;var delta=(Vector3)b-av;float t=Mathf.Clamp01(Vector3.Dot(p-av,delta)/delta.sqrMagnitude);return Vector3.Distance(p,av+delta*t);}
            foreach(var e in mismatched)
            {
                double gap=Enumerable.Range(0,5).Max(k=>bridgeEdges.Min(b=>Distance(b.Item1,b.Item2,Vector3.Lerp((Vector3)e.Item1,(Vector3)e.Item2,k*.25f))));
                Require(gap<=5,"terrain/bridge contour disagreement in saved geometry: "+e+" gap="+gap/10000);
            }
            Console.WriteLine($"LOD{level}: terrain {before}->{indices.Count/3} triangles; contour {originalRim.Count}->{rim.Count}; pre-existing unmatched quantized edges {missing.Count}");
        }
        var random=new System.Random(317);for(int trial=0;trial<40;trial++)
        {
            int seamLevel=4;for(int p=0;p<data.topology.patches.Length;p++){int level=random.Next(1,5);seamLevel=Math.Min(level,seamLevel);topo.SetLevel(0,p,level);}topo.Update();topo.Validate();
            var indices=new List<int>();topo.WriteIndices(0,indices);bandPlans[seamLevel].Apply(indices);
            var reference=bank.triangles.SelectMany(t=>t).ToList();bandPlans[seamLevel].Apply(reference);
            Require(Boundary(parts[0],indices,true).SetEquals(Boundary(parts[0],reference,true)),"mixed spatial patches changed joint rim or opened an extra crack");
        }
        Console.WriteLine("PASS: 40 mixed spatial patch assignments, stable remapped contour; actual saved assets read only.");
        int paired=0;var rawBuffer=new List<int>();var finalBuffer=new List<int>();
        (int raw,int final) Count(int[] levels,int seamLevel)
        {
            for(int p=0;p<levels.Length;p++)topo.SetLevel(0,p,levels[p]);topo.Update();topo.Validate();
            topo.WriteIndices(0,rawBuffer);topo.WriteIndices(0,finalBuffer,runtimePlans[seamLevel]);
            Boundary(parts[0],finalBuffer,true); // Reject same-direction edges / overlaps in every paired layout.
            var faces=SpatialIndexTriangles(rawBuffer.ToArray());
            foreach(var band in bandPlans[seamLevel].bands)for(int i=0;i<band.source.Length;i+=3)
                Require(faces.Contains(LTRockSeamIndices.Face(band.source[i],band.source[i+1],band.source[i+2])),"saved native source not present");
            return (rawBuffer.Count/3,finalBuffer.Count/3);
        }
        for(int seamLevel=1;seamLevel<5;seamLevel++)
        {
            random=new System.Random(824);int minReduction=int.MaxValue,maxReduction=0;
            for(int trial=0;trial<250;trial++)
            {
                var levels=Enumerable.Range(0,data.topology.patches.Length).Select(_=>random.Next(1,4)).ToArray();
                int p=random.Next(levels.Length);var a=Count(levels,seamLevel);levels[p]++;var b=Count(levels,seamLevel);
                minReduction=Math.Min(minReduction,Math.Min(a.raw-a.final,b.raw-b.final));maxReduction=Math.Max(maxReduction,Math.Max(a.raw-a.final,b.raw-b.final));
                Require(b.final-a.final<=Math.Max(0,b.raw-a.raw),$"saved seam density backslide beyond ordinary terrain: LOD{seamLevel}, trial {trial}, {a}->{b}");paired++;
            }
            Console.WriteLine($"LOD{seamLevel} native reductions {minReduction}..{maxReduction}");
        }
        Console.WriteLine($"PASS: {paired} paired coarsening changes on saved geometry; no density backslide beyond ordinary native terrain (all four seam LODs).");
        for(int l=1;l<4;l++)for(int trial=0;trial<80;trial++)
        {
            var levels=Enumerable.Range(0,data.topology.patches.Length).Select(_=>random.Next(l,5)).ToArray();
            var a=Count(levels,l);var b=Count(levels,l+1);
            Require(b.final<=a.final,"far seam restored density at a fixed native layout");
        }
        Console.WriteLine("PASS: 240 progressive seam-level changes; farther seam never adds triangles at a fixed native layout.");
    }
}
