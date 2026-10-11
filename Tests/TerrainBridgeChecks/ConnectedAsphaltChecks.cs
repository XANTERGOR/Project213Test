using System;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void ConnectedAsphaltChecks()
    {
        var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.GeometricAsphalt;settings.width=8;
        settings.connectedDamage=true;settings.connectedFloorAsphalt=.7f;settings.repairPatchStrength=.75f;
        settings.asphaltDamage.potholes=1;settings.asphaltDamage.brokenEdges=1;settings.asphaltDamage.depth=.06f;
        var path=new[]{new LTRoadPoint(new Vector3(8,2,0)),new LTRoadPoint(new Vector3(8,2,64))};
        LTRoadMath.Snapshot Road(LTRoadMath.Settings s)=>LTRoadMath.Build(path,Matrix4x4.identity,s);
        var first=Road(settings);var change=settings;change.connectedDamage=false;var legacy=Road(change);
        change.connectedFloorAsphalt=change.repairPatchStrength=0;
        Require(Road(change).geometryHash==legacy.geometryHash&&Road(change).paintHash==legacy.paintHash,"disabled connected controls preserve legacy hashes");
        change=settings;change.connectedFloorAsphalt=0;change.repairPatchStrength=0;var unpainted=Road(change);
        Require(first.geometryHash==unpainted.geometryHash&&first.paintHash!=unpainted.paintHash,"floor and patch paint do not invalidate geometry");
        change=settings;change.pitBevelWidth=.12f;change.pitBevelDepth=.05f;change.pitShapeVariation=1;
        Require(Road(change).geometryHash==first.geometryHash&&Road(change).paintHash==first.paintHash,"connected mode ignores old convex shape/bevel controls");
        change=settings;change.asphaltDamage.strength=0;Require(Road(change).geometricPits.Count==0,"damage off restores geometry");
        foreach(var mode in new[]{LTRoadMode.Offroad,LTRoadMode.Asphalt,LTRoadMode.TerrainAsphalt})
        {
            var a=settings;a.mode=mode;var b=a;b.connectedDamage=false;b.connectedFloorAsphalt=b.repairPatchStrength=0;
            Require(Road(a).geometryHash==Road(b).geometryHash&&Road(a).paintHash==Road(b).paintHash,"other modes ignore connected controls");
        }
        int count=0,retained=0;
        foreach(float origin in new[]{0f,4096f})foreach(float size in new[]{.1f,1f,4.5f})for(int seed=0;seed<24;seed++)
        {
            var s=settings;s.asphaltDamage.size=size;s.asphaltDamage.seed=seed;
            var p=path.Select(q=>new LTRoadPoint(q.position+new Vector3(origin,0,0))).ToArray();
            var a=LTRoadMath.Build(p,Matrix4x4.identity,s);var b=LTRoadMath.Build(p,Matrix4x4.identity,s);
            Require(a.geometricPits.Count==b.geometricPits.Count,"deterministic connected count");
            for(int i=0;i<a.geometricPits.Count;i++)
            {
                var pit=a.geometricPits[i];Require(pit.Concave&&pit.Count==12&&!pit.Beveled,"joined concave lobes with bounded topology");
                Require(pit.depth>0&&pit.depth<=.06f,"shallow variable depth");
                double polygon=0,parts=0;
                double Cross(Vector2 u,Vector2 v,Vector2 w)=>((double)v.x-u.x)*((double)w.y-u.y)-((double)v.y-u.y)*((double)w.x-u.x);
                for(int j=1;j+1<pit.Count;j++)polygon+=Cross(pit[0],pit[j],pit[j+1]);
                for(int k=0;k<pit.PartCount;k++)for(int j=1;j+1<pit.PartSize(k);j++)parts+=Cross(pit.PartPoint(k,0),pit.PartPoint(k,j),pit.PartPoint(k,j+1));
                Require(Math.Abs(polygon-parts)<1e-8,$"convex pieces cover concave contour exactly: origin={origin},size={size},seed={seed},pit={i},area={polygon:R},parts={parts:R}");
                for(int j=0;j<pit.Count;j++)Require(pit[j]==b.geometricPits[i][j],"deterministic outline");
                for(int j=i+1;j<a.geometricPits.Count;j++)Require(!LTRoadGeometryMath.Intersects(pit,a.geometricPits[j]),"no overlapping connected zones");
                var mid=pit.bounds.center;Require(pit.Contains(mid.x,mid.y),"fixture centre in middle lobe");
                if(pit.asphaltCoverage>0){Require(a.PaintWeight(mid.x,mid.y)>0,"retained asphalt floor");retained++;}
                count++;
            }
        }
        Require(retained>100,"many retained floors tested");
        float coverage=0,crack=0,repair=0;
        LTRoadAsphaltMath.CompositeSurface(ref coverage,ref crack,ref repair,.8f,.4f,.6f,true);
        LTRoadAsphaltMath.CompositeSurface(ref coverage,ref crack,ref repair,.5f,0,0,false);
        Require(Math.Abs(coverage-.4f)<1e-6&&Math.Abs(crack-.16f)<1e-6&&Math.Abs(repair-.24f)<1e-6,"patch ownership attenuated by later layer");
        LTRoadAsphaltMath.CompositeSurface(ref coverage,ref crack,ref repair,1,0,0,true);
        Require(coverage==1&&crack==0&&repair==0,"later clean same-layer road removes previous patch");
        int painted=0,clean=0;
        for(int z=0;z<640;z++)for(int x=0;x<16;x++)
        {
            Require(first.TrySample(4+x*.5f,z*.1f,out var hit),"sample test road");
            float patch=first.RepairPatch(hit);Require(patch>=0&&patch<=.75f,"bounded patches");
            if(patch>.1f)painted++;else clean++;
        }
        Require(painted>100&&clean>100,"patches interspersed with unchanged asphalt");
        GeometricAsphaltChecks(connected:true);
        Console.WriteLine($"PASS connected damage: {count} concave contours, {retained} retained floors, 24 seeds / 3 sizes / 0 and 4096m; exact partitions, hashes, neutral modes, ordered patch ownership, {painted} patch samples.");
    }
}
