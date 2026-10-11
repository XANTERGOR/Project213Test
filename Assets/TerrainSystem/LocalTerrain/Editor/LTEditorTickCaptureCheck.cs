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
    public static class LTEditorTickCaptureCheck
    {
        [MenuItem("Tools/Local Terrain/Validate Editor Capture Polling (no scene changes)")]
        public static void Run()
        {
            const string tag="[Editor Capture Polling v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();var active=SceneManager.GetActiveScene();
            string Hash(string path){using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return Convert.ToBase64String(sha.ComputeHash(stream));}
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();GameObject root=null;var owned=new List<Mesh>();
            int checks=0;Exception failure=null;
            void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;}
            try
            {
                root=new GameObject("Owned capture polling validation"){hideFlags=HideFlags.HideAndDontSave};root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root,preview);var world=root.AddComponent<LTWorld>();world.enabled=false;world.autoUpdate=false;
                world.enableLODs=false;world.enableLayerPainting=false;
                GameObject Child(string name,Transform parent)
                {var go=new GameObject(name){hideFlags=HideFlags.HideAndDontSave};go.transform.SetParent(parent,false);return go;}
                var road=Child("Owned road",root.transform).AddComponent<LTRoad>();road.enabled=false;road.mode=LTRoadMode.Asphalt;
                road.simplifyTerrainUnderAsphalt=true;var snapshot=road.Capture(world);road.bakedGeometryHash=snapshot.geometryHash;
                var output=Child("Owned generated road",road.transform);road.generatedRoot=output.transform;
                var marker=output.AddComponent<LTRoadGenerated>();marker.owner=road;
                Mesh Mesh(float y)
                {
                    var m=new Mesh{name="Owned capture triangle",hideFlags=HideFlags.HideAndDontSave};owned.Add(m);
                    m.vertices=new[]{new Vector3(0,y,0),new Vector3(0,y,2),new Vector3(2,y,0)};
                    m.triangles=new[]{0,1,2};return m;
                }
                LTRoadLOD Segment(string name,float x)
                {
                    var go=Child(name,output.transform);go.transform.localPosition=new Vector3(x,0,0);
                    var lod=go.AddComponent<LTRoadLOD>();lod.owner=road;lod.meshes=new[]{Mesh(.1f),Mesh(.05f),Mesh(.02f)};return lod;
                }
                var a=Segment("A",0);var b=Segment("B",4);root.SetActive(true);
                string Compare(string expected=null)
                {
                    var actual=LTRoadTerrainLODCapture.Capture(road,world,snapshot,out string key);
                    var reference=LTRoadCaptureReference.Capture(road,world,snapshot,out string oldKey);
                    Check(key==oldKey,"Legacy signature mismatch: "+key);
                    Check((actual==null)==(reference==null),"Legacy null/fallback mismatch");
                    if(expected!=null)Check(key==expected,"Wrong fallback: "+key);
                    if(actual!=null)
                    {
                        Check(actual.Bounds.Equals(reference.Bounds)&&actual.TriangleCount==reference.TriangleCount&&actual.EdgeError==reference.EdgeError,"Surface metadata differs");
                        for(int i=0;i<12;i++)
                        {
                            var r=new Rect(i*.6f-.1f,-.1f,.7f,.8f);
                            Check(actual.Blocks(r,0,.04,.09,.01)==reference.Blocks(r,0,.04,.09,.01),"Foundation clearance differs");
                            Check(actual.CrossesEdge(r)==reference.CrossesEdge(r),"Foundation edge differs");
                        }
                        var again=LTRoadTerrainLODCapture.Capture(road,world,snapshot,out string warmKey);
                        Check(ReferenceEquals(actual,again)&&ReferenceEquals(key,warmKey),"Warm capture did not reuse immutable surface/key");
                    }
                    return key;
                }
                string original=Compare();for(int i=0;i<20;i++)Compare();
                b.enabled=false;b.name="Renamed";Check(Compare()==original,"Disabled LOD component/name changed capture membership");
                b.transform.SetSiblingIndex(0);Check(Compare()!=original,"Sibling order missed");b.transform.SetSiblingIndex(1);Compare();
                b.transform.localPosition+=new Vector3(.00001f,0,0);Check(Compare()!=original,"Tiny transform edit missed");
                var parent=Child("Moving parent",output.transform);b.transform.SetParent(parent.transform,false);Compare();
                parent.transform.localPosition=new Vector3(.25f,0,0);Compare();parent.SetActive(false);Compare();parent.SetActive(true);Compare();
                output.SetActive(false);Compare("pending-road-bake");output.SetActive(true);Compare();
                marker.owner=null;Compare("pending-road-bake");marker.owner=road;Compare();
                road.bakedGeometryHash++;Compare("pending-road-bake");road.bakedGeometryHash=snapshot.geometryHash;Compare();
                road.simplifyTerrainUnderAsphalt=false;Compare("fixed");road.simplifyTerrainUnderAsphalt=true;Compare();
                float error=road.terrainLODEdgeError;road.terrainLODEdgeError=error+.1f;Compare();road.terrainLODEdgeError=error;Compare();
                a.owner=null;Compare();b.owner=null;Compare("missing-road-lods");a.owner=road;b.owner=road;Compare();
                var meshArray=a.meshes;a.meshes=null;Compare("unreadable-road-lods");a.meshes=Array.Empty<Mesh>();Compare("unreadable-road-lods");
                a.meshes=meshArray;var low=a.meshes[2];a.meshes[2]=null;Compare("unreadable-road-lods");a.meshes[2]=low;Compare();
                var unreadable=Mesh(.1f);unreadable.UploadMeshData(true);a.meshes[2]=unreadable;Compare("unreadable-road-lods");a.meshes[2]=low;Compare();
                a.meshes=new[]{meshArray[2],meshArray[0]};Compare();a.meshes=meshArray;Compare();
                var oldMesh=a.meshes[0];a.meshes[0]=Mesh(.15f);Compare();a.meshes[0]=oldMesh;Compare();
                string beforeMesh=Compare();var vertices=oldMesh.vertices;vertices[0].y=-.1f;oldMesh.vertices=vertices;EditorUtility.SetDirty(oldMesh);
                Check(Compare()!=beforeMesh,"Marked mesh content edit missed");
                oldMesh.subMeshCount=2;oldMesh.SetTriangles(new[]{0,1,2},0);oldMesh.SetTriangles(Array.Empty<int>(),1);Compare();
                oldMesh.SetTriangles(new[]{0,2,1},0);EditorUtility.SetDirty(oldMesh);Compare(); // unusable faces remain conservative
                a.meshes=new[]{oldMesh};b.owner=null;Compare();
                oldMesh.SetIndices(new[]{0,1},MeshTopology.Lines,0);EditorUtility.SetDirty(oldMesh);Compare();
                a.meshes=meshArray;a.meshes[0]=Mesh(.1f);b.owner=road;Compare();
                Object.DestroyImmediate(b.gameObject);Compare();b=Segment("Replacement",4);Compare();
                var emptyRoot=Child("Missing owner marker",road.transform);road.generatedRoot=emptyRoot.transform;
                Compare("pending-road-bake");road.generatedRoot=output.transform;Compare();

                // Source boundary queries are live, including disabled companions.
                // No negative cache that could miss a newly added road/generated tag.
                var nodeGo=Child("Owned junction",root.transform);nodeGo.SetActive(false);
                var node=nodeGo.AddComponent<LTRoadJunction>();node.enabled=false;node.meshSource=LTRoadJunctionSource.OwnHierarchy;
                var holder=Child("Source holder",nodeGo.transform);var leaf=Child("Source leaf",holder.transform);
                bool LegacyMember(Transform candidate)
                {
                    var source=node.SourceRoot;if(!source||!candidate)return false;
                    for(var t=candidate;t;t=t.parent)
                    {
                        if(t.GetComponent<LTRoadJunctionGenerated>()||t.GetComponent<LTRoadGenerated>())return false;
                        if(t==source.transform)return true;
                        if(t.GetComponent<LTRoadJunction>()||t.GetComponent<LTRoad>())return false;
                    }
                    return false;
                }
                void Member(bool expected)
                {Check(node.SourceMember(leaf.transform)==expected&&node.SourceMember(leaf.transform)==LegacyMember(leaf.transform),"Junction source ownership changed");}
                Member(true);Check(node.SourceMember(node.transform),"Own root rejected");
                var generated=holder.AddComponent<LTRoadGenerated>();Member(false);Object.DestroyImmediate(generated);Member(true);
                var generatedNode=holder.AddComponent<LTRoadJunctionGenerated>();Member(false);Object.DestroyImmediate(generatedNode);Member(true);
                var nested=holder.AddComponent<LTRoad>();nested.enabled=false;Member(false);Object.DestroyImmediate(nested);Member(true);
                var nestedNode=holder.AddComponent<LTRoadJunction>();nestedNode.enabled=false;Member(false);Object.DestroyImmediate(nestedNode);Member(true);
                leaf.transform.SetParent(root.transform,false);Member(false);leaf.transform.SetParent(holder.transform,false);Member(true);
                node.meshSource=LTRoadJunctionSource.AuthoredPrefab;node.junctionPrefab=null;Member(false);
                node.junctionPrefab=holder;Member(true);Check(!node.SourceMember(null),"Null source accepted");
            }
            catch(Exception ex){failure=ex;}
            finally
            {
                if(root)Object.DestroyImmediate(root);foreach(var mesh in owned)if(mesh)Object.DestroyImmediate(mesh);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                if(SceneManager.GetActiveScene()!=active)failure=new InvalidOperationException("Active user scene changed.",failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);
            else Debug.Log(tag+$"PASS {checks} checks; frozen legacy road-key/surface parity, warm reuse, live LOD/parent/order/mesh/revision/fallback edits and junction ownership. Owned preview only; no scene/assets/settings saved. Live Capture timing requires a new flight.");
        }
    }
}
