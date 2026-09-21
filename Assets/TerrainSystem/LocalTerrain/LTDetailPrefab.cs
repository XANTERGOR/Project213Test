using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // Immutable-to-consumers recipe. No Instantiate, material cloning or asset mutation.
    public sealed class LTDetailPrefab
    {
        public sealed class Part
        {
            public Mesh mesh;
            public int submesh;
            public Material material;
            public Matrix4x4 localMatrix;
            public ShadowCastingMode castShadows;
            public bool receiveShadows;
            public int layer;
            public uint renderingLayerMask;
            public LightProbeUsage lightProbes;
            public ReflectionProbeUsage reflectionProbes;
            public MotionVectorGenerationMode motionVectors;
        }
        public sealed class Level
        {
            public float screenRelativeHeight;
            public readonly List<Part> parts=new List<Part>();
        }
        public readonly List<Level> levels=new List<Level>();
        public Bounds bounds;
        public float lodSize;
        public Vector3 lodReferencePoint;
        public LODFadeMode fadeMode;
        public Vector3 rootScale;
        public bool hasLODGroup;

        public static bool TryBuild(GameObject root,out LTDetailPrefab result,List<string> errors,List<string> warnings)
        {
            result=null;int initialErrors=errors.Count;
            if(!root){errors.Add("Префаб не назначен.");return false;}
            var recipe=new LTDetailPrefab{rootScale=root.transform.localScale};
            var groups=root.GetComponentsInChildren<LODGroup>(true);
            if(groups.Length>1||(groups.Length==1&&groups[0].gameObject!=root))
                errors.Add("Поддерживается один LODGroup на корне. Вложенные LODGroup пока не поддерживаются.");
            var renderers=root.GetComponentsInChildren<Renderer>(true);
            foreach(var renderer in renderers)
                if(!(renderer is MeshRenderer))errors.Add(renderer.name+": поддерживается только MeshRenderer.");
            if(root.GetComponentsInChildren<Collider>(true).Length>0)
                warnings.Add("Коллайдеры не копируются в статическую детализацию.");
            if(root.GetComponentsInChildren<MonoBehaviour>(true).Length>0)
                warnings.Add("Скрипты префаба не выполняются у инстансов.");
            bool hasBounds=false;
            var used=new HashSet<Renderer>();
            bool Active(Transform t)
            {
                while(t!=root.transform){if(!t.gameObject.activeSelf)return false;t=t.parent;}
                return true; // Root asset active flag does not disable the recipe.
            }
            void AddLevel(Renderer[] source,float threshold)
            {
                var level=new Level{screenRelativeHeight=threshold};recipe.levels.Add(level);
                var unique=new HashSet<Renderer>();
                foreach(var renderer in source)
                {
                    if(!renderer){errors.Add("LOD содержит пустую ссылку Renderer.");continue;}
                    if(!renderer.transform.IsChildOf(root.transform)){errors.Add("Renderer LOD вне префаба.");continue;}
                    used.Add(renderer);
                    if(!unique.Add(renderer)){errors.Add("Renderer повторяется внутри одного LOD.");continue;}
                    if(!renderer.enabled||!Active(renderer.transform))continue;
                    if(!(renderer is MeshRenderer))continue;
                    var filter=renderer.GetComponent<MeshFilter>();
                    if(!filter||!filter.sharedMesh){errors.Add(renderer.name+": нет MeshFilter/mesh.");continue;}
                    var mesh=filter.sharedMesh;var materials=renderer.sharedMaterials;
                    if(materials.Length!=mesh.subMeshCount){errors.Add(renderer.name+": число материалов должно совпадать с submesh.");continue;}
                    var matrix=root.transform.worldToLocalMatrix*renderer.transform.localToWorldMatrix;
                    var localBounds=mesh.bounds;
                    for(int corner=0;corner<8;corner++)
                    {
                        var p=matrix.MultiplyPoint3x4(localBounds.center+Vector3.Scale(localBounds.extents,
                            new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1)));
                        if(!hasBounds){recipe.bounds=new Bounds(p,Vector3.zero);hasBounds=true;}else recipe.bounds.Encapsulate(p);
                    }
                    for(int sub=0;sub<materials.Length;sub++)
                    {
                        var material=materials[sub];
                        if(!material){errors.Add(renderer.name+": пустой материал.");continue;}
                        if(!material.enableInstancing)warnings.Add(material.name+": GPU Instancing выключен; материал не изменён.");
                        level.parts.Add(new Part{mesh=mesh,submesh=sub,material=material,localMatrix=matrix,
                            castShadows=renderer.shadowCastingMode,receiveShadows=renderer.receiveShadows,
                            layer=renderer.gameObject.layer,renderingLayerMask=renderer.renderingLayerMask,
                            lightProbes=renderer.lightProbeUsage,reflectionProbes=renderer.reflectionProbeUsage,
                            motionVectors=renderer.motionVectorGenerationMode});
                    }
                }
                if(level.parts.Count==0)errors.Add("LOD не содержит поддерживаемых включённых частей.");
            }
            if(groups.Length==1&&groups[0].gameObject==root)
            {
                var group=groups[0];
                recipe.hasLODGroup=true;
                if(!group.enabled)errors.Add("LODGroup отключён: сначала задайте однозначную конфигурацию LOD.");
                recipe.lodSize=group.size;recipe.lodReferencePoint=group.localReferencePoint;recipe.fadeMode=group.fadeMode;
                var lods=group.GetLODs();
                if(lods.Length==0)errors.Add("LODGroup пуст.");
                float previous=1.01f;
                foreach(var lod in lods)
                {
                    if(lod.screenRelativeTransitionHeight>=previous)errors.Add("Пороги LOD должны убывать.");
                    previous=lod.screenRelativeTransitionHeight;AddLevel(lod.renderers,previous);
                }
                foreach(var renderer in renderers)
                    if(renderer.enabled&&Active(renderer.transform)&&!used.Contains(renderer))
                        errors.Add(renderer.name+": Renderer не назначен ни одному LOD.");
                if(group.fadeMode!=LODFadeMode.None)warnings.Add("LOD fade требует совместимого шейдера и рендерера; плавность пока не подтверждена.");
            }
            else if(groups.Length==0)
            {
                AddLevel(renderers,0);
                recipe.lodSize=recipe.bounds.size.magnitude;recipe.lodReferencePoint=recipe.bounds.center;
            }
            if(!hasBounds)errors.Add("Нет геометрии для детализации.");
            if(errors.Count!=initialErrors)return false;
            result=recipe;return true;
        }
    }
}
