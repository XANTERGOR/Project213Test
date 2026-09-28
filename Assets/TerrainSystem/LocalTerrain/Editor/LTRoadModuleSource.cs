using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
namespace LocalTerrainPrototype
{
    public static class LTRoadModuleSource
    {
        sealed class SourceCache { public LTRoad road;public string key;public LTRoadMesh.Chunk[] levels;public Material[] materials; }
        static readonly Dictionary<int,SourceCache> sourceCache=new Dictionary<int,SourceCache>();
        public sealed class Prepared { public List<LTRoadMesh.Chunk>[] levels; public Material[] materials; public string status; }
        public static Prepared Build(LTRoad road,LTRoadMath.Snapshot snapshot)
        {
            if(road.asphaltLODs==null||road.asphaltLODs.Length>3)throw new InvalidOperationException("Асфальт поддерживает 0–3 дополнительных LOD.");
            int count=road.asphaltLODMode==LTRoadLODMode.Disabled?0:road.asphaltLODs.Length;
            foreach(var l in road.asphaltLODs)if(l==null||!LTRoadMesh.Finite(l.maxHeightError)||l.maxHeightError<0)throw new InvalidOperationException("Ошибка упрощения LOD должна быть конечной и неотрицательной.");
            var matrix=road.transform.worldToLocalMatrix*road.World.transform.localToWorldMatrix;
            var result=new Prepared{levels=new List<LTRoadMesh.Chunk>[count+1]};
            if(road.asphaltSource==LTRoadMeshSource.ProceduralRibbon)
            {
                result.materials=new[]{road.asphaltMaterial};
                result.levels[0]=LTRoadMesh.Build(snapshot,road.meshChunkLength,road.textureRepeatMetres,road.surfaceOffset,matrix);
                for(int l=1;l<=count;l++)result.levels[l]=result.levels[0].Select(c=>LTRoadModuleMath.RibbonLOD(c,l)).ToList();
            }
            else
            {
                var materials=new List<Material>();
                string key=DependencyKey(road)+"|"+road.moduleAxis+"|"+road.asphaltLODMode+"|"+count+"|"+
                    (road.asphaltMaterial?road.asphaltMaterial.GetInstanceID():0)+"|"+string.Join(",",(road.moduleMaterials??Array.Empty<Material>()).Select(m=>m?m.GetInstanceID():0))+"|"+
                    string.Join(",",road.asphaltLODs.Select(l=>l.maxHeightError.ToString("R",System.Globalization.CultureInfo.InvariantCulture)));
                foreach(int dead in sourceCache.Where(p=>!p.Value.road).Select(p=>p.Key).ToArray())sourceCache.Remove(dead);
                LTRoadMesh.Chunk ReadLevel(int level)
                {
                    var pieces=new List<(Mesh mesh,Matrix4x4 matrix,Material[] mats)>();
                    Matrix4x4 axis=road.moduleAxis==LTRoadModuleAxis.X?Matrix4x4.Rotate(Quaternion.Euler(0,-90,0)):Matrix4x4.identity;
                    if(road.asphaltModulePrefab)
                    {
                        if(!EditorUtility.IsPersistent(road.asphaltModulePrefab))throw new InvalidOperationException("Назначьте ассет префаба модуля из Project, не объект сцены.");
                        var root=road.asphaltModulePrefab.transform;var group=road.asphaltModulePrefab.GetComponent<LODGroup>();
                        Renderer[] renderers;
                        if(group)
                        {var lods=group.GetLODs();if(level>=lods.Length)throw new InvalidOperationException($"В префабе нет LOD{level}.");renderers=lods[level].renderers;}
                        else
                        {if(level>0)throw new InvalidOperationException("Для Authored назначьте префаб с LODGroup или отдельные меши LOD.");renderers=road.asphaltModulePrefab.GetComponentsInChildren<MeshRenderer>(true);}
                        foreach(var renderer in renderers)
                        {
                            var filter=renderer?renderer.GetComponent<MeshFilter>():null;
                            if(!filter||!filter.sharedMesh)throw new InvalidOperationException("Модуль поддерживает только статические MeshRenderer с MeshFilter.");
                            pieces.Add((filter.sharedMesh,axis*root.worldToLocalMatrix*filter.transform.localToWorldMatrix,renderer.sharedMaterials));
                        }
                    }
                    else
                    {
                        Mesh mesh=level==0?road.asphaltModule:(road.moduleLODMeshes!=null&&level<=road.moduleLODMeshes.Length?road.moduleLODMeshes[level-1]:null);
                        if(!mesh)throw new InvalidOperationException($"Назначьте меш модуля LOD{level} либо префаб.");
                        pieces.Add((mesh,axis,road.moduleMaterials??Array.Empty<Material>()));
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
                LTRoadMesh.Chunk[] sources;
                if(sourceCache.TryGetValue(road.GetInstanceID(),out var cached)&&cached.road==road&&cached.key==key)
                {sources=cached.levels;materials.AddRange(cached.materials);}
                else
                {
                    sources=new LTRoadMesh.Chunk[count+1];sources[0]=ReadLevel(0);
                    for(int l=1;l<=count;l++)sources[l]=road.asphaltLODMode==LTRoadLODMode.Authored?ReadLevel(l):
                        LTRoadModuleMath.Simplify(sources[0],1f/(1<<l),road.asphaltLODs[l-1].maxHeightError);
                    // Bound retained source geometry; no bent-road output is cached here.
                    if(sourceCache.Count>=8)sourceCache.Clear();
                    sourceCache[road.GetInstanceID()]=new SourceCache{road=road,key=key,levels=sources,materials=materials.ToArray()};
                }
                Vector3 min=sources[0].vertices[0],max=min;foreach(var v in sources[0].vertices){min=Vector3.Min(min,v);max=Vector3.Max(max,v);}
                var front=new HashSet<Vector2>(sources[0].vertices.Where(v=>Math.Abs(v.z-min.z)<.00001f).Select(v=>new Vector2(v.x,v.y)));
                var back=new HashSet<Vector2>(sources[0].vertices.Where(v=>Math.Abs(v.z-max.z)<.00001f).Select(v=>new Vector2(v.x,v.y)));
                if(!front.SetEquals(back))throw new InvalidOperationException("Торцы модуля различаются. Для бесшовного повторения сечения X/Y на обоих концах должны совпадать.");
                var ends=new HashSet<Vector3>(sources[0].vertices.Where(v=>Math.Abs(v.z-min.z)<.00001f||Math.Abs(v.z-max.z)<.00001f));
                for(int l=1;l<=count;l++)
                {
                    var other=new HashSet<Vector3>(sources[l].vertices.Where(v=>Math.Abs(v.z-min.z)<.00001f||Math.Abs(v.z-max.z)<.00001f));
                    if(!ends.SetEquals(other))throw new InvalidOperationException($"LOD{l}: торцы модуля должны совпадать с LOD0, иначе на стыках появятся щели.");
                    if(sources[l].vertices.Any(v=>v.z<min.z-.00001f||v.z>max.z+.00001f))throw new InvalidOperationException($"LOD{l}: длина выходит за границы LOD0.");
                }
                // Match end shading across authored LODs as well as across repeats.
                var endFrames=new Dictionary<(Vector3,Vector2),(Vector3,Vector4)>();
                for(int i=0;i<sources[0].vertices.Length;i++)if(ends.Contains(sources[0].vertices[i]))
                    endFrames[(sources[0].vertices[i],sources[0].uv[i])]=(sources[0].normals[i],sources[0].tangents[i]);
                for(int l=1;l<=count;l++)for(int i=0;i<sources[l].vertices.Length;i++)
                    if(endFrames.TryGetValue((sources[l].vertices[i],sources[l].uv[i]),out var frame))
                    {sources[l].normals[i]=frame.Item1;sources[l].tangents[i]=frame.Item2;}
                for(int l=0;l<=count;l++)
                {
                    // Material palette is shared by all levels, including absent slots.
                    if(sources[l].submeshes.Length<materials.Count)
                    {var subs=sources[l].submeshes;Array.Resize(ref subs,materials.Count);for(int i=0;i<subs.Length;i++)if(subs[i]==null)subs[i]=Array.Empty<int>();sources[l].submeshes=subs;}
                    result.levels[l]=LTRoadModuleMath.Bend(sources[l],snapshot,min,max,road.moduleLength,road.moduleFitWidth,road.moduleBaseY,road.surfaceOffset,road.meshChunkLength,matrix);
                }
                result.materials=materials.ToArray();
            }
            result.status=string.Join(" / ",result.levels.Select((chunks,l)=>$"LOD{l}: {chunks.Sum(c=>(long)c.triangles.Length/3):N0} tris"));
            return result;
        }
        public static string DependencyKey(LTRoad road)
        {
            var ids=new List<UnityEngine.Object>{road.asphaltModule,road.asphaltModulePrefab};if(road.moduleLODMeshes!=null)ids.AddRange(road.moduleLODMeshes);
            return string.Join(";",ids.Select(x=>x?x.GetInstanceID()+":"+EditorUtility.GetDirtyCount(x)+":"+AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(x)):"null"));
        }
    }
}
