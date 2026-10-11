using System;
using System.IO;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void LODCameraChecks()
    {
        var gate=new LTLODCameraSchedule();var origin=Vector3.zero;
        Require(gate.TryBegin(0,1,origin,false),"first fallback publishes LOD even without a render callback");
        Require(!gate.TryBegin(.001,1,origin,true),"same pose does not repeat fallback work before rendering");
        var latePosition=new Vector3(100,2,0);
        Require(!gate.TryBegin(.002,1,latePosition,false),"fallback retains its 100 ms polling budget");
        Require(gate.TryBegin(.002,1,latePosition,true),"late camera movement bypasses the polling interval before rendering");
        Require(!gate.TryBegin(.002,1,latePosition,true),"multiple render callbacks at the same pose do not repeat work");
        Require(gate.TryBegin(.002,1,new Vector3(200,2,0),true),"no frame/time stamp blocks a second changed pose");
        Require(gate.TryBegin(.003,2,new Vector3(200,2,0),true),"switching the selected camera invalidates even at identical positions");
        Require(gate.TryBegin(.004,0,origin,true),"loss of the selected camera updates the forced/fallback path");
        Require(gate.TryBegin(.005,1,origin,true),"restored selected camera updates immediately");
        Require(gate.TryBegin(.006,1,new Vector3(.000001f,0,0),true),"sub-epsilon motion is not lost at a threshold");
        Require(!gate.PollDue(.05),"idle poll remains bounded");
        Require(gate.TryBegin(.11,1,new Vector3(.000001f,0,0),false),"stationary scripts/settings still get periodic polling");
        gate.Invalidate();
        Require(gate.TryBegin(.111,1,new Vector3(.000001f,0,0),true),"inspector/cache invalidation bypasses stationary guard");

        foreach(var type in new[]{CameraType.Game,CameraType.SceneView})
            Require(LTLODCameraSchedule.IsRenderCamera(type),"game and Scene view are eligible render callbacks");
        foreach(var type in new[]{CameraType.Reflection,CameraType.Preview,CameraType.VR})
            Require(!LTLODCameraSchedule.IsRenderCamera(type),"auxiliary camera cannot drive a render update");

        gate.Invalidate();int updates=0;const int frames=720;
        for(int frame=0;frame<frames;frame++)
        {
            double now=10+frame/120.0;
            var position=new Vector3(frame*2.5f,20,(frame%37)*.1f);
            if(gate.TryBegin(now,1,position,true))updates++;
            Require(!gate.TryBegin(now,1,position,true),"duplicate callback during fast flight is skipped");
        }
        Require(updates==frames,"120 Hz flight updates at every camera pose, not 10 Hz");
        gate.Invalidate();int idleUpdates=0;
        for(int frame=0;frame<120;frame++)
            if(gate.TryBegin(frame/120.0,1,origin,true))idleUpdates++;
        Require(idleUpdates>=9&&idleUpdates<=10,"120 stationary repaints need only about 10 polls");
        for(int i=0;i<1000;i++)gate.TryBegin(1,1,origin,true);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)gate.TryBegin(1+i/120.0,1,new Vector3(i,0,0),true);
        Require(GC.GetAllocatedBytesForCurrentThread()==before,"camera scheduling has no managed allocation per move");

        string source=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTWorld.cs");
        string Section(string start,string end)
        {int a=source.IndexOf(start,StringComparison.Ordinal);return source.Substring(a,source.IndexOf(end,a,StringComparison.Ordinal)-a);}
        var enabled=Section("void OnEnable()","void OnValidate()");
        Require(enabled.Contains("beginCameraRendering-=BeforeCameraRendering")&&enabled.Contains("beginCameraRendering+=BeforeCameraRendering"),"enable subscribes idempotently");
        var disabled=Section("void OnDisable()","void Update()");
        Require(disabled.Contains("beginCameraRendering-=BeforeCameraRendering")&&disabled.Contains("lodCameraSchedule.Invalidate()"),"disable releases subscription and scheduling state");
        var hook=Section("void BeforeCameraRendering(","Camera ResolveLODCamera()");
        Require(hook.Contains("!isActiveAndEnabled")&&hook.Contains("paintBenchmarkRunning")&&hook.Contains("IsRenderCamera(renderingCamera.cameraType)"),"render callback respects component, benchmark and auxiliary-camera guards");
        Require(hook.Contains("UpdateLOD(ResolveLODCamera(),true)")&&!hook.Contains("UpdatePainting")&&!hook.Contains("Rebuild"),"render callback only selects LOD from the stable driver");
        var camera=Section("Camera ResolveLODCamera()","void UpdateLOD(Camera camera,bool beforeRender)");
        Require(camera.Contains("camera=lodCamera")&&camera.Contains("SceneView.lastActiveSceneView.camera")&&camera.Contains("camera=Camera.main"),"preserve assigned, Scene view and main camera policy");
        var update=Section("void UpdateLOD(Camera camera,bool beforeRender)","bool UpdateSpatialTopology(Camera camera)");
        Require(update.Contains("lodCameraSchedule.TryBegin(")&&update.Contains("updateColliders && camera")&&update.Contains("nextColliderLODUpdate=now+.1"),"visual updates keep collider maintenance separately throttled");
        Require(update.Contains("spatialTopology.DirtyChunks.Contains(id)")&&!update.Contains("yield return")&&!update.Contains("await "),"publish dirty chunk indices synchronously after shared topology balance");
        var chunk=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTChunk.cs");
        Require(chunk.Contains("if(changed||dirty)")&&chunk.Contains("spatialMesh.SetIndices"),"unchanged chunk index buffers are not uploaded again");
        var paint=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTPaintGlobals.cs");
        int materialHook=paint.IndexOf("void SelectGlobalMaterials(",StringComparison.Ordinal);
        Require(materialHook>=0,"material camera callback exists");
        var selection=paint.Substring(materialHook);
        int previewGuard=selection.IndexOf("if(!camera||camera.cameraType==CameraType.Preview)return;",StringComparison.Ordinal);
        Require(previewGuard>=0&&previewGuard<selection.IndexOf("CameraScope()",StringComparison.Ordinal)&&
            previewGuard<selection.IndexOf("foreach(var state in chunks.Values)",StringComparison.Ordinal)&&
            previewGuard<selection.IndexOf("camera.transform.position",StringComparison.Ordinal),
            "null/destroyed and inspector-preview cameras return before timing, distance queries or shared material writes");
        Require(selection.Contains("state.renderer.bounds.SqrDistance(camera.transform.position)")&&
            selection.Contains("far?state.farMaterial:state.displacementActive")&&
            selection.Contains("if(state.renderer.sharedMaterial!=selected)state.renderer.sharedMaterial=selected;"),
            "real camera renders retain distance-based near/far/no-displacement selection and idle write dedup");
        Require(!selection.Contains("IsRenderCamera(")&&!selection.Contains("camera=Camera.main")&&!selection.Contains("camera=globalWorld.lodCamera"),
            "material selection keeps each real view, including reflection cameras, instead of the geometry LOD driver");
        Console.WriteLine("PASS material-camera source contracts: preview/null guard precedes all effects; real per-view near/far/no-displacement selection remains intact. C# compilation and live inspector rendering are separate checks.");
        Console.WriteLine($"PASS LOD camera scheduling: {updates} fast-flight poses at 120 Hz, same-tick/late movement, idle dedup, camera change, invalidation, auxiliary-camera guards and zero scheduling allocations. Source contracts for render lifecycle/driver/dirty uploads; not a native render test.");
    }
}
