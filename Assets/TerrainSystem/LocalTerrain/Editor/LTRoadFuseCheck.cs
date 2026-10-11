using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static partial class LTRoadCollarCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Road Fuse (no scene changes)
        public static void RunFuse()
        {
            const string tag="[Road Fuse v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var paths=CurrentPaths.Take(2).ToArray();
            var deps=paths.SelectMany(p=>AssetDatabase.GetDependencies(p,true)).Distinct().ToArray();
            var files=deps.Concat(deps.Select(p=>p+".meta")).Concat(Directory.GetFiles("ProjectSettings"))
                .Concat(Directory.GetFiles("Assets/TerrainSystem/LocalTerrain/Editor","LTRoad*.cs"))
                .Concat(new[]{"Assets/TerrainSystem/LocalTerrain/LTRoad.cs","Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,FileHash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();LTRoad road=null;
            Exception failure=null;string success=null;var issues=new List<string>();var diagnostics=new List<string>();
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            GameObject Child(string name,Transform parent=null)
            {var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,preview);if(parent)go.transform.SetParent(parent,false);return go;}
            try
            {
                var prefabs=paths.Select(p=>AssetDatabase.LoadAssetAtPath<GameObject>(p)).ToArray();Require(prefabs.All(p=>p),"Road_1/2 prefabs required.");
                var originals=prefabs.SelectMany(p=>p.GetComponentsInChildren<MeshFilter>(true)).Select(f=>f.sharedMesh).Distinct().ToArray();
                var sourceHashes=originals.ToDictionary(m=>m,m=>LTRoadMesh.ContentHash(Copy(m,Matrix4x4.identity)));
                var root=Child("Owned Fuse validation");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;root.SetActive(true);
                road=Child("Owned road",root.transform).AddComponent<LTRoad>();road.enabled=false;
                road.mode=LTRoadMode.Asphalt;road.asphaltSource=LTRoadMeshSource.Module;
                road.moduleVariants=prefabs.Select(p=>new LTRoadModuleVariant{prefab=p,weight=1}).ToArray();
                road.moduleFuseEnds=true;road.moduleFuseTolerance=.001f;road.moduleFusePlaneTolerance=.0001f;
                // Prove Fuse takes precedence; the old surgery settings are deliberately invalid.
                road.moduleAutomaticJoints=true;road.moduleJointWidth=0;road.moduleFitEnds=true;road.moduleRestoreLODEnds=true;
                road.moduleLength=8;road.meshChunkLength=1;road.width=6;road.sampleSpacing=.5f;road.moduleBaseY=.02f;
                road.points=new List<LTRoadPoint>{new LTRoadPoint(new Vector3(0,1,0),4),new LTRoadPoint(new Vector3(1,3,25),9),new LTRoadPoint(new Vector3(8,5,55),-3)};
                var snapshot=road.Capture(world);var levelPlan=LTRoadModuleLODPlan.Create(road);
                Require(levelPlan.prefabLevels.All(p=>p!=null&&p.Length==3),"Fixture expects LOD0/1/2.");
                var inputs=levelPlan.prefabLevels.Select((levels,v)=>levels.Select(level=>
                {
                    Require(level.Length==1,"Fixture expects one renderer per level.");var f=level[0].GetComponent<MeshFilter>();
                    return Copy(f.sharedMesh,prefabs[v].transform.worldToLocalMatrix*f.transform.localToWorldMatrix);
                }).ToArray()).ToArray();
                float min=inputs[0][0].vertices.Min(p=>p.z),max=inputs[0][0].vertices.Max(p=>p.z);
                for(int v=0;v<inputs.Length;v++)
                {float a=inputs[v][0].vertices.Min(p=>p.z),b=inputs[v][0].vertices.Max(p=>p.z);inputs[v]=inputs[v].Select(c=>LTRoadModuleMath.NormalizeLength(c,a,b,min,max,true)).ToArray();}
                var common=LTRoadModuleFuse.Create(inputs[0][0],road.moduleFuseTolerance,road.moduleFusePlaneTolerance);
                var prepared=new List<LTRoadMesh.Chunk>();
                for(int v=0;v<2;v++)for(int l=0;l<3;l++)
                {
                    LTRoadMesh.Chunk output;
                    try
                    {
                        output=LTRoadModuleFuse.Apply(inputs[v][l],common,out int moved,out float maxMove);
                        diagnostics.Add($"Road_{v+1} LOD{l}: compatible; {output.triangles.Length/3} tris unchanged; moved {moved}, max {maxMove*1000:G5} mm.");
                    }
                    catch(InvalidOperationException e)
                    {
                        if(l<2)throw;
                        issues.Add($"Road_{v+1} LOD{l}: "+e.Message);continue;
                    }
                    VerifyFuseUnchanged(inputs[v][l],output,min,max,road.moduleFusePlaneTolerance);
                    LTRoadModuleFuse.ValidateRims(prepared.Count==0?output:prepared[0],output);prepared.Add(output);
                }
                var refMin=prepared[0].vertices.Aggregate(Vector3.Min);var refMax=prepared[0].vertices.Aggregate(Vector3.Max);
                var bent=prepared.Select(c=>LTRoadModuleMath.Bend(c,snapshot,refMin,refMax,8,true,.02f,road.surfaceOffset,1,Matrix4x4.identity)).ToArray();int seams=0;
                for(int a=0;a<bent.Length;a++)for(int b=0;b<bent.Length;b++)for(int j=1;j<bent[a].Count;j++)
                {
                    var tail=new HashSet<Vector3>(Enumerable.Range(0,prepared[a].vertices.Length).Where(i=>prepared[a].vertices[i].z==max).Select(i=>bent[a][j-1].vertices[i]));
                    var head=new HashSet<Vector3>(Enumerable.Range(0,prepared[b].vertices.Length).Where(i=>prepared[b].vertices[i].z==min).Select(i=>bent[b][j].vertices[i]));
                    Require(tail.SetEquals(head),"Mixed bent seam mismatch.");seams++;
                }
                // This limit applies ONLY to the transient validation road. User LOD settings stay untouched.
                road.asphaltLODMode=LTRoadLODMode.Authored;road.asphaltLODs=new[]{new LTLODSettings(1,.05f,50)};
                var result=LTRoadModuleSource.Build(road,snapshot);
                Require(result.levels.Length==2&&result.status.Contains("Fuse торцов"),"Production Fuse mode not used.");
                Require(Hashes(result)==Hashes(LTRoadModuleSource.Build(road,snapshot)),"Cached output differs.");
                void MustReject(Action change,Action restore)
                {
                    change();bool rejected=false;
                    try{LTRoadModuleSource.Build(road,snapshot);}catch(InvalidOperationException){rejected=true;}
                    finally{restore();}
                    Require(rejected,"Invalid setting reused cache or silently fell back.");
                    Require(Hashes(result)==Hashes(LTRoadModuleSource.Build(road,snapshot)),"Restored settings differ.");
                }
                MustReject(()=>road.moduleFuseTolerance=0,()=>road.moduleFuseTolerance=.001f);
                MustReject(()=>road.moduleFusePlaneTolerance=.01f,()=>road.moduleFusePlaneTolerance=.0001f);
                MustReject(()=>road.moduleFuseEnds=false,()=>road.moduleFuseEnds=true);
                LTRoadModuleSource.Forget(road);Require(Hashes(result)==Hashes(LTRoadModuleSource.Build(road,snapshot)),"Fresh and cached Fuse builds differ.");
                // Full authored chain must remain blocked when any LOD is incompatible, never silently dropped.
                road.asphaltLODMode=LTRoadLODMode.Automatic;
                bool fullRejected=false;try{var full=LTRoadModuleSource.Build(road,snapshot);Require(full.levels.Length==3,"Authored LOD2 was dropped.");}
                catch(InvalidOperationException e){fullRejected=true;Require(e.Message.Contains("LOD2")&&e.Message.Contains("Fuse"),"Unexpected full-chain error: "+e.Message);}
                Require(fullRejected==(issues.Count>0),"Full-chain acceptance differs from mesh diagnostics.");
                var segment=Child("Owned Fuse output",root.transform);var filter=segment.AddComponent<MeshFilter>();var collider=segment.AddComponent<MeshCollider>();var lod=segment.AddComponent<LTRoadLOD>();lod.owner=road;
                lod.meshes=result.levels.Select(level=>
                {
                    var c=level[0];var mesh=Own(new Mesh{indexFormat=UnityEngine.Rendering.IndexFormat.UInt32});
                    mesh.vertices=c.vertices;mesh.normals=c.normals;mesh.tangents=c.tangents;mesh.uv=c.uv;
                    if(c.uv2!=null)mesh.uv2=c.uv2;if(c.uv3!=null)mesh.uv3=c.uv3;if(c.uv4!=null)mesh.uv4=c.uv4;if(c.colors!=null)mesh.colors=c.colors;
                    mesh.subMeshCount=c.submeshes.Length;for(int s=0;s<c.submeshes.Length;s++)mesh.SetTriangles(c.submeshes[s],s);mesh.RecalculateBounds();
                    Require(LTRoadMesh.ContentHash(c)==LTRoadMesh.ContentHash(Copy(mesh,Matrix4x4.identity)),"Native channels changed.");return mesh;
                }).ToArray();collider.sharedMesh=lod.meshes[0];
                for(int l=0;l<lod.meshes.Length;l++){lod.Show(l);Require(filter.sharedMesh==lod.meshes[l]&&collider.sharedMesh==lod.meshes[0],"LOD switch changed collider.");}
                foreach(var e in sourceHashes)Require(e.Value==LTRoadMesh.ContentHash(Copy(e.Key,Matrix4x4.identity)),"Imported mesh changed.");
                success=$"PASS compatible levels: {prepared.Count}; {seams} mixed bent seams; production LOD0/1 pool, cache invalidation, native channels, LOD0 collider.\n"+string.Join("\n",diagnostics)+
                    "\nNo scene/assets saved. This is not a rendering/performance test.";
            }
            catch(Exception e){failure=e;}
            finally
            {
                if(road)LTRoadModuleSource.Forget(road);
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene state changed: "+s.scene.name,failure);
                foreach(var file in files)if(!File.Exists(file.Key)||file.Value!=FileHash(file.Key))failure=new InvalidOperationException("File changed: "+file.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);
            else
            {
                Debug.Log(tag+success);
                if(issues.Count>0)Debug.LogWarning(tag+"ASSET INCOMPATIBLE — full Road_1/2 LOD chain is NOT ready for strict Fuse; no levels removed or repaired.\n"+string.Join("\n",issues));
            }
        }
        static void VerifyFuseUnchanged(LTRoadMesh.Chunk a,LTRoadMesh.Chunk b,float min,float max,float band)
        {
            bool Same<T>(T[] x,T[] y)=>x==null?y==null:y!=null&&x.SequenceEqual(y);
            Require(a.vertices.Length==b.vertices.Length&&a.triangles.SequenceEqual(b.triangles)&&a.submeshes.Length==b.submeshes.Length&&a.submeshes.Zip(b.submeshes,(x,y)=>x.SequenceEqual(y)).All(x=>x),"Fuse changed topology.");
            Require(Same(a.normals,b.normals)&&Same(a.tangents,b.tangents)&&Same(a.uv,b.uv)&&Same(a.uv2,b.uv2)&&Same(a.uv3,b.uv3)&&Same(a.uv4,b.uv4)&&Same(a.colors,b.colors),"Fuse changed authored channels.");
            for(int i=0;i<a.vertices.Length;i++)if(Math.Abs(a.vertices[i].z-min)>band&&Math.Abs(a.vertices[i].z-max)>band)Require(a.vertices[i].Equals(b.vertices[i]),"Fuse changed interior.");
        }
    }
}
