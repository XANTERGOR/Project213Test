using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Runtime-only, opt-in via F8; exact restoration on F7/F8/disable/reload.
    internal sealed class LTTerrainFramePacing : IDisposable
    {
        public bool Enabled {get;private set;}
        public int OriginalVSync {get;private set;}
        public int OriginalTargetFPS {get;private set;}
        public void Enable()
        {
            if(Enabled)return;
            OriginalVSync=QualitySettings.vSyncCount;OriginalTargetFPS=Application.targetFrameRate;
            Enabled=true;QualitySettings.vSyncCount=0;Application.targetFrameRate=-1;
        }
        public void Dispose()
        {
            if(!Enabled)return;
            QualitySettings.vSyncCount=OriginalVSync;Application.targetFrameRate=OriginalTargetFPS;Enabled=false;
        }
    }
}
