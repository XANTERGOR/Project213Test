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
        sealed class Selection : IDisposable
        {
            public int lod;public ShadowCastingMode prefabMode,mode;public bool culled;
            public GraphicsBuffer visible;public LTDetailGpuRenderer owner;
            public void Dispose(){owner.Free(ref visible);}
        }
        sealed class Draw : IDisposable
        {
            public LTDetailPrefab.Part part;public int lod;public ShadowCastingMode mode;
            public Selection selection;
            public GraphicsBuffer visible=>selection.visible;
            public GraphicsBuffer args;public MaterialPropertyBlock properties;
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
        readonly HashSet<Material> updated=new HashSet<Material>();
        readonly Dictionary<Material,double> materialTouched=new Dictionary<Material,double>();
        readonly List<Material> expiredMaterials=new List<Material>();
        readonly List<Camera> expired=new List<Camera>();
        // One row per prefab/shader/reason, not per instance. Clear retains dictionary storage.
        readonly Dictionary<(GameObject prefab,Shader shader,string reason),(int groups,long candidates)> fallbackDetails=
            new Dictionary<(GameObject,Shader,string),(int,long)>();
        readonly LTDetailUploadBudget uploadBudget=new LTDetailUploadBudget();
        LTDetailGpuData[] staging;
        readonly Vector4[] planes=new Vector4[6],thresholds=new Vector4[8];
        static readonly string[] PassNames={"MotionVectors","MOTIONVECTORS","ShadowCaster","DepthOnly","GBuffer","Forward"};
        ComputeShader cull;int kernel;bool initialized;long budget;
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
        public string Status { get; private set; }
        string unavailable;

        public void Begin(int memoryMiB,int frameId,float uploadMs,int uploadMiB)
        {
            DrawCalls=CullDispatches=Candidates=FallbackGroups=PendingGroups=0;Status=null;updated.Clear();fallbackDetails.Clear();
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
            {Destroy(materials[material]);materials.Remove(material);materialTouched.Remove(material);}
            if(initialized)return;
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
            }
            catch(Exception ex){Destroy(cull);cull=null;unavailable="GPU initialization: "+ex.Message;return;}
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
        Material Adapt(Material original)
        {
            materialTouched[original]=Time.realtimeSinceStartupAsDouble;
            if(!materials.TryGetValue(original,out var material))
            {
                material=new Material(adapters[original.shader]){hideFlags=HideFlags.HideAndDontSave};
                materials.Add(original,material);
            }
            if(updated.Add(original))
            {
                if(material.shader!=adapters[original.shader])material.shader=adapters[original.shader];
                material.CopyPropertiesFromMaterial(original);material.enableInstancing=true;
                foreach(var pass in PassNames)
                    material.SetShaderPassEnabled(pass,original.GetShaderPassEnabled(pass));
            }
            return material;
        }
        public bool TryRender(ILTDetailGpuSource source,Camera camera,Plane[] frustum,float maxDistance)
        {
            if(source.Count==0)return true;
            if(!cull||adapters.Count==0)return Fallback(source,unavailable??"GPU backend не готов.");
            if(camera.stereoEnabled)return Fallback(source,"XR пока использует CPU fallback.");
            var recipe=source.Recipe;var entry=source.Entry;
            if(recipe.levels.Count>8)return Fallback(source,"Более 8 LOD: CPU fallback.");
            foreach(var level in recipe.levels)foreach(var part in level.parts)
            {
                if(!part.material||!adapters.ContainsKey(part.material.shader))return Fallback(source,"Неподдерживаемый шейдер: CPU fallback.",part.material?part.material.shader:null);
                if(part.material.renderQueue>=(int)RenderQueue.Transparent)return Fallback(source,"Прозрачный материал: CPU fallback.",part.material.shader);
                if(part.lightProbes==LightProbeUsage.CustomProvided||part.lightProbes==LightProbeUsage.UseProxyVolume)
                    return Fallback(source,"Custom/LPPV probes: CPU fallback.",part.material.shader);
                if(part.material.HasProperty("_AddPrecomputedVelocity")&&part.material.GetFloat("_AddPrecomputedVelocity")!=0)
                    return Fallback(source,"Add Precomputed Velocity включён: CPU fallback.",part.material.shader);
            }
            int drawCount=0,selectionCount=0;
            foreach(var level in recipe.levels)
            {
                // Four Unity shadow modes: each (source mode, draw mode) is one bit.
                // LOD partitions are independent; materials/meshes do not affect Cull.
                uint combinations=0;
                foreach(var part in level.parts)
                {
                    int passes=ShadowPasses(entry,part);drawCount+=passes;
                    for(int p=0;p<passes;p++)
                        combinations|=1u<<((int)part.castShadows*4+(p==0?(int)part.castShadows:0));
                }
                while(combinations!=0){selectionCount++;combinations&=combinations-1;}
            }
            long viewBytes=(long)selectionCount*source.Count*4+ (long)drawCount*GraphicsBuffer.IndirectDrawIndexedArgs.size;
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
                    int passes=ShadowPasses(entry,part);
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
            for(int i=0;i<6;i++)planes[i]=new Vector4(frustum[i].normal.x,frustum[i].normal.y,frustum[i].normal.z,frustum[i].distance);
            for(int i=0;i<8;i++)thresholds[i]=new Vector4(i<recipe.levels.Count?recipe.levels[i].screenRelativeHeight:0,0,0,0);
            cull.SetVectorArray("_Planes",planes);cull.SetVectorArray("_LodThresholds",thresholds);
            cull.SetVector("_CameraPosition",camera.transform.position);cull.SetVector("_CameraForward",camera.transform.forward);
            cull.SetVector("_Distance",new Vector4(Mathf.Min(maxDistance,entry.cullDistance),entry.fadeStart,entry.farDensity,entry.shadowDistance));
            cull.SetVector("_Projection",new Vector4(camera.orthographic?1:0,camera.orthographic?camera.orthographicSize:Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f),QualitySettings.lodBias,QualitySettings.maximumLODLevel));
            cull.SetInt("_Count",group.count);cull.SetInt("_LodCount",recipe.levels.Count);cull.SetInt("_HasLod",recipe.hasLODGroup?1:0);
            cull.SetInt("_Thin",entry.thinInDistance?1:0);cull.SetInt("_LimitShadows",entry.limitShadowDistance?1:0);
            cull.SetBuffer(kernel,"_LTInstances",group.instances);
            Candidates+=group.count;
            foreach(var selection in view.selections)selection.culled=false;
            foreach(var draw in view.draws)
            {
                var part=draw.part;
                if(!part.material.enableInstancing||(camera.cullingMask&(1<<part.layer))==0)continue;
                if(!draw.selection.culled)
                {
                    draw.visible.SetCounterValue(0);
                    cull.SetInt("_TargetLod",draw.lod);cull.SetInt("_PrefabShadowMode",(int)part.castShadows);cull.SetInt("_DrawShadowMode",(int)draw.mode);
                    cull.SetBuffer(kernel,"_LTVisible",draw.visible);cull.Dispatch(kernel,(group.count+63)/64,1,1);
                    draw.selection.culled=true;CullDispatches++;
                }
                // D3D indexed indirect layout: instanceCount is the second uint.
                GraphicsBuffer.CopyCount(draw.visible,draw.args,4);
                var rp=new RenderParams(Adapt(part.material)){camera=camera,worldBounds=source.Bounds,matProps=draw.properties,
                    layer=part.layer,renderingLayerMask=part.renderingLayerMask,shadowCastingMode=draw.mode,receiveShadows=part.receiveShadows,
                    motionVectorMode=part.motionVectors,lightProbeUsage=LightProbeUsage.Off,reflectionProbeUsage=part.reflectionProbes};
                Graphics.RenderMeshIndirect(rp,part.mesh,draw.args);DrawCalls++;
            }
            return true;
        }
        static int ShadowPasses(LTDetailEntry entry,LTDetailPrefab.Part part)=>
            entry.limitShadowDistance&&part.castShadows!=ShadowCastingMode.Off&&part.castShadows!=ShadowCastingMode.ShadowsOnly?2:1;
        public void Remove(ILTDetailGpuSource source)
        {
            if(!groups.TryGetValue(source,out var group))return;
            foreach(var view in group.views.Values){ReservedBytes-=view.bytes;ViewCount--;}
            ReservedBytes-=group.bytes;group.Dispose();groups.Remove(source);
        }
        bool Prepare(ILTDetailGpuSource source,Group group,View view)
        {
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
                    draw.properties.SetBuffer("_LTInstances",group.instances);draw.properties.SetBuffer("_LTVisible",draw.visible);
                    draw.properties.SetMatrix("_LTPartToRoot",part.localMatrix);draw.properties.SetMatrix("_LTRootToPart",part.localMatrix.inverse);
                    draw.properties.SetFloat("_LTForceNoMotion",part.motionVectors==MotionVectorGenerationMode.ForceNoMotion?1:0);
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
        void ClearGroups(){foreach(var group in groups.Values)group.Dispose();groups.Clear();ReservedBytes=0;ViewCount=0;}
        public bool CheckMemoryAccounting(out string report)
        {
            long actual=0,reserved=0;int buffers=0,views=0;
            void Count(GraphicsBuffer buffer){if(buffer==null)return;actual+=(long)buffer.count*buffer.stride;buffers++;}
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
            ClearGroups();
            foreach(var material in materials.Values)Destroy(material);
            materials.Clear();materialTouched.Clear();expiredMaterials.Clear();adapters.Clear();updated.Clear();fallbackDetails.Clear();staging=null;
            Destroy(cull);cull=null;initialized=false;
        }
        static void Destroy(UnityEngine.Object obj)
        {if(!obj)return;if(Application.isPlaying)UnityEngine.Object.Destroy(obj);else UnityEngine.Object.DestroyImmediate(obj);}
    }
}
