using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Enumerates only camera neighbourhoods, never the complete world grid.
    public static class LTDetailStreaming
    {
        public struct View
        {
            public Vector2 position;
            public Plane[] planes; // World-space frustum, including orthographic cameras.
        }
        struct Priority
        {
            public bool resident;
            public int tier;
            public float distance;
        }
        // Pure plane/AABB test so the queue policy can be exercised outside the Unity player.
        public static bool InView(Plane[] planes,Bounds bounds)
        {
            if(planes==null||planes.Length==0)return true;
            foreach(var plane in planes)
            {
                var n=plane.normal;var e=bounds.extents;
                float radius=Mathf.Abs(n.x)*e.x+Mathf.Abs(n.y)*e.y+Mathf.Abs(n.z)*e.z;
                if(Vector3.Dot(n,bounds.center)+plane.distance+radius<0)return false;
            }
            return true;
        }
        // Reorders only: residency radius, candidate identities and the render culling are unchanged.
        public static void Prioritize(List<Vector2Int> plan,List<View> views,Vector2 worldSize,int size,
            float drawDistance,Func<Vector2Int,Bounds> boundsForCell,Func<Vector2Int,bool> isResident)
        {
            var priorities=new Dictionary<Vector2Int,Priority>(plan.Count);
            foreach(var key in plan)
            {
                var rect=CellRect(key,size,worldSize);var bounds=boundsForCell(key);
                var priority=new Priority{resident=isResident(key),tier=2,distance=float.PositiveInfinity};
                foreach(var view in views)
                {
                    float distance=DistanceSquared(view.position,rect);
                    int tier=distance>drawDistance*drawDistance?2:InView(view.planes,bounds)?0:1;
                    // Distance belongs to the camera that supplied the winning tier.
                    if(tier<priority.tier||(tier==priority.tier&&distance<priority.distance))
                    {priority.tier=tier;priority.distance=distance;}
                }
                priorities.Add(key,priority);
            }
            plan.Sort((a,b)=>{
                var pa=priorities[a];var pb=priorities[b];
                int order=pa.resident.CompareTo(pb.resident); // Fill missing cells before refreshing cached cells.
                if(order==0)order=pa.tier.CompareTo(pb.tier);
                if(order==0)order=pa.distance.CompareTo(pb.distance);
                return order!=0?order:a.y!=b.y?a.y.CompareTo(b.y):a.x.CompareTo(b.x);
            });
        }
        public static float DistanceSquared(Vector2 point,Rect rect)
        {
            float x=Mathf.Max(rect.xMin-point.x,Mathf.Max(0,point.x-rect.xMax));
            float z=Mathf.Max(rect.yMin-point.y,Mathf.Max(0,point.y-rect.yMax));
            return x*x+z*z;
        }
        public static Rect CellRect(Vector2Int key,int size,Vector2 worldSize)
            =>Rect.MinMaxRect(key.x*size,key.y*size,Mathf.Min((key.x+1)*size,worldSize.x),Mathf.Min((key.y+1)*size,worldSize.y));
        public static bool Plan(List<Vector2> cameras,Vector2 worldSize,int size,float radius,int limit,List<Vector2Int> result)
        {
            result.Clear();
            var distances=new Dictionary<Vector2Int,float>();
            int nx=Mathf.CeilToInt(worldSize.x/size),nz=Mathf.CeilToInt(worldSize.y/size);
            foreach(var camera in cameras)
            {
                int x0=Mathf.Max(0,Mathf.FloorToInt((camera.x-radius)/size)),x1=Mathf.Min(nx-1,Mathf.FloorToInt((camera.x+radius)/size));
                int z0=Mathf.Max(0,Mathf.FloorToInt((camera.y-radius)/size)),z1=Mathf.Min(nz-1,Mathf.FloorToInt((camera.y+radius)/size));
                for(int z=z0;z<=z1;z++)for(int x=x0;x<=x1;x++)
                {
                    var key=new Vector2Int(x,z);float distance=DistanceSquared(camera,CellRect(key,size,worldSize));
                    if(distance>radius*radius)continue;
                    if(!distances.TryGetValue(key,out float previous)||distance<previous)distances[key]=distance;
                    if(distances.Count>limit)return false;
                }
            }
            result.AddRange(distances.Keys);
            result.Sort((a,b)=>{
                int order=distances[a].CompareTo(distances[b]);
                return order!=0?order:a.y!=b.y?a.y.CompareTo(b.y):a.x.CompareTo(b.x);
            });
            return true;
        }
        public static bool Expired(bool wanted,double now,double lastWanted,float delay)=>!wanted&&now-lastWanted>=delay;
    }
}
