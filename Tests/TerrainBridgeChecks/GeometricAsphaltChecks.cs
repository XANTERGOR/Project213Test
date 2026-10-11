using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void GeometricAsphaltChecks(bool beveled=false,bool connected=false)
    {
        Require((int)LTRoadMode.GeometricAsphalt==3,"new mode appended, serialized legacy modes preserved");
        var s=LTRoadMath.Settings.Default;s.mode=LTRoadMode.GeometricAsphalt;s.width=10;
        s.projection=LTRoadProjection.Spline;s.pattern=LTRoadPattern.Solid;s.asphaltDamage.potholes=1;
        s.asphaltDamage.brokenEdges=1;s.asphaltDamage.depth=.4f;s.asphaltDamage.size=3;
        if(beveled){s.pitBevelWidth=.12f;s.pitBevelDepth=.05f;s.pitShapeVariation=.7f;}
        s.connectedDamage=connected;
        var path=new[]{new LTRoadPoint(new Vector3(8,2,0)),new LTRoadPoint(new Vector3(8,2,32))};
        LTRoadMath.Snapshot Road(LTRoadMath.Settings settings)=>LTRoadMath.Build(path,Matrix4x4.identity,settings);
        var road=Road(s);Require(road.geometricPits.Count>=8,"deterministic internal potholes and edge chips generated");
        var again=Road(s);
        for(int i=0;i<road.geometricPits.Count;i++)
        {
            var pit=road.geometricPits[i];Require(pit.depth>0&&pit.depth<=s.asphaltDamage.depth,"bounded geometric depth");
            for(int j=0;j<pit.Count;j++)Require(pit[j]==again.geometricPits[i][j],"deterministic contour");
            for(int j=i+1;j<road.geometricPits.Count;j++)Require(!LTRoadGeometryMath.Intersects(pit,road.geometricPits[j]),"no overlapping pits / double floors");
            var p=pit.bounds.center;Require(road.PaintWeight(p.x,p.y)==0,"same contour exposes underlying paint");
            Require(Math.Abs(road.ApplyHeight(p.x,p.y,2)-2)<1e-6,"top height field is not pre-depressed / no double deformation");
        }
        var clean=s;clean.asphaltDamage.strength=0;Require(Road(clean).geometricPits.Count==0,"zero strength removes geometric damage");
        clean=s;clean.asphaltDamage.depth=0;Require(Road(clean).geometricPits.Count==0,"zero depth restores original surface");
        clean=s;clean.textureOffset=new Vector2(.71f,.3f);Require(Road(clean).geometryHash==road.geometryHash,"UV phase does not invalidate/move geometry");
        Require(!LTLODMesh.RequiresFixedRoadSurface(s.mode),"geometric mode never pins LOD0");
        // Same one-layer contour mask in the worker, composed between biome
        // underpaint and a later overlay. No additional substrate slot/texture.
        int paintPixels=0;
        foreach(int slot in new[]{1,8,11})
        {
            var fixture=new LTPaintWeightFixture{input=new LTPaintWeightInput{size=1,
                stamps=new[]{new LTPaintWeightMath.Stamp{kind=3,slot=3,maskOffset=0},
                    new LTPaintWeightMath.Stamp{kind=3,slot=slot,maskOffset=1},
                    new LTPaintWeightMath.Stamp{kind=3,slot=5,maskOffset=2}},
                hits=Array.Empty<LTPaintWeightMath.RoadHit>(),coordinates=new Vector2[1],masks=new float[3],terrain=new LTPaintTerrainSnapshot()}};
            for(int z=0;z<=128;z++)for(int x=0;x<=64;x++)
            {
                float px=x*.25f,pz=z*.25f;float weight=road.PaintWeight(px,pz);
                fixture.input.masks[0]=.65f;fixture.input.masks[1]=weight;fixture.input.masks[2]=.2f;
                var expected=new LTPaintWeightMath.Weights{a=new Vector4(1,0,0,0)};
                expected.Composite(3,.65f);expected.Composite(slot,weight);expected.Composite(5,.2f);
                Require(LTPaintWeightFixture.Same(fixture.Scalar(0),expected.Pack()),"exact ordered geometric paint worker pixels");paintPixels++;
            }
        }
        // Hard rims/edge bites remain fully displacement-suppressed even with no
        // shoulder or blend, including chunks just outside the old road footprint.
        var narrow=s;narrow.shoulderWidth=narrow.blendWidth=0;var thin=Road(narrow);
        for(int i=0;i<thin.geometricPits.Count;i++)for(int j=0;j<thin.geometricPits[i].Count;j++)
        {
            var p=thin.geometricPits[i][j];Require(thin.Intersects(new Rect(p.x,p.y,.001f,.001f)),"edge-chip dirty-region coverage");
            Require(thin.DisplacementSuppression(p.x,p.y,.2f)>.999f,"all rim vertices protected from displacement");
        }
        var pitFixture=new LTRoadGeometryMath.Pit(new[]{new Vector2(6,13),new Vector2(9,12),new Vector2(11,14),new Vector2(9,17),new Vector2(6,16)},.4f,beveled?.12f:0,beveled?.05f:0);
        if(connected)pitFixture=new LTRoadGeometryMath.Pit(new[]{new Vector2(6,12),new Vector2(10,12),new Vector2(10,14),new Vector2(8,14),
            new Vector2(8,15),new Vector2(11,15),new Vector2(11,18),new Vector2(6,18),new Vector2(6,16),new Vector2(7,16),new Vector2(7,14),new Vector2(6,14)},.4f,allowConcave:true);
        var field=LTRoadGeometryMath.Field.Combine(new[]{pitFixture});
        int probes=0,variants=0;int firstTriangles=0,lastTriangles=0;
        float H(float x,float z)=>2+.01f*x+.02f*z;
        for(int cells=64;cells>=2;cells/=2)
        {
            var rect=new Rect(0,0,16,32);var plan=LTStampMesh.Plan(rect,cells,false,.01f,300000,new List<LTStampMesh.Zone>(),H);
            var forest=new LTBalancedForest(1,1,300000,new Dictionary<int,List<Vector3Int>>{{0,plan}});forest.Balance();
            LTStampMesh.Emit(rect,new Vector2(16,32),300000,forest,0,forest.Plan(0),H,null,out var v,out var n,out var uv,out var tris,asphaltPits:field);
            Verify(v,tris,rect,field,H);probes++;
            if(cells==64)firstTriangles=tris.Length/3;if(cells==2)lastTriangles=tris.Length/3;
            Require(v.Length==uv.Length&&v.Length==n.Length&&n.Any(p=>Math.Abs(p.y)<1e-6),"wall normals and UV arrays");
        }
        Require(lastTriangles<firstTriangles/10,"distant geometry reduces with ordinary terrain LOD");
        // A tiny pit entirely inside a single coarse triangle must not disappear.
        var small=LTRoadGeometryMath.Field.Combine(new[]{new LTRoadGeometryMath.Pit(new[]{new Vector2(2,10),new Vector2(3,10),new Vector2(3,11),new Vector2(2,11)},.3f)});
        var single=new List<Vector3Int>{new Vector3Int(0,0,LTSpatialLODMath.N)};
        LTStampMesh.Emit(new Rect(0,0,16,32),new Vector2(16,32),10000,null,0,single,H,null,out var sv,out _,out _,out var si,midpoint:(p,e)=>false,asphaltPits:small);
        Verify(sv,si,new Rect(0,0,16,32),small,H);
        // Mixed levels, inter-chunk and internal patch seams; translated origin.
        foreach(float shift in new[]{0f,-512f,4096f})
        {
            var shifted=new LTRoadGeometryMath.Pit(Enumerable.Range(0,pitFixture.Count).Select(i=>pitFixture[i]+new Vector2(shift,0)).ToArray(),.4f,beveled?.12f:0,beveled?.05f:0,allowConcave:connected);
            var damage=LTRoadGeometryMath.Field.Combine(new[]{shifted});
            var outputs=new LTSpatialLODMath.Output[2];var chunks=new LTSpatialLODTopology.Chunk[2];
            var plans=new Dictionary<int,List<Vector3Int>>();
            for(int c=0;c<2;c++)plans[c]=LTStampMesh.Plan(new Rect(shift+c*8,0,8,32),16,false,.01f,300000,new List<LTStampMesh.Zone>(),H);
            var forest=new LTBalancedForest(2,1,300000,plans);forest.Balance();
            for(int c=0;c<2;c++)
            {
                int chunk=c;var rect=new Rect(shift+c*8,0,8,32);var fine=forest.Plan(c);
                var preparation=new LTLODMesh.Preparation(fine,rect,H);var levels=new List<Vector3Int>[5];levels[0]=fine;
                for(int l=1;l<5;l++)levels[l]=preparation.Coarsen(l,100,null,null,2,false);
                int Mask(Vector3Int p){int mask=0;for(int e=0;e<4;e++)if(forest.Midpoint(chunk,p,e))mask|=1<<e;return mask;}
                var output=LTSpatialLODMath.BuildLayout(levels,2,300000,Mask);outputs[c]=output;
                LTStampMesh.EmitSpatialVariants(output,rect,new Vector2(16,32),300000,H,null,asphaltPits:damage);
                chunks[c]=new LTSpatialLODTopology.Chunk{cells=output.cells,patches=output.patches};
                LTStampMesh.Emit(rect,new Vector2(16,32),300000,forest,c,fine,H,null,out var fv,out _,out _,out var fi,asphaltPits:damage);
                Require(SpatialTriangles(fv,fi).SetEquals(SpatialTriangles(output.vertices,output.baseIndices)),"spatial LOD0 exactly matches collider mesh");
            }
            var topology=new LTSpatialLODTopology(2,1,2,chunks);var random=new System.Random(153);
            for(int attempt=0;attempt<40;attempt++)
            {
                for(int c=0;c<2;c++)for(int p=0;p<4;p++)topology.SetLevel(c,p,random.Next(5));
                topology.Update();topology.Validate();
                var allV=new List<Vector3>();var allI=new List<int>();
                for(int c=0;c<2;c++)
                {
                    var indices=new List<int>();topology.WriteIndices(c,indices);int start=allV.Count;
                    allV.AddRange(outputs[c].vertices.Select(p=>p+new Vector3(c*8,0,0)));allI.AddRange(indices.Select(i=>i+start));
                }
                Verify(allV.ToArray(),allI.ToArray(),new Rect(shift,0,16,32),damage,H);variants++;
            }
        }
        bool rejected=false;
        try{LTRoadGeometryMath.Field.Combine(new[]{pitFixture,pitFixture});}catch(InvalidOperationException){rejected=true;}
        Require(rejected,"overlapping road damage fails before publishing meshes");
        rejected=false;try{LTStampMesh.Emit(new Rect(0,0,16,32),new Vector2(16,32),4,null,0,single,H,null,out _,out _,out _,out _,midpoint:(p,e)=>false,asphaltPits:field);}catch(InvalidOperationException){rejected=true;}
        Require(rejected,"new vertices respect budget");
        // Existing cuts are applied before damage: no generated floor/roof may
        // fill an authored through-hole back in.
        LTStampMesh.Emit(new Rect(0,0,16,32),new Vector2(16,32),10000,null,0,single,H,(x,z)=>true,out var empty,out _,out _,out var emptyIndices,midpoint:(p,e)=>false,asphaltPits:field);
        Require(empty.Length==0&&emptyIndices.Length==0,"through-hole remains empty");
        Console.WriteLine($"PASS Geometric Asphalt: {road.geometricPits.Count} deterministic pits/chips; {probes} whole chunk densities; {variants} mixed spatial LOD layouts; {paintPixels} exact ordered worker pixels; coarse-contained holes; depth, areas, walls, floor, budget, displacement guard. Triangles {firstTriangles} -> {lastTriangles}.");
    }
    static void Verify(Vector3[] vertices,int[] triangles,Rect rect,LTRoadGeometryMath.Field pits,Func<float,float,float> height)
    {
        double projected=0,wall=0;
        for(int i=0;i<triangles.Length;i+=3)
        {
            var a=vertices[triangles[i]];var b=vertices[triangles[i+1]];var c=vertices[triangles[i+2]];
            Require(float.IsFinite(a.x)&&float.IsFinite(a.y)&&float.IsFinite(a.z),"finite geometry");
            var cross=Vector3.Cross(b-a,c-a);Require(cross.sqrMagnitude>1e-18f,"nondegenerate triangle");
            if(Math.Abs(cross.y)<1e-7){wall+=cross.magnitude*.5;continue;}
            Require(cross.y>0,"terrain and bottom face upward");projected+=cross.y*.5;
            var centre=(a+b+c)/3;float x=centre.x+rect.xMin,z=centre.z+rect.yMin;
            bool inside=pits.Contains(x,z);float target=height(x,z);
            if(inside)
            {
                var candidates=new List<LTRoadGeometryMath.Pit>();pits.Query(new Rect(x,z,0,0),candidates);
                target-=candidates.First(p=>p.Contains(x,z)).SurfaceDrop(x,z);
            }
            Require(Math.Abs(centre.y-target)<.0002f,"top removed only inside pit; floor not covered or floating");
        }
        Require(Math.Abs(projected-rect.width*rect.height)<.01,"exact projected area: no missing floor or overlapping surface");
        double expected=0;
        for(int i=0;i<pits.Count;i++)for(int j=0;j<pits[i].Count;j++)
        {
            var pit=pits[i];int k=(j+1)%pit.Count;
            double dx=pit.InnerX(j)-pit.InnerX(k),dz=pit.InnerZ(j)-pit.InnerZ(k);
            expected+=Math.Sqrt(dx*dx+dz*dz)*(pit.depth-pit.bevelDepth);
        }
        Require(Math.Abs(wall-expected)<.015,"all perimeter walls, no internal cell/chunk walls");
    }
}
