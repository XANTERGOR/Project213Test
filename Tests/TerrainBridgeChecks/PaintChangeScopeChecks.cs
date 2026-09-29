using System;
using System.IO;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void PaintChangeScopeChecks()
    {
        var random=new System.Random(18813);
        foreach(var layout in new[]{new Vector2Int(16,16),new Vector2Int(7,23),new Vector2Int(1,1)})
        foreach(float radius in new[]{0f,.1f,5f,97f,100000f})
        for(int fixture=0;fixture<60;fixture++)
        {
            const float width=64,depth=37;
            float sx=layout.x*width,sz=layout.y*depth;
            var area=fixture%3==0?new Rect((fixture%layout.x)*width,(fixture%layout.y)*depth,width,depth):
                new Rect((float)random.NextDouble()*sx*1.4f-sx*.2f,(float)random.NextDouble()*sz*1.4f-sz*.2f,
                    width*(float)random.NextDouble()*3,depth*(float)random.NextDouble()*3);
            var range=LTPaintMath.TerrainSampleTileRange(area,radius,width,depth,layout.x,layout.y);
            Require(range.xMin>=0&&range.yMin>=0&&range.xMax<=layout.x&&range.yMax<=layout.y,
                "terrain dependency tile range stays in world");
            void Sample(float px,float pz)
            {
                int x=Math.Max(0,Math.Min(layout.x-1,(int)Math.Floor(Math.Max(0,Math.Min(sx,px))/width)));
                int z=Math.Max(0,Math.Min(layout.y-1,(int)Math.Floor(Math.Max(0,Math.Min(sz,pz))/depth)));
                Require(x>=range.xMin&&x<range.xMax&&z>=range.yMin&&z<range.yMax,
                    "local signature must include each centre/curvature probe dependency, including world clamps");
            }
            for(int y=0;y<=8;y++)for(int x=0;x<=8;x++)
            {
                float px=area.xMin+area.width*x/8,pz=area.yMin+area.height*y/8;
                Sample(px,pz);Sample(px-radius,pz);Sample(px+radius,pz);Sample(px,pz-radius);Sample(px,pz+radius);
            }
        }
        var local=LTPaintMath.TerrainSampleTileRange(new Rect(8*64,8*64,64,64),5,64,64,16,16);
        Require(local.width*local.height==25,"local curvature fixture depends on 25 tiles, not all 256");
        var fallback=LTPaintMath.TerrainSampleTileRange(new Rect(0,0,64,64),float.NaN,64,64,16,16);
        Require(fallback.width*fallback.height==256,"invalid dependency inputs must fail conservative, never hide changes");
        const string root="Assets/TerrainSystem/LocalTerrain/";
        var runtime=File.ReadAllText(root+"LTPaintRuntime.cs");
        var rocks=File.ReadAllText(root+"LTPaintRocks.cs");
        Require(runtime.Contains("int filterSignature=TerrainFilterSignature(rect,local)")&&runtime.Contains("coverage=Mix(coverage,filterSignature);weightInput=Mix(weightInput,filterSignature)")&&
            rocks.Contains("coverage=Mix(coverage,TerrainFilterSignature(rect,local))"),"terrain and rocks use local filter dependencies");
        Require(!runtime.Contains("coverage=Mix(coverage,terrain.signature.GetHashCode())")&&
            !rocks.Contains("coverage=Mix(coverage,terrain.signature.GetHashCode())"),"distant terrain edits must not invalidate local paint through a global filter signature");
        Require(runtime.Contains("world.paintTerrainSignature=terrain.signature"),"global consumers keep the full terrain signature");
        Require(runtime.Contains("densityInput=Mix(coverage,world.displacementGeometry.RegionSignature(rect,TerrainFilterRadius(local)))"),
            "conservative density union uses local authoring revisions, including curvature dependencies");
        var tick=runtime.Substring(runtime.IndexOf("public void Tick(LTWorld world)"));
        Require(!tick.Contains("JsonUtility.ToJson(stamp.")&&!tick.Contains("layer.SurfaceHash()"),
            "per-chunk loops do not reserialize shared stamp filters or reread layer hashes");
        Require(tick.Contains("foreach(var stamp in active)coverageInputs.Add(stamp,CoverageInputs(world,stamp))")&&
            tick.Contains("foreach(var layer in palette)layerInputs.Add(layer,new LayerChangeInput(layer))"),
            "change snapshots are refreshed on every tick for Undo/import correctness");
        var editor=File.ReadAllText(root+"Editor/LTEditor.cs");
        var seam=editor.Substring(editor.IndexOf("static void ApplySeamNormals("));
        seam=seam.Substring(0,seam.IndexOf("sealed class PendingMesh"));
        Require(seam.Contains("if(mesh==chunk.mesh)chunk.updatedAt=EditorApplication.timeSinceStartup"),
            "normal-only neighbour changes invalidate cached terrain slopes for paint/details");
        Console.WriteLine("PASS paint change scope: 364500 dependency probes, rectangular/single-tile worlds, seams, curvature and clamping; tick-local hash/source contracts. Native rebake counts not measured.");
    }
}
