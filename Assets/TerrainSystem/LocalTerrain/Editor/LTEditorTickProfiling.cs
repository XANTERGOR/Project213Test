using Unity.Profiling;

namespace LocalTerrainPrototype
{
    public static partial class LTEditorEngine
    {
        // Coarse scopes only: no per-chunk/vertex/plant marker overhead.
        static readonly ProfilerMarker pollMarker=new ProfilerMarker("LT.Editor.Poll");
        static readonly ProfilerMarker worldScanMarker=new ProfilerMarker("LT.Editor.WorldScan");
        static readonly ProfilerMarker pollLODMarker=new ProfilerMarker("LT.Editor.PollLOD");
        static readonly ProfilerMarker reloadResumeMarker=new ProfilerMarker("LT.Editor.ReloadResume");
        static readonly ProfilerMarker chunkLayoutMarker=new ProfilerMarker("LT.Editor.ChunkLayout");
        static readonly ProfilerMarker buildAdvanceMarker=new ProfilerMarker("LT.Editor.BuildAdvance");
        static readonly ProfilerMarker coarseAdvanceMarker=new ProfilerMarker("LT.Editor.CoarseAdvance");
        static readonly ProfilerMarker paintPollMarker=new ProfilerMarker("LT.Editor.PaintPoll");
        static readonly ProfilerMarker colliderPollMarker=new ProfilerMarker("LT.Editor.Colliders");
        static readonly ProfilerMarker seamPollMarker=new ProfilerMarker("LT.Editor.SeamBake");
        static readonly ProfilerMarker captureRoadMarker=new ProfilerMarker("LT.Editor.Capture.Road");
        static readonly ProfilerMarker captureJunctionMarker=new ProfilerMarker("LT.Editor.Capture.Junction");
        static readonly ProfilerMarker captureHeightMarker=new ProfilerMarker("LT.Editor.Capture.Height");
        static readonly ProfilerMarker captureMeshMarker=new ProfilerMarker("LT.Editor.Capture.Mesh");
    }
}
