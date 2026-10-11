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
    public static partial class LTEditorEngine
    {
        [MenuItem("Tools/Local Terrain/Validate Editor Tick Polling (no scene changes)")]
        public static void ValidateEditorTickPolling()
        {
            const string tag="[Editor Tick Polling v1] ";
            if(EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var active=SceneManager.GetActiveScene();
            string Hash(string path){using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return Convert.ToBase64String(sha.ComputeHash(stream));}
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();GameObject root=null;int worldId=0,checks=0;Exception failure=null;
            void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
            try
            {
                root=new GameObject("Owned editor tick validation"){hideFlags=HideFlags.HideAndDontSave};root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root,preview);var world=root.AddComponent<LTWorld>();world.enabled=false;world.autoUpdate=false;
                world.chunksX=world.chunksZ=3;world.fixedChunkSize=new Vector2(8,8);world.enableLayerPainting=false;world.enableLODs=false;
                worldId=world.GetInstanceID();
                GameObject Child(string name,Transform parent)
                {var go=new GameObject(name){hideFlags=HideFlags.HideAndDontSave};go.transform.SetParent(parent,false);return go;}
                world.generatedRoot=Child("Owned generated",root.transform).transform;
                LTChunk Chunk(int x,int z)
                {var go=Child("Owned chunk",world.generatedRoot);go.SetActive(false);var c=go.AddComponent<LTChunk>();c.enabled=false;c.x=x;c.z=z;go.SetActive(true);return c;}
                var a=Chunk(0,0);var b=Chunk(1,0);root.SetActive(true);var state=GetState(world);
                string LegacyLayout()
                {
                    var chunks=world.generatedRoot.GetComponentsInChildren<LTChunk>();
                    return world.chunkOrigin+":"+world.chunksX+":"+world.chunksZ+":"+
                        string.Join(";",chunks.OrderBy(c=>world.ChunkId(c.x,c.z)).Select(c=>$"{c.x},{c.z}:{c.GetInstanceID()}"));
                }
                void Layout()
                {DetectChunkLayout(world,state);Check(state.chunkLayout==LegacyLayout(),"Legacy/native layout parity");}
                Layout();long revision=state.revision;string original=state.chunkLayout;var live=state.liveChunks;
                state.dirty.Add(0);state.colliders.Add(0);
                for(int i=0;i<20;i++)Layout();
                Check(state.revision==revision&&ReferenceEquals(original,state.chunkLayout)&&ReferenceEquals(live,state.liveChunks)
                    &&state.dirty.Contains(0)&&state.colliders.Contains(0),"Unchanged poll rebuilt signature or disturbed pending work");
                a.name="Renamed only";a.transform.SetSiblingIndex(1);Layout();Check(state.revision==revision,"Sibling order/name changed layout");
                b.x=2;Layout();Check(state.revision>revision&&state.liveChunks.SetEquals(new[]{0,2})&&state.dirty.Count==0,"Direct coordinate edit not detected");
                b.gameObject.SetActive(false);Layout();Check(state.liveChunks.SetEquals(new[]{0}),"Inactive chunk retained");
                b.gameObject.SetActive(true);Layout();Check(state.liveChunks.SetEquals(new[]{0,2}),"Reactivated disabled component lost");
                int oldId=b.GetInstanceID();Object.DestroyImmediate(b.gameObject);b=Chunk(2,0);Layout();
                Check(b.GetInstanceID()!=oldId&&state.chunkLayout.Contains(b.GetInstanceID().ToString()),"Same-coordinate replacement missed");
                var c=Chunk(0,1);Layout();Check(state.liveChunks.Count==3,"Added chunk missed");
                var off=Child("Inactive chunk parent",world.generatedRoot);off.SetActive(false);c.transform.SetParent(off.transform,false);
                Layout();Check(state.liveChunks.Count==2,"Inactive ancestor missed");c.transform.SetParent(world.generatedRoot,false);Layout();
                revision=state.revision;b.x=0;bool rejected=false;
                try{DetectChunkLayout(world,state);}catch(InvalidOperationException){rejected=true;}
                Check(rejected&&state.revision==revision,"Duplicate coordinate accepted/committed");b.x=2;Layout();
                b.x=99;rejected=false;try{DetectChunkLayout(world,state);}catch(InvalidOperationException){rejected=true;}
                Check(rejected&&state.revision==revision,"Out-of-grid coordinate accepted/committed");b.x=2;Layout();
                world.chunkOrigin=new Vector2Int(-1,-1);world.chunksX=world.chunksZ=4;Layout();
                Check(state.liveChunks.SetEquals(new[]{world.ChunkId(0,0),world.ChunkId(2,0),world.ChunkId(0,1)}),"Expanded grid IDs stale");
                revision=state.revision;state.chunkLayout=null;Layout();Check(state.revision>revision,"Explicit invalidation skipped by fast path");
                Object.DestroyImmediate(a.gameObject);Object.DestroyImmediate(b.gameObject);Object.DestroyImmediate(c.gameObject);Layout();
                Check(state.liveChunks.Count==0,"Empty generated root not detected");

                // Compare authoring hierarchy semantics. No negative-result cache:
                // adding a companion after a failed lookup must work immediately.
                var holder=Child("Ordinary hierarchy",root.transform);
                for(int i=0;i<32;i++)Child("Empty authoring node",holder.transform);
                var first=Child("Height A",holder.transform).AddComponent<LTHeightStamp>();
                var second=Child("Height B",first.transform).AddComponent<LTHeightStamp>();
                var inactive=Child("Inactive height",holder.transform).AddComponent<LTHeightStamp>();inactive.gameObject.SetActive(false);
                var generated=Child("Generated road boundary",holder.transform);generated.AddComponent<LTRoadGenerated>();
                Child("Hidden authoring",generated.transform).AddComponent<LTHeightStamp>();
                var nestedGo=Child("Nested world",holder.transform);nestedGo.SetActive(false);
                var nested=nestedGo.AddComponent<LTWorld>();nested.enabled=false;nested.autoUpdate=false;nestedGo.SetActive(true);
                Child("Other world's height",nested.transform).AddComponent<LTHeightStamp>();
                int[] Captured()=>Capture(world).Select(s=>s.id).ToArray();
                Check(Captured().SequenceEqual(new[]{first.GetInstanceID(),second.GetInstanceID()}),"Capture DFS ownership / generated / inactive filters changed");
                first.enabled=false;Check(Captured().SequenceEqual(new[]{second.GetInstanceID()}),"Disabled height stopped child traversal");first.enabled=true;
                second.transform.SetParent(holder.transform,false);second.transform.SetSiblingIndex(0);
                Check(Captured().SequenceEqual(new[]{second.GetInstanceID(),first.GetInstanceID()}),"Live sibling reorder missed");
                second.transform.localPosition=Vector3.one;var captured=Capture(world);var cold=Capture(world,true);
                Check(captured.Select(s=>s.signature).SequenceEqual(cold.Select(s=>s.signature)),"Live/cold capture mismatch");
                var paint=first.gameObject.AddComponent<LTPaintStamp>();paint.enabled=false;
                var hiddenPaint=inactive.gameObject.AddComponent<LTPaintStamp>();
                Child("Generated paint",generated.transform).AddComponent<LTPaintStamp>();
                Child("Other world's paint",nested.transform).AddComponent<LTPaintStamp>();
                Check(!paint.Road&&!paint.Junction,"Absent paint companions not null");
                var road=first.gameObject.AddComponent<LTRoad>();road.enabled=false;Check(paint.Road==road,"New/disabled road companion not visible");
                Object.DestroyImmediate(road);Check(!paint.Road,"Removed road companion retained");
                // Junction goes on an inactive object to avoid any source-visibility callback.
                inactive.gameObject.SetActive(false);var node=inactive.gameObject.AddComponent<LTRoadJunction>();node.enabled=false;
                Check(hiddenPaint.Junction==node,"New/disabled junction companion not visible");
                var palette=world.CollectPaintStamps();
                Check(palette.SequenceEqual(new[]{paint,hiddenPaint}),"Paint includes disabled/inactive stamps; junction-last order/ownership changed");
                Object.DestroyImmediate(node);Check(!hiddenPaint.Junction,"Removed junction companion retained");
                // Chunk and capture checks never rebuild, paint, bake, or save a world.
            }
            catch(Exception ex){failure=ex;}
            finally
            {
                if(worldId!=0)states.Remove(worldId);if(root)Object.DestroyImmediate(root);EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                if(SceneManager.GetActiveScene()!=active)failure=new InvalidOperationException("Active user scene changed.",failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(tag+"FAIL: "+failure);
            else Debug.Log(tag+$"PASS {checks} checks; live exact chunk polling, legacy signature parity, mutation/activation/replacement/errors/invalidation, hierarchy capture and optional paint companions. Owned preview only; no scene/assets/settings saved. Real Tick spike and allocation timings require a new flight.");
        }
    }
}
