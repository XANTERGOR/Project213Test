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
    public static class LTChunkGridCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Chunk Grid (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var scene=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();
            T Own<T>(T value) where T:Object {owned.Add(value);return value;}
            GameObject Child(string name,Transform parent=null)
            {
                var go=Own(new GameObject(name));SceneManager.MoveGameObjectToScene(go,scene);
                if(parent)go.transform.SetParent(parent,false);return go;
            }
            try
            {
                var root=Child("Chunk grid validation");var world=root.AddComponent<LTWorld>();world.enabled=false;
                world.autoUpdate=false;world.enableLayerPainting=false;world.previewLODs=false;world.enableLODs=true;
                world.chunksX=world.chunksZ=2;world.cellsPerChunk=8;world.adaptive=false;world.lodPatchDivisions=2;
                world.terrainLODMode=LTTerrainLODMode.WithinChunk;
                world.source=Own(ScriptableObject.CreateInstance<LTSource>());world.source.size=new Vector3(32,8,32);
                world.generatedRoot=Child("Generated",root.transform).transform;
                var stamp=Child("Preserved height stamp",root.transform).AddComponent<LTHeightStamp>();stamp.affectHeight=false;
                void Add(params Vector2Int[] positions)
                {
                    var before=new HashSet<LTChunk>(LTChunkManagement.Read(world).Values);
                    LTChunkManagement.AddPreview(world,positions);
                    foreach(var chunk in LTChunkManagement.Read(world).Values)if(!before.Contains(chunk))
                    {Own(chunk.gameObject);Own(chunk.mesh);}
                }
                void Fill()
                {
                    foreach(var chunk in LTChunkManagement.Read(world).Values)
                    {
                        var rect=world.ChunkRect(chunk.x,chunk.z);float Height(float x,float z)=>world.source.Sample(x,z);
                        var fine=LTStampMesh.Plan(rect,8,false,.1f,100000,new List<LTStampMesh.Zone>(),Height);
                        var levels=new List<Vector3Int>[5];levels[0]=fine;var prep=new LTLODMesh.Preparation(fine,rect,Height);
                        for(int level=1;level<5;level++)levels[level]=prep.Coarsen(level,100,null,null,2,false);
                        var data=LTSpatialLODMath.BuildLayout(levels,2,100000,_=>0);
                        LTStampMesh.EmitSpatialVariants(data,rect,new Vector2(world.source.size.x,world.source.size.z),100000,Height,null);
                        chunk.mesh.Clear();chunk.mesh.vertices=data.vertices;chunk.mesh.uv=data.uv;chunk.mesh.triangles=data.baseIndices;chunk.mesh.RecalculateNormals();chunk.mesh.RecalculateBounds();
                        chunk.GetComponent<MeshCollider>().sharedMesh=chunk.mesh;
                        var asset=Own(ScriptableObject.CreateInstance<LTSpatialLODAsset>());asset.vertexBank=Own(Object.Instantiate(chunk.mesh));
                        asset.cells=data.cells;asset.patches=data.patches;asset.divisions=2;asset.formatVersion=LTSpatialLODMath.Version;
                        chunk.spatialLOD=asset;chunk.lodsPending=false;
                    }
                    world.RefreshLODCache();
                }
                int Show(int level)
                {
                    world.forceLOD=level;world.RefreshLODCache();world.UpdateLOD();
                    Require(string.IsNullOrEmpty(world.spatialLODStatus),"LOD fallback: "+world.spatialLODStatus);
                    int total=0;
                    foreach(var c in LTChunkManagement.Read(world).Values)
                    {
                        var mesh=c.GetComponent<MeshFilter>().sharedMesh;Require(mesh,"missing render mesh");total+=(int)mesh.GetIndexCount(0);
                        Require(c.GetComponent<MeshCollider>().sharedMesh==c.mesh,"LOD changed the collider");
                    }
                    return total;
                }
                Add(new Vector2Int(0,0),new Vector2Int(1,0),new Vector2Int(0,1));Fill();
                Require(Show(4)<Show(0),"sparse grid is not simplifying");LTEditorEngine.ValidateChunkGridGeometry(world);
                var original=LTChunkManagement.Read(world)[Vector2Int.zero];var originalMesh=original.mesh;var position=original.transform.position;
                var sourceSize=world.source.size;int count=LTChunkManagement.Read(world).Count;
                Add(new Vector2Int(-1,-1),new Vector2Int(2,1));
                Require(original.mesh==originalMesh&&original.transform.position==position&&world.source.size==sourceSize,"expansion changed existing terrain/source");
                Require(world.chunkOrigin==new Vector2Int(-1,-1)&&world.ChunkSize==new Vector2(16,16),"expanded layout incorrect");
                Undo.PerformUndo();Require(LTChunkManagement.Read(world).Count==count&&world.chunkOrigin==Vector2Int.zero,"Undo expansion failed");
                Add(new Vector2Int(1,1));Fill();Require(Show(4)<Show(0),"restored grid is not simplifying");
                LTChunkManagement.Remove(world,new Vector2Int(1,1));Require(LTChunkManagement.Read(world).Count==3,"deletion failed");
                Require(stamp&&original.mesh==originalMesh&&original.transform.position==position,"deletion affected authoring data");
                Require(Show(4)<Show(0),"deletion left stale LOD cache");LTEditorEngine.ValidateChunkGridGeometry(world);
                Undo.PerformUndo();Require(LTChunkManagement.Read(world).Count==4,"Undo deletion failed");
                Fill();Require(Show(4)<Show(0),"Undo did not restore LOD");
                Add(new Vector2Int(-1,0));Fill();Require(Show(4)<Show(0),"expanded negative grid is not simplifying");
                LTEditorEngine.ValidateChunkGridGeometry(world);
                foreach(var c in LTChunkManagement.Read(world).Values.ToArray())LTChunkManagement.Remove(world,new Vector2Int(c.x,c.z));
                world.RefreshLODCache();world.UpdateLOD();Require(string.IsNullOrEmpty(world.spatialLODStatus),"empty grid fallback");
                Require(stamp,"all-chunk deletion removed stamps");
                Debug.Log("[Chunk Grid v1] PASS: restore/add/remove, Undo, signed expansion, unchanged old transforms/source, sparse/empty LOD and LOD0 colliders; production geometry preparation passed. Temporary preview scene only, no saved scenes/assets. Not a GPU render or performance test.");
            }
            catch(Exception e){Debug.LogError("[Chunk Grid v1] FAIL: "+e);}
            finally
            {
                for(int i=owned.Count-1;i>=0;i--)if(owned[i]){Undo.ClearUndo(owned[i]);Object.DestroyImmediate(owned[i]);}
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
