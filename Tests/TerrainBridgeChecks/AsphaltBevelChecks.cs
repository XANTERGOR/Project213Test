using System;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void AsphaltBevelChecks()
    {
        var offset=new Vector3(4096,0,0);var nearBorder=new Vector3(15.9998f,2,11);var border=new Vector3(16,2,11);
        Require(nearBorder+offset==border+offset,"fixture loses a sub-mm X difference in world-space float");
        Require(LTRoadGeometryBoundary.TranslatedKey(nearBorder,offset)!=LTRoadGeometryBoundary.TranslatedKey(border,offset),"precise translated cache keys do not hide a real chunk boundary behind an interior edge");
        var s=LTRoadMath.Settings.Default;s.mode=LTRoadMode.GeometricAsphalt;s.width=8;
        s.asphaltDamage.potholes=1;s.asphaltDamage.brokenEdges=1;
        var path=new[]{new LTRoadPoint(new Vector3(8,2,0)),new LTRoadPoint(new Vector3(8,2,64))};
        LTRoadMath.Snapshot Road(LTRoadMath.Settings settings)=>LTRoadMath.Build(path,Matrix4x4.identity,settings);
        var old=Road(s);s.pitBevelWidth=.12f;s.pitBevelDepth=.05f;var beveled=Road(s);
        Require(old.geometryHash!=beveled.geometryHash&&old.paintHash==beveled.paintHash,"bevel invalidates geometry only");
        for(int i=0;i<old.geometricPits.Count;i++)for(int j=0;j<old.geometricPits[i].Count;j++)
            Require(old.geometricPits[i][j]==beveled.geometricPits[i][j],"bevel preserves outer paint/contact contour");
        s.pitShapeVariation=.7f;var varied=Road(s);
        Require(varied.geometryHash!=beveled.geometryHash&&varied.paintHash!=beveled.paintHash,"shape invalidates geometry and paint");
        var off=s;off.pitBevelDepth=0;Require(Road(off).geometricPits[0].bevelDepth==0,"zero depth disables bevel");
        off=s;off.pitBevelWidth=0;Require(Road(off).geometricPits[0].bevelDepth==0,"zero width disables bevel");
        foreach(var mode in new[]{LTRoadMode.Offroad,LTRoadMode.Asphalt,LTRoadMode.TerrainAsphalt})
        {
            var a=s;a.mode=mode;var first=Road(a);a.pitBevelWidth=a.pitBevelDepth=a.pitShapeVariation=0;var second=Road(a);
            Require(first.geometryHash==second.geometryHash&&first.paintHash==second.paintHash,"legacy modes ignore bevel and shape settings");
        }
        int pits=0;
        foreach(float shift in new[]{0f,4096f})foreach(float size in new[]{.1f,1f,3f})for(int seed=0;seed<24;seed++)
        {
            var p=path.Select(q=>new LTRoadPoint(q.position+new Vector3(shift,0,0))).ToArray();
            var settings=s;settings.asphaltDamage.size=size;settings.asphaltDamage.seed=seed;
            var a=LTRoadMath.Build(p,Matrix4x4.identity,settings);var b=LTRoadMath.Build(p,Matrix4x4.identity,settings);
            Require(a.geometricPits.Count==b.geometricPits.Count,"deterministic pit count");
            for(int i=0;i<a.geometricPits.Count;i++)
            {
                var pit=a.geometricPits[i];Require(pit.Count==8&&pit.Beveled,"eight bounded bevel facets");
                Require(pit.bevelDepth<=pit.depth*.5f,"bevel cannot consume the wall");
                for(int j=0;j<pit.Count;j++)
                {
                    Require(pit[j]==b.geometricPits[i][j]&&pit.InnerX(j)==b.geometricPits[i].InnerX(j),"deterministic facets");
                    Require(pit.Contains((float)pit.InnerX(j),(float)pit.InnerZ(j)),"inset stays inside footprint");
                }
                pits++;
            }
        }
        bool rejected=false;var invalid=s;invalid.pitBevelWidth=float.NaN;
        try{Road(invalid);}catch(ArgumentException){rejected=true;}Require(rejected,"reject invalid bevel settings");
        GeometricAsphaltChecks(true);
        ChunkBoundaryChecks();
        Console.WriteLine($"PASS bevel settings/shape: {pits} pits at 0/4096 m, sizes .1/1/3, 24 seeds; bounded convex contours, determinism, neutral/legacy hashes, paint silhouette.");
    }
}
