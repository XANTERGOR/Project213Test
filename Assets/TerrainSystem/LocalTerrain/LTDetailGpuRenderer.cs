using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    public interface ILTDetailGpuSource
    {
        LTDetailPrefab Recipe { get; }
        LTDetailEntry Entry { get; }
        Bounds Bounds { get; }
        int Count { get; }
        void CopyGpuData(int start,int count,LTDetailGpuData[] destination);
    }

    // Persistent roots; GPU cull/LOD shared by compatible parts; no CPU readback.
    // Each camera has its own output/args buffers because HDRP defers actual draws.
    public sealed class LTDetailGpuRenderer : IDisposable
    {
        // CPU submission timings only, not GPU execution. Static names and value
        // scopes: no per-group strings, closures, counters or readbacks added.
        static class Profile
        {
            internal static readonly Unity.Profiling.ProfilerMarker Plan=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.PlanCPU");
            internal static readonly Unity.Profiling.ProfilerMarker MaterialCheck=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.MaterialCheckCPU");
            internal static readonly Unity.Profiling.ProfilerMarker Prepare=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.PrepareBuffersCPU");
            internal static readonly Unity.Profiling.ProfilerMarker Camera=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.CameraParametersCPU");
            internal static readonly Unity.Profiling.ProfilerMarker Recipe=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.RecipeParametersCPU");
            internal static readonly Unity.Profiling.ProfilerMarker Group=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.GroupParametersCPU");
            internal static readonly Unity.Profiling.ProfilerMarker Cull=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.CullSubmitCPU");
            internal static readonly Unity.Profiling.ProfilerMarker Draw=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.DrawCPU");
            internal static readonly Unity.Profiling.ProfilerMarker CopyCount=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.CopyCountCPU");
            internal static readonly Unity.Profiling.ProfilerMarker TerrainBind=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.TerrainBindCPU");
            internal static readonly Unity.Profiling.ProfilerMarker MaterialAdapt=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.MaterialAdaptCPU");
            internal static readonly Unity.Profiling.ProfilerMarker MaterialSync=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.MaterialSyncCPU");
            internal static readonly Unity.Profiling.ProfilerMarker MaterialCopy=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.MaterialCopyCPU");
            internal static readonly Unity.Profiling.ProfilerMarker MaterialPasses=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.MaterialPassesCPU");
            internal static readonly Unity.Profiling.ProfilerMarker DrawSubmit=new Unity.Profiling.ProfilerMarker("LT.Details.GPU.Stage.DrawSubmitCPU");
        }
        sealed class Selection : IDisposable
        {
            public int lod;public ShadowCastingMode prefabMode,mode;public bool culled,wanted;
            public GraphicsBuffer visible;public LTDetailGpuRenderer owner;
            public void Dispose(){owner.Free(ref visible);}
        }
        sealed class Draw : IDisposable
        {
            public LTDetailPrefab.Part part;public int lod;public ShadowCastingMode mode;
            public Selection selection;
            public GraphicsBuffer visible=>selection.visible;
            public GraphicsBuffer args;public MaterialPropertyBlock properties;
            public LTGrassTerrainBinding.Cache terrainBinding;
            public LTDetailGpuRenderer owner;
            public void Dispose(){owner.Free(ref args);}
        }
        sealed class View : IDisposable
        {
            public Camera camera;public double touched;public readonly List<Draw> draws=new List<Draw>();
            public readonly List<Selection> selections=new List<Selection>();
            public long bytes;public int prepared;
            public void Dispose(){foreach(var draw in draws)draw.Dispose();foreach(var selection in selections)selection.Dispose();}
        }
        sealed class Group : IDisposable
        {
            public LTDetailGpuRenderer owner;
            public GraphicsBuffer instances;public int count,uploaded;public long bytes;
            public readonly Dictionary<Camera,View> views=new Dictionary<Camera,View>();
            public void Dispose(){foreach(var view in views.Values)view.Dispose();owner.Free(ref instances);}
        }
        readonly Dictionary<ILTDetailGpuSource,Group> groups=new Dictionary<ILTDetailGpuSource,Group>();
        readonly Dictionary<Shader,Shader> adapters=new Dictionary<Shader,Shader>();
        readonly Dictionary<Material,Material> materials=new Dictionary<Material,Material>();
        struct MaterialState
        {
            public Shader shader;
            public bool supported,transparent,precomputedVelocity,instancing,grassColor;
        }
        struct PassPlan
        {
            public string reason;public Shader shader;public int draws,selections;
        }
        // The production caller opts into an immutable, synchronous camera pass.
        // Begin clears these value caches; dictionary storage is reused. No
        // material/recipe decision survives into the next camera or editor update.
        readonly Dictionary<Material,MaterialState> passMaterials=new Dictionary<Material,MaterialState>();
        readonly Dictionary<(LTDetailPrefab recipe,bool shadows),PassPlan> passPlans=new Dictionary<(LTDetailPrefab,bool),PassPlan>();
        Camera preparedCamera;int preparedCameraMask;LTDetailPrefab boundRecipe;
        LTGrassTerrainBinding preparedTerrainBinding;
        public int TerrainBindingUpdates {get;private set;}
        public int TerrainBindingReuses {get;private set;}
        // Default: adapted handles are reused only until Begin, full sync per pass.
        // Optional explicit notifications allow reuse across passes for an allowlist.
        readonly Dictionary<Material,Material> updated=new Dictionary<Material,Material>();
        struct MaterialReceipt
        {
            internal LTDetailMaterialNotifications.Ticket ticket;
            internal Material target;internal Shader sourceShader,adapterShader;
        }
        readonly Dictionary<Material,LTDetailMaterialNotifications.Registration> notifiedMaterials=new Dictionary<Material,LTDetailMaterialNotifications.Registration>();
        readonly Dictionary<Material,MaterialReceipt> materialReceipts=new Dictionary<Material,MaterialReceipt>();
        Material[] notificationSources=Array.Empty<Material>();
        ulong notificationVersion,adapterVersion;
        public int MaterialSyncs {get;private set;}
        public int MaterialReuses {get;private set;}
        public int NotifiedMaterialCount=>notifiedMaterials.Count;
        // Explicit allowlist, never inferred from a prefab or from a dirty/CRC result.
        // Called before Begin. An unchanged list does not allocate registrations.
        public void ConfigureMaterialNotifications(bool enabled,IReadOnlyList<Material> sources)
        {
            int count=enabled&&sources!=null?sources.Count:0;
            bool same=count==notificationSources.Length;
            for(int i=0;same&&i<count;i++)same=ReferenceEquals(sources[i],notificationSources[i]);
            if(same)return;
            foreach(var registration in notifiedMaterials.Values)registration.Dispose();
            notifiedMaterials.Clear();materialReceipts.Clear();updated.Clear();passMaterials.Clear();passPlans.Clear();
            notificationSources=count==0?Array.Empty<Material>():new Material[count];
            for(int i=0;i<count;i++)
            {
                var source=sources[i];notificationSources[i]=source;
                if(source&&!notifiedMaterials.ContainsKey(source))notifiedMaterials.Add(source,LTDetailMaterialNotifications.Register(source));
            }
        }
        void ObserveMaterialNotifications()
        {
            if(notifiedMaterials.Count==0||notificationVersion==LTDetailMaterialNotifications.ChangeVersion)return;
            notificationVersion=LTDetailMaterialNotifications.ChangeVersion;
            updated.Clear();passMaterials.Clear();passPlans.Clear();
        }
        readonly Dictionary<Material,double> materialTouched=new Dictionary<Material,double>();
        readonly List<Material> expiredMaterials=new List<Material>();
        readonly List<Camera> expired=new List<Camera>();
        // One row per prefab/shader/reason, not per instance. Clear retains dictionary storage.
        readonly Dictionary<(GameObject prefab,Shader shader,string reason),(int groups,long candidates)> fallbackDetails=
            new Dictionary<(GameObject,Shader,string),(int,long)>();
        readonly LTDetailUploadBudget uploadBudget=new LTDetailUploadBudget();
        LTDetailGpuData[] staging;
        readonly Vector4[] planes=new Vector4[6],thresholds=new Vector4[8];
        readonly Vector4[] multiSelections=new Vector4[8];
        readonly GraphicsBuffer[] cullPadding=new GraphicsBuffer[7];
        readonly List<Selection> activeSelections=new List<Selection>(48);
        // Optional so callers can A/B the established single-output kernel.
        public bool UnifiedCulling {get;set;}
        public int UnifiedCullDispatches {get;private set;}
        static readonly string[] PassNames={"MotionVectors","MOTIONVECTORS","ShadowCaster","DepthOnly","GBuffer","Forward"};
        static class Property
        {
            internal static readonly int Planes=Shader.PropertyToID("_Planes"),Thresholds=Shader.PropertyToID("_LodThresholds"),
                CameraPosition=Shader.PropertyToID("_CameraPosition"),CameraForward=Shader.PropertyToID("_CameraForward"),
                Distance=Shader.PropertyToID("_Distance"),Projection=Shader.PropertyToID("_Projection"),Count=Shader.PropertyToID("_Count"),
                LodCount=Shader.PropertyToID("_LodCount"),HasLod=Shader.PropertyToID("_HasLod"),Thin=Shader.PropertyToID("_Thin"),
                LimitShadows=Shader.PropertyToID("_LimitShadows"),Instances=Shader.PropertyToID("_LTInstances"),
                TargetLod=Shader.PropertyToID("_TargetLod"),PrefabShadowMode=Shader.PropertyToID("_PrefabShadowMode"),
                DrawShadowMode=Shader.PropertyToID("_DrawShadowMode"),Visible=Shader.PropertyToID("_LTVisible"),
                PartToRoot=Shader.PropertyToID("_LTPartToRoot"),RootToPart=Shader.PropertyToID("_LTRootToPart"),
                ForceNoMotion=Shader.PropertyToID("_LTForceNoMotion"),PrecomputedVelocity=Shader.PropertyToID("_AddPrecomputedVelocity"),
                GrassRoot=Shader.PropertyToID(LTDetailPrefab.Part.GrassRootProperty);
            internal static readonly int Selections=Shader.PropertyToID("_Selections");
            internal static readonly int[] VisibleSlots={Shader.PropertyToID("_LTVisible0"),Shader.PropertyToID("_LTVisible1"),
                Shader.PropertyToID("_LTVisible2"),Shader.PropertyToID("_LTVisible3"),Shader.PropertyToID("_LTVisible4"),
                Shader.PropertyToID("_LTVisible5"),Shader.PropertyToID("_LTVisible6"),Shader.PropertyToID("_LTVisible7")};
        }
        ComputeShader cull;int kernel,multiKernel=-1;bool initialized;long budget;
        public long ResidentBytes { get; private set; }
        public long ReservedBytes { get; private set; }
        public long PeakBytes { get; private set; }
        public long ReleasedBytes { get; private set; }
        public long AllocatedBytes { get; private set; }
        public int BufferCount { get; private set; }
        public int GroupCount=>groups.Count;
        public int ViewCount { get; private set; }
        public int MaterialCount=>materials.Count;
        public int PendingGroups { get; private set; }
        public long UploadBytes=>uploadBudget.Bytes;
        public double UploadMilliseconds=>uploadBudget.Milliseconds;
        public double PeakUploadMilliseconds { get; private set; }
        public int DrawCalls { get; private set; }
        public int CullDispatches { get; private set; }
        public int Candidates { get; private set; }
        public int FallbackGroups { get; private set; }
        public int PassPlanBuilds { get; private set; }
        public int PassMaterialChecks { get; private set; }
        public int CameraParameterUploads { get; private set; }
        public string Status { get; private set; }
        string unavailable;

        public void Begin(int memoryMiB,int frameId,float uploadMs,int uploadMiB)
        {
            UnifiedCullDispatches=0;
            TerrainBindingUpdates=TerrainBindingReuses=0;preparedTerrainBinding=default;
            MaterialSyncs=MaterialReuses=0;
            foreach(var registration in notifiedMaterials.Values)registration.RefreshEditor();
            ObserveMaterialNotifications();
            DrawCalls=CullDispatches=Candidates=FallbackGroups=PendingGroups=0;Status=null;updated.Clear();fallbackDetails.Clear();
            preparedCamera=null;boundRecipe=null;passPlans.Clear();passMaterials.Clear();
            PassPlanBuilds=PassMaterialChecks=CameraParameterUploads=0;
            uploadBudget.Begin(frameId,uploadMs,(long)uploadMiB*1024*1024,8);
            budget=(long)memoryMiB*1024*1024;
            if(ReservedBytes>budget)ClearGroups();
            foreach(var group in groups.Values)
            {
                expired.Clear();
                foreach(var pair in group.views)
                    if(!pair.Key||Time.realtimeSinceStartupAsDouble-pair.Value.touched>5)expired.Add(pair.Key);
                foreach(var camera in expired)
                {
                    var view=group.views[camera];view.Dispose();group.views.Remove(camera);
                    ReservedBytes-=view.bytes;ViewCount--;
                }
            }
            expiredMaterials.Clear();
            foreach(var pair in materialTouched)
                if(!pair.Key||Time.realtimeSinceStartupAsDouble-pair.Value>5)expiredMaterials.Add(pair.Key);
            foreach(var material in expiredMaterials)
            {Destroy(materials[material]);materials.Remove(material);materialTouched.Remove(material);materialReceipts.Remove(material);}
            if(initialized)
            {
                if(adapterVersion!=LTDetailMaterialNotifications.AdapterVersion)
                {
                    adapterVersion=LTDetailMaterialNotifications.AdapterVersion;
                    adapters.Clear();materialReceipts.Clear();LoadAdapters();
                }
                return;
            }
            initialized=true;
            if(!SystemInfo.supportsComputeShaders||!SystemInfo.supportsInstancing||!SystemInfo.supportsIndirectArgumentsBuffer)
            {unavailable="Нет поддержки compute/indirect instancing.";return;}
            // The initial implementation/argument offsets are qualified for Windows D3D only.
            if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D11&&SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12)
            {unavailable="GPU-режим пока поддерживает только Direct3D 11/12.";return;}
            var shader=Resources.Load<ComputeShader>("LTDetailCull");
            if(!shader){unavailable="Не найден LTDetailCull.compute.";return;}
            try
            {
                cull=UnityEngine.Object.Instantiate(shader);cull.hideFlags=HideFlags.HideAndDontSave;kernel=cull.FindKernel("Cull");
                if(!cull.IsSupported(kernel))throw new InvalidOperationException("Cull kernel не поддерживается.");
                if(cull.HasKernel("CullMany"))
                {
                    int candidate=cull.FindKernel("CullMany");
                    if(cull.IsSupported(candidate))multiKernel=candidate;
                }
            }
            catch(Exception ex){Destroy(cull);cull=null;unavailable="GPU initialization: "+ex.Message;return;}
            adapterVersion=LTDetailMaterialNotifications.AdapterVersion;LoadAdapters();
        }
        void LoadAdapters()
        {
            if(cull)unavailable=null;
            foreach(var item in Resources.LoadAll<LTDetailGpuShader>("LTDetailGpu"))
            {
                if(item.source&&item.indirect&&string.IsNullOrEmpty(item.error)&&item.indirect.isSupported)
                {
#if UNITY_EDITOR
                    if(UnityEditor.ShaderUtil.ShaderHasError(item.indirect))continue;
#endif
                    adapters[item.source]=item.indirect;
                }
                else if(!string.IsNullOrEmpty(item.error))unavailable=item.error;
            }
            if(adapters.Count==0)unavailable=unavailable??"Нет готовых indirect-адаптеров. Дождитесь импорта GrassWind.ltdetailshader.";
        }
        bool Fallback(ILTDetailGpuSource source,string reason,Shader shader=null)
        {FallbackGroups++;if(string.IsNullOrEmpty(Status))Status=reason;RecordFallback(source,reason,shader);return false;}
        void RecordFallback(ILTDetailGpuSource source,string reason,Shader shader=null)
        {
            var key=(source.Entry.prefab,shader,reason);
            fallbackDetails.TryGetValue(key,out var value);
            fallbackDetails[key]=(value.groups+1,value.candidates+source.Count);
        }
        public string GetFallbackReport()
        {
            var report=new System.Text.StringBuilder("CPU fallback последнего прохода камеры (после грубого отсечения ячеек).\n");
            report.AppendLine("Кандидаты — корневые экземпляры до индивидуального отсечения, НЕ видимые части и НЕ время CPU/GPU.");
            var rows=new List<KeyValuePair<(GameObject prefab,Shader shader,string reason),(int groups,long candidates)>>(fallbackDetails);
            rows.Sort((a,b)=>b.Value.candidates.CompareTo(a.Value.candidates));
            foreach(var row in rows)
                report.AppendLine((row.Key.prefab?row.Key.prefab.name:"<без префаба>")+" | "+
                    (row.Key.shader?row.Key.shader.name:"—")+" | групп: "+row.Value.groups+" | кандидатов: "+row.Value.candidates+" | "+row.Key.reason);
            if(rows.Count==0)report.AppendLine("CPU fallback / ожидающих GPU-групп в последнем проходе нет.");
            return report.ToString();
        }
        Material Adapt(Material original,bool cached)
        {
            using var profile=Profile.MaterialAdapt.Auto();
            ObserveMaterialNotifications();
            LTDetailMaterialNotifications.Registration registration=null;
            bool notified=notifiedMaterials.Count!=0&&notifiedMaterials.TryGetValue(original,out registration);
            var ticket=notified?registration.Capture():default;
            if(updated.TryGetValue(original,out var material))
            {
                // Prepared passes are synchronous; touch once per material. Direct
                // callers keep the original last-use lifetime between TryRender calls.
                if(!cached)materialTouched[original]=Time.realtimeSinceStartupAsDouble;
                if(!notified)return material;
                // Source/adapter/target shader guards run on the first use in each
                // pass. Retain the cheap synchronous-pass hit for repeated parts;
                // script writes during a pass must notify (or use Mutate).
                if(materialReceipts.TryGetValue(original,out var passReceipt)&&ticket.CanReuse(passReceipt.ticket))
                {MaterialReuses++;return material;}
            }
            materialTouched[original]=Time.realtimeSinceStartupAsDouble;
            var sourceShader=original.shader;var adapterShader=adapters[sourceShader];
            if(!materials.TryGetValue(original,out material)||!material)
            {
                material=new Material(adapterShader){hideFlags=HideFlags.HideAndDontSave};
                materials[original]=material;
            }
            if(notified&&materialReceipts.TryGetValue(original,out var receipt)&&
                receipt.target==material&&receipt.sourceShader==sourceShader&&receipt.adapterShader==adapterShader&&
                material.shader==adapterShader&&ticket.CanReuse(receipt.ticket))
            {
                MaterialReuses++;updated[original]=material;return material;
            }
            materialReceipts.Remove(original);updated.Remove(original);
            using(Profile.MaterialSync.Auto())
            {
                var shader=adapterShader;
                if(material.shader!=shader)material.shader=shader;
                using(Profile.MaterialCopy.Auto())material.CopyPropertiesFromMaterial(original);
                if(!material.enableInstancing)material.enableInstancing=true;
                // Read the actual target AFTER copying: never assume which pass
                // flags CopyPropertiesFromMaterial preserves on a Unity version.
                using(Profile.MaterialPasses.Auto())foreach(var pass in PassNames)
                {
                    bool enabled=original.GetShaderPassEnabled(pass);
                    if(material.GetShaderPassEnabled(pass)!=enabled)material.SetShaderPassEnabled(pass,enabled);
                }
            }
            MaterialSyncs++;
            if(notified&&ticket.CanReuse(registration.Capture()))
                materialReceipts[original]=new MaterialReceipt{ticket=ticket,target=material,sourceShader=sourceShader,adapterShader=adapterShader};
            // Publish only after successful synchronization; a failed copy can retry.
            updated.Add(original,material);
            return material;
        }
        internal LTGrassTerrainMap GrassTerrain;
        // Call once after Begin and frustum calculation, immediately before the
        // synchronous group loop. Direct TryRender callers retain uncached reads.
        public void PrepareCameraPass(Camera camera,Plane[] frustum)
        {
            preparedCamera=null;boundRecipe=null;
            if(!cull||!camera)return;
            // The production pass is synchronous: one snapshot of map readiness/UVs
            // for all its groups. Each private draw block still owns its own binding.
            preparedTerrainBinding=LTGrassTerrainMap.CaptureBinding(GrassTerrain);
            BindCamera(camera,frustum);preparedCamera=camera;preparedCameraMask=camera.cullingMask;
        }
        void BindCamera(Camera camera,Plane[] frustum)
        {
            using var profile=Profile.Camera.Auto();
            for(int i=0;i<6;i++)planes[i]=new Vector4(frustum[i].normal.x,frustum[i].normal.y,frustum[i].normal.z,frustum[i].distance);
            cull.SetVectorArray(Property.Planes,planes);
            cull.SetVector(Property.CameraPosition,camera.transform.position);cull.SetVector(Property.CameraForward,camera.transform.forward);
            cull.SetVector(Property.Projection,new Vector4(camera.orthographic?1:0,camera.orthographic?camera.orthographicSize:Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f),QualitySettings.lodBias,QualitySettings.maximumLODLevel));
            CameraParameterUploads++;
        }
        MaterialState CheckMaterial(Material material,bool cached)
        {
            using var profile=Profile.MaterialCheck.Auto();
            if(!material)return default;
            if(cached&&passMaterials.TryGetValue(material,out var saved))return saved;
            PassMaterialChecks++;
            var state=new MaterialState{shader=material.shader,instancing=material.enableInstancing};
            state.supported=state.shader&&adapters.ContainsKey(state.shader);
            state.transparent=material.renderQueue>=(int)RenderQueue.Transparent;
            state.precomputedVelocity=material.HasProperty(Property.PrecomputedVelocity)&&material.GetFloat(Property.PrecomputedVelocity)!=0;
            state.grassColor=material.HasProperty(Property.GrassRoot);
            if(cached)passMaterials[material]=state;
            return state;
        }
        PassPlan Plan(LTDetailPrefab recipe,bool limitShadows,bool cached)
        {
            using var profile=Profile.Plan.Auto();
            var key=(recipe,limitShadows);
            if(cached&&passPlans.TryGetValue(key,out var saved))return saved;
            PassPlanBuilds++;
            var plan=new PassPlan();
            if(recipe.levels.Count>8)plan.reason="Более 8 LOD: CPU fallback.";
            else
            {
                foreach(var level in recipe.levels)
                {
                    uint combinations=0;
                    foreach(var part in level.parts)
                    {
                        var material=CheckMaterial(part.material,cached);
                        string reason=!material.supported?"Неподдерживаемый шейдер: CPU fallback.":
                            material.transparent?"Прозрачный материал: CPU fallback.":
                            part.lightProbes==LightProbeUsage.CustomProvided||part.lightProbes==LightProbeUsage.UseProxyVolume?"Custom/LPPV probes: CPU fallback.":
                            material.precomputedVelocity?"Add Precomputed Velocity включён: CPU fallback.":null;
                        if(reason!=null){plan.reason=reason;plan.shader=material.shader;break;}
                        int passes=ShadowPasses(limitShadows,part);plan.draws+=passes;
                        for(int p=0;p<passes;p++)combinations|=1u<<((int)part.castShadows*4+(p==0?(int)part.castShadows:0));
                    }
                    if(plan.reason!=null)break;
                    while(combinations!=0){plan.selections++;combinations&=combinations-1;}
                }
            }
            if(cached)passPlans[key]=plan;
            return plan;
        }
        public bool TryRender(ILTDetailGpuSource source,Camera camera,Plane[] frustum,float maxDistance)
        {
            ObserveMaterialNotifications();
            if(source.Count==0)return true;
            if(!cull||adapters.Count==0)return Fallback(source,unavailable??"GPU backend не готов.");
            if(camera.stereoEnabled)return Fallback(source,"XR пока использует CPU fallback.");
            var recipe=source.Recipe;var entry=source.Entry;
            bool cached=preparedCamera==camera;
            if(!cached){preparedCamera=null;boundRecipe=null;}
            var plan=Plan(recipe,entry.limitShadowDistance,cached);
            if(plan.reason!=null)return Fallback(source,plan.reason,plan.shader);
            long viewBytes=(long)plan.selections*source.Count*4+ (long)plan.draws*GraphicsBuffer.IndirectDrawIndexedArgs.size;
            if(!groups.TryGetValue(source,out var group))
            {
                long bytes=(long)source.Count*LTDetailGpuData.Stride;
                if(ReservedBytes+bytes+viewBytes>budget)return Fallback(source,"Лимит GPU-памяти: CPU fallback.");
                group=new Group{owner=this,count=source.Count,bytes=bytes};
                groups.Add(source,group);ReservedBytes+=bytes;
            }
            if(!group.views.TryGetValue(camera,out var view))
            {
                long bytes=viewBytes;
                if(ReservedBytes+bytes>budget)return Fallback(source,"Лимит GPU-памяти для камеры: CPU fallback.");
                view=new View{camera=camera,bytes=bytes};
                for(int lod=0;lod<recipe.levels.Count;lod++)foreach(var part in recipe.levels[lod].parts)
                {
                    int passes=ShadowPasses(entry.limitShadowDistance,part);
                    for(int p=0;p<passes;p++)
                    {
                        var draw=new Draw{owner=this,part=part,lod=lod,mode=p==0?part.castShadows:ShadowCastingMode.Off};
                        foreach(var selection in view.selections)
                            if(selection.lod==lod&&selection.prefabMode==part.castShadows&&selection.mode==draw.mode)
                            {draw.selection=selection;break;}
                        if(draw.selection==null)
                        {
                            draw.selection=new Selection{owner=this,lod=lod,prefabMode=part.castShadows,mode=draw.mode};
                            view.selections.Add(draw.selection);
                        }
                        view.draws.Add(draw);
                    }
                }
                group.views.Add(camera,view);ReservedBytes+=bytes;ViewCount++;
            }
            view.touched=Time.realtimeSinceStartupAsDouble;
            try
            {
                if(!Prepare(source,group,view)){PendingGroups++;RecordFallback(source,"Ожидание GPU upload / выделения буферов.");return false;}
            }
            catch(Exception ex)
            {
                // Other cameras may already have submitted draws with a completed root.
                if(group.uploaded<group.count)Remove(source);
                else{view.Dispose();group.views.Remove(camera);ReservedBytes-=view.bytes;ViewCount--;}
                return Fallback(source,"GPU preparation: "+ex.Message);
            }
            if(!cached)BindCamera(camera,frustum);
            if(!cached||!ReferenceEquals(boundRecipe,recipe))
            {
                using var profile=Profile.Recipe.Auto();
                for(int i=0;i<8;i++)thresholds[i]=new Vector4(i<recipe.levels.Count?recipe.levels[i].screenRelativeHeight:0,0,0,0);
                cull.SetVectorArray(Property.Thresholds,thresholds);
                cull.SetInt(Property.LodCount,recipe.levels.Count);cull.SetInt(Property.HasLod,recipe.hasLODGroup?1:0);
                boundRecipe=recipe;
            }
            using(Profile.Group.Auto())
            {
                cull.SetVector(Property.Distance,new Vector4(Mathf.Min(maxDistance,entry.cullDistance),entry.fadeStart,entry.farDensity,entry.shadowDistance));
                cull.SetInt(Property.Count,group.count);
                cull.SetInt(Property.Thin,entry.thinInDistance?1:0);cull.SetInt(Property.LimitShadows,entry.limitShadowDistance?1:0);
                cull.SetBuffer(kernel,Property.Instances,group.instances);
            }
            int cameraMask=cached?preparedCameraMask:camera.cullingMask;
            var terrainBinding=cached?preparedTerrainBinding:LTGrassTerrainMap.CaptureBinding(GrassTerrain);
            Candidates+=group.count;
            foreach(var selection in view.selections){selection.culled=false;selection.wanted=false;}
            if(UnifiedCulling&&multiKernel>=0&&view.selections.Count>1)
            {
                activeSelections.Clear();
                foreach(var draw in view.draws)
                {
                    var part=draw.part;
                    if(draw.selection.wanted||!CheckMaterial(part.material,cached).instancing||(cameraMask&(1<<part.layer))==0)continue;
                    draw.selection.wanted=true;activeSelections.Add(draw.selection);
                }
                int tail=activeSelections.Count%8;
                if(activeSelections.Count>1&&EnsureCullPadding(tail>1?8-tail:0))
                {
                    cull.SetBuffer(multiKernel,Property.Instances,group.instances);
                    for(int first=0;first+1<activeSelections.Count;first+=8)
                    {
                        using var profile=Profile.Cull.Auto();
                        int count=Math.Min(8,activeSelections.Count-first);
                        for(int slot=0;slot<8;slot++)
                        {
                            if(slot<count)
                            {
                                var selection=activeSelections[first+slot];selection.visible.SetCounterValue(0);
                                multiSelections[slot]=new Vector4(selection.lod,(int)selection.prefabMode,(int)selection.mode,1);
                                cull.SetBuffer(multiKernel,Property.VisibleSlots[slot],selection.visible);
                            }
                            else
                            {
                                multiSelections[slot]=Vector4.zero;
                                cull.SetBuffer(multiKernel,Property.VisibleSlots[slot],cullPadding[slot-count]);
                            }
                        }
                        cull.SetVectorArray(Property.Selections,multiSelections);
                        cull.Dispatch(multiKernel,(group.count+63)/64,1,1);
                        for(int slot=0;slot<count;slot++)activeSelections[first+slot].culled=true;
                        CullDispatches++;UnifiedCullDispatches++;
                    }
                }
            }
            foreach(var draw in view.draws)
            {
                var part=draw.part;
                var materialState=CheckMaterial(part.material,cached);
                if(!materialState.instancing||(cameraMask&(1<<part.layer))==0)continue;
                if(!draw.selection.culled)
                {
                    using var profile=Profile.Cull.Auto();
                    draw.visible.SetCounterValue(0);
                    cull.SetInt(Property.TargetLod,draw.lod);cull.SetInt(Property.PrefabShadowMode,(int)part.castShadows);cull.SetInt(Property.DrawShadowMode,(int)draw.mode);
                    cull.SetBuffer(kernel,Property.Visible,draw.visible);cull.Dispatch(kernel,(group.count+63)/64,1,1);
                    draw.selection.culled=true;CullDispatches++;
                }
                using(Profile.Draw.Auto())
                {
                    // D3D indexed indirect layout: instanceCount is the second uint.
                    using(Profile.CopyCount.Auto())GraphicsBuffer.CopyCount(draw.visible,draw.args,4);
                    if(materialState.grassColor)using(Profile.TerrainBind.Auto())
                    {
                        if(draw.terrainBinding.Apply(draw.properties,terrainBinding))TerrainBindingUpdates++;
                        else TerrainBindingReuses++;
                    }
                    var rp=new RenderParams(Adapt(part.material,cached)){camera=camera,worldBounds=source.Bounds,matProps=draw.properties,
                        layer=part.layer,renderingLayerMask=part.renderingLayerMask,shadowCastingMode=draw.mode,receiveShadows=part.receiveShadows,
                        motionVectorMode=part.motionVectors,lightProbeUsage=LightProbeUsage.Off,reflectionProbeUsage=part.reflectionProbes};
                    using(Profile.DrawSubmit.Auto())Graphics.RenderMeshIndirect(rp,part.mesh,draw.args);DrawCalls++;
                }
            }
            return true;
        }
        static int ShadowPasses(bool limitShadows,LTDetailPrefab.Part part)=>
            limitShadows&&part.castShadows!=ShadowCastingMode.Off&&part.castShadows!=ShadowCastingMode.ShadowsOnly?2:1;
        bool EnsureCullPadding(int count)
        {
            // Distinct inactive UAVs: never alias an append counter in another slot.
            // A tiny shared pool is safe across cameras because inactive slots never write.
            for(int i=0;i<count;i++)
            {
                if(cullPadding[i]!=null)continue;
                if(!uploadBudget.CanAllocate||ReservedBytes+4>budget)return false;
                long start=System.Diagnostics.Stopwatch.GetTimestamp();
                try{cullPadding[i]=Allocate(GraphicsBuffer.Target.Append,1,4);ReservedBytes+=4;}
                catch(Exception){return false;} // Retain the old path if optional preparation fails.
                finally{Spend(start,0,1);}
            }
            return true;
        }
        public void Remove(ILTDetailGpuSource source)
        {
            if(!groups.TryGetValue(source,out var group))return;
            foreach(var view in group.views.Values){ReservedBytes-=view.bytes;ViewCount--;}
            ReservedBytes-=group.bytes;group.Dispose();groups.Remove(source);
        }
        bool Prepare(ILTDetailGpuSource source,Group group,View view)
        {
            using var profile=Profile.Prepare.Auto();
            if(group.instances==null)
            {
                if(!uploadBudget.CanAllocate)return false;
                long start=System.Diagnostics.Stopwatch.GetTimestamp();
                try{group.instances=Allocate(GraphicsBuffer.Target.Structured,group.count,LTDetailGpuData.Stride);}
                finally{Spend(start,0,1);}
            }
            while(group.uploaded<group.count)
            {
                int count=uploadBudget.UploadCount(group.count-group.uploaded,LTDetailGpuData.Stride,2048);
                if(count==0)return false;
                long start=System.Diagnostics.Stopwatch.GetTimestamp();
                long uploadedBytes=0;
                try
                {
                    if(staging==null)staging=new LTDetailGpuData[2048];
                    source.CopyGpuData(group.uploaded,count,staging);
                    group.instances.SetData(staging,0,group.uploaded,count);group.uploaded+=count;
                    uploadedBytes=(long)count*LTDetailGpuData.Stride;
                }
                finally{Spend(start,uploadedBytes,0);}
            }
            while(view.prepared<view.draws.Count)
            {
                if(!uploadBudget.CanAllocate)return false;
                var draw=view.draws[view.prepared];var part=draw.part;
                long start=System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    // At most one shared selection and one draw argument buffer per unit.
                    if(draw.selection.visible==null)draw.selection.visible=Allocate(GraphicsBuffer.Target.Append,group.count,4);
                    draw.args=Allocate(GraphicsBuffer.Target.IndirectArguments,1,GraphicsBuffer.IndirectDrawIndexedArgs.size);
                    draw.args.SetData(new[]{new GraphicsBuffer.IndirectDrawIndexedArgs{
                        indexCountPerInstance=part.mesh.GetIndexCount(part.submesh),instanceCount=0,
                        startIndex=part.mesh.GetIndexStart(part.submesh),baseVertexIndex=part.mesh.GetBaseVertex(part.submesh),startInstance=0}});
                    draw.properties=new MaterialPropertyBlock();
                    draw.properties.SetBuffer(Property.Instances,group.instances);draw.properties.SetBuffer(Property.Visible,draw.visible);
                    draw.properties.SetMatrix(Property.PartToRoot,part.localMatrix);draw.properties.SetMatrix(Property.RootToPart,part.localMatrix.inverse);
                    draw.properties.SetVector(Property.GrassRoot,part.grassRootInPart);
                    draw.properties.SetFloat(Property.ForceNoMotion,part.motionVectors==MotionVectorGenerationMode.ForceNoMotion?1:0);
                    view.prepared++;
                }
                finally{Spend(start,0,1);}
            }
            return true;
        }
        void Spend(long start,long bytes,int allocations)
        {
            uploadBudget.Spend((System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency,bytes,allocations);
            PeakUploadMilliseconds=Math.Max(PeakUploadMilliseconds,UploadMilliseconds);
        }
        GraphicsBuffer Allocate(GraphicsBuffer.Target target,int count,int stride)
        {
            var buffer=new GraphicsBuffer(target,count,stride);
            long bytes=(long)count*stride;ResidentBytes+=bytes;AllocatedBytes+=bytes;BufferCount++;PeakBytes=Math.Max(PeakBytes,ResidentBytes);
            return buffer;
        }
        void Free(ref GraphicsBuffer buffer)
        {
            if(buffer==null)return;
            long bytes=(long)buffer.count*buffer.stride;buffer.Dispose();buffer=null;
            ResidentBytes-=bytes;ReleasedBytes+=bytes;BufferCount--;
        }
        void ClearGroups()
        {
            foreach(var group in groups.Values)group.Dispose();groups.Clear();
            for(int i=0;i<cullPadding.Length;i++)Free(ref cullPadding[i]);
            activeSelections.Clear();ReservedBytes=0;ViewCount=0;
        }
        public bool CheckMemoryAccounting(out string report)
        {
            long actual=0,reserved=0;int buffers=0,views=0;
            void Count(GraphicsBuffer buffer){if(buffer==null)return;actual+=(long)buffer.count*buffer.stride;buffers++;}
            foreach(var padding in cullPadding)if(padding!=null){Count(padding);reserved+=(long)padding.count*padding.stride;}
            foreach(var group in groups.Values)
            {
                reserved+=group.bytes;Count(group.instances);
                foreach(var view in group.views.Values)
                {
                    reserved+=view.bytes;views++;
                    foreach(var selection in view.selections)Count(selection.visible);
                    foreach(var draw in view.draws)Count(draw.args);
                }
            }
            bool ok=actual==ResidentBytes&&reserved==ReservedBytes&&buffers==BufferCount&&views==ViewCount&&
                ResidentBytes>=0&&ResidentBytes<=ReservedBytes&&AllocatedBytes-ReleasedBytes==ResidentBytes;
            report=(ok?"Учёт буферов сходится":"ОШИБКА учёта буферов")+": "+buffers+" буферов, "+views+" камер-групп, "+actual+" байт. Это учёт принадлежащих рендереру ресурсов, не замер VRAM драйвера.";
            return ok;
        }
        public void Dispose()
        {
            ConfigureMaterialNotifications(false,null);materialReceipts.Clear();
            ClearGroups();
            foreach(var material in materials.Values)Destroy(material);
            materials.Clear();materialTouched.Clear();expiredMaterials.Clear();adapters.Clear();updated.Clear();fallbackDetails.Clear();staging=null;
            passPlans.Clear();passMaterials.Clear();preparedCamera=null;boundRecipe=null;
            preparedTerrainBinding=default;
            Destroy(cull);cull=null;initialized=false;multiKernel=-1;
        }
        static void Destroy(UnityEngine.Object obj)
        {if(!obj)return;if(Application.isPlaying)UnityEngine.Object.Destroy(obj);else UnityEngine.Object.DestroyImmediate(obj);}
    }
}
