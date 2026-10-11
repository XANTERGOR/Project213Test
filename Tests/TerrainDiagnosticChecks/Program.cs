using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LocalTerrainPrototype;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

static class Program
{
    static int assertions;
    static void Require(bool value,string why){assertions++;if(!value)throw new Exception(why);}
    static object Call(object obj,string method,params object[] args)=>obj.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(obj,args);
    static T Field<T>(object obj,string name)=>(T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj);
    static void Main()
    {
        SampleChecks();MaterialChecks();HeightPairChecks();FiveLayerChecks();ShadowChecks();MetricChecks();PacingChecks();GeometryChecks();ControllerChecks();SourceChecks();
        Console.WriteLine($"PASS Terrain diagnostic: {assertions} managed assertions. Statistics, material ownership/restore, capture cancellation, same-view comparison, export, source safety. NOT native Unity/GPU or live performance.");
    }
    static void SampleChecks()
    {
        var capture=new LTTerrainFrameSamples();capture.Start(0);
        Require(!capture.Tick(1)&&capture.milliseconds.Count==0,"warm-up omitted");
        Require(!capture.Tick(2)&&capture.Sampling&&capture.milliseconds.Count==0,"warm-up boundary omitted");
        for(int i=3;i<7;i++)Require(!capture.Tick(i),"five seconds not reached");
        Require(capture.Tick(7)&&!capture.Running&&capture.milliseconds.Count==5,"five complete intervals");
        Require(LTTerrainFrameSamples.TrySummarize(capture.milliseconds,out var mean,out var median,out var p95)&&mean==1000&&median==1000&&p95==1000,"known timing");
        Require(LTTerrainFrameSamples.TrySummarize(new double[]{1,2,3,4,100},out mean,out median,out p95)&&mean==22&&median==3&&p95==100,"nearest rank p95, mean not average FPS");
        foreach(var bad in new[]{double.NaN,double.PositiveInfinity,0,-1})
            Require(!LTTerrainFrameSamples.TrySummarize(new[]{1d,bad},out mean,out median,out p95),"invalid frames must not be silently dropped");
        Require(!LTTerrainFrameSamples.TrySummarize(Array.Empty<double>(),out mean,out median,out p95)&&double.IsNaN(mean),"empty unavailable not zero");
        capture.Start(10);Require(!capture.Tick(10)&&!capture.Running,"zero interval cancels");
        capture.Start(10);Require(!capture.Tick(9)&&!capture.Running,"reversed interval cancels");
        capture.Start(0);capture.Tick(3);capture.Tick(3.01);capture.Cancel();
        Require(!capture.Tick(20)&&capture.milliseconds.Count==1,"cancel does not collect later time");
        capture.Start(100);Require(capture.milliseconds.Count==0&&!capture.Sampling,"restart resets");
        capture.Tick(102);Require(capture.Tick(108)&&capture.milliseconds.Single()==6000,"long stalls included, not clipped");
    }
    static void MaterialChecks()
    {
        int baseOnly=Shader.PropertyToID("_LTBaseOnly"),tri=Shader.PropertyToID("_LTTriplanar"),other=Shader.PropertyToID("Other");
        var source=new Material(new Shader());source.SetFloat(tri,1);source.SetFloat(other,42);source.EnableKeyword("KEEP_ME");
        var renderer=new MeshRenderer{sharedMaterial=source};
        var cache=new LTTerrainDiagnosticMaterials(renderer);
        renderer.bounds=new Bounds{distance=10};Require(cache.DistanceSquared(default)==10,"reference material bounds captured");
        renderer.bounds=new Bounds{distance=30};Require(cache.DistanceSquared(default)==10,"LOD bound changes cannot change material distance choice");
        var normal=cache.Select(source,1,LTTerrainDiagnosticMode.Normal,false,out bool refreshed);renderer.sharedMaterial=normal;
        Require(refreshed&&normal!=source&&normal.GetFloat(other)==42&&normal.GetFloat(tri)==1,"normal cloned without semantic changes");
        int copies=normal.copies,revision=LTTerrainDiagnosticMaterials.Revision,content=LTTerrainDiagnosticMaterials.ContentRevision;
        Require(cache.Select(source,1,LTTerrainDiagnosticMode.Normal,false,out refreshed)==normal&&!refreshed&&normal.copies==copies&&revision==LTTerrainDiagnosticMaterials.Revision,"no property copy/allocation every frame");
        Require(cache.Owns(normal)&&!cache.Owns(source),"ownership guard for runtime Tick");
        var one=cache.Select(source,1,LTTerrainDiagnosticMode.BaseLayer,false,out refreshed);
        Require(one.GetFloat(baseOnly)==1&&one.GetFloat(tri)==1&&one.IsKeywordEnabled("KEEP_ME"),"single base retains triplanar/keywords");
        Require(LTTerrainDiagnosticMaterials.ContentRevision==content,"mode switch not source change");
        var flat=cache.Select(source,1,LTTerrainDiagnosticMode.NoTriplanar,false,out refreshed);
        Require(flat.GetFloat(baseOnly)==0&&flat.GetFloat(tri)==0,"modes do not accumulate overrides");
        Require(source.GetFloat(baseOnly)==0&&source.GetFloat(tri)==1&&source.GetFloat(other)==42,"source untouched");
        var noShadows=cache.Select(source,1,LTTerrainDiagnosticMode.NoShadows,false,out refreshed);
        Require(noShadows.GetFloat(baseOnly)==0&&noShadows.GetFloat(tri)==1&&noShadows.GetFloat(other)==42,"no-shadows mode keeps NORMAL terrain material");
        source.SetFloat(other,99);cache.Select(source,2,LTTerrainDiagnosticMode.NoTriplanar,false,out refreshed);
        Require(refreshed&&flat.GetFloat(other)==99&&LTTerrainDiagnosticMaterials.ContentRevision>content,"source refresh propagated separately");
        var farSource=new Material(source);farSource.EnableKeyword("_LT_FAR_ONLY");
        var far=cache.Select(farSource,2,LTTerrainDiagnosticMode.BaseLayer,true,out refreshed);
        Require(far.GetFloat(baseOnly)==0&&far.GetFloat(tri)==1&&far.IsKeywordEnabled("_LT_FAR_ONLY"),"far atlas untouched");
        cache.Select(farSource,2,LTTerrainDiagnosticMode.NoTriplanar,true,out refreshed);
        Require(far.GetFloat(tri)==1,"far projection basis retained");
        var shader=new Shader();source.shader=shader;
        cache.Select(source,2,LTTerrainDiagnosticMode.Normal,false,out refreshed);
        Require(refreshed&&normal.shader==shader,"shader source replacement updates copy");
        renderer.sharedMaterial=normal;LTTerrainDiagnosticMaterials.RestoreAll();
        Require(renderer.sharedMaterial==source&&normal.destroyed&&far.destroyed&&!source.destroyed&&!farSource.destroyed,"restore exact source; destroy owned copies only");
        cache.Dispose();Require(renderer.sharedMaterial==source,"dispose idempotent");
        Require(cache.DistanceSquared(default)==30,"material reference bounds reset on close");
        var next=cache.Select(source,3,LTTerrainDiagnosticMode.Normal,false,out refreshed);
        renderer.sharedMaterial=farSource;cache.Dispose();
        Require(next.destroyed&&renderer.sharedMaterial==farSource,"do not overwrite external assignment");
        cache.SetRestoreMaterial(source);
        cache.Select(source,4,LTTerrainDiagnosticMode.Normal,false,out _);
        var warmed=cache.Select(farSource,4,LTTerrainDiagnosticMode.Normal,true,out _);
        content=LTTerrainDiagnosticMaterials.ContentRevision;
        cache.Select(source,4,LTTerrainDiagnosticMode.ForceFar,false,out _);
        renderer.sharedMaterial=cache.Select(farSource,4,LTTerrainDiagnosticMode.Normal,true,out _);
        Require(renderer.sharedMaterial==warmed&&warmed.IsKeywordEnabled("_LT_FAR_ONLY")&&LTTerrainDiagnosticMaterials.ContentRevision==content,"prewarmed force-far preserves baseline/content revision");
        cache.Dispose();Require(renderer.sharedMaterial==source,"forced far restores NATURAL near selection immediately");
        foreach(var mode in new[]{LTTerrainDiagnosticMode.TerrainLOD2,LTTerrainDiagnosticMode.TerrainLOD4})
        {
            var lod=cache.Select(source,4,mode,false,out _);
            Require(lod.GetFloat(tri)==source.GetFloat(tri)&&lod.GetFloat(other)==source.GetFloat(other)&&lod.GetFloat(baseOnly)==source.GetFloat(baseOnly),"terrain LOD mode keeps normal material");
        }
        cache.Dispose();
    }
    static void HeightPairChecks()
    {
        int id=Shader.PropertyToID("_LTFastLayerParams");
        foreach(float originalZ in new[]{0f,1f})foreach(float originalW in new[]{0f,1f,2f})
        {
            var source=new Material(new Shader());var initial=new Vector4(.03f,.025f,originalZ,originalW);source.SetVector(id,initial);
            source.EnableKeyword("_LT_FAST_LAYERS");source.EnableKeyword("_LT_FAST_ROADS");source.EnableKeyword("TESSELLATION_ON");
            if(originalZ==1&&originalW>=1)source.EnableKeyword("_LT_FAST_PREHEIGHT");
            if(originalZ==1&&originalW==2)source.EnableKeyword("_LT_FAST_COLOR_REMAINDER");
            var renderer=new MeshRenderer{sharedMaterial=source};var cache=new LTTerrainDiagnosticMaterials(renderer);
            renderer.sharedMaterial=cache.Select(source,1,LTTerrainDiagnosticMode.TopTwoAfterHeight,false,out _);
            var p=renderer.sharedMaterial.GetVector(id);int revision=LTTerrainDiagnosticMaterials.ContentRevision;
            Require(p.x==initial.x&&p.y==initial.y&&p.z==1&&p.w==0,"checkbox OFF: only pair selection changes; top-two forced on");
            for(int i=0;i<10;i++)
            {
                var mode=i%2==0?LTTerrainDiagnosticMode.TopTwoBeforeHeight:LTTerrainDiagnosticMode.TopTwoAfterHeight;
                renderer.sharedMaterial=cache.Select(source,1,mode,false,out _);p=renderer.sharedMaterial.GetVector(id);
                Require(p.x==initial.x&&p.y==initial.y&&p.z==1&&p.w==(i%2==0?1:0),"repeat toggles no parameter drift");
                Require(LTTerrainDiagnosticMaterials.LayerModeBound(renderer.sharedMaterial,mode),"actual binding verified");
                Require(renderer.sharedMaterial.IsKeywordEnabled("_LT_FAST_PREHEIGHT")== (i%2==0),"compiled specialization follows diagnostic arm");
                Require(!renderer.sharedMaterial.IsKeywordEnabled("_LT_FAST_COLOR_REMAINDER"),"color-only remainder excluded from both height A/B arms");
                Require(renderer.sharedMaterial.IsKeywordEnabled("_LT_FAST_ROADS")&&renderer.sharedMaterial.IsKeywordEnabled("TESSELLATION_ON"),"roads and geometry keywords retained");
                Require(LTTerrainDiagnosticMaterials.ContentRevision==revision,"A/B mode changes do not discard comparable results");
            }
            var copy=renderer.sharedMaterial;
            cache.Select(source,1,LTTerrainDiagnosticMode.TopTwoAfterHeight,true,out _);
            Require(copy.GetVector(id).Equals(initial),"natural far source parameters unchanged");
            cache.Dispose();Require(renderer.sharedMaterial==source&&source.GetVector(id).Equals(initial),"source and original checkbox restored exactly");
            Require(source.IsKeywordEnabled("_LT_FAST_PREHEIGHT")== (originalZ==1&&originalW>=1),"source specialization keyword restored");
            Require(source.IsKeywordEnabled("_LT_FAST_COLOR_REMAINDER")== (originalZ==1&&originalW==2),"source color-only keyword restored");
        }
        var unsupported=new Material(new Shader());unsupported.SetVector(id,new Vector4(0,0,1,1));
        Require(LTTerrainDiagnosticMaterials.CanCompareLayers(unsupported),"ordinary authored source is now comparable");
        unsupported.EnableKeyword("_LT_FAST_LAYERS");unsupported.EnableKeyword("_LT_FAR_ONLY");
        Require(!LTTerrainDiagnosticMaterials.CanCompareLayers(unsupported),"far-only keyword is not comparable");
        var missing=new Material(new Shader());missing.EnableKeyword("_LT_FAST_LAYERS");
        Require(!LTTerrainDiagnosticMaterials.CanCompareLayers(missing),"missing parameters rejected");
        var stale=new Material(new Shader());stale.EnableKeyword("_LT_FAST_LAYERS");stale.SetVector(id,new Vector4(0,0,1,1));
        Require(!LTTerrainDiagnosticMaterials.LayerModeBound(stale,LTTerrainDiagnosticMode.TopTwoBeforeHeight),"preheight parameters without compiled variant rejected");
        stale.EnableKeyword("_LT_FAST_PREHEIGHT");stale.SetVector(id,new Vector4(0,0,1,0));
        Require(!LTTerrainDiagnosticMaterials.LayerModeBound(stale,LTTerrainDiagnosticMode.TopTwoAfterHeight),"after-height parameters with stale compiled variant rejected");
        stale.SetVector(id,new Vector4(0,0,1,1));stale.EnableKeyword("_LT_FAST_COLOR_REMAINDER");
        Require(!LTTerrainDiagnosticMaterials.LayerModeBound(stale,LTTerrainDiagnosticMode.TopTwoBeforeHeight),"color remainder variant rejected from strict pair comparison");
    }
    static void FiveLayerChecks()
    {
        var modes=new[]{LTTerrainDiagnosticMode.LiveLayers,LTTerrainDiagnosticMode.FastLayers,LTTerrainDiagnosticMode.TopTwoAfterHeight,LTTerrainDiagnosticMode.TopTwoBeforeHeight,LTTerrainDiagnosticMode.TopTwoColorRemainder};
        int id=Shader.PropertyToID("_LTFastLayerParams"),other=Shader.PropertyToID("Other");
        foreach(bool authoredFast in new[]{false,true})foreach(bool roads in new[]{false,true})
        {
            var source=new Material(new Shader());var original=authoredFast?new Vector4(.03f,.025f,1,2):default;
            source.SetVector(id,original);source.SetFloat(other,42);source.EnableKeyword("TESSELLATION_ON");
            if(authoredFast){source.EnableKeyword("_LT_FAST_LAYERS");source.EnableKeyword("_LT_FAST_PREHEIGHT");source.EnableKeyword("_LT_FAST_COLOR_REMAINDER");}
            var renderer=new MeshRenderer{sharedMaterial=source};var cache=new LTTerrainDiagnosticMaterials(renderer);
            var settings=new Vector4(.08f,.06f,0,0);
            int content=-1;
            // Visit all transitions, not only 1 -> 2 -> 3 -> 4 -> 5.
            for(int from=0;from<5;from++)for(int to=0;to<5;to++)foreach(int index in new[]{from,to})
            {
                var material=cache.Select(source,1,modes[index],false,out _,settings,roads);renderer.sharedMaterial=material;
                var p=material.GetVector(id);
                Require(p.x==settings.x&&p.y==settings.y&&p.z==(index>=2?1:0)&&p.w==(index==4?2:index==3?1:0),"five-mode exact parameters from authored tolerances");
                Require(material.IsKeywordEnabled("_LT_FAST_LAYERS")== (index>0)&&material.IsKeywordEnabled("_LT_FAST_PREHEIGHT")== (index>=3)&&material.IsKeywordEnabled("_LT_FAST_COLOR_REMAINDER")== (index==4),"five-mode exact compiled keywords");
                Require(LTTerrainDiagnosticMaterials.LayerModeBound(material,modes[index]),"five-mode bound verification");
                Require(material.IsKeywordEnabled("_LT_FAST_ROADS")==roads&&material.IsKeywordEnabled("TESSELLATION_ON")&&material.GetFloat(other)==42,"ordinary-to-fast preserves chunk road needs and unrelated settings");
                if(content<0)content=LTTerrainDiagnosticMaterials.ContentRevision;
                Require(content==LTTerrainDiagnosticMaterials.ContentRevision,"mode changes preserve comparable content revision");
                Require(cache.Select(source,1,modes[index],false,out bool refreshed,settings,roads)==material&&!refreshed,"no repeated property copy for same mode");
            }
            settings.x=.07f;cache.Select(source,1,modes[4],false,out bool changed,settings,roads);
            Require(changed&&LTTerrainDiagnosticMaterials.ContentRevision>content,"authored tolerance change invalidates comparisons");
            var far=new Material(source);far.EnableKeyword("_LT_FAR_ONLY");
            foreach(var mode in modes)
            {
                var copy=cache.Select(far,1,mode,true,out _,settings,roads);
                Require(copy.IsKeywordEnabled("_LT_FAR_ONLY")&&copy.GetVector(id).Equals(original)&&copy.IsKeywordEnabled("_LT_FAST_COLOR_REMAINDER")==authoredFast,"all five modes leave far source semantics untouched");
            }
            cache.Dispose();
            Require(renderer.sharedMaterial==source&&source.GetVector(id).Equals(original)&&source.IsKeywordEnabled("_LT_FAST_LAYERS")==authoredFast&&source.IsKeywordEnabled("_LT_FAST_COLOR_REMAINDER")==authoredFast,"five-mode close restores authored source exactly");
        }
        var broken=new Material(new Shader());broken.SetVector(id,new Vector4(0,0,1,2));broken.EnableKeyword("_LT_FAST_LAYERS");broken.EnableKeyword("_LT_FAST_PREHEIGHT");
        Require(!LTTerrainDiagnosticMaterials.LayerModeBound(broken,LTTerrainDiagnosticMode.TopTwoColorRemainder),"mode 5 rejects missing color variant");
        Require(!LTTerrainDiagnosticMaterials.LayerModeBound(broken,LTTerrainDiagnosticMode.LiveLayers),"mode 1 rejects leftover fast keyword");
    }
    static LTWorld GeometryFixture()
    {
        var world=new LTWorld{name="Fixture terrain"};var chunk=new LTChunk{name="chunk",mesh=new Mesh{indices=new uint[]{300}}};
        chunk.lodMeshes=new[]{210u,120u,60u,30u}.Select(n=>new Mesh{indices=new[]{n}}).ToArray();
        chunk.gameObject.AddComponent<MeshFilter>().sharedMesh=chunk.mesh;
        chunk.gameObject.AddComponent<MeshRenderer>();world.generatedRoot.children.Add(chunk);return world;
    }
    static void GeometryChecks()
    {
        var world=GeometryFixture();var chunk=world.generatedRoot.GetComponentsInChildren<LTChunk>().Single();
        var scope=new LTTerrainFrameGeometry();var camera=Camera.main;
        var normal=scope.Read(camera);Require(normal.error==null&&normal.triangles==100&&normal.frustumTriangles==100,"actual mesh index counts");
        scope.SetMode(LTTerrainDiagnosticMode.TerrainLOD2);var lod2=scope.Read(camera);
        Require(lod2.error==null&&lod2.triangles==40&&world.FrameDiagnosticLOD==2&&world.forceLOD==-1,"separate terrain-only override; original Force LOD untouched");
        scope.SetMode(LTTerrainDiagnosticMode.TerrainLOD4);var lod4=scope.Read(camera);
        Require(lod4.error==null&&lod4.triangles==10&&world.TerrainForcedLOD==4,"switch 2 to 4 counts assigned geometry");
        Require(normal.fingerprint!=lod2.fingerprint&&lod2.fingerprint!=lod4.fingerprint,"mesh/index change fingerprint");
        scope.SetMode(LTTerrainDiagnosticMode.Normal);
        Require(world.FrameDiagnosticLOD==-1&&scope.Read(camera).triangles==100,"normal restores policy and geometry immediately");
        world.forceLOD=1;world.SetFrameDiagnosticLOD(3);scope.SetMode(LTTerrainDiagnosticMode.TerrainLOD4);scope.Dispose();scope.Dispose();
        Require(world.FrameDiagnosticLOD==3&&world.forceLOD==1&&scope.Read(camera).triangles==20,"idempotent exact previous override restoration, not hardcoded defaults");
        world.SetFrameDiagnosticLOD(-1);world.forceLOD=-1;
        scope.SetMode(LTTerrainDiagnosticMode.TerrainLOD2);chunk.lodsPending=true;
        Require(scope.Read(camera).error.Contains("pending"),"pending data cannot masquerade as requested LOD");chunk.lodsPending=false;
        world.enableLODs=false;Require(scope.Read(camera).error.Contains("disabled"),"disabled terrain LOD rejected");world.enableLODs=true;
        var meshes=chunk.lodMeshes;chunk.lodMeshes=Array.Empty<Mesh>();
        Require(scope.Read(camera).error.Contains("not prepared"),"missing LOD cannot report success");chunk.lodMeshes=meshes;
        world.UseSpatialLODs=true;world.spatialFailed=true;Require(scope.Read(camera).error.Contains("unavailable"),"spatial failure rejected");world.spatialFailed=false;
        chunk.ActiveSpatialLOD=new LTSpatialLODAsset{vertexBank=new Mesh(),patches=new[]{new LTSpatialLODPatch()}};chunk.currentLOD=0;
        Require(scope.Read(camera).error==null&&scope.Read(camera).triangles==40,"spatial currentLOD=0 is NOT triangle source");
        chunk.ActiveSpatialLOD.patches[0].levels=new object[2];
        Require(scope.Read(camera).error.Contains("not prepared"),"clamped spatial LOD rejected");chunk.ActiveSpatialLOD.patches[0].levels=new object[5];
        chunk.pinned=true;Require(scope.Read(camera).error.Contains("pinned"),"all fixed displacement chunks not a geometry test");chunk.pinned=false;
        var renderer=chunk.GetComponent<MeshRenderer>();renderer.bounds=new Bounds{outside=true};
        Require(scope.Read(camera).frustumTriangles==0&&scope.Read(camera).triangles==40,"frustum candidates separate from assigned chunks");renderer.bounds=default;
        camera.cullingMask=0;Require(scope.Read(camera).frustumTriangles==0,"camera culling mask respected");camera.cullingMask=-1;
        var assigned=chunk.GetComponent<MeshFilter>().sharedMesh;
        assigned.indices=new uint[]{30,60,80};assigned.topology=new[]{MeshTopology.Triangles,MeshTopology.Triangles,MeshTopology.Lines};
        Require(scope.Read(camera).triangles==30,"all triangle submeshes counted; lines excluded");
        var newWorld=GeometryFixture();Require(scope.Read(camera).error.Contains("override changed"),"world added during capture rejects partial force");
        Require(scope.Read(camera,true).error==null&&newWorld.FrameDiagnosticLOD==2,"new worlds included at next capture boundary");
        UnityEngine.Object.Destroy(world);scope.Dispose();Require(newWorld.FrameDiagnosticLOD==-1,"destroyed target safe; remaining worlds restored");UnityEngine.Object.Destroy(newWorld);
    }
    static void PacingChecks()
    {
        QualitySettings.vSyncCount=2;Application.targetFrameRate=45;
        var scope=new LTTerrainFramePacing();scope.Enable();scope.Enable();
        Require(QualitySettings.vSyncCount==0&&Application.targetFrameRate==-1,"uncapped runtime scope");
        scope.Dispose();scope.Dispose();
        Require(QualitySettings.vSyncCount==2&&Application.targetFrameRate==45,"idempotent exact cap restoration");
        QualitySettings.vSyncCount=0;Application.targetFrameRate=-1;
    }
    static void MetricChecks()
    {
        var metrics=new LTTerrainFrameMetrics();
        Unity.Profiling.ProfilerRecorder.Values["CPU Main Thread Frame Time"]=2_000_000;
        Unity.Profiling.ProfilerRecorder.Values["GPU Frame Time"]=0;
        Unity.Profiling.ProfilerRecorder.Values["Draw Calls Count"]=0;
        Unity.Profiling.ProfilerRecorder.Missing.Add("Triangles Count");
        var probe=Unity.Profiling.ProfilerRecorder.StartNew(default,"CPU Main Thread Frame Time",1);
        var copy=probe;
        Require(probe.IsRunning&&probe.Count==0,"recorder starts empty until frame end");
        Unity.Profiling.ProfilerRecorder.EndFrame();Require(probe.LastValue==2_000_000,"frame-end publication");
        copy.Reset();Unity.Profiling.ProfilerRecorder.EndFrame();
        Require(probe.Valid&&!probe.IsRunning&&probe.Count==0&&probe.LastValue==0,"Reset stops and clears shared handle (native contract)");
        probe.Dispose();copy.Dispose();Require(Unity.Profiling.ProfilerRecorder.live==0,"copied handle disposal does not double free");
        metrics.Begin();for(int i=0;i<10;i++){metrics.Poll(false);Unity.Profiling.ProfilerRecorder.EndFrame();}
        FrameTimingManager.data=new[]{new FrameTiming{frameStartTimestamp=20,cpuFrameTime=10,gpuFrameTime=7},new FrameTiming{frameStartTimestamp=10,cpuFrameTime=10,gpuFrameTime=8}};
        metrics.Poll(true);Unity.Profiling.ProfilerRecorder.EndFrame();metrics.Poll(true);
        var snapshot=metrics.Finish();
        Require(snapshot.native.Length==2&&snapshot.native[0].frameStartTimestamp==10&&snapshot.native[1].frameStartTimestamp==20,"native out-of-order records sorted and deduplicated");
        Require(snapshot.values[1]==2&&double.IsNaN(snapshot.values[3]),"ns to ms; zero GPU unavailable");
        Require(snapshot.hasCpuGpu&&snapshot.summary.Contains("recorder=running; buffered=1"),"continuous collection survives warmup; health reported");
        Require(snapshot.values[4]==0&&double.IsNaN(snapshot.values[7]),"zero draw calls valid; absent counter not zero");
        Require(snapshot.summary.Contains("valid=2/2")&&snapshot.summary.Contains("GPU Frame Time: N/A")&&snapshot.summary.Contains("Present/target-FPS wait (native): mean=0.000"),"report completeness and legitimate zero wait");
        Require(Unity.Profiling.ProfilerRecorder.live==0,"finish frees every recorder");
        metrics.Begin();metrics.Poll(true);snapshot=metrics.Finish();
        Require(snapshot.native.Length==0&&snapshot.values.All(double.IsNaN),"startup delayed samples excluded; no cross-capture arrays");
        Require(!snapshot.hasCpuGpu&&snapshot.summary.Contains("WARNING: no valid CPU/GPU"),"missing timings visibly warned, not a successful GPU capture");
        metrics.Begin();FrameTimingManager.fail=true;metrics.Poll(true);snapshot=metrics.Finish();FrameTimingManager.fail=false;
        Require(snapshot.summary.Contains("Native timing unavailable"),"unsupported native timing reported without breaking capture");
        metrics.Begin();metrics.Dispose();metrics.Dispose();Require(Unity.Profiling.ProfilerRecorder.live==0,"cancel disposal idempotent");
        Require(LTTerrainFrameMetrics.Snapshot.Statistics(new[]{0d,1,2,3,double.NaN},"ms").Contains("valid=4/5"),"partial coverage denominator includes missing samples");
        FrameTimingManager.data=Array.Empty<FrameTiming>();Unity.Profiling.ProfilerRecorder.Values.Clear();Unity.Profiling.ProfilerRecorder.Missing.Clear();
    }
    static void ShadowChecks()
    {
        var a=new Camera();var data=a.gameObject.AddComponent<HDAdditionalCameraData>();
        var fields=new[]{FrameSettingsField.ShadowMaps,FrameSettingsField.ContactShadows,FrameSettingsField.ScreenSpaceShadows,FrameSettingsField.Shadowmask};
        foreach(var field in fields)data.renderingPathCustomFrameSettings.SetEnabled(field,true);
        data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.SSAO]=true;
        data.customRenderingSettings=false; // dormant SSAO override must stay dormant
        var b=new Camera();var custom=b.gameObject.AddComponent<HDAdditionalCameraData>();custom.customRenderingSettings=true;
        custom.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.SSAO]=true;
        custom.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.ShadowMaps,true);
        custom.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.ShadowMaps]=true;
        var light=new Light{shadows=LightShadows.Soft,intensity=3};
        var lightData=light.gameObject.AddComponent<HDAdditionalLightData>();
        lightData.useContactShadow.useOverride=false;lightData.useContactShadow.@override=true;lightData.useContactShadow.level=2;
        var hard=new Light{shadows=LightShadows.Hard};var none=new Light{shadows=LightShadows.None};
        var scope=new LTTerrainFrameShadows();scope.Enable();
        Require(scope.Enabled&&light.shadows==LightShadows.None&&hard.shadows==LightShadows.None,"all existing lights shadows disabled");
        Require(light.enabled&&light.intensity==3,"lights are NOT switched off");
        Require(lightData.useContactShadow.useOverride&&!lightData.useContactShadow.@override&&lightData.useContactShadow.level==2,"contact shadows off without changing quality level");
        foreach(var field in fields)Require(!data.renderingPathCustomFrameSettings.IsEnabled(field)&&data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field],"HDRP shadow feature forced off: "+field);
        Require(data.customRenderingSettings&&!data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.SSAO],"dormant non-shadow mask not activated");
        Require(custom.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.SSAO],"active non-shadow overrides preserved");
        scope.Enable(); // repeated entry must not snapshot the suppressed state
        int finds=UnityEngine.Object.findCalls;
        RenderPipelineManager.Render(a,b);RenderPipelineManager.Render(a,b);
        Require(finds==UnityEngine.Object.findCalls,"no scene-wide scans during each rendered frame");
        var newCamera=new Camera();RenderPipelineManager.Render(newCamera);
        var added=newCamera.GetComponent<HDAdditionalCameraData>();
        Require(added&&!added.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.ShadowMaps),"new rendered camera covered before HDRP aggregation");
        var newLight=new Light{shadows=LightShadows.Hard};UnityEngine.SceneManagement.SceneManager.Load();
        Require(newLight.shadows==LightShadows.None,"additive scene lights covered");
        light.shadows=LightShadows.Hard;RenderPipelineManager.Render(a);
        Require(light.shadows==LightShadows.None,"runtime writer cannot re-enable tracked light shadows mid-test");
        custom.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.SSAO,true);
        scope.Dispose();
        Require(!scope.Enabled&&light.shadows==LightShadows.Soft&&hard.shadows==LightShadows.Hard&&none.shadows==LightShadows.None&&newLight.shadows==LightShadows.Hard,"exact per-light values restored");
        Require(!lightData.useContactShadow.useOverride&&lightData.useContactShadow.@override&&lightData.useContactShadow.level==2,"contact settings restored exactly");
        Require(!data.customRenderingSettings&&data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.SSAO],"custom off and dormant mask restored");
        foreach(var field in fields)Require(data.renderingPathCustomFrameSettings.IsEnabled(field)&&!data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field],"HDRP values and override bits restored: "+field);
        Require(custom.customRenderingSettings&&custom.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.SSAO)&&custom.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.ShadowMaps),"restore shadows without reverting unrelated runtime change");
        Require(added.destroyed&&!newCamera.destroyed&&!data.destroyed,"only temporary additional camera component destroyed");
        scope.Dispose();light.shadows=LightShadows.Hard;RenderPipelineManager.Render(a);
        Require(light.shadows==LightShadows.Hard,"callback detached on dispose");
        scope.Enable();UnityEngine.Object.Destroy(light);UnityEngine.Object.Destroy(data);scope.Dispose();
        Require(!scope.Enabled,"destroyed lights/cameras safe during restoration");
        GameObject.failAddComponent=true;var missing=new Camera();scope.Enable();GameObject.failAddComponent=false;
        Require(!scope.Enabled&&hard.shadows==LightShadows.Hard&&newLight.shadows==LightShadows.Hard,"partial setup failure rolls back and disables test");
    }
    static void ControllerChecks()
    {
        var world=GeometryFixture();
        var controller=new LTTerrainFrameDiagnostic();Call(controller,"OnEnable");
        QualitySettings.vSyncCount=1;Application.targetFrameRate=75;
        var camera=Camera.main;camera.transform.position=default;camera.isActiveAndEnabled=true;
        int parameters=Shader.PropertyToID("_LTFastLayerParams");
        var renderer=new MeshRenderer();var source=new Material(new Shader());
        source.EnableKeyword("_LT_FAST_LAYERS");source.SetVector(parameters,new Vector4(.03f,.025f,1,1));
        var cache=new LTTerrainDiagnosticMaterials(renderer);
        var shadowLight=new Light{shadows=LightShadows.Soft};
        void Press(Key key){key.wasPressedThisFrame=true;Call(controller,"Update");key.wasPressedThisFrame=false;}
        void Frame(double step=.1,int near=2,int unpaired=0)
        {
            Unity.Profiling.ProfilerRecorder.EndFrame();
            Time.frameCount++;Time.realtimeSinceStartupAsDouble+=step;
            if(LTTerrainFrameDiagnostic.Active)
            {
                renderer.sharedMaterial=cache.Select(source,1,LTTerrainFrameDiagnostic.Mode,false,out _);
                LTTerrainFrameDiagnostic.Observe(camera,near,1,0,paired:near,unpaired:unpaired);
                RenderPipelineManager.End(camera);
            }
            Call(controller,"Update");
        }
        var input=Keyboard.current;
        Press(input.f8Key);Require(LTTerrainFrameDiagnostic.Active,"F8 opens in Input System");Frame();
        Require(LTTerrainFrameDiagnostic.Mode==LTTerrainDiagnosticMode.LiveLayers&&!renderer.sharedMaterial.IsKeywordEnabled("_LT_FAST_LAYERS"),"F8 starts with ordinary layers");
        Require(QualitySettings.vSyncCount==0&&Application.targetFrameRate==-1,"F8 removes Unity presentation caps");
        var results=Field<Array>(controller,"results");
        Require(results.Length==5,"exactly five result slots");
        var keys=new[]{input.digit1Key,input.digit2Key,input.digit3Key,input.digit4Key,input.digit5Key};
        var modes=new[]{LTTerrainDiagnosticMode.LiveLayers,LTTerrainDiagnosticMode.FastLayers,LTTerrainDiagnosticMode.TopTwoAfterHeight,LTTerrainDiagnosticMode.TopTwoBeforeHeight,LTTerrainDiagnosticMode.TopTwoColorRemainder};
        for(int index=0;index<5;index++)
        {
            Press(keys[index]);Frame();Require(LTTerrainFrameDiagnostic.Mode==modes[index]&&LTTerrainDiagnosticMaterials.LayerModeBound(renderer.sharedMaterial,modes[index]),"keys 1..5 select actual requested modes");
            Call(controller,"OnGUI");int guiCalls=GUI.calls;string panel=Field<string>(controller,"panel");
            Press(input.f9Key);for(int i=0;i<10;i++)Frame();Call(controller,"OnGUI");
            Require(GUI.calls==guiCalls&&Field<string>(controller,"panel")==panel,"warmup has no GUI rendering or variable text refresh");
            for(int i=0;i<15;i++)Frame();Call(controller,"OnGUI");
            Require(Field<LTTerrainFrameSamples>(controller,"samples").Sampling&&GUI.calls==guiCalls&&Field<string>(controller,"panel")==panel,"recording has no GUI rendering or variable text refresh");
            for(int i=0;i<55;i++)Frame();Call(controller,"OnGUI");
            Require(GUI.calls>guiCalls,"panel returns after capture");
            Require(Enumerable.Range(0,index+1).All(i=>results.GetValue(i)!=null),"same camera retains every completed mode");
        }
        foreach(var key in new[]{input.digit6Key,input.digit7Key})
        {Press(key);Frame();Require(LTTerrainFrameDiagnostic.Mode==LTTerrainDiagnosticMode.TopTwoColorRemainder,"old shadow/LOD number shortcuts inactive");}
        Require(world.FrameDiagnosticLOD==-1&&world.forceLOD==-1&&shadowLight.shadows==LightShadows.Soft,"no LOD or shadow side effects");
        Require(source.GetVector(parameters).w==1,"source checkbox never written");
        var output=Path.GetFullPath("Logs/TerrainDiagnosticChecks");Application.persistentDataPath=output;
        Press(input.f10Key);
        var report=Directory.GetFiles(Path.Combine(output,"TerrainDiagnostics"),"*.txt").OrderBy(File.GetLastWriteTimeUtc).Last();
        string text=File.ReadAllText(report),csv=File.ReadAllText(Path.ChangeExtension(report,"csv"));
        Require(text.Contains("v6")&&text.Contains("NOT GPU")&&text.Contains("fast=0; topTwo=0")&&text.Contains("beforeHeight=1; colorRemainder=1")&&text.Contains("verifiedMultilayerChunks=2"),"report identifies all five actual bindings/context");
        Require(Enumerable.Range(1,5).All(i=>csv.Contains($"\n{i},0,"))&&!csv.Contains("\n6,"),"export all five modes with stable 1-based ids");
        Require(!text.Contains("Mode 2 uses the base layer")&&!text.Contains("Only two modes")&&text.Contains("Mode 5:"),"no obsolete mode descriptions");
        Require(File.Exists(report.Replace(".txt","-counters.csv"))&&File.Exists(report.Replace(".txt","-timings.csv"))&&text.Contains("CPU Main Thread Frame Time")&&text.Contains("displayRefreshHz=180"),"metrics exported separately");
        Press(input.digit1Key);Frame();
        Press(input.f9Key);for(int i=0;i<25;i++)Frame();
        world.generatedRoot.GetComponentsInChildren<LTChunk>()[0].mesh.indices[0]=600;
        for(int i=0;i<55;i++)Frame();
        Require(Field<string>(controller,"message").Contains("меши/настройки"),"geometry changes during recording cancel");
        world.generatedRoot.GetComponentsInChildren<LTChunk>()[0].mesh.indices[0]=300;
        Press(input.f7Key);Require(results.Cast<object>().All(x=>x==null),"changing pacing clears incomparable results");
        Require(QualitySettings.vSyncCount==1&&Application.targetFrameRate==75,"F7 restores custom limits");
        Press(input.f7Key);
        Press(input.f9Key);Frame();camera.transform.position=new Vector3{x=1};Frame();
        Require(!Field<LTTerrainFrameSamples>(controller,"samples").Running,"movement cancels");
        Press(input.f9Key);Require(results.Cast<object>().All(x=>x==null),"changed camera clears comparison");
        for(int i=0;i<25;i++)Frame();
        source.SetFloat(Shader.PropertyToID("Other"),5);cache.Select(source,99,LTTerrainFrameDiagnostic.Mode,false,out _);Frame();
        Require(!Field<LTTerrainFrameSamples>(controller,"samples").Running,"material mutation cancels sampling");
        Press(input.f9Key);Frame();ScalableBufferManager.widthScaleFactor=.5f;Frame();
        Require(!Field<LTTerrainFrameSamples>(controller,"samples").Running,"dynamic resolution cancels");ScalableBufferManager.widthScaleFactor=1;
        Press(input.f9Key);Frame();DynamicResolutionHandler.instance.scale=.75f;Frame();
        Require(!Field<LTTerrainFrameSamples>(controller,"samples").Running,"HDRP software scaling cancels");DynamicResolutionHandler.instance.scale=1;Frame();
        Press(input.f9Key);Frame();camera.nonJitteredProjectionMatrix=new Matrix4x4{first=2};Frame();
        Require(!Field<LTTerrainFrameSamples>(controller,"samples").Running,"projection change cancels");
        Press(input.f9Key);Frame();Call(controller,"OnApplicationFocus",false);
        Require(!Field<LTTerrainFrameSamples>(controller,"samples").Running,"focus loss cancels");
        Require(Unity.Profiling.ProfilerRecorder.live==0,"focus loss releases metric recorders");
        Press(input.f9Key);for(int i=0;i<30;i++)Frame(near:0);
        Require(!Field<LTTerrainFrameSamples>(controller,"samples").Running&&Field<string>(controller,"message").Contains("неприменим"),"base/far-only is not a valid pair test");
        Press(input.f9Key);for(int i=0;i<30;i++)Frame(unpaired:1);
        Require(!Field<LTTerrainFrameSamples>(controller,"samples").Running,"mixed supported/unsupported chunks cancel");
        Press(input.f9Key);camera.isActiveAndEnabled=false;Frame();
        Require(!Field<LTTerrainFrameSamples>(controller,"samples").Running,"disabled camera cancels");camera.isActiveAndEnabled=true;
        Press(input.digit2Key);Frame();Press(input.f8Key);
        Require(!LTTerrainFrameDiagnostic.Active&&renderer.sharedMaterial==source&&source.GetVector(parameters).w==1,"F8 restores exact source checkbox");
        Require(QualitySettings.vSyncCount==1&&Application.targetFrameRate==75,"F8 restores caps");
        Press(input.f8Key);Frame();Call(controller,"OnDisable");
        Require(renderer.sharedMaterial==source&&!LTTerrainFrameDiagnostic.Active,"disable restores material");
        Require(QualitySettings.vSyncCount==1&&Application.targetFrameRate==75,"disable restores caps");
        Call(controller,"OnEnable");Press(input.f8Key);Frame();
        typeof(LTTerrainFrameDiagnostic).GetMethod("ResetStatics",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
        Require(renderer.sharedMaterial==source&&!LTTerrainFrameDiagnostic.Active,"subsystem reset restores (domain reload disabled)");
        Require(Unity.Profiling.ProfilerRecorder.live==0,"disable/reload releases all recorders");
        Require(QualitySettings.vSyncCount==1&&Application.targetFrameRate==75&&shadowLight.shadows==LightShadows.Soft&&world.FrameDiagnosticLOD==-1,"no lingering caps/shadow/LOD edits");
        UnityEngine.Object.Destroy(world);
    }
    static void SourceChecks()
    {
        string root="Assets/TerrainSystem/LocalTerrain/";
        string controller=File.ReadAllText(root+"LTTerrainFrameDiagnostic.cs"),materials=File.ReadAllText(root+"LTTerrainDiagnosticMaterials.cs");
        foreach(var forbidden in new[]{"Shader.SetGlobal","MaterialPropertyBlock","UnityEditor","EditorSceneManager","world.triplanarTexturing=","QualitySettings.vSyncCount=","Application.targetFrameRate=","SaveAssets(","SaveScene(","Rebuild("})
            Require(!controller.Contains(forbidden)&&!materials.Contains(forbidden),"no invasive API: "+forbidden);
        string globals=File.ReadAllText(root+"LTPaintGlobals.cs"),runtime=File.ReadAllText(root+"LTPaintRuntime.cs");
        string world=File.ReadAllText(root+"LTWorld.cs"),geometry=File.ReadAllText(root+"LTTerrainFrameGeometry.cs"),worldScope=File.ReadAllText(root+"LTTerrainFrameWorld.cs");
        Require(world.Contains("int terrainForce=TerrainForcedLOD;")&&world.Contains("if(terrainForce>=0)level=terrainForce;")&&world.Contains("int level=fixedLOD?0:TerrainForcedLOD>=0?TerrainForcedLOD:"),"real whole/spatial terrain selectors use separate override");
        Require(world.Substring(world.IndexOf("using(LodRoadMarker.Auto())")).Contains("if(forceLOD>=0)level=forceLOD;"),"road selector keeps original Force LOD");
        Require(!geometry.Contains(".forceLOD=")&&!worldScope.Contains("forceLOD="),"no serialized LOD writes");
        Require(geometry.Contains("GetIndexCount(sub)")&&!geometry.Contains(".triangles;")&&!geometry.Contains(".vertices;"),"count actual indices without triangle/vertex array reads");
        Require(!File.ReadAllText(root+"LTTerrainFrameMetrics.cs").Contains("recorders[i].Reset()"),"continuous native recorders: never reset per Update");
        Require(globals.Contains("if(state.coverageActive&&!layerComparison){state.diagnosticMaterials?.Dispose();diagnosticSkipped++;}"),"only layer fragment overrides allowed on geometry chunks");
        Require(globals.Contains("LayerModeBound(selected,LTTerrainFrameDiagnostic.Mode)")&&globals.Contains("selected.GetFloat(\"_LTBaseOnly\")<.5f"),"verify actual non-base-only bindings");
        Require(globals.Contains("globalWorld.fastLayerBlendBudget")&&globals.Contains("globalWorld.fastLayerProjectionCutoff")&&globals.Contains("roadProjections.TryGetValue(state,out _)||asphaltStates.TryGetValue(state,out _)"),"authored tolerances and road requirements supplied even from ordinary mode");
        Require(!controller.Contains("shadows.Enable")&&!controller.Contains("geometry.SetMode")&&!controller.Contains("digit6Key")&&!controller.Contains("Alpha6"),"five-mode F8 has no old shadow/LOD shortcuts");
        foreach(int digit in Enumerable.Range(1,5))Require(controller.Contains($"digit{digit}Key")&&controller.Contains($"Alpha{digit}"),"all five shortcuts in both input backends");
        Require(!controller.Contains("fastLayerTopTwoBeforeHeight=")&&!materials.Contains("fastLayerTopTwoBeforeHeight="),"authored checkbox unchanged");
        Require(globals.Contains("if(refreshed)BindRoadProjection(selected,state);"),"projection bindings restored on copy refresh");
        Require(globals.Contains("if(globalsReady&&state.farMaterial)")&&globals.Contains("SetRestoreMaterial(selected)")&&globals.Contains("if(farRefreshed)BindRoadProjection(diagnosticFarMaterial,state);"),"far atlas readiness, restore and projection guards");
        Require(runtime.Contains("!(state.diagnosticMaterials?.Owns(renderer.sharedMaterial)??false)"),"Tick does not capture diagnostic as original");
        Require(runtime.Split("state.diagnosticMaterials?.Dispose();").Length-1==2,"release and save paths both detach diagnostics");
        Require(controller.Contains("#if ENABLE_INPUT_SYSTEM")&&controller.Contains("#elif ENABLE_LEGACY_INPUT_MANAGER"),"new input first, legacy guarded");
        string shadow=File.ReadAllText(root+"LTTerrainFrameShadows.cs");
        Require(shadow.Contains("RenderPipelineManager.beginContextRendering+=BeforeContext")&&shadow.Contains("RenderPipelineManager.beginContextRendering-=BeforeContext"),"shadow overrides precede HDRP frame aggregation and unsubscribe");
        foreach(var forbidden in new[]{"QualitySettings.","Shader.SetGlobal","VolumeProfile","sharedProfile","SetDirty(","SaveScene(","SaveAssets("})
            Require(!shadow.Contains(forbidden),"no shadow asset/quality mutation: "+forbidden);
    }
}
