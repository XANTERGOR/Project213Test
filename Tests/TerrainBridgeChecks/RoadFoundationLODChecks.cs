using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadFoundationLODChecks()
    {
        Console.WriteLine(LTRoadTerrainLODCheck.RunData());
        var settings=Enumerable.Range(0,5).Select(l=>new LTLODJobMath.Level{steps=l,tolerance=.3f}).ToArray();
        foreach(float shift in new[]{0f,902.3f,-1081.1f})
        {
            var rect=new Rect(shift,shift,16,16);var fine=LODFixture(64);
            var part=LTRoadTerrainLODCheck.Grid(.04f);
            for(int i=0;i<part.vertices.Length;i++)part.vertices[i]+=new Vector3(shift,0,shift);
            var surface=new LTRoadTerrainSurface(new[]{part});var protection=new LTRoadTerrainLODProtection();
            float H(float x,float z)=>.01f*Mathf.Sin((x-shift)*3)*Mathf.Sin((z-shift)*2);
            foreach(var _ in protection.Prepare(fine,rect,new[]{surface},H)){}
            var input=new LTLODJobInput();foreach(var _ in input.Prepare(fine,rect,settings,H,null,protection.Protects,4,false)){}
            var actual=SolveJobInput(input);var prep=new LTLODMesh.Preparation(fine,rect,H);
            for(int l=0;l<5;l++)Require(actual[l].SequenceEqual(prep.Coarsen(l,.3f,null,protection.Protects,4,false)),"Road foundation Jobs scalar parity failed at offset "+shift);
        }
        Console.WriteLine("PASS road foundation Jobs-input/scalar parity: 15 levels, uneven heights and positive/negative world offsets. Not native Burst execution.");
        var source=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        Require(source.Contains("asphalt-mesh-clearance-lod-v2")&&source.Contains("surfaceChanged?null:a.road")&&source.Contains("surfaceChanged?null:b.road"),"Road mesh changes must invalidate terrain even when spline height is unchanged.");
        Require(source.Split("CutTerrainAt(w,local,x,z),height)").Length==4,"All three terrain LOD paths must use the actual emitted height sampler.");
        var capture=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTRoadTerrainLODCapture.cs");
        Require(capture.Contains("road.bakedGeometryHash!=snapshot.geometryHash")&&capture.Contains("lod.meshes.Length"),"Stale geometry guard and all-LOD capture required.");
        var cleanup=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTRoadSegmentCleanup.cs");
        Require(cleanup.Contains("!marker.obsolete")&&cleanup.Contains("segment.gameObject.activeSelf")&&cleanup.Contains("segment.childCount!=0")&&cleanup.Contains("marker.bakeId!=road.bakeId"),"Cleanup must require obsolete ownership, inactiveSelf and no user children.");
        Require(cleanup.Contains("Undo.DestroyObjectImmediate(segment.gameObject)")&&!cleanup.Contains("DeleteAsset")&&!cleanup.Contains("SaveAssets")&&!cleanup.Contains("SaveScene"),"Cleanup must use Undo and never delete assets/save scenes.");
        Console.WriteLine("PASS cleanup static safety contracts; native owned-object validation and user Undo round-trip remain separate.");
        Console.WriteLine("PASS road foundation production wiring; native capture and rendered appearance require Unity validation.");
    }
}
