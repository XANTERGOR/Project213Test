using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void InsetRoadChecks()
    {
        var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.GeometricAsphalt;
        settings.width=5;settings.terrainCellSize=1;settings.insetCellSize=.75f;settings.asphaltDamage.strength=0;
        var path=new[]{new LTRoadPoint(new Vector3(7.3f,2,-2)),new LTRoadPoint(new Vector3(9.7f,2,34))};
        var rect=new Rect(0,0,16,32);
        LTRoadMath.Snapshot Road()=>LTRoadMath.Build(path,Matrix4x4.identity,settings);
        var road=Road();var field=new LTRoadInsetMesh.Field(new[]{road},rect);
        (Vector3[] v,int[] t) Emit(int cells,LTRoadInsetMesh.Field f,Func<float,float,bool> cut=null)
        {
            var plan=LTStampMesh.Plan(rect,cells,false,.01f,300000,new List<LTStampMesh.Zone>(),(x,z)=>2);
            LTStampMesh.Emit(rect,new Vector2(16,32),300000,null,0,plan,(x,z)=>2,cut,out var v,out _,out _,out var t,midpoint:(p,e)=>false,insetRoads:f);
            return(v,t);
        }
        int previous=int.MaxValue,near=0,far=0;
        foreach(int cells in new[]{32,16,8,4,2})
        {
            var output=Emit(cells,field);Verify(output.v,output.t,rect,LTRoadGeometryMath.Field.Combine(Array.Empty<LTRoadGeometryMath.Pit>()),(x,z)=>2);
            VerifyAsphaltBoundaryCache(new[]{output.v},new[]{output.t},new[]{Vector3.zero},1);
            int count=output.t.Length/3;Console.WriteLine($"Inset {cells}: {count} tris");
            Require(count<=previous,"inset triangle count must not increase at farther LOD");previous=count;
            if(cells==32)near=count;if(cells==2)far=count;
        }
        Require(far<near/3,"inset road simplifies together with the terrain");
        var fine=Emit(8,field);settings.insetCellSize=2;
        var coarse=Emit(8,new LTRoadInsetMesh.Field(new[]{Road()},rect));
        Require(coarse.t.Length<fine.t.Length,"independent road density control");
        var empty=Emit(8,field,(x,z)=>true);Require(empty.t.Length==0,"inserted ribbon does not fill authored holes");
        foreach(int cells in new[]{32,16})
        {
            var hole=Emit(cells,field,(x,z)=>(x-8.5f)*(x-8.5f)+(z-16)*(z-16)<9);
            VerifyAsphaltBoundaryCache(new[]{hole.v},new[]{hole.t},new[]{Vector3.zero},2);
        }
        bool rejected=false;try{new LTRoadInsetMesh.Field(new[]{road,road},rect);}catch(InvalidOperationException){rejected=true;}
        Require(rejected,"overlapping roads rejected before replacing geometry");
        Require(Road().geometryHash!=road.geometryHash,"density change invalidates geometry");
        var legacy=settings;legacy.mode=LTRoadMode.Offroad;var aLegacy=LTRoadMath.Build(path,Matrix4x4.identity,legacy);
        legacy.insetCellSize=.1f;legacy.insetLodReduction=0;
        Require(aLegacy.geometryHash==LTRoadMath.Build(path,Matrix4x4.identity,legacy).geometryHash,"inset controls do not change legacy road geometry");
        settings.insetCellSize=1;settings.asphaltDamage=LTRoadAsphaltDamage.Default;
        settings.asphaltDamage.size=1;settings.asphaltDamage.spacing=5;settings.asphaltDamage.potholes=1;settings.asphaltDamage.brokenEdges=0;settings.asphaltDamage.depth=.3f;
        var damaged=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(8,2,2)),new LTRoadPoint(new Vector3(8,2,30))},Matrix4x4.identity,settings);
        var damagedField=new LTRoadInsetMesh.Field(new[]{damaged},rect);
        foreach(int cells in new[]{32,8,2})
        {
            var output=Emit(cells,damagedField);Verify(output.v,output.t,rect,damaged.geometricPits,(x,z)=>2);
            VerifyAsphaltBoundaryCache(new[]{output.v},new[]{output.t},new[]{Vector3.zero},1);
        }
        rejected=false;
        try{LTStampMesh.Emit(rect,new Vector2(16,32),4,null,0,new List<Vector3Int>{new Vector3Int(0,0,LTSpatialLODMath.N)},(x,z)=>2,null,out _,out _,out _,out _,midpoint:(p,e)=>false,insetRoads:field);}
        catch(InvalidOperationException){rejected=true;}
        Require(rejected,"inset respects chunk vertex budget");
        Console.WriteLine($"PASS inset road basic: {near} -> {far} triangles; density, area, boundaries, holes, overlap rejection and cache.");
        Console.WriteLine(LTRoadInsetCheck.Run(VerifyAsphaltBoundaryCache));
        Console.WriteLine("Strong reduction: "+LTRoadInsetCheck.Run(VerifyAsphaltBoundaryCache,2));
        Console.WriteLine("Damaged ribbon: "+LTRoadInsetCheck.Run(VerifyAsphaltBoundaryCache,damaged:true));
    }
}
