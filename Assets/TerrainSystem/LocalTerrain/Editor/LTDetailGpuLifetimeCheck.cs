using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    public static class LTDetailGpuLifetimeCheck
    {
        sealed class Source : ILTDetailGpuSource
        {
            public LTDetailPrefab Recipe { get; set; }
            public LTDetailEntry Entry { get; }=new LTDetailEntry{cullDistance=200,thinInDistance=false};
            public Bounds Bounds=>new Bounds(new Vector3(0,0,5),Vector3.one*10);
            public int Count { get; set; }=50000;
            public int copied;public bool fail;
            public void CopyGpuData(int start,int count,LTDetailGpuData[] data)
            {
                if(fail)throw new InvalidOperationException("Injected upload failure");
                Require(start==copied&&count<=2048&&count<=data.Length,"incremental contiguous staging");
                for(int i=0;i<count;i++)data[i]=new LTDetailGpuData{objectToWorld=Matrix4x4.identity,worldToObject=Matrix4x4.identity,
                    positionSize=new Vector4(0,0,5,1),lodPositionRandom=new Vector4(0,0,5,.2f),center=new Vector4(0,0,5,0),extents=Vector4.one};
                copied+=count;
            }
        }
        static void Require(bool ok,string name){if(!ok)throw new Exception(name);}
        static uint[] ReadArgumentCounts(LTDetailGpuRenderer gpu,ILTDetailGpuSource source,Camera camera)
        {
            // Validation-only synchronous readback. Keep the production renderer readback-free.
            object Field(object obj,string name)=>obj.GetType().GetField(name,System.Reflection.BindingFlags.Instance|
                System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).GetValue(obj);
            var groups=(System.Collections.IDictionary)Field(gpu,"groups");
            var views=(System.Collections.IDictionary)Field(groups[source],"views");
            var draws=(System.Collections.IList)Field(views[camera],"draws");
            var counts=new uint[draws.Count];var args=new GraphicsBuffer.IndirectDrawIndexedArgs[1];
            for(int i=0;i<counts.Length;i++)
            {((GraphicsBuffer)Field(draws[i],"args")).GetData(args);counts[i]=args[0].instanceCount;}
            return counts;
        }
        [MenuItem("Tools/Local Terrain/Check GPU Buffer Lifetime")]
        public static void Run()
        {
            var report=new StringBuilder();var gpu=new LTDetailGpuRenderer();
            GameObject a=null,b=null;Mesh mesh=null;Material material=null;bool passed=false;
            try
            {
                var adapter=Resources.Load<LTDetailGpuShader>("LTDetailGpu/GrassWind");
                Require(adapter&&adapter.source&&adapter.indirect&&string.IsNullOrEmpty(adapter.error),"ready GPU adapter");
                material=new Material(adapter.source){enableInstancing=true};
                mesh=new Mesh{vertices=new[]{Vector3.zero,Vector3.right,Vector3.up},triangles=new[]{0,1,2}};
                var recipe=new LTDetailPrefab();var level=new LTDetailPrefab.Level();recipe.levels.Add(level);
                for(int i=0;i<10;i++)level.parts.Add(new LTDetailPrefab.Part{mesh=mesh,material=material,localMatrix=Matrix4x4.identity,
                    castShadows=ShadowCastingMode.Off,lightProbes=LightProbeUsage.Off});
                a=new GameObject("GPU validation camera A"){hideFlags=HideFlags.HideAndDontSave};
                b=new GameObject("GPU validation camera B"){hideFlags=HideFlags.HideAndDontSave};
                var ca=a.AddComponent<Camera>();var cb=b.AddComponent<Camera>();
                ca.enabled=cb.enabled=false;ca.cullingMask=cb.cullingMask=0; // exercise preparation, never submit scene draws
                var planes=GeometryUtility.CalculateFrustumPlanes(ca);int frame=0;
                void Audit(){Require(gpu.CheckMemoryAccounting(out var result),result);}
                void Empty(){Audit();Require(gpu.ResidentBytes==0&&gpu.ReservedBytes==0&&gpu.BufferCount==0&&gpu.GroupCount==0&&gpu.ViewCount==0,"all buffer ownership released");}
                for(int cycle=0;cycle<8;cycle++)
                {
                    var source=new Source{Recipe=recipe};bool readyA=false,readyB=false;
                    for(int n=0;n<40;n++)
                    {
                        gpu.Begin(32,++frame,1000,1); // byte cap, not wall-clock speed, makes the test deterministic
                        readyA=gpu.TryRender(source,ca,planes,200);
                        gpu.Begin(32,frame,1000,1); // same frame: second camera must not reset the quota
                        readyB=gpu.TryRender(source,cb,planes,200);
                        Require(gpu.UploadBytes<=1024*1024,"shared camera upload byte cap");Audit();
                        Require(gpu.FallbackGroups==0,"unexpected fallback: "+gpu.Status);
                        if(n==0){Require(!readyA&&!readyB&&source.copied<source.Count,"large source spans frames");}
                        if(cycle%2==0||readyA&&readyB)break;
                    }
                    if(cycle%2==1)
                    {
                        Require(readyA&&readyB&&source.copied==source.Count,"complete upload switches both cameras without duplicate copies");
                        Require(gpu.BufferCount==23&&gpu.ViewCount==2,"one shared root; each camera owns one shared selection and ten argument buffers");
                        Require(gpu.ResidentBytes==50000L*192+2*(50000L*4+10*GraphicsBuffer.IndirectDrawIndexedArgs.size),"shared selection payload, no duplicate append buffers");
                    }
                    gpu.Remove(source);gpu.Remove(source);Empty();
                }
                report.AppendLine("PASS eight partial/complete upload and unload cycles; same-frame two-camera budget; idempotent removal.");
                var shared=new Source{Recipe=recipe,Count=65};ca.cullingMask=1;
                bool ready=false;
                for(int n=0;n<20&&!ready;n++){gpu.Begin(32,++frame,1000,1);ready=gpu.TryRender(shared,ca,planes,200);Audit();}
                Require(ready&&gpu.DrawCalls==10&&gpu.CullDispatches==1,"ten compatible draws share one compute dispatch");
                foreach(uint count in ReadArgumentCounts(gpu,shared,ca))Require(count==65,"shared selection copied to every indirect argument buffer");
                gpu.Remove(shared);Empty();
                report.AppendLine("PASS ten compatible parts: one dispatch, ten independent draw arguments, all 65 selected instances retained.");
                var partitions=new LTDetailPrefab{hasLODGroup=true};
                for(int lod=0;lod<2;lod++)
                {
                    var partition=new LTDetailPrefab.Level{screenRelativeHeight=lod==0?.5f:.01f};partitions.levels.Add(partition);
                    for(int repeat=0;repeat<2;repeat++)for(int mode=0;mode<4;mode++)
                        partition.parts.Add(new LTDetailPrefab.Part{mesh=mesh,material=material,localMatrix=Matrix4x4.identity,
                            castShadows=(ShadowCastingMode)mode,lightProbes=LightProbeUsage.Off});
                }
                var split=new Source{Recipe=partitions,Count=65};split.Entry.limitShadowDistance=true;split.Entry.shadowDistance=6;
                ca.fieldOfView=60;ready=false;
                int oldMaxLod=QualitySettings.maximumLODLevel;float oldBias=QualitySettings.lodBias;
                try
                {
                    QualitySettings.maximumLODLevel=0;QualitySettings.lodBias=1;
                    for(int n=0;n<20&&!ready;n++){gpu.Begin(32,++frame,1000,1);ready=gpu.TryRender(split,ca,planes,200);Audit();}
                    Require(ready&&gpu.DrawCalls==24&&gpu.CullDispatches==12,"LOD and all shadow modes retain separate selection partitions");
                    Require(gpu.BufferCount==37,"one root, twelve selections, twenty-four arguments");
                    var counts=ReadArgumentCounts(gpu,split,ca);
                    for(int i=0;i<12;i++)Require(counts[i]==0,"unselected LOD draws empty");
                    uint[] expected={65,65,0,65,0,65};
                    for(int i=12;i<24;i++)Require(counts[i]==expected[(i-12)%6],"near shadow partition counts match");
                    split.Entry.shadowDistance=0;gpu.Begin(32,++frame,1000,1);Require(gpu.TryRender(split,ca,planes,200),"ready partition reuse");
                    counts=ReadArgumentCounts(gpu,split,ca);uint[] far={65,0,65,0,65,0};
                    for(int i=12;i<24;i++)Require(counts[i]==far[(i-12)%6],"far shadow partition counts match; ShadowsOnly never becomes color");
                }
                finally{QualitySettings.maximumLODLevel=oldMaxLod;QualitySettings.lodBias=oldBias;}
                gpu.Remove(split);Empty();ca.cullingMask=0;
                report.AppendLine("PASS independent LOD/shadow partitions, duplicated parts share selections, near/far argument counts and memory ownership.");
                var failure=new Source{Recipe=recipe,fail=true};gpu.Begin(32,++frame,1000,1);
                Require(!gpu.TryRender(failure,ca,planes,200)&&gpu.FallbackGroups==1,"upload failure uses fallback");Empty();
                Require(gpu.GetFallbackReport().Contains("Injected upload failure"),"fallback report names failure");
                report.AppendLine("PASS injected copy failure releases partially allocated resources.");
                var large=new Source{Recipe=recipe,Count=100000};gpu.Begin(32,++frame,1000,1);gpu.TryRender(large,ca,planes,200);Audit();
                gpu.Begin(16,++frame,1000,1);Empty();
                Require(!gpu.TryRender(large,ca,planes,200)&&gpu.GroupCount==0,"memory limit rejects entire reservation before allocation");
                report.AppendLine("PASS shrinking memory cap and oversized group preflight.");
                var pending=new Source{Recipe=recipe};gpu.Begin(32,++frame,1000,1);gpu.TryRender(pending,ca,planes,200);
                UnityEngine.Object.DestroyImmediate(a);a=null;gpu.Begin(32,++frame,1000,1);Audit();
                Require(gpu.ViewCount==0&&gpu.ReservedBytes==(long)pending.Count*LTDetailGpuData.Stride,"destroyed camera releases pending view reservation");
                gpu.Dispose();gpu.Dispose();Empty();
                Require(gpu.AllocatedBytes==gpu.ReleasedBytes,"allocation/release totals balance after shutdown");
                report.AppendLine("PASS camera destruction and repeated disposal; allocated/released "+gpu.AllocatedBytes+" bytes.");
                report.AppendLine("No image/FPS or driver-wide VRAM measurement. Disabled cameras: submission/arguments tested, not HDRP image rendering.");passed=true;
            }
            catch(Exception ex){report.AppendLine("FAIL: "+ex);Debug.LogException(ex);}
            finally
            {
                gpu.Dispose();if(a)UnityEngine.Object.DestroyImmediate(a);if(b)UnityEngine.Object.DestroyImmediate(b);
                if(mesh)UnityEngine.Object.DestroyImmediate(mesh);if(material)UnityEngine.Object.DestroyImmediate(material);
            }
            Directory.CreateDirectory("Logs/DetailProfiles");File.WriteAllText("Logs/DetailProfiles/gpu-buffer-lifetime-check.txt",report.ToString());
            Debug.Log(report.ToString());if(Application.isBatchMode)EditorApplication.Exit(passed?0:1);
        }
    }
}
