using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace LocalTerrainPrototype
{
    // Read-only inspection of the actual renderer bindings. No bake/Refresh calls,
    // no scene activation/selection changes, no edits to materials or meshes.
    public static class LTRoadGroundSceneCheck
    {
        [MenuItem("Tools/Local Terrain/Diagnose Road Ground Blend (no scene changes)")]
        public static void Run()
        {
            var roads=Object.FindObjectsByType<LTRoad>(FindObjectsInactive.Include,FindObjectsSortMode.None)
                .Where(r=>r.mode==LTRoadMode.Asphalt&&r.groundBlend&&r.gameObject.scene.IsValid()&&!EditorUtility.IsPersistent(r)).ToArray();
            var scenes=Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Select(s=>(scene:s,dirty:s.isDirty,roots:s.rootCount)).ToArray();
            var files=scenes.Select(s=>s.scene.path).Where(File.Exists).Distinct().ToDictionary(p=>p,Hash);
            var report=new StringBuilder("[Road Ground Scene v2]\n");
            var previous=RenderTexture.active;bool srgb=GL.sRGBWrite,wire=GL.wireframe,async=ShaderUtil.allowAsyncCompilation;
            var owned=new List<Object>();Exception failure=null;
            T Own<T>(T o)where T:Object{o.hideFlags=HideFlags.HideAndDontSave;owned.Add(o);return o;}
            try
            {
                GL.sRGBWrite=false;GL.wireframe=false;ShaderUtil.allowAsyncCompilation=false;
                var shader=UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/TerrainSystem/LocalTerrain/Editor/LTRoadGroundProbe.shader");if(!shader)throw new InvalidOperationException("Probe shader missing.");
                var rt=Own(new RenderTexture(1,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));rt.Create();
                var read=Own(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                var camera=SceneView.lastActiveSceneView?SceneView.lastActiveSceneView.camera:null;
                report.AppendLine("Roads="+roads.Length+"; Scene camera="+(camera?camera.transform.position.ToString("R"):"none"));
                foreach(var road in roads)
                {
                    var world=road.World;
                    report.AppendLine($"ROAD {road.name}, active={road.isActiveAndEnabled}, position={road.transform.position.ToString("R")}, layer={road.groundLayer?.name}, status={road.meshLODStatus}");
                    report.AppendLine($"GROUND PROJECTION: saved={road.projection}, effective={road.GroundProjection}");
                    report.AppendLine($"GROUND SOURCE: {road.groundBlendSource}; {world?.RoadTerrainBlendStatus}");
                    report.AppendLine("BAKE ERROR: "+LTRoadBakeQueue.Error(road));
                    report.AppendLine("TERRAIN STATUS: "+(world?world.paintStatus:"no world"));
                    report.AppendLine($"TERRAIN autoUpdate={world?.autoUpdate}, layerPainting={world?.enableLayerPainting}, worldToLocal="+(world?world.transform.worldToLocalMatrix.ToString("R"):"none"));
                    if(!road.generatedRoot){report.AppendLine("NO GENERATED ROOT");continue;}
                    foreach(var lod in road.generatedRoot.GetComponentsInChildren<LTRoadLOD>(true).Where(l=>l.owner==road&&l.gameObject.activeInHierarchy).Take(2))
                    {
                        var renderer=lod.GetComponent<MeshRenderer>();var binding=lod.GetComponent<LTRoadGroundBlend>();var block=new MaterialPropertyBlock();renderer.GetPropertyBlock(block);
                        report.AppendLine($"SEGMENT {lod.name}, LOD={lod.current}, binding={(binding?binding.isActiveAndEnabled:false)}, blockEmpty={block.isEmpty}");
                        var maskMatrix=block.GetMatrix("_LTRoadBlendWorldToMask");var groundMatrix=block.GetMatrix("_LTSurfaceWorldToLocal");
                        report.AppendLine("MPB MASK="+maskMatrix.ToString("R")+" GROUND="+groundMatrix.ToString("R"));
                        if(binding)report.AppendLine("EXPECTED MASK="+(binding.roadToMask*road.transform.worldToLocalMatrix).ToString("R"));
                        var material=renderer.sharedMaterials.FirstOrDefault(m=>m&&m.HasProperty("_LTRoadBlendDistances"));
                        if(!material){report.AppendLine("NO BLEND MATERIAL");continue;}
                        // Matrices are supplied by the renderer's property block above;
                        // they are not serialized ShaderLab material properties.
                        report.AppendLine($"MATERIAL {material.name} shader={material.shader.name} mask={material.GetTexture("_LTRoadBlendDistances")?.name}, rect={material.GetVector("_LTRoadBlendRect")}, params={material.GetVector("_LTRoadBlendParams")}");
                        var probe=Own(new Material(shader));probe.CopyPropertiesFromMaterial(material);probe.shaderKeywords=Array.Empty<string>();
                        probe.SetVector("_LTProbeNormal",Vector3.up);
                        probe.SetMatrix("_LTRoadBlendWorldToMask",maskMatrix);probe.SetMatrix("_LTSurfaceWorldToLocal",groundMatrix);
                        if(road.AutomaticGroundBlend){report.AppendLine("Automatic terrain probe: use Validate Automatic Road Ground Blend; original single-layer pixel probe skipped.");continue;}
                        float Pixel(Vector3 p)
                        {
                            probe.SetVector("_LTProbePosition",new Vector4(p.x,p.y,p.z,2));RenderTexture.active=rt;GL.Clear(false,true,Color.magenta);
                            if(!probe.SetPass(0))throw new InvalidOperationException("Probe SetPass failed.");Graphics.DrawProceduralNow(MeshTopology.Triangles,3);
                            read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();return read.GetPixel(0,0).g;
                        }
                        var mesh=lod.meshes[0];var vertices=mesh.vertices;int count=0;float max=0,sum=0;
                        Vector3 maxPoint=Vector3.zero;
                        for(int i=0;i<vertices.Length;i+=Math.Max(1,vertices.Length/48))
                        {
                            var point=lod.transform.TransformPoint(vertices[i]);float value=Pixel(point);
                            if(value>max){max=value;maxPoint=point;}sum+=value;count++;
                        }
                        report.AppendLine($"GPU WITH ACTUAL BINDING: samples={count}, max={max:R}, mean={sum/count:R}, maxPoint={maxPoint.ToString("R")}");
                        if(binding&&world)
                        {
                            probe.SetMatrix("_LTRoadBlendWorldToMask",binding.roadToMask*road.transform.worldToLocalMatrix);
                            probe.SetMatrix("_LTSurfaceWorldToLocal",world.transform.worldToLocalMatrix);
                            float expectedMax=0;
                            for(int i=0;i<vertices.Length;i+=Math.Max(1,vertices.Length/48))expectedMax=Math.Max(expectedMax,Pixel(lod.transform.TransformPoint(vertices[i])));
                            report.AppendLine($"GPU WITH EXPECTED BINDING: max={expectedMax:R}");
                        }
                    }
                }
            }
            catch(Exception e){failure=e;}
            finally
            {
                RenderTexture.active=previous;GL.sRGBWrite=srgb;GL.wireframe=wire;ShaderUtil.allowAsyncCompilation=async;
                for(int i=owned.Count-1;i>=0;i--)if(owned[i])Object.DestroyImmediate(owned[i]);
                foreach(var s in scenes)if(s.scene.isDirty!=s.dirty||s.scene.rootCount!=s.roots)failure=new InvalidOperationException("User scene state changed",failure);
                foreach(var file in files)if(Hash(file.Key)!=file.Value)failure=new InvalidOperationException("File changed: "+file.Key,failure);
            }
            if(failure!=null)Debug.LogError(report+"FAIL "+failure);else Debug.Log(report+"Read-only inspection complete; no scene/asset writes.");
        }
        static string Hash(string path){using(var file=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(file));}
    }
}
