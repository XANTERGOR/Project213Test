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
    public static class LTRoadModuleEndsCheck
    {
        const string Tag="[Road Modules v2 / Grass] ";
        const string Path="Assets/Data/Models/Environment/Roads/Road_1_Line.prefab";
        // Archived validation menu: Tools/Local Terrain/Validate Road Module Ends (no scene changes)
        // Archived validation menu: Tools/Local Terrain/Validate Road Modules and Grass (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var files=AssetDatabase.GetDependencies(Path,true).Concat(new[]{Path,Path+".meta"})
                .Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();LTRoad road=null;
            string success=null;Exception failure=null;
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            GameObject Child(string name,Transform parent=null)
            {
                var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,preview);
                if(parent)go.transform.SetParent(parent,false);return go;
            }
            try
            {
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Path);Require(prefab,"Road_1_Line prefab not found.");
                var sourceMeshes=prefab.GetComponentsInChildren<MeshFilter>(true).Select(f=>f.sharedMesh).Distinct().ToArray();
                Require(sourceMeshes.Length>0,"Prefab has no meshes.");
                var sourceHashes=sourceMeshes.ToDictionary(m=>m,MeshHash);
                var root=Child("Owned module seam validation");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.previewLODs=false;world.enableLayerPainting=false;
                root.SetActive(true); // GetComponentInParent must see the disabled world on an ACTIVE parent.
                road=Child("Owned road",root.transform).AddComponent<LTRoad>();road.enabled=false;
                road.mode=LTRoadMode.Asphalt;road.asphaltSource=LTRoadMeshSource.Module;road.asphaltModulePrefab=prefab;
                road.moduleAxis=LTRoadModuleAxis.Z;road.meshChunkLength=1;road.width=5;road.sampleSpacing=.5f;
                road.points=new List<LTRoadPoint>{new LTRoadPoint(new Vector3(0,1,0),4),new LTRoadPoint(new Vector3(1,3,25),9),new LTRoadPoint(new Vector3(8,5,55),-3)};
                var snapshot=road.Capture(world);
                bool strictFailed=false;
                try{LTRoadModuleSource.Build(road,snapshot);}catch(InvalidOperationException e){strictFailed=e.Message.Contains("Торцы модуля различаются");}
                Require(strictFailed,"Fixture should reproduce the original mismatched-end error with fitting OFF.");
                road.moduleFitEnds=true;road.moduleEndBlendLength=.5f;road.moduleEndMaxMismatch=.05f;
                var fitted=LTRoadModuleSource.Build(road,snapshot);CheckSeams(fitted);
                var cached=LTRoadModuleSource.Build(road,snapshot);
                Require(Hashes(fitted)==Hashes(cached),"Repeated build/cache changed output.");
                road.moduleEndMaxMismatch=.001f;bool limitFailed=false;
                try{LTRoadModuleSource.Build(road,snapshot);}catch(InvalidOperationException){limitFailed=true;}
                Require(limitFailed,"Lower mismatch limit reused an invalid cached fit.");
                road.moduleEndMaxMismatch=.05f;
                Require(Hashes(fitted)==Hashes(LTRoadModuleSource.Build(road,snapshot)),"Restoring fit settings failed.");
                // The individual imported Mesh path, independent of prefab material extraction.
                if(sourceMeshes.Length==1)
                {
                    road.asphaltModulePrefab=null;road.asphaltModule=sourceMeshes[0];road.asphaltMaterial=fitted.materials[0];
                    CheckSeams(LTRoadModuleSource.Build(road,snapshot));
                    var alternate=Own(Object.Instantiate(sourceMeshes[0]));var positions=alternate.vertices;
                    float minZ=positions.Min(p=>p.z),maxZ=positions.Max(p=>p.z);
                    for(int i=0;i<positions.Length;i++)
                    {
                        if(positions[i].z>minZ+.5f&&positions[i].z<maxZ-.5f)positions[i].y+=.02f;
                        positions[i].z*=.75f; // Different source length, same cross section.
                    }
                    alternate.vertices=positions;alternate.RecalculateBounds();
                    var secondMaterial=Own(new Material(fitted.materials[0]));
                    road.asphaltMaterial=null;road.moduleAvoidRepeat=false;
                    road.moduleVariants=new[]{new LTRoadModuleVariant{prefab=prefab},new LTRoadModuleVariant{mesh=alternate,
                        materials=Enumerable.Repeat(secondMaterial,alternate.subMeshCount).ToArray()}};
                    var mixed=LTRoadModuleSource.Build(road,snapshot);CheckSeams(mixed);
                    Require(mixed.materials.Contains(secondMaterial)&&mixed.materials.Contains(fitted.materials[0]),"Pool material palette lost a module material.");
                    Require(Hashes(mixed)==Hashes(LTRoadModuleSource.Build(road,snapshot)),"Pool build/cache nondeterministic.");
                    int seed=road.moduleRandomSeed;var order=LTRoadModuleMath.SelectModules(new[]{1f,1f},mixed.levels[0].Count,seed,false);
                    do{road.moduleRandomSeed++;}while(order.SequenceEqual(LTRoadModuleMath.SelectModules(new[]{1f,1f},mixed.levels[0].Count,road.moduleRandomSeed,false)));
                    var shuffled=LTRoadModuleSource.Build(road,snapshot);CheckSeams(shuffled);
                    Require(Hashes(mixed)!=Hashes(shuffled),"Seed change did not reshuffle native output.");
                    road.moduleRandomSeed=seed;road.moduleVariants[1].weight=0;
                    Require(Hashes(LTRoadModuleSource.Build(road,snapshot))==Hashes(fitted),"Disabled variant / legacy single module mismatch.");
                    road.moduleVariants[1].weight=1;
                    Require(Hashes(LTRoadModuleSource.Build(road,snapshot))==Hashes(mixed),"Restoring pool weight did not restore output.");
                }
                var segment=Child("Owned LOD switch",root.transform);
                var filter=segment.AddComponent<MeshFilter>();var collider=segment.AddComponent<MeshCollider>();var lod=segment.AddComponent<LTRoadLOD>();
                var meshes=new Mesh[fitted.levels.Length];
                for(int l=0;l<meshes.Length;l++)
                {
                    var c=fitted.levels[l][0];var m=Own(new Mesh{indexFormat=UnityEngine.Rendering.IndexFormat.UInt32});
                    m.vertices=c.vertices;m.normals=c.normals;m.tangents=c.tangents;m.uv=c.uv;
                    if(c.uv2!=null)m.uv2=c.uv2;if(c.uv3!=null)m.uv3=c.uv3;if(c.uv4!=null)m.uv4=c.uv4;if(c.colors!=null)m.colors=c.colors;
                    m.subMeshCount=c.submeshes.Length;for(int s=0;s<c.submeshes.Length;s++)m.SetTriangles(c.submeshes[s],s);
                    m.RecalculateBounds();meshes[l]=m;
                    Require(MeshHash(m)==LTRoadMesh.ContentHash(c),"Native mesh lost channels/topology.");
                }
                filter.sharedMesh=collider.sharedMesh=meshes[0];lod.meshes=meshes;lod.owner=road;
                for(int l=meshes.Length-1;l>=0;l--){lod.Show(l);Require(filter.sharedMesh==meshes[l]&&collider.sharedMesh==meshes[0],"LOD altered collider or did not switch render mesh.");}
                var generated=Child("Owned grass mask output",road.transform);generated.AddComponent<LTRoadGenerated>().owner=road;
                road.generatedRoot=generated.transform;segment.transform.SetParent(generated.transform,false);
                generated.SetActive(true);segment.SetActive(true);road.gameObject.SetActive(true);road.enabled=true;
                road.clearVegetation=true;road.asphaltClearMesh=true;road.asphaltGrassPadding=.35f;
                var maskCache=new LTRoadVegetationMask();var masks=maskCache.Capture(new[]{road},world);
                Require(masks.Count==1,"Active asphalt mesh mask missing.");
                var meshMask=masks[0];var points=meshes[0].vertices;var ids=meshes[0].triangles;int probes=0;
                for(int i=0;i<ids.Length;i+=3)
                {
                    var a=points[ids[i]];var b=points[ids[i+1]];var c=points[ids[i+2]];
                    if(Math.Abs(Vector3.Cross(b-a,c-a).y)<.00001f)continue;
                    var p=(a+b+c)/3;Require(meshMask.footprint.Contains(p.x,p.z),"Grass root survives below a pavement triangle.");probes++;
                }
                Require(probes>100,"Insufficient pavement root probes.");
                lod.Show(3);Require(ReferenceEquals(meshMask,maskCache.Capture(new[]{road},world)[0]),"Render LOD rebuilt/changed vegetation mask.");
                segment.transform.localPosition=Vector3.right;
                Require(!ReferenceEquals(meshMask,maskCache.Capture(new[]{road},world)[0]),"Transform did not invalidate mask.");
                segment.SetActive(false);Require(maskCache.Capture(new[]{road},world).Count==0,"Obsolete/inactive generated section still clears grass.");
                segment.SetActive(true);road.clearVegetation=false;Require(maskCache.Capture(new[]{road},world).Count==0,"Opt-out ignored.");
                road.clearVegetation=true;road.mode=LTRoadMode.Offroad;Require(maskCache.Capture(new[]{road},world).Count==0,"Non-asphalt mode acquired mesh removal.");
                maskCache.Clear();road.enabled=false;
                foreach(var pair in sourceHashes)Require(pair.Value==MeshHash(pair.Key),"Imported source mesh changed.");
                success="PASS: actual imported Road_1_Line, prefab + mesh/pool paths, weighted seed/cache/disable, strict opt-out, fit/cache/limit, exact mixed LOD joins, native mesh channels, collider remains LOD0. Grass: "+probes+" pavement root probes, LOD-independent cache, transform/disable/opt-out. "+fitted.status+" No scene/assets saved; rendered grass/material seams and physics raycasts not tested.";
            }
            catch(Exception e){failure=e;}
            finally
            {
                LTRoadModuleSource.Forget(road);
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(preview);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    failure=new InvalidOperationException("User scene state changed: "+s.scene.name,failure);
                foreach(var f in files)if(!File.Exists(f.Key)||Hash(f.Key)!=f.Value)
                    failure=new InvalidOperationException("Asset bytes changed: "+f.Key,failure);
            }
            if(failure!=null)Debug.LogError(Tag+"FAIL: "+failure);else Debug.Log(Tag+success);
        }
        static void CheckSeams(LTRoadModuleSource.Prepared p)
        {
            Require(p.levels.Length==4&&p.levels.All(l=>l.Count==p.levels[0].Count)&&p.levels[0].Count>1,"Incorrect section/LOD count.");
            for(int a=0;a<4;a++)for(int b=0;b<4;b++)for(int c=1;c<p.levels[a].Count;c++)
            {
                var shared=new HashSet<Vector3>(p.levels[a][c-1].vertices);shared.IntersectWith(p.levels[b][c].vertices);
                Require(shared.Count==10,$"LOD{a}/LOD{b} section {c}: expected 10 bit-exact seam points, found {shared.Count}.");
            }
        }
        static string Hashes(LTRoadModuleSource.Prepared p)=>string.Join(";",p.levels.SelectMany(l=>l).Select(LTRoadMesh.ContentHash));
        static string MeshHash(Mesh m)=>LTRoadMesh.ContentHash(new LTRoadMesh.Chunk{vertices=m.vertices,normals=m.normals,tangents=m.tangents,uv=m.uv,
            uv2=Optional(m.uv2),uv3=Optional(m.uv3),uv4=Optional(m.uv4),colors=Optional(m.colors),triangles=m.triangles,
            submeshes=Enumerable.Range(0,m.subMeshCount).Select(m.GetTriangles).ToArray()});
        static T[] Optional<T>(T[] a)=>a.Length==0?null:a;
        static string Hash(string path){using var sha=SHA256.Create();using var file=File.OpenRead(path);return BitConverter.ToString(sha.ComputeHash(file));}
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
