using System;
using System.Collections.Generic;
using System.IO;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void RoadIntegrationChecks()
    {
        var settings=LTRoadMath.Settings.Default;settings.width=2;settings.shoulderWidth=.5f;
        settings.blendWidth=1;settings.terrainCellSize=.25f;settings.edgeNoise=0;
        var road=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(8,2,2)),new LTRoadPoint(new Vector3(8,2,14))},Matrix4x4.identity,settings);
        var area=new Rect(0,0,16,16);
        var zones=new List<LTStampMesh.Zone>{new LTStampMesh.Zone{bounds=road.bounds,cellSize=road.terrainCellSize,
            customCellSize=(x,z)=>road.terrainCellSize,coverageIntersects=road.Intersects}};
        var raw=LTStampMesh.Plan(area,4,true,.1f,100000,zones,(x,z)=>road.ApplyHeight(x,z,0));
        var forest=new LTBalancedForest(1,1,100000,new Dictionary<int,List<Vector3Int>>{{0,raw}});forest.Balance();
        LTStampMesh.Emit(area,new Vector2(16,16),100000,forest,0,forest.Plan(0),(x,z)=>road.ApplyHeight(x,z,0),null,
            out var vertices,out var normals,out var uv,out var indices,r=>true);
        bool centre=false,outside=false;
        foreach(var vertex in vertices)
        {
            Require(Math.Abs(vertex.y-road.ApplyHeight(vertex.x,vertex.z,0))<1e-5,"road height must be part of emitted terrain");
            centre|=Math.Abs(vertex.x-8)<.01&&vertex.z>3&&vertex.z<13&&Math.Abs(vertex.y-2)<.001;
            outside|=vertex.x<1&&Math.Abs(vertex.y)<.001;
        }
        Require(centre&&outside,"road must flatten only its influence area");
        for(int i=0;i<indices.Length;i+=3)
            Require(Vector3.Cross(vertices[indices[i+1]]-vertices[indices[i]],vertices[indices[i+2]]-vertices[indices[i]]).y>0,"road terrain triangles must face upward");

        const string root="Assets/TerrainSystem/LocalTerrain/";
        var runtime=File.ReadAllText(root+"LTPaintRuntime.cs");
        foreach(var hook in new[]{"BakeRoadProjection(world,rect,stamps,layers,state,asphaltInputs)","ReleaseRoadProjection(state);",
            "BindRoadProjection(material,state);","TryRoadProjectionUV(state,i,px,pz","roads[i].PaintWeights(point.x,point.z",
            "asphaltInputs","RoadDisplacementMultiplier(state,px,pz)"})Require(runtime.Contains(hook),"road painter integration missing "+hook);
        var details=File.ReadAllText(root+"LTDetailRenderer.cs");
        Require(details.Contains("road.ClearWeight(px,pz,entry.category)"),"road clearing must precede placement acceptance");
        Require(details.IndexOf("road.detailHash")<details.IndexOf("int placementHash=hash;"),"road changes must invalidate placement, not just scale");
        foreach(var shader in new[]{"LTEightLayers.shader","LTEightLayersTessellation.shader","LTGlobalLayerBake.shader"})
        {
            var source=File.ReadAllText(root+"Resources/"+shader);
            foreach(var property in new[]{"_LTRoadProjectionMap","_LTRoadProjectionEnabled","_LTRoadProjectionSlots0","_LTRoadProjectionSlots1","_LTRoadProjectionSlots2","_LTRoadSuppressionMap"})
                Require(source.Contains(property+"(\""),"ShaderLab road binding missing "+shader+" "+property);
        }
        var editor=File.ReadAllText(root+"Editor/LTRoadEditor.cs");
        var mapping=File.ReadAllText(root+"Shaders/LTRoadProjection.hlsl");
        var roadProjection=File.ReadAllText(root+"LTPaintRoadProjection.cs");
        Require(!roadProjection.Contains("GetComponentsInChildren<LTRoad>")&&roadProjection.Contains("foreach (var snapshot in asphaltInputs)"),
            "road projection reuses the tick's validated asphalt snapshots rather than recapturing per chunk");
        var terrainEditor=File.ReadAllText(root+"Editor/LTEditor.cs");
        foreach(var hook in new[]{"Mark(w,s,a.bounds,a.road,b?.road)","Mark(w,s,b.bounds,b.road,a?.road)",
            "MarkDensity(w,state,a.bounds,a.road,b?.road)","MarkDensity(w,state,b.bounds,b.road,a?.road)",
            "road.Intersects(Expanded(chunk,hx,hz))","id=stamp.id,road=road"})
            Require(terrainEditor.Contains(hook),"old/new road dirty-region/normal halo integration missing "+hook);
        Require(roadProjection.Contains("road.TryTextureCoordinates(x, z, out uv, out right)"),"road texture bake uses the endpoint-extending UV query");
        var projectionMath=File.ReadAllText(root+"LTRoadProjectionMath.cs");
        Require(roadProjection.Contains("LTRoadProjectionMath.Bake(sources[slot],rect,RoadProjectionSize)")&&
            projectionMath.Contains("TextureBakeRegion(rect,size)")&&projectionMath.Contains("TextureBakeBlockIntersects(rect,size,block)")&&
            projectionMath.Contains("z=block.yMin;z<block.yMax;z++")&&projectionMath.Contains("x=block.xMin;x<block.xMax;x++"),"UV baker evaluates only intersecting guarded blocks in the road region");
        Require(mapping.Contains("slots[slot&3]")&&mapping.Contains("_LTRoadProjectionSlots2"),"all twelve slot indices must stay inside float4 bounds");
        Require(mapping.Contains("256/_LTRect.z")&&mapping.Contains("256/_LTRect.w"),"road gradients must account for non-square chunks");
        Require(editor.Contains("Assets/LocalTerrainRoads")&&!editor.Contains("DeleteAsset("),"road mesh assets must be separate and recoverable");
        Require(editor.Contains("CreateHeightSamplerForRoad(world)"),"road projection must capture terrain once per action");
        Require(editor.Contains("LTEditorEngine.Refresh(road.World, false, false)")&&editor.Contains("?.RequestSurfaceRefresh()"),
            "explicit road bake refreshes terrain then details without saving unrelated dirty assets");
        Require(details.Contains("public void RequestSurfaceRefresh(){requested=true;blocked=false;}"),
            "road refresh must not clear prefab recipes or bump the global placement revision");
        Require(editor.Contains("go.transform.localPosition = path[1]") && editor.Contains("vector3Value = path[i] - path[1]"),
            "road origin must be on its path and authored points must be relative to that origin");
        foreach(var hook in new[]{"Показать сплайн", "Перенести путь к объекту Road", "OnGetFrameBounds()", "view.Frame(PathBounds",
            "HandleUtility.AddDefaultControl(control)", "hit.collider.GetComponent<LTChunk>()", "hit.collider.GetComponentInParent<LTWorld>() != Road.World",
            "finally { Handles.zTest = previousDepth; }", "Selection.selectionChanged -= OnSelectionChanged"})
            Require(editor.Contains(hook), "road authoring safety/navigation contract missing: "+hook);
        // A non-zero origin must not move the world-space curve (the old confusing pivot layout).
        var authored = new[]{new LTRoadPoint(new Vector3(999,3,281)),new LTRoadPoint(new Vector3(999,4,291)),new LTRoadPoint(new Vector3(999,5,301))};
        var origin=authored[1].position;
        var local=new LTRoadPoint[authored.Length];
        for(int i=0;i<local.Length;i++)local[i]=new LTRoadPoint(authored[i].position-origin);
        var shifted=Matrix4x4.identity;shifted.m03=origin.x;shifted.m13=origin.y;shifted.m23=origin.z;
        var originalRoad=LTRoadMath.Build(authored,Matrix4x4.identity,settings);
        var localRoad=LTRoadMath.Build(local,shifted,settings);
        Require(originalRoad.samples.Count==localRoad.samples.Count,"rebasing origin must retain sample count");
        for(int i=0;i<localRoad.samples.Count;i++)
            Require((localRoad.samples[i].position-originalRoad.samples[i].position).sqrMagnitude<1e-6f,"rebasing must not translate curve twice");
        Console.WriteLine("PASS road integration: production terrain planner/emitter, scoped refinement, height/winding, paint/detail/suppression/lifetime/shader binding source contracts. Native GPU smoke test is separate.");
    }
}
