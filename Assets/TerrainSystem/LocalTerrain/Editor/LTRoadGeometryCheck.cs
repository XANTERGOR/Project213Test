using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTRoadGeometryCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Worn Asphalt (no scene changes)
        public static void RunWorn()
        {
            Run();
            LTRoadTerrainAsphaltCheck.Run();
        }
        // Archived validation menu: Tools/Local Terrain/Validate Geometric Asphalt (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var before=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);before.Add((s,s.isDirty,s.rootCount));}
            var scene=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();
            T Own<T>(T value)where T:Object{value.hideFlags=HideFlags.HideAndDontSave;owned.Add(value);return value;}
            GameObject Child(string name,Transform parent=null)
            {
                var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,scene);
                if(parent)go.transform.SetParent(parent,false);return go;
            }
            try
            {
                string boundaries=LTRoadGeometryBoundaryCheck.Run(LTEditorEngine.ValidateRoadGeometryBoundaries,resolutions:new[]{8,32,64,128});
                string inset=LTRoadInsetCheck.Run(LTEditorEngine.ValidateRoadGeometryBoundaries);
                string unreduced=LTRoadInsetCheck.Run(LTEditorEngine.ValidateRoadGeometryBoundaries,0);
                string bevelBoundaries=LTRoadGeometryBoundaryCheck.Run(LTEditorEngine.ValidateRoadGeometryBoundaries,resolutions:new[]{8,32,64,128},beveled:true);
                string bevelInset=LTRoadInsetCheck.Run(LTEditorEngine.ValidateRoadGeometryBoundaries,damaged:true,beveled:true);
                string connectedInset=LTRoadInsetCheck.Run(LTEditorEngine.ValidateRoadGeometryBoundaries,damaged:true,connected:true);
                string wornPreset=LTRoadInsetCheck.Run(LTEditorEngine.ValidateRoadGeometryBoundaries,damaged:true,connected:true,wornPreset:true);
                string connectedBoundaries=LTRoadGeometryBoundaryCheck.Run(LTEditorEngine.ValidateRoadGeometryBoundaries,resolutions:new[]{8,32,64,128},connected:true);
                var root=Child("Geometric asphalt validation");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;
                world.baseLayer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());
                var road=Child("Owned road",root.transform).AddComponent<LTRoad>();road.enabled=false;
                road.mode=LTRoadMode.GeometricAsphalt;road.groundLayer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());
                road.width=8;road.terrainCellSize=2;road.asphaltDamage.potholes=1;road.asphaltDamage.brokenEdges=.5f;
                road.asphaltDamage.size=3;road.asphaltDamage.depth=.4f;
                road.pitBevelWidth=.12f;road.pitBevelDepth=.05f;road.pitShapeVariation=.7f;
                road.points=new List<LTRoadPoint>{new LTRoadPoint(new Vector3(8,2,0)),new LTRoadPoint(new Vector3(8,2,32))};
                var capture=road.Capture(world);Require(capture.geometricPits.Count>2,"No generated pits.");
                Require(capture.projection==LTRoadProjection.Spline&&!LTLODMesh.RequiresFixedRoadSurface(capture.mode),"Projection/LOD routing.");
                var rect=new Rect(0,0,16,32);float H(float x,float z)=>capture.ApplyHeight(x,z,2);
                var fine=LTStampMesh.Plan(rect,32,false,.01f,300000,new List<LTStampMesh.Zone>(),H,new LTLODMesh.BuildProgress(false));
                var forest=new LTBalancedForest(1,1,300000,new Dictionary<int,List<Vector3Int>>{{0,fine}});forest.Balance();fine=forest.Plan(0);
                var levels=new List<Vector3Int>[5];levels[0]=fine;var preparation=new LTLODMesh.Preparation(fine,rect,H);
                for(int i=1;i<5;i++)levels[i]=preparation.Coarsen(i,100,null,null,2,false);
                var output=LTSpatialLODMath.BuildLayout(levels,2,300000,p=>0);
                LTStampMesh.EmitSpatialVariants(output,rect,new Vector2(16,32),300000,H,null,progress:new LTLODMesh.BuildProgress(false),insetRoads:new LTRoadInsetMesh.Field(new[]{capture},rect));
                var colliderMesh=Own(new Mesh());colliderMesh.indexFormat=UnityEngine.Rendering.IndexFormat.UInt32;
                colliderMesh.vertices=output.vertices;colliderMesh.uv=output.uv;colliderMesh.triangles=output.baseIndices;colliderMesh.RecalculateNormals();colliderMesh.RecalculateBounds();
                var colliderObject=Child("Owned collider",root.transform);var collider=colliderObject.AddComponent<MeshCollider>();collider.sharedMesh=colliderMesh;
                root.SetActive(true);colliderObject.SetActive(true);
                int floorProbes=0,wallProbes=0,bevelProbes=0;
                for(int i=0;i<capture.geometricPits.Count;i++)
                {
                    var pit=capture.geometricPits[i];var centre=pit.bounds.center;
                    if(!rect.Contains(centre))continue;
                    float floor=H(centre.x,centre.y)-pit.depth;
                    Require(collider.Raycast(new Ray(new Vector3(centre.x,5,centre.y),Vector3.down),out var hit,10)&&Math.Abs(hit.point.y-floor)<.001f,"Collider has a roof / missing pit floor.");floorProbes++;
                    Require(capture.PaintWeight(centre.x,centre.y)==0,"Floor paint is not exposed.");
                    var middle=(pit[0]+pit[1])*.5f;var direction=new Vector3(middle.x-centre.x,0,middle.y-centre.y);
                    Require(collider.Raycast(new Ray(new Vector3(centre.x,floor+pit.depth*.5f,centre.y),direction.normalized),out hit,direction.magnitude+.05f),"Missing native vertical wall.");wallProbes++;
                    var bevelPoint=new Vector2((float)((pit[0].x+pit[1].x+pit.InnerX(0)+pit.InnerX(1))*.25),
                        (float)((pit[0].y+pit[1].y+pit.InnerZ(0)+pit.InnerZ(1))*.25));
                    float bevelY=H(bevelPoint.x,bevelPoint.y)-pit.bevelDepth*.5f;
                    Require(pit.Beveled&&collider.Raycast(new Ray(new Vector3(bevelPoint.x,5,bevelPoint.y),Vector3.down),out hit,10)
                        &&Math.Abs(hit.point.y-bevelY)<.001f&&hit.normal.y>0&&hit.normal.y<.9999f,"Missing or incorrect native bevel surface.");bevelProbes++;
                    foreach(var q in new[]{pit[0],pit[1],centre})Require(capture.DisplacementSuppression(q.x,q.y,.2f)>.999f,"Displacement may tear the rim.");
                }
                var topology=new LTSpatialLODTopology(1,1,2,new[]{new LTSpatialLODTopology.Chunk{cells=output.cells,patches=output.patches}});
                topology.Update();var indices=new List<int>();topology.WriteIndices(0,indices);int near=indices.Count/3;
                for(int p=0;p<4;p++)topology.SetLevel(0,p,4);topology.Update();topology.Validate();topology.WriteIndices(0,indices);int far=indices.Count/3;
                Require(far<near/3,"Far terrain/pit geometry did not simplify.");
                Require(collider.sharedMesh==colliderMesh&&colliderMesh.triangles.SequenceEqual(output.baseIndices),"Visual LOD changed the collider.");
                road.connectedDamage=true;road.connectedFloorAsphalt=.7f;road.repairPatchStrength=.75f;road.asphaltDamage.depth=.06f;
                var connected=road.Capture(world);
                var connectedOutput=LTSpatialLODMath.BuildLayout(levels,2,300000,p=>0);
                LTStampMesh.EmitSpatialVariants(connectedOutput,rect,new Vector2(16,32),300000,H,null,progress:new LTLODMesh.BuildProgress(false),insetRoads:new LTRoadInsetMesh.Field(new[]{connected},rect));
                collider.sharedMesh=null;colliderMesh.Clear();colliderMesh.vertices=connectedOutput.vertices;colliderMesh.uv=connectedOutput.uv;
                colliderMesh.triangles=connectedOutput.baseIndices;colliderMesh.RecalculateNormals();colliderMesh.RecalculateBounds();collider.sharedMesh=colliderMesh;
                int connectedFloors=0,connectedWalls=0,notches=0;
                for(int i=0;i<connected.geometricPits.Count;i++)
                {
                    var pit=connected.geometricPits[i];var centre=pit.bounds.center;
                    Require(pit.Concave&&!pit.Beveled,"Connected fixture must have concave joined zones.");
                    if(!rect.Contains(centre))continue;
                    Require(collider.Raycast(new Ray(new Vector3(centre.x,5,centre.y),Vector3.down),out var hit,10)&&
                        Math.Abs(hit.point.y-(2-pit.depth))<.001f,"Connected floor has a roof or gap.");connectedFloors++;
                    if(pit.asphaltCoverage>0)Require(connected.PaintWeight(centre.x,centre.y)>0,"Retained asphalt floor lost paint.");
                    var middle=(pit[0]+pit[1])*.5f;var direction=new Vector3(middle.x-centre.x,0,middle.y-centre.y);
                    Require(collider.Raycast(new Ray(new Vector3(centre.x,2-pit.depth*.5f,centre.y),direction.normalized),out hit,direction.magnitude+.1f)&&
                        Math.Abs(hit.normal.y)<.01f,"Missing connected native wall.");connectedWalls++;
                    // A point outside a re-entrant neck but inside its convex
                    // hull must remain TOP asphalt, not become another pit.
                    var bay=(pit[1]+pit[2]+pit[3])/3;
                    if(!rect.Contains(bay)||connected.geometricPits.Contains(bay.x,bay.y))continue;
                    Require(collider.Raycast(new Ray(new Vector3(bay.x,5,bay.y),Vector3.down),out hit,10)&&Math.Abs(hit.point.y-2)<.001f,"Concave bay was swallowed by convex hull.");notches++;
                }
                var connectedTopology=new LTSpatialLODTopology(1,1,2,new[]{new LTSpatialLODTopology.Chunk{cells=connectedOutput.cells,patches=connectedOutput.patches}});
                connectedTopology.Update();connectedTopology.WriteIndices(0,indices);int connectedNear=indices.Count/3;
                for(int p=0;p<4;p++)connectedTopology.SetLevel(0,p,4);connectedTopology.Update();connectedTopology.Validate();connectedTopology.WriteIndices(0,indices);
                int connectedFar=indices.Count/3;Require(connectedFar<connectedNear/2,"Connected damage failed to simplify.");
                Require(colliderMesh.triangles.SequenceEqual(connectedOutput.baseIndices),"Connected collider changed with visual LOD.");
                Require(connectedFloors>2&&connectedWalls>2&&notches>2,"Insufficient connected native probes.");
                road.asphaltDamage.strength=0;Require(road.Capture(world).geometricPits.Count==0,"Disabling damage retained pits.");
                Require(floorProbes>2&&wallProbes>2&&bevelProbes>2,"Too few native physics probes.");
                Debug.Log($"[Geometric Asphalt v7 / Connected] PASS: legacy {floorProbes} floors, {wallProbes} walls, {bevelProbes} bevels; LOD {near} -> {far}. Connected: {connectedFloors} floors, {connectedWalls} walls, {notches} intact concave bays; LOD {connectedNear} -> {connectedFar}; both colliders remain LOD0. Connected inset: {connectedInset}. Worn preset: {wornPreset}. Connected boundaries: {connectedBoundaries}. Inset checks: {inset}. Reduction disabled: {unreduced}. Bevel inset: {bevelInset}. Bevel boundaries: {bevelBoundaries}. Boundary/cache/strict-loop checks: {boundaries} User scenes/assets not saved. Visual materials, native paint jobs and live performance still require scene testing.");
            }
            catch(Exception error){Debug.LogError("[Geometric Asphalt v7 / Connected] FAIL: "+error);}
            finally
            {
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(scene);
                foreach(var state in before)if(!state.scene.IsValid()||state.scene.isDirty!=state.dirty||state.scene.rootCount!=state.roots)
                    Debug.LogError("[Geometric Asphalt v7 / Connected] User scene state changed: "+state.scene.name);
            }
        }
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }

    public static partial class LTEditorEngine
    {
        // Exercise the real production cache builder and strict contour walker,
        // not just the managed regression's reference edge counter. Owned arrays
        // only: no cache belonging to the user's world is read or changed.
        internal static void ValidateRoadGeometryBoundaries(Vector3[][] vertices,int[][] indices,Vector3[] offsets,int expectedLoops)
        {
            var cache=new BoundaryCache();
            for(int c=0;c<vertices.Length;c++)
            {
                var matrix=Matrix4x4.Translate(offsets[c]);
                cache.Replace(c,BuildChunkBoundary(vertices[c],Array.Empty<Vector3>(),indices[c],matrix,Matrix4x4.identity));
            }
            void Check()
            {
                var positions=new Dictionary<Vector3Int,Vector3>();
                foreach(var data in cache.Values)foreach(var pair in data.pointIndices)
                {if(!positions.ContainsKey(pair.Key))positions.Add(pair.Key,data.points[pair.Value[0]]);}
                var open=cache.edgeTotals.Where(e=>e.Value==1).Select(e=>new LTRoadGeometryBoundary.Edge(e.Key.a,e.Key.b)).ToArray();
                var edges=LTRoadGeometryBoundary.Resolve(open,k=>positions[k],out _);
                var segments=new List<RockClipVertex>();
                foreach(var e in edges)
                {segments.Add(new RockClipVertex{position=positions[e.a],normal=Vector3.up});segments.Add(new RockClipVertex{position=positions[e.b],normal=Vector3.up});}
                int loops=OrderedBoundaryLoops(segments,"geometric asphalt regression").Count;
                if(loops!=expectedLoops)throw new InvalidOperationException($"Expected {expectedLoops} world/rock boundary loops, got {loops}.");
            }
            Check();
            // Replacing one damaged chunk must not leave stale uses from it or
            // lose the unchanged neighbour's contribution to a shared edge.
            cache.Replace(0,BuildChunkBoundary(vertices[0],Array.Empty<Vector3>(),indices[0],Matrix4x4.Translate(offsets[0]),Matrix4x4.identity));
            Check();
        }
    }
}
