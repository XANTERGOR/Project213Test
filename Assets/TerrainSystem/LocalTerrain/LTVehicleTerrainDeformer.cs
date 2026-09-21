using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    [DisallowMultipleComponent, AddComponentMenu("Local Terrain/Vehicle Terrain Deformer")]
    public sealed class LTVehicleTerrainDeformer : MonoBehaviour
    {
        public LTWorld world;
        [Tooltip("Пустой список: автоматически найти WheelCollider в дочерних объектах.")]
        public WheelCollider[] wheels;
        [Min(0)] public float pressure=.3f;
        [Min(.01f)] public float wheelWidth=.25f;
        [Min(.01f)] public float contactLength=.3f;
        [Min(.001f)] public float contactDistance=.08f;
        [Min(.001f)] public float edgeSoftness=.01f;
        [Range(0,.5f)] public float predictionSeconds=.2f;
        readonly List<LTTerrainDeformer> owned=new List<LTTerrainDeformer>();
        void OnEnable()
        {
            if(!Application.isPlaying)return;
            if(owned.Count>0)
            {
                Configure();foreach(var d in owned)if(d)d.enabled=true;
                return;
            }
            var selected=wheels!=null&&wheels.Length>0?wheels:GetComponentsInChildren<WheelCollider>(true);
            foreach(var wheel in selected)
            {
                if(!wheel)continue;
                // Never reconfigure a user-owned deformer.
                if(wheel.TryGetComponent<LTTerrainDeformer>(out _))continue;
                var d=wheel.gameObject.AddComponent<LTTerrainDeformer>();
                d.hideFlags=HideFlags.DontSave;
                d.contactCollider=wheel;owned.Add(d);
            }
            Configure();
        }
        void Configure()
        {
            foreach(var d in owned)if(d)
            {
                d.world=world;d.pressure=Mathf.Max(0,pressure);
                d.wheelWidth=Mathf.Max(.01f,wheelWidth);d.wheelContactLength=Mathf.Max(.01f,contactLength);
                d.contactDistance=Mathf.Max(.001f,contactDistance);d.edgeSoftness=Mathf.Max(.001f,edgeSoftness);
                d.predictionSeconds=Mathf.Clamp(predictionSeconds,0,.5f);
            }
        }
        void OnValidate(){if(Application.isPlaying)Configure();}
        void OnDisable()
        {
            // Keep owned components disabled for safe same-frame re-enable.
            foreach(var d in owned)if(d)d.enabled=false;
        }
        void OnDestroy()
        {
            foreach(var d in owned)if(d){d.enabled=false;Destroy(d);}
            owned.Clear();
        }
    }
}
