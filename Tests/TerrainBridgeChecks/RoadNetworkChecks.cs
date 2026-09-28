using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using LocalTerrainPrototype;
using UnityEngine;
partial class Checks
{
    static void RoadNetworkChecks()
    {
        var s=LTRoadMath.Settings.Default;s.projection=LTRoadProjection.Spline;s.width=6;s.shoulderWidth=1;s.blendWidth=2;
        var a=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(0,0,-60)),new LTRoadPoint(new Vector3(0,0,60))},Matrix4x4.identity,s);
        s.textureOffset=new Vector2(70,900);s.textureRepeatMetres=7;
        var b=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(-60,0,0)),new LTRoadPoint(new Vector3(60,0,0))},Matrix4x4.identity,s);
        var rect=new Rect(-32,-32,64,64);const int size=129;
        var map=LTRoadProjectionMath.Bake(new[]{a,b},rect,size);
        Require(map.derivatives!=null,"shared slot gets an adjacent frame slice");
        int samples=0,seams=0;
        for(float z=-8;z<8;z+=.13f)for(float x=-8;x<8;x+=.17f)
        {
            if(Math.Max(a.PaintWeight(x,z),b.PaintWeight(x,z))<.01f)continue;
            var p=LTRoadProjectionMath.Sample(map.coordinates,map.derivatives,rect,size,x,z,out var dx,out var dz);
            int owner=LTRoadProjectionMath.Owner(p);Require(owner==1||owner==2,"visible road must have a valid owner");
            (owner==1?a:b).TryTextureCoordinates(x,z,out var expected,out var right);
            Require((new Vector2(p.r,p.g)-expected).magnitude<.025f,"no interpolation between unrelated UV phases");
            Require((new Vector2(p.b,p.a).normalized-right).magnitude<.0001f,"normal-map orientation follows chosen branch");
            Require(float.IsFinite(dx.x)&&float.IsFinite(dx.y)&&float.IsFinite(dz.x)&&float.IsFinite(dz.y)&&dx.magnitude+dz.magnitude<1,"seam Jacobian must not contain the 900-tile phase jump");samples++;
        }
        for(int i=0;i<map.derivatives.Length;i++)if(map.derivatives[i]!=default(Color))seams++;
        Require(seams>0&&seams<map.coordinates.Length,"Jacobians are only populated near ownership/ROI boundaries");
        var single=LTRoadProjectionMath.Bake(new[]{a},rect,size);
        Require(single.derivatives==null,"single road retains one-slice storage");
        for(float z=-20;z<20;z+=.37f)
        {
            var p=LTRoadProjectionMath.Sample(single.coordinates,null,rect,size,.7f,z,out _,out _);
            a.TryTextureCoordinates(.7f,z,out var uv,out _);Require((new Vector2(p.r,p.g)-uv).sqrMagnitude<1e-8,"single-owner mapping parity");
        }
        // Adjacent chunks agree on a shared texel line, even when it crosses the ownership seam.
        var leftRect=new Rect(-32,-32,32,64);var rightRect=new Rect(0,-32,32,64);
        var left=LTRoadProjectionMath.Bake(new[]{a,b},leftRect,129);var rightMap=LTRoadProjectionMath.Bake(new[]{a,b},rightRect,129);
        for(float z=-3;z<=3;z+=.07f)
        {
            var l=LTRoadProjectionMath.Sample(left.coordinates,left.derivatives,leftRect,129,0,z,out _,out _);
            var r=LTRoadProjectionMath.Sample(rightMap.coordinates,rightMap.derivatives,rightRect,129,0,z,out _,out _);
            Require((new Vector2(l.r,l.g)-new Vector2(r.r,r.g)).magnitude<.025f,"chunk boundary UV continuity");
        }
        int layouts=0;
        foreach(var angles in new[]{new[]{0f,180f},new[]{0f,90f,180f},new[]{0f,90f,180f,270f},new[]{0f,120f,240f}})
        {
            var centre=new Vector3(100,7,200);var ports=new List<LTRoadJunctionMath.Port>();
            foreach(float angle in angles)
            {float r=angle*(float)Math.PI/180;ports.Add(LTRoadJunctionMath.MakePort(centre,centre+new Vector3((float)Math.Sin(r),0,(float)Math.Cos(r))*30,8,4));}
            var node=LTRoadJunctionMath.Build(centre,ports,3,.5f);
            Require(Math.Abs(node.ApplyHeight(100,200,50)-7)<1e-6,"node centre has a common height");
            Require(node.Weight(150,200)==0,"junction influence is bounded");
            Require(node.Intersects(new Rect(99,199,2,2))&&!node.Intersects(new Rect(150,250,2,2)),"density footprint intersects only the node influence");
            var splits=new List<Vector3>();
            foreach(var port in ports)splits.Add(port.centre+Vector3.up*.06f);
            var splitNode=LTRoadJunctionMath.WithBoundaryPoints(node,splits);
            Require(splitNode.outline.Length==node.outline.Length+ports.Count,"module end subdivisions are retained in the central boundary");
            node=splitNode;
            int previous=int.MaxValue;
            foreach(int rings in new[]{8,4,2,1})
            {
                var mesh=LTRoadJunctionMesh.Build(node,rings,4,.06f,Matrix4x4.identity);
                Require(mesh.triangles.Length<previous,"junction LOD collapses interior only");previous=mesh.triangles.Length;
                for(int i=0;i<mesh.triangles.Length;i+=3)
                    Require(Vector3.Cross(mesh.vertices[mesh.triangles[i+1]]-mesh.vertices[mesh.triangles[i]],mesh.vertices[mesh.triangles[i+2]]-mesh.vertices[mesh.triangles[i]]).y>0,"junction triangles face up and do not degenerate");
                int n=node.outline.Length;
                for(int i=0;i<n;i++)
                {var p=mesh.vertices[1+(rings-1)*n+i];Require(Math.Abs(p.x-node.outline[i].x)<1e-5&&Math.Abs(p.z-node.outline[i].y)<1e-5&&Math.Abs(p.y-7.06f)<1e-5,"LOD boundary stays exactly on port edges");}
            }
            layouts++;
        }
        bool rejected=false;
        try{LTRoadJunctionMath.Build(Vector3.zero,new[]{LTRoadJunctionMath.MakePort(Vector3.zero,new Vector3(0,0,30),8,6),LTRoadJunctionMath.MakePort(Vector3.zero,new Vector3(1,0,30),8,6)},3,.5f);}catch(ArgumentException){rejected=true;}
        Require(rejected,"overlapping mouths must not publish geometry");
        // Derived flat entrance remains flat even if the following authored point has height/bank.
        s=LTRoadMath.Settings.Default;s.straightStart=true;s.junctionStartLength=3;s.pattern=LTRoadPattern.Tracks;s.flatten=0;
        var entrance=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(0,2,8)),new LTRoadPoint(new Vector3(0,2,11)),new LTRoadPoint(new Vector3(0,5,20),10),new LTRoadPoint(new Vector3(8,7,40),15)},Matrix4x4.identity,s);
        foreach(var sample in entrance.samples)if(sample.position.z<=11)Require(Math.Abs(sample.position.y-2)<.00001f&&sample.bank==0,"straight entrance must preserve flat port");
        Require(Math.Abs(entrance.ApplyHeight(.9f,8,0)-2)<1e-4,"ruts and reduced flattening must not leave a step at the port");
        Require(entrance.ApplyHeight(0,7,0)==0,"connected height cap cannot lift the central asphalt terrain");
        const string root="Assets/TerrainSystem/LocalTerrain/";
        string runtime=File.ReadAllText(root+"LTPaintRuntime.cs"),editor=File.ReadAllText(root+"Editor/LTRoadJunctionEditor.cs");
        Require(runtime.Contains("junctions[i].Weight(point.x,point.z)")&&runtime.Contains("asphaltInputs.Add(node.Suppression())"),"node paint and displacement suppression are integrated");
        Require(File.ReadAllText(root+"LTDetailRenderer.cs").Contains("stamps.AddRange(junctionRemovals)"),"node clearing follows all add/replace stamps");
        Require(editor.Contains("GUIUtility.hotControl!=0")&&editor.Contains("timeSinceStartup+.45")&&!editor.Contains("SaveAssets(")&&!editor.Contains("SaveScene(")&&!editor.Contains("DeleteAsset("),"debounced baking must preserve scenes and unrelated assets");
        Require(editor.Contains("collider.sharedMesh=meshes[0]")&&editor.Contains("lod.junction=n"),"junction uses LOD0 collider and distance LOD binding");
        Console.WriteLine($"PASS road network/junction: {samples} ownership-aware samples; {seams} guarded frame texels; {layouts} layouts × 4 LODs; shared-chunk boundary, flat entries and integration contracts.");
    }
}
