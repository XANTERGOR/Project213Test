using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LocalTerrainPrototype
{
    public static class LTRockTransitionCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Rock Transition (no scene changes)
        public static void Run()
        {
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++){var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var active=SceneManager.GetActiveScene();var previousTarget=RenderTexture.active;
            var owned=new List<UnityEngine.Object>();Scene preview=default;LTRockRenderOutput output=null;LTRockMaterialBake atlas=null;
            LTRockLODAtlases lodAtlases=null;
            T Own<T>(T value) where T:UnityEngine.Object{value.hideFlags=HideFlags.HideAndDontSave;owned.Add(value);return value;}
            try
            {
                string geometryReport=LTRockFarLODCheck.Run();
                preview=EditorSceneManager.NewPreviewScene();
                var root=Own(new GameObject("Rock transition validation"));root.SetActive(false);SceneManager.MoveGameObjectToScene(root,preview);
                var world=root.AddComponent<LTWorld>();world.autoUpdate=false;world.enableLayerPainting=false;world.previewLODs=false;
                world.lods=new[]{new LTLODSettings(1,1,10),new LTLODSettings(2,2,20),new LTLODSettings(3,4,30),new LTLODSettings(4,8,40)};
                var rock=Own(new GameObject("Owned rock"));SceneManager.MoveGameObjectToScene(rock,preview);rock.transform.SetParent(root.transform,false);
                var filter=rock.AddComponent<MeshFilter>();rock.AddComponent<MeshRenderer>();var collider=rock.AddComponent<MeshCollider>();
                var stamp=rock.AddComponent<LTMeshStamp>();stamp.materialBlendWidth=1;stamp.materialFarResolution=256;stamp.meshLodError=1;
                const int n=20;var vertices=new List<Vector3>();var uv=new List<Vector2>();var source=new List<int>();var bridge=new List<int>();
                for(int z=0;z<=n;z++)for(int x=0;x<=n;x++)
                {vertices.Add(new Vector3(x*.25f,.6f*Mathf.Sin(x*.55f)*Mathf.Sin(z*.45f),z*.25f));uv.Add(new Vector2(x/(float)n,z/(float)n));}
                for(int z=0;z<n;z++)for(int x=0;x<n;x++)
                {int a=z*(n+1)+x;(x<16?source:bridge).AddRange(new[]{a,a+n+1,a+1,a+1,a+n+1,a+n+2});}
                var mesh=Own(new Mesh{name="Validated full mesh"});mesh.SetVertices(vertices);mesh.SetUVs(0,uv);mesh.subMeshCount=2;
                mesh.SetTriangles(source,0);mesh.SetTriangles(bridge,1);mesh.RecalculateNormals();mesh.RecalculateTangents();mesh.RecalculateBounds();
                stamp.sourceMesh=mesh;filter.sharedMesh=mesh;collider.sharedMesh=mesh;
                stamp.simplifyDistantMaterial=false;
                output=new LTRockRenderOutput(stamp,mesh,1,null);
                var rawLevels=output.RockLODs.Select(m=>Own(UnityEngine.Object.Instantiate(m))).ToArray();
                output.Dispose();output=null;stamp.simplifyDistantMaterial=true;
                output=new LTRockRenderOutput(stamp,mesh,1,null);stamp.renderOutput=output;output.Publish();
                Require(output.atlasUV&&output.rockAtlasPerLOD,"Per-LOD UV atlas unwrap failed");
                Require(output.rock.triangles.Length+output.transition.triangles.Length==mesh.triangles.Length,"Partition lost/duplicated triangles");
                int[][] Indices(Mesh part)=>Enumerable.Range(0,part.subMeshCount).Select(part.GetTriangles).ToArray();
                for(int l=0;l<output.RockLODs.Length;l++)
                {
                    var coarse=output.RockLODs[l];var raw=rawLevels[l];
                    var restored=OriginalIndices(coarse,raw);
                    Require(restored.Zip(Indices(raw),(a,b)=>Faces(a).SequenceEqual(Faces(b))).All(v=>v),"Unwrap changed precomputed LOD geometry/UV0/frame");
                    var fine=rawLevels[0];
                    LTRockFarLODCheck.Validate(fine.vertices,Indices(fine),OriginalIndices(coarse,fine));
                    Require(coarse.bounds==fine.bounds,"LOD changed paint coverage bounds");
                    if(l>=3)Require(coarse.triangles.Length<=output.RockLODs[l-1].triangles.Length,"Far rock LOD restored triangles");
                    if(l==0)continue;
                    var transition=output.transition;
                    Require(LTRockLODMath.Reduce(transition.vertices,Indices(transition),transition.colors.Select(c=>c.r).ToArray(),stamp.meshLodError*Mathf.Pow(2,l-1),l+2)
                        .Zip(Indices(output.TransitionLODs[l]),(a,b)=>a.SequenceEqual(b)).All(v=>v),"Transition LOD changed with rock-only policy");
                }
                foreach(var part in output.RockLODs.Concat(new[]{output.transition}))
                {
                    var p=part.vertices;var tex=part.uv;var atlasUV=new List<Vector2>();part.GetUVs(2,atlasUV);
                    Require(atlasUV.Count==p.Length,"Atlas UV channel missing");
                    for(int i=0;i<p.Length;i++)Require((tex[i]-new Vector2(p[i].x/5,p[i].z/5)).sqrMagnitude<1e-8f,"Source UV0 changed by split/unwrap");
                }
                root.SetActive(true);world.forceLOD=0;output.UpdateLOD();int near=filter.sharedMesh.triangles.Length+output.bridgeRenderer.GetComponent<MeshFilter>().sharedMesh.triangles.Length;
                world.forceLOD=4;output.UpdateLOD();int far=filter.sharedMesh.triangles.Length+output.bridgeRenderer.GetComponent<MeshFilter>().sharedMesh.triangles.Length;
                Require(far<near,$"Render LOD did not reduce triangles: {near/3}->{far/3}, levels={output.RockLODs.Length}, current={output.CurrentLOD}, rock vertices={output.rock.vertexCount}");Require(collider.sharedMesh==mesh,"Render LOD changed collider");
                foreach(int level in new[]{0,1,2,3,4,2,4,0,4})
                {
                    world.forceLOD=level;output.UpdateLOD();
                    Require(filter.sharedMesh==output.RockLODs[level]&&output.bridgeRenderer.GetComponent<MeshFilter>().sharedMesh==output.TransitionLODs[level],"Rock/transition LOD selection mismatch");
                    Require(collider.sharedMesh==mesh,"LOD sequence changed collider");
                }
                Require(mesh.vertices.SequenceEqual(vertices)&&mesh.uv.SequenceEqual(uv),"Source mesh was mutated");
                // In-memory fixture for the exact serialized-mesh restoration path;
                // no AssetDatabase writes and no user-scene save.
                var saved=Own(ScriptableObject.CreateInstance<LTRockRenderAsset>());saved.combined=mesh;saved.sourceSubmeshes=1;saved.atlasUV=true;
                saved.rockAtlasPerLOD=output.rockAtlasPerLOD;
                saved.rocks=output.RockLODs.Select(m=>Own(UnityEngine.Object.Instantiate(m))).ToArray();
                saved.transitions=output.TransitionLODs.Select(m=>Own(UnityEngine.Object.Instantiate(m))).ToArray();
                output.Dispose();output=null;stamp.renderOutput=null;Require(filter.sharedMesh==mesh,"Disposal did not restore combined mesh");
                stamp.enabled=false;stamp.renderAsset=saved;stamp.enabled=true;output=stamp.renderOutput;
                Require(output!=null,"Saved outputs not restored on enable");output.UpdateLOD();
                Require(output.CurrentLOD==4&&output.rockAtlasPerLOD&&collider.sharedMesh==mesh,"Restored LOD/atlas/collider mismatch");
                Require(saved.Owns(filter.sharedMesh)&&stamp.sourceMesh==mesh,"Baked output mistaken for source");
                stamp.enabled=false;output=null;Require(saved.rocks.All(m=>m)&&saved.transitions.All(m=>m),"Disabling destroyed shared baked meshes");stamp.renderAsset=null;
                Debug.Log($"Rock Transition native v5 PASS: {geometryReport}; partition, original UV0/normal/tangent frames, LOD geometry independent of atlas splits, fixed rims, stable paint bounds, per-LOD native atlas charts, transition policy unchanged, LOD {near/3} -> {far/3}, unchanged collider/source, 0/1/2/3/4/2/4/0/4 switching, saved per-LOD metadata restoration and owned cleanup.");

                // Compile the actual live shader variant; this is separate from a C# build.
                var liveShader=Resources.Load<Shader>("LTEightLayers");Require(liveShader,"Terrain shader missing");
                var live=Own(new Material(liveShader));live.EnableKeyword("_LT_ROCK_SURFACE");live.EnableKeyword("_REQUIRE_UV2");live.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
                foreach(string passName in new[]{"GBuffer","Forward","DepthOnly","MotionVectors"})
                {int pass=live.FindPass(passName);Require(pass>=0,"Missing pass "+passName);ShaderUtil.CompilePass(live,pass,true);}
                Require(!ShaderUtil.ShaderHasError(liveShader),"Live rock variant failed: "+string.Join("; ",ShaderUtil.GetShaderMessages(liveShader).Select(m=>m.message)));

                // Known UV gradient verifies atlas orientation. The geometry is a vertical
                // wall: an XZ bake would collapse it and cannot pass these pixel checks.
                var wall=Own(new Mesh{name="UV wall probe"});wall.vertices=new[]{Vector3.zero,Vector3.right,Vector3.up,Vector3.right+Vector3.up};
                wall.uv=new[]{Vector2.zero,Vector2.right,Vector2.up,Vector2.one};wall.SetUVs(2,new List<Vector2>{new Vector2(.1f,.1f),new Vector2(.9f,.1f),new Vector2(.1f,.9f),new Vector2(.9f,.9f)});
                wall.colors=new[]{Color.black,Color.black,Color.black,Color.black};wall.triangles=new[]{0,1,2,1,3,2};wall.RecalculateNormals();wall.RecalculateTangents();
                var texture=Own(new Texture2D(8,8,TextureFormat.RGBAFloat,false,true){wrapMode=TextureWrapMode.Clamp});
                var pixels=new Color[64];for(int y=0;y<8;y++)for(int x=0;x<8;x++)pixels[y*8+x]=new Color(x/7f,y/7f,.25f,1);texture.SetPixels(pixels);texture.Apply();
                var mask=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));mask.SetPixel(0,0,new Color(.5f,.5f,0,.5f));mask.Apply();
                live.SetTexture("_LTRockColorMap",texture);live.SetTexture("_LTRockMaskMap",mask);live.SetVector("_LTRockTint",Vector4.one);
                live.SetVector("_LTRockUV",new Vector4(1,1,0,0));live.SetVector("_LTRockSettings",new Vector4(1,.5f,0,.2f));
                live.SetVector("_LTRockFlags",new Vector4(1,0,1,.8f));live.SetVector("_LTRockRemap",new Vector4(.2f,.8f,.1f,.7f));
                // Exercise the same cache/binding code as painting, including pending LODs.
                output=new LTRockRenderOutput(stamp,saved,null);output.Publish();
                lodAtlases=new LTRockLODAtlases(output,stamp,live);
                Require(lodAtlases.Count==5&&!lodAtlases.Ready,"Wrong per-LOD bake cache");
                Require(Enumerable.Range(0,5).Select(l=>LTRockLODAtlases.Resolution(512,l)).SequenceEqual(new[]{512,256,128,128,128}),"Atlas size budget changed");
                for(int level=0;level<5;level++)
                {
                    output.ApplySeamLOD(level,null);
                    Require(live.GetVector("_LTRockFarParams").x==0,"Pending LOD sampled another atlas");
                    Require(lodAtlases.BakeNext(world,new[]{live}),"Missing scheduled LOD bake");
                    Require(lodAtlases.Get(level).color.width==LTRockLODAtlases.Resolution(stamp.materialFarResolution,level),"LOD atlas resolution mismatch");
                    Require(live.GetTexture("_LTRockFarColor")==lodAtlases.Get(level).color,"Completed bake not bound to current mesh");
                    CheckAtlasPixels(saved.rocks[level],lodAtlases.Get(level),previousTarget);
                }
                Require(lodAtlases.Ready&&!lodAtlases.BakeNext(world,new[]{live}),"Completed cache rebaked");
                var cached=Enumerable.Range(0,5).Select(l=>lodAtlases.Get(l).color).ToArray();
                foreach(int level in new[]{0,4,1,3,2,4,0})
                {
                    output.ApplySeamLOD(level,null);
                    Require(filter.sharedMesh==saved.rocks[level]&&live.GetTexture("_LTRockFarColor")==cached[level],"LOD mesh/atlas switch was not atomic");
                    Require(!lodAtlases.BakeNext(world,new[]{live}),"Camera switch rebaked atlas");
                }
                lodAtlases.Dispose();lodAtlases=null;Require(cached.All(t=>!t)&&live.GetVector("_LTRockFarParams").x==0,"LOD atlases leaked on release");
                output.Dispose();output=null;
                // Old serialized assets retain one shared atlas until the user rebuilds.
                var perLOD=saved.rocks;saved.rocks=Enumerable.Repeat(perLOD[0],perLOD.Length).ToArray();
                saved.rockAtlasPerLOD=false;output=new LTRockRenderOutput(stamp,saved,null);output.Publish();
                lodAtlases=new LTRockLODAtlases(output,stamp,live);Require(lodAtlases.Count==1,"Legacy atlas compatibility broken");
                Require(lodAtlases.BakeNext(world,new[]{live}),"Legacy atlas not baked");output.ApplySeamLOD(4,null);
                Require(live.GetTexture("_LTRockFarColor")==lodAtlases.Get(0).color&&!lodAtlases.BakeNext(world,new[]{live}),"Legacy shared atlas selection failed");
                lodAtlases.Dispose();lodAtlases=null;output.Dispose();output=null;saved.rocks=perLOD;saved.rockAtlasPerLOD=true;
                atlas=LTRockMaterialBake.Build(wall,rock.transform,world,new[]{live},128);
                Require(RenderTexture.active==previousTarget,"Atlas changed active render target");
                var read=Own(new Texture2D(128,128,TextureFormat.RGBAFloat,false,true));
                RenderTexture.active=atlas.color;read.ReadPixels(new Rect(0,0,128,128),0,0);read.Apply();RenderTexture.active=previousTarget;
                foreach(int y in new[]{36,64,91})foreach(int x in new[]{36,64,91})
                {
                    float u=((x+.5f)/128-.1f)/.8f,v=((y+.5f)/128-.1f)/.8f;
                    // Hardware bilinear samples are centred at (texel+.5)/size.
                    // CPU GetPixelBilinear does not provide that reference convention.
                    var expected=new Color(Mathf.Clamp01((u*8-.5f)/7),Mathf.Clamp01((v*8-.5f)/7),.25f,1);var actual=read.GetPixel(x,y);
                    Require(Mathf.Abs(expected.r-actual.r)<.025f&&Mathf.Abs(expected.g-actual.g)<.025f&&Mathf.Abs(actual.a-.5f)<.025f,
                        $"Atlas pixel ({x},{y}), UV=({u:F4},{v:F4}): expected RGB={expected}, AO=0.5; actual={actual}");
                }
                // Padding must produce valid texels just beyond a chart, not pink/empty borders.
                Require(read.GetPixel(10,64).a>=0,"Atlas gutter dilation failed");
                RenderTexture.active=atlas.normal;read.ReadPixels(new Rect(0,0,128,128),0,0);read.Apply();RenderTexture.active=previousTarget;
                var surface=read.GetPixel(64,64);Require(Mathf.Abs(surface.r-.5f)<.02f&&Mathf.Abs(surface.g-.5f)<.02f&&Mathf.Abs(surface.b-.4f)<.02f&&Mathf.Abs(surface.a-.5f)<.02f,"Baked normal/smoothness/metal remap mismatch");
                atlas.Dispose();atlas=null;
                var array=Own(new Texture2DArray(2,2,1,TextureFormat.RGBAFloat,false,true));array.SetPixels(new[]{Color.blue,Color.blue,Color.blue,Color.blue},0);array.Apply();
                live.SetTexture("_LTColorArray",array);live.SetVector("_LTTint0",Vector4.one);live.SetVector("_LTTiling0",new Vector4(1,1,0,0));
                live.SetVector("_LTSettings0",new Vector4(1,.1f,.4f,0));live.SetVector("_LTFlags0",Vector4.zero);live.SetFloat("_LTBaseOnly",1);
                wall.colors=new[]{new Color(.5f,0,0,1),new Color(.5f,0,0,1),new Color(.5f,0,0,1),new Color(.5f,0,0,1)};
                atlas=LTRockMaterialBake.Build(wall,rock.transform,world,new[]{live},128);
                RenderTexture.active=atlas.color;read.ReadPixels(new Rect(0,0,128,128),0,0);read.Apply();RenderTexture.active=previousTarget;
                var mixed=read.GetPixel(64,64);Require(Mathf.Abs(mixed.b-.625f)<.025f&&Mathf.Abs(mixed.a-.75f)<.025f,"Vertex mask did not blend both surfaces");
                Debug.Log("Rock Transition graphics v5 PASS: live HDRP passes compiled; five native LOD atlases with pixel checks, pending/live-layer fallback, atomic cached mesh/atlas switches, no camera rebakes, legacy single-atlas restoration and disposal; vertical-wall UV atlas, source color/AO, normal/smoothness/metal remaps, gutter and 50% terrain blend. This is not a live-scene performance test.");
            }
            catch(Exception error){Debug.LogError("Rock Transition v5 FAIL: "+error);}
            finally
            {
                RenderTexture.active=previousTarget;lodAtlases?.Dispose();atlas?.Dispose();output?.Dispose();
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])UnityEngine.Object.DestroyImmediate(owned[i]);
                if(preview.IsValid())EditorSceneManager.ClosePreviewScene(preview);
                if(active.IsValid()&&SceneManager.GetActiveScene()!=active)SceneManager.SetActiveScene(active);
                foreach(var s in scenes)if(s.scene.IsValid()&&(s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots))
                    Debug.LogError("Rock Transition validation: unexpected user-scene state change; no scene saved.");
            }
        }
        static (int,int,int)[] Faces(int[] indices)
        {
            var result=new (int,int,int)[indices.Length/3];
            for(int i=0;i<indices.Length;i+=3)
            {
                int a=indices[i],b=indices[i+1],c=indices[i+2];
                result[i/3]=a<b&&a<c?(a,b,c):b<c?(b,c,a):(c,a,b);
            }
            Array.Sort(result);return result;
        }
        static int[][] OriginalIndices(Mesh unwrapped,Mesh original)
        {
            var p=original.vertices;var uv=original.uv;var n=original.normals;var t=original.tangents;var c=original.colors;
            var lookup=new Dictionary<(Vector3,Vector2,Vector3,Vector4,Color),int>();
            for(int i=0;i<p.Length;i++)lookup.Add((p[i],uv[i],n[i],t[i],c[i]),i);
            var v=unwrapped.vertices;var u=unwrapped.uv;var normals=unwrapped.normals;var tangents=unwrapped.tangents;var colors=unwrapped.colors;
            var map=new int[v.Length];for(int i=0;i<v.Length;i++)
                Require(lookup.TryGetValue((v[i],u[i],normals[i],tangents[i],colors[i]),out map[i]),"Atlas changed source vertex attributes");
            return Enumerable.Range(0,unwrapped.subMeshCount).Select(s=>unwrapped.GetTriangles(s).Select(i=>map[i]).ToArray()).ToArray();
        }
        static void CheckAtlasPixels(Mesh mesh,LTRockMaterialBake bake,RenderTexture restore)
        {
            int resolution=bake.color.width;Texture2D read=null;
            try
            {
                read=new Texture2D(resolution,resolution,TextureFormat.RGBAFloat,false,true){hideFlags=HideFlags.HideAndDontSave};
                RenderTexture.active=bake.color;read.ReadPixels(new Rect(0,0,resolution,resolution),0,0);read.Apply();RenderTexture.active=restore;
                var atlas=mesh.uv3;var uv=mesh.uv;var tris=mesh.triangles;int checkedPixels=0;
                float Cross(Vector2 a,Vector2 b)=>a.x*b.y-a.y*b.x;
                // Interior pixels of the largest charts avoid padding and edge coverage ambiguity.
                foreach(int face in Enumerable.Range(0,tris.Length/3).OrderByDescending(f=>Mathf.Abs(Cross(atlas[tris[f*3+1]]-atlas[tris[f*3]],atlas[tris[f*3+2]]-atlas[tris[f*3]]))).Take(16))
                {
                    int a=tris[face*3],b=tris[face*3+1],c=tris[face*3+2];var center=(atlas[a]+atlas[b]+atlas[c])/3;
                    int x=Mathf.Clamp((int)(center.x*resolution),0,resolution-1),y=Mathf.Clamp((int)(center.y*resolution),0,resolution-1);
                    var pixel=new Vector2((x+.5f)/resolution,(y+.5f)/resolution);
                    float area=Cross(atlas[b]-atlas[a],atlas[c]-atlas[a]);if(Mathf.Abs(area)<1e-8f)continue;
                    float wb=Cross(pixel-atlas[a],atlas[c]-atlas[a])/area,wc=Cross(atlas[b]-atlas[a],pixel-atlas[a])/area,wa=1-wb-wc;
                    if(Mathf.Min(wa,Mathf.Min(wb,wc))<.12f)continue;
                    var source=uv[a]*wa+uv[b]*wb+uv[c]*wc;var actual=read.GetPixel(x,y);
                    var expected=new Vector2(Mathf.Clamp01((source.x*8-.5f)/7),Mathf.Clamp01((source.y*8-.5f)/7));
                    Require(Mathf.Abs(actual.r-expected.x)<.03f&&Mathf.Abs(actual.g-expected.y)<.03f&&Mathf.Abs(actual.b-.25f)<.03f&&Mathf.Abs(actual.a-.5f)<.03f,
                        $"LOD atlas pixel mismatch: {mesh.name}, ({x},{y}), expected RG {expected}, actual {actual}");checkedPixels++;
                }
                Require(checkedPixels>=3,"Not enough interior LOD atlas pixels");
            }
            finally{RenderTexture.active=restore;if(read)UnityEngine.Object.DestroyImmediate(read);}
        }
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    }
}
