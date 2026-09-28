using UnityEngine;

namespace LocalTerrainPrototype
{
    [System.Serializable]
    public sealed class LTPaintNoise
    {
        public bool enabled;
        [Min(.1f)] public float size=10;
        [Range(0,1)] public float strength=1;
        [Range(0,1)] public float threshold=.5f;
        [Range(0,1)] public float softness=.2f;
        public int seed;
        public int CoverageHash()
        {
            if(!enabled)return 0;
            unchecked {int h=1;h=h*397^size.GetHashCode();h=h*397^strength.GetHashCode();
                h=h*397^threshold.GetHashCode();h=h*397^softness.GetHashCode();return h*397^seed;}
        }
    }
    [System.Serializable]
    public sealed class LTPaintFilter
    {
        public bool enabled;
        public Vector2 range;
        [Min(0)] public float feather;
        public LTPaintFilter(float min,float max,float softness){range=new Vector2(min,max);feather=softness;}
        public float Evaluate(float value)=>enabled?LTPaintMath.RangeWeight(value,range,feather):1;
    }
    [DisallowMultipleComponent]
    [AddComponentMenu("Local Terrain/Layer Stamp")]
    public sealed class LTPaintStamp : MonoBehaviour
    {
        public LTSurfaceLayer layer;
        public LTRoad Road=>GetComponent<LTRoad>();
        public LTRoadJunction Junction=>GetComponent<LTRoadJunction>();
        public LTSurfaceLayer EffectiveLayer {get {var road=Road;var node=Junction;return road?road.groundLayer:node?node.groundLayer:layer;}}
        public bool ActiveForPaint {get {var road=Road;var node=Junction;return isActiveAndEnabled&&(!road||road.isActiveAndEnabled)&&(!node||node.isActiveAndEnabled);}}
        public LTStampShape shape=LTStampShape.Ellipse;
        public Vector2 size=new Vector2(40,40);
        [Min(.5f), Tooltip("Желаемый шаг карты глубины этой области, сантиметры. Не меняет маску покраски.")]
        public float deformationStepCm=2;
        [Range(128,2048), Tooltip("Максимальная сторона карты глубины. При достижении лимита фактический шаг увеличивается.")]
        public int deformationMaxResolution=1024;
        [System.NonSerialized] public string deformationMapStatus;
        [Range(0,1)] public float strength=1;
        [Range(.001f,1)] public float edgeFalloff=.5f;
        [Tooltip("Optional coverage mask: R channel, linear (sRGB off). Read/Write is not required.")]
        public Texture2D mask;
        public LTPaintNoise noise=new LTPaintNoise();
        public LTPaintFilter heightFilter=new LTPaintFilter(0,1000,10);
        public LTPaintFilter slopeFilter=new LTPaintFilter(0,90,5);
        public LTPaintFilter curveFilter=new LTPaintFilter(-1,1,.1f);
        [Min(.1f)] public float curveRadius=5;
        public bool HasTerrainFilters=>!Road&&!Junction&&(heightFilter.enabled||slopeFilter.enabled||curveFilter.enabled);
        void OnValidate(){size.x=Mathf.Max(.01f,size.x);size.y=Mathf.Max(.01f,size.y);}
        void OnDrawGizmosSelected()
        {
            if(Road||Junction)return;
            var matrix=Gizmos.matrix;var color=Gizmos.color;
            Gizmos.matrix=transform.localToWorldMatrix;Gizmos.color=new Color(.9f,.3f,1);
            if(shape==LTStampShape.Rectangle)Gizmos.DrawWireCube(Vector3.zero,new Vector3(size.x,.1f,size.y));
            else for(int i=0;i<64;i++)
            {
                float a=i*Mathf.PI*2/64,b=(i+1)*Mathf.PI*2/64;
                Gizmos.DrawLine(new Vector3(Mathf.Cos(a)*size.x*.5f,0,Mathf.Sin(a)*size.y*.5f),new Vector3(Mathf.Cos(b)*size.x*.5f,0,Mathf.Sin(b)*size.y*.5f));
            }
            Gizmos.matrix=matrix;Gizmos.color=color;
        }
    }
}
