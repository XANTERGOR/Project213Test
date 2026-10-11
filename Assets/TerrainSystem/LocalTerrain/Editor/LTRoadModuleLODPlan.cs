using System;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Resolve each module independently: mixed pools may contain authored and automatic LODs.
    internal sealed class LTRoadModuleLODPlan
    {
        internal LTRoadModuleVariant[] variants;
        internal Renderer[][][] prefabLevels;
        internal int count;
        internal string status;

        internal static LTRoadModuleLODPlan Create(LTRoad road)
        {
            var variants=road.moduleVariants??Array.Empty<LTRoadModuleVariant>();
            if(variants.Length>32)throw new InvalidOperationException("Максимум 32 варианта модуля.");
            if(variants.Any(v=>v==null||!LTRoadMesh.Finite(v.weight)||v.weight<0))throw new InvalidOperationException("Проверьте записи и веса набора модулей.");
            if(variants.Length==0)variants=new[]{new LTRoadModuleVariant{prefab=road.asphaltModulePrefab,mesh=road.asphaltModule,materials=road.moduleMaterials,lodMeshes=road.moduleLODMeshes}};
            else variants=variants.Where(v=>v.weight>0).ToArray();
            if(variants.Length==0)throw new InvalidOperationException("В наборе нет модулей с положительным весом.");
            var levels=variants.Select(v=>ReadPrefabLevels(v.prefab)).ToArray();
            int configured=road.asphaltLODs?.Length??0;
            if(configured>LTRoadLODPolicy.MaxAdditionalLevels)throw new InvalidOperationException("Асфальт поддерживает 0–7 дополнительных LOD.");
            int count=road.asphaltLODMode==LTRoadLODMode.Disabled?0:road.asphaltLODMode==LTRoadLODMode.Automatic?
                LTRoadLODPolicy.AutomaticCount(configured,levels.Select(l=>l?.Length??0).ToArray()):configured;
            string status=string.Join("\n",variants.Select((v,i)=>
                $"{i+1}. {(v.prefab?v.prefab.name:v.mesh?v.mesh.name:"Модуль")}: "+
                (road.asphaltLODMode==LTRoadLODMode.Disabled?"только LOD0":
                levels[i]!=null?$"LODGroup: {levels[i].Length} ур."+(road.asphaltLODMode==LTRoadLODMode.Automatic&&count>=levels[i].Length?"; дальше — последний LOD":""):
                road.asphaltLODMode==LTRoadLODMode.Authored?"готовые меши (Authored)":$"автоупрощение: {configured+1} ур."+(count>configured?"; дальше — последний LOD":""))));
            return new LTRoadModuleLODPlan{variants=variants,prefabLevels=levels,count=count,status=status};
        }

        internal static Renderer[][] ReadPrefabLevels(GameObject prefab,Func<Transform,bool> include=null)
        {
            if(!prefab)return null;
            var groups=prefab.GetComponentsInChildren<LODGroup>(true).Where(g=>include==null||include(g.transform)).ToArray();
            if(groups.Length==0)return null;
            if(groups.Length>1)throw new InvalidOperationException($"{prefab.name}: найдено несколько LODGroup. Для модуля нужен один общий LODGroup.");
            var lods=groups[0].GetLODs();int count=lods.Length;
            // An explicit empty tail is culling, not another road mesh. Never cull pavement.
            while(count>0&&(lods[count-1].renderers==null||lods[count-1].renderers.Length==0))count--;
            if(count==0||count>LTRoadLODPolicy.MaxAdditionalLevels+1)throw new InvalidOperationException($"{prefab.name}: нужны 1–8 непустых уровней LODGroup.");
            var result=new Renderer[count][];
            for(int l=0;l<count;l++)
            {
                var renderers=lods[l].renderers;
                if(renderers==null||renderers.Length==0)throw new InvalidOperationException($"{prefab.name}: LOD{l} пуст внутри цепочки LOD.");
                if(renderers.Any(r=>!r||!(r is MeshRenderer)||!r.GetComponent<MeshFilter>()||!r.GetComponent<MeshFilter>().sharedMesh||
                    (r.transform!=prefab.transform&&!r.transform.IsChildOf(prefab.transform))||(include!=null&&!include(r.transform))))
                    throw new InvalidOperationException($"{prefab.name}: LOD{l} должен ссылаться на статические меши внутри этого префаба.");
                if(renderers.Distinct().Count()!=renderers.Length)throw new InvalidOperationException($"{prefab.name}: повторный Renderer в LOD{l}.");
                result[l]=renderers;
            }
            // Ungrouped static parts are always visible in Unity; retain them at every level.
            var grouped=result.SelectMany(r=>r).ToArray();
            var constant=prefab.GetComponentsInChildren<MeshRenderer>(true).Where(r=>!grouped.Contains(r)&&(include==null||include(r.transform))).Cast<Renderer>().ToArray();
            if(constant.Length>0)for(int l=0;l<count;l++)result[l]=result[l].Concat(constant).ToArray();
            return result;
        }
    }
}
