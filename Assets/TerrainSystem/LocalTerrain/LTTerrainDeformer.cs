using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(Collider))]
    [AddComponentMenu("Local Terrain/Terrain Deformer")]
    public sealed class LTTerrainDeformer : MonoBehaviour
    {
        internal static readonly HashSet<LTTerrainDeformer> Active=new HashSet<LTTerrainDeformer>();
        [Tooltip("Мир, в котором оставлять след. Назначьте явно, чтобы не затрагивать другие миры.")]
        public LTWorld world;
        public Collider contactCollider;
        [Min(0), Tooltip("Скорость продавливания, метров в секунду. Это визуальный параметр, не сила Rigidbody.")]
        public float pressure=.3f;
        [Min(.001f), Tooltip("Допуск контакта по высоте, м. Не расширяет отпечаток в стороны.")]
        public float contactDistance=.08f;
        [Min(.001f), Tooltip("Мягкость края отпечатка по горизонтали, м. Независима от допуска контакта по высоте.")]
        public float edgeSoftness=.01f;
        [Tooltip("Временно разрешить следы вне Play Mode. Следы не сохраняются в сцену.")]
        public bool previewInEditor;
        [Min(.01f), Tooltip("Ширина отпечатка WheelCollider, м.")]
        public float wheelWidth=.25f;
        [Min(.01f), Tooltip("Длина контактного пятна WheelCollider, м.")]
        public float wheelContactLength=.3f;
        [Range(0,.5f), Tooltip("На сколько секунд заранее уплотнять сетку по скорости; глубина вперёд не записывается.")]
        public float predictionSeconds=.2f;
        bool SupportedCollider()=>contactCollider is BoxCollider||contactCollider is SphereCollider||
            contactCollider is CapsuleCollider||contactCollider is WheelCollider||(contactCollider is MeshCollider mesh&&mesh.convex);
        void Reset(){contactCollider=GetComponent<Collider>();world=GetComponentInParent<LTWorld>();}
        void OnEnable(){if(!contactCollider)contactCollider=GetComponent<Collider>();Active.Add(this);}
        void OnDisable(){Active.Remove(this);}
        internal bool CanPress(LTWorld target)=>isActiveAndEnabled&&world==target&&
            (Application.isPlaying||previewInEditor)&&pressure>0&&contactCollider&&contactCollider.enabled&&
            contactCollider.gameObject.activeInHierarchy&&
            SupportedCollider();
        void OnValidate()
        {
            pressure=Mathf.Max(0,pressure);contactDistance=Mathf.Max(.001f,contactDistance);
            edgeSoftness=Mathf.Max(.001f,edgeSoftness);
            if(contactCollider&&!SupportedCollider())
                Debug.LogWarning("Terrain Deformer: нужен Box/Sphere/Capsule/Wheel Collider или convex MeshCollider.",this);
        }
    }
}
