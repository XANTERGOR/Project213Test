using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // One endpoint-collapse decision, validated against EVERY participating mesh.
    // Vertex streams are never welded: each UV chart keeps its own endpoint index.
    public static class LTRockSeamMath
    {
        public sealed class Part
        {
            public Vector3[] positions;
            public int[][] triangles;
            public bool[] movable;
            public bool[] targets;
            public int onlyLevel=-1;
            public bool terrain;
            // All reachable native LOD/stitch faces. This union is NOT a manifold
            // surface: use it for orientation checks, never for link/boundary tests.
            public int[] constraints;
            public float[] heightErrors;
        }
        public sealed class Result { public int[][][] maps; public int[] collapses; }
        public static Vector3Int Key(Vector3 p)=>new Vector3Int(Mathf.RoundToInt(p.x*10000),Mathf.RoundToInt(p.y*10000),Mathf.RoundToInt(p.z*10000));
        static long Edge(int a,int b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
        public static int[] SpatialConstraints(LTSpatialLODCell[] cells,LTSpatialLODPatch[] patches)
        {
            var faces=new HashSet<(int,int,int)>();var output=new List<int>();
            var visited=new HashSet<int>();var stack=new Stack<int>(patches.SelectMany(p=>p.levels.SelectMany(l=>l.cells)));
            while(stack.Count>0)
            {
                int id=stack.Pop();if(!visited.Add(id))continue;var c=cells[id];
                if(c.child0>=0)for(int k=0;k<4;k++)stack.Push(c.Child(k));
                for(int mask=0;mask<16;mask++)if((mask&~c.possibleMask)==0&&(mask&c.requiredMask)==c.requiredMask)
                {
                    var indices=c.variants[LTSpatialLODMath.VariantIndex(mask,c.possibleMask)].indices;
                    for(int t=0;t<indices.Length;t+=3)if(faces.Add(LTRockSeamIndices.Face(indices[t],indices[t+1],indices[t+2])))
                    {output.Add(indices[t]);output.Add(indices[t+1]);output.Add(indices[t+2]);}
                }
            }
            return output.ToArray();
        }
        public static bool[] InteriorSpatialVertices(Vector3[] vertices,float patchWidth,float patchDepth)
            =>vertices.Select(v=>Mathf.Abs(v.x/patchWidth-Mathf.Round(v.x/patchWidth))>=.00001f&&
                Mathf.Abs(v.z/patchDepth-Mathf.Round(v.z/patchDepth))>=.00001f).ToArray();
        public static void ProtectOutsideStars(bool[] movable,bool[] inside,IEnumerable<int[]> variants)
        {
            // Independent groups must not move opposite ends of the same long
            // native triangle. Its complete star remains owned by ordinary LOD.
            foreach(var indices in variants)for(int t=0;t<indices.Length;t+=3)
            {
                int a=indices[t],b=indices[t+1],c=indices[t+2];
                if(!inside[a]||!inside[b]||!inside[c])movable[a]=movable[b]=movable[c]=false;
            }
        }
        public static bool[] StableSpatialVertices(Vector3[] vertices,LTSpatialLODCell[] cells,LTSpatialLODPatch[] patches,float patchWidth,float patchDepth)
        {
            var safe=Enumerable.Repeat(true,vertices.Length).ToArray();var stable=new HashSet<int>();
            foreach(var patch in patches)
            {
                var common=new HashSet<int>(patch.levels[0].cells);foreach(var level in patch.levels)common.IntersectWith(level.cells);stable.UnionWith(common);
            }
            var reachable=new HashSet<int>();var stack=new Stack<int>(patches.SelectMany(p=>p.levels.SelectMany(l=>l.cells)));
            while(stack.Count>0){int id=stack.Pop();if(!reachable.Add(id))continue;var c=cells[id];if(c.child0>=0)for(int k=0;k<4;k++)stack.Push(c.Child(k));}
            foreach(int id in reachable)
            {
                var cell=cells[id];
                var variants=new List<int[]>();
                for(int mask=0;mask<16;mask++)if((mask&~cell.possibleMask)==0&&(mask&cell.requiredMask)==cell.requiredMask)
                    variants.Add(cell.variants[LTSpatialLODMath.VariantIndex(mask,cell.possibleMask)].indices);
                if(!stable.Contains(id))
                {foreach(var variant in variants)foreach(int v in variant)safe[v]=false;continue;}
                if(variants.Count==1)continue;
                // Optional stitches need not freeze an ENTIRE persistent cell.
                // Freeze only vertices whose incident triangles actually vary.
                // Other stars stay identical in every reachable neighbour state.
                Dictionary<int,HashSet<(int,int,int)>> Stars(int[] indices)
                {
                    var stars=new Dictionary<int,HashSet<(int,int,int)>>();
                    for(int t=0;t<indices.Length;t+=3)
                    {
                        var ids=new[]{indices[t],indices[t+1],indices[t+2]};Array.Sort(ids);var face=(ids[0],ids[1],ids[2]);
                        foreach(int v in ids){if(!stars.TryGetValue(v,out var set))stars[v]=set=new HashSet<(int,int,int)>();set.Add(face);}
                    }
                    return stars;
                }
                var first=Stars(variants[0]);
                for(int variant=1;variant<variants.Count;variant++)
                {
                    var other=Stars(variants[variant]);
                    foreach(var star in first)if(!other.TryGetValue(star.Key,out var set)||!star.Value.SetEquals(set))safe[star.Key]=false;
                    foreach(int v in other.Keys)if(!first.ContainsKey(v))safe[v]=false;
                }
            }
            for(int i=0;i<vertices.Length;i++)
            {
                var v=vertices[i];bool border=Mathf.Abs(v.x/patchWidth-Mathf.Round(v.x/patchWidth))<.00001f||Mathf.Abs(v.z/patchDepth-Mathf.Round(v.z/patchDepth))<.00001f;
                safe[i]&=!border;
            }
            return safe;
        }
        sealed class Surface
        {
            public Part input;public int[] logical,map;public List<int[]> faces,constraints;
            public Dictionary<int,int[]> aliases;
            public Func<int,int,int,float,bool> heightConstraint;
        }
        sealed class Adjacency
        {
            public readonly Dictionary<int,List<int>> logical=new Dictionary<int,List<int>>(),local=new Dictionary<int,List<int>>();
            public readonly Dictionary<int,HashSet<int>> neighbours=new Dictionary<int,HashSet<int>>();
            public Adjacency(Surface s,List<int[]> faces)
            {
                for(int id=0;id<faces.Count;id++)
                {
                    var f=faces[id];for(int k=0;k<3;k++)
                    {
                        int v=f[k],key=s.logical[v];
                        if(!logical.TryGetValue(key,out var a))logical[key]=a=new List<int>();a.Add(id);
                        if(!local.TryGetValue(v,out var b))local[v]=b=new List<int>();b.Add(id);
                        if(!neighbours.TryGetValue(v,out var n))neighbours[v]=n=new HashSet<int>();n.Add(f[(k+1)%3]);n.Add(f[(k+2)%3]);
                    }
                }
            }
        }
        public static Result Build(Part[] parts,float[] errors,int passes=64,bool progressive=false)
        {
            if(parts==null||parts.Length==0||errors==null)throw new ArgumentException("Missing joint seam inputs.");
            var lookup=new Dictionary<Vector3Int,int>();var points=new List<Vector3>();var locked=new List<bool>();var targetAllowed=new List<bool>();
            var surfaces=new List<Surface>();
            foreach(var part in parts)
            {
                if(part.positions==null||part.triangles==null||part.movable==null||part.movable.Length!=part.positions.Length||part.targets!=null&&part.targets.Length!=part.positions.Length)throw new ArgumentException("Invalid seam part.");
                var s=new Surface{input=part,logical=new int[part.positions.Length],map=Enumerable.Range(0,part.positions.Length).ToArray(),faces=new List<int[]>()};
                var used=new HashSet<int>(part.triangles.SelectMany(t=>t));
                if(part.constraints!=null)used.UnionWith(part.constraints);
                for(int i=0;i<s.logical.Length;i++)
                {
                    var key=Key(part.positions[i]);
                    if(!lookup.TryGetValue(key,out int id)){id=points.Count;lookup.Add(key,id);points.Add(part.positions[i]);locked.Add(false);targetAllowed.Add(true);}
                    s.logical[i]=id;if(used.Contains(i)&&!part.movable[i])locked[id]=true;
                    if(used.Contains(i)&&part.targets!=null&&!part.targets[i])targetAllowed[id]=false;
                }
                for(int sub=0;sub<part.triangles.Length;sub++)
                {
                    var tris=part.triangles[sub];if(tris.Length%3!=0)throw new ArgumentException("Incomplete seam triangles.");
                    for(int i=0;i<tris.Length;i+=3)
                    {
                        int a=tris[i],b=tris[i+1],c=tris[i+2];
                        if(a<0||b<0||c<0||a>=s.logical.Length||b>=s.logical.Length||c>=s.logical.Length)throw new ArgumentException("Invalid seam index.");
                        s.faces.Add(new[]{a,b,c,sub});
                    }
                }
                s.constraints=new List<int[]>();
                if(part.constraints!=null)
                {
                    if(part.constraints.Length%3!=0)throw new ArgumentException("Incomplete native seam constraints.");
                    for(int t=0;t<part.constraints.Length;t+=3)
                    {
                        var f=new[]{part.constraints[t],part.constraints[t+1],part.constraints[t+2],0};
                        if(f.Take(3).Any(v=>v<0||v>=s.logical.Length))throw new ArgumentException("Invalid native seam constraint.");
                        s.constraints.Add(f);
                    }
                }
                s.aliases=used.GroupBy(v=>s.logical[v]).ToDictionary(g=>g.Key,g=>g.ToArray());
                if(part.heightErrors!=null)
                {
                    if(!part.terrain||part.heightErrors.Length!=errors.Length||part.heightErrors.Any(e=>float.IsNaN(e)||float.IsInfinity(e)||e<0))
                        throw new ArgumentException("Invalid terrain height error budget.");
                    s.heightConstraint=LTRockSeamBandMath.HeightConstraint(part.positions,part.triangles.SelectMany(t=>t).ToArray());
                }
                surfaces.Add(s);
            }
            var result=new Result{maps=new int[errors.Length][][],collapses=new int[errors.Length]};
            List<int[]>[] previousFaces=null,previousConstraints=null;float[] previousRadius=null;
            // Progressive seams validate ALL transition LODs together. With increasing
            // budgets a far level continues the accepted contractions, never restores
            // deleted triangles. Decreasing user budgets explicitly start over.
            for(int level=0;level<errors.Length;level++)
            {
                float error=errors[level];if(float.IsNaN(error)||float.IsInfinity(error)||error<0)throw new ArgumentException("Invalid seam error.");
                bool resume=progressive&&level>0&&error>=errors[level-1]&&parts.All(p=>p.heightErrors==null||p.heightErrors[level]>=p.heightErrors[level-1]);
                var faces=surfaces.Select((s,p)=>(resume?previousFaces[p]:!progressive&&s.input.onlyLevel>=0&&s.input.onlyLevel!=level?new List<int[]>():s.faces).Select(f=>(int[])f.Clone()).ToList()).ToArray();
                var constraints=surfaces.Select((s,p)=>(resume?previousConstraints[p]:!progressive&&s.input.onlyLevel>=0&&s.input.onlyLevel!=level?new List<int[]>():s.constraints).Select(f=>(int[])f.Clone()).ToList()).ToArray();
                var maps=surfaces.Select((s,p)=>(int[])(resume?result.maps[level-1][p]:s.map).Clone()).ToArray();var radius=resume?(float[])previousRadius.Clone():new float[points.Count];
                if(resume)result.collapses[level]=result.collapses[level-1];
                var levelLocked=locked.ToArray();var boundaryRoles=new int[points.Count];
                for(int p=0;p<surfaces.Count;p++)
                {
                    var incidence=new Dictionary<long,int>();var s=surfaces[p];
                    foreach(var f in faces[p])for(int k=0;k<3;k++)
                    {long edge=Edge(s.logical[f[k]],s.logical[f[(k+1)%3]]);incidence.TryGetValue(edge,out int uses);incidence[edge]=uses+1;}
                    foreach(var edge in incidence)if(edge.Value!=2)
                    {int a=(int)(edge.Key>>32),b=(int)edge.Key;if(edge.Value!=1){levelLocked[a]=levelLocked[b]=true;}boundaryRoles[a]|=s.input.terrain?1:2;boundaryRoles[b]|=s.input.terrain?1:2;}
                }
                for(int i=0;i<boundaryRoles.Length;i++)if(boundaryRoles[i]!=0&&boundaryRoles[i]!=3)levelLocked[i]=true;
                for(int pass=0;error>0&&pass<passes;pass++)
                {
                    var adjacency=surfaces.Select((s,p)=>new Adjacency(s,faces[p])).ToArray();
                    var constraintGraph=surfaces.Select((s,p)=>new Adjacency(s,constraints[p])).ToArray();
                    var edges=new HashSet<long>();
                    for(int p=0;p<surfaces.Count;p++)foreach(var f in faces[p])for(int k=0;k<3;k++)
                    {int a=surfaces[p].logical[f[k]],b=surfaces[p].logical[f[(k+1)%3]];if(a!=b&&(!levelLocked[a]||!levelLocked[b]))edges.Add(Edge(a,b));}
                    // A fixed endpoint may ABSORB an interior point without moving.
                    // Try both directions: vertex numbering must not decide which
                    // half of the terrain band can be retriangulated.
                    var candidates=edges.SelectMany(e=>new[]{(keep:(int)(e>>32),remove:(int)e),(keep:(int)e,remove:(int)(e>>32))})
                        .Where(e=>!levelLocked[e.remove]&&targetAllowed[e.keep])
                        .Select(e=>(e.keep,e.remove,length:Vector3.Distance(points[e.keep],points[e.remove])))
                        .Where(e=>e.length<=error).OrderBy(e=>e.length).ThenBy(e=>e.remove).ThenBy(e=>e.keep).ToArray();
                    var touched=new HashSet<int>();int changes=0;
                    foreach(var candidate in candidates)
                    {
                        int keep=candidate.keep,remove=candidate.remove;
                        if(touched.Contains(keep)||touched.Contains(remove)||candidate.length+radius[remove]>error)continue;
                        var proposals=new List<(int part,Dictionary<int,int> indices,HashSet<int> affected)>();bool safe=true;
                        for(int p=0;p<surfaces.Count&&safe;p++)
                        {
                            var s=surfaces[p];var graph=adjacency[p];
                            graph.logical.TryGetValue(remove,out var incident);
                            constraintGraph[p].logical.TryGetValue(remove,out var constrained);
                            if(incident==null&&constrained==null)continue;
                            if(incident==null)incident=new List<int>();
                            var active=new HashSet<int>();var targets=new Dictionary<int,HashSet<int>>();
                            foreach(int id in incident){var f=faces[p][id];for(int k=0;k<3;k++)if(s.logical[f[k]]==remove)
                            {
                                int from=f[k];active.Add(from);
                                for(int j=0;j<3;j++)if(s.logical[f[j]]==keep)
                                {if(!targets.TryGetValue(from,out var set))targets[from]=set=new HashSet<int>();set.Add(f[j]);}
                            }}
                            var remap=new Dictionary<int,int>();
                            foreach(int from in active)
                            {
                                if(!targets.TryGetValue(from,out var to)||to.Count!=1){safe=false;break;}
                                int target=to.First();var a=graph.neighbours[from];var b=graph.neighbours[target];int uses=0;var slotsA=new HashSet<int>();var slotsB=new HashSet<int>();
                                foreach(int id in graph.local[from]){var f=faces[p][id];slotsA.Add(f[3]);if(f[0]==target||f[1]==target||f[2]==target)uses++;}
                                foreach(int id in graph.local[target])slotsB.Add(faces[p][id][3]);
                                if(uses<1||uses>2||a.Count(v=>b.Contains(v))!=uses||!slotsA.SetEquals(slotsB)){safe=false;break;}
                                remap.Add(from,target);
                            }
                            if(safe&&constrained!=null)
                            {
                                // Bank aliases used only by a coarse/stitch variant must
                                // follow the same endpoint as its LOD0 counterpart.
                                if(!s.aliases.TryGetValue(keep,out var aliases)){safe=false;break;}
                                var to=aliases.Select(v=>maps[p][v]).Distinct().ToArray();
                                foreach(int id in constrained)for(int k=0;k<3;k++)
                                {
                                    int from=constraints[p][id][k];if(s.logical[from]!=remove||remap.ContainsKey(from))continue;
                                    if(to.Length!=1){safe=false;break;}remap[from]=to[0];
                                }
                            }
                            if(!safe)break;
                            var affected=new HashSet<int>(incident);var compare=new HashSet<int>(incident);
                            foreach(int target in remap.Values)if(graph.local.TryGetValue(target,out var ids))foreach(int id in ids)compare.Add(id);
                            var unique=new HashSet<(int,int,int)>();
                            foreach(int id in compare)
                            {
                                var f=faces[p][id];
                                int a=remap.TryGetValue(f[0],out int x)?x:f[0],b=remap.TryGetValue(f[1],out x)?x:f[1],c=remap.TryGetValue(f[2],out x)?x:f[2];
                                if(a==b||b==c||a==c)continue;
                                var sorted=new[]{a,b,c};Array.Sort(sorted);if(!unique.Add((sorted[0],sorted[1],sorted[2]))){safe=false;break;}
                                if(a==f[0]&&b==f[1]&&c==f[2])continue;
                                var v=s.input.positions;var old=Vector3.Cross(v[f[1]]-v[f[0]],v[f[2]]-v[f[0]]);var next=Vector3.Cross(v[b]-v[a],v[c]-v[a]);
                                if(next.sqrMagnitude<1e-14f||Vector3.Dot(old.normalized,next.normalized)<.8f||s.input.terrain&&old.y*next.y<=0){safe=false;break;}
                                if(s.heightConstraint!=null&&!s.heightConstraint(a,b,c,s.input.heightErrors[level])){safe=false;break;}
                            }
                            if(safe&&constrained!=null)foreach(int id in constrained)
                            {
                                var f=constraints[p][id];int a=remap.TryGetValue(f[0],out int x)?x:f[0],b=remap.TryGetValue(f[1],out x)?x:f[1],c=remap.TryGetValue(f[2],out x)?x:f[2];
                                if(a==b||b==c||a==c)continue;
                                var v=s.input.positions;var old=Vector3.Cross(v[f[1]]-v[f[0]],v[f[2]]-v[f[0]]);var next=Vector3.Cross(v[b]-v[a],v[c]-v[a]);
                                if(next.sqrMagnitude<1e-14f||Vector3.Dot(old.normalized,next.normalized)<.8f||s.input.terrain&&old.y*next.y<=0){safe=false;break;}
                                if(s.heightConstraint!=null&&!s.heightConstraint(a,b,c,s.input.heightErrors[level])){safe=false;break;}
                            }
                            if(safe)proposals.Add((p,remap,affected));
                        }
                        if(!safe||proposals.Count==0)continue;
                        foreach(var proposal in proposals)
                        {
                            int p=proposal.part;var remap=proposal.indices;
                            foreach(int id in proposal.affected){var f=faces[p][id];for(int k=0;k<3;k++)if(remap.TryGetValue(f[k],out int to))f[k]=to;}
                            if(constraintGraph[p].logical.TryGetValue(remove,out var constrained))foreach(int id in constrained)
                            {
                                var f=constraints[p][id];for(int k=0;k<3;k++)if(remap.TryGetValue(f[k],out int to))f[k]=to;
                                for(int k=0;k<3;k++)touched.Add(surfaces[p].logical[f[k]]);
                            }
                            for(int i=0;i<maps[p].Length;i++)if(remap.TryGetValue(maps[p][i],out int to))maps[p][i]=to;
                            // No adjacent decisions in this pass; all validation sees the accepted state.
                            var affected=new HashSet<int>(proposal.affected);
                            if(adjacency[p].logical.TryGetValue(keep,out var neighbours))affected.UnionWith(neighbours);
                            foreach(int id in affected)for(int k=0;k<3;k++)touched.Add(surfaces[p].logical[faces[p][id][k]]);
                        }
                        radius[keep]=Mathf.Max(radius[keep],candidate.length+radius[remove]);touched.Add(remove);touched.Add(keep);changes++;
                    }
                    foreach(var part in faces)part.RemoveAll(f=>f[0]==f[1]||f[1]==f[2]||f[2]==f[0]);
                    foreach(var part in constraints)part.RemoveAll(f=>f[0]==f[1]||f[1]==f[2]||f[2]==f[0]);
                    result.collapses[level]+=changes;if(changes==0)break;
                }
                result.maps[level]=maps;
                previousFaces=faces;previousConstraints=constraints;previousRadius=radius;
            }
            return result;
        }
        public static void Remap(List<int> indices,int[] map)
            =>LTRockSeamIndices.Remap(indices,map);
    }
}
