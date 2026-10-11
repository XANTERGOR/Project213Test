using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTPaintRuntime
    {
        readonly LTRoadCrackTextures asphaltCracks=new LTRoadCrackTextures();
        sealed class AsphaltState
        {
            public int hash;
            public Texture2DArray masks;
            public readonly Vector4[] styles=new Vector4[LayerCapacity];
        }
        static readonly ConditionalWeakTable<ChunkState,AsphaltState> asphaltStates=new ConditionalWeakTable<ChunkState,AsphaltState>();
        void BakeAsphalt(LTWorld world,Rect rect,List<LTPaintStamp> stamps,List<LTSurfaceLayer> layers,ChunkState state,bool bakeWeights)
        {
            var roads=new List<LTRoad>[layers.Count];int count=0,hash=Mix(asphaltCracks.Revision,rect.GetHashCode());
            foreach(var stamp in stamps)
            {
                var road=stamp.Road;if(!road||!LTRoadMath.IsTerrainAsphalt(road.mode)||!road.isActiveAndEnabled)continue;
                int slot=layers.IndexOf(road.groundLayer);if(slot<0)continue;
                if(roads[slot]==null){roads[slot]=new List<LTRoad>();count++;}
                roads[slot].Add(road);hash=Mix(Mix(hash,slot),road.Capture(world).paintHash);
                hash=Mix(Mix(hash,Id(road.asphaltCrackMask)),road.asphaltCrackRepeat.GetHashCode());
            }
            if(count==0||!asphaltCracks.Texture){ReleaseAsphalt(state);return;}
            // Weight rebuilds also invalidate crack ownership: an intervening biome/
            // wheel stamp can cover an earlier road before a later asphalt road paints.
            if(!bakeWeights&&asphaltStates.TryGetValue(state,out var old)&&old.hash==hash)return;
            var snapshots=new LTRoadMath.Snapshot[stamps.Count];var nodes=new LTRoadJunctionMath.Snapshot[stamps.Count];
            var inverse=new Matrix4x4[stamps.Count];var copies=new Texture2D[stamps.Count];
            var slots=new int[stamps.Count];var wheelSlots=new int[stamps.Count];
            for(int i=0;i<stamps.Count;i++)
            {
                var stamp=stamps[i];slots[i]=layers.IndexOf(stamp.EffectiveLayer);wheelSlots[i]=layers.IndexOf(stamp.SecondaryLayer);
                if(stamp.Road)snapshots[i]=stamp.Road.Capture(world);
                else if(stamp.Junction)nodes[i]=stamp.Junction.Capture();
                else{Projection(world,stamp,out inverse[i]);copies[i]=ReadMask(stamp.mask);}
            }
            var extent=world.TerrainRect;
            var terrainSamples=new LTPaintMath.PixelTerrainCache(terrain.Sample,extent.width,extent.height,stamps.Count,extent.xMin,extent.yMin);
            float OtherWeight(int i,float x,float z)
            {
                if(nodes[i]!=null)return nodes[i].Weight(x,z);
                var stamp=stamps[i];var local=inverse[i].MultiplyPoint3x4(new Vector3(x,0,z));
                var p=new Vector2(local.x/Mathf.Max(.01f,stamp.size.x)*2,local.z/Mathf.Max(.01f,stamp.size.y)*2);
                float alpha=LTPaintMath.Coverage(p,stamp.shape==LTStampShape.Rectangle,stamp.edgeFalloff,stamp.strength);
                if(alpha<=0)return 0;
                if(copies[i])alpha*=copies[i].GetPixelBilinear(p.x*.5f+.5f,p.y*.5f+.5f).r;
                if(stamp.noise.enabled)alpha*=LTPaintMath.NoiseCoverage(new Vector2(local.x,local.z),stamp.noise.size,stamp.noise.seed,stamp.noise.strength,stamp.noise.threshold,stamp.noise.softness);
                if(alpha>0&&stamp.HasTerrainFilters)
                {
                    if(!terrainSamples.Surface(out float height,out float slope))return 0;
                    alpha*=stamp.heightFilter.Evaluate(height)*stamp.slopeFilter.Evaluate(slope);
                    if(alpha>0&&stamp.curveFilter.enabled)
                    {if(!terrainSamples.Curvature(stamp.curveRadius,out float curvature))return 0;alpha*=stamp.curveFilter.Evaluate(curvature);}
                }
                return alpha;
            }
            var next=new AsphaltState{hash=hash};
            try
            {
                next.masks=new Texture2DArray(Resolution,Resolution,count,TextureFormat.RGBA32,false,true)
                    {name="Asphalt crack influence",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
                int slice=0;
                for(int slot=0;slot<roads.Length;slot++)
                {
                    if(roads[slot]==null)continue;
                    var first=roads[slot][0];
                    foreach(var stamp in stamps)
                        if(stamp.SecondaryLayer==layers[slot]||stamp.EffectiveLayer==layers[slot]&&(!stamp.Road||!LTRoadMath.IsTerrainAsphalt(stamp.Road.mode)))
                            throw new InvalidOperationException("Terrain Asphalt: используйте слой, отдельный от грунтовых дорог и обычных штампов.");
                    foreach(var road in roads[slot])
                    {
                        if(road.asphaltCrackMask!=first.asphaltCrackMask||road.asphaltCrackRepeat!=first.asphaltCrackRepeat)
                            throw new InvalidOperationException("Terrain Asphalt: дороги с одним слоем должны иметь одинаковую текстуру и повтор трещин.");
                    }
                    int textureSlice=asphaltCracks.Slice(first.asphaltCrackMask);
                    next.styles[slot]=textureSlice<0?Vector4.zero:new Vector4(slice+1,textureSlice,Mathf.Max(.1f,first.asphaltCrackRepeat),0);
                    var pixels=new Color32[Resolution*Resolution];
                    for(int z=0;z<Resolution;z++)for(int x=0;x<Resolution;x++)
                    {
                        float px=rect.xMin+rect.width*x/(Resolution-1),pz=rect.yMin+rect.height*z/(Resolution-1),total=0,crack=0,repair=0;
                        terrainSamples.Begin(px,pz);
                        for(int i=0;i<stamps.Count;i++)
                        {
                            var snapshot=snapshots[i];
                            if(snapshot!=null)
                            {
                                if(!snapshot.TrySample(px,pz,out var hit))continue;
                                snapshot.PaintWeights(hit,px,pz,out float weight,out float wheels);
                                if(slots[i]>=0)LTRoadAsphaltMath.CompositeSurface(ref total,ref crack,ref repair,weight,snapshot.AsphaltDamage(hit).cracks,snapshot.RepairPatch(hit),slots[i]==slot);
                                if(wheelSlots[i]>=0)LTRoadAsphaltMath.CompositeSurface(ref total,ref crack,ref repair,wheels,0,0,false);
                            }
                            else if(slots[i]>=0)LTRoadAsphaltMath.CompositeSurface(ref total,ref crack,ref repair,OtherWeight(i,px,pz),0,0,false);
                        }
                        pixels[z*Resolution+x]=new Color32((byte)Mathf.RoundToInt(Mathf.Clamp01(crack/Mathf.Max(total,.00001f))*255),
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(repair/Mathf.Max(total,.00001f))*255),0,255);
                    }
                    next.masks.SetPixels32(pixels,slice++);
                }
                next.masks.Apply(false,true);
            }
            catch{DestroyOwned(next.masks);throw;}
            ReleaseAsphalt(state);asphaltStates.Add(state,next);
        }
        void BindAsphalt(Material material,ChunkState state)
        {
            bool active=asphaltStates.TryGetValue(state,out var data)&&asphaltCracks.Texture;
            material.SetTexture("_LTAsphaltCracks",active?asphaltCracks.Texture:null);
            material.SetTexture("_LTAsphaltDamage",active?data.masks:null);
            for(int i=0;i<LayerCapacity;i++)material.SetVector("_LTAsphaltStyle"+i,active?data.styles[i]:Vector4.zero);
        }
        static void ReleaseAsphalt(ChunkState state)
        {if(asphaltStates.TryGetValue(state,out var data)){asphaltStates.Remove(state);DestroyOwned(data.masks);}}
    }
}
