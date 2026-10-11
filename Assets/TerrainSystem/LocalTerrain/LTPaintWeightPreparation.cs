using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalTerrainPrototype
{
    public sealed partial class LTPaintRuntime
    {
        readonly LTPaintWeightInputPool weightInputs=new LTPaintWeightInputPool();
        static LTPaintWeightMath.Filter CopyFilter(LTPaintFilter f)=>new LTPaintWeightMath.Filter{enabled=f.enabled?1:0,range=f.range,feather=f.feather};
        static bool PrecomputedRoad(LTRoad road)=>road.mode==LTRoadMode.GeometricAsphalt||road.mode==LTRoadMode.Asphalt&&
            (road.startJunction&&road.startJunction.CoordinateShoulders||road.endJunction&&road.endJunction.CoordinateShoulders);
        static bool WeightInputFits(List<LTPaintStamp> stamps)
        {
            if(stamps.Count>64)return false;
            long bytes=(long)Resolution*Resolution*8+(long)stamps.Count*LTPaintWeightInputPool.StampBytes;
            foreach(var stamp in stamps)bytes+=(long)Resolution*Resolution*(stamp.Road?(PrecomputedRoad(stamp.Road)?20:16):stamp.Junction?(stamp.Junction.MeshFoundation?4:0):8+(stamp.mask?4:0));
            return bytes<=16L*1024*1024;
        }
        bool TryPrepareWeights(LTWorld world,Rect rect,List<LTPaintStamp> stamps,List<LTSurfaceLayer> layers,LTRoadRasterSet raster,out LTPaintWeightInput input)
        {
            input=null;if(!WeightInputFits(stamps))return false;
            LTPaintTerrainSnapshot ground;
            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightTerrainSnapshot))
            {
                if(stamps.Exists(s=>s.HasTerrainFilters))
                {
                    bool made=terrain.TrySnapshot(rect,TerrainFilterRadius(stamps),out ground,out bool reused);
                    world.paintCpu.TerrainSnapshotPrepared(made,reused);if(!made)return false;
                }
                else ground=new LTPaintTerrainSnapshot();
            }
            int pixels=Resolution*Resolution,roadCount=0,maskCount=0,localCount=0;
            LTPaintWeightMath.Stamp[] descriptors;LTPaintWeightMath.RoadHit[] hits;float[] maskValues;Vector2[] coordinates;
            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightInputSetup))
            {
                foreach(var stamp in stamps){if(stamp.Road){roadCount++;if(PrecomputedRoad(stamp.Road))maskCount++;}else if(stamp.Junction){if(stamp.Junction.MeshFoundation)maskCount++;}else{localCount++;if(stamp.mask)maskCount++;}}
                input=weightInputs.Rent(stamps.Count,roadCount*pixels,maskCount*pixels,(localCount+1)*pixels,
                    out int created,out int reused,out int allocated);
                world.paintCpu.WeightInputRented(created,reused,allocated);
                descriptors=input.stamps;hits=input.hits;maskValues=input.masks;coordinates=input.coordinates;
            }
            try
            {
                using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightCoordinates))
                    for(int p=0;p<pixels;p++)coordinates[p]=new Vector2(rect.xMin+rect.width*(p%Resolution)/(Resolution-1),rect.yMin+rect.height*(p/Resolution)/(Resolution-1));
                int hitOffset=0,maskOffset=0,localOffset=pixels;
                for(int i=0;i<stamps.Count;i++)
                {
                    var stamp=stamps[i];var s=new LTPaintWeightMath.Stamp{slot=layers.IndexOf(stamp.EffectiveLayer),wheelSlot=layers.IndexOf(stamp.SecondaryLayer),maskOffset=-1};
                    if(stamp.Road)
                    {
                        using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightRoadHits))
                        {
                            var road=stamp.Road.Capture(world);s.kind=1;s.hitOffset=hitOffset;s.road=road.PaintKernelData();
                            bool geometric=road.mode==LTRoadMode.GeometricAsphalt||road.HasShoulderJoins;
                            if(geometric){s.kind=3;s.maskOffset=maskOffset;}
                            int rasterRoad=raster?.RoadIndex(road)??-1;
                            for(int p=0;p<pixels;p++)
                            {
                                float x=coordinates[p].x,z=coordinates[p].y;
                                if(geometric)maskValues[maskOffset+p]=0; // pooled buffers may contain previous road pixels
                                var h=new LTPaintWeightMath.RoadHit{radial=-1};
                                if(x>=road.bounds.xMin&&x<=road.bounds.xMax&&z>=road.bounds.yMin&&z<=road.bounds.yMax)
                                {
                                    LTRoadMath.Hit hit;
                                    bool found=rasterRoad>=0?raster.TrySample(rasterRoad,p,out hit):road.TrySample(x,z,out hit);
                                    if(found)
                                    {
                                        h=LTPaintWeightMath.RoadHit.From(hit);
                                        if(geometric){road.PaintWeights(hit,x,z,out float weight,out _);maskValues[maskOffset+p]=weight;}
                                    }
                                }
                                hits[hitOffset+p]=h;
                            }
                            hitOffset+=pixels;
                            if(geometric)maskOffset+=pixels;
                        }
                    }
                    else if(stamp.Junction)
                    {
                        var j=stamp.Junction.Capture();s.kind=2;s.centre=j.centre;s.coreRadius=j.coreRadius;s.blend=j.blend;
                        if(j.footprint!=null)
                        {
                            s.kind=3;s.maskOffset=maskOffset;
                            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightMasks))
                                for(int p=0;p<pixels;p++)maskValues[maskOffset+p]=j.Weight(coordinates[p].x,coordinates[p].y);
                            maskOffset+=pixels;
                        }
                    }
                    else
                    {
                        s.size=stamp.size;s.rectangle=stamp.shape==LTStampShape.Rectangle?1:0;
                        s.localOffset=localOffset;
                        using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightCoordinates))
                        {
                            Projection(world,stamp,out s.inverse);
                            for(int p=0;p<pixels;p++)
                            {var xy=coordinates[p];var local=s.inverse.MultiplyPoint3x4(new Vector3(xy.x,0,xy.y));coordinates[localOffset+p]=new Vector2(local.x,local.z);}
                        }
                        s.strength=stamp.strength;s.falloff=stamp.edgeFalloff;
                        s.noiseEnabled=stamp.noise.enabled?1:0;s.noiseSize=stamp.noise.size;s.noiseSeed=stamp.noise.seed;
                        s.noiseStrength=stamp.noise.strength;s.noiseThreshold=stamp.noise.threshold;s.noiseSoftness=stamp.noise.softness;
                        s.height=CopyFilter(stamp.heightFilter);s.slope=CopyFilter(stamp.slopeFilter);s.curve=CopyFilter(stamp.curveFilter);s.curveRadius=stamp.curveRadius;
                        if(stamp.mask)
                        {
                            using(world.paintCpu.Measure(LTPaintCpuCapture.Stage.WeightMasks))
                            {
                                // Keep Unity's CPU bilinear convention exactly. Texture APIs
                                // are never called by a worker, even for readable textures.
                                var copy=ReadMask(stamp.mask);s.maskOffset=maskOffset;
                                for(int p=0;p<pixels;p++)
                                {
                                    var local=coordinates[localOffset+p];
                                    var q=new Vector2(local.x/Mathf.Max(.01f,stamp.size.x)*2,local.y/Mathf.Max(.01f,stamp.size.y)*2);
                                    if(LTPaintMath.Coverage(q,s.rectangle!=0,s.falloff,s.strength)>0)
                                        maskValues[maskOffset+p]=copy.GetPixelBilinear(q.x*.5f+.5f,q.y*.5f+.5f).r;
                                }
                                maskOffset+=pixels;
                            }
                        }
                        localOffset+=pixels;
                    }
                    descriptors[i]=s;
                }
                input.rect=rect;input.size=Resolution;input.terrain=ground;return true;
            }
            catch{input.Dispose();input=null;throw;}
        }
    }
}
