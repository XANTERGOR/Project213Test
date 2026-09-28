using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadTilingVegetationChecks()
    {
        var points=new[]{new LTRoadPoint(new Vector3(0,2,0)),new LTRoadPoint(new Vector3(0,2,30)),new LTRoadPoint(new Vector3(0,2,60))};
        LTRoadMath.Snapshot Build(LTRoadMath.Settings settings)=>LTRoadMath.Build(points,Matrix4x4.identity,settings);
        Vector2 UV(LTRoadMath.Snapshot road,float x,float z)
        {Require(road.TryTextureCoordinates(x,z,out var uv,out _),"finite road UV sample");return uv;}
        bool Near(Vector2 a,Vector2 b)=>(a-b).sqrMagnitude<1e-9f;
        var s=LTRoadMath.Settings.Default;s.projection=LTRoadProjection.Spline;s.wheelTracks.enabled=true;s.wheelLayerId=17;
        var legacy=Build(s);
        Require(Near(UV(legacy,1,12),new Vector2(1f/6+.5f,3)),"zero across size retains legacy road mapping");
        Require(UV(legacy,1,12)==UV(legacy.WheelLayerProjection(),1,12),"wheel mapping inherits defaults exactly");
        s.textureAcrossMetres=2;s.textureRepeatMetres=3;s.textureOffset=new Vector2(.2f,-.4f);
        var ground=Build(s);
        Require(Near(UV(ground,1,12),new Vector2(1.2f,3.6f)),"independent across/along ground repeats and UV offsets");
        Require(ground.geometryHash==legacy.geometryHash&&ground.detailHash==legacy.detailHash&&ground.paintHash!=legacy.paintHash&&ground.projectionHash!=legacy.projectionHash,"ground UV changes only paint/projection signatures");
        Require(UV(ground,1,12)==UV(ground.WheelLayerProjection(),1,12),"wheel inheritance follows changed ground mapping");
        s.wheelTracks.independentTiling=true;s.wheelTracks.tileSizeMetres=new Vector2(.5f,1.5f);s.wheelTracks.textureOffset=new Vector2(-.1f,.25f);
        var own=Build(s);var wheel=own.WheelLayerProjection();
        Require(UV(ground,1,12)==UV(own,1,12)&&ground.projectionHash==own.projectionHash,"wheel UV edit must not change ground projection");
        Require(Near(UV(wheel,1,12),new Vector2(2.4f,8.25f)),"wheel repeat/offset independent in both axes");
        Require(!LTRoadProjectionMath.SameInputs(ground.WheelLayerProjection(),wheel,false),"wheel-only tiling must invalidate even a single-road atlas");
        Require(own.geometryHash==ground.geometryHash&&own.detailHash==ground.detailHash&&own.paintHash!=ground.paintHash,"wheel UV edit must not deform or change detail clearing");
        var edit=s;edit.textureAcrossMetres=8;edit.textureRepeatMetres=7;edit.textureOffset=new Vector2(20,70);var groundEdit=Build(edit);
        Require(UV(wheel,1,12)==UV(groundEdit.WheelLayerProjection(),1,12)&&wheel.projectionHash==groundEdit.WheelLayerProjection().projectionHash&&wheel.paintHash==groundEdit.WheelLayerProjection().paintHash,"own wheel mapping and shared ownership ignore ground UV edits");
        edit=s;edit.wheelTracks.independentTiling=false;edit.wheelTracks.tileSizeMetres=new Vector2(99,121);edit.wheelTracks.textureOffset=new Vector2(20,80);var inherited=Build(edit);
        Require(inherited.projectionHash==ground.projectionHash&&inherited.paintHash==ground.paintHash&&UV(inherited.WheelLayerProjection(),1,12)==UV(ground,1,12),"disabled own tiling ignores stale values");
        // Independent wheel maps remain continuous across chunk seams and finite caps.
        var left=new Rect(-4,-2,4,64);var right=new Rect(0,-2,4,64);const int size=65;
        var lm=LTRoadProjectionMath.Bake(new[]{wheel},left,size);var rm=LTRoadProjectionMath.Bake(new[]{wheel},right,size);
        for(int row=0;row<size;row++)
        {
            float z=-2+row;var l=lm.coordinates[row*size+size-1];var r=rm.coordinates[row*size];
            Require(l==r&&Near(new Vector2(l.r,l.g),UV(wheel,0,z)),"wheel atlas seam and endpoint extension agree with exact mapping");
        }
        var shiftedPoints=new[]{new LTRoadPoint(new Vector3(.9f,2,0)),new LTRoadPoint(new Vector3(.9f,2,60))};
        var networkSettings=s;networkSettings.wheelTracks.tileSizeMetres=new Vector2(3,7);networkSettings.wheelTracks.textureOffset=new Vector2(10,90);
        var neighbour=LTRoadMath.Build(shiftedPoints,Matrix4x4.identity,networkSettings).WheelLayerProjection();
        var networkRect=new Rect(-4,0,8,60);const int networkSize=81;
        var network=LTRoadProjectionMath.Bake(new[]{wheel,neighbour},networkRect,networkSize);
        var first=network.coordinates[40*networkSize+49];var second=network.coordinates[40*networkSize+40];
        Require(LTRoadProjectionMath.Owner(first)==1&&Near(new Vector2(first.r,first.g),UV(wheel,.9f,30)),"shared wheel atlas retains first road independent tiling");
        Require(LTRoadProjectionMath.Owner(second)==2&&Near(new Vector2(second.r,second.g),UV(neighbour,0,30)),"shared wheel atlas retains second road independent tiling");
        // Clearing is an independent, deterministic strip exclusion: full removal
        // even where paint opacity, edge softness or rut variation is weak.
        s.vegetationOnlyWheelTracks=true;s.wheelTracks.clearStones=false;s.vegetationFade=.1f;
        var only=Build(s);var beforeSettings=s;beforeSettings.vegetationOnlyWheelTracks=false;var before=Build(beforeSettings);
        Require(only.geometryHash==before.geometryHash&&only.paintHash==before.paintHash&&only.detailHash!=before.detailHash,"vegetation scope changes only placement signature");
        Require(only.ClearWeight(0,30,LTDetailCategory.Vegetation)==0&&only.ClearWeight(2,30,LTDetailCategory.Vegetation)==0,"centre and shoulders retain vegetation candidates");
        float centre=s.wheelTracks.separation*.5f,half=s.wheelTracks.width*.5f;
        Require(only.ClearWeight(centre+half*.95f,30,LTDetailCategory.Vegetation)==1,"soft visual edges cannot retain vegetation roots inside the strip");
        Require(only.ClearWeight(centre,-half*2,LTDetailCategory.Vegetation)==0&&only.ClearWeight(centre,-half*.5f,LTDetailCategory.Vegetation)==1,"finite rounded vegetation-exclusion caps");
        Require(only.ClearWeight(centre,30,LTDetailCategory.Stones)==0,"vegetation mode must not remove stones");
        edit=s;edit.wheelTracks.width=.8f;var wider=Build(edit);
        Require(wider.detailHash!=only.detailHash&&wider.geometryHash==only.geometryHash,"width invalidates vegetation even with stone clearing disabled");
        edit=s;edit.wheelTracks.separation=2.2f;Require(Build(edit).detailHash!=only.detailHash,"spacing invalidates vegetation exclusion");
        edit=s;edit.clearVegetation=false;var noClear=Build(edit);
        Require(noClear.ClearWeight(centre,30,LTDetailCategory.Vegetation)==0,"none mode preserves candidate");
        edit=s;edit.wheelTracks.enabled=false;var noWheels=Build(edit);
        Require(noWheels.ClearWeight(0,30,LTDetailCategory.Vegetation)==0&&noWheels.ClearWeight(centre,30,LTDetailCategory.Vegetation)==0,"disabled wheels must not silently fall back to whole-road removal");
        edit=s;edit.wheelLayerId=0;edit.wheelTracks.strength=0;var noPaint=Build(edit);
        Require(noPaint.detailHash==only.detailHash&&noPaint.ClearWeight(centre,30,LTDetailCategory.Vegetation)==1,"vegetation exclusion independent of assigned layer and paint strength");
        edit=s;edit.variation.enabled=true;edit.variation.rutVariation=1;edit.variation.patchStrength=1;edit.edgeNoise=1;var varied=Build(edit);
        int probes=0;
        for(float z=2;z<58;z+=.19f)for(float x=-4;x<4;x+=.11f)
        {
            float clear=only.ClearWeight(x,z,LTDetailCategory.Vegetation);
            Require(clear==varied.ClearWeight(x,z,LTDetailCategory.Vegetation)&&clear==noPaint.ClearWeight(x,z,LTDetailCategory.Vegetation),"paint fading/patches/edge noise must not regrow roots in wheel strips");
            if(Math.Abs(Math.Abs(x)-centre)<half)
            {
                Require(clear==1,"all wheel-strip roots must be removed");
                Require(!LTDetailMath.Accept((uint)(probes+1),1-clear),"production acceptance rejects fully cleared candidates");
            }
            else Require(clear==0,"outside strips the mode preserves original spawn probability");
            Require(only.ApplyHeight(x,z,0)==before.ApplyHeight(x,z,0)&&only.PaintWeight(x,z)==before.PaintWeight(x,z),"vegetation scope cannot change heights or painting");
            probes++;
        }
        var bentPoints=new[]{new LTRoadPoint(new Vector3(0,2,0)),new LTRoadPoint(new Vector3(13,5,30),15),new LTRoadPoint(new Vector3(0,3,60))};
        edit=s;edit.variation.enabled=true;edit.variation.widthAmount=.35f;edit.edgeNoise=.8f;
        var bent=LTRoadMath.Build(bentPoints,Matrix4x4.identity,edit);
        for(float z=-2;z<62;z+=.37f)for(float x=-4;x<18;x+=.29f)
            if(bent.WheelPaintWeight(x,z)>0)Require(bent.ClearWeight(x,z,LTDetailCategory.Vegetation)==1,"visible wheel paint on a curved/banked road excludes vegetation roots");
        // Offroad-only controls never alter asphalt or World texture projection.
        edit=s;edit.projection=LTRoadProjection.World;var world=Build(edit);edit.textureAcrossMetres=4;edit.wheelTracks.tileSizeMetres=new Vector2(41,17);var worldEdit=Build(edit);
        Require(world.geometryHash==worldEdit.geometryHash&&world.paintHash==worldEdit.paintHash&&world.projectionHash==worldEdit.projectionHash,"World ignores new spline-only tiling");
        edit=s;edit.mode=LTRoadMode.Asphalt;var asphalt=Build(edit);edit.vegetationOnlyWheelTracks=false;edit.textureAcrossMetres=9;edit.wheelTracks.tileSizeMetres=new Vector2(41,17);var asphaltEdit=Build(edit);
        Require(asphalt.geometryHash==asphaltEdit.geometryHash&&asphalt.paintHash==asphaltEdit.paintHash&&asphalt.detailHash==asphaltEdit.detailHash,"asphalt ignores new offroad controls");
        Require(asphalt.ClearWeight(0,30,LTDetailCategory.Vegetation)==asphaltEdit.ClearWeight(0,30,LTDetailCategory.Vegetation),"asphalt whole-road removal retained");
        foreach(int index in Enumerable.Range(0,5))
        {
            edit=s;if(index==0)edit.textureAcrossMetres=float.NaN;if(index==1)edit.textureAcrossMetres=-1;
            if(index==2)edit.wheelTracks.tileSizeMetres.x=0;if(index==3)edit.wheelTracks.tileSizeMetres.y=float.PositiveInfinity;
            if(index==4)edit.wheelTracks.textureOffset.x=float.NaN;
            bool rejected=false;try{Build(edit);}catch(ArgumentException){rejected=true;}Require(rejected,"invalid new tiling settings rejected");
        }
        float Hot(){float sum=0;for(int i=0;i<3000;i++){float x=i%37*.15f-2,z=i%239*.23f;sum+=only.ClearWeight(x,z,LTDetailCategory.Vegetation);wheel.TryTextureCoordinates(x,z,out var uv,out _);sum+=uv.x+uv.y;}return sum;}
        Hot();long memory=GC.GetAllocatedBytesForCurrentThread();float checksum=Hot();long allocated=GC.GetAllocatedBytesForCurrentThread()-memory;
        Require(allocated==0&&float.IsFinite(checksum),"new hot UV/vegetation queries allocate nothing");
        const string root="Assets/TerrainSystem/LocalTerrain/";
        var component=File.ReadAllText(root+"LTRoad.cs");var editor=File.ReadAllText(root+"Editor/LTRoadEditor.cs");var global=File.ReadAllText(root+"LTGlobalBakeAsset.cs");
        Require(component.Contains("textureAcrossMetres=textureAcrossMetres")&&component.Contains("vegetationOnlyWheelTracks=vegetationOnlyWheelTracks"),"component captures new inputs");
        Require(editor.Contains("Убирать только в следах")&&editor.Contains("wheelTracks.independentTiling")&&editor.Contains("wheelTracks.tileSizeMetres")&&editor.Contains("textureAcrossMetres"),"inspector exposes independent tiling and vegetation scope");
        Require(editor.Contains("!wasOwn&&own.boolValue&&!serializedObject.FindProperty(\"wheelTilingInitialized\").boolValue"),"re-enabling own tiling must preserve previously authored wheel values");
        Require(global.Contains("Number(road.textureAcrossMetres)")&&global.Contains("ToJson(road.wheelTracks)"),"saved global signatures invalidate both tiling changes");
        Console.WriteLine($"PASS road tiling/vegetation: {probes} root-exclusion probes; independent/inherited UVs, single-owner cache invalidation and atlas seam; full-strip removal with unchanged centre/shoulders; legacy/asphalt parity; {allocated} B hot-query allocation. Managed/source checks, not Unity rendering.");
    }
}
