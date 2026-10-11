using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public static class LTRoadJunctionPrefab
    {
        public sealed class Prepared
        {
            public LTRoadMesh.Chunk[] levels;
            public Material[] materials;
            public readonly Dictionary<LTRoadJunctionSocket,LTRoadJunctionSeams.Port> ports=new Dictionary<LTRoadJunctionSocket,LTRoadJunctionSeams.Port>();
            public readonly Dictionary<LTRoadJunctionSocket,LTRoadJunctionProfileFit.Profile> fittedPorts=new Dictionary<LTRoadJunctionSocket,LTRoadJunctionProfileFit.Profile>();
        }
        public static string DependencyKey(LTRoadJunction node)
        {
            if(!node||!node.Authored||!node.SourceRoot)return "";
            var root=node.SourceRoot;
            string Asset(UnityEngine.Object o)
            {
                if(!o)return "null";string path=AssetDatabase.GetAssetPath(o);
                return o.GetInstanceID()+":"+EditorUtility.GetDirtyCount(o)+":"+(string.IsNullOrEmpty(path)?"":AssetDatabase.GetAssetDependencyHash(path).ToString());
            }
            if(!node.OwnHierarchy)return "junction-prefab-v2:"+Asset(root);
            var key=new StringBuilder("junction-own-v1:");
            foreach(var renderer in root.GetComponentsInChildren<MeshRenderer>(true).Where(r=>node.SourceMember(r.transform)))
            {
                var filter=renderer.GetComponent<MeshFilter>();
                key.Append('|').Append(renderer.GetInstanceID()).Append(':').Append(Asset(filter?filter.sharedMesh:null))
                    .Append(':').Append((root.transform.worldToLocalMatrix*renderer.transform.localToWorldMatrix).ToString("R"));
                foreach(var mat in renderer.sharedMaterials)key.Append(':').Append(Asset(mat));
            }
            foreach(var group in root.GetComponentsInChildren<LODGroup>(true).Where(g=>node.SourceMember(g.transform)))
            {key.Append("|lod:").Append(group.GetInstanceID());foreach(var lod in group.GetLODs())key.Append('/').Append(string.Join(",",(lod.renderers??Array.Empty<Renderer>()).Select(r=>r?r.GetInstanceID():0)));}
            // Publishing only changes visibility, never this key: no endless auto-rebake.
            return key.ToString();
        }

        public static Prepared Build(LTRoadJunction node,bool preview=false)
        {
            float meshOffset=node.MeshSurfaceOffset;
            if(node.OwnHierarchy&&(node.GetComponent<LTWorld>()||node.GetComponent<LTRoad>()))
                throw new ArgumentException("Компонент перекрёстка нужен на корне его модели, не на LTWorld и не на объекте сплайна Road.");
            var prefab=node.SourceRoot;
            if(!prefab||!node.OwnHierarchy&&!EditorUtility.IsPersistent(prefab)&&!(preview&&EditorSceneManager.IsPreviewScene(node.gameObject.scene)&&prefab.scene==node.gameObject.scene))
                throw new ArgumentException("Назначьте ассет префаба перекрёстка из Project, не объект сцены.");
            if(prefab.GetComponentsInChildren<Renderer>(true).Any(r=>node.SourceMember(r.transform)&&!(r is MeshRenderer)))
                throw new ArgumentException("Перекрёсток поддерживает статические MeshRenderer, не SkinnedMeshRenderer.");
            node.Capture();
            var renderers=LTRoadModuleLODPlan.ReadPrefabLevels(prefab,node.SourceMember)??new[]{prefab.GetComponentsInChildren<MeshRenderer>(true).Where(r=>node.SourceMember(r.transform)).Cast<Renderer>().ToArray()};
            var materials=new List<Material>();var output=new Prepared{levels=new LTRoadMesh.Chunk[renderers.Length]};
            // Junctions use the same metre-distance LOD policy as roads, not the source
            // prefab's screen-relative percentages. Never auto-reduce a supplied LOD.
            for(int l=1;l<renderers.Length;l++)if(float.IsNaN(LTRoadLODPolicy.Distance(node.lods,l)))
                throw new ArgumentException("Дистанции LOD перекрёстка должны возрастать.");
            for(int l=0;l<renderers.Length;l++)output.levels[l]=Read(prefab,renderers[l],node.asphaltMaterial,materials);
            foreach(var level in output.levels)
            {
                if(!node.MeshFoundation&&level.vertices.Any(v=>new Vector2(v.x,v.z).magnitude>node.radius+node.socketTolerance))
                    throw new ArgumentException("Увеличьте радиус основания: он должен накрывать весь меш перекрёстка.");
                var subs=level.submeshes;Array.Resize(ref subs,materials.Count);
                for(int s=0;s<subs.Length;s++)if(subs[s]==null)subs[s]=Array.Empty<int>();level.submeshes=subs;
            }
            var sockets=node.Sockets;
            for(int a=0;a<sockets.Length;a++)for(int b=a+1;b<sockets.Length;b++)
                if((sockets[a].InPrefab(prefab).MultiplyPoint3x4(Vector3.zero)-sockets[b].InPrefab(prefab).MultiplyPoint3x4(Vector3.zero)).sqrMagnitude<node.socketTolerance*node.socketTolerance)
                    throw new ArgumentException("Два маркера въезда занимают одну позицию: "+sockets[a].name+" / "+sockets[b].name);
            // Freeze every canonical profile from untouched LOD0 before snapping any LOD.
            foreach(var socket in sockets)
            {
                try
                {
                    if(node.FitProfile)output.fittedPorts.Add(socket,LTRoadJunctionProfileFit.Create(output.levels[0],socket.InPrefab(prefab).inverse,node.profileSearchBand,socket.width));
                    else output.ports.Add(socket,LTRoadJunctionSeams.Create(output.levels[0],socket.InPrefab(prefab).inverse,node.socketTolerance,socket.width));
                }
                catch(Exception e){throw new ArgumentException(socket.name+": "+e.Message,e);}
            }
            for(int l=0;l<output.levels.Length;l++)foreach(var socket in sockets)
            {
                try
                {
                    output.levels[l]=node.FitProfile?
                        LTRoadJunctionProfileFit.Apply(output.levels[l],socket.InPrefab(prefab).inverse,output.fittedPorts[socket],node.profileBlendLength,node.profileMaxShift,-1):
                        LTRoadJunctionSeams.Snap(output.levels[l],socket.InPrefab(prefab).inverse,output.ports[socket]);
                }
                catch(Exception e){throw new ArgumentException(socket.name+$", LOD{l}: "+e.Message,e);}
            }
            foreach(var level in output.levels)
            {
                for(int i=0;i<level.vertices.Length;i++)level.vertices[i]+=Vector3.up*meshOffset;
                level.hash=LTRoadMesh.ContentHash(level);
            }
            output.materials=materials.ToArray();return output;
        }

        internal static LTRoadMesh.Chunk Read(GameObject prefab,Renderer[] renderers,Material materialOverride,List<Material> materials)
        {
            if(renderers.Length==0)throw new ArgumentException("В префабе нет статических мешей.");
            var v=new List<Vector3>();var n=new List<Vector3>();var t=new List<Vector4>();var uv=new List<Vector2>();
            var uv2=new List<Vector2>();var uv3=new List<Vector2>();var uv4=new List<Vector2>();var colors=new List<Color>();
            var faces=new List<List<int>>();bool has2=false,has3=false,has4=false,hasColors=false;
            foreach(var renderer in renderers)
            {
                var filter=renderer.GetComponent<MeshFilter>();var mesh=filter?filter.sharedMesh:null;
                if(!mesh||!mesh.isReadable)throw new ArgumentException(renderer.name+": нужен MeshFilter и Read/Write в импорте модели.");
                if(mesh.blendShapeCount>0)throw new ArgumentException(mesh.name+": Blend Shapes не поддерживаются.");
                for(int channel=0;channel<8;channel++)
                {
                    var attr=(UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0+channel);
                    if(mesh.HasVertexAttribute(attr)&&(channel>=4||mesh.GetVertexAttributeDimension(attr)!=2))
                        throw new ArgumentException(mesh.name+": поддерживаются двумерные UV0–UV3; остальные каналы не отбрасываются молча.");
                }
                var p=mesh.vertices;var normal=mesh.normals;var tangent=mesh.tangents;var u=mesh.uv;var u2=mesh.uv2;var u3=mesh.uv3;var u4=mesh.uv4;var c=mesh.colors;
                if(p.Length==0||normal.Length!=p.Length||tangent.Length!=p.Length||u.Length!=p.Length)
                    throw new ArgumentException(mesh.name+": нужны нормали, тангенты и UV0.");
                if(v.Count+(long)p.Length>1000000)throw new ArgumentException("Перекрёсток превышает лимит 1 млн вершин на LOD.");
                var matrix=prefab.transform.worldToLocalMatrix*renderer.transform.localToWorldMatrix;
                if(!LTRoadMesh.Finite(matrix.determinant)||matrix.determinant<=0)throw new ArgumentException("Нулевой/отражённый масштаб внутри префаба не поддерживается.");
                var nm=matrix.inverse.transpose;int first=v.Count;
                has2|=u2.Length==p.Length;has3|=u3.Length==p.Length;has4|=u4.Length==p.Length;hasColors|=c.Length==p.Length;
                for(int i=0;i<p.Length;i++)
                {
                    var pos=matrix.MultiplyPoint3x4(p[i]);var norm=nm.MultiplyVector(normal[i]).normalized;
                    var tan=matrix.MultiplyVector(new Vector3(tangent[i].x,tangent[i].y,tangent[i].z));tan=(tan-norm*Vector3.Dot(tan,norm)).normalized;
                    if(!LTRoadMesh.Finite(pos)||!LTRoadMesh.Finite(norm)||!LTRoadMesh.Finite(tan))throw new ArgumentException("Неконечные координаты/нормали префаба.");
                    v.Add(pos);n.Add(norm);t.Add(new Vector4(tan.x,tan.y,tan.z,tangent[i].w));uv.Add(u[i]);
                    uv2.Add(has2&&u2.Length==p.Length?u2[i]:Vector2.zero);uv3.Add(u3.Length==p.Length?u3[i]:Vector2.zero);uv4.Add(u4.Length==p.Length?u4[i]:Vector2.zero);colors.Add(c.Length==p.Length?c[i]:Color.white);
                }
                var mats=renderer.sharedMaterials;
                for(int s=0;s<mesh.subMeshCount;s++)
                {
                    if(mesh.GetTopology(s)!=MeshTopology.Triangles)throw new ArgumentException(mesh.name+": нужны треугольники.");
                    var material=materialOverride?materialOverride:s<mats.Length?mats[s]:null;
                    if(!material)throw new ArgumentException(mesh.name+$": не назначен материал submesh {s}.");
                    int slot=materials.IndexOf(material);if(slot<0){slot=materials.Count;materials.Add(material);}
                    while(faces.Count<=slot)faces.Add(new List<int>());
                    foreach(int index in mesh.GetTriangles(s))faces[slot].Add(first+index);
                }
            }
            var result=new LTRoadMesh.Chunk{vertices=v.ToArray(),normals=n.ToArray(),tangents=t.ToArray(),uv=uv.ToArray(),
                uv2=has2?uv2.ToArray():null,uv3=has3?uv3.ToArray():null,uv4=has4?uv4.ToArray():null,colors=hasColors?colors.ToArray():null,
                submeshes=faces.Select(f=>f.ToArray()).ToArray(),triangles=faces.SelectMany(f=>f).ToArray()};
            result.hash=LTRoadMesh.ContentHash(result);return result;
        }

        public static void MatchRoad(LTRoadJunction node,LTRoad road,bool start,LTRoadModuleSource.Prepared source,Prepared junction=null,bool preview=false,LTRoadJunctionSocket proposed=null)
        {
            node.Port(road,start,proposed);junction=junction??Build(node,preview);var socket=node.Socket(road,start,proposed);
            var toSocket=(node.transform.localToWorldMatrix*Matrix4x4.Translate(Vector3.up*node.MeshSurfaceOffset)*socket.InPrefab(node.SourceRoot)).inverse*road.transform.localToWorldMatrix;
            // Stage every level before publishing any changes to this prepared result.
            var fitted=new LTRoadMesh.Chunk[source.levels.Length];
            for(int l=0;l<source.levels.Length;l++)
            {
                var chunks=source.levels[l];int index=start?0:chunks.Count-1;
                try
                {
                    if(node.FitProfile)
                    {
                        // Avoid a displacement discontinuity at a generated chunk boundary,
                        // or deforming a distant loop of the road near the same socket.
                        for(int c=0;c<chunks.Count;c++)if(c!=index&&chunks[c].vertices.Select(toSocket.MultiplyPoint3x4).Any(p=>
                            p.z>=-node.profileSearchBand&&p.z<node.profileBlendLength&&Math.Abs(p.x)<=socket.width*.5f+node.profileMaxShift))
                            throw new ArgumentException("Переход выходит за концевой сегмент или пересекает другую часть дороги. Уменьшите длину подгонки либо увеличьте длину сегмента дороги.");
                        fitted[l]=LTRoadJunctionProfileFit.Apply(chunks[index],toSocket,junction.fittedPorts[socket],node.profileBlendLength,node.profileMaxShift,1);
                    }
                    else fitted[l]=LTRoadJunctionSeams.Snap(chunks[index],toSocket,junction.ports[socket]);
                }
                catch(Exception e){throw new ArgumentException(road.name+" / "+socket.name+$" / LOD{l}: "+e.Message,e);}
            }
            for(int l=0;l<source.levels.Length;l++){var chunks=source.levels[l];chunks[start?0:chunks.Count-1]=fitted[l];}
        }
    }
}
