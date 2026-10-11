using System;
using System.IO;
using LocalTerrainPrototype;
partial class Checks
{
    static void PaintWeightChecks()
    {
        int pixels=0,guards=0;
        for(int fixture=0;fixture<LTPaintWeightFixture.Count;fixture++)
        {
            var f=LTPaintWeightFixture.Build(fixture);
            for(int p=0;p<f.input.size*f.input.size;p++)
            {
                var actual=f.Scalar(p);var expected=f.Reference(p);
                if(actual.guard!=0){guards++;actual=expected;}
                Require(LTPaintWeightFixture.Same(actual,expected),$"weight mask exact fixture={fixture} pixel={p}: {actual.first}/{actual.second}/{actual.third} vs {expected.first}/{expected.second}/{expected.third}");pixels++;
            }
        }
        Require(guards<pixels/20,"rounding guard must remain sparse, not silently replace the whole job");
        const string root="Assets/TerrainSystem/LocalTerrain/";
        var runtime=File.ReadAllText(root+"LTPaintRuntime.cs");var work=File.ReadAllText(root+"LTPaintWeightWork.cs");
        Require(runtime.Contains("!state.weightWork.Matches(rect,coverage,weightInput,weightRoads)"),"stale paint revisions retired before result access");
        Require(runtime.Contains("!state.weightWork.work.IsCompleted||roadResultsApplied>=1"),"ready-only result and shared application budget");
        Require(work.Contains("if(!IsCompleted)throw")&&work.Contains("retired||released")&&work.Contains("live.Count<Capacity"),"bounded persistent ownership and nonblocking retirement");
        Require(runtime.Contains("bakedPixels[y*Resolution+x].guard==0")&&runtime.Contains("WeightPixelFallback"),"guard pixels must use complete original ordered CPU path");
        var prepare=File.ReadAllText(root+"LTPaintWeightPreparation.cs");
        Require(File.ReadAllText(root+"LTPaintWeightPending.cs").Contains("!work.IsValid")&&work.Contains("public bool IsValid=>!retired&&!released"),"domain-reload-disabled play transitions cannot accept drained jobs");
        Require(prepare.Contains("GetPixelBilinear")&&!work.Contains("Texture2D")&&!work.Contains("LTWorld"),"Unity objects stay outside workers");
        Console.WriteLine($"PASS paint weight kernel: {pixels} exact 12-layer pixels, {guards} guarded pixels. Managed only; native Burst requires editor validation.");
        pixels=guards=0;
        for(int fixture=0;fixture<LTPaintWeightFixture.Count;fixture++)
        {
            var f=LTPaintWeightFixture.BuildRock(fixture);
            Require(Array.TrueForAll(f.input.stamps,s=>s.kind!=1)&&f.input.hits.Length==0,"rocks must not receive road stamps/hits");
            for(int p=0;p<f.input.size*f.input.size;p++)
            {
                var actual=f.Scalar(p);var expected=f.Reference(p);
                if(actual.guard!=0){guards++;actual=expected;}
                Require(LTPaintWeightFixture.Same(actual,expected),$"rock mask exact fixture={fixture} pixel={p}");pixels++;
            }
        }
        Require(guards<pixels/20,"rock mask rounding guards remain sparse");
        var rocks=File.ReadAllText(root+"LTPaintRocks.cs");
        Require(rocks.Contains("foreach(int value in coverageInputs[s])")&&rocks.Contains("foreach(var layer in layers)coverage=Mix(coverage,Id(layer))"),"rock revisions include current stamp/junction inputs and ordered palette");
        Require(runtime.IndexOf("if(PendingRoadSampling)world.paintStatus")>runtime.IndexOf("TickRockMaterials(world,shader"),"rock-only pending state reaches UI after rock tick");
        Console.WriteLine($"PASS rock weight kernel: {pixels} exact 12-layer pixels, {guards} guarded pixels. Managed numeric parity; native Burst requires editor validation.");
    }
}
