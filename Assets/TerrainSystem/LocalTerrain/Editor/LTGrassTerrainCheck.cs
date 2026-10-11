using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTGrassTerrainCheck
    {
        // Archived validation menu: Tools/Local Terrain/Validate Grass Terrain Color (no scene changes)
        public static void Run()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            var owned=new List<Object>();var previous=RenderTexture.active;
            bool srgb=GL.sRGBWrite,wire=GL.wireframe,async=ShaderUtil.allowAsyncCompilation;
            using var map=new LTGrassTerrainMap(64);using var other=new LTGrassTerrainMap(32);
            T Own<T>(T obj) where T:Object{obj.hideFlags=HideFlags.HideAndDontSave;owned.Add(obj);return obj;}
            try
            {
                GL.sRGBWrite=false;GL.wireframe=false;ShaderUtil.allowAsyncCompilation=false;
                var source=AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath("09db06601f37f3a4da04a65182702fd6"));
                var adapter=Resources.Load<LTDetailGpuShader>("LTDetailGpu/GrassWind");
                Require(source&&adapter&&adapter.indirect&&string.IsNullOrEmpty(adapter.error),"Grass graph/adapter is not ready.");
                int passes=0;
                foreach(var shader in new[]{source,adapter.indirect})
                {
                    var m=Own(new Material(shader));m.enableInstancing=true;
                    Require(m.HasProperty(LTGrassTerrainMap.Map)&&m.HasProperty(LTGrassTerrainMap.Height)&&
                        m.GetFloat(LTGrassTerrainMap.Strength)==0&&m.GetFloat(LTGrassTerrainMap.Ready)==0,"Non-neutral or missing terrain properties.");
                    if(shader==adapter.indirect)m.EnableKeyword("PROCEDURAL_INSTANCING_ON");
                    for(int pass=0;pass<m.passCount;pass++){ShaderUtil.CompilePass(m,pass,true);passes++;}
                    Require(!ShaderUtil.ShaderHasError(shader),string.Join("; ",ShaderUtil.GetShaderMessages(shader).Select(e=>e.message)));
                }
                var bake=Resources.Load<Shader>("LTGlobalLayerBake");Require(bake&&bake.isSupported,"Shared albedo baker unavailable.");
                var white=Own(new Texture2DArray(1,1,1,TextureFormat.RGBA32,false,true));white.SetPixels(new[]{Color.white},0);white.Apply();
                var colors=new[]{new Color(.2f,.4f,.1f,1),new Color(.6f,.2f,.1f,1),new Color(.1f,.2f,.6f,1),new Color(.4f,.3f,.2f,1)};
                Material Surface(Color color)
                {
                    var m=Own(new Material(bake));m.SetTexture("_LTColorArray",white);m.SetVector("_LTLayerSlices0",Vector4.zero);
                    m.SetFloat("_LTBaseOnly",1);m.SetVector("_LTTiling0",Vector4.one);m.SetVector("_LTTint0",color);
                    m.SetVector("_LTSettings0",Vector4.zero);m.SetVector("_LTFlags0",Vector4.zero);
                    ShaderUtil.CompilePass(m,0,true);ShaderUtil.CompilePass(m,3,true);return m;
                }
                var area=new Rect(-8,-4,16,8);var tiles=new List<LTGrassTerrainMap.Tile>();
                for(int z=0;z<2;z++)for(int x=0;x<2;x++)
                {
                    int i=z*2+x;var rect=new Rect(-8+x*8,-4+z*4,8,4);var m=Surface(colors[i]);
                    m.SetVector("_LTRect",new Vector4(rect.x,rect.y,rect.width,rect.height));
                    tiles.Add(new LTGrassTerrainMap.Tile{id=i+1,revision=1,rect=rect,material=m,localToTerrain=Matrix4x4.identity,localToWorld=Matrix4x4.identity});
                }
                Require(!ShaderUtil.ShaderHasError(bake),string.Join("; ",ShaderUtil.GetShaderMessages(bake).Select(e=>e.message)));
                var probeShader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/TerrainSystem/LocalTerrain/Editor/LTGrassTerrainCheck.shader");
                Require(probeShader&&probeShader.isSupported,"Terrain color probe unavailable.");
                var probe=Own(new Material(probeShader));ShaderUtil.CompilePass(probe,0,true);
                Require(!ShaderUtil.ShaderHasError(probeShader),string.Join("; ",ShaderUtil.GetShaderMessages(probeShader).Select(e=>e.message)));
                Require(SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat),"ARGBFloat required for validation.");
                var target=Own(new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));target.Create();
                var read=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));var block=new MaterialPropertyBlock();int pixels=0;
                var transform=Matrix4x4.TRS(new Vector3(100,20,-50),Quaternion.Euler(0,37,0),new Vector3(2,1,1.5f));
                var albedo=new Color(.7f,.45f,.18f,.37f);
                Color Pixel(LTGrassTerrainMap cache,Vector3 local,float rest=0,float strength=1,bool raw=true)
                {
                    LTGrassTerrainMap.Bind(block,cache);probe.SetTexture("_Map",block.GetTexture(LTGrassTerrainMap.Map));
                    probe.SetFloat("_Ready",block.GetFloat(LTGrassTerrainMap.Ready));probe.SetVector("_U",block.GetVector(LTGrassTerrainMap.U));probe.SetVector("_V",block.GetVector(LTGrassTerrainMap.V));
                    probe.SetVector("_Root",transform.MultiplyPoint3x4(local));probe.SetVector("_Albedo",albedo);
                    probe.SetFloat("_Strength",strength);probe.SetFloat("_Height",.4f);probe.SetFloat("_Base",0);probe.SetFloat("_Rest",rest);probe.SetFloat("_ShowGround",raw?1:0);
                    GL.sRGBWrite=false;Graphics.Blit(Texture2D.whiteTexture,target,probe,0);RenderTexture.active=target;
                    read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();pixels++;return read.GetPixel(0,0);
                }
                void Near(Color a,Color b){for(int c=0;c<4;c++)Require(Mathf.Abs(a[c]-b[c])<.006f,$"Terrain pixel mismatch: {a} != {b}");}
                void Drain(LTGrassTerrainMap cache){for(int i=0;i<100&&cache.Pending>0;i++)cache.Advance(10,4);Require(cache.Pending==0&&cache.Texture,cache.Status);}
                var point=new Vector3(-4,0,-2);
                Near(Pixel(map,point,raw:false),albedo);
                map.Synchronize(area,transform.inverse,tiles,false);Require(map.Pending==4,map.Status);
                Near(Pixel(map,point,raw:false),albedo);map.Advance(10,1);
                Near(Pixel(map,point),colors[0]);Near(Pixel(map,new Vector3(4,0,2),raw:false),albedo);Drain(map);
                for(int i=0;i<4;i++)Near(Pixel(map,new Vector3(i%2==0?-4:4,0,i<2?-2:2)),colors[i]);
                Near(Pixel(map,point+Vector3.up*100),colors[0]);Near(Pixel(map,new Vector3(-20,0,-20),raw:false),albedo);
                foreach(float rest in new[]{0,.2f,.4f,1})foreach(float strength in new[]{0,.5f,1})
                {
                    float y=albedo.r*.2126f+albedo.g*.7152f+albedo.b*.0722f;
                    var ground=colors[0];float gy=ground.r*.2126f+ground.g*.7152f+ground.b*.0722f;
                    var tint=ground*(.5f+.5f*y/Mathf.Max(gy,.02f));for(int c=0;c<3;c++)tint[c]=Mathf.Clamp01(tint[c]);
                    float t=Mathf.Clamp01(rest/.4f),weight=strength*(1-t*t*(3-2*t));
                    var expected=Color.Lerp(albedo,tint,weight);expected.a=albedo.a;
                    Near(Pixel(map,point,rest,strength,false),expected);
                }
                int count=map.TilesRendered;map.Synchronize(area,transform.inverse,tiles,false);map.Advance();
                Require(map.Pending==0&&map.TilesRendered==count,"Unchanged map rebaked.");
                var changed=tiles[0];changed.revision++;changed.material.SetVector("_LTTint0",colors[1]);tiles[0]=changed;
                map.Synchronize(area,transform.inverse,tiles,false);Require(map.Pending==1,"Only the changed tile should update.");
                Near(Pixel(map,point),colors[0]);Drain(map);Near(Pixel(map,point),colors[1]);
                Require(map.TilesRendered==count+1,"Unchanged chunks were redrawn.");
                other.Synchronize(area,transform.inverse,new List<LTGrassTerrainMap.Tile>{tiles[2]},false);Drain(other);
                Near(Pixel(other,point,raw:false),albedo);Near(Pixel(map,point),colors[1]);
                tiles.RemoveAt(0);map.Synchronize(area,transform.inverse,tiles,false);Drain(map);Near(Pixel(map,point,raw:false),albedo);
                // Real weighted layers, height blend and road UVs must survive the
                // material copy into the automatic baker (not just base-only color).
                Texture2D Weight(Color value)
                {var tex=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));tex.SetPixel(0,0,value);tex.Apply();return tex;}
                var weights=Weight(new Color(.25f,.75f,0,0));var empty=Weight(Color.clear);
                var blended=Surface(colors[0]);blended.SetFloat("_LTBaseOnly",0);blended.SetFloat("_LTHeightBlend",0);
                blended.SetVector("_LTRect",new Vector4(area.x,area.y,area.width,area.height));
                blended.SetVector("_LTTint1",colors[1]);blended.SetVector("_LTTiling1",Vector4.one);
                blended.SetVector("_LTSettings1",Vector4.zero);blended.SetVector("_LTFlags1",Vector4.zero);
                blended.SetTexture("_LTWeights0",weights);blended.SetTexture("_LTWeights1",empty);blended.SetTexture("_LTWeights2",empty);
                var blendTile=new LTGrassTerrainMap.Tile{id=11,revision=1,rect=area,material=blended,localToTerrain=Matrix4x4.identity,localToWorld=Matrix4x4.identity};
                var blendTiles=new List<LTGrassTerrainMap.Tile>{blendTile};
                void RefreshBlend(){blendTile.revision++;blendTiles[0]=blendTile;map.Synchronize(area,transform.inverse,blendTiles,false);Drain(map);}
                RefreshBlend();Near(Pixel(map,point),Color.Lerp(colors[0],colors[1],.75f));
                weights.SetPixel(0,0,new Color(.75f,.25f,0,0));weights.Apply();RefreshBlend();Near(Pixel(map,point),Color.Lerp(colors[0],colors[1],.25f));
                blended.SetFloat("_LTHeightBlend",1);RefreshBlend();Near(Pixel(map,point),colors[0]);
                var stripes=Own(new Texture2DArray(2,2,1,TextureFormat.RGBA32,false,true));stripes.filterMode=FilterMode.Point;
                stripes.SetPixels(new[]{Color.red,Color.green,Color.red,Color.green},0);stripes.Apply();
                var road=Own(new Texture2DArray(257,257,1,TextureFormat.RGBAFloat,false,true));
                road.SetPixels(Enumerable.Repeat(new Color(.75f,.25f,1,0),257*257).ToArray(),0);road.Apply();
                weights.SetPixel(0,0,new Color(0,1,0,0));weights.Apply();blended.SetFloat("_LTHeightBlend",0);
                blended.SetTexture("_LTColorArray",stripes);blended.SetVector("_LTTint1",Color.white);
                blended.SetFloat("_LTRoadProjectionEnabled",1);blended.SetVector("_LTRoadProjectionSlots0",new Vector4(0,1,0,0));
                blended.SetTexture("_LTRoadProjectionMap",road);RefreshBlend();Near(Pixel(map,point),Color.green);
                road.SetPixels(Enumerable.Repeat(new Color(.25f,.25f,1,0),257*257).ToArray(),0);road.Apply();RefreshBlend();Near(Pixel(map,point),Color.red);
                // Triplanar path uses explicit LOD0 geometry, never the displayed LOD.
                var mesh=Own(new Mesh());mesh.vertices=new[]{new Vector3(-8,0,-4),new Vector3(-8,0,4),new Vector3(8,8,4),new Vector3(8,8,-4)};
                mesh.triangles=new[]{0,1,2,0,2,3};mesh.RecalculateNormals();mesh.tangents=Enumerable.Repeat(new Vector4(1,0,0,1),4).ToArray();
                var geometry=new LTGrassTerrainMap.Tile{id=9,revision=1,rect=area,material=Surface(colors[3]),mesh=mesh,localToTerrain=Matrix4x4.identity,localToWorld=transform};
                var tri=new List<LTGrassTerrainMap.Tile>{geometry};map.Synchronize(area,transform.inverse,tri,true);Drain(map);Near(Pixel(map,point),colors[3]);
                geometry.geometryRevision=2;tri[0]=geometry;map.Synchronize(area,transform.inverse,tri,true);Require(map.Pending==1,"Geometry update missed.");Drain(map);
                map.Synchronize(area,transform.inverse,new List<LTGrassTerrainMap.Tile>(),false);Require(!map.Texture,"Disabled cache leaked texture.");Near(Pixel(map,point,raw:false),albedo);
                Debug.Log($"[Grass Terrain Color v1] PASS: {passes} graph/indirect raster passes, {pixels} GPU pixels; real layer albedo/weight/height blending and spline UV bake, vertex-root sampling, transformed world, heights, sparse/deleted tiles, incremental updates, two maps, triplanar mesh, neutral strength and alpha. Only temporary resources; no scene/material assets changed. Live CPU/GPU instancing and performance are NOT verified.");
            }
            catch(Exception e){Debug.LogError("[Grass Terrain Color v1] FAIL: "+e);}
            finally
            {
                RenderTexture.active=previous;GL.sRGBWrite=srgb;GL.wireframe=wire;ShaderUtil.allowAsyncCompilation=async;
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
            }
        }
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    }
}
