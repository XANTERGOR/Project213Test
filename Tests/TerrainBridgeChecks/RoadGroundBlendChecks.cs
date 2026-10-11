using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadGroundBlendChecks()
    {
        foreach(LTRoadMode mode in Enum.GetValues(typeof(LTRoadMode)))
        foreach(LTRoadProjection saved in Enum.GetValues(typeof(LTRoadProjection)))
        foreach(bool blend in new[]{false,true})
        {
            var expected=LTRoadMath.IsTerrainAsphalt(mode)?LTRoadProjection.Spline:
                mode==LTRoadMode.Asphalt&&blend?LTRoadProjection.World:saved;
            Require(LTRoadMath.ResolveGroundProjection(mode,saved,blend)==expected,"effective ground projection / legacy mode routing");
        }
        var points=new[]{new LTRoadPoint(new Vector3(0,1,0)),new LTRoadPoint(new Vector3(4,2,20)),new LTRoadPoint(new Vector3(-2,4,40))};
        var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.Asphalt;settings.projection=LTRoadProjection.Spline;
        var legacy=LTRoadMath.Build(points,Matrix4x4.identity,settings);
        settings.projection=LTRoadMath.ResolveGroundProjection(settings.mode,settings.projection,true);
        var blended=LTRoadMath.Build(points,Matrix4x4.identity,settings);
        Require(legacy.geometryHash==blended.geometryHash&&legacy.detailHash==blended.detailHash,"ground projection must not change road geometry or vegetation removal");
        Require(legacy.paintHash!=blended.paintHash&&legacy.projectionHash!=blended.projectionHash&&!blended.HasSplineProjection,"paint/UV cache invalidation after enabling ground blend");
        // The mesh builder's Matrix4x4.determinant calls native Unity. Exact mesh/UV
        // comparison lives in LTRoadGroundBlendCheck, not this managed-only harness.
        LTRoadMesh.Chunk Rect(float x0,float z0,float x1,float z1,bool split=false)
        {
            var v=new[]{new Vector3(x0,0,z0),new Vector3(x1,0,z0),new Vector3(x1,0,z1),new Vector3(x0,0,z1)};
            return new LTRoadMesh.Chunk{vertices=split?new[]{v[0],v[1],v[2],v[0],v[2],v[3]}:v,
                triangles=split?new[]{0,1,2,3,4,5}:new[]{0,1,2,0,2,3}};
        }
        var pieces=new[]{Rect(-3,-5,3,-1,true),Rect(-3,1,3,5),Rect(-3,-1,-1,1),Rect(1,-1,3,1)};
        var before=pieces.SelectMany(p=>p.vertices).ToArray();
        var d=LTRoadBlendMask.Build(pieces,Matrix4x4.identity,.05f);
        Require(d.holePixels>100,"actual enclosed hole detected");
        Require(d.Sample(0,0).y==0&&d.Sample(0,0).x>2.9f,"hole centre independent from exterior");
        Require(d.Sample(1.1f,0).y<.15f&&d.Sample(1.1f,0).x>1.8f,"hole rim classified");
        Require(d.Sample(2.95f,0).x<.12f&&d.Sample(2.95f,0).y>1.8f,"outer rim classified");
        Require(d.Sample(2,-1).x>.9f&&d.Sample(2,-1).y>.9f,"module/T junction is not a rim");
        Require(before.SequenceEqual(pieces.SelectMany(p=>p.vertices)),"source untouched");
        var again=LTRoadBlendMask.Build(pieces.Reverse().ToArray(),Matrix4x4.identity,.05f);
        Require(d.exterior.SequenceEqual(again.exterior)&&d.holes.SequenceEqual(again.holes),"deterministic union independent of module ordering");
        var filled=LTRoadBlendMask.Build(new[]{Rect(-3,-5,3,5,true)},Matrix4x4.identity,.05f);
        Require(filled.holePixels==0&&filled.Sample(0,0).y==64,"no false holes on split UV/triangle diagonals");
        Require(Math.Abs(filled.Sample(0,0).x-3)<.08,"Euclidean distance in metres");
        var notch=LTRoadBlendMask.Build(new[]{pieces[0],pieces[1],pieces[2]},Matrix4x4.identity,.05f);
        Require(notch.holePixels==0&&notch.Sample(0,0).x==0,"open damage belongs to exterior, not hole");
        var scaled=LTRoadBlendMask.Build(new[]{Rect(-3,-5,3,5)},Matrix4x4.Scale(new Vector3(2,1,2)),.05f);
        Require(Math.Abs(scaled.Sample(0,0).x-6)<.08,"world metric scale respected");
        foreach(float c in new[]{0,float.NaN,float.PositiveInfinity})
        {bool failed=false;try{LTRoadBlendMask.Build(pieces,Matrix4x4.identity,c);}catch(InvalidOperationException){failed=true;}Require(failed,"invalid cell rejected");}
        bool budget=false;try{LTRoadBlendMask.Build(new[]{Rect(0,0,10000,10000)},Matrix4x4.identity,.01f);}catch(InvalidOperationException){budget=true;}
        Require(budget,"allocation budget checked before rasterization");
        // A junction sees only a padded local window of attached roads, even if
        // those roads are kilometres long. Their artificial cut ends are out of reach.
        var window=new Vector4(-5,-7,10,14);
        var junction=pieces.Concat(new[]{Rect(-2,5,2,10000),Rect(-2,-10000,2,-5)}).ToArray();
        var joined=LTRoadBlendMask.Build(junction,Matrix4x4.identity,.05f,window);
        Require(joined.width<210&&joined.height<290,"junction mask bounded independently of branch length");
        Require(joined.holePixels==d.holePixels,"clipped branches did not create or erase junction holes");
        Require(joined.Sample(0,5).x>1.9f&&joined.Sample(0,-5).x>1.9f,"attached entrances have no transverse rim");
        Require(joined.Sample(2,5.5f).x<.08f&&joined.Sample(2,-5.5f).x<.08f,"branch shoulders remain exterior");
        Require(joined.Sample(3,0).x<.08f&&joined.Sample(1,0).y<.08f,"junction outer/hole rims preserved");
        var detached=LTRoadBlendMask.Build(pieces,Matrix4x4.identity,.05f,window);
        Require(detached.Sample(0,5).x<.08f&&detached.Sample(0,-5).x<.08f,"disconnect restores entrance rim");
        var shortBranches=pieces.Concat(new[]{Rect(-2,5,2,20),Rect(-2,-20,2,-5)}).ToArray();
        var shorter=LTRoadBlendMask.Build(shortBranches,Matrix4x4.identity,.05f,window);
        Require(joined.exterior.SequenceEqual(shorter.exterior)&&joined.holes.SequenceEqual(shorter.holes),"local result independent of distant branch ends");
        var tight=LTRoadBlendMask.Build(junction,Matrix4x4.identity,.05f,new Vector4(-3.8f,-5.8f,7.6f,11.6f));
        var full=LTRoadBlendMask.Build(shortBranches,Matrix4x4.identity,.05f);
        float Weight(Vector2 distance)
        {
            float W(float d,float width){float t=Math.Clamp(d/width,0,1);return 1-t*t*(3-2*t);}
            return Math.Max(W(distance.x,.6f),W(distance.y,.3f));
        }
        for(int z=-100;z<=100;z++)for(int x=-60;x<=60;x++)
            Require(Math.Abs(Weight(tight.Sample(x*.05f,z*.05f))-Weight(full.Sample(x*.05f,z*.05f)))<.001f,
                "cropped artificial ends must not alter shader weight anywhere over junction bounds");
        var shifted=LTRoadBlendMask.Build(junction,Matrix4x4.Translate(new Vector3(50,3,-20)),.05f,new Vector4(45,-27,10,14));
        Require(Math.Abs(joined.Sample(0,5).x-shifted.Sample(50,-15).x)<.06f&&shifted.Sample(50,-20).y==0,"transformed junction window");
        bool badWindow=false;try{LTRoadBlendMask.Build(pieces,Matrix4x4.identity,.05f,new Vector4(float.NaN,0,1,1));}catch(InvalidOperationException){badWindow=true;}
        Require(badWindow,"invalid mask window rejected");
        // Actual authored source geometry, including separate bottom/side faces.
        var raw=ReadRoadFbx("Assets/Data/Models/Environment/Roads/Road_1_Line.fbx", "Asphalt_LOD0");
        var actual=LTRoadBlendMask.Build(new[]{raw},Matrix4x4.identity,.05f);
        Require(actual.exterior.Any(v=>v>1&&v<64),"actual Road_1_Line contains a clean interior");
        Console.WriteLine($"PASS road/junction ground blend: ground projection / legacy routing, paint invalidation, unchanged geometry/removal signatures; union, hole/outer distances, no module/UV seams, deterministic LOD0 mask, metrics, budgets; bounded 10km branches, connected/disconnected entrances, holes, transforms; actual Road_1_Line {actual.width}x{actual.height}, hole pixels {actual.holePixels}.");
    }
}
