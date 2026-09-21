using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

[Serializable]
public class SandstormRaymarchPass : CustomPass
{
    [Tooltip("Assign the included shader explicitly so builds retain it.")]
    public Shader raymarchShader;
    [Tooltip("Empty GameObject. Its scale is the full size of the volume in metres.")]
    public Transform bounds;
    public Texture3D densityTexture;
    [Header("Volume Animation")]
    [Tooltip("Chronological order. All frames must have identical dimensions and spatial bounds.")]
    public Texture3D[] densityFrames = new Texture3D[0];
    [Tooltip("Automatic playback in Play Mode. Disable to use Preview Position.")]
    public bool animate = true;
    [Tooltip("Play sequence/noise in Scene View without Play Mode. Sequence Animate and each Animate Noise are independent.")]
    public bool playInEditor = false;
    [Tooltip("Requested editor preview refresh rate. Does not change animation speed.")]
    [Range(1, 60)] public int editorPreviewFPS = 30;
    [Tooltip("Stored volume frames per second, not the original simulation FPS.")]
    [Min(0.01f)] public float framesPerSecond = 8;
    [Min(0)] public float playbackSpeed = 1;
    public bool loop = false;
    public bool interpolateFrames = true;
    [Tooltip("Fraction of each frame interval used for blending. 0: hard switching; 1: continuous linear blending; 0.5: hold for half, then blend. Does not change playback speed.")]
    [Range(0f, 1f)] public float interpolationStrength = 1f;
    [Range(0, 1)] public float previewPosition = 0;
    [Header("Lighting and Density")]
    public Light sun;
    public bool swapYZ = true;
    public bool flipX, flipY, flipZ;
    [Min(0)] public float densityMultiplier = 1;
    [Min(0)] public float densityThreshold = 0;
    [Tooltip("Extinction per metre, multiplied by texture density.")]
    [Min(0)] public float extinction = 0.08f;
    [Range(0, 0.2f)] public float edgeFade = 0.015f;
    [ColorUsage(false, true)] public Color dustColor = new Color(0.65f, 0.43f, 0.23f);
    [ColorUsage(false, true)] public Color ambientColor = new Color(0.35f, 0.42f, 0.55f);
    [Min(0)] public float ambientStrength = 0.3f;
    [Min(0)] public float sunStrength = 3;
    [Range(0, 0.85f)] public float anisotropy = 0.25f;
    [Range(16, 256)] public int viewSteps = 96;
    [Header("Screen Size LOD")]
    public bool screenSizeLOD = true;
    [Tooltip("Minimum view steps for small projected bounds. Never exceeds View Steps.")]
    [Range(16, 256)] public int lodMinViewSteps = 48;
    [Tooltip("Projected bounds size at which minimum steps are used. 0.1 = 10 percent of viewport width or height.")]
    [Range(0f, 0.99f)] public float lodSmallScreenSize = 0.1f;
    [Tooltip("Projected bounds size at which full View Steps are used. Must exceed Small Screen Size.")]
    [Range(0.01f, 1f)] public float lodFullScreenSize = 0.5f;
    [Header("March Quality")]

    [Range(1, 32)] public int lightSteps = 8;
    [Range(0, 1)] public float jitter = 0.5f;
    [Range(0.001f, 0.05f)] public float earlyExit = 0.01f;
    [Header("Adaptive View Steps")]
    public bool adaptiveSteps = true;
    [Range(1f, 4f)] public float maxStepMultiplier = 2f;
    [Tooltip("Accumulated opacity at which the view step starts increasing.")]
    [Range(0f, 0.99f)] public float opacityStart = 0.7f;
    [Tooltip("Accumulated opacity at which the maximum view step is reached.")]
    [Range(0.001f, 1f)] public float opacityEnd = 0.95f;
    [Header("Noise Baking and Empty Space")]
    public ComputeShader optimizationCompute;
    public bool useBakedNoise = false;
    [Range(32, 128)] public int noiseTextureSize = 64;
    public bool skipEmptySpace = false;
    [Range(8, 64)] public int occupancyGridSize = 32;
    [Tooltip("Rebuild occupancy each render for density textures modified in place.")]
    public bool forceOccupancyUpdate = false;
    RenderTexture bakedEdge, bakedInterior, occupancyRaw, occupancy;
    Vector4 bakedKeyA, bakedKeyB;
    bool bakedValid, occupancyValid, optimizationsWereBaked;
    Texture3D occupancyA, occupancyB;
    int occupancyRadius;
    ComputeShader previousOptimizationCompute;

    void ReleaseOptTexture(ref RenderTexture rt)
    {
        if (rt == null) return;
        rt.Release(); CoreUtils.Destroy(rt); rt = null;
    }
    bool EnsureOptTexture(ref RenderTexture rt, int size, bool repeat)
    {
        if (rt != null && rt.width == size && rt.IsCreated()) return true;
        ReleaseOptTexture(ref rt);
        rt = new RenderTexture(size, size, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
        rt.dimension = TextureDimension.Tex3D; rt.volumeDepth = size;
        rt.enableRandomWrite = true; rt.useMipMap = false;
        rt.wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        rt.filterMode = repeat ? FilterMode.Bilinear : FilterMode.Point;
        rt.hideFlags = HideFlags.HideAndDontSave;
        return rt.Create();
    }
    void BakeOptNoise(CustomPassContext ctx, RenderTexture rt, Vector4 key)
    {
        int k = optimizationCompute.FindKernel("BakeNoise");
        ctx.cmd.SetComputeTextureParam(optimizationCompute,k,"_OptOutput",rt);
        ctx.cmd.SetComputeIntParam(optimizationCompute,"_OptSize",rt.width);
        ctx.cmd.SetComputeIntParam(optimizationCompute,"_NoiseKind",(int)key.x);
        ctx.cmd.SetComputeIntParam(optimizationCompute,"_NoiseOctaves",(int)key.y);
        ctx.cmd.DispatchCompute(optimizationCompute,k,(rt.width+3)/4,(rt.width+3)/4,(rt.width+3)/4);
    }
    [Tooltip("Reuse identical baked noise and occupancy data across cloud copies. Does not share sun lighting.")]
    public bool shareOptimizationCaches = true;
    sealed class SharedEntry
    {
        public RenderTexture texture, scratch;
        public int users;
        public double idleSince;
    }
    static readonly Dictionary<(ComputeShader, int, int, int), SharedEntry> SharedNoise =
        new Dictionary<(ComputeShader, int, int, int), SharedEntry>();
    static readonly Dictionary<(ComputeShader, Texture3D, Texture3D, int, int), SharedEntry> SharedOccupancy =
        new Dictionary<(ComputeShader, Texture3D, Texture3D, int, int), SharedEntry>();
    SharedEntry leasedEdge, leasedInterior, leasedOccupancy;
    bool usingSharedResources;

    static void DropLease(ref SharedEntry entry)
    {
        if (entry == null) return;
        entry.users = Math.Max(0,entry.users-1);
        if (entry.users==0) entry.idleSince=Time.realtimeSinceStartupAsDouble;
        entry=null;
    }
    static void SetLease(ref SharedEntry current, SharedEntry next)
    {
        if (ReferenceEquals(current,next)) return;
        DropLease(ref current); current=next;
        if (current!=null) current.users++;
    }
    static void DestroySharedEntry(SharedEntry entry)
    {
        // Destroy through Unity rather than Release: previously queued draws may reference it.
        CoreUtils.Destroy(entry.texture); CoreUtils.Destroy(entry.scratch);
        entry.texture=entry.scratch=null;
    }
    static void PruneShared<TKey>(Dictionary<TKey,SharedEntry> entries)
    {
        var expired = new List<TKey>();
        double now=Time.realtimeSinceStartupAsDouble;
        foreach(var pair in entries)
            if(pair.Value.users==0 && now-pair.Value.idleSince>2) expired.Add(pair.Key);
        foreach(var key in expired) { DestroySharedEntry(entries[key]); entries.Remove(key); }
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetSharedCaches()
    {
        foreach(var e in SharedNoise.Values) DestroySharedEntry(e);
        foreach(var e in SharedOccupancy.Values) DestroySharedEntry(e);
        SharedNoise.Clear(); SharedOccupancy.Clear();
        lastSharedPrune=0;
    }
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoadMethod]
    static void HookSharedCacheCleanup()
    {
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= ResetSharedCaches;
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ResetSharedCaches;
        UnityEditor.EditorApplication.projectChanged -= ResetSharedCaches;
        UnityEditor.EditorApplication.projectChanged += ResetSharedCaches;
        UnityEditor.EditorApplication.quitting -= ResetSharedCaches;
        UnityEditor.EditorApplication.quitting += ResetSharedCaches;
        UnityEditor.EditorApplication.update -= PruneSharedCaches;
        UnityEditor.EditorApplication.update += PruneSharedCaches;
    }
#endif
    static double lastSharedPrune;
    static void PruneSharedCaches()
    {
        double now=Time.realtimeSinceStartupAsDouble;
        if(now-lastSharedPrune<1 && now>=lastSharedPrune) return;
        lastSharedPrune=now;
        PruneShared(SharedNoise); PruneShared(SharedOccupancy);
    }
    void LeaveSharedCaches()
    {
        DropLease(ref leasedEdge); DropLease(ref leasedInterior); DropLease(ref leasedOccupancy);
        if(usingSharedResources)
        {
            bakedEdge=bakedInterior=occupancy=occupancyRaw=null;
            bakedValid=occupancyValid=false; cacheValid=false;
            usingSharedResources=false;
        }
    }
    SharedEntry AcquireNoise(CustomPassContext ctx, Vector4 shape, Vector4 detail, int size)
    {
        var key=(optimizationCompute,(int)shape.y,(int)detail.y,size);
        if(SharedNoise.TryGetValue(key,out var entry) && entry.texture!=null && entry.texture.IsCreated()) return entry;
        if(entry!=null) DestroySharedEntry(entry);
        entry=new SharedEntry();
        if(!EnsureOptTexture(ref entry.texture,size,true)) { DestroySharedEntry(entry); return null; }
        ctx.cmd.BeginSample("Sandstorm Shared Bake Noise");
        BakeOptNoise(ctx,entry.texture,new Vector4(shape.y,detail.y,size,0));
        ctx.cmd.EndSample("Sandstorm Shared Bake Noise");
        SharedNoise[key]=entry; return entry;
    }
    SharedEntry AcquireOccupancy(CustomPassContext ctx, Texture3D a, Texture3D b, int size, int radius)
    {
        // Union is commutative; reverse playback can reuse the same entry.
        if(a.GetInstanceID()>b.GetInstanceID()) { var t=a; a=b; b=t; }
        var key=(optimizationCompute,a,b,size,radius);
        if(SharedOccupancy.TryGetValue(key,out var entry) && entry.texture!=null && entry.texture.IsCreated()) return entry;
        if(entry!=null) DestroySharedEntry(entry);
        entry=new SharedEntry();
        if(!EnsureOptTexture(ref entry.texture,size,false) || !EnsureOptTexture(ref entry.scratch,size,false))
        { DestroySharedEntry(entry); return null; }
        ctx.cmd.BeginSample("Sandstorm Shared Build Occupancy");
        int k=optimizationCompute.FindKernel("BuildOccupancy");
        ctx.cmd.SetComputeIntParam(optimizationCompute,"_OptSize",size);
        ctx.cmd.SetComputeVectorParam(optimizationCompute,"_SourceSize",new Vector4(a.width,a.height,a.depth,0));
        ctx.cmd.SetComputeTextureParam(optimizationCompute,k,"_DensityTex",a);
        ctx.cmd.SetComputeTextureParam(optimizationCompute,k,"_DensityTexB",b);
        ctx.cmd.SetComputeTextureParam(optimizationCompute,k,"_OptOutput",entry.scratch);
        ctx.cmd.DispatchCompute(optimizationCompute,k,(size+3)/4,(size+3)/4,(size+3)/4);
        k=optimizationCompute.FindKernel("DilateOccupancy");
        ctx.cmd.SetComputeIntParam(optimizationCompute,"_DilateRadius",radius);
        ctx.cmd.SetComputeTextureParam(optimizationCompute,k,"_OccInput",entry.scratch);
        ctx.cmd.SetComputeTextureParam(optimizationCompute,k,"_OptOutput",entry.texture);
        ctx.cmd.DispatchCompute(optimizationCompute,k,(size+3)/4,(size+3)/4,(size+3)/4);
        ctx.cmd.EndSample("Sandstorm Shared Build Occupancy");
        SharedOccupancy[key]=entry; return entry;
    }
    void PrepareSharedOptimizations(CustomPassContext ctx, Texture3D a, Texture3D b)
    {
        if(!usingSharedResources)
        {
            ReleaseOptTexture(ref bakedEdge); ReleaseOptTexture(ref bakedInterior);
            ReleaseOptTexture(ref occupancy); ReleaseOptTexture(ref occupancyRaw);
            bakedValid=occupancyValid=false; cacheValid=false; usingSharedResources=true;
        }
        var p=PassProperties;
        p.SetFloat("_StormBakedNoise",0); p.SetFloat("_StormSkipEmpty",0);
        p.SetTexture("_StormEdgeNoiseTex",a); p.SetTexture("_StormInteriorNoiseTex",a); p.SetTexture("_StormOccupancy",a);
        bool supported=optimizationCompute!=null && SystemInfo.supportsComputeShaders && SystemInfo.supports3DTextures &&
            SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RFloat);
        SharedEntry edge=null, interior=null, occ=null;
        if(supported && useBakedNoise && optimizationCompute.HasKernel("BakeNoise"))
        {
            int size=Mathf.Clamp(noiseTextureSize,32,128);
            edge=AcquireNoise(ctx,shapeValues[1],shapeValues[2],size);
            interior=AcquireNoise(ctx,shapeValues[4],shapeValues[5],size);
        }
        if(!ReferenceEquals(leasedEdge,edge) || !ReferenceEquals(leasedInterior,interior)) cacheValid=false;
        SetLease(ref leasedEdge,edge); SetLease(ref leasedInterior,interior);
        bakedEdge=edge?.texture; bakedInterior=interior?.texture;
        bool ready=bakedEdge!=null && bakedInterior!=null;
        if(ready!=optimizationsWereBaked) cacheValid=false;
        optimizationsWereBaked=ready;
        if(ready)
        {
            p.SetFloat("_StormBakedNoise",1); p.SetTexture("_StormEdgeNoiseTex",bakedEdge); p.SetTexture("_StormInteriorNoiseTex",bakedInterior);
        }
        if(supported && skipEmptySpace && optimizationCompute.HasKernel("BuildOccupancy") && optimizationCompute.HasKernel("DilateOccupancy") &&
            a.width==b.width && a.height==b.height && a.depth==b.depth)
        {
            int size=Mathf.Clamp(occupancyGridSize,8,64);
            float warp=Mathf.Max(shapeValues[1].x>0 ? shapeValues[2].x : 0,shapeValues[4].x>0 ? shapeValues[5].x : 0);
            occ=AcquireOccupancy(ctx,a,b,size,Mathf.CeilToInt(warp*size));
            if(occ!=null)
            { p.SetTexture("_StormOccupancy",occ.texture); p.SetFloat("_StormSkipEmpty",1); p.SetFloat("_StormOccupancySize",size); }
        }
        SetLease(ref leasedOccupancy,occ); occupancy=occ?.texture;
    }

    void PrepareOptimizations(CustomPassContext ctx, Texture3D a, Texture3D b)
    {
        PruneSharedCaches();
        // In-place mutation uses the private path so a stale shared map is never trusted.
        if(shareOptimizationCaches && !forceOccupancyUpdate)
        { PrepareSharedOptimizations(ctx,a,b); return; }
        if(forceOccupancyUpdate && SharedOccupancy.Count>0) ResetSharedCaches();
        LeaveSharedCaches();
        var p = PassProperties;
        p.SetFloat("_StormBakedNoise",0);
        p.SetFloat("_StormSkipEmpty",0);
        p.SetTexture("_StormEdgeNoiseTex",a); p.SetTexture("_StormInteriorNoiseTex",a);
        p.SetTexture("_StormOccupancy",a);
        if (previousOptimizationCompute != optimizationCompute)
        { bakedValid = occupancyValid = false; previousOptimizationCompute = optimizationCompute; }
        bool supported = optimizationCompute != null && SystemInfo.supportsComputeShaders &&
            SystemInfo.supports3DTextures && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RFloat);
        bool ready = false;
        if (supported && useBakedNoise && optimizationCompute.HasKernel("BakeNoise"))
        {
            int size = Mathf.Clamp(noiseTextureSize,32,128);
            Vector4 ka = new Vector4(shapeValues[1].y,shapeValues[2].y,size,0);
            Vector4 kb = new Vector4(shapeValues[4].y,shapeValues[5].y,size,0);
            if (bakedEdge == null || bakedInterior == null || bakedEdge.width != size ||
                bakedInterior.width != size || !bakedEdge.IsCreated() || !bakedInterior.IsCreated()) bakedValid = false;
            ready = EnsureOptTexture(ref bakedEdge,size,true) && EnsureOptTexture(ref bakedInterior,size,true);
            if (ready)
            {
                ctx.cmd.BeginSample("Sandstorm Bake Noise");
                bool changed = !bakedValid || ka != bakedKeyA || kb != bakedKeyB;
                if (!bakedValid || ka != bakedKeyA) BakeOptNoise(ctx,bakedEdge,ka);
                if (!bakedValid || kb != bakedKeyB) BakeOptNoise(ctx,bakedInterior,kb);
                ctx.cmd.EndSample("Sandstorm Bake Noise");
                if (changed) cacheValid = false;
                bakedKeyA=ka; bakedKeyB=kb; bakedValid=true;
                p.SetFloat("_StormBakedNoise",1);
                p.SetTexture("_StormEdgeNoiseTex",bakedEdge); p.SetTexture("_StormInteriorNoiseTex",bakedInterior);
            }
        }
        else { ReleaseOptTexture(ref bakedEdge); ReleaseOptTexture(ref bakedInterior); bakedValid=false; }
        if (ready != optimizationsWereBaked) cacheValid=false;
        optimizationsWereBaked=ready;
        if (supported && skipEmptySpace && optimizationCompute.HasKernel("BuildOccupancy") &&
            optimizationCompute.HasKernel("DilateOccupancy") && a.width==b.width && a.height==b.height && a.depth==b.depth)
        {
            int size=Mathf.Clamp(occupancyGridSize,8,64);
            // Edge/interior masks sum to one, so the maximum warp bounds both layers.
            float warp=Mathf.Max(shapeValues[1].x>0 ? shapeValues[2].x : 0,
                                shapeValues[4].x>0 ? shapeValues[5].x : 0);
            int radius=Mathf.CeilToInt(warp*size);
            if (occupancy==null || occupancy.width!=size || !occupancy.IsCreated() ||
                occupancyRaw==null || !occupancyRaw.IsCreated()) occupancyValid=false;
            if (EnsureOptTexture(ref occupancyRaw,size,false) && EnsureOptTexture(ref occupancy,size,false))
            {
                if (!occupancyValid || occupancyA!=a || occupancyB!=b || occupancyRadius!=radius || forceOccupancyUpdate)
                {
                    ctx.cmd.BeginSample("Sandstorm Build Occupancy");
                    int k=optimizationCompute.FindKernel("BuildOccupancy");
                    ctx.cmd.SetComputeIntParam(optimizationCompute,"_OptSize",size);
                    ctx.cmd.SetComputeVectorParam(optimizationCompute,"_SourceSize",new Vector4(a.width,a.height,a.depth,0));
                    ctx.cmd.SetComputeTextureParam(optimizationCompute,k,"_DensityTex",a);
                    ctx.cmd.SetComputeTextureParam(optimizationCompute,k,"_DensityTexB",b);
                    ctx.cmd.SetComputeTextureParam(optimizationCompute,k,"_OptOutput",occupancyRaw);
                    ctx.cmd.DispatchCompute(optimizationCompute,k,(size+3)/4,(size+3)/4,(size+3)/4);
                    k=optimizationCompute.FindKernel("DilateOccupancy");
                    ctx.cmd.SetComputeIntParam(optimizationCompute,"_DilateRadius",radius);
                    ctx.cmd.SetComputeTextureParam(optimizationCompute,k,"_OccInput",occupancyRaw);
                    ctx.cmd.SetComputeTextureParam(optimizationCompute,k,"_OptOutput",occupancy);
                    ctx.cmd.DispatchCompute(optimizationCompute,k,(size+3)/4,(size+3)/4,(size+3)/4);
                    ctx.cmd.EndSample("Sandstorm Build Occupancy");
                    occupancyValid=true; occupancyA=a; occupancyB=b; occupancyRadius=radius;
                }
                p.SetTexture("_StormOccupancy",occupancy); p.SetFloat("_StormSkipEmpty",1);
                p.SetFloat("_StormOccupancySize",size);
            }
        }
        else { ReleaseOptTexture(ref occupancy); ReleaseOptTexture(ref occupancyRaw); occupancyValid=false; }
    }

    public enum NoiseType { Value, Fractal, Ridged, Cellular }
    [Serializable]
    public class NoiseLayer
    {
        public bool enabled = false;
        public NoiseType type = NoiseType.Fractal;
        [Tooltip("Noise cells across a texture axis. Higher means finer detail.")]
        [Range(0.1f, 64)] public float frequency = 8;
        [Range(0, 1)] public float strength = 0.25f;
        [Tooltip("Coordinate displacement as fraction of the texture size. Changes the silhouette.")]
        [Range(0, 0.1f)] public float warp = 0.01f;
        [Range(1, 4)] public int octaves = 2;
        public int seed = 1;
        public Vector3 offset = Vector3.zero;
        public bool animateNoise = false;
        [Tooltip("Noise-space units per second; each axis may be negative.")]
        public Vector3 velocity = new Vector3(0.08f, 0.02f, 0);
    }
    [Header("Density Contrast")]
    [Tooltip("1 preserves density. Higher separates weak and dense parts around Contrast Pivot.")]
    [Range(0.25f, 4)] public float densityContrast = 1;
    [Min(0.0001f)] public float contrastPivot = 0.5f;
    [Tooltip("Edge noise affects low density up to this value BEFORE threshold/multiplier/contrast. Tune to the actual density range.")]
    [Min(0.0001f)] public float edgeDensityBand = 0.3f;
    [Header("Noise Layers")]
    public NoiseLayer edgeNoise = new NoiseLayer();
    public NoiseLayer interiorNoise = new NoiseLayer { frequency = 4, strength = 0.2f, warp = 0.005f, seed = 31 };

    static readonly string[] ShapeNames = { "_StormContrast", "_EdgeShape", "_EdgeDetail", "_EdgeOffset", "_InteriorShape", "_InteriorDetail", "_InteriorOffset" };
    readonly Vector4[] shapeValues = new Vector4[7];
    readonly Vector4[] previousShapeValues = new Vector4[7];
    double edgeNoiseTime, interiorNoiseTime, lastNoiseTime;
    bool noiseClockValid, noiseClockPlayMode;
    bool edgeWasMoving, interiorWasMoving;

    bool AnyNoiseMoving()
    {
        return (edgeNoise != null && edgeNoise.enabled && edgeNoise.animateNoise) ||
            (interiorNoise != null && interiorNoise.enabled && interiorNoise.animateNoise);
    }

    void UpdateShapeParameters()
    {
        bool isPlay = Application.isPlaying;
        bool runClock = isPlay;
        double now = Time.timeAsDouble;
#if UNITY_EDITOR
        if (!isPlay)
        {
            runClock = playInEditor && editorTimeReady;
            now = editorTimeReady ? editorSampleTime : UnityEditor.EditorApplication.timeSinceStartup;
        }
#endif
        bool edgeMoving = edgeNoise != null && edgeNoise.enabled && edgeNoise.animateNoise;
        bool interiorMoving = interiorNoise != null && interiorNoise.enabled && interiorNoise.animateNoise;
        if (runClock && noiseClockValid && noiseClockPlayMode == isPlay)
        {
            double delta = Math.Max(0, now - lastNoiseTime);
            if (edgeMoving && edgeWasMoving) edgeNoiseTime += delta;
            if (interiorMoving && interiorWasMoving) interiorNoiseTime += delta;
        }
        lastNoiseTime = now;
        noiseClockValid = runClock;
        noiseClockPlayMode = isPlay;
        edgeWasMoving = edgeMoving;
        interiorWasMoving = interiorMoving;
        shapeValues[0] = new Vector4(Mathf.Clamp(densityContrast, 0.25f, 4), Mathf.Max(0.0001f, contrastPivot), Mathf.Max(0.0001f, edgeDensityBand), 0);
        PackNoise(edgeNoise, edgeNoiseTime, 1);
        PackNoise(interiorNoise, interiorNoiseTime, 4);
    }

    void PackNoise(NoiseLayer layer, double seconds, int index)
    {
        if (layer == null || !layer.enabled)
        {
            shapeValues[index] = shapeValues[index + 1] = shapeValues[index + 2] = Vector4.zero;
            return;
        }
        shapeValues[index] = new Vector4(1, (int)layer.type, Mathf.Max(0.1f, layer.frequency), Mathf.Clamp01(layer.strength));
        shapeValues[index + 1] = new Vector4(Mathf.Clamp(layer.warp, 0, 0.1f), Mathf.Clamp(layer.octaves, 1, 4), layer.seed % 10000, 0);
        Vector3 offset = layer.offset + layer.velocity * (float)seconds;
        shapeValues[index + 2] = new Vector4(offset.x, offset.y, offset.z, 0);
    }

    bool ShapeChanged()
    {
        for (int i = 0; i < shapeValues.Length; i++)
            if (!shapeValues[i].Equals(previousShapeValues[i])) return true;
        return false;
    }

    public enum ResolutionMode { Full, Half }
    [Header("Render Resolution")]
    public ResolutionMode renderResolution = ResolutionMode.Half;
    [Tooltip("Relative scene-depth tolerance. 0.02 means approximately 2 percent.")]
    [Range(0.001f, 0.1f)] public float depthTolerance = 0.02f;
    [Tooltip("Recompute full-resolution rays where low-resolution depth samples do not match. Costs extra GPU time at silhouettes.")]
    public bool refineDepthEdges = true;
    public enum LightGridSize { Grid32 = 32, Grid64 = 64, Grid128 = 128, Grid256 = 256, Grid512 = 512 }
    [Header("Cached Sun Lighting")]
    [Tooltip("Off: original per-ray shadows. On: compute a reusable 3D sun transmission grid.")]
    public bool useLightCache = false;
    public ComputeShader lightCacheCompute;
    public LightGridSize lightGridSize = LightGridSize.Grid64;
    [Tooltip("Normally only changed inputs rebuild the grid. Enable for textures modified in place or when debugging compute-shader edits.")]
    public bool forceLightCacheUpdate = false;
    RenderTexture lightGrid;
    ComputeShader previousCompute;
    Texture3D previousA, previousB;
    Vector4 previousAxes, previousDensity;
    Vector3 previousLightLocal;
    float previousBlend;
    int previousSteps;
    bool cacheValid;
    bool cacheWarningShown;

    void ReleaseLightGrid()
    {
        if (lightGrid != null)
        {
            lightGrid.Release();
            CoreUtils.Destroy(lightGrid);
            lightGrid = null;
        }
        cacheValid = false;
    }

    bool CacheUnavailable(string message)
    {
        if (!cacheWarningShown)
        {
            Debug.LogWarning("Sandstorm light cache: " + message + " Using original lighting.");
            cacheWarningShown = true;
        }
        return false;
    }

    bool PrepareLightGrid(CustomPassContext ctx, Texture3D a, Texture3D b, float blend, Vector3 sunDirection)
    {
        if (!useLightCache)
        {
            ReleaseLightGrid();
            cacheWarningShown = false;
            return false;
        }
        if (lightCacheCompute == null) return CacheUnavailable("Assign SandstormLightCache.compute.");
        if (!SystemInfo.supportsComputeShaders || !SystemInfo.supports3DTextures ||
            !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat) ||
            !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.RFloat))
            return CacheUnavailable("Compute shaders or writable RFloat textures are unsupported.");
        if (!lightCacheCompute.HasKernel("BuildLightCache"))
            return CacheUnavailable("BuildLightCache kernel is missing or failed to compile.");
        int size = Mathf.Clamp((int)lightGridSize, 32, 512);
        if (size > SystemInfo.maxTexture3DSize)
            return CacheUnavailable("Requested light grid exceeds this GPU's 3D texture size limit.");
        if (lightGrid == null || lightGrid.width != size || !lightGrid.IsCreated())
        {
            ReleaseLightGrid();
            lightGrid = new RenderTexture(size, size, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "Sandstorm Sun Transmission",
                dimension = TextureDimension.Tex3D,
                volumeDepth = size,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            if (!lightGrid.Create())
            {
                ReleaseLightGrid();
                return CacheUnavailable("Could not allocate the 3D light grid.");
            }
        }
        Vector4 axes = new Vector4(swapYZ ? 1 : 0, flipX ? 1 : 0, flipY ? 1 : 0, flipZ ? 1 : 0);
        Vector4 density = new Vector4(densityMultiplier, densityThreshold, extinction, edgeFade);
        // Direction is not normalized after transformation: integration remains in metres.
        Vector3 lightLocal = bounds.worldToLocalMatrix.MultiplyVector(sunDirection);
        int steps = Mathf.Clamp(lightSteps, 1, 32);
        bool dirty = forceLightCacheUpdate || !cacheValid || previousCompute != lightCacheCompute ||
            previousA != a || previousB != b || !previousBlend.Equals(blend) ||
            !previousAxes.Equals(axes) || !previousDensity.Equals(density) ||
            !previousLightLocal.Equals(lightLocal) || previousSteps != steps || ShapeChanged();
        if (dirty)
        {
            int kernel = lightCacheCompute.FindKernel("BuildLightCache");
            ctx.cmd.BeginSample("Sandstorm Build Light Cache");
            ctx.cmd.SetComputeFloatParam(lightCacheCompute,"_StormBakedNoise",optimizationsWereBaked ? 1f : 0f);
            ctx.cmd.SetComputeTextureParam(lightCacheCompute,kernel,"_StormEdgeNoiseTex",optimizationsWereBaked ? (Texture)bakedEdge : a);
            ctx.cmd.SetComputeTextureParam(lightCacheCompute,kernel,"_StormInteriorNoiseTex",optimizationsWereBaked ? (Texture)bakedInterior : a);
            ctx.cmd.SetComputeTextureParam(lightCacheCompute, kernel, "_DensityTex", a);
            ctx.cmd.SetComputeTextureParam(lightCacheCompute, kernel, "_DensityTexB", b);
            ctx.cmd.SetComputeTextureParam(lightCacheCompute, kernel, "_LightCacheOutput", lightGrid);
            ctx.cmd.SetComputeVectorParam(lightCacheCompute, "_AxisOptions", axes);
            ctx.cmd.SetComputeVectorParam(lightCacheCompute, "_DensityParams", density);
            ctx.cmd.SetComputeVectorParam(lightCacheCompute, "_LightDirectionLocal", new Vector4(lightLocal.x, lightLocal.y, lightLocal.z, 0));
            ctx.cmd.SetComputeFloatParam(lightCacheCompute, "_FrameBlend", blend);
            ctx.cmd.SetComputeIntParam(lightCacheCompute, "_GridSize", size);
            ctx.cmd.SetComputeIntParam(lightCacheCompute, "_LightSteps", steps);
            for (int i = 0; i < ShapeNames.Length; i++)
                ctx.cmd.SetComputeVectorParam(lightCacheCompute, ShapeNames[i], shapeValues[i]);
            int groups = (size + 3) / 4;
            // Graphics queue dispatch: Unity orders the UAV write before the following draw.
            ctx.cmd.DispatchCompute(lightCacheCompute, kernel, groups, groups, groups);
            ctx.cmd.EndSample("Sandstorm Build Light Cache");
            previousCompute = lightCacheCompute;
            previousA = a; previousB = b; previousBlend = blend;
            previousAxes = axes; previousDensity = density;
            previousLightLocal = lightLocal; previousSteps = steps;
            Array.Copy(shapeValues, previousShapeValues, shapeValues.Length);
            cacheValid = true;
        }
        cacheWarningShown = false;
        return true;
    }

    static readonly int LowResId = Shader.PropertyToID("_SandstormLowResColor");
    Material material;
    double playbackFrame;
    double lastTime;
    bool clockValid;
    bool clockWasPlayMode;
#if UNITY_EDITOR
    bool editorHooked;
    double lastPreviewRepaint;
    double editorSampleTime;
    bool editorTimeReady;

    void HookEditorPreview()
    {
        if (editorHooked) return;
        UnityEditor.EditorApplication.update += TickEditorPreview;
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += UnhookEditorPreview;
        editorHooked = true;
    }

    void UnhookEditorPreview()
    {
        UnityEditor.EditorApplication.update -= TickEditorPreview;
        UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= UnhookEditorPreview;
        editorHooked = false;
        editorTimeReady = false;
        clockValid = false;
        noiseClockValid = false;
    }

    void TickEditorPreview()
    {
        if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode ||
            UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating)
        {
            editorTimeReady = false;
            return;
        }
        bool sequenceMoving = animate && densityFrames != null && densityFrames.Length > 1;
        if (!enabled || !playInEditor || (!sequenceMoving && !AnyNoiseMoving()) || bounds == null ||
            !bounds.gameObject.activeInHierarchy)
        {
            editorTimeReady = false;
            clockValid = false;
            noiseClockValid = false;
            return;
        }
        double now = UnityEditor.EditorApplication.timeSinceStartup;
        if (!editorTimeReady)
        {
            clockValid = false;
            lastPreviewRepaint = now - 1;
        }
        if (now - lastPreviewRepaint < 1.0 / Mathf.Clamp(editorPreviewFPS, 1, 60)) return;
        lastPreviewRepaint = now;
        // All cameras use one timestamp per preview tick: no extra cache rebuilds per camera.
        editorSampleTime = now;
        editorTimeReady = true;
        UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        UnityEditor.SceneView.RepaintAll();
    }
#endif

    public void RestartAnimation()
    {
        playbackFrame = 0;
        clockValid = false;
    }

    void GetAnimationFrames(out Texture3D a, out Texture3D b, out float blend)
    {
        a = b = densityTexture;
        blend = 0;
        int count = densityFrames == null ? 0 : densityFrames.Length;
        if (count == 0) { clockValid = false; return; }
        bool inPlayMode = Application.isPlaying;
        double now = Time.timeAsDouble;
        bool playing = inPlayMode && animate;
#if UNITY_EDITOR
        if (!inPlayMode)
        {
            now = editorTimeReady ? editorSampleTime : UnityEditor.EditorApplication.timeSinceStartup;
            playing = animate && playInEditor && editorTimeReady;
        }
#endif
        if (clockWasPlayMode != inPlayMode)
        {
            clockValid = false;
            playbackFrame = Mathf.Clamp01(previewPosition) * (count - 1);
            clockWasPlayMode = inPlayMode;
        }
        if (playing)
        {
            if (clockValid)
                playbackFrame += Math.Max(0, now - lastTime) * Math.Max(0.01f, framesPerSecond) * Math.Max(0, playbackSpeed);
            lastTime = now;
            clockValid = true;
            if (loop) playbackFrame %= count;
            else playbackFrame = Math.Min(playbackFrame, count - 1);
        }
        else
        {
            clockValid = false;
            playbackFrame = Mathf.Clamp01(previewPosition) * (count - 1);
        }
        double position = loop && playing ? playbackFrame % count : Math.Min(playbackFrame, count - 1);
        int first = Math.Max(0, Math.Min((int)Math.Floor(position), count - 1));
        int second = first + 1 < count ? first + 1 : (loop && playing ? 0 : first);
        a = densityFrames[first];
        b = densityFrames[second];
        // Incomplete arrays never bind null textures. Fix empty slots for proper timing.
        if (a == null) a = b != null ? b : densityTexture;
        if (b == null) b = a;
        float strength = Mathf.Clamp01(interpolationStrength);
        float fraction = (float)(position - first);
        // Complete each blend before advancing the frame index, including the loop seam.
        // Multiplying fraction by strength would leave a discontinuity at every boundary.
        blend = interpolateFrames && a != b && strength > 0f
            ? Mathf.Clamp01((fraction - (1f - strength)) / strength)
            : 0f;
    }

    int GetLODViewSteps(Camera camera)
    {
        int maximum = Mathf.Clamp(viewSteps,16,256);
        if (!screenSizeLOD || camera == null || bounds == null) return maximum;
        // Preserve full quality for stereo and near-plane intersections.
        if (camera.stereoEnabled) return maximum;
        Vector3 localCamera = bounds.InverseTransformPoint(camera.transform.position);
        if (Mathf.Abs(localCamera.x)<=0.5f && Mathf.Abs(localCamera.y)<=0.5f && Mathf.Abs(localCamera.z)<=0.5f)
            return maximum;
        float minX=float.PositiveInfinity, minY=float.PositiveInfinity;
        float maxX=float.NegativeInfinity, maxY=float.NegativeInfinity;
        for(int z=0;z<2;z++)
        for(int y=0;y<2;y++)
        for(int x=0;x<2;x++)
        {
            Vector3 world=bounds.TransformPoint(new Vector3(x-0.5f,y-0.5f,z-0.5f));
            Vector3 viewport=camera.WorldToViewportPoint(world);
            if (viewport.z<=camera.nearClipPlane || float.IsNaN(viewport.x) || float.IsNaN(viewport.y) ||
                float.IsInfinity(viewport.x) || float.IsInfinity(viewport.y)) return maximum;
            minX=Mathf.Min(minX,viewport.x); maxX=Mathf.Max(maxX,viewport.x);
            minY=Mathf.Min(minY,viewport.y); maxY=Mathf.Max(maxY,viewport.y);
        }
        // Use the uncut projected box, so moving it partly offscreen does not lower quality.
        float size=Mathf.Clamp01(Mathf.Max(maxX-minX,maxY-minY));
        float small=Mathf.Clamp(lodSmallScreenSize,0f,0.99f);
        float full=Mathf.Clamp(lodFullScreenSize,small+0.001f,1f);
        float t=Mathf.Clamp01((size-small)/(full-small));
        t=t*t*(3f-2f*t);
        int minimum=Mathf.Clamp(lodMinViewSteps,16,maximum);
        return Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(minimum,maximum,t)),minimum,maximum);
    }

    protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
    {
#if UNITY_EDITOR
        HookEditorPreview();
#endif
    }

    [Header("Visibility Optimization")]
    public bool cullOutsideCamera = true;
    public bool limitScreenArea = true;
    [Tooltip("Extra full-resolution pixels around projected bounds; protects filtering at the border.")]
    [Range(2,32)] public int screenPaddingPixels = 4;
    readonly Plane[] visibilityPlanes = new Plane[6];

    bool GetVisibleScreenRect(Camera camera, int width, int height, out Rect rect)
    {
        rect=new Rect(0,0,width,height);
        if(camera.stereoEnabled || (!cullOutsideCamera && !limitScreenArea)) return true;
        Matrix4x4 matrix=bounds.localToWorldMatrix;
        Vector3 center=matrix.MultiplyPoint3x4(Vector3.zero);
        Vector3 x=matrix.MultiplyVector(Vector3.right)*0.5f;
        Vector3 y=matrix.MultiplyVector(Vector3.up)*0.5f;
        Vector3 z=matrix.MultiplyVector(Vector3.forward)*0.5f;
        Vector3 extent=new Vector3(Mathf.Abs(x.x)+Mathf.Abs(y.x)+Mathf.Abs(z.x),
            Mathf.Abs(x.y)+Mathf.Abs(y.y)+Mathf.Abs(z.y),Mathf.Abs(x.z)+Mathf.Abs(y.z)+Mathf.Abs(z.z));
        // A conservative world AABB includes rotation, nonuniform scale and parent shear.
        Bounds worldBox=new Bounds(center,2*extent);
        worldBox.Expand(Mathf.Max(0.01f,extent.magnitude*0.001f));
        if(cullOutsideCamera)
        {
            GeometryUtility.CalculateFrustumPlanes(camera,visibilityPlanes);
            if(!GeometryUtility.TestPlanesAABB(visibilityPlanes,worldBox)) return false;
        }
        if(!limitScreenArea) return true;
        float minX=float.PositiveInfinity,minY=float.PositiveInfinity,maxX=float.NegativeInfinity,maxY=float.NegativeInfinity;
        for(int iz=0;iz<2;iz++) for(int iy=0;iy<2;iy++) for(int ix=0;ix<2;ix++)
        {
            Vector3 v=camera.WorldToViewportPoint(matrix.MultiplyPoint3x4(new Vector3(ix-.5f,iy-.5f,iz-.5f)));
            // Corner projection is unsafe when the box crosses the near plane.
            if(v.z<=camera.nearClipPlane || float.IsNaN(v.x) || float.IsNaN(v.y) ||
                float.IsInfinity(v.x) || float.IsInfinity(v.y)) return true;
            minX=Mathf.Min(minX,v.x);maxX=Mathf.Max(maxX,v.x);
            minY=Mathf.Min(minY,v.y);maxY=Mathf.Max(maxY,v.y);
        }
        int padding=Mathf.Clamp(screenPaddingPixels,2,32);
        float left=Mathf.Clamp(Mathf.Floor(minX*width)-padding,0,width);
        float right=Mathf.Clamp(Mathf.Ceil(maxX*width)+padding,0,width);
        float bottom=Mathf.Clamp(Mathf.Floor(minY*height)-padding,0,height);
        float top=Mathf.Clamp(Mathf.Ceil(maxY*height)+padding,0,height);
        if(right<=left || top<=bottom)
        {
            if(cullOutsideCamera) return false;
            return true;
        }
        // Include both vertical conventions: camera RT and temporary RT may differ.
        // This deliberately sacrifices some scissor tightness instead of clipping on a flipped target.
        float safeBottom=Mathf.Min(bottom,height-top),safeTop=Mathf.Max(top,height-bottom);
        rect=Rect.MinMaxRect(left,safeBottom,right,safeTop);
        return true;
    }
    static Rect LowResolutionRect(Rect full,int width,int height,int lowWidth,int lowHeight)
    {
        // Additional low-res halo supplies all four taps used by the upsampler.
        float x0=Mathf.Max(0,Mathf.Floor(full.xMin*lowWidth/width)-2);
        float y0=Mathf.Max(0,Mathf.Floor(full.yMin*lowHeight/height)-2);
        float x1=Mathf.Min(lowWidth,Mathf.Ceil(full.xMax*lowWidth/width)+2);
        float y1=Mathf.Min(lowHeight,Mathf.Ceil(full.yMax*lowHeight/height)+2);
        return Rect.MinMaxRect(x0,y0,x1,y1);
    }

    public void GetCheapShadowFrames(out Texture3D a, out Texture3D b, out float blend)
    {
        GetAnimationFrames(out a,out b,out blend);
    }
    bool preparingGroup, groupParametersReady;
    public bool BindGroupParameters(CustomPassContext ctx, MaterialPropertyBlock target, int slot)
    {
        groupParametersReady=false; preparingGroup=true;
        try { Execute(ctx); } finally { preparingGroup=false; }
        if(!groupParametersReady) return false;
        var source=PassProperties;
        string prefix="_V"+slot;
        foreach(string name in new[]{"_DensityTex","_DensityTexB","_StormEdgeNoiseTex","_StormInteriorNoiseTex","_StormOccupancy","_SunLightCache"})
            target.SetTexture(prefix+name,source.GetTexture(name));
        foreach(string name in new[]{"_FrameBlend","_StormBakedNoise","_StormSkipEmpty","_StormOccupancySize","_UseLightCache","_Anisotropy"})
            target.SetFloat(prefix+name,source.GetFloat(name));
        foreach(string name in new[]{"_AxisOptions","_DensityParams","_MarchParams","_AdaptiveMarch","_SunDirection","_StormContrast","_EdgeShape","_EdgeDetail","_EdgeOffset","_InteriorShape","_InteriorDetail","_InteriorOffset"})
            target.SetVector(prefix+name,source.GetVector(name));
        foreach(string name in new[]{"_SunRadiance","_DustColor","_AmbientRadiance"})
            target.SetColor(prefix+name,source.GetColor(name));
        target.SetMatrix(prefix+"_VolumeWorldToLocal",source.GetMatrix("_VolumeWorldToLocal"));
        return true;
    }
    public void ReleaseGroupResources() { Cleanup(); }

    [NonSerialized] MaterialPropertyBlock passProperties;
    MaterialPropertyBlock PassProperties
    {
        get
        {
            if(passProperties==null) passProperties=new MaterialPropertyBlock();
            return passProperties;
        }
    }

    protected override void Execute(CustomPassContext ctx)
    {
        if(!preparingGroup && !ctx.hdCamera.camera.orthographic && !ctx.hdCamera.camera.stereoEnabled &&
            SandstormGroupPass.FindOwner(this)!=null) return;
#if UNITY_EDITOR
        HookEditorPreview();
#endif
        if (bounds == null || raymarchShader == null)
            return;
        // Initial version deliberately supports perspective cameras only.
        if (ctx.hdCamera.camera.orthographic) return;
        Vector3 scale = bounds.lossyScale;
        if (Mathf.Abs(scale.x * scale.y * scale.z) < 0.000001f) return;
        if (material == null || material.shader != raymarchShader)
        {
            CoreUtils.Destroy(material);
            material = CoreUtils.CreateEngineMaterial(raymarchShader);
        }
        GetAnimationFrames(out Texture3D frameA, out Texture3D frameB, out float frameBlend);
        if (frameA == null || frameB == null) return;
        UpdateShapeParameters();
        Rect screenRect=new Rect(0,0,ctx.hdCamera.actualWidth,ctx.hdCamera.actualHeight);
        if(!preparingGroup && !GetVisibleScreenRect(ctx.hdCamera.camera,
            Mathf.Max(1,ctx.hdCamera.actualWidth),Mathf.Max(1,ctx.hdCamera.actualHeight),out screenRect)) return;
        // Animation clocks advanced above; GPU preparation starts only for visible clouds.
        var p = PassProperties;
        for (int i = 0; i < ShapeNames.Length; i++) p.SetVector(ShapeNames[i], shapeValues[i]);
        p.SetTexture("_DensityTex", frameA);
        p.SetTexture("_DensityTexB", frameB);
        p.SetFloat("_FrameBlend", frameBlend);
        p.SetMatrix("_VolumeWorldToLocal", bounds.worldToLocalMatrix);
        p.SetVector("_AxisOptions", new Vector4(swapYZ ? 1 : 0, flipX ? 1 : 0, flipY ? 1 : 0, flipZ ? 1 : 0));
        p.SetVector("_DensityParams", new Vector4(densityMultiplier, densityThreshold, extinction, edgeFade));
        p.SetVector("_MarchParams", new Vector4(GetLODViewSteps(ctx.hdCamera.camera), Mathf.Clamp(lightSteps, 1, 32), jitter, earlyExit));
        Vector3 direction = sun != null ? -sun.transform.forward : new Vector3(-0.6f, 0.7f, -0.3f).normalized;
        PrepareOptimizations(ctx, frameA, frameB);
        bool cachedLighting = PrepareLightGrid(ctx, frameA, frameB, frameBlend, direction);
        p.SetFloat("_UseLightCache", cachedLighting ? 1 : 0);
        if (cachedLighting) p.SetTexture("_SunLightCache", lightGrid);
        else p.SetTexture("_SunLightCache", frameA); // Valid 3D fallback binding; branch is disabled.
        Color lightColor = sun != null ? sun.color.linear : Color.white;
        p.SetVector("_SunDirection", new Vector4(direction.x, direction.y, direction.z, 0));
        p.SetColor("_SunRadiance", lightColor * sunStrength);
        p.SetColor("_DustColor", dustColor.linear);
        p.SetColor("_AmbientRadiance", ambientColor.linear * ambientStrength);
        p.SetFloat("_Anisotropy", anisotropy);
        float adaptiveStart = Mathf.Clamp(opacityStart, 0f, 0.99f);
        float adaptiveEnd = Mathf.Clamp(opacityEnd, adaptiveStart + 0.001f, 1f);
        p.SetVector("_AdaptiveMarch", new Vector4(adaptiveSteps ? 1f : 0f,
            Mathf.Clamp(maxStepMultiplier, 1f, 4f), adaptiveStart, adaptiveEnd));
        if(preparingGroup) { groupParametersReady=true; return; }
        int width = Mathf.Max(1, ctx.hdCamera.actualWidth);
        int height = Mathf.Max(1, ctx.hdCamera.actualHeight);
        p.SetVector("_StormFullSize", new Vector4(width, height, 1f / width, 1f / height));
        // The temporary buffer is a 2D desktop target. Stereo uses the original path.
        bool half = renderResolution == ResolutionMode.Half && !ctx.hdCamera.camera.stereoEnabled;
        p.SetFloat("_StormHalfPass", half ? 1 : 0);
        if (!half)
        {
            // Never inherit a viewport or render target from another Custom Pass.
            ctx.cmd.DisableScissorRect();
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer, ClearFlag.None);
            ctx.cmd.SetViewport(new Rect(0, 0, width, height));
            ctx.cmd.BeginSample("Sandstorm Full Resolution");
            if(limitScreenArea && !ctx.hdCamera.camera.stereoEnabled) ctx.cmd.EnableScissorRect(screenRect);
            CoreUtils.DrawFullScreen(ctx.cmd, material, p, shaderPassId: 0);
            if(limitScreenArea && !ctx.hdCamera.camera.stereoEnabled) ctx.cmd.DisableScissorRect();
            ctx.cmd.EndSample("Sandstorm Full Resolution");
            return;
        }
        int lowWidth = Mathf.Max(1, (width + 1) / 2);
        int lowHeight = Mathf.Max(1, (height + 1) / 2);
        p.SetVector("_StormLowSize", new Vector4(lowWidth, lowHeight, 1f / lowWidth, 1f / lowHeight));
        p.SetFloat("_StormDepthTolerance", Mathf.Max(0.001f, depthTolerance));
        p.SetFloat("_StormRefineEdges", refineDepthEdges ? 1 : 0);
        ctx.cmd.GetTemporaryRT(LowResId, lowWidth, lowHeight, 0, FilterMode.Point,
            RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        ctx.cmd.BeginSample("Sandstorm Half Resolution");
        ctx.cmd.DisableScissorRect();
        ctx.cmd.SetRenderTarget(new RenderTargetIdentifier(LowResId));
        ctx.cmd.SetViewport(new Rect(0, 0, lowWidth, lowHeight));
        ctx.cmd.ClearRenderTarget(false, true, Color.clear);
        if(limitScreenArea) ctx.cmd.EnableScissorRect(LowResolutionRect(screenRect,width,height,lowWidth,lowHeight));
        CoreUtils.DrawFullScreen(ctx.cmd, material, p, shaderPassId: 0);
        if(limitScreenArea) ctx.cmd.DisableScissorRect();
        ctx.cmd.EndSample("Sandstorm Half Resolution");
        // Restore the camera RTHandle and its viewport before compositing.
        ctx.cmd.DisableScissorRect();
        CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer, ClearFlag.None);
        ctx.cmd.SetViewport(new Rect(0, 0, width, height));
        ctx.cmd.SetGlobalTexture(LowResId, new RenderTargetIdentifier(LowResId));
        ctx.cmd.BeginSample("Sandstorm Depth Aware Upsample");
        if(limitScreenArea) ctx.cmd.EnableScissorRect(screenRect);
        CoreUtils.DrawFullScreen(ctx.cmd, material, p, shaderPassId: 1);
        if(limitScreenArea) ctx.cmd.DisableScissorRect();
        ctx.cmd.EndSample("Sandstorm Depth Aware Upsample");
        ctx.cmd.ReleaseTemporaryRT(LowResId);
    }

    protected override void Cleanup()
    {
#if UNITY_EDITOR
        UnhookEditorPreview();
#endif
        ReleaseLightGrid();
        LeaveSharedCaches();
        if (Application.isPlaying)
        {
            var noiseUnused = new List<(ComputeShader,int,int,int)>();
            foreach(var pair in SharedNoise) if(pair.Value.users==0) noiseUnused.Add(pair.Key);
            foreach(var key in noiseUnused) { DestroySharedEntry(SharedNoise[key]); SharedNoise.Remove(key); }
            var occUnused = new List<(ComputeShader,Texture3D,Texture3D,int,int)>();
            foreach(var pair in SharedOccupancy) if(pair.Value.users==0) occUnused.Add(pair.Key);
            foreach(var key in occUnused) { DestroySharedEntry(SharedOccupancy[key]); SharedOccupancy.Remove(key); }
        }
        ReleaseOptTexture(ref bakedEdge); ReleaseOptTexture(ref bakedInterior);
        ReleaseOptTexture(ref occupancy); ReleaseOptTexture(ref occupancyRaw);
        bakedValid=occupancyValid=false;
        CoreUtils.Destroy(material);
        material = null;
        clockValid = false;
    }
}
