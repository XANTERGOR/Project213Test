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
    public static class LTRoadAutoLODCheck
    {
        const string Tag="[Road Auto Prefab LOD v1] ";
        // Archived validation menu: Tools/Local Terrain/Validate Automatic Road Prefab LOD (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var files=scenes.Select(s=>s.scene.path).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json",
                "Assets/TerrainSystem/LocalTerrain/Editor/LTRoadModuleSource.cs","Assets/TerrainSystem/LocalTerrain/Editor/LTRoadModuleLODPlan.cs",
                "Assets/TerrainSystem/LocalTerrain/LTRoadLODPolicy.cs"}).Where(File.Exists).Distinct().ToDictionary(p=>p,FileHash);
            var owned=new List<Object>();var sourceHashes=new Dictionary<Mesh,string>();
            var preview=EditorSceneManager.NewPreviewScene();LTRoad road=null;Exception failure=null;string success=null;
            T Own<T>(T item)where T:Object{item.hideFlags=HideFlags.HideAndDontSave;owned.Add(item);return item;}
            GameObject Child(string name,Transform parent=null)
            {
                var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,preview);
                if(parent)go.transform.SetParent(parent,false);return go;
            }
            void Reject(Action action,string label)
            {bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}Require(failed,label);}
            try
            {
                var shader=Shader.Find("HDRP/Lit");Require(shader,"HDRP/Lit unavailable.");
                var materials=new[]{Own(new Material(shader)),Own(new Material(shader))};
                var root=Child("Owned auto LOD validation");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;root.SetActive(true);
                road=Child("Owned road",root.transform).AddComponent<LTRoad>();road.enabled=false;
                road.mode=LTRoadMode.Asphalt;road.asphaltSource=LTRoadMeshSource.Module;
                road.meshChunkLength=1;road.width=4;road.sampleSpacing=.5f;
                road.points=new List<LTRoadPoint>{new LTRoadPoint(new Vector3(0,1,0),4),new LTRoadPoint(new Vector3(1,3,12),9),new LTRoadPoint(new Vector3(8,5,30),-3)};
                var snapshot=road.Capture(world);
                Mesh Grid(int marker,int level)
                {
                    int rows=Math.Max(1,16>>level);var v=new List<Vector3>();var uv=new List<Vector2>();var tri=new List<int>();
                    for(int z=0;z<=rows;z++)for(int x=0;x<3;x++)
                    {float t=z/(float)rows;v.Add(new Vector3(x-1,z==0||z==rows?0:.02f*level*Mathf.Sin(t*Mathf.PI),4*t));uv.Add(new Vector2(x*.5f,t));}
                    for(int z=0;z<rows;z++)for(int x=0;x<2;x++)
                    {int a=z*3+x,b=a+3;tri.AddRange(new[]{a,b,a+1,a+1,b,b+1});}
                    var m=Own(new Mesh{name=$"Owned source {marker}/LOD{level}"});m.vertices=v.ToArray();m.triangles=tri.ToArray();
                    m.normals=Enumerable.Repeat(Vector3.up,v.Count).ToArray();m.tangents=Enumerable.Repeat(new Vector4(1,0,0,1),v.Count).ToArray();
                    m.uv=m.uv2=m.uv3=m.uv4=uv.ToArray();m.colors=Enumerable.Repeat(new Color(marker,level,.5f,1),v.Count).ToArray();
                    m.RecalculateBounds();sourceHashes[m]=MeshHash(m);return m;
                }
                GameObject Module(int marker,int levels,bool nested=false)
                {
                    var go=Child("Owned module "+marker);go.transform.position=new Vector3(17,3,5);
                    var holder=nested?Child("Nested LODGroup",go.transform):go;
                    var lods=new List<LOD>();
                    for(int l=0;l<Math.Max(1,levels);l++)
                    {
                        var part=Child("LOD"+l,holder.transform);part.transform.localPosition=new Vector3(.25f,.1f,0);
                        part.AddComponent<MeshFilter>().sharedMesh=Grid(marker,l);
                        var renderer=part.AddComponent<MeshRenderer>();renderer.sharedMaterial=materials[l%2];
                        lods.Add(new LOD(Mathf.Pow(.5f,l+1),new Renderer[]{renderer}));
                    }
                    if(levels>0)holder.AddComponent<LODGroup>().SetLODs(lods.ToArray());return go;
                }
                var first=Module(1,3,true);var second=Module(2,2);var automatic=Module(3,0);
                road.asphaltModulePrefab=first;
                LTRoadModuleSource.Prepared Build()
                {LTRoadModuleSource.Forget(road);return LTRoadModuleSource.BuildPreview(road,snapshot);}
                var p=Build();Require(p.levels.Length==3,"Prefab count did not override three configured extra LODs.");
                for(int l=0;l<3;l++)foreach(var c in p.levels[l])
                {
                    Require(c.triangles.Length/3==(16>>l)*4,"Generated simplification was used instead of authored topology.");
                    Require(c.colors.All(color=>color.g==l),"Authored LOD vertex channels lost.");
                    Require(c.uv2.Length==c.vertices.Length&&c.uv3.Length==c.vertices.Length&&c.uv4.Length==c.vertices.Length,"UV channels lost.");
                }
                Require(p.materials.Length==2,"LOD-specific material missing.");CheckSeams(p);
                Require(Hashes(p)==Hashes(LTRoadModuleSource.BuildPreview(road,snapshot)),"Source cache changed authored output.");
                Reject(()=>LTRoadModuleSource.Build(road,snapshot),"Public bake accepted a scene object instead of a Project prefab.");

                road.asphaltLODMode=LTRoadLODMode.Disabled;Require(Build().levels.Length==1,"Disabled mode ignored.");
                road.asphaltLODMode=LTRoadLODMode.Authored;
                Reject(()=>Build(),"Manual Authored no longer validates requested missing levels.");
                road.asphaltLODMode=LTRoadLODMode.Automatic;
                var settings=road.asphaltLODs;road.asphaltLODs=Array.Empty<LTLODSettings>();
                Require(Build().levels.Length==3,"Prefab levels lost with empty distance settings.");road.asphaltLODs=settings;
                road.asphaltModulePrefab=Module(4,1);Require(Build().levels.Length==1,"Single authored LOD incorrectly auto-simplified.");
                road.asphaltModulePrefab=Module(5,8);var eight=Build();Require(eight.levels.Length==8,"Eight-level prefab truncated.");CheckSeams(eight);
                Require(LTRoadLODPolicy.Select(10000,0,road.asphaltLODs,8,5)==7,"Runtime cannot reach the last authored LOD.");

                road.moduleVariants=new[]{new LTRoadModuleVariant{prefab=first},new LTRoadModuleVariant{prefab=second},new LTRoadModuleVariant{prefab=automatic}};
                var mixed=Build();Require(mixed.levels.Length==4,"Mixed auto/authored level count incorrect.");CheckSeams(mixed);
                for(int l=0;l<4;l++)for(int c=0;c<mixed.levels[l].Count;c++)
                {
                    var chunk=mixed.levels[l][c];int marker=(int)chunk.colors[0].r;
                    Require(marker==(int)mixed.levels[0][c].colors[0].r,"LOD changed random module sequence.");
                    if(marker==3)continue;
                    int sourceLevel=Math.Min(l,marker==1?2:1);
                    Require(chunk.colors.All(color=>color.r==marker&&color.g==sourceLevel),"Module did not hold its own last authored LOD.");
                    Require(chunk.triangles.Length/3==(16>>sourceLevel)*4,"Authored fallback changed topology.");
                }
                road.moduleVariants[2].weight=0;Require(Build().levels.Length==3,"Disabled auto variant still affects LOD count.");
                road.moduleVariants[0].prefab=road.asphaltModulePrefab;road.moduleVariants[2].weight=1;
                var longMixed=Build();Require(longMixed.levels.Length==8,"Mixed pool truncated longest authored chain.");
                for(int l=4;l<8;l++)for(int c=0;c<longMixed.levels[l].Count;c++)
                    if(longMixed.levels[l][c].colors[0].r==3)
                        Require(LTRoadMesh.ContentHash(longMixed.levels[l][c])==LTRoadMesh.ContentHash(longMixed.levels[3][c]),"Auto module extended beyond configured levels.");

                var group=second.GetComponent<LODGroup>();var original=group.GetLODs();
                group.SetLODs(original.Concat(new[]{new LOD(.01f,Array.Empty<Renderer>())}).ToArray());
                Require(LTRoadModuleLODPlan.ReadPrefabLevels(second).Length==2,"Explicit trailing cull became a road mesh.");
                var lower=original[1];lower.screenRelativeTransitionHeight=.05f;
                group.SetLODs(new[]{original[0],new LOD(.1f,Array.Empty<Renderer>()),lower});
                Reject(()=>LTRoadModuleLODPlan.ReadPrefabLevels(second),"Empty middle LOD accepted.");
                group.SetLODs(new[]{new LOD(.5f,new Renderer[]{null})});
                Reject(()=>LTRoadModuleLODPlan.ReadPrefabLevels(second),"Missing renderer silently dropped.");
                group.SetLODs(original);
                var constant=Child("Always-visible static part",second.transform);constant.AddComponent<MeshFilter>().sharedMesh=Grid(6,0);
                var constantRenderer=constant.AddComponent<MeshRenderer>();constantRenderer.sharedMaterial=materials[0];
                Require(LTRoadModuleLODPlan.ReadPrefabLevels(second).All(l=>l.Contains(constantRenderer)),"Ungrouped part disappeared on LODs.");
                Child("Ambiguous extra group",second.transform).AddComponent<LODGroup>();
                Reject(()=>LTRoadModuleLODPlan.ReadPrefabLevels(second),"Multiple independent groups accepted ambiguously.");

                var segment=Child("Owned switching",root.transform);var filter=segment.AddComponent<MeshFilter>();
                var collider=segment.AddComponent<MeshCollider>();var controller=segment.AddComponent<LTRoadLOD>();
                controller.meshes=eight.levels.Select(l=>
                {
                    var chunk=l[0];var mesh=Own(new Mesh());mesh.vertices=chunk.vertices;mesh.triangles=chunk.triangles;mesh.RecalculateBounds();return mesh;
                }).ToArray();collider.sharedMesh=controller.meshes[0];
                for(int l=0;l<8;l++)
                {controller.Show(l);Require(filter.sharedMesh==controller.meshes[l]&&collider.sharedMesh==controller.meshes[0],"Native switch failed or changed collider.");}
                foreach(var entry in sourceHashes)Require(MeshHash(entry.Key)==entry.Value,"A source mesh was modified.");
                success="PASS: owned native prefab-like hierarchies, nested/inactive LODGroup, authored topology/materials/UV/colors, mixed 3/2/auto and 8/2/auto pools, held-last levels, identical joins, deterministic cache, strict scene-source rejection, Disabled/Authored modes, empty/cull/invalid/multiple groups, LOD0–7 switching with LOD0 collider. No scene/assets saved. Real user prefab import, rendered appearance and physics raycasts remain untested.";
            }
            catch(Exception e){failure=e;}
            finally
            {
                if(road)LTRoadModuleSource.Forget(road);
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    failure=new InvalidOperationException("User scene state changed: "+s.scene.name,failure);
                foreach(var f in files)if(!File.Exists(f.Key)||FileHash(f.Key)!=f.Value)
                    failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(Tag+"FAIL: "+failure);else Debug.Log(Tag+success);
        }
        static void CheckSeams(LTRoadModuleSource.Prepared p)
        {
            Require(p.levels[0].Count>1&&p.levels.All(l=>l.Count==p.levels[0].Count),"Section count changed across LODs.");
            for(int a=0;a<p.levels.Length;a++)for(int b=0;b<p.levels.Length;b++)for(int c=1;c<p.levels[a].Count;c++)
            {
                var shared=new HashSet<Vector3>(p.levels[a][c-1].vertices);shared.IntersectWith(p.levels[b][c].vertices);
                Require(shared.Count==3,$"LOD{a}/{b}: expected 3 exact seam points, got {shared.Count}.");
            }
        }
        static string Hashes(LTRoadModuleSource.Prepared p)=>string.Join(";",p.levels.SelectMany(l=>l).Select(LTRoadMesh.ContentHash));
        static string MeshHash(Mesh m)=>LTRoadMesh.ContentHash(new LTRoadMesh.Chunk{vertices=m.vertices,normals=m.normals,tangents=m.tangents,
            uv=m.uv,uv2=m.uv2,uv3=m.uv3,uv4=m.uv4,colors=m.colors,triangles=m.triangles,submeshes=new[]{m.triangles}});
        static string FileHash(string path){using var sha=SHA256.Create();using var file=File.OpenRead(path);return BitConverter.ToString(sha.ComputeHash(file));}
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
