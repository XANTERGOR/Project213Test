using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    public static class LTRoadGroundBlendBuild
    {
        // These failures occur before the baker changes renderers/meshes. Keep
        // the previous road visible while the user fixes material/mask settings.
        public sealed class Failure : InvalidOperationException
        {public Failure(Exception cause):base("Бленд грунта: "+cause.Message+" Предыдущая дорога сохранена.",cause){}}
        const string AuthoredShaderGuid="3413a129f9bb8e2469e149128b4f91bd";
        const string AuthoredShaderHash="3BF057C6AC6220262EC42D5ECFA2DF3A2F80BB1555FC06365BA4EC0E2ED61D30";
        static bool Authored(Material m)=>m&&m.shader&&m.shader.name=="RoadShader"&&AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(m.shader))==AuthoredShaderGuid;
        static bool Graph(Material m)=>m&&m.shader&&AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(m.shader))==LTRoadGraphAdapter.SourceGuid;
        public sealed class Prepared : IDisposable
        {
            public LTRoadBlendMask.Data mask;
            public Matrix4x4 roadToMask;
            public Material[] originals;
            public string key;
            public Texture2D texture;
            public Material[] materials;
            readonly List<Object> transient=new List<Object>();
            public T Own<T>(T o)where T:Object {transient.Add(o);return o;}
            public void Dispose(){foreach(var o in transient)if(o&&!EditorUtility.IsPersistent(o))Object.DestroyImmediate(o);transient.Clear();}
        }

        // A value snapshot shared by road and junction adapters; no temporary scene components.
        sealed class Settings
        {
            public Transform transform;
            public LTWorld World;
            public LTSurfaceLayer groundLayer;
            public bool AutomaticGroundBlend;
            public float groundBlendWidth,groundBlendHoleWidth,groundBlendStrength,groundBlendNoise,groundBlendNoiseSize,groundBlendMaskCell;
            public int groundBlendSeed;
            public Settings(LTRoad r)
            {
                transform=r.transform;World=r.World;groundLayer=r.groundLayer;AutomaticGroundBlend=r.AutomaticGroundBlend;
                groundBlendWidth=r.groundBlendWidth;groundBlendHoleWidth=r.groundBlendHoleWidth;groundBlendStrength=r.groundBlendStrength;groundBlendNoise=r.groundBlendNoise;groundBlendNoiseSize=r.groundBlendNoiseSize;groundBlendMaskCell=r.groundBlendMaskCell;groundBlendSeed=r.groundBlendSeed;
            }
            public Settings(LTRoadJunction r)
            {
                transform=r.transform;World=r.World;groundLayer=r.groundBlendLayer;AutomaticGroundBlend=r.AutomaticGroundBlend;
                groundBlendWidth=r.groundBlendWidth;groundBlendHoleWidth=r.groundBlendHoleWidth;groundBlendStrength=r.groundBlendStrength;groundBlendNoise=r.groundBlendNoise;groundBlendNoiseSize=r.groundBlendNoiseSize;groundBlendMaskCell=r.groundBlendMaskCell;groundBlendSeed=r.groundBlendSeed;
            }
        }
        static void Validate(Settings s,Material[] materials)
        {
            if(!s.World)throw new InvalidOperationException("Объект должен находиться внутри LTWorld.");
            if(!s.AutomaticGroundBlend&&!s.groundLayer)throw new InvalidOperationException("Назначьте слой для однослойного бленда.");
            if(s.AutomaticGroundBlend&&(!s.World.enableLayerPainting||!s.World.baseLayer))
                throw new InvalidOperationException("Автоматический бленд требует покраски terrain и базового слоя LTWorld.");
            bool In(float v,float min,float max)=>float.IsFinite(v)&&v>=min&&v<=max;
            if(!In(s.groundBlendWidth,0,10)||!In(s.groundBlendHoleWidth,0,10)||!In(s.groundBlendStrength,0,1)||
                !In(s.groundBlendNoise,0,1)||!In(s.groundBlendNoiseSize,.05f,100)||!In(s.groundBlendMaskCell,.01f,1))
                throw new InvalidOperationException("Проверьте ширину (0–10 м), силу/неровность (0–1), размер пятен и шаг маски бленда.");
            if(materials==null||materials.Length==0)throw new InvalidOperationException("Нет материалов асфальта для бленда.");
            foreach(var m in materials)if(!Supported(m,out string reason))
                throw new InvalidOperationException((m?m.name:"Материал")+": "+reason+". Исходный материал не изменён.");
        }
        public static Prepared Prepare(LTRoadJunction node,LTRoadJunctionPrefab.Prepared source,
            IReadOnlyDictionary<LTRoad,LTRoadModuleSource.Prepared> roads)
        {
            if(!node.groundBlend||!node.Authored||node.surface!=LTRoadMode.Asphalt)return null;
            try
            {
                Validate(new Settings(node),source.materials);
                var frame=Matrix4x4.TRS(node.transform.position,Quaternion.Euler(0,node.transform.eulerAngles.y,0),Vector3.one);
                var result=new Prepared{roadToMask=frame.inverse*node.transform.localToWorldMatrix,originals=source.materials};
                var coverage=new List<LTRoadMesh.Chunk>{source.levels[0]};
                var min=new Vector2(float.PositiveInfinity,float.PositiveInfinity);var max=-min;
                // All LODs stay safely inside the mask window, while the boundary is
                // taken only from LOD0 to avoid a changing rim on LOD transitions.
                foreach(var level in source.levels)foreach(var p in level.vertices)
                {var v=result.roadToMask.MultiplyPoint3x4(p);min=Vector2.Min(min,new Vector2(v.x,v.z));max=Vector2.Max(max,new Vector2(v.x,v.z));}
                float margin=Math.Max(node.groundBlendWidth,node.groundBlendHoleWidth)+4*node.groundBlendMaskCell;
                var window=new Vector4(min.x-margin,min.y-margin,max.x-min.x+2*margin,max.y-min.y+2*margin);
                foreach(var pair in roads.OrderBy(p=>p.Key.GetInstanceID()))
                {
                    var r=pair.Key;
                    if(!r||!r.isActiveAndEnabled||(r.startJunction!=node&&r.endJunction!=node))continue;
                    var m=node.transform.worldToLocalMatrix*r.transform.localToWorldMatrix;
                    foreach(var original in pair.Value.levels[0])
                    {
                        var c=new LTRoadMesh.Chunk{vertices=original.vertices.Select(m.MultiplyPoint3x4).ToArray(),
                            triangles=original.triangles,normals=original.normals,tangents=original.tangents,uv=original.uv};
                        c.hash=LTRoadMesh.ContentHash(c);coverage.Add(c);
                    }
                }
                result.mask=LTRoadBlendMask.Build(coverage,result.roadToMask,node.groundBlendMaskCell,window);
                result.key=Hash128.Compute("junction-ground-union-v1|"+string.Join("|",coverage.Select(LTRoadMesh.ContentHash))+"|"+
                    result.roadToMask.ToString("R")+"|"+window.ToString("R")+"|"+node.groundBlendMaskCell.ToString("R",CultureInfo.InvariantCulture)).ToString();
                return result;
            }
            catch(Exception e){throw new Failure(e);}
        }
        public static Prepared Prepare(LTRoad road,LTRoadModuleSource.Prepared source)
        {
            try{return PrepareCore(road,source);}catch(Exception e){throw new Failure(e);}
        }
        static Prepared PrepareCore(LTRoad road,LTRoadModuleSource.Prepared source)
        {
            if(!road.groundBlend||road.mode!=LTRoadMode.Asphalt)return null;
            if(!road.AutomaticGroundBlend&&!road.groundLayer)throw new InvalidOperationException("Для однослойного бленда назначьте «Слой terrain» в разделе «Покрытие».");
            if(road.AutomaticGroundBlend&&(!road.World.enableLayerPainting||!road.World.baseLayer))
                throw new InvalidOperationException("Автоматический бленд требует включённой покраски terrain и базового слоя LTWorld.");
            foreach(var m in source.materials)if(m&&!Supported(m,out string reason))
                throw new InvalidOperationException($"Бленд с грунтом: {m.name}: {reason}. Исходный материал не изменён.");
            if(!Resources.Load<Shader>("LTEightLayers"))throw new InvalidOperationException("Шейдер Local Terrain недоступен.");
            var frame=Matrix4x4.TRS(road.transform.position,Quaternion.Euler(0,road.transform.eulerAngles.y,0),Vector3.one);
            var result=new Prepared{roadToMask=frame.inverse*road.transform.localToWorldMatrix,originals=source.materials};
            var coverage=new List<LTRoadMesh.Chunk>(source.levels[0]);
            // A connected prefab entrance is not a dirt rim. Include its real LOD0
            // footprint, including holes, without changing the road's render mesh.
            foreach(var node in new[]{road.startJunction,road.endJunction}.Where(n=>n&&n.isActiveAndEnabled&&n.Authored).Distinct())
            {
                var junction=LTRoadJunctionPrefab.Build(node);var m=road.transform.worldToLocalMatrix*node.transform.localToWorldMatrix;
                var original=junction.levels[0];
                var c=new LTRoadMesh.Chunk{vertices=original.vertices.Select(m.MultiplyPoint3x4).ToArray(),triangles=original.triangles,
                    normals=original.normals,tangents=original.tangents,uv=original.uv};
                c.hash=LTRoadMesh.ContentHash(c);coverage.Add(c);
            }
            result.mask=LTRoadBlendMask.Build(coverage,result.roadToMask,road.groundBlendMaskCell);
            result.key=Hash128.Compute("road-ground-junction-union-v2|"+string.Join("|",coverage.Select(c=>c.hash))+"|"+
                result.roadToMask.ToString("R")+"|"+road.groundBlendMaskCell.ToString("R",CultureInfo.InvariantCulture)).ToString();
            return result;
        }
        public static bool Supported(Material m,out string reason)
        {
            if(Graph(m))
            {
                if(!LTRoadGraphAdapter.IsCurrent(out reason))return false;
                var graphShader=Resources.Load<Shader>("LTRoadGraphGround");
                if(!graphShader||ShaderUtil.ShaderHasError(graphShader))
                {reason="адаптер Road_Shader.shadergraph ещё не импортирован или содержит ошибки компиляции; проверьте Console";return false;}
                reason=null;return true;
            }
            if(Authored(m))
            {
                using(var file=File.OpenRead(AssetDatabase.GetAssetPath(m.shader)))using(var hash=SHA256.Create())
                {
                    if(BitConverter.ToString(hash.ComputeHash(file)).Replace("-","")!=AuthoredShaderHash)
                    {reason="RoadShader изменился; адаптер бленда нужно обновить, чтобы не потерять новые настройки";return false;}
                }
                reason=null;return true;
            }
            reason="шейдер "+(m&&m.shader?m.shader.name:"не назначен")+" не поддерживается блендом; поддерживаются RoadShader, Road_Shader.shadergraph этого проекта и HDRP/Lit";
            if(!m||!m.shader||m.shader.name!="HDRP/Lit")return false;
            reason="нужен непрозрачный HDRP/Lit с UV0, tangent-space normal, без detail/height/emission/alpha-test/особой модели освещения";
            if(m.GetFloat("_SurfaceType")>.5f||m.GetFloat("_UVBase")>.01f||m.GetFloat("_NormalMapSpace")>.5f||
                m.GetFloat("_MaterialID")!=1||m.GetFloat("_CoatMask")>0||m.GetFloat("_DoubleSidedEnable")>.5f||
                m.GetColor("_EmissiveColor").maxColorComponent>.001f)return false;
            foreach(string k in new[]{"_DETAIL_MAP","_HEIGHTMAP","_ALPHATEST_ON","_BENTNORMALMAP","_EMISSIVE_COLOR_MAP",
                "_MATERIAL_FEATURE_SUBSURFACE_SCATTERING","_MATERIAL_FEATURE_ANISOTROPY","_MATERIAL_FEATURE_SPECULAR_COLOR"})
                if(m.IsKeywordEnabled(k))return false;
            reason=null;return true;
        }
        public static Texture2D CreateTexture(LTRoadBlendMask.Data d)
        {
            if(d.width>SystemInfo.maxTextureSize||d.height>SystemInfo.maxTextureSize||!SystemInfo.SupportsTextureFormat(TextureFormat.RGHalf))
                throw new InvalidOperationException("GPU не поддерживает размер/формат маски бленда. Увеличьте шаг маски или разделите дорогу.");
            var t=new Texture2D(d.width,d.height,TextureFormat.RGHalf,false,true){name="Road rim distances",wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
            try
            {
                var pixels=new Color[d.exterior.Length];for(int i=0;i<pixels.Length;i++)pixels[i]=new Color(d.exterior[i],d.holes[i],0,1);
                t.SetPixels(pixels);t.Apply(false,true);return t;
            }
            catch{Object.DestroyImmediate(t);throw;}
        }
        public static Material CreateMaterial(LTRoad road,Prepared p,Material source,Texture2D mask)
            =>CreateMaterial(new Settings(road),p,source,mask);
        public static Material CreateMaterial(LTRoadJunction node,Prepared p,Material source,Texture2D mask)
            =>CreateMaterial(new Settings(node),p,source,mask);
        static Material CreateMaterial(Settings road,Prepared p,Material source,Texture2D mask)
        {
            if(!Supported(source,out string reason))throw new InvalidOperationException(reason);
            bool authored=Authored(source),graph=Graph(source);
            var shader=Resources.Load<Shader>(graph?"LTRoadGraphGround":authored?"LTRoadAuthoredGround":"LTEightLayers");
            if(!shader)throw new InvalidOperationException("Шейдер бленда дороги недоступен.");
            var m=new Material(source){name=source.name+" / Road Ground",shader=shader};
            if(!authored&&!graph)
            {
                m.DisableKeyword("_LT_ROCK_SURFACE");m.DisableKeyword("_LT_FAR_ONLY");m.EnableKeyword("_LT_ROAD_BLEND");
                m.SetVector("_LTGlobalParams",Vector4.zero);m.SetVector("_LTRockFarParams",Vector4.zero);
                m.SetFloat("_LTDebugCoverage",0);m.SetFloat("_LTRoadProjectionEnabled",0);
            }
            m.SetTexture("_LTRoadBlendDistances",mask);m.SetVector("_LTRoadBlendRect",p.mask.rect);
            m.SetFloat("_LTRoadBlendSource",road.AutomaticGroundBlend?1:0);
            if(road.AutomaticGroundBlend)m.EnableKeyword("_LT_ROAD_TERRAIN");else m.DisableKeyword("_LT_ROAD_TERRAIN");
            m.SetVector("_LTRoadBlendParams",new Vector4(road.groundBlendWidth,road.groundBlendHoleWidth,road.groundBlendStrength,road.groundBlendNoise));
            m.SetVector("_LTRoadBlendNoise",new Vector4(road.groundBlendNoiseSize,road.groundBlendSeed&0xffffff,0,0));
            m.SetMatrix("_LTRoadBlendWorldToMask",p.roadToMask*road.transform.worldToLocalMatrix);
            m.SetMatrix("_LTSurfaceWorldToLocal",road.World.transform.worldToLocalMatrix);
            var layer=road.AutomaticGroundBlend?road.World.baseLayer:road.groundLayer;var size=layer.tileSizeMetres;
            size.x=Math.Max(.001f,size.x);size.y=Math.Max(.001f,size.y);
            m.SetVector("_LTRoadBlendTiling",new Vector4(size.x,size.y,layer.tileOffsetMetres.x/size.x,layer.tileOffsetMetres.y/size.y));
            m.SetVector("_LTRoadBlendTint",layer.tint.linear);
            m.SetVector("_LTRoadBlendSettings",new Vector4(layer.normalStrength,layer.metallic,layer.smoothness,layer.maskMap?1:0));
            bool triplanar=road.World.triplanarTexturing;
            m.SetVector("_LTRoadBlendFlags",new Vector4(layer.normalMap?1:0,layer.aoStrength,triplanar?1:0,0));
            m.SetTexture("_LTRoadBlendColor",layer.baseColorMap?layer.baseColorMap:Texture2D.whiteTexture);
            m.SetTexture("_LTRoadBlendNormal",layer.normalMap?layer.normalMap:Texture2D.normalTexture);
            m.SetTexture("_LTRoadBlendMask",layer.maskMap?layer.maskMap:Texture2D.whiteTexture);
            if(authored||graph)return m;
            var normal=source.IsKeywordEnabled("_NORMALMAP")?source.GetTexture("_NormalMap"):null;
            var ownMask=source.IsKeywordEnabled("_MASKMAP")?source.GetTexture("_MaskMap"):null;
            m.SetTexture("_LTRockColorMap",source.GetTexture("_BaseColorMap")??Texture2D.whiteTexture);
            m.SetTexture("_LTRockNormalMap",normal?normal:Texture2D.normalTexture);m.SetTexture("_LTRockMaskMap",ownMask?ownMask:Texture2D.whiteTexture);
            m.SetVector("_LTRockTint",source.GetColor("_BaseColor").linear);
            var scale=source.GetTextureScale("_BaseColorMap");var offset=source.GetTextureOffset("_BaseColorMap");
            m.SetVector("_LTRockUV",new Vector4(scale.x,scale.y,offset.x,offset.y));
            m.SetVector("_LTRockSettings",new Vector4(source.GetFloat("_NormalScale"),source.GetFloat("_Smoothness"),source.GetFloat("_Metallic"),source.GetFloat("_MetallicRemapMin")));
            m.SetVector("_LTRockFlags",new Vector4(1,normal?1:0,ownMask?1:0,source.GetFloat("_MetallicRemapMax")));
            m.SetVector("_LTRockRemap",new Vector4(source.GetFloat("_AORemapMin"),source.GetFloat("_AORemapMax"),source.GetFloat("_SmoothnessRemapMin"),source.GetFloat("_SmoothnessRemapMax")));
            return m;
        }
        // Content-addressed owned copies preserve prior states for Undo and never
        // overwrite imported materials. One map/material palette per road, not per LOD.
        public static void Save(LTRoad road,Prepared p,string folder)
        {
            try{SaveCore(new Settings(road),p,folder);}catch(Exception e){throw new Failure(e);}
        }
        public static void Save(LTRoadJunction node,Prepared p,string folder)
        {
            try{SaveCore(new Settings(node),p,folder);}catch(Exception e){throw new Failure(e);}
        }
        static void SaveCore(Settings road,Prepared p,string folder)
        {
            string path=folder+"/GroundMask_"+p.key+".asset";
            p.texture=AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if(!p.texture)
            {
                p.texture=p.Own(CreateTexture(p.mask));AssetDatabase.CreateAsset(p.texture,AssetDatabase.GenerateUniqueAssetPath(path));
                AssetDatabase.SaveAssetIfDirty(p.texture);
            }
            p.materials=new Material[p.originals.Length];
            for(int i=0;i<p.materials.Length;i++)
            {
                var m=p.Own(CreateMaterial(road,p,p.originals[i],p.texture));
                // Matrix fields are updated by the generated binding after reload.
                string key=Hash128.Compute(p.key+"|"+EditorJsonUtility.ToJson(m)+"|"+m.ComputeCRC()).ToString();
                path=folder+"/GroundMaterial_"+key+".mat";
                p.materials[i]=AssetDatabase.LoadAssetAtPath<Material>(path);
                if(!p.materials[i]){AssetDatabase.CreateAsset(m,AssetDatabase.GenerateUniqueAssetPath(path));AssetDatabase.SaveAssetIfDirty(m);p.materials[i]=m;}
            }
        }
        public static string DependencyKey(LTRoad road)
        {
            if(!road.groundBlend)return "";
            var objects=new List<Object>{road.groundLayer,road.asphaltMaterial};
            if(road.groundLayer)objects.AddRange(new Object[]{road.groundLayer.baseColorMap,road.groundLayer.normalMap,road.groundLayer.maskMap});
            objects.AddRange(road.moduleMaterials??Array.Empty<Material>());
            foreach(var v in road.moduleVariants??Array.Empty<LTRoadModuleVariant>())if(v!=null)objects.AddRange(v.materials??Array.Empty<Material>());
            // Unsaved prefab material edits must invalidate the generated copies too.
            void PrefabMaterials(GameObject prefab)
            {if(prefab)foreach(var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))objects.AddRange(renderer.sharedMaterials);}
            PrefabMaterials(road.asphaltModulePrefab);
            foreach(var v in road.moduleVariants??Array.Empty<LTRoadModuleVariant>())if(v!=null)PrefabMaterials(v.prefab);
            return "|ground-blend-v1|"+(road.World&&road.World.triplanarTexturing)+"|"+(road.groundLayer?road.groundLayer.SurfaceHash():0)+"|"+string.Join("|",objects.Select(o=>o?o.GetInstanceID()+":"+EditorUtility.GetDirtyCount(o)+":"+(o is Material m?m.ComputeCRC():0)+":"+AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(o)):"null"));
        }
        public static string DependencyKey(LTRoadJunction node)
        {
            if(!node.groundBlend||!node.Authored)return "";
            var world=node.World;var layer=node.AutomaticGroundBlend&&world?world.baseLayer:node.groundBlendLayer;
            var objects=new List<Object>{layer,node.asphaltMaterial};
            if(layer)objects.AddRange(new Object[]{layer.baseColorMap,layer.normalMap,layer.maskMap});
            var values=new[]{node.groundBlendWidth,node.groundBlendHoleWidth,node.groundBlendStrength,node.groundBlendNoise,
                node.groundBlendNoiseSize,node.groundBlendMaskCell};
            return "|junction-ground-v1|"+node.groundBlendSource+"|"+node.groundBlendSeed+"|"+
                string.Join(",",values.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)))+"|"+
                (world&&world.triplanarTexturing)+"|"+(world&&world.enableLayerPainting)+"|"+(layer?layer.SurfaceHash():0)+"|"+
                string.Join("|",objects.Select(o=>o?o.GetInstanceID()+":"+EditorUtility.GetDirtyCount(o)+":"+(o is Material m?m.ComputeCRC():0)+":"+AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(o)):"null"));
        }
    }
}
