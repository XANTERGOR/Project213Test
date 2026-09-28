using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public enum LTRoadMeshSource { ProceduralRibbon, Module }
    public enum LTRoadLODMode { Disabled, Automatic, Authored }
    public enum LTRoadModuleAxis { Z, X }
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(LTPaintStamp))]
    [AddComponentMenu("Local Terrain/Road")]
    public sealed class LTRoad : MonoBehaviour
    {
        public List<LTRoadPoint> points = new List<LTRoadPoint>
        {
            new LTRoadPoint(new Vector3(0, 0, -10)),
            new LTRoadPoint(Vector3.zero),
            new LTRoadPoint(new Vector3(0, 0, 10))
        };
        public LTRoadMode mode;
        public LTRoadPattern pattern;
        public LTRoadProjection projection = LTRoadProjection.World;
        public LTRoadJunction startJunction,endJunction;
        public Vector2 textureOffset;
        [Min(0),Tooltip("Spline only: metres per repeat across the road. 0 preserves one repeat over the road width.")]
        public float textureAcrossMetres;
        [Min(.01f)] public float width = 6;
        [Min(0)] public float shoulderWidth = 1;
        [Min(0)] public float blendWidth = 3;
        [Range(0, 1)] public float flatten = 1;
        [Min(.01f)] public float rutWidth = .55f;
        [Min(0)] public float rutSeparation = 1.8f;
        [Min(0)] public float rutDepth = .08f;
        [Range(0, 1)] public float edgeNoise = .2f;
        [Min(.01f)] public float noiseSize = 3;
        public int seed = 12345;
        public LTRoadVariation variation=LTRoadVariation.Default;
        public LTSurfaceLayer groundLayer;
        public LTRoadWheelTracks wheelTracks=LTRoadWheelTracks.Default;
        [HideInInspector] public bool wheelTilingInitialized;
        public LTSurfaceLayer wheelLayer;
        public LTSurfaceLayer ActiveWheelLayer=>mode==LTRoadMode.Offroad&&wheelTracks.enabled&&wheelLayer!=groundLayer?wheelLayer:null;
        public Material asphaltMaterial;
        public LTRoadMeshSource asphaltSource;
        public Mesh asphaltModule;
        public GameObject asphaltModulePrefab;
        public LTRoadModuleAxis moduleAxis;
        public Material[] moduleMaterials=Array.Empty<Material>();
        [Min(0), Tooltip("0 uses the module's original length; the last repeat is fitted to the spline.")]
        public float moduleLength;
        public bool moduleFitWidth=true;
        public float moduleBaseY;
        public LTRoadLODMode asphaltLODMode=LTRoadLODMode.Automatic;
        public Mesh[] moduleLODMeshes=Array.Empty<Mesh>();
        public LTLODSettings[] asphaltLODs={new LTLODSettings(1,.05f,50),new LTLODSettings(2,.1f,120),new LTLODSettings(3,.2f,250)};
        [HideInInspector] public string meshLODStatus;
        [Min(.01f)] public float sampleSpacing = 1;
        [Min(.01f)] public float terrainCellSize = .5f;
        [Min(.01f)] public float meshChunkLength = 32;
        [Min(.01f)] public float textureRepeatMetres = 4;
        [Min(0)] public float surfaceOffset = .06f;
        public bool clearVegetation = true;
        public bool vegetationOnlyWheelTracks;
        public bool clearStones;
        [Range(0, 1), Tooltip("Vegetation removal strength within the paint mask. Does not widen tracks.")]
        public float vegetationFade = 1;
        [HideInInspector] public string bakeId;
        [HideInInspector] public Transform generatedRoot;
        [HideInInspector] public string bakeSignature;
        [HideInInspector] public int bakedGeometryHash;

        public LTWorld World => GetComponentInParent<LTWorld>();

        [NonSerialized] LTRoadMath.Snapshot cached;
        [NonSerialized] LTRoadPoint[] cachedPoints;
        [NonSerialized] LTRoadMath.Settings cachedSettings;
        [NonSerialized] Matrix4x4 cachedRoadMatrix, cachedWorldMatrix;

        /// <summary>Immutable terrain-local snapshot. Throws ArgumentException for unsupported transforms or invalid paths.</summary>
        public LTRoadMath.Snapshot Capture(LTWorld world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world), "LTRoad.Capture requires its terrain world.");
            if(points==null||points.Count<2)throw new ArgumentException("Дороге нужны минимум две точки.");
            if(startJunction&&startJunction.isActiveAndEnabled&&endJunction&&endJunction.isActiveAndEnabled&&points.Count<3)
                throw new ArgumentException("Между двумя перекрёстками нужна хотя бы одна промежуточная точка для направления въездов.");
            Matrix4x4 roadMatrix = transform.localToWorldMatrix, worldMatrix = world.transform.localToWorldMatrix;
            LTRoadMath.ValidateTransform(worldMatrix, true, "Terrain world transform");
            LTRoadMath.ValidateTransform(roadMatrix, false, "Road transform");
            var settings = new LTRoadMath.Settings
            {
                mode = mode, pattern = pattern, width = width, shoulderWidth = shoulderWidth,
                projection = projection, textureOffset = textureOffset,textureAcrossMetres=textureAcrossMetres,
                blendWidth = blendWidth, flatten = flatten, rutWidth = rutWidth,
                rutSeparation = rutSeparation, rutDepth = rutDepth, edgeNoise = edgeNoise,
                noiseSize = noiseSize, seed = seed, sampleSpacing = sampleSpacing,
                variation=variation,
                wheelTracks=wheelTracks,wheelLayerId=ActiveWheelLayer?ActiveWheelLayer.GetInstanceID():0,
                terrainCellSize = terrainCellSize, meshChunkLength = meshChunkLength,
                textureRepeatMetres = textureRepeatMetres, surfaceOffset = surfaceOffset,
                clearVegetation = clearVegetation, clearStones = clearStones, vegetationFade = vegetationFade,
                vegetationOnlyWheelTracks=vegetationOnlyWheelTracks,
                straightStart=startJunction&&startJunction.isActiveAndEnabled,straightEnd=endJunction&&endJunction.isActiveAndEnabled,
                junctionStartLength=startJunction?startJunction.neckLength:0,junctionEndLength=endJunction?endJunction.neckLength:0,
                groundLayerId = groundLayer != null ? groundLayer.GetInstanceID() : 0,
                sourceTransformHash = unchecked(LTRoadMath.TransformHash(worldMatrix) * 397 ^ LTRoadMath.TransformHash(roadMatrix) ^
                    (startJunction?startJunction.EndpointHash:0)*17^(endJunction?endJunction.EndpointHash:0)*31)
            };
            if (cached != null && cachedSettings.Equals(settings) && cachedRoadMatrix.Equals(roadMatrix) &&
                cachedWorldMatrix.Equals(worldMatrix) && SamePoints()) return cached;
            // Build first: an invalid edit never returns the previous, stale snapshot as if it were valid.
            var effective=new List<LTRoadPoint>(points);
            if(startJunction&&startJunction.isActiveAndEnabled)
            {startJunction.Endpoint(this,true,out var port,out var neck);effective[0]=port;effective.Insert(1,neck);}
            if(endJunction&&endJunction.isActiveAndEnabled)
            {endJunction.Endpoint(this,false,out var port,out var neck);effective[effective.Count-1]=port;effective.Insert(effective.Count-1,neck);}
            var snapshot = LTRoadMath.Build(effective, world.transform.worldToLocalMatrix * roadMatrix, settings);
            cachedPoints = points.ToArray();
            cachedSettings = settings; cachedRoadMatrix = roadMatrix; cachedWorldMatrix = worldMatrix;
            cached = snapshot;
            return snapshot;
        }

        bool SamePoints()
        {
            if (points == null || cachedPoints == null || points.Count != cachedPoints.Length) return false;
            for (int i = 0; i < points.Count; i++)
                if (!points[i].position.Equals(cachedPoints[i].position) || !points[i].bank.Equals(cachedPoints[i].bank) ||
                    points[i].VariationStrength!=cachedPoints[i].VariationStrength) return false;
            return true;
        }

        void OnEnable() { SetOutputActive(mode == LTRoadMode.Asphalt); }
        void OnDisable() { SetOutputActive(false); }

        void SetOutputActive(bool active)
        {
            if (generatedRoot == null) return;
            var marker = generatedRoot.GetComponent<LTRoadGenerated>();
            if (marker != null && marker.owner == this) generatedRoot.gameObject.SetActive(active);
        }

        void OnValidate()
        {
            width = Clamp(width, .01f, 10000, 6);
            shoulderWidth = Clamp(shoulderWidth, 0, 10000, 1);
            blendWidth = Clamp(blendWidth, 0, 10000, 3);
            flatten = Clamp(flatten, 0, 1, 1);
            rutWidth = Clamp(rutWidth, .01f, 10000, .55f);
            rutSeparation = Clamp(rutSeparation, 0, 10000, 1.8f);
            rutDepth = Clamp(rutDepth, 0, 1000, .08f);
            edgeNoise = Clamp(edgeNoise, 0, 1, .2f);
            noiseSize = Clamp(noiseSize, .01f, 10000, 3);
            variation.strength=Clamp(variation.strength,0,1,1);
            variation.widthAmount=Clamp(variation.widthAmount,0,.35f,.15f);variation.widthLength=Clamp(variation.widthLength,.1f,100000,12);
            variation.patchStrength=Clamp(variation.patchStrength,0,1,.5f);variation.patchSize=Clamp(variation.patchSize,.1f,100000,8);
            variation.rutVariation=Clamp(variation.rutVariation,0,1,.65f);variation.rutLength=Clamp(variation.rutLength,.1f,100000,10);
            wheelTracks.width=Clamp(wheelTracks.width,.01f,10000,.65f);wheelTracks.separation=Clamp(wheelTracks.separation,.01f,10000,1.8f);
            wheelTracks.strength=Clamp(wheelTracks.strength,0,1,.85f);wheelTracks.softness=Clamp(wheelTracks.softness,.01f,1,.4f);
            wheelTracks.tileSizeMetres.x=Clamp(wheelTracks.tileSizeMetres.x,.01f,100000,6);
            wheelTracks.tileSizeMetres.y=Clamp(wheelTracks.tileSizeMetres.y,.01f,100000,4);
            wheelTracks.textureOffset.x=Clamp(wheelTracks.textureOffset.x,-1e6f,1e6f,0);
            wheelTracks.textureOffset.y=Clamp(wheelTracks.textureOffset.y,-1e6f,1e6f,0);
            sampleSpacing = Clamp(sampleSpacing, .01f, 10000, 1);
            terrainCellSize = Clamp(terrainCellSize, .01f, 10000, .5f);
            meshChunkLength = Clamp(meshChunkLength, .01f, 100000, 32);
            moduleLength=Clamp(moduleLength,0,100000,0);moduleBaseY=Clamp(moduleBaseY,-10000,10000,0);
            textureRepeatMetres = Clamp(textureRepeatMetres, .01f, 100000, 4);
            textureAcrossMetres=Clamp(textureAcrossMetres,0,100000,0);
            surfaceOffset = Clamp(surfaceOffset, 0, 1000, .06f);
            vegetationFade = Clamp(vegetationFade, 0, 1, 1);
            textureOffset.x = Clamp(textureOffset.x, -1e6f, 1e6f, 0);
            textureOffset.y = Clamp(textureOffset.y, -1e6f, 1e6f, 0);
            if (points != null) for (int i = 0; i < points.Count; i++)
            {
                var point = points[i]; point.bank = Clamp(point.bank, -80, 80, 0);
                point.variationStrength=Clamp(point.variationStrength,0,1,1);points[i] = point;
            }
            cached = null;
        }

        static float Clamp(float value, float min, float max, float fallback)
            => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Max(min, Math.Min(max, value));
    }
}
