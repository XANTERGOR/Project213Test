using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Scans/counts only at mode/capture boundaries, NEVER in the measured Update.
    // Uses index counts, not Mesh.triangles/vertices readback or mesh replacement.
    internal sealed class LTTerrainFrameGeometry : IDisposable
    {
        readonly Dictionary<LTWorld,int> originals=new Dictionary<LTWorld,int>();
        int level=-1;
        public static int Level(LTTerrainDiagnosticMode mode)=>mode==LTTerrainDiagnosticMode.TerrainLOD2?2:mode==LTTerrainDiagnosticMode.TerrainLOD4?4:-1;
        public void SetMode(LTTerrainDiagnosticMode mode)
        {
            int next=Level(mode);
            if(next<0){Dispose();return;}
            level=next;Refresh();
        }
        void Refresh()
        {
            foreach(var world in UnityEngine.Object.FindObjectsByType<LTWorld>(FindObjectsInactive.Exclude,FindObjectsSortMode.None))
            {
                if(!world.isActiveAndEnabled||!world.generatedRoot)continue;
                if(!originals.ContainsKey(world))originals.Add(world,world.FrameDiagnosticLOD);
                world.SetFrameDiagnosticLOD(level);
            }
        }
        public void Dispose()
        {
            foreach(var entry in originals)if(entry.Key)
            {
                // The world restores its override BEFORE applying the policy.
                // A broken mesh must not prevent other worlds/caps from restoring.
                try{entry.Key.SetFrameDiagnosticLOD(entry.Value);}
                catch(Exception error){Debug.LogWarning("Terrain diagnostic: LOD policy restored, but applying meshes failed: "+error.Message);}
            }
            originals.Clear();level=-1;
        }
        internal sealed class Snapshot
        {
            public long triangles,vertices,frustumTriangles;
            public int chunks,frustumChunks,pinnedChunks,worlds,eligible;
            public string policy,error;
            public ulong fingerprint=14695981039346656037UL;
            public void Hash(int value){unchecked{fingerprint=(fingerprint^(uint)value)*1099511628211UL;}}
            public string Brief=>$"terrain tris: {triangles:N0}; frustum candidates: {frustumTriangles:N0}";
            public override string ToString()=>$"terrainRequestedLOD={levelText}; assignedChunks={chunks}; assignedTriangles={triangles}; vertexBankVertices={vertices}; frustumCandidateChunks={frustumChunks}; frustumCandidateTriangles={frustumTriangles}; pinnedLOD0Chunks={pinnedChunks}; worlds={worlds}; {policy}";
            internal string levelText;
        }
        public Snapshot Read(Camera camera,bool prepare=false)
        {
            if(prepare&&level>=0)Refresh();
            var snapshot=new Snapshot{levelText=level<0?"original policy":level.ToString()};
            var planes=GeometryUtility.CalculateFrustumPlanes(camera);
            var policy=new StringBuilder();
            var worlds=UnityEngine.Object.FindObjectsByType<LTWorld>(FindObjectsInactive.Exclude,FindObjectsSortMode.None);
            // Stable hash independent of Unity's unspecified enumeration order.
            Array.Sort(worlds,(a,b)=>a.GetInstanceID().CompareTo(b.GetInstanceID()));
            foreach(var world in worlds)
            {
                if(!world.isActiveAndEnabled||!world.generatedRoot)continue;
                snapshot.worlds++;snapshot.Hash(world.GetInstanceID());snapshot.Hash(world.forceLOD);
                policy.Append($"world={world.name}, LODenabled={world.enableLODs}, spatial={world.UseSpatialLODs}, originalForceLOD={world.forceLOD}; ");
                var chunks=world.generatedRoot.GetComponentsInChildren<LTChunk>();
                Array.Sort(chunks,(a,b)=>a.GetInstanceID().CompareTo(b.GetInstanceID()));
                foreach(var chunk in chunks)
                {
                    var filter=chunk.GetComponent<MeshFilter>();var renderer=chunk.GetComponent<MeshRenderer>();
                    if(!renderer||!renderer.enabled||renderer.forceRenderingOff)continue;
                    var mesh=filter?filter.sharedMesh:null;
                    if(!mesh){snapshot.error=$"{world.name}/{chunk.name}: terrain mesh missing";continue;}
                    string problem=world.FrameDiagnosticLODStatus(chunk,level);
                    if(problem!=null)snapshot.error=$"{world.name}/{chunk.name}: {problem}";
                    if(world.IsRegularMaskGridChunk(chunk.x,chunk.z))snapshot.pinnedChunks++;else snapshot.eligible++;
                    long triangles=0;
                    for(int sub=0;sub<mesh.subMeshCount;sub++)if(mesh.GetTopology(sub)==MeshTopology.Triangles)triangles+=mesh.GetIndexCount(sub)/3;
                    snapshot.Hash(chunk.GetInstanceID());snapshot.Hash(mesh.GetInstanceID());snapshot.Hash((int)triangles);snapshot.Hash(mesh.vertexCount);
                    snapshot.triangles+=triangles;snapshot.vertices+=mesh.vertexCount;snapshot.chunks++;
                    if((camera.cullingMask&(1<<chunk.gameObject.layer))!=0&&GeometryUtility.TestPlanesAABB(planes,renderer.bounds))
                    {snapshot.frustumTriangles+=triangles;snapshot.frustumChunks++;}
                }
            }
            snapshot.policy=policy.ToString();
            if(snapshot.chunks==0)snapshot.error="No active terrain chunk meshes";
            else if(level>=0&&snapshot.eligible==0)snapshot.error="All terrain chunks pinned to LOD0; geometry test not applicable";
            return snapshot;
        }
    }
}
