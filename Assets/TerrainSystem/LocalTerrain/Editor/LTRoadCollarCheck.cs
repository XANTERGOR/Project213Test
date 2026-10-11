using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static partial class LTRoadCollarCheck
    {
        const string Tag="[Road Authored Collars v2] ";
        const string LegacyPath="Assets/Data/Models/Environment/Roads/Road_1_Line.prefab";
        static readonly string[] CurrentPaths=Enumerable.Range(1,3).Select(i=>$"Assets/Data/Models/Environment/Roads/Roads_1/Road_{i}.prefab").ToArray();
        // Archived validation menu: Tools/Local Terrain/Validate Authored Road LOD Ends (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            foreach(string path in CurrentPaths)if(!RunFixture(path,1,1,2,path==CurrentPaths[0]?CurrentPaths.Take(2).ToArray():null))return;
            if(!RunFixture(LegacyPath,.5f,.05f,.5f,null))return;
            Debug.Log(Tag+"PASS all collar fixtures: current Road_1/2/3, Road_1+Road_2 mixed pool, plus legacy Road_1_Line. Road_3 pool compatibility is not covered by these collar checks. No scene/assets saved.");
        }
        static bool RunFixture(string path,float blend,float mismatch,float width,string[] pool)
        {
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var dependencies=(pool??new[]{path}).SelectMany(p=>AssetDatabase.GetDependencies(p,true)).Distinct().ToArray();
            var files=dependencies.Concat(dependencies.Select(p=>p+".meta")).Concat(new[]{path,path+".meta","Packages/manifest.json","Packages/packages-lock.json",
                "Assets/TerrainSystem/LocalTerrain/Editor/LTRoadModuleCollar.cs","Assets/TerrainSystem/LocalTerrain/Editor/LTRoadModuleSource.cs"})
                .Concat(Directory.GetFiles("ProjectSettings"))
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,FileHash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();LTRoad road=null;
            Exception failure=null;string success=null;
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            GameObject Child(string name,Transform parent=null)
            {var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,preview);if(parent)go.transform.SetParent(parent,false);return go;}
            try
            {
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);Require(prefab,"Prefab missing: "+path);
                var poolPrefabs=(pool??new[]{path}).Select(p=>AssetDatabase.LoadAssetAtPath<GameObject>(p)).ToArray();
                Require(poolPrefabs.All(p=>p),"Pool prefab missing.");
                var meshes=poolPrefabs.SelectMany(p=>p.GetComponentsInChildren<MeshFilter>(true)).Select(f=>f.sharedMesh).Distinct().ToArray();
                var hashes=meshes.ToDictionary(m=>m,m=>LTRoadMesh.ContentHash(Copy(m,Matrix4x4.identity)));
                var root=Child("Owned authored LOD end test");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;root.SetActive(true);
                road=Child("Owned road",root.transform).AddComponent<LTRoad>();road.enabled=false;
                road.mode=LTRoadMode.Asphalt;road.asphaltSource=LTRoadMeshSource.Module;road.asphaltModulePrefab=prefab;
                road.moduleFitEnds=true;road.moduleEndBlendLength=blend;road.moduleEndMaxMismatch=mismatch;road.moduleLODEndWidth=width;
                road.meshChunkLength=1;road.width=5;road.sampleSpacing=.5f;
                if(path!=LegacyPath){road.moduleLength=8;road.width=6;road.moduleBaseY=.02f;}
                road.points=new List<LTRoadPoint>{new LTRoadPoint(new Vector3(0,1,0),4),new LTRoadPoint(new Vector3(1,3,25),9),new LTRoadPoint(new Vector3(8,5,55),-3)};
                var snapshot=road.Capture(world);var plan=LTRoadModuleLODPlan.Create(road);
                Require(plan.prefabLevels[0]?.Length==3,"Expected actual LOD0/1/2 fixture: "+path);
                road.moduleRestoreLODEnds=false;bool rejected=false;
                try{LTRoadModuleSource.Build(road,snapshot);}catch(InvalidOperationException e){rejected=e.Message.Contains("LOD1");}
                Require(rejected,"The imported fixture did not reproduce the original authored LOD1 failure.");
                road.moduleRestoreLODEnds=true;var result=LTRoadModuleSource.Build(road,snapshot);
                Require(result.levels.Length==3&&result.levels.All(l=>l.Count==result.levels[0].Count),"Authored LOD/section count changed.");
                Require(Hashes(result)==Hashes(LTRoadModuleSource.Build(road,snapshot)),"Cached output changed.");
                Require(result.status.Contains("торцы LOD0"),"No reconstructed-end diagnostic.");
                var inputs=plan.prefabLevels[0].Select(level=>
                {
                    Require(level.Length==1,"Centre fixture expects one renderer per imported level.");var filter=level[0].GetComponent<MeshFilter>();
                    return Copy(filter.sharedMesh,prefab.transform.worldToLocalMatrix*filter.transform.localToWorldMatrix);
                }).ToArray();
                var fit=LTRoadModuleEnds.Create(inputs[0],blend,mismatch);var reference=LTRoadModuleEnds.Apply(inputs[0],fit);
                VerifyRepeatSeams(result,fit.PointCount);
                int preserved=0;
                for(int l=1;l<3;l++)
                {
                    var joined=LTRoadModuleCollar.Prepare(reference,inputs[l],fit,width,out bool restored);
                    Require(restored,"Expected authored end restoration.");
                    float min=reference.vertices.Min(v=>v.z)+width,max=reference.vertices.Max(v=>v.z)-width;
                    preserved+=VerifyCentre(inputs[l],joined,min,max);
                    VerifyCuts(joined,min-width*.5f,min,max,max+width*.5f);
                    Require(joined.triangles.Length<reference.triangles.Length,"Authored LOD is no cheaper than LOD0.");
                }
                // A disabled pool entry must not affect the single-prefab result.
                road.moduleVariants=new[]{new LTRoadModuleVariant{prefab=prefab},new LTRoadModuleVariant{mesh=meshes[0],weight=0}};
                Require(Hashes(LTRoadModuleSource.Build(road,snapshot))==Hashes(result),"Single active pool differs from legacy prefab field.");
                if(pool!=null)
                {
                    road.moduleVariants=poolPrefabs.Select(p=>new LTRoadModuleVariant{prefab=p,weight=1}).ToArray();
                    var mixed=LTRoadModuleSource.Build(road,snapshot);
                    Require(mixed.levels.Length==3,"Mixed pool lost an authored LOD.");
                    VerifyRepeatSeams(mixed,fit.PointCount);
                    Require(Hashes(mixed)==Hashes(LTRoadModuleSource.Build(road,snapshot)),"Mixed pool is not deterministic.");
                }
                road.moduleVariants=Array.Empty<LTRoadModuleVariant>();
                var segment=Child("Owned LOD switch",root.transform);var filterOut=segment.AddComponent<MeshFilter>();
                var collider=segment.AddComponent<MeshCollider>();var lod=segment.AddComponent<LTRoadLOD>();lod.owner=road;
                lod.meshes=result.levels.Select(level=>
                {
                    var c=level[0];var mesh=Own(new Mesh{indexFormat=UnityEngine.Rendering.IndexFormat.UInt32});
                    mesh.vertices=c.vertices;mesh.normals=c.normals;mesh.tangents=c.tangents;mesh.uv=c.uv;
                    if(c.uv2!=null)mesh.uv2=c.uv2;if(c.uv3!=null)mesh.uv3=c.uv3;if(c.uv4!=null)mesh.uv4=c.uv4;if(c.colors!=null)mesh.colors=c.colors;
                    mesh.subMeshCount=c.submeshes.Length;for(int s=0;s<c.submeshes.Length;s++)mesh.SetTriangles(c.submeshes[s],s);mesh.RecalculateBounds();
                    Require(LTRoadMesh.ContentHash(Copy(mesh,Matrix4x4.identity))==LTRoadMesh.ContentHash(c),"Native output mesh lost channels.");return mesh;
                }).ToArray();collider.sharedMesh=lod.meshes[0];
                for(int l=0;l<3;l++){lod.Show(l);Require(filterOut.sharedMesh==lod.meshes[l]&&collider.sharedMesh==lod.meshes[0],"Render LOD / collider mismatch.");}
                foreach(var entry in hashes)Require(entry.Value==LTRoadMesh.ContentHash(Copy(entry.Key,Matrix4x4.identity)),"Imported mesh mutated.");
                success=$"PASS actual imported {prefab.name}: reproduced strict LOD1 rejection, restored authored LOD1/2 (zone {width} m), {preserved} unchanged centre triangles with exact UV0-3/normal/tangent/color channels, all cut edges paired, all nine mixed repeat seams, deterministic cache/pool, native output channels, collider stays LOD0. "+result.status+" No scene/assets saved. Rendered shading/material seams and physics raycasts not tested.";
            }
            catch(Exception e){failure=e;}
            finally
            {
                if(road)LTRoadModuleSource.Forget(road);
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    failure=new InvalidOperationException("User scene state changed: "+s.scene.name,failure);
                foreach(var entry in files)if(!File.Exists(entry.Key)||FileHash(entry.Key)!=entry.Value)
                    failure=new InvalidOperationException("File changed: "+entry.Key,failure);
            }
            if(failure!=null)Debug.LogError(Tag+"FAIL: "+failure);else Debug.Log(Tag+success);
            return failure==null;
        }
        static void VerifyRepeatSeams(LTRoadModuleSource.Prepared result,int count)
        {
            for(int a=0;a<result.levels.Length;a++)for(int b=0;b<result.levels.Length;b++)for(int i=1;i<result.levels[a].Count;i++)
            {
                var shared=new HashSet<Vector3>(result.levels[a][i-1].vertices);shared.IntersectWith(result.levels[b][i].vertices);
                Require(shared.Count==count,$"LOD{a}/{b} section {i}: incorrect repeat seam ({shared.Count}/{count}).");
            }
        }
        static LTRoadMesh.Chunk Copy(Mesh m,Matrix4x4 matrix)
        {
            var c=new LTRoadMesh.Chunk{vertices=m.vertices,normals=m.normals,tangents=m.tangents,uv=m.uv,uv2=Optional(m.uv2),uv3=Optional(m.uv3),
                uv4=Optional(m.uv4),colors=Optional(m.colors),triangles=m.triangles,submeshes=Enumerable.Range(0,m.subMeshCount).Select(m.GetTriangles).ToArray()};
            if(matrix!=Matrix4x4.identity)for(int i=0;i<c.vertices.Length;i++)
            {
                c.vertices[i]=matrix.MultiplyPoint3x4(c.vertices[i]);c.normals[i]=matrix.inverse.transpose.MultiplyVector(c.normals[i]).normalized;
                var t=c.tangents[i];var d=matrix.MultiplyVector(new Vector3(t.x,t.y,t.z));d=(d-c.normals[i]*Vector3.Dot(c.normals[i],d)).normalized;
                c.tangents[i]=new Vector4(d.x,d.y,d.z,t.w);
            }
            return c;
        }
        static int VerifyCentre(LTRoadMesh.Chunk source,LTRoadMesh.Chunk result,float min,float max)
        {
            var triangles=new HashSet<(Vector3,Vector3,Vector3)>();
            for(int i=0;i<result.triangles.Length;i+=3)triangles.Add((result.vertices[result.triangles[i]],result.vertices[result.triangles[i+1]],result.vertices[result.triangles[i+2]]));
            bool Same<T>(T[] a,T[] b,int i,int j)=>a==null||b!=null&&EqualityComparer<T>.Default.Equals(a[i],b[j]);
            int count=0;
            for(int i=0;i<source.triangles.Length;i+=3)
            {
                var ids=new[]{source.triangles[i],source.triangles[i+1],source.triangles[i+2]};
                if(ids.Any(id=>source.vertices[id].z<min||source.vertices[id].z>max))continue;
                Require(triangles.Contains((source.vertices[ids[0]],source.vertices[ids[1]],source.vertices[ids[2]])),"Authored central face changed.");count++;
                foreach(int id in ids)Require(Enumerable.Range(0,result.vertices.Length).Any(j=>source.vertices[id].Equals(result.vertices[j])&&
                    Same(source.normals,result.normals,id,j)&&Same(source.tangents,result.tangents,id,j)&&Same(source.uv,result.uv,id,j)&&
                    Same(source.uv2,result.uv2,id,j)&&Same(source.uv3,result.uv3,id,j)&&Same(source.uv4,result.uv4,id,j)&&Same(source.colors,result.colors,id,j)),"Authored central attributes changed.");
            }
            Require(count>10,"Insufficient retained authored centre.");return count;
        }
        static void VerifyCuts(LTRoadMesh.Chunk mesh,params float[] planes)
        {
            foreach(float z in planes)
            {
                var uses=new Dictionary<(Vector3,Vector3),(int count,int orientation)>();
                for(int i=0;i<mesh.triangles.Length;i+=3)for(int j=0;j<3;j++)
                {
                    var a=mesh.vertices[mesh.triangles[i+j]];var b=mesh.vertices[mesh.triangles[i+(j+1)%3]];
                    if(Math.Abs(a.z-z)>1e-6||Math.Abs(b.z-z)>1e-6)continue;
                    bool order=a.x<b.x||(a.x==b.x&&a.y<b.y);var key=order?(a,b):(b,a);uses.TryGetValue(key,out var entry);
                    uses[key]=(entry.count+1,entry.orientation+(order?1:-1));
                }
                Require(uses.Count>0&&uses.Values.All(e=>e.count==2&&e.orientation==0),"Internal cut not manifold/watertight.");
            }
        }
        static string Hashes(LTRoadModuleSource.Prepared p)=>string.Join(";",p.levels.SelectMany(l=>l).Select(LTRoadMesh.ContentHash));
        static T[] Optional<T>(T[] a)=>a.Length==0?null:a;
        static string FileHash(string path){using var sha=SHA256.Create();using var file=File.OpenRead(path);return BitConverter.ToString(sha.ComputeHash(file));}
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
