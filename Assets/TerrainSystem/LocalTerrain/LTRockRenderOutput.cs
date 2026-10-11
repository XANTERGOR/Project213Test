using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    // Owned, disposable render products. The validated combined LOD0 remains the collider.
    public sealed class LTRockRenderOutput : IDisposable
    {
        public readonly LTMeshStamp owner;
        public readonly Mesh combined, rock, transition;
        public readonly MeshRenderer bridgeRenderer;
        public readonly int sourceSubmeshes;
        public readonly bool atlasUV;
        public readonly bool rockAtlasPerLOD;
        public event Action<int> LODChanged;
        readonly GameObject bridgeObject;
        readonly List<Mesh> rockLODs=new List<Mesh>(),bridgeLODs=new List<Mesh>();
        readonly bool ownsMeshes=true;
        public Mesh[] RockLODs=>rockLODs.ToArray();
        public Mesh[] TransitionLODs=>bridgeLODs.ToArray();
        int currentLOD=-1;
        public int CurrentLOD=>currentLOD;
        public bool Owns(Mesh mesh)=>rockLODs.Contains(mesh)||mesh==rock;
        bool disposed;
        public bool SeamManaged {get;set;}
        Mesh seamBridge;
        Mesh seamBaked;
        int[] seamBridgeMap;
        public LTRockRenderOutput(LTMeshStamp stamp,LTRockRenderAsset asset,Material fallback)
        {
            if(!asset||!asset.Valid)throw new ArgumentException("Missing baked rock render data.");
            owner=stamp;combined=asset.combined;sourceSubmeshes=asset.sourceSubmeshes;atlasUV=asset.atlasUV;ownsMeshes=false;
            rockAtlasPerLOD=asset.rockAtlasPerLOD;
            rock=asset.rocks[0];transition=asset.transitions[0];rockLODs.AddRange(asset.rocks);bridgeLODs.AddRange(asset.transitions);
            try{bridgeObject=CreateRenderer(stamp,transition,fallback);bridgeRenderer=bridgeObject.GetComponent<MeshRenderer>();}
            catch{Dispose();throw;}
        }
        public LTRockRenderOutput(LTMeshStamp stamp,Mesh validated,int sourceSlots,Material fallback)
        {
            owner=stamp;combined=validated;sourceSubmeshes=sourceSlots;
            try
            {
                int[][] indices=new int[validated.subMeshCount][];
                for(int i=0;i<indices.Length;i++)indices[i]=validated.GetTriangles(i);
                var plan=LTRockTransitionMath.Split(validated.vertices,indices,sourceSlots,stamp.transform.localToWorldMatrix,
                    stamp.materialBlendWidth,stamp.materialBlendSoftness);
                rock=Extract(validated,plan.rock,null,stamp.name+" / Rock");
                transition=Extract(validated,plan.transition,plan.terrainWeight,stamp.name+" / Transition");
                // The transition keeps its shared vertex bank for joint terrain seam maps.
                // Rock LODs must be reduced BEFORE secondary charts split their vertices.
#if UNITY_EDITOR
                bool Unwrap(Mesh mesh,int resolution)
                {
                    if(mesh.vertexCount==0)return true;
                    UnityEditor.UnwrapParam.SetDefaults(out var settings);
                    settings.packMargin=8f/resolution;
                    if(!UnityEditor.Unwrapping.GenerateSecondaryUVSet(mesh,settings))return false;
                    var uv=mesh.uv2;if(uv.Length!=mesh.vertexCount)return false;
                    mesh.SetUVs(2,new List<Vector2>(uv));return true;
                }
                if(stamp.simplifyDistantMaterial)
                    atlasUV=Unwrap(transition,stamp.materialFarResolution);
#endif
                bridgeObject=CreateRenderer(stamp,transition,fallback);bridgeRenderer=bridgeObject.GetComponent<MeshRenderer>();
                rockLODs.Add(rock);bridgeLODs.Add(transition);
                var world=stamp.GetComponentInParent<LTWorld>(true);
                int levels=stamp.enableMeshLODs&&world&&world.enableLODs&&world.lods!=null?Math.Min(4,world.lods.Length):0;
                int[][][] rockPlan=null;
                if(levels>0&&rock.vertexCount>0)
                {
                    var positions=rock.vertices;for(int i=0;i<positions.Length;i++)positions[i]=stamp.transform.TransformPoint(positions[i]);
                    var triangles=new int[rock.subMeshCount][];for(int s=0;s<triangles.Length;s++)triangles[s]=rock.GetTriangles(s);
                    var colors=rock.colors;var blend=new float[positions.Length];for(int i=0;i<blend.Length;i++)blend[i]=colors[i].r;
                    var charts=new List<Vector2[]>();
                    foreach(var uv in new[]{rock.uv,rock.uv2,rock.uv3})if(uv.Length==positions.Length)charts.Add(uv);
                    rockPlan=LTRockLODMath.RockLevels(positions,triangles,blend,stamp.meshLodError,levels,charts.ToArray(),protectSourceUV:true);
                }
                Mesh MakeLOD(Mesh input,int level)
                {
                    if(input.vertexCount==0)return input;
                    var positions=input.vertices;for(int i=0;i<positions.Length;i++)positions[i]=stamp.transform.TransformPoint(positions[i]);
                    var triangles=new int[input.subMeshCount][];for(int i=0;i<triangles.Length;i++)triangles[i]=input.GetTriangles(i);
                    var colors=input.colors;var blend=new float[positions.Length];for(int i=0;i<blend.Length;i++)blend[i]=colors[i].r;
                    var reduced=input==rock&&rockPlan!=null?rockPlan[level]:
                        LTRockLODMath.Reduce(positions,triangles,blend,stamp.meshLodError*Mathf.Pow(2,level-1),level+2);
                    if(input==rock)
                    {
                        // Compact unused vertices before unwrap. Preserve the LOD0 bounds:
                        // camera-driven LOD changes must not invalidate paint coverage/bakes.
                        var compact=Extract(input,reduced,null,input.name+" LOD"+level);compact.bounds=input.bounds;return compact;
                    }
                    var mesh=UnityEngine.Object.Instantiate(input);mesh.name=input.name+" LOD"+level;mesh.hideFlags=HideFlags.HideAndDontSave;
                    for(int i=0;i<reduced.Length;i++)mesh.SetTriangles(reduced[i],i,false);return mesh;
                }
                for(int level=1;level<=levels;level++){rockLODs.Add(MakeLOD(rock,level));bridgeLODs.Add(MakeLOD(transition,level));}
#if UNITY_EDITOR
                if(stamp.simplifyDistantMaterial)
                {
                    var bounds=rock.bounds;
                    for(int level=0;level<rockLODs.Count;level++)
                    {
                        bool valid=Unwrap(rockLODs[level],LTRockLODAtlases.Resolution(stamp.materialFarResolution,level));
                        atlasUV=valid&&atlasUV;rockLODs[level].bounds=bounds;
                    }
                    rockAtlasPerLOD=atlasUV;
                }
#endif
            }
            catch{Dispose();throw;}
        }
        static GameObject CreateRenderer(LTMeshStamp stamp,Mesh mesh,Material fallback)
        {
            var child=new GameObject("Generated mesh transition"){hideFlags=HideFlags.HideAndDontSave,layer=stamp.gameObject.layer};
            try
            {
                child.transform.SetParent(stamp.transform,false);child.AddComponent<LTRockTransitionRenderer>();
                child.AddComponent<MeshFilter>().sharedMesh=mesh;var renderer=child.AddComponent<MeshRenderer>();
                renderer.sharedMaterials=Fill(fallback,mesh.subMeshCount);var original=stamp.GetComponent<MeshRenderer>();
                if(original)
                {
                    renderer.enabled=original.enabled;renderer.shadowCastingMode=original.shadowCastingMode;renderer.receiveShadows=original.receiveShadows;
                    renderer.renderingLayerMask=original.renderingLayerMask;renderer.lightProbeUsage=original.lightProbeUsage;
                    renderer.reflectionProbeUsage=original.reflectionProbeUsage;renderer.motionVectorGenerationMode=original.motionVectorGenerationMode;
                }
                // Swapped meshes follow the same GPU-driven exclusion policy as terrain.
                var exclusion=typeof(RenderPipelineManager).Assembly.GetType("UnityEngine.Rendering.DisallowGPUDrivenRendering");
                if(exclusion==null)foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if((exclusion=assembly.GetType("UnityEngine.Rendering.DisallowGPUDrivenRendering"))!=null)break;
                if(exclusion!=null){child.AddComponent(exclusion);if(!stamp.GetComponent(exclusion))stamp.gameObject.AddComponent(exclusion);}
                return child;
            }
            catch{Destroy(child);throw;}
        }
        public void Publish()
        {
            if(!owner||disposed)return;
            foreach(var marker in owner.GetComponentsInChildren<LTRockTransitionRenderer>(true))
                if(marker&&marker.transform.parent==owner.transform&&marker.gameObject!=bridgeObject)Destroy(marker.gameObject);
            owner.GetComponent<MeshFilter>().sharedMesh=rock;
            currentLOD=0;
            LODChanged?.Invoke(currentLOD);
        }
        public void UpdateLOD()
        {
            if(disposed||!owner||!owner.isActiveAndEnabled)return;
            if(SeamManaged)return;
            ApplySeamLOD(DesiredLOD(),null);
        }
        public int DesiredLOD()
        {
            if(disposed||!owner||!owner.isActiveAndEnabled)return 0;
            var world=owner.GetComponentInParent<LTWorld>(true);if(!world)return 0;
            Camera camera=world.lodCamera;
#if UNITY_EDITOR
            if(!Application.isPlaying&&world.previewLODs&&UnityEditor.SceneView.lastActiveSceneView)
                camera=UnityEditor.SceneView.lastActiveSceneView.camera;
#endif
            if(!camera)camera=Camera.main;
            int level=0;
            if(owner.enableMeshLODs&&world.enableLODs&&(Application.isPlaying||world.previewLODs||world.forceLOD>=0))
            {
                if(world.forceLOD>=0)level=world.forceLOD;
                else if(camera)
                {
                    var bounds=combined.bounds;var matrix=owner.transform.localToWorldMatrix;
                    var box=new Bounds(matrix.MultiplyPoint3x4(bounds.center),Vector3.zero);
                    for(int i=0;i<8;i++)box.Encapsulate(matrix.MultiplyPoint3x4(bounds.center+Vector3.Scale(bounds.extents,
                        new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1))));
                    level=LTWorld.SelectLOD(Mathf.Sqrt(box.SqrDistance(camera.transform.position)),Math.Max(0,currentLOD),world.lods,world.lodHysteresis);
                }
            }
            return Mathf.Clamp(level,0,rockLODs.Count-1);
        }
        public void ApplySeamLOD(int level,int[] map,Mesh baked=null)
        {
            if(disposed||!owner)return;
            level=Mathf.Clamp(level,0,rockLODs.Count-1);
            if(currentLOD==level&&ReferenceEquals(seamBridgeMap,map)&&seamBaked==baked)return;
            Destroy(seamBridge);seamBridge=null;seamBridgeMap=map;seamBaked=baked;
            if(map!=null&&!baked)
            {
                var source=bridgeLODs[level];
                if(map.Length!=source.vertexCount)throw new InvalidOperationException("Stale transition seam map.");
                seamBridge=UnityEngine.Object.Instantiate(source);seamBridge.name=source.name+" / Joint seam";seamBridge.hideFlags=HideFlags.HideAndDontSave;
                for(int sub=0;sub<source.subMeshCount;sub++){var indices=new List<int>(source.GetTriangles(sub));LTRockSeamMath.Remap(indices,map);seamBridge.SetTriangles(indices,sub,false);}
            }
            owner.GetComponent<MeshFilter>().sharedMesh=rockLODs[level];bridgeObject.GetComponent<MeshFilter>().sharedMesh=baked?baked:seamBridge?seamBridge:bridgeLODs[level];currentLOD=level;
            // Bind the matching atlas in the same update as its mesh, not on a later paint tick.
            LODChanged?.Invoke(currentLOD);
        }
        static Material[] Fill(Material value,int n){var result=new Material[n];for(int i=0;i<n;i++)result[i]=value;return result;}
        static Mesh Extract(Mesh input,int[][] submeshes,float[] weights,string name)
        {
            var vertices=input.vertices;var normals=input.normals;var tangents=input.tangents;var uv=input.uv;
            var p=new List<Vector3>();var n=new List<Vector3>();var t=new List<Vector4>();var u=new List<Vector2>();var colors=new List<Color>();
            var map=new Dictionary<int,int>();var triangles=new List<int>[submeshes.Length];
            for(int sub=0;sub<submeshes.Length;sub++)
            {
                triangles[sub]=new List<int>(submeshes[sub].Length);
                foreach(int index in submeshes[sub])
                {
                    if(!map.TryGetValue(index,out int target))
                    {
                        target=p.Count;map.Add(index,target);p.Add(vertices[index]);
                        n.Add(normals.Length==vertices.Length?normals[index]:Vector3.up);
                        t.Add(tangents.Length==vertices.Length?tangents[index]:new Vector4(1,0,0,1));
                        u.Add(uv.Length==vertices.Length?uv[index]:Vector2.zero);
                        colors.Add(new Color(weights==null?0:weights[index],0,0,1));
                    }
                    triangles[sub].Add(target);
                }
            }
            var mesh=new Mesh{name=name,hideFlags=HideFlags.HideAndDontSave,indexFormat=p.Count>65535?IndexFormat.UInt32:IndexFormat.UInt16};
            mesh.SetVertices(p);mesh.SetNormals(n);mesh.SetTangents(t);mesh.SetUVs(0,u);mesh.SetColors(colors);mesh.subMeshCount=triangles.Length;
            for(int i=0;i<triangles.Length;i++)mesh.SetTriangles(triangles[i],i,false);
            mesh.RecalculateBounds();return mesh;
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            LODChanged=null;
            Destroy(seamBridge);seamBridge=null;
            if(owner)
            {
                var filter=owner.GetComponent<MeshFilter>();
                if(filter&&Owns(filter.sharedMesh))filter.sharedMesh=combined?combined:owner.sourceMesh;
            }
            Destroy(bridgeObject);if(!ownsMeshes)return;
            Destroy(rock);Destroy(transition);
            foreach(var mesh in rockLODs)if(mesh&&mesh!=rock)Destroy(mesh);
            foreach(var mesh in bridgeLODs)if(mesh&&mesh!=transition)Destroy(mesh);
        }
        static void Destroy(UnityEngine.Object value)
        {if(!value)return;if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value);}
    }
}
