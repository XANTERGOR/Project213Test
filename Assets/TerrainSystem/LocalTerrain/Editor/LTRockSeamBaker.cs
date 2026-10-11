using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalTerrainPrototype
{
    internal static class LTRockSeamBaker
    {
        internal static bool Pending(LTWorld world)=>world.rockSeamBakePending;
        internal static void Bake(LTWorld world)
        {
            if(!world.rockSeamBakePending||!world.generatedRoot)return;
            var chunks=world.generatedRoot.GetComponentsInChildren<LTChunk>();
            if(chunks.Any(c=>c.lodsPending))return;
            world.rockSeamBakePending=false;
            try
            {
                var roads=world.GetComponentsInChildren<LTRoad>().Where(r=>r.isActiveAndEnabled&&r.World==world&&LTLODMesh.RequiresFixedRoadSurface(r.mode)).Select(r=>r.Capture(world)).ToArray();
                var junctions=world.GetComponentsInChildren<LTRoadJunction>().Where(r=>r.isActiveAndEnabled&&r.World==world&&LTLODMesh.RequiresFixedRoadSurface(r.surface)).Select(r=>r.Capture().bounds).ToArray();
                bool Protected(Vector3 a,Vector3 b,Vector3 c)
                {
                    var area=Rect.MinMaxRect(Mathf.Min(a.x,Mathf.Min(b.x,c.x)),Mathf.Min(a.z,Mathf.Min(b.z,c.z)),Mathf.Max(a.x,Mathf.Max(b.x,c.x)),Mathf.Max(a.z,Mathf.Max(b.z,c.z)));
                    return roads.Any(r=>r.Intersects(area))||junctions.Any(r=>r.Overlaps(area));
                }
                var stamps=world.GetComponentsInChildren<LTMeshStamp>().Where(s=>s.enabled&&s.affectHeight&&s.enableMeshLODs&&s.renderAsset&&s.renderAsset.Valid&&s.cutTerrain).ToArray();
                var groups=new List<List<LTMeshStamp>>();var boxes=new Dictionary<LTMeshStamp,Bounds>();
                foreach(var stamp in stamps)
                {
                    var box=new Bounds();bool first=true;var matrix=world.transform.worldToLocalMatrix*stamp.transform.localToWorldMatrix;
                    foreach(var p in stamp.renderAsset.transitions[0].vertices){var v=matrix.MultiplyPoint3x4(p);if(first){box=new Bounds(v,Vector3.zero);first=false;}else box.Encapsulate(v);}
                    box.Expand(new Vector3(16,100000,16));boxes[stamp]=box;
                    var group=new List<LTMeshStamp>{stamp};var union=box;bool joined;
                    do
                    {
                        joined=false;
                        foreach(var old in groups.ToArray())
                        {
                            var other=boxes[old[0]];foreach(var member in old)other.Encapsulate(boxes[member]);
                            if(!union.Intersects(other))continue;group.AddRange(old);groups.Remove(old);union.Encapsulate(other);joined=true;
                        }
                    }while(joined);
                    groups.Add(group);
                }
                var prepared=new List<LTRockSeamAsset.Group>();
                foreach(var group in groups)
                {
                    var bounds=boxes[group[0]];foreach(var stamp in group)bounds.Encapsulate(boxes[stamp]);
                    var parts=new List<LTRockSeamMath.Part>();var meshes=new List<Mesh>();var bindings=new List<LTRockSeamAsset.Terrain>();
                    void Add(Mesh mesh,Matrix4x4 matrix,bool[] movable,int onlyLevel=-1,int[] constraints=null)
                    {
                        if(!mesh||meshes.Contains(mesh))return;
                        var p=mesh.vertices;for(int i=0;i<p.Length;i++)p[i]=matrix.MultiplyPoint3x4(p[i]);
                        for(int i=0;i<p.Length;i++)movable[i]&=bounds.Contains(p[i]);
                        var triangles=Enumerable.Range(0,mesh.subMeshCount).Select(mesh.GetTriangles).ToArray();
                        var inside=p.Select(v=>bounds.Contains(v)).ToArray();
                        if(onlyLevel<0)LTRockSeamMath.ProtectOutsideStars(movable,inside,constraints==null?triangles:triangles.Concat(new[]{constraints}));
                        if(onlyLevel<0&&(roads.Length>0||junctions.Length>0))foreach(var indices in constraints==null?triangles:triangles.Concat(new[]{constraints}))for(int i=0;i<indices.Length;i+=3)
                            if(Protected(p[indices[i]],p[indices[i+1]],p[indices[i+2]]))movable[indices[i]]=movable[indices[i+1]]=movable[indices[i+2]]=false;
                        parts.Add(new LTRockSeamMath.Part{positions=p,triangles=triangles,movable=movable,targets=inside,onlyLevel=onlyLevel,terrain=onlyLevel<0,constraints=constraints});meshes.Add(mesh);
                    }
                    foreach(var chunk in chunks)
                    {
                        if(!chunk.mesh)continue;var r=new Bounds(chunk.transform.localPosition+chunk.mesh.bounds.center,chunk.mesh.bounds.size);
                        r.Expand(new Vector3(0,100000,0));if(!bounds.Intersects(r))continue;
                        var matrix=world.transform.worldToLocalMatrix*chunk.transform.localToWorldMatrix;var spatial=world.UseSpatialLODs?chunk.ActiveSpatialLOD:null;
                        if(world.UseSpatialLODs&&(!spatial||!spatial.vertexBank))continue;
                        bindings.Add(new LTRockSeamAsset.Terrain{x=chunk.x,z=chunk.z,lod0=chunk.mesh,bank=spatial?spatial.vertexBank:null,
                            spatialRevision=spatial?spatial.revision:0,legacy=chunk.lodMeshes});
                        // Validate endpoint contractions against every reachable native
                        // triangle instead of freezing all optional stitch stars.
                        var safeKeys=new HashSet<Vector3Int>();
                        if(spatial)
                        {
                            var vertices=spatial.vertexBank.vertices;
                            float dx=world.ChunkSize.x/world.SpatialLODDivisions,dz=world.ChunkSize.y/world.SpatialLODDivisions;
                            var safe=LTRockSeamMath.InteriorSpatialVertices(vertices,dx,dz);
                            for(int i=0;i<vertices.Length;i++)
                            {
                                if(safe[i])safeKeys.Add(LTRockSeamMath.Key(matrix.MultiplyPoint3x4(vertices[i])));
                            }
                            Add(spatial.vertexBank,matrix,safe,-1,LTRockSeamMath.SpatialConstraints(spatial.cells,spatial.patches));
                        }
                        else
                        {
                            // Whole-chunk support: every incident triangle must survive unchanged at every LOD.
                            var baseStars=Stars(chunk.mesh,matrix);
                            safeKeys.UnionWith(baseStars.Keys);
                            foreach(var mesh in chunk.lodMeshes)
                            {
                                var other=Stars(mesh,matrix);
                                safeKeys.RemoveWhere(k=>!other.TryGetValue(k,out var star)||!star.SetEquals(baseStars[k]));
                            }
                        }
                        foreach(var mesh in new[]{chunk.mesh}.Concat(chunk.lodMeshes??Array.Empty<Mesh>()))if(mesh)
                            Add(mesh,matrix,mesh.vertices.Select(p=>safeKeys.Contains(LTRockSeamMath.Key(matrix.MultiplyPoint3x4(p)))).ToArray());
                    }
                    var terrainKeys=new HashSet<Vector3Int>(parts.SelectMany(p=>p.positions).Select(LTRockSeamMath.Key));
                    foreach(var stamp in group)
                    {
                        var matrix=world.transform.worldToLocalMatrix*stamp.transform.localToWorldMatrix;
                        for(int level=0;level<stamp.renderAsset.transitions.Length;level++)
                        {
                            var mesh=stamp.renderAsset.transitions[level];
                            var vertices=mesh.vertices;var safe=vertices.Select(p=>terrainKeys.Contains(LTRockSeamMath.Key(matrix.MultiplyPoint3x4(p)))).ToArray();
                            // Only the actual terrain rim may move on the transition side.
                            Add(mesh,matrix,safe,level);
                        }
                    }
                    if(parts.Count==0)continue;
                    int count=Math.Min(4,world.lods?.Length??0)+1;var errors=new float[count];
                    float tolerance=group.Min(s=>s.terrainSeamLodError);
                    for(int l=1;l<count;l++)errors[l]=tolerance*Mathf.Pow(2,l-1);
                    foreach(var part in parts)if(part.terrain)
                        part.heightErrors=Enumerable.Range(0,count).Select(l=>l==0?0:world.lods[l-1].maxHeightError).ToArray();
                    var result=LTRockSeamMath.Build(parts.ToArray(),errors,progressive:true);
                    var meshBindings=meshes.Select((mesh,i)=>new LTRockSeamAsset.Binding{mesh=mesh,levels=result.maps.Select(level=>new LTRockSeamAsset.Map{indices=level[i]}).ToArray()}).ToArray();
                    for(int p=0;p<parts.Count;p++)if(parts[p].terrain&&parts[p].triangles.Length==1)
                    {
                        var part=parts[p];var stable=(bool[])part.movable.Clone();
                        bool Preserve(int a,int b,int c)=>Protected(part.positions[a],part.positions[b],part.positions[c]);
                        var spatialChunk=chunks.FirstOrDefault(c=>c.ActiveSpatialLOD&&c.ActiveSpatialLOD.vertexBank==meshes[p]);
                        if(world.UseSpatialLODs&&!spatialChunk)continue;
                        // Do not let independent groups replace the same long triangle.
                        var triangles=part.triangles[0];
                        for(int t=0;t<triangles.Length;t+=3)if(!bounds.Contains(part.positions[triangles[t]])||!bounds.Contains(part.positions[triangles[t+1]])||!bounds.Contains(part.positions[triangles[t+2]]))
                            stable[triangles[t]]=stable[triangles[t+1]]=stable[triangles[t+2]]=false;
                        for(int level=1;level<count;level++)if(errors[level]>0)
                        {
                            if(spatialChunk)
                            {
                                var spatial=spatialChunk.ActiveSpatialLOD;
                                var eligible=part.positions.Select(v=>bounds.Contains(v)).ToArray();
                                meshBindings[p].levels[level].bands=LTRockSeamBandMath.BuildSpatial(part.positions,triangles,spatial.cells,spatial.patches,
                                    eligible,result.maps[level][p],world.lods[level-1].maxHeightError,Preserve);
                            }
                            else
                            {
                                var band=LTRockSeamBandMath.Build(part.positions,triangles,stable,result.maps[level][p],world.lods[level-1].maxHeightError,null,Preserve,triangles);
                                meshBindings[p].levels[level].bands=band==null?Array.Empty<LTRockSeamIndices.Band>():new[]{band};
                            }
                        }
                    }
                    prepared.Add(new LTRockSeamAsset.Group{bounds=bounds,rocks=group.Select(s=>s.renderAsset).ToArray(),matrices=group.Select(s=>world.transform.worldToLocalMatrix*s.transform.localToWorldMatrix).ToArray(),terrain=bindings.ToArray(),collapses=result.collapses,bindings=meshBindings});
                }
                if(string.IsNullOrEmpty(world.outputFolder)||!AssetDatabase.IsValidFolder(world.outputFolder))return;
                var asset=world.rockSeams;
                string ownerKey=GlobalObjectId.GetGlobalObjectIdSlow(world).ToString();
                if(string.IsNullOrEmpty(world.gameObject.scene.path))ownerKey+="/session/"+world.GetInstanceID();
                if(!asset||asset.ownerKey!=ownerKey||!AssetDatabase.GetAssetPath(asset).StartsWith(world.outputFolder+"/",StringComparison.Ordinal))
                {
                    string path=AssetDatabase.GenerateUniqueAssetPath(world.outputFolder+"/RockTerrainSeams.asset");
                    asset=ScriptableObject.CreateInstance<LTRockSeamAsset>();asset.ownerKey=ownerKey;AssetDatabase.CreateAsset(asset,path);world.rockSeams=asset;
                }
                var oldBindings=asset.groups.SelectMany(g=>g.bindings).GroupBy(b=>b.mesh).ToDictionary(g=>g.Key,g=>g.First());
                foreach(var group in prepared)foreach(var binding in group.bindings)
                {
                    int level=-1;
                    foreach(var rock in group.rocks)for(int l=1;l<rock.transitions.Length;l++)if(rock.transitions[l]==binding.mesh)level=l;
                    if(level<1||level>=binding.levels.Length)continue;
                    oldBindings.TryGetValue(binding.mesh,out var previous);var baked=previous?.simplifiedTransition;
                    if(!baked||AssetDatabase.GetAssetPath(baked)!=AssetDatabase.GetAssetPath(asset)){baked=new Mesh();AssetDatabase.AddObjectToAsset(baked,asset);}
                    EditorUtility.CopySerialized(binding.mesh,baked);baked.hideFlags=HideFlags.None;baked.name=binding.mesh.name+" / Joint terrain seam";
                    for(int sub=0;sub<baked.subMeshCount;sub++)
                    {var indices=new List<int>(binding.mesh.GetTriangles(sub));LTRockSeamMath.Remap(indices,binding.levels[level].indices);baked.SetTriangles(indices,sub,false);}
                    binding.simplifiedTransition=baked;EditorUtility.SetDirty(baked);
                }
                asset.groups=prepared.ToArray();asset.revision++;EditorUtility.SetDirty(asset);EditorUtility.SetDirty(world);world.RefreshLODCache();
                Debug.Log("Rock/terrain joint LOD v4 baked: "+string.Join("; ",prepared.Select(g=>string.Join("/",g.collapses)+" collapses; native seam regions "+
                    string.Join("/",Enumerable.Range(0,g.collapses.Length).Select(l=>g.bindings.Sum(b=>b.levels[l].bands?.Length??0))))),world);
            }
            catch(Exception error){world.rockSeams=null;world.RefreshLODCache();Debug.LogWarning("Joint rock/terrain LOD kept at original contour: "+error,world);}
        }
        static Dictionary<Vector3Int,HashSet<string>> Stars(Mesh mesh,Matrix4x4 matrix)
        {
            var output=new Dictionary<Vector3Int,HashSet<string>>();if(!mesh)return output;
            var keys=mesh.vertices.Select(p=>LTRockSeamMath.Key(matrix.MultiplyPoint3x4(p))).ToArray();var tris=mesh.triangles;
            for(int i=0;i<tris.Length;i+=3)
            {
                var ids=new[]{keys[tris[i]],keys[tris[i+1]],keys[tris[i+2]]};string signature=string.Join("|",ids.Select(v=>v.ToString()).OrderBy(v=>v,StringComparer.Ordinal));
                foreach(var k in ids){if(!output.TryGetValue(k,out var set))output[k]=set=new HashSet<string>();set.Add(signature);}
            }
            return output;
        }
    }
}
