using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    sealed class CaptureLODInput
    {
        internal int id;internal Matrix4x4 matrix=Matrix4x4.identity;
        internal List<(int id,int revision,string asset,int vertices,int submeshes)> meshes=new();
    }
    static void EditorCaptureChecks()
    {
        int checks=0;void Check(bool ok,string why){checks++;Require(ok,"editor capture: "+why);}
        var probe=new LTRoadCaptureSignature();var inputs=new List<CaptureLODInput>();float edge=.03f;
        string Current()
        {
            probe.Begin(edge);
            foreach(var lod in inputs)
            {probe.AddLOD(lod.id,lod.matrix,lod.meshes.Count);foreach(var m in lod.meshes)probe.AddMesh(m.id,m.revision,m.asset,m.vertices,m.submeshes);}
            return probe.Finish();
        }
        // Frozen legacy text assembly: independent of the production probe.
        string Legacy()
        {
            var text=new StringBuilder("road-foundation-v2:");
            text.Append(edge.ToString("R",CultureInfo.InvariantCulture)).Append(':');
            foreach(var lod in inputs)
            {
                text.Append(lod.id).Append(':').Append(lod.matrix.ToString("R")).Append(':');
                foreach(var m in lod.meshes)
                    text.Append(m.id).Append(',').Append(m.revision).Append(',').Append(m.asset).Append(',').Append(m.vertices).Append(',').Append(m.submeshes).Append(';');
            }
            return text.ToString();
        }
        void Parity()
        {string key=Current();Check(key==Legacy(),"legacy signature mismatch");Check(ReferenceEquals(key,Current()),"warm signature allocated/replaced");}
        Parity();
        for(int i=0;i<48;i++)
        {
            var lod=new CaptureLODInput{id=i+1};lod.matrix.m03=i*2.125f;
            for(int level=0;level<3;level++)lod.meshes.Add((100+i*3+level,0,"asset",32>>level,1));inputs.Add(lod);
        }
        Parity();var random=new System.Random(874092);
        for(int i=0;i<1000;i++)
        {
            int at=random.Next(inputs.Count);var lod=inputs[at];var mesh=lod.meshes[0];
            switch(i%10)
            {
                case 0: lod.id++;break;
                case 1: lod.matrix[random.Next(16)]=(float)random.NextDouble();break;
                case 2: mesh.id++;break;
                case 3: mesh.revision++;break;
                case 4: mesh.asset="asset-"+i;break;
                case 5: mesh.vertices++;break;
                case 6: mesh.submeshes++;break;
                case 7: edge=(float)random.NextDouble();break;
                case 8: inputs.Reverse();break;
                case 9: if(lod.meshes.Count>1)lod.meshes.RemoveAt(lod.meshes.Count-1);else lod.meshes.Add((i,0,"",6,1));break;
            }
            lod.meshes[0]=mesh;Parity();
        }
        var saved=inputs[0];inputs.RemoveAt(0);Parity();inputs.Insert(0,saved);Parity();
        foreach(float value in new[]{0f,BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),float.Epsilon,1e-10f,-1e10f,float.NaN,float.PositiveInfinity})
        {saved.matrix.m13=value;Parity();}
        var culture=CultureInfo.CurrentCulture;
        try
        {
            foreach(string name in new[]{"en-US","ru-RU","fr-FR","ar-SA"})
            {CultureInfo.CurrentCulture=new CultureInfo(name,false);Parity();}
            var custom=(CultureInfo)CultureInfo.InvariantCulture.Clone();CultureInfo.CurrentCulture=custom;
            saved.id=-3;saved.matrix.m13=-1.125f;Parity();custom.NumberFormat.NegativeSign="minus";Parity();
            custom.NumberFormat.NumberDecimalSeparator="comma";Parity();custom.NumberFormat.PositiveSign="plus";Parity();
            saved.matrix.m13=float.NaN;custom.NumberFormat.NaNSymbol="nan";Parity();
            saved.matrix.m13=float.PositiveInfinity;custom.NumberFormat.PositiveInfinitySymbol="infinity";Parity();
        }
        finally{CultureInfo.CurrentCulture=culture;}
        saved.matrix=Matrix4x4.identity;Parity();string stable=Current();
        probe.Begin(.123f);probe.AddLOD(123,Matrix4x4.identity,1);bool rejected=false;
        try{probe.Finish();}catch(ArgumentOutOfRangeException){rejected=true;}
        Check(rejected&&ReferenceEquals(stable,Current()),"failed collection overwrote accepted signature");
        probe.Begin(.9f);probe.AddLOD(999,Matrix4x4.identity,3); // abandoned by unreadable/null LOD
        Check(ReferenceEquals(stable,Current()),"abandoned capture contaminated next poll");
        for(int i=0;i<10;i++)Current();long before=GC.GetAllocatedBytesForCurrentThread();bool same=true;
        for(int i=0;i<1000;i++)same&=ReferenceEquals(stable,Current());
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(same&&allocated==0,"warm signature polling allocated");
        const string root="Assets/TerrainSystem/LocalTerrain/";
        string capture=File.ReadAllText(root+"Editor/LTRoadTerrainLODCapture.cs");
        Check(capture.Contains("GetComponentsInChildren(false,lods)")&&capture.Contains("lod.owner!=road||!lod.gameObject.activeInHierarchy"),"live membership polling lost");
        Check(capture.Contains("finally {lods.Clear();inputs.Clear();}")&&capture.Contains("!mesh.isReadable")&&capture.Contains("GetDirtyCount(mesh)")&&capture.Contains("AssetInputHash(mesh)"),"input validation/reference cleanup lost");
        string junction=File.ReadAllText(root+"LTRoadJunction.cs");int start=junction.IndexOf("public bool SourceMember(");
        string member=junction.Substring(start,junction.IndexOf("[Serializable]",start)-start);
        Check(!member.Contains(".GetComponent<")&&member.Contains("TryGetComponent<LTRoad>"),"optional source boundary lookup regressed");
        Console.WriteLine($"PASS editor capture: {checks} assertions; 1000 mutated legacy-key comparisons, culture/signed-zero/tiny edits, aborted collection, 1000 warm polls / {allocated} bytes in pure signature bookkeeping. Native metadata lookups and live timing are separate.");
    }
}
