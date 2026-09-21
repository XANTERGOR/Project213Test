using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

[Serializable]
public class SandstormCheapShadowPass : CustomPass
{
    public bool shadowEnabled = true;
    [Tooltip("Custom Pass Volume containing the source SandstormRaymarchPass.")]
    public CustomPassVolume source;
    public ComputeShader shadowCompute;
    public Shader shadowShader;
    public enum MaskResolution { R64=64, R128=128, R256=256, R512=512 }
    public MaskResolution resolution = MaskResolution.R128;
    [Tooltip("Approximate blur radius in world metres. Coarse mask, no detailed noise.")]
    [Range(0f,50f)] public float softness = 5f;
    [Range(0f,1f)] public float strength = 0.45f;
    public Color shadowColor = new Color(0.25f,0.22f,0.2f,1f);
    [Range(1f,30f)] public float updatesPerSecond = 8f;
    [Tooltip("Artistic translation of the finished shadow in world metres. X moves along world X; Y moves along world Z. Does not move the cloud or rebuild the mask.")]
    public Vector2 shadowOffsetXZ = Vector2.zero;
    [Header("Shadow Center Falloff")]
    public bool useCenterFalloff = false;
    public enum FalloffOrigin { BelowCloudCenter, ProjectedShadowCenter }
    public FalloffOrigin falloffOrigin = FalloffOrigin.BelowCloudCenter;
    [Tooltip("Multiplier of Strength near the center. Final shadow opacity is clamped to 1.")]
    [Range(0f,2f)] public float centerStrength = 1f;
    [Tooltip("Multiplier of Strength beyond Outer Radius. Zero fades the shadow away.")]
    [Range(0f,2f)] public float edgeStrength = 0f;
    [Min(0f)] public float innerRadius = 20f;
    [Min(0.01f)] public float outerRadius = 120f;
    [Tooltip("Transition curve: 1 is smooth; higher retains the dark center farther out.")]
    [Range(0.25f,4f)] public float falloffPower = 1f;
    [Tooltip("Additional world X/Z offset of the gradient center, without moving the shadow mask.")]
    public Vector2 falloffCenterOffsetXZ = Vector2.zero;
    [Header("Ground Receiver")]
    public float groundHeight = 0f;
    [Tooltip("Only visible surfaces within this vertical distance from Ground Height receive the tint. Buildings in the band can also be tinted.")]
    [Min(0.01f)] public float receiverHeightRange = 2f;
    [NonSerialized] RenderTexture mask, scratch;
    [NonSerialized] ComputeShader computeInstance;
    [NonSerialized] Material material;
    [NonSerialized] MaterialPropertyBlock properties;
    Vector4 bakedRect;
    float bakedGround;
    Vector2 bakedCloudCenter;
    double nextUpdate;
    bool valid;
    SandstormRaymarchPass previousSource;
    ComputeShader previousCompute;
    bool warning;

    void ReleaseMaps()
    {
        if(mask!=null) { mask.Release();CoreUtils.Destroy(mask);mask=null; }
        if(scratch!=null) { scratch.Release();CoreUtils.Destroy(scratch);scratch=null; }
        valid=false;
    }
    RenderTexture CreateMap(int size)
    {
        var rt=new RenderTexture(size,size,0,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear);
        rt.enableRandomWrite=true;rt.useMipMap=false;rt.filterMode=FilterMode.Bilinear;
        rt.wrapMode=TextureWrapMode.Clamp;rt.hideFlags=HideFlags.HideAndDontSave;
        if(!rt.Create()) { CoreUtils.Destroy(rt);return null; }
        return rt;
    }
    protected override void Execute(CustomPassContext ctx)
    {
        if(!shadowEnabled) { ReleaseMaps();return; }
        if(source==null || shadowCompute==null || shadowShader==null || !source.gameObject.activeInHierarchy) return;
        if(ctx.hdCamera.camera.stereoEnabled || ctx.hdCamera.camera.orthographic) return;
        if(!SystemInfo.supportsComputeShaders || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RFloat))
        {
            if(!warning) { Debug.LogWarning("Sandstorm cheap shadow requires writable RFloat compute textures.");warning=true; }
            return;
        }
        if(!shadowCompute.HasKernel("BuildMask") || !shadowCompute.HasKernel("BlurMask")) return;
        SandstormRaymarchPass storm=null;
        foreach(var pass in source.customPasses)
            if(pass is SandstormRaymarchPass candidate && candidate.enabled) { storm=candidate;break; }
        if(storm==null || storm.bounds==null || !storm.bounds.gameObject.activeInHierarchy) return;
        if(storm!=previousSource || shadowCompute!=previousCompute)
        {
            valid=false;
            if(previousCompute!=shadowCompute)
            { CoreUtils.Destroy(computeInstance);computeInstance=null; }
            previousSource=storm;previousCompute=shadowCompute;
        }
        if(computeInstance==null)
        {
            computeInstance=UnityEngine.Object.Instantiate(shadowCompute);
            computeInstance.hideFlags=HideFlags.HideAndDontSave;
            valid=false;
        }
        int size=Mathf.Clamp((int)resolution,64,512);
        if(mask==null || scratch==null || mask.width!=size || !mask.IsCreated() || !scratch.IsCreated())
        { ReleaseMaps();mask=CreateMap(size);scratch=CreateMap(size); }
        if(mask==null || scratch==null) return;
        double now=Time.realtimeSinceStartupAsDouble;
        if(!valid || now>=nextUpdate || now<nextUpdate-2)
        {
            Vector3 sun=storm.sun!=null ? -storm.sun.transform.forward : new Vector3(-.6f,.7f,-.3f).normalized;
            // Nearly horizontal sunlight projects an impractically large footprint.
            if(sun.y<=0.05f) { valid=false;return; }
            storm.GetCheapShadowFrames(out Texture3D a,out Texture3D b,out float blend);
            if(a==null || b==null) { valid=false;return; }
            float x0=float.PositiveInfinity,z0=float.PositiveInfinity,x1=float.NegativeInfinity,z1=float.NegativeInfinity;
            for(int z=0;z<2;z++) for(int y=0;y<2;y++) for(int x=0;x<2;x++)
            {
                Vector3 p=storm.bounds.TransformPoint(new Vector3(x-.5f,y-.5f,z-.5f));
                Vector3 projected=p-sun*((p.y-groundHeight)/sun.y);
                x0=Mathf.Min(x0,projected.x);x1=Mathf.Max(x1,projected.x);
                z0=Mathf.Min(z0,projected.z);z1=Mathf.Max(z1,projected.z);
            }
            float blur=Mathf.Clamp(softness,0,50);
            float pad=blur*3+Mathf.Max(0.1f,Mathf.Max(x1-x0,z1-z0)*2/size);
            bakedRect=new Vector4(x0-pad,z0-pad,Mathf.Max(.01f,x1-x0+2*pad),Mathf.Max(.01f,z1-z0+2*pad));
            bakedGround=groundHeight;
            bakedCloudCenter=new Vector2(storm.bounds.position.x,storm.bounds.position.z);
            int build=computeInstance.FindKernel("BuildMask");
            ctx.cmd.BeginSample("Sandstorm Cheap Shadow Update");
            ctx.cmd.SetComputeIntParam(computeInstance,"_ShadowSize",size);
            ctx.cmd.SetComputeTextureParam(computeInstance,build,"_ShadowDensityA",a);
            ctx.cmd.SetComputeTextureParam(computeInstance,build,"_ShadowDensityB",b);
            ctx.cmd.SetComputeTextureParam(computeInstance,build,"_ShadowOutput",mask);
            ctx.cmd.SetComputeMatrixParam(computeInstance,"_ShadowWorldToLocal",storm.bounds.worldToLocalMatrix);
            ctx.cmd.SetComputeVectorParam(computeInstance,"_ShadowRect",bakedRect);
            ctx.cmd.SetComputeVectorParam(computeInstance,"_ShadowSun",sun);
            ctx.cmd.SetComputeFloatParam(computeInstance,"_ShadowGround",groundHeight);
            ctx.cmd.SetComputeFloatParam(computeInstance,"_ShadowBlend",blend);
            ctx.cmd.SetComputeVectorParam(computeInstance,"_ShadowAxes",new Vector4(storm.swapYZ?1:0,storm.flipX?1:0,storm.flipY?1:0,storm.flipZ?1:0));
            ctx.cmd.SetComputeVectorParam(computeInstance,"_ShadowDensityParams",new Vector4(Mathf.Max(0,storm.densityMultiplier),Mathf.Max(0,storm.densityThreshold),Mathf.Max(0,storm.extinction),storm.edgeFade));
            ctx.cmd.DispatchCompute(computeInstance,build,(size+7)/8,(size+7)/8,1);
            if(blur>0)
            {
                int k=computeInstance.FindKernel("BlurMask");
                ctx.cmd.SetComputeTextureParam(computeInstance,k,"_BlurInput",mask);
                ctx.cmd.SetComputeTextureParam(computeInstance,k,"_ShadowOutput",scratch);
                ctx.cmd.SetComputeVectorParam(computeInstance,"_BlurDirection",new Vector4(blur/bakedRect.z,0,0,0));
                ctx.cmd.DispatchCompute(computeInstance,k,(size+7)/8,(size+7)/8,1);
                ctx.cmd.SetComputeTextureParam(computeInstance,k,"_BlurInput",scratch);
                ctx.cmd.SetComputeTextureParam(computeInstance,k,"_ShadowOutput",mask);
                ctx.cmd.SetComputeVectorParam(computeInstance,"_BlurDirection",new Vector4(0,blur/bakedRect.w,0,0));
                ctx.cmd.DispatchCompute(computeInstance,k,(size+7)/8,(size+7)/8,1);
            }
            ctx.cmd.EndSample("Sandstorm Cheap Shadow Update");
            valid=true;nextUpdate=now+1.0/Mathf.Clamp(updatesPerSecond,1,30);
        }
        if(!valid) return;
        if(material==null || material.shader!=shadowShader)
        { CoreUtils.Destroy(material);material=CoreUtils.CreateEngineMaterial(shadowShader); }
        if(properties==null) properties=new MaterialPropertyBlock();
        properties.SetTexture("_CheapShadowMask",mask);
        Vector4 shiftedRect=bakedRect;
        shiftedRect.x+=shadowOffsetXZ.x;
        shiftedRect.y+=shadowOffsetXZ.y;
        properties.SetVector("_CheapShadowRect",shiftedRect);
        properties.SetFloat("_CheapShadowGround",bakedGround);
        properties.SetFloat("_CheapShadowHeightRange",Mathf.Max(.01f,receiverHeightRange));
        properties.SetFloat("_CheapShadowStrength",Mathf.Clamp01(strength));
        Vector2 origin=falloffOrigin==FalloffOrigin.BelowCloudCenter ? bakedCloudCenter :
            new Vector2(bakedRect.x+bakedRect.z*0.5f,bakedRect.y+bakedRect.w*0.5f);
        origin+=shadowOffsetXZ+falloffCenterOffsetXZ;
        float inner=Mathf.Max(0,innerRadius),outer=Mathf.Max(inner+0.01f,outerRadius);
        properties.SetVector("_ShadowFalloffArea",new Vector4(origin.x,origin.y,inner,outer));
        properties.SetVector("_ShadowFalloffSettings",new Vector4(useCenterFalloff?1:0,
            Mathf.Clamp(centerStrength,0,2),Mathf.Clamp(edgeStrength,0,2),Mathf.Clamp(falloffPower,0.25f,4f)));
        properties.SetColor("_CheapShadowColor",shadowColor.linear);
        ctx.cmd.DisableScissorRect();
        CoreUtils.SetRenderTarget(ctx.cmd,ctx.cameraColorBuffer,ctx.cameraDepthBuffer,ClearFlag.None);
        ctx.cmd.SetViewport(new Rect(0,0,ctx.hdCamera.actualWidth,ctx.hdCamera.actualHeight));
        ctx.cmd.BeginSample("Sandstorm Cheap Shadow Apply");
        CoreUtils.DrawFullScreen(ctx.cmd,material,properties,shaderPassId:0);
        ctx.cmd.EndSample("Sandstorm Cheap Shadow Apply");
    }
    protected override void Cleanup()
    {
        ReleaseMaps();CoreUtils.Destroy(material);material=null;
        CoreUtils.Destroy(computeInstance);computeInstance=null;
        properties=null;previousSource=null;previousCompute=null;nextUpdate=0;
    }
}
