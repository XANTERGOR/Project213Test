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
    public static class LTDetailPlacementCheckMenu
    {
        // Archived validation menu: Tools/Local Terrain/Validate Saved Detail Placement (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            const string tag="[Saved Detail Placement v1] ";
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var files=scenes.Select(s=>s.scene.path).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories))
                .Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();Exception failure=null;string result=null;
            string folder="Logs/DetailPlacementChecks/"+Guid.NewGuid().ToString("N");
            T Own<T>(T value)where T:Object{owned.Add(value);return value;}
            try
            {
                var root=Own(new GameObject("Owned detail placement check"));root.SetActive(false);SceneManager.MoveGameObjectToScene(root,preview);
                var world=root.AddComponent<LTWorld>();world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;
                root.transform.position=new Vector3(30,2,50);
                var renderer=root.AddComponent<LTDetailRenderer>();
                var child=Own(new GameObject("Owned surface"));child.SetActive(false);SceneManager.MoveGameObjectToScene(child,preview);child.transform.SetParent(root.transform,false);
                var chunk=child.AddComponent<LTChunk>();chunk.x=-1;chunk.z=0;
                var mesh=Own(new Mesh{name="Owned plane"});mesh.vertices=new[]{new Vector3(-8,0,0),new Vector3(0,0,0),new Vector3(-8,0,8),new Vector3(0,0,8)};
                mesh.normals=Enumerable.Repeat(Vector3.up,4).ToArray();mesh.triangles=new[]{0,2,1,1,2,3};chunk.mesh=mesh;
                var layer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());
                var texture=Own(new Texture2D(2,2,TextureFormat.RGBA32,false,true));texture.SetPixels(Enumerable.Repeat(Color.red,4).ToArray());texture.Apply();
                var asset=Own(ScriptableObject.CreateInstance<LTDetailPlacementAsset>());
                LTDetailPlacementAsset RoundTrip(LTDetailPlacementAsset value)
                {
                    Directory.CreateDirectory(folder);string path=folder+"/placement.bin";
                    UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(new Object[]{value,layer},path,false);
                    var loaded=UnityEditorInternal.InternalEditorUtility.LoadSerializedFileAndForget(path);owned.AddRange(loaded);
                    return loaded.OfType<LTDetailPlacementAsset>().Single();
                }
                result=renderer.CheckSavedPlacement(chunk,layer,texture,asset,RoundTrip);
            }
            catch(Exception e){failure=e;}
            finally
            {
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    failure=new InvalidOperationException("User scene state changed: "+s.scene.name,failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            Directory.CreateDirectory(folder);
            string report=tag+(failure==null?result:"FAIL: "+failure)+"\nUser scenes/assets were not saved. Owned binary fixture retained in "+folder;
            File.WriteAllText(folder+"/result.txt",report);
            if(failure==null)Debug.Log(report);else Debug.LogError(report);
        }
        static string Hash(string path){using var file=File.OpenRead(path);using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(file));}
    }
}
