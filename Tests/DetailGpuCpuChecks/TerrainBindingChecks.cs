using LocalTerrainPrototype;
using UnityEngine;

static partial class Program
{
    static void TerrainBindingChecks()
    {
        var camera=new Camera{name="terrain-cache"};var recipe=Recipe(Material());
        var sources=Enumerable.Range(0,24).Select(i=>new Source{id=i,Recipe=recipe}).ToArray();
        var map=new LTGrassTerrainMap();
        using var pair=new Pair();pair.old.GrassTerrain=pair.next.GrassTerrain=map;
        pair.Warm(sources,camera);
        var stable=pair.Compare(sources,camera);
        Check(stable.before.terrainWrites>0&&stable.after.terrainWrites==0,"steady map needs no repeated MPB writes");
        Console.WriteLine($"Terrain binding, 24 warm groups, identical draw/dispatch payloads: MPB writes {stable.before.terrainWrites}->{stable.after.terrainWrites}. Operation counts, not native CPU time or FPS.");
        for(int i=0;i<4;i++)Check(pair.Compare(sources,camera).after.terrainWrites==0,"reuse across frames");
        map.texture.pixelRevision++;
        Check(pair.Compare(sources,camera).after.terrainWrites==0,"same texture's new pixels need no binding update");
        void Changed(string label)
        {
            var changed=pair.Compare(sources,camera);Check(changed.after.terrainWrites==changed.before.terrainWrites,label);
            Check(pair.Compare(sources,camera).after.terrainWrites==0,label+" followed by stable reuse");
        }
        map.u=new Vector4(1,0,0,.0000001f);Changed("small UV change uses exact equality");
        map.v=new Vector4(0,0,2,-7);Changed("extent/world transform change");
        map.texture=new Texture{name="replacement"};Changed("texture recreation");
        map.ready=false;Changed("released texture / readiness loss");
        map.ready=true;Changed("map recovery");
        pair.old.GrassTerrain=pair.next.GrassTerrain=null;Changed("map removed");
        pair.old.GrassTerrain=pair.next.GrassTerrain=map;Changed("map restored");
        var other=new Camera{name="other camera"};pair.Warm(sources,other);
        Check(pair.Compare(sources,other).after.terrainWrites==0,"independent second-camera blocks");
        map.u=new Vector4(2,0,0,0);pair.Compare(sources,other);Changed("first camera observes changes after second camera");
        pair.Compare(sources,camera,false,i=>map.v=new Vector4(0,0,1,i));
        Check(pair.next.TerrainBindingUpdates>0,"direct callers see changes between TryRender calls");
        pair.next.Remove(sources[0]);pair.old.Remove(sources[0]);pair.Warm(new[]{sources[0]},camera);
        pair.Compare(sources,camera); // New private blocks cannot inherit a removed group's receipt.

        var block=new MaterialPropertyBlock();var cache=new LTGrassTerrainBinding.Cache();
        var value=LTGrassTerrainMap.CaptureBinding(map);
        Check(cache.Apply(block,value)&&!cache.Apply(block,value),"first bind and stable receipt");
        var fresh=new MaterialPropertyBlock();Check(cache.Apply(fresh,value),"replacement block always initialized");
        fresh.values.Clear();cache.Reset();Check(cache.Apply(fresh,value),"explicit reset after external Clear");
        var legacy=new MaterialPropertyBlock();LTGrassTerrainMap.Bind(legacy,map);
        Check(Trace.Values(fresh.values)==Trace.Values(legacy.values),"all four production binding values equal direct reference");
    }
}
