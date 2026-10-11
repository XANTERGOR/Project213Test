using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public enum LTRoadMeshSource { ProceduralRibbon, Module }
    public enum LTRoadLODMode { Disabled, Automatic, Authored }
    public enum LTRoadModuleAxis { Z, X }
    public enum LTRoadGroundSource { SingleLayer, Terrain }
    [Serializable]
    public sealed class LTRoadModuleVariant
    {
        [InspectorName("Префаб / FBX")] public GameObject prefab;
        [InspectorName("Меш (если нет префаба)")] public Mesh mesh;
        [InspectorName("Материалы меша")] public Material[] materials=Array.Empty<Material>();
        [InspectorName("Готовые LOD меша")] public Mesh[] lodMeshes=Array.Empty<Mesh>();
        [Min(0), InspectorName("Вес выбора")] public float weight=1;
    }
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
        public LTRoadJunctionSocket startJunctionSocket,endJunctionSocket;
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
        public LTRoadAsphaltDamage asphaltDamage=LTRoadAsphaltDamage.Default;
        // Mode 3 only; zero preserves existing roads until explicitly enabled.
        [Min(0)] public float pitBevelWidth;
        [Min(0)] public float pitBevelDepth;
        [Range(0,1)] public float pitShapeVariation;
        public bool connectedDamage;
        [Range(0,1)] public float connectedFloorAsphalt;
        [Range(0,1)] public float repairPatchStrength;
        [Tooltip("Optional linear grayscale texture: white = cracks. Empty uses the generated test pattern.")]
        public Texture2D asphaltCrackMask;
        [Min(.1f)] public float asphaltCrackRepeat=3;
        public LTSurfaceLayer groundLayer;
        public LTRoadProjection GroundProjection=>LTRoadMath.ResolveGroundProjection(mode,projection,groundBlend);
        public LTRoadProjection WheelProjection=>wheelTracks.ResolveProjection(projection);
        public LTRoadWheelTracks wheelTracks=LTRoadWheelTracks.Default;
        [HideInInspector] public bool wheelTilingInitialized;
        public LTSurfaceLayer wheelLayer;
        public LTSurfaceLayer ActiveWheelLayer=>mode==LTRoadMode.Offroad&&wheelTracks.enabled&&wheelLayer!=groundLayer?wheelLayer:null;
        public Material asphaltMaterial;
        // Opt-in; old roads and authored materials are unchanged until enabled.
        public bool groundBlend;
        public LTRoadGroundSource groundBlendSource;
        public bool AutomaticGroundBlend=>mode==LTRoadMode.Asphalt&&groundBlend&&groundBlendSource==LTRoadGroundSource.Terrain;
        public LTSurfaceLayer PaintGroundLayer=>AutomaticGroundBlend?null:groundLayer;
        [Min(0)] public float groundBlendWidth = .6f;
        [Min(0)] public float groundBlendHoleWidth = .2f;
        [Range(0,1)] public float groundBlendStrength = 1;
        [Range(0,1)] public float groundBlendNoise = .45f;
        [Min(.05f)] public float groundBlendNoiseSize = .7f;
        public int groundBlendSeed = 12345;
        [Min(.01f)] public float groundBlendMaskCell = .05f;
        public LTRoadMeshSource asphaltSource;
        public Mesh asphaltModule;
        public GameObject asphaltModulePrefab;
        public LTRoadModuleVariant[] moduleVariants=Array.Empty<LTRoadModuleVariant>();
        public int moduleRandomSeed=12345;
        public bool moduleAvoidRepeat=true;
        public LTRoadModuleAxis moduleAxis;
        public Material[] moduleMaterials=Array.Empty<Material>();
        [Min(0), Tooltip("0 uses the module's original length; the last repeat is fitted to the spline.")]
        public float moduleLength;
        public bool moduleFitWidth=true;
        public float moduleBaseY;
        public bool moduleFuseEnds;
        public bool simplifyTerrainUnderAsphalt=true;
        [Min(0)] public float terrainLODEdgeError=.03f;
        [Range(.00001f,.01f)] public float moduleFuseTolerance=.0001f;
        [Range(.00001f,.01f)] public float moduleFusePlaneTolerance=.0001f;
        // Opt-in: preserve existing roads and their strict end fitting until selected.
        public bool moduleAutomaticJoints;
        [Min(.01f)] public float moduleJointWidth=2;
        [Min(.00001f)] public float moduleJointMaxChange=.5f;
        [Min(.00001f)] public float moduleJointPrecision=.01f;
        public bool moduleFitEnds;
        [Min(.001f)] public float moduleEndBlendLength=.5f;
        [Min(.00001f)] public float moduleEndMaxMismatch=.05f;
        public bool moduleRestoreLODEnds=true;
        [Min(.01f)] public float moduleLODEndWidth=.5f;
        public LTRoadLODMode asphaltLODMode=LTRoadLODMode.Automatic;
        public Mesh[] moduleLODMeshes=Array.Empty<Mesh>();
        public LTLODSettings[] asphaltLODs={new LTLODSettings(1,.05f,50),new LTLODSettings(2,.1f,120),new LTLODSettings(3,.2f,250)};
        [HideInInspector] public string meshLODStatus;
        [Min(.01f)] public float sampleSpacing = 1;
        [Min(.01f)] public float terrainCellSize = .5f;
        [Min(.05f)] public float insetCellSize = 1;
        [Range(0, 2)] public float insetLodReduction = 1;
        [Min(.01f)] public float meshChunkLength = 32;
        [Min(.01f)] public float textureRepeatMetres = 4;
        [Min(0)] public float surfaceOffset = .06f;
        public bool clearVegetation = true;
        public bool asphaltClearMesh=true;
        [Min(0)] public float asphaltGrassPadding=.35f;
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

        // Route per paint layer, never apply wheel UVs to a shared ground/biome slot.
        public LTRoadMath.Snapshot CapturePaintProjection(LTWorld world,bool wheels)
        {
            if(wheels&&!ActiveWheelLayer)return null;
            if((wheels?WheelProjection:GroundProjection)!=LTRoadProjection.Spline)return null;
            var snapshot=Capture(world);
            return wheels?snapshot.WheelLayerProjection():snapshot;
        }

        /// <summary>Immutable terrain-local snapshot. Throws ArgumentException for unsupported transforms or invalid paths.</summary>
        public LTRoadMath.Snapshot Capture(LTWorld world)
            =>CaptureCore(world,startJunction,startJunctionSocket,endJunction,endJunctionSocket,true);

        // Detached Scene handle preview. No serialized edits or live cache replacement:
        // terrain, paint, detail and bake consumers continue seeing committed dimensions.
        public LTRoadMath.Snapshot PreviewDimensions(float previewWidth,float previewShoulder,float previewBlend)
            =>CaptureCore(World,startJunction,startJunctionSocket,endJunction,endJunctionSocket,false,
                previewWidth,previewShoulder,previewBlend);

        // Read-only proposed connection: no temporary reference edits, dirty flags or Undo.
        public LTRoadMath.Snapshot PreviewConnection(LTRoadJunction node,LTRoadJunctionSocket socket,bool start)
            =>CaptureCore(World,start?node:startJunction,start?socket:startJunctionSocket,
                start?endJunction:node,start?endJunctionSocket:socket,false);

        LTRoadMath.Snapshot CaptureCore(LTWorld world,LTRoadJunction startJunction,LTRoadJunctionSocket startJunctionSocket,
            LTRoadJunction endJunction,LTRoadJunctionSocket endJunctionSocket,bool useCache,
            float? previewWidth=null,float? previewShoulder=null,float? previewBlend=null)
        {
            if (world == null) throw new ArgumentNullException(nameof(world), "LTRoad.Capture requires its terrain world.");
            if(LTRoadMath.IsTerrainAsphalt(mode)&&(startJunction||endJunction))
                throw new ArgumentException("Terrain Asphalt (experimental): пока используйте дорогу без узлов перекрёстков.");
            if(points==null||points.Count<2)throw new ArgumentException("Дороге нужны минимум две точки.");
            if(startJunction&&startJunction.isActiveAndEnabled&&endJunction&&endJunction.isActiveAndEnabled&&points.Count<3)
                throw new ArgumentException("Между двумя перекрёстками нужна хотя бы одна промежуточная точка для направления въездов.");
            Matrix4x4 roadMatrix = transform.localToWorldMatrix, worldMatrix = world.transform.localToWorldMatrix;
            LTRoadMath.ValidateTransform(worldMatrix, true, "Terrain world transform");
            LTRoadMath.ValidateTransform(roadMatrix, false, "Road transform");
            var settings = new LTRoadMath.Settings
            {
                mode = mode, pattern = LTRoadMath.IsTerrainAsphalt(mode)?LTRoadPattern.Solid:pattern, width = previewWidth??width, shoulderWidth = previewShoulder??shoulderWidth,
                projection = GroundProjection, textureOffset = textureOffset,textureAcrossMetres=textureAcrossMetres,
                blendWidth = previewBlend??blendWidth, flatten = LTRoadMath.IsTerrainAsphalt(mode)?1:flatten, rutWidth = rutWidth,
                rutSeparation = rutSeparation, rutDepth = rutDepth, edgeNoise = edgeNoise,
                noiseSize = noiseSize, seed = seed, sampleSpacing = sampleSpacing,
                variation=variation,
                asphaltDamage=asphaltDamage,
                pitBevelWidth=pitBevelWidth,pitBevelDepth=pitBevelDepth,pitShapeVariation=pitShapeVariation,
                connectedDamage=connectedDamage,connectedFloorAsphalt=connectedFloorAsphalt,repairPatchStrength=repairPatchStrength,
                wheelTracks=wheelTracks,wheelLayerId=ActiveWheelLayer?ActiveWheelLayer.GetInstanceID():0,
                terrainCellSize = terrainCellSize, insetCellSize = insetCellSize, insetLodReduction = insetLodReduction, meshChunkLength = meshChunkLength,
                textureRepeatMetres = textureRepeatMetres, surfaceOffset = surfaceOffset,
                clearVegetation = clearVegetation, clearStones = clearStones, vegetationFade = vegetationFade,
                vegetationOnlyWheelTracks=vegetationOnlyWheelTracks,
                straightStart=startJunction&&startJunction.isActiveAndEnabled,straightEnd=endJunction&&endJunction.isActiveAndEnabled,
                junctionStartLength=startJunction?startJunction.neckLength:0,junctionEndLength=endJunction?endJunction.neckLength:0,
                groundLayerId = PaintGroundLayer != null ? PaintGroundLayer.GetInstanceID() : 0,
                sourceTransformHash = unchecked(LTRoadMath.TransformHash(worldMatrix) * 397 ^ LTRoadMath.TransformHash(roadMatrix) ^
                    (startJunction?startJunction.EndpointHash:0)*17^(endJunction?endJunction.EndpointHash:0)*31^
                    (startJunctionSocket?startJunctionSocket.GetInstanceID():0)*43^(endJunctionSocket?endJunctionSocket.GetInstanceID():0)*71)
            };
            var startShoulder=startJunction?startJunction.ShoulderJoin(this,true,settings.shoulderWidth,settings.blendWidth,startJunctionSocket):null;
            var endShoulder=endJunction?endJunction.ShoulderJoin(this,false,settings.shoulderWidth,settings.blendWidth,endJunctionSocket):null;
            string shoulderKey=(startShoulder?.ContentKey??"")+";"+(endShoulder?.ContentKey??"");
            if (useCache && cached != null && cached.FoundationKey==shoulderKey && cachedSettings.Equals(settings) && cachedRoadMatrix.Equals(roadMatrix) &&
                cachedWorldMatrix.Equals(worldMatrix) && SamePoints()) return cached;
            // Build first: an invalid edit never returns the previous, stale snapshot as if it were valid.
            var effective=new List<LTRoadPoint>(points);
            if(startJunction&&startJunction.isActiveAndEnabled)
            {startJunction.Endpoint(this,true,out var port,out var neck,startJunctionSocket);effective[0]=port;effective.Insert(1,neck);}
            if(endJunction&&endJunction.isActiveAndEnabled)
            {endJunction.Endpoint(this,false,out var port,out var neck,endJunctionSocket);effective[effective.Count-1]=port;effective.Insert(effective.Count-1,neck);}
            var snapshot = LTRoadMath.Build(effective, world.transform.worldToLocalMatrix * roadMatrix, settings).WithShoulders(startShoulder,endShoulder);
            if(!useCache)return snapshot;
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
            terrainLODEdgeError=Clamp(terrainLODEdgeError,0,1,.03f);
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
            asphaltDamage.strength=Clamp(asphaltDamage.strength,0,1,1);
            asphaltDamage.potholes=Clamp(asphaltDamage.potholes,0,1,.45f);
            asphaltDamage.brokenEdges=Clamp(asphaltDamage.brokenEdges,0,1,.25f);
            asphaltDamage.cracks=Clamp(asphaltDamage.cracks,0,1,.65f);
            asphaltDamage.spacing=Clamp(asphaltDamage.spacing,.125f,10000,6);
            asphaltDamage.size=Clamp(asphaltDamage.size,.1f,asphaltDamage.spacing*.8f,Mathf.Min(2,asphaltDamage.spacing*.8f));
            asphaltDamage.depth=Clamp(asphaltDamage.depth,0,2,.15f);
            pitBevelWidth=Clamp(pitBevelWidth,0,2,0);
            pitBevelDepth=Clamp(pitBevelDepth,0,1,0);
            pitShapeVariation=Clamp(pitShapeVariation,0,1,0);
            connectedFloorAsphalt=Clamp(connectedFloorAsphalt,0,1,0);
            repairPatchStrength=Clamp(repairPatchStrength,0,1,0);
            asphaltCrackRepeat=Clamp(asphaltCrackRepeat,.1f,10000,3);
            wheelTracks.width=Clamp(wheelTracks.width,.01f,10000,.65f);wheelTracks.separation=Clamp(wheelTracks.separation,.01f,10000,1.8f);
            wheelTracks.strength=Clamp(wheelTracks.strength,0,1,.85f);wheelTracks.softness=Clamp(wheelTracks.softness,.01f,1,.4f);
            wheelTracks.tileSizeMetres.x=Clamp(wheelTracks.tileSizeMetres.x,.01f,100000,6);
            wheelTracks.tileSizeMetres.y=Clamp(wheelTracks.tileSizeMetres.y,.01f,100000,4);
            wheelTracks.textureOffset.x=Clamp(wheelTracks.textureOffset.x,-1e6f,1e6f,0);
            wheelTracks.textureOffset.y=Clamp(wheelTracks.textureOffset.y,-1e6f,1e6f,0);
            sampleSpacing = Clamp(sampleSpacing, .01f, 10000, 1);
            terrainCellSize = Clamp(terrainCellSize, .01f, 10000, .5f);
            insetCellSize = Clamp(insetCellSize, .05f, 100, 1);
            insetLodReduction = Clamp(insetLodReduction, 0, 2, 1);
            meshChunkLength = Clamp(meshChunkLength, .01f, 100000, 32);
            moduleLength=Clamp(moduleLength,0,100000,0);moduleBaseY=Clamp(moduleBaseY,-10000,10000,0);
            moduleEndBlendLength=Clamp(moduleEndBlendLength,.001f,1000,.5f);
            moduleEndMaxMismatch=Clamp(moduleEndMaxMismatch,.00001f,1,.05f);
            moduleJointWidth=Clamp(moduleJointWidth,.01f,1000,2);
            moduleFuseTolerance=Clamp(moduleFuseTolerance,.00001f,.01f,.0001f);
            moduleFusePlaneTolerance=Clamp(moduleFusePlaneTolerance,.00001f,moduleFuseTolerance,Math.Min(.0001f,moduleFuseTolerance));
            moduleJointMaxChange=Clamp(moduleJointMaxChange,.00001f,100,.5f);
            moduleJointPrecision=Clamp(moduleJointPrecision,.00001f,moduleJointMaxChange,Math.Min(.01f,moduleJointMaxChange));
            textureRepeatMetres = Clamp(textureRepeatMetres, .01f, 100000, 4);
            textureAcrossMetres=Clamp(textureAcrossMetres,0,100000,0);
            surfaceOffset = Clamp(surfaceOffset, 0, 1000, .06f);
            vegetationFade = Clamp(vegetationFade, 0, 1, 1);
            asphaltGrassPadding=Clamp(asphaltGrassPadding,0,10,.35f);
            groundBlendWidth=Clamp(groundBlendWidth,0,10,.6f);
            groundBlendHoleWidth=Clamp(groundBlendHoleWidth,0,10,.2f);
            groundBlendStrength=Clamp(groundBlendStrength,0,1,1);
            groundBlendNoise=Clamp(groundBlendNoise,0,1,.45f);
            groundBlendNoiseSize=Clamp(groundBlendNoiseSize,.05f,100,.7f);
            groundBlendMaskCell=Clamp(groundBlendMaskCell,.01f,1,.05f);
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
