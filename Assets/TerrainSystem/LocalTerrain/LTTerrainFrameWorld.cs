using System;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTWorld
    {
        // Separate from serialized forceLOD, which also forces roads and rocks.
        [NonSerialized] int frameDiagnosticLOD=-1;
        internal int FrameDiagnosticLOD=>frameDiagnosticLOD;
        internal int TerrainForcedLOD=>frameDiagnosticLOD>=0?frameDiagnosticLOD:forceLOD;
        internal void SetFrameDiagnosticLOD(int level)
        {
            if(frameDiagnosticLOD==level)return;
            frameDiagnosticLOD=level;
            lodCameraSchedule.Invalidate();
            if(isActiveAndEnabled)UpdateLOD(ResolveLODCamera(),false);
        }
        internal string FrameDiagnosticLODStatus(LTChunk chunk,int level)
        {
            if(level<0)return null;
            if(frameDiagnosticLOD!=level)return "terrain override changed";
            if(!enableLODs)return "terrain LOD disabled";
            if(chunk.lodsPending)return "terrain LOD bake pending";
            // Preserve the existing displacement-grid and seam constraints.
            if(IsRegularMaskGridChunk(chunk.x,chunk.z))return null;
            if(UseSpatialLODs)
            {
                if(spatialFailed||spatialTopology==null)return spatialLODStatus??"spatial LOD unavailable";
                var asset=chunk.ActiveSpatialLOD;
                if(!asset||!asset.vertexBank||asset.patches==null||asset.patches.Length==0)return "spatial LOD asset missing";
                int id=ChunkId(chunk.x,chunk.z);
                for(int p=0;p<asset.patches.Length;p++)
                    if(asset.patches[p]?.levels==null||asset.patches[p].levels.Length<=level||spatialTopology.Requested(id,p)!=level)
                        return "requested spatial LOD not prepared/applied";
            }
            else if(chunk.lodMeshes==null||chunk.lodMeshes.Length<level||!chunk.lodMeshes[level-1]||chunk.currentLOD!=level)
                return "requested whole-chunk LOD not prepared/applied";
            return null;
        }
    }
}
