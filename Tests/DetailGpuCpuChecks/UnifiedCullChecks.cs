using LocalTerrainPrototype;
using UnityEngine;
using UnityEngine.Rendering;

static partial class Program
{
    static void UnifiedCullChecks()
    {
        int compared=0;
        for(int lods=1;lods<=8;lods++)foreach(bool shadowLimit in new[]{false,true})
        {
            using var pair=new Pair();var camera=new Camera{name="unified"};
            var recipe=Recipe(Material(),lods);
            var sources=new[]{new Source{Recipe=recipe,Entry=new(){limitShadowDistance=shadowLimit}}};
            pair.Warm(sources,camera);pair.next.UnifiedCulling=true;
            void CompareDraws(bool prepared=true)
            {
                var old=pair.Pass(false,sources,camera,prepared);var current=pair.Pass(true,sources,camera,prepared);
                var before=old.events.Where(e=>e.StartsWith("draw:")).ToArray();
                var after=current.events.Where(e=>e.StartsWith("draw:")).ToArray();
                Check(before.SequenceEqual(after),"unified culling preserves draw payloads/order and counts (mocked cull)");
                Check(old.accepted.SequenceEqual(current.accepted)&&old.fallback==current.fallback,"unified readiness/fallback parity");
                Check(current.dispatch<=old.dispatch,"unified never increases dispatch count");
                Check(current.reserved-old.reserved<=28,"padding pool <= seven uints, counted in budget");
                compared+=before.Length;
            }
            // Warm optional padding in the upload budget; no forced root/view rebuild.
            CompareDraws();CompareDraws();
            int oldDispatch=pair.old.CullDispatches,newDispatch=pair.next.CullDispatches;
            Check(newDispatch==(oldDispatch+7)/8,"one dispatch per up to eight active partitions");
            if(lods==8&&shadowLimit)Console.WriteLine($"Unified cull: {oldDispatch}->{newDispatch} dispatches for 8 LODs/all shadow modes; identical {pair.next.DrawCalls} draw payloads (API doubles, not GPU speed).");
            camera.cullingMask=1;CompareDraws();camera.cullingMask=0;CompareDraws();
            camera.cullingMask=-1;CompareDraws(false);
            var original=recipe.levels[0].parts[0].material;original.enableInstancing=false;CompareDraws();original.enableInstancing=true;
            pair.next.UnifiedCulling=false;CompareDraws();pair.next.UnifiedCulling=true;CompareDraws();
            pair.next.Remove(sources[0]);Check(pair.next.CheckMemoryAccounting(out _),"padding remains owned after last group unload");
        }
        using(var pair=new Pair())
        {
            var r=Recipe(Material(),1);r.levels[0].parts.RemoveRange(1,r.levels[0].parts.Count-1);
            var source=new[]{new Source{Recipe=r}};var camera=new Camera{name="one"};pair.Warm(source,camera);
            pair.next.UnifiedCulling=true;pair.Compare(source,camera);
            Check(pair.next.UnifiedCullDispatches==0,"single selection retains legacy kernel and allocation count");
        }
        ComputeShader.MultiSupported=false;
        try
        {
            using var pair=new Pair();pair.next.UnifiedCulling=true;
            var source=new[]{new Source{Recipe=Recipe(Material(),3)}};var camera=new Camera{name="unsupported multi"};
            pair.Warm(source,camera);pair.Compare(source,camera);
            Check(pair.next.UnifiedCullDispatches==0,"unsupported optional kernel retains complete legacy rendering");
        }
        finally{ComputeShader.MultiSupported=true;}
        using(var pair=new Pair())
        {
            var source=new[]{new Source{Recipe=Recipe(Material(),1)}};var camera=new Camera{name="padding failure"};
            pair.Warm(source,camera);pair.next.UnifiedCulling=true;GraphicsBuffer.FailNextAllocation=true;
            pair.Pass(true,source,camera);
            Check(pair.next.UnifiedCullDispatches==0&&pair.next.DrawCalls>0,"optional allocation failure falls back without lost draws");
            pair.Pass(true,source,camera);Check(pair.next.UnifiedCullDispatches==1,"padding allocation retries next pass");
            pair.next.Begin(0,++frame,1000000,16);
            Check(pair.next.ResidentBytes==0&&pair.next.ReservedBytes==0&&pair.next.BufferCount==0,"budget shrink releases roots, outputs and padding pool");
            Check(pair.next.CheckMemoryAccounting(out _),"accounting after pressure cleanup");
        }
        Console.WriteLine($"PASS unified CPU routing: {compared} draw payload comparisons; 1..8 LODs, shadow partitions, camera masks, disabled parts, toggle, direct calls and buffer lifetime.");
    }
}
