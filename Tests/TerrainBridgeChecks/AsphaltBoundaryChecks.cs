using System;
using System.Linq;
using System.Collections.Generic;
using LocalTerrainPrototype;
using UnityEngine;

partial class Checks
{
    // Managed mirror of the native editor validator. Checks the unchanged strict
    // 0.1 mm contour walker too, not merely degrees of quantized vertex keys.
    // Production helper/mesh emitter are linked; cache/walker are test models.
    static void VerifyAsphaltBoundaryCache(Vector3[][] vertices,int[][] indices,Vector3[] offsets,int expectedLoops)
    {
        var cache=new BoundaryCache();
        ChunkBoundaryData Build(int c)
        {
            var matrix=Matrix4x4.identity;matrix.m03=offsets[c].x;matrix.m13=offsets[c].y;matrix.m23=offsets[c].z;
            return BuildChunkBoundary(vertices[c],Array.Empty<Vector3>(),indices[c],matrix,Matrix4x4.identity);
        }
        for(int c=0;c<vertices.Length;c++)cache.Replace(c,Build(c));
        void Check()
        {
            var positions=new Dictionary<Vector3Int,Vector3>();
            foreach(var data in cache.Values)foreach(var pair in data.pointIndices)
            {if(!positions.ContainsKey(pair.Key))positions.Add(pair.Key,data.points[pair.Value[0]]);}
            var open=cache.edgeTotals.Where(e=>e.Value==1).Select(e=>new LTRoadGeometryBoundary.Edge(e.Key.Item1,e.Key.Item2)).ToArray();
            var resolved=LTRoadGeometryBoundary.Resolve(open,k=>positions[k],out _);
            var segments=new List<RockClipVertex>();
            foreach(var e in resolved)
            {segments.Add(new RockClipVertex{position=positions[e.a],normal=Vector3.up});segments.Add(new RockClipVertex{position=positions[e.b],normal=Vector3.up});}
            List<List<RockClipVertex>> loops;
            try{loops=OrderedBoundaryLoops(segments,"asphalt strict-loop model");}
            catch(Exception error)
            {
                var small=resolved.OrderBy(e=>(positions[e.a]-positions[e.b]).sqrMagnitude).Take(12);
                throw new InvalidOperationException(error.Message+" Residual short edges: "+string.Join("; ",small.Select(e=>
                    FormattableString.Invariant($"{positions[e.a].x:R}/{positions[e.a].y:R}/{positions[e.a].z:R} -> {positions[e.b].x:R}/{positions[e.b].y:R}/{positions[e.b].z:R}"))),error);
            }
            if(loops.Count!=expectedLoops)Require(false,"asphalt cache/strict contour mismatch: "+loops.Count+" loops; smallest: "+
                string.Join("; ",(loops.OrderBy(l=>l.Count).FirstOrDefault()??new List<RockClipVertex>()).Select(p=>FormattableString.Invariant($"{p.position.x:R},{p.position.y:R},{p.position.z:R}"))));
        }
        Check();cache.Replace(0,Build(0));Check();
    }
}
