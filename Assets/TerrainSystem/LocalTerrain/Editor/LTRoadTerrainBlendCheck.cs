using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTRoadTerrainBlendCheck
    {
        static bool running;
        const string Tag="[Automatic Road Ground v5] ";
        // Archived validation menu: Tools/Local Terrain/Validate Automatic Road Ground Blend (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            if(running){Debug.LogWarning(Tag+"Check is still awaiting native initialization / GPU diagnostics.");return;}
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var paths=Directory.GetFiles("Assets/TerrainSystem/LocalTerrain","*",SearchOption.AllDirectories)
                .Concat(AssetDatabase.GetDependencies("Assets/Data/Models/Environment/Roads/Road_1_Line.prefab",true))
                .Concat(scenes.Select(s=>s.scene.path)).Concat(Directory.GetFiles("ProjectSettings"))
                .Concat(new[]{"Packages/manifest.json","Packages/packages-lock.json"});
            var files=paths.Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var preview=EditorSceneManager.NewPreviewScene();var owned=new List<Object>();
            var batcherMaterials=new List<Material>();
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite,wire=GL.wireframe,async=ShaderUtil.allowAsyncCompilation;
            Exception failure=null;int samples=0;string report="";
            var gpuErrors=new ConcurrentQueue<string>();
            void Capture(string message,string stack,LogType type)
            {
                if(message.Contains("requires a buffer")||message.Contains("Skipping draw calls")||
                    message.Contains("not SRP Batcher compatible")||
                    ((type==LogType.Error||type==LogType.Exception)&&message.Contains("_LTRoadTerrain")))gpuErrors.Enqueue(message);
            }
            running=true;Application.logMessageReceivedThreaded+=Capture;
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            GameObject Child(string name,Transform parent=null)
            {var g=Own(new GameObject(name));g.SetActive(false);SceneManager.MoveGameObjectToScene(g,preview);if(parent)g.transform.SetParent(parent,false);return g;}
            Texture2D Solid(Color c,int size=2)
            {var t=Own(new Texture2D(size,size,TextureFormat.RGBA32,false,true));t.SetPixels(Enumerable.Repeat(c,size*size).ToArray());t.Apply();return t;}
            void Fail(Exception e){failure=failure==null?e:new AggregateException(failure,e);}
            void VerifyUnchanged()
            {
                try
                {
                    foreach(var s in scenes)Require(s.scene.IsValid()&&s.scene.isLoaded&&s.scene.isDirty==s.dirty&&s.scene.rootCount==s.roots,"User scene state changed during validation");
                    foreach(var f in files)Require(File.Exists(f.Key)&&Hash(f.Key)==f.Value,"File changed during validation: "+f.Key);
                }
                catch(Exception e){Fail(e);}
            }
            try
            {
                ShaderUtil.allowAsyncCompilation=false;GL.sRGBWrite=false;GL.wireframe=false;
                using var cache=new LTRoadTerrainBlendCache();
                using var commands=new CommandBuffer{name="Owned automatic road ground probe"};
                var rt=Own(new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));Require(rt.Create(),"GPU target allocation");
                var read=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                var arrays=new Texture2DArray[3];
                for(int family=0;family<3;family++)
                {
                    arrays[family]=Own(new Texture2DArray(4,4,12,TextureFormat.RGBA32,false,true){wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Bilinear});
                    for(int layer=0;layer<12;layer++)
                    {
                        var pixels=new Color[16];
                        for(int i=0;i<16;i++)pixels[i]=family==0?new Color((layer+1)/13f,(i+1)/17f,(12-layer)/13f,1):
                            family==1?new Color(.5f+layer*.01f,.5f-i*.005f,.98f,1):new Color(.3f+layer*.04f,.08f+layer*.07f,.15f+layer*.05f,1);
                        arrays[family].SetPixels(pixels,layer);
                    }
                    arrays[family].Apply();
                }
                var terrainShader=Resources.Load<Shader>("LTEightLayers");var referenceShader=Resources.Load<Shader>("LTGlobalLayerBake");
                var probeShader=UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/TerrainSystem/LocalTerrain/Editor/LTRoadGroundProbe.shader");Require(terrainShader&&referenceShader&&probeShader,"Shaders missing");
                var terrain=new[]{Own(new Material(terrainShader)),Own(new Material(terrainShader))};
                for(int tile=0;tile<2;tile++)
                {
                    var m=terrain[tile];m.SetVector("_LTRect",new Vector4((tile-1)*8,0,8,8));m.SetMatrix("_LTSurfaceWorldToLocal",Matrix4x4.identity);
                    m.SetFloat("_LTBaseOnly",0);m.SetFloat("_LTRoadProjectionEnabled",0);
                    m.SetTexture("_LTColorArray",arrays[0]);m.SetTexture("_LTNormalArray",arrays[1]);m.SetTexture("_LTMaskArray",arrays[2]);
                    for(int k=0;k<3;k++)
                    {
                        m.SetTexture("_LTWeights"+k,Solid(new Color(1/12f,1/12f,1/12f,1/12f),257));
                        m.SetVector("_LTRoadProjectionSlots"+k,Vector4.zero);
                        var slices=new Vector4();for(int j=0;j<4;j++)slices[j]=(k*4+j+tile*5)%12;
                        m.SetVector("_LTLayerSlices"+k,slices);
                    }
                    for(int s=0;s<12;s++)
                    {
                        int id=(s+tile*5)%12;
                        m.SetVector("_LTTiling"+s,new Vector4(2+id*.1f,3+id*.2f,.03f*id,.01f*id));
                        m.SetVector("_LTTint"+s,new Vector4(1,.8f,.9f,1));m.SetVector("_LTSettings"+s,new Vector4(.8f,id*.02f,.3f,1));
                        m.SetVector("_LTFlags"+s,new Vector4(1,.9f,.8f,id*.005f));m.SetVector("_LTAsphaltStyle"+s,Vector4.zero);
                    }
                }
                var reference=Own(new Material(referenceShader));int referencePass=reference.FindPass("TerrainSurfaceProbe");Require(referencePass>=0,"Reference probe missing");
                var probe=Own(new Material(probeShader));probe.SetFloat("_LTRoadBlendSource",1);
                probe.EnableKeyword("_LT_ROAD_TERRAIN");
                probe.SetTexture("_LTRoadBlendDistances",Solid(Color.clear));probe.SetVector("_LTRoadBlendRect",new Vector4(-10,0,20,10));
                probe.SetVector("_LTRoadBlendParams",new Vector4(1,1,1,0));probe.SetVector("_LTRoadBlendNoise",new Vector4(1,0,0,0));
                probe.SetTexture("_LTRockColorMap",Solid(new Color(.8f,.1f,.2f,1)));probe.SetVector("_LTRockTint",Vector4.one);probe.SetVector("_LTRockFlags",new Vector4(1,0,0,0));
                probe.SetMatrix("_LTSurfaceWorldToLocal",Matrix4x4.identity);probe.SetMatrix("_LTRoadBlendWorldToMask",Matrix4x4.identity);
                var block=new MaterialPropertyBlock();long revision=0;int changes=0;cache.Changed+=()=>changes++;
                List<LTRoadTerrainBlendCache.Tile> Tiles(bool left=true)=>Enumerable.Range(left?0:1,left?2:1)
                    .Select(i=>new LTRoadTerrainBlendCache.Tile(new Vector2Int(i-1,0),terrain[i],revision)).ToList();
                void Sync(){cache.Synchronize(new Vector2(8,8),Tiles());Require(cache.Ready,cache.Status);}
                Color Pixel(Material m,int pass,int vertices,MaterialPropertyBlock b=null)
                {
                    commands.Clear();commands.SetRenderTarget(rt);commands.ClearRenderTarget(false,true,Color.magenta);
                    commands.DrawProcedural(Matrix4x4.identity,m,pass,MeshTopology.Triangles,vertices,1,b);Graphics.ExecuteCommandBuffer(commands);
                    RenderTexture.active=rt;read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();samples++;return read.GetPixel(0,0);
                }
                Color Auto(Vector2 p,int mode,Vector3 normal)
                {
                    block.Clear();cache.Bind(block);block.SetMatrix("_LTSurfaceWorldToLocal",Matrix4x4.identity);block.SetMatrix("_LTRoadBlendWorldToMask",Matrix4x4.identity);
                    Require(block.HasBuffer("_LTRoadTerrainChunks")&&block.HasBuffer("_LTRoadTerrainLayers"),"Missing SRVs, including Ready=0 fallback");
                    probe.SetVector("_LTProbePosition",new Vector4(p.x,0,p.y,mode));probe.SetVector("_LTProbeNormal",normal);
                    var regular=Pixel(probe,0,3,block);
                    probe.EnableKeyword("_LT_GRAPH_PROBE");probe.SetVector("_LTRockTint",new Vector4(.8f,.1f,.2f,1));
                    try{Near(Pixel(probe,0,3,block),regular,"Graph automatic ground / "+p+" / "+mode);}
                    finally{probe.DisableKeyword("_LT_GRAPH_PROBE");probe.SetVector("_LTRockTint",Vector4.one);}
                    return regular;
                }
                Color Expected(Vector2 p,int mode,Vector3 normal)
                {
                    reference.CopyPropertiesFromMaterial(terrain[p.x<0?0:1]);reference.SetMatrix("_LTSurfaceWorldToLocal",Matrix4x4.identity);
                    reference.SetVector("_LTBakeRect",new Vector4(0,0,1,1));reference.SetVector("_LTProbeUV",new Vector4(p.x,p.y,mode,0));reference.SetVector("_LTProbeNormal",normal);
                    return Pixel(reference,referencePass,6);
                }
                void Near(Color a,Color b,string label){Require(Math.Abs(a.r-b.r)<.012f&&Math.Abs(a.g-b.g)<.012f&&Math.Abs(a.b-b.b)<.012f&&Math.Abs(a.a-b.a)<.012f,label+": "+a+" vs "+b);}
                Near(Auto(new Vector2(2,2),2,Vector3.up),new Color(0,0,0,1),"Fresh cache before its first terrain paint");
                Near(Auto(new Vector2(2,2),0,Vector3.up),new Color(.8f,.1f,.2f,1),"Fresh cache preserves original asphalt");
                var points=new[]{new Vector2(-7.5f,.3f),new Vector2(-.001f,3.5f),new Vector2(0,3.5f),new Vector2(.001f,3.5f),new Vector2(7.99f,7.5f),new Vector2(8,4)};
                foreach(bool tri in new[]{false,true})foreach(float height in new[]{0f,.7f,1f})
                {
                    foreach(var m in terrain){m.SetFloat("_LTTriplanar",tri?1:0);m.SetFloat("_LTHeightBlend",height);}revision++;Sync();
                    var normal=tri?new Vector3(.3f,1,.4f).normalized:Vector3.up;
                    foreach(var p in points)for(int mode=0;mode<3;mode++)Near(Auto(p,mode,normal),Expected(p,mode,normal),$"12 layers / tri={tri} / height={height} / point={p} / mode={mode}");
                }
                int unchanged=changes;Sync();Require(changes==unchanged,"Unchanged terrain rebuilt GPU resources");
                // Per-chunk projection slices must be remapped, including the signed
                // two-slice owner/Jacobian encoding used by intersecting spline roads.
                var cracks=Own(new Texture2DArray(2,2,1,TextureFormat.RGBA32,false,true));
                cracks.SetPixels(Enumerable.Repeat(new Color(.6f,.55f,.4f,1),4).ToArray(),0);cracks.Apply();
                for(int tile=0;tile<2;tile++)
                {
                    var projection=Own(new Texture2DArray(257,257,3,TextureFormat.RGBAFloat,false,true));
                    var damage=Own(new Texture2DArray(257,257,1,TextureFormat.RGBA32,false,true));
                    var direct=new Color[257*257];var network=new Color[direct.Length];var jacobian=new Color[direct.Length];
                    for(int z=0;z<257;z++)for(int x=0;x<257;x++)
                    {
                        int at=z*257+x;float px=(tile-1)*8+x/32f,pz=z/32f;
                        direct[at]=new Color(px*.2f,pz*.3f,1,0);
                        network[at]=new Color(px*.4f,pz*.1f,x<128?1:0,x<128?0:2);
                        jacobian[at]=new Color(.4f,0,0,.1f);
                    }
                    projection.SetPixels(direct,0);projection.SetPixels(network,1);projection.SetPixels(jacobian,2);projection.Apply();
                    damage.SetPixels(Enumerable.Repeat(new Color(.6f,.3f,0,1),direct.Length).ToArray(),0);damage.Apply();
                    var m=terrain[tile];m.SetFloat("_LTTriplanar",0);m.SetFloat("_LTRoadProjectionEnabled",1);
                    m.SetTexture("_LTRoadProjectionMap",projection);m.SetVector("_LTRoadProjectionSlots0",new Vector4(1,-2,0,0));
                    m.SetTexture("_LTAsphaltCracks",cracks);m.SetTexture("_LTAsphaltDamage",damage);m.SetVector("_LTAsphaltStyle0",new Vector4(1,0,2,0));
                }
                revision++;Sync();foreach(var p in points)for(int mode=0;mode<3;mode++)Near(Auto(p,mode,Vector3.up),Expected(p,mode,Vector3.up),"Spline ownership / crack slice remapping");
                unchanged=changes;
                terrain[1].SetVector("_LTTint6",new Vector4(.1f,1,.2f,1));Sync();Require(changes==unchanged+1,"Scalar layer edit not refreshed");
                Near(Auto(new Vector2(3,2),0,Vector3.up),Expected(new Vector2(3,2),0,Vector3.up),"Live tint refresh");
                var changed=(Texture2D)terrain[1].GetTexture("_LTWeights2");changed.SetPixels(Enumerable.Repeat(new Color(0,0,0,1),257*257).ToArray());changed.Apply();revision++;Sync();
                Near(Auto(new Vector2(3,2),0,Vector3.up),Expected(new Vector2(3,2),0,Vector3.up),"Paint/third weight map refresh");
                cache.Synchronize(new Vector2(8,8),Tiles(false));
                Near(Auto(new Vector2(-2,2),2,Vector3.up),new Color(0,0,0,1),"Removed chunk must not clamp onto its neighbour");
                cache.Dispose();Near(Auto(new Vector2(2,2),2,Vector3.up),new Color(0,0,0,1),"Unavailable terrain disables blend");
                // Shared samplers must not silently change the terrain's filtering.
                arrays[1].filterMode=FilterMode.Point;revision++;cache.Synchronize(new Vector2(8,8),Tiles());
                Require(!cache.Ready&&cache.Status.Contains("sampler"),"Mismatched filtering accepted by shared sampler");
                arrays[1].filterMode=FilterMode.Bilinear;revision++;Sync();
                Near(Auto(new Vector2(3,2),0,Vector3.up),Expected(new Vector2(3,2),0,Vector3.up),"Shared sampler recovery");
                // Automatic source adds no paint slot and preserves the single-layer selection.
                var root=Child("Owned auto-ground world");var world=root.AddComponent<LTWorld>();world.enabled=false;world.autoUpdate=false;root.SetActive(true);
                var road=Child("Owned auto-ground road",root.transform).AddComponent<LTRoad>();road.enabled=false;
                world.baseLayer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());road.groundLayer=Own(ScriptableObject.CreateInstance<LTSurfaceLayer>());
                road.mode=LTRoadMode.Asphalt;road.groundBlend=true;road.groundBlendSource=LTRoadGroundSource.Terrain;
                var palette=new List<LTSurfaceLayer>{world.baseLayer};road.GetComponent<LTPaintStamp>().AppendLayers(palette);
                Require(palette.Count==1&&!road.GetComponent<LTPaintStamp>().HasPaintLayers,"Auto mode repaints terrain/adds a layer slot");
                road.groundBlendSource=LTRoadGroundSource.SingleLayer;Require(road.GetComponent<LTPaintStamp>().EffectiveLayer==road.groundLayer,"Single-layer mode lost its selection");
                road.groundBlendSource=LTRoadGroundSource.Terrain;
                var rendererObject=Child("Owned road binding lifecycle");var renderer=rendererObject.AddComponent<MeshRenderer>();renderer.enabled=false;
                var binding=rendererObject.AddComponent<LTRoadGroundBlend>();binding.owner=road;rendererObject.SetActive(true);binding.Refresh();
                void BoundFallback(string label)
                {
                    var properties=new MaterialPropertyBlock();renderer.GetPropertyBlock(properties);
                    Require(properties.GetFloat("_LTRoadTerrainReady")==0&&properties.HasBuffer("_LTRoadTerrainChunks")&&properties.HasBuffer("_LTRoadTerrainLayers"),label);
                    Near(Pixel(probe,0,3,properties),new Color(.8f,.1f,.2f,1),label+" GPU draw");
                }
                probe.SetVector("_LTProbePosition",new Vector4(2,0,2,0));probe.SetVector("_LTProbeNormal",Vector3.up);
                BoundFallback("World disabled");binding.enabled=false;BoundFallback("Binding disabled");
                binding.enabled=true;binding.owner=null;binding.Refresh();BoundFallback("Owner removed");
                Object.DestroyImmediate(binding);renderer.GetPropertyBlock(block);Require(block.isEmpty,"Destroyed binding kept borrowed resources");
                // No renderer block at all: globals must still provide valid empty SRVs.
                Near(Pixel(probe,0,3),new Color(.8f,.1f,.2f,1),"Global fallback after binding removal");
                // Compile all three asphalt paths with automatic terrain data access.
                foreach(string shaderName in new[]{"LTEightLayers","LTRoadAuthoredGround","LTRoadGraphGround"})
                {
                    var material=Own(new Material(Resources.Load<Shader>(shaderName)));material.SetFloat("_LTRoadBlendSource",1);
                    batcherMaterials.Add(material);
                    material.EnableKeyword("_LT_ROAD_TERRAIN");
                    if(shaderName=="LTEightLayers")material.EnableKeyword("_LT_ROAD_BLEND");
                    foreach(string pass in new[]{"GBuffer","Forward","DepthOnly","ShadowCaster"}){int index=material.FindPass(pass);Require(index>=0,pass);ShaderUtil.CompilePass(material,index,true);}
                    Require(!ShaderUtil.ShaderHasError(material.shader),shaderName+": "+string.Join("; ",ShaderUtil.GetShaderMessages(material.shader).Select(m=>m.message)));
                    // Pass zero is ScenePicking for terrain, not its surface pass.
                    // Prime the actual GBuffer, then inspect on later editor updates.
                    Require(material.SetPass(material.FindPass("GBuffer")),shaderName+": could not prime GBuffer");
                }
                Require(!ShaderUtil.ShaderHasError(probeShader)&&!ShaderUtil.ShaderHasError(referenceShader),"Probe shader compilation error");
                report=$"PASS: {samples} GPU readbacks; automatic vs terrain color/normal/AO/smoothness/metallic, 12 layers, height blend, triplanar, reordered adjacent palettes, spline/network UVs + asphalt crack remapping, negative coordinates, scalar/paint refresh, chunk deletion, resource disposal, fresh/disabled/removed binding fallbacks and single-layer compatibility. Native HDRP + authored passes compiled and SRP Batcher compatibility checked. No scene/asset writes. Not a live-scene performance benchmark.";
            }
            catch(Exception e){failure=e;}
            finally
            {
                RenderTexture.active=previous;GL.sRGBWrite=srgb;GL.wireframe=wire;ShaderUtil.allowAsyncCompilation=async;
                // Keep only the inspection materials alive. No preview scene,
                // renderer, cache or global render-state override survives the call.
                for(int i=owned.Count-1;i>=0;i--)
                    if(owned[i]&&!batcherMaterials.Contains(owned[i] as Material))
                        try{Object.DestroyImmediate(owned[i]);}catch(Exception e){Fail(e);}
                try{EditorSceneManager.ClosePreviewScene(preview);}catch(Exception e){Fail(e);}
                VerifyUnchanged();
            }
            var wait=batcherMaterials.Count>0?new LTRoadBatcherWait(batcherMaterials.Count,EditorApplication.timeSinceStartup):null;
            int updates=0;bool released=false,finished=false;string inconclusive=null;
            void ReleaseMaterials()
            {
                if(released)return;released=true;
                foreach(var material in batcherMaterials)if(material)
                    try{Object.DestroyImmediate(material);}catch(Exception e){Fail(e);}
                batcherMaterials.Clear();
            }
            void Complete()
            {
                if(finished)return;finished=true;
                EditorApplication.update-=Finish;AssemblyReloadEvents.beforeAssemblyReload-=BeforeReload;
                EditorApplication.quitting-=BeforeQuit;EditorApplication.playModeStateChanged-=OnPlayMode;
                ReleaseMaterials();VerifyUnchanged();
                Application.logMessageReceivedThreaded-=Capture;running=false;
                if(!gpuErrors.IsEmpty)Fail(new InvalidOperationException("Native GPU/batcher diagnostics: "+string.Join("; ",gpuErrors.Distinct().Take(8))));
                if(failure!=null)Debug.LogError(Tag+"FAIL: "+failure);
                else if(inconclusive!=null)Debug.LogWarning(Tag+"INCONCLUSIVE (not a PASS): "+inconclusive);
                else if(wait==null||wait.Result!=LTRoadBatcherWait.Status.Compatible)Debug.LogError(Tag+"FAIL: native compatibility was not confirmed");
                else Debug.Log(Tag+report+" Native inspection: "+wait.Summary);
            }
            void Abort(string reason){inconclusive=reason;Complete();}
            void BeforeReload()=>Abort("Script reload interrupted native inspection; rerun after compilation.");
            void BeforeQuit()=>Abort("Editor shutdown interrupted native inspection.");
            void OnPlayMode(PlayModeStateChange state)
            {if(state==PlayModeStateChange.ExitingEditMode)Abort("Play mode interrupted native inspection; rerun in Edit mode.");}
            void Finish()
            {
                try
                {
                    if(!released)
                    {
                        if(failure==null&&gpuErrors.IsEmpty)
                        {
                            if(EditorApplication.isCompiling||EditorApplication.isPlayingOrWillChangePlaymode)
                            {Abort("Editor compilation / play transition interrupted native inspection.");return;}
                            Require(wait!=null,"No materials for native inspection");
                            var status=wait.Tick(EditorApplication.timeSinceStartup,i=>CheckBatcher(batcherMaterials[i]));
                            if(status==LTRoadBatcherWait.Status.Pending)return;
                            if(status==LTRoadBatcherWait.Status.Incompatible)Fail(new InvalidOperationException("SRP Batcher incompatibility: "+wait.Summary));
                            if(status==LTRoadBatcherWait.Status.TimedOut)
                                inconclusive=$"Native SRP Batcher metadata still uninitialized after {LTRoadBatcherWait.TimeoutSeconds}s / {wait.Attempts} polls. {wait.Summary}";
                        }
                        ReleaseMaterials();return;
                    }
                    // D3D diagnostics can arrive after inspection or resource release.
                    if(++updates<2)return;
                    Complete();
                }
                catch(Exception e){Fail(e);ReleaseMaterials();updates=0;}
            }
            AssemblyReloadEvents.beforeAssemblyReload+=BeforeReload;EditorApplication.quitting+=BeforeQuit;
            EditorApplication.playModeStateChanged+=OnPlayMode;
            EditorApplication.update+=Finish;
            if(failure==null)Debug.Log(Tag+$"{samples} GPU readbacks passed; awaiting native SRP Batcher initialization (up to {LTRoadBatcherWait.TimeoutSeconds}s). No scene changes.");
        }
        static LTRoadBatcherWait.Sample CheckBatcher(Material material)
        {
            var flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
            var active=typeof(ShaderUtil).GetMethod("GetShaderActiveSubshaderIndex",flags);
            var compatibility=typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityCode",flags);
            var reason=typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityIssueReason",flags);
            Require(active!=null&&compatibility!=null&&reason!=null,"Native SRP Batcher inspection API unavailable; not a PASS");
            Require(material&&material.shader,"Native inspection material was destroyed");
            var shader=material.shader;Require(!ShaderUtil.ShaderHasError(shader),shader.name+": shader compilation error during native inspection");
            int pass=material.FindPass("GBuffer");Require(pass>=0,shader.name+": GBuffer missing");
            bool async=ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation=false;
                Require(material.SetPass(pass),shader.name+": could not select GBuffer for native inspection");
                int sub=(int)active.Invoke(null,new object[]{shader});
                int code=sub<0?-1:(int)compatibility.Invoke(null,new object[]{shader,sub});
                string issue=sub<0?"Active subshader not initialized":code==0?"compatible":(string)reason.Invoke(null,new object[]{shader,sub,code});
                return new LTRoadBatcherWait.Sample(sub,code,issue,$"{shader.name}: GBuffer pass={pass}, subshader={sub}, code={code}, reason={issue}");
            }
            finally{ShaderUtil.allowAsyncCompilation=async;}
        }
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static string Hash(string path){using(var s=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(s));}
    }
}
