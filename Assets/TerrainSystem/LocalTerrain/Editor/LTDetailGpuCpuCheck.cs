using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    // Native parity and isolated material CPU microbenchmarks, NOT flight/FPS/image tests.
    public static class LTDetailGpuCpuCheck
    {
        sealed class Source : ILTDetailGpuSource
        {
            public LTDetailPrefab Recipe { get; set; }
            public LTDetailEntry Entry { get; }=new LTDetailEntry{cullDistance=100,fadeStart=10,farDensity=.3f};
            public Bounds Bounds=>new Bounds(new Vector3(0,0,10),Vector3.one*40);
            public int Count=>65;
            public bool varied;
            public void CopyGpuData(int start,int count,LTDetailGpuData[] data)
            {
                for(int i=0;i<count;i++)
                {
                    int id=start+i;var p=varied?new Vector3(id%7-3,0,2+id*.5f):new Vector3(0,0,5);
                    data[i]=new LTDetailGpuData{objectToWorld=Matrix4x4.Translate(p),worldToObject=Matrix4x4.Translate(-p),
                        positionSize=new Vector4(p.x,p.y,p.z,1),lodPositionRandom=new Vector4(p.x,p.y,p.z,id/65f),
                        center=new Vector4(p.x,p.y,p.z,0),extents=new Vector4(.1f,.1f,.1f,0)};
                }
            }
        }
        static object Field(object obj,string name)=>obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(obj);
        // Earlier CRC/runtime-enumeration prototypes were rejected; see profiling
        // notes and preserved v3/v4 logs. v5 uses explicit ownership/notifications.
        static readonly string[] MaterialPasses={"MotionVectors","MOTIONVECTORS","ShadowCaster","DepthOnly","GBuffer","Forward"};
        static void CopyMaterial(Material source,Material target)
        {
            target.CopyPropertiesFromMaterial(source);
            if(!target.enableInstancing)target.enableInstancing=true;
            foreach(string pass in MaterialPasses)
            {bool enabled=source.GetShaderPassEnabled(pass);if(target.GetShaderPassEnabled(pass)!=enabled)target.SetShaderPassEnabled(pass,enabled);}
        }
        // An isolated adapter consumer for the experiment, NOT LTDetailGpuRenderer.
        // Source and adapter shader *identities* are defensive guards, not a claim
        // to detect shader reimport, Undo or arbitrary unnotified property writes.
        sealed class NotifiedMaterialAdapter : IDisposable
        {
            readonly LTDetailMaterialRevisionProbe<Material> registry;
            LTDetailMaterialRevisionProbe<Material>.Ticket receipt;
            Material source;Shader sourceShader,adapterShader;
            public Material Target {get;private set;}
            public int Copies {get;private set;}
            public NotifiedMaterialAdapter(LTDetailMaterialRevisionProbe<Material> registry){this.registry=registry;}
            public Material Resolve(Material original,Shader adapter,Action afterCopy=null)
            {
                if(!original||!adapter){receipt=default;throw new ArgumentException("Missing source material or adapter shader.");}
                var ticket=registry.Capture(original);var shader=original.shader;
                if(Target&&source==original&&sourceShader==shader&&adapterShader==adapter&&Target.shader==adapter&&ticket.CanReuse(receipt))return Target;
                // A failed or re-entrant copy must never publish a usable receipt.
                receipt=default;
                if(!Target)Target=new Material(adapter){hideFlags=HideFlags.HideAndDontSave};
                if(Target.shader!=adapter)Target.shader=adapter;
                CopyMaterial(original,Target);Copies++;afterCopy?.Invoke();
                source=original;sourceShader=shader;adapterShader=adapter;
                if(ticket.CanReuse(registry.Capture(original)))receipt=ticket;
                return Target;
            }
            public void Invalidate()=>receipt=default;
            public void Dispose(){receipt=default;if(Target)Object.DestroyImmediate(Target);Target=null;}
        }
        static void MeasureMaterialNotifications(Material source,Shader adapter,string label,Action<bool,string> check)
        {
            using var registry=new LTDetailMaterialRevisionProbe<Material>();
            using var foreignRegistry=new LTDetailMaterialRevisionProbe<Material>();
            using var owner=registry.ClaimExclusive(source);
            using var owned=new NotifiedMaterialAdapter(registry);
            using var foreign=new NotifiedMaterialAdapter(foreignRegistry);
            var target=new Material(adapter){hideFlags=HideFlags.HideAndDontSave};
            try
            {
                // Some runtimes report zero even for known managed allocations.
                // Calibrate instead of presenting an unsupported counter as zero GC.
                long allocated=GC.GetAllocatedBytesForCurrentThread();var calibration=new byte[4096];
                bool allocationCounterWorks=GC.GetAllocatedBytesForCurrentThread()-allocated>=4096;GC.KeepAlive(calibration);
                const int warm=24,rounds=7,iterations=64;
                Action[] actions={()=>CopyMaterial(source,target),()=>owned.Resolve(source,adapter),()=>foreign.Resolve(source,adapter),
                    ()=>{owner.NotifyChanged();owned.Resolve(source,adapter);}};
                string[] names={"full sync","explicitly owned, unchanged","unowned fallback","notified each operation (includes copy)"};
                var times=new double[actions.Length][];var bytes=new double[actions.Length][];
                for(int a=0;a<actions.Length;a++)
                {
                    times[a]=new double[rounds];bytes[a]=new double[rounds];
                    for(int i=0;i<warm;i++)actions[a]();
                }
                int ownedBefore=owned.Copies,foreignBefore=foreign.Copies;
                for(int round=0;round<rounds;round++)for(int step=0;step<actions.Length;step++)
                {
                    int a=(step+round)%actions.Length;Action action=actions[a];
                    long startBytes=GC.GetAllocatedBytesForCurrentThread(),start=System.Diagnostics.Stopwatch.GetTimestamp();
                    for(int i=0;i<iterations;i++)action();
                    long elapsed=System.Diagnostics.Stopwatch.GetTimestamp()-start;
                    bytes[a][round]=(GC.GetAllocatedBytesForCurrentThread()-startBytes)/(double)iterations;
                    times[a][round]=elapsed*1000000.0/System.Diagnostics.Stopwatch.Frequency/iterations;
                }
                check(owned.Copies-ownedBefore==rounds*iterations,"Owned benchmark must copy only when notified: "+label);
                check(foreign.Copies-foreignBefore==rounds*iterations,"Foreign benchmark must copy every operation: "+label);
                string Stats(double[] values){Array.Sort(values);return $"{values[rounds/2]:F3} median [{values[0]:F3}..{values[rounds-1]:F3}]";}
                var report=new System.Text.StringBuilder("[GPU Material Notification Cost v1] "+label+$"; {rounds} x {iterations}; rotating order, {warm} warm operations per path.\n");
                for(int a=0;a<actions.Length;a++)report.AppendLine(names[a]+": us/op "+Stats(times[a])+"; managed B/op "+
                    (allocationCounterWorks?Stats(bytes[a]):"UNAVAILABLE (allocation calibration failed)"));
                report.Append("Complete resolve path incl. shader guards; no property scan/CRC. Notified path times notification+sync, not an actual property setter. Temporary-material CPU microbenchmark, NOT flight/FPS/GPU/native-memory. Production cache OFF.");
                Debug.Log(report.ToString());
            }
            finally {Object.DestroyImmediate(target);}
        }
        static void CheckMaterialNotifications(Shader shader,Shader adapter,Action<bool,string> check)
        {
            using var registry=new LTDetailMaterialRevisionProbe<Material>();
            using var first=new NotifiedMaterialAdapter(registry);
            using var second=new NotifiedMaterialAdapter(registry);
            var objects=new List<Object>();GraphicsBuffer bufferA=null,bufferB=null;
            var errors=new List<string>();
            void NativeError(string message,string trace,LogType type)
            {if((type==LogType.Error||type==LogType.Exception||type==LogType.Assert)&&errors.Count<8)errors.Add(message);}
            T Own<T>(T obj) where T:Object {obj.hideFlags=HideFlags.HideAndDontSave;objects.Add(obj);return obj;}
            const string scalar="_LTNotifyFloat",matrix="_LTNotifyMatrix",floats="_LTNotifyFloats",vectors="_LTNotifyVectors",matrices="_LTNotifyMatrices",buffer="_LTNotifyBuffer";
            Application.logMessageReceived+=NativeError;
            try
            {
                var source=Own(new Material(shader));var reference=Own(new Material(adapter));
                var textureA=Own(new Texture2D(1,1));var textureB=Own(new Texture2D(1,1));
                MeasureMaterialNotifications(source,adapter,"plain GrassWind",check);
                source.SetFloat(scalar,1);source.SetMatrix(matrix,Matrix4x4.identity);
                source.SetFloatArray(floats,new[]{1f,2f});source.SetVectorArray(vectors,new[]{Vector4.zero,Vector4.one});
                source.SetMatrixArray(matrices,new[]{Matrix4x4.identity,Matrix4x4.identity});
                bufferA=new GraphicsBuffer(GraphicsBuffer.Target.Structured,1,16);bufferB=new GraphicsBuffer(GraphicsBuffer.Target.Structured,1,16);
                source.SetBuffer(buffer,bufferA);
                string Values<T>(T[] values)=>values==null?null:string.Join("|",values.Select(v=>v.ToString()));
                void Equivalent(Material actual,string context)
                {
                    // Independent unconditional native-copy oracle, no revision gate.
                    reference.CopyPropertiesFromMaterial(source);reference.enableInstancing=true;
                    foreach(string pass in MaterialPasses)reference.SetShaderPassEnabled(pass,source.GetShaderPassEnabled(pass));
                    check(actual.shader==reference.shader&&actual.enableInstancing==reference.enableInstancing,"shader/instancing: "+context);
                    check(actual.renderQueue==reference.renderQueue&&actual.doubleSidedGI==reference.doubleSidedGI&&actual.globalIlluminationFlags==reference.globalIlluminationFlags,"settings: "+context);
                    check(actual.shaderKeywords.OrderBy(k=>k).SequenceEqual(reference.shaderKeywords.OrderBy(k=>k)),"keywords: "+context);
                    foreach(string pass in MaterialPasses)check(actual.GetShaderPassEnabled(pass)==reference.GetShaderPassEnabled(pass),"pass "+pass+": "+context);
                    for(int i=0;i<shader.GetPropertyCount();i++)
                    {
                        int id=shader.GetPropertyNameId(i);bool same;
                        switch(shader.GetPropertyType(i))
                        {
                            case ShaderPropertyType.Float:case ShaderPropertyType.Range:same=actual.GetFloat(id).Equals(reference.GetFloat(id));break;
                            case ShaderPropertyType.Int:same=actual.GetInteger(id)==reference.GetInteger(id);break;
                            case ShaderPropertyType.Color:same=actual.GetColor(id).Equals(reference.GetColor(id));break;
                            case ShaderPropertyType.Vector:same=actual.GetVector(id).Equals(reference.GetVector(id));break;
                            case ShaderPropertyType.Texture:same=actual.GetTexture(id)==reference.GetTexture(id)&&actual.GetTextureOffset(id).Equals(reference.GetTextureOffset(id))&&actual.GetTextureScale(id).Equals(reference.GetTextureScale(id));break;
                            default:throw new InvalidOperationException("Uncovered shader property type.");
                        }
                        check(same,"property "+shader.GetPropertyName(i)+": "+context);
                    }
                    check(actual.GetFloat(scalar).Equals(reference.GetFloat(scalar)),"runtime scalar: "+context);
                    check(actual.GetMatrix(matrix).Equals(reference.GetMatrix(matrix)),"runtime matrix: "+context);
                    check(actual.GetFloatArray(floats).SequenceEqual(reference.GetFloatArray(floats)),"runtime float array: "+context);
                    check(actual.GetVectorArray(vectors).SequenceEqual(reference.GetVectorArray(vectors)),"runtime vector array: "+context);
                    check(actual.GetMatrixArray(matrices).SequenceEqual(reference.GetMatrixArray(matrices)),"runtime matrix array: "+context);
                    check(actual.GetBuffer(buffer).Equals(reference.GetBuffer(buffer)),"runtime buffer handle: "+context);
                    check(errors.Count==0,"Native notification probe errors: "+string.Join("; ",errors));
                }
                first.Resolve(source,adapter);source.SetFloat(scalar,2);Equivalent(first.Resolve(source,adapter),"unowned external write");
                check(first.Copies==2,"Unowned source must copy on every resolve.");
                using var owner=registry.ClaimExclusive(source);
                int copies=first.Copies;first.Resolve(source,adapter);check(first.Copies==copies+1,"Claim requires initial sync.");
                copies=first.Copies;for(int i=0;i<32;i++)first.Resolve(source,adapter);
                check(first.Copies==copies,"Owned unchanged source can skip subsequent copies.");Equivalent(first.Target,"owned unchanged");
                int mutations=0;
                void Edit<T>(string label,Action write,Func<Material,T> read)
                {
                    var before=read(source);owner.Mutate(write);
                    check(!EqualityComparer<T>.Default.Equals(before,read(source)),"Vacuous mutation: "+label);
                    int count=first.Copies;Equivalent(first.Resolve(source,adapter),label);
                    check(first.Copies==count+1,"Notification requires one full sync: "+label);
                    first.Resolve(source,adapter);check(first.Copies==count+1,"Unchanged reuse after notification: "+label);mutations++;
                }
                Edit("runtime scalar",()=>source.SetFloat(scalar,3),m=>m.GetFloat(scalar));
                Edit("runtime matrix",()=>source.SetMatrix(matrix,Matrix4x4.Translate(Vector3.one)),m=>m.GetMatrix(matrix));
                Edit("float array tail",()=>source.SetFloatArray(floats,new[]{1f,9f}),m=>Values(m.GetFloatArray(floats)));
                Edit("vector array tail",()=>source.SetVectorArray(vectors,new[]{Vector4.zero,Vector4.one*3}),m=>Values(m.GetVectorArray(vectors)));
                Edit("matrix array tail",()=>source.SetMatrixArray(matrices,new[]{Matrix4x4.identity,Matrix4x4.Scale(Vector3.one*2)}),m=>Values(m.GetMatrixArray(matrices)));
                Edit("buffer binding",()=>source.SetBuffer(buffer,bufferB),m=>m.GetBuffer(buffer));
                int textureId=-1,floatId=-1;
                for(int i=0;i<shader.GetPropertyCount();i++)
                {
                    if(floatId<0&&(shader.GetPropertyType(i)==ShaderPropertyType.Float||shader.GetPropertyType(i)==ShaderPropertyType.Range))floatId=shader.GetPropertyNameId(i);
                    if(textureId<0&&shader.GetPropertyType(i)==ShaderPropertyType.Texture&&shader.GetPropertyTextureDimension(i)==TextureDimension.Tex2D)textureId=shader.GetPropertyNameId(i);
                }
                check(floatId!=-1&&textureId!=-1,"Fixture needs declared float and texture properties.");
                Edit("declared float",()=>source.SetFloat(floatId,source.GetFloat(floatId)==.37f?.81f:.37f),m=>m.GetFloat(floatId));
                // v4 incorrectly queried an undeclared texture. Use real shader properties;
                // do not quietly suppress native errors or turn that failed scan into a PASS.
                Edit("declared texture A",()=>source.SetTexture(textureId,textureA),m=>m.GetTexture(textureId));
                Edit("declared texture B",()=>source.SetTexture(textureId,textureB),m=>m.GetTexture(textureId));
                Edit("texture transform",()=>source.SetTextureOffset(textureId,new Vector2(.37f,.81f)),m=>m.GetTextureOffset(textureId));
                Edit("queue",()=>source.renderQueue=2461,m=>m.renderQueue);
                Edit("GI",()=>source.doubleSidedGI=!source.doubleSidedGI,m=>m.doubleSidedGI);
                Edit("source instancing",()=>source.enableInstancing=!source.enableInstancing,m=>m.enableInstancing);
                foreach(string pass in MaterialPasses)Edit("pass "+pass,()=>source.SetShaderPassEnabled(pass,!source.GetShaderPassEnabled(pass)),m=>m.GetShaderPassEnabled(pass));
                var keyword=shader.keywordSpace.keywords.FirstOrDefault(k=>k.isValid);
                if(keyword.isValid)Edit("keyword",()=>source.SetKeyword(keyword,!source.IsKeywordEnabled(keyword.name)),m=>m.IsKeywordEnabled(keyword.name));
                second.Resolve(source,adapter);copies=second.Copies;
                owner.Mutate(()=>source.SetFloat(scalar,4));first.Resolve(source,adapter);
                Equivalent(second.Resolve(source,adapter),"second consumer notified independently");check(second.Copies==copies+1,"First consumer cannot consume another consumer's notification.");
                owner.Mutate(()=>
                {
                    source.SetFloat(scalar,5);int n=first.Copies;first.Resolve(source,adapter);first.Resolve(source,adapter);
                    check(first.Copies==n+2,"Re-entrant read during write must always copy.");
                });
                copies=first.Copies;Equivalent(first.Resolve(source,adapter),"after in-progress write");check(first.Copies==copies+1,"In-progress copy cannot publish a receipt.");
                try{owner.Mutate(()=>{source.SetFloat(scalar,6);throw new ApplicationException("injected partial mutation");});}catch(ApplicationException){}
                Equivalent(first.Resolve(source,adapter),"throwing mutation");
                owner.NotifyChanged();copies=first.Copies;
                try{first.Resolve(source,adapter,()=>throw new ApplicationException("injected post-copy failure"));}catch(ApplicationException){}
                Equivalent(first.Resolve(source,adapter),"copy failure retry");check(first.Copies==copies+2,"Failed sync cannot publish receipt.");
                owner.NotifyChanged();copies=first.Copies;
                first.Resolve(source,adapter,()=>owner.Mutate(()=>source.SetFloat(scalar,7)));
                Equivalent(first.Resolve(source,adapter),"notification during sync");check(first.Copies==copies+2,"Notification during copy must prevent stale receipt publication.");
                copies=first.Copies;first.Invalidate();Equivalent(first.Resolve(source,adapter),"explicit adapter invalidation");check(first.Copies==copies+1,"Invalidation forces resync.");
                // Model reload/import/Undo loss of certainty WITHOUT invoking user Undo,
                // importing assets or claiming these real event integrations are proven.
                registry.RevokeAll();copies=first.Copies;source.SetFloat(scalar,8);
                Equivalent(first.Resolve(source,adapter),"uncertain ownership fallback");first.Resolve(source,adapter);
                check(first.Copies==copies+2,"Uncertain ownership never auto re-registers.");
                using var replacement=registry.ClaimExclusive(source);first.Resolve(source,adapter);
                var other=Own(new Material(source));other.SetFloat(scalar,45);
                using(var otherOwner=registry.ClaimExclusive(other))
                {
                    copies=first.Copies;check(first.Resolve(other,adapter).GetFloat(scalar)==45,"Switching source identity must not reuse another material.");
                    check(first.Copies==copies+1,"Switching source requires synchronization.");
                    // Keep the runtime-rich fixture unchanged: Unity may reset property
                    // data when assigning a shader. This case tests identity guards only.
                    copies=first.Copies;other.shader=adapter;first.Resolve(other,adapter);other.shader=shader;first.Resolve(other,adapter);
                    check(first.Copies==copies+2,"Source shader identity changes invalidate even without notification.");
                    copies=first.Copies;Equivalent(first.Resolve(source,adapter),"source identity restored");
                    check(first.Copies==copies+1,"Returning to the original source requires synchronization.");
                }
                copies=first.Copies;first.Resolve(source,shader);Equivalent(first.Resolve(source,adapter),"adapter identity guard");
                check(first.Copies==copies+2,"Adapter shader identity changes invalidate.");
                copies=first.Copies;first.Target.shader=shader;Equivalent(first.Resolve(source,adapter),"target shader identity guard");
                check(first.Copies==copies+1,"Unexpected target shader cannot reuse receipt.");
                copies=first.Copies;Object.DestroyImmediate(first.Target);Equivalent(first.Resolve(source,adapter),"destroyed target recreated");
                check(first.Copies==copies+1,"Destroyed target must copy into a new material.");
                copies=first.Copies;source.SetFloat(scalar,9);first.Resolve(source,adapter);
                check(first.Copies==copies&&!first.Target.GetFloat(scalar).Equals(source.GetFloat(scalar)),"Negative control: unnotified direct write is undetectable, not automatically safe.");
                replacement.NotifyChanged();Equivalent(first.Resolve(source,adapter),"explicit notification after negative control");
                replacement.Dispose();source.SetFloat(scalar,10);Equivalent(first.Resolve(source,adapter),"external writer after ownership revoked");
                MeasureMaterialNotifications(source,adapter,"runtime-rich GrassWind",check);
                check(errors.Count==0,"Native notification probe errors: "+string.Join("; ",errors));
                Debug.Log($"[GPU Material Notifications v1] PASS {mutations} non-vacuous notified edits; native full-copy oracle, unchanged reuse, unowned fallback, two consumers, revocation/re-registration, throwing/re-entrant writes, failed sync and notification during sync. Negative control CONFIRMS that unnotified direct writes cannot be detected. Source materials in real prefabs are NOT registered. Real Undo/reimport event integration is NOT tested. Editor-only prototype; production cache OFF.");
            }
            finally
            {
                Application.logMessageReceived-=NativeError;
                // Consumers hold texture/buffer references; release them before resources.
                first.Dispose();second.Dispose();
                foreach(var obj in objects.OfType<Material>())if(obj)Object.DestroyImmediate(obj);
                foreach(var obj in objects.Where(o=>!(o is Material)))if(obj)Object.DestroyImmediate(obj);
                bufferA?.Dispose();bufferB?.Dispose();
            }
        }
        static void CheckLiveMaterialNotifications(Shader shader,Shader adapter,Action<bool,string> check)
        {
            // Actual production backend. Own materials/resources only; no actual
            // Undo operation, reimport, user material registration or scene save.
            using var first=new LTDetailGpuRenderer();using var second=new LTDetailGpuRenderer();
            var objects=new List<Object>();GraphicsBuffer bufferA=null,bufferB=null;
            T Own<T>(T obj) where T:Object {obj.hideFlags=HideFlags.HideAndDontSave;objects.Add(obj);return obj;}
            var errors=new List<string>();
            void NativeError(string message,string trace,LogType type)
            {if((type==LogType.Error||type==LogType.Exception||type==LogType.Assert)&&errors.Count<8)errors.Add(message);}
            Application.logMessageReceived+=NativeError;
            try
            {
                var source=Own(new Material(shader));var foreign=Own(new Material(shader));var oracle=Own(new Material(adapter));
                var textureA=Own(new Texture2D(1,1));var textureB=Own(new Texture2D(1,1));
                const string scalar="_LTLiveProbeFloat",matrix="_LTLiveProbeMatrix",floats="_LTLiveProbeFloats",
                    vectors="_LTLiveProbeVectors",matrices="_LTLiveProbeMatrices",buffer="_LTLiveProbeBuffer";
                source.SetFloat(scalar,1);source.SetMatrix(matrix,Matrix4x4.identity);
                source.SetFloatArray(floats,new[]{1f,2f});source.SetVectorArray(vectors,new[]{Vector4.zero,Vector4.one});
                source.SetMatrixArray(matrices,new[]{Matrix4x4.identity,Matrix4x4.identity});
                bufferA=new GraphicsBuffer(GraphicsBuffer.Target.Structured,1,16);bufferB=new GraphicsBuffer(GraphicsBuffer.Target.Structured,1,16);
                source.SetBuffer(buffer,bufferA);int frame=1000000,edits=0;
                var method=typeof(LTDetailGpuRenderer).GetMethod("Adapt",BindingFlags.Instance|BindingFlags.NonPublic);
                Material Adapt(LTDetailGpuRenderer gpu,Material material)=>(Material)method.Invoke(gpu,new object[]{material,true});
                void Begin(LTDetailGpuRenderer gpu)=>gpu.Begin(64,++frame,10000,16);
                void Equivalent(Material actual,string label)
                {
                    oracle.CopyPropertiesFromMaterial(source);oracle.enableInstancing=true;
                    foreach(string pass in MaterialPasses)oracle.SetShaderPassEnabled(pass,source.GetShaderPassEnabled(pass));
                    check(actual.shader==oracle.shader&&actual.enableInstancing,"Live shader/instancing: "+label);
                    check(actual.renderQueue==oracle.renderQueue&&actual.doubleSidedGI==oracle.doubleSidedGI&&actual.globalIlluminationFlags==oracle.globalIlluminationFlags,"Live settings: "+label);
                    check(actual.shaderKeywords.OrderBy(k=>k).SequenceEqual(oracle.shaderKeywords.OrderBy(k=>k)),"Live keywords: "+label);
                    foreach(string pass in MaterialPasses)check(actual.GetShaderPassEnabled(pass)==oracle.GetShaderPassEnabled(pass),"Live pass "+pass+": "+label);
                    for(int i=0;i<shader.GetPropertyCount();i++)
                    {
                        int id=shader.GetPropertyNameId(i);bool same;
                        switch(shader.GetPropertyType(i))
                        {
                            case ShaderPropertyType.Float:case ShaderPropertyType.Range:same=actual.GetFloat(id).Equals(oracle.GetFloat(id));break;
                            case ShaderPropertyType.Int:same=actual.GetInteger(id)==oracle.GetInteger(id);break;
                            case ShaderPropertyType.Color:same=actual.GetColor(id).Equals(oracle.GetColor(id));break;
                            case ShaderPropertyType.Vector:same=actual.GetVector(id).Equals(oracle.GetVector(id));break;
                            case ShaderPropertyType.Texture:same=actual.GetTexture(id)==oracle.GetTexture(id)&&actual.GetTextureOffset(id).Equals(oracle.GetTextureOffset(id))&&actual.GetTextureScale(id).Equals(oracle.GetTextureScale(id));break;
                            default:throw new InvalidOperationException("Uncovered live shader property type.");
                        }
                        check(same,"Live property "+shader.GetPropertyName(i)+": "+label);
                    }
                    check(actual.GetFloat(scalar).Equals(oracle.GetFloat(scalar))&&actual.GetMatrix(matrix).Equals(oracle.GetMatrix(matrix)),"Live runtime scalar/matrix: "+label);
                    check(actual.GetFloatArray(floats).SequenceEqual(oracle.GetFloatArray(floats)),"Live float array: "+label);
                    check(actual.GetVectorArray(vectors).SequenceEqual(oracle.GetVectorArray(vectors)),"Live vector array: "+label);
                    check(actual.GetMatrixArray(matrices).SequenceEqual(oracle.GetMatrixArray(matrices)),"Live matrix array: "+label);
                    check(actual.GetBuffer(buffer).Equals(oracle.GetBuffer(buffer)),"Live buffer handle: "+label);
                    check(errors.Count==0,"Live native errors: "+string.Join("; ",errors));
                }
                Begin(first);Adapt(first,source);Begin(first);var target=Adapt(first,source);
                check(first.MaterialSyncs==1&&first.NotifiedMaterialCount==0,"Live cache defaults OFF");
                var list=new[]{source,source,null};first.ConfigureMaterialNotifications(false,list);Begin(first);Adapt(first,source);
                check(first.MaterialSyncs==1&&first.NotifiedMaterialCount==0,"Disabled allowlist cannot grant caching");
                first.ConfigureMaterialNotifications(true,Array.Empty<Material>());Begin(first);Adapt(first,source);
                check(first.MaterialSyncs==1,"Empty opt-in list keeps full sync");
                first.ConfigureMaterialNotifications(true,list);Begin(first);Adapt(first,source);
                check(first.MaterialSyncs==1&&first.NotifiedMaterialCount==1,"Live initial sync + deduplicated registration");
                for(int i=0;i<32;i++)
                {
                    first.ConfigureMaterialNotifications(true,list);Begin(first);Adapt(first,source);
                    check(first.MaterialSyncs==0&&first.MaterialReuses==1,"Live unchanged reuse across passes "+i);
                }
                Equivalent(target,"unchanged");
                Begin(first);Adapt(first,foreign);foreign.SetFloat(scalar,73);Begin(first);
                check(Adapt(first,foreign).GetFloat(scalar)==73&&first.MaterialSyncs==1,"Live foreign material always copies next pass");
                void Edit<T>(string label,Action write,Func<Material,T> read)
                {
                    T old=read(source);write();check(!EqualityComparer<T>.Default.Equals(old,read(source)),"Non-vacuous live edit: "+label);
                    LTDetailMaterialNotifications.NotifyChanged(source);Begin(first);Equivalent(Adapt(first,source),label);
                    check(first.MaterialSyncs==1,"One live copy after notification: "+label);
                    Adapt(first,source);check(first.MaterialSyncs==1,"Same-pass reuse after notification: "+label);edits++;
                }
                string Values<T>(T[] values)=>string.Join("|",values.Select(v=>v.ToString()));
                Edit("runtime scalar",()=>source.SetFloat(scalar,2),m=>m.GetFloat(scalar));
                Edit("runtime matrix",()=>source.SetMatrix(matrix,Matrix4x4.Translate(Vector3.one)),m=>m.GetMatrix(matrix));
                Edit("float array tail",()=>source.SetFloatArray(floats,new[]{1f,9f}),m=>Values(m.GetFloatArray(floats)));
                Edit("vector array tail",()=>source.SetVectorArray(vectors,new[]{Vector4.zero,Vector4.one*3}),m=>Values(m.GetVectorArray(vectors)));
                Edit("matrix array tail",()=>source.SetMatrixArray(matrices,new[]{Matrix4x4.identity,Matrix4x4.Scale(Vector3.one*2)}),m=>Values(m.GetMatrixArray(matrices)));
                Edit("buffer binding",()=>source.SetBuffer(buffer,bufferB),m=>m.GetBuffer(buffer));
                int floatId=-1,textureId=-1;
                for(int i=0;i<shader.GetPropertyCount();i++)
                {
                    if(floatId<0&&(shader.GetPropertyType(i)==ShaderPropertyType.Float||shader.GetPropertyType(i)==ShaderPropertyType.Range))floatId=shader.GetPropertyNameId(i);
                    if(textureId<0&&shader.GetPropertyType(i)==ShaderPropertyType.Texture&&shader.GetPropertyTextureDimension(i)==TextureDimension.Tex2D)textureId=shader.GetPropertyNameId(i);
                }
                check(floatId>=0&&textureId>=0,"Live fixture requires float and texture properties");
                Edit("declared float",()=>source.SetFloat(floatId,source.GetFloat(floatId)==.37f?.81f:.37f),m=>m.GetFloat(floatId));
                Edit("texture A",()=>source.SetTexture(textureId,textureA),m=>m.GetTexture(textureId));
                Edit("texture B",()=>source.SetTexture(textureId,textureB),m=>m.GetTexture(textureId));
                Edit("texture offset",()=>source.SetTextureOffset(textureId,new Vector2(.37f,.81f)),m=>m.GetTextureOffset(textureId));
                Edit("queue",()=>source.renderQueue=2461,m=>m.renderQueue);
                Edit("GI",()=>source.doubleSidedGI=!source.doubleSidedGI,m=>m.doubleSidedGI);
                Edit("instancing",()=>source.enableInstancing=!source.enableInstancing,m=>m.enableInstancing);
                foreach(string pass in MaterialPasses)Edit("pass "+pass,()=>source.SetShaderPassEnabled(pass,!source.GetShaderPassEnabled(pass)),m=>m.GetShaderPassEnabled(pass));
                var keyword=shader.keywordSpace.keywords.FirstOrDefault(k=>k.isValid);
                if(keyword.isValid)Edit("keyword",()=>source.SetKeyword(keyword,!source.IsKeywordEnabled(keyword.name)),m=>m.IsKeywordEnabled(keyword.name));
                second.ConfigureMaterialNotifications(true,new[]{source});Begin(second);Adapt(second,source);
                LTDetailMaterialNotifications.Mutate(source,m=>m.SetFloat(scalar,8));Begin(first);Begin(second);
                Equivalent(Adapt(first,source),"consumer A");Equivalent(Adapt(second,source),"consumer B");
                check(first.MaterialSyncs==1&&second.MaterialSyncs==1,"Two live consumers receive the same edit");
                LTDetailMaterialNotifications.Mutate(source,m=>
                {
                    m.SetFloat(scalar,9);int copies=first.MaterialSyncs;Adapt(first,source);Adapt(first,source);
                    check(first.MaterialSyncs==copies+2,"Live in-progress mutation never publishes reusable receipt");
                });
                Equivalent(Adapt(first,source),"after reentrant mutation");
                try{LTDetailMaterialNotifications.Mutate(source,m=>{m.SetFloat(scalar,10);throw new InvalidOperationException("expected write failure");});}
                catch(InvalidOperationException ex)when(ex.Message=="expected write failure"){}
                Equivalent(Adapt(first,source),"throwing mutation");
                source.SetFloat(scalar,11);LTDetailMaterialNotifications.NotifyChanged(source);Equivalent(Adapt(first,source),"same-pass notification");
                // A real SetDirty signal, but only on this temporary material.
                source.SetFloat(scalar,12);int dirty=EditorUtility.GetDirtyCount(source);EditorUtility.SetDirty(source);
                check(EditorUtility.GetDirtyCount(source)!=dirty,"Temporary Inspector-style dirty signal really changed");
                Begin(first);Equivalent(Adapt(first,source),"Inspector-style dirty signal");check(first.MaterialSyncs==1,"Dirty count triggers sync");
                LTDetailMaterialNotificationEditor.OnUndoRedo();Begin(first);Adapt(first,source);
                check(first.MaterialSyncs==1,"Undo callback invalidation (simulated, user Undo untouched)");
                var map=(IDictionary)Field(first,"adapters");map.Clear();
                LTDetailMaterialNotificationEditor.OnAssetsChanged();Begin(first);
                check(map.Contains(shader),"Import callback rebuilds actual adapter map");
                Equivalent(Adapt(first,source),"import callback");check(first.MaterialSyncs==1,"Import callback invalidates live receipt");
                Begin(first);Adapt(first,source);source.SetFloat(scalar,13);Adapt(first,source);
                check(target.GetFloat(scalar)==12&&first.MaterialSyncs==0,"Negative control: unnotified direct write is not detected within pass");
                LTDetailMaterialNotifications.NotifyChanged(source);Equivalent(Adapt(first,source),"repair negative control");
                first.ConfigureMaterialNotifications(false,list);source.SetFloat(scalar,14);Begin(first);
                Equivalent(Adapt(first,source),"disabled again");check(first.MaterialSyncs==1&&first.NotifiedMaterialCount==0,"Disabling restores full sync");
                LTDetailMaterialNotifications.NotifyChanged(source);Begin(second);Equivalent(Adapt(second,source),"remaining consumer");
                first.ConfigureMaterialNotifications(true,list);Begin(first);Adapt(first,source);Object.DestroyImmediate(target);
                Begin(first);target=Adapt(first,source);Equivalent(target,"recreated destroyed adapter");check(first.MaterialSyncs==1,"Destroyed target cannot reuse old receipt");
                Debug.Log($"[GPU Live Material Notifications v1] PASS {edits} non-vacuous notified edits on actual LTDetailGpuRenderer; default OFF, explicit list, foreign fallback, native full-copy oracle, cross-pass reuse, two consumers, throwing/reentrant writes, Inspector-style SetDirty, adapter-map reload and disabling. Undo/reimport callbacks are SIMULATED, real user Undo/import not invoked. Negative control confirms unnotified script writes are NOT detectable. Live-scene opt-in settings unchanged. No timing/FPS claim.");
            }
            finally
            {
                Application.logMessageReceived-=NativeError;first.Dispose();second.Dispose();
                foreach(var obj in objects.OfType<Material>())if(obj)Object.DestroyImmediate(obj);
                foreach(var obj in objects.Where(o=>!(o is Material)))if(obj)Object.DestroyImmediate(obj);
                bufferA?.Dispose();bufferB?.Dispose();
            }
        }
        static string[] Selections(LTDetailGpuRenderer gpu,Source source,Camera camera)
        {
            // Editor-fixture-only readback; production remains readback-free.
            var groups=(IDictionary)Field(gpu,"groups");var views=(IDictionary)Field(groups[source],"views");
            var draws=(IList)Field(views[camera],"draws");var result=new string[draws.Count];
            var args=new GraphicsBuffer.IndirectDrawIndexedArgs[1];
            for(int i=0;i<draws.Count;i++)
            {
                ((GraphicsBuffer)Field(draws[i],"args")).GetData(args);
                if(args[0].instanceCount>source.Count)throw new InvalidOperationException("Invalid indirect instance count.");
                var ids=new uint[(int)args[0].instanceCount];
                var selection=Field(draws[i],"selection");
                if(ids.Length>0)((GraphicsBuffer)Field(selection,"visible")).GetData(ids,0,0,ids.Length);
                Array.Sort(ids); // GPU append order is not specified; compare the selected identities.
                result[i]=args[0].indexCountPerInstance+":"+args[0].instanceCount+":"+args[0].startIndex+":"+
                    args[0].baseVertexIndex+":"+args[0].startInstance+"|"+string.Join(",",ids);
            }
            return result;
        }
        [MenuItem("Tools/Local Terrain/Validate GPU Detail CPU Preparation (no scene changes)")]
        public static void Run()
        {
            const string tag="[GPU Detail CPU Preparation v6] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var active=SceneManager.GetActiveScene();
            string Hash(string path){using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return Convert.ToBase64String(sha.ComputeHash(stream));}
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*",SearchOption.AllDirectories)
                .Where(p=>new[]{".cs",".compute",".hlsl",".shader",".ltdetailshader"}.Contains(Path.GetExtension(p)))
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();Mesh mesh=null;Material material=null,expectedMaterial=null;
            var direct=new LTDetailGpuRenderer();var cached=new LTDetailGpuRenderer();int checks=0,frame=0;Exception failure=null;
            void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            try
            {
                var adapter=Resources.Load<LTDetailGpuShader>("LTDetailGpu/GrassWind");
                Check(adapter&&adapter.source&&adapter.indirect&&string.IsNullOrEmpty(adapter.error)&&adapter.indirect.isSupported,
                    "GrassWind GPU adapter not ready; wait for native import. No forced import was attempted.");
                material=new Material(adapter.source){enableInstancing=true,hideFlags=HideFlags.HideAndDontSave,renderQueue=2000};
                if(material.HasProperty("_AddPrecomputedVelocity"))material.SetFloat("_AddPrecomputedVelocity",0);
                mesh=new Mesh{hideFlags=HideFlags.HideAndDontSave,vertices=new[]{Vector3.zero,Vector3.right,Vector3.up},triangles=new[]{0,1,2}};
                mesh.RecalculateNormals();mesh.RecalculateBounds();
                Camera MakeCamera(string name)
                {
                    var go=new GameObject(name){hideFlags=HideFlags.HideAndDontSave};go.SetActive(false);
                    SceneManager.MoveGameObjectToScene(go,preview);var camera=go.AddComponent<Camera>();
                    camera.enabled=false;camera.cullingMask=1;camera.nearClipPlane=.01f;camera.farClipPlane=200;camera.aspect=1;
                    // Never render either camera; only compute and draw arguments are exercised.
                    return camera;
                }
                var a=MakeCamera("Owned GPU CPU check A");var b=MakeCamera("Owned GPU CPU check B");
                LTDetailPrefab Recipe(int levels,bool allModes)
                {
                    var recipe=new LTDetailPrefab{hasLODGroup=levels>1};
                    for(int lod=0;lod<levels;lod++)
                    {
                        var level=new LTDetailPrefab.Level{screenRelativeHeight=lod==0?.2f:.005f};recipe.levels.Add(level);
                        for(int part=0;part<(allModes?8:1);part++)level.parts.Add(new LTDetailPrefab.Part{mesh=mesh,material=material,
                            localMatrix=Matrix4x4.identity,layer=0,renderingLayerMask=1,castShadows=(ShadowCastingMode)(part%4),
                            lightProbes=LightProbeUsage.Off,receiveShadows=true});
                    }
                    return recipe;
                }
                bool Pass(LTDetailGpuRenderer renderer,Source[] sources,Camera camera,bool optimized,bool expectReady)
                {
                    var planes=GeometryUtility.CalculateFrustumPlanes(camera);renderer.Begin(32,++frame,10000,4);
                    if(optimized)renderer.PrepareCameraPass(camera,planes);
                    bool ready=true;foreach(var source in sources)ready&=renderer.TryRender(source,camera,planes,100);
                    Check(renderer.CheckMemoryAccounting(out var audit),audit);
                    if(expectReady)Check(ready&&renderer.FallbackGroups==0,"Unexpected pending/fallback: "+renderer.Status);
                    return ready;
                }
                void Warm(Source[] sources,Camera camera)
                {
                    // Warm one owned group at a time; production allocation quotas still apply.
                    foreach(var source in sources)
                    {
                        bool x=false,y=false;
                        for(int n=0;n<32&&!(x&&y);n++)
                        {x=Pass(direct,new[]{source},camera,false,false);y=Pass(cached,new[]{source},camera,true,false);}
                        Check(x&&y,"Owned GPU fixture did not become ready: "+direct.Status+" / "+cached.Status);
                    }
                }
                void Compare(Source[] sources,Camera camera,bool fallback=false)
                {
                    bool x=Pass(direct,sources,camera,false,!fallback),y=Pass(cached,sources,camera,true,!fallback);
                    Check(x==y&&direct.FallbackGroups==cached.FallbackGroups&&direct.GetFallbackReport()==cached.GetFallbackReport(),"Fallback parity");
                    Check(direct.DrawCalls==cached.DrawCalls&&direct.CullDispatches==cached.CullDispatches,"Submission count parity");
                    if(!fallback&&cached.DrawCalls>0)foreach(var source in sources)
                        Check(Selections(direct,source,camera).SequenceEqual(Selections(cached,source,camera)),"Indirect arguments / selected identities parity");
                }
                var simple=new[]{new Source{Recipe=Recipe(1,false)}};Warm(simple,a);Compare(simple,a);
                Check(Selections(cached,simple[0],a)[0].Contains(":65:"),"Non-vacuous baseline: all 65 candidates must be selected.");
                var split=Recipe(2,true);
                var sources=Enumerable.Range(0,4).Select(i=>new Source{Recipe=split,varied=true}).ToArray();
                for(int i=0;i<sources.Length;i++){sources[i].Entry.limitShadowDistance=i%2==0;sources[i].Entry.shadowDistance=8+i;sources[i].Entry.thinInDistance=i>1;}
                Warm(sources,a);Compare(sources,a);
                Check(cached.PassPlanBuilds==2&&cached.PassMaterialChecks==1&&cached.CameraParameterUploads==1,"Per-pass work must be shared.");
                a.transform.position=new Vector3(2,0,4);a.fieldOfView=40;Compare(sources,a);
                a.orthographic=true;a.orthographicSize=4;Compare(sources,a);
                b.transform.position=new Vector3(-2,0,1);b.transform.rotation=Quaternion.Euler(0,50,0);
                Warm(sources,b);Compare(sources,b);Compare(sources,a);
                split.levels[0].screenRelativeHeight=.07f;Compare(sources,a);
                foreach(var source in sources)source.Entry.shadowDistance=0;Compare(sources,a);
                a.cullingMask=0;Compare(sources,a);Check(cached.DrawCalls==0,"Camera mask edit must be observed.");a.cullingMask=1;
                material.enableInstancing=false;Compare(sources,a);Check(cached.DrawCalls==0,"Instancing edit must be observed.");material.enableInstancing=true;
                material.renderQueue=3100;Compare(sources,a,true);Check(cached.FallbackGroups==sources.Length,"Material edit must invalidate validation next pass.");
                material.renderQueue=2000;Compare(sources,a);
                split.levels[0].parts[0].lightProbes=LightProbeUsage.UseProxyVolume;Compare(sources,a,true);
                split.levels[0].parts[0].lightProbes=LightProbeUsage.Off;Compare(sources,a);

                // Independently reproduce the pre-optimization material sync using
                // native Unity, not another invocation of the new implementation.
                expectedMaterial=new Material(adapter.indirect){hideFlags=HideFlags.HideAndDontSave};
                string[] passNames={"MotionVectors","MOTIONVECTORS","ShadowCaster","DepthOnly","GBuffer","Forward"};
                var adapt=typeof(LTDetailGpuRenderer).GetMethod("Adapt",BindingFlags.Instance|BindingFlags.NonPublic);
                Material Adapt(bool prepared)=>(Material)adapt.Invoke(cached,new object[]{material,prepared});
                void VerifyMaterial(string context)
                {
                    cached.Begin(32,++frame,10000,4);
                    var result=Adapt(true);
                    expectedMaterial.CopyPropertiesFromMaterial(material);expectedMaterial.enableInstancing=true;
                    foreach(var pass in passNames)expectedMaterial.SetShaderPassEnabled(pass,material.GetShaderPassEnabled(pass));
                    Check(result.shader==expectedMaterial.shader&&result.enableInstancing==expectedMaterial.enableInstancing,
                        "Material shader/instancing: "+context);
                    Check(result.renderQueue==expectedMaterial.renderQueue&&result.globalIlluminationFlags==expectedMaterial.globalIlluminationFlags&&
                        result.doubleSidedGI==expectedMaterial.doubleSidedGI,"Material settings: "+context);
                    Check(result.shaderKeywords.OrderBy(k=>k).SequenceEqual(expectedMaterial.shaderKeywords.OrderBy(k=>k)),"Material keywords: "+context);
                    foreach(var pass in passNames)Check(result.GetShaderPassEnabled(pass)==expectedMaterial.GetShaderPassEnabled(pass),"Material pass "+pass+": "+context);
                    var shader=adapter.source;
                    for(int i=0;i<shader.GetPropertyCount();i++)
                    {
                        int id=shader.GetPropertyNameId(i);bool same=true;
                        switch(shader.GetPropertyType(i))
                        {
                            case ShaderPropertyType.Float:case ShaderPropertyType.Range:same=result.GetFloat(id).Equals(expectedMaterial.GetFloat(id));break;
                            case ShaderPropertyType.Int:same=result.GetInteger(id)==expectedMaterial.GetInteger(id);break;
                            case ShaderPropertyType.Color:same=result.GetColor(id).Equals(expectedMaterial.GetColor(id));break;
                            case ShaderPropertyType.Vector:same=result.GetVector(id).Equals(expectedMaterial.GetVector(id));break;
                            case ShaderPropertyType.Texture:same=result.GetTexture(id)==expectedMaterial.GetTexture(id)&&
                                result.GetTextureOffset(id).Equals(expectedMaterial.GetTextureOffset(id))&&
                                result.GetTextureScale(id).Equals(expectedMaterial.GetTextureScale(id));break;
                        }
                        Check(same,"Material property "+shader.GetPropertyName(i)+": "+context);
                    }
                    Check(ReferenceEquals(result,Adapt(true))&&ReferenceEquals(result,Adapt(false)),"Prepared/direct adapted handle reused: "+context);
                }
                VerifyMaterial("initial");VerifyMaterial("unchanged next Begin");
                foreach(var pass in passNames)
                {
                    material.SetShaderPassEnabled(pass,false);VerifyMaterial(pass+" disabled");
                    VerifyMaterial(pass+" still disabled");material.SetShaderPassEnabled(pass,true);VerifyMaterial(pass+" re-enabled");
                }
                // No SetDirty/event needed: animated/script-set values must be seen
                // on the next pass, including properties unrelated to grass controls.
                for(int i=0;i<adapter.source.GetPropertyCount();i++)
                {
                    int id=adapter.source.GetPropertyNameId(i);
                    switch(adapter.source.GetPropertyType(i))
                    {
                        case ShaderPropertyType.Float:case ShaderPropertyType.Range:material.SetFloat(id,.37f);break;
                        case ShaderPropertyType.Int:material.SetInteger(id,3);break;
                        case ShaderPropertyType.Color:material.SetColor(id,new Color(.2f,.4f,.6f,.8f));break;
                        case ShaderPropertyType.Vector:material.SetVector(id,new Vector4(.1f,.3f,.5f,.7f));break;
                        case ShaderPropertyType.Texture:
                            material.SetTextureOffset(id,new Vector2(.2f,.3f));material.SetTextureScale(id,new Vector2(2,3));break;
                    }
                }
                material.renderQueue=2450;material.doubleSidedGI=!material.doubleSidedGI;
                VerifyMaterial("script-set properties without dirty notification");
                material.enableInstancing=false;VerifyMaterial("force adapter instancing after source copy");
                material.enableInstancing=true;VerifyMaterial("source instancing restored");
                var keyword=adapter.source.keywordSpace.keywords.FirstOrDefault(k=>k.isValid);
                if(keyword.isValid)
                {
                    material.SetKeyword(keyword,true);VerifyMaterial("keyword enabled");
                    material.SetKeyword(keyword,false);VerifyMaterial("keyword disabled");
                }
                CheckMaterialNotifications(adapter.source,adapter.indirect,Check);
                CheckLiveMaterialNotifications(adapter.source,adapter.indirect,Check);
            }
            catch(Exception ex){failure=ex;}
            finally
            {
                direct.Dispose();cached.Dispose();if(mesh)Object.DestroyImmediate(mesh);if(material)Object.DestroyImmediate(material);
                if(expectedMaterial)Object.DestroyImmediate(expectedMaterial);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                if(SceneManager.GetActiveScene()!=active)failure=new InvalidOperationException("Active user scene changed.",failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);
            else Debug.Log(tag+$"PASS {checks} checks; cached/direct native compute selections and indirect arguments match; cameras, LOD/shadow partitions, thinning, material invalidation and fallback; native adapter properties/keywords/pass flags match independent full-copy reference; prototype AND actual production notification cache tested on temporary materials (see separate coverage/cost reports). Real Undo/reimport event delivery and live-scene performance NOT tested. No user materials opted in. Owned preview scene only, scenes/assets/settings unchanged. No FPS, image or driver-wide memory measurement.");
        }
    }
}
