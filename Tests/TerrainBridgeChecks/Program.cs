using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
partial class Checks
{
    static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
    static (int,int) Case(float cutOffset,float retopoHeight,float density,float blend,float slope=0,bool irregular=false,float weld=0,bool cave=false,Vector3[] customSource=null,int[] customFaces=null,Func<Vector3,float> cellAt=null)
    {
        float h(float x,float z)=>slope*x+.13f*z;
        Vector3 shift=new Vector3(200,10,95);
        var source=new[]{new Vector3(-3,-5,-3),new Vector3(3,-5,-3),new Vector3(3,-5,3),new Vector3(-3,-5,3),
                         new Vector3(-2,9,-2),new Vector3(2,9,-2),new Vector3(2,9,2),new Vector3(-2,9,2)};
        var faces=new[]{0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7};
        if(customSource!=null){source=customSource;faces=customFaces;}
        var p=new List<Vector3>();var n=new List<Vector3>();var tangents=new List<Vector4>();var uv=new List<Vector2>();var indices=new List<int>();
        var rim=new List<RockClipVertex>();
        for(int i=0;i<faces.Length;i+=3)
        {
            if(customSource==null){int swap=faces[i+1];faces[i+1]=faces[i+2];faces[i+2]=swap;}
            Vector3 normal=Vector3.Cross(source[faces[i+1]]-source[faces[i]],source[faces[i+2]]-source[faces[i]]).normalized;
            RockClipVertex V(int id)
            {
                Vector3 v=source[id];return new RockClipVertex{position=v+shift,normal=cave?-normal:normal,tangent=new Vector4(1,0,0,cave?-1:1),distance=cave?h(v.x,v.z)-retopoHeight-v.y:v.y-h(v.x,v.z)-retopoHeight};
            }
            var clipped=ClipRockTriangle(V(faces[i]),V(faces[i+1]),V(faces[i+2]));
            CollectRockBoundarySegment(clipped,rim);if(clipped.Count<3)continue;
            int first=p.Count;foreach(var v in clipped){p.Add(v.position);n.Add(v.normal);tangents.Add(v.tangent);uv.Add(v.uv);}
            for(int k=1;k+1<clipped.Count;k++)indices.AddRange(new[]{first,first+(cave?k+1:k),first+(cave?k:k+1)});
        }
        var rockLoops=OrderedBoundaryLoops(rim);Require(rockLoops.Count==1,"source clip loop");
        float extent=3+cutOffset;var terrain=new List<RockClipVertex>();
        var corners=new[]{new Vector3(-extent,0,-extent),new Vector3(extent,0,-extent),new Vector3(extent,0,extent),new Vector3(-extent,0,extent)};
        for(int side=0;side<4;side++)
        {
            var times=irregular?new[]{0f,.0003f,.13f,.5f,.97f}:new[]{0f,.25f,.5f,.75f};
            foreach(float t in times)
            {
                Vector3 v=Vector3.Lerp(corners[side],corners[(side+1)%4],t);v.y=h(v.x,v.z);
                terrain.Add(new RockClipVertex{position=v+shift,normal=new Vector3(-slope,1,-.13f).normalized,tangent=new Vector4(1,0,0,1)});
            }
        }
        int start=p.Count;var contact=new List<int>();var outer=new HashSet<int>();var outerEdges=new HashSet<long>(GeometryEdgeComparer);
        int Add(Vector3 v,Vector3 normal,Vector4 tangent,Vector2 tex){int id=p.Count;p.Add(v);n.Add(normal);tangents.Add(tangent);uv.Add(tex);return id;}
        BuildContourBridge(rockLoops,new List<List<RockClipVertex>>{terrain},MeshContactCell(density),200000,new Vector3(400,50,400),
            Add,(a,b,c)=>contact.AddRange(new[]{a,b,c}),outer,outerEdges,blend,cellAt);
        int end=p.Count;var meshes=new[]{indices,contact};
        StitchRockRim(p,n,tangents,uv,meshes,1,start,rim);
        try{WeldGeneratedTopology(p,n,tangents,uv,meshes,Matrix4x4.identity,start,end,weld,outer,outerEdges);}
        catch(Exception e){throw new Exception($"offset={cutOffset}, height={retopoHeight}, density={density}, blend={blend}: {e.Message}",e);}
        var edgeFaces=new Dictionary<long,List<(int,int)>>();
        foreach(var tris in meshes)for(int i=0;i<tris.Count;i+=3)
        {
            int a=tris[i],b=tris[i+1],c=tris[i+2];Require(a!=b&&b!=c&&a!=c,"collapsed triangle");
            Require(Vector3.Cross(p[b]-p[a],p[c]-p[a]).sqrMagnitude>1e-14f,"zero triangle");
            for(int k=0;k<3;k++)
            {
                int x=tris[i+k],y=tris[i+(k+1)%3];long key=MeshEdge(x,y);
                if(!edgeFaces.TryGetValue(key,out var list))edgeFaces[key]=list=new List<(int,int)>();list.Add((x,y));
            }
        }
        foreach(var list in edgeFaces.Values)
        {
            Require(list.Count<=2,"nonmanifold");
            if(list.Count==2)Require(list[0].Item1==list[1].Item2&&list[0].Item2==list[1].Item1,"opposite face winding");
            else foreach(int id in new[]{list[0].Item1,list[0].Item2})
            {
                Vector3 v=p[id]-shift;
                Require(Math.Abs(Math.Abs(v.x)-extent)<.001f||Math.Abs(Math.Abs(v.z)-extent)<.001f,"hole in geometry");
            }
        }
        Require(p.All(v=>float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z)),"invalid coordinates");
        if(captureGeometry)
            lastGeometry=string.Join(";",p.Select(v=>$"{v.x:R},{v.y:R},{v.z:R}"))+"#"+
                string.Join("|",meshes.Select(m=>string.Join(",",m)))+"#"+
                string.Join(";",n.Select(v=>$"{v.x:R},{v.y:R},{v.z:R}"))+"#"+
                string.Join(";",uv.Select(v=>$"{v.x:R},{v.y:R}"))+"#"+
                string.Join(";",tangents.Select(v=>$"{v.x:R},{v.y:R},{v.z:R},{v.w:R}"));
        return(p.Count,meshes.Sum(m=>m.Count)/3);
    }
    static void Main()
    {
        RoadMathChecks.Run();
        RoadNetworkChecks();
        SpatialLODChecks();
        StagedBuildChecks();
        LocalLODQueueChecks();
        LODJobChecks();
        HeightSamplingChecks();
        HeightJobChecks();
        StagedBalanceChecks();
        LODPreparationChecks();
        RoadTerrainLODChecks();
        RoadVariationChecks();
        RoadWheelTracksChecks();
        RoadPaintCacheChecks();
        RoadTilingVegetationChecks();
        RoadModuleChecks();
        RoadIntegrationChecks();
        PaintChecks();
        EdgeBenchmark();
        CutRowsChecks();
        ChunkBoundaryChecks();
        BoundaryDeltaChecks();
        LocalBalanceChecks();
        CachedBalanceChecks();
        GeometryHashChecks();
        SeamNormalChecks();
        ContourChecks();
        MultipleStampChecks();
        int cases=0;
        foreach(float cut in new[]{.05f,.5f,1f,3.67f,6f})
        foreach(float height in new[]{.05f,.5f,1f,3f,6f})
        {
            var before=Case(cut,height,.7f,.1f,.2f);
            var after=Case(cut,height,.7f,5f,.2f);
            Require(before==after,"Blend Distance changes topology density");
            cases+=2;
        }
        var coarse=Case(2,2,.7f,3);var fine=Case(2,2,2,3);
        Require(fine.Item1>coarse.Item1,"density does not increase resolution");cases+=2;
        Case(3.67f,1,.7f,5,.4f,true);cases++;
        var unwelded=Case(2,2,1,3);var welded=Case(2,2,1,3,weld:.5f);
        Require(welded.Item1<unwelded.Item1,"post-density connected weld did not remove vertices");cases+=2;
        foreach(float cut in new[]{.5f,2f,4f})
        foreach(float height in new[]{.1f,1f,2f})
        foreach(float weld in new[]{0f,.5f})
        {Case(cut,height,.7f,2,.1f,false,weld,true);cases++;}
        Console.WriteLine($"PASS {cases} clipped-rock/bridge cases: offsets, heights, blend-independent density, irregular rims, watertight interior, consistent winding.");
    }

    static void MultipleStampChecks()
    {
        List<RockClipVertex> Loop(float left,float right)=>new List<RockClipVertex>{
            new RockClipVertex{position=new Vector3(left,0,-1)},new RockClipVertex{position=new Vector3(right,0,-1)},
            new RockClipVertex{position=new Vector3(right,0,1)},new RockClipVertex{position=new Vector3(left,0,1)}};
        List<Vector3> Segments(List<RockClipVertex> loop)=>Enumerable.Range(0,loop.Count)
            .SelectMany(i=>new[]{loop[i].position,loop[(i+1)%loop.Count].position}).ToList();
        var left=Loop(-3,-1);var right=Loop(1,3);var leftSegments=Segments(left);var rightSegments=Segments(right);
        int Owner(Vector3 p)=>Math.Abs(ContactCutDistance(leftSegments,p,0))<Math.Abs(ContactCutDistance(rightSegments,p,0))?10:20;
        var loops=new List<List<RockClipVertex>>{left,right};
        Require(SelectOwnedBoundaryLoops(loops,10,Owner).Select(v=>v.position).SequenceEqual(leftSegments),"left stamp takes foreign edges");
        Require(SelectOwnedBoundaryLoops(loops,20,Owner).Select(v=>v.position).SequenceEqual(rightSegments),"right stamp takes foreign edges");
        loops.Reverse();
        Require(SelectOwnedBoundaryLoops(loops,10,Owner).Count==8,"ownership depends on loop order");
        Require(SelectOwnedBoundaryLoops(loops,30,Owner).Count==0,"unrelated stamp receives a contour");
        bool rejected=false;
        try{SelectOwnedBoundaryLoops(new List<List<RockClipVertex>>{Loop(-3,3)},10,Owner);}
        catch(InvalidOperationException ex){rejected=ex.Message.Contains("Shared multi-object bridges");}
        Require(rejected,"merged cut must fail before terrain is changed");
        Require(ContactCutDistance(leftSegments,new Vector3(-2,0,0),.5f)<0,"inside cut sign");
        Require(Math.Abs(ContactCutDistance(leftSegments,new Vector3(-3.5f,0,0),.5f))<1e-6f,"cut offset boundary");
        Console.WriteLine("PASS multi-stamp contour ownership: separate rims, order independence, offset distance, merged-cut rejection.");
    }
    static void ContourChecks()
    {
        RockClipVertex V(float x,float z)=>new RockClipVertex{position=new Vector3(x,0,z)};
        var a=V(0,0);var b=V(1,0);var c=V(1,1);var d=V(0,1);
        var square=new List<RockClipVertex>{a,b,b,c,c,d,d,a,b,a};
        Require(OrderedBoundaryLoops(square).Single().Count==4,"duplicate/reversed segments");
        bool rejects(List<RockClipVertex> segments,string marker)
        {try{OrderedBoundaryLoops(segments,"test contour");return false;}catch(InvalidOperationException ex){return ex.Message.Contains(marker)&&ex.Message.Contains("test contour");}}
        Require(rejects(new List<RockClipVertex>{a,b,b,c},"2 open endpoints"),"open contour diagnostic");
        square.AddRange(new[]{a,c});Require(rejects(square,"2 branch points"),"branch contour diagnostic");
        var p=V(200,95);p.distance=-.00000001f;
        var q=V(201,96);q.distance=.00000001f;
        var r=V(201,94);r.distance=1;
        var forward=ClipRockTriangle(p,q,r);var reverse=ClipRockTriangle(q,p,r);
        Require(forward.Any(v=>v.position==new Vector3(200.5f,0,95.5f)),"small signed-distance edge collapsed to endpoint");
        Require(forward.All(v=>reverse.Any(w=>v.position==w.position)),"reverse winding changes cut coordinates");
    }
}
