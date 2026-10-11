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
    internal static class LTSpatialMaskCacheCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate LOD Border Mask Cache (no scene changes)
        static void Run()=>RunCheck(false);
        // Archived validation menu: Tools/Local Terrain/Validate LOD Neighbour Lookup (no scene changes)
        static void RunNeighbours()=>RunCheck(true);
        [MenuItem("Tools/Local Terrain/Validate LOD Index Copy (no scene changes)")]
        static void RunIndexCopy()=>RunCheck(true,true);
        static void RunCheck(bool neighbours,bool indexCopy=false)
        {
            if(EditorApplication.isCompiling||EditorApplication.isPlayingOrWillChangePlaymode)return;
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var active=SceneManager.GetActiveScene();
            string Hash(string path){using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return Convert.ToBase64String(sha.ComputeHash(stream));}
            var files=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*.cs",SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("ProjectSettings")).Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"})
                .Concat(scenes.Select(s=>s.scene.path)).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();GameObject root=null;Exception failure=null;int checks=0;
            void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;}
            try
            {
                const int divisions=2;const float width=16;
                root=new GameObject("Owned border mask cache check"){hideFlags=HideFlags.HideAndDontSave};root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root,preview);
                var source=new LTSpatialLODTopology.Chunk[2];var chunks=new LTChunk[2];var banks=new Mesh[2];var originals=new int[2][];
                for(int c=0;c<2;c++)
                {
                    var plans=new List<Vector3Int>[4];
                    for(int level=0;level<plans.Length;level++)
                    {
                        plans[level]=new List<Vector3Int>();int size=LTSpatialLODMath.N/(16>>level);
                        for(int z=0;z<LTSpatialLODMath.N;z+=size)for(int x=0;x<LTSpatialLODMath.N;x+=size)plans[level].Add(new Vector3Int(x,z,size));
                    }
                    var data=LTSpatialLODMath.BuildLayout(plans,divisions,50000,_=>0);
                    LTStampMesh.EmitSpatialVariants(data,new Rect(c*width,0,width,width),new Vector2(32,16),50000,(x,z)=>.02f*x+.001f*z*z,null);
                    source[c]=new LTSpatialLODTopology.Chunk{cells=data.cells,patches=data.patches};
                    var bank=new Mesh{name="Owned spatial bank",hideFlags=HideFlags.HideAndDontSave,indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};owned.Add(bank);
                    bank.vertices=data.vertices;bank.normals=data.normals;bank.uv=data.uv;bank.triangles=data.baseIndices;banks[c]=bank;originals[c]=bank.triangles;
                    var asset=ScriptableObject.CreateInstance<LTSpatialLODAsset>();asset.hideFlags=HideFlags.HideAndDontSave;owned.Add(asset);
                    asset.vertexBank=bank;asset.cells=data.cells;asset.patches=data.patches;asset.formatVersion=LTSpatialLODMath.Version;asset.divisions=divisions;
                    var go=new GameObject("Owned chunk "+c){hideFlags=HideFlags.HideAndDontSave};go.transform.SetParent(root.transform,false);
                    go.AddComponent<MeshFilter>().sharedMesh=bank;go.AddComponent<MeshRenderer>();go.AddComponent<MeshCollider>().sharedMesh=bank;
                    chunks[c]=go.AddComponent<LTChunk>();chunks[c].mesh=bank;chunks[c].spatialLOD=asset;
                }
                root.SetActive(true);
                var actual=new LTSpatialLODTopology(2,1,divisions,source);
                var reference=new LTSpatialLODTopology(2,1,divisions,source,0,0);
                if(neighbours)
                {
                    Check(actual.NeighbourCacheBytes==source.Sum(c=>(long)c.cells.Length*4),"Parent lookup size differs");
                    Check(reference.NeighbourCacheBytes==0,"Root-walk reference allocated parent links");
                }
                // Force full masks only on the private reference. Native mesh
                // publication is compared with fresh four-sided tree queries.
                var patchArray=(Array)typeof(LTSpatialLODTopology).GetField("patches",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(reference);
                var dirtyField=patchArray.GetValue(0).GetType().GetField("maskDirty");
                var random=new System.Random(3807);var expected=new List<int>();var got=new List<int>();
                for(int pass=0;pass<40;pass++)
                {
                    for(int c=0;c<2;c++)for(int p=0;p<4;p++)
                    {int level=pass==0||pass==39?0:random.Next(4);actual.SetLevel(c,p,level);reference.SetLevel(c,p,level);}
                    actual.Update();reference.Update();actual.Validate();reference.Validate();
                    if(neighbours)Check(actual.ProcessedCells==reference.ProcessedCells&&actual.ResetPatches==reference.ResetPatches,"Balance propagation changed");
                    foreach(var patch in patchArray)dirtyField.SetValue(patch,16);
                    Check(actual.DirtyChunks.SetEquals(reference.DirtyChunks),"Dirty chunk publication changed");
                    for(int c=0;c<2;c++)
                    {
                        reference.WriteIndices(c,expected);actual.WriteIndices(c,got);Check(got.SequenceEqual(expected),"Cached mask indices differ from full queries");
                        if(indexCopy)
                        {
                            if(got.Count>0)got[0]=-1;got.Add(-1);actual.WriteIndices(c,got);
                            Check(got.SequenceEqual(expected),"Mutable caller output reached cache, or cached capacity leaked");
                            Check(actual.IndexCacheBytes<=64*1024*1024,"Index cache exceeded payload budget");
                        }
                        chunks[c].ShowSpatialLOD(actual,c,actual.DirtyChunks.Contains(c));
                        var visible=chunks[c].GetComponent<MeshFilter>().sharedMesh;
                        Check(visible!=banks[c]&&visible.GetIndices(0).SequenceEqual(expected),"Native mesh publication differs");
                        Check(banks[c].triangles.SequenceEqual(originals[c]),"Source mesh changed");
                        Check(chunks[c].GetComponent<MeshCollider>().sharedMesh==banks[c],"LOD0 collider changed");
                        chunks[c].ShowSpatialLOD(actual,c,false);Check(chunks[c].GetComponent<MeshFilter>().sharedMesh==visible,"Idle clone replaced");
                    }
                }
                // Existing geometric check exercises required cut midpoints,
                // invariant native seam bands and near/far restoration.
                LTRockNativeLODCheck.Run();checks++; // Throws if a contour/ownership contract fails.
            }
            catch(Exception ex){failure=ex;}
            finally
            {
                if(root)Object.DestroyImmediate(root);foreach(var item in owned)if(item)Object.DestroyImmediate(item);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene changed: "+s.scene.name,failure);
                if(SceneManager.GetActiveScene()!=active)failure=new InvalidOperationException("Active user scene changed",failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)failure=new InvalidOperationException("File changed: "+f.Key,failure);
            }
            string label=indexCopy?"LOD Index Copy v1":neighbours?"LOD Neighbour Lookup v1":"LOD Border Mask Cache v1";
            if(failure!=null)Debug.LogError($"[{label}] FAIL: "+failure);
            else Debug.Log($"[{label}] PASS {checks} checks; root-walk/full-query parity, native index uploads, private clones, LOD0 colliders and native seam geometry. Owned preview only; no scene/assets/settings saved. Live speed requires a new flight.");
        }
    }
}
