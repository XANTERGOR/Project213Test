using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadShoulderJoinChecks()
    {
        int checks=0;
        void Check(bool ok,string label){Require(ok,label);checks++;}
        void Near(float a,float b,string label,float tolerance=.0001f)=>Check(Math.Abs(a-b)<=tolerance,$"{label}: {a:R} != {b:R}");
        var vertices=new[]{new Vector3(-2,3,-10),new Vector3(2,3,-10),new Vector3(2,3,0),new Vector3(-2,3,0)};
        var shape=new LTRoadFootprint(vertices,new[]{0,2,1,0,3,2},0);
        var port=new LTRoadJunctionMath.Port{centre=new Vector3(0,3,0),right=Vector3.right,width=4};
        var path=new[]{new LTRoadPoint(new Vector3(0,3,0)),new LTRoadPoint(new Vector3(0,3,6)),
            new LTRoadPoint(new Vector3(0,3,16)),new LTRoadPoint(new Vector3(4,5,40),5)};
        foreach(bool end in new[]{false,true})foreach(float nodeShoulder in new[]{0f,.5f,2f})
        foreach(float nodeBlend in new[]{0f,1f,4f})foreach(float roadShoulder in new[]{0f,.5f,2f})
        {
            var node=LTRoadJunctionMath.MeshFoundation(new Vector3(0,3,-5),shape,nodeShoulder,nodeBlend,.5f,1);
            var s=LTRoadMath.Settings.Default;s.mode=LTRoadMode.Asphalt;s.width=4;s.shoulderWidth=roadShoulder;s.blendWidth=2;
            s.straightStart=!end;s.straightEnd=end;s.junctionStartLength=6;s.junctionEndLength=6;
            var original=LTRoadMath.Build(end?path.Reverse().ToArray():path,Matrix4x4.identity,s);
            var join=new LTRoadShoulderJoin(node,port,roadShoulder,2,6,true,LTDetailCategory.Vegetation);
            var road=original.WithShoulders(end?null:join,end?join:null);
            Check(ReferenceEquals(original.samples,road.samples),"same spline samples, no resampling or deformation");
            Check(original.width==road.width&&original.FoundationKey==";"&&road.HasShoulderJoins,"mesh width unchanged; opt-in only");
            Check(ReferenceEquals(original,original.WithShoulders(null,null)),"disabled path returns original snapshot");
            Check(original.geometryHash!=road.geometryHash&&original.paintHash!=road.paintHash&&original.detailHash!=road.detailHash,"all terrain consumers invalidate");
            Check(!original.SameTerrainRegion(road,new Rect(-12,-5,24,20))&&!original.SamePaintRegion(road,new Rect(-12,-5,24,20)),"regional reuse rejects changed joining");
            foreach(float x in new[]{-9f,-4.1f,-3.1f,-2.2f,-1f,0f,1f,2.2f,3.1f,4.1f,9f})
            {
                float Height(float z)=>road.ApplyHeight(x,z,node.ApplyHeight(x,z,-2));
                Near(Height(-.0001f),Height(.0001f),$"height continuous across socket end={end} ns={nodeShoulder} nb={nodeBlend} rs={roadShoulder} x={x}",.002f);
                float Paint(float z){float n=node.Weight(x,z);return n+(1-n)*road.PaintWeight(x,z);}
                Near(Paint(-.0001f),Paint(.0001f),"same-layer paint continuous across socket",.002f);
                float Clear(float z){float n=node.Weight(x,z);return n+(1-n)*road.ClearWeight(x,z,LTDetailCategory.Vegetation);}
                Near(Clear(-.0001f),Clear(.0001f),"grass removal continuous across socket",.002f);
                for(int i=0;i<=120;i++)
                {
                    float z=i*.1f;
                    float h=Height(z),paint=road.PaintWeight(x,z),clear=road.ClearWeight(x,z,LTDetailCategory.Vegetation);
                    Check(float.IsFinite(h)&&h>=-2.0001f&&h<=3.0001f,"no berm overshoot / NaN on flat neck");
                    Check(paint>=0&&paint<=1.0001f&&clear>=0&&clear<=1.0001f,"bounded paint/grass masks");
                    original.TryTextureCoordinates(x,z,out var uv,out var right);road.TryTextureCoordinates(x,z,out var joinedUV,out var joinedRight);
                    Check(uv==joinedUV&&right==joinedRight,"UVs and texture frame unchanged");
                }
                Near(road.ApplyHeight(x,20,-2),original.ApplyHeight(x,20,-2),"far road height unchanged");
                Near(road.PaintWeight(x,20),original.PaintWeight(x,20),"far road paint unchanged");
                Near(road.ClearWeight(x,20,LTDetailCategory.Vegetation),original.ClearWeight(x,20,LTDetailCategory.Vegetation),"far grass unchanged");
            }
            foreach(var sample in road.samples)
            {
                var hit=new LTRoadMath.Hit{position=sample.position,right=sample.right,bank=sample.bank,distance=sample.distance};
                Near(original.SurfaceHeight(hit,-2),road.SurfaceHeight(hit,-2),"left authored deformation frame unchanged");
                Near(original.SurfaceHeight(hit,2),road.SurfaceHeight(hit,2),"right authored deformation frame unchanged");
            }
            var samples=new List<LTRoadMath.Sample>();var nodes=new List<LTRoadHeightKernel.Node>();var numeric=road.CopyHeightData(samples,nodes);
            var sb=new LTHeightJobMath.Buffer<LTRoadMath.Sample>{values=samples.ToArray()};var nb=new LTHeightJobMath.Buffer<LTRoadHeightKernel.Node>{values=nodes.ToArray()};
            for(int i=0;i<80;i++)
            {
                float x=i%9-4,z=i*.5f;
                float actual=LTRoadHeightKernel.ApplyDetailed(numeric,sb,nb,x,z,-2,out var trace);
                if(trace.ambiguous!=0)actual=road.ApplyHeight(x,z,-2);
                Near(actual,road.ApplyHeight(x,z,-2),"worker/reference routing parity");
            }
            LTRoadHeightKernel.ApplyDetailed(numeric,sb,nb,3,1,-2,out var near);
            Check(near.ambiguous!=0,"near socket requests exact footprint reference");
            LTRoadHeightKernel.ApplyDetailed(numeric,sb,nb,0,12.123f,-2,out var far);
            Check(far.ambiguous==0,"outside socket region remains on normal worker path");
            road.TrySample(0,0,out var startHit);road.FoundationWidths(startHit,out float shoulder,out float blend);
            Near(shoulder,nodeShoulder,"gizmo shoulder at mouth");Near(blend,nodeBlend,"gizmo blend at mouth");
        }
        var broad=LTRoadJunctionMath.MeshFoundation(new Vector3(0,3,-5),shape,4,4,.5f,1);
        var baseSettings=LTRoadMath.Settings.Default;baseSettings.mode=LTRoadMode.Asphalt;baseSettings.width=4;baseSettings.shoulderWidth=.2f;baseSettings.blendWidth=.5f;baseSettings.straightStart=true;baseSettings.junctionStartLength=6;
        var baseRoad=LTRoadMath.Build(path,Matrix4x4.identity,baseSettings);
        var a=new LTRoadShoulderJoin(broad,port,.2f,.5f,6,false);
        var widened=baseRoad.WithShoulders(a,null);
        Check(widened.bounds.xMax>baseRoad.bounds.xMax&&widened.Intersects(new Rect(7,.1f,.1f,.1f)),"expanded bounds/density culling include flare");
        Check(widened.Intersects(new Rect(9,25,.1f,.1f))==baseRoad.Intersects(new Rect(9,25,.1f,.1f)),"flare does not widen terrain refinement along entire road");
        Check(widened.PaintWeight(7,.1f)>0&&widened.ClearWeight(7,.1f,LTDetailCategory.Vegetation)>0,"new shoulder included beyond original road bounds");
        Check(widened.DisplacementSuppression(4,.1f,0)>baseRoad.DisplacementSuppression(4,.1f,0),"displacement guard follows joined shoulder");
        Near(widened.DisplacementSuppression(4,20,.25f),baseRoad.DisplacementSuppression(4,20,.25f),"far displacement guard unchanged");
        var gizmo=LTRoadFoundationOutline.Build(widened,true,false);
        Check(gizmo.shoulder.Length>0&&gizmo.transition.Length>0,"coordinated contours available");
        var other=baseRoad.WithShoulders(new LTRoadShoulderJoin(broad,port,.2f,.5f,3,false),null);
        Check(other.FoundationKey!=widened.FoundationKey&&other.detailHash!=widened.detailHash,"length invalidates saved placement identity");
        bool failed=false;try{baseRoad.WithShoulders(new LTRoadShoulderJoin(broad,port,.2f,.5f,100,false),new LTRoadShoulderJoin(broad,port,.2f,.5f,100,false));}catch(ArgumentException){failed=true;}
        Check(failed,"overlapping endpoint handoffs rejected");
        foreach(float bad in new[]{float.NaN,-1f,float.PositiveInfinity})
        {failed=false;try{new LTRoadShoulderJoin(broad,port,bad,1,3,false);}catch(ArgumentException){failed=true;}Check(failed,"invalid dimensions rejected");}
        foreach(float degrees in new[]{-127f,27f,90f})
        {
            double angle=degrees*Math.PI/180;float c=(float)Math.Cos(angle),s=(float)Math.Sin(angle);
            Vector3 Turn(Vector3 p)=>new Vector3(p.x*c+p.z*s,p.y,-p.x*s+p.z*c);
            Vector3 Place(Vector3 p)=>Turn(p)+new Vector3(902,8,137);
            var movedShape=new LTRoadFootprint(vertices.Select(Place).ToArray(),new[]{0,2,1,0,3,2},0);
            var movedNode=LTRoadJunctionMath.MeshFoundation(Place(broad.centre),movedShape,4,4,.5f,1);
            var movedPort=new LTRoadJunctionMath.Port{centre=Place(port.centre),right=Turn(port.right),width=4};
            var movedJoin=new LTRoadShoulderJoin(movedNode,movedPort,.2f,.5f,6,false);
            var movedRoad=LTRoadMath.Build(path.Select(p=>new LTRoadPoint(Place(p.position),p.bank)).ToArray(),Matrix4x4.identity,baseSettings).WithShoulders(movedJoin,null);
            foreach(float x in new[]{-7f,-3f,0f,3f,7f})foreach(float z in new[]{.1f,1f,3f,6f,12f})
            {
                var p=Place(new Vector3(x,0,z));
                float expected=widened.ApplyHeight(x,z,broad.ApplyHeight(x,z,-4));
                float actual=movedRoad.ApplyHeight(p.x,p.z,movedNode.ApplyHeight(p.x,p.z,4))-8;
                Near(actual,expected,"translated/yawed join height",.005f);
                Near(movedRoad.PaintWeight(p.x,p.z),widened.PaintWeight(x,z),"translated/yawed join mask",.001f);
            }
        }
        for(int i=0;i<1000;i++)widened.PaintWeight(3,i%8);
        long before=GC.GetAllocatedBytesForCurrentThread();float sum=0;
        for(int i=0;i<10000;i++)sum+=widened.PaintWeight(3,i%8);
        Check(sum>0&&GC.GetAllocatedBytesForCurrentThread()-before<256,"hot queries allocate no memory");
        var prep=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTPaintWeightPreparation.cs");
        Check(prep.Contains("road.HasShoulderJoins")&&prep.Contains("if(PrecomputedRoad(stamp.Road))maskCount++"),"worker mask path reserves joined-road pixels");
        Check(File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTDetailPlacementRuntime.cs").Contains("h.Add(road.FoundationKey)"),"persistent placement includes full non-session join identity");
        Console.WriteLine($"PASS road shoulder joining: {checks} assertions; both endpoints, varying bands/zero widths, seam continuity, worker/reference routing, masks, bounds, immutable asphalt/UV, saved placement identity and allocation-free queries. No native Unity or live scene execution.");
    }
}
