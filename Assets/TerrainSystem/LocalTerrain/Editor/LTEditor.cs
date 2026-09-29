using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace LocalTerrainPrototype
{
    // Prototype: planar XZ heightfield, editor-only evaluation, balanced adaptive mesh output.
    // Any roll/pitch, negative scale or rotated world is intentionally rejected.
    [InitializeOnLoad]
    public static class LTEditorEngine
    {
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable)]
        static void DrawMeshStampIcon(LTMeshStamp stamp,GizmoType type)
        {
            if(stamp.enabled)DrawStampIcon(stamp.transform.position,StampIcon.Mesh);
        }
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable)]
        static void DrawHeightStampIcon(LTHeightStamp stamp,GizmoType type)
        {
            if(stamp.enabled)DrawStampIcon(stamp.transform.position,StampIcon.Height);
        }
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable)]
        static void DrawPaintStampIcon(LTPaintStamp stamp,GizmoType type)
        {
            if(stamp.enabled)DrawStampIcon(stamp.transform.position,StampIcon.Paint);
        }
        enum StampIcon { Mesh, Height, Paint }
        static void DrawStampIcon(Vector3 position,StampIcon icon)
        {
            var view=SceneView.currentDrawingSceneView;
            if(!view||!view.camera)return;
            var camera=view.camera.transform;
            float size=HandleUtility.GetHandleSize(position)*.14f;
            var oldMatrix=Gizmos.matrix;var oldColor=Gizmos.color;
            Gizmos.matrix=Matrix4x4.identity;
            Gizmos.color=icon==StampIcon.Mesh?new Color(1,.55f,.12f):
                icon==StampIcon.Paint?new Color(.9f,.3f,1):new Color(.3f,1,.5f);
            Vector3 P(float x,float y)=>position+size*(camera.right*x+camera.up*y);
            void Line(float ax,float ay,float bx,float by)=>Gizmos.DrawLine(P(ax,ay),P(bx,by));
            try
            {
                // Camera-facing vector pictograms, independent of stamp scale.
                if(icon==StampIcon.Paint)
                {
                    // Three stacked sheets: a distinct silhouette for painting layers.
                    Line(0,1,.9f,.55f);Line(.9f,.55f,0,.1f);
                    Line(0,.1f,-.9f,.55f);Line(-.9f,.55f,0,1);
                    Line(-.9f,.05f,0,-.4f);Line(0,-.4f,.9f,.05f);
                    Line(-.9f,-.45f,0,-.9f);Line(0,-.9f,.9f,-.45f);
                }
                else if(icon==StampIcon.Mesh)
                {
                    Line(0,1,.85f,.5f);Line(.85f,.5f,.85f,-.5f);
                    Line(.85f,-.5f,0,-1);Line(0,-1,-.85f,-.5f);
                    Line(-.85f,-.5f,-.85f,.5f);Line(-.85f,.5f,0,1);
                    Line(-.85f,.5f,0,0);Line(.85f,.5f,0,0);Line(0,0,0,-1);
                }
                else
                {
                    Line(-1,-.7f,-.35f,.4f);Line(-.35f,.4f,.15f,-.3f);
                    Line(.15f,-.3f,.5f,.15f);Line(.5f,.15f,1,-.7f);Line(1,-.7f,-1,-.7f);
                    Line(.55f,.5f,.55f,1.2f);Line(.55f,1.2f,.3f,.95f);Line(.55f,1.2f,.8f,.95f);
                }
            }
            finally{Gizmos.matrix=oldMatrix;Gizmos.color=oldColor;}
        }
        sealed class State
        {
            public List<Stamp> previous = new List<Stamp>();
            public List<Density> densities = new List<Density>();
            public HashSet<int> dirty = new HashSet<int>();
            public string config;
            public bool initialized;
            public long revision;
            public readonly LTStagedBuild build=new LTStagedBuild();
            public readonly LTChunkLODQueue chunkLODs=new LTChunkLODQueue();
            public string lodJobStatus,lastLODJobBackend;
            public string heightJobStatus,lastHeightJobBackend;
            public int heightJobFallbacks;
            public readonly LTHeightSampling.Cache heightSamples=new LTHeightSampling.Cache();
            public LTHeightSampling.Source heightSource;
            public LTSource heightSourceOwner;
            public float[] heightSourceInput;
            public int heightSourceVersion;
            public string heightSourceAsset;
            public Func<bool> buildObjectsValid;
            public double lastChange, lastPreview,lastPaint=double.NegativeInfinity;
            public bool cycleActive;
            public int cycleMeshPasses,cyclePaintPasses;
            public double cycleMeshMs,cyclePaintMs,cycleColliderMs;
            public HashSet<int> colliders = new HashSet<int>();
            public BoundaryCache boundaryCache=new BoundaryCache();
            public LTBalancedForest.InternalPlanCache balanceCache=new LTBalancedForest.InternalPlanCache();
            public Dictionary<int,SeamChunkCache> seamCache=new Dictionary<int,SeamChunkCache>();
            public Dictionary<int,RockOutputCache> rockOutputs=new Dictionary<int,RockOutputCache>();
        }
        sealed class Density
        {
            public LTRoadMath.Snapshot road;
            public int id;public string signature;public Rect bounds;public float cellSize;public List<LTStampMesh.Zone> zones;
        }
        sealed class Stamp
        {
            public LTRoadJunctionMath.Snapshot junction;
            public LTRoadMath.Snapshot road;
            public bool fixedRoadSurface;
            public int id;
            public string signature;
            public Rect bounds;
            public Rect meshFootprintBounds;
            public Matrix4x4 inverse;
            public LTStampShape shape;
            public LTStampOperation operation;
            public Vector2 size;
            public float strength, edge, height, blend, reference;
            public bool maskIsHeight,affectHeight,densityZone;public float densityCellSizeMin,densityCellSizeMax;
            public Texture2D mask;
            public bool meshStamp,meshCutTerrain,meshTrimRock;
            public LTMeshConformMode meshMode;
            public LTMeshStamp meshComponent;
            public Mesh meshSource;
            public Vector3[] meshVertices;
            public int[] meshTriangles;
            public int[] meshAllTriangles;
            public int[] meshBoundaryEdges;
            public List<Vector3> contactSegments;
            public List<int>[] cutRows;
            public float cutRowMin,cutRowStep;
            public float meshCutOffset,meshRetopoHeight,meshWeldDistance;
            public float meshBlendDistance,meshGridDensityMultiplier;
        }
        struct RockClipVertex
        {
            public Vector3 position,normal;
            public Vector4 tangent;
            public Vector2 uv;
            public float distance;
        }
        static readonly Dictionary<int, State> states = new Dictionary<int, State>();
        static readonly Dictionary<string,string> assetInputHashes=new Dictionary<string,string>();
        static string AssetInputHash(UnityEngine.Object asset)
        {
            string path=asset?AssetDatabase.GetAssetPath(asset):"";
            if(string.IsNullOrEmpty(path))return "";
            if(!assetInputHashes.TryGetValue(path,out string hash))assetInputHashes[path]=hash=AssetDatabase.GetAssetDependencyHash(path).ToString();
            return hash;
        }
        static double nextPoll;
        static LTEditorEngine()
        {
            LTWorld.EditorOwnsPainting=OwnsPainting;
            EditorApplication.update += Tick;
            // Upgrade already-generated terrain after recompilation, without
            // rebuilding positions, normals, colliders, or any rock meshes.
            EditorApplication.delayCall += RepairLoadedTerrainTangents;
            // Snapshot diff detects undo as well as ordinary changes, including deleted/reordered stamps.
            Undo.undoRedoPerformed += () => {
                foreach(var state in states.Values){CancelBuilds(state);state.revision++;state.boundaryCache.Clear();}
                nextPoll = 0; SceneView.RepaintAll();
            };
            AssemblyReloadEvents.beforeAssemblyReload += StopLODJobs;
            EditorApplication.quitting += StopLODJobs;
            EditorApplication.projectChanged += () => {
                // Our own generated-LOD imports also raise this event. Compare
                // actual input assets on the next Detect; do not cancel every job.
                assetInputHashes.Clear();
                foreach(var state in states.Values)
                {
                    state.boundaryCache.Clear();
                }
            };
            EditorApplication.playModeStateChanged += s => {
                if (s == PlayModeStateChange.ExitingEditMode) FlushAll();
                if (s == PlayModeStateChange.EnteredEditMode){CancelAllBuilds();states.Clear();}
            };
            EditorSceneManager.sceneSaving += (scene, path) => FlushAll();
        }
        static void CancelAllBuilds()
        {
            foreach(var state in states.Values)CancelBuilds(state);
        }
        static void StopLODJobs(){CancelAllBuilds();LTLODJobWork.Drain();LTHeightJobWork.Drain();}
        static void CancelBuilds(State state)
        {state.build.Cancel(state.dirty);state.chunkLODs.CancelAll(state.dirty);}
        static void DirtyChunk(State state,int id)
        {state.dirty.Add(id);state.heightSamples.Invalidate(id);state.chunkLODs.Touch(id);}
        static LTWorld[] Worlds() => UnityEngine.Object.FindObjectsByType<LTWorld>(FindObjectsSortMode.None)
            .Where(w => w.gameObject.scene.IsValid() && w.gameObject.scene.isLoaded && !EditorUtility.IsPersistent(w)
                && !PrefabUtility.IsPartOfPrefabAsset(w) && UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(w.gameObject) == null).ToArray();
        static bool ValidTransform(LTWorld w)
        {
            return Quaternion.Angle(w.transform.rotation, Quaternion.identity) < 0.01f
                && (w.transform.lossyScale - Vector3.one).sqrMagnitude < 0.000001f;
        }
        static bool OwnsPainting(LTWorld w)=>w&&w.isActiveAndEnabled&&w.autoUpdate&&w.source&&w.generatedRoot&&ValidTransform(w)
            &&w.gameObject.scene.IsValid()&&w.gameObject.scene.isLoaded&&!EditorUtility.IsPersistent(w)
            &&!PrefabUtility.IsPartOfPrefabAsset(w)&&UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(w.gameObject)==null;
        [MenuItem("Tools/Local Terrain/Repair Terrain Tangents")]
        static void RepairLoadedTerrainTangents()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var visited=new HashSet<Mesh>();int repaired=0;
            int undoGroup=-1;
            foreach(var world in Worlds())
            {
                if(!world.generatedRoot || !ValidTransform(world))continue;
                foreach(var chunk in world.generatedRoot.GetComponentsInChildren<LTChunk>(true))
                {
                    if(chunk.GetComponentInParent<LTWorld>()!=world)continue;
                    foreach(var mesh in new[]{chunk.mesh}.Concat(chunk.lodMeshes??Array.Empty<Mesh>()))
                    {
                        if(!mesh || !visited.Add(mesh) || !mesh.isReadable)continue;
                        var normals=mesh.normals;
                        if(normals.Length!=mesh.vertexCount || normals.Length==0)continue;
                        var expected=LTStampMesh.TerrainTangents(normals);var actual=mesh.tangents;
                        bool changed=actual.Length!=expected.Length;
                        for(int i=0;!changed && i<expected.Length;i++)
                            changed=!actual[i].Equals(expected[i]);
                        if(!changed)continue;
                        if(undoGroup<0){Undo.IncrementCurrentGroup();undoGroup=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Repair terrain XZ tangents");}
                        Undo.RegisterCompleteObjectUndo(mesh,"Repair terrain XZ tangents");
                        mesh.tangents=expected;EditorUtility.SetDirty(mesh);repaired++;
                    }
                }
            }
            if(repaired==0)return;
            Undo.CollapseUndoOperations(undoGroup);
            foreach(var state in states.Values)state.boundaryCache.Clear();
            SceneView.RepaintAll();
            Debug.Log($"Local Terrain: repaired XZ tangents on {repaired} terrain/LOD meshes. Positions, normals, UVs and rock meshes unchanged. Undo is available; save assets to persist.");
        }
        static List<Stamp> Capture(LTWorld w)
        {
            var list = new List<Stamp>();
            Walk(w.transform, w, list);
            foreach(var node in w.GetComponentsInChildren<LTRoadJunction>())
            {
                if(!node.isActiveAndEnabled||node.World!=w)continue;
                try
                {
                    var snapshot=node.Capture();var ids=new HashSet<int>(node.Roads().Select(r=>r.GetInstanceID()));
                    int index=list.FindIndex(s=>ids.Contains(s.id));if(index<0)index=list.Count;
                    list.Insert(index,new Stamp{id=node.GetInstanceID(),signature="junction:"+snapshot.hash+":"+node.surface,bounds=snapshot.bounds,affectHeight=true,junction=snapshot,
                        fixedRoadSurface=LTLODMesh.RequiresFixedRoadSurface(node.surface)});
                }
                catch(ArgumentException error){node.status=error.Message;}
            }
            return list;
        }
        static void Walk(Transform t, LTWorld w, List<Stamp> result)
        {
            // Explicit depth-first sibling traversal defines the application order.
            foreach (Transform child in t)
            {
                if (!child.gameObject.activeInHierarchy || child == w.generatedRoot) continue;
                if (child.GetComponent<LTWorld>()) continue;
                if(child.GetComponent<LTRoadGenerated>()||child.GetComponent<LTRoadJunctionGenerated>())continue;
                var road=child.GetComponent<LTRoad>();
                if(road&&road.enabled)
                {
                    // Invalid paths are displayed by the road inspector; never reuse stale geometry.
                    try
                    {
                        var snapshot=road.Capture(w);
                        result.Add(new Stamp{id=road.GetInstanceID(),signature="road:"+snapshot.geometryHash,
                            bounds=snapshot.bounds,affectHeight=true,road=snapshot,fixedRoadSurface=LTLODMesh.RequiresFixedRoadSurface(snapshot.mode)});
                    }
                    catch(ArgumentException) { }
                }
                var s = child.GetComponent<LTHeightStamp>();
                if (s && s.enabled)
                {
                    var up = child.up;
                    if (Vector3.Dot(up, Vector3.up) < 0.9999f || child.lossyScale.x <= 0 || child.lossyScale.y <= .00001f || child.lossyScale.z <= 0)
                        continue;

                    var matrix = w.transform.worldToLocalMatrix * child.localToWorldMatrix;
                    Vector3 min = new Vector3(float.MaxValue, 0, float.MaxValue), max = -min;
                    for (int z = -1; z <= 1; z += 2) for (int x = -1; x <= 1; x += 2)
                    {
                        Vector3 p = matrix.MultiplyPoint3x4(new Vector3(x * s.size.x * .5f, 0, z * s.size.y * .5f));
                        min.x = Mathf.Min(min.x, p.x); min.z = Mathf.Min(min.z, p.z);
                        max.x = Mathf.Max(max.x, p.x); max.z = Mathf.Max(max.z, p.z);
                    }
                    string sig = EditorJsonUtility.ToJson(s) + matrix.ToString("R")
                        + (s.mask ? s.mask.imageContentsHash.ToString()+AssetInputHash(s.mask) : "");
                    result.Add(new Stamp { id = s.GetInstanceID(), signature = sig,
                        inverse = matrix.inverse, bounds = Rect.MinMaxRect(min.x, min.z, max.x, max.z),
                        size = s.size, shape = s.shape, operation = s.operation, strength = s.strength,
                        edge = Mathf.Max(.001f, s.edgeFalloff), height = matrix.MultiplyPoint3x4(Vector3.zero).y * matrix.MultiplyVector(Vector3.up).magnitude,
                        blend = s.blendAmount, reference = s.multiplyReference, maskIsHeight = s.maskIsHeight, mask = s.mask, affectHeight=s.affectHeight && (!s.mask||s.mask.isReadable), densityZone=s.densityZone, densityCellSizeMin=s.densityCellSizeMin, densityCellSizeMax=s.densityCellSizeMax,
                        });
                }
                var meshStamp=child.GetComponent<LTMeshStamp>();
                var meshFilter=child.GetComponent<MeshFilter>();
                if(meshStamp&&meshStamp.enabled&&meshFilter)
                {
                    var assignedMesh=meshFilter.sharedMesh;
                    // Duplicating a GameObject copies sharedMesh. It is not ownership:
                    // never adopt another stamp's output or treat it as source geometry.
                    bool assignedPreview=assignedMesh&&meshStamp.sourceMesh&&assignedMesh!=meshStamp.sourceMesh&&
                        (assignedMesh.hideFlags&HideFlags.DontSaveInEditor)!=0;
                    if(assignedMesh&&!assignedPreview&&assignedMesh!=meshStamp.generatedTrimmedMesh&&assignedMesh!=meshStamp.sourceMesh)
                    {
                        meshStamp.sourceMesh=assignedMesh;EditorUtility.SetDirty(meshStamp);
                    }
                    var sourceMesh=meshStamp.sourceMesh?meshStamp.sourceMesh:assignedMesh;
                    if(!sourceMesh){Walk(child,w,result);continue;}
                    var matrix=w.transform.worldToLocalMatrix*child.localToWorldMatrix;
                    var sourceVertices=sourceMesh.vertices;
                    var vertices=new Vector3[sourceVertices.Length];
                    Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),max=new Vector3(float.MinValue,float.MinValue,float.MinValue);
                    for(int i=0;i<vertices.Length;i++){vertices[i]=matrix.MultiplyPoint3x4(sourceVertices[i]);min=Vector3.Min(min,vertices[i]);max=Vector3.Max(max,vertices[i]);}
                    var sourceTriangles=sourceMesh.triangles;
                    var contactTriangles=new List<int>();
                    var edgeCounts=new Dictionary<long,int>();
                    var edgeVertices=new Dictionary<long,Vector2Int>();
                    var weldedIds=new int[vertices.Length];
                    var weldedByPosition=new Dictionary<Vector3Int,int>();
                    int nextWeldedId=0;
                    for(int i=0;i<vertices.Length;i++)
                    {
                        // FBX meshes commonly duplicate vertices at UV/normal seams. Treat
                        // coincident copies as one vertex when extracting the contact outline.
                        var positionKey=new Vector3Int(
                            Mathf.RoundToInt(vertices[i].x*1000),
                            Mathf.RoundToInt(vertices[i].y*1000),
                            Mathf.RoundToInt(vertices[i].z*1000));
                        if(!weldedByPosition.TryGetValue(positionKey,out int weldedId))
                        {
                            weldedId=nextWeldedId++;
                            weldedByPosition.Add(positionKey,weldedId);
                        }
                        weldedIds[i]=weldedId;
                    }
                    Action<int,int> addEdge=(a,b)=>
                    {
                        int wa=weldedIds[a],wb=weldedIds[b];
                        int lo=Math.Min(wa,wb),hi=Math.Max(wa,wb);long key=((long)lo<<32)|(uint)hi;
                        edgeCounts[key]=edgeCounts.TryGetValue(key,out int count)?count+1:1;
                        edgeVertices[key]=new Vector2Int(a,b);
                    };
                    for(int i=0;i+2<sourceTriangles.Length;i+=3)
                    {
                        int ia=sourceTriangles[i],ib=sourceTriangles[i+1],ic=sourceTriangles[i+2];
                        Vector3 normal=Vector3.Cross(vertices[ib]-vertices[ia],vertices[ic]-vertices[ia]);
                        if(normal.y>=-.000001f)continue;
                        contactTriangles.Add(ia);contactTriangles.Add(ib);contactTriangles.Add(ic);
                        addEdge(ia,ib);addEdge(ib,ic);addEdge(ic,ia);
                    }
                    var boundaryEdges=new List<int>();
                    foreach(var pair in edgeCounts)if(pair.Value==1)
                    {var edge=edgeVertices[pair.Key];boundaryEdges.Add(edge.x);boundaryEdges.Add(edge.y);}
                    float blend=Mathf.Max(0,meshStamp.blendDistance);
                    float meshExtent=Mathf.Max(blend,meshStamp.terrainCutOffset)+.001f;
                    string sig=EditorJsonUtility.ToJson(meshStamp)+matrix.ToString("R")+sourceMesh.GetInstanceID()+":"+sourceMesh.vertexCount+":"+sourceTriangles.Length+":"+EditorUtility.GetDirtyCount(sourceMesh)+":"+AssetInputHash(sourceMesh);
                    result.Add(new Stamp{id=meshStamp.GetInstanceID(),signature=sig,meshStamp=true,affectHeight=meshStamp.affectHeight,meshMode=meshStamp.mode,
                        meshComponent=meshStamp,meshSource=sourceMesh,meshTrimRock=meshStamp.trimRockInsideTerrain||meshStamp.mode==LTMeshConformMode.Cave,
                        bounds=Rect.MinMaxRect(min.x-meshExtent,min.z-meshExtent,max.x+meshExtent,max.z+meshExtent),meshFootprintBounds=Rect.MinMaxRect(min.x,min.z,max.x,max.z),meshVertices=vertices,meshTriangles=contactTriangles.ToArray(),meshAllTriangles=sourceTriangles,meshBoundaryEdges=boundaryEdges.ToArray(),
                        meshCutOffset=Mathf.Max(.01f,meshStamp.terrainCutOffset),meshRetopoHeight=Mathf.Max(.01f,meshStamp.rockRetopoHeight),meshWeldDistance=Mathf.Max(0,meshStamp.weldDistance),
                        meshBlendDistance=blend,
                        meshGridDensityMultiplier=Mathf.Max(.1f,meshStamp.gridDensityMultiplier),meshCutTerrain=meshStamp.cutTerrain||meshStamp.mode==LTMeshConformMode.Cave});
                }
                Walk(child, w, result);
            }
        }
        static float MeshContactCell(float density)=>1f/Mathf.Max(.1f,density);
        static List<Density> CaptureDensity(LTWorld w,State state)
        {
            var result=new List<Density>();
            float baseCell=Mathf.Max(.000001f,Mathf.Max(w.source.size.x/w.chunksX,w.source.size.z/w.chunksZ)/Mathf.Max(1,w.cellsPerChunk));
            foreach(var stamp in state.previous)
            {
                if(stamp.junction!=null)
                {
                    float cell=stamp.junction.cellSize;
                    result.Add(new Density{id=stamp.id,bounds=stamp.bounds,cellSize=cell,signature=stamp.signature,
                        zones=new List<LTStampMesh.Zone>{new LTStampMesh.Zone{bounds=stamp.bounds,cellSize=cell,edgeCellSize=cell,customCellSize=(x,z)=>cell,coverageIntersects=stamp.junction.Intersects}}});
                    continue;
                }
                if(stamp.road!=null)
                {
                    var road=stamp.road;float cell=road.terrainCellSize;
                    var roadZones=new List<LTStampMesh.Zone>{new LTStampMesh.Zone{bounds=road.bounds,
                        cellSize=cell,edgeCellSize=cell,customCellSize=(x,z)=>cell,coverageIntersects=road.Intersects}};
                    result.Add(new Density{id=stamp.id,road=road,bounds=road.bounds,cellSize=cell,zones=roadZones,signature=stamp.signature});
                    continue;
                }
                if(stamp.meshStamp)
                {
                    if(stamp.meshBlendDistance<=0&&!stamp.meshCutTerrain)continue;
                    float cell=MeshContactCell(stamp.meshGridDensityMultiplier);
                    Rect densityBounds=Expanded(stamp.meshFootprintBounds,stamp.meshCutOffset+cell*2,stamp.meshCutOffset+cell*2);
                    Vector2 centre=densityBounds.center,size=densityBounds.size;
                    Matrix4x4 inverse=Matrix4x4.Translate(new Vector3(-centre.x,0,-centre.y));
                    // Mesh stamps need their finest topology at the contact perimeter, not in the
                    // empty middle of the bounds. Zone.CellSizeAt interpolates cellSize ->
                    // edgeCellSize, so deliberately reverse the usual height-stamp layout here.
                    var meshZones=new List<LTStampMesh.Zone>{new LTStampMesh.Zone{bounds=densityBounds,inverse=inverse,size=size,shape=LTStampShape.Rectangle,cellSize=baseCell,edgeCellSize=cell}};
                    result.Add(new Density{id=stamp.id,bounds=densityBounds,cellSize=cell,zones=meshZones,
                        signature="mesh:"+stamp.signature+":"+cell.ToString("R")+":"+baseCell.ToString("R")});
                    continue;
                }
                if(!stamp.densityZone)continue;
                Matrix4x4 flat=stamp.inverse;flat.m13=0;flat.m11=1;
                var zones=new List<LTStampMesh.Zone>();
                float minCell=Mathf.Max(.000001f,stamp.densityCellSizeMin);
                float maxCell=Mathf.Max(minCell,stamp.densityCellSizeMax);
                zones.Add(new LTStampMesh.Zone{bounds=stamp.bounds,inverse=stamp.inverse,size=stamp.size,shape=stamp.shape,cellSize=minCell,edgeCellSize=maxCell});
                result.Add(new Density{id=stamp.id,bounds=stamp.bounds,cellSize=minCell,zones=zones,
                    signature=flat.ToString("R")+stamp.size.ToString("R")+stamp.shape+minCell.ToString("R")+maxCell.ToString("R")+
                        ""});
            }
            if(w.enableLayerPainting&&w.enableLayerDisplacement&&(w.refineDisplacementFootprints||w.RegularMaskGridActive))
            {
                foreach(var coverage in w.DisplacementCoverage)
                {
                    bool regular=w.IsRegularMaskGridChunk(coverage.x,coverage.z);
                    if(!regular&&!w.refineDisplacementFootprints)continue;
                    float cell=regular?Mathf.Max(.125f,w.regularMaskGridStep):w.EffectiveDisplacementCellSize;
                    var bounds=coverage.rect;var grid=coverage.grid;
                    if(!grid.Any(0,0,1,1))continue;
                    var zone=new LTStampMesh.Zone{bounds=bounds,cellSize=cell,edgeCellSize=cell,
                        customCellSize=(x,z)=>cell,
                        coverageIntersects=r=>grid.Any((r.xMin-bounds.xMin)/bounds.width,(r.yMin-bounds.yMin)/bounds.height,
                            (r.xMax-bounds.xMin)/bounds.width,(r.yMax-bounds.yMin)/bounds.height)};
                    var paintZones=new List<LTStampMesh.Zone>{zone};
                    float boundaryCell=cell;
                    if(!regular&&w.displacementBoundaryPrototype&&coverage.x==w.displacementBoundaryChunk.x&&coverage.z==w.displacementBoundaryChunk.y)
                    {
                        boundaryCell=Mathf.Min(cell,LTPaintMath.DisplacementBaseStep(w.displacementBoundaryCellSize,w.FineDisplacementBaseActive));
                        float target=boundaryCell;
                        paintZones.Add(new LTStampMesh.Zone{bounds=bounds,cellSize=target,edgeCellSize=target,
                            customCellSize=(x,z)=>target,
                            coverageIntersects=r=>grid.Boundary((r.xMin-bounds.xMin)/bounds.width,(r.yMin-bounds.yMin)/bounds.height,
                                (r.xMax-bounds.xMin)/bounds.width,(r.yMax-bounds.yMin)/bounds.height)});
                    }
                    result.Add(new Density{id=coverage.id,bounds=bounds,cellSize=boundaryCell,zones=paintZones,
                        signature="paint-coverage:"+grid.hash+":"+bounds.ToString("R")+":"+cell.ToString("R")+":boundary:"+boundaryCell.ToString("R")});
                }
            }
            return result;
        }
        static void MarkDensity(LTWorld w,State state,Rect bounds,LTRoadMath.Snapshot road=null,LTRoadMath.Snapshot other=null)
        {
            for(int id=0;id<w.chunksX*w.chunksZ;id++)
            {
                var chunk=ChunkRect(w,id);
                if(Overlap(bounds,chunk)&&(road==null||road.Intersects(chunk))&&
                    (road==null||other==null||!road.SameTerrainRegion(other,chunk)))DirtyChunk(state,id);
            }
        }
        static void DetectDensity(LTWorld w,State state)
        {
            var next=CaptureDensity(w,state);var old=state.densities.ToDictionary(d=>d.id);var current=next.ToDictionary(d=>d.id);
            bool changed=false;
            foreach(var a in state.densities)if(!current.TryGetValue(a.id,out var b)||a.signature!=b.signature){MarkDensity(w,state,a.bounds,a.road,b?.road);changed=true;}
            foreach(var b in next)if(!old.TryGetValue(b.id,out var a)||a.signature!=b.signature){MarkDensity(w,state,b.bounds,b.road,a?.road);changed=true;}
            var oldOrder=state.densities.Select(d=>d.id).Where(current.ContainsKey).ToArray();
            var newOrder=next.Select(d=>d.id).Where(old.ContainsKey).ToArray();
            if(!oldOrder.SequenceEqual(newOrder))
            {
                var rank=newOrder.Select((id,i)=>new {id,i}).ToDictionary(p=>p.id,p=>p.i);
                for(int i=0;i<oldOrder.Length;i++)for(int j=i+1;j<oldOrder.Length;j++)
                {
                    if(rank[oldOrder[i]]<rank[oldOrder[j]])continue;
                    Rect a=current[oldOrder[i]].bounds,b=current[oldOrder[j]].bounds;
                    if(!Overlap(a,b))continue;
                    MarkDensity(w,state,Rect.MinMaxRect(Mathf.Max(a.xMin,b.xMin),Mathf.Max(a.yMin,b.yMin),Mathf.Min(a.xMax,b.xMax),Mathf.Min(a.yMax,b.yMax)));changed=true;
                }
            }
            if(changed)state.lastChange=EditorApplication.timeSinceStartup;
            state.densities=next;
        }
        static Func<Rect,bool> TransitionDiagonals(LTWorld w,int chunk)
        {
            // Selected CPU preview: consistent diagonals in regular cells and
            // boundary-preserving triangulation of all balanced transition cells.
            if(w.IsRegularMaskGridChunk(chunk%w.chunksX,chunk/w.chunksX))return r=>true;
            if(w.enableLayerDisplacement&&w.FineDisplacementBaseActive&&
                w.DisplacementCoverage.Any(c=>c.x==chunk%w.chunksX&&c.z==chunk/w.chunksX))return r=>true;
            if(!w.enableLayerDisplacement||!w.refineDisplacementFootprints||!w.displacementBoundaryPrototype||!w.displacementTransitionDiagonals)return null;
            int x=chunk%w.chunksX,z=chunk/w.chunksX;
            if(x!=w.displacementBoundaryChunk.x||z!=w.displacementBoundaryChunk.y)return null;
            // Cover regular cells as well: the old boundary-only predicate never
            // touched the checkerboard eight-way hubs inside the painted region.
            return r=>true;
        }
        static string Config(LTWorld w) => (w.source ? w.source.GetInstanceID() : 0) + ":" + w.chunksX + ":" + w.chunksZ
            + ":source-version:"+(w.source?EditorUtility.GetDirtyCount(w.source):0)
            + ":source-asset:"+AssetInputHash(w.source)
            + ":source-layout:"+(w.source?w.source.resolution+":"+w.source.size.ToString("R")+":"+
                (w.source.heights==null?0:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(w.source.heights)):"")
            + ":root:"+(w.generatedRoot?w.generatedRoot.GetInstanceID():0)+":"+w.transform.localToWorldMatrix.ToString("R")
            + ":regular-mask-grid:"+w.RegularMaskGridActive+":"+w.regularMaskGridChunk+":"+w.regularMaskGridStep.ToString("R")
            + ":fine-base:"+w.FineDisplacementBaseActive
            + ":spatial-lod-v2:"+w.UseSpatialLODs+":"+w.SpatialLODDivisions
            + ":offroad-height-error-lod-v1"
            + ":transition-v2:"+w.displacementTransitionDiagonals+":"+w.displacementBoundaryPrototype+":"+w.displacementBoundaryChunk
            + ":" + w.cellsPerChunk + ":" + w.adaptive + ":" + w.maxVerticesPerChunk + ":" + w.maxHeightError + ":" + (w.material ? w.material.GetInstanceID() : 0)
            + ":" + w.enableLODs + ":" + (w.lods==null?"null":string.Join(";",w.lods.Select(l=>l==null?"null":l.simplificationSteps+":"+l.maxHeightError.ToString("R"))));
        static State GetState(LTWorld w)
        {
            if (!states.TryGetValue(w.GetInstanceID(), out var s)) states[w.GetInstanceID()] = s = new State();
            return s;
        }
        static bool Overlap(Rect a, Rect b) => a.xMin <= b.xMax && a.xMax >= b.xMin && a.yMin <= b.yMax && a.yMax >= b.yMin;
        static Rect ChunkRect(LTWorld w, int id)
        {
            float dx = w.source.size.x / w.chunksX, dz = w.source.size.z / w.chunksZ;
            return new Rect(id % w.chunksX * dx, id / w.chunksX * dz, dx, dz);
        }
        static Rect Expanded(Rect r, float x, float z) => Rect.MinMaxRect(r.xMin-x, r.yMin-z, r.xMax+x, r.yMax+z);
        static void Mark(LTWorld w, State s, Rect r, LTRoadMath.Snapshot road=null,LTRoadMath.Snapshot other=null)
        {
            // One-sample halo: central-difference normals need neighbouring surface samples.
            float hx = w.source.size.x / (w.chunksX * w.cellsPerChunk);
            float hz = w.source.size.z / (w.chunksZ * w.cellsPerChunk);
            r = Expanded(r, hx, hz);
            for (int id = 0; id < w.chunksX * w.chunksZ; id++)
            {
                var chunk=ChunkRect(w,id);
                // Include the normal-sampling halo, including on the old path when moved/deleted.
                if (Overlap(r,chunk) && (road==null || road.Intersects(Expanded(chunk,hx,hz)))&&
                    (road==null||other==null||!road.SameTerrainRegion(other,Expanded(chunk,hx,hz))))
                { DirtyChunk(s,id); w.displacementGeometry.Touch(id); }
            }
        }
        static void Detect(LTWorld w, State s, List<Stamp> current)
        {
            string config = Config(w);
            w.displacementGeometry.Configure(w.chunksX,w.chunksZ,w.source.size.x/w.chunksX,w.source.size.z/w.chunksZ,
                config+"|"+w.transform.localToWorldMatrix.ToString("R"));
            bool configChanged=!s.initialized||s.config!=config;
            if (!s.initialized || s.config != config)
            {
                CancelBuilds(s);s.revision++;
                for (int i = 0; i < w.chunksX*w.chunksZ; i++) DirtyChunk(s,i);
                s.initialized = true; s.config = config; s.lastChange = EditorApplication.timeSinceStartup;
            }
            var before = s.previous.ToDictionary(a => a.id);
            var after = current.ToDictionary(a => a.id);
            bool changed = false;
            foreach (var a in s.previous)
                if (!after.TryGetValue(a.id, out var b) || a.signature != b.signature)
                { Mark(w,s,a.bounds,a.road,b?.road); changed = true; }
            foreach (var b in current)
                if (!before.TryGetValue(b.id, out var a) || a.signature != b.signature)
                { Mark(w,s,b.bounds,b.road,a?.road); changed = true; }
            // Only inverted pairs matter for reorder. Insertion/deletion does not dirty unrelated stamps.
            var oldOrder = s.previous.Select(a=>a.id).Where(after.ContainsKey).ToArray();
            var newOrder = current.Select(a=>a.id).Where(before.ContainsKey).ToArray();
            if (!oldOrder.SequenceEqual(newOrder))
            {
                var rank = newOrder.Select((id,i)=>new { id,i }).ToDictionary(p=>p.id,p=>p.i);
                for (int i=0;i<oldOrder.Length;i++) for(int j=i+1;j<oldOrder.Length;j++)
                {
                    if(rank[oldOrder[i]] < rank[oldOrder[j]]) continue;
                    Rect a=after[oldOrder[i]].bounds,b=after[oldOrder[j]].bounds;
                    if(!Overlap(a,b)) continue;
                    Mark(w,s,Rect.MinMaxRect(Mathf.Max(a.xMin,b.xMin),Mathf.Max(a.yMin,b.yMin),
                        Mathf.Min(a.xMax,b.xMax),Mathf.Min(a.yMax,b.yMax)));
                    changed = true;
                }
            }
            if(changed) s.lastChange = EditorApplication.timeSinceStartup;
            if(changed||configChanged)
            {
                s.cycleActive=true;s.cycleMeshPasses=s.cyclePaintPasses=0;
                s.cycleMeshMs=s.cyclePaintMs=s.cycleColliderMs=0;
                s.heightSamples.ResetStatistics();
            }
            s.previous = current;
            DetectDensity(w,s);
        }
        static void Tick()
        {
            LTLODJobWork.CollectRetired();
            LTHeightJobWork.CollectRetired();
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + .1;
            var worlds = Worlds();
            var live = new HashSet<int>(worlds.Select(w=>w.GetInstanceID()));
            foreach (int id in states.Keys.Where(id=>!live.Contains(id)).ToArray())
            {CancelBuilds(states[id]);states.Remove(id);}
            foreach (var w in worlds)
            {
                var state = GetState(w);
                if (!w.source || !w.generatedRoot || !ValidTransform(w)||!w.isActiveAndEnabled||!w.autoUpdate)
                {CancelBuilds(state);continue;}
                w.UpdateLOD();
                try
                {
                    bool isEditing=GUIUtility.hotControl!=0;
                    var detectionTimer=Stopwatch.StartNew();
                    var current=Capture(w);
                    Detect(w,state,current);
                    w.lastDetectionMilliseconds=(float)detectionTimer.Elapsed.TotalMilliseconds;
                    if(state.dirty.Count>0&&!state.cycleActive)
                    {
                        state.cycleActive=true;state.cycleMeshPasses=state.cyclePaintPasses=0;
                        state.cycleMeshMs=state.cyclePaintMs=state.cycleColliderMs=0;
                        state.heightSamples.ResetStatistics();
                        state.heightJobFallbacks=0;
                    }
                    bool rebuilt=false;
                    // Detect ran before advancing: a changed snapshot can never
                    // resume an old iterator, even when the user is still dragging.
                    state.build.Invalidate(state.revision,state.dirty);
                    if(state.build.Active&&state.buildObjectsValid!=null&&!state.buildObjectsValid())
                    {
                        CancelBuilds(state);state.initialized=false;
                        // Deleted/replaced generated objects require a fresh capture,
                        // never a publication through references from the old layout.
                        continue;
                    }
                    if(!isEditing)
                    {
                        if(!state.build.Active&&state.dirty.Count>0&&now-state.lastChange>=.15)
                        {
                            state.buildObjectsValid=null;
                            state.build.Begin(state.revision,state.dirty,affected=>RebuildSteps(w,state,true,affected).GetEnumerator(),
                                state.chunkLODs.CaptureVersions(),state.chunkLODs.Version);
                            state.cycleMeshPasses++;
                        }
                        if(state.build.Active)
                        {
                            var stageTimer=Stopwatch.StartNew();
                            // Soft budget between indivisible algorithmic stages.
                            // LOD0 publication always yields a frame, even if fast.
                            do
                            {
                                rebuilt|=state.build.Advance(state.revision,state.dirty);
                                if(!state.build.Active){rebuilt=true;break;}
                            }while(!rebuilt&&!state.build.Waiting&&stageTimer.Elapsed.TotalMilliseconds<4);
                            state.cycleMeshMs+=stageTimer.Elapsed.TotalMilliseconds;
                            state.lastPreview=EditorApplication.timeSinceStartup;
                        }
                        // New LOD0 has priority. A completed base publication always
                        // gets a frame before independent coarse work may advance.
                        if(!rebuilt&&!state.build.Active&&state.dirty.Count==0&&state.chunkLODs.Count>0)
                        {
                            var stageTimer=Stopwatch.StartNew();bool published=false;
                            do{published|=state.chunkLODs.Advance(state.dirty);}
                            while(!published&&!state.chunkLODs.Waiting&&state.dirty.Count==0&&state.chunkLODs.Count>0&&stageTimer.Elapsed.TotalMilliseconds<4);
                            state.cycleMeshMs+=stageTimer.Elapsed.TotalMilliseconds;
                            if(published)SceneView.RepaintAll();
                        }
                    }
                    // Do not bake against stale/intermediate geometry in a separate callback.
                    // Recheck coverage immediately so colliders are cooked only after the
                    // conservative refinement has settled, not on every intermediate mesh.
                    if(state.dirty.Count==0&&!isEditing)
                    {
                        // Retain the paint poll interval while idle; a freshly rebuilt
                        // mesh bypasses it so its material/coverage is published together.
                        if(rebuilt||EditorApplication.timeSinceStartup-state.lastPaint>=.15)
                        {
                            var stageTimer=Stopwatch.StartNew();w.UpdatePainting(true);
                            state.lastPaint=EditorApplication.timeSinceStartup;
                            if(state.cycleActive){state.cyclePaintMs+=stageTimer.Elapsed.TotalMilliseconds;state.cyclePaintPasses++;}
                            if(rebuilt||(w.paintCpu.LastTick!=null&&(w.paintCpu.LastTick.weightBakes>0||w.paintCpu.LastTick.weightReuses>0||w.paintCpu.LastTick.roadProjectionBakes>0)))
                                w.lastEditorPaintStages=w.paintCpu.LastTickStatus;
                            DetectDensity(w,state);
                            if(state.dirty.Count==0&&state.colliders.Count>0&&EditorApplication.timeSinceStartup-state.lastChange>.35)
                            {
                                stageTimer.Restart();UpdateColliders(w,state);state.cycleColliderMs+=stageTimer.Elapsed.TotalMilliseconds;
                            }
                        }
                    }
                    if(state.cycleActive)
                    {
                        bool settled=state.dirty.Count==0&&state.colliders.Count==0&&!state.build.Active&&state.chunkLODs.Count==0;
                        w.lastEditorUpdateCycle=$"Цикл обновления: {(settled?"завершён":"уточнение / ожидание")}\n"+
                            $"Геометрия: {state.cycleMeshPasses} проходов / {state.cycleMeshMs:F1} мс\n"+
                            $"Покраска и маски: {state.cyclePaintPasses} проходов / {state.cyclePaintMs:F1} мс\n"+
                            $"Коллайдеры: {state.cycleColliderMs:F1} мс. Ожидают чанки: {state.dirty.Count}.\n"+
                            (state.build.Active?$"Поэтапная сборка r{state.revision}: {(state.build.Lod0Ready?"LOD0 показан; расчёт остальных LOD":"подготовка LOD0 и стыков")}; чанков: {state.build.PendingCount}.\n":"")+
                            $"Локальные LOD: в очереди {state.chunkLODs.Count}; завершено {state.chunkLODs.Completed}; отброшено {state.chunkLODs.Discarded} (за сессию).\n"+
                            (state.lodJobStatus!=null?state.lodJobStatus+"\n":"")+
                            (state.lastLODJobBackend!=null?"Последнее упрощение LOD: "+state.lastLODJobBackend+".\n":"")+
                            (state.heightJobStatus!=null?state.heightJobStatus+"\n":"")+
                            (state.lastHeightJobBackend!=null?"Последние высоты: "+state.lastHeightJobBackend+".\n":"")+
                            $"Высоты из Jobs: {state.heightSamples.FromJobs}.\n"+
                            $"Неоднозначные проекции дорог: {state.heightJobFallbacks} проверок CPU.\n"+
                            $"Высоты: повторно {state.heightSamples.Hits}; вычислено {state.heightSamples.Evaluations}; кэш {state.heightSamples.Count}/{state.heightSamples.Limit}; вытеснений {state.heightSamples.Evictions}; без сохранения {state.heightSamples.Bypassed}.\n"+
                            (state.chunkLODs.Count>0?"Чанки LOD: "+string.Join(", ",state.chunkLODs.PendingIds.OrderBy(id=>id).Take(12).Select(id=>$"({id%w.chunksX},{id/w.chunksX})"))+(state.chunkLODs.Count>12?" …":"")+"\n":"")+
                            "CPU основного потока: без времени фоновых Jobs и пауз между обновлениями. Фильтры могут требовать дополнительного уточнения.";
                        if(settled)state.cycleActive=false;
                    }
                }
                catch (Exception e) { CancelBuilds(state);w.autoUpdate = false; Debug.LogException(e,w); }
            }
        }
        public static void Refresh(LTWorld w, bool all, bool saveAssets = true)
        {
            if (!w.source || !w.generatedRoot || !ValidTransform(w)) return;
            var s = GetState(w);CancelBuilds(s);
            if(all){s.heightSource=null;s.heightSamples.Clear();}
            var detectionTimer=Stopwatch.StartNew(); Detect(w,s,Capture(w));
            w.lastDetectionMilliseconds=(float)detectionTimer.Elapsed.TotalMilliseconds;
            if (all) for (int i=0;i<w.chunksX*w.chunksZ;i++) s.dirty.Add(i);
            Rebuild(w,s); UpdateColliders(w,s);
            if(saveAssets)AssetDatabase.SaveAssets();
        }
        static void FlushAll()
        {
            if (EditorApplication.isPlaying) return;
            foreach (var w in Worlds()) if (w.source && w.generatedRoot) Refresh(w,false);
        }
        static void SampleEdge(Vector2 p,Vector3 a,Vector3 b,ref float distance,ref float height)
        {
            Vector2 aa=new Vector2(a.x,a.z),bb=new Vector2(b.x,b.z),ab=bb-aa;
            float t=ab.sqrMagnitude>.0000001f?Mathf.Clamp01(Vector2.Dot(p-aa,ab)/ab.sqrMagnitude):0;
            float d=Vector2.Distance(p,Vector2.Lerp(aa,bb,t));
            if(d<distance){distance=d;height=Mathf.Lerp(a.y,b.y,t);}
        }
        static bool SampleRockUnderside(Stamp s,float x,float z,out float height,out float distance)
        {
            height=float.MaxValue;distance=float.MaxValue;bool inside=false;
            if(s.meshVertices==null||s.meshTriangles==null)return false;
            Vector2 p=new Vector2(x,z);
            for(int i=0;i+2<s.meshTriangles.Length;i+=3)
            {
                Vector3 a=s.meshVertices[s.meshTriangles[i]],b=s.meshVertices[s.meshTriangles[i+1]],c=s.meshVertices[s.meshTriangles[i+2]];
                Vector2 aa=new Vector2(a.x,a.z),bb=new Vector2(b.x,b.z),cc=new Vector2(c.x,c.z);
                float denominator=(bb.y-cc.y)*(aa.x-cc.x)+(cc.x-bb.x)*(aa.y-cc.y);
                if(Mathf.Abs(denominator)>.0000001f)
                {
                    float u=((bb.y-cc.y)*(p.x-cc.x)+(cc.x-bb.x)*(p.y-cc.y))/denominator;
                    float v=((cc.y-aa.y)*(p.x-cc.x)+(aa.x-cc.x)*(p.y-cc.y))/denominator;
                    float q=1-u-v;
                    if(u>=-.00001f&&v>=-.00001f&&q>=-.00001f)
                    {
                        float y=a.y*u+b.y*v+c.y*q;
                        if(!inside||y<height)height=y;
                        inside=true;distance=0;continue;
                    }
                }
            }
            if(!inside&&s.meshBoundaryEdges!=null)
                for(int i=0;i+1<s.meshBoundaryEdges.Length;i+=2)
                    SampleEdge(p,s.meshVertices[s.meshBoundaryEdges[i]],s.meshVertices[s.meshBoundaryEdges[i+1]],ref distance,ref height);
            return inside||distance<=s.meshBlendDistance;
        }
        static bool InsideRockCut(Stamp s,float x,float z,float terrainHeight)
        {
            if(!s.meshCutTerrain||s.meshVertices==null||s.meshAllTriangles==null)return false;
            if(s.meshTrimRock&&s.contactSegments!=null)
            {
                bool inside=false;float closest=float.MaxValue;
                Vector2 query=new Vector2(x,z);
                var candidates=s.cutRows==null?null:s.cutRows[Mathf.Clamp(Mathf.FloorToInt((z-s.cutRowMin)/s.cutRowStep),0,s.cutRows.Length-1)];
                int count=candidates==null?s.contactSegments.Count/2:candidates.Count;
                for(int j=0;j<count;j++)
                {
                    int i=candidates==null?j*2:candidates[j];
                    Vector3 av=s.contactSegments[i],bv=s.contactSegments[i+1];
                    Vector2 a=new Vector2(av.x,av.z),b=new Vector2(bv.x,bv.z),ab=b-a;
                    float t=ab.sqrMagnitude>1e-12f?Mathf.Clamp01(Vector2.Dot(query-a,ab)/ab.sqrMagnitude):0;
                    closest=Mathf.Min(closest,(query-a-t*ab).sqrMagnitude);
                    if((a.y>z)!=(b.y>z)&&x<(b.x-a.x)*(z-a.y)/(b.y-a.y)+a.x)inside=!inside;
                }
                float offset=s.meshCutOffset;
                return inside||closest<offset*offset;
            }
            Vector2 p=new Vector2(x,z);bool projected=false;
            float bottom=float.MaxValue,top=float.MinValue;
            for(int i=0;i+2<s.meshAllTriangles.Length;i+=3)
            {
                Vector3 a=s.meshVertices[s.meshAllTriangles[i]],b=s.meshVertices[s.meshAllTriangles[i+1]],c=s.meshVertices[s.meshAllTriangles[i+2]];
                Vector2 aa=new Vector2(a.x,a.z),bb=new Vector2(b.x,b.z),cc=new Vector2(c.x,c.z);
                float den=(bb.y-cc.y)*(aa.x-cc.x)+(cc.x-bb.x)*(aa.y-cc.y);
                if(Mathf.Abs(den)<.0000001f)continue;
                float u=((bb.y-cc.y)*(p.x-cc.x)+(cc.x-bb.x)*(p.y-cc.y))/den;
                float v=((cc.y-aa.y)*(p.x-cc.x)+(aa.x-cc.x)*(p.y-cc.y))/den;
                if(u>=-.0001f&&v>=-.0001f&&u+v<=1.0001f)
                {
                    float hit=u*a.y+v*b.y+(1-u-v)*c.y;
                    bottom=Mathf.Min(bottom,hit);top=Mathf.Max(top,hit);projected=true;
                }
            }
            // The terrain is cut only where its original height passes through the
            // vertical rock volume. A mere XZ projection must not create an open hole
            // below an overhang or a part of the rock that has already been trimmed.
            if(!projected||terrainHeight<bottom-.001f||terrainHeight>top+.001f)return false;
            return true;
        }
        static bool CutTerrainAt(LTWorld world,List<Stamp> stamps,float x,float z)
        {
            float terrainHeight=0;bool sampled=false;
            foreach(var s in stamps)
            {
                if(!s.meshStamp||!s.meshCutTerrain)continue;
                if(x<s.bounds.xMin||x>s.bounds.xMax||z<s.bounds.yMin||z>s.bounds.yMax)continue;
                if(!(s.meshTrimRock&&s.contactSegments!=null)&&!sampled)
                {terrainHeight=Evaluate(world,stamps,x,z,false);sampled=true;}
                if(InsideRockCut(s,x,z,terrainHeight))return true;
            }
            return false;
        }
        public static float SampleHeightForRoad(LTWorld world,float x,float z)
            =>CreateHeightSamplerForRoad(world)(x,z);
        public static Func<float,float,float> CreateHeightSamplerForRoad(LTWorld world)
        {
            if(!world||!world.source)return (x,z)=>0;
            var stamps=Capture(world);stamps.RemoveAll(s=>s.road!=null||s.junction!=null);
            return (x,z)=>Evaluate(world,stamps,x,z);
        }
        static float Evaluate(LTWorld w, List<Stamp> stamps, float x, float z,bool includeMeshStamps=true)
            =>ApplyHeightStamps(w.source.Sample(x,z),stamps,x,z,includeMeshStamps);
        // Separate factory: do not capture the RebuildSteps closure (world forest,
        // pending meshes and rock proposals) in long-lived coarse continuations.
        static Func<float,float,float> HeightSampler(LTWorld w,State state,int id,List<Stamp> local)
        {
            var source=w.source;int version=EditorUtility.GetDirtyCount(source);string asset=AssetInputHash(source);
            if(state.heightSource==null||state.heightSourceOwner!=source||state.heightSourceVersion!=version||state.heightSourceAsset!=asset||
                state.heightSourceInput!=source.heights||state.heightSource.resolution!=source.resolution||!state.heightSource.size.Equals(source.size))
            {
                var next=new LTHeightSampling.Source(source.heights,source.resolution,source.size);
                state.heightSamples.Clear();state.heightSource=next;state.heightSourceOwner=source;
                state.heightSourceVersion=version;state.heightSourceAsset=asset;state.heightSourceInput=source.heights;
            }
            var snapshot=state.heightSource;
            // An untouched source lookup is cheaper than a dictionary lookup.
            // Reserve the composed-height cache for chunks with height modifiers.
            if(!local.Any(s=>s.affectHeight))return snapshot.Sample;
            var sampler=state.heightSamples.Bind(id,(x,z)=>ApplyHeightStamps(snapshot.Sample(x,z),local,x,z,true));
            return sampler.Sample;
        }
        // Snapshot only the requested chunk. Texture reads and rock intersection
        // queries stay on the editor thread; workers receive their numeric operands.
        // Road BVH/variation/ruts and source interpolation execute in parallel.
        static IEnumerable<LTBuildStep> PrefetchHeights(LTWorld w,State state,int id,List<Stamp> local,IEnumerable<Vector2> coordinates)
        {
            // Cheap unmodified source / non-road edits keep the direct path.
            if(!local.Any(s=>s.affectHeight&&s.road!=null))yield break;
            var fallback=HeightSampler(w,state,id,local);
            var binding=state.heightSamples.Bind(id,fallback);
            if(binding.Remaining==0)yield break;
            LTHeightJobWork.Input input=null;
            var roadIds=new Dictionary<LTRoadMath.Snapshot,int>();var roadSources=new List<LTRoadMath.Snapshot>();
            foreach(var stamp in local)if(stamp.affectHeight&&stamp.road!=null&&!roadIds.ContainsKey(stamp.road))
            {roadIds.Add(stamp.road,roadSources.Count);roadSources.Add(stamp.road);}
            var seen=new HashSet<LTHeightSampling.Cache.Point>();
            var points=new List<LTHeightJobMath.Point>();var commands=new List<LTHeightJobMath.Command>();
            int visits=0;
            try
            {
                using(var it=coordinates.GetEnumerator())
                {
                    bool more=true;
                    while(more&&binding.Remaining>0)
                    {
                        int limit=Math.Min(LTHeightJobWork.MaxBatch,binding.Remaining);
                        while(points.Count<limit&&commands.Count<131072&&(more=it.MoveNext()))
                        {
                            var p=it.Current;
                            if(!binding.Contains(p.x,p.y)&&seen.Add(new LTHeightSampling.Cache.Point(p.x,p.y)))
                            {
                                int start=commands.Count;
                                foreach(var s in local)
                                {
                                    float x=p.x,z=p.y;
                                    if(!s.affectHeight||x<s.bounds.xMin||x>s.bounds.xMax||z<s.bounds.yMin||z>s.bounds.yMax)continue;
                                    if(s.road!=null){commands.Add(new LTHeightJobMath.Command{kind=1,road=roadIds[s.road]});continue;}
                                    if(s.junction!=null){commands.Add(new LTHeightJobMath.Command{kind=3,target=s.junction.centre.y,weight=s.junction.Weight(x,z)});continue;}
                                    if(s.meshStamp)
                                    {
                                        if(s.meshCutTerrain&&s.meshTrimRock)continue;
                                        if(!SampleRockUnderside(s,x,z,out float h,out float d))continue;
                                        float weight=d<=0?1:1-Mathf.Clamp01(d/Mathf.Max(.000001f,s.meshBlendDistance));weight=weight*weight*(3-2*weight);
                                        commands.Add(new LTHeightJobMath.Command{kind=2,target=h,weight=weight});continue;
                                    }
                                    Vector3 q=s.inverse.MultiplyPoint3x4(new Vector3(x,0,z));
                                    float u=q.x/Mathf.Max(.01f,s.size.x)+.5f,v=q.z/Mathf.Max(.01f,s.size.y)+.5f;
                                    float nx=Mathf.Abs(u*2-1),nz=Mathf.Abs(v*2-1);
                                    float distance=s.shape==LTStampShape.Ellipse?Mathf.Sqrt(nx*nx+nz*nz):Mathf.Max(nx,nz);
                                    if(distance>=1)continue;
                                    float t=Mathf.Clamp01((1-distance)/s.edge),weight2=t*t*(3-2*t)*s.strength;
                                    float texture=s.mask?s.mask.GetPixelBilinear(u,v).r:1,target=s.height;
                                    if(s.maskIsHeight)target*=texture;else weight2*=texture;
                                    commands.Add(new LTHeightJobMath.Command{target=target,weight=weight2,operation=s.operation,blend=s.blend,reference=s.reference});
                                }
                                points.Add(new LTHeightJobMath.Point{position=p,first=start,count=commands.Count-start});
                            }
                            if(++visits==256){visits=0;state.heightJobStatus=$"Высоты ({id%w.chunksX},{id/w.chunksX}): подготовка числовых данных";yield return LTBuildStep.Working;}
                        }
                        // A tiny remainder is cheaper through the original sampler.
                        if(points.Count<128)break;
                        if(input==null)
                        {
                            input=new LTHeightJobWork.Input();var roads=new List<LTRoadHeightKernel.Road>();
                            var samples=new List<LTRoadMath.Sample>();var nodes=new List<LTRoadHeightKernel.Node>();
                            foreach(var road in roadSources)roads.Add(road.CopyHeightData(samples,nodes));
                            input.roads=roads.ToArray();input.samples=samples.ToArray();input.nodes=nodes.ToArray();
                            input.source=state.heightSource.CopyPatch(ChunkRect(w,id),out input.layout);
                        }
                        while(!LTHeightJobWork.CanSchedule)yield return LTBuildStep.Waiting;
                        using(var job=new LTHeightJobWork(input,points,commands))
                        {
                            state.heightJobStatus=$"Высоты ({id%w.chunksX},{id/w.chunksX}): Jobs, {points.Count} точек";
                            yield return LTBuildStep.Waiting;
                            while(!job.IsCompleted)yield return LTBuildStep.Waiting;
                            state.lastHeightJobBackend=job.UsedBurst?"Burst Job":"Jobs, managed (Burst выключен или компилируется)";
                            for(int i=0;i<points.Count;i++)
                            {
                                var p=points[i].position;
                                if(job.NeedsReference(i)){fallback(p.x,p.y);state.heightJobFallbacks++;}
                                else binding.Accept(p.x,p.y,job.Read(i));
                                if((i&1023)==1023)yield return LTBuildStep.Working;
                            }
                        }
                        points.Clear();commands.Clear();
                    }
                }
            }
            finally{state.heightJobStatus=null;}
        }
        static float ApplyHeightStamps(float value,List<Stamp> stamps,float x,float z,bool includeMeshStamps)
        {
            foreach (var s in stamps)
            {
                if(!s.affectHeight)continue;
                if (x < s.bounds.xMin || x > s.bounds.xMax || z < s.bounds.yMin || z > s.bounds.yMax) continue;
                if(s.road!=null){value=s.road.ApplyHeight(x,z,value);continue;}
                if(s.junction!=null){value=s.junction.ApplyHeight(x,z,value);continue;}
                if(s.meshStamp)
                {
                    if(!includeMeshStamps)continue;
                    // Bridge mode replaces the lower rock and the terrain inside the
                    // offset contour. Its outer rim must remain on the original terrain.
                    if(s.meshCutTerrain&&s.meshTrimRock)continue;
                    if(!SampleRockUnderside(s,x,z,out float rockHeight,out float rockDistance))continue;
                    float meshWeight=rockDistance<=0?1:1-Mathf.Clamp01(rockDistance/Mathf.Max(.000001f,s.meshBlendDistance));
                    meshWeight=meshWeight*meshWeight*(3-2*meshWeight);
                    float meshTarget=rockHeight;
                    meshTarget=Mathf.Max(value,meshTarget);
                    value=Mathf.Lerp(value,meshTarget,meshWeight);
                    continue;
                }
                Vector3 p = s.inverse.MultiplyPoint3x4(new Vector3(x,0,z));
                float u = p.x / Mathf.Max(.01f,s.size.x) + .5f, v = p.z / Mathf.Max(.01f,s.size.y) + .5f;
                float nx = Mathf.Abs(u * 2 - 1), nz = Mathf.Abs(v * 2 - 1);
                float d = s.shape == LTStampShape.Ellipse ? Mathf.Sqrt(nx*nx+nz*nz) : Mathf.Max(nx,nz);
                if (d >= 1) continue;
                float t = Mathf.Clamp01((1-d)/s.edge);
                float weight = t*t*(3-2*t) * s.strength;
                float texture = s.mask ? s.mask.GetPixelBilinear(u,v).r : 1;
                float target = s.height;
                if(s.maskIsHeight) target *= texture; else weight *= texture;
                value = LTBlend.Apply(value,target,weight,s.operation,s.blend,s.reference);
            }
            return value;
        }
        static RockClipVertex LerpRockVertex(RockClipVertex a,RockClipVertex b,float t)
        {
            var r=a;
            r.position=Vector3.LerpUnclamped(a.position,b.position,t);
            r.normal=Vector3.LerpUnclamped(a.normal,b.normal,t).normalized;
            r.tangent=Vector4.LerpUnclamped(a.tangent,b.tangent,t);
            var tangent3=new Vector3(r.tangent.x,r.tangent.y,r.tangent.z).normalized;
            r.tangent=new Vector4(tangent3.x,tangent3.y,tangent3.z,r.tangent.w>=0?1:-1);
            r.uv=Vector2.LerpUnclamped(a.uv,b.uv,t);r.distance=0;return r;
        }
        static void PrepareContactContours(LTWorld world,List<Stamp> stamps)
        {
            foreach(var stamp in stamps)
            {
                if(!stamp.meshStamp||!stamp.meshCutTerrain||!stamp.meshTrimRock)continue;
                var vertices=new RockClipVertex[stamp.meshVertices.Length];
                for(int i=0;i<vertices.Length;i++)
                {
                    Vector3 p=stamp.meshVertices[i];
                    vertices[i]=new RockClipVertex{position=p,distance=p.y-Evaluate(world,stamps,p.x,p.z,false)};
                }
                var segments=new List<RockClipVertex>();
                for(int i=0;i+2<stamp.meshAllTriangles.Length;i+=3)
                    CollectRockBoundarySegment(ClipRockTriangle(vertices[stamp.meshAllTriangles[i]],vertices[stamp.meshAllTriangles[i+1]],vertices[stamp.meshAllTriangles[i+2]]),segments);
                stamp.contactSegments=new List<Vector3>();
                // Deduplicate geometric edges at source UV/material seams and verify
                // closed loops before using a parity test to cut the terrain.
                foreach(var loop in OrderedBoundaryLoops(segments,stamp.meshComponent.name+": terrain intersection"))
                    for(int i=0;i<loop.Count;i++)
                    {stamp.contactSegments.Add(loop[i].position);stamp.contactSegments.Add(loop[(i+1)%loop.Count].position);}
                BuildCutRows(stamp);
            }
        }
        static void BuildCutRows(Stamp stamp)
        {
            var segments=stamp.contactSegments;stamp.cutRows=null;
            if(segments==null||segments.Count<2)return;
            float pad=Mathf.Abs(stamp.meshCutOffset)+.001f;
            float min=segments.Min(p=>p.z)-pad,max=segments.Max(p=>p.z)+pad;
            int count=Math.Min(64,Math.Max(1,segments.Count/2));
            stamp.cutRowMin=min;stamp.cutRowStep=Mathf.Max(.001f,(max-min)/count);
            stamp.cutRows=new List<int>[count];
            for(int row=0;row<count;row++)stamp.cutRows[row]=new List<int>();
            for(int i=0;i+1<segments.Count;i+=2)
            {
                // Include every ray crossing and every segment within the offset.
                // Extra rows are conservative guards against float boundary rounding.
                int lo=Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(segments[i].z,segments[i+1].z)-pad-min)/stamp.cutRowStep)-1,0,count-1);
                int hi=Mathf.Clamp(Mathf.FloorToInt((Mathf.Max(segments[i].z,segments[i+1].z)+pad-min)/stamp.cutRowStep)+1,0,count-1);
                for(int row=lo;row<=hi;row++)stamp.cutRows[row].Add(i);
            }
        }
        static List<List<RockClipVertex>> OrderedBoundaryLoops(List<RockClipVertex> segments,string context="intersection")
        {
            var vertices=new List<RockClipVertex>();var ids=new Dictionary<Vector3Int,List<int>>();
            var adjacency=new Dictionary<int,List<int>>();var edges=new HashSet<long>(EdgeKeyComparer.Instance);
            Func<RockClipVertex,int> id=v=>
            {
                Vector3 p=v.position;var key=new Vector3Int(Mathf.RoundToInt(p.x*10000),Mathf.RoundToInt(p.y*10000),Mathf.RoundToInt(p.z*10000));
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                    if(ids.TryGetValue(key+new Vector3Int(x,y,z),out var nearby))
                        foreach(int found in nearby)if((vertices[found].position-p).sqrMagnitude<=1e-8f)return found;
                int added=vertices.Count;vertices.Add(v);
                if(!ids.TryGetValue(key,out var bucket))ids.Add(key,bucket=new List<int>());
                bucket.Add(added);adjacency.Add(added,new List<int>());return added;
            };
            for(int i=0;i+1<segments.Count;i+=2)
            {
                int a=id(segments[i]),b=id(segments[i+1]);if(a==b)continue;
                long key=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
                if(!edges.Add(key))continue;adjacency[a].Add(b);adjacency[b].Add(a);
            }
            var invalid=adjacency.Where(pair=>pair.Value.Count!=0&&pair.Value.Count!=2).ToArray();
            if(invalid.Length>0)
            {
                int open=invalid.Count(pair=>pair.Value.Count==1),branches=invalid.Length-open;
                var first=invalid[0];Vector3 p=vertices[first.Key].position;
                throw new InvalidOperationException($"{context}: contour has {open} open endpoints and {branches} branch points. First at ({p.x:R}, {p.y:R}, {p.z:R}), degree {first.Value.Count}. Check cut segments and source topology; final Weld Distance is applied later and cannot repair this contour.");
            }
            var result=new List<List<RockClipVertex>>();var visited=new HashSet<int>();
            foreach(var pair in adjacency)
            {
                if(pair.Value.Count==0||visited.Contains(pair.Key))continue;
                var loop=new List<RockClipVertex>();int first=pair.Key,current=first,previous=-1;
                do
                {
                    if(!visited.Add(current))throw new InvalidOperationException("Bridge contour crosses itself.");
                    loop.Add(vertices[current]);var next=adjacency[current];int target=next[0]==previous?next[1]:next[0];previous=current;current=target;
                }while(current!=first);
                if(loop.Count>=3)result.Add(loop);
            }
            return result;
        }
        static List<RockClipVertex> ClipRockTriangle(RockClipVertex a,RockClipVertex b,RockClipVertex c)
        {
            var input=new List<RockClipVertex>(4){a,b,c};var output=new List<RockClipVertex>(4);
            var previous=input[input.Count-1];bool previousInside=previous.distance>=0;
            foreach(var current in input)
            {
                bool currentInside=current.distance>=0;
                if(currentInside!=previousInside)
                {
                    // Always interpolate a shared edge in the same direction. Reversed
                    // triangle winding must not produce a different cut point.
                    var from=previous.distance<0?previous:current;
                    var to=previous.distance<0?current:previous;
                    double denominator=(double)from.distance-to.distance;
                    float t=(float)(from.distance/denominator);
                    output.Add(LerpRockVertex(from,to,t));
                }
                if(currentInside)output.Add(current);
                previous=current;previousInside=currentInside;
            }
            return output;
        }
        static void CollectRockBoundarySegment(List<RockClipVertex> polygon,List<RockClipVertex> segments)
        {
            var cut=new List<RockClipVertex>(2);
            foreach(var vertex in polygon)
                if(Mathf.Abs(vertex.distance)<.00001f&&cut.All(p=>(p.position-vertex.position).sqrMagnitude>.00000001f))cut.Add(vertex);
            if(cut.Count==2){segments.Add(cut[0]);segments.Add(cut[1]);}
        }
        static float ContactCutDistance(List<Vector3> segments,Vector3 point,float offset)
        {
            bool inside=false;float closest=float.MaxValue;var query=new Vector2(point.x,point.z);
            for(int i=0;i+1<segments.Count;i+=2)
            {
                var a=new Vector2(segments[i].x,segments[i].z);var b=new Vector2(segments[i+1].x,segments[i+1].z);var ab=b-a;
                float t=ab.sqrMagnitude>1e-12f?Mathf.Clamp01(Vector2.Dot(query-a,ab)/ab.sqrMagnitude):0;
                closest=Mathf.Min(closest,(query-a-t*ab).sqrMagnitude);
                if((a.y>query.y)!=(b.y>query.y)&&query.x<(b.x-a.x)*(query.y-a.y)/(b.y-a.y)+a.x)inside=!inside;
            }
            return (inside?-1:1)*Mathf.Sqrt(closest)-offset;
        }
        static List<RockClipVertex> SelectOwnedBoundaryLoops(List<List<RockClipVertex>> loops,int owner,Func<Vector3,int> ownerAt)
        {
            var result=new List<RockClipVertex>();
            foreach(var loop in loops)
            {
                var owners=new HashSet<int>();
                for(int i=0;i<loop.Count;i++)owners.Add(ownerAt((loop[i].position+loop[(i+1)%loop.Count].position)*.5f));
                if(!owners.Contains(owner))continue;
                if(owners.Count!=1)
                    throw new InvalidOperationException("Terrain cut contours merge between mesh stamps or reach the terrain boundary. Separate their cut regions (reduce Terrain Cut Offset or move the objects). Shared multi-object bridges are not yet supported; previous geometry is preserved.");
                for(int i=0;i<loop.Count;i++){result.Add(loop[i]);result.Add(loop[(i+1)%loop.Count]);}
            }
            return result;
        }
        sealed class ChunkBoundaryData
        {
            public Mesh mesh;public int dirtyVersion;public Matrix4x4 matrix;
            public Vector3[] points,normals;
            public List<(int count,int a,int b)> edges;
            public Dictionary<Vector3Int,List<int>> pointIndices;
            public ChunkBoundaryData WithNormals(Vector3[] localNormals,int version,Matrix4x4 normalMatrix)
            {
                var transformed=new Vector3[points.Length];
                for(int i=0;i<transformed.Length;i++)
                    transformed[i]=i<localNormals.Length?normalMatrix.MultiplyVector(localNormals[i]).normalized:Vector3.up;
                return new ChunkBoundaryData{mesh=mesh,dirtyVersion=version,matrix=matrix,
                    points=points,edges=edges,normals=transformed,pointIndices=pointIndices};
            }
        }
        static Vector3Int BoundaryPointKey(Vector3 p)=>new Vector3Int(Mathf.RoundToInt(p.x*1000),Mathf.RoundToInt(p.y*1000),Mathf.RoundToInt(p.z*1000));
        static (Vector3Int a,Vector3Int b) BoundaryEdgeKey(Vector3 a,Vector3 b)
        {
            var x=BoundaryPointKey(a);var y=BoundaryPointKey(b);
            bool swap=x.x>y.x||(x.x==y.x&&(x.y>y.y||(x.y==y.y&&x.z>y.z)));
            return swap?(y,x):(x,y);
        }
        sealed class BoundaryCache : Dictionary<int,ChunkBoundaryData>
        {
            public readonly Dictionary<(Vector3Int a,Vector3Int b),int> edgeTotals;
            public BoundaryCache(){edgeTotals=new Dictionary<(Vector3Int,Vector3Int),int>();}
            public BoundaryCache(BoundaryCache source):base(source)
            {edgeTotals=new Dictionary<(Vector3Int,Vector3Int),int>(source.edgeTotals);}
            public new void Clear(){base.Clear();edgeTotals.Clear();}
            void Contribute(ChunkBoundaryData data,int sign)
            {
                foreach(var edge in data.edges)
                {
                    var key=BoundaryEdgeKey(data.points[edge.a],data.points[edge.b]);
                    edgeTotals.TryGetValue(key,out int total);total+=sign*edge.count;
                    if(total<0)throw new InvalidOperationException("Negative cached boundary edge count.");
                    if(total==0)edgeTotals.Remove(key);else edgeTotals[key]=total;
                }
            }
            public void Replace(int id,ChunkBoundaryData data)
            {
                if(TryGetValue(id,out var old))Contribute(old,-1);
                Contribute(data,1);this[id]=data;
            }
            public void RemoveChunk(int id)
            {if(TryGetValue(id,out var old)){Contribute(old,-1);Remove(id);}}
        }
        sealed class EdgeKeyComparer : IEqualityComparer<long>
        {
            public static readonly EdgeKeyComparer Instance=new EdgeKeyComparer();
            public bool Equals(long a,long b)=>a==b;
            public int GetHashCode(long key)
            {
                // Packed adjacent vertex IDs collide heavily with the default
                // Int64 high/low XOR hash. Mix all bits; equality stays exact.
                unchecked
                {
                    ulong value=(ulong)key;
                    value^=value>>33;value*=0xff51afd7ed558ccdUL;
                    value^=value>>33;value*=0xc4ceb9fe1a85ec53UL;
                    value^=value>>33;
                    return (int)(value^(value>>32));
                }
            }
        }
        static ChunkBoundaryData BuildChunkBoundary(Vector3[] vertices,Vector3[] normals,int[] indices,Matrix4x4 matrix,Matrix4x4 normalMatrix)
        {
            var data=new ChunkBoundaryData{matrix=matrix,points=new Vector3[vertices.Length],normals=new Vector3[vertices.Length]};
            var ids=new int[vertices.Length];var positions=new Dictionary<Vector3Int,int>();
            data.pointIndices=new Dictionary<Vector3Int,List<int>>();
            for(int i=0;i<vertices.Length;i++)
            {
                var p=data.points[i]=matrix.MultiplyPoint3x4(vertices[i]);
                data.normals[i]=i<normals.Length?normalMatrix.MultiplyVector(normals[i]).normalized:Vector3.up;
                var key=new Vector3Int(Mathf.RoundToInt(p.x*1000),Mathf.RoundToInt(p.y*1000),Mathf.RoundToInt(p.z*1000));
                if(!data.pointIndices.TryGetValue(key,out var uses))data.pointIndices[key]=uses=new List<int>();
                uses.Add(i);
                if(!positions.TryGetValue(key,out int id))positions[key]=id=i;
                ids[i]=id;
            }
            var edges=new Dictionary<long,(int count,int a,int b)>(EdgeKeyComparer.Instance);
            for(int i=0;i+2<indices.Length;i+=3)for(int k=0;k<3;k++)
            {
                int a=ids[indices[i+k]],b=ids[indices[i+(k+1)%3]];long key=MeshEdge(a,b);
                edges.TryGetValue(key,out var use);edges[key]=(use.count+1,a,b);
            }
            // Keep counts > 1 too: another chunk must not turn an internal or
            // non-manifold edge into an apparent opening during global merging.
            data.edges=edges.Values.ToList();return data;
        }
        static Dictionary<int,List<RockClipVertex>> TerrainBoundarySegments(LTWorld world,List<Stamp> stamps,List<PendingMesh> pending,RockTimings parentTimings,BoundaryCache cache,Dictionary<Vector3Int,Vector3> seamNormals)
        {
            var detail=new RockTimings();detail.Start();
            int cacheHits=0,cacheMisses=0;
            var liveChunks=world.generatedRoot.GetComponentsInChildren<LTChunk>();
            var liveIds=new HashSet<int>(liveChunks.Select(c=>c.GetInstanceID()));
            foreach(int id in cache.Keys.Where(id=>!liveIds.Contains(id)).ToArray())cache.RemoveChunk(id);
            var points=new List<Vector3>();var pointIds=new Dictionary<Vector3Int,int>();
            var normalSums=new Dictionary<int,Vector3>();
            var boundaryData=new List<ChunkBoundaryData>();
            Func<Vector3,int> pointId=p=>
            {
                var key=new Vector3Int(Mathf.RoundToInt(p.x*1000),Mathf.RoundToInt(p.y*1000),Mathf.RoundToInt(p.z*1000));
                if(pointIds.TryGetValue(key,out int id))return id;
                id=points.Count;pointIds.Add(key,id);points.Add(p);return id;
            };
            foreach(var chunk in liveChunks)
            {
                detail.Lap("Boundary: setup / iteration");
                if(!chunk.mesh){cache.RemoveChunk(chunk.GetInstanceID());continue;}
                int chunkId=chunk.GetInstanceID();
                var replacement=pending?.FirstOrDefault(p=>p.chunk==chunk);
                Matrix4x4 toTerrain=world.transform.worldToLocalMatrix*chunk.transform.localToWorldMatrix;
                int version=EditorUtility.GetDirtyCount(chunk.mesh);
                bool hit=replacement==null&&cache.TryGetValue(chunkId,out var cached)&&cached.mesh==chunk.mesh&&
                    cached.dirtyVersion==version&&cached.matrix.Equals(toTerrain);
                ChunkBoundaryData data;
                if(hit){data=cache[chunkId];cacheHits++;}
                else
                {
                    cacheMisses++;
                    var vertices=replacement!=null?replacement.vertices:chunk.mesh.vertices;
                    var indices=replacement!=null?replacement.indices:chunk.mesh.triangles;
                    Vector3[] normals;
                    if(replacement!=null)
                    {
                        var temporary=new Mesh{indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};
                        try{temporary.vertices=vertices;temporary.triangles=indices;temporary.RecalculateNormals();normals=temporary.normals;}
                        finally{UnityEngine.Object.DestroyImmediate(temporary);}
                    }
                    else normals=chunk.mesh.normals;
                    data=BuildChunkBoundary(vertices,normals,indices,toTerrain,toTerrain.inverse.transpose);
                    data.mesh=chunk.mesh;data.dirtyVersion=version;
                    detail.Lap("Boundary: changed chunk data / cache lookup");
                    cache.Replace(chunkId,data);
                    detail.Lap("Boundary: subtract old + add new edge counts");
                }
                detail.Lap("Boundary: changed chunk data / cache lookup");
                boundaryData.Add(data);
            }
            var openEdges=cache.edgeTotals.Where(e=>e.Value==1)
                .OrderBy(e=>e.Key.a.x).ThenBy(e=>e.Key.a.y).ThenBy(e=>e.Key.a.z)
                .ThenBy(e=>e.Key.b.x).ThenBy(e=>e.Key.b.y).ThenBy(e=>e.Key.b.z).ToArray();
            var requiredPoints=new HashSet<Vector3Int>();
            foreach(var edge in openEdges){requiredPoints.Add(edge.Key.a);requiredPoints.Add(edge.Key.b);}
            detail.Lap("Boundary: select open edges");
            int mergedVertices=0;
            foreach(var data in boundaryData)foreach(var key in requiredPoints)
            {
                if(!data.pointIndices.TryGetValue(key,out var uses))continue;
                foreach(int i in uses)
                {
                    int id=pointId(data.points[i]);
                    normalSums[id]=normalSums.TryGetValue(id,out var existing)?existing+data.normals[i]:data.normals[i];
                    mergedVertices++;
                }
            }
            detail.Lap("Boundary: merge cached points + normals");
            parentTimings.Append($"Boundary points: {mergedVertices} merged / {boundaryData.Sum(d=>d.points.Length)} cached vertices");
            var result=new List<RockClipVertex>();
            // Stable contour starts regardless of cache insertion/removal history.
            foreach(var pair in openEdges)
            {
                if(pair.Value!=1)continue;
                int ia=pointIds[pair.Key.a],ib=pointIds[pair.Key.b];
                result.Add(new RockClipVertex{position=points[ia],normal=seamNormals.TryGetValue(pair.Key.a,out var na)?na:normalSums[ia].normalized});
                result.Add(new RockClipVertex{position=points[ib],normal=seamNormals.TryGetValue(pair.Key.b,out var nb)?nb:normalSums[ib].normalized});
            }
            // Identify whole closed contours, never crop individual edges by Bounds:
            // a neighbouring stamp's rim can cross the current stamp's Bounds.
            var cutters=stamps.Where(s=>s.meshStamp&&s.meshCutTerrain&&s.meshTrimRock&&s.contactSegments!=null&&s.contactSegments.Count>0).OrderBy(s=>s.id).ToArray();
            Func<Vector3,int> ownerAt=p=>
            {
                int owner=-1;float best=float.MaxValue;
                foreach(var cutter in cutters)
                {
                    if(!cutter.bounds.Contains(new Vector2(p.x,p.z)))continue;
                    float distance=Mathf.Abs(ContactCutDistance(cutter.contactSegments,p,cutter.meshCutOffset));
                    if(distance<best){best=distance;owner=cutter.id;}
                }
                return owner;
            };
            detail.Lap("Boundary: open-edge extraction + cutter setup");
            var loops=OrderedBoundaryLoops(result,"terrain boundaries");
            detail.Lap("Boundary: closed contour assembly");
            var boundaries=new Dictionary<int,List<RockClipVertex>>();
            // Classify once globally; preserve the same rejection of mixed-owner rims.
            foreach(var loop in loops)
            {
                var owners=new HashSet<int>();
                for(int i=0;i<loop.Count;i++)owners.Add(ownerAt((loop[i].position+loop[(i+1)%loop.Count].position)*.5f));
                if(owners.All(id=>id<0))continue;
                if(owners.Count!=1)
                    throw new InvalidOperationException("Terrain cut contours merge between mesh stamps or reach the terrain boundary. Separate their cut regions (reduce Terrain Cut Offset or move the objects). Shared multi-object bridges are not yet supported; previous geometry is preserved.");
                int owner=owners.First();
                if(!boundaries.TryGetValue(owner,out var segments))boundaries[owner]=segments=new List<RockClipVertex>();
                for(int i=0;i<loop.Count;i++){segments.Add(loop[i]);segments.Add(loop[(i+1)%loop.Count]);}
            }
            detail.Lap("Boundary: stamp ownership");
            parentTimings.Append("Boundary details (included in extraction):\n"+detail);
            parentTimings.Append($"Boundary cache: {cacheHits} reused / {cacheMisses} rebuilt chunks");
            parentTimings.children.Add(detail);
            return boundaries;
        }
        static float[] ContourParameters(List<RockClipVertex> loop)
        {
            var cumulative=new float[loop.Count+1];
            for(int i=0;i<loop.Count;i++)cumulative[i+1]=cumulative[i]+Vector3.Distance(loop[i].position,loop[(i+1)%loop.Count].position);
            float length=cumulative[loop.Count];
            if(length<.0001f)throw new InvalidOperationException("Degenerate bridge contour.");
            for(int i=1;i<cumulative.Length;i++)cumulative[i]/=length;
            return cumulative;
        }
        static RockClipVertex SampleContour(List<RockClipVertex> loop,float[] parameters,float u)
        {
            int index=Array.BinarySearch(parameters,u);
            if(index>=0)return loop[index%loop.Count];
            index=Mathf.Clamp(~index-1,0,loop.Count-1);
            return LerpRockVertex(loop[index],loop[(index+1)%loop.Count],(u-parameters[index])/Mathf.Max(1e-9f,parameters[index+1]-parameters[index]));
        }
        static float ContourArea(List<RockClipVertex> loop)
        {
            float area=0;
            for(int i=0;i<loop.Count;i++){Vector3 a=loop[i].position,b=loop[(i+1)%loop.Count].position;area+=a.x*b.z-b.x*a.z;}
            return area*.5f;
        }
        static long MeshEdge(int a,int b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
        static List<RockClipVertex> SampleRim(List<RockClipVertex> loop,float cell,int budget,out float[] parameters,Func<Vector3,float> cellAt=null)
        {
            var original=ContourParameters(loop);var result=new List<RockClipVertex>();var u=new List<float>();
            for(int i=0;i<loop.Count;i++)
            {
                float localCell=cellAt==null?cell:cellAt((loop[i].position+loop[(i+1)%loop.Count].position)*.5f);
                int steps=Math.Max(1,Mathf.CeilToInt(Vector3.Distance(loop[i].position,loop[(i+1)%loop.Count].position)/Mathf.Max(.00001f,localCell)));
                if((long)result.Count+steps>budget)throw new InvalidOperationException("Bridge rim exceeds the mesh budget.");
                for(int k=0;k<steps;k++)
                {
                    float t=k/(float)steps;result.Add(k==0?loop[i]:LerpRockVertex(loop[i],loop[(i+1)%loop.Count],t));
                    u.Add(Mathf.Lerp(original[i],original[i+1],t));
                }
            }
            u.Add(1);parameters=u.ToArray();return result;
        }
        static void ZipBridgeRings(int[] inner,float[] innerU,int[] outer,float[] outerU,Action<int,int,int> triangle)
        {
            int i=0,j=0;
            while(i<inner.Length||j<outer.Length)
            {
                int a=inner[i%inner.Length],c=outer[j%outer.Length];
                float ni=i<inner.Length?innerU[i+1]:float.PositiveInfinity;
                float nj=j<outer.Length?outerU[j+1]:float.PositiveInfinity;
                if(ni==nj)
                {
                    int b=inner[(i+1)%inner.Length],d=outer[(j+1)%outer.Length];
                    if(((i+j)&1)==0){triangle(a,b,c);triangle(c,b,d);}
                    else{triangle(a,b,d);triangle(a,d,c);}
                    i++;j++;
                }
                else if(ni<nj){triangle(a,inner[(i+1)%inner.Length],c);i++;}
                else{triangle(a,outer[(j+1)%outer.Length],c);j++;}
            }
        }
        static void BuildContourBridge(List<List<RockClipVertex>> rockLoops,List<List<RockClipVertex>> terrainLoops,
            float cell,int budget,Vector3 worldSize,Func<Vector3,Vector3,Vector4,Vector2,int> vertex,Action<int,int,int> triangle,
            HashSet<int> outerRim,HashSet<long> outerEdges,float blendDistance,Func<Vector3,float> cellAt=null)
        {
            if(rockLoops.Count!=terrainLoops.Count)
                throw new InvalidOperationException("Bridge contours merged or disappeared. The previous geometry is preserved; reduce Cut Offset or Retopo Height.");
            var unused=new List<List<RockClipVertex>>(terrainLoops);int total=0;
            foreach(var sourceLoop in rockLoops)
            {
                var rock=new List<RockClipVertex>(sourceLoop);
                if(ContourArea(rock)<0)rock.Reverse();
                Vector3 centre=Vector3.zero;foreach(var v in rock)centre+=v.position;centre/=rock.Count;
                var matched=unused.OrderBy(loop=>
                {
                    Vector3 c=Vector3.zero;foreach(var v in loop)c+=v.position;return (c/loop.Count-centre).sqrMagnitude;
                }).First();unused.Remove(matched);
                var terrain=new List<RockClipVertex>(matched);if(ContourArea(terrain)<0)terrain.Reverse();
                var rp=ContourParameters(rock);int phase=0;float best=float.MaxValue;
                int stride=Math.Max(1,terrain.Count/128);
                for(int candidate=0;candidate<terrain.Count;candidate+=stride)
                {
                    var rotated=terrain.Skip(candidate).Concat(terrain.Take(candidate)).ToList();var tp=ContourParameters(rotated);float error=0;
                    for(int k=0;k<32;k++)error+=(SampleContour(rock,rp,k/32f).position-SampleContour(rotated,tp,k/32f).position).sqrMagnitude;
                    if(error<best){best=error;phase=candidate;}
                }
                terrain=terrain.Skip(phase).Concat(terrain.Take(phase)).ToList();var tpFinal=ContourParameters(terrain);
                float rockLength=0,terrainLength=0;
                for(int i=0;i<rock.Count;i++)rockLength+=Vector3.Distance(rock[i].position,rock[(i+1)%rock.Count].position);
                for(int i=0;i<terrain.Count;i++)terrainLength+=Vector3.Distance(terrain[i].position,terrain[(i+1)%terrain.Count].position);
                int columns=Math.Max(3,Mathf.CeilToInt(Mathf.Max(rockLength,terrainLength)/cell));
                float[] interiorParameters=null;
                if(cellAt!=null)
                {
                    var sampling=new List<float>{0};float u=0;
                    while(u<1)
                    {
                        Vector3 p=(SampleContour(rock,rp,u).position+SampleContour(terrain,tpFinal,u).position)*.5f;
                        u+=Mathf.Max(.00001f,cellAt(p))/Mathf.Max(rockLength,terrainLength);
                        if(u<1)sampling.Add(u);
                        if(sampling.Count>budget)throw new InvalidOperationException("Union Bridge density exceeds the mesh budget.");
                    }
                    if(sampling.Count<3)sampling=new List<float>{0,1f/3,2f/3};
                    else for(int i=0;i<sampling.Count;i++)sampling[i]/=u;
                    sampling.Add(1);interiorParameters=sampling.ToArray();columns=sampling.Count-1;
                }
                if(columns>budget)throw new InvalidOperationException("Bridge density exceeds the mesh budget.");
                float width=0;
                for(int i=0;i<columns;i++)width=Mathf.Max(width,Vector3.Distance(SampleContour(rock,rp,i/(float)columns).position,SampleContour(terrain,tpFinal,i/(float)columns).position));
                int rows=Math.Max(2,Mathf.CeilToInt(width*1.5f/cell));
                var rockRim=SampleRim(rock,cell,budget,out var rockU,cellAt);
                var terrainRim=SampleRim(terrain,cell,budget,out var terrainU,cellAt);
                if((long)(rows-1)*columns+rockRim.Count+terrainRim.Count+total>budget)throw new InvalidOperationException("Bridge exceeds Max Vertices Per Chunk.");
                total+=(rows-1)*columns+rockRim.Count+terrainRim.Count;
                int[] previous=null;float[] previousU=null;
                // Interior rows are evenly sampled. Rim corners are retained only on
                // their own boundary and zipped to the next row, never propagated as
                // entire nearly coincident strips across the patch.
                for(int row=0;row<=rows;row++)
                {
                    int count=row==0?rockRim.Count:row==rows?terrainRim.Count:columns;
                    var ring=new int[count];float[] parameters;
                    if(row==0)parameters=rockU;
                    else if(row==rows)parameters=terrainU;
                    else if(interiorParameters!=null)parameters=interiorParameters;
                    else{parameters=new float[count+1];for(int i=0;i<=count;i++)parameters[i]=i/(float)count;}
                    float t=row/(float)rows;
                    for(int i=0;i<count;i++)
                    {
                        float u=parameters[i];var a=SampleContour(rock,rp,u);var b=SampleContour(terrain,tpFinal,u);
                        Vector3 delta=b.position-a.position;float bend=Mathf.Min(delta.magnitude,Mathf.Max(0,blendDistance));
                        Vector3 ta=Vector3.ProjectOnPlane(delta,a.normal).normalized*bend;
                        Vector3 tb=Vector3.ProjectOnPlane(delta,b.normal).normalized*bend;
                        // Blend between a straight bridge and bounded Hermite tangents.
                        // Density and row count do not depend on Blend Distance.
                        float smooth=Mathf.Clamp01(blendDistance/Mathf.Max(.001f,delta.magnitude));
                        Vector3 m0=Vector3.Lerp(delta,ta,smooth),m1=Vector3.Lerp(delta,tb,smooth);
                        float t2=t*t,t3=t2*t;
                        Vector3 position=(2*t3-3*t2+1)*a.position+(t3-2*t2+t)*m0+(-2*t3+3*t2)*b.position+(t3-t2)*m1;
                        if(row==0)position=rockRim[i].position;
                        else if(row==rows)position=terrainRim[i].position;
                        Vector3 normal=Vector3.Lerp(a.normal,b.normal,t*t*(3-2*t)).normalized;
                        Vector3 tangent=Vector3.ProjectOnPlane(Vector3.right,normal).normalized;
                        if(tangent.sqrMagnitude<.01f)tangent=Vector3.ProjectOnPlane(Vector3.forward,normal).normalized;
                        Vector2 uv=Vector2.Lerp(a.uv,new Vector2(position.x/worldSize.x,position.z/worldSize.z),t*t*(3-2*t));
                        ring[i]=vertex(position,normal,row==0?a.tangent:new Vector4(tangent.x,tangent.y,tangent.z,1),uv);
                        if(row==rows)outerRim.Add(ring[i]);
                    }
                    if(previous!=null)ZipBridgeRings(previous,previousU,ring,parameters,triangle);
                    if(row==rows)for(int i=0;i<count;i++)outerEdges.Add(MeshEdge(ring[i],ring[(i+1)%count]));
                    previous=ring;previousU=parameters;
                }
            }
        }
        static void StitchRockRim(List<Vector3> positions,List<Vector3> normals,List<Vector4> tangents,List<Vector2> uv,
            List<int>[] submeshes,int sourceSubmeshes,int originalCount,List<RockClipVertex> rim)
        {
            const float rimTolerance=.0001f;
            Func<Vector3,Vector3Int> key=p=>new Vector3Int(Mathf.RoundToInt(p.x/rimTolerance),Mathf.RoundToInt(p.y/rimTolerance),Mathf.RoundToInt(p.z/rimTolerance));
            var bridgeIds=new Dictionary<Vector3Int,int>();
            for(int i=originalCount;i<positions.Count;i++)bridgeIds[key(positions[i])]=i;
            var splitEdges=new Dictionary<(Vector3Int,Vector3Int),List<int>>();var rimKeys=new HashSet<Vector3Int>();
            for(int i=0;i+1<rim.Count;i+=2)
            {
                Vector3 a=rim[i].position,b=rim[i+1].position,ab=b-a;float length=ab.sqrMagnitude;
                if(length<1e-14f)continue;
                var ka=key(a);var kb=key(b);rimKeys.Add(ka);rimKeys.Add(kb);
                var candidates=new List<(float t,int id)>();
                for(int j=originalCount;j<positions.Count;j++)
                {
                    float t=Vector3.Dot(positions[j]-a,ab)/length;
                    if(t<=0||t>=1||(positions[j]-a).sqrMagnitude<rimTolerance*rimTolerance||(positions[j]-b).sqrMagnitude<rimTolerance*rimTolerance)continue;
                    if((positions[j]-(a+t*ab)).sqrMagnitude<rimTolerance*rimTolerance)candidates.Add((t,j));
                }
                var ordered=candidates.OrderBy(c=>c.t).Select(c=>c.id).ToList();
                splitEdges[(ka,kb)]=ordered;splitEdges[(kb,ka)]=ordered.AsEnumerable().Reverse().ToList();
            }
            Func<int,int> weld=i=>
            {
                var k=key(positions[i]);if(!rimKeys.Contains(k))return i;
                // Source -> terrain -> source transforms can land on opposite sides
                // of a quantization bin even when the rim positions coincide.
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                    if(bridgeIds.TryGetValue(k+new Vector3Int(x,y,z),out int bridge)&&(positions[bridge]-positions[i]).sqrMagnitude<rimTolerance*rimTolerance)return bridge;
                throw new InvalidOperationException("Bridge rim vertex could not be welded to the source rock.");
            };
            for(int sub=0;sub<sourceSubmeshes;sub++)
            {
                var input=submeshes[sub];var output=new List<int>();
                for(int i=0;i+2<input.Count;i+=3)
                {
                    var polygon=new List<int>();bool split=false;
                    for(int k=0;k<3;k++)
                    {
                        int a=input[i+k],b=input[i+(k+1)%3];polygon.Add(weld(a));
                        if(splitEdges.TryGetValue((key(positions[a]),key(positions[b])),out var extra)&&extra.Count>0)
                        {polygon.AddRange(extra);split=true;}
                    }
                    if(!split){output.AddRange(polygon);continue;}
                    // Only the source triangles touched by the new rim are retriangulated.
                    // The bridge and rock now reference the same rim vertex indices.
                    int centre=positions.Count;int ia=input[i],ib=input[i+1],ic=input[i+2];
                    positions.Add((positions[ia]+positions[ib]+positions[ic])/3);
                    normals.Add((normals[ia]+normals[ib]+normals[ic]).normalized);
                    tangents.Add(tangents[ia]);uv.Add((uv[ia]+uv[ib]+uv[ic])/3);
                    for(int k=0;k<polygon.Count;k++)
                    {int a=polygon[k],b=polygon[(k+1)%polygon.Count];if(a==b)continue;output.Add(centre);output.Add(a);output.Add(b);}
                }
                submeshes[sub]=output;
            }
        }
        static void OrientConnectedFaces(List<int>[] submeshes)
        {
            var faces=new List<(int sub,int offset)>();var edges=new Dictionary<long,List<(int face,bool direction)>>(EdgeKeyComparer.Instance);
            for(int sub=0;sub<submeshes.Length;sub++)for(int i=0;i+2<submeshes[sub].Count;i+=3)
            {
                int face=faces.Count;faces.Add((sub,i));
                for(int k=0;k<3;k++)
                {
                    int a=submeshes[sub][i+k],b=submeshes[sub][i+(k+1)%3];long key=MeshEdge(a,b);
                    if(!edges.TryGetValue(key,out var incident))edges.Add(key,incident=new List<(int,bool)>());
                    incident.Add((face,a<b));
                    if(incident.Count>2)throw new InvalidOperationException("Non-manifold bridge edge; previous geometry preserved.");
                }
            }
            var visited=new bool[faces.Count];var flipped=new bool[faces.Count];var queue=new Queue<int>();
            for(int seed=0;seed<faces.Count;seed++)
            {
                if(visited[seed])continue;visited[seed]=true;queue.Enqueue(seed);
                while(queue.Count>0)
                {
                    int f=queue.Dequeue();var face=faces[f];var indices=submeshes[face.sub];
                    for(int k=0;k<3;k++)
                    {
                        int a=indices[face.offset+k],b=indices[face.offset+(k+1)%3];
                        foreach(var neighbor in edges[MeshEdge(a,b)])
                        {
                            if(neighbor.face==f)continue;
                            bool flip=flipped[f]^((a<b)==neighbor.direction);
                            if(visited[neighbor.face])
                            {
                                if(flipped[neighbor.face]!=flip)throw new InvalidOperationException("Inconsistent bridge orientation; previous geometry preserved.");
                            }
                            else{visited[neighbor.face]=true;flipped[neighbor.face]=flip;queue.Enqueue(neighbor.face);}
                        }
                    }
                }
            }
            for(int i=0;i<faces.Count;i++)if(flipped[i])
            {var f=faces[i];var indices=submeshes[f.sub];int swap=indices[f.offset+1];indices[f.offset+1]=indices[f.offset+2];indices[f.offset+2]=swap;}
        }
        static void WeldGeneratedTopology(List<Vector3> positions,List<Vector3> normals,List<Vector4> tangents,List<Vector2> uv,
            List<int>[] submeshes,Matrix4x4 toTerrain,int bridgeStart,int bridgeEnd,float weldDistance,HashSet<int> outerRim,HashSet<long> outerEdges=null)
        {
            int count=positions.Count;if(count==0)return;
            var parent=Enumerable.Range(0,count).ToArray();
            Func<int,int> root=null;root=i=>parent[i]==i?i:parent[i]=root(parent[i]);
            var terrainPositions=new Vector3[count];
            for(int i=0;i<count;i++)terrainPositions[i]=toTerrain.MultiplyPoint3x4(positions[i]);
            var protectedRoots=new HashSet<int>(outerRim);
            Action<int,int> join=(a,b)=>
            {
                a=root(a);b=root(b);if(a==b)return;
                bool aProtected=protectedRoots.Contains(a),bProtected=protectedRoots.Contains(b);
                if(aProtected&&bProtected&&a!=b)return;
                bool aBridge=a>=bridgeStart&&a<bridgeEnd,bBridge=b>=bridgeStart&&b<bridgeEnd;
                if(aProtected&&!bProtected)parent[b]=a;
                else if(bProtected&&!aProtected)parent[a]=b;
                else if(aBridge&&!bBridge)parent[a]=b;
                else if(bBridge&&!aBridge)parent[b]=a;
                else if(a<b)parent[b]=a;else parent[a]=b;
            };
            Func<int,int,long> edgeKey=(a,b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
            Func<Dictionary<long,int>> edgeCounts=()=>
            {
                var result=new Dictionary<long,int>(EdgeKeyComparer.Instance);
                foreach(var triangles in submeshes)for(int i=0;i+2<triangles.Count;i+=3)
                {
                    int a=root(triangles[i]),b=root(triangles[i+1]),c=root(triangles[i+2]);
                    if(a==b||b==c||a==c)continue;
                    foreach(long key in new[]{edgeKey(a,b),edgeKey(b,c),edgeKey(c,a)})
                        result[key]=result.TryGetValue(key,out int value)?value+1:1;
                }
                return result;
            };
            // Weld exact per-triangle copies first. Neighbour buckets are checked too,
            // so equal points on opposite sides of a quantization cell still connect.
            const float exact=.0001f;var buckets=new Dictionary<Vector3Int,List<int>>();
            for(int i=0;i<count;i++)
            {
                Vector3 p=terrainPositions[i];var key=new Vector3Int(Mathf.FloorToInt(p.x/exact),Mathf.FloorToInt(p.y/exact),Mathf.FloorToInt(p.z/exact));
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                    if(buckets.TryGetValue(key+new Vector3Int(x,y,z),out var candidates))
                        foreach(int other in candidates)if((terrainPositions[other]-p).sqrMagnitude<=exact*exact)join(i,other);
                if(!buckets.TryGetValue(key,out var own))buckets.Add(key,own=new List<int>());own.Add(i);
            }
            protectedRoots=new HashSet<int>(outerRim.Select(root));
            // Any open edge away from the terrain rim is a crack. Pair its vertices
            // with the nearest non-neighbouring open vertex inside Weld Distance.
            float weld=Mathf.Max(0,weldDistance),weldSq=weld*weld;
            for(int iteration=0;iteration<8&&weld>0;iteration++)
            {
                var edges=edgeCounts();var boundaryEdges=edges.Where(p=>p.Value==1).Select(p=>p.Key).ToArray();
                var boundaryVertices=new HashSet<int>();
                var connected=new HashSet<long>(EdgeKeyComparer.Instance);
                foreach(long key in boundaryEdges)
                {
                    int a=root((int)(key>>32)),b=root((int)(key&0xffffffff));boundaryVertices.Add(a);boundaryVertices.Add(b);connected.Add(edgeKey(a,b));
                }
                var loose=boundaryVertices.Where(v=>!protectedRoots.Contains(root(v))).ToArray();
                if(loose.Length==0)break;
                bool changed=false;var reserved=new HashSet<int>();
                foreach(int source in loose)
                {
                    int a=root(source);if(protectedRoots.Contains(a)||reserved.Contains(a))continue;
                    int nearest=-1;float best=weldSq;
                    foreach(int candidate in boundaryVertices)
                    {
                        int b=root(candidate);if(a==b||reserved.Contains(b)||connected.Contains(edgeKey(a,b)))continue;
                        float distance=(terrainPositions[a]-terrainPositions[b]).sqrMagnitude;
                        if(distance<best){best=distance;nearest=b;}
                    }
                    if(nearest<0)continue;
                    bool targetProtected=protectedRoots.Contains(nearest);
                    join(a,nearest);int representative=root(a);
                    if(targetProtected)protectedRoots.Add(representative);
                    reserved.Add(representative);changed=true;
                }
                protectedRoots=new HashSet<int>(protectedRoots.Select(root));
                if(!changed)break;
            }
            // Density and rim stitching are finished. Collapse short connected edges too,
            // not just disconnected crack vertices. Keep the terrain rim fixed so the
            // independently rendered terrain still has exactly the same boundary.
            if(weld>0)
            {
                var faces=new List<int[]>();var incident=new Dictionary<int,HashSet<int>>();
                foreach(var triangles in submeshes)for(int i=0;i+2<triangles.Count;i+=3)
                {
                    int face=faces.Count;var ids=new[]{triangles[i],triangles[i+1],triangles[i+2]};faces.Add(ids);
                    foreach(int id in ids)
                    {
                        int r=root(id);if(!incident.TryGetValue(r,out var set))incident[r]=set=new HashSet<int>();set.Add(face);
                    }
                }
                var candidates=edgeCounts().Keys.Select(key=>((int)(key>>32),(int)(key&0xffffffff)))
                    .Where(e=>(terrainPositions[e.Item1]-terrainPositions[e.Item2]).sqrMagnitude<=weldSq)
                    .OrderBy(e=>(terrainPositions[e.Item1]-terrainPositions[e.Item2]).sqrMagnitude)
                    .ThenBy(e=>e.Item1).ThenBy(e=>e.Item2).ToArray();
                foreach(var candidate in candidates)
                {
                    int a=root(candidate.Item1),b=root(candidate.Item2);
                    if(a==b||(terrainPositions[a]-terrainPositions[b]).sqrMagnitude>weldSq)continue;
                    bool movableA=a>=bridgeStart&&a<bridgeEnd&&!protectedRoots.Contains(a);
                    bool movableB=b>=bridgeStart&&b<bridgeEnd&&!protectedRoots.Contains(b);
                    if(!movableA&&!movableB)continue;
                    int remove=movableA?a:b,keep=remove==a?b:a;
                    var neighboursA=new HashSet<int>();var neighboursB=new HashSet<int>();
                    var opposite=new HashSet<int>();bool safe=true;int shared=0;
                    var affected=new HashSet<int>(incident[remove]);affected.UnionWith(incident[keep]);
                    foreach(int face in affected)
                    {
                            var triangle=faces[face];
                            int x=root(triangle[0]),y=root(triangle[1]),z=root(triangle[2]);
                            if(x==y||y==z||z==x)continue;
                            bool hasA=x==remove||y==remove||z==remove;
                            bool hasB=x==keep||y==keep||z==keep;
                            if(hasA){neighboursA.Add(x);neighboursA.Add(y);neighboursA.Add(z);}
                            if(hasB){neighboursB.Add(x);neighboursB.Add(y);neighboursB.Add(z);}
                            if(hasA&&hasB)
                            {
                                shared++;opposite.Add(x!=remove&&x!=keep?x:y!=remove&&y!=keep?y:z);continue;
                            }
                            if(!hasA)continue;
                            Vector3 oldNormal=Vector3.Cross(terrainPositions[y]-terrainPositions[x],terrainPositions[z]-terrainPositions[x]);
                            int nx=x==remove?keep:x,ny=y==remove?keep:y,nz=z==remove?keep:z;
                            Vector3 newNormal=Vector3.Cross(terrainPositions[ny]-terrainPositions[nx],terrainPositions[nz]-terrainPositions[nx]);
                            if(newNormal.sqrMagnitude<1e-14f||Vector3.Dot(oldNormal,newNormal)<=0){safe=false;break;}
                    }
                    // The edge-collapse link condition prevents pinched surfaces and
                    // duplicate faces. Boundary edges are deliberately not collapsed.
                    neighboursA.Remove(remove);neighboursA.Remove(keep);
                    neighboursB.Remove(remove);neighboursB.Remove(keep);
                    neighboursA.IntersectWith(neighboursB);
                    if(!safe||shared!=2||!neighboursA.SetEquals(opposite)||opposite.Count!=2)continue;
                    parent[remove]=keep;
                    incident[keep]=affected;incident.Remove(remove);
                }
            }
            var uniqueTriangles=new HashSet<(int,int,int)>();
            for(int sub=0;sub<submeshes.Length;sub++)
            {
                var input=submeshes[sub];var output=new List<int>(input.Count);
                for(int i=0;i+2<input.Count;i+=3)
                {
                    int a=root(input[i]),b=root(input[i+1]),c=root(input[i+2]);
                    if(a==b||b==c||a==c)continue;
                    if(Vector3.Cross(terrainPositions[b]-terrainPositions[a],terrainPositions[c]-terrainPositions[a]).sqrMagnitude<1e-14f)continue;
                    int lo=Math.Min(a,Math.Min(b,c)),hi=Math.Max(a,Math.Max(b,c)),mid=a+b+c-lo-hi;
                    if(!uniqueTriangles.Add((lo,mid,hi)))continue;
                    output.Add(a);output.Add(b);output.Add(c);
                }
                submeshes[sub]=output;
            }
            protectedRoots=new HashSet<int>(protectedRoots.Select(root));
            OrientConnectedFaces(submeshes);
            var finalEdges=edgeCounts();
            var actualOuter=new HashSet<long>(EdgeKeyComparer.Instance);
            foreach(var pair in finalEdges)
            {
                if(pair.Value>2)throw new InvalidOperationException("Weld Distance creates a non-manifold bridge. Reduce Weld Distance.");
                if(pair.Value!=1)continue;
                int a=root((int)(pair.Key>>32)),b=root((int)(pair.Key&0xffffffff));
                if(!protectedRoots.Contains(a)||!protectedRoots.Contains(b))
                    throw new InvalidOperationException("Bridge still contains an open crack. Increase Weld Distance or Grid Density.");
                actualOuter.Add(edgeKey(a,b));
            }
            if(outerEdges!=null)
            {
                var expected=new HashSet<long>(outerEdges.Select(e=>edgeKey(root((int)(e>>32)),root((int)(e&0xffffffff)))),EdgeKeyComparer.Instance);
                if(!actualOuter.SetEquals(expected))throw new InvalidOperationException("Bridge no longer matches the complete terrain rim; previous geometry preserved.");
            }
            // Compact after remapping; unused isolated points disappear here.
            var used=new bool[count];foreach(var triangles in submeshes)foreach(int index in triangles)used[root(index)]=true;
            var remap=Enumerable.Repeat(-1,count).ToArray();var newPositions=new List<Vector3>();var newNormals=new List<Vector3>();
            var newTangents=new List<Vector4>();var newUv=new List<Vector2>();
            for(int i=0;i<count;i++)
            {
                int representative=root(i);if(representative!=i||!used[i])continue;
                remap[i]=newPositions.Count;newPositions.Add(positions[i]);newNormals.Add(normals[i]);newTangents.Add(tangents[i]);newUv.Add(uv[i]);
            }
            for(int sub=0;sub<submeshes.Length;sub++)for(int i=0;i<submeshes[sub].Count;i++)submeshes[sub][i]=remap[root(submeshes[sub][i])];
            positions.Clear();positions.AddRange(newPositions);normals.Clear();normals.AddRange(newNormals);
            tangents.Clear();tangents.AddRange(newTangents);uv.Clear();uv.AddRange(newUv);
        }
        static void RestoreSourceRock(LTMeshStamp component)
        {
            if(!component)return;
            var filter=component.GetComponent<MeshFilter>();
            if(filter&&component.sourceMesh)filter.sharedMesh=component.sourceMesh;
            var renderer=component.GetComponent<MeshRenderer>();
            if(renderer&&component.objectMaterials!=null&&component.objectMaterials.Length>0)
                renderer.sharedMaterials=component.objectMaterials;
            else if(renderer&&component.sourceMesh&&renderer.sharedMaterials.Length>component.sourceMesh.subMeshCount)
                renderer.sharedMaterials=renderer.sharedMaterials.Take(component.sourceMesh.subMeshCount).ToArray();
            component.terrainMaterialApplied=false;component.unionGroupOutputApplied=false;component.geometryIssueEdges=null;
            if(component.generatedTrimmedMesh&&component.generatedMeshOwner==component.GetInstanceID())
                UnityEngine.Object.DestroyImmediate(component.generatedTrimmedMesh);
            component.generatedTrimmedMesh=null;
            component.generatedMeshOwner=0;
        }
        static Mesh GetOwnedOutputMesh(LTMeshStamp component,Mesh source)
        {
            if(component.generatedTrimmedMesh&&component.generatedMeshOwner==component.GetInstanceID())
                return component.generatedTrimmedMesh;
            // Also handles copied nonserialized references and domain reloads.
            var generated=new Mesh{name=source.name+"_TerrainTrim",hideFlags=HideFlags.HideAndDontSave};
            component.generatedTrimmedMesh=generated;
            component.generatedMeshOwner=component.GetInstanceID();
            return generated;
        }
        [MenuItem("Tools/Local Terrain/Validate Mesh Instance Ownership")]
        public static void ValidateMeshInstanceOwnership()
        {
            GameObject first=null,second=null;var allocated=new HashSet<Mesh>();
            try
            {
                var source=new Mesh{name="Ownership test source",hideFlags=HideFlags.HideAndDontSave};allocated.Add(source);
                source.vertices=new[]{Vector3.zero,Vector3.right,Vector3.forward};source.triangles=new[]{0,2,1};
                first=new GameObject("Ownership test A"){hideFlags=HideFlags.HideAndDontSave};
                var a=first.AddComponent<LTMeshStamp>();a.sourceMesh=source;
                var outputA=GetOwnedOutputMesh(a,source);allocated.Add(outputA);
                outputA.vertices=source.vertices;outputA.triangles=source.triangles;
                a.GetComponent<MeshFilter>().sharedMesh=outputA;
                second=UnityEngine.Object.Instantiate(first);second.hideFlags=HideFlags.HideAndDontSave;
                var b=second.GetComponent<LTMeshStamp>();
                // Exercise both shared MeshFilter and copied managed preview references.
                b.generatedTrimmedMesh=outputA;b.generatedMeshOwner=a.generatedMeshOwner;
                var outputB=GetOwnedOutputMesh(b,source);allocated.Add(outputB);
                if(outputA==outputB||b.sourceMesh!=source)throw new InvalidOperationException("Duplicated stamp shares generated output or loses its source.");
                outputB.Clear();
                if(outputA.vertexCount!=3||source.vertexCount!=3)throw new InvalidOperationException("Writing B changed A or the source.");
                if(GetOwnedOutputMesh(a,source)!=outputA)throw new InvalidOperationException("Owner cannot reuse its own mesh.");
                b.GetComponent<MeshFilter>().sharedMesh=outputB;RestoreSourceRock(b);
                if(!outputA||outputA.vertexCount!=3||b.GetComponent<MeshFilter>().sharedMesh!=source)
                    throw new InvalidOperationException("Restoring B invalidates A.");
                a.generatedMeshOwner=0;
                var reloaded=GetOwnedOutputMesh(a,source);allocated.Add(reloaded);
                if(reloaded==outputA)throw new InvalidOperationException("Unowned mesh was adopted after reload.");
                Debug.Log("PASS mesh instance ownership: duplicate, independent write, source preservation, restore and reload.");
            }
            finally
            {
                if(second)UnityEngine.Object.DestroyImmediate(second);
                if(first)UnityEngine.Object.DestroyImmediate(first);
                foreach(var mesh in allocated)if(mesh)UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
        // Scoped to one synchronous rebuild: never reused after a stamp/terrain edit.
        sealed class PreparedRock
        {
            public Stamp stamp;
            public bool restore,hasContact;
            public Matrix4x4 matrix;
            public List<Vector3> vertices;
            public List<Vector2> uv;
            public List<int>[] triangles;
            public List<RockClipVertex> terrainBoundary;
        }
        sealed class RockOutputCache
        {
            public string signature,config;
            public int authoring,sourceVersion,outputVersion;
            public Mesh source,output;
        }
        static bool CanReuseRock(LTWorld world,Stamp stamp,List<PendingMesh> pending,HashSet<int> changedSurface,Dictionary<int,RockOutputCache> cache)
        {
            if(cache==null||!cache.TryGetValue(stamp.id,out var old))return false;
            var component=stamp.meshComponent;var filter=component.GetComponent<MeshFilter>();
            if(!filter||!old.output||old.output!=component.generatedTrimmedMesh||filter.sharedMesh!=old.output||
                component.generatedMeshOwner!=component.GetInstanceID()||old.source!=stamp.meshSource||
                old.sourceVersion!=EditorUtility.GetDirtyCount(stamp.meshSource)||old.outputVersion!=EditorUtility.GetDirtyCount(old.output)||
                old.signature!=stamp.signature||old.config!=Config(world)||
                old.authoring!=world.displacementGeometry.RegionSignature(stamp.bounds,0))return false;
            // Include neighbouring geometry: shared normals at a rock contact can
            // change even when its own chunk retained the same topology.
            var region=Expanded(stamp.bounds,world.source.size.x/world.chunksX,world.source.size.z/world.chunksZ);
            return !pending.Any(p=>Overlap(region,ChunkRect(world,p.id)))&&!changedSurface.Any(id=>Overlap(region,ChunkRect(world,id)));
        }
        sealed class RockTimings
        {
            public void Add(string name,double milliseconds)
            {
                totals.TryGetValue(name,out double total);totals[name]=total+milliseconds;
            }
            public readonly List<RockTimings> children=new List<RockTimings>();
            public void CopyTo(Dictionary<string,double> output)
            {
                foreach(var p in totals)output[p.Key]=p.Value;
                foreach(var child in children)child.CopyTo(output);
            }
            readonly Stopwatch timer=new Stopwatch();
            readonly Dictionary<string,double> totals=new Dictionary<string,double>();
            readonly List<string> reports=new List<string>();
            public void Append(string report)=>reports.Add(report);
            public void Start()=>timer.Restart();
            public void Pause()=>timer.Stop();
            public void Resume()=>timer.Start();
            public void Lap(string name)
            {
                double elapsed=timer.Elapsed.TotalMilliseconds;
                totals.TryGetValue(name,out double total);totals[name]=total+elapsed;
                timer.Restart();
            }
            public override string ToString()=>string.Join("\n",totals.Select(p=>p.Key+": "+p.Value.ToString("F1")+" ms"))+(reports.Count>0?"\n\n"+string.Join("\n",reports):"");
        }
        static List<PreparedRock> PrepareTrimmedRockMeshes(LTWorld world,List<Stamp> stamps,List<PendingMesh> pending,RockTimings timings,BoundaryCache boundaryCache,Dictionary<Vector3Int,Vector3> seamNormals,HashSet<int> changedSurface,Dictionary<int,RockOutputCache> reusable=null,HashSet<int> allowedChunks=null)
        {
            var prepared=new List<PreparedRock>();
            Dictionary<int,List<RockClipVertex>> boundaries=null;
            foreach(var stamp in stamps)
            {
                if(!stamp.meshStamp||!stamp.meshComponent||!stamp.meshSource)continue;
                if(allowedChunks!=null)
                {
                    var influence=Expanded(stamp.bounds,world.source.size.x/world.chunksX,world.source.size.z/world.chunksZ);
                    if(!allowedChunks.Any(id=>Overlap(influence,ChunkRect(world,id))))continue;
                }
                if(CanReuseRock(world,stamp,pending,changedSurface,reusable)){timings.Append("Reused unchanged rock: "+stamp.meshComponent.name);continue;}
                bool cave=stamp.meshMode==LTMeshConformMode.Cave;
                var component=stamp.meshComponent;var filter=component.GetComponent<MeshFilter>();
                if(!filter)continue;
                if(!stamp.meshTrimRock){prepared.Add(new PreparedRock{stamp=stamp,restore=true});continue;}
                if(stamp.meshCutTerrain&&(stamp.contactSegments==null||stamp.contactSegments.Count==0))
                {
                    if(cave)throw new InvalidOperationException("Cave cutter must intersect the terrain to create an entrance: "+component.name);
                    prepared.Add(new PreparedRock{stamp=stamp,restore=true});continue;
                }
                timings.Start();
                var source=stamp.meshSource;var sourceVertices=source.vertices;
                var sourceNormals=source.normals;var sourceTangents=source.tangents;var sourceUv=source.uv;
                bool hasNormals=sourceNormals!=null&&sourceNormals.Length==sourceVertices.Length;
                if(!hasNormals)
                {
                    sourceNormals=new Vector3[sourceVertices.Length];var sourceIndices=source.triangles;
                    for(int i=0;i+2<sourceIndices.Length;i+=3)
                    {
                        int a=sourceIndices[i],b=sourceIndices[i+1],c=sourceIndices[i+2];
                        Vector3 normal=Vector3.Cross(sourceVertices[b]-sourceVertices[a],sourceVertices[c]-sourceVertices[a]);
                        sourceNormals[a]+=normal;sourceNormals[b]+=normal;sourceNormals[c]+=normal;
                    }
                    for(int i=0;i<sourceNormals.Length;i++)sourceNormals[i]=sourceNormals[i].normalized;
                    hasNormals=true;
                }
                bool hasTangents=sourceTangents!=null&&sourceTangents.Length==sourceVertices.Length;
                bool hasUv=sourceUv!=null&&sourceUv.Length==sourceVertices.Length;
                var matrix=world.transform.worldToLocalMatrix*component.transform.localToWorldMatrix;
                var heightCache=new Dictionary<Vector2,float>();
                Func<RockClipVertex,RockClipVertex> sample=vertex=>
                {
                    Vector3 terrainPoint=matrix.MultiplyPoint3x4(vertex.position);
                    var key=new Vector2(terrainPoint.x,terrainPoint.z);
                    if(!heightCache.TryGetValue(key,out float terrainHeight))
                        heightCache[key]=terrainHeight=Evaluate(world,stamps,terrainPoint.x,terrainPoint.z,false);
                    float band=stamp.meshCutTerrain?stamp.meshRetopoHeight:0;
                    vertex.distance=cave?terrainHeight-band-terrainPoint.y:terrainPoint.y-terrainHeight-band;
                    if(cave){vertex.normal=-vertex.normal;vertex.tangent.w=-vertex.tangent.w;}
                    return vertex;
                };
                var clipVertices=new RockClipVertex[sourceVertices.Length];
                for(int i=0;i<sourceVertices.Length;i++)
                {
                    clipVertices[i]=sample(new RockClipVertex{position=sourceVertices[i],normal=hasNormals?sourceNormals[i]:Vector3.up,
                        tangent=hasTangents?sourceTangents[i]:new Vector4(1,0,0,1),uv=hasUv?sourceUv[i]:Vector2.zero,
                        });
                }
                var resultVertices=new List<Vector3>();var resultNormals=new List<Vector3>();
                var resultTangents=new List<Vector4>();var resultUv=new List<Vector2>();
                var rockBoundarySegments=new List<RockClipVertex>();
                var submeshTriangles=new List<int>[source.subMeshCount];
                for(int submesh=0;submesh<source.subMeshCount;submesh++)
                {
                    var resultTriangles=submeshTriangles[submesh]=new List<int>();var sourceIndices=source.GetTriangles(submesh);
                    for(int i=0;i+2<sourceIndices.Length;i+=3)
                    {
                        var polygon=ClipRockTriangle(clipVertices[sourceIndices[i]],clipVertices[sourceIndices[i+1]],clipVertices[sourceIndices[i+2]]);
                        if(polygon.Count<3)continue;
                        CollectRockBoundarySegment(polygon,rockBoundarySegments);
                        int first=resultVertices.Count;
                        foreach(var vertex in polygon)
                        {
                            resultVertices.Add(vertex.position);resultNormals.Add(vertex.normal);
                            resultTangents.Add(vertex.tangent);resultUv.Add(vertex.uv);
                        }
                        for(int j=1;j+1<polygon.Count;j++)
                        {resultTriangles.Add(first);resultTriangles.Add(first+(cave?j+1:j));resultTriangles.Add(first+(cave?j:j+1));}
                    }
                }
                timings.Lap("Prepare: source + height sampling + clipping");
                if(boundaries==null)boundaries=TerrainBoundarySegments(world,stamps,pending,timings,boundaryCache,seamNormals);
                if(!boundaries.TryGetValue(stamp.id,out var terrainBoundarySegments))terrainBoundarySegments=new List<RockClipVertex>();
                timings.Lap("Prepare: terrain boundary extraction");
                if(stamp.meshCutTerrain&&(terrainBoundarySegments.Count==0||rockBoundarySegments.Count==0))
                    throw new InvalidOperationException("No closed bridge rim: reduce Rock Retopo Height or increase terrain density around the intersection.");
                bool hasContact=stamp.meshCutTerrain&&rockBoundarySegments.Count>=2&&terrainBoundarySegments.Count>=2;
                if(hasContact)
                {
                    int originalVertexCount=resultVertices.Count;
                    Array.Resize(ref submeshTriangles,source.subMeshCount+1);var contact=submeshTriangles[source.subMeshCount]=new List<int>();
                    Matrix4x4 terrainToRock=matrix.inverse;
                    float contactCell=MeshContactCell(stamp.meshGridDensityMultiplier);
                    Func<Vector3,Vector3,Vector4,Vector2,int> addContactVertex=(terrainPoint,terrainNormal,terrainTangent,uv)=>
                    {
                        int id=resultVertices.Count;
                        resultVertices.Add(terrainToRock.MultiplyPoint3x4(terrainPoint));
                        resultNormals.Add(matrix.transpose.MultiplyVector(terrainNormal).normalized);
                        Vector3 localTangent=terrainToRock.MultiplyVector(new Vector3(terrainTangent.x,terrainTangent.y,terrainTangent.z)).normalized;
                        resultTangents.Add(new Vector4(localTangent.x,localTangent.y,localTangent.z,terrainTangent.w));resultUv.Add(uv);
                        return id;
                    };
                    Action<int,int,int> addContactTriangle=(a,b,c)=>
                    {
                        Vector3 aa=matrix.MultiplyPoint3x4(resultVertices[a]),bb=matrix.MultiplyPoint3x4(resultVertices[b]),cc=matrix.MultiplyPoint3x4(resultVertices[c]);
                        Vector3 cross=Vector3.Cross(bb-aa,cc-aa);
                        if(a==b||b==c||a==c||cross.sqrMagnitude<1e-16f)return;
                        contact.Add(a);contact.Add(b);contact.Add(c);
                    };
                    var rockSegmentsInTerrain=new List<RockClipVertex>();
                    foreach(var vertex in rockBoundarySegments)
                    {
                        var v=vertex;v.position=matrix.MultiplyPoint3x4(vertex.position);
                        v.normal=matrix.inverse.transpose.MultiplyVector(vertex.normal).normalized;
                        Vector3 tangent=matrix.MultiplyVector(new Vector3(vertex.tangent.x,vertex.tangent.y,vertex.tangent.z)).normalized;
                        v.tangent=new Vector4(tangent.x,tangent.y,tangent.z,vertex.tangent.w);
                        rockSegmentsInTerrain.Add(v);
                    }
                    var outerRim=new HashSet<int>();var outerEdges=new HashSet<long>(EdgeKeyComparer.Instance);
                    timings.Lap("Prepare: bridge setup");
                    BuildContourBridge(OrderedBoundaryLoops(rockSegmentsInTerrain,component.name+": trimmed model rim"),OrderedBoundaryLoops(terrainBoundarySegments,component.name+": terrain cut rim"),
                        contactCell,Math.Max(10000,world.maxVerticesPerChunk),world.source.size,addContactVertex,addContactTriangle,outerRim,outerEdges,stamp.meshBlendDistance);
                    int bridgeVertexEnd=resultVertices.Count;
                    timings.Lap("Prepare: contour ordering + Bridge");
                    StitchRockRim(resultVertices,resultNormals,resultTangents,resultUv,submeshTriangles,source.subMeshCount,originalVertexCount,rockBoundarySegments);
                    timings.Lap("Prepare: StitchRockRim");
                    WeldGeneratedTopology(resultVertices,resultNormals,resultTangents,resultUv,submeshTriangles,matrix,
                        originalVertexCount,bridgeVertexEnd,stamp.meshWeldDistance,outerRim,outerEdges);
                    timings.Lap("Prepare: Weld + topology validation");
                    if(submeshTriangles[source.subMeshCount].Count==0){hasContact=false;Array.Resize(ref submeshTriangles,source.subMeshCount);}
                }
                prepared.Add(new PreparedRock{stamp=stamp,matrix=matrix,hasContact=hasContact,
                    vertices=resultVertices,uv=resultUv,triangles=submeshTriangles,terrainBoundary=terrainBoundarySegments});
            }
            return prepared;
        }
        static void ApplyTrimmedRockMeshes(LTWorld world,List<Stamp> stamps,List<PreparedRock> prepared,RockTimings timings)
        {
            foreach(var data in prepared)
            {
                var stamp=data.stamp;var component=stamp.meshComponent;
                if(data.restore){RestoreSourceRock(component);continue;}
                timings.Start();
                var source=stamp.meshSource;var filter=component.GetComponent<MeshFilter>();
                var matrix=data.matrix;bool hasContact=data.hasContact;
                var resultVertices=data.vertices;var resultUv=data.uv;var submeshTriangles=data.triangles;
                // Preparation recalculates normals on the exact pending vertices and
                // indices with Unity's RecalculateNormals, just like terrain upload.
                // No geometry changes between validation and upload: these are the
                // final normals, not the emitter's provisional normals. Reuse them.
                var terrainBoundarySegments=data.terrainBoundary;
                timings.Lap("Apply: reuse validated terrain boundary");
                var generated=GetOwnedOutputMesh(component,source);
                generated.Clear();
                generated.indexFormat=resultVertices.Count>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16;
                generated.SetVertices(resultVertices);generated.SetUVs(0,resultUv);
                generated.subMeshCount=submeshTriangles.Length;
                for(int submesh=0;submesh<submeshTriangles.Length;submesh++)generated.SetTriangles(submeshTriangles[submesh],submesh,false);
                timings.Lap("Apply: mesh upload");
                // Source/interpolated normals are construction hints only. Clipping,
                // bridge generation, winding repair and welding change the final faces.
                // Rebuild shading from final indices in both Union and Cave, even when
                // the source supplied normals/tangents. Welded submesh seams share normals.
                generated.RecalculateNormals();
                generated.RecalculateBounds();
                timings.Lap("Apply: recalculate normals + bounds");
                // Pin the final contact normals to the terrain's edge interpolation.
                // Interior normals still come from the finished, welded triangles.
                var finalNormals=generated.normals;var issues=new List<Vector3>();
                var edgeUse=new Dictionary<long,(int count,int direction)>(EdgeKeyComparer.Instance);
                foreach(var triangles in submeshTriangles)for(int i=0;i+2<triangles.Count;i+=3)
                {
                    for(int k=0;k<3;k++)
                    {
                        int a=triangles[i+k],b=triangles[i+(k+1)%3];long key=MeshEdge(a,b);
                        edgeUse.TryGetValue(key,out var use);edgeUse[key]=(use.count+1,use.direction+(a<b?1:-1));
                    }
                    int x=triangles[i],y=triangles[i+1],z=triangles[i+2];
                    if(Vector3.Cross(resultVertices[y]-resultVertices[x],resultVertices[z]-resultVertices[x]).sqrMagnitude<1e-14f)
                    {issues.Add(resultVertices[x]);issues.Add(resultVertices[y]);issues.Add(resultVertices[y]);issues.Add(resultVertices[z]);}
                }
                timings.Lap("Apply: edge incidence + degenerate checks");
                var contactVertices=new HashSet<int>();
                foreach(int index in edgeUse.Where(e=>e.Value.count==1).SelectMany(e=>new[]{(int)(e.Key>>32),(int)(e.Key&0xffffffff)}).Distinct())
                {
                    Vector3 p=matrix.MultiplyPoint3x4(resultVertices[index]);float best=1e-8f;Vector3 normal=Vector3.zero;
                    if(hasContact)for(int i=0;i+1<terrainBoundarySegments.Count;i+=2)
                    {
                        var a=terrainBoundarySegments[i];var b=terrainBoundarySegments[i+1];var delta=b.position-a.position;
                        float t=delta.sqrMagnitude>1e-12f?Mathf.Clamp01(Vector3.Dot(p-a.position,delta)/delta.sqrMagnitude):0;
                        float distance=(p-a.position-t*delta).sqrMagnitude;
                        if(distance<=best){best=distance;normal=Vector3.Lerp(a.normal,b.normal,t).normalized;}
                    }
                    if(normal.sqrMagnitude>0){finalNormals[index]=matrix.transpose.MultiplyVector(normal).normalized;contactVertices.Add(index);}
                }
                timings.Lap("Apply: seam normal matching");
                foreach(var edge in edgeUse)
                {
                    int a=(int)(edge.Key>>32),b=(int)(edge.Key&0xffffffff);
                    bool valid=edge.Value.count==2&&edge.Value.direction==0;
                    if(edge.Value.count==1&&contactVertices.Contains(a)&&contactVertices.Contains(b))valid=true;
                    if(!valid){issues.Add(resultVertices[a]);issues.Add(resultVertices[b]);}
                }
                component.geometryIssueEdges=issues.ToArray();
                timings.Lap("Apply: issue edges");
                generated.SetNormals(finalNormals);generated.RecalculateTangents();
                timings.Lap("Apply: final normals + tangents");
                filter.sharedMesh=generated;
                var meshRenderer=component.GetComponent<MeshRenderer>();
                if(meshRenderer)
                {
                    if((!component.terrainMaterialApplied&&!component.unionGroupOutputApplied&&!component.terrainLayersMaterialApplied)||component.objectMaterials==null)
                        component.objectMaterials=meshRenderer.sharedMaterials.Take(component.sourceMesh?component.sourceMesh.subMeshCount:source.subMeshCount).ToArray();
                    var materials=(component.objectMaterials??Array.Empty<Material>()).ToList();
                    while(materials.Count<source.subMeshCount)materials.Add(materials.Count>0?materials[materials.Count-1]:world.material);
                    bool useTerrain=component.useTerrainMaterial&&world.material;
                    if(useTerrain)
                    {
                        // Keep submesh ownership for reversible material switching.
                        materials=Enumerable.Repeat(world.material,generated.subMeshCount).ToList();
                    }
                    else if(hasContact)materials.Add(world.material?world.material:materials.LastOrDefault());
                    meshRenderer.sharedMaterials=materials.ToArray();
                    component.terrainMaterialApplied=useTerrain;component.unionGroupOutputApplied=false;EditorUtility.SetDirty(component);
                }
                timings.Lap("Apply: renderer + materials");
                var collider=component.GetComponent<MeshCollider>();if(collider){collider.sharedMesh=null;collider.sharedMesh=generated;}
                timings.Lap("Apply: rock collider");
            }
        }
        sealed class SeamNormalAccumulator
        {
            sealed class Entry {public Vector3 sum;public int owner;public bool shared;}
            readonly Dictionary<Vector3Int,Entry> entries=new Dictionary<Vector3Int,Entry>();
            public void Add(int owner,Rect rect,Vector3[] points,int[] triangles)
            {
                Add(owner,LTPaintMath.TerrainSeamContributions(rect,points,triangles));
            }
            public void Add(int owner,LTPaintMath.SeamContribution[] contributions)
            {
                // Preserve full-rebuild face order and floating point accumulation.
                foreach(var item in contributions)
                {
                    if(!entries.TryGetValue(item.key,out var entry))entries[item.key]=entry=new Entry{owner=owner};
                    entry.shared|=entry.owner!=owner;entry.sum+=item.normal;
                }
            }
            public Dictionary<Vector3Int,Vector3> Finish()=>entries.Where(p=>p.Value.shared&&p.Value.sum.sqrMagnitude>1e-20f)
                .ToDictionary(p=>p.Key,p=>p.Value.sum.normalized);
        }
        sealed class SeamChunkCache
        {
            public Mesh mesh;public int version;public Matrix4x4 matrix;public Rect rect;
            public LTPaintMath.SeamContribution[] contributions;
        }
        static Dictionary<Vector3Int,Vector3> PrepareSeamNormals(LTWorld world,LTChunk[] chunks,List<PendingMesh> pending,
            Dictionary<int,SeamChunkCache> cache,HashSet<int> changed)
        {
            var accumulator=new SeamNormalAccumulator();var proposed=pending.ToDictionary(p=>p.id);
            var live=new HashSet<int>(chunks.Select(c=>c.z*world.chunksX+c.x));
            foreach(int id in cache.Keys.Where(id=>!live.Contains(id)).ToArray()){cache.Remove(id);changed.Add(id);}
            foreach(var chunk in chunks)
            {
                int id=chunk.z*world.chunksX+chunk.x;
                if(!chunk.mesh){cache.Remove(id);changed.Add(id);continue;}
                proposed.TryGetValue(id,out var data);
                var matrix=world.transform.worldToLocalMatrix*chunk.transform.localToWorldMatrix;
                var rect=ChunkRect(world,id);int version=EditorUtility.GetDirtyCount(chunk.mesh);
                if(data!=null||!cache.TryGetValue(id,out var cached)||cached.mesh!=chunk.mesh||cached.version!=version||cached.matrix!=matrix||cached.rect!=rect)
                {
                    var vertices=data!=null?data.vertices:chunk.mesh.vertices;
                    var indices=data!=null?data.indices:chunk.mesh.triangles;
                    var points=new Vector3[vertices.Length];
                    for(int i=0;i<points.Length;i++)points[i]=matrix.MultiplyPoint3x4(vertices[i]);
                    cached=new SeamChunkCache{mesh=chunk.mesh,version=version,matrix=matrix,rect=rect,
                        contributions=LTPaintMath.TerrainSeamContributions(rect,points,indices)};
                    cache[id]=cached;changed.Add(id);
                }
                accumulator.Add(id,cached.contributions);
            }
            return accumulator.Finish();
        }
        static void ApplySeamNormals(LTWorld world,LTChunk[] chunks,Dictionary<Vector3Int,Vector3> shared,BoundaryCache cache,HashSet<int> affected,bool coarseOnly=false)
        {
            foreach(var chunk in chunks)
            {
                if(!affected.Contains(chunk.z*world.chunksX+chunk.x))continue;
                var matrix=world.transform.worldToLocalMatrix*chunk.transform.localToWorldMatrix;
                // Use the same LOD0-derived normal at every boundary on every LOD:
                // neighbouring chunks may display different LOD levels.
                var baseMeshes=coarseOnly?Array.Empty<Mesh>():new[]{chunk.mesh};
                var coarseMeshes=chunk.lodsPending?Array.Empty<Mesh>():chunk.lodMeshes??Array.Empty<Mesh>();
                foreach(var mesh in baseMeshes.Concat(coarseMeshes).Where(m=>m).Distinct())
                {
                    var vertices=mesh.vertices;var normals=mesh.normals;bool changed=false;
                    if(normals.Length!=vertices.Length)continue;
                    for(int i=0;i<vertices.Length;i++)
                    {
                        var key=BoundaryPointKey(matrix.MultiplyPoint3x4(vertices[i]));
                        if(!shared.TryGetValue(key,out var normal))continue;
                        var local=matrix.transpose.MultiplyVector(normal).normalized;
                        if(normals[i].Equals(local))continue;
                        normals[i]=local;changed=true;
                    }
                    if(!changed)continue;
                    int version=EditorUtility.GetDirtyCount(mesh);
                    mesh.normals=normals;mesh.tangents=LTStampMesh.TerrainTangents(normals);EditorUtility.SetDirty(mesh);
                    // A neighbour can keep its geometry but receive new seam normals.
                    // CPU paint/detail samplers must not retain its previous slope data.
                    if(mesh==chunk.mesh)chunk.updatedAt=EditorApplication.timeSinceStartup;
                    // Only acknowledge our known normal-only write. Unknown geometry
                    // revisions must still invalidate the cache. Copy shared entries.
                    int id=chunk.GetInstanceID();
                    if(mesh==chunk.mesh&&cache.TryGetValue(id,out var entry)&&entry.mesh==mesh&&
                        entry.dirtyVersion==version&&entry.matrix.Equals(matrix))
                        cache[id]=entry.WithNormals(normals,EditorUtility.GetDirtyCount(mesh),matrix.inverse.transpose);
                }
            }
        }
        sealed class PendingMesh
        {
            public LTChunk chunk;public int id;public Vector3[] vertices,normals;public Vector2[] uv;public int[] indices;public List<Vector3Int> raw,balanced;public List<int> border;public List<PendingMesh> lodData=new List<PendingMesh>();
        }
        static void PublishChunkLegacyLODs(LTWorld w,PendingMesh data)
        {
            var c=data.chunk;int count=data.lodData.Count;
            var lodMeshes=new Mesh[count];var lodPlans=new LTLODPlan[count];
            for(int level=0;level<count;level++)
            {
                string path=AssetDatabase.GetAssetPath(c.mesh);
                path=path.Substring(0,path.Length-6)+"_LOD"+(level+1)+".asset";
                var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if(!mesh){mesh=new Mesh{name=c.mesh.name+"_LOD"+(level+1)};AssetDatabase.CreateAsset(mesh,path);}
                var lod=data.lodData[level];mesh.Clear();
                mesh.indexFormat=lod.vertices.Length>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.vertices=lod.vertices;mesh.uv=lod.uv;mesh.triangles=lod.indices;mesh.RecalculateNormals();mesh.RecalculateBounds();mesh.tangents=LTStampMesh.TerrainTangents(mesh.normals);
                EditorUtility.SetDirty(mesh);lodMeshes[level]=mesh;lodPlans[level]=new LTLODPlan{leaves=lod.balanced};
            }
            c.lodMeshes=lodMeshes;c.lodPlans=lodPlans;
            if(!w.UseSpatialLODs){c.ReleaseLODPreview();c.lodsPending=false;}
            c.ShowLOD(0);EditorUtility.SetDirty(c);
        }
        static void QueueChunkLODs(LTWorld w,State state,LTChunk c,int id,Dictionary<Vector3Int,int> masks,List<Stamp> local)
        {
            // Keep this closure OUTSIDE RebuildSteps: its local functions capture
            // the world's pending meshes/forest. A validator must not retain that
            // entire transaction until the last chunk finishes.
            var mesh=c.mesh;var root=w.generatedRoot;var source=w.source;
            string meshInput=AssetInputHash(mesh);var fine=c.renderLeaves;var border=c.borderStitches;
            // Seam propagation can replace LOD0 without an authored input edit.
            state.chunkLODs.Touch(id);
            state.chunkLODs.Enqueue(id,state.chunkLODs.Version(id),DeferredChunkLODs(w,state,c,id,fine,masks,local).GetEnumerator(),
                ()=>w&&root&&w.generatedRoot==root&&w.source==source&&c&&mesh&&c.mesh==mesh&&AssetInputHash(mesh)==meshInput&&c.transform.IsChildOf(root)&&
                    c.GetComponentInParent<LTWorld>()==w&&c.renderLeaves==fine&&c.borderStitches==border&&c.lodsPending);
        }
        // Owns only this chunk's source plan, midpoint masks and local stamp list.
        // The scheduler checks its input version before every MoveNext, including
        // the publication step. No LOD0 write / paint invalidation on completion.
        static IEnumerable<LTBuildStep> DeferredChunkLODs(LTWorld w,State state,LTChunk chunk,int id,
            List<Vector3Int> fine,Dictionary<Vector3Int,int> masks,List<Stamp> local)
        {
            var progress=new LTLODMesh.BuildProgress(false);
            var r=ChunkRect(w,id);int budget=Math.Max(10000,w.maxVerticesPerChunk);
            int count=w.enableLODs&&w.lods!=null?w.lods.Length:0;
            bool spatial=w.UseSpatialLODs;
            var cutRegions=local.Where(s=>s.meshStamp&&s.meshCutTerrain)
                .Select(s=>Expanded(s.bounds,2*MeshContactCell(s.meshGridDensityMultiplier),2*MeshContactCell(s.meshGridDensityMultiplier))).ToList();
            var protectedRegions=spatial?new List<Rect>(cutRegions):local.Where(s=>s.meshStamp&&s.meshCutTerrain&&s.meshTrimRock)
                .Select(s=>Expanded(s.bounds,2*MeshContactCell(s.meshGridDensityMultiplier),2*MeshContactCell(s.meshGridDensityMultiplier))).ToList();
            protectedRegions.AddRange(local.Where(s=>s.fixedRoadSurface&&s.junction!=null).Select(s=>s.bounds));
            var roads=local.Where(s=>s.fixedRoadSurface&&s.road!=null).Select(s=>s.road).ToArray();
            var levels=new List<Vector3Int>[count+1];levels[0]=fine;
            var output=new PendingMesh{chunk=chunk,id=id};
            var height=HeightSampler(w,state,id,local);
            foreach(var step in PrefetchHeights(w,state,id,local,LTHeightJobMath.FinePoints(r,fine)))yield return step;
            // All settings and Unity-dependent samples belong to this accepted
            // chunk revision. The queue validates it again before EVERY resume.
            var settings=new LTLODJobMath.Level[count];
            for(int level=0;level<count;level++)settings[level]=new LTLODJobMath.Level
                {steps=w.lods[level].simplificationSteps,tolerance=w.lods[level].maxHeightError};
            try
            {
                if(count>0)
                {
                    state.lodJobStatus=$"LOD ({chunk.x},{chunk.z}): подготовка снимка для Jobs";
                    var input=new LTLODJobInput();
                    foreach(var step in input.Prepare(fine,r,settings,height,
                        protectedRegions,roads.Length==0?(Func<Rect,bool>)null:area=>roads.Any(road=>road.Intersects(area)),w.SpatialLODDivisions,!spatial))yield return step;
                    while(!LTLODJobWork.CanSchedule)yield return LTBuildStep.Waiting;
                    using(var job=new LTLODJobWork(input))
                    {
                        state.lodJobStatus=$"LOD ({chunk.x},{chunk.z}): фоновое упрощение Jobs / Burst";
                        // Always return control after Schedule, even for a tiny job.
                        yield return LTBuildStep.Waiting;
                        while(!job.IsCompleted)yield return LTBuildStep.Waiting;
                        for(int level=1;level<=count;level++)
                        {
                            levels[level]=settings[level-1].steps==0?new List<Vector3Int>(fine):job.ReadLevel(level-1);
                            yield return LTBuildStep.Working;
                        }
                        state.lastLODJobBackend=job.UsedBurst?"Burst Job":"Jobs, managed (Burst выключен или ещё компилируется)";
                    }
                }
            }
            finally{state.lodJobStatus=null;}
            for(int level=1;level<=count;level++)
            {
                if(!spatial)
                {
                    var forest=new LTBalancedForest(1,1,budget,new Dictionary<int,List<Vector3Int>>{{0,levels[level]}});
                    foreach(int step in forest.BalanceSteps())yield return LTBuildStep.Working;
                    var plan=forest.Plan(0);LTLODMesh.ValidateBoundary(fine,plan);
                    LTStampMesh.Emit(r,new Vector2(w.source.size.x,w.source.size.z),budget,forest,0,plan,
                        height,(x,z)=>CutTerrainAt(w,local,x,z),out var vertices,out var normals,out var uv,out var indices,
                        TransitionDiagonals(w,id),progress,(cell,side)=>LTLODMesh.LocalMidpoint(forest,masks,cell,side));
                    output.lodData.Add(new PendingMesh{vertices=vertices,normals=normals,uv=uv,indices=indices,balanced=plan});
                    yield return LTBuildStep.Working;
                }
            }
            LTSpatialLODMath.Output proposal=null;
            if(spatial)
            {
                var baseForest=new LTBalancedForest(1,1,budget,new Dictionary<int,List<Vector3Int>>{{0,fine}},new HashSet<int>{0});
                int BaseMask(Vector3Int cell)
                {int mask=0;for(int side=0;side<4;side++)if(LTLODMesh.LocalMidpoint(baseForest,masks,cell,side))mask|=1<<side;return mask;}
                bool PreserveContour(Vector3Int cell)
                {
                    var area=new Rect(r.xMin+cell.x/(float)LTSpatialLODMath.N*r.width,r.yMin+cell.y/(float)LTSpatialLODMath.N*r.height,
                        cell.z/(float)LTSpatialLODMath.N*r.width,cell.z/(float)LTSpatialLODMath.N*r.height);
                    return protectedRegions.Any(region=>LTStampMesh.Overlap(region,area))||roads.Any(road=>road.Intersects(area));
                }
                proposal=LTSpatialLODMath.BuildLayout(levels,w.SpatialLODDivisions,budget,BaseMask,PreserveContour);
                LTStampMesh.EmitSpatialVariants(proposal,r,new Vector2(w.source.size.x,w.source.size.z),budget,height,
                    cutRegions.Count==0?(Func<float,float,bool>)null:(x,z)=>CutTerrainAt(w,local,x,z),TransitionDiagonals(w,id),progress);
                yield return LTBuildStep.Working;
            }
            // Read CURRENT accepted seam normals: a neighbour may have finished a
            // new LOD0 while this unchanged chunk's coarse work was paused.
            if(spatial)
            {PublishChunkLegacyLODs(w,output);LTSpatialLODBake.Apply(chunk,proposal);}
            else
            {
                var chunks=w.generatedRoot.GetComponentsInChildren<LTChunk>();
                var shared=PrepareSeamNormals(w,chunks,new List<PendingMesh>(),state.seamCache,new HashSet<int>());
                PublishChunkLegacyLODs(w,output);
                ApplySeamNormals(w,chunks,shared,state.boundaryCache,new HashSet<int>{id},true);
            }
            chunk.lodsPending=false;EditorUtility.SetDirty(chunk);w.RefreshLODCache();
            EditorSceneManager.MarkSceneDirty(w.gameObject.scene);
        }
        [MenuItem("Tools/Local Terrain/Validate Mesh Bridges")]
        public static void ValidateCurrentMeshBridges()
        {
            try{Debug.Log(ValidateBridgeGeometry());}catch(Exception error){Debug.LogException(error);}
        }
        public static string ValidateBridgeGeometry(float cutScale=1,float heightScale=1)
        {
            var reports=new List<string>();
            foreach(var world in Worlds())
            {
                if(!world.source||!world.generatedRoot)continue;
                var snapshot=Capture(world);
                if(!snapshot.Any(s=>s.meshStamp&&s.meshCutTerrain&&s.meshTrimRock))continue;
                foreach(var stamp in snapshot.Where(s=>s.meshStamp))
                {
                    stamp.meshCutOffset*=cutScale;stamp.meshRetopoHeight*=heightScale;
                    float extent=Mathf.Max(stamp.meshBlendDistance,stamp.meshCutOffset)+.001f;
                    stamp.bounds=Expanded(stamp.meshFootprintBounds,extent,extent);
                }
                var state=new State{previous=snapshot};state.densities=CaptureDensity(world,state);
                foreach(var chunk in world.generatedRoot.GetComponentsInChildren<LTChunk>())state.dirty.Add(chunk.z*world.chunksX+chunk.x);
                Rebuild(world,state,true);reports.Add(world.name+": validated");
            }
            return reports.Count==0?"No active mesh bridges found.":string.Join("\n",reports);
        }
        public static void BenchmarkPreparation(LTWorld w)
        {
            if(!w.source||!w.generatedRoot||!ValidTransform(w))return;
            const int warmup=2,runs=7;
            try
            {
                // Private snapshot/state: the live dirty queue and timing history
                // are untouched. Synchronous runs cannot observe intervening edits.
                var state=new State();Detect(w,state,Capture(w));
                for(int id=0;id<w.chunksX*w.chunksZ;id++)state.dirty.Add(id);
                var samples=new List<Dictionary<string,double>>();
                for(int i=0;i<warmup+runs;i++)
                {
                    if(EditorUtility.DisplayCancelableProgressBar("Terrain preparation benchmark",
                        i<warmup?"Warm-up":"Measured run "+(i-warmup+1)+" / "+runs,i/(float)(warmup+runs)))
                        throw new OperationCanceledException();
                    var measurement=new Dictionary<string,double>();
                    Rebuild(w,state,true,measurement);
                    if(i>=warmup)samples.Add(measurement);
                }
                var lines=new List<string>{"Preparation only: all chunks, 2 warm-ups + 7 runs.",
                    "Median [min–max], ms. Nested stages overlap parent totals.",
                    "Excludes capture, mesh upload, final rock normals, colliders and saving."};
                foreach(string key in samples.SelectMany(s=>s.Keys).Distinct())
                {
                    var values=samples.Select(s=>s.TryGetValue(key,out double value)?value:0).OrderBy(v=>v).ToArray();
                    lines.Add(key+": "+values[runs/2].ToString("F1")+" ["+values[0].ToString("F1")+"–"+values[runs-1].ToString("F1")+"]");
                }
                w.benchmarkTimings=string.Join("\n",lines);
                Debug.Log(w.benchmarkTimings,w);
            }
            catch(OperationCanceledException){w.benchmarkTimings="Benchmark cancelled; no result applied.";}
            catch(Exception error){w.benchmarkTimings="Benchmark failed: "+error.Message;Debug.LogException(error,w);}
            finally{EditorUtility.ClearProgressBar();}
        }
        static void Rebuild(LTWorld w, State state,bool validateOnly=false,Dictionary<string,double> measurements=null)
        {
            // Manual rebuild, validation and save barriers drain the SAME pipeline.
            using(var steps=RebuildSteps(w,state,false,null,validateOnly,measurements).GetEnumerator())
                while(steps.MoveNext()){}
        }
        static IEnumerable<LTBuildStep> RebuildSteps(LTWorld w,State state,bool staged,HashSet<int> affected,
            bool validateOnly=false,Dictionary<string,double> measurements=null)
        {
            if(state.dirty.Count==0)yield break;
            // A later unrelated edit must not enter this already captured build.
            var requested=new HashSet<int>(affected??state.dirty);
            var timer = Stopwatch.StartNew();
            try
            {
                var lodProgress=new LTLODMesh.BuildProgress(!staged);
                int lodPreparedChunks=0;long lodHeightSamples=0;
                var timings=new List<string>();double previousTime=0;
                void Stage(string name)
                {
                    double elapsed=timer.Elapsed.TotalMilliseconds;
                    if(measurements!=null)measurements[name]=elapsed-previousTime;
                    timings.Add(name+": "+(elapsed-previousTime).ToString("F1")+" ms");previousTime=elapsed;
                }
                // Capture owns its managed stamp/contour data for this revision. Detect
                // may replace state.previous between ticks without changing this snapshot.
                var stamps=state.previous;
                PrepareContactContours(w,stamps);
                Stage("Contact contours");
                var terrainTimings=new RockTimings();terrainTimings.Start();
                var chunks = w.generatedRoot.GetComponentsInChildren<LTChunk>();
                void ReadDependencies(HashSet<int> targets)
                {
                    state.build.Dependencies.UnionWith(LTPaintMath.TerrainNeighbours(targets,w.chunksX,w.chunksZ));
                    foreach(var rock in stamps.Where(s=>s.meshStamp))
                    {
                        var region=Expanded(rock.bounds,w.source.size.x/w.chunksX,w.source.size.z/w.chunksZ);
                        if(!targets.Any(id=>Overlap(region,ChunkRect(w,id))))continue;
                        foreach(var c in chunks)
                        {int id=c.z*w.chunksX+c.x;if(Overlap(region,ChunkRect(w,id)))state.build.Dependencies.Add(id);}
                    }
                }
                if(staged)
                {
                    // LOD0 publication is a seam transaction. Include input readers
                    // around its targets and whole rock contacts, but not the world.
                    ReadDependencies(requested);
                    var root=w.generatedRoot;var source=w.source;int children=root.childCount;
                    var meshes=chunks.Select(c=>c.mesh).ToArray();var meshInputs=meshes.Select(AssetInputHash).ToArray();
                    state.buildObjectsValid=()=>w&&w.generatedRoot==root&&w.source==source&&root&&root.childCount==children&&
                        chunks.Select((c,i)=>c&&meshes[i]&&c.mesh==meshes[i]&&AssetInputHash(meshes[i])==meshInputs[i]&&c.transform.IsChildOf(root)&&c.GetComponentInParent<LTWorld>()==w).All(valid=>valid);
                }
                var pending=new List<PendingMesh>();
                var plans=new Dictionary<int,List<Vector3Int>>();var zones=state.densities.SelectMany(d=>d.zones).ToList();
                int budget=Math.Max(10000,w.maxVerticesPerChunk);
                int lodCount=w.enableLODs && w.lods!=null?w.lods.Length:0;
                if(lodCount>4)throw new InvalidOperationException("Use at most 4 extra LOD levels (LOD1..LOD4).");
                for(int l=0;l<lodCount;l++)
                    if(w.lods[l]==null || w.lods[l].simplificationSteps<0 || w.lods[l].simplificationSteps>12 ||
                        float.IsNaN(w.lods[l].maxHeightError)||float.IsInfinity(w.lods[l].maxHeightError)||w.lods[l].maxHeightError<0 ||
                        (l>0 && (w.lods[l].simplificationSteps<w.lods[l-1].simplificationSteps||w.lods[l].maxHeightError<w.lods[l-1].maxHeightError)))
                        throw new InvalidOperationException("LOD steps and height errors must be finite, valid and non-decreasing.");
                int spatialLevels=w.UseSpatialLODs?lodCount:0;
                if(w.UseSpatialLODs)lodCount=0; // no duplicate legacy meshes with pinned borders
                var spatialProposals=new Dictionary<int,LTSpatialLODMath.Output>();
                var rockTimings=new RockTimings();
                LTBalancedForest forest=null;
                bool early=staged&&!validateOnly&&(lodCount>0||w.UseSpatialLODs);
                IEnumerable<LTBuildStep> LegacyLODs()
                {
                    var changed=pending.ToDictionary(p=>p.id);
                    // Prepare all levels per changed chunk, then discard its local
                    // preparation. Shared composed heights have a bounded LRU budget.
                    var preparedLODPlans=new Dictionary<int,List<Vector3Int>[]>();
                    if(lodCount>0)foreach(var data in pending)
                    {
                        var r=ChunkRect(w,data.id);
                        var local=stamps.Where(s=>Overlap(s.bounds,Expanded(r,r.width/w.cellsPerChunk,r.height/w.cellsPerChunk))).ToList();
                        var bridgeRegions=local.Where(s=>s.meshStamp&&s.meshCutTerrain&&s.meshTrimRock)
                            .Select(s=>Expanded(s.bounds,2*MeshContactCell(s.meshGridDensityMultiplier),2*MeshContactCell(s.meshGridDensityMultiplier))).ToList();
                        var roads=local.Where(s=>s.fixedRoadSurface&&s.road!=null).Select(s=>s.road).ToArray();
                        bridgeRegions.AddRange(local.Where(s=>s.fixedRoadSurface&&s.junction!=null).Select(s=>s.bounds));
                        var height=HeightSampler(w,state,data.id,local);
                        var preparation=new LTLODMesh.Preparation(data.balanced,r,height,lodProgress);
                        var levels=new List<Vector3Int>[lodCount];
                        for(int level=0;level<lodCount;level++)
                        {
                            levels[level]=preparation.Coarsen(w.lods[level].simplificationSteps,w.lods[level].maxHeightError,bridgeRegions,
                                roads.Length==0?(Func<Rect,bool>)null:area=>roads.Any(road=>road.Intersects(area)),w.SpatialLODDivisions);
                            yield return LTBuildStep.Working;
                        }
                        preparedLODPlans.Add(data.id,levels);lodPreparedChunks++;lodHeightSamples+=preparation.HeightSampleCount;
                    }
                    for(int level=0;level<lodCount;level++)
                    {
                        var lodPlans=new Dictionary<int,List<Vector3Int>>();
                        foreach(var c in chunks)
                        {
                            int id=c.z*w.chunksX+c.x;
                            if(changed.ContainsKey(id))lodPlans[id]=preparedLODPlans[id][level];
                            else lodPlans[id]=c.lodPlans[level].leaves;
                        }
                        var lodForest=new LTBalancedForest(w.chunksX,w.chunksZ,budget,lodPlans);
                        if(staged)foreach(int step in lodForest.BalanceSteps())yield return LTBuildStep.Working;
                        else lodForest.Balance(lodProgress);
                        foreach(var data in pending)
                        {
                            int id=data.id;var plan=lodForest.Plan(id);
                            LTLODMesh.ValidateBoundary(data.balanced,plan);
                            if(!forest.BoundaryStitches(id,data.balanced).SequenceEqual(lodForest.BoundaryStitches(id,plan)))
                                throw new InvalidOperationException("LOD seam mismatch. Previous meshes preserved.");
                            Rect r=ChunkRect(w,id);
                            var local=stamps.Where(s=>Overlap(s.bounds,Expanded(r,r.width/w.cellsPerChunk,r.height/w.cellsPerChunk))).ToList();
                            var height=HeightSampler(w,state,id,local);
                            LTStampMesh.Emit(r,new Vector2(w.source.size.x,w.source.size.z),budget,lodForest,id,plan,height,(x,z)=>CutTerrainAt(w,local,x,z),out var vertices,out var normals,out var uv,out var indices,TransitionDiagonals(w,id),lodProgress);
                            data.lodData.Add(new PendingMesh{vertices=vertices,normals=normals,uv=uv,indices=indices,balanced=plan});
                            yield return LTBuildStep.Working;
                        }
                    }
                    preparedLODPlans.Clear();
                    Stage("Extra LODs");
                }
                IEnumerable<LTBuildStep> SpatialLODs(bool extra)
                {
                    if(w.UseSpatialLODs)foreach(var data in pending)
                    {
                        int id=data.id;var r=ChunkRect(w,id);
                        var local=stamps.Where(s=>Overlap(s.bounds,Expanded(r,r.width/w.cellsPerChunk,r.height/w.cellsPerChunk))).ToList();
                        var cutRegions=local.Where(s=>s.meshStamp&&s.meshCutTerrain)
                            .Select(s=>Expanded(s.bounds,2*MeshContactCell(s.meshGridDensityMultiplier),2*MeshContactCell(s.meshGridDensityMultiplier))).ToList();
                        // Road density zones still shape LOD0/colliders. Only separate
                        // asphalt surfaces require fixed terrain cells and midpoint masks.
                        var roads=local.Where(s=>s.fixedRoadSurface&&s.road!=null).Select(s=>s.road).ToArray();
                        var protectedRegions=new List<Rect>(cutRegions);
                        protectedRegions.AddRange(local.Where(s=>s.fixedRoadSurface&&s.junction!=null).Select(s=>s.bounds));
                        var levels=new List<Vector3Int>[extra?spatialLevels+1:1];levels[0]=data.balanced;
                        var height=HeightSampler(w,state,id,local);
                        if(extra)
                        {
                            var preparation=new LTLODMesh.Preparation(data.balanced,r,height,lodProgress);
                            for(int level=1;level<levels.Length;level++)
                            {
                                levels[level]=preparation.Coarsen(w.lods[level-1].simplificationSteps,w.lods[level-1].maxHeightError,
                                    protectedRegions,roads.Length==0?(Func<Rect,bool>)null:area=>roads.Any(road=>road.Intersects(area)),w.SpatialLODDivisions,false);
                                yield return LTBuildStep.Working;
                            }
                            lodPreparedChunks++;lodHeightSamples+=preparation.HeightSampleCount;
                        }
                        int BaseMask(Vector3Int cell)
                        {int mask=0;for(int s=0;s<4;s++)if(forest.Midpoint(id,cell,s))mask|=1<<s;return mask;}
                        bool PreserveContour(Vector3Int cell)
                        {
                            var area=new Rect(r.xMin+cell.x/(float)LTSpatialLODMath.N*r.width,r.yMin+cell.y/(float)LTSpatialLODMath.N*r.height,
                                cell.z/(float)LTSpatialLODMath.N*r.width,cell.z/(float)LTSpatialLODMath.N*r.height);
                            return protectedRegions.Any(region=>LTStampMesh.Overlap(region,area))||roads.Any(road=>road.Intersects(area));
                        }
                        var output=LTSpatialLODMath.BuildLayout(levels,w.SpatialLODDivisions,budget,BaseMask,PreserveContour);
                        LTStampMesh.EmitSpatialVariants(output,r,new Vector2(w.source.size.x,w.source.size.z),budget,height,
                            cutRegions.Count==0?(Func<float,float,bool>)null:(x,z)=>CutTerrainAt(w,local,x,z),TransitionDiagonals(w,id),lodProgress);
                        spatialProposals[id]=output;
                        yield return LTBuildStep.Working;
                    }
                }
                void PublishLegacyLODs()
                {
                    foreach(var data in pending)PublishChunkLegacyLODs(w,data);
                }
                void Report()
                {
                    w.lastUpdatedChunks=pending.Count;w.lastUpdateMilliseconds=(float)timer.Elapsed.TotalMilliseconds;
                    w.lastBuildTimings=string.Join("\n",timings)+"\n\nTerrain LOD0 details (sum across chunks):\n"+terrainTimings+"\n\nRock details (sum across stamps):\n"+rockTimings;
                    w.lastUpdatedIds=string.Join(", ",pending.Select(p=>p.id).OrderBy(i=>i).Select(i=>$"({i%w.chunksX},{i/w.chunksX})"));
                    EditorUtility.SetDirty(w);EditorSceneManager.MarkSceneDirty(w.gameObject.scene);SceneView.RepaintAll();
                }
                terrainTimings.Lap("Terrain: setup");
                foreach(var c in chunks)
                {
                    int id=c.z*w.chunksX+c.x;
                    if(requested.Contains(id)||c.sourceLeaves==null||c.sourceLeaves.Count==0)
                    {
                        Rect r=ChunkRect(w,id);float dx=r.width/w.cellsPerChunk,dz=r.height/w.cellsPerChunk;
                        var local=stamps.Where(s=>Overlap(s.bounds,Expanded(r,dx,dz))).ToList();
                        var height=HeightSampler(w,state,id,local);
                        if(staged&&w.adaptive)foreach(var step in PrefetchHeights(w,state,id,local,LTHeightJobMath.BasePoints(r,w.cellsPerChunk)))
                        {terrainTimings.Pause();timer.Stop();yield return step;timer.Start();terrainTimings.Resume();}
                        plans[id]=LTStampMesh.Plan(r,w.cellsPerChunk,w.adaptive,Mathf.Max(.0001f,w.maxHeightError),budget,zones,height,lodProgress);
                        if(w.SpatialLODDivisions>1)plans[id]=LTSpatialLODMath.PartitionLeaves(plans[id],w.SpatialLODDivisions);
                    }
                    else plans[id]=c.sourceLeaves;
                    if(staged&&requested.Contains(id))
                    {
                        terrainTimings.Lap("Terrain: density + height planning");
                        timer.Stop();yield return LTBuildStep.Working;timer.Start();terrainTimings.Start();
                    }
                }
                terrainTimings.Lap("Terrain: density + height planning");
                Dictionary<int,List<Vector3Int>> internalPlans;
                if(staged)
                {
                    internalPlans=new Dictionary<int,List<Vector3Int>>();
                    foreach(int step in state.balanceCache.PrepareSteps(plans,budget,internalPlans))
                    {terrainTimings.Pause();timer.Stop();yield return LTBuildStep.Working;timer.Start();terrainTimings.Resume();}
                }
                else internalPlans=state.balanceCache.Prepare(plans,budget,lodProgress);
                terrainTimings.Lap("Terrain: internal balance cache");
                forest=new LTBalancedForest(w.chunksX,w.chunksZ,budget,internalPlans,new HashSet<int>(internalPlans.Keys));
                terrainTimings.Lap("Terrain: forest construction");
                if(staged)foreach(int step in forest.BalanceSteps())
                {terrainTimings.Pause();timer.Stop();yield return LTBuildStep.Working;timer.Start();terrainTimings.Resume();}
                else forest.Balance(lodProgress);
                terrainTimings.Lap("Terrain: balancing");
                terrainTimings.Append($"Balance cache: {state.balanceCache.Reused} reused / {state.balanceCache.Rebuilt} rebuilt chunks; {state.balanceCache.ProcessedCells} internal cells; {forest.ProcessedCells} border-pass cells");
                foreach(var c in chunks)
                {
                    terrainTimings.Start();
                    int id=c.z*w.chunksX+c.x;var balanced=forest.Plan(id);var border=forest.BoundaryStitches(id,balanced);
                    terrainTimings.Lap("Terrain: chunk plans + boundary stitches");
                    bool ownedCoarse=staged&&c.lodsPending&&state.chunkLODs.Contains(id);
                    bool lodValid=ownedCoarse||(!c.lodsPending&&(!w.UseSpatialLODs||(c.spatialLOD&&c.spatialLOD.formatVersion==LTSpatialLODMath.Version&&c.spatialLOD.divisions==w.SpatialLODDivisions))&&
                        c.lodMeshes!=null&&c.lodMeshes.Length==lodCount&&c.lodMeshes.All(m=>m)&&c.lodPlans!=null&&c.lodPlans.Length==lodCount&&c.lodPlans.All(p=>p!=null&&p.leaves!=null&&p.leaves.Count>0));
                    if((lodValid||staged&&state.dirty.Contains(id))&&!requested.Contains(id)&&c.renderLeaves!=null&&c.renderLeaves.SequenceEqual(balanced)&&c.borderStitches!=null&&c.borderStitches.SequenceEqual(border))continue;
                    Rect r=ChunkRect(w,id);float dx=r.width/w.cellsPerChunk,dz=r.height/w.cellsPerChunk;
                    var local=stamps.Where(s=>Overlap(s.bounds,Expanded(r,dx,dz))).ToList();
                    terrainTimings.Lap("Terrain: change checks + local stamp selection");
                    // Only LOD0: measure callback work, including shared height-cache
                    // lookup (the emitter's own hits do not call these). Remainder includes topology,
                    // cache lookup, clipping search, compaction and instrumentation.
                    long heightTicks=0,cutTicks=0;
                    var sampleHeight=HeightSampler(w,state,id,local);
                    if(staged)foreach(var step in PrefetchHeights(w,state,id,local,LTHeightJobMath.FinePoints(r,balanced)))
                    {terrainTimings.Pause();timer.Stop();yield return step;timer.Start();terrainTimings.Resume();}
                    float TimedHeight(float x,float z)
                    {
                        long start=Stopwatch.GetTimestamp();
                        try{return sampleHeight(x,z);}
                        finally{heightTicks+=Stopwatch.GetTimestamp()-start;}
                    }
                    bool TimedCut(float x,float z)
                    {
                        long start=Stopwatch.GetTimestamp();
                        try{return CutTerrainAt(w,local,x,z);}
                        finally{cutTicks+=Stopwatch.GetTimestamp()-start;}
                    }
                    long emitStart=Stopwatch.GetTimestamp();
                    LTStampMesh.Emit(r,new Vector2(w.source.size.x,w.source.size.z),budget,forest,id,balanced,TimedHeight,TimedCut,out var vertices,out var normals,out var uv,out var indices,TransitionDiagonals(w,id),lodProgress);
                    long emitTicks=Stopwatch.GetTimestamp()-emitStart;
                    terrainTimings.Lap("Terrain: emit triangles + height / cut evaluation");
                    double tickMs=1000.0/Stopwatch.Frequency;
                    terrainTimings.Add("Emit LOD0: height callbacks",heightTicks*tickMs);
                    terrainTimings.Add("Emit LOD0: cut callbacks",cutTicks*tickMs);
                    terrainTimings.Add("Emit LOD0: remaining mesh work + timing overhead",Math.Max(0,emitTicks-heightTicks-cutTicks)*tickMs);
                    affected?.Add(id);
                    if(staged)ReadDependencies(new HashSet<int>{id});
                    pending.Add(new PendingMesh{chunk=c,id=id,vertices=vertices,normals=normals,uv=uv,indices=indices,raw=plans[id],balanced=balanced,border=border});
                    terrainTimings.Lap("Terrain: stage output");
                    if(staged){timer.Stop();yield return LTBuildStep.Working;timer.Start();}
                }
                Stage("Terrain planning + LOD0");

                // With staged builds, prepare only the topology required to show
                // LOD0 safely next to neighbours that still use coarse levels.
                // Manual/validation builds prepare everything before any upload.
                if(!early)foreach(var step in LegacyLODs()){}
                foreach(var step in SpatialLODs(!early))
                    if(staged){timer.Stop();yield return step;timer.Start();}
                Stage(early?"LOD0 spatial preview":"Spatial LOD preparation");
                // Validate all bridges against proposed chunks before changing any terrain
                // assets. A failed loop match must not leave a newly cut, unbridged hole.
                // Private cache proposal: failed bridge validation cannot publish new data.
                var seamCache=new Dictionary<int,SeamChunkCache>(state.seamCache);
                var seamChanged=new HashSet<int>();
                var seamNormals=PrepareSeamNormals(w,chunks,pending,seamCache,seamChanged);
                Stage("Shared chunk seam normals");
                // Proposed cache entries stay private until all bridge checks and mesh
                // application succeed; cancellation/validation-only cannot poison it.
                var boundaryCache=new BoundaryCache(state.boundaryCache);
                var preparedRocks=PrepareTrimmedRockMeshes(w,stamps,pending,rockTimings,boundaryCache,seamNormals,seamChanged,validateOnly?null:state.rockOutputs,
                    staged?affected:null);
                Stage("Bridge / weld validation");
                if(validateOnly)
                {
                    if(measurements!=null)
                    {
                        measurements["Total preparation (no upload)"]=timer.Elapsed.TotalMilliseconds;
                        terrainTimings.CopyTo(measurements);rockTimings.CopyTo(measurements);
                    }
                    yield break;
                }

                foreach(var data in pending)
                {
                    var c=data.chunk;int id=data.id;var vertices=data.vertices;var normals=data.normals;var uv=data.uv;var indices=data.indices;
                    var collider = c.GetComponent<MeshCollider>(); collider.sharedMesh=null;
                    c.mesh.Clear(); c.mesh.indexFormat = vertices.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16; c.mesh.vertices=vertices;c.mesh.uv=uv;c.mesh.triangles=indices;
                    c.mesh.RecalculateNormals();c.mesh.RecalculateBounds();c.mesh.tangents=LTStampMesh.TerrainTangents(c.mesh.normals);
                    c.lodsPending=early;c.ShowLOD(0);
                    c.GetComponent<MeshRenderer>().sharedMaterial=w.material;
                    c.sourceLeaves=data.raw;c.renderLeaves=data.balanced;c.borderStitches=data.border;EditorUtility.SetDirty(c);
                    EditorUtility.SetDirty(c.mesh);c.updatedAt=EditorApplication.timeSinceStartup;
                    state.colliders.Add(id);state.dirty.Remove(id);
                }
                if(!early)PublishLegacyLODs();
                Stage("Terrain mesh upload + normals");
                ApplySeamNormals(w,chunks,seamNormals,boundaryCache,LTPaintMath.TerrainNeighbours(seamChanged,w.chunksX,w.chunksZ));
                Stage("Apply shared normals + tangents (all LODs)");
                var spatialNeighbours=LTPaintMath.TerrainNeighbours(seamChanged,w.chunksX,w.chunksZ);
                foreach(var c in chunks)
                {
                    int id=c.z*w.chunksX+c.x;
                    if(spatialProposals.TryGetValue(id,out var spatial))
                    {if(early)LTSpatialLODBake.ApplyPreview(c,spatial);else LTSpatialLODBake.Apply(c,spatial);}
                    else if(w.UseSpatialLODs&&spatialNeighbours.Contains(id))LTSpatialLODBake.UpdateNormals(c);
                }
                Stage("Spatial LOD upload");
                ApplyTrimmedRockMeshes(w,stamps,preparedRocks,rockTimings);
                // Acknowledge only this transaction's known geometry/normal writes.
                foreach(var c in chunks)
                {
                    int id=c.z*w.chunksX+c.x;
                    if(!c.mesh||!seamCache.TryGetValue(id,out var entry))continue;
                    seamCache[id]=new SeamChunkCache{mesh=c.mesh,version=EditorUtility.GetDirtyCount(c.mesh),matrix=entry.matrix,
                        rect=entry.rect,contributions=entry.contributions};
                }
                state.seamCache=seamCache;
                var liveRockIds=new HashSet<int>(state.previous.Where(s=>s.meshStamp).Select(s=>s.id));
                foreach(int id in state.rockOutputs.Keys.Where(id=>!liveRockIds.Contains(id)).ToArray())state.rockOutputs.Remove(id);
                foreach(var data in preparedRocks)
                {
                    var s=data.stamp;var output=s.meshComponent.generatedTrimmedMesh;
                    if(data.restore||!output){state.rockOutputs.Remove(s.id);continue;}
                    state.rockOutputs[s.id]=new RockOutputCache{signature=s.signature,config=Config(w),authoring=w.displacementGeometry.RegionSignature(s.bounds,0),
                        source=s.meshSource,sourceVersion=EditorUtility.GetDirtyCount(s.meshSource),output=output,outputVersion=EditorUtility.GetDirtyCount(output)};
                }
                foreach(var data in pending)
                    if(boundaryCache.TryGetValue(data.chunk.GetInstanceID(),out var entry))
                    {
                        // No active bridge may have requested boundary preparation.
                        // Never relabel old data as valid for a newly uploaded mesh.
                        if(state.boundaryCache.TryGetValue(data.chunk.GetInstanceID(),out var old)&&ReferenceEquals(entry,old))
                            boundaryCache.RemoveChunk(data.chunk.GetInstanceID());
                        else boundaryCache[data.chunk.GetInstanceID()]=entry.WithNormals(data.chunk.mesh.normals,EditorUtility.GetDirtyCount(data.chunk.mesh),entry.matrix.inverse.transpose);
                    }
                state.boundaryCache=boundaryCache;
                Stage("Prepared rocks upload + final normals");
                w.RefreshLODCache();

                if(early)
                {
                    foreach(var data in pending)
                    {
                        int id=data.id;var c=data.chunk;
                        var masks=LTLODMesh.BoundaryMasks(c.renderLeaves,c.borderStitches);
                        var r=ChunkRect(w,id);
                        var local=stamps.Where(s=>Overlap(s.bounds,Expanded(r,r.width/w.cellsPerChunk,r.height/w.cellsPerChunk))).ToList();
                        QueueChunkLODs(w,state,c,id,masks,local);
                    }
                    Report();
                    // The base transaction is complete; coarse ownership is now
                    // per chunk. Do not requeue its dependencies after publication.
                    affected.Clear();state.build.Dependencies.Clear();
                    timer.Stop();yield return LTBuildStep.LOD0Ready;
                    yield break;
                }
                if(lodPreparedChunks>0)terrainTimings.Append($"LOD preparation: {lodPreparedChunks} chunk snapshots; {lodHeightSamples} unique height samples shared across levels (coarsening only)");
                Report();
            }
            finally{timer.Stop();if(staged)state.buildObjectsValid=null;EditorUtility.ClearProgressBar();}
        }
        static void UpdateColliders(LTWorld w, State s)
        {
            if(s.colliders.Count==0)return;
            var timer=Stopwatch.StartNew();
            foreach(var c in w.generatedRoot.GetComponentsInChildren<LTChunk>())
                if(s.colliders.Contains(c.z*w.chunksX+c.x)) c.GetComponent<MeshCollider>().sharedMesh=c.mesh;
            s.colliders.Clear();
            w.lastColliderMilliseconds=(float)timer.Elapsed.TotalMilliseconds;
        }
        [MenuItem("Tools/Local Terrain/Clean Unused Meshes")]
        public static void CleanUnusedMeshes()
        {
            var worlds=Worlds().Where(w=>!string.IsNullOrEmpty(w.outputFolder)).ToArray();
            if(worlds.Length==0){EditorUtility.DisplayDialog("Local Terrain","No generated terrain worlds found.","OK");return;}
            var used=new HashSet<string>();
            foreach(var w in worlds)
                foreach(var c in w.generatedRoot.GetComponentsInChildren<LTChunk>())
                {
                    if(c.mesh)used.Add(AssetDatabase.GetAssetPath(c.mesh));
                    if(c.lodMeshes!=null)foreach(var m in c.lodMeshes)if(m)used.Add(AssetDatabase.GetAssetPath(m));
                    if(c.spatialLOD)used.Add(AssetDatabase.GetAssetPath(c.spatialLOD));
                }
            var unused=new List<string>();
            foreach(var w in worlds)
                foreach(var guid in AssetDatabase.FindAssets("t:Mesh",new[]{w.outputFolder}))
                {
                    string path=AssetDatabase.GUIDToAssetPath(guid);
                    if(!used.Contains(path))unused.Add(path);
                }
            unused=unused.Distinct().OrderBy(p=>p).ToList();
            if(unused.Count==0){EditorUtility.DisplayDialog("Local Terrain","No unused mesh assets found.","OK");return;}
            if(!EditorUtility.DisplayDialog("Clean unused terrain meshes",$"Delete {unused.Count} unused mesh assets?\n\nThis cannot be undone from Unity.","Delete","Cancel"))return;
            AssetDatabase.StartAssetEditing();
            try{foreach(var path in unused)AssetDatabase.DeleteAsset(path);}
            finally{AssetDatabase.StopAssetEditing();AssetDatabase.Refresh();}
            Debug.Log($"Local Terrain: deleted {unused.Count} unused mesh assets.");
        }
        public static void Create(Terrain terrain,Vector3 requestedSize,int nx,int nz,int cells,Material material)
        {
            if (terrain && (Quaternion.Angle(terrain.transform.rotation,Quaternion.identity)>.01f
                || (terrain.transform.lossyScale-Vector3.one).sqrMagnitude>.000001f))
                throw new InvalidOperationException("Source Terrain must have identity rotation and scale 1.");
            if (!AssetDatabase.IsValidFolder("Assets/LocalTerrainGenerated")) AssetDatabase.CreateFolder("Assets","LocalTerrainGenerated");
            string folder="Assets/LocalTerrainGenerated/World_"+Guid.NewGuid().ToString("N").Substring(0,10);
            AssetDatabase.CreateFolder("Assets/LocalTerrainGenerated",folder.Split('/').Last());
            var source=ScriptableObject.CreateInstance<LTSource>();
            if(terrain)
            {
                var data=terrain.terrainData;source.resolution=data.heightmapResolution;source.size=data.size;
                float[,] h=data.GetHeights(0,0,source.resolution,source.resolution);
                source.heights=new float[source.resolution*source.resolution];
                for(int z=0;z<source.resolution;z++)for(int x=0;x<source.resolution;x++)source.heights[z*source.resolution+x]=h[z,x]*data.size.y;
            }
            else
            {
                source.size=new Vector3(Mathf.Max(1,requestedSize.x),Mathf.Max(1,requestedSize.y),Mathf.Max(1,requestedSize.z));
            }
            AssetDatabase.CreateAsset(source,folder+"/Source.asset");
            if(!material)
            {
                Shader shader=Shader.Find("HDRP/Lit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if(!shader) throw new InvalidOperationException("Assign a compatible material.");
                material=new Material(shader);material.name="Terrain Preview";
                if(material.HasProperty("_BaseColor"))material.SetColor("_BaseColor",new Color(.36f,.30f,.20f));
                if(material.HasProperty("_Smoothness"))material.SetFloat("_Smoothness",.2f);
                AssetDatabase.CreateAsset(material,folder+"/Preview.mat");
            }
            var go=new GameObject("Local Terrain Prototype");Undo.RegisterCreatedObjectUndo(go,"Create Local Terrain");
            var w=go.AddComponent<LTWorld>();w.source=source;w.material=material;w.chunksX=nx;w.chunksZ=nz;w.cellsPerChunk=cells;w.outputFolder=folder;
            if(terrain)go.transform.position=terrain.transform.position;
            var root=new GameObject("Generated Chunks");root.transform.SetParent(go.transform,false);w.generatedRoot=root.transform;
            for(int z=0;z<nz;z++)for(int x=0;x<nx;x++)
            {
                var g=new GameObject($"Chunk_{x}_{z}");g.transform.SetParent(root.transform,false);
                g.transform.localPosition=new Vector3(x*source.size.x/nx,0,z*source.size.z/nz);
                var c=g.AddComponent<LTChunk>();c.x=x;c.z=z;c.mesh=new Mesh();c.mesh.name=g.name;
                AssetDatabase.CreateAsset(c.mesh,folder+$"/{g.name}.asset");
                g.AddComponent<MeshFilter>();g.AddComponent<MeshRenderer>();g.AddComponent<MeshCollider>();
            }
            Refresh(w,true);Selection.activeGameObject=go;
            if(terrain)Debug.Log("Prototype created. Original Terrain is unchanged. Hide it manually and pause MicroVerse before editing the prototype to avoid overlapping surfaces.",w);
        }
    }

    public sealed class LTCreateWindow : EditorWindow
    {
        Terrain source;Material material;Vector3 terrainSize=new Vector3(192,100,192);int chunksX=3,chunksZ=3,cells=32;
        [MenuItem("Tools/Local Terrain/Create Prototype")]
        static void Open()=>GetWindow<LTCreateWindow>("Local Terrain");
        void OnGUI()
        {
            EditorGUILayout.HelpBox("Empty Source creates a flat 192 x 192 m test world. Source Terrain is copied, never modified. Use a saved, non-prefab scene.",MessageType.Info);
            source=(Terrain)EditorGUILayout.ObjectField("Source Terrain",source,typeof(Terrain),true);
            material=(Material)EditorGUILayout.ObjectField("Preview Material",material,typeof(Material),false);
            using(new EditorGUI.DisabledScope(source))
                terrainSize=EditorGUILayout.Vector3Field("Terrain Size (m)",source?source.terrainData.size:terrainSize);
            if(source)EditorGUILayout.HelpBox($"Using source Terrain size: {source.terrainData.size.x:0.##} x {source.terrainData.size.y:0.##} x {source.terrainData.size.z:0.##} m",MessageType.Info);
            chunksX=EditorGUILayout.IntSlider("Chunks X",chunksX,1,16);chunksZ=EditorGUILayout.IntSlider("Chunks Z",chunksZ,1,16);
            cells=EditorGUILayout.IntSlider("Cells Per Chunk",cells,8,128);
            EditorGUILayout.LabelField("Uniform reference triangles",(chunksX*chunksZ*cells*cells*2).ToString("N0"));
            if(GUILayout.Button("Create Prototype"))
            {
                if(UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage()!=null)
                { EditorUtility.DisplayDialog("Local Terrain","Exit Prefab Mode first.","OK");return; }
                LTEditorEngine.Create(source,terrainSize,chunksX,chunksZ,cells,material);
            }
        }
    }
    [InitializeOnLoad]
    static class LTPaintEditorUpdate
    {
        static double next;
        static void ValidateGlobalAtlasCoordinates(bool baseOnly=false)
        {
            var shader=Resources.Load<Shader>("LTGlobalLayerBake");
            if(!shader||ShaderUtil.ShaderHasError(shader))return;
            var material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
            var atlas=RenderTexture.GetTemporary(8,8,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            var probe=RenderTexture.GetTemporary(1,1,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            var readback=new Texture2D(1,1,TextureFormat.RGBA32,false,true);
            var weights=new Texture2D(1,1,TextureFormat.RGBA32,false,true);
            var array=new Texture2DArray(1,1,1,TextureFormat.RGBA32,false,true);
            array.SetPixels(new[]{Color.white},0);array.Apply();
            var previous=RenderTexture.active;
            try
            {
                material.SetFloat("_LTBaseOnly",baseOnly?1:0);
                weights.SetPixel(0,0,new Color(1,0,0,0));weights.Apply();
                material.SetTexture("_LTWeights0",weights);
                var empty=new Color(0,0,0,0);
                material.SetVector("_LTWorldSize",Vector4.one);
                material.SetVector("_LTRect",new Vector4(0,0,1,1));
                material.SetTexture("_LTColorArray",array);material.SetTexture("_LTNormalArray",array);material.SetTexture("_LTMaskArray",array);
                material.SetVector("_LTTiling0",new Vector4(1,1,0,0));
                material.SetVector("_LTFlags0",new Vector4(0,1,1,0));
                // Reuse the weights red channel as layer 0 and set the unused half to a transparent texture.
                var zero=new Texture2D(1,1,TextureFormat.RGBA32,false,true);
                try
                {
                    zero.SetPixel(0,0,empty);zero.Apply();material.SetTexture("_LTWeights1",zero);material.SetTexture("_LTWeights2",zero);
                    Color[] colors={Color.red,Color.green,Color.blue,Color.white};
                    for(int i=0;i<4;i++)
                    {
                        material.SetVector("_LTBakeRect",new Vector4((i%2)*.5f,(i/2)*.5f,.5f,.5f));
                        material.SetVector("_LTTint0",colors[i]);
                        using(var cb=new UnityEngine.Rendering.CommandBuffer())
                        {
                            cb.SetRenderTarget(atlas);cb.SetViewport(new Rect(0,0,8,8));
                            cb.DrawProcedural(Matrix4x4.identity,material,0,MeshTopology.Triangles,6);Graphics.ExecuteCommandBuffer(cb);
                        }
                    }
                    material.SetTexture("_LTProbe",atlas);material.SetVector("_LTBakeRect",new Vector4(0,0,1,1));
                    for(int i=0;i<4;i++)
                    {
                        material.SetVector("_LTProbeUV",new Vector4((i%2)*.5f+.25f,(i/2)*.5f+.25f,0,0));
                        using(var cb=new UnityEngine.Rendering.CommandBuffer())
                        {
                            cb.SetRenderTarget(probe);cb.SetViewport(new Rect(0,0,1,1));
                            cb.DrawProcedural(Matrix4x4.identity,material,2,MeshTopology.Triangles,6);Graphics.ExecuteCommandBuffer(cb);
                        }
                        RenderTexture.active=probe;readback.ReadPixels(new Rect(0,0,1,1),0,0);readback.Apply();
                        Color got=readback.GetPixel(0,0),expected=colors[i];
                        if(Mathf.Abs(got.r-expected.r)+Mathf.Abs(got.g-expected.g)+Mathf.Abs(got.b-expected.b)>.03f)
                            throw new InvalidOperationException($"Global atlas UV check failed at tile {i}: {got}, expected {expected}.");
                    }
                    Debug.Log($"Global atlas GPU UV check ({(baseOnly?"background fast path":"layer blend")}): four colored tiles sampled at their expected coordinates PASS.");
                }
                finally{UnityEngine.Object.DestroyImmediate(zero);}
            }
            finally
            {
                RenderTexture.active=previous;RenderTexture.ReleaseTemporary(atlas);RenderTexture.ReleaseTemporary(probe);
                UnityEngine.Object.DestroyImmediate(array);UnityEngine.Object.DestroyImmediate(weights);UnityEngine.Object.DestroyImmediate(readback);UnityEngine.Object.DestroyImmediate(material);
            }
        }
        [MenuItem("Tools/Local Terrain/Validate Eight Layer Shader")]
        static void ValidateShader()
        {
            foreach(string resource in new[]{"LTEightLayers","LTGlobalLayerBake","LTEightLayersTessellation"})
            {
                var shader=Resources.Load<Shader>(resource);
                if(!shader){Debug.LogError(resource+" not imported.");continue;}
                var material=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                try
                {
                    for(int variant=0;variant<(resource=="LTEightLayers"?2:1);variant++)
                    {
                        if(variant==1)material.EnableKeyword("_LT_FAR_ONLY");
                        for(int i=0;i<material.passCount;i++)ShaderUtil.CompilePass(material,i,true);
                    }
                    var errors=ShaderUtil.GetShaderMessages(shader).Where(m=>m.severity==UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).ToArray();
                    foreach(var error in errors)Debug.LogError($"{resource}: {error.message} ({error.file}:{error.line})");
                    if(errors.Length==0)Debug.Log(resource+": raster passes compiled without errors (near/far where applicable). Check appearance separately.");
                }
                finally {UnityEngine.Object.DestroyImmediate(material);}
            }
            ValidateGlobalAtlasCoordinates();
            ValidateGlobalAtlasCoordinates(true);
        }
        static LTPaintEditorUpdate()
        {
            EditorApplication.update+=Tick;
            // Save original persistent materials, never transient preview resources.
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving+=(scene,path)=>
            {
                foreach(var world in UnityEngine.Object.FindObjectsByType<LTWorld>(FindObjectsSortMode.None))
                    if(world.gameObject.scene==scene&&!world.paintBenchmarkRunning)world.RestorePaintingMaterialsForSave();
            };
        }
        static void Tick()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.timeSinceStartup<next)return;
            next=EditorApplication.timeSinceStartup+.15;
            bool repaint=false;
            foreach(var world in UnityEngine.Object.FindObjectsByType<LTWorld>(FindObjectsSortMode.None))
            {
                world.UpdatePainting();repaint|=world.enableLayerPainting&&world.isActiveAndEnabled;
                world.paintCpu.Poll();
            }
            if(repaint)SceneView.RepaintAll();
        }
    }

    [CustomEditor(typeof(LTWorld))]
    public sealed class LTWorldInspector : Editor
    {
        int tab;
        bool tessellationDebugExpanded;
        readonly HashSet<int> expandedLayers=new HashSet<int>();
        void DrawTessellation(LTWorld world)
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("regularMaskGridPreview"),new GUIContent("Регулярная сетка по маске"));
            if(serializedObject.FindProperty("regularMaskGridPreview").boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("regularMaskGridChunk"),new GUIContent("Чанк проверки X/Z"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("regularMaskGridStep"),new GUIContent("Шаг регулярных ячеек, м"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("regularMaskGridDisplacement"),new GUIContent("Displacement без дробления"));
                EditorGUILayout.HelpBox("Регулярные ячейки внутри маски, редкая сетка снаружи и переходные треугольники. LOD0 фиксирован. Шаг округляется вниз к уровню quadtree; более мелкая детализация других штампов сохраняется. Граница маски приближена ячейками; настоящие вырезы обрабатывает существующий генератор. Балансировка может затронуть соседей. Нужны покраска и Displacement слоёв. Изменяется исходная геометрия и её коллайдер; лимит вершин сохраняется.",MessageType.Warning);
                EditorGUILayout.HelpBox(serializedObject.FindProperty("regularMaskGridDisplacement").boolValue?
                    "Displacement включён на существующих вершинах через текущий GPU-шейдер с Hull=1. Дополнительного дробления нет; максимум тесселяции, длина ребра и тест одинаковых Hull-факторов не управляют этим чанком. Сила, нормали и затухание диспы сохраняются. Коллайдер не получает GPU-смещение. Требуется поддержка tessellation shaders. Снимите галочку для сравнения без диспы.":
                    "Displacement выбранного чанка отключён: проверка исходной сетки. Включите «Displacement без дробления», чтобы проверить рельеф на этой же сетке.",MessageType.Info);
            }
            if(!world.enableLayerPainting)
                EditorGUILayout.HelpBox("Для displacement слоёв включите покраску во вкладке «Текстуры».",MessageType.Warning);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enableLayerDisplacement"),new GUIContent("Displacement слоёв"));
            if(serializedObject.FindProperty("enableLayerDisplacement").boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("layerTessellationFactor"),new GUIContent("Тесселяция (максимум)"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementTargetEdgeEnabled"),new GUIContent("Тесселяция по длине ребра"));
                if(serializedObject.FindProperty("displacementTargetEdgeEnabled").boolValue)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementTargetEdgeLength"),new GUIContent("Целевая длина ребра, м"));
                    EditorGUILayout.HelpBox("Больше значение — меньше дробление. Начните с 0.25 м. Размер приблизительный: действует максимум тесселяции, затухание вдаль и экранное ограничение. Маленькие исходные рёбра получают меньший множитель. Исходная сетка и запекание не меняются.",MessageType.Info);
                }
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementFadeStart"),new GUIContent("Начало затухания, м"));
                EditorGUILayout.HelpBox("Нормали и касательные сохраняются от исходного террейна; normal map материала остаётся активной. Тесселяция меняет только позиции вершин, без дополнительного расчёта нормалей по displacement.",MessageType.Info);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementFadeEnd"),new GUIContent("Конец затухания, м"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementSeamFade"),new GUIContent("Затухание у стыка, м"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementMaskEdgeFade"),new GUIContent("Затухание у края маски, м"));
                EditorGUILayout.HelpBox("0 — выключено. Высота плавно уменьшается внутрь от границы видимой displacement-маски, включая отверстия. Цвет и уплотнение не меняются. Расстояние приближённое, по разрешению карты; внутренние границы чанков обрабатываются отдельно параметром стыка.",MessageType.Info);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("refineDisplacementFootprints"),new GUIContent("Уплотнять по покрытию слоёв"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("tessellationMaskTransition"),new GUIContent("Переход сетки у маски, м"));
                EditorGUILayout.HelpBox("0 — прежнее дробление. Снаружи маски в указанной полосе рёбра получают половину дополнительного дробления. Это запас геометрии, не расширение displacement. Общие рёбра внутри одного чанка используют одинаковый запрос; переходы чанков по-прежнему требуют проверки. Начните с 0.5 м.",MessageType.Info);
                if(serializedObject.FindProperty("refineDisplacementFootprints").boolValue)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementBaseCellSize"),new GUIContent("Шаг исходной сетки, м"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementFineBase"),new GUIContent("Мелкая основа + слабее тесселяция"));
                    if(serializedObject.FindProperty("displacementFineBase").boolValue)
                    {
                        float baseStep=LTPaintMath.DisplacementBaseStep(serializedObject.FindProperty("displacementBaseCellSize").floatValue,true);
                        float gpuFactor=LTPaintMath.DisplacementGpuFactor(serializedObject.FindProperty("layerTessellationFactor").floatValue,true);
                        EditorGUILayout.HelpBox($"Пробный режим: шаг основы /4 = {baseStep:0.###} м, максимум GPU /4 = {gpuFactor:0.##}. Уплотнение по покрытию displacement; балансировка затрагивает соседние ячейки. В затронутых чанках используются регулярные диагонали. Исходных полигонов может стать примерно в 16 раз больше в уплотняемой области; стоимость и итоговая плотность не гарантированно прежние. Лимит вершин сохраняется, коллайдер и LOD перестраиваются. Отключите тесты Hull для сравнения. Выключение возвращает прежние настройки.",MessageType.Warning);
                    }
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementBoundaryPrototype"),new GUIContent("Прототип: сетка у границ маски"));
                    if(serializedObject.FindProperty("displacementBoundaryPrototype").boolValue)
                    {
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementBoundaryChunk"),new GUIContent("Тестовый чанк X/Z"));
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementBoundaryCellSize"),new GUIContent("Шаг у границы, м"));
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementTransitionDiagonals"),new GUIContent("Диагонали вместо исходного веера"));
                        EditorGUILayout.HelpBox("Весь тестовый чанк: одинаковые диагонали обычных ячеек вместо чередующихся звёзд на 8 треугольников; переходные ячейки без центрального веера. Плотность и граничные вершины сохраняются. Меняется триангуляция исходной поверхности (также коллайдера), но не алгоритм GPU-тесселяции. На неплоских ячейках форма может немного измениться. Выключите для сравнения.",MessageType.Info);
                        EditorGUILayout.HelpBox("Только выбранный чанк: смешанные ячейки маски получают более мелкую исходную сетку. Минимум 0.5 м, не крупнее основного шага. Пустые области сохраняют прежние правила; балансировка может добавить переходные полигоны, в том числе у соседнего чанка. Общий лимит вершин сохраняется. Это приближение контура, не разрезание по каждому пикселю. Выключение возвращает прежние правила построения.",MessageType.Info);
                    }
                }
                EditorGUILayout.HelpBox("Экспериментальный GPU displacement. Уплотнение учитывает покрытие, шум и фильтры слоёв; у границ остаётся запас сетки. Коллайдер не получает displacement; у края чанка смещение затухает. Облегчённый фон не смещается.",MessageType.Info);
            }
            else
            {
                EditorGUILayout.LabelField("Дальность визуального продавливания",EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementFadeStart"),new GUIContent("Начало затухания, м"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementFadeEnd"),new GUIContent("Конец затухания, м"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementSeamFade"),new GUIContent("Затухание у стыка, м"));
                EditorGUILayout.HelpBox("Продавливание включается отдельно у слоя и не требует heightmap displacement. У стыков чанков след плавно ослабляется по существующему защитному затуханию.",MessageType.None);
            }
            serializedObject.ApplyModifiedProperties();
            DrawDisplacementLayers(world);
            if(GUILayout.Button("Сбросить следы продавливания")){world.ClearDeformation();SceneView.RepaintAll();}
            EditorGUILayout.Space(8);
            bool debugActive=world.showDisplacementCoverage||world.uniformHullDiagnostic||world.forceHullOneDiagnostic;
            tessellationDebugExpanded=EditorGUILayout.Foldout(tessellationDebugExpanded,
                debugActive?"Отладка тесселяции (АКТИВНА)":"Отладка тесселяции",true);
            if(debugActive && GUILayout.Button("Выключить всю отладку тесселяции"))
            {
                world.uniformHullDiagnostic=false;
                world.SetHullOneDiagnostic(false);
                world.ApplyUniformHullDiagnostic();
                world.SetDisplacementCoverageDebug(false);
                SceneView.RepaintAll();
            }
            if(!tessellationDebugExpanded)return;
            if(!string.IsNullOrEmpty(world.paintStatus))EditorGUILayout.HelpBox(world.paintStatus,MessageType.Info);
            bool coverageDebug=EditorGUILayout.Toggle("Показать маску уплотнения",world.showDisplacementCoverage);
            EditorGUI.BeginChangeCheck();
            world.uniformHullDiagnostic=EditorGUILayout.Toggle("Тест: одинаковые факторы Hull",world.uniformHullDiagnostic);
            if(world.uniformHullDiagnostic)
            {
                world.uniformHullChunk=EditorGUILayout.Vector2IntField("Тестовый Hull-чанк X/Z",world.uniformHullChunk);
                world.uniformHullFactor=EditorGUILayout.Slider("Одинаковый фактор",world.uniformHullFactor,1,63);
                EditorGUILayout.HelpBox("Временный тест только выбранного displacement-чанка. Все три ребра и внутренность получают один фактор: без масочного, экранного, дистанционного и размерного уменьшения. Отсечение невидимых патчей остаётся; высота, маска и затухание displacement не меняются. Начните с 16. Пустые участки чанка тоже дробятся: возможна высокая GPU-нагрузка. На границе с соседним чанком факторы могут не совпадать — оценивайте внутреннюю область. Fractional odd округляет дробление к нечётному значению, максимум 63. Выключение возвращает обычный Hull; состояние не сохраняется.",MessageType.Warning);
            }
            if(EditorGUI.EndChangeCheck()){world.ApplyUniformHullDiagnostic();SceneView.RepaintAll();}
            bool hullProbe=EditorGUILayout.Toggle("Тест: Hull возвращает 1",world.forceHullOneDiagnostic);
            if(hullProbe!=world.forceHullOneDiagnostic)
            {
                if(hullProbe)world.uniformHullDiagnostic=false;
                world.SetHullOneDiagnostic(hullProbe);world.ApplyUniformHullDiagnostic();SceneView.RepaintAll();
            }
            if(hullProbe)
            {
                EditorGUILayout.HelpBox("Временный тест нашего hull-шейдера. Оставьте обычный фактор 8, выключите маску и сравните Wireframe с этой галочкой и без неё в одном ракурсе вблизи поверхности. Исходная сетка не меняется. Сама запись Hull-тест = 1 ещё не доказывает исполнение шейдера.",MessageType.Info);
                int probeCount=0;
                foreach(var status in world.HullOneDiagnosticStatus){probeCount++;EditorGUILayout.HelpBox(status,MessageType.Info);}
                if(probeCount==0)EditorGUILayout.HelpBox("Активных displacement-чанков нет — этот тест сейчас не показателен.",MessageType.Warning);
            }
            if(coverageDebug!=world.showDisplacementCoverage)
            {
                world.SetDisplacementCoverageDebug(coverageDebug);SceneView.RepaintAll();
            }
            if(coverageDebug)EditorGUILayout.HelpBox("Обычный Shaded, не Wireframe. Белое — карта уплотнения GPU, чёрное — пусто. Показан mip 0 до запаса по границам треугольников; это не готовая сетка. Режим временный, глобальные карты не меняет.",MessageType.Info);
            if(coverageDebug)
            {
                int currentMode=world.displacementSurfaceProbe>=6?world.displacementSurfaceProbe-1:world.displacementHullReasons?4:world.displacementCoverageCoordinateProbe?3:world.displacementCoverageControlColor?1:world.displacementCoverageFixedProjection?2:0;
                int diagnosticMode=EditorGUILayout.Popup("Режим диагностики",currentMode,new[]{"Маска на террейне","Контрольный цвет (без маски)","Текстура — экранные плитки","Проверка координат маски","Причины дробления (Hull)","UV — шахматка без освещения","Серый — без normal map","Серый — с normal map","Серый — реальные грани","Нормаль — normal map (TS)","Нормаль — итоговая (WS)","Нормаль — исходный террейн (WS)","Базис — tangent (WS)","Базис — bitangent (WS)","Базис — знак ориентации","Грани — лицевая / обратная"});
                if(diagnosticMode!=currentMode)
                {
                    world.displacementCoverageControlColor=diagnosticMode==1;
                    world.displacementCoverageFixedProjection=diagnosticMode==2;
                    world.displacementCoverageCoordinateProbe=diagnosticMode==3;
                    world.displacementHullReasons=diagnosticMode==4;
                    world.displacementSurfaceProbe=diagnosticMode>=5?diagnosticMode+1:0;
                    world.SetDisplacementCoverageDebug(true);SceneView.RepaintAll();
                }
                if(diagnosticMode==1)EditorGUILayout.HelpBox("Ожидается сплошной бирюзовый цвет на покрашенных чанках, даже без displacement. Маска и координаты в этом выводе не читаются. Режим диагностики в информации материала должен быть 2. Это не исправление сетки, а проверка вывода шейдера.",MessageType.Info);
                if(diagnosticMode==2)EditorGUILayout.HelpBox("Маска повторяется плитками 256×256 пикселей на экране, только на чанках с картой. UV меша, LTWorld и границы области не используются. Это проверка чтения текстуры, не её расположения в мире. Режим материала: 3.",MessageType.Info);
                int count=0;
                if(diagnosticMode>=5 && diagnosticMode<=8)EditorGUILayout.HelpBox("Сравнивайте в одном ракурсе, Hull=1 выключен. Шахматка: клетки 0.25 м в исходных координатах слоёв, яркость компенсирует экспозицию HDRP; постобработка и туман остаются. Серые режимы: цвет 0.4, AO=1, metallic=0, smoothness=0. «Реальные грани»: нормаль из производных конечной позиции, без normal map; сама геометрия не меняется. Остальные серые режимы используют базис исходного террейна. Снимите «Показать маску уплотнения», чтобы вернуть материал.",MessageType.Info);
                if(diagnosticMode>=9)EditorGUILayout.HelpBox("Цветовая диагностика без освещения и теней; туман и постобработка остаются. Сравните displacement 0 и 0.1 в одном ракурсе, Hull=1 выключен. TS — смешанная normal map до преобразования; итоговая WS — нормаль для освещения; исходный террейн — нормаль до обработки обратной стороны. Tangent/bitangent — базис преобразования. XYZ кодируются в RGB от 0 до 1. Пурпурный — неверный/нулевой вектор. Знак ориентации: зелёный +, красный − (сам по себе минус не ошибка). Грани: зелёный — лицевая, красный — обратная, жёлтый — вариант шейдера не передаёт сторону грани; отсечённые грани не видны. Начните с TS и итоговой WS. Новых чтений текстур эти режимы не добавляют. Снимите «Показать маску уплотнения», чтобы вернуть материал.",MessageType.Info);
                if(diagnosticMode==4)EditorGUILayout.HelpBox("Shaded: синий — пусто, зелёный — пересечение с маской (с запасом в одну ячейку), красный — лимит обхода; серый — проверка не требовалась (фактор ≤ 1), жёлтый — включён тест Hull=1. Цвет одинаков для всего исходного треугольника, включая его тёмные участки. Красный имеет приоритет, если лимит сработал хотя бы для одного дробимого ребра/внутренности. Это реальный результат Hull, не повторное чтение маски в пикселе. Выключите тест Hull=1 и оставьте тесселяцию 8; смотрите вблизи, в зоне 5–15 м.",MessageType.Info);
                if(diagnosticMode==3)EditorGUILayout.HelpBox("Без чтения текстуры: красный — вне прямоугольника маски; зелёно-синий градиент — внутри; жёлтый — неверные размеры/координаты. На чанках без карты остаётся чёрный. Режим материала: 4.",MessageType.Info);
                foreach(var diagnostic in world.CoverageDiagnostics)
                {
                    count++;
                    EditorGUILayout.LabelField(diagnostic.label,EditorStyles.boldLabel);
                    EditorGUILayout.HelpBox(diagnostic.status,MessageType.Info);
                    if(diagnostic.preview)
                    {
                        var previewRect=GUILayoutUtility.GetAspectRect(1,GUILayout.MaxHeight(350));
                        EditorGUI.DrawPreviewTexture(previewRect,diagnostic.preview,null,ScaleMode.ScaleToFit);
                    }
                }
                if(count==0)EditorGUILayout.HelpBox("Карты уплотнения отсутствуют. Дождитесь обновления покраски; проверьте Displacement, назначение слоя и сообщения Console. Это не результат чтения чёрной маски — самой карты пока нет.",MessageType.Warning);
            }
        }
        static bool IsDisplacementLayerProperty(string name)
            => name=="displacement" || name=="displacementAmplitude" || name=="displacementCenter" || name=="displacementSmoothingMip";
        void DrawDisplacementLayers(LTWorld world)
        {
            var layers=new List<LTSurfaceLayer>();
            if(world.baseLayer)layers.Add(world.baseLayer);
            foreach(var stamp in world.CollectPaintStamps())
                stamp.AppendLayers(layers,true);
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Displacement используемых слоёв",EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("gpuMudSimulation"),new GUIContent("Грязь: GPU-симуляция"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("mudNearDistance"),new GUIContent("Грязь: граница 30 Гц, м"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("mudFarDistance"),new GUIContent("Грязь: граница 10 Гц, м"));
            EditorGUILayout.HelpBox("Изменения asset слоя применяются ко всем его использованиям. Текстуры и смешивание Height остаются во вкладке «Текстуры».",MessageType.None);
            foreach(var layer in layers)
            {
                using(new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.ObjectField("Слой",layer,typeof(LTSurfaceLayer),false);
                    using var settings=new SerializedObject(layer);settings.Update();
                    EditorGUILayout.PropertyField(settings.FindProperty("displacement"),new GUIContent("Displacement (геометрия)"));
                    EditorGUILayout.PropertyField(settings.FindProperty("displacementAmplitude"),new GUIContent("Диапазон, м"));
                    EditorGUILayout.PropertyField(settings.FindProperty("displacementCenter"),new GUIContent("Нулевой уровень"));
                    EditorGUILayout.PropertyField(settings.FindProperty("displacementSmoothingMip"),new GUIContent("Сглаживание высоты (Mip)"));
                    LTSurfaceLayerInspector.DrawDeformation(settings);
                    settings.ApplyModifiedProperties();
                }
            }
        }
        void DrawTextures(LTWorld world)
        {
            EditorGUILayout.HelpBox("До 12 слоёв на чанк или область скалы, включая базовый. Скалы с Use Terrain Material получают слои по их положению над террейном; фильтры оцениваются по террейну.",MessageType.Info);
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enableLayerPainting"),new GUIContent("Включить покраску"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("baseLayer"),new GUIContent("Фоновый слой"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("triplanarTexturing"),new GUIContent("Трипланар для террейна и скал"));
            if(serializedObject.FindProperty("triplanarTexturing").boolValue)
                EditorGUILayout.HelpBox("Три проекции текстур вместо одной: больше выборок вблизи. Для дальнего террейна запеките и сохраните глобальные карты заново: они учитывают высоту и нормали исходной сетки. Скалы остаются на детальных слоях (навесы не помещаются в X/Z-атлас). Маски и геометрический displacement остаются X/Z; displacement на скалах выключен.",MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("lightweightBackground"),new GUIContent("Облегчённый фон", "Albedo + Normal; Mask Map фона не читается. AO = 1, Height = 0.5, Smoothness и Metallic — значения слоя. Применяется и к штампам, использующим тот же фоновый слой."));
            EditorGUILayout.HelpBox("Настройки displacement и тесселяции находятся во вкладке «Тесселяция».",MessageType.None);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("layerHeightBlend"),new GUIContent("Смешивание по Height"));
            EditorGUILayout.Space(8);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enableGlobalLayerMaps"),new GUIContent("Глобальные карты вдали"));
            if(serializedObject.FindProperty("enableGlobalLayerMaps").boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalLayerResolution"),new GUIContent("Разрешение атласа", "Округляется вверх до степени двойки. Две карты RGBA8 с mipmaps: примерно 43 МБ при 2048, 171 МБ при 4096."));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalLayerStart"),new GUIContent("Начало перехода, м"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("globalLayerEnd"),new GUIContent("Конец перехода, м"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("farLayerSmoothness"),new GUIContent("Дальний Smoothness"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("farLayerMetallic"),new GUIContent("Дальний Metallic"));
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(serializedObject.FindProperty("savedGlobalBake"),new GUIContent("Сохранённое запекание"));
                if(EditorGUI.EndChangeCheck()){serializedObject.ApplyModifiedProperties();world.ReleasePainting();}
            }
            serializedObject.ApplyModifiedProperties();
            if(world.enableGlobalLayerMaps)
            {
                if(!string.IsNullOrEmpty(world.globalLayerStatus))EditorGUILayout.HelpBox(world.globalLayerStatus,MessageType.Info);
                using(new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("Global Albedo",world.globalAlbedoPreview,typeof(Texture),false);
                    EditorGUILayout.ObjectField("Global Normal",world.globalNormalPreview,typeof(Texture),false);
                }
                using(new EditorGUI.DisabledScope(world.paintBenchmarkRunning))
                    if(GUILayout.Button("Запечь в память (без сохранения)"))world.BakeGlobalLayerMaps();
                using(new EditorGUI.DisabledScope(world.paintBenchmarkRunning||Application.isPlaying))
                    if(GUILayout.Button("Запечь и сохранить глобальные карты…"))LTGlobalBakeSaving.BakeAndSave(world);
            }
            if(!string.IsNullOrEmpty(world.paintStatus))EditorGUILayout.HelpBox(world.paintStatus,MessageType.Info);
            using(new EditorGUI.DisabledScope(world.paintCpu.Running||world.paintBenchmarkRunning||!world.enableLayerPainting))
                if(GUILayout.Button("CPU-замер покраски — 10 секунд"))world.paintCpu.Start(world.name);
            EditorGUILayout.HelpBox(world.paintCpu.Status,MessageType.Info);
            if(GUILayout.Button("Добавить Layer Stamp"))
            {
                var go=new GameObject("Layer Stamp");Undo.RegisterCreatedObjectUndo(go,"Add Layer Stamp");
                go.transform.SetParent(world.transform,false);
                if(world.source)go.transform.localPosition=new Vector3(world.source.size.x*.5f,0,world.source.size.z*.5f);
                Undo.AddComponent<LTPaintStamp>(go);Selection.activeGameObject=go;
            }
            var stamps=world.CollectPaintStamps();
            var layers=new List<LTSurfaceLayer>();
            if(world.baseLayer)layers.Add(world.baseLayer);
            foreach(var stamp in stamps)stamp.AppendLayers(layers,true);
            EditorGUILayout.LabelField("Используемые слои",layers.Count.ToString());
            int missing=stamps.Count(s=>!s.HasPaintLayers);
            if(missing>0)EditorGUILayout.HelpBox($"Штампов без слоя: {missing}",MessageType.Warning);
            foreach(var layer in layers)
            {
                var users=stamps.Where(s=>s.EffectiveLayer==layer||s.Road&&s.Road.wheelLayer==layer).ToArray();
                using(new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using(new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(layer.baseColorMap?layer.baseColorMap:Texture2D.grayTexture,GUILayout.Width(42),GUILayout.Height(42));
                        bool open=EditorGUILayout.Foldout(expandedLayers.Contains(layer.GetInstanceID()),layer.name,true);
                        if(open)expandedLayers.Add(layer.GetInstanceID());else expandedLayers.Remove(layer.GetInstanceID());
                        if(GUILayout.Button("Найти",GUILayout.Width(55)))EditorGUIUtility.PingObject(layer);
                    }
                    EditorGUILayout.LabelField($"Штампов: {users.Length}; активных: {users.Count(s=>s.isActiveAndEnabled)}"+(world.baseLayer==layer?" • базовый":""));
                    if(!expandedLayers.Contains(layer.GetInstanceID()))continue;
                    EditorGUILayout.HelpBox("Изменения этого asset слоя применяются ко всем его использованиям.",MessageType.None);
                    using var settings=new SerializedObject(layer);settings.Update();
                    var property=settings.GetIterator();bool enter=true;
                    while(property.NextVisible(enter))
                    {
                        enter=false;if(property.name=="m_Script"||IsDisplacementLayerProperty(property.name))continue;
                        EditorGUILayout.PropertyField(property,true);
                    }
                    settings.ApplyModifiedProperties();
                    foreach(var stamp in users)
                        using(new EditorGUI.DisabledScope(true))EditorGUILayout.ObjectField("Штамп",stamp,typeof(LTPaintStamp),true);
                }
            }
            EditorGUILayout.HelpBox("Порядок покраски задаётся порядком штампов в иерархии. Это список ссылок, не каналы маски. Удаление штампа не удаляет assets или текстуры.",MessageType.None);
        }
        void DrawArrays(LTWorld world)
        {
            EditorGUILayout.HelpBox("Три общих массива на LT World вместо 36 отдельных привязок. До 12 слоёв на чанк. Актуальный сохранённый комплект загружается без упаковки; иначе редактор создаёт временный предпросмотр. Исходные assets не изменяются.",MessageType.Info);
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("arrayColorResolution"),new GUIContent("Цвет — разрешение"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("arrayNormalResolution"),new GUIContent("Нормали — разрешение"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("arrayMaskResolution"),new GUIContent("Маски — разрешение"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("arrayTrilinear"),new GUIContent("Трилинейная фильтрация"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("arrayAnisotropy"),new GUIContent("Анизотропия"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("arrayMemoryBudgetMiB"),new GUIContent("Бюджет упаковки GPU, МиБ"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("savedLayerArrays"),new GUIContent("Сохранённый комплект"));
            serializedObject.ApplyModifiedProperties();
            var palette=LTLayerArrayBakeAsset.Palette(world);
            long bytes=LTLayerTextureArrays.EstimateBytes(palette.Count,(int)world.arrayColorResolution,(int)world.arrayNormalResolution,(int)world.arrayMaskResolution);
            EditorGUILayout.LabelField("Слоёв в палитре",palette.Count.ToString());
            EditorGUILayout.LabelField("Объём новых массивов GPU",$"{bytes/1048576f:F1} МиБ");
            EditorGUILayout.HelpBox("RGBA32, полная цепочка mipmaps. Цвет: sRGB; нормали: RGB linear; маски: linear. Упаковка выполняется GPU без Read/Write исходников. При обновлении одновременно живут старые и новые массивы плюс временная карта — бюджет учитывает этот пик, но не исходные текстуры и остальной рендер. Изменение разрешения может изменить детализацию смешивания/высоты; CPU-фильтры растительности читают оригиналы.",MessageType.Info);
            EditorGUILayout.HelpBox(world.arrayPackStatus??"Ещё не упаковано.",MessageType.Info);
            if(world.savedLayerArrays)
            {
                bool current=false;string state;
                try{current=world.savedLayerArrays.Matches(world,LTLayerArrayBakeAsset.Palette(world),out state);}
                catch(System.Exception error){state=error.Message;}
                EditorGUILayout.HelpBox(state,current?MessageType.Info:MessageType.Warning);
            }
            else EditorGUILayout.HelpBox("Нет сохранённого комплекта. Перед сборкой игры нажмите «Запечь и сохранить».",MessageType.Warning);
            EditorGUILayout.HelpBox("Сохранение создаёт новую папку в Assets/LocalTerrainGenerated/LayerArrays. Предыдущие версии остаются. Сохраняемые RGBA32-массивы содержат также CPU-копию данных (примерно такой же объём, как GPU). После назначения комплекта сохраните сцену. Изменение фильтрации сохранённого комплекта требует нового запекания.",MessageType.None);
            using(new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Color array",world.arrayColorPreview,typeof(Texture2DArray),false);
                EditorGUILayout.ObjectField("Normal array",world.arrayNormalPreview,typeof(Texture2DArray),false);
                EditorGUILayout.ObjectField("Mask array",world.arrayMaskPreview,typeof(Texture2DArray),false);
            }
            using(new EditorGUI.DisabledScope(!world.enableLayerPainting||world.paintBenchmarkRunning))
                if(GUILayout.Button("Перепаковать массивы"))world.RepackLayerArrays();
            using(new EditorGUI.DisabledScope(LTLayerArraySaving.IsSaving||Application.isPlaying||EditorApplication.isCompiling||!world.enableLayerPainting||world.paintBenchmarkRunning))
                if(GUILayout.Button("Запечь и сохранить"))LTLayerArraySaving.BakeAndSave(world);
            if(LTLayerArraySaving.IsSaving&&GUILayout.Button("Отменить сохранение"))LTLayerArraySaving.Cancel();
        }
        public override void OnInspectorGUI()
        {
            var w=(LTWorld)target;
            tab=GUILayout.Toolbar(tab,new[]{"Геометрия","Текстуры","Тесселяция","Диагностика","Массивы"});
            if(tab==1){DrawTextures(w);return;}
            if(tab==2){DrawTessellation(w);return;}
            if(tab==3){DrawDiagnostics(w);return;}
            if(tab==4){DrawArrays(w);return;}
            EditorGUILayout.HelpBox("Запечённые LOD: целый чанк или участки внутри чанка. WithinChunk адаптивно стыкует соседей, в том числе между чанками. Коллайдер всегда использует LOD0.",MessageType.Info);
            using(new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Source snapshot",w.source,typeof(LTSource),false);
                EditorGUILayout.LabelField("Layout",$"{w.chunksX} x {w.chunksZ}; {w.cellsPerChunk} cells/chunk");
            }
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("material"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("cellsPerChunk"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("adaptive"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxHeightError"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxVerticesPerChunk"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enableLODs"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("terrainLODMode"),new GUIContent("Режим LOD","WholeChunk — весь чанк; WithinChunk — независимые участки внутри чанка."));
            if(serializedObject.FindProperty("terrainLODMode").enumValueIndex==1)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("lodPatchDivisions"),new GUIContent("Участков по стороне","Округляется вверх до 1/2/4/8. Четыре — 16 участков в одном чанке."));
                EditorGUILayout.HelpBox("Выбор по расстоянию до участка; соседние ячейки согласованы 2:1. Плотные границы LOD0 не закреплены. Один MeshRenderer на чанк, обновляются только индексы. Больше участков — точнее выбор по расстоянию и больше служебных данных. После перехода со старого формата один раз перестройте геометрию. Preview LODs использует камеру Scene.",MessageType.Info);
                if(!string.IsNullOrEmpty(w.spatialLODStatus))EditorGUILayout.HelpBox(w.spatialLODStatus,MessageType.Warning);
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("lods"),true);
            if(w.enableLODs)EditorGUILayout.HelpBox("Грунтовки и грунтовые перекрёстки упрощаются вместе с terrain по Max Height Error каждого LOD. Размер ячейки дороги задаёт только LOD0 и коллайдер. Под отдельным асфальтовым полотном сетка terrain остаётся подробной для защиты от пересечений; у полотна свои LOD.",MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("lodCamera"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("lodHysteresis"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("previewLODs"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("forceLOD"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("autoUpdate"));
            EditorGUILayout.HelpBox("Автообновление — после отпускания мыши. Сначала LOD0 и стыки, затем остальные LOD поэтапно. Это ещё главный поток, не Burst: один тяжёлый этап может вызвать паузу. Ручная перестройка и сохранение завершают весь расчёт.",MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("showDebug"));serializedObject.ApplyModifiedProperties();
            if(GUILayout.Button("Add Height Stamp"))
            {
                var go=new GameObject("Height Stamp");Undo.RegisterCreatedObjectUndo(go,"Add Height Stamp");
                go.transform.SetParent(w.transform,false);
                go.transform.localPosition=new Vector3(w.source.size.x*.5f,5,w.source.size.z*.5f);
                go.AddComponent<LTHeightStamp>();Selection.activeGameObject=go;
            }
            if(GUILayout.Button("Add Density-Only Stamp"))
            {
                var go=new GameObject("Density Stamp");Undo.RegisterCreatedObjectUndo(go,"Add Density Stamp");go.transform.SetParent(w.transform,false);
                go.transform.localPosition=new Vector3(w.source.size.x*.5f,0,w.source.size.z*.5f);
                var stamp=go.AddComponent<LTHeightStamp>();stamp.affectHeight=false;stamp.densityZone=true;stamp.shape=LTStampShape.Rectangle;stamp.size=new Vector2(8,8);
                Selection.activeGameObject=go;
            }
            if(w.GetComponentsInChildren<LTDensityZone>(true).Length>0&&GUILayout.Button("Convert Legacy Density Zones to Stamps"))
            {
                foreach(var zone in w.GetComponentsInChildren<LTDensityZone>(true))
                {
                    if(!zone.enabled)continue;
                    var stamp=zone.GetComponent<LTHeightStamp>();
                    if(stamp){Debug.LogWarning("Existing height stamp found: enable Density Zone manually on this object.",zone);continue;}
                    stamp=Undo.AddComponent<LTHeightStamp>(zone.gameObject);stamp.affectHeight=false;stamp.densityZone=true;stamp.densityCellSizeMin=zone.cellSize;stamp.densityCellSizeMax=zone.cellSize;stamp.size=zone.size;stamp.shape=LTStampShape.Rectangle;
                    Undo.RecordObject(zone,"Disable legacy density zone");zone.enabled=false;EditorUtility.SetDirty(stamp);EditorUtility.SetDirty(zone);
                }
                // Respect Auto Update pause; next editor tick detects the conversion.
            }
            if(GUILayout.Button("Apply Pending Changes"))LTEditorEngine.Refresh(w,false);
            if(GUILayout.Button("Rebuild All + Save Meshes"))LTEditorEngine.Refresh(w,true);
        }
        void DrawDiagnostics(LTWorld w)
        {
            if(GUILayout.Button("Benchmark Preparation (2 warm-ups + 7 runs)"))LTEditorEngine.BenchmarkPreparation(w);
            if(!string.IsNullOrEmpty(w.benchmarkTimings))EditorGUILayout.HelpBox(w.benchmarkTimings,MessageType.None);
            if(w.generatedRoot)
            {
                var chunks=w.generatedRoot.GetComponentsInChildren<LTChunk>();
                EditorGUILayout.LabelField("Generated vertices",chunks.Where(c=>c.mesh).Sum(c=>c.mesh.vertexCount).ToString("N0"));
                EditorGUILayout.LabelField("Generated triangles",chunks.Where(c=>c.mesh&&c.mesh.subMeshCount>0).Sum(c=>(long)c.mesh.GetIndexCount(0)/3).ToString("N0"));
                var testChunk=chunks.FirstOrDefault(c=>c.x==w.displacementBoundaryChunk.x&&c.z==w.displacementBoundaryChunk.y);
                if(testChunk&&testChunk.mesh)
                    EditorGUILayout.LabelField("Boundary test chunk (source)",testChunk.mesh.vertexCount.ToString("N0")+" vertices / "+
                        (testChunk.mesh.subMeshCount>0?((long)testChunk.mesh.GetIndexCount(0)/3).ToString("N0"):"0")+" triangles");
            }
            if(w.enableLODs && w.lods!=null)
            {
                for(int i=0;i<w.lods.Length;i++)
                    if(w.lods[i]==null || float.IsNaN(w.lods[i].startDistance) || float.IsInfinity(w.lods[i].startDistance) || w.lods[i].startDistance<0 ||
                        (i>0 && w.lods[i-1]!=null && w.lods[i].startDistance<=w.lods[i-1].startDistance))
                    {EditorGUILayout.HelpBox("LOD start distances must be finite, positive and strictly increasing. Automatic selection falls back to LOD0 while invalid.",MessageType.Error);break;}
            }
            if(w.generatedRoot)
            {
                var chunks=w.generatedRoot.GetComponentsInChildren<LTChunk>();
                if(w.UseSpatialLODs)
                {
                    long triangles=0;
                    foreach(var c in chunks){var f=c.GetComponent<MeshFilter>();if(f&&f.sharedMesh&&f.sharedMesh.subMeshCount>0)triangles+=(long)f.sharedMesh.GetIndexCount(0)/3;}
                    EditorGUILayout.LabelField("Spatial LOD: текущие треугольники",triangles.ToString("N0"));
                    EditorGUILayout.HelpBox("Число индексов, назначенных всем чанкам до отсечения камерой; не аппаратный счётчик GPU.",MessageType.None);
                    if(!string.IsNullOrEmpty(w.spatialLODStatus))EditorGUILayout.HelpBox(w.spatialLODStatus,MessageType.Warning);
                }
                else for(int l=0;l<4;l++)
                    EditorGUILayout.LabelField("LOD"+(l+1)+" triangles",chunks.Where(c=>c.lodMeshes!=null&&c.lodMeshes.Length>l&&c.lodMeshes[l]&&c.lodMeshes[l].subMeshCount>0).Sum(c=>(long)c.lodMeshes[l].GetIndexCount(0)/3).ToString("N0"));
            }
            EditorGUILayout.LabelField("Last mesh update",$"{w.lastUpdatedChunks} chunks / {w.lastUpdateMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Latest detection poll",$"{w.lastDetectionMilliseconds:F1} ms");
            EditorGUILayout.LabelField("Last terrain collider update",$"{w.lastColliderMilliseconds:F1} ms");
            if(!string.IsNullOrEmpty(w.lastEditorUpdateCycle))EditorGUILayout.HelpBox(w.lastEditorUpdateCycle,MessageType.None);
            if(!string.IsNullOrEmpty(w.lastEditorPaintStages))EditorGUILayout.HelpBox(w.lastEditorPaintStages,MessageType.None);
            if(!string.IsNullOrEmpty(w.lastBuildTimings))
                EditorGUILayout.HelpBox(w.lastBuildTimings,MessageType.None);
            EditorGUILayout.HelpBox("Build timings exclude detection, deferred terrain colliders and SaveAssets. Values describe the last completed operation; detection also runs while idle.",MessageType.None);
            EditorGUILayout.LabelField("Chunk IDs",w.lastUpdatedIds??"",EditorStyles.wordWrappedLabel);
            EditorGUILayout.HelpBox("World: rotation 0, scale 1. Stamp height = relative Position Y multiplied by Scale Y (including parents). Green bounds = recently rebuilt. A one-cell border halo may update a neighbour for normals. Colliders finalize after movement stops.",MessageType.None);
        }
        public override bool RequiresConstantRepaint()=>true;
    }
    [CustomEditor(typeof(LTSurfaceLayer))]
    public sealed class LTSurfaceLayerInspector : Editor
    {
        public static void DrawDeformation(SerializedObject settings)
        {
            EditorGUILayout.PropertyField(settings.FindProperty("deformation"),new GUIContent("Продавливание"));
            if(!settings.FindProperty("deformation").boolValue)return;
            EditorGUILayout.PropertyField(settings.FindProperty("deformationDepth"),new GUIContent("Глубина колеи, м"));
            EditorGUILayout.PropertyField(settings.FindProperty("deformationRecovery"),new GUIContent("Восстановление, с"));
            EditorGUILayout.PropertyField(settings.FindProperty("deformationTessellation"),new GUIContent("Тесселяция грязи"));
            EditorGUILayout.HelpBox("Визуальная колея от Terrain Deformer с коллайдером. 0 секунд — без восстановления. Heightmap displacement не обязателен. Физика и нормали исходные; скалы не продавливаются. Следы временные и сбрасываются при изменении покрытия/геометрии. Плотность затухает по дистанциям во вкладке «Тесселяция».",MessageType.Info);
        }
        int preview;
        static readonly string[] PreviewNames={"Base Color","Normal","Mask Map"};

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using(new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
            DrawTexture("baseColorMap","Base Color Map");
            EditorGUILayout.PropertyField(serializedObject.FindProperty("tint"));
            DrawTexture("normalMap","Normal Map");
            EditorGUILayout.PropertyField(serializedObject.FindProperty("normalStrength"));
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Mask: R = AO, G = Height, B = Smoothness",EditorStyles.wordWrappedLabel);
            DrawTexture("maskMap","Mask Map (RGB)");
            EditorGUILayout.PropertyField(serializedObject.FindProperty("aoStrength"),new GUIContent("AO Strength"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("heightStrength"),new GUIContent("Height Strength"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("heightOffset"),new GUIContent("Height Offset"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("displacement"),new GUIContent("Displacement (геометрия)"));
            if(serializedObject.FindProperty("displacement").boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementAmplitude"),new GUIContent("Диапазон, м"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementCenter"),new GUIContent("Нулевой уровень"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("displacementSmoothingMip"),new GUIContent("Сглаживание высоты (Mip)"));
                EditorGUILayout.HelpBox("0 — прежняя высота. Начните с 3–4: сглаживается только смещение геометрии, маска и смешивание слоёв остаются прежними. Увеличение снижает мелкие пики, но не добавляет полигонов. Есть дополнительное чтение heightmap для активного displacement-слоя.",MessageType.Info);
                var smoothingMap=serializedObject.FindProperty("maskMap").objectReferenceValue as Texture2D;
                if(smoothingMap&&smoothingMap.mipmapCount<=1&&serializedObject.FindProperty("displacementSmoothingMip").intValue>0)
                    EditorGUILayout.HelpBox("У Mask Map нет mipmaps: сглаживание пока не действует. В импорте текстуры включите Generate Mip Maps и нажмите Apply.",MessageType.Warning);
                EditorGUILayout.HelpBox("G-канал Mask Map: (Height − нулевой уровень) × диапазон. Нужен LTWorld с включённым displacement. Коллайдер не изменяется.",MessageType.None);
            }
            DrawDeformation(serializedObject);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("metallic"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("smoothness"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("tileSizeMetres"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("tileOffsetMetres"));
            LTDetailInspector.DrawList(serializedObject);
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(12);
            DrawLayerPreview((LTSurfaceLayer)target,ref preview,Mathf.Clamp(EditorGUIUtility.currentViewWidth-44,180,480));
        }

        // Shared read-only texture preview; no material/texture copies or importer changes.
        public static void DrawLayerPreview(LTSurfaceLayer layer,ref int preview,float height)
        {
            if(!layer)
            {
                EditorGUILayout.HelpBox("Назначьте Layer, чтобы увидеть превью слоя.",MessageType.None);
                return;
            }
            EditorGUILayout.LabelField("Предпросмотр текстуры",EditorStyles.boldLabel);
            preview=GUILayout.Toolbar(Mathf.Clamp(preview,0,PreviewNames.Length-1),PreviewNames);
            var texture=preview==0?layer.baseColorMap:preview==1?layer.normalMap:layer.maskMap;
            var rect=GUILayoutUtility.GetRect(1,height,GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect,new Color(.12f,.12f,.12f));
            if(texture)
            {
                if(Event.current.type==EventType.Repaint)
                    EditorGUI.DrawPreviewTexture(rect,texture,null,ScaleMode.ScaleToFit);
                EditorGUILayout.LabelField($"{texture.name} — {texture.width} × {texture.height}",EditorStyles.wordWrappedMiniLabel);
            }
            else GUI.Label(rect,"Текстура не назначена",EditorStyles.centeredGreyMiniLabel);
        }

        void DrawTexture(string propertyName,string label)
        {
            var property=serializedObject.FindProperty(propertyName);
            using(new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent(label,property.tooltip),GUILayout.MinWidth(80));
                EditorGUI.BeginChangeCheck();
                var texture=EditorGUILayout.ObjectField(property.objectReferenceValue,typeof(Texture2D),false,
                    GUILayout.Width(88),GUILayout.Height(88));
                if(EditorGUI.EndChangeCheck())property.objectReferenceValue=texture;
            }
        }
    }

    [CustomEditor(typeof(LTPaintStamp))]
    public sealed class LTPaintStampInspector : Editor
    {
        int filterTab;
        int layerPreview;
        void DrawFilter(LTPaintStamp stamp)
        {
            EditorGUILayout.Space(8);
            filterTab=GUILayout.Toolbar(filterTab,new[]{"Height","Slope","Curve","Noise"});
            if(filterTab==3)
            {
                var noise=serializedObject.FindProperty("noise");
                EditorGUILayout.PropertyField(noise.FindPropertyRelative("enabled"),new GUIContent("Включить шум"));
                using(new EditorGUI.DisabledScope(!noise.FindPropertyRelative("enabled").boolValue))
                {
                    EditorGUILayout.PropertyField(noise.FindPropertyRelative("size"),new GUIContent("Размер пятен, м"));
                    EditorGUILayout.PropertyField(noise.FindPropertyRelative("strength"),new GUIContent("Сила"));
                    EditorGUILayout.PropertyField(noise.FindPropertyRelative("threshold"),new GUIContent("Порог", "Выше порог — меньше покрытия. 0 — сплошное покрытие, 1 — убрать слой при силе 1."));
                    EditorGUILayout.PropertyField(noise.FindPropertyRelative("softness"),new GUIContent("Мягкость"));
                    EditorGUILayout.PropertyField(noise.FindPropertyRelative("seed"),new GUIContent("Seed"));
                }
                EditorGUILayout.HelpBox("Шум открывает нижние слои внутри штампа. Рисунок движется и масштабируется вместе со штампом; размер задан в его локальных метрах. Очень мелкие пятна ограничены разрешением маски чанка 257×257.",MessageType.None);
                return;
            }
            string name=filterTab==0?"heightFilter":filterTab==1?"slopeFilter":"curveFilter";
            var filter=serializedObject.FindProperty(name);
            EditorGUILayout.PropertyField(filter.FindPropertyRelative("enabled"),new GUIContent("Включить фильтр"));
            using(new EditorGUI.DisabledScope(!filter.FindPropertyRelative("enabled").boolValue))
            {
                var range=filter.FindPropertyRelative("range");var value=range.vector2Value;
                var world=stamp.GetComponentInParent<LTWorld>();
                float lower=filterTab==2?-1:0;
                float upper=filterTab==0?(world&&world.source?Mathf.Max(1,world.source.size.y):1000):filterTab==1?90:1;
                // Height fields may extend outside the imported terrain's nominal range.
                if(filterTab==0){lower=Mathf.Min(lower,value.x);upper=Mathf.Max(upper,value.y);}
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label("Range",GUILayout.Width(45));
                EditorGUI.BeginChangeCheck();
                float min=EditorGUILayout.FloatField(value.x,GUILayout.Width(65));
                float max=value.y;
                if(filterTab==0)lower=Mathf.Min(lower,min);
                EditorGUILayout.MinMaxSlider(ref min,ref max,lower,upper);
                max=EditorGUILayout.FloatField(max,GUILayout.Width(65));
                if(EditorGUI.EndChangeCheck())
                {
                    if(filterTab!=0){min=Mathf.Clamp(min,lower,upper);max=Mathf.Clamp(max,lower,upper);}
                    range.vector2Value=new Vector2(Mathf.Min(min,max),Mathf.Max(min,max));
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.PropertyField(filter.FindPropertyRelative("feather"),new GUIContent("Плавность границ"));
                if(filterTab==2)EditorGUILayout.PropertyField(serializedObject.FindProperty("curveRadius"),new GUIContent("Радиус, м"));
            }
            EditorGUILayout.HelpBox(filterTab==0?"Высота относительно LTWorld, в метрах.":filterTab==1?"Уклон: 0° — горизонтально, 90° — вертикально.":"Кривизна: −1 — впадины, 0 — плоскость, +1 — выпуклости. Радиус задаёт масштаб деталей.",MessageType.None);
        }
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Layer Stamp: покраска по XZ, порядок в иерархии задаёт наложение. Маска использует R. Включённые фильтры перемножаются.",MessageType.Info);
            var stamp=(LTPaintStamp)target;
            if(stamp.Road||stamp.Junction)
            {
                EditorGUILayout.HelpBox("Этот Layer Stamp управляется компонентом Road / Road Junction. Покрытие настраивается в нём.",MessageType.Info);
                LTSurfaceLayerInspector.DrawLayerPreview(stamp.EffectiveLayer,ref layerPreview,160);
                return;
            }
            serializedObject.Update();
            using(new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
            var layerProperty=serializedObject.FindProperty("layer");
            EditorGUILayout.PropertyField(layerProperty);
            LTSurfaceLayerInspector.DrawLayerPreview(layerProperty.objectReferenceValue as LTSurfaceLayer,ref layerPreview,160);
            EditorGUILayout.Space(6);
            DrawPropertiesExcluding(serializedObject,"m_Script","layer","heightFilter","slopeFilter","curveFilter","curveRadius","noise","deformationStepCm","deformationMaxResolution");
            if(stamp.layer&&stamp.layer.deformation)
            {
                EditorGUILayout.LabelField("Карта глубины этой области",EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("deformationStepCm"),new GUIContent("Шаг, см"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("deformationMaxResolution"),new GUIContent("Максимальная сторона"));
                if(!string.IsNullOrEmpty(stamp.deformationMapStatus))EditorGUILayout.HelpBox(stamp.deformationMapStatus,MessageType.Info);
                EditorGUILayout.HelpBox("Карта покрывает X/Z-прямоугольник области, включая её поворот и масштаб. Изменение размера/точности сбросит следы этой области. Лимит: 8 областей и 128 МиБ буферов глубины на мир. Фоновый слой без штампа использует прежнюю карту чанка.",MessageType.None);
            }
            DrawFilter(stamp);
            serializedObject.ApplyModifiedProperties();
            if(!stamp.GetComponentInParent<LTWorld>(true))EditorGUILayout.HelpBox("Помести штамп внутрь LTWorld, чтобы слой появился в списке.",MessageType.Warning);
            if(GUILayout.Button("Создать и назначить слой…"))
            {
                string path=EditorUtility.SaveFilePanelInProject("Новый слой поверхности","Surface Layer","asset","Выберите папку слоя");
                if(string.IsNullOrEmpty(path))return;
                // Never overwrite an existing user asset.
                path=AssetDatabase.GenerateUniqueAssetPath(path);
                var layer=ScriptableObject.CreateInstance<LTSurfaceLayer>();
                AssetDatabase.CreateAsset(layer,path);
                Undo.RecordObject(stamp,"Assign surface layer");stamp.layer=layer;
                PrefabUtility.RecordPrefabInstancePropertyModifications(stamp);
                EditorUtility.SetDirty(stamp);EditorUtility.SetDirty(layer);AssetDatabase.SaveAssets();
                EditorGUIUtility.PingObject(layer);
            }
        }
    }
    [CustomEditor(typeof(LTHeightStamp))]
    public sealed class LTStampInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("affectHeight"));
            if(serializedObject.FindProperty("affectHeight").boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("operation"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("strength"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("edgeFalloff"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("blendAmount"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("multiplyReference"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("maskIsHeight"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("mask"));
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("shape"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("size"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("densityZone"));
            if(serializedObject.FindProperty("densityZone").boolValue)
            {
                var minProp=serializedObject.FindProperty("densityCellSizeMin");
                var maxProp=serializedObject.FindProperty("densityCellSizeMax");
                float min=Mathf.Max(.000001f,minProp.floatValue), max=Mathf.Max(min,maxProp.floatValue);
                EditorGUILayout.BeginHorizontal();
                min=EditorGUILayout.FloatField("Min Cell Size (m)",min);
                GUILayout.Space(12f);
                max=EditorGUILayout.FloatField(new GUIContent("Max Cell Size (m)"),max);
                EditorGUILayout.EndHorizontal();
                minProp.floatValue=Mathf.Max(.000001f,min);maxProp.floatValue=Mathf.Max(min,max);
            }
            serializedObject.ApplyModifiedProperties();var s=(LTHeightStamp)target;
            if(s.densityZone)
            {
                double width=s.size.x*Math.Abs(s.transform.lossyScale.x),depth=s.size.y*Math.Abs(s.transform.lossyScale.z);
                double cells=width*depth/(Math.Max(.000001,s.densityCellSizeMin)*Math.Max(.000001,s.densityCellSizeMin));
                if(s.shape==LTStampShape.Ellipse)cells*=Math.PI/4;
                EditorGUILayout.LabelField("Approx. cells before stitching",cells.ToString("N0"));
            }
            if(s.densityZone)EditorGUILayout.HelpBox("Density Cell Size overrides coarse AND fine global mesh density. Later density stamps win. Density follows shape/size/rotation, not the height texture or Strength. Values are not clamped to global settings.",MessageType.Info);
            if(GUILayout.Button("Copy v0.1 Height to Transform Y"))
            {
                Undo.RecordObject(s.transform,"Migrate stamp height");
                var w=s.GetComponentInParent<LTWorld>();
                var p=s.transform.position;p.y=(w?w.transform.position.y:0)+s.height/Mathf.Max(.00001f,s.transform.lossyScale.y);s.transform.position=p;
                Undo.RecordObject(s,"Migrate mask");s.maskIsHeight=false;EditorUtility.SetDirty(s);
            }
            if(s.mask && !s.mask.isReadable)EditorGUILayout.HelpBox("Height ignored: enable Read/Write on texture. Density remains active.",MessageType.Error);
            if(Vector3.Dot(s.transform.up,Vector3.up)<.9999f || s.transform.lossyScale.x<=0 || s.transform.lossyScale.y<=.00001f || s.transform.lossyScale.z<=0)
                EditorGUILayout.HelpBox("Stamp skipped: use only Y rotation and positive scale.",MessageType.Error);
            EditorGUILayout.HelpBox("Move the stamp along Y to change height. Later siblings apply last. Use Override or Max to test layering. Multiply uses Multiply Reference (metres); Blend uses Blend Amount. Scale Y multiplies height amplitude; Y=0 remains zero.",MessageType.Info);
        }
    }
    [CustomEditor(typeof(LTDensityZone))]
    public sealed class LTDensityInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();EditorGUILayout.HelpBox("Legacy component is no longer used. Select LTWorld and Convert Legacy Density Zones to Stamps. New density settings are on LTHeightStamp.",MessageType.Warning);
        }
    }
}
