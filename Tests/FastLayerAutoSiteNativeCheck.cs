// Used only in the independent project created by ValidateFastLayerAutoSite.ps1.
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using LocalTerrainPrototype;

namespace LocalTerrainPrototype
{
    // Minimal data holders, not a substitute implementation of the search.
    public sealed class LTChunk : MonoBehaviour {public int x,z;public double updatedAt;}
    public struct LTFastLayerBenchmarkChunk
    {public LTChunk chunk;public MeshRenderer renderer;public Material liveMaterial;public string exclusion;}
}

public static class FastLayerAutoSiteNativeCheck
{
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    public static void Run()
    {
        int code=0;var log=new StringBuilder();
        try
        {
            var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/Probe.shader");Check(shader&&shader.isSupported,"Native probe shader");
            var go=new GameObject("Synthetic five-layer terrain");
            var chunk=go.AddComponent<LTChunk>();var renderer=go.AddComponent<MeshRenderer>();var filter=go.AddComponent<MeshFilter>();
            var mesh=new Mesh{vertices=new[]{new Vector3(0,0,0),new Vector3(32,0,0),new Vector3(0,0,32),new Vector3(32,0,32)},triangles=new[]{0,2,1,1,2,3}};
            mesh.RecalculateBounds();mesh.RecalculateNormals();filter.sharedMesh=mesh;
            var mat=new Material(shader);renderer.sharedMaterial=mat;
            mat.SetVector("_LTRect",new Vector4(0,0,32,32));mat.SetMatrix("_LTSurfaceWorldToLocal",Matrix4x4.identity);
            var maps=new Texture2D[3];
            for(int m=0;m<3;m++)
            {
                maps[m]=new Texture2D(32,32,TextureFormat.RGBA32,false,true){wrapMode=TextureWrapMode.Clamp};
                var pixels=new Color32[1024];
                for(int y=0;y<32;y++)for(int x=0;x<32;x++)
                {
                    bool blend=x>=8&&x<24&&y>=8&&y<24;
                    pixels[y*32+x]=m==0?(blend?new Color32(51,51,51,51):new Color32(255,0,0,0)):
                        m==1&&blend?new Color32(51,0,0,0):new Color32(0,0,0,0);
                }
                maps[m].SetPixels32(pixels);maps[m].Apply(false,false);mat.SetTexture("_LTWeights"+m,maps[m]);
            }
            var camera=new GameObject("Probe camera").AddComponent<Camera>();camera.orthographic=true;camera.aspect=1.7f;camera.orthographicSize=3;camera.nearClipPlane=.1f;camera.farClipPlane=200;
            var item=new LTFastLayerBenchmarkChunk{chunk=chunk,renderer=renderer,liveMaterial=mat};
            var sites=LTFastLayerBenchmarkSite.Find(new[]{item},camera,out int max,out _);
            Check(max==5&&sites.Count>0,"Native texture search finds overlap");var site=sites[0];Check(site.Locate(),"Locate on rendered mesh without any collider");
            Check(site.surface.GeometryCurrent,"Same mesh remains valid during setup");
            chunk.updatedAt++;Check(!site.surface.GeometryCurrent,"Reject in-place mesh revision during setup");chunk.updatedAt--;
            Check(Mathf.Abs(site.focus.x-16)<.01f&&Mathf.Abs(site.focus.z-16)<.01f,"Focus in real blend region");
            Check(site.surface.Count(.05f,.05f)==1,"Palette outside blend is only one contributor");
            Check(site.surface.Count(7.2f/31,.5f)==5&&site.surface.Count(7.1f/31,.5f)==1,"Coverage probes use shader endpoint coordinates at the blend boundary");
            camera.transform.SetPositionAndRotation(site.focus+Vector3.up*20,site.Rotation);
            site.Probe(camera,out int ground,out int overlap,out int min,out max);
            Check(ground==441&&overlap==441&&min==5&&max==5,"Full frame ground and five-way overlap");
            camera.transform.position+=Vector3.right*25;
            site.Probe(camera,out ground,out overlap,out min,out max);
            Check(!LTFastLayerBenchmarkPolicy.FrameSuitable(441,ground,overlap),"Reject camera off terrain");
            camera.transform.SetPositionAndRotation(site.focus-Vector3.up,site.Rotation);
            site.Probe(camera,out ground,out overlap,out min,out max);Check(ground==0,"Reject geometry behind camera");
            camera.transform.SetPositionAndRotation(site.focus+Vector3.up*20,site.Rotation);camera.farClipPlane=10;
            site.Probe(camera,out ground,out overlap,out min,out max);Check(ground==0,"Reject far-clipped terrain");camera.farClipPlane=200;
            var excluded=item;excluded.exclusion="road/asphalt";
            Check(LTFastLayerBenchmarkSite.Find(new[]{excluded},camera,out _,out string reasons).Count==0&&reasons.Contains("road/asphalt"),"Road exclusion preserved");
            var block=new MaterialPropertyBlock();block.SetFloat("_Probe",1);renderer.SetPropertyBlock(block);
            Check(LTFastLayerBenchmarkSite.Exclusion(item,camera)=="property block","Additional renderer bindings excluded");renderer.SetPropertyBlock(null);
            mesh.triangles=Array.Empty<int>();site.surface.geometry=null;
            Check(!site.Locate(),"Hole rejected using current draw mesh");mesh.triangles=new[]{0,2,1,1,2,3};site.surface.geometry=null;
            go.transform.SetPositionAndRotation(new Vector3(50,8,70),Quaternion.Euler(12,35,0));go.transform.localScale=new Vector3(2,1.2f,.8f);
            mat.SetMatrix("_LTSurfaceWorldToLocal",go.transform.worldToLocalMatrix);
            site=LTFastLayerBenchmarkSite.Find(new[]{item},camera,out _,out _)[0];Check(site.Locate(),"Transformed terrain locate");
            camera.transform.SetPositionAndRotation(site.focus+go.transform.up*20,site.Rotation);camera.orthographicSize=2;
            site.Probe(camera,out ground,out overlap,out min,out max);Check(ground==441&&overlap==441,"Rotated and scaled world aligns coverage and mesh");
            var view=ScriptableObject.CreateInstance<SceneView>();
            foreach(bool twoD in new[]{false,true})
            {
                view.in2DMode=false;view.LookAt(new Vector3(9,4,6),Quaternion.Euler(30,60,0),17,false,true);
                view.in2DMode=twoD;view.isRotationLocked=true;
                Vector3 pivot=view.pivot;Quaternion rotation=view.rotation,last3D=view.lastSceneViewRotation;
                float size=view.size;bool ortho=view.orthographic;
                var lease=new LTFastLayerBenchmarkView(view);lease.Frame(site.focus,site.Rotation,site.size);
                Check(!view.in2DMode&&view.orthographic&&!view.isRotationLocked,"Frame sets unlocked overhead orthographic view");
                Check(Vector3.Distance(view.pivot,site.focus)<.0001f,"Frame applies real surface focus");
                Check(lease.Restore(),"View restoration verifies");
                Check(view.in2DMode==twoD&&view.isRotationLocked&&view.orthographic==ortho,"Previous 2D/rotation lock/projection restored");
                Check(Vector3.Distance(view.pivot,pivot)<.0001f&&Quaternion.Angle(view.rotation,rotation)<.01f&&Mathf.Abs(view.size-size)<.0001f,"Previous pose restored");
                // Unity can leave its unused last-3D slot at the zero quaternion.
                Check(view.lastSceneViewRotation.Equals(last3D),$"Previous 3D memory restored ({last3D} -> {view.lastSceneViewRotation})");
            }
            UnityEngine.Object.DestroyImmediate(view);
            log.AppendLine($"PASS {checks} native Unity checks: coverage texture search, rendered mesh probes, holes, clipping, transforms, exclusions, SceneView framing/restoration. No live HDRP timing measured.");
        }
        catch(Exception e){code=1;log.AppendLine("FAIL "+e);}
        File.WriteAllText(Path.GetFullPath("auto-site-result.txt"),log.ToString());Debug.Log(log.ToString());EditorApplication.Exit(code);
    }
}
