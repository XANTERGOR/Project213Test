using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    static void MeshStampCacheChecks()
    {
        int checks=0;
        void Check(bool ok,string message){checks++;Require(ok,"mesh stamp cache: "+message);}
        var random=new System.Random(49071);
        // An independent copy of the old capture algorithm, including coincident
        // FBX vertices, exact ordering, the downward-face test and 1 mm weld grid.
        (Vector3[] vertices,int[] contact,int[] edges,Vector3 min,Vector3 max) Legacy(Vector3[] p,int[] t,Matrix4x4 m)
        {
            var v=p.Select(x=>m.MultiplyPoint3x4(x)).ToArray();
            var min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue);var max=new Vector3(float.MinValue,float.MinValue,float.MinValue);
            foreach(var x in v){min=Vector3.Min(min,x);max=Vector3.Max(max,x);}
            var welded=new Dictionary<Vector3Int,int>();var ids=new int[v.Length];
            for(int i=0;i<v.Length;i++)
            {
                var key=new Vector3Int(Mathf.RoundToInt(v[i].x*1000),Mathf.RoundToInt(v[i].y*1000),Mathf.RoundToInt(v[i].z*1000));
                if(!welded.ContainsKey(key))welded.Add(key,welded.Count);ids[i]=welded[key];
            }
            var faces=new List<int>();var counts=new Dictionary<long,int>();var ends=new Dictionary<long,(int,int)>();
            for(int i=0;i+2<t.Length;i+=3)
            {
                int a=t[i],b=t[i+1],c=t[i+2];if(Vector3.Cross(v[b]-v[a],v[c]-v[a]).y>=-.000001f)continue;
                faces.AddRange(new[]{a,b,c});
                foreach(var edge in new[]{(a,b),(b,c),(c,a)})
                {
                    int x=ids[edge.Item1],y=ids[edge.Item2];long key=((long)Math.Min(x,y)<<32)|(uint)Math.Max(x,y);
                    counts[key]=counts.TryGetValue(key,out int n)?n+1:1;ends[key]=edge;
                }
            }
            var boundary=new List<int>();foreach(var e in counts)if(e.Value==1){boundary.Add(ends[e.Key].Item1);boundary.Add(ends[e.Key].Item2);}
            return(v,faces.ToArray(),boundary.ToArray(),min,max);
        }
        for(int run=0;run<200;run++)
        {
            var p=Enumerable.Range(0,30).Select(i=>new Vector3((float)random.NextDouble()*12-6,(float)random.NextDouble()*8-4,(float)random.NextDouble()*12-6)).ToArray();
            p[29]=p[0];p[28]=p[1];p[27]=p[2]; // split vertices at shading/UV seams
            var t=Enumerable.Range(0,180).Select(_=>random.Next(p.Length)).ToArray();
            var m=Matrix4x4.identity;m.m00=run%2==0?-1.5f:1.5f;m.m11=.8f;m.m22=2;m.m02=.3f;m.m10=-.2f;m.m03=run*.137f;m.m13=-5;m.m23=97;
            var expected=Legacy(p,t,m);var actual=LTMeshStampGeometry.Build(p,t,m);
            Check(actual.vertices.SequenceEqual(expected.vertices),"identical transformed positions");
            Check(actual.contactTriangles.SequenceEqual(expected.contact),"identical contact triangle order");
            Check(actual.boundaryEdges.SequenceEqual(expected.edges),"identical boundary edge order");
            Check(actual.triangles.SequenceEqual(t)&&actual.min.Equals(expected.min)&&actual.max.Equals(expected.max),"indices and bounds unchanged");
            Check(actual.SameSource(p,t),"exact audit of original data");
            var changed=(Vector3[])p.Clone();changed[0].x+=.00001f;
            Check(!actual.SameSource(changed,t),"small in-place vertex edit is not lost to approximate equality");
            var changedIndices=(int[])t.Clone();changedIndices[0]=(changedIndices[0]+1)%p.Length;
            Check(!actual.SameSource(p,changedIndices),"same-count topology edit detected");
            Check(actual.contentHash!=LTMeshStampGeometry.Build(changed,t,m).contentHash,"unannounced vertex edit changes stamp signature");
            Check(actual.contentHash!=LTMeshStampGeometry.Build(p,changedIndices,m).contentHash,"unannounced topology edit changes signature");
            p[0]=new Vector3(999,999,999);t[0]=999;
            Check(actual.triangles[0]!=999&&!actual.SameSource(p,t),"input arrays cannot mutate published snapshots");
        }
        var empty=LTMeshStampGeometry.Build(Array.Empty<Vector3>(),Array.Empty<int>(),Matrix4x4.identity);
        Check(empty.vertices.Length==0&&empty.boundaryEdges.Length==0,"empty source preserves legacy output");
        var source=new[]{Vector3.zero,Vector3.right,Vector3.forward};var indices=new[]{0,1,2};
        var data=LTMeshStampGeometry.Build(source,indices,Matrix4x4.identity);
        var key=new LTMeshStampGeometryKey(11,5,3,1,3,Matrix4x4.identity,"asset-a");
        var cache=new LTMeshStampGeometryCache();cache.Begin();
        Check(cache.Lookup(1,key,0,out _)==LTMeshStampGeometryLookup.Miss,"cold cache misses");cache.Store(1,key,data,0);
        Check(cache.Lookup(1,key,.1,out var hot)==LTMeshStampGeometryLookup.Hit&&ReferenceEquals(hot,data),"warm cache shares geometry");
        Check(cache.Lookup(2,key,.1,out _)==LTMeshStampGeometryLookup.Miss,"different owner does not inherit another transform snapshot");
        Check(cache.Lookup(1,key,1,out _)==LTMeshStampGeometryLookup.Audit,"periodic content guard");cache.ConfirmAudit(1,1);
        Check(cache.Lookup(1,key,1.1,out _)==LTMeshStampGeometryLookup.Hit,"confirmed audit reuses same geometry");
        var shifted=Matrix4x4.identity;shifted.m03=1e-7f;
        var alternatives=new[]{
            new LTMeshStampGeometryKey(12,5,3,1,3,key.matrix,key.asset),new LTMeshStampGeometryKey(11,6,3,1,3,key.matrix,key.asset),
            new LTMeshStampGeometryKey(11,5,4,1,3,key.matrix,key.asset),new LTMeshStampGeometryKey(11,5,3,2,3,key.matrix,key.asset),
            new LTMeshStampGeometryKey(11,5,3,1,6,key.matrix,key.asset),new LTMeshStampGeometryKey(11,5,3,1,3,shifted,key.asset),
            new LTMeshStampGeometryKey(11,5,3,1,3,key.matrix,"asset-b")};
        foreach(var next in alternatives)
        {cache.Store(1,key,data,0);Check(cache.Lookup(1,next,.1,out _)==LTMeshStampGeometryLookup.Miss,"mesh/revision/layout/exact transform/dependency invalidation");Check(cache.Bytes==0,"superseded entry is released");}
        cache.Store(1,key,data,0);cache.Store(2,key,data,0);cache.Begin();cache.Lookup(1,key,.1,out _);cache.End();
        Check(cache.Count==1&&cache.Bytes==data.Bytes,"removed or disabled owners are pruned");
        cache.Clear();Check(cache.Count==0&&cache.Bytes==0,"undo/reload/force reset clears retained entries");
        Check(data.vertices[1].Equals(Vector3.right),"clearing leaves in-flight geometry intact");
        var limited=new LTMeshStampGeometryCache(data.Bytes);limited.Begin();limited.Store(1,key,data,0);limited.Store(2,key,data,0);
        Check(limited.Count==1&&limited.Bytes==data.Bytes,"retained memory budget cannot be exceeded");
        limited.Store(1,key,data,0);Check(limited.Count==1,"replacement does not double-count bytes");
        var disabled=new LTMeshStampGeometryCache(0);disabled.Store(1,key,data,0);Check(disabled.Count==0,"zero budget bypasses retention");
        cache.Begin();cache.Store(1,key,data,0);
        for(int i=0;i<100;i++){cache.Begin();cache.Lookup(1,key,.1,out _);cache.End();}
        long before=GC.GetAllocatedBytesForCurrentThread();bool allHit=true;
        for(int i=0;i<10000;i++){cache.Begin();allHit&=cache.Lookup(1,key,.1,out _)==LTMeshStampGeometryLookup.Hit;cache.End();}
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(allHit&&allocated==0,"hot cache bookkeeping allocates zero managed bytes");
        string engine=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTEditor.cs");
        string capture=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTMeshStampCapture.cs");
        Check(engine.Contains("readOnlyPreview?null:GetState(w).meshCapture"),"read-only preview bypasses world cache state");
        Check(engine.Contains("ClearMeshCaptureCaches();")&&engine.Contains("geometry.contentHash"),"explicit invalidation and content signature integrated");
        Check(capture.Contains("mesh.GetVertices(meshAuditVertices)")&&capture.Contains("mesh.GetTriangles(meshAuditPart,s)"),"infrequent audit uses reusable buffers");
        Check(capture.Contains("LT.Editor.MeshStamp.CacheMissBuild")&&capture.Contains("LT.Editor.MeshStamp.ContentAudit"),"separate hit/miss/audit markers");
        string native=File.ReadAllText("Assets/TerrainSystem/LocalTerrain/Editor/LTMeshStampCaptureCheck.cs");
        Check(native.Contains("generatedPreview.hideFlags=HideFlags.HideAndDontSave")&&native.Contains("Generated preview was adopted as the source."),"native fixture checks protected preview rejection");
        Check(native.Contains("replacement.hideFlags=HideFlags.HideInHierarchy|HideFlags.DontSaveInBuild|HideFlags.DontUnloadUnusedAsset"),"native replacement does not carry the generated-preview flag");
        Check(engine.Contains("(assignedMesh.hideFlags&HideFlags.DontSaveInEditor)!=0"),"production generated-source protection stays intact");
        Console.WriteLine($"PASS mesh stamp geometry cache: {checks} assertions; 200 legacy-output fixtures; zero allocation hot bookkeeping. Managed only, not Unity import or live performance.");
    }
}
