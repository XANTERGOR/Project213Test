using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    // Bounded reusable CPU staging buffers. Not persistent GPU storage / indirect rendering.
    public sealed class LTDetailBatchQueue<TKey> where TKey:struct,IEquatable<TKey>
    {
        public sealed class Batch
        {
            public TKey key;
            public readonly Matrix4x4[] matrices;
            public Bounds bounds;
            public int count;
            public Batch(int capacity){matrices=new Matrix4x4[capacity];}
        }
        readonly Dictionary<TKey,Batch> active=new Dictionary<TKey,Batch>();
        readonly List<Batch> pool=new List<Batch>();
        readonly int capacity,limit;
        int used;
        public LTDetailBatchQueue(int capacity=511,int limit=128)
        {
            if(capacity<=0||limit<=0)throw new ArgumentOutOfRangeException();
            this.capacity=capacity;this.limit=limit;
        }
        public int BufferCount=>pool.Count;
        public void Begin()
        {
            active.Clear();
            // Keys may contain Unity object references. Do not retain unloaded recipes.
            for(int i=0;i<used;i++){pool[i].key=default;pool[i].count=0;}
            used=0;
        }
        public void Clear(){Begin();pool.Clear();}
        public void Add(TKey key,Matrix4x4 matrix,Bounds bounds,Action<Batch> emit)
        {
            if(!active.TryGetValue(key,out var batch))
            {
                // Bound memory independently of the number of materials/cells/cameras.
                if(used==limit)Flush(emit);
                if(used==pool.Count)pool.Add(new Batch(capacity));
                batch=pool[used++];batch.key=key;batch.count=0;active.Add(key,batch);
            }
            if(batch.count==0)batch.bounds=bounds;else batch.bounds.Encapsulate(bounds);
            batch.matrices[batch.count++]=matrix;
            if(batch.count==capacity){emit(batch);batch.count=0;}
        }
        public void Flush(Action<Batch> emit)
        {
            for(int i=0;i<used;i++)if(pool[i].count>0){emit(pool[i]);pool[i].count=0;}
            Begin();
        }
    }
    // All draw state used by the detail renderer, plus a bounded spatial merge region.
    public struct LTDetailDrawState:IEquatable<LTDetailDrawState>
    {
        public int mesh,material,submesh,layer,shadow,lightProbes,reflectionProbes,motionVectors,regionX,regionZ;
        public uint renderingLayerMask;
        public bool receiveShadows;
        public bool Equals(LTDetailDrawState b)=>mesh==b.mesh&&material==b.material&&submesh==b.submesh&&layer==b.layer&&
            shadow==b.shadow&&lightProbes==b.lightProbes&&reflectionProbes==b.reflectionProbes&&motionVectors==b.motionVectors&&
            regionX==b.regionX&&regionZ==b.regionZ&&renderingLayerMask==b.renderingLayerMask&&receiveShadows==b.receiveShadows;
        public override bool Equals(object obj)=>obj is LTDetailDrawState b&&Equals(b);
        public override int GetHashCode()
        {
            unchecked
            {
                int h=mesh;h=h*397^material;h=h*397^submesh;h=h*397^layer;h=h*397^shadow;
                h=h*397^lightProbes;h=h*397^reflectionProbes;h=h*397^motionVectors;
                h=h*397^regionX;h=h*397^regionZ;h=h*397^(int)renderingLayerMask;
                return h*397^(receiveShadows?1:0);
            }
        }
    }
}
