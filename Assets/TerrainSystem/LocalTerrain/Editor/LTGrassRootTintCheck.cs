using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTGrassRootTintCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Grass Root Tint (no scene changes)
        // Archived validation menu: Tools/Local Terrain/Validate Grass Color (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var owned=new List<Object>();var previous=RenderTexture.active;
            bool srgb=GL.sRGBWrite,wire=GL.wireframe,async=ShaderUtil.allowAsyncCompilation;
            T Own<T>(T value) where T:Object{value.hideFlags=HideFlags.HideAndDontSave;owned.Add(value);return value;}
            try
            {
                ShaderUtil.allowAsyncCompilation=false;GL.wireframe=false;GL.sRGBWrite=false;
                var shader=AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath("09db06601f37f3a4da04a65182702fd6"));
                Require(shader,"Grass Shader Graph missing; wait for import.");
                var defaults=Own(new Material(shader));Require(LTGrassRootTintWindow.Supported(defaults),"Root properties missing from imported graph.");
                Require(defaults.GetFloat(LTGrassRootTintWindow.Strength)==0,"Existing materials must remain neutral by default.");
                var source=AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath("477914fe08f0eaa40bd5eeb2b16ae8b6"));
                Require(source&&source.shader==shader,"Expected source grass material missing.");var originalCrc=source.ComputeCRC();
                var live=Own(new Material(source));live.SetFloat(LTGrassRootTintWindow.Strength,.65f);
                live.SetFloat(LTGrassRootTintWindow.Base,-.2f);live.SetFloat(LTGrassRootTintWindow.Height,.4f);
                var adapter=Resources.Load<LTDetailGpuShader>("LTDetailGpu/GrassWind");
                Require(adapter&&adapter.source==shader&&adapter.indirect&&string.IsNullOrEmpty(adapter.error),"Grass indirect adapter missing or failed: "+(adapter?adapter.error:"not imported"));
                var indirect=Own(new Material(adapter.indirect));indirect.CopyPropertiesFromMaterial(live);
                Require(LTGrassRootTintWindow.Supported(indirect),"Root properties missing from indirect graph.");
                foreach(var property in new[]{LTGrassRootTintWindow.Strength,LTGrassRootTintWindow.Base,LTGrassRootTintWindow.Height})
                    Require(indirect.GetFloat(property)==live.GetFloat(property),"Indirect copy lost "+property);
                Require(indirect.GetColor(LTGrassRootTintWindow.Tint)==live.GetColor(LTGrassRootTintWindow.Tint),"Indirect tint mismatch.");
                LTGrassVariationCheck.Properties(defaults,live,indirect);
                int passes=0;
                foreach(var material in new[]{live,indirect})
                {
                    // INSTANCING_ON is an engine-controlled keyword. Enabling it
                    // directly logs an error in Unity 6; use the material flag.
                    // CompilePass is not proof of a live instanced draw.
                    material.enableInstancing=true;
                    if(material==indirect)material.EnableKeyword("PROCEDURAL_INSTANCING_ON");
                    for(int pass=0;pass<material.passCount;pass++){ShaderUtil.CompilePass(material,pass,true);passes++;}
                    Require(!ShaderUtil.ShaderHasError(material.shader),string.Join("; ",ShaderUtil.GetShaderMessages(material.shader).Select(m=>m.message)));
                }
                Require(SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat),"ARGBFloat is required for the pixel check.");
                var probeShader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/TerrainSystem/LocalTerrain/Editor/LTGrassRootTintCheck.shader");
                Require(probeShader&&probeShader.isSupported,"Root tint probe shader unavailable.");
                var probe=Own(new Material(probeShader));ShaderUtil.CompilePass(probe,0,true);
                Require(!ShaderUtil.ShaderHasError(probeShader),"Root tint probe did not compile.");
                var target=Own(new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));target.Create();
                var read=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                var albedo=new Color(.7f,.45f,.18f,.37f);var tint=new Color(.3f,.2f,.1f,1);int pixels=0;
                void Pixel(float rest,float rootBase,float height,float amount,Color multiplier)
                {
                    probe.SetVector("_TestBaseColor",albedo);probe.SetVector("_TestTint",multiplier);
                    probe.SetFloat("_TestRestHeight",rest);probe.SetFloat("_TestBase",rootBase);
                    probe.SetFloat("_TestHeight",height);probe.SetFloat("_TestStrength",amount);
                    GL.sRGBWrite=false;Graphics.Blit(Texture2D.whiteTexture,target,probe,0);
                    RenderTexture.active=target;read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();
                    float t=Mathf.Clamp01((rest-rootBase)/Mathf.Max(height,.0001f));float w=Mathf.Clamp01(amount)*(1-t*t*(3-2*t));
                    var expected=albedo*Color.Lerp(Color.white,multiplier,w);expected.a=albedo.a;var actual=read.GetPixel(0,0);
                    Require(Mathf.Abs(actual.r-expected.r)<.0002f&&Mathf.Abs(actual.g-expected.g)<.0002f&&Mathf.Abs(actual.b-expected.b)<.0002f&&Mathf.Abs(actual.a-expected.a)<.0002f,
                        $"Root pixel mismatch at height {rest}: {actual} != {expected}");pixels++;
                }
                foreach(float rest in new[]{-.5f,-.2f,0f,.2f,1f})
                {Pixel(rest,-.2f,.4f,.65f,tint);Pixel(rest,-.2f,.4f,0,tint);Pixel(rest,-.2f,.4f,1,Color.white);}
                Pixel(0,0,0,1,tint);Pixel(1,0,-1,1,tint);Pixel(0,0,.5f,2,tint);Pixel(0,0,.5f,-1,tint);
                int variationPixels=LTGrassVariationCheck.Pixels(probe,target,read);
                LTGrassVariationCheck.PlacementRoots();
                Require(source.ComputeCRC()==originalCrc,"Source material changed during validation.");
                Debug.Log($"[Grass Color v3] PASS: neutral defaults, source/indirect properties, {passes} raster pass compilations, {pixels} root + {variationPixels} variation HLSL pixels, offset/rotated/mirrored placement roots. No saved scene or source material changes. Does not verify live wind/LOD appearance, instanced rendering or performance.");
            }
            catch(Exception e){Debug.LogError("[Grass Color v3] FAIL: "+e);}
            finally
            {
                RenderTexture.active=previous;GL.sRGBWrite=srgb;GL.wireframe=wire;ShaderUtil.allowAsyncCompilation=async;
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
            }
        }
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    }
}
