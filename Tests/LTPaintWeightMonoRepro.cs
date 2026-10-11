using System;
using LocalTerrainPrototype;
static class LTPaintWeightMonoRepro
{
    static int Main(string[] args)
    {
        bool rocks=Array.IndexOf(args,"--rocks")>=0;
        bool preparation=Array.IndexOf(args,"--preparation")>=0;
        int pixels=0,guards=0;
        var pool=new LTPaintWeightInputPool();
        for(int i=0;i<LTPaintWeightFixture.Count;i++)
        {
            var fixture=rocks?LTPaintWeightFixture.BuildRock(i):LTPaintWeightFixture.Build(i);
            var actualFixture=preparation?new LTPaintWeightFixture{input=fixture.PreparedInput(pool),roads=fixture.roads}:fixture;
            for(int p=0;p<fixture.input.size*fixture.input.size;p++)
            {
                var expected=fixture.Reference(p);var actual=actualFixture.Scalar(p);
                if(actual.guard!=0){guards++;actual=expected;}
                if(!LTPaintWeightFixture.Same(actual,expected))
                {Console.WriteLine("FAIL fixture="+i+" pixel="+p+" actual="+actual.first+"/"+actual.second+"/"+actual.third+" expected="+expected.first+"/"+expected.second+"/"+expected.third);return 1;}
                pixels++;
            }
            if(preparation){actualFixture.input.Dispose();fixture.OverwriteReturnedBuffers(pool);}
        }
        Console.WriteLine("PASS Mono "+(preparation?"prepared ":"")+(rocks?"rock":"terrain")+" paint weights: "+pixels+" exact 12-layer pixels; "+guards+" guarded. Numeric code only, not native Jobs or scene performance.");return 0;
    }
}
