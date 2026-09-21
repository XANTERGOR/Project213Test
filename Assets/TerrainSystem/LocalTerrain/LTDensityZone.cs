using UnityEngine;
namespace LocalTerrainPrototype
{
    [DisallowMultipleComponent]
    public sealed class LTDensityZone : MonoBehaviour
    {
        [Min(.01f)] public Vector2 size = new Vector2(16,16);
        [Min(.01f), Tooltip("Maximum cell side in metres in this zone; rounded down to a supported subdivision step.")]
        public float cellSize = .5f;
        void OnDrawGizmos()
        {
            var old=Gizmos.matrix;Gizmos.matrix=transform.localToWorldMatrix;
            Gizmos.color=new Color(0,1,1,.8f);
            Gizmos.DrawWireCube(Vector3.zero,new Vector3(size.x,.3f,size.y));Gizmos.matrix=old;
        }
    }
}
