using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadMixedProjectionChecks()
    {
        var points=new[]{new LTRoadPoint(new Vector3(0,2,0)),new LTRoadPoint(new Vector3(0,2,60))};
        var settings=LTRoadMath.Settings.Default;settings.groundLayerId=1;settings.wheelLayerId=2;settings.wheelTracks.enabled=true;
        settings.vegetationOnlyWheelTracks=true;
        LTRoadMath.Snapshot Build(LTRoadMath.Settings s)=>LTRoadMath.Build(points,Matrix4x4.identity,s);
        var legacy=Build(settings);
        Require(settings.wheelTracks.projection==LTRoadWheelProjection.Inherit,"old serialized roads inherit projection by default");
        settings.wheelTracks.projection=LTRoadWheelProjection.Spline;
        settings.wheelTracks.tileSizeMetres=new Vector2(.5f,3);settings.wheelTracks.textureOffset=new Vector2(.2f,-.4f);
        var mixed=Build(settings);var wheels=mixed.WheelLayerProjection();
        Require(mixed.projection==LTRoadProjection.World&&wheels.projection==LTRoadProjection.Spline&&mixed.HasSplineProjection,"ground World + wheels Spline");
        Require(wheels.textureAcrossMetres==.5f&&wheels.textureRepeatMetres==3&&wheels.textureOffset==settings.wheelTracks.textureOffset,"World ground must not supply hidden spline settings to wheels");
        wheels.TryTextureCoordinates(.9f,30,out var uv,out var right);
        Require((uv-new Vector2(2.5f,9.6f)).sqrMagnitude<1e-9f&&right==Vector2.right,"independent wheel UV and frame");
        Require(ReferenceEquals(wheels,mixed.WheelLayerProjection())&&ReferenceEquals(wheels.samples,mixed.samples),"wheel view reuses the spline/BVH");
        Require(legacy.geometryHash==mixed.geometryHash&&legacy.detailHash==mixed.detailHash&&legacy.projectionHash==mixed.projectionHash&&legacy.paintHash!=mixed.paintHash,"only wheel rendering invalidates");
        Require(legacy.SamePaintRegion(mixed,new Rect(-6,-3,12,66)),"projection alone must reuse ground/wheel paint masks");
        Require(!LTRoadProjectionMath.SameInputs(legacy.WheelLayerProjection(),wheels,false),"single-road wheel atlas invalidates when projection changes");
        int samples=0;
        for(float x=-5;x<5;x+=.13f)for(float z=-3;z<63;z+=.37f)
        {
            legacy.PaintWeights(x,z,out var lg,out var lw);mixed.PaintWeights(x,z,out var mg,out var mw);
            Require(lg==mg&&lw==mw&&legacy.ApplyHeight(x,z,0)==mixed.ApplyHeight(x,z,0),"projection changed coverage or geometry");
            foreach(var category in new[]{LTDetailCategory.Vegetation,LTDetailCategory.Stones})
                Require(legacy.ClearWeight(x,z,category)==mixed.ClearWeight(x,z,category),"projection changed detail removal");
            samples++;
        }
        var edited=settings;edited.textureOffset=new Vector2(100,200);edited.textureRepeatMetres=99;edited.textureAcrossMetres=20;
        var groundEdit=Build(edited).WheelLayerProjection();
        Require(groundEdit.projectionHash==wheels.projectionHash&&groundEdit.paintHash==wheels.paintHash,"World ground's unused spline values must not alter wheel UVs");
        edited=settings;edited.wheelTracks.projection=LTRoadWheelProjection.World;var world=Build(edited);
        Require(!world.HasSplineProjection&&world.WheelLayerProjection().projection==LTRoadProjection.World&&world.paintHash==legacy.paintHash,"explicit World restores legacy World rendering");
        edited=settings;edited.projection=LTRoadProjection.Spline;edited.wheelTracks.projection=LTRoadWheelProjection.World;
        var inverse=Build(edited);Require(inverse.projection==LTRoadProjection.Spline&&inverse.WheelLayerProjection().projection==LTRoadProjection.World,"inverse independent selection");
        edited=settings;edited.wheelTracks.enabled=false;Require(!Build(edited).HasSplineProjection,"disabled wheels need no spline raster guard");
        edited=settings;edited.wheelLayerId=0;Require(!Build(edited).HasSplineProjection,"unassigned wheel material needs no spline raster guard");
        foreach(int invalid in new[]{0,1,2})
        {
            edited=settings;if(invalid==0)edited.wheelTracks.tileSizeMetres.x=0;
            if(invalid==1)edited.wheelTracks.textureOffset.y=float.NaN;
            if(invalid==2)edited.wheelTracks.projection=(LTRoadWheelProjection)99;
            bool failed=false;try{Build(edited);}catch(ArgumentException){failed=true;}Require(failed,"invalid independent projection input accepted");
        }
        // Geometry orientation, UV continuity and shared ownership remain unchanged by World ground.
        var curved=new[]{new LTRoadPoint(new Vector3(0,2,0)),new LTRoadPoint(new Vector3(10,3,25)),new LTRoadPoint(new Vector3(0,2,50))};
        var splitCurve=LTRoadMath.Build(curved,Matrix4x4.identity,settings).WheelLayerProjection();
        edited=settings;edited.projection=LTRoadProjection.Spline;edited.wheelTracks.independentTiling=true;
        var referenceCurve=LTRoadMath.Build(curved,Matrix4x4.identity,edited).WheelLayerProjection();
        var region=new Rect(-6,-4,24,60);var actual=LTRoadProjectionMath.Bake(new[]{splitCurve},region,65);
        var expected=LTRoadProjectionMath.Bake(new[]{referenceCurve},region,65);
        Require(actual.coordinates.SequenceEqual(expected.coordinates),"curved wheel UVs/normals must equal the original Spline path");
        var left=LTRoadProjectionMath.Bake(new[]{splitCurve},new Rect(-6,-4,12,60),65);
        var rightMap=LTRoadProjectionMath.Bake(new[]{splitCurve},new Rect(6,-4,12,60),65);
        for(int z=0;z<65;z++)Require(left.coordinates[z*65+64]==rightMap.coordinates[z*65],"mixed-projection chunk seam");
        const string root="Assets/TerrainSystem/LocalTerrain/";
        Require(File.ReadAllText(root+"LTPaintRoadProjection.cs").Contains("road.CapturePaintProjection(world,wheels)"),"per-slot routing not wired");
        Require(File.ReadAllText(root+"LTGlobalBakeAsset.cs").Contains("ToJson(road.wheelTracks)"),"saved global bake must include projection selection");
        Require(!File.ReadAllText(root+"LTRoad.cs").Contains("surfaceBake")&&!File.ReadAllText(root+"Editor/LTRoadEditor.cs").Contains("DrawBakedSurface"),"retired experimental bake still active");
        Console.WriteLine($"PASS independent road projection: {samples} unchanged height/weight/removal probes; World/Spline combinations, legacy defaults, UV/normal frames, chunk seams and invalidation.");
    }
}
