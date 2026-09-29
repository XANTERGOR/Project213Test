using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void LocalLODQueueChecks()
    {
        var queue=new LTChunkLODQueue();var dirty=new HashSet<int>();
        var built=new List<string>();var disposed=new List<string>();int advancesB=0;
        IEnumerable<LTBuildStep> Job(string name,int count=3)
        {
            try
            {
                for(int i=0;i<count;i++){if(name=="B")advancesB++;yield return LTBuildStep.Working;}
                built.Add(name);
            }
            finally{disposed.Add(name);}
        }
        queue.Enqueue(9,queue.Version(9),Job("B").GetEnumerator(),()=>true);
        queue.Advance(dirty);
        queue.Enqueue(1,queue.Version(1),Job("old A").GetEnumerator(),()=>true);
        queue.Touch(1);dirty.Add(1);
        Require(queue.Count==1&&disposed.Count==0&&advancesB==1,"A cancels only A; B retains work; queued A never allocates its working state");
        queue.Enqueue(1,queue.Version(1),Job("new A").GetEnumerator(),()=>true);dirty.Clear();
        while(queue.Count>0)queue.Advance(dirty);
        Require(built.SequenceEqual(new[]{"B","new A"})&&advancesB==3&&queue.Completed==2&&queue.Discarded==1,"independent jobs finish once; old result never applied");
        for(int edit=0;edit<100;edit++)
        {
            queue.Enqueue(1,queue.Version(1),Job("obsolete "+edit).GetEnumerator(),()=>true);
            queue.Advance(dirty);queue.Touch(1);
        }
        Require(queue.Count==0&&built.Count==2&&disposed.Count==102,"100 superseding edits free continuations without publishing");
        bool valid=true;
        queue.Enqueue(4,queue.Version(4),Job("invalid object").GetEnumerator(),()=>valid);
        queue.Advance(dirty);valid=false;queue.Advance(dirty);
        Require(dirty.SetEquals(new[]{4})&&!built.Contains("invalid object"),"native object replacement rejects only its chunk and requeues it");
        dirty.Clear();queue.Enqueue(5,queue.Version(5),Job("save A").GetEnumerator(),()=>true);
        queue.Enqueue(6,queue.Version(6),Job("save B").GetEnumerator(),()=>true);
        queue.Advance(dirty);queue.CancelAll(dirty);
        Require(queue.Count==0&&dirty.SetEquals(new[]{5,6}),"save/disable/reload barrier preserves all unfinished IDs");
        dirty.Clear();int failedDisposal=0;
        IEnumerable<LTBuildStep> Failure()
        {try{yield return LTBuildStep.Working;throw new InvalidOperationException("fixture failure");}finally{failedDisposal++;}}
        queue.Enqueue(7,queue.Version(7),Failure().GetEnumerator(),()=>true);queue.Advance(dirty);
        bool failed=false;try{queue.Advance(dirty);}catch(InvalidOperationException){failed=true;}
        Require(failed&&failedDisposal==1&&queue.Count==0&&dirty.SetEquals(new[]{7}),"failed work is disposed and locally requeued");
        dirty.Clear();int liveCaches=0,peakCaches=0;
        IEnumerable<LTBuildStep> MemoryJob()
        {
            liveCaches++;peakCaches=Math.Max(peakCaches,liveCaches);
            try{for(int i=0;i<3;i++)yield return LTBuildStep.Working;}finally{liveCaches--;}
        }
        for(int id=0;id<256;id++)queue.Enqueue(id,queue.Version(id),MemoryJob().GetEnumerator(),()=>true);
        while(queue.Count>0)queue.Advance(dirty);
        Require(peakCaches==1&&liveCaches==0,"256 queued chunks retain at most one active height cache/coarse set");

        // Base transactions read seam dependencies without turning read-only
        // neighbours into rebuild targets when the transaction is superseded.
        var baseWork=new LTStagedBuild();dirty=new HashSet<int>{1};int basePublished=0;
        IEnumerable<LTBuildStep> Base(HashSet<int> targets)
        {
            baseWork.Dependencies.Add(2);
            yield return LTBuildStep.Working;
            basePublished++;dirty.Remove(1);targets.Clear();baseWork.Dependencies.Clear();
            yield return LTBuildStep.LOD0Ready;
        }
        baseWork.Begin(10,dirty,a=>Base(a).GetEnumerator(),queue.CaptureVersions(),queue.Version);
        baseWork.Advance(10,dirty);queue.Touch(9);dirty.Add(9);baseWork.Invalidate(10,dirty);
        Require(baseWork.Active&&baseWork.Advance(10,dirty)&&basePublished==1&&dirty.SetEquals(new[]{9}),"unrelated edit survives base snapshot without entering its targets");
        baseWork.Advance(10,dirty);dirty.Clear();dirty.Add(1);
        baseWork.Begin(10,dirty,a=>Base(a).GetEnumerator(),queue.CaptureVersions(),queue.Version);
        baseWork.Advance(10,dirty);queue.Touch(2);baseWork.Invalidate(10,dirty);
        Require(!baseWork.Active&&basePublished==1&&dirty.SetEquals(new[]{1}),"changed seam reader rejects stale base, without rebuilding every dependency");

        LocalLODGeometryChecks();LocalRoadInputChecks();
        var source=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        var deferred=source.Substring(source.IndexOf("static IEnumerable<LTBuildStep> DeferredChunkLODs"));
        deferred=deferred.Substring(0,deferred.IndexOf("[MenuItem("));
        Require(deferred.Contains("new LTBalancedForest(1,1,")&&deferred.Contains("new HashSet<int>{id},true)"),"local legacy work never builds a world coarse forest");
        Require(!deferred.Contains("updatedAt=")&&!deferred.Contains("state.dirty.Remove")&&!deferred.Contains("chunk.mesh.Clear"),"coarse completion cannot publish LOD0 or acknowledge another edit");
        Require(source.Contains("var requested=new HashSet<int>(affected??state.dirty)")&&source.Contains("bool ownedCoarse=staged&&c.lodsPending&&state.chunkLODs.Contains(id)"),"base owns its request and leaves unrelated pending LODs intact");
        Require(source.Contains("ReadDependencies(new HashSet<int>{id})")&&source.Contains("state.chunkLODs.CaptureVersions(),state.chunkLODs.Version"),"base version guard includes newly discovered seam readers");
        var project=source.Substring(source.IndexOf("EditorApplication.projectChanged +="));project=project.Substring(0,project.IndexOf("EditorApplication.playModeStateChanged"));
        Require(project.Contains("assetInputHashes.Clear()")&&!project.Contains("CancelBuilds"),"generated LOD asset events do not globally cancel independent work");
        Require(source.Contains("AssetInputHash(s.mask)")&&source.Contains("AssetInputHash(sourceMesh)")&&source.Contains("\":source-asset:\"+AssetInputHash(w.source)"),"real source reimports enter input signatures even if dirty counters reset");
        Console.WriteLine("PASS local LOD queue: independent versions/progress, 100 superseding edits, local invalidation, current-seam publication guards, scoped base dependencies and lifecycle barriers. Managed only.");
    }

    static void LocalLODGeometryChecks()
    {
        const int budget=100000;var progress=new LTLODMesh.BuildProgress(false);int comparisons=0;
        var random=new System.Random(420731);
        for(int fixture=0;fixture<6;fixture++)
        {
            var raw=Enumerable.Range(0,4).ToDictionary(id=>id,id=>LODFixture(1<<random.Next(2,5),true));
            var fineForest=new LTBalancedForest(2,2,budget,raw);fineForest.Balance(progress);
            var fine=raw.Keys.ToDictionary(id=>id,id=>fineForest.Plan(id));
            Rect Area(int id)=>new Rect(id%2*64,id/2*64,64,64);
            float H(float x,float z)=>.0003f*x*x+.02f*(float)Math.Sin(z*.1f);
            for(int level=1;level<=3;level++)
            {
                var coarse=fine.ToDictionary(pair=>pair.Key,pair=>new LTLODMesh.Preparation(pair.Value,Area(pair.Key),H,progress).Coarsen(level,1));
                var reference=new LTBalancedForest(2,2,budget,coarse);reference.Balance(progress);
                for(int id=0;id<4;id++)
                {
                    var masks=LTLODMesh.BoundaryMasks(fine[id],fineForest.BoundaryStitches(id,fine[id]));
                    var baseLocal=new LTBalancedForest(1,1,budget,new Dictionary<int,List<Vector3Int>>{{0,fine[id]}},new HashSet<int>{0});
                    foreach(var cell in fine[id])for(int side=0;side<4;side++)
                        Require(LTLODMesh.LocalMidpoint(baseLocal,masks,cell,side)==fineForest.Midpoint(id,cell,side),"local spatial LOD0 masks match world snapshot");
                    var local=new LTBalancedForest(1,1,budget,new Dictionary<int,List<Vector3Int>>{{0,coarse[id]}});local.Balance(progress);
                    var plan=local.Plan(0);LTLODMesh.ValidateBoundary(fine[id],plan);
                    Require(plan.SequenceEqual(reference.Plan(id)),"per-chunk balancing equals former global LOD planning");
                    foreach(var cell in plan)for(int side=0;side<4;side++)
                        Require(LTLODMesh.LocalMidpoint(local,masks,cell,side)==reference.Midpoint(id,cell,side),"pinned border masks and internal coarse transitions match world forest");
                    bool Cut(float x,float z)=>x>61.25f&&x<66.75f&&z>27.25f&&z<34.75f;
                    foreach(bool cut in new[]{false,true})
                    {
                        Func<float,float,bool> clip=cut?Cut:null;
                        LTStampMesh.Emit(Area(id),new Vector2(128,128),budget,reference,id,reference.Plan(id),H,clip,out var v0,out var n0,out var u0,out var t0,null,progress);
                        LTStampMesh.Emit(Area(id),new Vector2(128,128),budget,local,0,plan,H,clip,out var v1,out var n1,out var u1,out var t1,null,progress,
                            (cell,side)=>LTLODMesh.LocalMidpoint(local,masks,cell,side));
                        Require(v0.SequenceEqual(v1)&&n0.SequenceEqual(n1)&&u0.SequenceEqual(u1)&&t0.SequenceEqual(t1),"local LOD geometry/attributes/cuts exactly match full-world reference");comparisons++;
                    }
                }
            }
        }
        Console.WriteLine($"PASS local legacy LOD geometry: {comparisons} exact mesh/normal/UV/index comparisons, irregular neighbours, 3 levels, cut contours and pinned seams.");
    }

    static void LocalRoadInputChecks()
    {
        var settings=LTRoadMath.Settings.Default;settings.variation.enabled=false;settings.width=4;settings.shoulderWidth=1;settings.blendWidth=2;
        var points=Enumerable.Range(0,17).Select(i=>new LTRoadPoint(new Vector3(18+(float)Math.Sin(i*.4)*4,2,i*32))).ToArray();
        var before=LTRoadMath.Build(points,Matrix4x4.identity,settings);int reused=0,changed=0,checks=0;
        for(int fixture=0;fixture<8;fixture++)
        {
            var edited=(LTRoadPoint[])points.Clone();var options=settings;
            if(fixture==0)edited[7].position.y+=.4f;
            if(fixture==1)edited[7].position.x+=3;
            if(fixture==2)edited[7].bank+=3;
            if(fixture==3)options.width+=1;
            if(fixture==4){edited[7].overrideVariation=true;edited[7].variationStrength=.2f;}
            if(fixture==5)edited=edited.Where((p,i)=>i!=7).ToArray();
            if(fixture==6)edited[0].position.x-=6;
            if(fixture==7){options.straightEnd=true;options.junctionEndLength=4;}
            var after=LTRoadMath.Build(edited,Matrix4x4.identity,options);
            for(int id=0;id<16;id++)
            {
                var area=new Rect(0,id*32,32,32);
                Require(before.SameTerrainRegion(before,area),"identical road region always reuses");
                if(!before.SameTerrainRegion(after,area)){changed++;continue;}
                reused++;
                for(int z=0;z<=16;z++)for(int x=0;x<=16;x++)
                {
                    float px=area.xMin+x*2,pz=area.yMin+z*2;
                    Require(before.ApplyHeight(px,pz,0)==after.ApplyHeight(px,pz,0),"reused region preserves height exactly");
                    Require(before.Intersects(new Rect(px,pz,1,1))==after.Intersects(new Rect(px,pz,1,1)),"reused region preserves conservative density queries");checks++;
                }
            }
        }
        Require(reused>16&&changed>0,"local road changes do not invalidate its entire length");
        // Arc-length-driven variation is a real dependency, even far downstream.
        settings.variation.enabled=true;
        var varied=LTRoadMath.Build(points,Matrix4x4.identity,settings);points[3].position.y+=10;
        var longer=LTRoadMath.Build(points,Matrix4x4.identity,settings);
        Require(!varied.SameTerrainRegion(longer,new Rect(0,448,32,32)),"length-driven width/rut variation must invalidate dependent downstream chunks");
        Console.WriteLine($"PASS local road inputs: {reused} reused / {changed} changed regions; {checks} exact height/density comparisons; arc-length dependencies retained.");
    }
}
