using UnityEngine;

namespace LocalTerrainPrototype
{
    // Value 3 avoids interpreting legacy RaiseOnly (1) / LowerOnly (2) as Cave.
    public enum LTMeshConformMode { Union = 0, Cave = 3 }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    public sealed class LTMeshStamp : MonoBehaviour, ISerializationCallbackReceiver
    {
        public bool affectHeight = true;
        [Tooltip("Union embeds a rock. Cave keeps the underground part of a closed cutter mesh as inward-facing cave walls. The cutter must intersect the terrain.")]
        public LTMeshConformMode mode = LTMeshConformMode.Union;
        [Min(0), Tooltip("Distance in metres over which terrain blends into the rock footprint.")]
        public float blendDistance = 4;
        [Min(.1f), Tooltip("Samples per metre at the mesh contact. Cell size = 1 / this value, independent of Blend Distance, Cut Offset and Retopo Height.")]
        public float gridDensityMultiplier = 1;
        [Tooltip("Remove terrain triangles below the projected contact surface of this mesh.")]
        public bool cutTerrain = true;
        [Min(.01f), Tooltip("Distance in metres from the rock intersection to the terrain cut. The resulting gap is filled by the retopologized bridge.")]
        public float terrainCutOffset = 1;
        [Min(.01f), Tooltip("Height above the terrain of the lower rock band replaced by the bridge. Its topology uses Grid Density Multiplier.")]
        public float rockRetopoHeight = 1;
        [Min(0), Tooltip("Weld distance after Grid Density and bridge generation. Collapses nearby connected bridge vertices when topology is safe and repairs disconnected edges. Terrain boundary stays fixed to prevent cracks. Zero only welds coincident points.")]
        public float weldDistance = .1f;
        [Tooltip("Generate an instance mesh with the part below the terrain surface removed. The source mesh asset is never modified.")]
        public bool trimRockInsideTerrain = true;
        [Tooltip("Use terrain layers on this rock (world-space projection, no rock displacement). Disable to restore the object's materials; the bridge keeps the terrain preview material.")]
        public bool useTerrainMaterial = true;
        [Tooltip("Show detected open/invalid edges in red when selected. Expected terrain contact edges are not errors.")]
        public bool showGeometryIssues = false;
        [SerializeField, HideInInspector] public Material[] objectMaterials;
        [SerializeField, HideInInspector] public bool terrainMaterialApplied;
        [System.NonSerialized] public bool terrainLayersMaterialApplied;
        [SerializeField, HideInInspector] public bool unionGroupOutputApplied;
        [System.NonSerialized] public Vector3[] geometryIssueEdges;

        [SerializeField, HideInInspector] public Mesh sourceMesh;
        [System.NonSerialized] public Mesh generatedTrimmedMesh;
        [System.NonSerialized] public int generatedMeshOwner;

        void OnValidate()
        {
            NormalizeMode();
            blendDistance=Mathf.Max(0,blendDistance);
            terrainCutOffset=Mathf.Max(.01f,terrainCutOffset);
            rockRetopoHeight=Mathf.Max(.01f,rockRetopoHeight);
            weldDistance=Mathf.Max(0,weldDistance);
            gridDensityMultiplier=Mathf.Max(.1f,gridDensityMultiplier);
        }

        void NormalizeMode(){if(mode!=LTMeshConformMode.Cave)mode=LTMeshConformMode.Union;}
        public void OnBeforeSerialize(){}
        public void OnAfterDeserialize(){NormalizeMode();}

        void OnDrawGizmosSelected()
        {
            var renderer=GetComponent<MeshRenderer>();
            if(!renderer)return;
            var old=Gizmos.matrix;Gizmos.matrix=Matrix4x4.identity;
            var bounds=renderer.bounds;
            if(sourceMesh)
            {
                var sourceBounds=sourceMesh.bounds;
                bounds=new Bounds(transform.TransformPoint(sourceBounds.center),Vector3.zero);
                for(int i=0;i<8;i++)bounds.Encapsulate(transform.TransformPoint(sourceBounds.center+Vector3.Scale(sourceBounds.extents,
                    new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1))));
            }
            // Blend Distance is expressed in world metres and must not be
            // multiplied by the rock object's transform scale.
            float extent=Mathf.Max(blendDistance,terrainCutOffset);
            bounds.Expand(new Vector3(extent*2,0,extent*2));
            Gizmos.color=new Color(1,.45f,0,.85f);
            Gizmos.DrawWireCube(bounds.center,bounds.size);
            if(showGeometryIssues&&geometryIssueEdges!=null)
            {
                Gizmos.matrix=transform.localToWorldMatrix;Gizmos.color=Color.red;
                for(int i=0;i+1<geometryIssueEdges.Length;i+=2)Gizmos.DrawLine(geometryIssueEdges[i],geometryIssueEdges[i+1]);
            }
            Gizmos.matrix=old;
        }
    }
}
