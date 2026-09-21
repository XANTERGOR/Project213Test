using UnityEngine;
namespace LocalTerrainPrototype
{
    [System.Serializable]
    public sealed class LTLODPlan
    {
        public System.Collections.Generic.List<Vector3Int> leaves = new System.Collections.Generic.List<Vector3Int>();
    }
    public sealed class LTChunk : MonoBehaviour
    {
        public int x, z;
        public Mesh mesh;
        [HideInInspector] public Mesh[] lodMeshes = new Mesh[0];
        [HideInInspector] public LTLODPlan[] lodPlans = new LTLODPlan[0];
        [System.NonSerialized] public int currentLOD;
        MeshFilter cachedFilter;
        public void ShowLOD(int level)
        {
            if(!cachedFilter)cachedFilter=GetComponent<MeshFilter>();
            if(!cachedFilter)return;
            level=Mathf.Clamp(level,0,lodMeshes==null?0:lodMeshes.Length);
            Mesh selected=level==0?mesh:lodMeshes[level-1];
            if(!selected){level=0;selected=mesh;}
            if(cachedFilter.sharedMesh!=selected)cachedFilter.sharedMesh=selected;
            currentLOD=level;
        }
        [HideInInspector] public System.Collections.Generic.List<Vector3Int> sourceLeaves = new System.Collections.Generic.List<Vector3Int>();
        [HideInInspector] public System.Collections.Generic.List<Vector3Int> renderLeaves = new System.Collections.Generic.List<Vector3Int>();
        [HideInInspector] public System.Collections.Generic.List<int> borderStitches = new System.Collections.Generic.List<int>();
        [System.NonSerialized] public double updatedAt;
        void OnDrawGizmos()
        {
#if UNITY_EDITOR
            var world = GetComponentInParent<LTWorld>();
            if (!world || !world.showDebug || !mesh) return;
            Gizmos.color = UnityEditor.EditorApplication.timeSinceStartup - updatedAt < 1.5
                ? Color.green : new Color(0.3f, 0.6f, 1, 0.3f);
            var old = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(mesh.bounds.center, mesh.bounds.size);
            Gizmos.matrix = old;
#endif
        }
    }
}
