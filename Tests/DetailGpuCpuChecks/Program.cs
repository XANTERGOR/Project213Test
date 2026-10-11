using System.Globalization;
using LocalTerrainPrototype;
using UnityEngine;
using UnityEngine.Rendering;

static partial class Program
{
    static int checks,comparedCommands,frame;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    sealed class Source:ILTDetailGpuSource
    {
        public LTDetailPrefab Recipe{get;set;}public LTDetailEntry Entry{get;set;}=new();
        public Bounds Bounds=>new(new Vector3(id,0,5),new Vector3(3,3,3));public int Count{get;set;}=7;public int id;public bool fail;
        public void CopyGpuData(int start,int count,LTDetailGpuData[] data)
        {
            if(fail)throw new InvalidOperationException("injected copy failure");
            for(int i=0;i<count;i++)data[i]=new LTDetailGpuData{objectToWorld=new(){token=id+1},worldToObject=new(){token=id+2},
                positionSize=new(id+i+start,0,5,1),lodPositionRandom=new(0,0,5,.3f),center=new(id,1,5,0),extents=new(.1f,.2f,.3f,0)};
        }
    }
    static Material Material()
    {
        var m=new Material(Resources.Source){enableInstancing=true};m.SetFloat("_AddPrecomputedVelocity",0);m.SetFloat(LTDetailPrefab.Part.GrassRootProperty,0);return m;
    }
    static LTDetailPrefab Recipe(Material material,int levels=2)
    {
        var r=new LTDetailPrefab{hasLODGroup=levels>1};
        for(int lod=0;lod<levels;lod++)
        {
            var level=new LTDetailPrefab.Level{screenRelativeHeight=.5f/(lod+1)};r.levels.Add(level);
            for(int repeat=0;repeat<2;repeat++)for(int mode=0;mode<4;mode++)level.parts.Add(new(){material=material,mesh=new(){name="mesh-"+lod+"-"+mode},
                submesh=repeat,localMatrix=new(){token=10+repeat},castShadows=(ShadowCastingMode)mode,lightProbes=LightProbeUsage.Off,
                receiveShadows=true,layer=mode%2,renderingLayerMask=(uint)(2+repeat),reflectionProbes=ReflectionProbeUsage.BlendProbes,
                motionVectors=repeat==0?MotionVectorGenerationMode.Object:MotionVectorGenerationMode.ForceNoMotion,
                grassRootInPart=new Vector3(1,2,3)});
        }
        return r;
    }
    static readonly Plane[] Planes={new(new(1,0,0),10),new(new(-1,0,0),20),new(new(0,1,0),30),new(new(0,-1,0),40),new(new(0,0,1),50),new(new(0,0,-1),60)};
    sealed record Result(string[] events,bool[] accepted,int draws,int dispatch,int fallback,string report,long reserved,int uniformWrites,int materialReads,
        int copies,int passWrites,int instancingWrites,int timeReads,int terrainWrites);
    sealed class Pair:IDisposable
    {
        public readonly LegacyDetailGpuRenderer old=new();public readonly LTDetailGpuRenderer next=new();
        public Pair(){old.GrassTerrain=new();next.GrassTerrain=new();}
        public Result Pass(bool modern,Source[] sources,Camera camera,bool prepared=true,Action<int> edit=null)
        {
            Unity.Profiling.ProfileTrace.Reset();
            Trace.Reset();var accepted=new bool[sources.Length];
            if(modern){next.Begin(64,++frame,1000000,16);if(prepared)next.PrepareCameraPass(camera,Planes);}
            else old.Begin(64,++frame,1000000,16);
            for(int i=0;i<sources.Length;i++){edit?.Invoke(i);accepted[i]=modern?next.TryRender(sources[i],camera,Planes,177):old.TryRender(sources[i],camera,Planes,177);}
            Check(Unity.Profiling.ProfileTrace.Depth==0,"profiling balanced across early returns/upload failures");
            if(modern)
            {
                Check(Unity.Profiling.ProfileTrace.Calls("DrawSubmit")==next.DrawCalls,"one submission scope per draw");
                Check(Unity.Profiling.ProfileTrace.Calls("Draw")==next.DrawCalls,"one draw preparation scope per draw");
                Check(Unity.Profiling.ProfileTrace.Calls("CopyCount")==next.DrawCalls,"one count copy scope per draw");
                Check(Unity.Profiling.ProfileTrace.Calls("MaterialAdapt")==next.DrawCalls,"material adaptation included per draw");
                Check(Unity.Profiling.ProfileTrace.Calls("MaterialSync")==UnityEngine.Material.Copies,"one synchronization per actual copy");
                Check(Unity.Profiling.ProfileTrace.Calls("MaterialCopy")==UnityEngine.Material.Copies,"copy operation separately scoped");
                Check(Unity.Profiling.ProfileTrace.Calls("MaterialPasses")==UnityEngine.Material.Copies,"pass-state synchronization separately scoped");
                Check(Unity.Profiling.ProfileTrace.Calls("CullSubmit")==next.CullDispatches,"one scope per shared-selection dispatch");
                Check(Unity.Profiling.ProfileTrace.Calls("CameraParameters")==next.CameraParameterUploads,"actual camera parameter uploads scoped");
            }
            Check(modern?next.CheckMemoryAccounting(out _):old.CheckMemoryAccounting(out _),"owned buffer accounting");
            return new(Trace.Events.ToArray(),accepted,modern?next.DrawCalls:old.DrawCalls,modern?next.CullDispatches:old.CullDispatches,
                modern?next.FallbackGroups:old.FallbackGroups,modern?next.GetFallbackReport():old.GetFallbackReport(),
                modern?next.ReservedBytes:old.ReservedBytes,Trace.UniformWrites,UnityEngine.Material.NativeReads,
                UnityEngine.Material.Copies,UnityEngine.Material.PassWrites,UnityEngine.Material.InstancingWrites,Time.Reads,MaterialPropertyBlock.TerrainWrites);
        }
        public (Result before,Result after) Compare(Source[] sources,Camera camera,bool prepared=true,Action<int> edit=null,bool notificationMode=false)
        {
            var a=Pass(false,sources,camera,prepared,edit);var b=Pass(true,sources,camera,prepared,edit);
            Check(a.accepted.SequenceEqual(b.accepted),"fallback/readiness parity");
            Check(a.draws==b.draws&&a.dispatch==b.dispatch,"draw/dispatch count parity");
            Check(a.fallback==b.fallback&&a.report==b.report,"fallback reasons and attribution parity");
            Check(a.reserved==b.reserved,"reservation parity");
            Check(notificationMode?b.copies<=a.copies:a.copies==b.copies,notificationMode?
                "explicit notification mode may skip copies without changing commands":"full material copies retained per pass (no cross-frame stale cache)");
            Check(a.events.Length==b.events.Length,"command length parity");
            for(int i=0;i<a.events.Length;i++)Check(a.events[i]==b.events[i],"dispatch/draw command payload mismatch at "+i);
            comparedCommands+=a.events.Length;
            return(a,b);
        }
        public void Warm(Source[] sources,Camera camera)
        {
            foreach(var s in sources)
            {
                bool ready=false;for(int i=0;i<100&&!ready;i++){var result=Compare(new[]{s},camera);ready=result.after.accepted[0];}
                Check(ready,"warmup completed");
            }
        }
        public void Dispose(){old.Dispose();next.Dispose();Check(old.ResidentBytes==0&&next.ResidentBytes==0,"pair released owned buffers");}
    }
    static object Field(object instance,string name)=>instance.GetType().GetField(name,
        System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(instance);
    static Material Adapt(LTDetailGpuRenderer renderer,Material material,bool prepared=true)=>(Material)typeof(LTDetailGpuRenderer)
        .GetMethod("Adapt",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)
        .Invoke(renderer,new object[]{material,prepared});
    static void MaterialAdaptChecks()
    {
        // Do not bake a guessed native CopyProperties pass/instancing behavior into
        // the proof. Both copying and preserving target flags must produce parity.
        foreach(bool copyPasses in new[]{false,true})foreach(bool copyInstancing in new[]{false,true})
        {
            UnityEngine.Material.CopyPassState=copyPasses;UnityEngine.Material.CopyInstancingState=copyInstancing;
            using var pair=new Pair();var m=Material();var other=Material();other.SetFloat("arbitrary",97);
            var recipe=Recipe(m);recipe.levels[0].parts[0].material=other;
            var sources=new[]{new Source{Recipe=recipe},new Source{Recipe=recipe}};
            var camera=new Camera{name="material-check"};pair.Warm(sources,camera);
            var warm=pair.Compare(sources,camera);
            Check(warm.after.copies==2&&warm.before.copies==2,"two materials synchronized exactly once, including warm passes");
            Check(warm.after.passWrites==0&&warm.after.instancingWrites==0,"warm adapters have no redundant state writes");
            Check(warm.before.passWrites==12&&warm.before.instancingWrites==2,"legacy fixture exercises removed writes");
            Check(warm.after.timeReads<warm.before.timeReads,"prepared material hits avoid native time reads/touch writes");
            foreach(var pass in new[]{"MotionVectors","MOTIONVECTORS","ShadowCaster","DepthOnly","GBuffer","Forward"})
            {
                m.SetShaderPassEnabled(pass,false);var changed=pair.Compare(sources,camera);
                Check(changed.after.passWrites==(copyPasses?0:1),"one changed flag is applied after native property copy");
                Check(pair.Compare(sources,camera).after.passWrites==0,"unchanged disabled pass needs no write");
                m.SetShaderPassEnabled(pass,true);pair.Compare(sources,camera);
            }
            for(int i=0;i<5;i++){m.SetFloat("arbitrary",-5+i);other.SetFloat("arbitrary",40+i);pair.Compare(sources,camera);}
            // Unprepared callers keep the original once-per-Begin copy semantics,
            // even if they mutate a material between individual TryRender calls.
            pair.Compare(sources,camera,false,i=>{m.SetFloat("arbitrary",500+i);Time.realtimeSinceStartupAsDouble=i*2;});
            var direct=pair.Compare(sources,camera,false);
            Check(direct.after.timeReads==direct.before.timeReads,"direct callers preserve per-draw lifetime touches");
            pair.Compare(sources,new Camera{name="material-check-B"});pair.Compare(sources,camera);
        }
        UnityEngine.Material.CopyPassState=false;UnityEngine.Material.CopyInstancingState=false;
        Time.realtimeSinceStartupAsDouble=100;
        using(var gpu=new LTDetailGpuRenderer())
        {
            var m=Material();gpu.Begin(64,++frame,10000,16);
            UnityEngine.Material.FailNextCopy=true;bool failed=false;
            try{Adapt(gpu,m);}catch(System.Reflection.TargetInvocationException ex){failed=ex.InnerException is InvalidOperationException;}
            Check(failed&&Unity.Profiling.ProfileTrace.Depth==0,"copy failure does not leak profiling scopes");
            Check(((Dictionary<Material,Material>)Field(gpu,"updated")).Count==0,"failed sync never published in pass cache");
            var adapted=Adapt(gpu,m);Check(adapted.enableInstancing,"failed copy can retry in the same pass");
            Trace.Reset();Check(ReferenceEquals(adapted,Adapt(gpu,m))&&Time.Reads==0&&UnityEngine.Material.Copies==0,
                "warm prepared hit returns same adapter without native state access");
            m.SetFloat("arbitrary",9);gpu.Begin(64,++frame,10000,16);Adapt(gpu,m);
            Check(adapted.GetFloat("arbitrary")==9,"next Begin refreshes arbitrary properties without dirty notifications");
            var replacement=new Shader{name="replacement-indirect"};
            ((Dictionary<Shader,Shader>)Field(gpu,"adapters"))[Resources.Source]=replacement;
            gpu.Begin(64,++frame,10000,16);Adapt(gpu,m);Check(adapted.shader==replacement,"adapter shader replacement observed next pass");
            Time.realtimeSinceStartupAsDouble=104;Adapt(gpu,m,false);
            Time.realtimeSinceStartupAsDouble=108;gpu.Begin(64,++frame,10000,16);
            Check(gpu.MaterialCount==1&&adapted,"direct-call late touch preserves active adapter");
            Time.realtimeSinceStartupAsDouble=110;gpu.Begin(64,++frame,10000,16);
            Check(gpu.MaterialCount==0&&!adapted&&((Dictionary<Material,Material>)Field(gpu,"updated")).Count==0,
                "unused adapter expires and pass cache is empty before reuse");
            var fresh=Adapt(gpu,m);Check(!ReferenceEquals(fresh,adapted),"expired adapter recreated rather than cached dead handle");
            UnityEngine.Object.DestroyImmediate(m);gpu.Begin(64,++frame,10000,16);
            Check(gpu.MaterialCount==0&&!fresh,"destroyed source releases owned adapter");
            Adapt(gpu,Material());gpu.Dispose();
            Check(gpu.MaterialCount==0&&((Dictionary<Material,Material>)Field(gpu,"updated")).Count==0,"Dispose clears adapted handles");
            gpu.Begin(64,++frame,10000,16);Check(Adapt(gpu,Material()),"renderer can initialize again after Dispose");
        }
        Time.realtimeSinceStartupAsDouble=0;
    }
    static void LiveNotificationCommandChecks()
    {
        using var pair=new Pair();var material=Material();var foreign=Material();
        var recipe=Recipe(material);recipe.levels[0].parts[0].material=foreign;
        var sources=new[]{new Source{Recipe=recipe},new Source{Recipe=recipe}};
        var a=new Camera{name="live-cache-A"};var b=new Camera{name="live-cache-B"};
        pair.Warm(sources,a);pair.Warm(sources,b);
        pair.next.ConfigureMaterialNotifications(true,new[]{material});
        pair.Compare(sources,a,notificationMode:true);
        for(int i=0;i<16;i++)
        {
            foreign.SetFloat("arbitrary",40+i); // no notification required for foreign materials
            var warm=pair.Compare(sources,i%2==0?a:b,notificationMode:true);
            Check(warm.before.copies==2&&warm.after.copies==1,"live render skips only opted-in source, including second camera");
            LTDetailMaterialNotifications.Mutate(material,m=>m.SetFloat("arbitrary",100+i));
            Check(pair.Compare(sources,a,notificationMode:true).after.copies==2,"notified render refreshes opted-in material");
        }
        material.renderQueue=3000;LTDetailMaterialNotifications.NotifyChanged(material);
        Check(pair.Compare(sources,a,notificationMode:true).after.fallback==2,"transparent edit preserves fallback");
        material.renderQueue=2000;material.enableInstancing=false;LTDetailMaterialNotifications.NotifyChanged(material);
        pair.Compare(sources,a,notificationMode:true);
        material.enableInstancing=true;LTDetailMaterialNotifications.NotifyChanged(material);
        pair.Compare(sources,b,notificationMode:true);
        LTDetailMaterialNotifications.InvalidateAll(true);pair.Compare(sources,a,notificationMode:true);
        pair.next.ConfigureMaterialNotifications(false,null);
        var disabled=pair.Compare(sources,a);
        Check(disabled.after.copies==2,"disabling returns render path to exact legacy full-copy counts");
    }
    static void Main()
    {
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        var camera=new Camera{name="A"};var material=Material();var recipe=Recipe(material);
        var sources=Enumerable.Range(0,24).Select(i=>new Source{id=i,Recipe=recipe,Count=7+i,
            Entry=new(){limitShadowDistance=i%2==0,shadowDistance=20+i,cullDistance=130+i,fadeStart=80+i,thinInDistance=i%3==0,farDensity=.2f+i*.01f}}).ToArray();
        using(var pair=new Pair())
        {
            pair.Warm(sources,camera);var r=pair.Compare(sources,camera);
            Check(pair.next.PassPlanBuilds==2,"one plan per recipe/shadow configuration, not per group");
            Check(pair.next.PassMaterialChecks==1,"one material inspection per pass");
            Check(pair.next.CameraParameterUploads==1,"one shared camera parameter upload per pass");
            Check(r.after.uniformWrites<r.before.uniformWrites&&r.after.materialReads<r.before.materialReads,"fewer bindings and material native reads");
            Console.WriteLine($"24 warm groups, identical commands: uniform writes {r.before.uniformWrites}->{r.after.uniformWrites}; mocked material API reads {r.before.materialReads}->{r.after.materialReads}. These are counts, NOT milliseconds.");
            Check(r.after.copies==1&&r.after.passWrites==0&&r.after.instancingWrites==0,"one live material sync without redundant warm state writes");
            Console.WriteLine($"Material adaptation, 24 warm groups: full copies {r.before.copies}->{r.after.copies}; pass writes {r.before.passWrites}->{r.after.passWrites}; instancing writes {r.before.instancingWrites}->{r.after.instancingWrites}; total time reads {r.before.timeReads}->{r.after.timeReads}. Counts only; uniform/material-validation caching predates this change.");
            camera.transform.position=new(10,3,-2);camera.transform.forward=new(1,0,0);camera.orthographic=true;camera.orthographicSize=17;camera.cullingMask=1;
            QualitySettings.lodBias=1.7f;QualitySettings.maximumLODLevel=1;pair.Compare(sources,camera);
            var b=new Camera{name="B",fieldOfView=95,cullingMask=2};pair.Warm(sources,b);pair.Compare(sources,b);pair.Compare(sources,camera);
            recipe.levels[0].screenRelativeHeight=.75f;pair.Compare(sources,camera); // next pass must see recipe edit
            material.renderQueue=3100;var fallback=pair.Compare(sources,camera);Check(fallback.after.fallback==sources.Length,"transparent edit seen next pass");
            material.renderQueue=2000;material.SetFloat("_AddPrecomputedVelocity",1);pair.Compare(sources,camera);
            material.SetFloat("_AddPrecomputedVelocity",0);material.enableInstancing=false;
            Check(pair.Compare(sources,camera).after.draws==0,"instancing edit seen next pass");material.enableInstancing=true;
            material.shader=new Shader{name="unsupported"};pair.Compare(sources,camera);material.shader=Resources.Source;
            recipe.levels[0].parts[0].lightProbes=LightProbeUsage.UseProxyVolume;pair.Compare(sources,camera);recipe.levels[0].parts[0].lightProbes=LightProbeUsage.Off;
            material.SetFloat(LTDetailPrefab.Part.GrassRootProperty,2);material.SetFloat("arbitrary",17);pair.Compare(sources,camera);
            camera.stereoEnabled=true;pair.Compare(sources,camera);camera.stereoEnabled=false;
            // Old public API remains live without opting into a frozen camera pass.
            pair.Compare(sources,camera,false,i=>{camera.fieldOfView=35+i;camera.orthographic=i%2==0;camera.transform.position=new(i,1,2);});
            Check(pair.next.CameraParameterUploads==sources.Length,"uncached direct callers rebind camera per call");
            pair.next.Begin(64,++frame,1000000,16);pair.next.PrepareCameraPass(camera,Planes);
            Check(pair.next.TryRender(sources[0],camera,Planes,177),"prepared camera A ready");
            Check(pair.next.TryRender(sources[0],b,Planes,177),"unexpected camera B ready");
            Check(pair.next.TryRender(sources[0],camera,Planes,177)&&pair.next.CameraParameterUploads==3,"foreign camera invalidates A camera binding");
        }
        for(int n=1;n<=8;n++)
        {
            using var pair=new Pair();var m=Material();var r=Recipe(m,n);
            var s=new[]{new Source{Recipe=r,Entry=new(){limitShadowDistance=true}}};
            pair.Warm(s,camera);pair.Compare(s,camera);
            r.levels[0].parts[0].material=null;pair.Compare(s,camera);
        }
        using(var pair=new Pair())
        {
            var secondMaterial=Material();secondMaterial.SetFloat("arbitrary",23);
            var secondRecipe=Recipe(secondMaterial,3);secondRecipe.levels[0].screenRelativeHeight=.33f;
            Source first=new Source{Recipe=recipe},second=new Source{Recipe=secondRecipe},third=new Source{Recipe=recipe};
            var mixed=new[]{first,second,third};pair.Warm(mixed,camera);pair.Compare(mixed,camera);
            Check(pair.next.PassPlanBuilds==2&&pair.next.PassMaterialChecks==2,"mixed recipes/materials have distinct pass entries");
            secondMaterial.renderQueue=3100;pair.Compare(mixed,camera);
            secondMaterial.renderQueue=2000;pair.Compare(mixed,camera);
            Planes[0]=new Plane(new Vector3(.5f,0,1),-7);pair.Compare(mixed,camera);
            material.SetShaderPassEnabled("ShadowCaster",false);pair.Compare(mixed,camera);
            material.SetShaderPassEnabled("ShadowCaster",true);
        }
        using(var pair=new Pair())
        {
            var r=Recipe(Material(),9);pair.Compare(new[]{new Source{Recipe=r}},camera);
            pair.Compare(new[]{new Source{Recipe=Recipe(Material()),Count=0}},camera);
            pair.Compare(new[]{new Source{Recipe=Recipe(Material()),fail=true}},camera);
        }
        MaterialAdaptChecks();
        TerrainBindingChecks();
        UnifiedCullChecks();
        LiveNotificationCommandChecks();
        MaterialRevisionChecks.Run(Check);
        LiveMaterialNotificationChecks.Run(Check);
        Check(Unity.Profiling.ProfileTrace.Depth==0&&Unity.Profiling.ProfileTrace.seen.Count==15,"all 15 stages exercised and balanced");
        Check(Unity.Profiling.ProfileTrace.nesting.Contains("LT.Details.GPU.Stage.PlanCPU/LT.Details.GPU.Stage.MaterialCheckCPU"),"plan material checks nested");
        foreach(var child in new[]{"CopyCount","TerrainBind","MaterialAdapt","DrawSubmit"})
            Check(Unity.Profiling.ProfileTrace.nesting.Contains("LT.Details.GPU.Stage.DrawCPU/LT.Details.GPU.Stage."+child+"CPU"),"draw stage nesting: "+child);
        Check(Unity.Profiling.ProfileTrace.nesting.Contains("LT.Details.GPU.Stage.MaterialAdaptCPU/LT.Details.GPU.Stage.MaterialSyncCPU"),"material sync nested in adaptation");
        foreach(var child in new[]{"MaterialCopy","MaterialPasses"})
            Check(Unity.Profiling.ProfileTrace.nesting.Contains("LT.Details.GPU.Stage.MaterialSyncCPU/LT.Details.GPU.Stage."+child+"CPU"),"sync stage nesting: "+child);
        Console.WriteLine($"PASS GPU CPU preparation: {checks} managed assertions; {comparedCommands} dispatch/draw payload comparisons against frozen legacy code; 15 profiler stages and scope balance/count/nesting; material sync, flags, lifetime and copy-failure recovery. No native Unity, HLSL execution, graphics or performance claim.");
    }
}
