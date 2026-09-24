using System;
using System.Linq;
using UnityEngine;
using LocalTerrainPrototype;
partial class Checks
{
    static void PaintChecks()
    {
        HullCoverageChecks();
        RegularMaskGridChecks();
        BoundaryCoverageChecks();
        GpuSamplingChecks();
        DisplacementNormalChecks();
        TerrainTangentChecks();
        NormalFilteringChecks();
        SurfaceProjectionChecks();
        DeformationChecks();
        DetailChecks();
        TwelveLayerChecks();
        var dw=new float[12];var dh=new float[12];
        dw[0]=.8f;dw[1]=.2f;dh[0]=.8f;dh[1]=.1f;
        Require(Math.Abs(LTPaintMath.DisplacementVisibility(dw,dh,0,2)-.2f)<1e-6,"visibility uses normalized layer weight");
        Require(LTPaintMath.DisplacementVisibility(dw,dh,1,2)==0,"height-hidden layer needs no tessellation");
        Require(dw[1]==.2f,"coverage sampling must not mutate paint weights");
        dw[0]=0;dw[1]=1;
        Require(LTPaintMath.DisplacementVisibility(dw,dh,1,2)==1,"visible layer stays refined regardless of raw height or zero offset");
        Require(LTPaintMath.DisplacementVisibility(dw,dh,1,0)==0,"disabled displacement never refines");
        dh[0]=dh[1]=.5f;
        foreach(float amount in new[]{0f,.1f,.49f,.5f,.51f,.65f,1f})
        {
            dw[0]=1-amount;dw[1]=amount;
            bool refine=LTPaintMath.DisplacementVisibility(dw,dh,1,2)>LTPaintMath.DisplacementVisibilityStart;
            Require(refine==(amount>.5f),"light-layer dominance boundary preserves dark-layer holes");
        }
        dw[0]=.2f;dw[1]=.4f;dw[2]=.4f;dh[2]=.5f;
        Require(LTPaintMath.DisplacementVisibility(dw,dh,0,6)>.5f,"multiple displacement layers combine visibility");
        var occupancyA=new Color32[25];var occupancyB=new Color32[25];
        occupancyB[12]=new Color32(0,0,0,1); // tiny island in slot 7, not on any corner
        var occupancy=LTPaintMath.DisplacementPyramid(occupancyA,occupancyB,5,128);
        Require(occupancy.Count==3&&occupancy[0].Count(p=>p.r>0)==4&&occupancy[2][0].r==255,"displacement max mips preserve interior island");
        Require(occupancy[0][0].r==0&&occupancy[0][15].r==0,"outside displacement coverage stays empty");
        Require(LTPaintMath.DisplacementPyramid(occupancyA,occupancyB,5,1)[2][0].r==0,"non-displacement layer does not refine");
        occupancyB[24]=new Color32(0,0,0,1);
        Require(LTPaintMath.DisplacementPyramid(occupancyA,occupancyB,5,128)[0][15].r==255,"world/chunk endpoint contributes coverage");
        Require(Math.Abs(LTPaintMath.DisplacementBound(.2f,.5f)-.1f)<1e-6,"centered displacement bound");
        Require(Math.Abs(LTPaintMath.DisplacementBound(.2f,0)-.2f)<1e-6,"positive displacement bound");
        Require(LTPaintMath.DisplacementBound(0,.5f)==0,"disabled displacement bounds");
        var fade=LTPaintMath.DisplacementDistances(15,50,true,30);
        Require(fade.x==15&&fade.y==30,"displacement ends before global map transition");
        fade=LTPaintMath.DisplacementDistances(50,10,false,0);
        Require(fade.x<fade.y&&fade.y==10,"invalid fade distances sanitized");
        var sparse=new Color32[16*16];sparse[8*16+8]=new Color32(255,0,0,255);
        var grid=new LTPaintMath.CoverageGrid(sparse,16);
        Require(grid.Any(0,0,1,1),"density finds island inside large triangle bounds");
        Require(!grid.Any(0,0,.1f,.1f),"density leaves distant empty region coarse");
        Require(grid.Any(.49f,.49f,.51f,.51f),"density retains island boundary halo");
        Require(!grid.Any(2,2,3,3),"density outside chunk stays empty");
        Require(object.ReferenceEquals(grid,grid.Include(new Color32[256])),"mesh feedback cannot shrink coverage");
        var extra=new Color32[256];extra[0]=new Color32(255,0,0,255);
        var merged=grid.Include(extra);
        Require(merged.Any(0,0,0,0)&&merged.Any(.5f,.5f,.5f,.5f),"filter feedback preserves both islands");
        Require(!grid.Any(0,0,0,0),"density snapshots immutable during mesh planning");
        Require(!new LTPaintMath.CoverageGrid(new Color32[256],16).Any(0,0,1,1),"fresh authoring revision removes old coverage");
        // Compare every rectangle of a small grid to a direct scan (including halo).
        for(int y0=0;y0<16;y0++)for(int x0=0;x0<16;x0++)
        for(int y1=y0;y1<16;y1++)for(int x1=x0;x1<16;x1++)
            Require(grid.Any(x0/16f,y0/16f,x1/16f,y1/16f)==
                (x0-1<=8&&x1+1>=8&&y0-1<=8&&y1+1>=8),"summed coverage agrees with direct scan");
        var noisePoint=new Vector2(-3.7f,8.2f);
        Require(LTPaintMath.NoiseCoverage(noisePoint,10,42,0,.5f,.2f)==1,"noise zero strength preserves layer");
        Require(LTPaintMath.NoiseCoverage(noisePoint,10,42,1,0,.2f)==1,"noise zero threshold full coverage");
        Require(LTPaintMath.NoiseCoverage(noisePoint,10,42,1,1,.2f)==0,"noise maximum threshold removes layer");
        Require(Math.Abs(LTPaintMath.NoiseCoverage(noisePoint,10,42,.3f,1,.2f)-.7f)<1e-6,"noise partial strength");
        Require(LTPaintMath.ValueNoise(noisePoint,42)==LTPaintMath.ValueNoise(noisePoint,42),"noise deterministic seed");
        Require(Math.Abs(LTPaintMath.ValueNoise(noisePoint,42)-LTPaintMath.ValueNoise(noisePoint,43))>.0001f,"noise different seeds");
        Require(Math.Abs(LTPaintMath.ValueNoise(new Vector2(-.00001f,2.3f),42)-LTPaintMath.ValueNoise(new Vector2(.00001f,2.3f),42))<.0001f,"noise continuous across negative cell boundary");
        float noiseMin=1,noiseMax=0;
        for(int i=0;i<1000;i++)
        {
            float value=LTPaintMath.NoiseCoverage(new Vector2(i*.37f,i*-.21f),10,42,1,.5f,.2f);
            Require(value>=0&&value<=1,"noise bounded coverage");noiseMin=Math.Min(noiseMin,value);noiseMax=Math.Max(noiseMax,value);
        }
        Require(noiseMin==0&&noiseMax==1,"noise creates both holes and solid patches");
        Require(LTPaintMath.RangeWeight(15,new Vector2(10,20),0)==1,"filter interior");
        Require(LTPaintMath.RangeWeight(9,new Vector2(10,20),0)==0,"filter hard cutoff");
        Require(LTPaintMath.RangeWeight(10,new Vector2(10,20),0)==1,"filter inclusive boundary");
        Require(Math.Abs(LTPaintMath.RangeWeight(9,new Vector2(10,20),2)-.5f)<1e-6,"filter lower feather");
        Require(Math.Abs(LTPaintMath.RangeWeight(21,new Vector2(10,20),2)-.5f)<1e-6,"filter upper feather");
        Require(LTPaintMath.RangeWeight(15,new Vector2(20,10),2)==1,"filter reversed range");
        Require(LTPaintMath.Curvature(5,3,7,4,6,2)==0,"sloping plane has zero curvature");
        Require(LTPaintMath.Curvature(2,1,1,1,1,5)>0,"ridge curvature positive");
        Require(LTPaintMath.Curvature(1,2,2,2,2,5)<0,"hollow curvature negative");
        var a=new Vector3(0,0,0);var b=new Vector3(10,10,0);var c=new Vector3(0,0,10);
        Require(LTPaintMath.TriangleWeights(new Vector2(2,3),a,b,c,out var bary),"terrain barycentric inside");
        Require(Math.Abs(bary.x-.5f)<1e-6&&Math.Abs(bary.y-.2f)<1e-6&&Math.Abs(bary.z-.3f)<1e-6,"terrain interpolated height");
        Require(LTPaintMath.TriangleWeights(new Vector2(5,5),a,b,c,out _),"terrain shared edge inclusive");
        Require(!LTPaintMath.TriangleWeights(new Vector2(8,8),a,b,c,out _),"terrain outside triangle");
        Require(!LTPaintMath.TriangleWeights(Vector2.zero,a,a,c,out _),"terrain degenerate projected triangle");
        foreach(int resolution in new[]{256,2048,4096})for(int count=1;count<=16;count++)
        {
            int end=0;
            for(int i=0;i<count;i++)
            {
                var range=LTPaintMath.AtlasRange(i,count,resolution);
                Require(range.x==end&&range.y>range.x,"global atlas tiles: no gaps/overlap");end=range.y;
            }
            Require(end==resolution,"global atlas covers full dimension");
        }
        Require(LTPaintMath.Coverage(Vector2.zero,false,.5f,1)==1,"paint center");
        Require(LTPaintMath.Coverage(new Vector2(1,0),false,.5f,1)==0,"paint boundary");
        Require(LTPaintMath.Coverage(new Vector2(2,0),true,.5f,1)==0,"paint outside");
        Require(Math.Abs(LTPaintMath.Coverage(new Vector2(.75f,0),false,.5f,1)-.5f)<1e-6,"paint smooth edge");
        var weights=new float[12];weights[0]=1;
        for(int i=1;i<12;i++)LTPaintMath.Composite(weights,i,1f/(i+1));
        Require(weights.All(w=>Math.Abs(w-1f/12)<1e-6),"twelve equal weights including base");
        var random=new System.Random(83);
        for(int i=0;i<10000;i++)
        {
            LTPaintMath.Composite(weights,random.Next(12),(float)random.NextDouble());
            Require(Math.Abs(weights.Sum()-1)<1e-5&&weights.All(w=>w>=0&&w<=1),"normalized twelve-layer compositing");
        }
        LTPaintMath.Composite(weights,11,1);Require(weights[11]==1&&weights.Take(11).All(w=>w==0),"last stamp overrides");
        LTPaintMath.Composite(weights,11,.5f);Require(weights[11]==1,"duplicate layer preserves full coverage");
        Console.WriteLine("PASS paint: 12 layers, smooth bounds, 10000 normalized blends, hierarchy order and duplicate layer.");
    }
}
