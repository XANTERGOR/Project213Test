using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadUpdateCycleChecks()
    {
        const int nx=16,nz=16;
        const float width=64,depth=37;
        var revisions=new LTPaintMath.TerrainAuthoringRevisions();
        revisions.Configure(nx,nz,width,depth,"config-a");
        Rect Area(int id)=>new Rect(id%nx*width,id/nx*depth,width,depth);
        int[] Keys(float radius)=>Enumerable.Range(0,nx*nz).Select(id=>revisions.RegionSignature(Area(id),radius)).ToArray();
        int assertions=0;
        void Check(bool condition,string message){assertions++;Require(condition,"road update cycle: "+message);}
        // Includes edges, old and new disconnected locations, repeat edit/deletion/
        // Undo (all must start a fresh union, even if geometry returns to an old value).
        foreach(float radius in new[]{0f,5f,95f})
        foreach(int changed in new[]{0,15,240,255,7*nx+7,7*nx+8,2*nx+2,12*nx+12,2*nx+2})
        {
            var before=Keys(radius);
            revisions.Touch(changed);
            var after=Keys(radius);
            for(int id=0;id<before.Length;id++)
            {
                var range=LTPaintMath.TerrainSampleTileRange(Area(id),radius,width,depth,nx,nz);
                bool dependent=changed%nx>=range.xMin&&changed%nx<range.xMax&&changed/nx>=range.yMin&&changed/nx<range.yMax;
                Check((before[id]!=after[id])==dependent,"only consumers whose probe/normal halo contains a touched tile reset");
            }
            var stable=Keys(radius);
            revisions.Configure(nx,nz,width,depth,"config-a");
            Check(stable.SequenceEqual(Keys(radius)),"same authoring configuration/generated mesh updates do not reset coverage");
        }
        var initial=Keys(0);
        revisions.Touch(8*nx+8);
        int localCount=initial.Zip(Keys(0),(a,b)=>a!=b?1:0).Sum();
        Check(localCount>0&&localCount<256,"local edit does not reset all chunks");
        var oldFootprint=Keys(0);revisions.Touch(2*nx+2);var newFootprint=Keys(0);revisions.Touch(12*nx+12);
        Check(oldFootprint[2*nx+2]!=newFootprint[2*nx+2],"old footprint resets when road moves away or is deleted");
        Check(newFootprint[12*nx+12]!=Keys(0)[12*nx+12],"new footprint resets when road arrives");
        var globalBefore=Keys(0);revisions.Configure(nx,nz,width,depth,"config-b");var globalAfter=Keys(0);
        for(int id=0;id<globalBefore.Length;id++)Check(globalBefore[id]!=globalAfter[id],"real global configuration changes reset every consumer");
        int invalidBefore=revisions.RegionSignature(Area(0),float.NaN);
        revisions.Touch(255);
        Check(invalidBefore!=revisions.RegionSignature(Area(0),float.NaN),"invalid radius conservatively includes all chunks");
        revisions.Configure(1,1,5,7,"resized");
        int single=revisions.RegionSignature(new Rect(-1,-1,20,20),0);revisions.Touch(0);
        Check(single!=revisions.RegionSignature(new Rect(-1,-1,20,20),0),"resized single-tile worlds invalidate correctly");

        const string root="Assets/TerrainSystem/LocalTerrain/";
        string editor=File.ReadAllText(root+"Editor/LTEditor.cs"),world=File.ReadAllText(root+"LTWorld.cs"),runtime=File.ReadAllText(root+"LTPaintRuntime.cs");
        Check(!editor.Contains("displacementGeometryKey")&&!runtime.Contains("displacementGeometryKey"),"global stamp key is no longer mixed into every chunk");
        Check(editor.Contains("w.displacementGeometry.Touch(id)")&&editor.Contains("Mark(w,s,a.bounds,a.road,b?.road)")&&editor.Contains("Mark(w,s,b.bounds,b.road,a?.road)"),
            "authoring diff touches old and new path including deletion; density-only invalidation remains separate");
        var markDensity=editor.Substring(editor.IndexOf("static void MarkDensity("));
        markDensity=markDensity.Substring(0,markDensity.IndexOf("static void DetectDensity("));
        Check(!markDensity.Contains("displacementGeometry"),"generated density refinement never resets its own coverage union");
        Check(runtime.Contains("state.densityInputHash==densityInput&&state.densityCoverage.grid.size==cells?"),"conservative coverage union remains enabled within each local authoring revision");
        Check(editor.Contains("LTWorld.EditorOwnsPainting=OwnsPainting")&&world.Contains("!afterGeometryUpdate&&EditorOwnsPainting!=null&&EditorOwnsPainting(this)"),
            "ExecuteAlways and independent editor paint callbacks defer to the automatic geometry owner");
        Check(world.Contains("if(!Application.isPlaying&&!afterGeometryUpdate")&&editor.Contains("w.autoUpdate&&w.source&&w.generatedRoot"),
            "runtime/manual/non-generated worlds are not gated by automatic editor ownership");
        var tick=editor.Substring(editor.IndexOf("static void Tick()"));tick=tick.Substring(0,tick.IndexOf("public static void Refresh("));
        int advance=tick.IndexOf("state.build.Advance(state.revision,state.dirty)");
        Check(advance>=0&&advance<tick.IndexOf("w.UpdatePainting(true)"),"coordinated paint runs after geometry advances");
        Check(tick.Contains("if(state.dirty.Count==0&&!isEditing)"),"paint cannot run while geometry is still dirty or dragging");
        Check(tick.Contains("if(rebuilt||EditorApplication.timeSinceStartup-state.lastPaint>=.15)")&&
            world.Contains("paintRuntime.Tick(this,afterGeometryUpdate)")&&runtime.Contains("if(!force&&now<nextUpdate)return"),
            "coordinator retains idle throttling but cannot skip fresh post-mesh coverage due to the runtime throttle");
        Check(tick.IndexOf("DetectDensity(w,state)")>tick.IndexOf("w.UpdatePainting(true)")&&
            tick.Contains("if(state.dirty.Count==0&&state.colliders.Count>0"),"new coverage is consumed before deciding whether to cook colliders");
        Check(editor.Contains("lastEditorUpdateCycle")&&editor.Contains("cycleMeshPasses")&&editor.Contains("cyclePaintMs"),"diagnostics retain cycle pass counts and stage totals");
        Console.WriteLine($"PASS road update cycle: {assertions} checks; one-tile edit resets {localCount}/256 consumers with halo, old/new/Undo/config/curvature covered. Scheduling checked by source contracts, not a native scene timing.");
    }
}
