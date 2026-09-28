using UnityEngine;
namespace LocalTerrainPrototype
{
    public sealed class LTRoadLOD : MonoBehaviour
    {
        public LTRoad owner;
        public LTRoadJunction junction;
        public Mesh[] meshes;
        public Bounds bounds;
        [System.NonSerialized] public int current;
        MeshFilter filter;
        public void Show(int level)
        {
            if(meshes==null||meshes.Length==0)return;
            if(!filter)filter=GetComponent<MeshFilter>();if(!filter)return;
            level=Mathf.Clamp(level,0,meshes.Length-1);
            if(!meshes[level])level=0;
            if(filter.sharedMesh!=meshes[level])filter.sharedMesh=meshes[level];current=level;
        }
    }
}
