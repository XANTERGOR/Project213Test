using System;
using System.Collections.Generic;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void HoleVolumeContourChecks()
    {
        int comparisons=0;
        string result=LTHoleContourLODCheck.Run(comparePlans:(fine,region,height,protect)=>
        {
            var settings=Enumerable.Range(1,4).Select(l=>new LTLODJobMath.Level{steps=l,tolerance=100}).ToArray();
            foreach(bool legacy in new[]{false,true})
            {
                int divisions=legacy?1:2;
                var input=new LTLODJobInput();foreach(var _ in input.Prepare(fine,region,settings,height,null,protect,divisions,legacy)){}
                var plans=SolveJobInput(input);var reference=new LTLODMesh.Preparation(fine,region,height,new LTLODMesh.BuildProgress(false));
                for(int l=0;l<4;l++)
                {Require(plans[l].SequenceEqual(reference.Coarsen(settings[l].steps,settings[l].tolerance,null,protect,divisions,legacy)),"mesh contour Jobs-input / manual LOD parity");comparisons++;}
            }
        });
        Console.WriteLine("PASS "+result);
        Console.WriteLine($"PASS mesh contour Jobs-input parity: {comparisons} spatial/legacy levels. Managed scalar, not native Burst.");
        string cache=System.IO.File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTHoleMeshCache.cs");
        Require(cache.Contains("mesh-volume-contour-lod-v3:")&&cache.Contains("stamp.lodEdgeCellSize.ToString")&&cache.Contains("stamp.meshLodContourError.ToString"),
            "new contour settings invalidate persisted plans and snapshots");
    }
    static void HoleMeshVolumeChecks()
    {
        var vertices=new[]{new Vector3(-1,-1,-1),new Vector3(1,-1,-1),new Vector3(1,-1,1),new Vector3(-1,-1,1),
            new Vector3(-1,1,-1),new Vector3(1,1,-1),new Vector3(1,1,1),new Vector3(-1,1,1)};
        var faces=new[]{0,1,2,0,2,3,4,6,5,4,7,6,0,4,5,0,5,1,1,5,6,1,6,2,2,6,7,2,7,3,3,7,4,3,4,0};
        LTHoleMeshData Data(Vector3[] v,int[] f)=>new LTHoleMeshData(v,f);
        var data=Data(vertices,faces);var cube=new LTHoleMeshVolume(data,Matrix4x4.identity);
        foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,-1})foreach(bool error in new[]{false,true})
        {
            bool failed=false;try{new LTHoleStampSnapshot(cube,.125f,error?4:invalid,error?invalid:.5f);}catch(ArgumentException){failed=true;}
            Require(failed,"invalid mesh LOD contour settings rejected");
        }
        int queries=0;
        for(int z=-15;z<=15;z++)for(int x=-15;x<=15;x++)for(int y=-15;y<=15;y++)
        {
            float px=x*.073f,py=y*.073f,pz=z*.073f;
            bool expected=Math.Abs(px)<1&&Math.Abs(py)<1&&Math.Abs(pz)<1;
            Require(cube.Contains(px,py,pz)==expected,$"cube parity at {px},{py},{pz}");queries++;
        }
        Require(cube.Contains(0,0,0),"vertical ray through both face diagonals counted once");
        Require(!cube.Contains(0,-2,0)&&!cube.Contains(0,2,0),"above and below are not XZ projections");
        // Two aligned shells: a bottom/top envelope would incorrectly cut the gap.
        var stacked=vertices.Select(v=>v+new Vector3(0,3,0)).Concat(vertices.Select(v=>v-new Vector3(0,3,0))).ToArray();
        var doubled=faces.Concat(faces.Select(i=>i+8)).ToArray();
        var two=new LTHoleMeshVolume(Data(stacked,doubled),Matrix4x4.identity);
        Require(two.Contains(0,3,0)&&two.Contains(0,-3,0)&&!two.Contains(0,0,0)&&!two.Contains(0,-5,0),"stacked shells retain vertical air gap");
        var nested=vertices.Select(v=>v*2).Concat(vertices).ToArray();
        var hollow=new LTHoleMeshVolume(Data(nested,doubled),Matrix4x4.identity);
        Require(!hollow.Contains(0,0,0)&&hollow.Contains(1.5f,0,0),"nested shell parity leaves a cavity");
        // Concave closed prism extruded from an L polygon; winding is consistent.
        var polygon=new[]{new Vector2(0,0),new Vector2(3,0),new Vector2(3,1),new Vector2(1,1),new Vector2(1,3),new Vector2(0,3)};
        var pv=polygon.Select(p=>new Vector3(p.x,-1,p.y)).Concat(polygon.Select(p=>new Vector3(p.x,1,p.y))).ToArray();
        var pf=new List<int>();var cap=new[]{0,1,3,1,2,3,0,3,5,3,4,5};
        for(int i=0;i<cap.Length;i+=3){pf.AddRange(new[]{cap[i],cap[i+1],cap[i+2],cap[i]+6,cap[i+2]+6,cap[i+1]+6});}
        for(int i=0;i<6;i++){int j=(i+1)%6;pf.AddRange(new[]{i,i+6,j+6,i,j+6,j});}
        var concave=new LTHoleMeshVolume(Data(pv,pf.ToArray()),Matrix4x4.identity);
        Require(concave.Contains(.5f,0,2)&&concave.Contains(2,0,.5f)&&!concave.Contains(2,0,2),"concavity not filled by bounds");
        // UV seams duplicate every triangle vertex, still a closed geometric mesh.
        var seams=faces.Select(i=>vertices[i]).ToArray();
        var seamCube=new LTHoleMeshVolume(Data(seams,Enumerable.Range(0,seams.Length).ToArray()),Matrix4x4.identity);
        Require(seamCube.Contains(0,0,0),"exact vertex welding handles normal/UV seams");
        var random=new System.Random(9051);
        for(int n=0;n<15;n++)
        {
            float ax=(float)random.NextDouble()*5,ay=(float)random.NextDouble()*5;
            float cx=(float)Math.Cos(ax),sx=(float)Math.Sin(ax),cy=(float)Math.Cos(ay),sy=(float)Math.Sin(ay);
            var rx=Matrix4x4.identity;rx.m11=cx;rx.m12=-sx;rx.m21=sx;rx.m22=cx;
            var ry=Matrix4x4.identity;ry.m00=cy;ry.m02=sy;ry.m20=-sy;ry.m22=cy;
            var scale=Matrix4x4.identity;scale.m00=.4f+n*.1f;scale.m11=2;scale.m22=.7f;
            var matrix=ry*rx*scale;matrix.m03=12;matrix.m13=4;matrix.m23=-3;
            var rotated=new LTHoleMeshVolume(data,matrix);
            for(int i=0;i<500;i++)
            {
                var local=new Vector3((float)random.NextDouble()*3-1.5f,(float)random.NextDouble()*3-1.5f,(float)random.NextDouble()*3-1.5f);
                if(Math.Abs(Math.Abs(local.x)-1)<.0001||Math.Abs(Math.Abs(local.y)-1)<.0001||Math.Abs(Math.Abs(local.z)-1)<.0001)continue;
                var p=matrix.MultiplyPoint3x4(local);
                Require(rotated.Contains(p.x,p.y,p.z)==(Math.Abs(local.x)<1&&Math.Abs(local.y)<1&&Math.Abs(local.z)<1),"XYZ rotation + scale + translation");queries++;
            }
        }
        void Rejected(Vector3[] v,int[] f,string label)
        {bool failed=false;try{Data(v,f);}catch(ArgumentException){failed=true;}Require(failed,"reject "+label);}
        Rejected(vertices,faces.Skip(3).ToArray(),"open mesh");
        Rejected(vertices,faces.Concat(faces.Take(3)).ToArray(),"nonmanifold mesh");
        var wrong=(int[])faces.Clone();(wrong[0],wrong[1])=(wrong[1],wrong[0]);Rejected(vertices,wrong,"inconsistent winding");
        var bad=(Vector3[])vertices.Clone();bad[0].x=float.NaN;Rejected(bad,faces,"nonfinite vertex");
        var outOfRange=(int[])faces.Clone();outOfRange[0]=200;Rejected(vertices,outOfRange,"invalid index");
        var copied=(Vector3[])vertices.Clone();var saved=Data(copied,faces);copied[0]=new Vector3(100,100,100);
        Require(new LTHoleMeshVolume(saved,Matrix4x4.identity).Contains(0,0,0),"caller mutation cannot alter immutable snapshot");
        Require(saved.ContentHash==data.ContentHash&&Data(vertices.Select(v=>v*2).ToArray(),faces).ContentHash!=data.ContentHash,"numeric mesh cache signature");
        // Real planner/emitter and mixed-resolution input, not just point tests.
        var move=Matrix4x4.identity;move.m03=4;move.m23=4;
        var hole=new LTHoleStampSnapshot(new LTHoleMeshVolume(data,move),.125f);
        bool Cut(float x,float z)=>hole.Contains(x,0,z);
        var region=new Rect(0,0,8,8);
        var plan=LTStampMesh.Plan(region,4,true,.1f,100000,new List<LTStampMesh.Zone>{hole.CreateZone()},(x,z)=>0,new LTLODMesh.BuildProgress(false));
        var forest=new LTBalancedForest(1,1,100000,new Dictionary<int,List<Vector3Int>>{{0,plan}});forest.Balance();
        var fine=forest.Plan(0);var prep=new LTLODMesh.Preparation(fine,region,(x,z)=>0);
        var protection=new LTHoleVolumeLODProtection();
        foreach(var _ in protection.Prepare(new[]{hole},fine,region,(x,z)=>0)){}
        var coarse=prep.Coarsen(4,100,null,protection.Protects,1,false);
        Require(coarse.Count<fine.Count,"surroundings still coarsen");
        LTStampMesh.Emit(region,new Vector2(8,8),100000,forest,0,fine,(x,z)=>0,Cut,out var output,out _,out _,out var triangles,null,new LTLODMesh.BuildProgress(false));
        Require(triangles.Length>0,"volume cut leaves outer terrain");
        for(int i=0;i<triangles.Length;i+=3)
        {
            var p=(output[triangles[i]]+output[triangles[i+1]]+output[triangles[i+2]])/3;
            Require(!(p.x>3.05f&&p.x<4.95f&&p.z>3.05f&&p.z<4.95f),"emitter removed hole interior");
        }
        Require(hole.RequiresHeight&&protection.BoundaryCellCount>0&&!protection.Protects(new Rect(0,0,1,1)),"local contour LOD protection");
        move.m13=5;var raised=new LTHoleStampSnapshot(new LTHoleMeshVolume(data,move),.125f);
        Require(!raised.Contains(4,0,4),"raising cutter restores ground");
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)cube.Contains(.1f,.2f,.3f);
        Require(GC.GetAllocatedBytesForCurrentThread()==before,"allocation-free containment queries");
        before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)protection.Protects(region);
        Require(GC.GetAllocatedBytesForCurrentThread()==before,"allocation-free published contour queries");
        var raisedProtection=new LTHoleVolumeLODProtection();
        foreach(var _ in raisedProtection.Prepare(new[]{raised},fine,region,(x,z)=>0)){}
        Require(raisedProtection.BoundaryCellCount==0&&!raisedProtection.Protects(region)&&protection.Protects(region),"new volume revision releases projected terrain without mutating old snapshot");
        var cancelled=new LTHoleVolumeLODProtection();
        using(var iterator=cancelled.Prepare(new[]{hole},LODFixture(32),region,(x,z)=>0).GetEnumerator())
            Require(iterator.MoveNext(),"contour preparation yields before publication");
        bool incomplete=false;try{cancelled.Protects(region);}catch(InvalidOperationException){incomplete=true;}
        Require(incomplete,"cancelled partial contour must never be published");
        var shifted=Matrix4x4.identity;shifted.m03=896;shifted.m23=384;
        var shiftedHole=new LTHoleStampSnapshot(new LTHoleMeshVolume(data,shifted),.125f);
        var shiftedRegion=new Rect(768,256,256,256);var shiftedFine=LODFixture(128);
        var shiftedProtection=new LTHoleVolumeLODProtection();
        foreach(var _ in shiftedProtection.Prepare(new[]{shiftedHole},shiftedFine,shiftedRegion,(x,z)=>0)){}
        Require(shiftedProtection.BoundaryCellCount>0&&shiftedProtection.Protects(shiftedRegion)&&
            !shiftedProtection.Protects(new Rect(768,256,64,64)),"nonzero chunk origin and dyadic cell lookup");
        move.m13=0;move.m03=4.13f;move.m23=4.27f;
        var enabled=new LTHoleStampSnapshot(new LTHoleMeshVolume(data,move),.125f,4,.5f);
        var uniform=LODFixture(64);int heightCalls=0;
        float CaptureHeight(float x,float z){heightCalls++;return 0;}
        var simplified=new LTHoleVolumeLODProtection();
        foreach(var _ in simplified.Prepare(new[]{enabled},uniform,region,CaptureHeight,p=>0)){}
        Require(simplified.ReleasedMergeCount>0,"enabled contour releases actual merges");
        int captured=heightCalls;before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)simplified.Protects(region);
        Require(heightCalls==captured&&GC.GetAllocatedBytesForCurrentThread()==before,"published simplified contour has no height callbacks or allocations");
        var overlap=new LTHoleVolumeLODProtection();
        foreach(var _ in overlap.Prepare(new[]{enabled},uniform,region,CaptureHeight,p=>0,null,new[]{Hole(4,4,8,8,LTStampShape.Rectangle)})){}
        Require(overlap.ReleasedMergeCount==0,"nearby shape/mask cuts conservatively block cross-mode contour merges");
        Console.WriteLine($"HOLE MESH VOLUME PASS: {queries} containment queries; concavity, disconnected/nested shells, welded seams, XYZ transforms, validation, immutable cache, terrain emission and local LOD protection. Native collider is separate.");
    }
}
