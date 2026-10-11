using System.Reflection;
using LocalTerrainPrototype;
using UnityEngine;
using Object=UnityEngine.Object;

internal static class LiveMaterialNotificationChecks
{
    static object Field(object value,string name)=>value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);
    static Material Adapt(LTDetailGpuRenderer gpu,Material source)=>(Material)typeof(LTDetailGpuRenderer)
        .GetMethod("Adapt",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(gpu,new object[]{source,true});
    public static void Run(Action<bool,string> check)
    {
        int frame=100000;
        void Begin(LTDetailGpuRenderer gpu)=>gpu.Begin(64,++frame,10000,16);
        using var gpu=new LTDetailGpuRenderer();using var second=new LTDetailGpuRenderer();
        var source=new Material(Resources.Source);var foreign=new Material(Resources.Source);
        var sources=new[]{source,source,null};
        source.SetFloat("live",1);foreign.SetFloat("live",10);Time.realtimeSinceStartupAsDouble=0;
        Begin(gpu);var target=Adapt(gpu,source);Begin(gpu);Adapt(gpu,source);
        check(gpu.MaterialSyncs==1&&gpu.MaterialReuses==0,"default OFF always copies next pass");
        gpu.ConfigureMaterialNotifications(false,sources);Begin(gpu);Adapt(gpu,source);
        check(gpu.NotifiedMaterialCount==0&&gpu.MaterialSyncs==1,"disabled list cannot grant caching");
        gpu.ConfigureMaterialNotifications(true,Array.Empty<Material>());Begin(gpu);Adapt(gpu,source);
        check(gpu.MaterialSyncs==1,"enabled with empty list still full copies");
        gpu.ConfigureMaterialNotifications(true,sources);Begin(gpu);Adapt(gpu,source);
        check(gpu.NotifiedMaterialCount==1&&gpu.MaterialSyncs==1,"deduplicated explicit registration first sync");
        var receipt=Field(gpu,"materialReceipts");
        for(int i=0;i<64;i++)
        {
            gpu.ConfigureMaterialNotifications(true,sources);Begin(gpu);
            check(ReferenceEquals(target,Adapt(gpu,source))&&gpu.MaterialSyncs==0&&gpu.MaterialReuses==1,"unchanged live source reused across Begin "+i);
            check(ReferenceEquals(receipt,Field(gpu,"materialReceipts")),"receipt storage retained "+i);
        }
        Begin(gpu);Adapt(gpu,foreign);foreign.SetFloat("live",11);Begin(gpu);var foreignTarget=Adapt(gpu,foreign);
        check(foreignTarget.GetFloat("live")==11&&gpu.MaterialSyncs==1,"unlisted material observes unnotified writes");
        source.SetFloat("live",2);LTDetailMaterialNotifications.NotifyChanged(source);Begin(gpu);Adapt(gpu,source);
        check(target.GetFloat("live")==2&&gpu.MaterialSyncs==1,"notified arbitrary property copied");
        source.SetFloat("live",3);LTDetailMaterialNotifications.NotifyChanged(source);Adapt(gpu,source);
        check(target.GetFloat("live")==3&&gpu.MaterialSyncs==2,"notification also invalidates same-pass cache");
        Begin(gpu);Adapt(gpu,source);source.SetFloat("live",4);Begin(gpu);Adapt(gpu,source);
        check(target.GetFloat("live")==3&&gpu.MaterialSyncs==0,"negative control: unnotified opted-in write is NOT detected");
        LTDetailMaterialNotifications.NotifyChanged(source);Adapt(gpu,source);
        check(target.GetFloat("live")==4,"notification repairs deliberate stale negative control");
        second.ConfigureMaterialNotifications(true,new[]{source});Begin(second);var otherTarget=Adapt(second,source);
        LTDetailMaterialNotifications.Mutate(source,m=>m.SetFloat("live",5));Begin(gpu);Begin(second);Adapt(gpu,source);Adapt(second,source);
        check(target.GetFloat("live")==5&&otherTarget.GetFloat("live")==5&&gpu.MaterialSyncs==1&&second.MaterialSyncs==1,"one write reaches both consumers");
        LTDetailMaterialNotifications.Mutate(source,m=>
        {
            m.SetFloat("live",6);int before=gpu.MaterialSyncs;Adapt(gpu,source);Adapt(gpu,source);
            check(gpu.MaterialSyncs==before+2,"in-progress writes cannot reuse, even within a pass");
            LTDetailMaterialNotifications.Mutate(m,n=>n.SetFloat("live",7));Adapt(gpu,source);
            check(gpu.MaterialSyncs==before+3,"nested mutation keeps outer write in progress");
        });
        Adapt(gpu,source);int stable=gpu.MaterialSyncs;Adapt(gpu,source);
        check(gpu.MaterialSyncs==stable&&target.GetFloat("live")==7,"mutation completion sync then reuse");
        try{LTDetailMaterialNotifications.Mutate(source,m=>{m.SetFloat("live",8);throw new Exception("expected write failure");});}
        catch(Exception ex)when(ex.Message=="expected write failure"){}
        Adapt(gpu,source);check(target.GetFloat("live")==8,"partial throwing write invalidates cache");
        source.SetFloat("live",9);LTDetailMaterialNotifications.NotifyChanged(source);Material.FailNextCopy=true;
        bool failed=false;try{Adapt(gpu,source);}catch(TargetInvocationException){failed=true;}
        check(failed,"actual backend copy failure injected");Adapt(gpu,source);
        check(target.GetFloat("live")==9,"failed sync retries without reusable receipt");
        LTDetailMaterialNotifications.NotifyChanged(source);
        Material.AfterCopy=()=>{Material.AfterCopy=null;LTDetailMaterialNotifications.NotifyChanged(source);};
        Adapt(gpu,source);stable=gpu.MaterialSyncs;Adapt(gpu,source);
        check(gpu.MaterialSyncs==stable+1,"notification during copy prevents receipt publication");
        LTDetailMaterialNotifications.InvalidateAll();Begin(gpu);Adapt(gpu,source);
        check(gpu.MaterialSyncs==1,"global invalidation drops receipts without asset writes");
        var adapters=(Dictionary<Shader,Shader>)Field(gpu,"adapters");var replacement=new Shader{name="replacement"};
        adapters[Resources.Source]=replacement;Begin(gpu);Adapt(gpu,source);
        check(target.shader==replacement&&gpu.MaterialSyncs==1,"adapter shader identity guard");
        target.shader=Resources.Source;Begin(gpu);Adapt(gpu,source);
        check(target.shader==replacement&&gpu.MaterialSyncs==1,"target shader identity guard");
        var changedSource=new Shader{name="source2"};adapters[changedSource]=replacement;source.shader=changedSource;
        Begin(gpu);Adapt(gpu,source);check(gpu.MaterialSyncs==1,"source shader identity guard without notification");
        source.shader=Resources.Source;
        LTDetailMaterialNotifications.InvalidateAll(true);Begin(gpu);Adapt(gpu,source);
        check(target.shader==Resources.Indirect&&gpu.MaterialSyncs==1,"adapter import invalidation reloads resource map");
        Object.DestroyImmediate(target);Begin(gpu);target=Adapt(gpu,source);
        check(target&&gpu.MaterialSyncs==1,"destroyed owned adapter recreated");
        Time.realtimeSinceStartupAsDouble=10;Begin(gpu);
        check(gpu.MaterialCount==0&&((System.Collections.IDictionary)Field(gpu,"materialReceipts")).Count==0,"expiration releases targets and receipts");
        target=Adapt(gpu,source);check(target&&gpu.MaterialSyncs==1,"registered source can recreate expired target");
        gpu.ConfigureMaterialNotifications(false,sources);source.SetFloat("live",12);Begin(gpu);Adapt(gpu,source);
        check(gpu.NotifiedMaterialCount==0&&target.GetFloat("live")==12,"disable revokes this backend and restores safe path");
        LTDetailMaterialNotifications.NotifyChanged(source);Begin(second);otherTarget=Adapt(second,source);
        check(otherTarget.GetFloat("live")==12,"revoking one backend preserves other registration");
        gpu.ConfigureMaterialNotifications(true,sources);Begin(gpu);Adapt(gpu,source);
        check(gpu.MaterialSyncs==1,"re-registration cannot reuse previous receipt");
        sources[0]=foreign;sources[1]=null;gpu.ConfigureMaterialNotifications(true,sources);Begin(gpu);Adapt(gpu,source);
        check(gpu.NotifiedMaterialCount==1&&gpu.MaterialSyncs==1,"in-place list edit removes old source");
        gpu.Dispose();second.Dispose();
        var states=(System.Collections.IDictionary)typeof(LTDetailMaterialNotifications).GetField("states",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
        check(states.Count==0,"last consumer disposal releases all static source references");
        LTDetailMaterialNotifications.Mutate(source,m=>m.SetFloat("live",13));
        check(states.Count==0,"temporary write registration also releases unowned material");
        Begin(gpu);Adapt(gpu,source);check(gpu.NotifiedMaterialCount==0&&gpu.MaterialSyncs==1,"disposed backend may reinitialize without opt-in");
        Object.DestroyImmediate(source);Object.DestroyImmediate(foreign);Time.realtimeSinceStartupAsDouble=0;
    }
}
