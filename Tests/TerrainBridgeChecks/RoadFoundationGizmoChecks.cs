using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadFoundationGizmoChecks()
    {
        int checks=0;
        void Check(bool ok,string why){Require(ok,why);checks++;}
        var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.Asphalt;settings.width=4;settings.shoulderWidth=1;settings.blendWidth=2;
        settings.sampleSpacing=1;settings.surfaceOffset=.25f;
        var points=new[]{new LTRoadPoint(new Vector3(0,10,0),8),new LTRoadPoint(new Vector3(0,12,15),8),new LTRoadPoint(new Vector3(0,15,30),8)};
        var straight=LTRoadMath.Build(points,Matrix4x4.identity,settings);
        var before=straight.samples.ToArray();
        void Validate(LTRoadMath.Snapshot road,LTRoadFoundationOutline.Lines lines,bool cutStart=false,bool cutEnd=false)
        {
            Check(lines.shoulder.Length>0&&lines.shoulder.Length%2==0,"complete shoulder line pairs");
            Check((road.blendWidth==0)==(lines.transition.Length==0),"transition presence matches settings");
            foreach(bool outer in new[]{false,true})foreach(var p in outer?lines.transition:lines.shoulder)
            {
                Check(float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z),"finite line positions");
                Check(road.TrySample(p.x,p.z,out var hit),"valid nearest spline sample");
                Check(Math.Abs(p.y-road.SurfaceHeight(hit,hit.lateral))<.0001f,"height follows slope/bank, no asphalt offset");
                float distance=hit.radialDistance-road.HalfWidth(hit)-road.shoulderWidth-(outer?road.blendWidth:0);
                var first=road.samples[0];var last=road.samples[road.samples.Count-1];float clip=float.NegativeInfinity;
                // Clipped fixtures are straight (also translated/yawed): their exact
                // boundary is the capsule intersected with these two half-planes.
                // A point slightly INSIDE a cap is near that boundary, not many metres
                // away just because its nearest point is already inside the spline.
                if(cutStart)clip=-Vector3.Dot(new Vector3(p.x-first.position.x,0,p.z-first.position.z),new Vector3(-first.right.z,0,first.right.x));
                if(cutEnd)clip=Math.Max(clip,Vector3.Dot(new Vector3(p.x-last.position.x,0,p.z-last.position.z),new Vector3(-last.right.z,0,last.right.x)));
                Check(Math.Abs(Math.Max(distance,clip))<=Math.Max(.025f,lines.cellSize*.6f),$"isoline matches nearest-field coverage: p={p}, distance={distance}, clip={clip}, step={lines.cellSize}, cuts={cutStart}/{cutEnd}");
            }
        }
        var result=LTRoadFoundationOutline.Build(straight,false,false);Validate(straight,result);
        Check(Math.Abs(result.shoulder.Min(p=>p.x)+3)<.001f&&Math.Abs(result.shoulder.Max(p=>p.x)-3)<.001f,"shoulder width");
        Check(Math.Abs(result.transition.Min(p=>p.x)+5)<.001f&&Math.Abs(result.transition.Max(p=>p.x)-5)<.001f,"outer transition width");
        Check(result.transition.Min(p=>p.z)<-4.9f&&result.transition.Max(p=>p.z)>34.9f,"free ends are rounded");
        var repeat=LTRoadFoundationOutline.Build(straight,false,false);
        Check(repeat.shoulder.SequenceEqual(result.shoulder)&&repeat.transition.SequenceEqual(result.transition),"deterministic contours");
        Check(before.SequenceEqual(straight.samples),"snapshot unchanged");
        foreach(bool start in new[]{false,true})foreach(bool end in new[]{false,true})
        {
            var lines=LTRoadFoundationOutline.Build(straight,start,end);Validate(straight,lines,start,end);
            Check(!start||lines.transition.All(p=>p.z>=-.0001f),$"no rounded cap inside start junction: min={lines.transition.Min(p=>p.z):R}");
            Check(!end||lines.transition.All(p=>p.z<=30.0001f),"no rounded cap inside end junction");
        }
        settings.blendWidth=0;settings.shoulderWidth=0;
        Validate(LTRoadMath.Build(points,Matrix4x4.identity,settings),LTRoadFoundationOutline.Build(LTRoadMath.Build(points,Matrix4x4.identity,settings),false,false));
        settings.blendWidth=2;settings.shoulderWidth=1;
        var curve=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(0,4,0)),new LTRoadPoint(new Vector3(0,5,25)),new LTRoadPoint(new Vector3(5,5,29)),new LTRoadPoint(new Vector3(9,6,25)),new LTRoadPoint(new Vector3(9,6,0))},Matrix4x4.identity,settings);
        Validate(curve,LTRoadFoundationOutline.Build(curve,false,false));
        var transform=Matrix4x4.identity;float angle=.37f;transform.m00=(float)Math.Cos(angle);transform.m02=(float)Math.Sin(angle);transform.m20=-transform.m02;transform.m22=transform.m00;
        transform.m03=902;transform.m13=4;transform.m23=137;
        var moved=LTRoadMath.Build(points,transform,settings);Validate(moved,LTRoadFoundationOutline.Build(moved,true,true),true,true);
        var longRoad=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(0,0,0)),new LTRoadPoint(new Vector3(1000,15,1000))},Matrix4x4.identity,settings);
        var longLines=LTRoadFoundationOutline.Build(longRoad,false,false);Validate(longRoad,longLines);
        Check(longLines.shoulder.Length<32768*4&&longLines.transition.Length<32768*4,"bounded long-road output");
        settings.width=6;settings.shoulderWidth=2;
        var wider=LTRoadFoundationOutline.Build(LTRoadMath.Build(points,Matrix4x4.identity,settings),false,false);
        Check(wider.shoulder.Max(p=>p.x)>result.shoulder.Max(p=>p.x)+1.9f,"settings reflected immediately");
        const string folder="Assets/TerrainSystem/LocalTerrain/Editor/";
        var draw=File.ReadAllText(folder+"LTRoadFoundationGizmos.cs");var editor=File.ReadAllText(folder+"LTRoadEditor.cs");
        Check(draw.Contains("ReferenceEquals(cache.source,snapshot)")&&draw.Contains("ConditionalWeakTable"),"cached read-only repaint");
        Check(!draw.Contains("GizmoType.Pickable")&&!draw.Contains("BakeNow")&&!draw.Contains("SetDirty"),"display doesn't intercept scene controls or bake/save");
        Check(editor.IndexOf("Показывать зоны основания",StringComparison.Ordinal)>=0&&draw.Contains("SessionState"),"UI switch without serialized scene changes");
        Console.WriteLine($"PASS road foundation gizmo: {checks} assertions; widths, rounded/clipped ends, bank/slope, tight curves, translated/yawed and 1.4km roads, deterministic bounded output, zero transition, immutable snapshot and UI/cache contracts. Native Scene rendering not exercised.");
    }
}
