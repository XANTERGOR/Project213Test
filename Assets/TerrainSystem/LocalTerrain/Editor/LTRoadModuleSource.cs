using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
namespace LocalTerrainPrototype
{
    public static class LTRoadModuleSource
    {
        sealed class SourceCache { public LTRoad road;public string key;public LTRoadMesh.Chunk[][] variants;public Material[] materials;public string fitStatus; }
        static readonly Dictionary<int,SourceCache> sourceCache=new Dictionary<int,SourceCache>();
        internal static void Forget(LTRoad road){if(road)sourceCache.Remove(road.GetInstanceID());}
        public sealed class Prepared { public List<LTRoadMesh.Chunk>[] levels; public Material[] materials; public string status; }
        public static Prepared Build(LTRoad road,LTRoadMath.Snapshot snapshot)
            =>BuildCore(road,snapshot,false);
        // Owned preview fixtures only; the public bake path still rejects scene objects.
        internal static Prepared BuildPreview(LTRoad road,LTRoadMath.Snapshot snapshot)
        {
            if(!UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(road.gameObject.scene))
                throw new InvalidOperationException("Transient module validation requires an owned preview scene.");
            return BuildCore(road,snapshot,true);
        }
        static Prepared BuildCore(LTRoad road,LTRoadMath.Snapshot snapshot,bool preview)
        {
            if(road.asphaltLODs==null||road.asphaltLODs.Length>LTRoadLODPolicy.MaxAdditionalLevels)throw new InvalidOperationException("Асфальт поддерживает 0–7 дополнительных LOD.");
            var plan=road.asphaltSource==LTRoadMeshSource.Module?LTRoadModuleLODPlan.Create(road):null;
            int count=plan?.count??(road.asphaltLODMode==LTRoadLODMode.Disabled?0:road.asphaltLODs.Length);
            foreach(var l in road.asphaltLODs)if(l==null||!LTRoadMesh.Finite(l.maxHeightError)||l.maxHeightError<0)throw new InvalidOperationException("Ошибка упрощения LOD должна быть конечной и неотрицательной.");
            for(int l=1;l<=count;l++)if(float.IsNaN(LTRoadLODPolicy.Distance(road.asphaltLODs,l)))
                throw new InvalidOperationException("Дистанции LOD должны быть конечными, неотрицательными и возрастающими.");
            var matrix=road.transform.worldToLocalMatrix*road.World.transform.localToWorldMatrix;
            var result=new Prepared{levels=new List<LTRoadMesh.Chunk>[count+1]};
            string fitStatus=null;
            if(road.asphaltSource==LTRoadMeshSource.ProceduralRibbon)
            {
                result.materials=new[]{road.asphaltMaterial};
                result.levels[0]=LTRoadMesh.Build(snapshot,road.meshChunkLength,road.textureRepeatMetres,road.surfaceOffset,matrix);
                for(int l=1;l<=count;l++)result.levels[l]=result.levels[0].Select(c=>LTRoadModuleMath.RibbonLOD(c,l)).ToList();
            }
            else
            {
                var materials=new List<Material>();
                var variants=plan.variants;
                foreach(var v in variants)if(v.prefab&&!EditorUtility.IsPersistent(v.prefab)&&
                    !(preview&&v.prefab.scene==road.gameObject.scene))
                    throw new InvalidOperationException("Назначьте ассет префаба модуля из Project, не объект сцены.");
                var weights=variants.Select(v=>v.weight).ToArray();
                string key=DependencyKey(road)+"|"+road.moduleAxis+"|"+road.asphaltLODMode+"|"+count+"|"+
                    (road.asphaltMaterial?road.asphaltMaterial.GetInstanceID():0)+"|"+string.Join(",",(road.moduleMaterials??Array.Empty<Material>()).Select(m=>m?m.GetInstanceID():0))+"|"+
                    string.Join(",",road.asphaltLODs.Select(l=>l.maxHeightError.ToString("R",System.Globalization.CultureInfo.InvariantCulture)))+"|"+
                    string.Join(";",variants.Select(v=>string.Join(",",(v.materials??Array.Empty<Material>()).Select(m=>m?m.GetInstanceID():0))))+"|"+
                    string.Join(",",(road.moduleVariants??Array.Empty<LTRoadModuleVariant>()).Select(v=>v.weight>0?1:0))+"|"+
                    (road.moduleFitEnds?"fit:"+road.moduleEndBlendLength.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":"+
                        road.moduleEndMaxMismatch.ToString("R",System.Globalization.CultureInfo.InvariantCulture):"strict")+"|"+
                    road.moduleRestoreLODEnds+":"+road.moduleLODEndWidth.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"|joints:"+
                    road.moduleAutomaticJoints+":"+road.moduleJointWidth.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":"+
                    road.moduleJointMaxChange.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":"+road.moduleJointPrecision.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"|fuse:"+
                    road.moduleFuseEnds+":"+road.moduleFuseTolerance.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+":"+
                    road.moduleFusePlaneTolerance.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
                foreach(int dead in sourceCache.Where(p=>!p.Value.road).Select(p=>p.Key).ToArray())sourceCache.Remove(dead);
                LTRoadMesh.Chunk ReadLevel(int variantIndex,int level)
                {
                    var variant=variants[variantIndex];
                    var pieces=new List<(Mesh mesh,Matrix4x4 matrix,Material[] mats)>();
                    Matrix4x4 axis=road.moduleAxis==LTRoadModuleAxis.X?Matrix4x4.Rotate(Quaternion.Euler(0,-90,0)):Matrix4x4.identity;
                    if(variant.prefab)
                    {
                        var root=variant.prefab.transform;var levels=plan.prefabLevels[variantIndex];
                        Renderer[] renderers;
                        if(levels!=null)
                        {if(level>=levels.Length)throw new InvalidOperationException($"В префабе нет LOD{level}.");renderers=levels[level];}
                        else
                        {if(level>0)throw new InvalidOperationException("Для Authored назначьте префаб с LODGroup или отдельные меши LOD.");renderers=variant.prefab.GetComponentsInChildren<MeshRenderer>(true);}
                        foreach(var renderer in renderers)
                        {
                            var filter=renderer?renderer.GetComponent<MeshFilter>():null;
                            if(!filter||!filter.sharedMesh)throw new InvalidOperationException("Модуль поддерживает только статические MeshRenderer с MeshFilter.");
                            pieces.Add((filter.sharedMesh,axis*root.worldToLocalMatrix*filter.transform.localToWorldMatrix,renderer.sharedMaterials));
                        }
                    }
                    else
                    {
                        Mesh mesh=level==0?variant.mesh:(variant.lodMeshes!=null&&level<=variant.lodMeshes.Length?variant.lodMeshes[level-1]:null);
                        if(!mesh)throw new InvalidOperationException($"Назначьте меш модуля LOD{level} либо префаб.");
                        pieces.Add((mesh,axis,variant.materials??Array.Empty<Material>()));
                    }
                    var vertices=new List<Vector3>();var normals=new List<Vector3>();var tangents=new List<Vector4>();
                    var uv=new List<Vector2>();var uv2=new List<Vector2>();var uv3=new List<Vector2>();var uv4=new List<Vector2>();var colors=new List<Color>();var sub=new List<List<int>>();
                    bool hasUV2=false,hasUV3=false,hasUV4=false,hasColors=false;
                    foreach(var piece in pieces)
                    {
                        var mesh=piece.mesh;
                        if(!mesh.isReadable)throw new InvalidOperationException($"Модуль {mesh.name}: включите Read/Write в Import Settings модели.");
                        if(mesh.blendShapeCount>0)throw new InvalidOperationException("Blend Shapes модуля не поддерживаются; используйте статическую копию.");
                        for(int channel=0;channel<8;channel++)
                        {
                            var attribute=(UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0+channel);
                            if(mesh.HasVertexAttribute(attribute)&&(channel>=4||mesh.GetVertexAttributeDimension(attribute)!=2))
                                throw new InvalidOperationException($"{mesh.name}: поддерживаются двумерные UV0–UV3. Канал UV{channel} не будет отброшен молча — подготовьте совместимую копию модуля.");
                        }
                        var v=mesh.vertices;var n=mesh.normals;var t=mesh.tangents;var u=mesh.uv;var u2=mesh.uv2;var u3=mesh.uv3;var u4=mesh.uv4;var color=mesh.colors;
                        if(v.Length==0||n.Length!=v.Length||t.Length!=v.Length||u.Length!=v.Length)throw new InvalidOperationException($"{mesh.name}: нужны вершины, нормали, тангенты и UV0. Включите импорт/расчёт нормалей и тангентов.");
                        if(piece.matrix.determinant<=0)throw new InvalidOperationException("Отражённый или нулевой масштаб внутри префаба модуля не поддерживается.");
                        hasUV2|=u2.Length==v.Length;hasUV3|=u3.Length==v.Length;hasUV4|=u4.Length==v.Length;hasColors|=color.Length==v.Length;
                        int start=vertices.Count;var normalMatrix=piece.matrix.inverse.transpose;
                        for(int i=0;i<v.Length;i++)
                        {
                            var p=piece.matrix.MultiplyPoint3x4(v[i]);var normal=normalMatrix.MultiplyVector(n[i]).normalized;
                            var tangent=piece.matrix.MultiplyVector(new Vector3(t[i].x,t[i].y,t[i].z));tangent=(tangent-normal*Vector3.Dot(normal,tangent)).normalized;
                            if(!LTRoadMesh.Finite(p)||!LTRoadMesh.Finite(normal)||!LTRoadMesh.Finite(tangent))throw new InvalidOperationException("Модуль содержит NaN/Infinity.");
                            vertices.Add(p);normals.Add(normal);tangents.Add(new Vector4(tangent.x,tangent.y,tangent.z,t[i].w));uv.Add(u[i]);
                            uv2.Add(u2.Length==v.Length?u2[i]:Vector2.zero);uv3.Add(u3.Length==v.Length?u3[i]:Vector2.zero);uv4.Add(u4.Length==v.Length?u4[i]:Vector2.zero);colors.Add(color.Length==v.Length?color[i]:Color.white);
                        }
                        for(int s=0;s<mesh.subMeshCount;s++)
                        {
                            if(mesh.GetTopology(s)!=MeshTopology.Triangles)throw new InvalidOperationException("Модуль должен содержать треугольники.");
                            Material material=road.asphaltMaterial?road.asphaltMaterial:(s<piece.mats.Length?piece.mats[s]:null);
                            if(!material)throw new InvalidOperationException($"Для {mesh.name}, submesh {s} не назначен материал.");
                            int slot=materials.IndexOf(material);if(slot<0){slot=materials.Count;materials.Add(material);}
                            while(sub.Count<=slot)sub.Add(new List<int>());foreach(int id in mesh.GetTriangles(s))sub[slot].Add(start+id);
                        }
                    }
                    if(vertices.Count==0)throw new InvalidOperationException("В модуле нет мешей.");
                    while(sub.Count<materials.Count)sub.Add(new List<int>());
                    return new LTRoadMesh.Chunk{vertices=vertices.ToArray(),normals=normals.ToArray(),tangents=tangents.ToArray(),uv=uv.ToArray(),
                        uv2=hasUV2?uv2.ToArray():null,uv3=hasUV3?uv3.ToArray():null,uv4=hasUV4?uv4.ToArray():null,colors=hasColors?colors.ToArray():null,
                        submeshes=sub.Select(s=>s.ToArray()).ToArray(),triangles=sub.SelectMany(s=>s).ToArray()};
                }
                LTRoadMesh.Chunk[][] allSources;
                if(sourceCache.TryGetValue(road.GetInstanceID(),out var cached)&&cached.road==road&&cached.key==key)
                {allSources=cached.variants;materials.AddRange(cached.materials);fitStatus=cached.fitStatus;}
                else
                {
                    allSources=new LTRoadMesh.Chunk[variants.Length][];LTRoadModuleEnds.Plan fit=null;float commonMin=0,commonMax=0;
                    LTRoadModuleCollar.AutomaticPlan joints=null;
                    LTRoadModuleFuse.Plan fuse=null;
                    LTRoadMesh.Chunk Fuse(LTRoadMesh.Chunk input,int variant,int level)
                    {
                        var output=LTRoadModuleFuse.Apply(input,fuse,out int moved,out float maxMove);
                        fitStatus+=$"\nМодуль {variant+1}, LOD{level}: Fuse, {input.triangles.Length/3} tris без изменений; сдвинуто {moved} вершин, максимум {maxMove*1000:G4} мм.";
                        return output;
                    }
                    for(int v=0;v<variants.Length;v++)
                    {
                        try
                        {
                            var sources=allSources[v]=new LTRoadMesh.Chunk[count+1];sources[0]=ReadLevel(v,0);
                            float zMin=sources[0].vertices.Min(p=>p.z),zMax=sources[0].vertices.Max(p=>p.z);
                            if(v==0){commonMin=zMin;commonMax=zMax;}
                            sources[0]=LTRoadModuleMath.NormalizeLength(sources[0],zMin,zMax,commonMin,commonMax);
                            if(road.moduleFuseEnds)
                            {
                                if(v==0)
                                {
                                    fuse=LTRoadModuleFuse.Create(sources[0],road.moduleFuseTolerance,road.moduleFusePlaneTolerance);
                                    fitStatus=$"Fuse торцов: общий край {fuse.PointCount} точек; допуск {road.moduleFuseTolerance*1000:G4} мм, полоса поиска {road.moduleFusePlaneTolerance*1000:G4} мм. Без срезов и переходных полос; UV/нормали/треугольники сохранены.";
                                }
                                sources[0]=Fuse(sources[0],v,0);
                            }
                            else if(road.moduleAutomaticJoints)
                            {
                                if(v==0)
                                {
                                    joints=LTRoadModuleCollar.CreateAutomatic(sources[0],road.moduleJointWidth,road.moduleJointMaxChange,road.moduleJointPrecision);
                                    fitStatus=$"Автоматические стыки: общий профиль {joints.PointCount} точек; зона {road.moduleJointWidth:G3} м; допуск формы {road.moduleJointMaxChange:G3} м; точность {road.moduleJointPrecision:G3} м.";
                                }
                                int original=sources[0].triangles.Length/3;
                                sources[0]=LTRoadModuleCollar.ApplyAutomatic(sources[0],joints);
                                fitStatus+=$"\nМодуль {v+1}, LOD0: {original} → {sources[0].triangles.Length/3} tris. Перестроены только концы.";
                            }
                            else if(road.moduleFitEnds)
                            {
                                if(v==0)
                                {
                                    fit=LTRoadModuleEnds.Create(sources[0],road.moduleEndBlendLength,road.moduleEndMaxMismatch);
                                    fitStatus=$"Общий профиль торцов: {fit.PointCount} точек; разница первого модуля {fit.Mismatch*1000:F1} мм; сглаживание {fit.BlendLength:G3} м.";
                                }
                                sources[0]=LTRoadModuleEnds.Apply(sources[0],fit);
                            }
                            for(int l=1;l<=count;l++)
                            {
                                bool automatic=road.asphaltLODMode==LTRoadLODMode.Automatic;
                                int prefabCount=plan.prefabLevels[v]?.Length??0;
                                int sourceLevel=automatic?LTRoadLODPolicy.SourceLevel(l,prefabCount>0?prefabCount:road.asphaltLODs.Length+1):l;
                                if(sourceLevel<l){sources[l]=sources[sourceLevel];continue;}
                                if(!automatic||prefabCount>0)
                                {
                                    try
                                    {
                                        sources[l]=LTRoadModuleMath.NormalizeLength(ReadLevel(v,sourceLevel),zMin,zMax,commonMin,commonMax,road.moduleRestoreLODEnds||road.moduleAutomaticJoints||road.moduleFuseEnds);
                                        if(road.moduleFuseEnds)sources[l]=Fuse(sources[l],v,l);
                                        else if(road.moduleAutomaticJoints)
                                        {
                                            int original=sources[l].triangles.Length/3;
                                            sources[l]=LTRoadModuleCollar.ApplyAutomatic(sources[l],joints);
                                            fitStatus+=$"\nМодуль {v+1}, LOD{l}: общий стык; {original} → {sources[l].triangles.Length/3} tris. Середина авторского LOD сохранена.";
                                        }
                                        else if(road.moduleRestoreLODEnds)
                                        {
                                            int original=sources[l].triangles.Length/3;
                                            sources[l]=LTRoadModuleCollar.Prepare(sources[0],sources[l],fit,road.moduleLODEndWidth,out bool restored);
                                            if(restored)fitStatus+=$"\nМодуль {v+1}, LOD{l}: торцы LOD0, зона {road.moduleLODEndWidth:G3} м; {original} → {sources[l].triangles.Length/3} tris. Середина авторского LOD сохранена.";
                                        }
                                        else if(fit!=null)sources[l]=LTRoadModuleEnds.Apply(sources[l],fit);
                                    }
                                    catch(InvalidOperationException e){throw new InvalidOperationException($"LOD{l}: "+e.Message,e);}
                                }
                                else sources[l]=LTRoadModuleMath.Simplify(sources[0],1f/(1<<l),road.asphaltLODs[l-1].maxHeightError);
                            }
                        }
                        catch(InvalidOperationException e){throw new InvalidOperationException($"Модуль {v+1} ({(variants[v].prefab?variants[v].prefab.name:variants[v].mesh?variants[v].mesh.name:"не назначен")}): "+e.Message,e);}
                    }
                    // Bound retained source geometry; no bent-road output is cached here.
                    if(sourceCache.Count>=8)sourceCache.Clear();
                    sourceCache[road.GetInstanceID()]=new SourceCache{road=road,key=key,variants=allSources,materials=materials.ToArray(),fitStatus=fitStatus};
                }
                var reference=allSources[0][0];
                Vector3 min=reference.vertices[0],max=min;foreach(var v in reference.vertices){min=Vector3.Min(min,v);max=Vector3.Max(max,v);}
                var front=new HashSet<Vector2>(reference.vertices.Where(v=>Math.Abs(v.z-min.z)<.00001f).Select(v=>new Vector2(v.x,v.y)));
                var back=new HashSet<Vector2>(reference.vertices.Where(v=>Math.Abs(v.z-max.z)<.00001f).Select(v=>new Vector2(v.x,v.y)));
                if(!road.moduleFuseEnds&&!front.SetEquals(back))throw new InvalidOperationException("Торцы модуля различаются. Для близких профилей включите «Автоподгонка торцов»; иначе подготовьте одинаковые сечения X/Y на обоих концах.");
                var ends=new HashSet<Vector3>(reference.vertices.Where(v=>Math.Abs(v.z-min.z)<.00001f||Math.Abs(v.z-max.z)<.00001f));
                for(int v=0;v<allSources.Length;v++)for(int l=0;l<=count;l++)
                {
                    var sources=allSources[v];
                    var other=new HashSet<Vector3>(sources[l].vertices.Where(p=>Math.Abs(p.z-min.z)<.00001f||Math.Abs(p.z-max.z)<.00001f));
                    if(!road.moduleFuseEnds&&!ends.SetEquals(other))throw new InvalidOperationException($"Модуль {v+1}, LOD{l}: торцы ВСЕХ модулей должны совпадать с первым модулем LOD0. Включите автоподгонку для близких профилей.");
                    if(sources[l].vertices.Any(p=>p.z<min.z-.00001f||p.z>max.z+.00001f))throw new InvalidOperationException($"LOD{l}: длина выходит за границы LOD0.");
                    if(road.moduleFuseEnds)LTRoadModuleFuse.ValidateRims(reference,sources[l]);
                    else if(allSources.Length>1)LTRoadModuleEnds.ValidateRims(reference,sources[l]);
                }
                // Match end shading across authored LODs as well as across repeats.
                var endFrames=new Dictionary<(Vector3,Vector2),(Vector3,Vector4)>();
                for(int i=0;i<reference.vertices.Length;i++)if(ends.Contains(reference.vertices[i]))
                    endFrames[(reference.vertices[i],reference.uv[i])]=(reference.normals[i],reference.tangents[i]);
                foreach(var sources in allSources)for(int l=0;l<=count;l++)for(int i=0;i<sources[l].vertices.Length;i++)
                    if(!road.moduleAutomaticJoints&&!road.moduleFuseEnds&&!ReferenceEquals(sources[l],reference)&&endFrames.TryGetValue((sources[l].vertices[i],sources[l].uv[i]),out var frame))
                    {sources[l].normals[i]=frame.Item1;sources[l].tangents[i]=frame.Item2;}
                for(int l=0;l<=count;l++)
                {
                    foreach(var sources in allSources)
                    {
                    // Material palette is shared by all levels, including absent slots.
                    if(sources[l].submeshes.Length<materials.Count)
                    {var subs=sources[l].submeshes;Array.Resize(ref subs,materials.Count);for(int i=0;i<subs.Length;i++)if(subs[i]==null)subs[i]=Array.Empty<int>();sources[l].submeshes=subs;}
                    }
                    result.levels[l]=LTRoadModuleMath.BendVariants(allSources.Select(s=>s[l]).ToArray(),weights,road.moduleRandomSeed,road.moduleAvoidRepeat,
                        snapshot,min,max,road.moduleLength,road.moduleFitWidth,road.moduleBaseY,road.surfaceOffset,road.meshChunkLength,matrix);
                }
                result.materials=materials.ToArray();
                if(variants.Length>1)fitStatus+=$"\nСлучайный набор: {variants.Length} модулей; seed {road.moduleRandomSeed}.";
                fitStatus+="\n"+plan.status;
            }
            result.status=string.Join(" / ",result.levels.Select((chunks,l)=>$"LOD{l}: {chunks.Sum(c=>(long)c.triangles.Length/3):N0} tris"));
            if(fitStatus!=null)result.status+="\n"+fitStatus;
            return result;
        }
        public static string DependencyKey(LTRoad road)
        {
            var ids=new List<UnityEngine.Object>{road.asphaltModule,road.asphaltModulePrefab};if(road.moduleLODMeshes!=null)ids.AddRange(road.moduleLODMeshes);
            foreach(var v in road.moduleVariants??Array.Empty<LTRoadModuleVariant>())if(v!=null)
            {ids.Add(v.prefab);ids.Add(v.mesh);if(v.lodMeshes!=null)ids.AddRange(v.lodMeshes);}
            return "authored-fuse-v1;"+string.Join(";",ids.Select(x=>x?x.GetInstanceID()+":"+EditorUtility.GetDirtyCount(x)+":"+AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(x)):"null"));
        }
    }
}
