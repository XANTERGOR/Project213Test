using System;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadDimensionHandleChecks()
    {
        int checks=0;
        void Check(bool ok,string text){Require(ok,text);checks++;}
        void Reject(Action action){bool failed=false;try{action();}catch(ArgumentException){failed=true;}Check(failed,"bad dimension input rejected");}
        var values=new Vector3(6,1,3);
        foreach(float factor in new[]{.65f,1,1.35f})for(int part=0;part<3;part++)
        {
            float radius=LTRoadDimensionMath.Radius(values,part,factor);
            var same=LTRoadDimensionMath.Drag(values,part,radius,factor);
            Check((same-values).sqrMagnitude<1e-10f,"no movement preserves dimensions");
            var next=LTRoadDimensionMath.Drag(values,part,radius+2,factor);
            for(int j=0;j<3;j++)if(j!=part)Check(next[j]==values[j],"only dragged dimension changes");
            Check(Math.Abs(next[part]-values[part]-(part==0?4/factor:2))<.00001f,"width doubles single-sided displacement, shoulder/blend do not");
            Check(LTRoadDimensionMath.Drag(values,part,-100,factor)[part]==(part==0?.01f:0),"crossing centre clamps, never mirrors");
            Check(LTRoadDimensionMath.Drag(values,part,1000000,factor)[part]==10000,"same upper bound as road inspector");
        }
        foreach(float bad in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity})
        {Reject(()=>LTRoadDimensionMath.Drag(values,0,bad,1));Reject(()=>LTRoadDimensionMath.Radius(new Vector3(bad,1,3),0,1));}
        Reject(()=>LTRoadDimensionMath.Radius(values,3,1));Reject(()=>LTRoadDimensionMath.Radius(values,0,0));
        var s=LTRoadMath.Settings.Default;s.mode=LTRoadMode.Asphalt;s.width=values.x;s.shoulderWidth=values.y;s.blendWidth=values.z;s.sampleSpacing=.7f;
        var points=new[]{new LTRoadPoint(new Vector3(2,3,1),-12),new LTRoadPoint(new Vector3(8,9,18),5),new LTRoadPoint(new Vector3(24,7,30),18)};
        var road=LTRoadMath.Build(points,Matrix4x4.identity,s);
        var samples=road.samples.ToArray();int hash=road.geometryHash;
        foreach(float distance in new[]{-5f,0,road.length*.13f,road.length*.5f,road.length,road.length+5})
        {
            var frame=LTRoadDimensionMath.Frame(road,distance);
            Check(frame.distance==Mathf.Clamp(distance,0,road.length),"station clamped to spline");
            Check(Math.Abs(frame.right.magnitude-1)<.00001f&&Math.Abs(frame.right.y)<.00001f,"unit horizontal frame");
            if(distance<=0)Check(frame.position==samples[0].position,"first station");
            if(distance>=road.length)Check((frame.position-samples[samples.Length-1].position).magnitude<.00001f,"last station");
            foreach(int sign in new[]{-1,1})for(int part=0;part<3;part++)
            {
                frame.lateral=sign*road.width*.5f;float factor=road.HalfWidth(frame)/(road.width*.5f);
                float radius=LTRoadDimensionMath.Radius(values,part,factor);
                var p=frame.position+frame.right*(sign*radius);p.y=road.SurfaceHeight(frame,sign*radius);
                var delta=p-frame.position;float measured=(delta.x*frame.right.x+delta.z*frame.right.z)*sign;
                var recovered=LTRoadDimensionMath.Drag(values,part,measured,factor);
                Check((recovered-values).magnitude<.00001f,"both sides recover global settings with slope/bank");
            }
        }
        Check(hash==road.geometryHash&&samples.SequenceEqual(road.samples),"handle math cannot mutate road snapshot");
        var handles=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTRoadDimensionHandles.cs");
        var runtime=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/LTRoad.cs");
        Check(handles.Contains("owner.PreviewDimensions(next.x,next.y,next.z)")&&handles.Contains("ReferenceEquals(source,committed)"),"detached preview and concurrent-edit cancellation");
        Check(runtime.Contains("false,\n                previewWidth,previewShoulder,previewBlend")||runtime.Contains("false,\r\n                previewWidth,previewShoulder,previewBlend"),"preview never replaces live capture cache");
        int commit=handles.IndexOf("void Commit()",StringComparison.Ordinal),cancel=handles.IndexOf("internal void Cancel()",StringComparison.Ordinal);
        Check(handles.IndexOf("ApplyModifiedProperties",StringComparison.Ordinal)>commit&&handles.LastIndexOf("ApplyModifiedProperties",StringComparison.Ordinal)<cancel,"only commit serializes the gesture");
        Check(handles.IndexOf("LTRoadBakeQueue.Queue",StringComparison.Ordinal)>commit&&handles.LastIndexOf("LTRoadBakeQueue.Queue",StringComparison.Ordinal)<cancel,"only release queues rebuild");
        Check(handles.Contains("Undo.CollapseUndoOperations")&&handles.Contains("Undo.undoRedoPerformed-=Cancel")&&handles.Contains("AssemblyReloadEvents.beforeAssemblyReload-=Cancel"),"single undo group and callback release");
        Check(handles.Contains("if(WidthLocked(road))")&&handles.Contains("field==0&&WidthLocked(road)"),"authored socket width protected at display and commit");
        Check(handles.Contains("e.alt||e.shift")&&handles.Contains("view!=dragView")&&handles.Contains("KeyCode.Escape"),"navigation, other Scene view and escape guards");
        Check(handles.Contains("EditorGUIUtility.SetWantsMouseJumping(0)"),"cancelling owned mouse control restores cursor mode");
        Console.WriteLine($"PASS road dimension handles: {checks} assertions; full/half width, shoulder/blend isolation, both sides, slope/bank, bounds, station frames, detached-preview and commit-only write contracts. Native mouse events/Undo not exercised.");
    }
}
