using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Enumerates only camera neighbourhoods, never the complete world grid.
    public static class LTDetailStreaming
    {
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
