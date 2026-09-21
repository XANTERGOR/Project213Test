using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTPaintRuntime
    {
        sealed class DeformationState
        {
            public Rect rect;
            public int size;
            public int rows;
            public LTPaintStamp stamp;
            public int signature;
            public float[] recovery;
            public Vector4[] controls;
            public float[] depths;
            public Texture depthMap;
            public Texture2D factors;
            public GpuMud gpu;
            public double lastRecovery;
            public Texture2D uploadTile;
            public float[] uploadValues;
            public bool partialUpload;
            public readonly HashSet<int> dirtyBlocks=new HashSet<int>();
            public readonly HashSet<int> activityBlocks=new HashSet<int>();
            public readonly List<float[]> factorMips=new List<float[]>();
            public float[] factorTargets;
            public readonly HashSet<int> factorTouched=new HashSet<int>();
            public readonly HashSet<int> factorLive=new HashSet<int>();
            public readonly HashSet<int> active=new HashSet<int>();
            public readonly HashSet<int> contacted=new HashSet<int>();
            public readonly List<int> remove=new List<int>();
            public bool dirty;
        }
        readonly LTPaintTerrain deformationTerrain=new LTPaintTerrain();
        readonly Dictionary<LTTerrainDeformer,Vector3> deformationPrevious=new Dictionary<LTTerrainDeformer,Vector3>();
        readonly HashSet<LTTerrainDeformer> deformationLive=new HashSet<LTTerrainDeformer>();
        double deformationTime=-1;
        bool deformationPlaying;
        bool deformationBindingsDirty;
        bool deformationFactorFading;
        bool gpuMode;
        double gpuEpoch;
        readonly List<Rect> predictedMudAreas=new List<Rect>();
        readonly Dictionary<LTPaintStamp,DeformationState> deformationRegions=new Dictionary<LTPaintStamp,DeformationState>();
        readonly Dictionary<Vector2Int,ChunkState> deformationChunks=new Dictionary<Vector2Int,ChunkState>();
        const int DepthBlock=32,ActivityBlock=8;
        static readonly Unity.Profiling.ProfilerMarker ContactMarker=new Unity.Profiling.ProfilerMarker("LT.Mud.Contact");
        static readonly Unity.Profiling.ProfilerMarker UploadMarker=new Unity.Profiling.ProfilerMarker("LT.Mud.UploadBlocks");
        static readonly Unity.Profiling.ProfilerMarker ActivityMarker=new Unity.Profiling.ProfilerMarker("LT.Mud.ActivityFactors");
        // Capture transforms once per emitter/tick, not once per depth texel.
        struct ContactShape
        {
            public Collider collider;
            public Bounds bounds;
            public int kind;
            public Vector3 center,x,y,z,half,a,b;
            public float radius;
            public static ContactShape Capture(LTTerrainDeformer press)
            {
                var collider=press.contactCollider;
                var s=new ContactShape{collider=collider,bounds=collider.bounds};
                if(collider is WheelCollider wheel)
                {
                    s.kind=-1;
                    if(!wheel.GetGroundHit(out var hit)||!hit.collider||
                        !hit.collider.transform.IsChildOf(press.world.transform))return s;
                    s.kind=1;s.y=hit.normal.normalized;s.z=Vector3.ProjectOnPlane(hit.forwardDir,s.y).normalized;
                    if(s.z.sqrMagnitude<.5f)s.z=Vector3.ProjectOnPlane(wheel.transform.forward,s.y).normalized;
                    if(s.z.sqrMagnitude<.5f){s.kind=-1;return s;}
                    s.x=Vector3.Cross(s.y,s.z).normalized;
                    s.half=new Vector3(Mathf.Max(.01f,press.wheelWidth)*.5f,.025f,
                        Mathf.Max(.01f,press.wheelContactLength)*.5f);
                    s.center=hit.point+s.y*s.half.y;
                    var ext=Abs(s.x)*s.half.x+Abs(s.y)*s.half.y+Abs(s.z)*s.half.z;
                    s.bounds=new Bounds(s.center,ext*2);
                    return s;
                }
                var m=collider.transform.localToWorldMatrix;
                var vx=m.MultiplyVector(Vector3.right);var vy=m.MultiplyVector(Vector3.up);var vz=m.MultiplyVector(Vector3.forward);
                var scale=new Vector3(vx.magnitude,vy.magnitude,vz.magnitude);
                if(Mathf.Min(scale.x,Mathf.Min(scale.y,scale.z))<1e-6f)return s;
                s.x=vx/scale.x;s.y=vy/scale.y;s.z=vz/scale.z;
                // Sheared hierarchies retain the native collider path.
                if(Mathf.Abs(Vector3.Dot(s.x,s.y))>1e-4f||Mathf.Abs(Vector3.Dot(s.x,s.z))>1e-4f||
                    Mathf.Abs(Vector3.Dot(s.y,s.z))>1e-4f)return s;
                if(collider is BoxCollider box)
                {
                    s.kind=1;s.center=m.MultiplyPoint3x4(box.center);
                    s.half=Vector3.Scale(box.size,scale)*.5f;
                }
                else if(collider is SphereCollider sphere)
                {
                    s.kind=2;s.center=m.MultiplyPoint3x4(sphere.center);
                    s.radius=sphere.radius*Mathf.Max(scale.x,Mathf.Max(scale.y,scale.z));
                }
                else if(collider is CapsuleCollider capsule)
                {
                    s.kind=3;s.center=m.MultiplyPoint3x4(capsule.center);
                    int axis=capsule.direction;
                    var direction=axis==0?s.x:axis==1?s.y:s.z;
                    float axial=scale[axis],radial=Mathf.Max(scale[(axis+1)%3],scale[(axis+2)%3]);
                    s.radius=capsule.radius*radial;
                    float length=Mathf.Max(0,capsule.height*axial*.5f-s.radius);
                    s.a=s.center-direction*length;s.b=s.center+direction*length;
                }
                return s;
            }
            static Vector3 Abs(Vector3 v)=>new Vector3(Mathf.Abs(v.x),Mathf.Abs(v.y),Mathf.Abs(v.z));
            public Vector3 Closest(Vector3 point)
            {
                if(kind==1)return LTDeformationMath.ClosestBox(point,center,x,y,z,half);
                if(kind==2)return LTDeformationMath.ClosestSphere(point,center,radius);
                if(kind==3)return LTDeformationMath.ClosestCapsule(point,a,b,radius);
                return collider.ClosestPoint(point);
            }
        }
        static void ReleaseDepth(DeformationState d)
        {
            if(d.gpu!=null){d.gpu.Dispose();d.gpu=null;d.depthMap=null;}
            DestroyOwned(d.depthMap);DestroyOwned(d.uploadTile);
            d.depthMap=null;d.uploadTile=null;d.uploadValues=null;d.depths=null;d.recovery=null;
            d.dirtyBlocks.Clear();d.activityBlocks.Clear();d.dirty=false;
        }
        static void MarkDepthDirty(DeformationState d,int p)
        {
            d.dirty=true;
            d.dirtyBlocks.Add(LTDeformationMath.BlockIndex(p%d.size,p/d.size,d.size,DepthBlock));
        }
        static void AllocateDepth(DeformationState d)
        {
            d.depths=new float[d.size*d.rows];
            if(d.stamp)d.recovery=new float[d.depths.Length];
            d.partialUpload=(SystemInfo.copyTextureSupport&UnityEngine.Rendering.CopyTextureSupport.Basic)!=0;
            d.depthMap=new Texture2D(d.size,d.rows,TextureFormat.RFloat,false,true)
                {name="Transient mud depth (metres)",hideFlags=HideFlags.HideAndDontSave,
                 wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
            // Initialize once. GPU-only destination must never Apply after copies.
            ((Texture2D)d.depthMap).SetPixelData(d.depths,0);((Texture2D)d.depthMap).Apply(false,d.partialUpload);
            if(d.partialUpload)
            {
                d.uploadValues=new float[DepthBlock*DepthBlock];
                d.uploadTile=new Texture2D(DepthBlock,DepthBlock,TextureFormat.RFloat,false,true)
                    {name="Mud depth upload tile",hideFlags=HideFlags.HideAndDontSave};
            }
        }
        static void UploadDepth(DeformationState d)
        {
            using var profiling=UploadMarker.Auto();
            if(!d.dirty)return;
            if(!d.partialUpload)
            {
                // Compatibility fallback for devices without region texture copies.
                ((Texture2D)d.depthMap).SetPixelData(d.depths,0);((Texture2D)d.depthMap).Apply(false,false);
            }
            else foreach(int block in d.dirtyBlocks)
            {
                var r=LTDeformationMath.BlockRect(block,d.size,d.rows,DepthBlock);
                for(int y=0;y<r.height;y++)
                    Array.Copy(d.depths,(r.y+y)*d.size+r.x,d.uploadValues,y*DepthBlock,r.width);
                d.uploadTile.SetPixelData(d.uploadValues,0);d.uploadTile.Apply(false,false);
                Graphics.CopyTexture(d.uploadTile,0,0,0,0,r.width,r.height,d.depthMap,0,0,r.x,r.y);
            }
            d.dirtyBlocks.Clear();d.dirty=false;
        }
        IEnumerable<DeformationState> SimulationStates(LTWorld world)
        {
            // A base layer has no authored area: retain the legacy chunk fallback.
            if(world.baseLayer&&world.baseLayer.deformation)
                foreach(var state in chunks.Values)
                    if(state.displacementActive&&state.deformation!=null)yield return state.deformation;
            foreach(var d in deformationRegions.Values)yield return d;
        }
        void ReleaseDeformationRegions()
        {
            foreach(var d in deformationRegions.Values)ReleaseDepth(d);
            deformationRegions.Clear();deformationChunks.Clear();
        }

        static void ReleaseDeformation(ChunkState state)
        {
            if(state.deformation==null)return;
            ReleaseDepth(state.deformation);DestroyOwned(state.deformation.factors);
            state.deformation=null;
        }
        public void ClearDeformation()
        {
            foreach(var state in chunks.Values)
            {
                var d=state.deformation;if(d==null)continue;
                ReleaseDepth(d);d.active.Clear();d.contacted.Clear();
                ResetFactorMap(d);
            }
            deformationPrevious.Clear();deformationTime=-1;
            foreach(var d in deformationRegions.Values)
            {
                ReleaseDepth(d);
                d.active.Clear();d.active.TrimExcess();d.contacted.Clear();d.contacted.TrimExcess();d.dirty=false;
            }
            foreach(var state in chunks.Values)
            {
                BindDeformation(state.material,state);
                if(state.flatMaterial)BindDeformation(state.flatMaterial,state);
                if(state.farMaterial)BindDeformation(state.farMaterial,state);
            }
        }
        static Vector4[] DeformationSettings(List<LTSurfaceLayer> layers)
        {
            var settings=new Vector4[8];
            for(int i=0;i<layers.Count;i++)if(layers[i].deformation)
                settings[i]=new Vector4(Mathf.Clamp(layers[i].deformationDepth,0,2),
                    Mathf.Max(0,layers[i].deformationRecovery),Mathf.Clamp(layers[i].deformationTessellation,1,63),0);
            return settings;
        }
        static void BeginDeformationControls(ChunkState state,Rect area,int cells,List<LTSurfaceLayer> layers)
        {
            // Authoring changes reset transient tracks, never resample them into a new region.
            ReleaseDeformation(state);
            if(!layers.Exists(l=>l.deformation&&l.deformationDepth>0))return;
            int size=Math.Min(cells,512)+1;
            state.deformation=new DeformationState{rect=area,size=size,rows=size,controls=new Vector4[size*size]};
        }
        static void FinishDeformationControls(ChunkState state)
        {
            var d=state.deformation;if(d==null)return;
            int cells=d.size-1;
            d.factors=new Texture2D(cells,cells,TextureFormat.RFloat,true,true)
                {name="Mud tessellation (conservative max, active tracks)",hideFlags=HideFlags.HideAndDontSave,
                 wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Point};
            d.factorTargets=new float[cells*cells];
            for(int width=cells;;width/=2)
            {
                d.factorMips.Add(new float[width*width]);
                if(width==1)break;
            }
            ResetFactorMap(d);
        }
        static void ResetFactorMap(DeformationState d)
        {
            if(!d.factors)return;
            d.factorTouched.Clear();d.factorLive.Clear();
            Array.Clear(d.factorTargets,0,d.factorTargets.Length);
            for(int mip=0;mip<d.factorMips.Count;mip++)
            {
                var values=d.factorMips[mip];
                for(int p=0;p<values.Length;p++)values[p]=1;
                d.factors.SetPixelData(values,mip);
            }
            d.factors.Apply(false,false);
        }
        static void UploadFactors(DeformationState d)
        {
            int width=d.size-1;
            d.factors.SetPixelData(d.factorMips[0],0);
            for(int mip=1;mip<d.factorMips.Count;mip++)
            {
                var previous=d.factorMips[mip-1];var values=d.factorMips[mip];int half=width/2;
                for(int y=0;y<half;y++)for(int x=0;x<half;x++)
                {
                    int p=y*2*width+x*2;
                    values[y*half+x]=Mathf.Max(Mathf.Max(previous[p],previous[p+1]),
                        Mathf.Max(previous[p+width],previous[p+width+1]));
                }
                d.factors.SetPixelData(values,mip);width=half;
            }
            d.factors.Apply(false,false);
        }
        void UpdateActivityFactors(LTWorld world,float dt)
        {
            using var profiling=ActivityMarker.Auto();
            foreach(var state in chunks.Values)
            {
                var d=state.deformation;if(d==null)continue;
                foreach(int p in d.factorTouched)d.factorTargets[p]=0;
                d.factorTouched.Clear();
            }
            foreach(var source in SimulationStates(world))
            {
                source.activityBlocks.Clear();
                if(source.gpu!=null)foreach(int tile in source.gpu.tiles.Keys)source.activityBlocks.Add(tile);
                foreach(int p in source.active)
                    source.activityBlocks.Add(LTDeformationMath.BlockIndex(p%source.size,p/source.size,source.size,ActivityBlock));
                foreach(int block in source.activityBlocks)
                {
                    var r=LTDeformationMath.BlockRect(block,source.size,source.rows,ActivityBlock);
                    // Include bilinear depth support and a small transition apron.
                    float sx=source.rect.width/(source.size-1),sz=source.rect.height/(source.rows-1);
                    var area=Rect.MinMaxRect(source.rect.xMin+(r.x-1)*sx,source.rect.yMin+(r.y-1)*sz,
                        source.rect.xMin+r.xMax*sx,source.rect.yMin+r.yMax*sz);
                    MarkActivityArea(world,area);
                }
            }
            foreach(var area in predictedMudAreas)MarkActivityArea(world,area);
            deformationFactorFading=false;
            foreach(var state in chunks.Values)
            {
                var d=state.deformation;if(d==null)continue;
                var values=d.factorMips[0];bool changed=false;
                foreach(int p in d.factorTouched)d.factorLive.Add(p);
                d.remove.Clear();
                foreach(int p in d.factorLive)
                {
                    float target=Mathf.Max(1,d.factorTargets[p]);
                    // Immediate rise avoids coarse first contact; half-second fall avoids popping.
                    float value=LTDeformationMath.FadeFactor(values[p],target,dt);
                    changed|=value!=values[p];values[p]=value;
                    if(value<=1)d.remove.Add(p);
                    if(value>target)deformationFactorFading=true;
                }
                foreach(int p in d.remove)d.factorLive.Remove(p);
                if(changed)UploadFactors(d);
            }
        }
        void MarkActivityArea(LTWorld world,Rect area)
        {
                    // Only visit chunks overlapped by this footprint plus its apron.
                    float chunkX=world.source.size.x/world.chunksX,chunkZ=world.source.size.z/world.chunksZ;
                    int cx0=Mathf.Clamp(Mathf.FloorToInt(area.xMin/chunkX)-1,0,world.chunksX-1);
                    int cx1=Mathf.Clamp(Mathf.FloorToInt(area.xMax/chunkX)+1,0,world.chunksX-1);
                    int cz0=Mathf.Clamp(Mathf.FloorToInt(area.yMin/chunkZ)-1,0,world.chunksZ-1);
                    int cz1=Mathf.Clamp(Mathf.FloorToInt(area.yMax/chunkZ)+1,0,world.chunksZ-1);
                    for(int cz=cz0;cz<=cz1;cz++)for(int cx=cx0;cx<=cx1;cx++)
                    {
                        if(!deformationChunks.TryGetValue(new Vector2Int(cx,cz),out var state))continue;
                        var d=state.deformation;
                        if(d==null||!state.displacementActive||!d.rect.Overlaps(area))continue;
                        int cells=d.size-1;
                        float dx=d.rect.width/cells,dz=d.rect.height/cells;
                        int x0=Mathf.Clamp(Mathf.FloorToInt((area.xMin-d.rect.xMin)/dx)-1,0,cells-1);
                        int x1=Mathf.Clamp(Mathf.FloorToInt((area.xMax-d.rect.xMin)/dx)+1,0,cells-1);
                        int y0=Mathf.Clamp(Mathf.FloorToInt((area.yMin-d.rect.yMin)/dz)-1,0,cells-1);
                        int y1=Mathf.Clamp(Mathf.FloorToInt((area.yMax-d.rect.yMin)/dz)+1,0,cells-1);
                        for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
                        {
                            int p=y*d.size+x;
                            float factor=Mathf.Max(Mathf.Max(d.controls[p].z,d.controls[p+1].z),
                                Mathf.Max(d.controls[p+d.size].z,d.controls[p+d.size+1].z));
                            // Fade the outer coarse-cell apron; shared edges sample the same map.
                            float px=d.rect.xMin+(x+.5f)*dx,pz=d.rect.yMin+(y+.5f)*dz;
                            int index=y*cells+x;
                            d.factorTargets[index]=Mathf.Max(d.factorTargets[index],
                                LTDeformationMath.ActivityFactor(area,px,pz,dx,dz,factor));
                            if(d.factorTargets[index]>1)d.factorTouched.Add(index);
                        }
                    }
        }
        static void BindDeformation(Material material,ChunkState state)
        {
            var d=state.deformation;
            material.SetFloat("_LTDeformationEnabled",d!=null?1:0);
            material.SetTexture("_LTDeformationDepth",d!=null&&d.depthMap?d.depthMap:Texture2D.blackTexture);
            material.SetTexture("_LTDeformationFactors",d!=null?d.factors:Texture2D.whiteTexture);
            var r=d!=null?d.rect:new Rect(0,0,1,1);
            material.SetVector("_LTDeformationRect",new Vector4(r.x,r.y,r.width,r.height));
            material.SetFloat("_LTDeformationCells",d!=null?d.size-1:1);
            int count=0;
            foreach(var region in state.deformationRegions)
            {
                if(!region.depthMap)continue;
                material.SetTexture("_LTDeformationRegion"+count,region.depthMap);
                var area=region.rect;
                material.SetVector("_LTDeformationRegionRect"+count,new Vector4(area.x,area.y,area.width,area.height));
                material.SetVector("_LTDeformationRegionSize"+count,new Vector4(region.size,region.rows,0,0));
                count++;
            }
            material.SetFloat("_LTDeformationRegionCount",count);
            for(int i=count;i<8;i++)
            {
                material.SetTexture("_LTDeformationRegion"+i,Texture2D.blackTexture);
            }
        }
        void PrepareDeformationTerrain(LTWorld world,LTChunk[] current)
        {
            bool hasMud=false;
            foreach(var state in chunks.Values)hasMud|=state.deformation!=null&&state.displacementActive;
            bool hasContact=false;
            foreach(var press in LTTerrainDeformer.Active)if(press&&press.CanPress(world)){hasContact=true;break;}
            if(hasMud&&hasContact)deformationTerrain.Update(world,current);
            else deformationTerrain.Clear();
        }
        void AppendDeformationStatus(LTWorld world)
        {
            int count=0;float texel=0;
            foreach(var state in chunks.Values)
            {
                var d=state.deformation;if(d==null)continue;
                count++;texel=Mathf.Max(texel,Mathf.Max(d.rect.width,d.rect.height)/(d.size-1));
            }
            if(count>0)world.paintStatus+=$"\nПродавливание: карта на Layer Stamp, точность настраивается в области покраски. Фон без области: шаг до {texel:0.###} м. Контакты 30 Гц, следы временные.";
            if(count>0)world.paintStatus+=" "+(gpuMode?"GPU depth/recovery.":"CPU depth/recovery.")+" Recovery LOD: 30/10/2 Hz.";
        }
        void TickDeformation(LTWorld world)
        {
            TickDeformationSimulation(world);
        }
        void PrepareDeformationRegions(LTWorld world,List<LTPaintStamp> stamps)
        {
            deformationChunks.Clear();
            foreach(var pair in chunks)deformationChunks[new Vector2Int(pair.Key.x,pair.Key.z)]=pair.Value;
            var live=new HashSet<LTPaintStamp>();
            long reserved=0;
            var ordered=new List<LTPaintStamp>();
            foreach(var stamp in stamps)if(deformationRegions.ContainsKey(stamp))ordered.Add(stamp);
            foreach(var stamp in stamps)if(!deformationRegions.ContainsKey(stamp))ordered.Add(stamp);
            foreach(var stamp in ordered)
            {
                if(!stamp.layer.deformation||stamp.layer.deformationDepth<=0)continue;
                var rect=StampBounds(world,stamp);
                rect=Rect.MinMaxRect(Mathf.Max(0,rect.xMin),Mathf.Max(0,rect.yMin),
                    Mathf.Min(world.source.size.x,rect.xMax),Mathf.Min(world.source.size.z,rect.yMax));
                if(rect.width<=0||rect.height<=0)continue;
                int width=LTDeformationMath.Resolution(rect.width,stamp.deformationStepCm,stamp.deformationMaxResolution);
                int height=LTDeformationMath.Resolution(rect.height,stamp.deformationStepCm,stamp.deformationMaxResolution);
                long bytes=(long)width*height*12; // CPU depth + recovery + GPU RFloat.
                if(live.Count>=8||reserved+bytes>128L*1024*1024)
                {
                    stamp.deformationMapStatus="Карта не создана: лимит 8 областей / 128 МиБ буферов глубины на мир. Уменьшите разрешение или число областей.";
                    continue;
                }
                reserved+=bytes;live.Add(stamp);
                int signature=Mix(rect.GetHashCode(),Mix(width,height));
                signature=Mix(signature,JsonUtility.ToJson(stamp).GetHashCode());
                foreach(var pair in chunks)
                    if(pair.Value.deformation!=null&&rect.Overlaps(pair.Value.deformation.rect))
                        signature=Mix(signature,pair.Value.densityInputHash);
                if(!deformationRegions.TryGetValue(stamp,out var d)||d.signature!=signature)
                {
                    if(d!=null)ReleaseDepth(d);
                    d=new DeformationState{stamp=stamp,rect=rect,size=width,rows=height,signature=signature};
                    deformationRegions[stamp]=d;
                }
                stamp.deformationMapStatus=$"{width}×{height}; шаг X/Z: {rect.width/(width-1)*100:0.##} / {rect.height/(height-1)*100:0.##} см. Буферы CPU+GPU: {bytes/1048576f:0.##} МиБ, плюс учёт активных ячеек. Память выделяется при первом контакте.";
            }
            var removed=new List<LTPaintStamp>();
            foreach(var pair in deformationRegions)if(!live.Contains(pair.Key))removed.Add(pair.Key);
            foreach(var key in removed){ReleaseDepth(deformationRegions[key]);deformationRegions.Remove(key);}
            foreach(var state in chunks.Values)
            {
                state.deformationRegions.Clear();
                if(state.deformation!=null)
                    foreach(var d in deformationRegions.Values)
                        if(state.deformation.rect.Overlaps(d.rect))state.deformationRegions.Add(d);
                BindDeformation(state.material,state);
                if(state.flatMaterial)BindDeformation(state.flatMaterial,state);
                if(state.farMaterial)BindDeformation(state.farMaterial,state);
            }
        }
        Vector4 RegionControl(LTWorld world,float x,float z)
        {
            int cx=Mathf.Clamp(Mathf.FloorToInt(x/world.source.size.x*world.chunksX),0,world.chunksX-1);
            int cz=Mathf.Clamp(Mathf.FloorToInt(z/world.source.size.z*world.chunksZ),0,world.chunksZ-1);
            if(!deformationChunks.TryGetValue(new Vector2Int(cx,cz),out var state)||!state.displacementActive)return Vector4.zero;
            var d=state.deformation;
            if(d==null||x<d.rect.xMin||x>d.rect.xMax||z<d.rect.yMin||z>d.rect.yMax)return Vector4.zero;
            float u=(x-d.rect.xMin)/d.rect.width*(d.size-1),v=(z-d.rect.yMin)/d.rect.height*(d.size-1);
            int ix=Mathf.Clamp(Mathf.FloorToInt(u),0,d.size-2),iy=Mathf.Clamp(Mathf.FloorToInt(v),0,d.size-2);
            int p=iy*d.size+ix;
            return Vector4.Lerp(Vector4.Lerp(d.controls[p],d.controls[p+1],u-ix),
                Vector4.Lerp(d.controls[p+d.size],d.controls[p+d.size+1],u-ix),v-iy);
        }
        void TickDeformationSimulation(LTWorld world)
        {
            if(!world.enableLayerPainting||world.paintBenchmarkRunning)return;
            if(deformationPlaying!=Application.isPlaying)
            {ClearDeformation();deformationPlaying=Application.isPlaying;}
            double now=Application.isPlaying?Time.timeAsDouble:Time.realtimeSinceStartupAsDouble;
            bool desiredGpu=world.gpuMudSimulation&&ResolveMudCompute();
            if(desiredGpu!=gpuMode){ClearDeformation();gpuMode=desiredGpu;gpuEpoch=now;}
            if(deformationTime<0){deformationTime=now;return;}
            double elapsed=now-deformationTime;
            if(elapsed<1.0/30)return;
            float dt=(float)Math.Min(elapsed,.1);deformationTime=now;
            bool preview=false;
            foreach(var press in LTTerrainDeformer.Active)
                if(press&&press.CanPress(world)){preview=true;break;}
            // A removed editor tester must not freeze recovering tracks.
            bool hasTracks=false;
            foreach(var d in SimulationStates(world))hasTracks|=d.active.Count>0||d.gpu!=null;
            if(!Application.isPlaying&&!preview&&!hasTracks&&!deformationFactorFading)return;
            // ClosestPoint must see Transform-driven testers as well as rigidbodies.
            if(preview)Physics.SyncTransforms();
            foreach(var d in SimulationStates(world))d.contacted.Clear();
            deformationLive.Clear();predictedMudAreas.Clear();
            foreach(var press in LTTerrainDeformer.Active)
            {
                if(!press||!press.CanPress(world))continue;
                deformationLive.Add(press);
                var shape=ContactShape.Capture(press);
                if(shape.kind<0){deformationPrevious.Remove(press);continue;}
                var center=shape.bounds.center;
                var previous=deformationPrevious.TryGetValue(press,out var old)?old:center;
                // Small translation sweeps fill movement between simulation ticks.
                // Teleports (>4m) deliberately do not draw a line across the world.
                float travel=Vector3.Distance(previous,center);
                if(travel<=4)PredictMud(world,shape.bounds,(center-previous)/(float)Math.Max(elapsed,.001),press.predictionSeconds,press.contactDistance);
                int steps=travel>4?1:Mathf.Clamp(Mathf.CeilToInt(travel/.05f),1,32);
                for(int step=1;step<=steps;step++)
                {
                    var offset=steps==1?Vector3.zero:Vector3.Lerp(previous,center,(float)step/steps)-center;
                    foreach(var d in SimulationStates(world))PressDeformation(world,d,press,shape,offset,dt/steps);
                }
                deformationPrevious[press]=center;
            }
            var removed=new List<LTTerrainDeformer>();
            foreach(var pair in deformationPrevious)if(!deformationLive.Contains(pair.Key))removed.Add(pair.Key);
            foreach(var key in removed)deformationPrevious.Remove(key);
            foreach(var d in SimulationStates(world))
            {
                if(d.gpu!=null){TickGpuMud(world,d,now,dt);continue;}
                if(d.depths==null)continue;
                double recoveryElapsed=now-d.lastRecovery;
                if(d.contacted.Count==0&&recoveryElapsed<MudUpdateInterval(world,d))continue;
                d.lastRecovery=now;
                d.remove.Clear();
                foreach(int p in d.active)
                {
                    // Recovery starts only after contact leaves this texel.
                    if(d.contacted.Contains(p))continue;
                    float depth=LTDeformationMath.Recover(d.depths[p],d.stamp?d.recovery[p]:d.controls[p].y,(float)recoveryElapsed);
                    if(depth!=d.depths[p]){d.depths[p]=depth;MarkDepthDirty(d,p);}
                    if(depth<=0)d.remove.Add(p);
                }
                foreach(int p in d.remove)d.active.Remove(p);
                if(d.active.Count==0)
                {
                    deformationBindingsDirty=true;
                    ReleaseDepth(d);d.dirty=false;
                    d.active.TrimExcess();d.contacted.Clear();d.contacted.TrimExcess();d.remove.Clear();d.remove.TrimExcess();
                    continue;
                }
                if(!d.dirty)continue;
                UploadDepth(d);
            }
            UpdateActivityFactors(world,dt);
            if(deformationBindingsDirty)
            {
                foreach(var state in chunks.Values)
                {
                    BindDeformation(state.material,state);
                    if(state.flatMaterial)BindDeformation(state.flatMaterial,state);
                    if(state.farMaterial)BindDeformation(state.farMaterial,state);
                }
                deformationBindingsDirty=false;
            }
        }
        void PressDeformation(LTWorld world,DeformationState d,LTTerrainDeformer press,ContactShape shape,Vector3 offset,float dt)
        {
            using var profiling=ContactMarker.Auto();
            var bounds=shape.bounds;bounds.center+=offset;bounds.Expand(2*Mathf.Max(press.contactDistance,press.edgeSoftness));
            var inverse=world.transform.worldToLocalMatrix;
            Vector3 low=new Vector3(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity),high=-low;
            for(int corner=0;corner<8;corner++)
            {
                var p=inverse.MultiplyPoint3x4(bounds.center+Vector3.Scale(bounds.extents,
                    new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1)));
                low=Vector3.Min(low,p);high=Vector3.Max(high,p);
            }
            if(high.x<d.rect.xMin||low.x>d.rect.xMax||high.z<d.rect.yMin||low.z>d.rect.yMax)return;
            int cells=d.size-1;
            int x0=Mathf.Clamp(Mathf.FloorToInt((low.x-d.rect.xMin)/d.rect.width*cells),0,cells);
            int x1=Mathf.Clamp(Mathf.CeilToInt((high.x-d.rect.xMin)/d.rect.width*cells),0,cells);
            int y0=Mathf.Clamp(Mathf.FloorToInt((low.z-d.rect.yMin)/d.rect.height*(d.rows-1)),0,d.rows-1);
            int y1=Mathf.Clamp(Mathf.CeilToInt((high.z-d.rect.yMin)/d.rect.height*(d.rows-1)),0,d.rows-1);
            for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)
            {
                int p=y*d.size+x;
                float px=d.rect.xMin+d.rect.width*x/cells,pz=d.rect.yMin+d.rect.height*y/(d.rows-1);
                var control=d.stamp?RegionControl(world,px,pz):d.controls[p];
                float limit=control.x;if(limit<=0)continue;
                if(!deformationTerrain.Sample(px,pz,out float height,out _))continue;
                var point=world.transform.TransformPoint(new Vector3(px,height,pz));
                var closest=shape.Closest(point-offset)+offset;
                var delta=point-closest;
                float horizontal=new Vector2(delta.x,delta.z).magnitude;
                float contact=LTDeformationMath.Contact(horizontal,Mathf.Abs(delta.y),press.edgeSoftness,press.contactDistance);
                if(contact<=0)continue;
                if(gpuMode)
                {
                    // A GPU failure switches the whole backend on the next tick.
                    // Never attach CPU arrays to a still-live GPU state mid-frame.
                    if(!mudGpuFailed)AddGpuContact(d,p,control,press.pressure*contact*dt,dt,deformationTime);
                    continue;
                }
                bool firstContact=d.contacted.Add(p);
                if(d.depths==null)
                {
                    deformationBindingsDirty=true;
                    AllocateDepth(d);d.lastRecovery=deformationTime;
                }
                if(firstContact&&d.depths[p]>0)
                {
                    float oldRate=d.stamp?d.recovery[p]:d.controls[p].y;
                    float overdue=(float)Math.Max(0,deformationTime-d.lastRecovery-dt);
                    float recovered=LTDeformationMath.Recover(d.depths[p],oldRate,overdue);
                    if(recovered!=d.depths[p]){d.depths[p]=recovered;MarkDepthDirty(d,p);}
                }
                if(d.stamp)d.recovery[p]=control.y;
                float depth=LTDeformationMath.Press(d.depths[p],limit,press.pressure,contact,dt);
                if(depth==d.depths[p])continue;
                d.depths[p]=depth;d.active.Add(p);MarkDepthDirty(d,p);
            }
        }
    }
}
