using UnityEngine;
namespace LocalTerrainPrototype
{
    public enum LTStampOperation { Override = 1, Max = 2, Min = 3, Add = 0, Subtract = 4, Multiply = 5, Average = 6, Difference = 7, SqrtMultiply = 8, Blend = 9 }
    public enum LTStampShape { Ellipse, Rectangle }
    public sealed class LTHeightStamp : MonoBehaviour
    {
        public bool affectHeight = true;
        public LTStampOperation operation = LTStampOperation.Max;
        [Tooltip("Override mesh cell size inside this stamp. Independent of global density.")]
        public bool densityZone;
        [Min(.000001f), Tooltip("Smallest cell size in the centre of the density zone.")]
        public float densityCellSizeMin = .25f;
        [Min(.000001f), Tooltip("Largest cell size at the edge of the density zone.")]
        public float densityCellSizeMax = .5f;
        public LTStampShape shape;
        [Min(0.01f)] public Vector2 size = new Vector2(40, 40);
        [Range(0, 1)] public float strength = 1;
        [Range(0.001f, 1)] public float edgeFalloff = 0.5f;
        [HideInInspector] public float height = 5; // v0.1 migration only
        [Range(0,1)] public float blendAmount = 0.5f;
        [Min(0.001f)] public float multiplyReference = 10;
        [Tooltip("If enabled, Mask supplies height shape; otherwise it modulates coverage as in v0.1.")]
        public bool maskIsHeight = true;
        [Tooltip("Optional readable mask: red channel, UV 0..1. Enable Read/Write in texture importer.")]
        public Texture2D mask;
        void OnDrawGizmosSelected()
        {
            Gizmos.color = densityZone ? Color.cyan : Color.yellow;
            Matrix4x4 old = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            if(shape==LTStampShape.Rectangle)Gizmos.DrawWireCube(Vector3.zero, new Vector3(size.x, 0.2f, size.y));
            else
            {
                for(int i=0;i<64;i++)
                {
                    float a=i*Mathf.PI*2/64,b=(i+1)*Mathf.PI*2/64;
                    Gizmos.DrawLine(new Vector3(Mathf.Cos(a)*size.x*.5f,0,Mathf.Sin(a)*size.y*.5f),new Vector3(Mathf.Cos(b)*size.x*.5f,0,Mathf.Sin(b)*size.y*.5f));
                }
            }
            Gizmos.matrix = old;
        }
    }
}
