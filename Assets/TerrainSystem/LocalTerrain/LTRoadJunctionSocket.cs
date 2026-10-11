using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Place on a child of the authored junction prefab, outside its LOD hierarchies.
    [DisallowMultipleComponent, AddComponentMenu("Local Terrain/Road Junction Socket")]
    public sealed class LTRoadJunctionSocket : MonoBehaviour
    {
        [Min(.1f)] public float width=4;

        public Matrix4x4 InPrefab(GameObject prefab)
        {
            if(!prefab||transform==prefab.transform||!transform.IsChildOf(prefab.transform))
                throw new ArgumentException("Въезд должен быть дочерним маркером назначенного префаба перекрёстка.");
            var m=prefab.transform.worldToLocalMatrix*transform.localToWorldMatrix;
            var p=m.MultiplyPoint3x4(Vector3.zero);var x=m.MultiplyVector(Vector3.right);
            var y=m.MultiplyVector(Vector3.up);var z=m.MultiplyVector(Vector3.forward);
            bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
            if(!Finite(width)||width<=0||!Finite(p.x)||!Finite(p.y)||!Finite(p.z)||
                !Finite(m.determinant)||Mathf.Abs(m.determinant-1)>.001f||
                Mathf.Abs(x.sqrMagnitude-1)>.001f||Mathf.Abs(z.sqrMagnitude-1)>.001f||
                (y-Vector3.up).sqrMagnitude>1e-6f||Mathf.Abs(Vector3.Dot(x,z))>.001f||Mathf.Abs(p.y)>.001f)
                throw new ArgumentException(name+": первая версия требует Y=0 относительно корня, Scale=1, зелёную Y вверх и синюю Z горизонтально наружу.");
            return m;
        }
    }
}
