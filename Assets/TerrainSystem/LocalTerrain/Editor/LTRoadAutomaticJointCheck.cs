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
        // Archived validation menu: Tools/Local Terrain/Validate Automatic Road Joints (no scene changes)
        public static void RunAutomaticJoints()
        {
            const string tag="[Road Automatic Joints v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var dependencies=CurrentPaths.SelectMany(p=>AssetDatabase.GetDependencies(p,true)).Distinct().ToArray();
            var files=dependencies.Concat(dependencies.Select(p=>p+".meta")).Concat(Directory.GetFiles("ProjectSettings"))
                .Concat(Directory.GetFiles("Assets/TerrainSystem/LocalTerrain/Editor","LTRoad*.cs"))
                .Concat(new[]{"Assets/TerrainSystem/LocalTerrain/LTRoad.cs","Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,FileHash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();LTRoad road=null;
            Exception failure=null;string success=null;
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            GameObject Child(string name,Transform parent=null)
            {var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,preview);if(parent)go.transform.SetParent(parent,false);return go;}
            try
            {
                var prefabs=CurrentPaths.Select(p=>AssetDatabase.LoadAssetAtPath<GameObject>(p)).ToArray();
                Require(prefabs.All(p=>p),"Current Road_1/2/3 prefabs required.");
                var originals=prefabs.SelectMany(p=>p.GetComponentsInChildren<MeshFilter>(true)).Select(f=>f.sharedMesh).Distinct().ToArray();
                var hashes=originals.ToDictionary(m=>m,m=>LTRoadMesh.ContentHash(Copy(m,Matrix4x4.identity)));
                var root=Child("Owned automatic joint check");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;root.SetActive(true);
                road=Child("Owned road",root.transform).AddComponent<LTRoad>();road.enabled=false;
                road.mode=LTRoadMode.Asphalt;road.asphaltSource=LTRoadMeshSource.Module;
                road.moduleVariants=prefabs.Select(p=>new LTRoadModuleVariant{prefab=p,weight=1}).ToArray();
                road.moduleAutomaticJoints=true;road.moduleJointWidth=2;road.moduleJointMaxChange=1;road.moduleJointPrecision=.01f;
                road.moduleLength=8;road.meshChunkLength=1;road.width=6;road.sampleSpacing=.5f;road.moduleBaseY=.02f;
                road.points=new List<LTRoadPoint>{new LTRoadPoint(new Vector3(0,1,0),4),new LTRoadPoint(new Vector3(1,3,25),9),new LTRoadPoint(new Vector3(8,5,55),-3)};
                var snapshot=road.Capture(world);var levelPlan=LTRoadModuleLODPlan.Create(road);
                Require(levelPlan.prefabLevels.All(p=>p!=null&&p.Length==3),"Fixture expects 3 authored LODs for every module.");
                var inputs=levelPlan.prefabLevels.Select((levels,v)=>levels.Select(level=>
                {
                    Require(level.Length==1,"Fixture expects one renderer per level.");var f=level[0].GetComponent<MeshFilter>();
                    return Copy(f.sharedMesh,prefabs[v].transform.worldToLocalMatrix*f.transform.localToWorldMatrix);
                }).ToArray()).ToArray();
                float min=inputs[0][0].vertices.Min(p=>p.z),max=inputs[0][0].vertices.Max(p=>p.z);
                for(int v=0;v<inputs.Length;v++)
                {
                    float a=inputs[v][0].vertices.Min(p=>p.z),b=inputs[v][0].vertices.Max(p=>p.z);
                    inputs[v]=inputs[v].Select(c=>LTRoadModuleMath.NormalizeLength(c,a,b,min,max,true)).ToArray();
                }
                var common=LTRoadModuleCollar.CreateAutomatic(inputs[0][0],2,1,.01f);
                var prepared=inputs.Select(levels=>levels.Select(c=>LTRoadModuleCollar.ApplyAutomatic(c,common)).ToArray()).ToArray();
                int kept=0;
                for(int v=0;v<inputs.Length;v++)for(int l=0;l<3;l++)
                {
                    kept+=VerifyCentre(inputs[v][l],prepared[v][l],min+2,max-2);
                    VerifyCuts(prepared[v][l],min+2,max-2);
                    LTRoadModuleEnds.ValidateRims(prepared[0][0],prepared[v][l]);
                    Require(LTRoadMesh.ContentHash(prepared[v][l])==LTRoadMesh.ContentHash(LTRoadModuleCollar.ApplyAutomatic(inputs[v][l],common)),"Joint preparation is not deterministic.");
                }
                var refMin=prepared[0][0].vertices.Aggregate(Vector3.Min);var refMax=prepared[0][0].vertices.Aggregate(Vector3.Max);
                var bent=prepared.SelectMany(s=>s).Select(c=>LTRoadModuleMath.Bend(c,snapshot,refMin,refMax,8,true,.02f,road.surfaceOffset,1,Matrix4x4.identity)).ToArray();
                int seams=0;
                for(int a=0;a<bent.Length;a++)for(int b=0;b<bent.Length;b++)for(int i=1;i<bent[a].Count;i++)
                {
                    var shared=new HashSet<Vector3>(bent[a][i-1].vertices);shared.IntersectWith(bent[b][i].vertices);
                    Require(shared.Count==common.PointCount,"Mixed module/LOD seam mismatch.");seams++;
                }
                var result=LTRoadModuleSource.Build(road,snapshot);VerifyRepeatSeams(result,common.PointCount);
                Require(result.levels.Length==3&&result.status.Contains("Автоматические стыки"),"Automatic mode not used by production build.");
                Require(Hashes(result)==Hashes(LTRoadModuleSource.Build(road,snapshot)),"Cached output changed.");
                // An invalid setting must not return an earlier successful cached build.
                road.moduleJointMaxChange=.000001f;bool rejected=false;
                try{LTRoadModuleSource.Build(road,snapshot);}catch(InvalidOperationException){rejected=true;}
                Require(rejected,"Changed joint tolerance reused stale cache.");road.moduleJointMaxChange=1;
                Require(Hashes(result)==Hashes(LTRoadModuleSource.Build(road,snapshot)),"Restoring settings failed.");
                road.moduleJointPrecision=.005f;
                var precisionResult=LTRoadModuleSource.Build(road,snapshot);LTRoadModuleSource.Forget(road);
                Require(Hashes(precisionResult)==Hashes(LTRoadModuleSource.Build(road,snapshot)),"Precision setting cache differs from fresh build.");
                road.moduleJointPrecision=.01f;
                var segment=Child("Owned render LOD switch",root.transform);var filter=segment.AddComponent<MeshFilter>();
                var collider=segment.AddComponent<MeshCollider>();var lod=segment.AddComponent<LTRoadLOD>();lod.owner=road;
                lod.meshes=result.levels.Select(level=>
                {
                    var c=level[0];var mesh=Own(new Mesh{indexFormat=UnityEngine.Rendering.IndexFormat.UInt32});
                    mesh.vertices=c.vertices;mesh.normals=c.normals;mesh.tangents=c.tangents;mesh.uv=c.uv;
                    if(c.uv2!=null)mesh.uv2=c.uv2;if(c.uv3!=null)mesh.uv3=c.uv3;if(c.uv4!=null)mesh.uv4=c.uv4;if(c.colors!=null)mesh.colors=c.colors;
                    mesh.subMeshCount=c.submeshes.Length;for(int s=0;s<c.submeshes.Length;s++)mesh.SetTriangles(c.submeshes[s],s);mesh.RecalculateBounds();
                    Require(LTRoadMesh.ContentHash(c)==LTRoadMesh.ContentHash(Copy(mesh,Matrix4x4.identity)),"Native output channels changed.");return mesh;
                }).ToArray();collider.sharedMesh=lod.meshes[0];
                for(int l=0;l<3;l++){lod.Show(l);Require(filter.sharedMesh==lod.meshes[l]&&collider.sharedMesh==lod.meshes[0],"LOD switch changed collider.");}
                foreach(var entry in hashes)Require(entry.Value==LTRoadMesh.ContentHash(Copy(entry.Key,Matrix4x4.identity)),"Imported source mesh changed.");
                success=$"PASS imported Road_1/2/3 together: {common.PointCount} shared profile points; {kept} exact authored centre triangles/UV/normal/tangent/color channels; {seams} mixed module/LOD seams; paired cuts, native output, cache invalidation, collider LOD0. "+result.status+" No scene/assets saved. Visible shading/texture seams and physics raycasts not tested.";
            }
            catch(Exception e){failure=e;}
            finally
            {
                if(road)LTRoadModuleSource.Forget(road);
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    failure=new InvalidOperationException("User scene state changed: "+s.scene.name,failure);
                foreach(var file in files)if(!File.Exists(file.Key)||file.Value!=FileHash(file.Key))
                    failure=new InvalidOperationException("File changed: "+file.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);else Debug.Log(tag+success);
        }
    }
}
