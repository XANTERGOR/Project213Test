using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTPaintRuntime
    {
        sealed partial class RockState
        {
            public LTRockRenderOutput split;
            public Material[] splitMaterials;
            public readonly List<Material> ownedSplitMaterials=new List<Material>();
            public Material[] bridgeRestore;
            public LTRockMaterialBake bridgeBake;
            public LTRockLODAtlases rockBakes;
            public int splitSignature;
            public uint splitFastRevision=uint.MaxValue;
            public bool bakeFailed,bridgeBakeSupported;
            public string compatibilityWarning;
        }
        static bool SupportedRockMaterial(Material material,out string reason)
        {
            reason="";
            if(!material){reason="не назначен собственный материал";return false;}
            if(!material.shader||material.shader.name!="HDRP/Lit")
            {reason="переход собственного материала пока поддерживает HDRP/Lit";return false;}
            if(material.GetFloat("_SurfaceType")>.5f||material.GetFloat("_UVBase")>.01f||material.GetFloat("_NormalMapSpace")>.5f||
                material.IsKeywordEnabled("_DETAIL_MAP")||material.IsKeywordEnabled("_HEIGHTMAP")||material.IsKeywordEnabled("_ALPHATEST_ON")||
                material.IsKeywordEnabled("_MATERIAL_FEATURE_SUBSURFACE_SCATTERING")||material.IsKeywordEnabled("_MATERIAL_FEATURE_ANISOTROPY")||
                material.IsKeywordEnabled("_MATERIAL_FEATURE_SPECULAR_COLOR")||material.IsKeywordEnabled("_EMISSIVE_COLOR_MAP")||
                material.GetColor("_EmissiveColor").maxColorComponent>.001f||material.GetFloat("_MaterialID")!=1||
                material.GetFloat("_CoatMask")>0||material.GetFloat("_DoubleSidedEnable")>.5f||material.IsKeywordEnabled("_BENTNORMALMAP"))
            {reason="нужен непрозрачный HDRP/Lit с UV0, tangent-normal и без detail/height/alpha-test/особой модели освещения";return false;}
            return true;
        }
        static void BindOriginalRock(Material target,Material source,bool useOriginal)
        {
            target.SetVector("_LTRockFlags",Vector4.zero);
            if(!useOriginal||!source)return;
            Texture normal=source.IsKeywordEnabled("_NORMALMAP")?source.GetTexture("_NormalMap"):null,
                mask=source.IsKeywordEnabled("_MASKMAP")?source.GetTexture("_MaskMap"):null;
            target.SetTexture("_LTRockColorMap",source.GetTexture("_BaseColorMap")??Texture2D.whiteTexture);
            target.SetTexture("_LTRockNormalMap",normal?normal:Texture2D.normalTexture);
            target.SetTexture("_LTRockMaskMap",mask?mask:Texture2D.whiteTexture);
            target.SetVector("_LTRockTint",source.GetColor("_BaseColor").linear);
            var scale=source.GetTextureScale("_BaseColorMap");var offset=source.GetTextureOffset("_BaseColorMap");
            target.SetVector("_LTRockUV",new Vector4(scale.x,scale.y,offset.x,offset.y));
            target.SetVector("_LTRockSettings",new Vector4(source.GetFloat("_NormalScale"),source.GetFloat("_Smoothness"),source.GetFloat("_Metallic"),source.GetFloat("_MetallicRemapMin")));
            target.SetVector("_LTRockFlags",new Vector4(1,normal?1:0,mask?1:0,source.GetFloat("_MetallicRemapMax")));
            target.SetVector("_LTRockRemap",new Vector4(source.GetFloat("_AORemapMin"),source.GetFloat("_AORemapMax"),
                source.GetFloat("_SmoothnessRemapMin"),source.GetFloat("_SmoothnessRemapMax")));
        }
        static void ConfigureRockShader(Material material)
        {
            material.EnableKeyword("_LT_ROCK_SURFACE");material.EnableKeyword("_REQUIRE_UV2");
            material.DisableKeyword("_LT_FAR_ONLY");material.SetVector("_LTGlobalParams",Vector4.zero);
            material.SetVector("_LTRockFarParams",Vector4.zero);material.SetFloat("_LTRockProjection",1);
            material.SetFloat("_LTDebugCoverage",0);
        }
        static void AssignRock(Material[] materials,MeshRenderer renderer)
        {
            if(!renderer)return;var old=renderer.sharedMaterials;bool changed=old.Length!=materials.Length;
            for(int i=0;!changed&&i<old.Length;i++)changed=old[i]!=materials[i];
            if(changed)renderer.sharedMaterials=materials;
        }
        static void RestoreSplitRock(RockState state)
        {
            if(state.split==null)return;
            if(state.split.bridgeRenderer&&state.bridgeRestore!=null)AssignRock(state.bridgeRestore,state.split.bridgeRenderer);
            if(state.renderer&&state.stamp&&state.stamp.objectMaterials!=null)AssignRock(state.stamp.objectMaterials,state.renderer);
        }
        static void ReleaseSplitRock(RockState state)
        {
            state.rockBakes?.Dispose();state.bridgeBake?.Dispose();state.rockBakes=null;state.bridgeBake=null;
            foreach(var material in state.ownedSplitMaterials)DestroyOwned(material);state.ownedSplitMaterials.Clear();
            state.splitMaterials=null;state.split=null;state.bridgeRestore=null;
        }
        void UpdateSplitRock(LTWorld world,RockState state,bool rebound,ref bool bakedThisTick)
        {
            var stamp=state.stamp;var output=stamp.renderOutput;
            int signature=Mix(stamp.useTerrainMaterial?1:0,stamp.materialFarResolution);
            signature=Mix(signature,stamp.simplifyDistantMaterial?1:0);
            signature=Mix(signature,stamp.transform.localToWorldMatrix.GetHashCode());
            var originals=stamp.objectMaterials??Array.Empty<Material>();
            foreach(var material in originals)signature=Mix(signature,material?(int)material.ComputeCRC():0);
            bool changed=rebound||state.split!=output||state.splitSignature!=signature;
            if(changed)
            {
                RestoreSplitRock(state);ReleaseSplitRock(state);
                state.split=output;state.splitSignature=signature;state.bakeFailed=false;
                state.bridgeRestore=output.bridgeRenderer.sharedMaterials;
                ConfigureRockShader(state.paint.material);BindOriginalRock(state.paint.material,null,false);
                if(stamp.useTerrainMaterial&&stamp.simplifyDistantMaterial&&output.atlasUV&&output.rock.vertexCount>0)
                    state.rockBakes=new LTRockLODAtlases(output,stamp,state.paint.material);
                state.splitMaterials=new Material[output.transition.subMeshCount];
                string warning="";state.bridgeBakeSupported=true;
                for(int sub=0;sub<state.splitMaterials.Length;sub++)
                {
                    var material=state.splitMaterials[sub]=new Material(state.paint.material)
                        {name=stamp.name+" / Transition "+sub,hideFlags=HideFlags.HideAndDontSave};
                    state.ownedSplitMaterials.Add(material);
                    ConfigureRockShader(material);
                    bool unique=!stamp.useTerrainMaterial&&sub<output.sourceSubmeshes;
                    Material own=sub<originals.Length?originals[sub]:null;
                    if(unique&&!SupportedRockMaterial(own,out string reason))
                    {
                        warning=reason;state.bridgeBakeSupported=false;
                        // Unsupported shaders retain the original collar appearance, not a
                        // silently substituted terrain material. No far bake of foreign code.
                        state.splitMaterials[sub]=own?own:world.material;continue;
                    }
                    BindOriginalRock(material,own,unique);
                }
                state.compatibilityWarning=warning;
                stamp.renderStatus=warning.Length>0?"Внимание: "+warning+". Собственный материал сохранён, бленд для этого слота отключён.":
                    "Скала и переход разделены. Дальний атлас: "+(stamp.simplifyDistantMaterial?"ожидает запекания":"выключен");
            }
            // Settings also reach existing transition copies without rebuilding far atlases.
            // Only our owned copies are touched; unsupported original materials stay intact.
            if(changed||state.splitFastRevision!=state.paint.diagnosticMaterialRevision)
            {
                foreach(var material in state.ownedSplitMaterials)
                    ApplyFastLayers(material,state.paint.fastLayerEnabled,state.paint.fastLayerRoads,state.paint.fastLayerParams);
                state.splitFastRevision=state.paint.diagnosticMaterialRevision;
            }
            var rockMaterials=new Material[output.sourceSubmeshes];
            for(int i=0;i<rockMaterials.Length;i++)rockMaterials[i]=stamp.useTerrainMaterial?state.paint.material:
                i<originals.Length?originals[i]:world.material;
            AssignRock(rockMaterials,state.renderer);AssignRock(state.splitMaterials,output.bridgeRenderer);
            output.bridgeRenderer.enabled=state.renderer.enabled;
            stamp.terrainLayersMaterialApplied=stamp.useTerrainMaterial;
            // At most one atlas pair per coordinator tick. Never rebake on camera motion.
            if(stamp.simplifyDistantMaterial&&output.atlasUV&&!state.bakeFailed&&!bakedThisTick)
            {
                try
                {
                    if(state.bridgeBakeSupported&&state.bridgeBake==null&&output.transition.vertexCount>0)
                    {
                        state.bridgeBake=LTRockMaterialBake.Build(output.transition,stamp.transform,world,state.splitMaterials,stamp.materialFarResolution);
                        bakedThisTick=true;
                    }
                    else if(state.rockBakes!=null)bakedThisTick=state.rockBakes.BakeNext(world,rockMaterials);
                }
                catch(Exception error)
                {state.bakeFailed=true;stamp.renderStatus="Дальний атлас недоступен; сохранён ближний материал: "+error.Message;Debug.LogWarning(stamp.renderStatus,stamp);}
            }
            state.rockBakes?.RefreshBinding();
            if(state.bridgeBake!=null)foreach(var material in state.splitMaterials)state.bridgeBake.Bind(material,stamp);
            bool needsRock=stamp.useTerrainMaterial&&output.rock.vertexCount>0;
            bool needsBridge=output.transition.vertexCount>0;
            if(state.compatibilityWarning.Length==0&&stamp.simplifyDistantMaterial&&(!needsRock||state.rockBakes!=null&&state.rockBakes.Ready)&&(!needsBridge||state.bridgeBake!=null))
                stamp.renderStatus="Дальний атлас готов: переход"+(stamp.useTerrainMaterial?" и скала":"; скала сохраняет свой материал")+". Атлас: 2 выборки вместо слоёв.";
        }
    }
}
