using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTRoadTerrainAsphaltCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Terrain Asphalt (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var scenes=new List<(Scene scene,bool dirty,int roots)>();
            for(int i=0;i<SceneManager.sceneCount;i++)
            {var s=SceneManager.GetSceneAt(i);scenes.Add((s,s.isDirty,s.rootCount));}
            var scene=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite,wire=GL.wireframe,async=ShaderUtil.allowAsyncCompilation;
            using var atlas=new LTRoadCrackTextures();
            T Own<T>(T value)where T:Object{value.hideFlags=HideFlags.HideAndDontSave;owned.Add(value);return value;}
            GameObject Child(string name,Transform parent=null)
            {
                var go=Own(new GameObject(name));go.SetActive(false);SceneManager.MoveGameObjectToScene(go,scene);
                if(parent)go.transform.SetParent(parent,false);return go;
            }
            try
            {
                GL.sRGBWrite=false;GL.wireframe=false;ShaderUtil.allowAsyncCompilation=false;
                var root=Child("Terrain asphalt validation");var world=root.AddComponent<LTWorld>();
                world.enabled=false;world.autoUpdate=false;world.enableLayerPainting=false;world.previewLODs=false;
                world.baseLayer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());
                var layer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());
                var road=Child("Owned asphalt road",root.transform).AddComponent<LTRoad>();
                road.mode=LTRoadMode.TerrainAsphalt;road.groundLayer=layer;road.width=8;road.edgeNoise=0;
                road.points=new List<LTRoadPoint>{new LTRoadPoint(new Vector3(8,1,0)),new LTRoadPoint(new Vector3(8,1,32))};
                road.asphaltDamage.potholes=1;road.asphaltDamage.depth=.4f;road.asphaltDamage.size=3;
                road.terrainCellSize=.25f;road.wheelLayer=world.baseLayer;road.wheelTracks.enabled=true;
                var capture=road.Capture(world);var palette=new List<LTSurfaceLayer>{world.baseLayer};
                road.GetComponent<LTPaintStamp>().AppendLayers(palette);
                Require(palette.Count==2&&!road.ActiveWheelLayer,"Experimental road used an extra wheel layer.");
                Require(road.CapturePaintProjection(world,false)?.projection==LTRoadProjection.Spline&&
                    road.CapturePaintProjection(world,true)==null,"Spline/one-layer routing failed.");
                Require(!LTLODMesh.RequiresFixedRoadSurface(capture.mode),"Terrain road pins LOD0.");
                root.SetActive(true);road.gameObject.SetActive(true);

                // Native terrain mesh + collider, generated from the same height function.
                var rect=new Rect(0,0,16,32);
                var zones=new List<LTStampMesh.Zone>{new LTStampMesh.Zone{bounds=capture.bounds,cellSize=.25f,
                    customCellSize=(x,z)=>.25f,coverageIntersects=capture.Intersects}};
                float H(float x,float z)=>capture.ApplyHeight(x,z,0);
                var progress=new LTLODMesh.BuildProgress(false);
                var plan=LTStampMesh.Plan(rect,8,true,.01f,100000,zones,H,progress);
                var forest=new LTBalancedForest(1,1,100000,new Dictionary<int,List<Vector3Int>>{{0,plan}});forest.Balance();
                LTStampMesh.Emit(rect,new Vector2(16,32),100000,forest,0,forest.Plan(0),H,null,
                    out var positions,out var normals,out var uv,out var indices,null,progress);
                var mesh=Own(new Mesh());mesh.indexFormat=UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices=positions;mesh.normals=normals;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateBounds();
                var colliderObject=Child("Owned collider",root.transform);var collider=colliderObject.AddComponent<MeshCollider>();
                collider.sharedMesh=mesh;colliderObject.SetActive(true);
                int probes=0;
                foreach(var p in positions)
                {
                    if(p.x<5||p.x>11||p.z<3||p.z>29||p.y>.9f)continue;
                    Require(collider.Raycast(new Ray(p+new Vector3(.001f,5,.001f),Vector3.down),out var hit,10)&&
                        Mathf.Abs(hit.point.y-p.y)<.015f,"Pothole collider is missing or does not follow the depression.");
                    if(++probes==20)break;
                }
                Require(probes>=5,"Fixture did not generate enough real pothole vertices.");
                Debug.Log($"[Terrain Asphalt v1] Native geometry PASS: one layer, forced spline UV, no fixed LOD fence, {indices.Length/3} terrain triangles and {probes} native collider depression probes. Owned preview scene only.");

                int passes=0;
                foreach(string resource in new[]{"LTEightLayers","LTEightLayersTessellation","LTGlobalLayerBake","LTRockSurfaceBake","LTRoadCrackPack"})
                {
                    var shader=Resources.Load<Shader>(resource);Require(shader&&shader.isSupported,"Unavailable shader: "+resource);
                    var material=Own(new Material(shader));
                    for(int p=0;p<material.passCount;p++){ShaderUtil.CompilePass(material,p,true);passes++;}
                    Require(!ShaderUtil.ShaderHasError(shader),resource+": "+string.Join("; ",ShaderUtil.GetShaderMessages(shader).Select(m=>m.message)));
                }
                Require(atlas.Ensure(world)&&atlas.Texture&&atlas.Slice(null)==0,"Built-in crack atlas missing.");
                int revision=atlas.Revision;Require(!atlas.Ensure(world)&&atlas.Revision==revision,"Unchanged crack atlas repacked.");
                var source=Own(new Texture2D(2,2,TextureFormat.RGBA32,false,true));source.SetPixels(Enumerable.Repeat(Color.white,4).ToArray());source.Apply();
                road.asphaltCrackMask=source;Require(atlas.Ensure(world)&&atlas.Slice(source)==0,"Custom crack atlas not rebuilt.");
                var influence=Own(new Texture2DArray(1,1,1,TextureFormat.RGBAFloat,false,true));influence.SetPixels(new[]{new Color(1,0,0,1)},0);influence.Apply();
                var color=Own(new Texture2DArray(1,1,1,TextureFormat.RGBAFloat,false,true));color.SetPixels(new[]{Color.white},0);color.Apply();
                var weights=new Texture2D[3];
                for(int i=0;i<3;i++){weights[i]=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));weights[i].SetPixel(0,0,Color.clear);weights[i].Apply();}
                var bake=Own(new Material(Resources.Load<Shader>("LTGlobalLayerBake")));
                bake.SetTexture("_LTColorArray",color);bake.SetTexture("_LTAsphaltDamage",influence);
                bake.SetFloat("_LTBaseOnly",0);bake.SetFloat("_LTHeightBlend",0);
                var tint=new Color(.6f,.4f,.2f,1);
                for(int i=0;i<12;i++)
                {bake.SetVector("_LTTiling"+i,Vector4.one);bake.SetVector("_LTTint"+i,tint);bake.SetVector("_LTSettings"+i,Vector4.zero);bake.SetVector("_LTFlags"+i,Vector4.zero);}
                for(int i=0;i<3;i++){bake.SetTexture("_LTWeights"+i,weights[i]);bake.SetVector("_LTLayerSlices"+i,Vector4.zero);}
                var target=Own(new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));Require(target.Create(),"GPU probe target allocation failed.");
                var read=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));int pixels=0;
                Color Pixel()
                {
                    bake.SetTexture("_LTAsphaltCracks",atlas.Texture);RenderTexture.active=target;GL.Clear(false,true,Color.magenta);
                    Require(bake.SetPass(0),"Color bake pass unavailable.");Graphics.DrawProceduralNow(MeshTopology.Triangles,6);
                    read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();pixels++;return read.GetPixel(0,0);
                }
                void Near(Color value,float multiplier)
                {for(int c=0;c<3;c++)Require(Mathf.Abs(value[c]-tint[c]*multiplier)<.008f,$"Crack GPU pixel {value}; expected RGB {tint} × {multiplier}.");}
                foreach(int slot in new[]{1,8,11})
                {
                    for(int i=0;i<3;i++){var w=Color.clear;if(i==slot/4)w[slot%4]=1;weights[i].SetPixel(0,0,w);weights[i].Apply();}
                    Near(Pixel(),1);
                    bake.SetVector("_LTAsphaltStyle"+slot,new Vector4(1,0,3,0));Near(Pixel(),.3f);
                    influence.SetPixels(new[]{Color.clear},0);influence.Apply();Near(Pixel(),1);
                    influence.SetPixels(new[]{new Color(0,1,0,1)},0);influence.Apply();Near(Pixel(),.68f);
                    influence.SetPixels(new[]{new Color(1,1,0,1)},0);influence.Apply();Near(Pixel(),.68f*(1-.7f*.15f));
                    influence.SetPixels(new[]{new Color(1,0,0,1)},0);influence.Apply();
                    bake.SetVector("_LTAsphaltStyle"+slot,Vector4.zero);Near(Pixel(),1);
                }
                source.SetPixels(Enumerable.Repeat(Color.black,4).ToArray());source.Apply();
                Require(atlas.Ensure(world),"Changed mask pixels did not repack.");
                bake.SetVector("_LTAsphaltStyle11",new Vector4(1,0,3,0));Near(Pixel(),1);
                road.asphaltDamage.cracks=0;Require(atlas.Ensure(world)&&!atlas.Texture,"Disabled cracks leaked atlas.");
                road.mode=LTRoadMode.GeometricAsphalt;road.connectedDamage=true;road.repairPatchStrength=.75f;
                Require(atlas.Ensure(world)&&atlas.Texture,"Patch-only road needs the influence binding even with cracks disabled.");
                road.repairPatchStrength=0;Require(atlas.Ensure(world)&&!atlas.Texture,"Disabled patches leaked atlas.");
                Debug.Log($"[Terrain Asphalt v2 / Patches] GPU PASS: {passes} compiled raster passes; built-in/custom array creation, cache reuse, source update/disable, patch-only lifetime, {pixels} actual shared-baker pixels at slots 1/8/11 including repairs, cracks and neutral controls. Live HDRP appearance, native Burst jobs and scene performance NOT verified.");
            }
            catch(Exception e){Debug.LogError("[Terrain Asphalt v2 / Patches] FAIL: "+e);}
            finally
            {
                RenderTexture.active=previous;GL.sRGBWrite=srgb;GL.wireframe=wire;ShaderUtil.allowAsyncCompilation=async;
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                EditorSceneManager.ClosePreviewScene(scene);
                foreach(var s in scenes)if(!s.scene.IsValid()||s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)
                    Debug.LogError("[Terrain Asphalt v1] User scene state changed during validation: "+s.scene.name);
            }
        }
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
