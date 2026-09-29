using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static List<Vector3Int>[] SolveJobInput(LTLODJobInput input)
    {
        // Executes the exact scalar arithmetic used by BuildJob. This is NOT a
        // native JobHandle/Burst test; those require the Unity editor menu check.
        var cells=input.Cells;var metrics=new LTLODJobMath.Metric[cells.Length];
        for(int i=0;i<cells.Length;i++)
        {
            var p=cells[i];
            Require(p.parent<0||p.parent>i,"parent follows its children in snapshot");
            if(p.merge!=0)Require(p.c0<i&&p.c1<i&&p.c2<i&&p.c3<i,"all child metrics ready before parent");
            metrics[i]=p.leaf!=0?LTLODJobMath.Leaf(p):p.merge==0?default:
                LTLODJobMath.Parent(p,cells[p.c0],metrics[p.c0],cells[p.c1],metrics[p.c1],cells[p.c2],metrics[p.c2],cells[p.c3],metrics[p.c3]);
        }
        var result=new List<Vector3Int>[input.Levels.Length];
        for(int level=0;level<result.Length;level++)
        {
            if(input.Levels[level].steps==0){result[level]=input.Fine.ToList();continue;}
            result[level]=new List<Vector3Int>();
            for(int i=0;i<cells.Length;i++)
            {
                var p=cells[i];var setting=input.Levels[level];
                if(LTLODJobMath.Eligible(p,metrics[i],setting)&&(p.parent<0||!LTLODJobMath.Eligible(cells[p.parent],metrics[p.parent],setting)))
                    result[level].Add(new Vector3Int(p.x,p.z,p.size));
            }
            LTLODJobInput.SortPlan(result[level]);
        }
        return result;
    }
    static void LODJobChecks()
    {
        int comparisons=0,slices=0;var random=new System.Random(932532);
        var levels=Enumerable.Range(0,7).Select(n=>new LTLODJobMath.Level{steps=n,tolerance=n*.07f}).ToArray();
        // Include asymmetric, rectangular, protected, low-error, empty and mixed
        // quadtree inputs, arbitrary fine order and non-monotonic level settings.
        for(int fixture=0;fixture<60;fixture++)
        {
            int side=1<<random.Next(2,6);var fine=LODFixture(side,fixture%2==0);
            if(fixture%5==0)
            {
                var mixed=new List<Vector3Int>();
                foreach(var cell in fine)
                {
                    if(random.Next(3)!=0){mixed.Add(cell);continue;}
                    int h=cell.z/2;
                    for(int z=0;z<2;z++)for(int x=0;x<2;x++)mixed.Add(new Vector3Int(cell.x+x*h,cell.y+z*h,h));
                }
                fine=mixed;
            }
            fine=fine.OrderBy(_=>random.Next()).ToList();
            var rect=new Rect(-53.25f+fixture,17.4f,32+fixture*2,40+fixture);
            bool boundary=fixture%2==0;int divisions=new[]{1,2,4,8}[fixture%4];
            var protect=fixture%3==0?null:new List<Rect>{new Rect(rect.xMin+5,rect.yMin+4,2,6)};
            Func<Rect,bool> protectedArea=fixture%7==0?area=>area.xMin<rect.xMin+12&&area.xMax>rect.xMin+11:null;
            float H(float x,float z)=>fixture%4==0?0:fixture%4==1?.025f*x+.031f*z:
                .0007f*x*x+.04f*(float)Math.Sin(z*.3)+.2f*(float)Math.Exp(-Math.Pow(x+z*.2,2));
            if(fixture%6==0)levels=levels.Reverse().ToArray();
            var input=new LTLODJobInput();
            foreach(var step in input.Prepare(fine,rect,levels,H,protect,protectedArea,divisions,boundary))slices++;
            var actual=SolveJobInput(input);
            for(int level=0;level<levels.Length;level++)
            {
                var expected=LODPreparationReference.Coarsen(fine,rect,levels[level].steps,levels[level].tolerance,H,protect,protectedArea,divisions,boundary);
                Require(actual[level].SequenceEqual(expected),$"job scalar parity fixture {fixture}, steps {levels[level].steps}: {actual[level].Count} != {expected.Count}");
                if(boundary)LTLODMesh.ValidateBoundary(fine,actual[level]);comparisons++;
            }
            Require(input.Cells.Length<=fine.Count*2+32,"compact tree snapshot, not a dense world grid");
            var old=input.Fine.ToArray();fine.Clear();
            Require(input.Fine.SequenceEqual(old),"snapshot owns fine cells");
        }
        foreach(var error in new[]{-1f,float.NaN,float.PositiveInfinity})
        {
            int calls=0;bool failed=false;
            try{foreach(var _ in new LTLODJobInput().Prepare(LODFixture(4),new Rect(0,0,1,1),new[]{new LTLODJobMath.Level{steps=1,tolerance=error}},(x,z)=>{calls++;return 0;})){};}
            catch(InvalidOperationException){failed=true;}
            Require(failed&&calls==0,"invalid error fails before native allocation or sampling");
        }
        foreach(int steps in new[]{-1,13})
        {
            bool failed=false;try{LTLODJobMath.Validate(new LTLODJobMath.Level{steps=steps,tolerance=1});}catch(InvalidOperationException){failed=true;}
            Require(failed,"invalid steps fail before jobs");
        }
        var empty=new LTLODJobInput();foreach(var _ in empty.Prepare(new List<Vector3Int>(),new Rect(0,0,1,1),levels,(x,z)=>0)){}
        Require(SolveJobInput(empty).All(p=>p.Count==0),"empty chunks supported");
        var noSimplification=new LTLODJobInput();int zeroCalls=0;
        foreach(var _ in noSimplification.Prepare(LODFixture(4),new Rect(0,0,1,1),new[]{new LTLODJobMath.Level()},(x,z)=>{zeroCalls++;return 0;})){}
        Require(zeroCalls==0,"all steps zero does not sample heights");
        bool nonFinite=false;
        try{foreach(var _ in new LTLODJobInput().Prepare(LODFixture(4),new Rect(0,0,1,1),levels,(x,z)=>float.NaN)){};}
        catch(InvalidOperationException){nonFinite=true;}
        Require(nonFinite,"NaN samples cannot enter native work");
        var partial=new LTLODJobInput();int partialCalls=0;
        using(var iterator=partial.Prepare(LODFixture(128),new Rect(0,0,128,128),levels,(x,z)=>{partialCalls++;return 0;}).GetEnumerator())
        {iterator.MoveNext();}
        Require(partial.Cells==null&&partialCalls==0,"cancel during snapshot collection does not evaluate or publish a partial input");
        var queue=new LTChunkLODQueue();var dirty=new HashSet<int>();bool ready=false,published=false;int retired=0;
        IEnumerable<LTBuildStep> WaitJob()
        {try{while(!ready)yield return LTBuildStep.Waiting;published=true;}finally{retired++;}}
        queue.Enqueue(3,queue.Version(3),WaitJob().GetEnumerator(),()=>true);queue.Advance(dirty);
        Require(queue.Waiting&&!published,"scheduler exposes waiting instead of spinning its whole time budget");
        queue.Touch(3);ready=true;
        Require(retired==1&&!published&&queue.Count==0,"cancel waiting continuation retires without accepting later result");
        queue.Enqueue(3,queue.Version(3),WaitJob().GetEnumerator(),()=>true);queue.Advance(dirty);
        Require(published&&!queue.Waiting&&queue.Completed==1,"new version resumes normally");

        var source=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTLODJobWork.cs");
        Require(source.Contains("[BurstCompile(FloatMode=FloatMode.Strict")&&source.Contains("struct BuildJob : IJob")&&source.Contains("}.Schedule()"),"real scheduled Burst job, not a Task wrapper");
        Require(!source.Contains("Allocator.TempJob")&&source.Contains("internal const int Capacity=2"),"persistent bounded allocations across editor frames");
        Require(source.Contains("execution[1]=1")&&source.Contains("if(execution[1]!=1)throw")&&source.Contains("[BurstDiscard] static void MarkManaged"),"result completion marker and actual backend are checked, not assumed from IsCompleted");
        var read=source.Substring(source.IndexOf("public List<Vector3Int> ReadLevel"));read=read.Substring(0,read.IndexOf("void Complete()"));
        Require(!read.Contains(".Dispose()")&&read.Contains("RequireResult()"),"reading a level keeps shared result/backend buffers alive until owner disposal");
        var dispose=source.Substring(source.IndexOf("public void Dispose()"));dispose=dispose.Substring(0,dispose.IndexOf("public static void CollectRetired"));
        Require(dispose.Contains("if(IsCompleted)Release()")&&!dispose.Contains("handle.Complete()"),"cancel never blocks on unfinished job");
        var editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        Require(editor.Contains("while(!job.IsCompleted)yield return LTBuildStep.Waiting")&&editor.Contains("using(var job=new LTLODJobWork(input))"),"poll then read, including iterator-dispose lifetime");
        Require(editor.Contains("beforeAssemblyReload += StopLODJobs")&&editor.Contains("quitting += StopLODJobs")&&editor.Contains("static void StopLODJobs(){CancelAllBuilds();LTLODJobWork.Drain();LTHeightJobWork.Drain();}"),"domain shutdown joins owned workers");
        var tick=editor.Substring(editor.IndexOf("static void Tick()"));
        Require(tick.IndexOf("LTLODJobWork.CollectRetired()")<tick.IndexOf("EditorApplication.isPlayingOrWillChangePlaymode"),"retired jobs are collected even in play mode or during compilation");
        Require(slices>300,"preparation has frequent cooperative slices");
        Console.WriteLine($"PASS LOD job scalar/input: {comparisons} exact reference levels, {slices} slices, protection/borders, snapshot isolation, cancellation and source lifetime guards. NOT a native Burst/Jobs execution test.");
        LODJobBenchmark();
    }
    static void LODJobBenchmark()
    {
        var fine=LODFixture(128);var rect=new Rect(0,0,256,256);
        float H(float x,float z)=>.0002f*x*x+.01f*(float)Math.Sin(z*.1f);
        var levels=Enumerable.Range(1,4).Select(n=>new LTLODJobMath.Level{steps=n,tolerance=n*.5f}).ToArray();
        var timer=Stopwatch.StartNew();var input=new LTLODJobInput();
        foreach(var _ in input.Prepare(fine,rect,levels,H,null,null,4,false)){}
        double prepare=timer.Elapsed.TotalMilliseconds;timer.Restart();var output=SolveJobInput(input);double solve=timer.Elapsed.TotalMilliseconds;
        var reference=new LTLODMesh.Preparation(fine,rect,H,new LTLODMesh.BuildProgress(false));timer.Restart();
        for(int i=0;i<levels.Length;i++)Require(output[i].SequenceEqual(reference.Coarsen(levels[i].steps,levels[i].tolerance,null,null,4,false)),"large fixture exact parity");
        Console.WriteLine($"LOD scalar synthetic 16384 cells / 4 levels: snapshot {prepare:F2} ms, hierarchy+selection {solve:F2} ms, previous shared coarsener {timer.Elapsed.TotalMilliseconds:F2} ms; {input.Cells.Length} nodes / {input.HeightSampleCount} heights. Single managed sample, NOT native worker/live scene timing.");
    }
}
