using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

[Serializable]
public class SandstormGroupPass : CustomPass
{
    [Tooltip("Assign SandstormGroup.shader.")]
    public Shader groupShader;
    [Tooltip("Up to eight source Custom Pass Volume components. Keep GameObjects and individual Sandstorm passes enabled. Source volume components may stay enabled: individual drawing is suppressed automatically.")]
    public List<CustomPassVolume> sources = new List<CustomPassVolume>();
    public bool halfResolution = true;
    [Range(0.001f,0.2f)] public float depthTolerance = 0.02f;
    public bool refineDepthEdges = true;
    Material material;
    MaterialPropertyBlock properties;
    readonly HashSet<SandstormRaymarchPass> owned = new HashSet<SandstormRaymarchPass>();
    readonly HashSet<SandstormRaymarchPass> current = new HashSet<SandstormRaymarchPass>();
    static readonly int LowResId=Shader.PropertyToID("_SandstormLowResColor");
    bool warned;

    // Resolve membership before any pass draws, independent of hierarchy/execution order.
    // Scene queries deliberately are not cached across editor changes or camera renders.
    public static SandstormGroupPass FindOwner(SandstormRaymarchPass source)
    {
        SandstormGroupPass owner=null;
        int ownerId=int.MaxValue;
        foreach(var volume in Resources.FindObjectsOfTypeAll<CustomPassVolume>())
        {
            if(volume==null || !volume.enabled || !volume.gameObject.activeInHierarchy ||
                !volume.gameObject.scene.IsValid() || !volume.gameObject.scene.isLoaded) continue;
            foreach(var pass in volume.customPasses)
            {
                var group=pass as SandstormGroupPass;
                if(group==null || !group.enabled || group.groupShader==null || group.sources==null) continue;
                bool contains=false;
                var unique=new HashSet<SandstormRaymarchPass>();
                foreach(var input in group.sources)
                {
                    if(input==null || !input.gameObject.activeInHierarchy) continue;
                    foreach(var candidate in input.customPasses)
                    {
                        var storm=candidate as SandstormRaymarchPass;
                        if(storm==null || !storm.enabled || storm.bounds==null || !storm.bounds.gameObject.activeInHierarchy || !unique.Add(storm)) continue;
                        if(unique.Count<=8 && ReferenceEquals(storm,source)) contains=true;
                    }
                }
                if(contains && volume.GetInstanceID()<ownerId) { owner=group; ownerId=volume.GetInstanceID(); }
            }
        }
        return owner;
    }

    protected override void Execute(CustomPassContext ctx)
    {
        if(groupShader==null || ctx.hdCamera.camera.orthographic || ctx.hdCamera.camera.stereoEnabled) return;
        if(material==null || material.shader!=groupShader)
        { CoreUtils.Destroy(material); material=CoreUtils.CreateEngineMaterial(groupShader); }
        if(properties==null) properties=new MaterialPropertyBlock();
        properties.Clear(); current.Clear();
        int count=0;
        if(sources!=null) foreach(var volume in sources)
        {
            if(volume==null || !volume.gameObject.activeInHierarchy) continue;
            foreach(var pass in volume.customPasses)
            {
                var storm=pass as SandstormRaymarchPass;
                if(storm==null || !storm.enabled || storm.bounds==null || !storm.bounds.gameObject.activeInHierarchy || current.Contains(storm)) continue;
                if(!ReferenceEquals(FindOwner(storm),this)) continue;
                if(count>=8)
                {
                    if(!warned) { Debug.LogWarning("Sandstorm Group supports at most eight source clouds. Extra clouds are not rendered by this group."); warned=true; }
                    continue;
                }
                current.Add(storm); owned.Add(storm);
                if(storm.BindGroupParameters(ctx,properties,count)) count++;
            }
        }
        var removed=new List<SandstormRaymarchPass>();
        foreach(var storm in owned) if(!current.Contains(storm)) removed.Add(storm);
        foreach(var storm in removed) { if(FindOwner(storm)==null) storm.ReleaseGroupResources(); owned.Remove(storm); }
        if(count==0) return;
        properties.SetInt("_VolumeCount",count);
        // Bind valid fallback textures for unused statically declared slots.
        string[] textures={"_DensityTex","_DensityTexB","_StormEdgeNoiseTex","_StormInteriorNoiseTex","_StormOccupancy","_SunLightCache"};
        for(int i=count;i<8;i++) foreach(string name in textures)
            properties.SetTexture("_V"+i+name,properties.GetTexture("_V0"+name));
        int width=Mathf.Max(1,ctx.hdCamera.actualWidth),height=Mathf.Max(1,ctx.hdCamera.actualHeight);
        properties.SetVector("_StormFullSize",new Vector4(width,height,1f/width,1f/height));
        properties.SetFloat("_StormHalfPass",halfResolution ? 1 : 0);
        CoreUtils.SetRenderTarget(ctx.cmd,ctx.cameraColorBuffer,ctx.cameraDepthBuffer,ClearFlag.None);
        ctx.cmd.SetViewport(new Rect(0,0,width,height));
        if(!halfResolution)
        {
            ctx.cmd.BeginSample("Sandstorm Group Raymarch");
            CoreUtils.DrawFullScreen(ctx.cmd,material,properties,shaderPassId:0);
            ctx.cmd.EndSample("Sandstorm Group Raymarch"); return;
        }
        int w=(width+1)/2,h=(height+1)/2;
        properties.SetVector("_StormLowSize",new Vector4(w,h,1f/w,1f/h));
        properties.SetFloat("_StormDepthTolerance",Mathf.Max(0.001f,depthTolerance));
        properties.SetFloat("_StormRefineEdges",refineDepthEdges ? 1 : 0);
        ctx.cmd.GetTemporaryRT(LowResId,w,h,0,FilterMode.Point,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
        ctx.cmd.SetRenderTarget(new RenderTargetIdentifier(LowResId));
        ctx.cmd.SetViewport(new Rect(0,0,w,h));ctx.cmd.ClearRenderTarget(false,true,Color.clear);
        ctx.cmd.BeginSample("Sandstorm Group Raymarch Half");
        CoreUtils.DrawFullScreen(ctx.cmd,material,properties,shaderPassId:0);
        ctx.cmd.EndSample("Sandstorm Group Raymarch Half");
        CoreUtils.SetRenderTarget(ctx.cmd,ctx.cameraColorBuffer,ctx.cameraDepthBuffer,ClearFlag.None);
        ctx.cmd.SetViewport(new Rect(0,0,width,height));
        ctx.cmd.SetGlobalTexture(LowResId,new RenderTargetIdentifier(LowResId));
        ctx.cmd.BeginSample("Sandstorm Group Upsample");
        CoreUtils.DrawFullScreen(ctx.cmd,material,properties,shaderPassId:1);
        ctx.cmd.EndSample("Sandstorm Group Upsample");
        ctx.cmd.ReleaseTemporaryRT(LowResId);
    }
    protected override void Cleanup()
    {
        foreach(var storm in owned)
        { var owner=FindOwner(storm); if(owner==null || ReferenceEquals(owner,this)) storm.ReleaseGroupResources(); }
        owned.Clear(); current.Clear();
        CoreUtils.Destroy(material);material=null;
    }
}
