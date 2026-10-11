using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

static class Program
{
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static (LTWorld,LTPaintRuntime,List<LTPaintStamp>) Setup(int count=1)
    {
        LTPaintWeightWork.Drain();var w=new LTWorld();var r=new LTPaintRuntime();
        for(int i=0;i<count;i++)w.rocks.Add(new LTMeshStamp{world=w});
        var s=new List<LTPaintStamp>{new(){revision=1,HasTerrainFilters=true}};r.TickTest(w,s);
        Check(r.bakes==count&&w.paintCpu.LastTick.rockWeightCpuBakes==count,"initial maps keep reference path");return(w,r,s);
    }
    static void Main()
    {
        var(w,r,s)=Setup(2);var hashes=w.rocks.Select(r.Hash).ToArray();int binds=r.binds;
        int returned=LTPaintWeightInput.returns;
        s[0].revision=2;r.TickTest(w,s);
        Check(LTPaintWeightInput.returns==returned+2,"both scheduled inputs return immediately after independent job copies");
        Check(LTPaintWeightWork.live.Count==2&&r.PendingRoadSampling&&r.bakes==2&&r.binds==binds,"schedule preserves old maps and palette");
        Check(w.paintCpu.LastTick.rockWeightJobs==2&&w.paintCpu.LastTick.weightJobs==2,"rock diagnostics are subset of total");
        r.TickTest(w,s);Check(r.prepares==2&&r.PendingRoadSampling&&r.binds==binds,"running jobs never block, reschedule or rebind");
        LTPaintWeightWork.Finish();r.TickTest(w,s,usedBudget:1);
        Check(r.bakes==2&&r.PendingRoadSampling,"terrain and rocks share result budget");
        r.TickTest(w,s);Check(r.bakes==3&&r.PendingRoadSampling&&r.lastPixel==2,"one ready map publishes per poll");
        Check(r.Hash(w.rocks[0])!=hashes[0]&&r.Hash(w.rocks[1])==hashes[1],"only published dependencies commit");
        r.TickTest(w,s);Check(r.bakes==4&&!r.PendingRoadSampling&&LTPaintWeightWork.live.Count==0,"second map settles on later poll");
        r.TickTest(w,s);Check(r.bakes==4&&r.prepares==2,"unchanged masks stay cached");
        r.DisposeTest();

        (w,r,s)=Setup(2);s[0].revision=2;r.TickTest(w,s);var stale=LTPaintWeightWork.live.ToArray();
        s[0].revision=3;r.TickTest(w,s);
        Check(stale.All(j=>j.retired&&!j.released&&j.reads==0)&&r.PendingRoadSampling&&r.bakes==2,"stale running jobs retire without reading/freeing busy buffers");
        Check(r.prepares==2&&w.paintCpu.LastTick.rockWeightDiscards==2,"full retired pool waits without CPU fallback");
        LTPaintWeightWork.Finish();r.TickTest(w,s);LTPaintWeightWork.Finish();r.TickTest(w,s);r.TickTest(w,s);
        Check(r.lastPixel==3&&stale.All(j=>j.reads==0)&&!r.PendingRoadSampling,"only newest revision publishes");r.DisposeTest();

        (w,r,s)=Setup();int saved=r.Hash(w.rocks[0]);s[0].revision=2;r.TickTest(w,s);var undo=LTPaintWeightWork.live.Single();
        LTPaintWeightWork.Finish();s[0].revision=1;r.TickTest(w,s);
        Check(undo.retired&&undo.reads==0&&r.Hash(w.rocks[0])==saved&&r.bakes==1&&!r.PendingRoadSampling,"Undo to published state discards even a completed stale map");
        s[0].revision=2;r.TickTest(w,s);var baseOnly=LTPaintWeightWork.live.Single();r.TickTest(w,new());
        Check(baseOnly.retired&&r.bakes==1&&!r.PendingRoadSampling,"base-only cancels pending work without allocating a map");r.DisposeTest();

        foreach(string change in new[]{"palette","bounds","terrain","base","drain"})
        {
            (w,r,s)=Setup();s[0].revision=2;r.TickTest(w,s);var job=LTPaintWeightWork.live.Single();LTPaintWeightWork.Finish();
            switch(change)
            {
                case "palette":s[0].EffectiveLayer=new();break;
                case "bounds":w.rocks[0].renderer.bounds=new Bounds{center=new Vector3(7,0,5),extents=new Vector3(5,1,5)};break;
                case "terrain":w.terrainVersion++;break;
                case "base":w.baseLayer=new();break;
                case "drain":LTPaintWeightWork.Drain();break;
            }
            r.TickTest(w,s);Check(job.retired&&job.reads==0&&r.bakes==1&&r.PendingRoadSampling,"changed "+change+" rejects old result");r.DisposeTest();
        }
        foreach(string removal in new[]{"disabled","removed","overflow","dispose","no mesh","opt out"})
        {
            (w,r,s)=Setup();var rock=w.rocks[0];var original=rock.objectMaterials[0];s[0].revision=2;r.TickTest(w,s);var job=LTPaintWeightWork.live.Single();
            switch(removal)
            {
                case "disabled":rock.isActiveAndEnabled=false;break;
                case "removed":w.rocks.Clear();break;
                case "overflow":for(int i=0;i<12;i++)s.Add(new LTPaintStamp());break;
                case "dispose":r.DisposeTest();break;
                case "no mesh":rock.filter.sharedMesh=null;break;
                case "opt out":rock.useTerrainMaterial=false;break;
            }
            if(removal!="dispose")r.TickTest(w,s);
            Check(r.Count==0&&job.retired&&job.reads==0&&rock.renderer.sharedMaterials[0]==original,"release and restore on "+removal);
        }
        foreach(string fallback in new[]{"manual","import","input budget","terrain budget"})
        {
            (w,r,s)=Setup();s[0].revision=2;r.TickTest(w,s);var job=LTPaintWeightWork.live.Single();
            if(fallback=="import")r.Invalidate();if(fallback=="input budget")r.fits=false;
            if(fallback=="terrain budget"){s[0].revision=3;LTPaintWeightWork.Finish();r.prepareFits=false;}
            r.TickTest(w,s,background:fallback!="manual");
            Check(job.retired&&r.bakes==2&&r.lastPixel==-1&&!r.PendingRoadSampling&&w.paintCpu.LastTick.rockWeightCpuBakes==1,"safe reference fallback on "+fallback);r.DisposeTest();
        }
        (w,r,s)=Setup();s[0].revision=2;r.TickTest(w,s);var broken=LTPaintWeightWork.live.Single();LTPaintWeightWork.Finish();LTPaintWeightWork.FailResult=true;
        int oldHash=r.Hash(w.rocks[0]);bool threw=false;try{r.TickTest(w,s);}catch(InvalidOperationException){threw=true;}
        Check(threw&&broken.released&&r.bakes==1&&r.Hash(w.rocks[0])==oldHash,"result failure releases completed job and does not commit");r.DisposeTest();LTPaintWeightWork.Drain();
        (w,r,s)=Setup();s[0].revision=2;r.TickTest(w,s);var uploaded=LTPaintWeightWork.live.Single();LTPaintWeightWork.Finish();r.failBake=true;
        oldHash=r.Hash(w.rocks[0]);threw=false;try{r.TickTest(w,s);}catch(InvalidOperationException){threw=true;}
        Check(threw&&uploaded.released&&r.Hash(w.rocks[0])==oldHash,"failed upload does not mark new dependencies ready");
        r.failBake=false;r.TickTest(w,s);Check(r.PendingRoadSampling&&r.bakes==1,"failed publication is retried, not cached as success");r.DisposeTest();LTPaintWeightWork.Drain();
        (w,r,s)=Setup();s[0].revision=2;returned=LTPaintWeightInput.returns;LTPaintWeightWork.FailConstructor=true;threw=false;
        try{r.TickTest(w,s);}catch(InvalidOperationException){threw=true;}
        Check(threw&&LTPaintWeightInput.returns==returned+1&&LTPaintWeightWork.live.Count==0&&r.bakes==1,"copy/schedule failure returns staging input without committing a map");
        LTPaintWeightWork.FailConstructor=false;r.TickTest(w,s);Check(r.PendingRoadSampling,"failed copy can be retried with fresh input");r.DisposeTest();LTPaintWeightWork.Drain();
        Console.WriteLine($"PASS rock paint lifecycle: {checks} checks, linked production coordinator/pending keys/counters, controlled worker and Unity doubles. Not native Burst or rendering.");
    }
}
