using System;
using UnityEngine;
using LocalTerrainPrototype;
partial class Checks
{
    static void BoundaryCoverageChecks()
    {
        const int n=32;
        var pixels=new Color32[n*n];
        for(int y=0;y<n;y++)for(int x=0;x<n;x++)
            if(x>=8&&x<24&&y>=8&&y<24)pixels[y*n+x]=new Color32(255,255,0,255);
        var grid=new LTPaintMath.CoverageGrid(pixels,n);
        Require(!grid.Boundary(.4f,.4f,.6f,.6f),"solid interior needs no boundary refinement");
        Require(!grid.Boundary(0,0,.1f,.1f),"empty interior stays coarse");
        Require(grid.Boundary(.2f,.4f,.3f,.6f),"mixed footprint boundary refines");
        Require(grid.Boundary(0,0,1,1),"enclosed island found without corner sampling");
        var random=new System.Random(473);
        for(int i=0;i<2000;i++)
        {
            float x=(float)random.NextDouble()*1.4f-.2f,y=(float)random.NextDouble()*1.4f-.2f;
            float xx=x+(float)random.NextDouble()*.3f,yy=y+(float)random.NextDouble()*.3f;
            bool white=false,black=false;
            for(int v=(int)Math.Floor(y*n)-1;v<(int)Math.Floor(yy*n)+2;v++)
                for(int u=(int)Math.Floor(x*n)-1;u<(int)Math.Floor(xx*n)+2;u++)
                {
                    bool covered=u>=0&&u<n&&v>=0&&v<n&&pixels[v*n+u].r>0;
                    white|=covered;black|=!covered;
                }
            bool intersects=xx>=0&&yy>=0&&x<=1&&y<=1;
            Require(grid.Boundary(x,y,xx,yy)==(intersects&&white&&black),"boundary integral matches guarded brute-force query");
        }
        for(int i=0;i<pixels.Length;i++)pixels[i]=new Color32(255,255,0,255);
        grid=new LTPaintMath.CoverageGrid(pixels,n);
        Require(grid.Boundary(-.1f,.4f,.1f,.6f),"crop exterior counts as empty");
        Require(!grid.Boundary(.3f,.3f,.6f,.6f),"full map does not refine its whole interior");
        Console.WriteLine("PASS boundary coverage: empty/solid/mixed, enclosed islands, crop edge, 2000 brute-force comparisons.");
    }
}
