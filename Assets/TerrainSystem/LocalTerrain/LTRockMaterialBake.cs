using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LocalTerrainPrototype
{
    public sealed class LTRockMaterialBake : IDisposable
    {
        public RenderTexture color, normal;
        public static LTRockMaterialBake Build(Mesh mesh,Transform transform,LTWorld world,Material[] materials,int resolution)
        {
            var shader=Resources.Load<Shader>("LTRockSurfaceBake");var padShader=Resources.Load<Shader>("LTRockAtlasPadding");
            if(!shader||!shader.isSupported||!padShader||!padShader.isSupported)throw new InvalidOperationException("Rock atlas bake shaders unavailable.");
            if(SystemInfo.supportedRenderTargetCount<2)throw new InvalidOperationException("Rock atlas requires two render targets.");
            if(!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))throw new InvalidOperationException("Rock atlas requires ARGBHalf.");
            if(materials==null||materials.Length<mesh.subMeshCount)throw new ArgumentException("Missing rock submesh materials.");
            var previousTarget=RenderTexture.active;
            var result=new LTRockMaterialBake();Material pad=null;RenderTexture a=null,b=null;
            var bakeMaterials=new Material[materials.Length];
            try
            {
                RenderTexture New(string name)
                {
                    var rt=new RenderTexture(resolution,resolution,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear)
                    {name=name,hideFlags=HideFlags.HideAndDontSave,useMipMap=true,autoGenerateMips=false,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
                    if(!rt.Create()){Destroy(rt);throw new InvalidOperationException("Cannot allocate rock atlas.");}return rt;
                }
                result.color=New("Rock far / color + AO");result.normal=New("Rock far / normal + surface");
                using(var commands=new CommandBuffer{name="Local Terrain / rock surface atlas"})
                {
                    commands.SetRenderTarget(new[]{new RenderTargetIdentifier(result.color),new RenderTargetIdentifier(result.normal)},BuiltinRenderTextureType.None);
                    commands.ClearRenderTarget(false,true,new Color(0,0,0,-1));
                    for(int sub=0;sub<mesh.subMeshCount;sub++)
                    {
                        if(mesh.GetIndexCount(sub)==0)continue;
                        var material=bakeMaterials[sub]=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
                        material.CopyPropertiesFromMaterial(materials[sub]);
                        material.SetMatrix("_LTBakeLocalToTerrain",world.transform.worldToLocalMatrix*transform.localToWorldMatrix);
                        material.SetMatrix("_LTBakeLocalToWorld",transform.localToWorldMatrix);
                        material.SetMatrix("_LTBakeNormalToWorld",transform.worldToLocalMatrix.transpose);
                        material.SetMatrix("_LTSurfaceWorldToLocal",world.transform.worldToLocalMatrix);
#if UNITY_EDITOR
                        // An async loading/error variant must never become a cached atlas.
                        UnityEditor.ShaderUtil.CompilePass(material,0,true);
                        if(UnityEditor.ShaderUtil.ShaderHasError(shader))throw new InvalidOperationException("Rock surface baker compilation failed.");
#endif
                        commands.DrawMesh(mesh,Matrix4x4.identity,material,sub,0);
                    }
                    Graphics.ExecuteCommandBuffer(commands);
                }
                a=RenderTexture.GetTemporary(resolution,resolution,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
                b=RenderTexture.GetTemporary(resolution,resolution,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
                pad=new Material(padShader){hideFlags=HideFlags.HideAndDontSave};
#if UNITY_EDITOR
                for(int pass=0;pass<2;pass++)UnityEditor.ShaderUtil.CompilePass(pad,pass,true);
                if(UnityEditor.ShaderUtil.ShaderHasError(padShader))throw new InvalidOperationException("Rock atlas padding compilation failed.");
#endif
                // Eight pixels of dilation match the generated unwrap margin. Source charts
                // never use camera projection or terrain XZ, including on vertical faces.
                for(int pass=0;pass<8;pass++)
                {
                    pad.SetTexture("_LTRockPadNormal",result.normal);
                    Graphics.Blit(result.color,a,pad,0);Graphics.Blit(result.color,b,pad,1);
                    Graphics.CopyTexture(a,0,0,result.color,0,0);Graphics.CopyTexture(b,0,0,result.normal,0,0);
                }
                result.color.GenerateMips();result.normal.GenerateMips();return result;
            }
            catch{result.Dispose();throw;}
            finally
            {
                RenderTexture.active=previousTarget;
                foreach(var material in bakeMaterials)Destroy(material);Destroy(pad);
                if(a)RenderTexture.ReleaseTemporary(a);if(b)RenderTexture.ReleaseTemporary(b);
            }
        }
        public void Bind(Material material,LTMeshStamp stamp)
        {
            material.SetTexture("_LTRockFarColor",color);material.SetTexture("_LTRockFarNormal",normal);
            material.SetVector("_LTRockFarParams",new Vector4(stamp.simplifyDistantMaterial?1:0,stamp.materialFarStart,stamp.materialFarEnd,0));
        }
        public void Dispose(){Destroy(color);Destroy(normal);color=null;normal=null;}
        static void Destroy(UnityEngine.Object value)
        {if(!value)return;if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value);}
    }
}
