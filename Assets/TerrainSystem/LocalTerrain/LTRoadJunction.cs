using System;
using System.Collections.Generic;
using UnityEngine;
namespace LocalTerrainPrototype
{
    [ExecuteAlways,DisallowMultipleComponent,RequireComponent(typeof(LTPaintStamp))]
    [AddComponentMenu("Local Terrain/Road Junction")]
    public sealed class LTRoadJunction : MonoBehaviour
    {
        public LTRoadMode surface;
        [Min(.1f)] public float radius=8;
        [Min(.1f)] public float neckLength=3;
        [Min(0)] public float blendWidth=3;
        [Min(.01f)] public float terrainCellSize=.5f;
        public LTSurfaceLayer groundLayer;
        public Material asphaltMaterial;
        [Min(.01f)] public float textureRepeatMetres=4;
        [Min(0)] public float surfaceOffset=.06f;
        public bool clearVegetation=true,clearStones;
        public LTLODSettings[] lods={new LTLODSettings(1,.01f,60),new LTLODSettings(2,.01f,140),new LTLODSettings(3,.01f,300)};
        [HideInInspector] public string bakeId;
        [HideInInspector] public Transform generatedRoot;
        [NonSerialized] public string status;
        public LTWorld World=>GetComponentInParent<LTWorld>();
        public IEnumerable<LTRoad> Roads()
        {
            var world=World;if(!world)yield break;
            foreach(var road in world.GetComponentsInChildren<LTRoad>())
                if(road.isActiveAndEnabled&&road.World==world&&(road.startJunction==this||road.endJunction==this))yield return road;
        }
        public int EndpointHash=>unchecked(transform.localToWorldMatrix.GetHashCode()*397^radius.GetHashCode()^neckLength.GetHashCode()*13^
            (int)surface*23^surfaceOffset.GetHashCode()^(isActiveAndEnabled?97:0));
        public LTRoadJunctionMath.Port Port(LTRoad road,bool start)
        {
            if(!World||World!=road.World)throw new ArgumentException("Дорога и перекрёсток должны принадлежать одному LTWorld.");
            LTRoadMath.ValidateTransform(transform.localToWorldMatrix,false,"Junction transform");
            LTRoadMath.ValidateTransform(World.transform.localToWorldMatrix,true,"Terrain world transform");
            if(road.points==null||road.points.Count<2)throw new ArgumentException("Для подключения нужны минимум две точки дороги.");
            var centre=World.transform.InverseTransformPoint(transform.position);
            var anchor=World.transform.InverseTransformPoint(road.transform.TransformPoint(road.points[start?1:road.points.Count-2].position));
            var port=LTRoadJunctionMath.MakePort(centre,anchor,radius,road.width);
            if(new Vector2(anchor.x-port.centre.x,anchor.z-port.centre.z).magnitude<=neckLength+.05f)
                throw new ArgumentException("Отодвиньте вторую точку от перекрёстка: не хватает места для прямого въезда (Neck Length).");
            return port;
        }
        public LTRoadJunctionMath.Snapshot Capture()
        {
            if(!World)throw new ArgumentException("Перекрёсток должен находиться внутри LTWorld.");
            var ports=new List<LTRoadJunctionMath.Port>();
            foreach(var road in Roads())
            {
                if(road.startJunction==this)ports.Add(Port(road,true));
                if(road.endJunction==this)ports.Add(Port(road,false));
                if(road.groundLayer&&road.groundLayer==groundLayer&&road.projection==LTRoadProjection.Spline)
                    throw new ArgumentException("Для площадки назначьте отдельный слой с обычным грунтом. Направленный слой веток может быть общим для всех дорог.");
                if(road.mode==LTRoadMode.Asphalt&&surface!=LTRoadMode.Asphalt)
                    throw new ArgumentException("Для подключения асфальта выберите Asphalt у узла перекрёстка.");
                if(road.mode==LTRoadMode.Asphalt&&Mathf.Abs(road.surfaceOffset-surfaceOffset)>.0001f)
                    throw new ArgumentException("Surface Offset узла и подключённого асфальта должен совпадать.");
            }
            return LTRoadJunctionMath.Build(World.transform.InverseTransformPoint(transform.position),ports,blendWidth,terrainCellSize);
        }
        public void Endpoint(LTRoad road,bool start,out LTRoadPoint portPoint,out LTRoadPoint neckPoint)
        {
            var port=Port(road,start);var direction=new Vector3(-port.right.z,0,port.right.x);
            var centre=port.centre;
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
        void OnEnable(){if(OwnsOutput)generatedRoot.gameObject.SetActive(surface==LTRoadMode.Asphalt);}
        void OnDisable(){if(OwnsOutput)generatedRoot.gameObject.SetActive(false);}
        void OnValidate()
        {
            radius=Mathf.Max(.1f,radius);neckLength=Mathf.Max(.1f,neckLength);blendWidth=Mathf.Max(0,blendWidth);
            terrainCellSize=Mathf.Max(.01f,terrainCellSize);textureRepeatMetres=Mathf.Max(.01f,textureRepeatMetres);surfaceOffset=Mathf.Max(0,surfaceOffset);
        }
    }
}
