using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Shared managed/native regression: uses the production emitter and boundary
    // accounting, without constructing/saving user objects, meshes or assets.
    public static class LTRoadGeometryBoundaryCheck
    {
        static Vector3Int Key(Vector3 p)=>LTRoadGeometryBoundary.PointKey(p);
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException("Asphalt boundary regression: "+message);}
        // Exact coordinates from the user's failed v4 Apply Pending Changes.
        // This is below the EXISTING strict walker's 0.1 mm endpoint weld, not
        // permission to discard a larger hole or an actually non-manifold edge.
        public static string MicroCorner(Action<Vector3[][],int[][],Vector3[],int> verifyEditorLoops=null)
        {
            var a=new Vector3(884.375f,7.25532055f,226.5f);
            var b=new Vector3(884.375f,7.255383f,226.5f);
            var c=new Vector3(884.375f,7.255379f,226.500229f);
            var positions=new Dictionary<Vector3Int,Vector3>{{Key(a),a},{Key(b),b},{Key(c),c}};
            var raw=new[]{new LTRoadGeometryBoundary.Edge(Key(a),Key(b)),new LTRoadGeometryBoundary.Edge(Key(a),Key(c)),new LTRoadGeometryBoundary.Edge(Key(b),Key(c))};
            for(int first=0;first<3;first++)foreach(int direction in new[]{1,-1})
            {
                var order=Enumerable.Range(0,3).Select(i=>raw[(first+i*direction+6)%3]).ToArray();
                var resolved=LTRoadGeometryBoundary.Resolve(order,k=>positions[k],out _);
                Require(resolved.Count==0,"captured sub-weld corner must cancel at the same precision as the strict walker");
            }
            verifyEditorLoops?.Invoke(new[]{new[]{a,b,c}},new[]{new[]{0,1,2}},new[]{Vector3.zero},0);
            // A similarly small but RESOLVABLE triangle may not be treated as
            // three collinear uses merely because the absolute epsilon is large.
            b=new Vector3(a.x,a.y+.00015f,a.z);positions=new Dictionary<Vector3Int,Vector3>{{Key(a),a},{Key(b),b},{Key(c),c}};
            raw=new[]{new LTRoadGeometryBoundary.Edge(Key(a),Key(b)),new LTRoadGeometryBoundary.Edge(Key(a),Key(c)),new LTRoadGeometryBoundary.Edge(Key(b),Key(c))};
            var kept=LTRoadGeometryBoundary.Resolve(raw,k=>positions[k],out _);
            Require(kept.Count==3,"keep a resolvable small triangular contour");
            verifyEditorLoops?.Invoke(new[]{new[]{a,b,c}},new[]{new[]{0,1,2}},new[]{Vector3.zero},1);
            bool rejected=false;
            try{LTRoadGeometryBoundary.Resolve(new[]{new LTRoadGeometryBoundary.Edge(Key(a),Key(b),3)},k=>positions[k],out _);}
            catch(InvalidOperationException){rejected=true;}
            Require(rejected,"resolvable three-use edge must still fail");
            // A chain of close points must not enlarge the 0.1 mm weld radius.
            var p0=new Vector3(0,0,0);var p1=new Vector3(0,.000075f,0);var p2=new Vector3(0,.00016f,0);
            var q0=new Vector3(1,0,0);var q1=new Vector3(1,.001f,0);
            positions=new[]{p0,p1,p2,q0,q1}.ToDictionary(Key,p=>p);
            kept=LTRoadGeometryBoundary.Resolve(new[]{new LTRoadGeometryBoundary.Edge(Key(p0),Key(q0)),new LTRoadGeometryBoundary.Edge(Key(p1),Key(q0)),new LTRoadGeometryBoundary.Edge(Key(p2),Key(q1))},k=>positions[k],out _);
            Require(kept.Count==1&&(kept[0].a==Key(p2)||kept[0].b==Key(p2)),"endpoint weld must not grow transitively");
            // A sub-weld triangle lying along an unrelated long edge must cancel
            // BEFORE collinear subdivision; otherwise its doubled remainder can
            // falsely turn part of the long edge into a triple use.
            var l=new Vector3(0,0,0);var r=new Vector3(2,0,0);
            a=new Vector3(1,0,0);b=new Vector3(1,.000075f,0);c=new Vector3(1.00015f,0,0);
            positions=new[]{l,r,a,b,c}.ToDictionary(Key,p=>p);
            kept=LTRoadGeometryBoundary.Resolve(new[]{new LTRoadGeometryBoundary.Edge(Key(l),Key(r)),
                new LTRoadGeometryBoundary.Edge(Key(a),Key(b)),new LTRoadGeometryBoundary.Edge(Key(b),Key(c)),new LTRoadGeometryBoundary.Edge(Key(c),Key(a))},k=>positions[k],out _);
            Require(kept.Count==1&&kept[0].a==Key(l)&&kept[0].b==Key(r),"sub-weld triangle must not create a false triple use along a separate edge");
            c=new Vector3(1,0,.00015f);positions=new[]{l,r,c}.ToDictionary(Key,p=>p);
            kept=LTRoadGeometryBoundary.Resolve(new[]{new LTRoadGeometryBoundary.Edge(Key(l),Key(r)),
                new LTRoadGeometryBoundary.Edge(Key(l),Key(c)),new LTRoadGeometryBoundary.Edge(Key(c),Key(r))},k=>positions[k],out _,preciseFrame:true);
            Require(kept.Count==3,"precise local frame retains a real shallow 0.15 mm corner");
            return "captured 62-micrometre corner cancelled in six edge orders; resolvable small contour retained; true triple use rejected; fixed weld radius";
        }
        public static string Run(Action<Vector3[][],int[][],Vector3[],int> verifyEditorLoops=null,int seedOffset=0,int[] resolutions=null,bool beveled=false,bool connected=false)
        {
            string micro=MicroCorner(verifyEditorLoops);
            int fixtures=0,before=0,after=0,splits=0,invalidBefore=0,localCancelled=0;
            foreach(float origin in new[]{0f,879.375f,4096f})foreach(int cells in resolutions??new[]{8,32})foreach(bool rockHole in new[]{false,true})
            {
                const float z0=100.25f;
                var settings=LTRoadMath.Settings.Default;settings.mode=LTRoadMode.GeometricAsphalt;settings.width=8;
                settings.asphaltDamage.potholes=1;settings.asphaltDamage.brokenEdges=1;settings.asphaltDamage.size=3;
                settings.asphaltDamage.depth=.4f;settings.asphaltDamage.seed=7131+cells+seedOffset;
                if(beveled){settings.pitBevelWidth=.12f;settings.pitBevelDepth=.05f;settings.pitShapeVariation=.7f;}
                if(connected){settings.connectedDamage=true;settings.asphaltDamage.depth=.06f;}
                var road=LTRoadMath.Build(new[]{new LTRoadPoint(new Vector3(origin+8,2,z0)),
                    new LTRoadPoint(new Vector3(origin+8,2,z0+32))},Matrix4x4.identity,settings);
                float H(float x,float z)=>2.19f+.013f*(x-origin)+.008f*(z-z0)+.0007f*(z-z0)*(z-z0);
                bool Cut(float x,float z)=>rockHole&&(x-origin-24)*(x-origin-24)+(z-z0-13)*(z-z0-13)<9;
                var plans=new Dictionary<int,List<Vector3Int>>();
                for(int c=0;c<2;c++)plans[c]=LTStampMesh.Plan(new Rect(origin+c*8,z0,8,32),cells,false,.01f,300000,new List<LTStampMesh.Zone>(),H,new LTLODMesh.BuildProgress(false));
                // A second pair contains an ordinary rock cut away from the road.
                for(int c=2;c<4;c++)plans[c]=LTStampMesh.Plan(new Rect(origin+c*8,z0,8,32),cells,false,.01f,300000,new List<LTStampMesh.Zone>(),H,new LTLODMesh.BuildProgress(false));
                var forest=new LTBalancedForest(4,1,300000,plans);forest.Balance();
                var points=new Dictionary<Vector3Int,Vector3>();var totals=new Dictionary<(Vector3Int,Vector3Int),int>();
                var rawTotals=new Dictionary<(Vector3Int,Vector3Int),int>();
                var chunkVertices=new Vector3[4][];var chunkIndices=new int[4][];var offsets=new Vector3[4];
                for(int c=0;c<4;c++)
                {
                    var rect=new Rect(origin+c*8,z0,8,32);
                    LTStampMesh.Emit(rect,new Vector2(32,32),300000,forest,c,forest.Plan(c),H,Cut,
                        out var v,out _,out _,out var indices,progress:new LTLODMesh.BuildProgress(false),asphaltPits:road.geometricPits);
                    chunkVertices[c]=v;chunkIndices[c]=indices;offsets[c]=new Vector3(rect.xMin,0,rect.yMin);
                    var keys=new Vector3Int[v.Length];
                    for(int i=0;i<v.Length;i++){var p=v[i]+offsets[c];keys[i]=LTRoadGeometryBoundary.TranslatedKey(v[i],offsets[c]);if(!points.ContainsKey(keys[i]))points.Add(keys[i],p);}
                    for(int i=0;i<indices.Length;i+=3)
                    {
                        var a=keys[indices[i]];var b=keys[indices[i+1]];var d=keys[indices[i+2]];
                        if(a==b||b==d||a==d)continue;
                        for(int e=0;e<3;e++)
                        {
                            var edge=new LTRoadGeometryBoundary.Edge(keys[indices[i+e]],keys[indices[i+(e+1)%3]]);
                            var key=(edge.a,edge.b);rawTotals.TryGetValue(key,out int count);rawTotals[key]=count+1;
                        }
                    }
                    // Same two-phase accounting as production: local cancellation
                    // precedes world translation; precise keys keep interior
                    // edges from hiding a nearby chunk border after float rounding.
                    foreach(var use in LTRoadGeometryBoundary.MeshEdges(v,indices))
                    {
                        var edge=new LTRoadGeometryBoundary.Edge(keys[use.a],keys[use.b]);
                        if(edge.a==edge.b)continue;
                        var key=(edge.a,edge.b);totals.TryGetValue(key,out int count);totals[key]=count+use.count;
                    }
                }
                var candidates=totals.Where(e=>e.Value==1).Select(e=>new LTRoadGeometryBoundary.Edge(e.Key.Item1,e.Key.Item2)).ToArray();
                var rawCandidates=rawTotals.Where(e=>e.Value==1).Select(e=>new LTRoadGeometryBoundary.Edge(e.Key.Item1,e.Key.Item2)).ToArray();
                localCancelled+=Math.Max(0,rawCandidates.Length-candidates.Length);
                var degrees=new Dictionary<Vector3Int,int>();
                foreach(var e in rawCandidates)if(e.a!=e.b){degrees.TryGetValue(e.a,out int a);degrees[e.a]=a+1;degrees.TryGetValue(e.b,out int b);degrees[e.b]=b+1;}
                invalidBefore+=degrees.Count(p=>p.Value!=2);
                var resolved=LTRoadGeometryBoundary.Resolve(candidates,k=>points[k],out int n);splits+=n;
                degrees.Clear();
                foreach(var e in resolved){degrees.TryGetValue(e.a,out int a);degrees[e.a]=a+1;degrees.TryGetValue(e.b,out int b);degrees[e.b]=b+1;}
                var invalid=degrees.Where(p=>p.Value!=2).ToArray();
                Require(invalid.Length==0,$"origin={origin}, cells={cells}, hole={rockHole}: {invalid.Length} open/branch vertices; "+
                    string.Join("; ",invalid.Take(8).Select(p=>$"{p.Key} degree={p.Value} point={points[p.Key].x:R},{points[p.Key].y:R},{points[p.Key].z:R}"))+"; segments: "+
                    string.Join("; ",resolved.Where(e=>invalid.Any(p=>p.Key==e.a||p.Key==e.b)).Select(e=>$"{e.a} -> {e.b}")));
                var neighbours=new Dictionary<Vector3Int,List<Vector3Int>>();
                foreach(var e in resolved)
                {
                    if(!neighbours.TryGetValue(e.a,out var a))neighbours[e.a]=a=new List<Vector3Int>();a.Add(e.b);
                    if(!neighbours.TryGetValue(e.b,out var b))neighbours[e.b]=b=new List<Vector3Int>();b.Add(e.a);
                }
                var visited=new HashSet<Vector3Int>();int loops=0;var loopKeys=new List<List<Vector3Int>>();
                foreach(var key in neighbours.Keys)
                {
                    if(!visited.Add(key))continue;loops++;var queue=new Queue<Vector3Int>();queue.Enqueue(key);var members=new List<Vector3Int>();loopKeys.Add(members);
                    while(queue.Count>0){var current=queue.Dequeue();members.Add(current);foreach(var next in neighbours[current])if(visited.Add(next))queue.Enqueue(next);}
                }
                Require(loops==(rockHole?2:1),$"origin={origin}, cells={cells}, hole={rockHole}, seed={seedOffset}: expected only world border and optional rock hole, found {loops} loops; smallest: "+
                    string.Join("; ",loopKeys.OrderBy(l=>l.Count).First().Take(12).Select(k=>FormattableString.Invariant($"key={k.x},{k.y},{k.z} pos={points[k].x:R},{points[k].y:R},{points[k].z:R}"))));
                verifyEditorLoops?.Invoke(chunkVertices,chunkIndices,offsets,rockHole?2:1);
                // Only the rectangular world border and optional existing rock
                // cut may remain. Potholes with a floor are CLOSED geometry.
                foreach(var e in resolved)
                {
                    var p=(points[e.a]+points[e.b])*.5f;
                    bool outer=Math.Abs(p.x-origin)<.002f||Math.Abs(p.x-origin-32)<.002f||Math.Abs(p.z-z0)<.002f||Math.Abs(p.z-z0-32)<.002f;
                    bool hole=rockHole&&Math.Abs(p.x-origin-24)<3.002f&&Math.Abs(p.z-z0-13)<3.002f;
                    Require(outer||hole,$"false pit boundary at {p}");
                }
                before+=rawCandidates.Length;after+=resolved.Count;fixtures++;
            }
            Require(invalidBefore>50&&splits+localCancelled>50,"fixture must reproduce and resolve branched contours, not only a trivial grid");
            // Negative control: two almost-collinear but separated edges are not
            // a seam. Nor may a real open endpoint or a branch be silently erased.
            var positions=new Dictionary<Vector3Int,Vector3>();
            Vector3Int P(float x,float z){var p=new Vector3(x,0,z);var k=Key(p);positions[k]=p;return k;}
            var a0=P(0,0);var b0=P(2,0);var mid=P(1,0);var side=P(1,1);
            var raw=new[]{new LTRoadGeometryBoundary.Edge(a0,b0),new LTRoadGeometryBoundary.Edge(a0,mid),new LTRoadGeometryBoundary.Edge(mid,b0),new LTRoadGeometryBoundary.Edge(mid,side)};
            var kept=LTRoadGeometryBoundary.Resolve(raw,k=>positions[k],out _);Require(kept.Count==1&&kept[0].a!=kept[0].b,"real unmatched edge must remain");
            var c0=P(0,.01f);var d0=P(2,.01f);
            kept=LTRoadGeometryBoundary.Resolve(new[]{new LTRoadGeometryBoundary.Edge(a0,b0),new LTRoadGeometryBoundary.Edge(c0,d0)},k=>positions[k],out _);
            Require(kept.Count==2,"do not bridge a real 1 cm gap");
            foreach(float offset in new[]{0f,4096f})
            {
                var lo=P(offset,0);var hi=P(offset+2,0);var p=P(offset+1,.002f);
                // Endpoints meet but the intermediate vertex is genuinely off
                // the straight edge. Do not flatten this thin, real triangle.
                kept=LTRoadGeometryBoundary.Resolve(new[]{new LTRoadGeometryBoundary.Edge(lo,hi),new LTRoadGeometryBoundary.Edge(lo,p),new LTRoadGeometryBoundary.Edge(p,hi)},k=>positions[k],out _);
                Require(kept.Count==3,"do not erase a real 2 mm departure, including at world offsets");
            }
            bool rejected=false;
            try{LTRoadGeometryBoundary.Resolve(new[]{new LTRoadGeometryBoundary.Edge(a0,b0,3)},k=>positions[k],out _);}
            catch(InvalidOperationException){rejected=true;}
            Require(rejected,"non-manifold overlap must still fail");
            return $"{fixtures} four-chunk fixtures: {invalidBefore} raw open/branch vertices; {localCancelled} local cancellations, {splits} global splits; {before} -> {after} unmatched segments; closed pit walls/floors, ordinary rock holes and world border retained; real gaps/endpoints retained. {micro}.";
        }
    }
}
