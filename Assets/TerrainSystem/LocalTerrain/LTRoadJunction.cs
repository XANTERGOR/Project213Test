using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace LocalTerrainPrototype
{
    public enum LTRoadJunctionSource { Procedural, AuthoredPrefab, OwnHierarchy }
    public enum LTRoadJunctionFit { StrictFuse, FitProfile }
    public enum LTRoadJunctionFoundation { Circle, MeshFootprint }
    [ExecuteAlways,DisallowMultipleComponent,RequireComponent(typeof(LTPaintStamp))]
    [AddComponentMenu("Local Terrain/Road Junction")]
    public sealed class LTRoadJunction : MonoBehaviour
    {
        public LTRoadMode surface;
        public LTRoadJunctionSource meshSource;
        public GameObject junctionPrefab;
        [Range(.00001f,.01f)] public float socketTolerance=.0005f;
        public LTRoadJunctionFit connectionFit;
        [Min(.02f)] public float profileBlendLength=1;
        [Range(.001f,.5f)] public float profileMaxShift=.1f;
        [Range(.00001f,.02f)] public float profileSearchBand=.005f;
        public bool FitProfile=>connectionFit==LTRoadJunctionFit.FitProfile;
        public bool OwnHierarchy=>meshSource==LTRoadJunctionSource.OwnHierarchy;
        public bool Authored=>meshSource==LTRoadJunctionSource.AuthoredPrefab||OwnHierarchy;
        public GameObject SourceRoot=>OwnHierarchy?gameObject:junctionPrefab;
        public LTRoadJunctionSocket[] Sockets=>SourceRoot?SourceRoot.GetComponentsInChildren<LTRoadJunctionSocket>(true).Where(s=>SourceMember(s.transform)).ToArray():Array.Empty<LTRoadJunctionSocket>();
        // Generated output and nested independent roads/junctions are NEVER source geometry.
        public bool SourceMember(Transform candidate)
        {
            var root=SourceRoot;if(!root||!candidate)return false;
            for(var t=candidate;t;t=t.parent)
            {
                if(t.TryGetComponent<LTRoadJunctionGenerated>(out _)||t.TryGetComponent<LTRoadGenerated>(out _))return false;
                if(t==root.transform)return true;
                if(t.TryGetComponent<LTRoadJunction>(out _)||t.TryGetComponent<LTRoad>(out _))return false;
            }
            return false;
        }
        [Serializable] public sealed class SourceRendererState { public Renderer target;public bool enabled; }
        [Serializable] public sealed class SourceLODState { public LODGroup target;public bool enabled; }
        [Serializable] public sealed class SourceColliderState { public Collider target;public bool enabled; }
        [HideInInspector] public List<SourceRendererState> sourceRenderers=new List<SourceRendererState>();
        [HideInInspector] public List<SourceLODState> sourceLODGroups=new List<SourceLODState>();
        [HideInInspector] public List<SourceColliderState> sourceColliders=new List<SourceColliderState>();
        // Saved state survives reload/player startup. Renderer.enabled is used rather than
        // disabling source GameObjects, which could also contain the sockets or scripts.
        public void SetSourceSuppressed(bool suppress)
        {
            if(!suppress)
            {
                foreach(var s in sourceRenderers)if(s.target)s.target.enabled=s.enabled;
                foreach(var s in sourceColliders)if(s.target)s.target.enabled=s.enabled;
                foreach(var s in sourceLODGroups)if(s.target)s.target.enabled=s.enabled;
                sourceRenderers.Clear();sourceLODGroups.Clear();sourceColliders.Clear();return;
            }
            if(!OwnHierarchy)return;
            foreach(var group in GetComponentsInChildren<LODGroup>(true).Where(g=>SourceMember(g.transform)))
            {if(!sourceLODGroups.Any(s=>s.target==group))sourceLODGroups.Add(new SourceLODState{target=group,enabled=group.enabled});group.enabled=false;}
            foreach(var renderer in GetComponentsInChildren<Renderer>(true).Where(r=>SourceMember(r.transform)))
            {if(!sourceRenderers.Any(s=>s.target==renderer))sourceRenderers.Add(new SourceRendererState{target=renderer,enabled=renderer.enabled});renderer.enabled=false;}
            foreach(var collider in GetComponentsInChildren<Collider>(true).Where(c=>SourceMember(c.transform)))
            {if(!sourceColliders.Any(s=>s.target==collider))sourceColliders.Add(new SourceColliderState{target=collider,enabled=collider.enabled});collider.enabled=false;}
        }
        [Min(.1f)] public float radius=8;
        public LTRoadJunctionFoundation foundationMode=LTRoadJunctionFoundation.MeshFootprint;
        [Min(0)] public float foundationShoulder=.5f;
        public bool coordinateShoulders;
        [Min(.1f)] public float shoulderJoinLength=3;
        public bool MeshFoundation=>Authored&&foundationMode==LTRoadJunctionFoundation.MeshFootprint;
        [Min(.1f)] public float neckLength=3;
        [Min(0)] public float blendWidth=3;
        [Min(.01f)] public float terrainCellSize=.5f;
        public bool simplifyTerrainUnderAsphalt=true;
        [Range(0,1)] public float terrainLODEdgeError=.03f;
        public LTSurfaceLayer groundLayer;
        public Material asphaltMaterial;
        // Independent from the foundation paint layer: blending must not repaint terrain.
        public bool groundBlend;
        public LTRoadGroundSource groundBlendSource=LTRoadGroundSource.Terrain;
        public LTSurfaceLayer groundBlendLayer;
        [Range(0,10)] public float groundBlendWidth=.6f,groundBlendHoleWidth=.2f;
        [Range(0,1)] public float groundBlendStrength=1,groundBlendNoise=.45f;
        [Range(.05f,100)] public float groundBlendNoiseSize=.7f;
        public int groundBlendSeed=12345;
        [Range(.01f,1)] public float groundBlendMaskCell=.05f;
        public bool AutomaticGroundBlend=>Authored&&surface==LTRoadMode.Asphalt&&groundBlend&&groundBlendSource==LTRoadGroundSource.Terrain;
        [Min(.01f)] public float textureRepeatMetres=4;
        [Min(0)] public float surfaceOffset=.06f;
        [Range(0,10),Tooltip("Опускает только готовый меш и коллайдер. Основание terrain остаётся на прежней высоте. Как положительный «Уровень покрытия в модуле» у дороги.")]
        public float meshDepth;
        public float MeshSurfaceOffset
        {
            get
            {
                if(!Authored)return surfaceOffset;
                if(!float.IsFinite(meshDepth)||meshDepth<0||meshDepth>10)
                    throw new ArgumentException("Заглубление меша перекрёстка должно быть конечным числом от 0 до 10 м.");
                return surfaceOffset-meshDepth;
            }
        }
        public bool clearVegetation=true,clearStones;
        public LTLODSettings[] lods={new LTLODSettings(1,.01f,60),new LTLODSettings(2,.01f,140),new LTLODSettings(3,.01f,300)};
        [HideInInspector] public string bakeId;
        [HideInInspector] public Transform generatedRoot;
        [NonSerialized] public string status;
        public LTWorld World=>GetComponentInParent<LTWorld>();
        public bool CoordinateShoulders=>isActiveAndEnabled&&surface==LTRoadMode.Asphalt&&MeshFoundation&&coordinateShoulders;
        public LTRoadShoulderJoin ShoulderJoin(LTRoad road,bool start,float shoulder,float blend,LTRoadJunctionSocket proposed=null)
        {
            if(!CoordinateShoulders||road.mode!=LTRoadMode.Asphalt)return null;
            if(!float.IsFinite(shoulderJoinLength)||shoulderJoinLength<.1f||shoulderJoinLength>1000)
                throw new ArgumentException("Длина согласования обочин: от 0.1 до 1000 м.");
            // Do not call Capture here: it validates all roads. This dependency
            // is one-way from road to immutable LOD0 foundation, never recursive.
            var node=LTRoadJunctionMath.MeshFoundation(World.transform.InverseTransformPoint(transform.position),
                LTRoadJunctionFootprint.Capture(this),foundationShoulder,blendWidth,terrainCellSize,0);
            return new LTRoadShoulderJoin(node,Port(road,start,proposed),shoulder,blend,Math.Min(shoulderJoinLength,neckLength),
                groundLayer&&groundLayer==road.PaintGroundLayer&&GetComponent<LTPaintStamp>().ActiveForPaint,
                (clearVegetation?LTDetailCategory.Vegetation:0)|(clearStones?LTDetailCategory.Stones:0));
        }
        public IEnumerable<LTRoad> Roads()
        {
            var world=World;if(!world)yield break;
            foreach(var road in world.GetComponentsInChildren<LTRoad>())
                if(road.isActiveAndEnabled&&road.World==world&&(road.startJunction==this||road.endJunction==this))yield return road;
        }
        public int EndpointHash
        {
            get
            {
                unchecked
                {
                    int h=transform.localToWorldMatrix.GetHashCode()*397^radius.GetHashCode()^neckLength.GetHashCode()*13^
                        (int)surface*23^surfaceOffset.GetHashCode()^(isActiveAndEnabled?97:0)^(int)meshSource*37;
                    if(!Authored)return h;
                    h=h*397^(int)foundationMode;h=h*397^foundationShoulder.GetHashCode();
                    // Preserve the old signature at the default depth; nonzero changes
                    // also invalidate attached road meshes so their seam is rebuilt.
                    if(meshDepth!=0)h=h*397^meshDepth.GetHashCode();
                    h=h*397^(SourceRoot?SourceRoot.GetInstanceID():0);h=h*397^socketTolerance.GetHashCode();
                    h=h*397^(int)connectionFit;h=h*397^profileBlendLength.GetHashCode();
                    h=h*397^profileMaxShift.GetHashCode();h=h*397^profileSearchBand.GetHashCode();
                    foreach(var socket in Sockets)
                    {h=h*397^socket.GetInstanceID();h=h*397^socket.transform.localToWorldMatrix.GetHashCode();h=h*397^socket.width.GetHashCode();}
                    return h;
                }
            }
        }
        public Matrix4x4 SocketMatrix(LTRoadJunctionSocket socket)
        {
            if(!Authored||surface!=LTRoadMode.Asphalt||!SourceRoot||!socket||!SourceMember(socket.transform))
                throw new ArgumentException("Выберите въезд внутри этого перекрёстка (Asphalt).");
            if(!World)throw new ArgumentException("Перекрёсток должен находиться внутри LTWorld.");
            var local=World.transform.worldToLocalMatrix*transform.localToWorldMatrix;
            LTRoadMath.ValidateTransform(local,false,"Authored junction transform");
            if((local.MultiplyVector(Vector3.up)-Vector3.up).sqrMagnitude>1e-6f)
                throw new ArgumentException("Перекрёсток из префаба: допускается поворот вокруг Y, без наклона и масштаба.");
            return local*socket.InPrefab(SourceRoot);
        }
        public LTRoadJunctionSocket Socket(LTRoad road,bool start,LTRoadJunctionSocket proposed=null)
        {
            var socket=proposed?proposed:start?road.startJunctionSocket:road.endJunctionSocket;
            SocketMatrix(socket);
            foreach(var other in Roads())
            {
                if(other.startJunction==this&&other.startJunctionSocket==socket&&(other!=road||!start)||
                   other.endJunction==this&&other.endJunctionSocket==socket&&(other!=road||start))
                    throw new ArgumentException(socket.name+": въезд уже занят другим концом дороги.");
            }
            return socket;
        }
        public LTRoadJunctionMath.Port Port(LTRoad road,bool start,LTRoadJunctionSocket proposed=null)
        {
            if(LTRoadMath.IsTerrainAsphalt(road.mode))throw new ArgumentException("Terrain Asphalt (experimental): перекрёстки пока не поддерживаются.");
            if(!World||World!=road.World)throw new ArgumentException("Дорога и перекрёсток должны принадлежать одному LTWorld.");
            LTRoadMath.ValidateTransform(transform.localToWorldMatrix,false,"Junction transform");
            LTRoadMath.ValidateTransform(World.transform.localToWorldMatrix,true,"Terrain world transform");
            if(road.points==null||road.points.Count<2)throw new ArgumentException("Для подключения нужны минимум две точки дороги.");
            var centre=World.transform.InverseTransformPoint(transform.position);
            var anchor=World.transform.InverseTransformPoint(road.transform.TransformPoint(road.points[start?1:road.points.Count-2].position));
            if(Authored)
            {
                if(road.mode!=LTRoadMode.Asphalt)throw new ArgumentException("К префабу перекрёстка пока подключается только Asphalt.");
                var socket=Socket(road,start,proposed);var m=SocketMatrix(socket);var p=m.MultiplyPoint3x4(Vector3.zero);
                if(Mathf.Abs(socket.width-road.width)>socketTolerance)throw new ArgumentException(socket.name+": ширина дороги должна совпадать с шириной въезда.");
                if(Vector3.Dot(anchor-p,m.MultiplyVector(Vector3.forward))<=neckLength+.05f)
                    throw new ArgumentException(socket.name+": отодвиньте вторую/предпоследнюю точку наружу за прямой въезд (Neck Length).");
                return new LTRoadJunctionMath.Port{centre=p,right=m.MultiplyVector(Vector3.right).normalized,width=socket.width};
            }
            var port=LTRoadJunctionMath.MakePort(centre,anchor,radius,road.width);
            if(new Vector2(anchor.x-port.centre.x,anchor.z-port.centre.z).magnitude<=neckLength+.05f)
                throw new ArgumentException("Отодвиньте вторую точку от перекрёстка: не хватает места для прямого въезда (Neck Length).");
            return port;
        }
        public LTRoadJunctionMath.Snapshot Capture()
        {
            if(LTRoadMath.IsTerrainAsphalt(surface))throw new ArgumentException("Terrain Asphalt (experimental): перекрёстки пока не поддерживаются.");
            if(!World)throw new ArgumentException("Перекрёсток должен находиться внутри LTWorld.");
            var ports=new List<LTRoadJunctionMath.Port>();
            _=MeshSurfaceOffset; // Validate before any bake publishes geometry.
            foreach(var road in Roads())
            {
                if(road.startJunction==this)ports.Add(Port(road,true));
                if(road.endJunction==this)ports.Add(Port(road,false));
                if(groundLayer&&(road.groundLayer==groundLayer&&road.GroundProjection==LTRoadProjection.Spline||
                    road.ActiveWheelLayer==groundLayer&&road.WheelProjection==LTRoadProjection.Spline))
                    throw new ArgumentException("Для площадки назначьте отдельный слой с обычным грунтом. Направленный слой веток может быть общим для всех дорог.");
                if(road.mode==LTRoadMode.Asphalt&&surface!=LTRoadMode.Asphalt)
                    throw new ArgumentException("Для подключения асфальта выберите Asphalt у узла перекрёстка.");
                if(!OwnHierarchy&&road.mode==LTRoadMode.Asphalt&&Mathf.Abs(road.surfaceOffset-surfaceOffset)>.0001f)
                    throw new ArgumentException("Surface Offset узла и подключённого асфальта должен совпадать.");
            }
            var centre=World.transform.InverseTransformPoint(transform.position);
            if(Authored)
            {
                if(connectionFit!=LTRoadJunctionFit.StrictFuse&&connectionFit!=LTRoadJunctionFit.FitProfile)
                    throw new ArgumentException("Неизвестный режим стыка перекрёстка.");
                bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
                if(FitProfile&&(!Finite(profileBlendLength)||!Finite(profileMaxShift)||!Finite(profileSearchBand)||
                    profileSearchBand<.00001f||profileSearchBand>.02f||profileMaxShift<profileSearchBand||profileMaxShift>.5f||
                    profileBlendLength<Math.Max(.02f,profileSearchBand*4)||profileBlendLength>neckLength))
                    throw new ArgumentException("Подгонка: полоса поиска 0.01–20 мм; предел сдвига от полосы поиска до 0.5 м; переход не короче четырёх полос поиска и не длиннее прямого въезда.");
                var sockets=Sockets;
                if(sockets.Length<2||sockets.Length>8)throw new ArgumentException("На префабе нужны 2–8 маркеров Road Junction Socket.");
                foreach(var socket in sockets)SocketMatrix(socket);
                if(MeshFoundation)return LTRoadJunctionMath.MeshFoundation(centre,LTRoadJunctionFootprint.Capture(this),
                    foundationShoulder,blendWidth,terrainCellSize,EndpointHash);
                if(foundationMode!=LTRoadJunctionFoundation.Circle)throw new ArgumentException("Неизвестная форма основания перекрёстка.");
                return LTRoadJunctionMath.Foundation(centre,radius,blendWidth,terrainCellSize,EndpointHash);
            }
            return LTRoadJunctionMath.Build(centre,ports,blendWidth,terrainCellSize);
        }
        public void Endpoint(LTRoad road,bool start,out LTRoadPoint portPoint,out LTRoadPoint neckPoint,LTRoadJunctionSocket proposed=null)
        {
            var port=Port(road,start,proposed);var direction=new Vector3(-port.right.z,0,port.right.x);
            var centre=port.centre;
            // These are terrain/spline reference points, NOT the rendered rim.
            // meshDepth is applied to geometry and the seam frame only; lowering
            // these points too would pull the road's ground down with the asphalt.
            if(OwnHierarchy&&road.mode==LTRoadMode.Asphalt)centre.y+=surfaceOffset-road.surfaceOffset;
            if(road.mode==LTRoadMode.Offroad&&surface==LTRoadMode.Asphalt)centre.y+=surfaceOffset;
            portPoint=new LTRoadPoint(road.transform.InverseTransformPoint(World.transform.TransformPoint(centre)),0);
            neckPoint=new LTRoadPoint(road.transform.InverseTransformPoint(World.transform.TransformPoint(centre+direction*neckLength)),0);
        }
        public LTRoadMath.Snapshot Suppression()
        {
            var data=Capture();var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.Asphalt;
            settings.width=2*data.coreRadius;settings.shoulderWidth=blendWidth;settings.blendWidth=0;settings.sourceTransformHash=data.hash;
            return LTRoadMath.Build(new[]{new LTRoadPoint(data.centre-Vector3.right*.01f),new LTRoadPoint(data.centre+Vector3.right*.01f)},Matrix4x4.identity,settings);
        }
        public bool OwnsOutput=>generatedRoot&&generatedRoot.IsChildOf(transform)&&generatedRoot.GetComponent<LTRoadJunctionGenerated>()?.owner==this;
        void OnEnable()=>RefreshSourceVisibility();
        void OnTransformParentChanged(){if(isActiveAndEnabled)RefreshSourceVisibility();}
        void RefreshSourceVisibility()
        {
            bool output=World&&surface==LTRoadMode.Asphalt&&OwnsOutput;
            if(OwnsOutput)generatedRoot.gameObject.SetActive(output);
            SetSourceSuppressed(OwnHierarchy&&output);
            if(World)World.RefreshLODCache();
        }
        void OnDisable(){if(OwnsOutput)generatedRoot.gameObject.SetActive(false);SetSourceSuppressed(false);}
        void Reset()
        {
            if(GetComponentsInChildren<MeshRenderer>(true).Length>0)
            {meshSource=LTRoadJunctionSource.OwnHierarchy;surface=LTRoadMode.Asphalt;}
        }
        void OnValidate()
        {
            radius=Mathf.Max(.1f,radius);neckLength=Mathf.Max(.1f,neckLength);blendWidth=Mathf.Max(0,blendWidth);
            terrainCellSize=Mathf.Max(.01f,terrainCellSize);textureRepeatMetres=Mathf.Max(.01f,textureRepeatMetres);surfaceOffset=Mathf.Max(0,surfaceOffset);
            meshDepth=float.IsFinite(meshDepth)?Mathf.Clamp(meshDepth,0,10):0;
            terrainLODEdgeError=float.IsFinite(terrainLODEdgeError)?Mathf.Clamp(terrainLODEdgeError,0,1):.03f;
            foundationShoulder=float.IsFinite(foundationShoulder)?Mathf.Max(0,foundationShoulder):0;
            shoulderJoinLength=float.IsFinite(shoulderJoinLength)?Mathf.Clamp(shoulderJoinLength,.1f,1000):3;
        }
    }
}
