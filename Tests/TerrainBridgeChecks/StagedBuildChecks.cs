using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;

partial class Checks
{
    static void StagedBuildChecks()
    {
        var queue=new LTStagedBuild();var dirty=new HashSet<int>{3};
        int lod0=0,coarse=0,disposed=0;
        IEnumerable<LTBuildStep> Run(HashSet<int> affected)
        {
            try
            {
                affected.Add(4); // a balanced seam neighbour found during preparation
                yield return LTBuildStep.Working;
                lod0++;dirty.Clear();yield return LTBuildStep.LOD0Ready;
                coarse++;yield return LTBuildStep.Working;
            }
            finally{disposed++;}
        }
        queue.Begin(1,dirty,a=>Run(a).GetEnumerator());
        Require(queue.Active&&lod0==0,"begin is lazy; no work while dragging");
        Require(!queue.Advance(1,dirty)&&lod0==0,"planning checkpoint keeps old display");
        Require(queue.Advance(1,dirty)&&lod0==1&&coarse==0&&queue.Lod0Ready,"LOD0 is published on a separate advance before coarse work");
        dirty.Add(9);queue.Invalidate(2,dirty);
        Require(!queue.Active&&disposed==1&&coarse==0&&dirty.SetEquals(new[]{3,4,9}),"superseded build disposes and requeues even already-published chunks and seam neighbours");
        queue.Begin(2,dirty,a=>Run(a).GetEnumerator());
        queue.Advance(2,dirty);queue.Advance(2,dirty);queue.Advance(2,dirty);queue.Advance(2,dirty);
        Require(!queue.Active&&lod0==2&&coarse==1&&disposed==2&&dirty.Count==0&&queue.PendingCount==0,"newest revision completes once and releases storage");
        queue.Cancel(dirty);Require(dirty.Count==0&&disposed==2,"idle cancellation cannot requeue completed work");

        // Rapid edits are coalesced in the dirty set, not retained as a job queue.
        for(int revision=3;revision<103;revision++)
        {
            dirty.Add(7);queue.Begin(revision,dirty,a=>Run(a).GetEnumerator());
            queue.Advance(revision,dirty);queue.Advance(revision+1,dirty);
            Require(!queue.Active&&lod0==2&&coarse==1,"stale advance cannot publish");
        }
        Require(disposed==102&&dirty.SetEquals(new[]{4,7}),"rapid superseding retains only bounded latest dirty IDs");
        // Disable, reload, destruction and manual/save barrier use the same disposal.
        for(int phase=0;phase<3;phase++)
        {
            queue.Begin(200,dirty,a=>Run(a).GetEnumerator());
            for(int step=0;step<phase;step++)queue.Advance(200,dirty);
            queue.Cancel(dirty);Require(!queue.Active&&dirty.Count>0,"lifecycle cancellation requeues outstanding work at every phase");
        }
        var expectedDirty=dirty.ToArray();int failedDisposals=0;
        IEnumerable<LTBuildStep> Fail()
        {
            try{yield return LTBuildStep.Working;throw new InvalidOperationException("test");}
            finally{failedDisposals++;}
        }
        queue.Begin(300,dirty,a=>Fail().GetEnumerator());queue.Advance(300,dirty);
        try{queue.Advance(300,dirty);throw new Exception("must fail");}
        catch(InvalidOperationException){queue.Cancel(dirty);}
        Require(failedDisposals==1&&!queue.Active&&dirty.SetEquals(expectedDirty),"failure releases continuation without losing dirty work");

        const string root="Assets/TerrainSystem/LocalTerrain/";
        var engine=File.ReadAllText(root+"Editor/LTEditor.cs");
        var chunk=File.ReadAllText(root+"LTChunk.cs");var bake=File.ReadAllText(root+"Editor/LTSpatialLODBake.cs");
        var tick=engine.Substring(engine.IndexOf("static void Tick()"));tick=tick.Substring(0,tick.IndexOf("public static void Refresh("));
        Require(tick.IndexOf("Detect(w,state,current)")<tick.IndexOf("state.build.Invalidate")&&tick.IndexOf("state.build.Invalidate")<tick.IndexOf("state.build.Advance"),"detect and invalidate always precede resume");
        Require(tick.Contains("if(!isEditing)")&&tick.Contains("while(!rebuilt&&!state.build.Waiting&&stageTimer.Elapsed.TotalMilliseconds<4)"),"release gate, worker wait and mandatory publication frame remain in production");
        Require(engine.Contains("var stamps=state.previous;")&&engine.Contains("PrepareTrimmedRockMeshes(w,stamps,pending"),"staged snapshot owns the contact contours across detection ticks");
        Require(chunk.Contains("level=lodsPending?0:")&&chunk.Contains("lodsPending ? null : spatialLOD"),"pending chunks never show old coarse geometry after reload or publication");
        Require(chunk.Contains("OnDisable(){ReleaseLODPreview();}")&&chunk.Contains("OnDestroy(){ReleaseLODPreview();}")&&bake.Contains("HideFlags.HideAndDontSave"),"temporary mesh ownership and destruction are explicit");
        Require(engine.Contains("CancelBuilds(s)")&&engine.Contains("while(steps.MoveNext()){}")&&bake.Contains("if(chunk.lodsPending)throw"),"save/manual builds drain; player export rejects unfinished data");
        Require(engine.Contains("state.boundaryCache,new HashSet<int>{id},true)")&&engine.Contains("var baseMeshes=coarseOnly?Array.Empty<Mesh>()"),"coarse publication does not write LOD0 again or invalidate terrain painting");
        var undo=engine.Substring(engine.IndexOf("Undo.undoRedoPerformed +="));undo=undo.Substring(0,undo.IndexOf("AssemblyReloadEvents.beforeAssemblyReload"));
        Require(undo.Contains("CancelBuilds(state)")&&!undo.Contains("initialized=false"),"Undo rejects stale work without globally invalidating unrelated chunks");
        Require(engine.Contains("assetInputHashes.Clear()")&&engine.Contains("AssetInputHash(mesh)==meshInput")&&engine.Contains("!state.buildObjectsValid()"),"asset reimport and generated-object replacement invalidate pending publications");
        Console.WriteLine("PASS staged builds: LOD0-first, revision rejection, 100 rapid edits, dirty union/seam neighbours, lifecycle cancellation and failure disposal; production source guards. No native Editor timing.");
    }
}
