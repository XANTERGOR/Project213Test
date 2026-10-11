using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;

partial class Checks
{
    static void RockTerrainLODChecks()
    {
        int levelsCompared=0;
        string report=LTRockTerrainLODCheck.Run((fine,rect,height,protect)=>
        {
            foreach(bool legacy in new[]{false,true})
            {
                var settings=Enumerable.Range(1,4).Select(i=>new LTLODJobMath.Level{steps=i,tolerance=10}).ToArray();
                var input=new LTLODJobInput();foreach(var _ in input.Prepare(fine,rect,settings,height,null,protect,2,legacy)){}
                var actual=SolveJobInput(input);var reference=new LTLODMesh.Preparation(fine,rect,height,new LTLODMesh.BuildProgress(false));
                for(int i=0;i<settings.Length;i++)
                {
                    var expected=reference.Coarsen(settings[i].steps,settings[i].tolerance,null,protect,2,legacy);
                    Require(actual[i].SequenceEqual(expected),"rock terrain LOD: deferred Jobs input differs from manual preparation");levelsCompared++;
                }
            }
        });
        string engine=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        string integration=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTHoleStampEditor.cs");
        Require(!engine.Contains("RockCutRegions(")&&!integration.Contains("RockCutRegions("),"whole rock footprint still freezes terrain LOD");
        Require(integration.Contains("rockEdges.Prepare(fine,rect,rockBounds,cut)")&&integration.Contains("rockEdges?.Protects(area)"),"narrow contact protection not wired into shared coarsener");
        Require(engine.Split("TransitionDiagonals(w,id),(x,z)=>CutTerrainAt(w,local,x,z)").Length==3&&
            engine.Contains("TransitionDiagonals(w,data.id),(x,z)=>CutTerrainAt(w,local,x,z)"),"all three LOD paths must use the exact terrain cutter");
        Require(engine.Contains("split-rock-v3-band-lod:"),"previously baked protected-band LODs must invalidate");
        Console.WriteLine("PASS rock terrain LOD: "+report+$"; {levelsCompared} scalar Jobs/manual levels identical. No native Burst or scene performance measured.");
    }
}
