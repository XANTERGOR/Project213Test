using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Bake-only cavity retriangulation. Spatial seams own an invariant footprint
    // in the native hierarchy, not a guessed uniform-LOD triangle pattern.
    // Both rims are fixed AFTER joint seam mapping. Neighbour stitches remain
    // owned by the ordinary terrain topology and cannot switch this footprint off.
    // No new positions, UVs, renderers or meshes are created by this operation.
    public static class LTRockSeamBandMath
    {
        struct Face
        {
            public int a,b,c;
            public Face(int a,int b,int c){this.a=a;this.b=b;this.c=c;}
        }
        static double Cross(Vector3 a,Vector3 b,Vector3 c)=>(double)(b.x-a.x)*(c.z-a.z)-(double)(b.z-a.z)*(c.x-a.x);
        static long Edge(int a,int b)=>((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
        public static Func<int,int,int,float,bool> HeightConstraint(Vector3[] vertices,int[] reference)
        {
            var faces=new Face[reference.Length/3];
            for(int t=0;t<reference.Length;t+=3)faces[t/3]=new Face(reference[t],reference[t+1],reference[t+2]);
            var surface=new Surface(vertices,faces);
            // A shared rim can move into the old opening. Bound the height on
            // overlapping ground; the joint topology checks own the new rim.
            return (a,b,c,error)=>surface.Accept(new Face(a,b,c),error,false);
        }
        public static LTRockSeamIndices.Band[] BuildSpatial(Vector3[] vertices,int[] fine,
            LTSpatialLODCell[] cells,LTSpatialLODPatch[] patches,bool[] eligible,int[] map,
            float heightError,Func<int,int,int,bool> preserve=null)
        {
            var persistent=new HashSet<int>();
            foreach(var patch in patches)
            {
                var common=new HashSet<int>(patch.levels[0].cells);
                foreach(var level in patch.levels)common.IntersectWith(level.cells);
                // Only leaves cannot be split again by cross-patch 2:1 balance.
                foreach(int id in common)if(cells[id].child0<0)persistent.Add(id);
            }
            var owned=new HashSet<(int,int,int)>();
            foreach(int id in persistent.OrderBy(i=>i))
            {
                var cell=cells[id];HashSet<(int,int,int)> common=null;
                for(int mask=0;mask<16;mask++)if((mask&~cell.possibleMask)==0&&(mask&cell.requiredMask)==cell.requiredMask)
                {
                    var faces=new HashSet<(int,int,int)>();var input=cell.variants[LTSpatialLODMath.VariantIndex(mask,cell.possibleMask)].indices;
                    for(int i=0;i<input.Length;i+=3)faces.Add(LTRockSeamIndices.Face(input[i],input[i+1],input[i+2]));
                    if(common==null)common=faces;else common.IntersectWith(faces);
                }
                if(common!=null)owned.UnionWith(common);
            }
            // Retain original ordering/winding; the same footprint is present in
            // every reachable layout, including optional stitches and mixed LODs.
            var source=new List<int>();var stable=(bool[])eligible.Clone();
            // Whole outside stars stay fixed. Disjoint seam groups cannot own
            // opposite corners of the same long triangle.
            for(int i=0;i<fine.Length;i+=3)
                if(!eligible[fine[i]]||!eligible[fine[i+1]]||!eligible[fine[i+2]])
                    stable[fine[i]]=stable[fine[i+1]]=stable[fine[i+2]]=false;
            var reachable=new HashSet<int>();var pending=new Stack<int>(patches.SelectMany(p=>p.levels.SelectMany(l=>l.cells)));
            while(pending.Count>0)
            {
                int id=pending.Pop();if(!reachable.Add(id))continue;var cell=cells[id];
                if(cell.child0>=0)for(int k=0;k<4;k++)pending.Push(cell.Child(k));
                for(int mask=0;mask<16;mask++)if((mask&~cell.possibleMask)==0&&(mask&cell.requiredMask)==cell.requiredMask)
                {
                    var input=cell.variants[LTSpatialLODMath.VariantIndex(mask,cell.possibleMask)].indices;
                    for(int i=0;i<input.Length;i+=3)
                        if(!owned.Contains(LTRockSeamIndices.Face(input[i],input[i+1],input[i+2])))
                            stable[input[i]]=stable[input[i+1]]=stable[input[i+2]]=false;
                }
            }
            for(int i=0;i<fine.Length;i+=3)
                if(owned.Contains(LTRockSeamIndices.Face(fine[i],fine[i+1],fine[i+2])))source.AddRange(new[]{fine[i],fine[i+1],fine[i+2]});
            var band=Build(vertices,source.ToArray(),stable,map,heightError,fine,preserve,fine);
            if(band==null)return Array.Empty<LTRockSeamIndices.Band>();
            band.invariant=true;
            return new[]{band};
        }
        public static LTRockSeamIndices.Band Build(Vector3[] vertices,int[] source,bool[] stable,int[] map,float heightError,int[] referenceIndices=null,Func<int,int,int,bool> preserve=null,int[] heightReference=null)
        {
            if(heightError<=0)return null;
            if(float.IsNaN(heightError)||float.IsInfinity(heightError))throw new ArgumentException("Invalid band height error.");
            if(preserve!=null)
            {
                stable=(bool[])stable.Clone();
                for(int i=0;i<source.Length;i+=3)if(preserve(source[i],source[i+1],source[i+2]))stable[source[i]]=stable[source[i+1]]=stable[source[i+2]]=false;
            }
            var selected=new List<int>();var fixedVertex=new bool[vertices.Length];
            for(int i=0;i<source.Length;i+=3)
            {
                int a=source[i],b=source[i+1],c=source[i+2];
                if(!stable[a])fixedVertex[map[a]]=true;if(!stable[b])fixedVertex[map[b]]=true;if(!stable[c])fixedVertex[map[c]]=true;
                if(stable[a]||stable[b]||stable[c])selected.AddRange(new[]{a,b,c});
                else fixedVertex[map[a]]=fixedVertex[map[b]]=fixedVertex[map[c]]=true;
            }
            if(selected.Count==0)return null;
            var indices=new List<int>(selected);LTRockSeamIndices.Remap(indices,map);
            var faces=new List<Face>();for(int i=0;i<indices.Count;i+=3)faces.Add(new Face(indices[i],indices[i+1],indices[i+2]));
            var reference=faces.ToArray();
            if(referenceIndices!=null)
            {
                var fine=new List<int>(referenceIndices);LTRockSeamIndices.Remap(fine,map);reference=new Face[fine.Count/3];
                for(int i=0;i<fine.Count;i+=3)reference[i/3]=new Face(fine[i],fine[i+1],fine[i+2]);
            }
            var surface=new Surface(vertices,reference);
            var height=heightReference==null?null:HeightConstraint(vertices,heightReference);
            var edges=new Dictionary<long,int>();var positions=new Dictionary<Vector3Int,int>();
            foreach(var f in faces)
            {
                int[] v={f.a,f.b,f.c};
                for(int k=0;k<3;k++){long edge=Edge(v[k],v[(k+1)%3]);edges.TryGetValue(edge,out int n);edges[edge]=n+1;}
                foreach(int vtx in v)
                {
                    var key=LTRockSeamMath.Key(vertices[vtx]);
                    if(positions.TryGetValue(key,out int other)&&other!=vtx)fixedVertex[vtx]=fixedVertex[other]=true;
                    else positions[key]=vtx;
                }
            }
            foreach(var edge in edges)if(edge.Value!=2)fixedVertex[(int)(edge.Key>>32)]=fixedVertex[(int)edge.Key]=true;
            int removed=0;
            for(int pass=0;pass<64;pass++)
            {
                var incident=new Dictionary<int,List<int>>();
                for(int i=0;i<faces.Count;i++)foreach(int v in new[]{faces[i].a,faces[i].b,faces[i].c})
                {if(!incident.TryGetValue(v,out var list))incident[v]=list=new List<int>();list.Add(i);}
                var touched=new HashSet<int>();var deleted=new HashSet<int>();var added=new List<Face>();int changes=0;
                foreach(var entry in incident.OrderBy(e=>e.Value.Count).ThenBy(e=>e.Key))
                {
                    int v=entry.Key;if(fixedVertex[v]||touched.Contains(v)||entry.Value.Count<3||entry.Value.Count>64)continue;
                    var next=new Dictionary<int,int>();bool valid=true;
                    foreach(int id in entry.Value)
                    {
                        var f=faces[id];int a=f.a==v?f.b:f.b==v?f.c:f.a,b=f.a==v?f.c:f.b==v?f.a:f.b;
                        if(touched.Contains(a)||touched.Contains(b)||next.ContainsKey(a)){valid=false;break;}next.Add(a,b);
                    }
                    if(!valid)continue;
                    int start=next.Keys.Min(),current=start;var polygon=new List<int>();
                    do{polygon.Add(current);if(!next.TryGetValue(current,out current)){valid=false;break;}}while(current!=start&&polygon.Count<=next.Count);
                    if(!valid||polygon.Count!=next.Count||current!=start)continue;
                    var replacement=Triangulate(vertices,polygon);if(replacement==null)continue;
                    // Reject a cavity diagonal already used outside the removed star.
                    var outside=new HashSet<long>();var star=new HashSet<int>(entry.Value);
                    foreach(int n in polygon)foreach(int id in incident[n])if(!star.Contains(id))
                    {var f=faces[id];outside.Add(Edge(f.a,f.b));outside.Add(Edge(f.b,f.c));outside.Add(Edge(f.c,f.a));}
                    var border=new HashSet<long>();for(int i=0;i<polygon.Count;i++)border.Add(Edge(polygon[i],polygon[(i+1)%polygon.Count]));
                    foreach(var f in replacement)
                    {
                        foreach(long e in new[]{Edge(f.a,f.b),Edge(f.b,f.c),Edge(f.c,f.a)})if(!border.Contains(e)&&outside.Contains(e))valid=false;
                        if(!valid||!surface.Accept(f,heightError)||height!=null&&!height(f.a,f.b,f.c,heightError)){valid=false;break;}
                    }
                    if(!valid)continue;
                    foreach(int id in entry.Value)deleted.Add(id);added.AddRange(replacement);
                    touched.Add(v);foreach(int n in polygon)touched.Add(n);changes++;removed++;
                }
                if(changes==0)break;
                var updated=new List<Face>(faces.Count-deleted.Count+added.Count);
                for(int i=0;i<faces.Count;i++)if(!deleted.Contains(i))updated.Add(faces[i]);updated.AddRange(added);faces=updated;
            }
            if(removed==0)return null;
            return new LTRockSeamIndices.Band{source=selected.ToArray(),replacement=faces.SelectMany(f=>new[]{f.a,f.b,f.c}).ToArray()};
        }
        static List<Face> Triangulate(Vector3[] p,List<int> input)
        {
            var polygon=new List<int>(input);var output=new List<Face>();double area=0;
            for(int i=1;i<polygon.Count-1;i++)area+=Cross(p[polygon[0]],p[polygon[i]],p[polygon[i+1]]);
            if(Math.Abs(area)<1e-10)return null;double sign=Math.Sign(area);
            while(polygon.Count>3)
            {
                int best=-1;double quality=-1;
                for(int i=0;i<polygon.Count;i++)
                {
                    int a=polygon[(i+polygon.Count-1)%polygon.Count],b=polygon[i],c=polygon[(i+1)%polygon.Count];
                    double cross=Cross(p[a],p[b],p[c])*sign;if(cross<=1e-10)continue;bool clear=true;
                    foreach(int v in polygon)if(v!=a&&v!=b&&v!=c&&Cross(p[a],p[b],p[v])*sign>=-1e-10&&Cross(p[b],p[c],p[v])*sign>=-1e-10&&Cross(p[c],p[a],p[v])*sign>=-1e-10){clear=false;break;}
                    if(!clear)continue;
                    double score=cross/((p[a]-p[b]).sqrMagnitude+(p[b]-p[c]).sqrMagnitude+(p[c]-p[a]).sqrMagnitude);
                    if(score>quality){quality=score;best=i;}
                }
                if(best<0)return null;
                output.Add(new Face(polygon[(best+polygon.Count-1)%polygon.Count],polygon[best],polygon[(best+1)%polygon.Count]));polygon.RemoveAt(best);
            }
            if(Cross(p[polygon[0]],p[polygon[1]],p[polygon[2]])*sign<=1e-10)return null;
            output.Add(new Face(polygon[0],polygon[1],polygon[2]));return output;
        }
        sealed class Surface
        {
            readonly Vector3[] p;readonly Face[] source;readonly Dictionary<(int,int),List<int>> bins=new Dictionary<(int,int),List<int>>();
            readonly float step;readonly HashSet<int> visited=new HashSet<int>();
            public Surface(Vector3[] p,Face[] source)
            {
                this.p=p;this.source=source;double area=0;foreach(var f in source)area+=Math.Abs(Cross(p[f.a],p[f.b],p[f.c]));
                step=Mathf.Max(.01f,(float)Math.Sqrt(area/Math.Max(1,source.Length)));
                for(int id=0;id<source.Length;id++)
                {
                    var box=Box(source[id]);for(int x=box.x0;x<=box.x1;x++)for(int z=box.z0;z<=box.z1;z++)
                    {if(!bins.TryGetValue((x,z),out var list))bins[(x,z)]=list=new List<int>();list.Add(id);}
                }
            }
            (int x0,int x1,int z0,int z1) Box(Face f)
            {
                return ((int)Math.Floor(Math.Min(p[f.a].x,Math.Min(p[f.b].x,p[f.c].x))/step),(int)Math.Floor(Math.Max(p[f.a].x,Math.Max(p[f.b].x,p[f.c].x))/step),
                    (int)Math.Floor(Math.Min(p[f.a].z,Math.Min(p[f.b].z,p[f.c].z))/step),(int)Math.Floor(Math.Max(p[f.a].z,Math.Max(p[f.b].z,p[f.c].z))/step));
            }
            double Height(Face f,Vector3 q)
            {
                double area=Cross(p[f.a],p[f.b],p[f.c]);
                return (Cross(q,p[f.b],p[f.c])*p[f.a].y+Cross(p[f.a],q,p[f.c])*p[f.b].y+Cross(p[f.a],p[f.b],q)*p[f.c].y)/area;
            }
            public bool Accept(Face triangle,float error,bool requireCoverage=true)
            {
                var box=Box(triangle);visited.Clear();double covered=0;
                for(int x=box.x0;x<=box.x1;x++)for(int z=box.z0;z<=box.z1;z++)if(bins.TryGetValue((x,z),out var ids))foreach(int id in ids)
                {
                    if(!visited.Add(id))continue;var original=source[id];
                    if(Math.Abs(Cross(p[original.a],p[original.b],p[original.c]))<1e-10)continue;
                    var polygon=new List<Vector3>{p[original.a],p[original.b],p[original.c]};
                    int[] clip={triangle.a,triangle.b,triangle.c};double sign=Math.Sign(Cross(p[clip[0]],p[clip[1]],p[clip[2]]));
                    for(int edge=0;edge<3&&polygon.Count>0;edge++)
                    {
                        var clipped=new List<Vector3>();var a=p[clip[edge]];var b=p[clip[(edge+1)%3]];var previous=polygon[polygon.Count-1];double dp=Cross(a,b,previous)*sign;
                        foreach(var current in polygon)
                        {
                            double dc=Cross(a,b,current)*sign;
                            if((dc>=0)!=(dp>=0))clipped.Add(Vector3.Lerp(previous,current,(float)(dp/(dp-dc))));
                            if(dc>=0)clipped.Add(current);previous=current;dp=dc;
                        }
                        polygon=clipped;
                    }
                    if(polygon.Count<3)continue;
                    double area=0;for(int i=1;i<polygon.Count-1;i++)area+=Math.Abs(Cross(polygon[0],polygon[i],polygon[i+1]));
                    if(area<=1e-10)continue;covered+=area;
                    // Height difference is affine on each overlap polygon; testing
                    // its corners bounds the WHOLE surface, including crossing edges.
                    foreach(var q in polygon)if(Math.Abs(Height(triangle,q)-Height(original,q))>error+1e-5)return false;
                }
                double expected=Math.Abs(Cross(p[triangle.a],p[triangle.b],p[triangle.c]));
                return !requireCoverage||Math.Abs(covered-expected)<=Math.Max(1e-7,expected*1e-4);
            }
        }
    }
}
