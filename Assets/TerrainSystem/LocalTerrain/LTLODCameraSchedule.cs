using UnityEngine;

namespace LocalTerrainPrototype
{
    // Visual LOD follows the selected camera before rendering. Keep the old low
    // frequency polling for stationary views, inspector changes and non-SRP use.
    // No frameCount guard: Scene view repaints and LateUpdate camera moves can
    // happen after an earlier update with the same Unity frame number.
    public sealed class LTLODCameraSchedule
    {
        const double PollInterval=.1;
        bool valid;
        int cameraId;
        Vector3 position;
        double nextPoll;

        public static bool IsRenderCamera(CameraType type)=>type==CameraType.Game||type==CameraType.SceneView;
        public bool PollDue(double now)=>!valid||now>=nextPoll;
        public void Invalidate(){valid=false;nextPoll=0;}

        public bool TryBegin(double now,int cameraId,Vector3 position,bool beforeRender)
        {
            // Use exact position equality, not Vector3's epsilon comparison, so
            // a small move across a LOD threshold is not suppressed.
            bool moved=!valid||this.cameraId!=cameraId||!this.position.Equals(position);
            if(!PollDue(now)&&(!beforeRender||!moved))return false;
            valid=true;this.cameraId=cameraId;this.position=position;nextPoll=now+PollInterval;
            return true;
        }
    }
}
