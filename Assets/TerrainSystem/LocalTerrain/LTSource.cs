using UnityEngine;
namespace LocalTerrainPrototype
{
    public sealed class LTSource : ScriptableObject
    {
        public Vector3 size = new Vector3(192, 100, 192);
        public int resolution = 2;
        public float[] heights = new float[4]; // metres, row-major z * resolution + x
        public float Sample(float x, float z)
        {
            float fx = Mathf.Clamp01(x / size.x) * (resolution - 1);
            float fz = Mathf.Clamp01(z / size.z) * (resolution - 1);
            int ix = Mathf.Min(Mathf.FloorToInt(fx), resolution - 2);
            int iz = Mathf.Min(Mathf.FloorToInt(fz), resolution - 2);
            float tx = fx - ix, tz = fz - iz;
            return Mathf.Lerp(Mathf.Lerp(heights[iz * resolution + ix], heights[iz * resolution + ix + 1], tx),
                Mathf.Lerp(heights[(iz + 1) * resolution + ix], heights[(iz + 1) * resolution + ix + 1], tx), tz);
        }
    }
}
