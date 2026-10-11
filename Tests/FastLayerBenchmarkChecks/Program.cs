using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LocalTerrainPrototype;

static class Program
{
    static int count;
    static void Check(bool value,string why){count++;if(!value)throw new Exception(why);}
    static void Main()
    {
        // Old serialized enum ids must select ordinary rendering without changing
        // the serialized field; id 6 must continue selecting fast layers.
        var world=new LTWorld();
        foreach(int id in new[]{int.MinValue,-1,0,1,2,3,4,5,6,7,int.MaxValue})
        {
            world.surfaceRenderMode=(LTSurfaceRenderMode)id;
            Check(world.EffectiveSurfaceRenderMode==(id==6?LTSurfaceRenderMode.FastLiveLayers:LTSurfaceRenderMode.LiveLayers),"Compatible rendering for stored mode "+id);
            Check(world.FastLiveLayersActive==(id==6),"Fast toggle for stored mode "+id);
            Check((int)world.surfaceRenderMode==id,"Reading old mode does not rewrite serialized state");
        }
        Check(Enum.GetValues<LTSurfaceRenderMode>().Select(v=>(int)v).SequenceEqual(new[]{0,6}),"Only ordinary and fast modes remain, with stable ids");
        var camera=new UnityEngine.Camera();var anotherCamera=new UnityEngine.Camera();var owner=new object();var intruder=new object();
        Check(!LTDetailBenchmarkIsolation.Skip(camera),"No suppression without lease");
        Check(!LTDetailBenchmarkIsolation.Acquire(null,camera),"Reject null owner");
        Check(!LTDetailBenchmarkIsolation.Acquire(owner,null),"Reject null camera");
        Check(LTDetailBenchmarkIsolation.Acquire(owner,camera),"Acquire isolation");
        Check(!LTDetailBenchmarkIsolation.Acquire(intruder,anotherCamera),"No competing lease");
        Check(!LTDetailBenchmarkIsolation.Skip(anotherCamera),"Other camera unaffected");
        Check(LTDetailBenchmarkIsolation.Skip(camera)&&LTDetailBenchmarkIsolation.SuppressedCallbacks==1,"Target skipped and counted");
        LTDetailBenchmarkIsolation.Release(intruder);
        Check(LTDetailBenchmarkIsolation.IsOwner(owner)&&LTDetailBenchmarkIsolation.Camera==camera,"Wrong owner cannot release");
        camera.alive=false;Check(!LTDetailBenchmarkIsolation.Skip(camera),"Destroyed camera ignored");camera.alive=true;
        LTDetailBenchmarkIsolation.Release(owner);
        Check(!LTDetailBenchmarkIsolation.Skip(camera)&&LTDetailBenchmarkIsolation.Camera==null,"Cancellation releases suppression");
        LTDetailBenchmarkIsolation.Release(owner);Check(LTDetailBenchmarkIsolation.Acquire(intruder,anotherCamera),"Idempotent release and next run");
        Check(LTDetailBenchmarkIsolation.SuppressedCallbacks==0,"Counter reset");LTDetailBenchmarkIsolation.Release(intruder);
        Check(Enumerable.Range(0,4).Select(LTFastLayerBenchmarkPolicy.BeforeHeight).SequenceEqual(new[]{false,true,true,false}),"Select AFTER/BEFORE/BEFORE/AFTER height");
        Check(LTFastLayerBenchmarkPolicy.Mode(false).Contains("AFTER")&&LTFastLayerBenchmarkPolicy.Mode(true).Contains("BEFORE"),"Selection timing labelled correctly");
        Check(LTFastLayerBenchmarkPolicy.LayerCount(new[]{.25f,.25f,.25f,.25f,0})==4,"Four overlapping layers");
        Check(LTFastLayerBenchmarkPolicy.LayerCount(new[]{1f,0,0,0,0})==1,"Five palette entries do not mean five layers per point");
        Check(LTFastLayerBenchmarkPolicy.LayerCount(new[]{.95f,.02f,.01f,.01f,.01f})==1,"Ignore insignificant traces");
        Check(LTFastLayerBenchmarkPolicy.LayerCount(new[]{50f,50f,50f,50f})==4,"Normalize quantized coverage");
        Check(LTFastLayerBenchmarkPolicy.LayerCount(new float[12])==0,"Empty maps");
        Check(!LTFastLayerBenchmarkPolicy.FrameSuitable(81,2,2),"Reject the previous sparse benchmark view");
        Check(!LTFastLayerBenchmarkPolicy.FrameSuitable(441,441,350),"Ground alone does not prove overlap");
        Check(!LTFastLayerBenchmarkPolicy.FrameSuitable(441,432,400),"Reject more than two percent holes/sky");
        Check(LTFastLayerBenchmarkPolicy.FrameSuitable(441,433,353),"Accept threshold coverage");
        Check(!LTFastLayerBenchmarkPolicy.FrameSuitable(0,0,0),"Do not accept an unprobed view");
        Check(LTFastLayerBenchmarkPolicy.FindPatches(Enumerable.Repeat((byte)1,81).ToArray(),9,9).Length==0,"Separated single-layer paints are not a blend site");
        var grid=new byte[81];
        for(int y=2;y<7;y++)for(int x=1;x<6;x++)grid[y*9+x]=4;
        var patch=LTFastLayerBenchmarkPolicy.FindPatches(grid,9,9).First();
        Check(patch.x==1&&patch.y==2&&patch.size==5&&patch.layers==4,"Locate the broadest four-layer square");
        grid[4*9+3]=1;
        Check(LTFastLayerBenchmarkPolicy.FindPatches(grid,9,9).All(p=>!(p.x<=3&&p.y<=4&&p.x+p.size>3&&p.y+p.size>4)),"A missing overlap texel breaks the region");
        Check(LTFastLayerBenchmarkPolicy.FindPatches(Enumerable.Repeat((byte)5,25).ToArray(),5,5).First().layers==5,"Prefer five layers for equal footprints");
        Check(LTFastLayerBenchmarkPolicy.FindPatches(Enumerable.Repeat((byte)6,25).ToArray(),5,5).Length==0,"Do not mislabel six layers as four/five");
        var stripe=new byte[81];for(int x=0;x<9;x++)stripe[4*9+x]=4;
        Check(LTFastLayerBenchmarkPolicy.FindPatches(stripe,9,9).Length==0,"A one-texel stripe cannot fill the frame");
        Check(!LTFastLayerBenchmarkPolicy.InRange(double.NaN,80),"NaN range");
        Check(!LTFastLayerBenchmarkPolicy.InRange(double.PositiveInfinity,80),"Infinite range");
        Check(!LTFastLayerBenchmarkPolicy.InRange(-1,80),"Negative distance");
        Check(!LTFastLayerBenchmarkPolicy.InRange(0,0),"Zero radius");
        Check(LTFastLayerBenchmarkPolicy.InRange(6400,80),"Inclusive range");
        Check(!LTFastLayerBenchmarkPolicy.InRange(6400.1,80),"Outside range");
        Check(LTFastLayerBenchmarkPolicy.InRange(0,80),"Camera inside bounds");
        Check(!LTFastLayerBenchmarkPolicy.Warm(1.99,120),"Time warmup");
        Check(!LTFastLayerBenchmarkPolicy.Warm(10,60),"Frame warmup");
        Check(LTFastLayerBenchmarkPolicy.Warm(2,61),"Warmup done");
        var farSelection=LTFastLayerBenchmarkPolicy.SelectionSummary(new[]{("outside radius",130d),("base only",1d)},80);
        Check(farSelection.Contains("130")&&farSelection.Contains("увеличьте радиус"),"Explain camera distance instead of material restrictions");
        var excludedSelection=LTFastLayerBenchmarkPolicy.SelectionSummary(new[]{("road/asphalt",3d),("displacement",4d)},80);
        Check(excludedSelection.Contains("Дороги/асфальт: 1")&&excludedSelection.Contains("не снимет"),"Material exclusions are not solved by moving closer");
        Check(LTFastLayerBenchmarkPolicy.SelectionSummary(new[]{("outside frustum",5d)},80).Contains("Наведите камеру"),"Frustum has its own remedy");
        Check(LTFastLayerBenchmarkPolicy.SelectionSummary(Array.Empty<(string,double)>(),80).Contains("Нет данных"),"Missing runtime snapshot distinguished");
        Check(LTFastLayerBenchmarkPolicy.SelectionSummary(new[]{("selected",5d)},80).Contains("Подходят: 1"),"Eligible count visible");
        var original=new object();var own=new object();var external=new object();
        var restore=LTFastLayerBenchmarkPolicy.Restore(new[]{own},new[]{original},own);
        Check(restore[0]==original,"Normal restoration");
        Check(LTFastLayerBenchmarkPolicy.Restore(restore,new[]{original},own)[0]==original,"Idempotent restoration");
        Check(LTFastLayerBenchmarkPolicy.Restore(new[]{external},new[]{original},own)[0]==external,"Preserve external edit");
        Check(LTFastLayerBenchmarkPolicy.Restore(Array.Empty<object>(),new[]{original},own).Length==0,"Preserve external array removal");
        var current=new[]{own,external,own};
        restore=LTFastLayerBenchmarkPolicy.Restore(current,new[]{original},own);
        Check(restore.SequenceEqual(new[]{original,external,null}),"No owned materials left after array growth");
        Check(current[0]==own&&current[2]==own,"No mutation of input snapshot");
        for(int phase=0;phase<4;phase++)for(int blocks=0;blocks<4;blocks++)for(int ns=-1;ns<=1;ns++)for(int frame=0;frame<2;frame++)
            Check(LTFastLayerBenchmarkStats.Accept(frame,0,blocks,ns)==(frame!=0&&blocks==1&&ns>0),"Valid GPU observation");
        Check(LTFastLayerBenchmarkStats.Quantile(new[]{9d,1,3,5},.5)==4,"Median");
        Check(double.IsNaN(LTFastLayerBenchmarkStats.Quantile(Array.Empty<double>(),.5)),"No samples is N/A");
        Check(LTFastLayerBenchmarkStats.Reduction(10,8)==20,"Positive improvement");
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        string Read(string p)=>File.ReadAllText(Path.Combine(root,"Assets/TerrainSystem/LocalTerrain",p));
        var window=Read("Editor/LTFastLayerBenchmark.cs");
        foreach(var forbidden in new[]{"SaveScene(","SaveAssets(","SetDirty(","SetActive(","new RenderTexture","RebuildSurfaceCache(","ClearDirtiness(","SetPropertyBlock("})
            Check(!window.Contains(forbidden),"No unsafe mutation: "+forbidden);
        foreach(var field in new[]{"surfaceRenderMode","enableGlobalLayerMaps","QualitySettings.vSyncCount","Application.targetFrameRate"})
            Check(!Regex.IsMatch(window,Regex.Escape(field)+@"\s*=(?!=)"),"No serialized mode/quality assignment: "+field);
        foreach(var required in new[]{"beforeAssemblyReload","hierarchyChanged","sceneSaving","OnDisable()","Undo.undoRedoPerformed","playModeStateChanged",
            "Time.renderedFrameCount","scopePhase","gpuSampleBlockCount","supportsGpuRecorder","ShaderUtil.anythingCompiling","ShaderUtil.CompilePass",
            "hashes.All","scenes.All","worldSettings","INCOMPLETE / INVALID","finally","RestoreRendering();","if(scopeOpen)return;",
            "_LT_FAST_LAYERS","_LT_FAR_ONLY","_LTGlobalParams","fastLayerTopTwo","world.paintBenchmarkRunning=true","world.paintBenchmarkRunning=false",
            "camera!=target","d.filter.sharedMesh==d.mesh","d.chunk.updatedAt==d.updated","LTDetailBenchmarkIsolation.Release(this)","LTDetailBenchmarkIsolation.Acquire(this,target)","Logs/FastLayerBenchmarks"})
            Check(window.Contains(required),"Safety/source contract: "+required);
        var siteSource=Read("Editor/LTFastLayerBenchmarkSite.cs");
        Check(siteSource.Contains("HasPropertyBlock()")&&siteSource.Contains("mesh.isReadable"),"Auto site keeps material/mesh exclusions");
        Check(!window.Contains("DisableKeyword(\"_LT_FAST_LAYERS\")"),"Both arms stay in the fast shader path");
        Check(window.Contains("m.DisableKeyword(\"_LT_FAST_COLOR_REMAINDER\")")&&window.Contains("!d.assigned.IsKeywordEnabled(\"_LT_FAST_COLOR_REMAINDER\")"),"Color-only remainder disabled and verified in both strict A/B arms");
        Check(window.Contains("new Vector4(report.blendBudget,report.projectionCutoff,1,beforeHeight?1:0)")&&
            window.Contains("new Vector4(report.blendBudget,report.projectionCutoff,1,BeforeHeight?1:0)"),"Both top-two arms differ only in selection timing, including binding verification");
        Check(window.Contains("RestoreCamera();preparing=false;")&&siteSource.Contains("view.in2DMode=in2D"),"Camera restored with previous view mode");
        var isolation=Read("LTDetailBenchmarkIsolation.cs");
        Check(isolation.StartsWith("#if UNITY_EDITOR"),"No player isolation code");
        var snapshot=Read("LTPaintSurfaceBenchmark.cs");
        Check(snapshot.Contains("roadSampling=roads")&&!snapshot.Contains("\"road/asphalt\"")&&snapshot.Contains("s.displacementActive")&&snapshot.Contains("_LTBaseOnly"),"Road chunks eligible; geometry exclusions remain");
        Check(window.Contains("item.roadSampling")&&window.Contains("m.EnableKeyword(\"_LT_FAST_ROADS\")"),"Both A/B arms preserve road projection from any authored mode");
        Check(isolation.Contains("camera!=Camera")&&isolation.Contains("if(!IsOwner(token))return;"),"Owned camera-scoped suppression");
        var renderer=Read("LTDetailRenderer.cs");
        int begin=renderer.IndexOf("void Render(ScriptableRenderContext");
        Check(renderer.IndexOf("LTDetailBenchmarkIsolation.Skip(camera)",begin)<renderer.IndexOf("gpuRenderer.Begin(",begin),"Skip before any procedural GPU submission");
        Console.WriteLine($"PASS Fast layer benchmark: {count} managed assertions. Policy/statistics + source contracts; NOT a native Unity rendering or performance result.");
    }
}
