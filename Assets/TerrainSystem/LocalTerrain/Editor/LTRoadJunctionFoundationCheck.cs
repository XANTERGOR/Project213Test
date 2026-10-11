using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTRoadJunctionFoundationCheck
    {
        const string Prefab="Assets/Data/Models/Environment/Roads/Roads_1/Road_Perek_1.prefab";
        // Archived validation menu: Tools/Local Terrain/Validate Junction Mesh Foundation (no scene changes)
        public static void Run()
        {
            const string tag="[Junction Mesh Foundation v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var files=AssetDatabase.GetDependencies(Prefab,true).Concat(Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories))
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();Exception failure=null;int checks=0;string report=null;
            void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            GameObject Child(string name,Transform parent=null)
            {var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,preview);if(parent)go.transform.SetParent(parent,false);return go;}
            try
            {
                var root=Child("Owned mesh foundation check");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;
                root.transform.position=new Vector3(15,2,-40);root.transform.rotation=Quaternion.Euler(0,23,0);
                var node=Child("Synthetic ring",root.transform).AddComponent<LTRoadJunction>();
                node.meshSource=LTRoadJunctionSource.OwnHierarchy;node.surface=LTRoadMode.Asphalt;
                node.foundationMode=LTRoadJunctionFoundation.MeshFootprint;node.foundationShoulder=.25f;node.blendWidth=.5f;
                node.transform.localPosition=new Vector3(30,4,20);
                Child("Port A",node.transform).AddComponent<LTRoadJunctionSocket>();
                var socket=Child("Port B",node.transform);socket.transform.localPosition=Vector3.forward*4;socket.AddComponent<LTRoadJunctionSocket>();
                Mesh Shape(bool ring)
                {
                    var v=new List<Vector3>();var t=new List<int>();
                    void Quad(float x0,float z0,float x1,float z1)
                    {int i=v.Count;v.AddRange(new[]{new Vector3(x0,0,z0),new Vector3(x1,0,z0),new Vector3(x0,0,z1),new Vector3(x1,0,z1)});t.AddRange(new[]{i,i+2,i+1,i+1,i+2,i+3});}
                    if(ring){Quad(-4,-5,-2,5);Quad(2,-5,4,5);Quad(-2,-5,2,-3);Quad(-2,3,2,5);}else Quad(-20,-20,20,20);
                    var m=Own(new Mesh());m.SetVertices(v);m.SetTriangles(t,0);m.RecalculateBounds();return m;
                }
                MeshRenderer Part(string name,Mesh mesh)
                {var go=Child(name,node.transform);go.AddComponent<MeshFilter>().sharedMesh=mesh;return go.AddComponent<MeshRenderer>();}
                var mesh=Shape(true);var r0=Part("LOD0",mesh);var r1=Part("LOD1 intentionally wider",Shape(false));
                var group=node.gameObject.AddComponent<LODGroup>();group.SetLODs(new[]{new LOD(.6f,new[]{r0}),new LOD(.1f,new[]{r1})});
                var data=node.Capture();var shape=data.footprint;
                Check(data.Weight(30,20)==0&&data.Weight(33,20)==1&&data.Weight(40,20)==0,"LOD0 hole/exterior not respected.");
                Check(ReferenceEquals(shape,node.Capture().footprint),"Unchanged geometry did not reuse footprint.");
                group.ForceLOD(1);node.SetSourceSuppressed(true);
                Check(ReferenceEquals(shape,node.Capture().footprint),"Display LOD/source visibility changed foundation.");
                var generated=Part("Owned generated output",Shape(false));generated.gameObject.AddComponent<LTRoadJunctionGenerated>().owner=node;
                Check(ReferenceEquals(shape,node.Capture().footprint),"Generated output became source geometry.");
                node.meshDepth=.12f;Check(ReferenceEquals(shape,node.Capture().footprint)&&node.Capture().Weight(33,20)==1,"Depth moved the foundation.");
                checks+=LTDetailRenderer.CheckJunctionRemoval(node);
                var layer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());node.groundLayer=layer;
                var baseLayer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());var stamps=new List<LTPaintStamp>{node.GetComponent<LTPaintStamp>()};
                var layers=new List<LTSurfaceLayer>{baseLayer,layer};var rect=new Rect(20,10,20,20);
                using(var runtime=new LTPaintRuntime())
                {
                    var method=typeof(LTPaintRuntime).GetMethod("TryPrepareWeights",BindingFlags.Instance|BindingFlags.NonPublic);
                    var args=new object[]{world,rect,stamps,layers,null,null};
                    Check((bool)method.Invoke(runtime,args),"Mesh junction weight input failed.");
                    using(var input=(LTPaintWeightInput)args[5])
                    {
                        Check(input.stamps[0].kind==3&&input.masks.Length>=input.size*input.size,"Mesh junction wasn't prepared as a mask.");
                        var fixture=new LTPaintWeightFixture{input=input};
                        for(int i=0;i<input.size*input.size;i++)
                        {
                            var p=input.coordinates[i];float weight=data.Weight(p.x,p.y);
                            Check(Mathf.Abs(weight-input.masks[i])<.00001f,"Prepared paint mask differs from terrain.");
                            Check(fixture.Scalar(i).first.g==(byte)Mathf.RoundToInt(weight*255),"Weight kernel differs from terrain.");
                        }
                    }
                }
                // Test the actual suppression map including no-paint-layer junctions.
                var stateType=typeof(LTPaintRuntime).GetNestedType("ChunkState",BindingFlags.NonPublic);
                var state=Activator.CreateInstance(stateType,true);
                var bake=typeof(LTPaintRuntime).GetMethod("BakeRoadProjection",BindingFlags.Static|BindingFlags.NonPublic);
                var capture=typeof(LTPaintRuntime).GetMethod("CaptureRoadProjection",BindingFlags.Static|BindingFlags.NonPublic);
                var release=typeof(LTPaintRuntime).GetMethod("ReleaseRoadProjection",BindingFlags.Static|BindingFlags.NonPublic);
                try
                {
                    var args=new object[]{world,rect,new List<LTPaintStamp>(),layers,state,new List<LTRoadMath.Snapshot>(),null,new[]{data}};
                    Check((bool)bake.Invoke(null,args),"Junction-only suppression not baked.");
                    var cpu=capture.Invoke(null,new[]{state});var sample=cpu.GetType().GetMethod("DisplacementSuppression",BindingFlags.Instance|BindingFlags.NonPublic);
                    float Suppress(float x,float z)=>(float)sample.Invoke(cpu,new object[]{x,z});
                    Check(Suppress(33,20)==1&&Suppress(30,20)==0&&Suppress(39,20)==0,"Suppression filled hole or retained circular exterior.");
                    Check(!(bool)bake.Invoke(null,args),"Unchanged suppression not cached.");
                    args[7]=Array.Empty<LTRoadJunctionMath.Snapshot>();Check((bool)bake.Invoke(null,args)&&capture.Invoke(null,new[]{state})==null,"Removed junction left suppression behind.");
                }
                finally{release.Invoke(null,new[]{state});}
                node.transform.localPosition+=Vector3.right*2;var moved=node.Capture();
                Check(moved.hash!=data.hash&&moved.Weight(35,20)==1&&moved.Weight(25,20)==0,"Move did not invalidate footprint.");
                var vertices=mesh.vertices;for(int i=0;i<vertices.Length;i++)vertices[i].x*=.5f;mesh.vertices=vertices;EditorUtility.SetDirty(mesh);
                Check(node.Capture().footprint.ContentKey!=moved.footprint.ContentKey,"Mesh edit not detected.");
                node.SetSourceSuppressed(false);
                var asset=AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);Check(asset,"Missing Road_Perek_1.");
                foreach(bool own in new[]{true,false})
                {
                    var instance=own?Own((GameObject)PrefabUtility.InstantiatePrefab(asset,preview)):Child("Separate prefab check");
                    instance.SetActive(false);instance.transform.SetParent(root.transform,false);instance.transform.localPosition=new Vector3(902,4,137);
                    instance.transform.localRotation=Quaternion.Euler(0,17,0);instance.transform.localScale=Vector3.one;
                    var n=instance.GetComponent<LTRoadJunction>();if(!n)n=instance.AddComponent<LTRoadJunction>();
                    n.meshSource=own?LTRoadJunctionSource.OwnHierarchy:LTRoadJunctionSource.AuthoredPrefab;n.junctionPrefab=own?null:asset;n.surface=LTRoadMode.Asphalt;
                    n.foundationMode=LTRoadJunctionFoundation.MeshFootprint;n.foundationShoulder=.25f;n.blendWidth=.5f;n.radius=.1f;n.meshDepth=0;n.groundLayer=null;
                    n.connectionFit=LTRoadJunctionFit.FitProfile;n.profileMaxShift=.1f;n.profileSearchBand=.005f;n.profileBlendLength=1;n.neckLength=3;
                    var actual=n.Capture();var built=LTRoadJunctionPrefab.Build(n,true);
                    Check(built.levels.Length>=3&&actual.footprint!=null,"Authored LOD/footprint missing.");
                    Check(ReferenceEquals(actual.footprint,n.Capture().footprint),"Authored footprint not cached.");
                    checks+=LTDetailRenderer.CheckJunctionRemoval(n);
                    n.foundationMode=LTRoadJunctionFoundation.Circle;n.radius=50;
                    Check(n.Capture().footprint==null,"Legacy circle unavailable.");
                    Check(built.levels.Select(LTRoadMesh.ContentHash).SequenceEqual(LTRoadJunctionPrefab.Build(n,true).levels.Select(LTRoadMesh.ContentHash)),"Foundation changed authored mesh/LOD topology.");
                }
                report=$"PASS {checks} assertions; real Road_Perek_1 (own hierarchy + separate prefab), source LOD0 cache, depth/visibility/LOD independence, generated-output exclusion, move/mesh invalidation, holes, terrain/paint/removal parity, cached displacement map, unchanged authored meshes and legacy circle. Preview only, no scene/assets saved. Live terrain rebuilding and appearance need scene verification.";
            }
            catch(Exception e){failure=e;}
            finally
            {
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);else Debug.Log(tag+report);
        }
        static string Hash(string path)
        {using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream));}
    }
}
