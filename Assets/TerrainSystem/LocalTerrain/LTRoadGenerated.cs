using UnityEngine;

namespace LocalTerrainPrototype
{
    /// <summary>Explicit ownership for generated road output; authored scene objects must not be toggled.</summary>
    [DisallowMultipleComponent]
    public sealed class LTRoadGenerated : MonoBehaviour
    {
        public LTRoad owner;
        public string bakeId;
    }
}
