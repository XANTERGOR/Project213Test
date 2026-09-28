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
        [HideInInspector] public LTSpatialLODAsset spatialLOD;
        Mesh spatialMesh;
        LTSpatialLODAsset activeSpatial;
        int spatialRevision;
        readonly System.Collections.Generic.List<int> spatialIndices=new System.Collections.Generic.List<int>();
        MeshFilter cachedFilter;
        public void ShowSpatialLOD(LTSpatialLODTopology topology,int chunk,bool dirty)
        {
            if(!spatialLOD||!spatialLOD.vertexBank||spatialLOD.formatVersion!=LTSpatialLODMath.Version){ShowLOD(0);return;}
            if(!cachedFilter)cachedFilter=GetComponent<MeshFilter>();
            if(!cachedFilter)return;
            bool changed=!spatialMesh||activeSpatial!=spatialLOD||spatialRevision!=spatialLOD.revision;
            if(changed)
            {
                ReleaseSpatialLOD();activeSpatial=spatialLOD;spatialRevision=spatialLOD.revision;
                spatialMesh=Instantiate(spatialLOD.vertexBank);spatialMesh.name=name+" Spatial LOD (runtime)";
                spatialMesh.hideFlags=HideFlags.HideAndDontSave;spatialMesh.MarkDynamic();
                int capacity=(int)spatialMesh.GetIndexCount(0);
                if(spatialIndices.Capacity<capacity)spatialIndices.Capacity=capacity;
            }
            // Refill a preview clone after scene save, even with an unchanged camera.
            if(changed||dirty)
            {
                topology.WriteIndices(chunk,spatialIndices);
                spatialMesh.SetIndices(spatialIndices,MeshTopology.Triangles,0,false);
            }
            if(cachedFilter.sharedMesh!=spatialMesh)cachedFilter.sharedMesh=spatialMesh;currentLOD=0;
        }
        public void ReleaseSpatialLOD()
        {
            if(cachedFilter&&cachedFilter.sharedMesh==spatialMesh)cachedFilter.sharedMesh=mesh;
            if(spatialMesh){if(Application.isPlaying)Destroy(spatialMesh);else DestroyImmediate(spatialMesh);}
            spatialMesh=null;activeSpatial=null;
        }
        void OnDisable(){ReleaseSpatialLOD();}
        void OnDestroy(){ReleaseSpatialLOD();}
        public void ShowLOD(int level)
        {
            if(spatialMesh)ReleaseSpatialLOD();
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
