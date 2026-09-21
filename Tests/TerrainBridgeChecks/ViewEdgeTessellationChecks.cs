using System;
using UnityEngine;
partial class Checks
{
    static void ViewEdgeTessellationChecks()
    {
        // CPU reference, not GPU execution. Half-space x <= boundary stands for
        // a side plane rotating across one edge of a still-visible triangle.
        float[] Evaluate(Vector3 a,Vector3 b,Vector3 c,float boundary,bool old)
        {
            if(a.x>boundary&&b.x>boundary&&c.x>boundary)return new float[4];
            float Factor(Vector3 p,Vector3 q)
            {
                float fade=Math.Clamp(1-(((p+q)*.5f).magnitude-5)/20,0,1);
                return Math.Max(1,16*fade*fade*(old&&p.x>boundary&&q.x>boundary?0:1));
            }
            var f=new[]{Factor(b,c),Factor(c,a),Factor(a,b),0f};
            f[3]=(f[0]+f[1]+f[2])/3;return f;
        }
        var a=new Vector3(-2,0,1);var b=new Vector3(1,0,1);var c=new Vector3(1,0,3);
        var before=Evaluate(a,b,c,1.1f,false);var after=Evaluate(a,b,c,.9f,false);
        for(int i=0;i<4;i++)Require(before[i]==after[i],"view plane crossing cannot collapse a visible patch edge/inside");
        Require(Evaluate(a,b,c,.9f,true)[0]==1&&after[0]>1,"reproduces old off-screen edge collapse");
        var neighbour=Evaluate(new Vector3(-3,0,2),c,b,.9f,false);
        Require(neighbour[0]==after[0],"shared-edge factors agree across opposite vertices and edge reversal");
        Require(Evaluate(a,b,c,-3,false)[3]==0,"fully outside patch remains culled");
        var far=Evaluate(a+Vector3.forward*40,b+Vector3.forward*40,c+Vector3.forward*40,.9f,false);
        for(int i=0;i<4;i++)Require(far[i]==1,"distance fade still reaches one");
        // Structural guard ties the reference regression to the production shader.
        var path="Assets/TerrainSystem/LocalTerrain/Shaders/LTAdaptiveHull.hlsl";
        if(System.IO.File.Exists(path))
        {
            var shader=System.IO.File.ReadAllText(path);
            Require(shader.Contains("float4 tf=LTViewSafeTessellationFactors("),"hull uses view-safe factors");
            Require(!shader.Contains("float4 tf=GetTessellationFactors("),"no legacy per-edge frustum suppression");
            Require(shader.Contains("_ShadowFrustumPlanes,4")&&shader.Contains("_FrustumPlanes,5"),"both render views retain whole-patch culling");
        }
        Console.WriteLine("PASS view-edge tessellation CPU reference + source contract: boundary rotation, shared edge, whole-patch culling, distance fade.");
    }
}
