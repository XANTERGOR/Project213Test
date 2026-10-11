using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void JunctionFoundationLODChecks()
    {
        Console.WriteLine(LTRoadJunctionTerrainLODData.Run());
        int levels=0;
        foreach(bool low in new[]{false,true})foreach(float shift in new[]{0f,902.3f,-1081.1f})
        {
            var rect=new Rect(shift,shift,16,16);var fine=LODFixture(64);
            var parts=new[]{LTRoadJunctionTerrainLODData.Shape(.06f),LTRoadJunctionTerrainLODData.Shape(low?-.02f:.03f,1)};
            foreach(var part in parts)for(int i=0;i<part.vertices.Length;i++)part.vertices[i]+=new Vector3(shift,0,shift);
            var surface=new LTRoadTerrainSurface(parts);var protection=new LTRoadTerrainLODProtection();
            float H(float x,float z)=>.005f*Mathf.Sin((x-shift)*3)*Mathf.Sin((z-shift)*2);
            foreach(var _ in protection.Prepare(fine,rect,new[]{surface},H)){}
            var settings=Enumerable.Range(0,5).Select(l=>new LTLODJobMath.Level{steps=l,tolerance=.3f}).ToArray();
            var input=new LTLODJobInput();foreach(var _ in input.Prepare(fine,rect,settings,H,null,protection.Protects,4,false)){}
            var actual=SolveJobInput(input);var prep=new LTLODMesh.Preparation(fine,rect,H);
            for(int l=0;l<5;l++){Require(actual[l].SequenceEqual(prep.Coarsen(l,.3f,null,protection.Protects,4,false)),"Junction Jobs/scalar parity");levels++;}
        }
        string editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        Require(editor.Contains("LTRoadJunctionTerrainLODCapture.Capture(node,w,out terrainKey)"),"Junction surface not captured by editor.");
        Require(editor.Split("s.fixedRoadSurface&&s.junction!=null&&s.roadTerrainSurface==null").Length==4,"All three LOD paths must retain fallback-only junction lock.");
        Require(!editor.Contains("s.fixedRoadSurface&&s.junction!=null).Select"),"Blanket junction lock remains.");
        Require(editor.Contains("junction-mesh-clearance-lod-v1")&&editor.Contains("node.surface+\":\"+terrainKey"),"Junction meshes/settings must invalidate baked terrain.");
        string capture=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTRoadJunctionTerrainLODCapture.cs");
        Require(capture.Contains("marker.owner!=node")&&capture.Contains("lod.junction!=node||lod.owner")&&capture.Contains("level<lod.meshes.Length"),"Ownership and all-LOD capture required.");
        Require(!capture.Contains("sharedMesh=")&&!capture.Contains("SetDirty(")&&!capture.Contains("SaveAssets("),"Capture must not edit mesh/scene/assets.");
        Console.WriteLine($"PASS junction foundation wiring + {levels} Jobs-input/scalar levels. Native Mesh capture/import and scene appearance require Unity validation.");
    }
}
