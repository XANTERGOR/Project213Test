using System;
using System.IO;
using UnityEngine;
partial class Checks
{
    static void SurfaceProjectionChecks()
    {
        Vector3 Weights(Vector3 n)
        {
            var w=new Vector3(Mathf.Pow(Mathf.Abs(n.x),4),Mathf.Pow(Mathf.Abs(n.y),4),Mathf.Pow(Mathf.Abs(n.z),4));
            return w/(w.x+w.y+w.z);
        }
        Vector3 Resolve(Vector3 n,Vector3 x,Vector3 y,Vector3 z)
        {
            var w=Weights(n);
            var g=w.x*new Vector3(0,-x.y/Mathf.Max(x.z,.0001f),-x.x/Mathf.Max(x.z,.0001f))+
                w.y*new Vector3(-y.x/Mathf.Max(y.z,.0001f),0,-y.y/Mathf.Max(y.z,.0001f))+
                w.z*new Vector3(-z.x/Mathf.Max(z.z,.0001f),-z.y/Mathf.Max(z.z,.0001f),0);
            g-=n*Vector3.Dot(n,g);return (n-g).normalized;
        }
        var flat=Vector3.forward;
        foreach(var n in new[]{Vector3.up,Vector3.down,Vector3.left,Vector3.right,Vector3.forward,Vector3.back,new Vector3(1,1,1).normalized})
        {
            var w=Weights(n);
            Require(Math.Abs(w.x+w.y+w.z-1)<1e-6,"triplanar weights must sum to one");
            Require((Resolve(n,flat,flat,flat)-n).magnitude<1e-5,"flat normal map must preserve all six source-normal directions");
        }
        var detail=new Vector3(.3f,.2f,.9327379f).normalized;
        Require((Resolve(Vector3.up,flat,detail,flat)-new Vector3(detail.x,detail.z,detail.y)).magnitude<1e-5,"top triplanar normal differs from corrected XZ handedness");
        var random=new System.Random(918);
        for(int i=0;i<1000;i++)
        {
            var n=new Vector3((float)random.NextDouble()*2-1,(float)random.NextDouble()*2-1,(float)random.NextDouble()*2-1).normalized;
            var result=Resolve(n,detail,detail,detail);
            Require(Math.Abs(result.magnitude-1)<1e-5&&Vector3.Dot(n,result)>0,"projected normals must remain finite and outward-facing");
        }
        const string root="Assets/TerrainSystem/LocalTerrain/";
        var rocks=File.ReadAllText(root+"LTPaintRocks.cs");
        Require(rocks.Contains("!stamp.useTerrainMaterial")&&rocks.Contains("stamp.GetComponentInParent<LTWorld>()!=world"),"rock material must be opt-in and scoped to its world");
        Require(rocks.Contains("layers.Count>8")&&rocks.Contains("continue;"),"layer overflow must not silently drop layers");
        Require(rocks.Contains("RestoreRockMaterialsForSave")&&rocks.Contains("ReleaseRockMaterials")&&rocks.Contains("HasRockMaterial(state)"),"owned rock material lifecycle restoration missing");
        Require(!rocks.Contains("filter.sharedMesh=")&&!rocks.Contains(".vertices="),"material projection must not edit rock geometry");
        Require(rocks.Contains("_LTRockProjection\",1")&&rocks.Contains("_LTGlobalParams\",Vector4.zero"),"rock must use world coordinates and no planar far atlas");
        var runtime=File.ReadAllText(root+"LTPaintRuntime.cs");
        Require(runtime.Contains("RestoreRockMaterialsForSave();")&&runtime.Contains("ReleaseRockMaterials();")&&runtime.Contains("TickRockMaterials(world,shader,active,bounds);"),"rock lifecycle not connected");
        var sampling=File.ReadAllText(root+"Shaders/LTEightLayerSampling.hlsl");
        var projection=File.ReadAllText(root+"Shaders/LTProjectedLayers.hlsl");
        Require(projection.Contains("LTSampleLayersMapped(p.xz,uv,ux,uy")&&sampling.Contains("GetAbsolutePositionWS(sourcePositionRWS)"),"paint coverage and world texture coordinates must stay separate");
        Require(sampling.Contains("[branch] if(projected && fade < 1)")&&sampling.Contains("else if(fade < 1)"),"full far must skip projected sampling; ordinary XZ path must remain available");
        var globals=File.ReadAllText(root+"LTPaintGlobals.cs");
        Require(globals.Contains("globalsReady=projectionMatches")&&globals.Contains("saved.triplanar==world.triplanarTexturing"),"atlas projection must match live and saved material mode");
        Require(globals.Contains("commands.DrawMesh(chunk.mesh")&&!globals.Contains("commands.DrawMesh(chunk.lodMeshes"),"triplanar bake must use original full-resolution geometry");
        var baker=File.ReadAllText(root+"Resources/LTGlobalLayerBake.shader");
        Require(baker.Contains("LTProjectedLayers.hlsl")&&sampling.Contains("LTProjectedLayers.hlsl"),"near and bake projections must share the same implementation");
        Require(baker.Contains("o.terrainPosition.xz/_LTWorldSize.xy")&&baker.Contains("LTSampleProjectedLayers(input.terrainPosition"),"bake needs actual source height and global atlas coordinates");
        Require(sampling.Contains("projectedGradient=lerp(projectedGradient,farGradient,fade)"),"near/far projected normals must blend in gradient space");
        for(int i=0;i<1000;i++)
        {
            var n=new Vector3((float)random.NextDouble()*2-1,.05f+(float)random.NextDouble(),(float)random.NextDouble()*2-1).normalized;
            var t=new Vector3(n.y,-n.x,0).normalized;
            var b=-Vector3.Cross(n,t);
            var ws=Resolve(n,detail,detail,detail);
            var g=n-ws/Vector3.Dot(n,ws);
            var encoded=new Vector3(-Vector3.Dot(g,t),-Vector3.Dot(g,b),1).normalized;
            var restored=(-encoded.x*t-encoded.y*b)/encoded.z;
            Require((g-restored).magnitude<1e-4,"triplanar atlas tangent encoding must roundtrip its surface gradient");
            foreach(float fade in new[]{0f,.25f,.5f,.75f,1f})
                Require(((n-Vector3.Lerp(g,restored,fade)).normalized-ws).magnitude<1e-4,"identical baked detail must not flip normals during fade");
        }
        Console.WriteLine("PASS surface projection: six axes, 1000 slopes, 1000 atlas gradient roundtrips and fades; geometry bake, shared projection, mode compatibility and rock lifecycle source contracts. Unity rendering still requires validation.");
    }
}
