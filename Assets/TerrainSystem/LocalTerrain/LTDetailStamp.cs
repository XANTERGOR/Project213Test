using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    [DisallowMultipleComponent, AddComponentMenu("Local Terrain/Detail Stamp")]
    public sealed class LTDetailStamp : MonoBehaviour
    {
        [SerializeField,HideInInspector] string id;
        public string Id=>id;
        [Tooltip("Мир террейна, на детализацию которого действует штамп. Материалы и покраска не изменяются.")]
        public LTWorld world;
        [Tooltip("Форма области в локальной плоскости XZ: Ellipse — эллипс, Rectangle — прямоугольник.")]
        public LTStampShape shape=LTStampShape.Ellipse;
        [Tooltip("Полный размер области по локальным X/Z в метрах при масштабе Transform = 1. Учитываются поворот и масштаб штампа.")]
        public Vector2 size=new Vector2(10,10);
        [Range(0,1), Tooltip("Доля области с мягким переходом к краю: 0 — жёсткая граница, 1 — плавное ослабление от центра.")]
        public float edgeFalloff=.2f;
        [Tooltip("Разрешить действие на всех слоях. Если выключено, используются только Allowed Layers.")]
        public bool allLayers;
        [Tooltip("Слои, на которых разрешено действие штампа. Пустой список при выключенном All Layers означает отсутствие действия.")]
        public List<LTSurfaceLayer> allowedLayers=new List<LTSurfaceLayer>();
        [Tooltip("Какие категории детализации переопределять. Например, Vegetation позволяет убрать траву, не затрагивая камни.")]
        public LTDetailCategory categories=LTDetailCategory.Vegetation;
        [Tooltip("Add — добавить набор к существующему; Replace — заменить выбранные категории своим набором; Remove — убрать выбранные категории в области.")]
        public LTDetailStampMode mode=LTDetailStampMode.Replace;
        [Min(0), Tooltip("Множитель плотности добавляемого набора: 1 — исходная, 2 — вдвое выше, 0 — без добавления. В Remove не используется.")]
        public float densityMultiplier=1;
        [Tooltip("Число для воспроизводимой случайной расстановки. Измените его, чтобы получить другой вариант внутри этого штампа.")]
        public int seed;
        [Tooltip("Порядок при пересечении штампов: больший приоритет применяется позже. При равенстве используется постоянный ID.")]
        public int priority;
        [Tooltip("Общая маска собственного набора Add/Replace. Не меняет область удаления или замены.")]
        public LTDetailDensityMask detailDensityMask=new LTDetailDensityMask();
        [Tooltip("Собственный набор префабов для Add/Replace. В Remove не используется.")]
        public List<LTDetailEntry> details=new List<LTDetailEntry>();

        // Pure area mask; the generator also applies final allowed-layer weights.
        public float AreaWeight(Vector3 positionWS)
        {
            var p=transform.InverseTransformPoint(positionWS);
            return LTDetailMath.AreaWeight(p.x,p.z,size.x,size.y,shape==LTStampShape.Ellipse,edgeFalloff);
        }
        void Reset(){world=GetComponentInParent<LTWorld>();}
        void OnValidate()
        {
            if(detailDensityMask==null)detailDensityMask=new LTDetailDensityMask();
            detailDensityMask.Validate();
            if(string.IsNullOrEmpty(id))id=Guid.NewGuid().ToString("N");
            size.x=Mathf.Max(.01f,size.x);size.y=Mathf.Max(.01f,size.y);
            edgeFalloff=Mathf.Clamp01(edgeFalloff);densityMultiplier=Mathf.Max(0,densityMultiplier);
            LTDetailEntry.ValidateAll(details);
        }
#if UNITY_EDITOR
        // Inspector uses this for an explicitly detected duplicate, with Undo.
        public void RegenerateIdentity(){id=Guid.NewGuid().ToString("N");}
#endif
        void OnDrawGizmosSelected()
        {
            var matrix=Gizmos.matrix;var color=Gizmos.color;
            Gizmos.matrix=transform.localToWorldMatrix;Gizmos.color=new Color(.25f,1,.35f);
            if(shape==LTStampShape.Rectangle)Gizmos.DrawWireCube(Vector3.zero,new Vector3(size.x,.05f,size.y));
            else for(int i=0;i<64;i++)
            {
                float a=i*Mathf.PI/32,b=(i+1)*Mathf.PI/32;
                Gizmos.DrawLine(new Vector3(Mathf.Cos(a)*size.x*.5f,0,Mathf.Sin(a)*size.y*.5f),
                    new Vector3(Mathf.Cos(b)*size.x*.5f,0,Mathf.Sin(b)*size.y*.5f));
            }
            Gizmos.matrix=matrix;Gizmos.color=color;
        }
    }
}
