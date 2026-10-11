using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static LTHoleStampSnapshot Hole(float x,float z,float width,float depth,LTStampShape shape=LTStampShape.Ellipse,
        float angle=0,float step=.25f,LTStampDensityMask mask=null,float threshold=.5f,float sx=1,float sz=1,float lodEdgeCellSize=0)
    {
        float c=(float)Math.Cos(angle*Math.PI/180),s=(float)Math.Sin(angle*Math.PI/180);
        var m=Matrix4x4.identity;m.m00=c*sx;m.m02=s*sz;m.m20=-s*sx;m.m22=c*sz;m.m03=x;m.m23=z;
        var inv=Matrix4x4.identity;inv.m00=c/sx;inv.m02=-s/sx;inv.m20=s/sz;inv.m22=c/sz;inv.m03=-inv.m00*x-inv.m02*z;inv.m23=-inv.m20*x-inv.m22*z;
        return new LTHoleStampSnapshot(m,inv,new Vector2(width,depth),shape,step,mask,threshold,lodEdgeCellSize);
    }
    static void HoleStampChecks()
    {
        Console.WriteLine("PASS Hole Stamp LOD reduction: "+LTHoleStampLODCheck.Run());
        Console.WriteLine("PASS Hole Stamp edge LOD: "+LTHoleEdgeLODCheck.Run());
        HoleEdgeJobParityChecks();
        var circle=Hole(8,8,4,4);
        Require(circle.Contains(8,8)&&circle.Contains(9,8)&&!circle.Contains(11,8),"circle contains");
        Require(!circle.Contains(9.8f,9.8f),"circle is not its bounding box");
        var turned=Hole(8,8,4,2,LTStampShape.Rectangle,90,sx:2,sz:1);
        Require(turned.Contains(8,11)&&!turned.Contains(10,8),"rotated/scaled rectangle");
        var tiny=Hole(3.13f,2.77f,.12f,.12f,step:2);
        Require(tiny.CellSize<=.015001f,"tiny footprint cannot be missed at a coarse requested step");
        var mask=new LTStampDensityMask(16,16,y=>Enumerable.Range(0,16).Select(x=>new Color(x<6&&y>3&&y<12?1:0,0,0,1)).ToArray(),false,new Vector2(0,1),1);
        var masked=Hole(8,8,6,6,LTStampShape.Rectangle,mask:mask);
        Require(masked.Contains(6,8)&&!masked.Contains(10,8),"mask red threshold and UVs");
        Require(!masked.Intersects(new Rect(9,7,1,1))&&masked.Intersects(new Rect(5,7,1,1)),"mask conservative refinement stays local");
        var inverted=new LTStampDensityMask(16,16,y=>Enumerable.Range(0,16).Select(x=>new Color(x<6&&y>3&&y<12?1:0,0,0,1)).ToArray(),true,new Vector2(0,1),1);
        var invHole=Hole(8,8,6,6,LTStampShape.Rectangle,mask:inverted);
        Require(!invHole.Contains(6,8)&&invHole.Contains(10,8),"inverted hole mask");
        var mask2=new LTStampDensityMask(3,3,y=>Enumerable.Range(0,3).Select(x=>new Color(.6f,0,0,1)).ToArray(),false,new Vector2(0,1),1);
        Require(Hole(8,8,4,4,mask:mask2,threshold:.5f).Contains(8,8)&&!Hole(8,8,4,4,mask:mask2,threshold:.7f).Contains(8,8),"threshold changes cut");
        foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,0,-1})
        {bool failed=false;try{Hole(8,8,4,4,step:invalid);}catch(ArgumentException){failed=true;}Require(failed,"invalid hole step rejected");}
        // Mesh volume crosses the shared corner of four chunks, including its
        // tilted contour. Reuse the exact mixed/staged/legacy seam checks below.
        var cubeVertices=new[]{new Vector3(-1,-1,-1),new Vector3(1,-1,-1),new Vector3(1,-1,1),new Vector3(-1,-1,1),
            new Vector3(-1,1,-1),new Vector3(1,1,-1),new Vector3(1,1,1),new Vector3(-1,1,1)};
        var cubeIndices=new[]{0,1,2,0,2,3,4,6,5,4,7,6,0,4,5,0,5,1,1,5,6,1,6,2,2,6,7,2,7,3,3,7,4,3,4,0};
        var cube=new LTHoleMeshData(cubeVertices,cubeIndices);
        var volumeMatrix=Matrix4x4.identity;volumeMatrix.m03=8;volumeMatrix.m23=8;
        var volume=new LTHoleStampSnapshot(new LTHoleMeshVolume(cube,volumeMatrix),.25f);
        var tilt=Matrix4x4.identity;tilt.m11=.8f;tilt.m12=-.6f;tilt.m21=.6f;tilt.m22=.8f;
        var yaw=Matrix4x4.identity;yaw.m00=.8f;yaw.m02=.6f;yaw.m20=-.6f;yaw.m22=.8f;
        var tiltedVolume=new LTHoleStampSnapshot(new LTHoleMeshVolume(cube,volumeMatrix*yaw*tilt),.25f);
        var cases=new[]{new[]{circle},new[]{Hole(8,8,4,3,LTStampShape.Rectangle,27)},new[]{masked},new[]{invHole},
            new[]{tiny},new[]{Hole(7,8,4,4),Hole(9,8,4,4)},new[]{Hole(4,4,24,24,LTStampShape.Rectangle)},Array.Empty<LTHoleStampSnapshot>(),
            new[]{volume},new[]{tiltedVolume},new[]{tiltedVolume,Hole(9,8,2,2)}};
        int lodChecks=0;
        foreach(var holes in cases)
        {
            bool Cut(float x,float z)=>holes.Any(h=>h.Contains(x,0,z));
            var shapeHoles=holes.Where(h=>!h.RequiresHeight).ToArray();
            var volumeHoles=holes.Where(h=>h.RequiresHeight).ToArray();
            var zones=holes.Select(h=>h.CreateZone()).ToList();
            // Deliberately coarse later user density must not erase a hole.
            zones.Insert(0,new LTStampMesh.Zone{bounds=new Rect(0,0,16,16),inverse=Matrix4x4.Translate(new Vector3(-8,0,-8)),size=new Vector2(16,16),shape=LTStampShape.Rectangle,cellSize=4,edgeCellSize=4});
            var plans=new Dictionary<int,List<Vector3Int>>();
            Rect Region(int id)=>new Rect(id%2*8,id/2*8,8,8);
            for(int id=0;id<4;id++)plans[id]=LTSpatialLODMath.PartitionLeaves(LTStampMesh.Plan(Region(id),4,true,.1f,100000,zones,(x,z)=>0,new LTLODMesh.BuildProgress(false)),2);
            var forest=new LTBalancedForest(2,2,100000,plans);forest.Balance();
            var outputs=new LTSpatialLODMath.Output[4];var previews=new LTSpatialLODMath.Output[4];var inputs=new LTSpatialLODTopology.Chunk[4];
            var legacyOutputs=new LTSpatialLODMath.Output[4];var legacyIndices=new int[4][];
            double area=0;
            for(int id=0;id<4;id++)
            {
                int chunk=id;var r=Region(id);var fine=forest.Plan(id);
                var volumeEdges=new LTHoleVolumeLODProtection();
                foreach(var _ in volumeEdges.Prepare(volumeHoles,fine,r,(x,z)=>0)){}
                bool ProtectedArea(Rect area)=>LTHoleStampSnapshot.ProtectsAnyLOD(shapeHoles,area)||volumeEdges.Protects(area);
                var levels=new List<Vector3Int>[5];levels[0]=fine;var prep=new LTLODMesh.Preparation(fine,r,(x,z)=>0);
                for(int l=1;l<5;l++)levels[l]=prep.Coarsen(l,100,null,ProtectedArea,2,false);
                var jobInput=new LTLODJobInput();
                foreach(var _ in jobInput.Prepare(fine,r,Enumerable.Range(1,4).Select(l=>new LTLODJobMath.Level{steps=l,tolerance=100}).ToArray(),(x,z)=>0,null,ProtectedArea,2,false)){}
                var jobLevels=SolveJobInput(jobInput);
                for(int l=1;l<5;l++)Require(jobLevels[l-1].SequenceEqual(levels[l]),"hole protection has identical manual/Jobs input results");
                int Midpoints(Vector3Int p){int bits=0;for(int s=0;s<4;s++)if(forest.Midpoint(chunk,p,s))bits|=1<<s;return bits;}
                bool Protected(Vector3Int p)=>ProtectedArea(new Rect(r.xMin+p.x/(float)LTSpatialLODMath.N*8,r.yMin+p.y/(float)LTSpatialLODMath.N*8,p.z/(float)LTSpatialLODMath.N*8,p.z/(float)LTSpatialLODMath.N*8));
                var data=LTSpatialLODMath.BuildLayout(levels,2,100000,Midpoints,Protected);
                LTStampMesh.EmitSpatialVariants(data,r,new Vector2(16,16),100000,(x,z)=>0,holes.Length>0?Cut:null);
                LTStampMesh.Emit(r,new Vector2(16,16),100000,forest,id,fine,(x,z)=>0,holes.Length>0?Cut:null,out var v,out _,out _,out var tris,null,new LTLODMesh.BuildProgress(false));
                Require(SpatialTriangles(v,tris).SetEquals(SpatialTriangles(data.vertices,data.baseIndices)),"hole LOD0 exactly matches spatial preview geometry");
                for(int i=0;i<tris.Length;i+=3)
                {
                    float signed=Vector3.Cross(v[tris[i+1]]-v[tris[i]],v[tris[i+2]]-v[tris[i]]).y;
                    Require(signed>0,"hole mesh has no flipped/degenerate triangles");area+=signed*.5;
                }
                outputs[id]=data;inputs[id]=new LTSpatialLODTopology.Chunk{cells=data.cells,patches=data.patches};
                var preview=LTSpatialLODMath.BuildLayout(new[]{fine},2,100000,Midpoints,Protected);
                LTStampMesh.EmitSpatialVariants(preview,r,new Vector2(16,16),100000,(x,z)=>0,holes.Length>0?Cut:null);previews[id]=preview;
                // Legacy whole-chunk path must keep the same rim as well.
                var legacy=prep.Coarsen(4,100,null,ProtectedArea,1,true);
                var lf=new LTBalancedForest(1,1,100000,new Dictionary<int,List<Vector3Int>>{{0,legacy}});lf.Balance();
                var masks=LTLODMesh.BoundaryMasks(fine,forest.BoundaryStitches(id,fine));
                LTStampMesh.Emit(r,new Vector2(16,16),100000,lf,0,lf.Plan(0),(x,z)=>0,holes.Length>0?Cut:null,out var legacyVertices,out _,out _,out legacyIndices[id],null,new LTLODMesh.BuildProgress(false),
                    (p,s)=>LTLODMesh.LocalMidpoint(lf,masks,p,s));
                legacyOutputs[id]=new LTSpatialLODMath.Output{vertices=legacyVertices};
            }
            Require(holes.Length==0?Math.Abs(area-256)<1e-5:area<255.9999,"hole removed real surface area / removal restored ground");
            if(holes.Length==1&&holes[0]==circle)Require(Math.Abs(area-(256-4*Math.PI))<.12,"circle area accurate at requested contour resolution");
            var topo=new LTSpatialLODTopology(2,2,2,inputs);topo.Update();
            int[][] Read(LTSpatialLODTopology t)
            {var all=new int[4][];var list=new List<int>();for(int c=0;c<4;c++){t.WriteIndices(c,list);all[c]=list.ToArray();}return all;}
            var boundary=SpatialBoundary(outputs,Read(topo),2,8,16);
            Require(SpatialBoundary(legacyOutputs,legacyIndices,2,8,16).SetEquals(boundary),"whole-chunk far LOD preserves the same hole rim and chunk seams");
            foreach(int level in new[]{0,1,2,3,4,0,4})
            {
                for(int c=0;c<4;c++)for(int p=0;p<4;p++)topo.SetLevel(c,p,(c+p)%2==0?level:0);
                topo.Update();topo.Validate();Require(SpatialBoundary(outputs,Read(topo),2,8,16).SetEquals(boundary),"same hole boundary for mixed near/far LODs");lodChecks++;
            }
            var staged=outputs.Select((o,c)=>c==0?previews[c]:o).ToArray();
            var stagedTopo=new LTSpatialLODTopology(2,2,2,staged.Select(o=>new LTSpatialLODTopology.Chunk{cells=o.cells,patches=o.patches}).ToArray());
            for(int c=0;c<4;c++)for(int p=0;p<4;p++)stagedTopo.SetLevel(c,p,4);
            stagedTopo.Update();stagedTopo.Validate();Require(SpatialBoundary(staged,Read(stagedTopo),2,8,16).SetEquals(boundary),"staged LOD0 plus old coarse neighbours preserves hole");lodChecks++;
        }
        string editor=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs"),integration=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTHoleStampEditor.cs");
        Require(editor.Contains("CaptureHole(child,w,result)")&&editor.Contains("s.hole.Contains(x,volumeHeight,z)")&&
            editor.Contains("s.hole.RequiresHeight")&&editor.Contains("volumeHeight=Evaluate(world,stamps,x,z)"),"hole wired into capture and height-aware common terrain cutter");
        Require(editor.Split("PrepareLODProtectedArea(local,").Length==4&&editor.Split("HasTerrainCuts(local)").Length==3&&
            integration.Contains("LTHoleStampSnapshot.ProtectsAnyLOD(holes,area)")&&!integration.Contains("regions.Add(s.hole.ProtectedBounds)"),"manual/deferred and spatial/legacy protection uses actual hole, not its black canvas");
        Require(integration.Contains("s.hole!=null&&!s.hole.RequiresHeight")&&integration.Contains("volumeEdges.Prepare(volumes,fine,rect,height,baseMask,regularTopology,holes)")&&
            integration.Contains("volumeEdges?.Protects(area)"),"volume LOD uses prepared surface contour, not the projection fallback");
        Require(editor.Split("r,(x,z)=>Evaluate(w,local,x,z),value=>protectedArea=value,").Length==4,
            "all contour snapshots use the cutter's CPU height, not a rounded prefetched Burst height");
        Require(editor.Contains("c.mesh.GetIndexCount(0)>0?c.mesh:null"),"fully cut chunk has no collider");
        Require(integration.Contains("ConditionalWeakTable")&&integration.Contains("cache.key!=maskKey")&&integration.Contains("AssetInputHash(stamp.mask)"),"immutable mask snapshots reused and reimport invalidated");
        Require(editor.Contains("Mark(w,s,a.bounds")&&editor.Contains("Mark(w,s,b.bounds")&&integration.Contains("bounds=snapshot.ProtectedBounds"),"old/new footprints participate in local move/delete invalidation");
        Console.WriteLine($"PASS Hole Stamp: {cases.Length} fixtures / {lodChecks} mixed and staged LOD checks; real cuts, masks, mesh volumes across chunk seams, inversion/threshold, tiny shapes, rotation/scale, overlap, full cuts, restoration, LOD0 parity and integration contracts. Native collider test is separate.");
    }
    static void HoleEdgeJobParityChecks()
    {
        var fine=LODFixture(64);var region=new Rect(0,0,16,16);
        var settings=Enumerable.Range(1,4).Select(l=>new LTLODJobMath.Level{steps=l,tolerance=.2f*l}).ToArray();
        int comparisons=0;
        foreach(bool legacy in new[]{false,true})foreach(var shape in new[]{LTStampShape.Ellipse,LTStampShape.Rectangle})
        {
            var holes=new[]{Hole(7.13f,7.77f,4,6,shape,27,lodEdgeCellSize:4),Hole(11.43f,7.77f,4,6,shape,27,lodEdgeCellSize:4)};
            bool Protect(Rect area)=>LTHoleStampSnapshot.ProtectsAnyLOD(holes,area);
            float Height(float x,float z)=>.002f*x*x+.01f*(float)Math.Sin(z);
            var input=new LTLODJobInput();foreach(var _ in input.Prepare(fine,region,settings,Height,null,Protect,2,legacy)){}
            var result=SolveJobInput(input);var reference=new LTLODMesh.Preparation(fine,region,Height,new LTLODMesh.BuildProgress(false));
            for(int l=0;l<settings.Length;l++)
            {Require(result[l].SequenceEqual(reference.Coarsen(settings[l].steps,settings[l].tolerance,null,Protect,2,legacy)),"simplifying cut edge: deferred Jobs input and synchronous plan differ");comparisons++;}
        }
        var small=Hole(4,4,.12f,.12f,step:2,lodEdgeCellSize:4);
        Require(small.LODEdgeCellSize<=.015001f,"LOD cap keeps tiny shapes open");
        var locked=Hole(4,4,4,4,lodEdgeCellSize:0);
        Require(locked.ProtectsLOD(new Rect(2,3,.1f,.1f)),"zero LOD contour size retains exact rim");
        bool invalid=false;try{Hole(4,4,4,4,lodEdgeCellSize:float.NaN);}catch(ArgumentException){invalid=true;}
        Require(invalid,"non-finite LOD edge setting rejected");
        Console.WriteLine($"PASS hole edge LOD Jobs-input parity: {comparisons} spatial/legacy levels, curved heights, close-hole bridge, tiny cap and exact-lock setting. Managed scalar, not native Burst.");
    }
}
