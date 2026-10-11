using System;
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
    public static class LTRoadDimensionPreviewCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Road Dimension Preview (no scene changes)
        public static void Run()
        {
            const string tag="[Road Dimension Preview v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","LTRoad*.cs",SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var scene=EditorSceneManager.NewPreviewScene();GameObject root=null;Exception failure=null;int checks=0;
            void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            try
            {
                root=new GameObject("Owned road dimension preview check"){hideFlags=HideFlags.HideAndDontSave};root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root,scene);var world=root.AddComponent<LTWorld>();world.enabled=false;world.autoUpdate=false;
                root.SetActive(true); // Parent lookup needs an active hierarchy; LTWorld stays disabled.
                root.transform.position=new Vector3(40,5,20);root.transform.rotation=Quaternion.Euler(0,13,0);
                var child=new GameObject("Owned road"){hideFlags=HideFlags.HideAndDontSave};child.SetActive(false);child.transform.SetParent(root.transform,false);
                child.transform.localPosition=new Vector3(100,4,200);child.transform.localRotation=Quaternion.Euler(0,27,0);
                var road=child.AddComponent<LTRoad>();road.enabled=false;road.mode=LTRoadMode.Asphalt;road.width=6;road.shoulderWidth=1;road.blendWidth=3;
                road.points=new System.Collections.Generic.List<LTRoadPoint>{new LTRoadPoint(new Vector3(0,1,0),-8),new LTRoadPoint(new Vector3(4,3,20),5),new LTRoadPoint(new Vector3(9,5,45),12)};
                var baseline=road.Capture(world);string original=EditorJsonUtility.ToJson(road);int dirty=EditorUtility.GetDirtyCount(road);
                var values=new Vector3(road.width,road.shoulderWidth,road.blendWidth);
                for(int field=0;field<3;field++)for(int i=1;i<=20;i++)
                {
                    var next=values;next[field]+=i*.1f;
                    var draft=road.PreviewDimensions(next.x,next.y,next.z);
                    Check(draft.width==next.x&&draft.shoulderWidth==next.y&&draft.blendWidth==next.z,"Draft dimensions incorrect.");
                    Check(draft.geometryHash!=baseline.geometryHash,"Draft signature unchanged.");
                    Check(baseline.samples.SequenceEqual(draft.samples),"Dimension preview changed spline geometry.");
                    Check(ReferenceEquals(road.Capture(world),baseline),"Draft replaced live road cache.");
                    Check(EditorJsonUtility.ToJson(road)==original&&EditorUtility.GetDirtyCount(road)==dirty,"Preview wrote serialized settings or dirtied road.");
                    Check(!road.generatedRoot,"Preview created output geometry.");
                }
                foreach(float bad in new[]{float.NaN,float.PositiveInfinity,-1f,10001f})
                {
                    bool failed=false;try{road.PreviewDimensions(bad,1,3);}catch(ArgumentException){failed=true;}
                    Check(failed&&ReferenceEquals(road.Capture(world),baseline)&&EditorJsonUtility.ToJson(road)==original,"Invalid preview damaged committed road.");
                }
                var unchanged=road.PreviewDimensions(6,1,3);
                Check(unchanged.geometryHash==baseline.geometryHash&&unchanged.samples.SequenceEqual(baseline.samples),"Returning to original dimensions changes geometry.");
                var outline=LTRoadFoundationOutline.Build(road.PreviewDimensions(8,2,4),false,false);
                Check(outline.shoulder.Length>0&&outline.transition.Length>0,"Draft cannot drive contour preview.");
                Check(ReferenceEquals(road.Capture(world),baseline)&&EditorUtility.GetDirtyCount(road)==dirty,"Discarding draft changed committed state.");
            }
            catch(Exception e){failure=e;}
            finally
            {
                if(root)Object.DestroyImmediate(root);EditorSceneManager.ClosePreviewScene(scene);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);
            else Debug.Log(tag+$"PASS {checks} checks; translated/yawed road, all three detached dimensions, repeated preview, cache and serialized/dirty-state isolation, invalid input, original-value restore, preview contours. No scene/assets saved. Mouse capture, Esc and Undo/Redo still require Scene interaction.");
        }
        static string Hash(string path)
        {using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream));}
    }
}
