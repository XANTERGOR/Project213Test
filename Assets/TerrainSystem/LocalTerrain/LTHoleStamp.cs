using UnityEngine;
namespace LocalTerrainPrototype
{
    public enum LTHoleStampMode { ShapeMask = 0, MeshVolume = 1 }
    [DisallowMultipleComponent,AddComponentMenu("Local Terrain/Hole Stamp")]
    public sealed class LTHoleStamp : MonoBehaviour
    {
        public LTHoleStampMode cutMode;
        [Tooltip("Closed readable triangle mesh. Its local coordinates/pivot use this stamp's Transform, including Y and XYZ rotation. No renderer, walls or floor are created. Shape/Size/Mask are ignored in Mesh Volume mode.")]
        public Mesh cutMesh;
        [Min(0),Tooltip("Maximum horizontal deviation of a simplified mesh-volume cut contour from LOD0, in metres. 0 retains the exact rim. Complex boundaries and narrow bridges can keep finer cells. Collider remains LOD0.")]
        public float meshLodContourError=.5f;
        public LTStampShape shape=LTStampShape.Ellipse;
        [Min(.01f)] public Vector2 size=new Vector2(8,8);
        [Tooltip("Optional readable mask: white cuts, black keeps terrain. Empty = cut the whole Shape.")]
        public Texture2D mask;
        public bool invertMask;
        [Range(.001f,1),Tooltip("Cut where red (after inversion) reaches this value.")]
        public float maskThreshold=.5f;
        [Min(.01f),Tooltip("Requested contour cell size in world metres. Smaller = finer contour, more geometry. Sub-cell mask details may be lost.")]
        public float edgeCellSize=.25f;
        [Min(0),Tooltip("Maximum contour cell size on distant LODs, in metres. 0 locks the original rim. Shape-dependent limits protect the opening and corners; see effective limits below. Mask contours remain protected. Collider always uses LOD0.")]
        public float lodEdgeCellSize=4;
        [Min(0),Tooltip("Allowed horizontal corner error when merging rectangular cut cells (metres). Capped to 1/16 of the narrowest footprint span. 0 keeps original corner cells. Does not repair LOD0 approximation; LOD0/collider are unchanged.")]
        public float lodCornerError=1;
        [System.NonSerialized] public string status;
        void OnDrawGizmosSelected()
        {
            var old=Gizmos.matrix;var color=Gizmos.color;
            Gizmos.matrix=transform.localToWorldMatrix;Gizmos.color=new Color(1,.3f,.2f);
            if(cutMode==LTHoleStampMode.MeshVolume)
            {if(cutMesh)Gizmos.DrawWireMesh(cutMesh);}
            else if(shape==LTStampShape.Rectangle)Gizmos.DrawWireCube(Vector3.zero,new Vector3(size.x,.05f,size.y));
            else for(int i=0;i<64;i++)
            {
                float a=i*Mathf.PI/32,b=(i+1)*Mathf.PI/32;
                Gizmos.DrawLine(new Vector3(Mathf.Cos(a)*size.x*.5f,0,Mathf.Sin(a)*size.y*.5f),new Vector3(Mathf.Cos(b)*size.x*.5f,0,Mathf.Sin(b)*size.y*.5f));
            }
            Gizmos.matrix=old;Gizmos.color=color;
        }
    }
}
