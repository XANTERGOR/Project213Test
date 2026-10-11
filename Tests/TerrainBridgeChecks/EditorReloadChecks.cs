using System;
using System.IO;
using LocalTerrainPrototype;

partial class Checks
{
    static void EditorReloadChecks()
    {
        int checks=0;
        void Check(bool ok,string why){checks++;Require(ok,"editor reload: "+why);}
        var p=new LTEditorReloadProof{code="code",owner="owner",inputs="inputs",outputs="outputs"};
        Check(p.Matches("code","owner","inputs","outputs",out var reason)&&reason==null,"unchanged proof accepted");
        foreach(string invalid in new[]{null,"","changed"})
        {
            Check(!p.Matches(invalid,"owner","inputs","outputs",out reason)&&reason!=null,"implementation invalidation");
            Check(!p.Matches("code",invalid,"inputs","outputs",out reason),"owner invalidation");
            Check(!p.Matches("code","owner",invalid,"outputs",out reason),"input invalidation");
            Check(!p.Matches("code","owner","inputs",invalid,out reason),"output invalidation");
        }
        p.version++;Check(!p.Matches("code","owner","inputs","outputs",out _),"version invalidation");p.version--;
        p.code="";Check(!p.Matches("","owner","inputs","outputs",out _),"empty proof never matches");
        // Every combination must explain exactly the existing settled predicate;
        // adding diagnostics must not loosen or tighten recovery eligibility.
        for(int mask=0;mask<256;mask++)
        {
            bool initialized=(mask&1)==0,density=(mask&2)!=0,build=(mask&16)!=0,paint=(mask&64)!=0,seams=(mask&128)!=0;
            int dirty=(mask&4)!=0?3:0,colliders=(mask&8)!=0?2:0,lods=(mask&32)!=0?7:0;
            bool settled=initialized&&!density&&dirty==0&&colliders==0&&!build&&lods==0&&!paint&&!seams;
            var message=LTEditorReloadDiagnostics.Pending(initialized,density,dirty,colliders,build,lods,paint,seams);
            Check((message==null)==settled,"diagnostic/guard parity "+mask);
        }
        string[] expected={"state-not-initialized","density-restore-pending","dirty-chunks=3","pending-colliders=2",
            "geometry-build-active","pending-lods=7","paint-pending","rock-seam-bake-pending"};
        var allReasons=LTEditorReloadDiagnostics.Pending(false,true,3,2,true,7,true,true);
        foreach(var expectedReason in expected)Check(allReasons.Contains(expectedReason),"diagnostic includes "+expectedReason);
        string engine=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        string recovery=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditorReloadRecovery.cs");
        string detail=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTDetailPlacementRuntime.cs");
        Check(engine.Contains("TryResumeAfterReload(w,state,current)")&&engine.Contains("if(state.reloadDensityPending)return"),"production hooks and density gate");
        Check(engine.Contains("state.reloadDensityPending=false;DetectDensity(w,state)")&&engine.Contains("s.reloadDensityPending=false;"),"density gate released after paint and edits");
        Check(recovery.Contains("s.dirty.Count==0&&s.colliders.Count==0")&&recovery.Contains("!s.build.Active&&s.chunkLODs.Count==0"),"pending work excluded");
        Check(recovery.Contains("Mesh.AcquireReadOnlyMeshData")&&recovery.Contains("part.GetVertexData<byte>")&&recovery.Contains("part.GetIndexData<byte>"),"exact output payload checked");
        Check(recovery.Contains("ReloadOutputKey(w,current,out reason)")&&recovery.Contains("ReloadOutputKey(w,current,out string outputReason)"),"fresh captured inputs used on both handoff sides");
        Check(recovery.Contains("PrepareContactContours(w,current,captured)")&&recovery.Contains("captured.contactSegments==null||captured.contactSegments.Count!=0"),"no-contact explicitly prepared and proved, never inferred from null");
        Check(engine.Contains("Stamp only=null")&&engine.Contains("if(only!=null&&!ReferenceEquals(stamp,only))continue;"),"targeted contact proof shares normal build algorithm");
        Check(recovery.Contains("captured.meshMode!=LTMeshConformMode.Union||!captured.meshCutTerrain||!captured.meshTrimRock"),"only original no-contact Union restore case allowed");
        Check(recovery.Contains("filter.sharedMesh!=stamp.sourceMesh")&&recovery.Contains("stamp.renderOutput!=null||stamp.generatedTrimmedMesh||stamp.generatedMeshOwner!=0")
            &&recovery.Contains("collider.sharedMesh!=stamp.sourceMesh"),"missing/generated output and incompatible collider excluded");
        Check(recovery.Contains("GetComponentsInChildren<LTRockTransitionRenderer>(true)")&&recovery.Contains("stamp.unionGroupOutputApplied"),"stale transitions and union outputs excluded");
        Check(recovery.Contains("meshes.Add(stamp.sourceMesh)")&&recovery.Contains("h.Add(\"source-only-rock\")")&&recovery.Contains("h.Add(\"saved-rock\")"),"source payload included and output kinds distinguished");
        Check(recovery.Contains("if(!stamp.renderAsset.Valid)")&&recovery.Contains("has an invalid saved render asset"),"invalid existing assets still rejected");
        Check(recovery.Contains("SessionState.EraseString(ReloadSessionKey)")&&!recovery.Contains("AssetDatabase.Save")&&!recovery.Contains("SaveScene("),"one-shot session handoff without project saves");
        Check(detail.Contains("[NonSerialized] LTDetailPlacementAsset reloadPlacement")&&detail.Contains("ValidPayload(restored,sources,maxInstances,rect)"),"transient placement validated");
        Check(!detail.Contains("savedPlacement=asset"),"user saved placement never replaced");
        Check(recovery.Contains("handoff={session.diagnosticId}")&&recovery.Contains("session.skipped.Add(item)")
            &&!recovery.Contains("if(session.worlds.Count>0)Debug.Log"),"zero-checkpoint handoffs and skip reasons logged");
        Check(recovery.Contains("No checkpoint for '")&&recovery.Contains("Init: handoff="),"both handoff sides observable");
        int readinessStart=recovery.IndexOf("public static void ReportEditorReloadReadiness()",StringComparison.Ordinal);
        int readinessEnd=recovery.IndexOf("static void DestroyReloadPlacement",readinessStart,StringComparison.Ordinal);
        var readiness=recovery.Substring(readinessStart,readinessEnd-readinessStart);
        Check(!readiness.Contains("GetState(w)")&&!readiness.Contains("Capture(w)")&&!readiness.Contains("Detect(w")
            &&!readiness.Contains("SessionState.Set")&&!readiness.Contains("SetDirty(")&&!readiness.Contains("SaveScene("),"readiness reporter remains passive");
        Console.WriteLine($"Editor reload managed policy: PASS {checks} checks (native lifecycle and performance not measured).");
    }
}
