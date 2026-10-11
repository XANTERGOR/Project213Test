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
    public static class LTRoadJunctionTerrainLODCheck
    {
        [MenuItem("Tools/Local Terrain/Validate Junction Foundation LOD (no scene changes)")]
        public static void Run()
        {
            const string tag="[Junction Foundation LOD v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();var active=SceneManager.GetActiveScene();
            string Hash(string path){using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return Convert.ToBase64String(sha.ComputeHash(stream));}
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();GameObject root=null;var owned=new List<Mesh>();
            int checks=0;Exception failure=null;string data=null;
            void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;}
            try
            {
                data=LTRoadJunctionTerrainLODData.Run();
                root=new GameObject("Owned junction LOD validation"){hideFlags=HideFlags.HideAndDontSave};root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root,preview);var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.enableLODs=false;world.enableLayerPainting=false;
                GameObject Child(string name,Transform parent)
                {var go=new GameObject(name){hideFlags=HideFlags.HideAndDontSave};go.transform.SetParent(parent,false);return go;}
                Mesh Mesh(float y)
                {
                    var p=LTRoadJunctionTerrainLODData.Shape(y);var m=new Mesh{name="Owned junction T",hideFlags=HideFlags.HideAndDontSave};
                    owned.Add(m);m.vertices=p.vertices;m.triangles=p.indices;return m;
                }
                var node=Child("Owned junction",root.transform).AddComponent<LTRoadJunction>();
                node.meshSource=LTRoadJunctionSource.OwnHierarchy;node.surface=LTRoadMode.Asphalt;
                var source=Child("Source LOD0",node.transform);source.AddComponent<MeshFilter>().sharedMesh=Mesh(.06f);source.AddComponent<MeshRenderer>();
                Child("Port A",node.transform).AddComponent<LTRoadJunctionSocket>();
                var port=Child("Port B",node.transform);port.transform.localPosition=Vector3.forward*4;port.AddComponent<LTRoadJunctionSocket>();
                var output=Child("Generated junction",node.transform);node.generatedRoot=output.transform;
                var marker=output.AddComponent<LTRoadJunctionGenerated>();marker.owner=node;
                var lod=output.AddComponent<LTRoadLOD>();lod.junction=node;lod.meshes=new[]{Mesh(.06f),Mesh(.04f),Mesh(.02f)};
                var filter=output.AddComponent<MeshFilter>();filter.sharedMesh=lod.meshes[0];
                var collider=output.AddComponent<MeshCollider>();collider.sharedMesh=lod.meshes[0];
                var originalVertices=lod.meshes[0].vertices;var originalTriangles=lod.meshes[0].triangles;
                var originalCollider=collider.sharedMesh;root.SetActive(true);
                LTRoadTerrainSurface Capture(out string key)=>LTRoadJunctionTerrainLODCapture.Capture(node,world,out key);
                void Fallback(string expected){Check(Capture(out var key)==null&&key==expected,"Wrong fallback: "+key);}
                var surface=Capture(out var initial);Check(surface!=null&&surface.TriangleCount==30,"Not all three generated LODs captured.");
                Check(!surface.Blocks(new Rect(7,3,1,1),0,0,0,0),"Safe foundation blocked.");
                for(int i=0;i<12;i++)Check(ReferenceEquals(surface,Capture(out var key))&&ReferenceEquals(initial,key),"Warm surface/key not reused.");
                foreach(var mesh in lod.meshes)
                {filter.sharedMesh=mesh;Check(ReferenceEquals(surface,Capture(out var key))&&key==initial,"Current display LOD changed foundation.");}
                filter.sharedMesh=lod.meshes[0];
                var original=lod.meshes[2];lod.meshes[2]=Mesh(-.02f);
                var low=Capture(out var changed);Check(changed!=initial&&low.Blocks(new Rect(7,3,1,1),0,0,0,0),"Low LOD replacement ignored.");
                lod.meshes[2]=original;surface=Capture(out initial);
                node.terrainLODEdgeError=.2f;Check(Capture(out changed).EdgeError==.2f&&changed!=initial,"Edge error edit missed.");node.terrainLODEdgeError=.03f;
                node.simplifyTerrainUnderAsphalt=false;Fallback("fixed-junction");node.simplifyTerrainUnderAsphalt=true;
                output.SetActive(false);Fallback("missing-junction-bake");output.SetActive(true);
                marker.owner=null;Fallback("missing-junction-bake");marker.owner=node;
                lod.junction=null;Fallback("missing-junction-lods");lod.junction=node;
                var meshes=lod.meshes;lod.meshes=null;Fallback("missing-junction-lods");lod.meshes=Array.Empty<Mesh>();Fallback("missing-junction-lods");lod.meshes=meshes;
                lod.meshes[2]=null;Fallback("unreadable-junction-lods");
                var unreadable=Mesh(.02f);unreadable.UploadMeshData(true);lod.meshes[2]=unreadable;Fallback("unreadable-junction-lods");lod.meshes[2]=original;
                var lines=Mesh(.02f);lines.SetIndices(new[]{0,1},MeshTopology.Lines,0);lod.meshes[2]=lines;
                Check(Capture(out _) == null,"Non-triangle LOD accepted.");lod.meshes[2]=original;
                var replacement=Mesh(.02f);lod.meshes[2]=replacement;Capture(out var beforeEdit);
                var v=replacement.vertices;for(int i=0;i<v.Length;i++)v[i].y=-.03f;replacement.vertices=v;EditorUtility.SetDirty(replacement);
                Check(Capture(out changed).Blocks(new Rect(7,3,1,1),0,0,0,0)&&changed!=beforeEdit,"In-place mesh revision missed.");lod.meshes[2]=original;
                var beforeMove=Capture(out initial);node.transform.localPosition=new Vector3(902,2,137);
                var moved=Capture(out changed);Check(changed!=initial&&Mathf.Abs(moved.Bounds.xMin-beforeMove.Bounds.xMin-902)<.001f,"Parent transform missed.");
                node.transform.localPosition=Vector3.zero;
                Check(LTEditorEngine.CheckJunctionTerrainLODStamp(world,node),"Production stamp did not receive surface/key/bounds or fallback.");checks++;
                Check(originalVertices.SequenceEqual(lod.meshes[0].vertices)&&originalTriangles.SequenceEqual(lod.meshes[0].triangles)&&collider.sharedMesh==originalCollider,"Source LOD0/collider changed.");
                // Re-enable the guard, not a source mesh rewrite, when generated output goes missing.
                node.generatedRoot=null;Fallback("missing-junction-bake");node.generatedRoot=output.transform;
            }
            catch(Exception e){failure=e;}
            finally
            {
                if(root)Object.DestroyImmediate(root);foreach(var mesh in owned)if(mesh)Object.DestroyImmediate(mesh);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                if(SceneManager.GetActiveScene()!=active)failure=new InvalidOperationException("Active scene changed.",failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);
            else Debug.Log(tag+$"PASS {checks} native capture checks. "+data+" Owned preview only; no scene/assets/settings saved. Live junction rebuild and appearance remain to be checked.");
        }
    }
    public static partial class LTEditorEngine
    {
        internal static bool CheckJunctionTerrainLODStamp(LTWorld world,LTRoadJunction node)
        {
            if(!EditorSceneManager.IsPreviewScene(world.gameObject.scene))throw new InvalidOperationException("Owned preview only.");
            var root=node.generatedRoot;var position=root.localPosition;bool simplify=node.simplifyTerrainUnderAsphalt;
            try
            {
                var first=Capture(world).Single(s=>s.id==node.GetInstanceID());
                var exact=LTRoadJunctionTerrainLODCapture.Capture(node,world,out var key);
                if(exact==null||!ReferenceEquals(first.roadTerrainSurface,exact)||first.roadTerrainKey!=key||!first.fixedRoadSurface)return false;
                if(!first.bounds.Equals(LTRoadTerrainSurface.Union(node.Capture().bounds,exact.Bounds)))return false;
                var preview=Capture(world,true).Single(s=>s.id==node.GetInstanceID());
                if(preview.roadTerrainSurface!=null||preview.roadTerrainKey!=null)return false;
                node.simplifyTerrainUnderAsphalt=false;var fallback=Capture(world).Single(s=>s.id==node.GetInstanceID());
                if(fallback.roadTerrainSurface!=null||fallback.signature==first.signature||!fallback.fixedRoadSurface)return false;
                node.simplifyTerrainUnderAsphalt=true;root.localPosition=new Vector3(30,0,0);
                var moved=Capture(world).Single(s=>s.id==node.GetInstanceID());
                return moved.signature!=first.signature&&moved.roadTerrainKey!=first.roadTerrainKey&&moved.bounds.xMax>first.bounds.xMax;
            }
            finally {root.localPosition=position;node.simplifyTerrainUnderAsphalt=simplify;states.Remove(world.GetInstanceID());}
        }
    }
}
