using System;
using System.Collections.Generic;

namespace LocalTerrainPrototype
{
    public static class LTRockSeamIndices
    {
        [Serializable] public sealed class Band
        {
            // Original (before endpoint mapping) triangles, and the baked replacement.
            public int[] source, replacement;
            // Proven at bake time against ALL reachable native cell variants.
            // Valid only with the baked vertex bank + spatial revision.
            public bool invariant;
        }
        // Prepared once per LOD selection. Apply does no mesh analysis or allocation.
        public sealed class Plan
        {
            public readonly int[] map;
            public readonly Band[] bands;
            readonly Dictionary<(int,int,int),List<int>> owners=new Dictionary<(int,int,int),List<int>>();
            readonly int[] found;
            readonly int[] priority;
            readonly bool[] selected;
            readonly HashSet<(int,int,int)> matched=new HashSet<(int,int,int)>();
            readonly Plan first,second;
            readonly bool invariant;
            Plan(Plan first,Plan second){this.first=first;this.second=second;}
            public static Plan Combine(Plan first,Plan second)=>new Plan(first,second);
            public Plan(int[] map,params Band[] bands)
            {
                this.map=map;this.bands=bands??Array.Empty<Band>();found=new int[this.bands.Length];selected=new bool[found.Length];priority=new int[found.Length];
                invariant=this.bands.Length>0&&Array.TrueForAll(this.bands,b=>b.invariant);
                for(int b=0;b<this.bands.Length;b++)
                {
                    priority[b]=b;
                    var band=this.bands[b];
                    if(band.source.Length%3!=0||band.replacement.Length%3!=0)throw new ArgumentException("Incomplete seam band.");
                    for(int i=0;i<band.source.Length;i+=3)
                    {
                        var key=Face(band.source[i],band.source[i+1],band.source[i+2]);
                        if(!owners.TryGetValue(key,out var list))owners[key]=list=new List<int>();
                        if(list.Contains(b)||invariant&&list.Count>0)throw new ArgumentException("Overlapping seam footprint.");list.Add(b);
                        matched.Add(key); // reserve capacity; subsequent Apply calls reuse it
                    }
                }
                Array.Sort(priority,(a,b)=>this.bands[b].source.Length.CompareTo(this.bands[a].source.Length));
                if(!invariant)matched.Clear();
            }
            public void Apply(List<int> indices)
            {
                if(first!=null){first.Apply(indices);second.Apply(indices);return;}
                if(bands.Length==0){Remap(indices,map);return;}
                if(invariant)
                {
                    // Native ownership, not alternative-pattern matching. The
                    // footprint cannot disappear when a neighbour changes LOD.
                    int writeNative=0;
                    for(int i=0;i<indices.Count;i+=3)
                    {
                        int a=indices[i],b=indices[i+1],c=indices[i+2];
                        if(matched.Contains(Face(a,b,c)))continue;
                        a=map[a];b=map[b];c=map[c];if(a==b||b==c||a==c)continue;
                        indices[writeNative++]=a;indices[writeNative++]=b;indices[writeNative++]=c;
                    }
                    if(writeNative<indices.Count)indices.RemoveRange(writeNative,indices.Count-writeNative);
                    foreach(var band in bands)indices.AddRange(band.replacement);
                    return;
                }
                Array.Clear(found,0,found.Length);Array.Clear(selected,0,selected.Length);matched.Clear();
                for(int i=0;i<indices.Count;i+=3)
                {
                    var key=Face(indices[i],indices[i+1],indices[i+2]);
                    if(owners.TryGetValue(key,out var ownersOfFace))
                    {bool fresh=matched.Add(key);foreach(int band in ownersOfFace){if(fresh)found[band]++;else found[band]=int.MinValue;}}
                }
                matched.Clear();
                foreach(int b in priority)if(found[b]==bands[b].source.Length/3)
                {
                    var source=bands[b].source;bool overlaps=false;
                    for(int i=0;i<source.Length;i+=3)if(matched.Contains(Face(source[i],source[i+1],source[i+2]))){overlaps=true;break;}
                    if(overlaps)continue;selected[b]=true;
                    for(int i=0;i<source.Length;i+=3)matched.Add(Face(source[i],source[i+1],source[i+2]));
                }
                int write=0;
                for(int i=0;i<indices.Count;i+=3)
                {
                    int a=indices[i],b=indices[i+1],c=indices[i+2];
                    // Incomplete/stale input keeps the original remapped band, never a hole.
                    if(matched.Contains(Face(a,b,c)))continue;
                    a=map[a];b=map[b];c=map[c];if(a==b||b==c||a==c)continue;
                    indices[write++]=a;indices[write++]=b;indices[write++]=c;
                }
                if(write<indices.Count)indices.RemoveRange(write,indices.Count-write);
                for(int b=0;b<bands.Length;b++)if(selected[b])indices.AddRange(bands[b].replacement);
            }
        }
        public static (int,int,int) Face(int a,int b,int c)
        {
            // Canonical cyclic order, preserving winding (not an unordered triangle).
            return a<=b&&a<=c?(a,b,c):b<=c?(b,c,a):(c,a,b);
        }
        public static void Remap(List<int> indices,int[] map)
        {
            int write=0;
            for(int i=0;i<indices.Count;i+=3)
            {
                int a=map[indices[i]],b=map[indices[i+1]],c=map[indices[i+2]];if(a==b||b==c||a==c)continue;
                indices[write++]=a;indices[write++]=b;indices[write++]=c;
            }
            if(write<indices.Count)indices.RemoveRange(write,indices.Count-write);
        }
    }
}
