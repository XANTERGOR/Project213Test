using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    public sealed partial class LTPaintRuntime
    {
        RenderTexture globalColor,globalNormal;
        Shader globalBakeShader;
        LTWorld globalWorld;
        bool globalsReady,globalHook,globalBakeRequested,bakeThisTick,hasGlobalBake;
        int globalLayout,tilesBaked;
        bool globalTriplanar;
        public void RequestGlobalBake(){globalBakeRequested=true;nextUpdate=0;}

        void ReleaseGlobals()
        {
            if(globalHook)RenderPipelineManager.beginCameraRendering-=SelectGlobalMaterials;
            globalHook=false;globalsReady=false;hasGlobalBake=false;globalBakeRequested=false;
            if(globalWorld){globalWorld.globalAlbedoPreview=null;globalWorld.globalNormalPreview=null;}
            DestroyOwned(globalColor);DestroyOwned(globalNormal);globalColor=null;globalNormal=null;
        }
        static RenderTexture NewGlobal(string name,int size)
        {
            var texture=new RenderTexture(size,size,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear)
            {
                name=name,hideFlags=HideFlags.HideAndDontSave,useMipMap=true,autoGenerateMips=false,
                wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Trilinear
            };
            if(!texture.Create()){DestroyOwned(texture);throw new System.InvalidOperationException("Cannot allocate global layer maps.");}
            return texture;
        }
        void PrepareGlobals(LTWorld world)
        {
            globalWorld=world;tilesBaked=0;
            bakeThisTick=globalBakeRequested;globalBakeRequested=false;
            if(!globalHook){RenderPipelineManager.beginCameraRendering+=SelectGlobalMaterials;globalHook=true;}
        }
        bool PrepareRequestedBake(LTWorld world)
        {
            // Always bake the full-resolution source, independently of camera LOD.
            if(world.triplanarTexturing)
                foreach(var pair in chunks)
                    if(!pair.Key||!pair.Key.mesh||!pair.Key.mesh.HasVertexAttribute(VertexAttribute.Normal)||
                        !pair.Key.mesh.HasVertexAttribute(VertexAttribute.Tangent))return false;
            globalBakeShader=Resources.Load<Shader>("LTGlobalLayerBake");
            if(!globalBakeShader||!globalBakeShader.isSupported)
                return false;
            int size=Mathf.Clamp(Mathf.NextPowerOfTwo(world.globalLayerResolution),256,Mathf.Min(8192,SystemInfo.maxTextureSize));
            int layout=Mix(world.TerrainRect.GetHashCode(),Mix(world.chunksX,world.chunksZ));
            if(!globalColor||!globalNormal||!globalColor.IsCreated()||!globalNormal.IsCreated()||globalColor.width!=size||layout!=globalLayout)
            {
                DestroyOwned(globalColor);DestroyOwned(globalNormal);globalColor=null;globalNormal=null;
                globalsReady=false;
                hasGlobalBake=false;
                globalColor=NewGlobal("Global terrain Albedo",size);globalNormal=NewGlobal("Global terrain Normal",size);
                globalLayout=layout;
                foreach(var state in chunks.Values)state.globallyBaked=false;
            }
            world.globalAlbedoPreview=globalColor;world.globalNormalPreview=globalNormal;
            // Sparse cells have no draw. Never retain old or uninitialized texels in those slots.
            using(var commands=new CommandBuffer{name="Clear sparse terrain atlas"})
            {
                commands.SetRenderTarget(globalColor);commands.ClearRenderTarget(false,true,world.baseLayer?world.baseLayer.tint.linear:Color.gray);
                commands.SetRenderTarget(globalNormal);commands.ClearRenderTarget(false,true,new Color(.5f,.5f,1,1));
                Graphics.ExecuteCommandBuffer(commands);
            }
            foreach(var state in chunks.Values)state.globallyBaked=false;
            return true;
        }
        void BakeGlobalTile(LTWorld world,LTChunk chunk,ChunkState state)
        {
            if(!world.enableGlobalLayerMaps||!globalColor||!globalNormal||!globalBakeShader||!globalBakeShader.isSupported||state.globallyBaked)return;
            if(!state.bakeMaterial)state.bakeMaterial=new Material(globalBakeShader){hideFlags=HideFlags.HideAndDontSave};
            state.bakeMaterial.CopyPropertiesFromMaterial(state.material);
            BindRoadProjection(state.bakeMaterial,state);
            state.bakeMaterial.shaderKeywords=System.Array.Empty<string>();
            int size=globalColor.width;
            var xr=LTPaintMath.AtlasRange(chunk.x-world.chunkOrigin.x,world.chunksX,size);var yr=LTPaintMath.AtlasRange(chunk.z-world.chunkOrigin.y,world.chunksZ,size);
            int x0=xr.x,x1=xr.y,y0=yr.x,y1=yr.y;
            state.bakeMaterial.SetVector("_LTBakeRect",new Vector4((float)x0/size,(float)y0/size,(float)(x1-x0)/size,(float)(y1-y0)/size));
            // The bake shader places a six-vertex tile directly in atlas UV space.
            // Keep a full viewport: no second, platform-dependent viewport Y flip.
            using(var commands=new CommandBuffer{name="Bake global terrain layer tile"})
            {
                commands.SetRenderTarget(globalColor);
                commands.SetViewport(new Rect(0,0,size,size));
                commands.DrawProcedural(Matrix4x4.identity,state.bakeMaterial,0,MeshTopology.Triangles,6);
                commands.SetRenderTarget(globalNormal);
                commands.SetViewport(new Rect(0,0,size,size));
                commands.DrawProcedural(Matrix4x4.identity,state.bakeMaterial,1,MeshTopology.Triangles,6);
                Graphics.ExecuteCommandBuffer(commands);
            }
            state.globallyBaked=true;tilesBaked++;
        }
        void BakeGlobalGeometry(LTWorld world,LTChunk chunk,ChunkState state)
        {
            var material=state.bakeMaterial;
            var localToWorld=chunk.transform.localToWorldMatrix;
            material.SetMatrix("_LTBakeLocalToTerrain",world.transform.worldToLocalMatrix*localToWorld);
            material.SetMatrix("_LTBakeLocalToWorld",localToWorld);
            material.SetMatrix("_LTBakeNormalToWorld",localToWorld.inverse.transpose);
            material.SetMatrix("_LTSurfaceWorldToLocal",world.transform.worldToLocalMatrix);
            using(var commands=new CommandBuffer{name="Bake triplanar terrain geometry"})
            {
                commands.SetRenderTarget(globalColor);
                commands.SetViewport(new Rect(0,0,globalColor.width,globalColor.height));
                for(int sub=0;sub<chunk.mesh.subMeshCount;sub++)
                    commands.DrawMesh(chunk.mesh,Matrix4x4.identity,material,sub,3);
                commands.SetRenderTarget(globalNormal);
                commands.SetViewport(new Rect(0,0,globalNormal.width,globalNormal.height));
                for(int sub=0;sub<chunk.mesh.subMeshCount;sub++)
                    commands.DrawMesh(chunk.mesh,Matrix4x4.identity,material,sub,4);
                Graphics.ExecuteCommandBuffer(commands);
            }
            state.globalGeometryMesh=chunk.mesh;
            state.globalGeometryRevision=chunk.updatedAt;
            state.globalGeometryTransform=world.transform.worldToLocalMatrix*localToWorld;
        }
        void FinishGlobals(LTWorld world,bool complete)
        {
            bool failed=false;
            if(bakeThisTick)
            {
                using var bakeTimer=world.paintCpu.Measure(LTPaintCpuCapture.Stage.GlobalBake);
                if(world.enableGlobalLayerMaps&&complete&&PrepareRequestedBake(world))
                {
                    foreach(var pair in chunks)BakeGlobalTile(world,pair.Key,pair.Value);
                    // Fill first, then overlay geometry: a neighbouring tile's
                    // rounded procedural rectangle must never erase a mesh edge.
                    if(world.triplanarTexturing)
                        foreach(var pair in chunks)BakeGlobalGeometry(world,pair.Key,pair.Value);
                    globalTriplanar=world.triplanarTexturing;
                    hasGlobalBake=true;
                }
                else failed=true;
            }
            if(tilesBaked>0){globalColor.GenerateMips();globalNormal.GenerateMips();}
            if(tilesBaked>0)world.globalBakeRevision++;
            int layout=Mix(world.TerrainRect.GetHashCode(),Mix(world.chunksX,world.chunksZ));
            bool live=hasGlobalBake&&globalColor&&globalNormal&&globalColor.IsCreated()&&globalNormal.IsCreated();
            var saved=world.savedGlobalBake;
            bool useSaved=!live&&saved&&saved.FormatsSupported;
            Texture colorMap=live?(Texture)globalColor:useSaved?saved.albedo:null;
            Texture normalMap=live?(Texture)globalNormal:useSaved?saved.normal:null;
            bool projectionMatches=live?globalTriplanar==world.triplanarTexturing:useSaved&&saved.triplanar==world.triplanarTexturing;
            globalsReady=projectionMatches&&world.enableGlobalLayerMaps&&complete&&colorMap&&normalMap&&(live?layout==globalLayout:saved.MatchesLayout(world));
            world.globalAlbedoPreview=colorMap;world.globalNormalPreview=normalMap;
            bool stale=!complete||(globalColor&&globalColor.width!=Mathf.Clamp(Mathf.NextPowerOfTwo(world.globalLayerResolution),256,Mathf.Min(8192,SystemInfo.maxTextureSize)));
            foreach(var state in chunks.Values)stale|=!state.globallyBaked;
            if(live&&globalTriplanar)
                foreach(var pair in chunks)
                    stale|=pair.Value.globalGeometryMesh!=pair.Key.mesh||
                        pair.Value.globalGeometryRevision!=pair.Key.updatedAt||
                        pair.Value.globalGeometryTransform!=world.transform.worldToLocalMatrix*pair.Key.transform.localToWorldMatrix;
            if(useSaved)
            {
#if UNITY_EDITOR
                using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.SavedSignature))
                    stale=!complete||saved.signature!=LTGlobalBakeAsset.Signature(world);
#else
                stale=!complete;
#endif
            }
            float start=Mathf.Max(0,world.globalLayerStart),end=Mathf.Max(start+1,world.globalLayerEnd);
            var globalParams=new Vector4(globalsReady?1:0,start,end,LTGlobalBakeAsset.NormalEncoding(normalMap));
            // Store brightness minus one: existing materials/defaults with z=0 stay neutral.
            // Display-only correction, deliberately excluded from the atlas bake/signature.
            var farSurface=new Vector4(world.farLayerSmoothness,world.farLayerMetallic,Mathf.Clamp(world.farLayerBrightness,0,4)-1,0);
            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.GlobalMaterials))
            {
                foreach(var state in chunks.Values)
                {
                    // Compare actual binding state, not the stale-bake indicator. Editing near
                    // layers must refresh the far copy even while the manual atlas stays unchanged.
                    bool changed=!state.globalBindingReady||!state.boundGlobalParams.Equals(globalParams)||
                        !state.boundFarSurface.Equals(farSurface)||state.boundGlobalColor!=colorMap||state.boundGlobalNormal!=normalMap||
                        ((globalsReady||state.displacementActive)&&state.farMaterialDirty)||
                        (globalsReady&&!state.farMaterial)||(state.displacementActive&&!state.flatMaterial);
                    if(changed)
                    {
                        state.material.SetVector("_LTGlobalParams",globalParams);
                        state.material.SetVector("_LTFarSurface",farSurface);
                        state.material.SetTexture("_LTGlobalAlbedo",colorMap);state.material.SetTexture("_LTGlobalNormal",normalMap);
                        if(globalsReady)
                        {
                            if(!state.farMaterial)state.farMaterial=new Material(Resources.Load<Shader>("LTEightLayers")){name=state.material.name+" Far",hideFlags=HideFlags.HideAndDontSave};
                            state.farMaterial.CopyPropertiesFromMaterial(state.material);state.farMaterial.EnableKeyword("_LT_FAR_ONLY");
                            BindRoadProjection(state.farMaterial,state);
                        }
                        if(state.displacementActive)
                        {
                            if(!state.flatMaterial)state.flatMaterial=new Material(Resources.Load<Shader>("LTEightLayers")){name=state.material.name+" No displacement",hideFlags=HideFlags.HideAndDontSave};
                            state.flatMaterial.CopyPropertiesFromMaterial(state.material);
                            BindRoadProjection(state.flatMaterial,state);
                        }
                        state.farMaterialDirty=false;
                        state.boundGlobalParams=globalParams;state.boundFarSurface=farSurface;
                        state.boundGlobalColor=colorMap;state.boundGlobalNormal=normalMap;state.globalBindingReady=true;
                        unchecked{state.diagnosticMaterialRevision++;}
                        world.paintCpu.GlobalUpdated(globalsReady);
                    }
                    // Keep restoration independent of binding changes (e.g. another camera/save).
                    if(!globalsReady&&state.renderer&&state.renderer.sharedMaterial==state.farMaterial)
                        state.renderer.sharedMaterial=state.material;
                    if(!state.displacementActive&&state.renderer&&state.renderer.sharedMaterial==state.flatMaterial)
                        state.renderer.sharedMaterial=state.material;
                }
            }
            if(!world.enableGlobalLayerMaps)world.globalLayerStatus="Глобальные карты выключены.";
            else if(colorMap&&!projectionMatches)world.globalLayerStatus="Режим проекции изменён. Запеките и сохраните карты заново; до этого используются детальные слои.";
            else if(!live&&saved&&!saved.FormatsSupported)world.globalLayerStatus="Сохранённые карты отсутствуют или их формат не поддерживается GPU. Используйте совместимое запекание.";
            else if(globalsReady)world.globalLayerStatus=$"{(useSaved?$"Сохранённые карты {saved.albedo.format}/{saved.normal.format}, {saved.TextureBytes/(1024.0*1024):0.0} МиБ GPU":"Запекание в памяти RGBA32 (без сжатия)")}: {colorMap.width}×{colorMap.height}. "+(stale?"Карты устарели. Вдали используется предыдущий результат — запеките и сохраните новые карты.":"Карты актуальны.");
            else world.globalLayerStatus="Запеките и сохраните глобальные карты. Пока используются детальные слои; также проверьте соответствие размеров мира сохранённому запеканию.";
            if(failed)world.globalLayerStatus=$"Запекание не выполнено: проверьте готовность всех чанков, лимит {LayerCapacity} слоёв и шейдер запекания. "+world.globalLayerStatus;
        }
        void SelectGlobalMaterials(ScriptableRenderContext context,Camera camera)
        {
            // Inspector/asset preview cameras render their own objects, not this
            // terrain. They must not replace shared chunk materials with Far or
            // No displacement while the Scene view is using the near variant.
            // Keep per-view selection for real renders, including reflections.
            if(!camera||camera.cameraType==CameraType.Preview)return;
            if(!globalWorld||globalWorld.paintBenchmarkRunning||!arraysAvailable)return;
            using var cameraTimer=globalWorld.paintCpu.CameraScope();
            float end=Mathf.Max(Mathf.Max(0,globalWorld.globalLayerStart)+1,globalWorld.globalLayerEnd);
            bool diagnostic=LTTerrainFrameDiagnostic.Active;
            bool layerComparison=diagnostic&&LTTerrainDiagnosticMaterials.IsLayerComparison(LTTerrainFrameDiagnostic.Mode);
            var diagnosticSettings=diagnostic?new Vector4(Mathf.Clamp(globalWorld.fastLayerBlendBudget,0,.15f),
                Mathf.Clamp(globalWorld.fastLayerProjectionCutoff,0,.15f),0,0):Vector4.zero;
            int diagnosticPaired=0,diagnosticUnpaired=0;
            int diagnosticNear=0,diagnosticFar=0,diagnosticSkipped=0,diagnosticForced=0,diagnosticUnavailable=0;
            foreach(var state in chunks.Values)
            {
                if(!state.renderer)continue;
                // Nearest point of the complete mesh bounds: only fully distant chunks use far-only.
                float distanceSquared=state.renderer.bounds.SqrDistance(camera.transform.position);
                if(diagnostic&&(!state.coverageActive||layerComparison))
                {
                    state.diagnosticMaterials??=new LTTerrainDiagnosticMaterials(state.renderer);
                    distanceSquared=state.diagnosticMaterials.DistanceSquared(camera.transform.position);
                }
                bool far=globalsReady&&state.farMaterial&&distanceSquared>=end*end;
                float displacementEnd=DisplacementEnd(globalWorld);
                var selected=far?state.farMaterial:state.displacementActive&&state.flatMaterial&&distanceSquared>=displacementEnd*displacementEnd?state.flatMaterial:state.material;
                if(diagnostic)
                {
                    // Layer comparison modes change fragment material parameters only;
                    // keep tessellation/deformation and its full-layer heights intact.
                    // Clones belong only to terrain chunks; shared road/grass sampling keeps the originals.
                    if(state.coverageActive&&!layerComparison){state.diagnosticMaterials?.Dispose();diagnosticSkipped++;}
                    else if(selected)
                    {
                        if(far)diagnosticFar++;else diagnosticNear++;
                        state.diagnosticMaterials??=new LTTerrainDiagnosticMaterials(state.renderer);
                        state.diagnosticMaterials.SetRestoreMaterial(selected);
                        selected=state.diagnosticMaterials.Select(selected,state.diagnosticMaterialRevision,
                            far?LTTerrainDiagnosticMode.Normal:LTTerrainFrameDiagnostic.Mode,far,out bool refreshed,
                            diagnosticSettings,roadProjections.TryGetValue(state,out _)||asphaltStates.TryGetValue(state,out _));
                        if(refreshed)BindRoadProjection(selected,state);
                        if(layerComparison&&!far&&selected.GetFloat("_LTBaseOnly")<.5f)
                        {
                            if(state.ready&&LTTerrainDiagnosticMaterials.LayerModeBound(selected,LTTerrainFrameDiagnostic.Mode))diagnosticPaired++;
                            else diagnosticUnpaired++;
                        }
                        // Warm the far copy in EVERY mode to keep ownership stable.
                        Material diagnosticFarMaterial=null;
                        if(globalsReady&&state.farMaterial)
                        {
                            diagnosticFarMaterial=state.diagnosticMaterials.Select(state.farMaterial,state.diagnosticMaterialRevision,
                                LTTerrainDiagnosticMode.Normal,true,out bool farRefreshed,
                                diagnosticSettings,roadProjections.TryGetValue(state,out _)||asphaltStates.TryGetValue(state,out _));
                            if(farRefreshed)BindRoadProjection(diagnosticFarMaterial,state);
                        }
                        if(LTTerrainFrameDiagnostic.Mode==LTTerrainDiagnosticMode.ForceFar&&!far)
                        {
                            if(diagnosticFarMaterial){selected=diagnosticFarMaterial;diagnosticForced++;}
                            else diagnosticUnavailable++;
                        }
                    }
                }
                else state.diagnosticMaterials?.Dispose();
                if(state.renderer.sharedMaterial!=selected)state.renderer.sharedMaterial=selected;
            }
            if(diagnostic)LTTerrainFrameDiagnostic.Observe(camera,diagnosticNear,diagnosticFar,diagnosticSkipped,diagnosticForced,diagnosticUnavailable,diagnosticPaired,diagnosticUnpaired);
        }
    }
}
