using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    internal static class LTRockSeamRuntimeCheck
    {
        internal static string Run()
        {
            var before=new List<(Scene scene,bool dirty,int roots)>();for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);before.Add((s,s.isDirty,s.rootCount));}
            var active=SceneManager.GetActiveScene();var preview=EditorSceneManager.NewPreviewScene();var owned=new List<UnityEngine.Object>();GameObject root=null;
            T Own<T>(T item) where T:UnityEngine.Object{item.hideFlags=HideFlags.HideAndDontSave;owned.Add(item);return item;}
            try
            {
                root=Own(new GameObject("Joint seam check"));root.SetActive(false);SceneManager.MoveGameObjectToScene(root,preview);
                var world=root.AddComponent<LTWorld>();world.autoUpdate=false;world.enableLayerPainting=false;world.previewLODs=false;world.chunksX=world.chunksZ=1;
                world.terrainLODMode=LTTerrainLODMode.WholeChunk;world.enableLODs=true;world.source=Own(ScriptableObject.CreateInstance<LTSource>());world.source.size=new Vector3(16,10,16);
                world.lods=Enumerable.Range(1,4).Select(l=>new LTLODSettings(l,l,10*l)).ToArray();
                Mesh Ring(float[] radius,bool rock)
                {
                    const int n=32;var vertices=new List<Vector3>();var uv=new List<Vector2>();var inside=new List<int>();var outside=new List<int>();
                    foreach(float r in radius)for(int j=0;j<n;j++){float a=j*Mathf.PI*2/n;vertices.Add(new Vector3(r*Mathf.Cos(a),0,r*Mathf.Sin(a)));uv.Add(new Vector2(j/(float)n,r/6));}
                    for(int row=0;row<radius.Length-1;row++)for(int j=0;j<n;j++)
                    {int a=row*n+j,b=row*n+(j+1)%n,c=a+n,d=b+n;(rock&&row==0?inside:outside).AddRange(new[]{a,b,c,b,d,c});}
                    var mesh=Own(new Mesh());mesh.SetVertices(vertices);mesh.SetUVs(0,uv);mesh.subMeshCount=rock?2:1;
                    if(rock){mesh.SetTriangles(inside,0);mesh.SetTriangles(outside,1);}else mesh.SetTriangles(outside,0);mesh.RecalculateNormals();mesh.RecalculateTangents();mesh.RecalculateBounds();return mesh;
                }
                GameObject Child(string name){var go=Own(new GameObject(name));SceneManager.MoveGameObjectToScene(go,preview);go.transform.SetParent(root.transform,false);return go;}
                var generated=Child("Generated");world.generatedRoot=generated.transform;
                var ground=Child("Chunk");ground.transform.SetParent(generated.transform,false);var gf=ground.AddComponent<MeshFilter>();ground.AddComponent<MeshRenderer>();var gc=ground.AddComponent<MeshCollider>();var chunk=ground.AddComponent<LTChunk>();
                chunk.mesh=Ring(new[]{3f,3.5f,4f,4.5f,5f,6f},false);gf.sharedMesh=gc.sharedMesh=chunk.mesh;
                chunk.lodMeshes=Enumerable.Range(0,4).Select(_=>Own(UnityEngine.Object.Instantiate(chunk.mesh))).ToArray();
                var rock=Child("Rock");rock.AddComponent<MeshFilter>();rock.AddComponent<MeshRenderer>();var collider=rock.AddComponent<MeshCollider>();var stamp=rock.AddComponent<LTMeshStamp>();
                stamp.simplifyDistantMaterial=false;stamp.materialBlendWidth=0;stamp.meshLodError=.25f;stamp.sourceMesh=Ring(new[]{1f,2f,3f},true);collider.sharedMesh=stamp.sourceMesh;
                var output=new LTRockRenderOutput(stamp,stamp.sourceMesh,1,null);stamp.renderOutput=output;output.Publish();
                var saved=Own(ScriptableObject.CreateInstance<LTRockRenderAsset>());saved.combined=stamp.sourceMesh;saved.sourceSubmeshes=1;
                saved.rocks=output.RockLODs;saved.transitions=output.TransitionLODs;stamp.renderAsset=saved;
                var meshes=new[]{chunk.mesh}.Concat(chunk.lodMeshes).Concat(saved.transitions).ToArray();
                var parts=meshes.Select((mesh,p)=>new LTRockSeamMath.Part{positions=mesh.vertices,triangles=Enumerable.Range(0,mesh.subMeshCount).Select(mesh.GetTriangles).ToArray(),
                    terrain=p<5,onlyLevel=p<5?-1:p-5,movable=mesh.vertices.Select(v=>p<5?v.magnitude<5.9f:Mathf.Abs(v.magnitude-3)<.001f).ToArray()}).ToArray();
                var plan=LTRockSeamMath.Build(parts,new[]{0f,.5f,1f,2f,4f},progressive:true);
                var bindings=meshes.Select((mesh,p)=>new LTRockSeamAsset.Binding{mesh=mesh,levels=plan.maps.Select(m=>new LTRockSeamAsset.Map{indices=m[p]}).ToArray()}).ToArray();
                int bandCount=0;
                for(int p=0;p<5;p++)for(int level=1;level<5;level++)
                {
                    var band=LTRockSeamBandMath.Build(parts[p].positions,parts[p].triangles[0],parts[p].movable,plan.maps[level][p],.1f);
                    if(band!=null){bindings[p].levels[level].bands=new[]{band};bandCount++;}
                }
                Require(bandCount>0,"native fixture did not exercise terrain band replacements");
                for(int p=6;p<bindings.Length;p++)
                {
                    int level=p-5;var baked=Own(UnityEngine.Object.Instantiate(meshes[p]));
                    for(int sub=0;sub<baked.subMeshCount;sub++){var indices=new List<int>(meshes[p].GetTriangles(sub));LTRockSeamMath.Remap(indices,plan.maps[level][p]);baked.SetTriangles(indices,sub,false);}
                    Require(baked.uv.SequenceEqual(meshes[p].uv),"transition UV stream changed");bindings[p].simplifiedTransition=baked;
                }
                var seam=Own(ScriptableObject.CreateInstance<LTRockSeamAsset>());seam.groups=new[]{new LTRockSeamAsset.Group{bounds=new Bounds(Vector3.zero,Vector3.one*16),rocks=new[]{saved},matrices=new[]{Matrix4x4.identity},
                    terrain=new[]{new LTRockSeamAsset.Terrain{x=0,z=0,lod0=chunk.mesh,legacy=chunk.lodMeshes}},bindings=bindings,collapses=plan.collapses}};world.rockSeams=seam;
                root.SetActive(true);int near=chunk.mesh.triangles.Length,far=near;
                HashSet<(Vector3Int,Vector3Int)> Rim(Mesh mesh)
                {
                    var vertices=mesh.vertices;var indices=mesh.triangles;var edges=new HashSet<(Vector3Int,Vector3Int)>();
                    for(int i=0;i<indices.Length;i+=3)for(int k=0;k<3;k++)
                    {var a=LTRockSeamMath.Key(vertices[indices[i+k]]);var b=LTRockSeamMath.Key(vertices[indices[i+(k+1)%3]]);if(!edges.Remove((b,a)))Require(edges.Add((a,b)),"nonmanifold native mesh");}
                    edges.RemoveWhere(e=>Math.Abs(Math.Sqrt((double)e.Item1.x*e.Item1.x+(double)e.Item1.z*e.Item1.z)-30000)>2||Math.Abs(Math.Sqrt((double)e.Item2.x*e.Item2.x+(double)e.Item2.z*e.Item2.z)-30000)>2);return edges;
                }
                foreach(int level in new[]{0,4,2,4,0})
                {
                    world.forceLOD=level;world.RefreshLODCache();world.UpdateLOD();
                    var bridge=output.bridgeRenderer.GetComponent<MeshFilter>().sharedMesh;
                    Require(Rim(gf.sharedMesh).SetEquals(Rim(bridge).Select(e=>(e.Item2,e.Item1))),"runtime terrain/bridge disagreement");
                    Require(gc.sharedMesh==chunk.mesh&&collider.sharedMesh==stamp.sourceMesh,"runtime changed collider");
                    Require(gf.sharedMesh.uv.SequenceEqual(chunk.mesh.uv),"terrain band changed UV stream");
                    if(level==4)far=gf.sharedMesh.triangles.Length;
                    if(level==0)Require(gf.sharedMesh==chunk.mesh,"LOD0 was not restored");
                }
                Require(far<near,"native joint terrain did not reduce");
                world.rockSeamBakePending=true;world.forceLOD=4;world.RefreshLODCache();world.UpdateLOD();
                Require(gf.sharedMesh==chunk.lodMeshes[3]&&output.bridgeRenderer.GetComponent<MeshFilter>().sharedMesh==saved.transitions[4],"pending bake did not restore both rims");
                world.rockSeamBakePending=false;world.RefreshLODCache();world.UpdateLOD();Require(gf.sharedMesh.triangles.Length==far,"valid seam did not restore");
                // Exercise the actual WithinChunk publication path as well as
                // whole-chunk meshes. This one persistent cell owns the annulus;
                // mixed neighbour topology is covered by LTRockNativeLODCheck.
                var spatial=Own(ScriptableObject.CreateInstance<LTSpatialLODAsset>());
                spatial.vertexBank=chunk.mesh;spatial.formatVersion=LTSpatialLODMath.Version;spatial.divisions=1;spatial.revision=1;
                spatial.cells=new[]{new LTSpatialLODCell{x=0,z=0,size=LTSpatialLODMath.N,patch=0,child0=-1,child1=-1,child2=-1,child3=-1,
                    variants=new[]{new LTSpatialLODVariant{indices=chunk.mesh.triangles}}}};
                spatial.patches=new[]{new LTSpatialLODPatch{root=0,bounds=chunk.mesh.bounds,
                    levels=Enumerable.Range(0,5).Select(_=>new LTSpatialLODLevel{cells=new[]{0}}).ToArray()}};
                chunk.spatialLOD=spatial;world.terrainLODMode=LTTerrainLODMode.WithinChunk;world.lodPatchDivisions=1;
                seam.groups[0].terrain[0].bank=chunk.mesh;seam.groups[0].terrain[0].spatialRevision=spatial.revision;
                for(int level=1;level<5;level++)
                    bindings[0].levels[level].bands=LTRockSeamBandMath.BuildSpatial(parts[0].positions,parts[0].triangles[0],spatial.cells,spatial.patches,
                        parts[0].movable,plan.maps[level][0],.1f);
                world.RefreshLODCache();
                foreach(int level in new[]{0,4,2,4,0})
                {
                    world.forceLOD=level;world.RefreshLODCache();world.UpdateLOD();
                    Require(string.IsNullOrEmpty(world.spatialLODStatus),"spatial publication failed: "+world.spatialLODStatus);
                    var bridge=output.bridgeRenderer.GetComponent<MeshFilter>().sharedMesh;
                    Require(Rim(gf.sharedMesh).SetEquals(Rim(bridge).Select(e=>(e.Item2,e.Item1))),"spatial terrain/bridge disagreement");
                    Require(gc.sharedMesh==chunk.mesh&&collider.sharedMesh==stamp.sourceMesh,"spatial changed collider");
                    Require(gf.sharedMesh.uv.SequenceEqual(chunk.mesh.uv),"spatial changed UV stream");
                    if(level==4)Require(gf.sharedMesh.triangles.Length<near,"spatial seam stayed dense");
                    if(level==0)Require(gf.sharedMesh.triangles.SequenceEqual(chunk.mesh.triangles),"spatial LOD0 not restored");
                }
                // Stale provenance cannot publish a guaranteed footprint on a
                // different bank revision. BOTH attachments return to their base rim.
                spatial.revision++;world.forceLOD=4;world.RefreshLODCache();world.UpdateLOD();
                Require(gf.sharedMesh.triangles.Length==near&&output.bridgeRenderer.GetComponent<MeshFilter>().sharedMesh==saved.transitions[4],"stale spatial revision used native footprint");
                world.enabled=false;Require(gf.sharedMesh==chunk.mesh,"disable did not restore ground");
                foreach(var s in before)Require(s.scene.isDirty==s.dirty&&s.scene.rootCount==s.roots,"user scene changed");
                Require(SceneManager.GetActiveScene()==active,"active scene changed");
                return $"native whole-chunk and WithinChunk switching 0/4/2/4/0, whole terrain {near/3}->{far/3}, {bandCount} band plans, matched rims, UV/colliders unchanged, pending/stale bake fallback, disable cleanup";
            }
            finally
            {
                if(root)UnityEngine.Object.DestroyImmediate(root);
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])UnityEngine.Object.DestroyImmediate(owned[i]);
                if(preview.IsValid())EditorSceneManager.ClosePreviewScene(preview);
            }
        }
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException("Joint seam runtime: "+message);}
    }
}
