using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // LOD0 only; spatial bins avoid raycasts/collider lag and full mesh scans per texel.
    // Cache rebuilds on terrain generator revision, mesh replacement or transform change.
    internal sealed class LTPaintTerrain
    {
        sealed class Tile
        {
            public Mesh mesh;
            public double revision;
            public Matrix4x4 matrix;
            public Rect rect;
            public Vector3[] vertices,normals;
            public int[] indices;
            public int binAxis;
            public List<int>[] bins;
            public string hash;
        }
        readonly Dictionary<LTChunk,Tile> cache=new Dictionary<LTChunk,Tile>();
        readonly Dictionary<Vector2Int,Tile> tiles=new Dictionary<Vector2Int,Tile>();
        float width,depth,sizeX,sizeZ;
        int countX,countZ;
        public string signature="";
        public void Clear(){cache.Clear();tiles.Clear();signature="";}
        public void Update(LTWorld world,LTChunk[] chunks)
        {
            sizeX=world.source.size.x;sizeZ=world.source.size.z;countX=world.chunksX;countZ=world.chunksZ;
            width=sizeX/countX;depth=sizeZ/countZ;tiles.Clear();
            var live=new HashSet<LTChunk>(chunks);
            foreach(var key in new List<LTChunk>(cache.Keys))if(!key||!live.Contains(key))cache.Remove(key);
            var keys=new List<Vector2Int>();
            foreach(var chunk in chunks)
            {
                if(!chunk.mesh||!chunk.mesh.isReadable)continue;
                var matrix=world.transform.worldToLocalMatrix*chunk.transform.localToWorldMatrix;
                var rect=new Rect(chunk.x*width,chunk.z*depth,width,depth);
                if(!cache.TryGetValue(chunk,out var tile)||tile.mesh!=chunk.mesh||tile.revision!=chunk.updatedAt||tile.matrix!=matrix||tile.rect!=rect)
                {
                    tile=new Tile{mesh=chunk.mesh,revision=chunk.updatedAt,matrix=matrix,rect=rect,
                        vertices=chunk.mesh.vertices,normals=chunk.mesh.normals,indices=chunk.mesh.triangles};
                    var normalMatrix=matrix.inverse.transpose;
                    var fingerprint=new LTPaintMath.GeometryFingerprint();
                    fingerprint.Add(tile.vertices.Length);fingerprint.Add(tile.normals.Length==tile.vertices.Length?1:0);
                    fingerprint.Add(tile.indices.Length);
                    for(int i=0;i<tile.vertices.Length;i++)
                    {
                        tile.vertices[i]=matrix.MultiplyPoint3x4(tile.vertices[i]);
                        fingerprint.Add(tile.vertices[i]);
                        if(tile.normals.Length==tile.vertices.Length)
                        {
                            tile.normals[i]=normalMatrix.MultiplyVector(tile.normals[i]).normalized;
                            fingerprint.Add(tile.normals[i]);
                        }
                    }
                    foreach(int index in tile.indices)fingerprint.Add(index);
                    tile.hash=fingerprint.ToString();
                    tile.binAxis=LTPaintMath.TerrainBinAxis(tile.indices.Length/3);
                    tile.bins=new List<int>[tile.binAxis*tile.binAxis];
                    for(int i=0;i+2<tile.indices.Length;i+=3)
                    {
                        var a=tile.vertices[tile.indices[i]];var b=tile.vertices[tile.indices[i+1]];var c=tile.vertices[tile.indices[i+2]];
                        var range=LTPaintMath.TerrainTriangleBins(rect,a,b,c,tile.binAxis);
                        for(int z=range.yMin;z<range.yMax;z++)for(int x=range.xMin;x<range.xMax;x++)
                        {int id=z*tile.binAxis+x;if(tile.bins[id]==null)tile.bins[id]=new List<int>();tile.bins[id].Add(i);}
                    }
                    cache[chunk]=tile;
                }
                var key=new Vector2Int(chunk.x,chunk.z);tiles[key]=tile;keys.Add(key);
            }
            keys.Sort((a,b)=>a.y!=b.y?a.y.CompareTo(b.y):a.x.CompareTo(b.x));
            var combined=new System.Text.StringBuilder();
            foreach(var key in keys)combined.Append(key.x).Append(':').Append(key.y).Append(':').Append(tiles[key].hash).Append(';');
            signature=Hash128.Compute(combined.ToString()).ToString();
        }
        public bool Sample(float x,float z,out float height,out float slope)
            =>Sample(x,z,out height,out slope,out _);
        public int RegionSignature(Rect area,float radius)
        {
            var range=LTPaintMath.TerrainSampleTileRange(area,radius,width,depth,countX,countZ);
            int Mix(int a,int b)=>unchecked(a*397^b);
            int hash=Mix(Mix(countX,countZ),Mix(width.GetHashCode(),depth.GetHashCode()));
            for(int z=range.yMin;z<range.yMax;z++)for(int x=range.xMin;x<range.xMax;x++)
            {
                hash=Mix(Mix(hash,x),z);
                bool present=tiles.TryGetValue(new Vector2Int(x,z),out var tile);
                hash=Mix(hash,present?1:0);
                if(present)hash=Mix(hash,tile.hash.GetHashCode());
            }
            return hash;
        }
        public bool Sample(float x,float z,out float height,out float slope,out Vector3 normal)
        {
            x=Mathf.Clamp(x,0,sizeX);z=Mathf.Clamp(z,0,sizeZ);height=0;slope=0;normal=Vector3.up;
            var key=new Vector2Int(Mathf.Clamp(Mathf.FloorToInt(x/width),0,countX-1),Mathf.Clamp(Mathf.FloorToInt(z/depth),0,countZ-1));
            if(!tiles.TryGetValue(key,out var tile))return false;
            var bin=tile.bins[LTPaintMath.TerrainBin(z,tile.rect.yMin,depth,tile.binAxis)*tile.binAxis+LTPaintMath.TerrainBin(x,tile.rect.xMin,width,tile.binAxis)];
            if(bin==null)return false;
            bool found=false;float top=float.NegativeInfinity;
            foreach(int t in bin)
            {
                int ia=tile.indices[t],ib=tile.indices[t+1],ic=tile.indices[t+2];
                var a=tile.vertices[ia];var b=tile.vertices[ib];var c=tile.vertices[ic];
                if(!LTPaintMath.TriangleWeights(new Vector2(x,z),a,b,c,out var weights))continue;
                float u=weights.x,v=weights.y,w=weights.z;
                float y=u*a.y+v*b.y+w*c.y;if(y<top)continue;
                top=y;found=true;
                normal=tile.normals.Length==tile.vertices.Length?(u*tile.normals[ia]+v*tile.normals[ib]+w*tile.normals[ic]).normalized:Vector3.Cross(b-a,c-a).normalized;
            }
            if(!found)return false;
            height=top;slope=Mathf.Acos(Mathf.Clamp(Mathf.Abs(normal.y),0,1))*Mathf.Rad2Deg;return true;
        }
    }
    // Separate from mesh generation. All generated materials/weight maps are transient.
    // Twelve slots include the base layer; texture assets are never modified.
    public sealed partial class LTPaintRuntime : IDisposable
    {
        public void SetHullOneDiagnostic(bool enabled)
        {
            foreach(var state in chunks.Values)
            {
                // The regular-grid mode owns factor 1 independently of debug toggles.
                float value=enabled||state.regularGridDisplacement?1:0;
                state.hullOneDiagnostic=value;
                void Set(Material material)
                {
                    if(material&&material.GetFloat("_LTForceHullOne")!=value)material.SetFloat("_LTForceHullOne",value);
                }
                Set(state.material);Set(state.flatMaterial);Set(state.farMaterial);
            }
        }
        public IEnumerable<string> HullOneDiagnosticStatus
        {
            get
            {
                foreach(var pair in chunks)
                {
                    if(!pair.Key||!pair.Value.displacementActive)continue;
                    var m=pair.Value.renderer?pair.Value.renderer.sharedMaterial:null;
                    float probe=m&&m.HasProperty("_LTForceHullOne")?m.GetFloat("_LTForceHullOne"):-1;
                    float factor=m&&m.HasProperty("_TessellationFactor")?m.GetFloat("_TessellationFactor"):-1;
                    yield return $"Чанк ({pair.Key.x}, {pair.Key.z}): Hull-тест = {probe}; обычный фактор = {factor}.\nТекущий шейдер: {(m&&m.shader?m.shader.name:"нет")}";
                }
            }
        }
        public void SetCoverageDebug(bool enabled,bool controlColor=false,bool fixedProjection=false,bool coordinateProbe=false,bool hullReasons=false,int surfaceProbe=0)
        {
            foreach(var state in chunks.Values)
            {
                // -1 is an explicitly empty mask, not the texture's white fallback.
                float mode=!enabled?0:surfaceProbe>=6&&surfaceProbe<=16?surfaceProbe:controlColor?2:state.displacementActive&&state.displacementOccupancy?(hullReasons?5:coordinateProbe?4:fixedProjection?3:1):-1;
                if(state.debugCoverageMode==mode)continue;
                state.debugCoverageMode=mode;
                BindCoverage(state.material,state);
                BindCoverage(state.flatMaterial,state);
                BindCoverage(state.farMaterial,state);
                if(state.material)state.material.SetFloat("_LTDebugCoverage",mode);
                if(state.flatMaterial)state.flatMaterial.SetFloat("_LTDebugCoverage",mode);
                if(state.farMaterial)state.farMaterial.SetFloat("_LTDebugCoverage",mode);
            }
        }
        public sealed class DensityCoverage
        {
            public int id;
            public int x,z;
            public Rect rect;
            public LTPaintMath.CoverageGrid grid;
        }
        public sealed class CoverageDiagnostic
        {
            public string label,status;
            public Texture2D preview;
        }
        public IEnumerable<CoverageDiagnostic> CoverageDiagnostics
        {
            get
            {
                foreach(var pair in chunks)
                {
                    var state=pair.Value;
                    if(!pair.Key||!state.displacementOccupancy)continue;
                    if(!state.coveragePreview)
                    {
                        var pixels=state.displacementOccupancy.GetPixels32(0);
                        int white=0;
                        for(int i=0;i<pixels.Length;i++)
                        {
                            byte v=pixels[i].r;if(v>0)white++;
                            pixels[i]=new Color32(v,v,v,255);
                        }
                        state.coverageWhite=white;
                        int size=state.displacementOccupancy.width;
                        state.coveragePreview=new Texture2D(size,size,TextureFormat.RGBA32,false,true)
                            {name="Coverage diagnostic preview",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point};
                        state.coveragePreview.SetPixels32(pixels);state.coveragePreview.Apply(false,false);
                    }
                    var material=state.renderer?state.renderer.sharedMaterial:null;
                    bool bound=material&&material.HasProperty("_LTDisplacementOccupancy")&&material.GetTexture("_LTDisplacementOccupancy")==state.displacementOccupancy;
                    float mode=material&&material.HasProperty("_LTDebugCoverage")?material.GetFloat("_LTDebugCoverage"):0;
                    var boundRect=material&&material.HasProperty("_LTDisplacementRect")?material.GetVector("_LTDisplacementRect"):Vector4.zero;
                    bool worldMapping=material&&material.HasProperty("_LTCoverageUseWorldPosition")&&material.GetFloat("_LTCoverageUseWorldPosition")>.5f;
                    int total=state.displacementOccupancy.width*state.displacementOccupancy.height;
                    yield return new CoverageDiagnostic{preview=state.coveragePreview,label=$"Чанк ({pair.Key.x}, {pair.Key.z}) — маска mip 0",
                        status=$"Белых ячеек: {state.coverageWhite} / {total} ({100f*state.coverageWhite/total:F2}%). Размер: {state.displacementOccupancy.width}×{state.displacementOccupancy.height}.\n"+
                            $"Область XZ: {state.displacementRect}. Displacement активен: {state.displacementActive}.\n"+
                            $"Карта назначена текущему материалу: {bound}; режим диагностики: {mode}.\nШейдер: {(material&&material.shader?material.shader.name:"нет")}"};
                    // Both expected crop and live material values are shown above/below:
                    // a texture reference alone is not proof of correct GPU coordinates.
                    var worldSize=material&&material.HasProperty("_LTWorldSize")?material.GetVector("_LTWorldSize"):Vector4.zero;
                    yield return new CoverageDiagnostic{label="Привязка GPU",status=$"Границы в материале: {boundRect}. Источник координат: {(worldMapping?"позиция мира":"UV0 × размер LTWorld")}. Размер LTWorld в материале: {worldSize}."};
                }
            }
        }
        public IEnumerable<DensityCoverage> DensityCoverages
        {
            get {foreach(var state in chunks.Values)if(state.coverageActive&&state.densityCoverage!=null)yield return state.densityCoverage;}
        }
        sealed class ChunkState
        {
            public MeshRenderer renderer;
            public Material original, material, farMaterial, bakeMaterial;
            public Material flatMaterial;
            public bool displacementActive;
            public DeformationState deformation;
            public readonly List<DeformationState> deformationRegions=new List<DeformationState>();
            public bool coverageActive;
            public bool regularGridDisplacement;
            public float debugCoverageMode=float.NaN;
            public float hullOneDiagnostic=float.NaN;
            public Bounds originalLocalBounds;
            public Bounds sourceBounds;
            public bool boundsExpanded;
            public Texture2D weights0, weights1, weights2;
            public Texture2D displacementOccupancy;
            public Texture2D displacementEdgeDistance;
            public Texture2D coveragePreview;
            public int coverageWhite;
            public Rect displacementRect;
            public Matrix4x4 coverageWorldToLocal=Matrix4x4.identity;
            public int occupancyLayers;
            public int densityInputHash;
            public DensityCoverage densityCoverage;
            public int coverageHash, surfaceHash;
            public int weightHash;
            public LTRoadMath.Snapshot[] weightRoads;
            public LTSurfaceLayer[] detailLayers;
            public LTDetailSurface.Tile detailSnapshot;
            public bool ready, globallyBaked;
            public Mesh globalGeometryMesh;
            public double globalGeometryRevision;
            public Matrix4x4 globalGeometryTransform;
            public bool globalBindingReady,farMaterialDirty=true;
            public Vector4 boundGlobalParams,boundFarSurface;
            public Texture boundGlobalColor,boundGlobalNormal;
        }
        sealed class MaskCopy
        {
            public Hash128 hash;
            public Texture2D copy;
            public LTDetailSurface.Image detailImage;
        }
        readonly Dictionary<LTChunk,ChunkState> chunks=new Dictionary<LTChunk,ChunkState>();
        readonly Dictionary<Texture2D,MaskCopy> masks=new Dictionary<Texture2D,MaskCopy>();
        readonly LTPaintTerrain terrain=new LTPaintTerrain();
        readonly LTLayerTextureArrays layerArrays=new LTLayerTextureArrays();
        LTWorld arrayWorld;
        bool arraysAvailable;
        public void BindLayerArrays(Material material,IList<LTSurfaceLayer> layers)=>layerArrays.Bind(material,layers);
        double nextUpdate;
        public const int LayerCapacity=12;
        const int Resolution=257;
        static int Mix(int hash,int value)=>unchecked(hash*397^value);

        // Tick-local snapshots: refresh every poll (including Undo/texture imports),
        // but serialize each stamp and read each texture hash only once, not per chunk.
        static int[] CoverageInputs(LTWorld world,LTPaintStamp stamp)
        {
            var values=new List<int>(16){Id(stamp),Id(stamp.EffectiveLayer)};
            var road=stamp.Road;
            if(road)values.Add(road.Capture(world).paintHash);
            else if(stamp.Junction)values.Add(stamp.Junction.Capture().hash);
            else
            {
                values.Add(stamp.transform.localToWorldMatrix.GetHashCode());values.Add(stamp.size.GetHashCode());
                values.Add((int)stamp.shape);values.Add(stamp.strength.GetHashCode());values.Add(stamp.edgeFalloff.GetHashCode());
                values.Add(Id(stamp.mask));if(stamp.mask)values.Add(stamp.mask.imageContentsHash.GetHashCode());
                values.Add(JsonUtility.ToJson(stamp.heightFilter).GetHashCode());
                values.Add(JsonUtility.ToJson(stamp.slopeFilter).GetHashCode());
                values.Add(JsonUtility.ToJson(stamp.curveFilter).GetHashCode());
                values.Add(stamp.curveRadius.GetHashCode());values.Add(stamp.noise.CoverageHash());
            }
            return values.ToArray();
        }
        readonly struct LayerChangeInput
        {
            readonly int surface,color,normal,mask;
            readonly bool hasColor,hasNormal,hasMask;
            public LayerChangeInput(LTSurfaceLayer layer)
            {
                surface=layer.SurfaceHash();hasColor=layer.baseColorMap;hasNormal=layer.normalMap;hasMask=layer.maskMap;
                color=hasColor?layer.baseColorMap.imageContentsHash.GetHashCode():0;
                normal=hasNormal?layer.normalMap.imageContentsHash.GetHashCode():0;
                mask=hasMask?layer.maskMap.imageContentsHash.GetHashCode():0;
            }
            public int AppendSurface(int hash)
            {
                hash=Mix(hash,surface);
                if(hasColor)hash=Mix(hash,color);if(hasNormal)hash=Mix(hash,normal);if(hasMask)hash=Mix(hash,mask);
                return hash;
            }
            public int AppendDensity(int hash)
            {
                hash=Mix(hash,surface);return hasMask?Mix(hash,mask):hash;
            }
        }
        static float TerrainFilterRadius(List<LTPaintStamp> stamps)
        {
            float radius=0;
            foreach(var stamp in stamps)if(stamp.HasTerrainFilters&&stamp.curveFilter.enabled)
                radius=Mathf.Max(radius,Mathf.Max(.1f,stamp.curveRadius));
            return radius;
        }
        int TerrainFilterSignature(Rect rect,List<LTPaintStamp> stamps)
            =>terrain.RegionSignature(rect,TerrainFilterRadius(stamps));
        static int Id(UnityEngine.Object obj)=>obj?obj.GetInstanceID():0;
        static void DestroyOwned(UnityEngine.Object obj)
        {
            if(!obj)return;
            if(Application.isPlaying)UnityEngine.Object.Destroy(obj);else UnityEngine.Object.DestroyImmediate(obj);
        }
        static void Release(ChunkState state)
        {
            ReleaseRoadProjection(state);
            ReleaseDeformation(state);
            if(state.renderer&&(state.renderer.sharedMaterial==state.material||state.renderer.sharedMaterial==state.farMaterial||state.renderer.sharedMaterial==state.flatMaterial))state.renderer.sharedMaterial=state.original;
            if(state.renderer&&state.boundsExpanded)state.renderer.localBounds=state.originalLocalBounds;
            DestroyOwned(state.flatMaterial);
            DestroyOwned(state.displacementOccupancy);
            DestroyOwned(state.displacementEdgeDistance);
            DestroyOwned(state.coveragePreview);
            DestroyOwned(state.material);DestroyOwned(state.farMaterial);DestroyOwned(state.bakeMaterial);DestroyOwned(state.weights0);DestroyOwned(state.weights1);DestroyOwned(state.weights2);
        }
        public void RestoreMaterialsForSave()
        {
            RestoreRockMaterialsForSave();
            foreach(var state in chunks.Values)
            {
                if(state.renderer&&(state.renderer.sharedMaterial==state.material||state.renderer.sharedMaterial==state.farMaterial||state.renderer.sharedMaterial==state.flatMaterial))
                    state.renderer.sharedMaterials=new[]{state.original};
            }
        }
        public void Dispose()
        {
            arraysAvailable=false;
            if(arrayWorld){arrayWorld.arrayColorPreview=arrayWorld.arrayNormalPreview=arrayWorld.arrayMaskPreview=null;arrayWorld.arrayPackStatus="Массивы освобождены. Включите покраску для упаковки.";}
            arrayWorld=null;
            ReleaseDeformationRegions();
            ReleaseRockMaterials();
            ReleaseGlobals();
            foreach(var state in chunks.Values)Release(state);
            layerArrays.Dispose();
            chunks.Clear();
            foreach(var mask in masks.Values)DestroyOwned(mask.copy);
            masks.Clear();
            terrain.Clear();
            deformationTerrain.Clear();deformationPrevious.Clear();deformationTime=-1;
        }
        public static Rect StampBounds(LTWorld world,LTPaintStamp stamp)
        {
            var road=stamp.Road;if(road)return road.Capture(world).bounds;
            if(stamp.Junction)return stamp.Junction.Capture().bounds;
            var matrix=world.transform.worldToLocalMatrix*stamp.transform.localToWorldMatrix;
            Vector2 min=new Vector2(float.PositiveInfinity,float.PositiveInfinity),max=-min;
            for(int z=-1;z<=1;z+=2)for(int x=-1;x<=1;x+=2)
            {
                var p=matrix.MultiplyPoint3x4(new Vector3(x*stamp.size.x*.5f,0,z*stamp.size.y*.5f));
                var q=new Vector2(p.x,p.z);min=Vector2.Min(min,q);max=Vector2.Max(max,q);
            }
            return Rect.MinMaxRect(min.x,min.y,max.x,max.y);
        }
        static bool Touches(Rect a,Rect b)=>a.xMin<=b.xMax&&a.xMax>=b.xMin&&a.yMin<=b.yMax&&a.yMax>=b.yMin;
        public static bool Projection(LTWorld world,LTPaintStamp stamp,out Matrix4x4 inverse)
        {
            var m=world.transform.worldToLocalMatrix*stamp.transform.localToWorldMatrix;
            float det=m.m00*m.m22-m.m02*m.m20;inverse=Matrix4x4.zero;
            if(Mathf.Abs(det)<.000001f)return false;
            inverse.m00=m.m22/det;inverse.m02=-m.m02/det;
            inverse.m20=-m.m20/det;inverse.m22=m.m00/det;
            inverse.m03=-(inverse.m00*m.m03+inverse.m02*m.m23);
            inverse.m23=-(inverse.m20*m.m03+inverse.m22*m.m23);inverse.m33=1;
            return true;
        }
        Texture2D ReadMask(Texture2D source)
        {
            if(!source)return null;
            var hash=source.imageContentsHash;
            if(masks.TryGetValue(source,out var old)&&old.hash==hash)return old.copy;
            // GPU copy also works when the imported mask has Read/Write disabled.
            var rt=RenderTexture.GetTemporary(source.width,source.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            var previous=RenderTexture.active;
            Texture2D copy=null;
            try
            {
                Graphics.Blit(source,rt);RenderTexture.active=rt;
                copy=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false,true){hideFlags=HideFlags.HideAndDontSave};
                copy.ReadPixels(new Rect(0,0,source.width,source.height),0,0);copy.Apply(false,false);
            }
            catch {DestroyOwned(copy);throw;}
            finally {RenderTexture.active=previous;RenderTexture.ReleaseTemporary(rt);}
            if(old!=null)DestroyOwned(old.copy);
            masks[source]=new MaskCopy{hash=hash,copy=copy};return copy;
        }
        static Texture2D NewWeights(string name)=>new Texture2D(Resolution,Resolution,TextureFormat.RGBA32,false,true)
            {name=name,hideFlags=HideFlags.HideAndDontSave,wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
        static bool SamePaintRoads(LTRoadMath.Snapshot[] previous,List<LTRoadMath.Snapshot> current,Rect rect)
        {
            if(previous==null||previous.Length!=current.Count)return false;
            for(int i=0;i<previous.Length;i++)if(!previous[i].SamePaintRegion(current[i],rect))return false;
            return true;
        }
        void Bake(LTWorld world,Rect rect,List<LTPaintStamp> stamps,List<LTSurfaceLayer> layers,ChunkState state,List<LTRoadMath.Snapshot> asphaltInputs,bool bakeWeights=true)
        {
            // Projection retains its whole-spline dependencies (arc length,
            // endpoint extension and shared-layer ownership), even on weight reuse.
            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.RoadProjection))
                if(BakeRoadProjection(world,rect,stamps,layers,state,asphaltInputs))world.paintCpu.RoadProjectionBaked();
            if(!bakeWeights){world.paintCpu.WeightReused();return;}
            Color32[] first,second,third;
            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightCompute))
            {
                var inverse=new Matrix4x4[stamps.Count];var copies=new Texture2D[stamps.Count];var slots=new int[stamps.Count];var wheelSlots=new int[stamps.Count];
                var roads=new LTRoadMath.Snapshot[stamps.Count];
                var junctions=new LTRoadJunctionMath.Snapshot[stamps.Count];
                var terrainFilters=new bool[stamps.Count];
                for(int i=0;i<stamps.Count;i++)
                {
                    var road=stamps[i].Road;if(road)roads[i]=road.Capture(world);
                    if(stamps[i].Junction)junctions[i]=stamps[i].Junction.Capture();
                    terrainFilters[i]=stamps[i].HasTerrainFilters;
                    Projection(world,stamps[i],out inverse[i]);
                    copies[i]=ReadMask(stamps[i].mask);slots[i]=layers.IndexOf(stamps[i].EffectiveLayer);
                    wheelSlots[i]=layers.IndexOf(stamps[i].SecondaryLayer);
                }
                first=new Color32[Resolution*Resolution];second=new Color32[first.Length];third=new Color32[first.Length];var weights=new float[LayerCapacity];
                var samples=new LTPaintMath.PixelTerrainCache(terrain.Sample,world.source.size.x,world.source.size.z,stamps.Count);
                for(int y=0;y<Resolution;y++)for(int x=0;x<Resolution;x++)
                {
                    Array.Clear(weights,0,weights.Length);weights[0]=1;
                    var point=new Vector3(rect.xMin+rect.width*x/(Resolution-1),0,rect.yMin+rect.height*y/(Resolution-1));
                    samples.Begin(point.x,point.z);
                    for(int i=0;i<stamps.Count;i++)
                    {
                        if(roads[i]!=null)
                        {
                            roads[i].PaintWeights(point.x,point.z,out float ground,out float wheel);
                            if(slots[i]>=0)LTPaintMath.Composite(weights,slots[i],ground);
                            if(wheelSlots[i]>=0)LTPaintMath.Composite(weights,wheelSlots[i],wheel);
                            continue;
                        }
                        if(junctions[i]!=null)
                        {LTPaintMath.Composite(weights,slots[i],junctions[i].Weight(point.x,point.z));continue;}
                        var stamp=stamps[i];var local=inverse[i].MultiplyPoint3x4(point);
                        var p=new Vector2(local.x/Mathf.Max(.01f,stamp.size.x)*2,local.z/Mathf.Max(.01f,stamp.size.y)*2);
                        float alpha=LTPaintMath.Coverage(p,stamp.shape==LTStampShape.Rectangle,stamp.edgeFalloff,stamp.strength);
                        if(alpha<=0)continue;
                        if(copies[i])alpha*=copies[i].GetPixelBilinear(p.x*.5f+.5f,p.y*.5f+.5f).r;
                        if(stamp.noise.enabled)
                            alpha*=LTPaintMath.NoiseCoverage(new Vector2(local.x,local.z),stamp.noise.size,stamp.noise.seed,
                                stamp.noise.strength,stamp.noise.threshold,stamp.noise.softness);
                        if(alpha<=0)continue;
                        if(terrainFilters[i])
                        {
                            if(!samples.Surface(out float height,out float slope))continue;
                            alpha*=stamp.heightFilter.Evaluate(height)*stamp.slopeFilter.Evaluate(slope);
                            if(alpha>0&&stamp.curveFilter.enabled)
                            {
                                if(!samples.Curvature(stamp.curveRadius,out float curvature))continue;
                                alpha*=stamp.curveFilter.Evaluate(curvature);
                            }
                        }
                        LTPaintMath.Composite(weights,slots[i],alpha);
                    }
                    int index=y*Resolution+x;
                    first[index]=new Color(weights[0],weights[1],weights[2],weights[3]);
                    second[index]=new Color(weights[4],weights[5],weights[6],weights[7]);
                    third[index]=new Color(weights[8],weights[9],weights[10],weights[11]);
                }
            }
            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightUpload))
            {
                if(!state.weights0)state.weights0=NewWeights("Layer weights 0–3");
                if(!state.weights1)state.weights1=NewWeights("Layer weights 4–7");
                if(!state.weights2)state.weights2=NewWeights("Layer weights 8–11");
                state.weights0.SetPixels32(first);state.weights0.Apply(false,false);
                state.weights1.SetPixels32(second);state.weights1.Apply(false,false);
                state.weights2.SetPixels32(third);state.weights2.Apply(false,false);
            }
            world.paintCpu.WeightBaked();
        }
        sealed class MaskReadbackCache
        {
            const long Budget=64L*1024*1024;
            readonly Dictionary<Texture2D,Color32[]> images=new Dictionary<Texture2D,Color32[]>();
            long bytes;
            public Color32[] Read(Texture2D texture)
            {
                if(images.TryGetValue(texture,out var pixels))return pixels;
                pixels=texture.GetPixels32();
                long size=(long)pixels.Length*4;
                // Cache is tick-local AND bounded; oversized/overflow images still
                // work but are retained only by their current chunk's bake.
                if(size<=Budget-bytes){images.Add(texture,pixels);bytes+=size;}
                return pixels;
            }
        }
        List<Color32[]> BakeDisplacementCoverage(LTWorld world,Rect rect,List<LTPaintStamp> stamps,List<LTSurfaceLayer> layers,ChunkState state,int active,
            MaskReadbackCache maskReadbacks)
        {
            // Crop to displaced footprints, rather than spending 256 cells on an entire
            // large terrain chunk. Sample the very same interpolated weights as the GPU.
            var area=rect;
            if((active&1)==0)
            {
                float x0=rect.xMax,y0=rect.yMax,x1=rect.xMin,y1=rect.yMin;
                foreach(var stamp in stamps)
                {
                    int primary=layers.IndexOf(stamp.EffectiveLayer),secondary=layers.IndexOf(stamp.SecondaryLayer);
                    if(!(primary>=0&&(active&(1<<primary))!=0)&&!(secondary>=0&&(active&(1<<secondary))!=0))continue;
                    var b=StampBounds(world,stamp);x0=Mathf.Min(x0,b.xMin);y0=Mathf.Min(y0,b.yMin);x1=Mathf.Max(x1,b.xMax);y1=Mathf.Max(y1,b.yMax);
                }
                // Weight interpolation can extend a footprint by one source-map cell.
                float pad=Mathf.Max(rect.width,rect.height)/(Resolution-1);
                area=Rect.MinMaxRect(Mathf.Max(rect.xMin,x0-pad),Mathf.Max(rect.yMin,y0-pad),Mathf.Min(rect.xMax,x1+pad),Mathf.Min(rect.yMax,y1+pad));
            }
            int cells=Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Max(area.width,area.height)/.125f)),32,1024),res=cells+1;
            BeginDeformationControls(state,area,cells,layers);
            var mudSettings=DeformationSettings(layers);
            var maps=new Texture2D[LayerCapacity];
            for(int i=0;i<layers.Count;i++)if(!(i==0&&world.lightweightBackground))maps[i]=ReadMask(layers[i].maskMap);
            // Read once per bake, never per sample. Use the GPU's texel-centre
            // convention for both clamped weights and repeated height textures.
            var weightPixels0=state.weights0.GetPixels32();var weightPixels1=state.weights1.GetPixels32();var weightPixels2=state.weights2.GetPixels32();
            var maskPixels=new Color32[LayerCapacity][];
            var maskSizes=new Vector2Int[LayerCapacity];
            for(int i=0;i<layers.Count;i++)if(maps[i])
            {
                // These immutable copies are shared only within this Tick, keyed by
                // the actual ReadMask result. Reimports/replacements get a new key.
                maskPixels[i]=maskReadbacks.Read(maps[i]);maskSizes[i]=new Vector2Int(maps[i].width,maps[i].height);
            }
            var first=new Color32[res*res];var second=new Color32[first.Length];
            var ordinaryFirst=state.deformation!=null?new Color32[first.Length]:null;
            int ordinaryLayers=0;
            for(int i=0;i<layers.Count;i++)
                if(world.enableLayerDisplacement&&!(i==0&&world.lightweightBackground)&&layers[i].displacement&&layers[i].maskMap&&layers[i].displacementAmplitude>0)
                    ordinaryLayers|=1<<i;
            var weights=new float[LayerCapacity];var heights=new float[LayerCapacity];
            for(int y=0;y<res;y++)for(int x=0;x<res;x++)
            {
                float px=area.xMin+area.width*x/cells,pz=area.yMin+area.height*y/cells;
                float u=((px-rect.xMin)/rect.width*(Resolution-1)+.5f)/Resolution,v=((pz-rect.yMin)/rect.height*(Resolution-1)+.5f)/Resolution;
                Color a=LTPaintMath.SampleGpuBilinear(weightPixels0,Resolution,Resolution,u,v,false);
                Color b=LTPaintMath.SampleGpuBilinear(weightPixels1,Resolution,Resolution,u,v,false);
                Color c=LTPaintMath.SampleGpuBilinear(weightPixels2,Resolution,Resolution,u,v,false);
                for(int i=0;i<LayerCapacity;i++)
                {
                    weights[i]=i<4?a[i]:i<8?b[i-4]:c[i-8];heights[i]=.5f;
                    if(i>=layers.Count)continue;
                    var layer=layers[i];float h=.5f;
                    if(maskPixels[i]!=null&&weights[i]>.00001f)
                    {
                        var textureUV=new Vector2((px+layer.tileOffsetMetres.x)/Mathf.Max(.001f,layer.tileSizeMetres.x),
                            (pz+layer.tileOffsetMetres.y)/Mathf.Max(.001f,layer.tileSizeMetres.y));
                        if(TryRoadProjectionUV(state,i,px,pz,out var roadUV))textureUV=roadUV;
                        h=LTPaintMath.SampleGpuBilinear(maskPixels[i],maskSizes[i].x,maskSizes[i].y,
                            textureUV.x,textureUV.y,true).g;
                    }
                    if(!(i==0&&world.lightweightBackground))heights[i]=Mathf.Clamp01((h-.5f)*layer.heightStrength+.5f+layer.heightOffset);
                }
                float roadDisplacement=RoadDisplacementMultiplier(state,px,pz);
                bool occupied=roadDisplacement>0&&LTPaintMath.DisplacementVisibility(weights,heights,world.layerHeightBlend,active)>LTPaintMath.DisplacementVisibilityStart;
                first[y*res+x]=new Color32((byte)(occupied?255:0),0,0,255);
                if(ordinaryFirst!=null)
                {
                    bool ordinary=roadDisplacement>0&&LTPaintMath.DisplacementVisibility(weights,heights,world.layerHeightBlend,ordinaryLayers)>LTPaintMath.DisplacementVisibilityStart;
                    ordinaryFirst[y*res+x]=new Color32((byte)(ordinary?255:0),0,0,255);
                }
                if(state.deformation!=null)
                {
                    int stride=cells/(state.deformation.size-1);
                    if(x%stride==0&&y%stride==0)
                        state.deformation.controls[(y/stride)*state.deformation.size+x/stride]=
                            LTDeformationMath.Controls(weights,heights,mudSettings,world.layerHeightBlend);
                }
            }
            FinishDeformationControls(state);
            state.displacementRect=area;
            var distances=LTPaintMath.MaskInteriorDistance(first,res,
                area.width/cells*world.transform.TransformVector(Vector3.right).magnitude,
                area.height/cells*world.transform.TransformVector(Vector3.forward).magnitude);
            if(state.displacementEdgeDistance&&state.displacementEdgeDistance.width!=res)
            {DestroyOwned(state.displacementEdgeDistance);state.displacementEdgeDistance=null;}
            if(!state.displacementEdgeDistance)state.displacementEdgeDistance=new Texture2D(res,res,TextureFormat.RFloat,false,true)
                {name="Displacement mask edge distance (metres)",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
            state.displacementEdgeDistance.SetPixelData(distances,0);
            state.displacementEdgeDistance.Apply(false,false);
            var pyramid=LTPaintMath.DisplacementPyramid(first,second,res,1);
            if(ordinaryFirst!=null)
            {
                // RG keeps union coverage for CPU geometry/diagnostics; BA stores
                // ordinary displacement separately so its global factor cannot
                // override a softer layer's independently chosen mud factor.
                var ordinary=LTPaintMath.DisplacementPyramid(ordinaryFirst,second,res,1);
                for(int mip=0;mip<pyramid.Count;mip++)
                    for(int p=0;p<pyramid[mip].Length;p++)
                    {
                        var v=pyramid[mip][p];v.b=ordinary[mip][p].r;v.a=ordinary[mip][p].g;
                        pyramid[mip][p]=v;
                    }
            }
            return pyramid;
        }
        static void BindCoverage(Material material,ChunkState state)
        {
            if(!material)return;
            var r=state.displacementRect;
            material.SetTexture("_LTDisplacementOccupancy",state.displacementOccupancy?state.displacementOccupancy:Texture2D.blackTexture);
            material.SetTexture("_LTDisplacementEdgeDistance",state.displacementEdgeDistance?state.displacementEdgeDistance:Texture2D.whiteTexture);
            material.SetVector("_LTDisplacementRect",new Vector4(r.x,r.y,r.width,r.height));
            material.SetFloat("_LTDisplacementCells",state.displacementOccupancy?state.displacementOccupancy.width:1);
            material.SetMatrix("_LTCoverageWorldToLocal",state.coverageWorldToLocal);
            // UV0 is the generator's canonical, pre-displacement LTWorld XZ mapping.
            // Use the same coordinates as layer sampling, independent of camera origin.
            material.SetFloat("_LTCoverageUseWorldPosition",0);
        }
        public void SetUniformHullDiagnostic(LTWorld world)
        {
            foreach(var pair in chunks)
            {
                var state=pair.Value;
                float factor=world.uniformHullDiagnostic&&!world.forceHullOneDiagnostic&&!state.regularGridDisplacement&&pair.Key&&state.displacementActive&&
                    pair.Key.x==world.uniformHullChunk.x&&pair.Key.z==world.uniformHullChunk.y?
                    Mathf.Clamp(world.uniformHullFactor,1,63):0;
                void Set(Material material)
                {
                    if(material&&material.GetFloat("_LTUniformHullFactor")!=factor)material.SetFloat("_LTUniformHullFactor",factor);
                }
                Set(state.material);Set(state.flatMaterial);Set(state.farMaterial);
            }
        }
        void Bind(Material material,LTWorld world,Rect rect,List<LTSurfaceLayer> layers,ChunkState state)
        {
            layerArrays.Bind(material,layers);
            float maxDisplacement=0,maxDepression=0;
            var tiling=new Vector4[LayerCapacity];var tint=new Vector4[LayerCapacity];var settings=new Vector4[LayerCapacity];var flags=new Vector4[LayerCapacity];
            for(int i=0;i<LayerCapacity;i++)
            {
                var layer=i<layers.Count?layers[i]:null;
                material.SetVector("_LTDisplacement"+i,Vector4.zero);
                if(!layer){tiling[i]=Vector4.one;continue;}
                tiling[i]=new Vector4(layer.tileSizeMetres.x,layer.tileSizeMetres.y,
                    layer.tileOffsetMetres.x/Mathf.Max(.001f,layer.tileSizeMetres.x),layer.tileOffsetMetres.y/Mathf.Max(.001f,layer.tileSizeMetres.y));
                tint[i]=layer.tint.linear;
                settings[i]=new Vector4(layer.normalStrength,layer.metallic,layer.smoothness,layer.maskMap?1:0);
                flags[i]=new Vector4(layer.normalMap?1:0,layer.aoStrength,layer.heightStrength,layer.heightOffset);
                if(i==0&&world.lightweightBackground)
                {
                    settings[i].w=0;
                    flags[i]=new Vector4(layer.normalMap&&layer.normalStrength>0?1:0,0,0,0);
                }
                float amplitude=world.enableLayerDisplacement&&layer.displacement&&layer.maskMap&&!(i==0&&world.lightweightBackground)?Mathf.Clamp(layer.displacementAmplitude,0,2):0;
                float center=Mathf.Clamp01(layer.displacementCenter);
                float smoothing=layer.maskMap?Mathf.Clamp(layer.displacementSmoothingMip,0,Mathf.Min(10,layer.maskMap.mipmapCount-1)):0;
                if(layer.maskMap&&smoothing>0)
                    smoothing=Mathf.Clamp(smoothing+Mathf.Log((float)layerArrays.mask.width/Mathf.Max(layer.maskMap.width,layer.maskMap.height),2),0,layerArrays.mask.mipmapCount-1);
                material.SetVector("_LTDisplacement"+i,new Vector4(amplitude,center,smoothing,0));
                maxDisplacement=Mathf.Max(maxDisplacement,LTPaintMath.DisplacementBound(amplitude,center));
                if(layer.deformation)maxDepression=Mathf.Max(maxDepression,Mathf.Clamp(layer.deformationDepth,0,2));
            }
            maxDisplacement+=maxDepression;
            for(int i=0;i<LayerCapacity;i++)
            {
                material.SetVector("_LTTiling"+i,tiling[i]);material.SetVector("_LTTint"+i,tint[i]);
                material.SetVector("_LTSettings"+i,settings[i]);material.SetVector("_LTFlags"+i,flags[i]);
            }
            material.SetTexture("_LTWeights0",state.weights0);material.SetTexture("_LTWeights1",state.weights1);material.SetTexture("_LTWeights2",state.weights2);
            material.SetVector("_LTRect",new Vector4(rect.x,rect.y,rect.width,rect.height));
            material.SetVector("_LTWorldSize",new Vector4(world.source.size.x,world.source.size.z,0,0));
            material.SetFloat("_LTTriplanar",world.triplanarTexturing?1:0);
            material.SetFloat("_LTRockProjection",0);
            material.SetMatrix("_LTSurfaceWorldToLocal",world.transform.worldToLocalMatrix);
            material.SetFloat("_LTHeightBlend",world.layerHeightBlend);
            material.SetFloat("_LTBaseOnly",layers.Count==1?1:0);
            material.SetFloat("_LTTargetEdgeLength",world.displacementTargetEdgeEnabled?Mathf.Max(.02f,world.displacementTargetEdgeLength):0);
            material.SetFloat("_LTMaskEdgeFade",Mathf.Max(0,world.displacementMaskEdgeFade));
            float transition=Mathf.Max(0,world.tessellationMaskTransition);
            material.SetVector("_LTTessellationMaskPadding",new Vector4(
                transition/Mathf.Max(.00001f,world.transform.TransformVector(Vector3.right).magnitude),
                transition/Mathf.Max(.00001f,world.transform.TransformVector(Vector3.forward).magnitude),0,0));
            state.coverageWorldToLocal=world.transform.worldToLocalMatrix;
            BindCoverage(material,state);
            BindRoadProjection(material,state);
            BindDeformation(material,state);
            float end=DisplacementEnd(world),start=Mathf.Clamp(world.displacementFadeStart,0,end-.01f);
            material.SetVector("_LTDisplacementParams",new Vector4(start,end,Mathf.Max(.1f,world.displacementSeamFade),maxDisplacement));
            if(state.displacementActive)
            {
                material.SetFloat("_TessellationFactor",world.EffectiveTessellationFactor);
                material.SetFloat("_TessellationFactorMinDistance",start);
                material.SetFloat("_TessellationFactorMaxDistance",end);
                material.SetFloat("_TessellationFactorTriangleSize",16);
                material.SetFloat("_TessellationBackFaceCullEpsilon",-1);
                material.SetFloat("_TessellationShapeFactor",0);
            }
            if(state.renderer)
            {
                if(!state.boundsExpanded)state.originalLocalBounds=state.renderer.localBounds;
                // Mesh changes may alter bounds while the renderer has our override.
                state.originalLocalBounds=state.sourceBounds;
                var expanded=state.originalLocalBounds;
                if(state.displacementActive)
                {
                    var scale=state.renderer.transform.lossyScale;
                    expanded.Expand(new Vector3(2*maxDisplacement/Mathf.Max(.0001f,Mathf.Abs(scale.x)),2*maxDisplacement/Mathf.Max(.0001f,Mathf.Abs(scale.y)),2*maxDisplacement/Mathf.Max(.0001f,Mathf.Abs(scale.z))));
                }
                state.renderer.localBounds=expanded;state.boundsExpanded=state.displacementActive;
            }
        }
        static float DisplacementEnd(LTWorld world)=>LTPaintMath.DisplacementDistances(world.displacementFadeStart,world.displacementFadeEnd,world.enableGlobalLayerMaps,world.globalLayerStart).y;
        public void Tick(LTWorld world)=>Tick(world,false);
        public void Tick(LTWorld world,bool force)
        {
            TickDeformation(world);
            double now=Time.realtimeSinceStartupAsDouble;if(!force&&now<nextUpdate)return;nextUpdate=now+.15;
            using var cpuTick=world.paintCpu.BeginTick(force);
            if(!world.enableLayerPainting||!world.baseLayer||!world.source||!world.generatedRoot)
            {
                Dispose();world.paintStatus=world.enableLayerPainting?"Назначьте базовый слой и создайте чанки.":"Покраска выключена.";return;
            }
            var shader=Resources.Load<Shader>("LTEightLayers");
            if(!shader||!shader.isSupported){Dispose();world.paintStatus="Шейдер Eight Layers HDRP недоступен или не поддерживается.";return;}
            PrepareGlobals(world);
            var tessShader=SystemInfo.supportsTessellationShaders?Resources.Load<Shader>("LTEightLayersTessellation"):null;
            var all=world.CollectPaintStamps();var active=new List<LTPaintStamp>();var bounds=new List<Rect>();
            // A shared, deterministic palette includes disabled stamps too: toggling
            // visibility or painting weights does not repack textures or shuffle slices.
            var palette=new List<LTSurfaceLayer>{world.baseLayer};
            foreach(var stamp in all)stamp.AppendLayers(palette,true);
            int arrayRevision=layerArrays.Revision;
            bool arraysReady=layerArrays.Ensure(world,palette);arrayWorld=world;arraysAvailable=arraysReady;
            world.arrayPackStatus=layerArrays.Status;
            world.arrayColorPreview=layerArrays.color;world.arrayNormalPreview=layerArrays.normal;world.arrayMaskPreview=layerArrays.mask;
            if(!arraysReady){RestoreMaterialsForSave();world.paintStatus=layerArrays.Status;return;}
            if(arrayRevision!=layerArrays.Revision)
            {
                foreach(var state in chunks.Values){state.ready=false;state.globalBindingReady=false;state.farMaterialDirty=true;}
                foreach(var rock in rocks.Values)rock.paint.ready=false;
            }
            var roadErrors=new List<string>();var asphaltInputs=new List<LTRoadMath.Snapshot>();
            foreach(var road in world.GetComponentsInChildren<LTRoad>())
            {
                if(!road.isActiveAndEnabled||road.World!=world||road.mode!=LTRoadMode.Asphalt)continue;
                try {asphaltInputs.Add(road.Capture(world));}
                catch(ArgumentException error){roadErrors.Add(road.name+": "+error.Message);}
            }
            foreach(var node in world.GetComponentsInChildren<LTRoadJunction>())
                if(node.isActiveAndEnabled&&node.World==world&&node.surface==LTRoadMode.Asphalt)
                    try{asphaltInputs.Add(node.Suppression());}catch(ArgumentException error){roadErrors.Add(node.name+": "+error.Message);}
            foreach(var stamp in all)if(stamp.ActiveForPaint&&stamp.HasPaintLayers&&(stamp.Road||stamp.Junction||stamp.strength>0)&&Projection(world,stamp,out _))
            {
                try {var box=StampBounds(world,stamp);active.Add(stamp);bounds.Add(box);}
                catch(ArgumentException) { /* Invalid road is reported by its inspector. */ }
            }
            var coverageInputs=new Dictionary<LTPaintStamp,int[]>(active.Count);
            var roadInfluences=new Dictionary<LTPaintStamp,LTRoadMath.Snapshot>();
            foreach(var stamp in active)if(stamp.Road)roadInfluences.Add(stamp,stamp.Road.Capture(world));
            var layerInputs=new Dictionary<LTSurfaceLayer,LayerChangeInput>(palette.Count);
            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.ChangeChecks))
            {
                foreach(var stamp in active)coverageInputs.Add(stamp,CoverageInputs(world,stamp));
                foreach(var layer in palette)layerInputs.Add(layer,new LayerChangeInput(layer));
            }
            var current=world.generatedRoot.GetComponentsInChildren<LTChunk>();var live=new HashSet<LTChunk>(current);
            bool filtered=active.Exists(s=>s.HasTerrainFilters);
            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.TerrainCache))
            {
                if(filtered){terrain.Update(world,current);world.paintTerrainSignature=terrain.signature;}
                else {terrain.Clear();world.paintTerrainSignature="";}
            }
            foreach(var key in new List<LTChunk>(chunks.Keys))if(!key||!live.Contains(key)){Release(chunks[key]);chunks.Remove(key);}
            // Drop CPU mask copies no longer referenced by an active stamp.
            var usedMasks=new HashSet<Texture2D>();foreach(var stamp in active)
            {if(stamp.mask)usedMasks.Add(stamp.mask);if(stamp.EffectiveLayer&&stamp.EffectiveLayer.maskMap)usedMasks.Add(stamp.EffectiveLayer.maskMap);
                if(stamp.SecondaryLayer&&stamp.SecondaryLayer.maskMap)usedMasks.Add(stamp.SecondaryLayer.maskMap);}
            if(world.baseLayer.maskMap)usedMasks.Add(world.baseLayer.maskMap);
            foreach(var key in new List<Texture2D>(masks.Keys))if(!key||!usedMasks.Contains(key)){DestroyOwned(masks[key].copy);masks.Remove(key);}
            int painted=0,maxLayers=0,displacedChunks=0,unsupportedDisplacement=0;var overflow=new List<string>();
            var displacementMaskReadbacks=new MaskReadbackCache();
            foreach(var chunk in current)
            {
                if(!chunk.mesh)continue;
                var renderer=chunk.GetComponent<MeshRenderer>();if(!renderer)continue;
                float width=world.source.size.x/world.chunksX,depth=world.source.size.z/world.chunksZ;
                var rect=new Rect(chunk.x*width,chunk.z*depth,width,depth);
                var local=new List<LTPaintStamp>();var layers=new List<LTSurfaceLayer>{world.baseLayer};
                // Keep the bilinear/road UV guard, but reject empty space inside a
                // bent road's large AABB. Removed old road slots still trigger Bake.
                var paintGuard=Rect.MinMaxRect(rect.xMin-2*width/(Resolution-1),rect.yMin-2*depth/(Resolution-1),
                    rect.xMax+2*width/(Resolution-1),rect.yMax+2*depth/(Resolution-1));
                for(int i=0;i<active.Count;i++)if(Touches(rect,bounds[i])&&
                    (!roadInfluences.TryGetValue(active[i],out var influence)||influence.Intersects(paintGuard)))
                {local.Add(active[i]);active[i].AppendLayers(layers);}
                maxLayers=Mathf.Max(maxLayers,layers.Count);
                if(layers.Count>LayerCapacity)
                {
                    overflow.Add($"({chunk.x},{chunk.z}): {layers.Count}");
                    if(chunks.TryGetValue(chunk,out var excess)){Release(excess);chunks.Remove(chunk);}continue;
                }
                if(!chunks.TryGetValue(chunk,out var state))
                {
                    state=new ChunkState{renderer=renderer,original=renderer.sharedMaterial};chunks.Add(chunk,state);
                    state.material=new Material(shader){name=$"Terrain layers ({chunk.x},{chunk.z})",hideFlags=HideFlags.HideAndDontSave};
                }
                bool wantsDisplacement=false;
                for(int i=0;i<layers.Count;i++)if(!(i==0&&world.lightweightBackground)&&layers[i].displacement&&layers[i].maskMap&&layers[i].displacementAmplitude>0)wantsDisplacement=true;
                bool wantsDeformation=layers.Exists(l=>l.deformation&&l.deformationDepth>0);
                bool wantsGeometry=(wantsDisplacement&&world.enableLayerDisplacement)||wantsDeformation;
                if(!wantsDeformation)ReleaseDeformation(state);
                bool gridPreview=world.IsRegularMaskGridChunk(chunk.x,chunk.z);
                bool needCoverage=wantsGeometry&&(gridPreview||(tessShader&&tessShader.isSupported));
                bool regularDisplacement=gridPreview&&world.regularMaskGridDisplacement;
                bool displace=needCoverage&&(!gridPreview||regularDisplacement)&&tessShader&&tessShader.isSupported;
                if(displace)displacedChunks++;
                else if(wantsGeometry&&(!gridPreview||regularDisplacement))unsupportedDisplacement++;
                var desiredShader=displace?tessShader:shader;
                bool shaderChanged=state.material.shader!=desiredShader;
                if(shaderChanged){state.material.shader=desiredShader;state.ready=false;state.globalBindingReady=false;}
                state.displacementActive=displace;
                state.coverageActive=needCoverage;
                state.regularGridDisplacement=regularDisplacement&&displace;
                state.sourceBounds=chunk.mesh.bounds;
                int coverage,surface,densityInput,weightInput;
                using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.ChangeChecks))
                {
                coverage=Mix(rect.GetHashCode(),world.transform.localToWorldMatrix.GetHashCode());coverage=Mix(coverage,Id(world.baseLayer));
                weightInput=coverage;
                // Palette/order are part of weights even if numeric coverage is
                // unchanged. Road coordinates are checked exactly per region below.
                foreach(var layer in layers)weightInput=Mix(weightInput,Id(layer));
                // Asphalt suppresses visual displacement even without a paint layer.
                foreach(var road in asphaltInputs)if(road.Intersects(rect))coverage=Mix(coverage,road.geometryHash);
                foreach(var stamp in local)
                {
                    foreach(int input in coverageInputs[stamp])coverage=Mix(coverage,input);
                    if(roadInfluences.ContainsKey(stamp))
                    {
                        weightInput=Mix(Mix(Mix(weightInput,Id(stamp)),Id(stamp.EffectiveLayer)),Id(stamp.SecondaryLayer));
                    }
                    else foreach(int input in coverageInputs[stamp])weightInput=Mix(weightInput,input);
                }
                densityInput=Mix(coverage,world.displacementGeometry.RegionSignature(rect,TerrainFilterRadius(local)));
                if(local.Exists(s=>s.HasTerrainFilters))
                {
                    int filterSignature=TerrainFilterSignature(rect,local);
                    coverage=Mix(coverage,filterSignature);weightInput=Mix(weightInput,filterSignature);
                }
                surface=Mix(world.layerHeightBlend.GetHashCode(),world.lightweightBackground?1:0);
                surface=Mix(surface,world.enableLayerDisplacement?1:0);
                surface=Mix(surface,world.triplanarTexturing?1:0);
                surface=Mix(surface,gridPreview?1:0);
                surface=Mix(surface,regularDisplacement?1:0);
                surface=Mix(surface,world.EffectiveTessellationFactor.GetHashCode());
                surface=Mix(surface,world.displacementTargetEdgeEnabled?1:0);
                surface=Mix(surface,world.displacementTargetEdgeLength.GetHashCode());
                surface=Mix(surface,world.displacementMaskEdgeFade.GetHashCode());
                surface=Mix(surface,world.tessellationMaskTransition.GetHashCode());
                surface=Mix(surface,world.displacementFadeStart.GetHashCode());surface=Mix(surface,DisplacementEnd(world).GetHashCode());
                surface=Mix(surface,world.displacementSeamFade.GetHashCode());
                surface=Mix(surface,state.sourceBounds.GetHashCode());
                foreach(var layer in layers)surface=layerInputs[layer].AppendSurface(surface);
                }
                if(!state.ready||state.coverageHash!=coverage)
                {
                    var weightRoads=new List<LTRoadMath.Snapshot>();
                    bool bakeWeights;
                    using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.ChangeChecks))
                    {
                        foreach(var stamp in local)if(roadInfluences.TryGetValue(stamp,out var roadInput))weightRoads.Add(roadInput);
                        bakeWeights=!state.ready||state.weightHash!=weightInput||!SamePaintRoads(state.weightRoads,weightRoads,rect);
                    }
                    try
                    {
                        using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightBake))Bake(world,rect,local,layers,state,asphaltInputs,bakeWeights);
                    }
                    catch(InvalidOperationException error)
                    {
                        roadErrors.Add($"({chunk.x},{chunk.z}): "+error.Message);
                        Release(state);chunks.Remove(chunk);continue;
                    }
                    // Commit dependencies only after a successful bake/reuse. Old
                    // snapshots let deletion, movement and Undo compare both states.
                    state.weightHash=weightInput;state.weightRoads=weightRoads.ToArray();
                }
                int occupancyLayers=0;
                for(int i=0;i<layers.Count;i++)
                    if((layers[i].deformation&&layers[i].deformationDepth>0)||
                        (world.enableLayerDisplacement&&!(i==0&&world.lightweightBackground)&&layers[i].displacement&&layers[i].maskMap&&layers[i].displacementAmplitude>0))
                        occupancyLayers|=1<<i;
                densityInput=Mix(densityInput,occupancyLayers);
                densityInput=Mix(densityInput,world.transform.TransformVector(Vector3.right).magnitude.GetHashCode());
                densityInput=Mix(densityInput,world.transform.TransformVector(Vector3.forward).magnitude.GetHashCode());
                densityInput=Mix(densityInput,world.layerHeightBlend.GetHashCode());
                foreach(var layer in layers)densityInput=layerInputs[layer].AppendDensity(densityInput);
                bool coverageRebuilt=false;
                if(needCoverage&&(!state.ready||state.coverageHash!=coverage||state.occupancyLayers!=occupancyLayers||!state.displacementOccupancy||!state.displacementEdgeDistance||state.densityInputHash!=densityInput))
                {
                    coverageRebuilt=true;
                    List<Color32[]> pyramid;
                    using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.DisplacementBake))
                        pyramid=BakeDisplacementCoverage(world,rect,local,layers,state,occupancyLayers,displacementMaskReadbacks);
                    int cells=Mathf.RoundToInt(Mathf.Sqrt(pyramid[0].Length));
                    // A density rebuild can slightly shift slope/curve filters. Within one
                    // authoring revision only grow coverage; never oscillate refine/coarsen.
                    // Moving/editing stamps or source geometry resets the conservative union.
                    var grid=state.densityCoverage!=null&&state.densityInputHash==densityInput&&state.densityCoverage.grid.size==cells?
                        state.densityCoverage.grid.Include(pyramid[0]):new LTPaintMath.CoverageGrid(pyramid[0],cells);
                    state.densityCoverage=new DensityCoverage{id=chunk.GetInstanceID(),x=chunk.x,z=chunk.z,rect=state.displacementRect,grid=grid};
                    state.densityInputHash=densityInput;
                    if(state.displacementOccupancy&&state.displacementOccupancy.width!=cells){DestroyOwned(state.displacementOccupancy);state.displacementOccupancy=null;}
                    if(!state.displacementOccupancy)state.displacementOccupancy=new Texture2D(cells,cells,TextureFormat.RGBA32,true,true)
                        {name="Displacement conservative coverage",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
                    for(int mip=0;mip<pyramid.Count;mip++)state.displacementOccupancy.SetPixels32(pyramid[mip],mip);
                    state.displacementOccupancy.Apply(false,false);state.occupancyLayers=occupancyLayers;
                    DestroyOwned(state.coveragePreview);state.coveragePreview=null;
                }
                if(!state.ready||state.coverageHash!=coverage||state.surfaceHash!=surface||coverageRebuilt)
                {
                    using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.LayerBind))Bind(state.material,world,rect,layers,state);
                    world.paintCpu.LayerBound();state.globallyBaked=false;state.farMaterialDirty=true;
                }
                if(!state.ready||state.coverageHash!=coverage||state.surfaceHash!=surface)state.detailLayers=layers.ToArray();
                state.coverageHash=coverage;state.surfaceHash=surface;state.ready=true;
                if(renderer.sharedMaterial!=state.material&&renderer.sharedMaterial!=state.farMaterial&&renderer.sharedMaterial!=state.flatMaterial)
                {state.original=renderer.sharedMaterial;renderer.sharedMaterial=state.material;}
                painted++;
            }
            world.paintStatus=$"Покрашено чанков: {painted}; максимум слоёв: {maxLayers}/{LayerCapacity} (включая базовый). Маски: {Resolution}×{Resolution}.";
            if(world.RegularMaskGridActive)world.paintStatus+=$"\nCPU-сетка по маске: чанк {world.regularMaskGridChunk}, запрошенный шаг {world.regularMaskGridStep:0.###} м. "+
                (world.regularMaskGridDisplacement?"Запрошен displacement существующих вершин, Hull=1 (без дополнительного дробления).":"Displacement выбранного чанка отключён.");
            if(displacedChunks>0)world.paintStatus+=$"\nDisplacement: {displacedChunks} чанков; затухание до {DisplacementEnd(world):F1} м. Коллайдеры исходные.";
            if(unsupportedDisplacement>0)world.paintStatus+="\nDisplacement недоступен: проверьте поддержку тесселяции GPU и компиляцию LTEightLayersTessellation.";
            if(overflow.Count>0)world.paintStatus+="\nЛимит превышен, исходный материал сохранён: "+string.Join(", ",overflow);
            if(roadErrors.Count>0)world.paintStatus+="\nДороги: "+string.Join("\n",roadErrors);
            AppendDeformationStatus(world);
            PrepareDeformationRegions(world,active);
            FinishGlobals(world,painted==world.chunksX*world.chunksZ&&overflow.Count==0&&roadErrors.Count==0);
            PrepareDeformationTerrain(world,current);
            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.RockMaterials))
                TickRockMaterials(world,shader,active,bounds,asphaltInputs,layerInputs);
            SetHullOneDiagnostic(world.forceHullOneDiagnostic);
            SetUniformHullDiagnostic(world);
            SetCoverageDebug(world.showDisplacementCoverage,world.displacementCoverageControlColor,world.displacementCoverageFixedProjection,world.displacementCoverageCoordinateProbe,world.displacementHullReasons,world.displacementSurfaceProbe);
        }
    }
}
